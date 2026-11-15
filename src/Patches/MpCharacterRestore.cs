using System;
using System.Reflection;
using CasualtiesUnknown.SaveManager.Interop;
using HarmonyLib;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>
    /// 进世界前的角色兜底：在 Together.WorldMP.LoadVanillaGeneratedWorld(bool) 之前把存档里的角色写回
    /// PreRunScript.characterSelected 与 Together.ScavPlayer.LocalPlayer.selectedCharacter。
    /// </summary>
    /// <remarks>
    /// 需要独立兜底的原因：
    ///   · 联机读档会经过 Together.CharacterSelectWait.SeedSavedCharacters()，但它依赖联机模组的内部静态状态，
    ///     任何绕过该流程的调用（外部接管、回档、直接切场景）都会把角色留在默认值 0（Experiment）；
    ///   · Together 的 PlayerCamera_Awake_CharacterSelect_MultiplayerPatch 会用 LocalPlayer.selectedCharacter
    ///     覆盖 PreRunScript.characterSelected，只写后者会被盖回去。
    /// 因此这里在最早的公共入口（世界加载前）一次性钉死两边，不依赖联机模组内部流程怎么变。
    /// 只在 loadsave=true 且联机运行中生效；新开局不写，避免把上一局角色带进新局。
    /// </remarks>
    internal static class MpCharacterRestore
    {
        private const string HarmonyId = "com.casualtiesUnknown.saveManager.mpCharacter";

        private static bool _patched;

        /// <summary>挂载兜底 Prefix；联机模组尚未加载时留待下一次调用重试。</summary>
        internal static void TryPatch()
        {
            if (_patched)
            {
                return;
            }

            try
            {
                MethodInfo load = ResolveLoadMethod();
                if (load == null)
                {
                    ModLog.Debug("读档角色兜底：Together.WorldMP.LoadVanillaGeneratedWorld(bool) 尚未就绪，留待主菜单重试");
                    return;
                }

                Harmony harmony = new Harmony(HarmonyId);
                harmony.Patch(load, prefix: new HarmonyMethod(
                    typeof(MpCharacterRestore).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic)));
                _patched = true;
                ModLog.Info("读档角色兜底已挂载（Together.WorldMP.LoadVanillaGeneratedWorld）");
            }
            catch (Exception ex)
            {
                ModLog.Warning($"MpCharacterRestore.TryPatch 失败：{ex.Message}");
            }
        }

        private static MethodInfo ResolveLoadMethod()
        {
            Type worldMp = TogetherReflection.FindType("WorldMP");
            return worldMp == null ? null : InteropReflection.FindMethod(worldMp, "LoadVanillaGeneratedWorld", typeof(bool));
        }

        /// <summary>世界加载前把存档角色钉回两边；取不到角色时保持原值，不阻塞读档。</summary>
        /// <remarks>
        /// 只在主机侧生效：客户端角色由服务器通过 CharacterSelectWait 广播后写入 LocalPlayer，
        /// 客户端本地若又拿自己的 mp_save 覆盖，反而会与主机不一致。
        /// 形参名故意用 __0：按位置注入不依赖目标参数名，联机模组重命名参数也不会让补丁失效。
        /// </remarks>
        private static void Prefix(bool __0)
        {
            bool loadsave = __0;
            if (!loadsave || !MultiplayerBridge.IsMultiplayerRunning() || !MultiplayerBridge.IsServer())
            {
                return;
            }

            // 专用服务端没有本地角色，ResolveLocalPlayerSavePath 会随手拿到某个玩家子目录，
            // 写 PreRunScript 也无意义，直接跳过。
            if (MultiplayerBridge.IsDedicatedServer())
            {
                return;
            }

            try
            {
                string savePath = MpSaveLocator.ResolveOwnSavePath();
                int? character = SaveStore.TryReadCharacterWithSidecar(savePath);
                if (character == null)
                {
                    ModLog.Info("读档角色兜底跳过：本地玩家存档里没有可解析的 character");
                    return;
                }

                PreRunScript.characterSelected = character.Value;
                bool applied = InteropBootstrap.Together.TryApplyCharacterToLocalPlayer(character.Value);
                ModLog.Info($"读档角色兜底：character={character.Value} path={savePath ?? "<none>"} "
                    + $"localPlayer={(applied ? "ok" : "skip")}");
            }
            catch (Exception ex)
            {
                ModLog.Warning($"读档角色兜底失败：{ex.Message}");
            }
        }
    }
}
