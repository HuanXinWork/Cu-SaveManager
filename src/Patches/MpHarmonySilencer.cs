using System;
using System.Reflection;
using CasualtiesUnknown.SaveManager.Interop;
using HarmonyLib;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>
    /// 在联机模组 StartRun 的 Prefix 方法上再挂一层 return false 的 Prefix，让原 Prefix 体不执行。
    /// </summary>
    /// <remarks>
    /// 目标方法：Together.PreRunScript_StartRun_MultiplayerPatch.Prefix，按类型简单名在联机模组程序集内定位，
    /// 再按名称 + 形参个数取 Prefix。
    /// 为什么需要它：HarmonyX 的 Prefix 返回值只汇总成"原方法是否执行"，**不会**阻止后续 Prefix 执行
    /// （HarmonyX 2.9 HarmonyManipulator.WritePrefixes 把所有 Prefix 都调完再统一判断）。所以本模组
    /// 无法靠 return true/false 阻止联机模组的 StartRun Prefix：它在单机时会硬拦原版开局，联机时会额外
    /// 打开 Ready 大厅。这里先尝试 Unpatch，摘不掉时用本类短路原 Prefix 兜底。
    /// LoadRun 不需要它：联机读档必须放行联机模组的 Prefix。
    /// </remarks>
    internal static class MpHarmonySilencer
    {
        /// <summary>联机模组的 Harmony owner id，也是 Unpatch 使用的 owner 前缀。</summary>
        internal const string OwnerId = "CasualtiesMP";

        private const string StartRunPatchTypeName = "PreRunScript_StartRun_MultiplayerPatch";

        /// <summary>摘不掉联机模组 StartRun Prefix 时短路它；<paramref name="startRunDetached"/> 表示对应 Prefix 已被摘掉。</summary>
        internal static void TrySilence(Harmony harmony, bool startRunDetached)
        {
            if (harmony == null || TogetherReflection.ModAssembly == null)
            {
                return;
            }

            if (startRunDetached)
            {
                return;
            }

            SilenceOne(harmony, StartRunPatchTypeName);
        }

        private static void SilenceOne(Harmony harmony, string typeName)
        {
            try
            {
                Type type = TogetherReflection.FindType(typeName);
                if (type == null)
                {
                    InteropDiagnostics.WarnOnce("MpHarmonySilencer." + typeName,
                        "Together." + typeName + " 未解析到，联机模组的 StartRun Prefix 既未摘掉也无法短路（单机新开局会被它硬拦，联机新开局会进 Ready 大厅）");
                    return;
                }

                MethodInfo prefix = InteropReflection.FindMethod(type, "Prefix", new Type[] { null });
                if (prefix == null)
                {
                    InteropDiagnostics.WarnOnce("MpHarmonySilencer.Prefix." + typeName,
                        "Together." + typeName + ".Prefix 未解析到，联机模组的 StartRun Prefix 既未摘掉也无法短路");
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
