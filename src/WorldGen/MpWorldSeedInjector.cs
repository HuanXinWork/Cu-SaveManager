using System;
using System.Reflection;
using CasualtiesUnknown.SaveManager.Interop;
using HarmonyLib;
using UnityEngine;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>
    /// 多人固定世界种子注入：在 Together.LastBeforeGenerationState 的无参构造前按本模组种子重置 Random.state，
    /// 使主机广播给客户端的生成状态确定、全员世界一致。
    /// </summary>
    /// <remarks>
    /// 目标成员：Together.LastBeforeGenerationState..ctor()。该构造会捕获 UnityEngine.Random.state 并随
    /// Together.ServerMain.Server_AnnounceSeed 广播；构造前用本模组种子 InitState 即可固定该状态。
    /// 仅主机 + self 引擎生效；联机模组不在场时跳过，成员缺失时记一次 WarnOnce 并降级为「不注入」（世界按原随机状态生成）。
    /// </remarks>
    internal static class MpWorldSeedInjector
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
                Type stateType = TogetherReflection.FindType("LastBeforeGenerationState");
                if (stateType == null)
                {
                    // 联机模组尚未加载：不置 _patched，留待世界生成时重试。
                    ModLog.Debug("多人世界种子注入：Together 尚未加载，留待世界生成时重试");
                    return;
                }

                ConstructorInfo ctor = InteropReflection.FindConstructor(stateType);
                if (ctor == null)
                {
                    InteropDiagnostics.WarnOnce("MpWorldSeedInjector.Ctor",
                        "Together.LastBeforeGenerationState..ctor() 未解析到，多人固定世界种子注入不可用（客户端按联机模组广播的随机状态生成世界）");
                    ModLog.Debug("多人世界种子注入：挂载失败（Together.LastBeforeGenerationState..ctor() 未解析到）");
                    _patched = true;
                    return;
                }

                Harmony harmony = new Harmony("com.casualtiesUnknown.saveManager.mpWorldSeed");
                HarmonyMethod prefix = new HarmonyMethod(typeof(MpWorldSeedInjector).GetMethod(
                    nameof(CtorPrefix), BindingFlags.Static | BindingFlags.NonPublic));
                harmony.Patch(ctor, prefix);
                _patched = true;
                ModLog.Info("多人固定世界注入已挂载（Together.LastBeforeGenerationState..ctor）");
                ModLog.Debug("多人世界种子注入：挂载成功 " + ctor.DeclaringType?.FullName + "..ctor()");
            }
            catch (Exception ex)
            {
                ModLog.Warning($"MpWorldSeedInjector.TryPatch 失败：{ex.Message}");
            }
        }

        private static void CtorPrefix()
        {
            if (WorldEngineArbiter.Current == WorldEngine.Krok)
            {
                ModLog.Info("多人世界种子注入跳过：引擎=联机模组原生");
                ModLog.Debug("多人世界种子注入：跳过（引擎=krok，世界状态由联机模组广播）");
                return;
            }

            bool active = SeededWorldEngine.IsActive;
            bool server = MultiplayerBridge.IsServer();
            if (!active || !server)
            {
                ModLog.Info($"多人世界种子注入跳过：selfActive={active} isServer={server}");
                ModLog.Debug($"多人世界种子注入：跳过（selfActive={active} isServer={server}；不满足注入前置条件）");
                return;
            }

            // 取「最深进度」的 totalTraveled：
            //   - 初次读档时内存 totalTraveled 尚未填充（GenerateWorld 早于 TryLoadGame），须用磁盘 save.sv 值；
            //   - 进下一层（RegenerateWorld）时内存值已 IncreaseDepthByLayer 递增，而磁盘 save.sv 仍是上一层旧值。
            // 若只读磁盘旧值，进层会用上一层的种子重算 → 重新生成与所在层相同的地图，表现为“卡层”。
            // 两者取最大即可同时覆盖读档与进层：始终对应当前正在生成的这一层。
            int diskTraveled = MpSaveLayerHelper.ReadPersistedTotalTraveled();
            int memTraveled = 0;
            try
            {
                if (WorldGeneration.world != null)
                {
                    memTraveled = WorldGeneration.world.totalTraveled;
                }
            }
            catch
            {
                // 世界尚未建立时保持 0，按磁盘值计算。
            }

            int traveled = Math.Max(diskTraveled, memTraveled);
            int seed = SeededWorldEngine.CurrentSeed + traveled * 265443576;
            UnityEngine.Random.InitState(seed);
            ModLog.Info($"多人世界种子注入：seed={seed} totalTraveled={traveled}（disk={diskTraveled} mem={memTraveled}）");
            ModLog.Debug($"多人世界种子注入：成功，已用 seed={seed} 重置 Random.state（totalTraveled={traveled}）");
        }
    }
}
