using System.IO;
using System.Reflection;
using CasualtiesUnknown.SaveManager.Interop;
using HarmonyLib;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>读档或回档前将存档顶层 character 同步写入 PreRunScript 与联机主机 LocalPlayer。</summary>
    internal static class PlayerCharacterBridge
    {
        private static readonly FieldInfo SavedCharacterField = AccessTools.Field(typeof(PreRunScript), "savedCharacter");

        /// <summary>解析当前待恢复的角色存档路径：联机取本机专属 mp_save，单机优先取 QoL 当前存档路径。</summary>
        internal static string ResolveCurrentSavePath()
        {
            if (!MultiplayerBridge.IsMultiplayerRunning())
            {
                return QolBridge.GetCurrentSaveFilePath();
            }

            return MpSaveLocator.ResolveOwnSavePath();
        }

        /// <summary>从存档读取并应用角色物种；未解析到时返回 null。</summary>
        internal static int? Apply(string savePath, PreRunScript instance)
        {
            int? character = SaveStore.TryReadCharacterWithSidecar(savePath);
            if (character == null)
            {
                ModLog.Flow("Character", "01", "未解析到 character → 保持原值 source=" + (savePath ?? "<none>"));
                return null;
            }

            PreRunScript.characterSelected = character.Value;
            bool wroteSaved = false;
            if (SavedCharacterField != null && instance != null)
            {
                SavedCharacterField.SetValue(instance, character.Value);
                wroteSaved = true;
            }

            bool wrotePlayer = MultiplayerBridge.IsServer()
                && !MultiplayerBridge.IsDedicatedServer()
                && InteropBootstrap.Together.TryApplyCharacterToLocalPlayer(character.Value);

            ModLog.Flow("Character", "02", "character=" + character.Value + " source=" + savePath
                + " characterSelected=" + PreRunScript.characterSelected
                + " savedCharacter=" + (wroteSaved ? "written" : "skipped")
                + " localPlayer=" + (wrotePlayer ? "written" : "skipped"));
            ModLog.Info($"读档角色恢复：character={character.Value}（{Path.GetFileName(savePath)}）");
            return character;
        }

        /// <summary>单机直接重载场景前恢复静态 characterSelected。</summary>
        internal static int? ApplyForSceneReload() => Apply(ResolveCurrentSavePath(), null);
    }
}
