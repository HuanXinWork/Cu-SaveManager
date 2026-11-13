using System;
using System.IO;
using System.Reflection;
using HarmonyLib;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>读档/回档前把存档顶层的 character 写回 PreRunScript，保证 PlayerCamera.Awake 生成正确角色。</summary>
    /// <remarks>
    /// 原版只在 PreRunScript.LoadRun 方法体里做 characterSelected = savedCharacter。
    /// 回档（RollbackController）和联机两阶段重载（MpRollbackController）直接切场景、绕过 LoadRun，
    /// 主菜单的 PreRunScript.Awake 又把 characterSelected 置 0，于是 Milky / Dune 会变回 Experiment。
    /// </remarks>
    internal static class PlayerCharacterBridge
    {
        /// <summary>PreRunScript.savedCharacter（私有实例字段）；原版 LoadRun 用它覆盖 characterSelected。</summary>
        private static readonly FieldInfo SavedCharacterField = AccessTools.Field(typeof(PreRunScript), "savedCharacter");

        /// <summary>该读哪个档：联机运行中用本地玩家 mp_save，否则单机 save.sv。</summary>
        internal static string ResolveCurrentSavePath()
            => MultiplayerBridge.IsMultiplayerRunning()
                ? (MpSaveLocator.ResolveLocalPlayerSavePath() ?? MpSaveLocator.VanillaSavePath)
                : MpSaveLocator.VanillaSavePath;

        /// <summary>按存档恢复角色；instance 非空时同时写私有 savedCharacter。返回生效的 character，未解析到返回 null。</summary>
        internal static int? Apply(string savePath, PreRunScript instance)
        {
            int? character = SaveStore.TryReadCharacterFromSv(savePath);
            if (character == null)
            {
                ModLog.Flow("Character", "01", "未解析到 character（无存档/旧格式）→ 保持原值 source=" + (savePath ?? "<none>"));
                return null;
            }

            PreRunScript.characterSelected = character.Value;
            bool wroteSaved = false;
            if (SavedCharacterField != null && instance != null)
            {
                SavedCharacterField.SetValue(instance, character.Value);
                wroteSaved = true;
            }

            ModLog.Flow("Character", "02", "character=" + character.Value + " source=" + savePath
                + " characterSelected=" + PreRunScript.characterSelected
                + " savedCharacter=" + (wroteSaved ? "written" : "skipped"));
            ModLog.Info($"读档角色恢复：character={character.Value}（{Path.GetFileName(savePath)}）");
            return character;
        }

        /// <summary>直接切场景的回档路径：没有 PreRunScript 实例，只写静态 characterSelected。</summary>
        internal static int? ApplyForSceneReload() => Apply(ResolveCurrentSavePath(), null);
    }
}
