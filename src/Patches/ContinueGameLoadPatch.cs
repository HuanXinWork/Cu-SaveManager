using System;
using System.IO;
using HarmonyLib;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>
    /// 读档前按存档恢复所选角色；非联机会话时再按单机 save.sv 准备世界与 sidecar。
    /// </summary>
    /// <remarks>
    /// 原版只在 PreRunScript.LoadRun 方法体里做 characterSelected = savedCharacter。本模组会用 Prefix 返回 false
    /// 接管读档，面板也可能在 PreRunScript.Start 之后才换档，少了这步 PlayerCamera.Awake 就永远生成 Experiment。
    /// </remarks>
    [HarmonyPatch(typeof(PreRunScript), "LoadRun")]
    [HarmonyPriority(Priority.First)]
    internal static class ContinueGameLoadPatch
    {
        private static void Prefix(PreRunScript __instance)
        {
            try
            {
                ModLog.Flow("LoadRun", "01", "入口 mpPresent=" + MultiplayerBridge.IsModPresent()
                    + " mpEnabled=" + MultiplayerBridge.IsMultiplayerEnabled()
                    + " mpRunning=" + MultiplayerBridge.IsMultiplayerRunning()
                    + " mpServer=" + MultiplayerBridge.IsServer()
                    + " save.sv=" + File.Exists(MpSaveLocator.VanillaSavePath)
                    + " mpSave=" + MpSaveLocator.HasMpSave()
                    + " mpLocal=" + (MpSaveLocator.ResolveLocalPlayerSavePath() ?? "<none>"));

                RestoreCharacterFromSaveFile(__instance);

                if (MultiplayerBridge.IsMultiplayerRunning())
                {
                    ModLog.Flow("LoadRun", "02", "联机会话运行中 → 跳过单机世界准备（由 LoadRunPrefixReplacer 处理）");
                    return;
                }

                // 单机（含联机模组在场未开房）必须按 save.sv 准备世界：
                // 否则 LoadRunPrefixReplacer 会用旧 mp_save 覆盖引擎/种子/位置/难度。
                if (!SaveStore.CurrentSaveExists())
                {
                    ModLog.Flow("LoadRun", "03", "没有当前 save.sv → 跳过单机世界准备");
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

        /// <summary>从当前存档恢复角色（联机运行中读本地玩家 mp_save，单机读 save.sv）。</summary>
        private static void RestoreCharacterFromSaveFile(PreRunScript instance)
            => PlayerCharacterBridge.Apply(PlayerCharacterBridge.ResolveCurrentSavePath(), instance);
    }
}
