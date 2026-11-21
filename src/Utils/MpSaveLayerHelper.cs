using System;
using System.IO;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>读取存档持久化的 totalTraveled 层深，并在快照后规范化 mp_save 内各玩家存档的 biome 字段。</summary>
    internal static class MpSaveLayerHelper
    {
        internal static int ReadTotalTraveledFromSv(string fullPath)
        {
            try
            {
                if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath)) return 0;
                string json = SaveSystem.Unzip(File.ReadAllBytes(fullPath));
                return TopJson.TryReadInt(json, "totalTraveled", out int v) ? v : 0;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>读档或回档前从磁盘存档读取 totalTraveled，供层种子计算。</summary>
        internal static int ReadPersistedTotalTraveled()
        {
            if (!SaveSystem.loadedRun) return 0;
            string path = MpSaveLocator.IsMultiplayerSaveActive()
                ? (MpSaveLocator.ResolveOwnSavePath() ?? MpSaveLocator.ResolveLocalPlayerSavePath())
                : SaveStore.ResolveActiveSingleplayerSavePath();
            if (path == null || !File.Exists(path)) return 0;
            int v = ReadTotalTraveledFromSv(path);
            ModLog.Debug($"层深：save.sv totalTraveled={v}（{path}）");
            return v;
        }

        /// <summary>保存快照后按需将 mp_save 内各玩家 save.sv 的 biome 原子规范回当前层，并记录 mp_rules.json 状态。</summary>
        internal static void NormalizeAfterSnapshot(int biomeDepth, bool rewriteBiome)
        {
            if (!MpSaveLocator.HasMpSave()) return;
            try
            {
                if (rewriteBiome)
                {
                    if (Directory.Exists(MpSaveLocator.MpSaveDir))
                    {
                        foreach (var dir in Directory.GetDirectories(MpSaveLocator.MpSaveDir))
                        {
                            string sv = Path.Combine(dir, "save.sv");
                            if (File.Exists(sv)) NormalizeSaveBiomeField(sv, biomeDepth);
                        }
                    }
                    string rootSv = Path.Combine(MpSaveLocator.MpSaveDir, "save.sv");
                    if (File.Exists(rootSv)) NormalizeSaveBiomeField(rootSv, biomeDepth);
                }
                ReportMpRules("保存快照");
            }
            catch (Exception ex)
            {
                ModLog.Warning($"NormalizeAfterSnapshot 失败：{ex.Message}");
            }
        }

        /// <summary>记录 mp_rules.json 的原始层状态；LEVEL 与 MODIFIER 由联机模组管理，本模组不改写。</summary>
        private static void ReportMpRules(string stage)
        {
            ModLog.Debug($"mp_rules 原值（{stage}）：{MpRulesJson.Describe(MpSaveLocator.MpRulesPath)}；保持 LEVEL/MODIFIER 原值");
        }

        internal static void NormalizeSaveBiomeField(string fullPath, int biomeDepth)
            => SaveStore.NormalizeSaveBiome(fullPath, biomeDepth);
    }
}
