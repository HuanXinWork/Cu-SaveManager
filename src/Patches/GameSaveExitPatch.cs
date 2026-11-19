using System;
using HarmonyLib;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>拦截原生 <c>WorldGeneration.SaveAndExit</c>，在退回主菜单清空实时状态前抓拍种子并在实际执行后补建槽位。</summary>
    [HarmonyPatch(typeof(WorldGeneration), "SaveAndExit")]
    internal static class GameSaveExitPatch
    {
        private static void Prefix()
        {
            try { Plugin.OnBeforeGameSaveAndExit(); }
            catch (Exception ex) { ModLog.Warning($"游戏保存并退出前置抓拍异常：{ex.Message}"); }
        }

        private static void Postfix(bool __runOriginal)
        {
            if (!__runOriginal)
            {
                Plugin.ClearBeforeGameSaveAndExit();
                return;
            }
            try { Plugin.OnGameSaveAndExit(); }
            catch (Exception ex) { ModLog.Warning($"游戏保存并退出补建槽位异常：{ex.Message}"); }
        }

        private static void Finalizer()
        {
            Plugin.ClearBeforeGameSaveAndExit();
        }
    }
}
