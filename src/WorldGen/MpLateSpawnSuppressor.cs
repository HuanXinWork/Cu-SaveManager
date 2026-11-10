using System;
using System.Reflection;
using CasualtiesUnknown.SaveManager.Interop;
using HarmonyLib;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>
    /// 多人回档窗口期内抑制 Together.ServerMain.LateSpawnLocation(NetBody)，避免异步协程覆盖 MpPositionRestorer 写回的位置。
    /// </summary>
    /// <remarks>
    /// 目标方法按「名称 + 形参个数 + 剥 ref 后元素类型」匹配 Together.ServerMain.LateSpawnLocation(NetBody)，
    /// 不依赖重载顺序。联机模组不在场时跳过；成员缺失时记一次 WarnOnce 并降级为「不抑制」，
    /// 即回档期间玩家位置仍可能被晚出生逻辑覆盖。
    /// </remarks>
    internal static class MpLateSpawnSuppressor
    {
        private static bool _patched;

        internal static void TryPatch()
        {
            if (_patched)
            {
                return;
            }

            try
            {
                Type serverType = TogetherReflection.FindType("ServerMain");
                Type netBodyType = TogetherReflection.FindType("NetBody");
                if (serverType == null || netBodyType == null)
                {
                    // 联机模组尚未加载：不置 _patched，留待下次重试。
                    return;
                }

                MethodInfo lateSpawn = InteropReflection.FindMethod(serverType, "LateSpawnLocation", netBodyType);
                if (lateSpawn == null)
                {
                    InteropDiagnostics.WarnOnce("MpLateSpawnSuppressor.Member",
                        "Together.ServerMain.LateSpawnLocation(NetBody) 未解析到，回档窗口期无法抑制晚出生位置覆盖");
                    _patched = true;
                    return;
                }

                Harmony harmony = new Harmony("com.casualtiesUnknown.saveManager.mpLateSpawnSuppressor");
                harmony.Patch(lateSpawn, prefix: new HarmonyMethod(typeof(MpLateSpawnSuppressor), nameof(SkipWhenRestoring)));
                _patched = true;
                ModLog.Info("多人 LateSpawnLocation 抑制已挂载");
            }
            catch (Exception ex)
            {
                ModLog.Warning($"MpLateSpawnSuppressor.TryPatch 失败：{ex.Message}");
            }
        }

        private static bool SkipWhenRestoring()
            => !MpPositionRestorer.ActiveSession;
    }
}
