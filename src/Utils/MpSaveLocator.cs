using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using UnityEngine;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>定位联机存档目录 mp_save，并提供 zip 打包与带回滚保护的暂存还原。</summary>
    internal static class MpSaveLocator
    {
        internal const string MpFolderName = "mp_save";
        internal const string MpRulesFile = "mp_rules.json";

        internal static string MpSaveDir => Path.Combine(Application.persistentDataPath, MpFolderName);
        internal static string MpSaveStagingDir => Path.Combine(Application.persistentDataPath, MpFolderName + ".restore");
        internal static string MpRulesPath => Path.Combine(MpSaveDir, MpRulesFile);
        internal static string VanillaSavePath => Path.Combine(Application.persistentDataPath, "save.sv");

        internal static bool HasMpSave() => File.Exists(MpRulesPath);

        /// <summary>仅在联机会话运行期间按多人存档处理。</summary>
        internal static bool IsMultiplayerSaveActive()
            => MultiplayerBridge.IsMultiplayerRunning();

        /// <summary>启动或回档前检测：若 mp_save 不存在但存在上次回档中断残留的 mp_save.old，则自动将其恢复为 mp_save。</summary>
        internal static void RecoverInterruptedRestoreIfNeeded()
        {
            string previous = MpSaveDir + ".old";
            if (!Directory.Exists(MpSaveDir) && Directory.Exists(previous))
            {
                try
                {
                    Directory.Move(previous, MpSaveDir);
                    ModLog.Warning("多人存档：检测到上次回档中断残留，已将原 mp_save 从 .old 自动恢复");
                }
                catch (Exception recoverError)
                {
                    ModLog.Warning($"多人存档：无法恢复中断残留的 mp_save（{previous}）：{recoverError.Message}");
                }
            }
        }

        /// <summary>将 mp_save 目录快速打包写入目标路径；若目标原本存在则保护原文件不被失败过程误删。</summary>
        internal static void PackMpSaveTo(string destZipPath)
        {
            if (!HasMpSave()) throw new FileNotFoundException("没有可打包的多人存档 mp_save");
            bool existedBefore = File.Exists(destZipPath);
            string tmp = destZipPath + ".tmp." + Guid.NewGuid().ToString("N");
            try
            {
                if (File.Exists(tmp)) File.Delete(tmp);
                ZipFile.CreateFromDirectory(MpSaveDir, tmp, System.IO.Compression.CompressionLevel.Fastest, includeBaseDirectory: false);
                if (File.Exists(destZipPath))
                {
                    try
                    {
                        File.Replace(tmp, destZipPath, null);
                        return;
                    }
                    catch
                    {
                        File.Delete(destZipPath);
                    }
                }
                File.Move(tmp, destZipPath);
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                if (!existedBefore)
                {
                    try { if (File.Exists(destZipPath)) File.Delete(destZipPath); } catch { }
                }
                throw;
            }
        }

        /// <summary>读取 mp_save 下可用的 save.sv：优先本机持久 ID 子目录，缺失时兜底取首个玩家子目录，仅用于读取全队共享参数。</summary>
        internal static string ResolveLocalPlayerSavePath()
        {
            try
            {
                string own = ResolveOwnSavePath();
                if (!string.IsNullOrEmpty(own)) return own;
                if (Directory.Exists(MpSaveDir))
                {
                    foreach (var dir in Directory.GetDirectories(MpSaveDir))
                    {
                        string p = Path.Combine(dir, "save.sv");
                        if (File.Exists(p)) return p;
                    }
                }
                string root = Path.Combine(MpSaveDir, "save.sv");
                if (File.Exists(root)) return root;
            }
            catch { }
            return null;
        }

        /// <summary>仅按本机玩家的持久 ID 定位专属 save.sv，不做跨玩家子目录兜底。</summary>
        internal static string ResolveOwnSavePath()
        {
            try
            {
                if (MultiplayerBridge.IsDedicatedServer()) return null;
                string id = MultiplayerBridge.GetLocalPersistentId();
                if (string.IsNullOrEmpty(id)) return null;
                string p = Path.Combine(MpSaveDir, id, "save.sv");
                return File.Exists(p) ? p : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>列出 mp_save 下的全部玩家持久 ID 子目录名。</summary>
        internal static List<string> ListPersistentIdDirs()
        {
            var result = new List<string>();
            try
            {
                if (!Directory.Exists(MpSaveDir)) return result;
                foreach (var dir in Directory.GetDirectories(MpSaveDir))
                {
                    string name = Path.GetFileName(dir);
                    if (!string.IsNullOrEmpty(name)) result.Add(name);
                }
            }
            catch (Exception ex)
            {
                ModLog.Warning($"ListPersistentIdDirs 失败：{ex.Message}");
            }
            return result;
        }

        /// <summary>以只读文件流打开多人槽位 zip 提取 RunId（优先读 mp_rules.json，避免将整包加载进内存）。</summary>
        internal static int ReadRunIdFromZipFile(string zipPath)
        {
            if (string.IsNullOrEmpty(zipPath) || !File.Exists(zipPath)) return 0;
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                return ReadRunIdFromArchive(zip);
            }
        }

        /// <summary>从打包的多人槽位 zip 字节流提取 RunId（优先读包内 mp_rules.json，未果再读玩家 save.sv 的 cId）。</summary>
        internal static int ReadRunIdFromZipBytes(byte[] zipBytes)
        {
            if (zipBytes == null || zipBytes.Length == 0) return 0;
            using (var ms = new MemoryStream(zipBytes))
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Read))
            {
                return ReadRunIdFromArchive(zip);
            }
        }

        private static int ReadRunIdFromArchive(ZipArchive zip)
        {
            foreach (var entry in zip.Entries)
            {
                if (!string.Equals(Path.GetFileName(entry.FullName), MpRulesFile, StringComparison.OrdinalIgnoreCase))
                    continue;
                using (var reader = new StreamReader(entry.Open(), System.Text.Encoding.UTF8))
                {
                    int rid = MpRulesJson.ParseRunId(reader.ReadToEnd());
                    if (rid != 0) return rid;
                }
            }

            foreach (var entry in zip.Entries)
            {
                if (!entry.FullName.EndsWith(".sv", StringComparison.OrdinalIgnoreCase)) continue;
                using (var es = entry.Open())
                using (var buf = new MemoryStream())
                {
                    es.CopyTo(buf);
                    byte[] inner = buf.ToArray();
                    if (inner.Length < 2) continue;
                    string json = SaveSystem.Unzip(inner);
                    if (!string.IsNullOrEmpty(json) && TopJson.TryReadInt(json, "cId", out int cid) && cid != 0)
                    {
                        return cid;
                    }
                }
            }
            return 0;
        }

        /// <summary>校验 zip 条目不包含路径穿越 ".." 或越出目标目录的非法路径。</summary>
        private static void EnsureZipEntriesSafe(string srcZipPath, string stagingDir)
        {
            string fullRoot = Path.GetFullPath(stagingDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            using (var zip = ZipFile.OpenRead(srcZipPath))
            {
                foreach (var entry in zip.Entries)
                {
                    string entryName = entry.FullName;
                    if (string.IsNullOrEmpty(entryName)) continue;
                    if (entryName.IndexOf("..", StringComparison.Ordinal) >= 0)
                    {
                        throw new InvalidDataException($"多人槽位压缩包含非法路径穿越条目：{entryName}");
                    }
                    string targetPath = Path.GetFullPath(Path.Combine(fullRoot, entryName));
                    if (!targetPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException($"多人槽位压缩包含越界路径条目：{entryName}");
                    }
                }
            }
        }

        /// <summary>先解包到暂存目录并执行可选改写，成功后原子替换 mp_save；中途失败时恢复原 mp_save。</summary>
        internal static void RestoreMpSaveFrom(string srcZipPath, Action<string> rewriteExtractedDir = null)
        {
            string staging = MpSaveStagingDir;
            string previous = MpSaveDir + ".old";
            RecoverInterruptedRestoreIfNeeded();
            if (Directory.Exists(staging))
            {
                try { Directory.Delete(staging, recursive: true); }
                catch (Exception ex) { ModLog.Warning($"清理 mp_save 暂存目录失败：{ex.Message}"); }
            }
            if (Directory.Exists(previous))
            {
                try { Directory.Delete(previous, recursive: true); }
                catch (Exception ex) { ModLog.Warning($"清理旧 mp_save 备份失败：{ex.Message}"); }
            }
            if (Directory.Exists(staging) || Directory.Exists(previous))
            {
                throw new InvalidOperationException(I18n.F("mp.restore_dir_busy",
                    Directory.Exists(staging) ? staging : previous));
            }

            EnsureZipEntriesSafe(srcZipPath, staging);
            Directory.CreateDirectory(staging);
            bool movedAside = false;
            try
            {
                ZipFile.ExtractToDirectory(srcZipPath, staging);
                rewriteExtractedDir?.Invoke(staging);
                if (Directory.Exists(MpSaveDir))
                {
                    Directory.Move(MpSaveDir, previous);
                    movedAside = true;
                }
                Directory.Move(staging, MpSaveDir);
            }
            catch
            {
                if (movedAside && !Directory.Exists(MpSaveDir) && Directory.Exists(previous))
                {
                    try { Directory.Move(previous, MpSaveDir); }
                    catch (Exception restoreError) { ModLog.Error($"多人回档：原 mp_save 回挪失败：{restoreError.Message}"); }
                }
                try
                {
                    if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
                }
                catch (Exception cleanupError)
                {
                    ModLog.Warning($"清理 mp_save 暂存目录失败：{cleanupError.Message}");
                }
                throw;
            }

            if (movedAside && Directory.Exists(previous))
            {
                try { Directory.Delete(previous, recursive: true); }
                catch (Exception ex) { ModLog.Warning($"旧 mp_save 目录删除失败（{previous}）：{ex.Message}"); }
            }
        }
    }
}
