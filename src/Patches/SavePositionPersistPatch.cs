using System;
using System.IO;
using HarmonyLib;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary><c>SaveSystem.SaveGame</c> 后在 QoL 恢复层深之后写入当前存档 sidecar；跳过 QoL 后台自动存档。</summary>
    [HarmonyPatch(typeof(SaveSystem), "SaveGame")]
    internal static class SaveGamePersistPositionPatch
    {
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        [HarmonyAfter("org.bepinex.plugins.qol.savesystem")]
        private static void Postfix()
        {
            if (QolBridge.IsAutosaveInProgress()) return;
            if (MultiplayerBridge.IsMultiplayerRunning() && !MultiplayerBridge.IsServer()) return;
            try { new SaveStore().PersistCurrentSaveForContinue(); }
            catch (Exception ex) { ModLog.Warning($"SaveGamePersistPositionPatch 失败：{ex.Message}"); }
        }
    }

    /// <summary>退回主菜单前（早于联机断线与 QoL 重置种子）为同局有效存档持久化最后坐标，并清理客机联机种子与会话缓存。</summary>
    [HarmonyPatch(typeof(PlayerCamera), "ToMainMenu")]
    internal static class ToMainMenuPersistPositionPatch
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        [HarmonyBefore("CasualtiesMP")]
        private static void Prefix()
        {
            // 多人回档阶段一退回主菜单时，已还原的 mp_save 与待恢复标志不得被旧现场覆写或清空。
            if (PlayerCameraToMainMenuGuard.ShouldBlock() || MpRollbackController.ReturningToMenu) return;

            try
            {
                if (ShouldPersistCurrentSave())
                    new SaveStore().PersistCurrentSaveForContinue();
            }
            catch (Exception ex)
            {
                ModLog.Warning($"ToMainMenuPersistPositionPatch 失败：{ex.Message}");
            }
            finally
            {
                // 联机客机退回主菜单时清除被动接收的服务端固定种子状态，防止污染随后开启的单机对局。
                if (MultiplayerBridge.IsMultiplayerRunning() && !MultiplayerBridge.IsServer())
                {
                    SeededWorldEngine.Deactivate();
                }
                SaveStore.InvalidateRunIdCache();
                IntroLifepodOnLoadGuardPatch.PendingSkipIntroLifepod = false;
                ApplyLayerModifiersRestorePatch.PendingIndex = -2;
                MpPositionRestorer.CancelSession();
            }
        }

        private static bool ShouldPersistCurrentSave()
        {
            if (MultiplayerBridge.IsMultiplayerRunning()) return MultiplayerBridge.IsServer();
            string spPath = SaveStore.ResolveActiveSingleplayerSavePath();
            if (!File.Exists(spPath)) return false;
            if (SaveSystem.loadedRun) return true;
            int currentRunId = SaveStore.ComputeCurrentRunId();
            return currentRunId != 0 && SaveStore.ReadRunIdFromSv(spPath) == currentRunId;
        }
    }
}
