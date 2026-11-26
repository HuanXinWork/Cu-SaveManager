using System;
using System.Collections;
using HarmonyLib;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>死亡感知或回档执行期间阻断 <c>WorldGeneration.RegenerateWorld</c> 自动重开世界，并返回空枚举器防止空协程异常。</summary>
    [HarmonyPatch(typeof(WorldGeneration), "RegenerateWorld", new[] { typeof(bool) })]
    internal static class WorldGenerationRegenerateWorldGuard
    {
        private static bool Prefix(ref IEnumerator __result)
        {
            if (RollbackController.IsActiveGlobal || RollbackController.IsDeathSuspected)
            {
                __result = Array.Empty<object>().GetEnumerator();
                return false;
            }
            return true;
        }
    }

    /// <summary>死亡感知或回档期间先于 <c>CasualtiesMP</c> 拦截非回档 <c>PlayerCamera.ToMainMenu</c>，防止联机模组抢先注销网络实体并广播全员退房。</summary>
    [HarmonyPatch(typeof(PlayerCamera), "ToMainMenu")]
    internal static class PlayerCameraToMainMenuGuard
    {
        internal static bool ShouldBlock()
            => !MpRollbackController.ReturningToMenu
               && (RollbackController.IsActiveGlobal || RollbackController.IsDeathSuspected);

        // 优先级与顺序特性必须标在方法而非类上，规避 0Harmony 2.9.0 Merge 静默丢弃类级 Priority 导致退化为 Normal (400)。
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        [HarmonyBefore("CasualtiesMP")]
        private static bool Prefix() => !ShouldBlock();
    }
}
