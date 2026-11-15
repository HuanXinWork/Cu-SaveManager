using System;
using System.IO;
using System.Reflection;
using CasualtiesUnknown.SaveManager.Interop;
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

        /// <summary>该读哪个档：联机运行中用本地玩家自己的 mp_save，否则单机 save.sv。</summary>
        /// <remarks>
        /// 联机运行中取不到自己的存档时返回 null，不回落到单机 save.sv，也不拿其他玩家的子目录——
        /// 否则会把别人的角色写进本机会话。
        /// </remarks>
        internal static string ResolveCurrentSavePath()
        {
            if (!MultiplayerBridge.IsMultiplayerRunning())
            {
                return MpSaveLocator.VanillaSavePath;
            }

            return MpSaveLocator.ResolveOwnSavePath();
        }

        /// <summary>按存档恢复角色；instance 非空时同时写私有 savedCharacter。返回生效的 character，未解析到返回 null。</summary>
        internal static int? Apply(string savePath, PreRunScript instance)
        {
            int? character = SaveStore.TryReadCharacterWithSidecar(savePath);
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

            // 联机时 Together 在 PlayerCamera.Awake 前会用 LocalPlayer.selectedCharacter 覆盖 characterSelected，
            // 只写 PreRunScript 会被盖回默认角色，两边必须一起写。客户端不写：角色由服务器广播决定，
            // 客户端拿本地 mp_save 再覆盖反而会与主机不一致。
            bool wrotePlayer = MultiplayerBridge.IsServer()
                && InteropBootstrap.Together.TryApplyCharacterToLocalPlayer(character.Value);

            ModLog.Flow("Character", "02", "character=" + character.Value + " source=" + savePath
                + " characterSelected=" + PreRunScript.characterSelected
                + " savedCharacter=" + (wroteSaved ? "written" : "skipped")
                + " localPlayer=" + (wrotePlayer ? "written" : "skipped"));
            ModLog.Info($"读档角色恢复：character={character.Value}（{Path.GetFileName(savePath)}）");
            return character;
        }

        /// <summary>直接切场景的回档路径：没有 PreRunScript 实例，只写静态 characterSelected。</summary>
        internal static int? ApplyForSceneReload() => Apply(ResolveCurrentSavePath(), null);
    }
}
