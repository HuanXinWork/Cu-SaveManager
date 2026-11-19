using HarmonyLib;
using UnityEngine;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>在 <c>LifepodPump.PumpLoop</c> 协程启动前将抽排水方块坐标裁剪至世界边界内，避免贴边救生舱首帧越界。</summary>
    [HarmonyPatch(typeof(LifepodPump), "PumpLoop")]
    internal static class LifepodPumpBoundsPatch
    {
        [HarmonyPrefix]
        private static void Prefix(LifepodPump __instance)
        {
            var world = WorldGeneration.world;
            if (world == null || __instance == null || world.width <= 0 || world.height <= 0) return;
            int maxX = (int)world.width - 1;
            int maxY = (int)world.height - 1;
            __instance.pumpMin = Clamp(__instance.pumpMin, maxX, maxY);
            __instance.pumpMax = Clamp(__instance.pumpMax, maxX, maxY);
            __instance.pumpOut = Clamp(__instance.pumpOut, maxX, maxY);
        }

        private static Vector2Int Clamp(Vector2Int v, int maxX, int maxY)
            => new Vector2Int(Mathf.Clamp(v.x, 0, maxX), Mathf.Clamp(v.y, 0, maxY));
    }
}
