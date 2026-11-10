using System;
using System.Reflection;
using CasualtiesUnknown.SaveManager.Interop;
using HarmonyLib;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>
    /// 在联机模组读档/开局 Prefix 方法上再挂一层 return false 的 Prefix，让原 Prefix 体不执行。
    /// </summary>
    /// <remarks>
    /// 目标方法：Together.PreRunScript_LoadRun_MultiplayerPatch.Prefix 与 Together.PreRunScript_StartRun_MultiplayerPatch.Prefix，
    /// 按类型简单名在联机模组程序集内定位，再按名称 + 形参个数取 Prefix。
    /// 只在 owner 前缀为 <see cref="OwnerId"/>（Together.Plugin 里 new Harmony("CasualtiesMP")）的 Unpatch 没摘掉对应
    /// Prefix 时才启用：摘掉后 PreRunScript 上不再有联机模组的 Prefix，本模组的 Prefix 才能用 return true 放行原版方法；
    /// 摘不掉时由本类短路原 Prefix，保证联机模组不会硬拦单机读档/开局。
    /// </remarks>
    internal static class MpHarmonySilencer
    {
        /// <summary>联机模组的 Harmony owner id，也是 Unpatch 使用的 owner 前缀。</summary>
        internal const string OwnerId = "CasualtiesMP";

        private const string LoadRunPatchTypeName = "PreRunScript_LoadRun_MultiplayerPatch";
        private const string StartRunPatchTypeName = "PreRunScript_StartRun_MultiplayerPatch";

        /// <summary>摘不掉联机模组 Prefix 时短路它；<paramref name="loadRunDetached"/> 与 <paramref name="startRunDetached"/> 表示对应 Prefix 已被摘掉。</summary>
        internal static void TrySilence(Harmony harmony, bool loadRunDetached, bool startRunDetached)
        {
            if (harmony == null || TogetherReflection.ModAssembly == null)
            {
                return;
            }

            if (loadRunDetached && startRunDetached)
            {
                return;
            }

            if (!loadRunDetached)
            {
                SilenceOne(harmony, LoadRunPatchTypeName);
            }

            if (!startRunDetached)
            {
                SilenceOne(harmony, StartRunPatchTypeName);
            }
        }

        private static void SilenceOne(Harmony harmony, string typeName)
        {
            try
            {
                Type type = TogetherReflection.FindType(typeName);
                if (type == null)
                {
                    InteropDiagnostics.WarnOnce("MpHarmonySilencer." + typeName,
                        "Together." + typeName + " 未解析到，联机模组读档/开局 Prefix 既未摘掉也无法短路");
                    return;
                }

                MethodInfo prefix = InteropReflection.FindMethod(type, "Prefix", new Type[] { null });
                if (prefix == null)
                {
                    InteropDiagnostics.WarnOnce("MpHarmonySilencer.Prefix." + typeName,
                        "Together." + typeName + ".Prefix 未解析到，联机模组读档/开局 Prefix 既未摘掉也无法短路");
                    return;
                }

                Patches info = Harmony.GetPatchInfo(prefix);
                if (info?.Prefixes != null)
                {
                    foreach (Patch patch in info.Prefixes)
                    {
                        if (patch.owner == harmony.Id && patch.PatchMethod?.Name == nameof(BlockMpPrefix))
                        {
                            return;
                        }
                    }
                }

                harmony.Patch(prefix, prefix: new HarmonyMethod(typeof(MpHarmonySilencer), nameof(BlockMpPrefix)));
                ModLog.Info($"已短路 Together.{typeName}.Prefix");
            }
            catch (Exception ex)
            {
                ModLog.Warning($"短路 Together.{typeName}.Prefix 失败：{ex.Message}");
            }
        }

        private static bool BlockMpPrefix() => false;
    }
}
