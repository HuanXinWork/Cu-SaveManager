using System;
using System.Reflection;
using CasualtiesUnknown.SaveManager.Interop;
using HarmonyLib;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>self 引擎启用时拦截 QoL SeededRunPatcher 的世界生成门控与出生防卡逻辑。</summary>
    internal static class QolSpawnSuppressor
    {
        private static readonly string[] SpawnGuardMethodNames =
        {
            "FinishWorldGeneration_Completed",
            "FinishWorldGeneration_RngPostfix",
            "FinishWorldGeneration_Postfix",
        };

        private static bool _patched;
        private static bool _absentCached;

        internal static void ResetPatchState()
        {
            _patched = false;
            _absentCached = false;
        }

        internal static void TryPatch()
        {
            if (_patched || _absentCached) return;
            if (!QolBridge.IsQolPresent())
            {
                if (InteropBootstrap.PluginScanComplete) _absentCached = true;
                return;
            }
            try
            {
                Type t = InteropBootstrap.Qol.TryResolveType("QoL_Unknown.SeededRunPatcher")
                    ?? AccessTools.TypeByName("QoL_Unknown.SeededRunPatcher");
                if (t == null)
                {
                    if (InteropBootstrap.PluginScanComplete) _absentCached = true;
                    return;
                }

                Harmony harmony = HarmonyBootstrap.Instance;
                var skipPrefix = new HarmonyMethod(typeof(QolSpawnSuppressor).GetMethod(
                    nameof(SkipWhenSelfEngine), BindingFlags.Static | BindingFlags.NonPublic));

                int guardPatched = 0;
                for (int i = 0; i < SpawnGuardMethodNames.Length; i++)
                {
                    MethodInfo m = t.GetMethod(SpawnGuardMethodNames[i],
                        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                    if (m != null)
                    {
                        if (!HasPrefix(m, harmony.Id, nameof(SkipWhenSelfEngine)))
                        {
                            harmony.Patch(m, prefix: skipPrefix);
                        }
                        guardPatched++;
                    }
                }
                if (guardPatched == 0)
                {
                    ModLog.Warning("QoL SeededRunPatcher 出生防卡回调未找到，仅由本模组处理");
                }

                MethodInfo worldgenGate = t.GetMethod("ShouldUseQoLSeededWorldgen",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                if (worldgenGate != null)
                {
                    if (!HasPrefix(worldgenGate, harmony.Id, nameof(OverrideWorldgenGate)))
                    {
                        harmony.Patch(worldgenGate, prefix: new HarmonyMethod(typeof(QolSpawnSuppressor).GetMethod(
                            nameof(OverrideWorldgenGate), BindingFlags.Static | BindingFlags.NonPublic)));
                    }
                }
                else
                {
                    ModLog.Warning("QoL ShouldUseQoLSeededWorldgen 未找到，世界生成门控接管未生效");
                }

                _patched = true;
                if (guardPatched > 0 || worldgenGate != null)
                {
                    ModLog.Info("QoL 世界生成/出生防卡接管已挂载（仅 self 引擎启用时生效）");
                }
            }
            catch (Exception ex)
            {
                ModLog.Warning($"QolSpawnSuppressor.TryPatch 失败：{ex.Message}");
            }
        }

        private static bool HasPrefix(MethodBase target, string ownerId, string methodName)
        {
            Patches info = Harmony.GetPatchInfo(target);
            if (info?.Prefixes == null) return false;
            foreach (Patch p in info.Prefixes)
            {
                if (p.owner == ownerId && p.PatchMethod?.Name == methodName) return true;
            }
            return false;
        }

        private static bool SkipWhenSelfEngine()
        {
            return !SeededWorldEngine.IsActive;
        }

        private static bool OverrideWorldgenGate(ref bool __result)
        {
            if (SeededWorldEngine.IsActive)
            {
                __result = false;
                return false;
            }
            return true;
        }
    }
}
