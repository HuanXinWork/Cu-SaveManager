using System;
using System.Reflection;
using CasualtiesUnknown.SaveManager.Interop;
using HarmonyLib;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>联机主机读档切入世界前，将本地存档角色同步写入 <c>PreRunScript.characterSelected</c> 与 <c>Together.ScavPlayer.LocalPlayer.selectedCharacter</c>。</summary>
    internal static class MpCharacterRestore
    {
        private static bool _patched;

        internal static void ResetPatchState() => _patched = false;

        internal static void TryPatch() => TryPatch(HarmonyBootstrap.Instance);

        internal static void TryPatch(Harmony harmony)
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

                Harmony targetHarmony = harmony ?? HarmonyBootstrap.Instance;
                Patches info = Harmony.GetPatchInfo(load);
                if (info?.Prefixes != null)
                {
                    foreach (Patch patch in info.Prefixes)
                    {
                        if (patch.owner == targetHarmony.Id && patch.PatchMethod?.Name == nameof(Prefix))
                        {
                            _patched = true;
                            return;
                        }
                    }
                }

                targetHarmony.Patch(load, prefix: new HarmonyMethod(
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

            RestoreSavedCharacter(PreRunScript.instance);
        }

        private static void RestoreSavedCharacter(PreRunScript pre)
        {
            try
            {
                PlayerCharacterBridge.Apply(
                    MpSaveLocator.ResolveOwnSavePath() ?? MpSaveLocator.ResolveLocalPlayerSavePath(),
                    pre);
            }
            catch (Exception ex)
            {
                ModLog.Warning($"读档角色兜底失败：{ex.Message}");
            }
        }
    }
}
