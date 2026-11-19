using System;
using System.Reflection;
using HarmonyLib;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>self 引擎启用时将 QoL SeededRunPatcher.RemoveNearbyTrapsLikeVanilla 转交本模组处理。</summary>
    internal static class QolTrapClearGuard
    {
        private static bool _patched;

        internal static void ResetPatchState() => _patched = false;

        internal static void TryPatch()
        {
            if (_patched) return;
            try
            {
                Type t = AccessTools.TypeByName("QoL_Unknown.SeededRunPatcher");
                if (t == null) return;

                MethodInfo m = t.GetMethod("RemoveNearbyTrapsLikeVanilla",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                if (m == null)
                {
                    ModLog.Warning("QoL RemoveNearbyTrapsLikeVanilla 未找到，随身物品保护接管跳过");
                    _patched = true;
                    return;
                }

                var harmony = new Harmony("com.casualtiesUnknown.saveManager.qolTrapClearGuard");
                harmony.Patch(m, prefix: new HarmonyMethod(typeof(QolTrapClearGuard).GetMethod(
                    nameof(SafeClearInstead), BindingFlags.Static | BindingFlags.NonPublic)));
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
