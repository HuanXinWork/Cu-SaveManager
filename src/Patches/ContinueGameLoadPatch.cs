using System;
using System.IO;
using System.Reflection;
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
        /// <summary>PreRunScript.savedCharacter（私有实例字段）；原版 LoadRun 用它覆盖 characterSelected。</summary>
        private static readonly FieldInfo SavedCharacterField = AccessTools.Field(typeof(PreRunScript), "savedCharacter");

        private static void Prefix(PreRunScript __instance)
        {
            try
            {
                RestoreCharacterFromSaveFile(__instance);
                if (MultiplayerBridge.IsMultiplayerRunning()) return;
                // 单机（含联机模组在场未开房）必须按 save.sv 准备世界：
                // 否则 LoadRunPrefixReplacer 会用旧 mp_save 覆盖引擎/种子/位置/难度。
                if (!SaveStore.CurrentSaveExists()) return;
                new SaveStore().PrepareCurrentSaveForContinue();
            }
            catch (Exception ex)
            {
                ModLog.Warning($"ContinueGameLoadPatch 失败：{ex.Message}");
            }
        }

        /// <summary>从当前存档顶层 character 恢复角色：联机运行中读本地玩家 mp_save，单机读 save.sv；读不到时保持原行为。</summary>
        private static void RestoreCharacterFromSaveFile(PreRunScript instance)
        {
            string sv = MultiplayerBridge.IsMultiplayerRunning()
                ? (MpSaveLocator.ResolveLocalPlayerSavePath() ?? MpSaveLocator.VanillaSavePath)
                : MpSaveLocator.VanillaSavePath;
            int? character = SaveStore.TryReadCharacterFromSv(sv);
            if (character == null) return;

            PreRunScript.characterSelected = character.Value;
            if (SavedCharacterField != null && instance != null)
            {
                SavedCharacterField.SetValue(instance, character.Value);
            }
            ModLog.Info($"读档角色恢复：character={character.Value}（{Path.GetFileName(sv)}）");
        }
    }
}
