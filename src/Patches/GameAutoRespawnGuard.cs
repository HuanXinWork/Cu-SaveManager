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

    /// <summary>
    /// 死亡感知或回档执行期间阻断非回档发起的 <c>PlayerCamera.ToMainMenu</c> 自动退回主菜单。
    /// 必须先于 <c>CasualtiesMP</c> 的同名 Prefix 执行，防止全灭读秒回档前联机模组抢先注销网络实体并向客机广播 <c>CLIENT_GoBackToMainMenu</c>。
    /// </summary>
    [HarmonyPatch(typeof(PlayerCamera), "ToMainMenu")]
    [HarmonyPriority(Priority.First)]
    [HarmonyBefore("CasualtiesMP")]
    internal static class PlayerCameraToMainMenuGuard
    {
        internal static bool ShouldBlock()
            => !MpRollbackController.ReturningToMenu
               && (RollbackController.IsActiveGlobal || RollbackController.IsDeathSuspected);

        [HarmonyPrefix]
        private static bool Prefix() => !ShouldBlock();
    }
}
