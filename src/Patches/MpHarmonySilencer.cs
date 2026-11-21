using System;
using System.Reflection;
using CasualtiesUnknown.SaveManager.Interop;
using HarmonyLib;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>在联机模组启用但未开房的单机态下，旁路 <c>Together</c> 对 <c>StartRun</c> 与 <c>LoadRun</c> 的单机禁玩拦截；联机运行态原样放行。</summary>
    internal static class MpHarmonySilencer
    {
        /// <summary>联机模组的 Harmony 实例 ID。</summary>
        internal const string OwnerId = "CasualtiesMP";

        private const string StartRunPatchTypeName = "PreRunScript_StartRun_MultiplayerPatch";
        private const string LoadRunPatchTypeName = "PreRunScript_LoadRun_MultiplayerPatch";

        internal static bool StartRunGuarded { get; private set; }
        internal static bool LoadRunGuarded { get; private set; }

        internal static void ResetPatchState()
        {
            StartRunGuarded = false;
            LoadRunGuarded = false;
        }

        internal static void TrySilence() => TrySilence(HarmonyBootstrap.Instance);

        internal static void TrySilence(Harmony harmony)
        {
            harmony = harmony ?? HarmonyBootstrap.Instance;
            if (TogetherReflection.ModAssembly == null)
            {
                return;
            }

            StartRunGuarded = GuardOne(harmony, StartRunPatchTypeName);
            LoadRunGuarded = GuardOne(harmony, LoadRunPatchTypeName);
        }

        private static bool GuardOne(Harmony harmony, string typeName)
        {
            try
            {
                Type type = TogetherReflection.FindType(typeName);
                if (type == null)
                {
                    InteropDiagnostics.WarnOnce("MpHarmonySilencer." + typeName,
                        "Together." + typeName + " 未解析到，无法挂载单机旁路守卫");
                    return false;
                }

                MethodInfo prefix = InteropReflection.FindMethod(type, "Prefix", new Type[] { null })
                    ?? InteropReflection.FindMethod(type, "Prefix", Type.EmptyTypes);
                if (prefix == null)
                {
                    InteropDiagnostics.WarnOnce("MpHarmonySilencer.Prefix." + typeName,
                        "Together." + typeName + ".Prefix 未解析到，无法挂载单机旁路守卫");
                    return false;
                }

                string guardName = prefix.ReturnType == typeof(bool)
                    ? nameof(BypassBoolWhenSingleplayer)
                    : nameof(BypassVoidWhenSingleplayer);

                Patches info = Harmony.GetPatchInfo(prefix);
                if (info?.Prefixes != null)
                {
                    foreach (Patch patch in info.Prefixes)
                    {
                        if (patch.owner == harmony.Id && patch.PatchMethod?.Name == guardName)
                        {
                            return true;
                        }
                    }
                }

                harmony.Patch(prefix, prefix: new HarmonyMethod(typeof(MpHarmonySilencer), guardName));
                ModLog.Info($"已挂载单机旁路守卫 Together.{typeName}.Prefix");
                return true;
            }
            catch (Exception ex)
            {
                ModLog.Warning($"挂载 Together.{typeName}.Prefix 单机旁路守卫失败：{ex.Message}");
                return false;
            }
        }

        private static bool BypassBoolWhenSingleplayer(ref bool __result)
        {
            if (!MultiplayerBridge.IsMultiplayerEnabled() || MultiplayerBridge.IsMultiplayerRunning())
            {
                return true;
            }

            __result = true;
            return false;
        }

        private static bool BypassVoidWhenSingleplayer()
            => !MultiplayerBridge.IsMultiplayerEnabled() || MultiplayerBridge.IsMultiplayerRunning();
    }
}
