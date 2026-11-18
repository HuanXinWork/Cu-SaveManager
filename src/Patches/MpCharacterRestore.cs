using System;
using System.Reflection;
using CasualtiesUnknown.SaveManager.Interop;
using HarmonyLib;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>联机主机读档切入世界前，将本地存档角色同步写入 <c>PreRunScript.characterSelected</c> 与 <c>Together.ScavPlayer.LocalPlayer.selectedCharacter</c>。</summary>
    internal static class MpCharacterRestore
    {
        private const string FallbackHarmonyId = "com.casualtiesUnknown.saveManager.mpCharacter";

        private static Harmony _fallbackHarmony;

        internal static void ResetPatchState()
        {
            try { _fallbackHarmony?.UnpatchSelf(); } catch { }
            _fallbackHarmony = null;
        }

        internal static void TryPatch(Harmony harmony = null)
        {
            if (harmony != null)
            {
                ApplyLayerModifiersRestorePatch.BindHarmony(harmony);
            }

            try
            {
                MethodInfo load = ResolveLoadMethod();
                if (load == null)
                {
                    ModLog.Debug("读档角色兜底：Together.WorldMP.LoadVanillaGeneratedWorld(bool) 尚未就绪，留待主菜单重试");
                    return;
                }

                Harmony targetHarmony = harmony ?? (_fallbackHarmony ?? (_fallbackHarmony = new Harmony(FallbackHarmonyId)));
                Patches info = Harmony.GetPatchInfo(load);
                if (info?.Prefixes != null)
                {
                    foreach (Patch patch in info.Prefixes)
                    {
                        if (patch.owner == targetHarmony.Id && patch.PatchMethod?.Name == nameof(Prefix))
                        {
                            return;
                        }
                    }
                }

                targetHarmony.Patch(load, prefix: new HarmonyMethod(
                    typeof(MpCharacterRestore).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic)));
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

        private static void Prefix(bool __0)
        {
            if (!__0 || !MultiplayerBridge.IsMultiplayerRunning() || !MultiplayerBridge.IsServer())
            {
                return;
            }

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
