using System;
using HarmonyLib;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>
    /// 单机 Continue：LoadRun 前准备 sidecar。多人 LoadRun 由 LoadRunPrefixReplacer 接管。
    /// </summary>
    [HarmonyPatch(typeof(PreRunScript), "LoadRun")]
    [HarmonyPriority(Priority.First)]
    internal static class ContinueGameLoadPatch
    {
        private static void Prefix()
        {
            try
            {
                if (MultiplayerBridge.IsMultiplayerEnabled()) return;
                if (MultiplayerBridge.IsMultiplayerRunning()) return;
                if (!SaveStore.CurrentSaveExists()) return;
                new SaveStore().PrepareCurrentSaveForContinue();
            }
            catch (Exception ex)
            {
                ModLog.Warning($"ContinueGameLoadPatch 失败：{ex.Message}");
            }
        }
    }
}
