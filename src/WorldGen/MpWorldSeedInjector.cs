using System;
using System.Reflection;
using CasualtiesUnknown.SaveManager.Interop;
using HarmonyLib;
using UnityEngine;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>主机启用 self 引擎时在 Together.LastBeforeGenerationState..ctor() 前重置随机状态，固定初始广播种子。</summary>
    internal static class MpWorldSeedInjector
    {
        private static bool _patched;

        internal static void ResetPatchState() => _patched = false;

        internal static void TryPatch()
        {
            if (_patched) return;
            TogetherModHooksBridge.TryBindAll();
            if (TogetherModHooksBridge.HasWorldSeedHook)
            {
                _patched = true;
                ModLog.Info("多人固定世界注入已由 Together.ModHooks.CustomWorldSeedProvider 原生接管");
                return;
            }

            try
            {
                Type stateType = TogetherReflection.FindType("LastBeforeGenerationState");
                if (stateType == null)
                {
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
            if (TogetherModHooksBridge.HasWorldSeedHook) return;
            if (WorldEngineArbiter.Current == WorldEngine.Krok)
            {
                ModLog.Debug("多人世界种子注入：跳过（引擎=krok）");
                return;
            }

            bool active = SeededWorldEngine.IsActive;
            bool server = MultiplayerBridge.IsServer();
            if (!active || !server)
            {
                ModLog.Debug($"多人世界种子注入：跳过（selfActive={active} isServer={server}）");
                return;
            }

            // 初次读档时 GenerateWorld 早于 TryLoadGame（内存为 0，须取磁盘值）；跨层进下一层时内存已递增而磁盘仍为旧值。
            // 取两者较大值可同时保证读档与跨层均对应当前待生成层，避免重算上一层种子导致卡层。
            int diskTraveled = MpSaveLayerHelper.ReadPersistedTotalTraveled();
            int memTraveled = 0;
            try
            {
                if (WorldGeneration.world != null)
                {
                    memTraveled = WorldGeneration.world.totalTraveled;
                }
            }
            catch { }

            int traveled = Math.Max(diskTraveled, memTraveled);
            int seed = SeededWorldEngine.CurrentSeed + traveled * 265443576;
            UnityEngine.Random.InitState(seed);
            ModLog.Info($"多人世界种子注入：seed={seed} totalTraveled={traveled}（disk={diskTraveled} mem={memTraveled}）");
        }
    }
}
