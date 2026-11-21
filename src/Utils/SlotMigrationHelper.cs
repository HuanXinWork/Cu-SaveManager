using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>负责启动期旧插件目录槽位无损搬移与旧版无 RunId 槽位一次性目录归档。</summary>
    internal static class SlotMigrationHelper
    {
        internal static void MigrateLegacyPluginDirectory(string legacyRoot, string targetRoot)
        {
            try
            {
                if (string.IsNullOrEmpty(legacyRoot) || string.IsNullOrEmpty(targetRoot)) return;
                if (!Directory.Exists(legacyRoot) || SaveStore.PathsEqual(legacyRoot, targetRoot)) return;

                string fullLegacy = Path.GetFullPath(legacyRoot)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                int moved = 0;
                foreach (string srcFile in Directory.GetFiles(fullLegacy, "*", SearchOption.AllDirectories))
                {
                    string fullSrc = Path.GetFullPath(srcFile);
                    if (!fullSrc.StartsWith(fullLegacy, StringComparison.OrdinalIgnoreCase)) continue;
                    string relPath = fullSrc.Substring(fullLegacy.Length);
                    string dstFile = Path.Combine(targetRoot, relPath);
                    if (File.Exists(dstFile)) continue;

                    string dstDir = Path.GetDirectoryName(dstFile);
                    if (!string.IsNullOrEmpty(dstDir)) Directory.CreateDirectory(dstDir);
                    MoveFileSafe(fullSrc, dstFile);
                    moved++;
                }

                CleanupEmptyDirectories(fullLegacy, includeRoot: true);
                if (moved > 0)
                {
                    ModLog.Info($"已从旧插件目录无损迁移 {moved} 个槽位及伴生文件至用户数据目录：{targetRoot}");
                }
            }
            catch (Exception ex)
            {
                ModLog.Warning($"迁移旧插件目录槽位失败（{legacyRoot} → {targetRoot}）：{ex.Message}");
            }
        }

        internal static void MigrateUnorganizedSlotsOnce(string slotsRoot)
        {
            try
            {
                var root = new DirectoryInfo(slotsRoot);
                if (!root.Exists) return;

                var candidates = new List<(FileInfo file, string topFolder, string dateFolder)>();
                foreach (var f in root.GetFiles("*.sv")) candidates.Add((f, "", null));
                foreach (var topDir in root.GetDirectories())
                {
                    bool topIsDate = IsDateFolderName(topDir.Name);
                    foreach (var f in topDir.GetFiles("*.sv"))
                    {
                        candidates.Add(topIsDate ? (f, "", topDir.Name) : (f, topDir.Name, null));
                    }
                    foreach (var dateDir in topDir.GetDirectories())
                    {
                        foreach (var f in dateDir.GetFiles("*.sv"))
                        {
                            candidates.Add((f, topDir.Name, dateDir.Name));
                        }
                    }
                }

                int migrated = 0;
                foreach (var item in candidates)
                {
                    if (TryArchiveSlotCandidate(slotsRoot, item.file, item.topFolder, item.dateFolder))
                    {
                        migrated++;
                    }
                }

                if (migrated > 0)
                {
                    CleanupEmptyDirectories(slotsRoot, includeRoot: false);
                    ModLog.Info($"启动归档：已为 {migrated} 个旧槽位整理迁移至 runId/日期 目录");
                }
            }
            catch (Exception ex)
            {
                ModLog.Warning($"旧版槽位启动归档异常：{ex.Message}");
            }
        }

        private static bool TryArchiveSlotCandidate(string slotsRoot, FileInfo fi, string topFolder, string dateFolder)
        {
            string originalPath = fi.FullName;
            var sidecar = SlotSidecar.LoadOrEmpty(originalPath);
            if (sidecar.RunId != 0
                && !SaveStore.IsNoDateFolder(dateFolder)
                && string.Equals(topFolder, sidecar.RunId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
            {
                return false;
            }

            int runId = sidecar.RunId != 0 ? sidecar.RunId : SaveStore.ReadRunIdFromSv(originalPath);
            if (runId == 0) return false;

            string runIdStr = runId.ToString(CultureInfo.InvariantCulture);
            string targetDate = SaveStore.IsNoDateFolder(dateFolder)
                ? fi.LastWriteTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : dateFolder;
            string targetDir = Path.Combine(slotsRoot, runIdStr, targetDate);
            string movedTo = null;
            try
            {
                Directory.CreateDirectory(targetDir);
                string targetPath = Path.Combine(targetDir, fi.Name);
                string stem = Path.GetFileNameWithoutExtension(fi.Name);
                int n = 1;
                while (File.Exists(targetPath) && !SaveStore.PathsEqual(targetPath, originalPath))
                {
                    targetPath = Path.Combine(targetDir, stem + "-" + n + ".sv");
                    n++;
                }

                bool moved = false;
                if (!SaveStore.PathsEqual(targetPath, originalPath))
                {
                    MoveFileSafe(originalPath, targetPath);
                    movedTo = targetPath;
                    MoveCompanionIfExists(SlotSidecar.SidecarPath(originalPath), SlotSidecar.SidecarPath(targetPath));
                    MoveCompanionIfExists(MpPersistentIdMapper.RulesBackupPath(originalPath), MpPersistentIdMapper.RulesBackupPath(targetPath));
                    moved = true;
                }

                if (sidecar.RunId == 0)
                {
                    sidecar.RunId = runId;
                    sidecar.Save(targetPath);
                }
                return moved;
            }
            catch (Exception ex)
            {
                if (movedTo != null && File.Exists(movedTo) && !File.Exists(originalPath))
                {
                    try { MoveFileSafe(movedTo, originalPath); } catch { }
                }
                ModLog.Warning($"归档迁移槽位失败 {originalPath} → runId={runId}: {ex.Message}");
                return false;
            }
        }

        private static bool IsDateFolderName(string name)
            => !string.IsNullOrEmpty(name)
                && name.Length == 10
                && DateTime.TryParseExact(name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

        private static void MoveCompanionIfExists(string oldPath, string newPath)
        {
            if (string.IsNullOrEmpty(oldPath) || string.IsNullOrEmpty(newPath)) return;
            if (!File.Exists(oldPath) || File.Exists(newPath)) return;
            try { MoveFileSafe(oldPath, newPath); }
            catch (Exception ex) { ModLog.Warning($"迁移伴生文件失败 {oldPath}: {ex.Message}"); }
        }

        private static void MoveFileSafe(string src, string dst)
        {
            try
            {
                File.Move(src, dst);
            }
            catch (IOException)
            {
                File.Copy(src, dst, overwrite: false);
                File.Delete(src);
            }
        }

        internal static void CleanupEmptyDirectories(string rootDir, bool includeRoot)
        {
            try
            {
                if (!Directory.Exists(rootDir)) return;
                string[] subDirs = Directory.GetDirectories(rootDir, "*", SearchOption.AllDirectories);
                Array.Sort(subDirs, (a, b) => b.Length.CompareTo(a.Length));
                for (int i = 0; i < subDirs.Length; i++)
                {
                    try
                    {
                        if (Directory.Exists(subDirs[i]) && Directory.GetFileSystemEntries(subDirs[i]).Length == 0)
                        {
                            Directory.Delete(subDirs[i], recursive: false);
                        }
                    }
                    catch { }
                }
                if (includeRoot && Directory.Exists(rootDir) && Directory.GetFileSystemEntries(rootDir).Length == 0)
                {
                    try { Directory.Delete(rootDir, recursive: false); } catch { }
                }
            }
            catch { }
        }
    }
}
