using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using UnityEngine;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>
    /// 多人存档（联机会话）位置定位与打包。会话运行期间 Together 把存档放在 persistentDataPath/mp_save/ 下：
    /// 每个玩家一个子目录 &lt;persistentId&gt;/save.sv，外加头文件 mp_rules.json。
    /// 单机存档则是 persistentDataPath/save.sv 单文件。
    /// 槽位仍是单个 .sv 文件——多人时其内容为 mp_save 目录的 zip 包。
    /// </summary>
    internal static class MpSaveLocator
    {
        internal const string MpFolderName = "mp_save";
        internal const string MpRulesFile = "mp_rules.json";

        internal static string MpSaveDir => Path.Combine(Application.persistentDataPath, MpFolderName);

        /// <summary>回档解包的暂存目录（与 mp_save 同级）：先解包并改写完再整体替换 mp_save，避免中途失败破坏现有存档。</summary>
        internal static string MpSaveStagingDir => Path.Combine(Application.persistentDataPath, MpFolderName + ".restore");

        internal static string MpRulesPath => Path.Combine(MpSaveDir, MpRulesFile);

        internal static string VanillaSavePath => Path.Combine(Application.persistentDataPath, "save.sv");

        /// <summary>多人存档目录是否有效：含 mp_rules.json 即视为一份多人存档。</summary>
        internal static bool HasMpSave() => File.Exists(MpRulesPath);

        /// <summary>当前是否应按多人存档处理：只有联机会话正在运行才算。
        /// Together 仅在会话运行期间把游戏存档目录切到 mp_save，未开房时游戏自身写 save.sv；
        /// 若按「联机 mod 已加载」判定，单机流程会去打包不存在的 mp_save 而失败。</summary>
        internal static bool IsMultiplayerSaveActive()
            => MultiplayerBridge.IsMultiplayerRunning();

        /// <summary>把整个 mp_save 目录打包成 zip 写到 destZipPath。目录不存在则抛异常。</summary>
        internal static void PackMpSaveTo(string destZipPath)
        {
            if (!HasMpSave()) throw new FileNotFoundException("没有可打包的多人存档 mp_save");
            if (File.Exists(destZipPath)) File.Delete(destZipPath);
            ZipFile.CreateFromDirectory(MpSaveDir, destZipPath, System.IO.Compression.CompressionLevel.Optimal, includeBaseDirectory: false);
        }

        /// <summary>本地玩家 save.sv：mp_save/&lt;persistentId&gt;/save.sv（Together 实际写入路径）。</summary>
        internal static string ResolveLocalPlayerSavePath()
        {
            try
            {
                string id = MultiplayerBridge.GetLocalPersistentId();
                if (!string.IsNullOrEmpty(id))
                {
                    string p = Path.Combine(MpSaveDir, id, "save.sv");
                    if (File.Exists(p)) return p;
                }
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

        /// <summary>mp_save 下的玩家子目录名（即联机模组的持久 ID）；目录不存在时返回空列表。</summary>
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

        /// <summary>
        /// 回档时用槽位的 .sv（实际是 .zip）还原 Together 存档目录 mp_save。
        /// 解包到 <see cref="MpSaveStagingDir"/>，交给 <paramref name="rewriteExtractedDir"/> 改写（玩家子目录名 / mp_rules.json），
        /// 成功后才把 mp_save 换成暂存目录：原目录先改名让位、暂存目录就位后再删除。
        /// 任一步失败都把原 mp_save 恢复回原位并抛出异常，不会留下写了一半的存档目录。
        /// </summary>
        /// <param name="srcZipPath">槽位文件（mp_save 目录的 zip 包）</param>
        /// <param name="rewriteExtractedDir">对暂存目录的改写回调；不需要改写时传 null</param>
        internal static void RestoreMpSaveFrom(string srcZipPath, Action<string> rewriteExtractedDir = null)
        {
            string staging = MpSaveStagingDir;
            string previous = MpSaveDir + ".old";
            // 上次回档在「原目录让位」与「暂存目录就位」之间中断时，mp_save 会缺位、数据还在 .old，先挪回去
            if (Directory.Exists(previous) && !Directory.Exists(MpSaveDir))
            {
                try
                {
                    Directory.Move(previous, MpSaveDir);
                    ModLog.Warning("多人回档：检测到上次回档中断，已把原 mp_save 从 .old 挪回原位");
                }
                catch (Exception recoverError)
                {
                    ModLog.Warning($"多人回档：上次中断的 mp_save 无法挪回原位（{previous}）：{recoverError.Message}");
                }
            }
            if (Directory.Exists(staging))
            {
                try { Directory.Delete(staging, recursive: true); }
                catch (Exception ex) { ModLog.Warning($"清理 mp_save 暂存目录失败：{ex.Message}"); }
            }
            if (Directory.Exists(previous))
            {
                try { Directory.Delete(previous, recursive: true); }
                catch (Exception ex) { ModLog.Warning($"清理上次回档的旧 mp_save 失败：{ex.Message}"); }
            }
            // 暂存目录 / 旧目录还没腾完时直接失败：强行解包会撞名，强行替换会丢原有进度
            if (Directory.Exists(staging) || Directory.Exists(previous))
            {
                throw new InvalidOperationException(I18n.F("mp.restore_dir_busy",
                    Directory.Exists(staging) ? staging : previous));
            }

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
                    catch (Exception restoreError) { ModLog.Error($"多人回档：原 mp_save 回挪失败，请从槽位重新回档：{restoreError.Message}"); }
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
                catch (Exception ex) { ModLog.Warning($"旧 mp_save 目录删除失败，可手动清理 {previous}：{ex.Message}"); }
            }
        }
    }
}
