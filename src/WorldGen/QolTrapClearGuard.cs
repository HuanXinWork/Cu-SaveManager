using System;
using System.Reflection;
using CasualtiesUnknown.SaveManager.Interop;
using HarmonyLib;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>self 引擎启用时将 QoL SeededRunPatcher.RemoveNearbyTrapsLikeVanilla 转交本模组处理。</summary>
    internal static class QolTrapClearGuard
    {
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

                MethodInfo m = t.GetMethod("RemoveNearbyTrapsLikeVanilla",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                if (m == null)
                {
                    ModLog.Warning("QoL RemoveNearbyTrapsLikeVanilla 未找到，随身物品保护接管跳过");
                    _patched = true;
                    return;
                }

                Harmony harmony = HarmonyBootstrap.Instance;
                Patches info = Harmony.GetPatchInfo(m);
                bool exists = false;
                if (info?.Prefixes != null)
                {
                    foreach (Patch p in info.Prefixes)
                    {
                        if (p.owner == harmony.Id && p.PatchMethod?.Name == nameof(SafeClearInstead))
                        {
                            exists = true;
                            break;
                        }
                    }
                }

                if (!exists)
                {
                    harmony.Patch(m, prefix: new HarmonyMethod(typeof(QolTrapClearGuard).GetMethod(
                        nameof(SafeClearInstead), BindingFlags.Static | BindingFlags.NonPublic)));
                }
                _patched = true;
                ModLog.Info("QoL 出生清陷阱接管已挂载（仅 self 引擎启用时生效）");
            }
            catch (Exception ex)
            {
                ModLog.Warning($"QolTrapClearGuard.TryPatch 失败：{ex.Message}");
            }
        }

        private static bool SafeClearInstead()
        {
            if (!SeededWorldEngine.IsActive) return true;
            SelfSpawnPatcher.RemoveNearbyTrapsLikeVanilla();
            return false;
        }
    }
}
