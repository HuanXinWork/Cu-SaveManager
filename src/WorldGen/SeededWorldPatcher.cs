using System;
using System.Collections.Generic;
using System.Reflection;
using CasualtiesUnknown.SaveManager.Interop;
using HarmonyLib;
using UnityEngine;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>
    /// 在 WorldGeneration 各生成阶段 Prefix 用 LayerSeed+阶段偏移重置 UnityEngine.Random，
    /// 使地形与实体分布可复现。仅 SeededWorldEngine.IsActive 且单机时介入。
    /// 显式逐方法挂载，避免 HarmonyX 对多目标特性类自动发现失效。
    /// PlaceLiquids、各 Pod、ApplyLayerModifiers 等）逐一即时重置，保证同种子下世界可复现。
    /// </summary>
    internal static class SeededWorldPatcher
    {
        private static bool _patched;

        /// <summary>已挂过补丁的目标方法，重试时据此去重，避免同一方法上叠加多层 prefix。</summary>
        private static readonly HashSet<MethodBase> PatchedTargets = new HashSet<MethodBase>();

        // 噪声步进 / DistributeEntities 调用序号计数器。
        private static int _noiseGenStep;
        private static int _distributeEntitiesCallIndex;

        private static readonly (string method, int offset)[] PhaseStages =
        {
            ("GenerateWorld", 1000),
            ("WorldPreprocess", 2000),
            ("WorldCreateBackground", 3000),
            ("WorldGenerateTerrain", 4000),
            ("WorldGenerateWorldBorders", 4500),
            ("WorldGenerateStructures", 5000),
            ("WorldPlacePlayer", 6000),
            ("WorldPlaceEntities", 7000),
            ("FinishWorldGeneration", 8000),
            ("InstantiateWorld", 500),
            ("GenerateOres", 5400),
            ("PlaceCrystals", 9100),
            ("DistributeMiniBarrels", 9200),
        };

        // 单机：self 引擎激活即介入。多人：仅在拿到主机广播的固定世界种子后介入（MpReseedEnabled），
        // 使全员逐阶段重置、世界与同种子单机一致；联机模组原生世界则不介入。
        private static bool On => SeededWorldEngine.IsActive
            && (!MultiplayerBridge.IsMultiplayerRunning() || SeededWorldEngine.MpReseedEnabled);

        /// <summary>显式挂载世界生成各阶段 Prefix；幂等，且只有全部目标挂载成功才置 <see cref="_patched"/>。</summary>
        /// <remarks>目标缺失时保持未完成状态，下次调用重试；已挂载的目标由 <see cref="PatchedTargets"/> 去重。</remarks>
        internal static void TryPatch(Harmony harmony)
        {
            if (_patched) return;
            try
            {
                var t = typeof(SeededWorldPatcher);
                bool ok = true;

                ok &= Patch(harmony, "Start", Pre(t, nameof(Start_Prefix)));

                // 协程阶段：作为基线重置（首个 yield 之前的同步抽随机有效）。
                var phasePrefix = Pre(t, nameof(Phase_Prefix));
                foreach (var stage in PhaseStages) ok &= Patch(harmony, stage.method, phasePrefix);

                // 协程内同步子方法：yield 之后才执行，必须各自即时重置（这才是真正决定确定性的部分）。
                ok &= Patch(harmony, "GenerateTree", Pre(t, nameof(GenerateTree_Prefix)));
                ok &= Patch(harmony, "GenerateBigMushroom", Pre(t, nameof(GenerateBigMushroom_Prefix)));
                ok &= Patch(harmony, "DistributeEntities", Pre(t, nameof(DistributeEntities_Prefix)));
                ok &= Patch(harmony, "PlaceLiquids", Pre(t, nameof(PlaceLiquids_Prefix)));
                ok &= Patch(harmony, "GenerateLifePods", Pre(t, nameof(GenerateLifePods_Prefix)));
                ok &= Patch(harmony, "GenerateDropCapsules", Pre(t, nameof(GenerateDropCapsules_Prefix)));
                ok &= Patch(harmony, "GenerateCollapsedPods", Pre(t, nameof(GenerateCollapsedPods_Prefix)));
                // 与读档还原补丁 ApplyLayerModifiersRestorePatch 共存：读/回档时原方法被 return false 跳过，
                // 此处重置无害；新开局时为随机选取提供确定性。
                ok &= Patch(harmony, "ApplyLayerModifiers", Pre(t, nameof(ApplyLayerModifiers_Prefix)));

                // 进层 / 重生成 / 清理：重置计数器与种子。
                ok &= Patch(harmony, "ContinueRun", Pre(t, nameof(ContinueRun_Prefix)));
                ok &= Patch(harmony, "RegenerateWorld", Pre(t, nameof(ResetCounters_Prefix)));
                ok &= Patch(harmony, "Clear", null, Post(t, nameof(Clear_Postfix)));

                // 地形/洞穴噪声：FastNoiseLite 构造函数是同步执行点，直接决定地图形状能否复现。
                ok &= PatchFastNoiseCtor(harmony, t);

                // 战利品确定化：商人库存 / 可开启容器（运行时触发，前后保存还原 Random.state 避免污染全局流）。
                ok &= PatchExternal(harmony, typeof(TraderScript), "GenerateInventory",
                    Pre(t, nameof(Trader_GenerateInv_Prefix)), Post(t, nameof(Trader_GenerateInv_Postfix)));
                ok &= PatchExternal(harmony, typeof(Openable), "OnUse",
                    Pre(t, nameof(Openable_OnUse_Prefix)), Post(t, nameof(Openable_OnUse_Postfix)));

                _patched = ok;
                ModLog.Debug($"世界引擎 patch 汇总：已挂载 {PatchedTargets.Count} 个挂载点，本次全部成功={ok}");
                if (ok)
                {
                    ModLog.Info("世界引擎 patch 已挂载（含同步子方法 + FastNoiseLite + 战利品确定化）");
                }
                else
                {
                    ModLog.Warning("世界引擎 patch 未全部挂载，下次世界生成时重试");
                }
            }
            catch (Exception ex)
            {
                ModLog.Warning($"SeededWorldPatcher.TryPatch 失败：{ex.Message}");
            }
        }

        private static HarmonyMethod Pre(Type t, string name)
            => new HarmonyMethod(t.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic));

        private static HarmonyMethod Post(Type t, string name)
            => new HarmonyMethod(t.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic));

        /// <summary>挂载 WorldGeneration 上的目标方法；目标缺失返回 false，已挂载过则直接返回 true。</summary>
        private static bool Patch(Harmony harmony, string method, HarmonyMethod prefix, HarmonyMethod postfix = null)
        {
            var mi = AccessTools.Method(typeof(WorldGeneration), method);
            if (mi == null)
            {
                InteropDiagnostics.WarnOnce("SeededWorldPatcher." + method, $"世界引擎 patch 跳过：WorldGeneration.{method} 未找到");
                ModLog.Debug($"挂载点 WorldGeneration.{method}：失败（方法未找到）");
                return false;
            }

            return PatchTarget(harmony, mi, prefix, postfix);
        }

        private static bool PatchExternal(Harmony harmony, Type type, string method, HarmonyMethod prefix, HarmonyMethod postfix)
        {
            var mi = AccessTools.Method(type, method);
            if (mi == null)
            {
                InteropDiagnostics.WarnOnce("SeededWorldPatcher." + method, $"战利品确定化 patch 跳过：{type?.Name}.{method} 未找到");
                ModLog.Debug($"挂载点 {type?.Name}.{method}：失败（方法未找到）");
                return false;
            }

            return PatchTarget(harmony, mi, prefix, postfix);
        }

        private static bool PatchFastNoiseCtor(Harmony harmony, Type t)
        {
            var fn = AccessTools.TypeByName("FastNoiseLite");
            if (fn == null)
            {
                InteropDiagnostics.WarnOnce("SeededWorldPatcher.FastNoiseLite", "FastNoiseLite 类型未找到，噪声确定化跳过");
                ModLog.Debug("挂载点 FastNoiseLite(int) 构造：失败（类型未找到）");
                return false;
            }

            var ctor = AccessTools.Constructor(fn, new[] { typeof(int) });
            if (ctor == null)
            {
                InteropDiagnostics.WarnOnce("SeededWorldPatcher.FastNoiseCtor", "FastNoiseLite(int) 构造未找到，噪声确定化跳过");
                ModLog.Debug("挂载点 FastNoiseLite(int) 构造：失败（构造函数未找到）");
                return false;
            }

            return PatchTarget(harmony, ctor, Pre(t, nameof(FastNoiseCtor_Prefix)), null);
        }

        /// <summary>同一目标方法只挂一次；返回该目标当前是否已挂载。</summary>
        private static bool PatchTarget(Harmony harmony, MethodBase target, HarmonyMethod prefix, HarmonyMethod postfix)
        {
            string name = target.DeclaringType?.FullName + "." + target.Name;
            if (PatchedTargets.Contains(target))
            {
                ModLog.Debug($"挂载点 {name}：已挂载，跳过");
                return true;
            }

            harmony.Patch(target, prefix, postfix);
            PatchedTargets.Add(target);
            ModLog.Debug($"挂载点 {name}：成功");
            return true;
        }

        private static int StageOffset(MethodBase __originalMethod)
        {
            string name = __originalMethod.Name;
            foreach (var s in PhaseStages) if (s.method == name) return s.offset;
            return 0;
        }

        /// <summary>稳定字符串散列（用于 DistributeEntities 种子组合）。</summary>
        private static int StableHashRaw(string str)
        {
            if (string.IsNullOrEmpty(str)) return 0;
            int num = 23;
            foreach (char c in str) num = num * 31 + c;
            return num;
        }

        private static int ComposeDistributeEntitySeed(GameObject basObj, float minPerChunk, float maxPerChunk, int callIndex)
        {
            int layerSeed = SeededWorldEngine.LayerSeed();
            int stableHash = StableHashRaw(basObj != null ? basObj.name : string.Empty);
            int a = (int)(minPerChunk * 1000f);
            int b = (int)(maxPerChunk * 100f);
            return layerSeed + stableHash + a + b + callIndex * 486187739;
        }

        private static void Start_Prefix()
        {
            QolSpawnSuppressor.TryPatch();
            QolTrapClearGuard.TryPatch();
            MpWorldSeedInjector.TryPatch();
            MpSeedBroadcast.TryPatch();
            LayerUnlockSeedPatcher.TryPatch();
            SaveStore.InvalidateRunIdCache();
            if (!SaveSystem.loadedRun) WorldEngineArbiter.ApplyForFreshRun();
            ModLog.Info($"世界生成开始：loadedRun={SaveSystem.loadedRun} engine={WorldEngineArbiter.Current} effective={WorldEngineArbiter.ResolveEffectiveEngineName()} selfActive={SeededWorldEngine.IsActive} seed={SeededWorldEngine.CurrentSeed} layerSeed={SeededWorldEngine.LayerSeed()}");
            ModLog.Debug("世界生成种子来源=" + WorldEngineArbiter.DescribeSeedSource());
        }

        private static void Phase_Prefix(MethodBase __originalMethod)
        {
            string name = __originalMethod.Name;
            if (!On) return;
            // 在世界生成 / 地形阶段开头复位噪声与实体计数器。
            if (name == "GenerateWorld") { _noiseGenStep = 0; _distributeEntitiesCallIndex = 0; }
            else if (name == "WorldGenerateTerrain") { _noiseGenStep = 0; }
            UnityEngine.Random.InitState(SeededWorldEngine.LayerSeed() + StageOffset(__originalMethod));
        }

        private static void FastNoiseCtor_Prefix(ref int seed)
        {
            if (On && WorldGeneration.world != null && WorldGeneration.world.generatingWorld)
            {
                seed = SeededWorldEngine.LayerSeed() + _noiseGenStep * 8121;
                _noiseGenStep++;
            }
        }

        private static void GenerateTree_Prefix(Vector2Int pos)
        {
            if (On) UnityEngine.Random.InitState(SeededWorldEngine.LayerSeed() + 5500 + pos.x * 31 + pos.y * 17);
        }

        private static void GenerateBigMushroom_Prefix(Vector2Int pos)
        {
            if (On) UnityEngine.Random.InitState(SeededWorldEngine.LayerSeed() + 5600 + pos.x * 31 + pos.y * 17);
        }

        private static void DistributeEntities_Prefix(GameObject basObj, float minPerChunk, float maxPerChunk)
        {
            if (On && basObj != null)
                UnityEngine.Random.InitState(ComposeDistributeEntitySeed(basObj, minPerChunk, maxPerChunk, _distributeEntitiesCallIndex++));
        }

        private static void PlaceLiquids_Prefix(byte type, int maxFill)
        {
            if (On) UnityEngine.Random.InitState(SeededWorldEngine.LayerSeed() + 9000 + type * 100 + maxFill);
        }

        private static void GenerateLifePods_Prefix(float amt)
        {
            if (On) UnityEngine.Random.InitState(SeededWorldEngine.LayerSeed() + 5100 + (int)(amt * 1000f));
        }

        private static void GenerateDropCapsules_Prefix(float amt)
        {
            if (On) UnityEngine.Random.InitState(SeededWorldEngine.LayerSeed() + 5200 + (int)(amt * 1000f));
        }

        private static void GenerateCollapsedPods_Prefix(float amt)
        {
            if (On) UnityEngine.Random.InitState(SeededWorldEngine.LayerSeed() + 5300 + (int)(amt * 1000f));
        }

        private static void ApplyLayerModifiers_Prefix()
        {
            if (On) UnityEngine.Random.InitState(SeededWorldEngine.LayerSeed() + 10000);
        }

        private static void ContinueRun_Prefix()
        {
            if (On)
            {
                _distributeEntitiesCallIndex = 0;
                UnityEngine.Random.InitState(SeededWorldEngine.LayerSeed());
            }
        }

        private static void ResetCounters_Prefix()
        {
            if (On) { _noiseGenStep = 0; _distributeEntitiesCallIndex = 0; }
        }

        private static void Clear_Postfix()
        {
            if (On) { _noiseGenStep = 0; _distributeEntitiesCallIndex = 0; }
        }

        private static void Trader_GenerateInv_Prefix(TraderScript __instance, ref UnityEngine.Random.State __state)
        {
            if (On)
            {
                __state = UnityEngine.Random.state;
                Vector3 p = __instance.transform.position;
                UnityEngine.Random.InitState(SeededWorldEngine.LayerSeed() + __instance.character * 1000 + (int)(p.x * 10f) + (int)(p.y * 10f));
            }
        }

        private static void Trader_GenerateInv_Postfix(UnityEngine.Random.State __state)
        {
            if (On) UnityEngine.Random.state = __state;
        }

        private static void Openable_OnUse_Prefix(Openable __instance, ref UnityEngine.Random.State __state)
        {
            if (On)
            {
                __state = UnityEngine.Random.state;
                Vector3 p = __instance.transform.position;
                int num = (int)(p.x * 100f) ^ (int)(p.y * 100f);
                UnityEngine.Random.InitState(SeededWorldEngine.LayerSeed() ^ num);
            }
        }

        private static void Openable_OnUse_Postfix(UnityEngine.Random.State __state)
        {
            if (On) UnityEngine.Random.state = __state;
        }
    }
}
