using HarmonyLib;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>监听主相机 <c>PlayerCamera.HandleDeathScreen</c> 状态变化，提供本地玩家死亡屏锁存标志。</summary>
    [HarmonyPatch(typeof(PlayerCamera), "HandleDeathScreen")]
    internal static class PlayerCameraDeathDetector
    {
        private static bool _lastDidDeathScreen;

        internal static bool LocalDeathLatched { get; private set; }

        private static void Postfix(PlayerCamera __instance)
        {
            if (__instance == null) return;
            if (PlayerCamera.main != null && __instance != PlayerCamera.main) return;
            bool now = __instance.didDeathScreen;
            if (now == _lastDidDeathScreen) return;
            _lastDidDeathScreen = now;
            LocalDeathLatched = now;
        }

        internal static void Reset()
        {
            _lastDidDeathScreen = false;
            LocalDeathLatched = false;
        }
    }
}
