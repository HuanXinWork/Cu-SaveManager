using System;
using HarmonyLib;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>
    /// Postfix patch <see cref="WorldGeneration"/>.SaveAndExit：
    /// 游戏暂停菜单自带的"保存并退出"不走本模组流程，这里在它写完 save.sv 后补建一个槽位。
    /// 方法体是 SaveGame() 后接 ToMainMenu()，场景真正卸载在本帧末，故 Postfix 里存档文件已就绪。
    /// </summary>
    [HarmonyPatch(typeof(WorldGeneration), "SaveAndExit")]
    internal static class GameSaveExitPatch
    {
        private static void Postfix()
        {
            try { Plugin.OnGameSaveAndExit(); }
            catch (Exception ex) { ModLog.Warning($"游戏保存并退出补建槽位异常：{ex.Message}"); }
        }
    }
}
