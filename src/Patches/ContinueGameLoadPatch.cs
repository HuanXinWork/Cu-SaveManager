using System;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>读档最前置补丁（Priority 800）：恢复存档角色，并在非联机会话下按当前单机存档与 sidecar 准备世界状态。</summary>
    [HarmonyPatch(typeof(PreRunScript), "LoadRun")]
    [HarmonyPriority(Priority.First)]
    internal static class ContinueGameLoadPatch
    {
        private static readonly FieldInfo StartedLoadingField = AccessTools.Field(typeof(PreRunScript), "startedLoading");

        internal static bool IsStartedLoading(PreRunScript instance)
            => instance != null && StartedLoadingField?.GetValue(instance) is bool v && v;

        internal static void SetStartedLoading(PreRunScript instance, bool value)
        {
            if (instance != null) StartedLoadingField?.SetValue(instance, value);
        }

        internal static bool IsTutorialIncomplete()
            => PlayerPrefs.GetInt("didbasiccourse", 0) == 0;

        internal static bool ShouldDeferToVanillaGate(PreRunScript instance)
            => instance != null && (IsStartedLoading(instance) || IsTutorialIncomplete());

        internal static bool HasSingleplayerSave()
        {
            string qolPath = QolBridge.GetCurrentSaveFilePath();
            return (!string.IsNullOrEmpty(qolPath) && File.Exists(qolPath)) || SaveStore.CurrentSaveExists();
        }

        private static void Prefix(PreRunScript __instance)
        {
            if (ShouldDeferToVanillaGate(__instance)) return;

            try
            {
                ModLog.Flow("LoadRun", "01", "入口 mpPresent=" + MultiplayerBridge.IsModPresent()
                    + " mpEnabled=" + MultiplayerBridge.IsMultiplayerEnabled()
                    + " mpRunning=" + MultiplayerBridge.IsMultiplayerRunning()
                    + " mpServer=" + MultiplayerBridge.IsServer()
                    + " save.sv=" + File.Exists(MpSaveLocator.VanillaSavePath)
                    + " qolSave=" + (QolBridge.GetCurrentSaveFilePath() ?? "<none>")
                    + " mpSave=" + MpSaveLocator.HasMpSave()
                    + " mpLocal=" + (MpSaveLocator.ResolveLocalPlayerSavePath() ?? "<none>"));

                PlayerCharacterBridge.Apply(PlayerCharacterBridge.ResolveCurrentSavePath(), __instance);

                if (MultiplayerBridge.IsMultiplayerRunning())
                {
                    ModLog.Flow("LoadRun", "02", "联机会话运行中 → 跳过单机世界准备");
                    return;
                }

                if (!HasSingleplayerSave())
                {
                    ModLog.Flow("LoadRun", "03", "没有可用单机存档 → 跳过单机世界准备");
                    return;
                }

                new SaveStore().PrepareCurrentSaveForContinue();
                ModLog.Flow("LoadRun", "04", "单机世界准备完成");
            }
            catch (Exception ex)
            {
                ModLog.Warning($"ContinueGameLoadPatch 失败：{ex.Message}");
            }
        }
    }
}
