using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using CasualtiesUnknown.SaveManager.Interop;
using HarmonyLib;
using UnityEngine;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>self 引擎世界生成确定化补丁：按层种子隔离生成协程每步 MoveNext 与同步子方法的随机流。</summary>
    internal static class SeededWorldPatcher
    {
        private static bool _patched;
        private static readonly HashSet<MethodBase> PatchedTargets = new HashSet<MethodBase>();

        internal static void ResetPatchState()
        {
            _patched = false;
            PatchedTargets.Clear();
        }

        private static int _noiseGenStep;
        private static int _distributeEntitiesCallIndex;
        private static int _placeCrystalsCallIndex;

        private static void ResetGenerationCounters()
        {
            _noiseGenStep = 0;
            _distributeEntitiesCallIndex = 0;
            _placeCrystalsCallIndex = 0;
        }

        private static readonly (string method, int offset)[] CoroutinePhases =
        {
            ("InstantiateWorld", 500),
            ("GenerateWorld", 1000),
            ("WorldPreprocess", 2000),
            ("WorldCreateBackground", 3000),
            ("WorldGenerateTerrain", 4000),
            ("WorldGenerateWorldBorders", 4500),
            ("WorldGenerateStructures", 5000),
            ("WorldPlacePlayer", 6000),
            ("WorldPlaceChip", 6500),
            ("WorldPlaceEntities", 7000),
            ("FinishWorldGeneration", 8000),
        };

        private static readonly (string method, int offset)[] SyncPhases =
        {
            ("GenerateOres", 5400),
            ("PlaceCrystals", 9100),
            ("DistributeMiniBarrels", 9200),
        };

        private struct RngScope
        {
            public bool Active;
            public UnityEngine.Random.State Saved;
        }

        private sealed class SeededPhaseEnumerator : IEnumerator, IDisposable
        {
            private readonly IEnumerator _inner;
            private readonly int _offset;
            private readonly bool _resetNoise;
            private readonly bool _resetSubCounters;
            private bool _initialized;
            private UnityEngine.Random.State _phaseState;

            internal SeededPhaseEnumerator(IEnumerator inner, int offset, bool resetNoise, bool resetSubCounters)
            {
                _inner = inner;
                _offset = offset;
                _resetNoise = resetNoise;
                _resetSubCounters = resetSubCounters;
            }

            public object Current => _inner.Current;

            public bool MoveNext()
            {
                if (!On)
                {
                    return _inner.MoveNext();
                }

                UnityEngine.Random.State prev = UnityEngine.Random.state;
                try
                {
                    if (!_initialized)
                    {
                        _initialized = true;
                        if (_resetNoise) _noiseGenStep = 0;
                        if (_resetSubCounters)
                        {
                            _distributeEntitiesCallIndex = 0;
                            _placeCrystalsCallIndex = 0;
                        }
                        UnityEngine.Random.InitState(SeededWorldEngine.LayerSeed() + _offset);
                    }
                    else
                    {
                        UnityEngine.Random.state = _phaseState;
                    }

                    return _inner.MoveNext();
                }
                finally
                {
                    _phaseState = UnityEngine.Random.state;
                    UnityEngine.Random.state = prev;
                }
            }

            public void Reset() => _inner.Reset();
            public void Dispose() => (_inner as IDisposable)?.Dispose();
        }

        private static bool On => SeededWorldEngine.IsActive
            && (!MultiplayerBridge.IsMultiplayerRunning() || SeededWorldEngine.MpReseedEnabled);

        internal static void TryPatch(Harmony harmony)
        {
            if (_patched || harmony == null) return;
            try
            {
                var t = typeof(SeededWorldPatcher);
                bool ok = true;

                ok &= Patch(harmony, "Start", Pre(t, nameof(Start_Prefix)));

                var coPostfix = Post(t, nameof(CoroutinePhase_Postfix));
                foreach (var stage in CoroutinePhases)
                {
                    ok &= Patch(harmony, stage.method, null, coPostfix);
                }

                var syncPrefix = Pre(t, nameof(SyncPhase_Prefix));
                var scopeFinalizer = Fin(t, nameof(Scope_Finalizer));
                foreach (var stage in SyncPhases)
                {
                    ok &= Patch(harmony, stage.method, syncPrefix, null, scopeFinalizer);
                }

                ok &= Patch(harmony, "GenerateTree", Pre(t, nameof(GenerateTree_Prefix)), null, scopeFinalizer);
                ok &= Patch(harmony, "GenerateBigMushroom", Pre(t, nameof(GenerateBigMushroom_Prefix)), null, scopeFinalizer);
                ok &= Patch(harmony, "DistributeEntities", Pre(t, nameof(DistributeEntities_Prefix)), null, scopeFinalizer);
                ok &= Patch(harmony, "PlaceLiquids", Pre(t, nameof(PlaceLiquids_Prefix)), null, scopeFinalizer);
                ok &= Patch(harmony, "GenerateLifePods", Pre(t, nameof(GenerateLifePods_Prefix)), null, scopeFinalizer);
                ok &= Patch(harmony, "GenerateDropCapsules", Pre(t, nameof(GenerateDropCapsules_Prefix)), null, scopeFinalizer);
                ok &= Patch(harmony, "GenerateCollapsedPods", Pre(t, nameof(GenerateCollapsedPods_Prefix)), null, scopeFinalizer);
                // 读档或回档时原方法由 ApplyLayerModifiersRestorePatch 接管跳过，仅新开局进入本随机作用域。
                ok &= Patch(harmony, "ApplyLayerModifiers", Pre(t, nameof(ApplyLayerModifiers_Prefix)), null, scopeFinalizer);

                ok &= Patch(harmony, "ContinueRun", Pre(t, nameof(ContinueRun_Prefix)), null, scopeFinalizer);
                ok &= Patch(harmony, "RegenerateWorld", Pre(t, nameof(ResetCounters_Prefix)));
                ok &= Patch(harmony, "Clear", null, Post(t, nameof(Clear_Postfix)));

                ok &= PatchFastNoiseCtor(harmony, t);

                ok &= PatchExternal(harmony, typeof(TraderScript), "GenerateInventory",
                    Pre(t, nameof(Trader_GenerateInv_Prefix)), scopeFinalizer);
                ok &= PatchExternal(harmony, typeof(Openable), "OnUse",
                    Pre(t, nameof(Openable_OnUse_Prefix)), scopeFinalizer);

                _patched = ok;
                ModLog.Debug($"世界引擎 patch 汇总：已挂载 {PatchedTargets.Count} 个挂载点，本次全部成功={ok}");
                if (ok)
                {
                    ModLog.Info("世界引擎 patch 已挂载（协程隔离 + 同步子方法 + FastNoiseLite + 战利品确定化）");
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

        private static HarmonyMethod Fin(Type t, string name)
            => new HarmonyMethod(t.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic));

        private static bool Patch(Harmony harmony, string method, HarmonyMethod prefix,
            HarmonyMethod postfix = null, HarmonyMethod finalizer = null)
        {
            var mi = AccessTools.Method(typeof(WorldGeneration), method);
            if (mi == null)
            {
                InteropDiagnostics.WarnOnce("SeededWorldPatcher." + method, $"世界引擎 patch 跳过：WorldGeneration.{method} 未找到");
                ModLog.Debug($"挂载点 WorldGeneration.{method}：失败（方法未找到）");
                return false;
            }

            return PatchTarget(harmony, mi, prefix, postfix, finalizer);
        }

        private static bool PatchExternal(Harmony harmony, Type type, string method,
            HarmonyMethod prefix, HarmonyMethod finalizer)
        {
            var mi = AccessTools.Method(type, method);
            if (mi == null)
            {
                InteropDiagnostics.WarnOnce("SeededWorldPatcher." + method, $"战利品确定化 patch 跳过：{type?.Name}.{method} 未找到");
                ModLog.Debug($"挂载点 {type?.Name}.{method}：失败（方法未找到）");
                return false;
            }

            return PatchTarget(harmony, mi, prefix, null, finalizer);
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

            return PatchTarget(harmony, ctor, Pre(t, nameof(FastNoiseCtor_Prefix)), null, null);
        }

        private static bool PatchTarget(Harmony harmony, MethodBase target,
            HarmonyMethod prefix, HarmonyMethod postfix, HarmonyMethod finalizer)
        {
            string name = target.DeclaringType?.FullName + "." + target.Name;
            if (PatchedTargets.Contains(target))
            {
                ModLog.Debug($"挂载点 {name}：已挂载，跳过");
                return true;
            }

            harmony.Patch(target, prefix: prefix, postfix: postfix, finalizer: finalizer);
            PatchedTargets.Add(target);
            ModLog.Debug($"挂载点 {name}：成功");
            return true;
        }

        private static int CoroutineStageOffset(string name)
        {
            foreach (var s in CoroutinePhases)
            {
                if (s.method == name) return s.offset;
            }
            return 0;
        }

        private static int SyncStageOffset(string name)
        {
            foreach (var s in SyncPhases)
            {
                if (s.method == name) return s.offset;
            }
            return 0;
        }

        private static RngScope EnterScope(int seed)
        {
            var scope = new RngScope { Active = true, Saved = UnityEngine.Random.state };
            UnityEngine.Random.InitState(seed);
            return scope;
        }

        private static Exception Scope_Finalizer(Exception __exception, RngScope __state)
        {
            if (__state.Active)
            {
                UnityEngine.Random.state = __state.Saved;
            }
            return __exception;
        }

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
            ResetGenerationCounters();
            TogetherModHooksBridge.TryBindAll();
            QolSpawnSuppressor.TryPatch();
            QolTrapClearGuard.TryPatch();
            MpWorldSeedInjector.TryPatch();
            MpSeedBroadcast.TryPatch();
            MpLateSpawnSuppressor.TryPatch();
            LayerUnlockSeedPatcher.TryPatch();
            SaveStore.InvalidateRunIdCache();

            // 联机客户端的 loadedRun 恒为 false，须禁用本地 QoL 干预并完全服从服务端种子广播。
            if (MultiplayerBridge.IsMultiplayerRunning() && !MultiplayerBridge.IsServer())
            {
                QolBridge.DisableQolIntervention();
            }
            else if (!SaveSystem.loadedRun)
            {
                WorldEngineArbiter.ApplyForFreshRun();
            }

            if (SeededWorldEngine.IsActive)
            {
                ModLog.Info($"世界生成开始：loadedRun={SaveSystem.loadedRun} engine={WorldEngineArbiter.Current} seed={SeededWorldEngine.CurrentSeed} layerSeed={SeededWorldEngine.LayerSeed()}");
            }
            else
            {
                ModLog.Debug($"世界生成开始：loadedRun={SaveSystem.loadedRun} engine={WorldEngineArbiter.Current} effective={WorldEngineArbiter.ResolveEffectiveEngineName()} selfActive=false");
            }
            ModLog.Debug("世界生成种子来源=" + WorldEngineArbiter.DescribeSeedSource());
        }

        private static void CoroutinePhase_Postfix(MethodBase __originalMethod, ref IEnumerator __result)
        {
            // 不在此处早退 !On：联机第 0 层创建枚举器时尚未收齐种子包，须由 MoveNext 每步动态判定 On。
            if (__result == null || __originalMethod == null) return;
            string name = __originalMethod.Name;
            // 联机 GenerateWorld 被替换为 Together 外层握手协程，包装其枚举器会在 finally 中回滚刚同步的 Random.state。
            if (name == "GenerateWorld" && MultiplayerBridge.IsMultiplayerRunning()) return;
            bool resetNoise = name == "GenerateWorld" || name == "WorldPreprocess" || name == "WorldGenerateTerrain";
            bool resetSub = name == "GenerateWorld" || name == "WorldPreprocess";
            __result = new SeededPhaseEnumerator(__result, CoroutineStageOffset(name), resetNoise, resetSub);
        }

        private static void SyncPhase_Prefix(MethodBase __originalMethod, out RngScope __state)
        {
            __state = default;
            if (!On || __originalMethod == null) return;
            string name = __originalMethod.Name;
            int offset = SyncStageOffset(name);
            if (name == "PlaceCrystals")
            {
                offset += (_placeCrystalsCallIndex++) * 997;
            }
            __state = EnterScope(SeededWorldEngine.LayerSeed() + offset);
        }

        private static void FastNoiseCtor_Prefix(ref int seed)
        {
            if (On && WorldGeneration.world != null && WorldGeneration.world.generatingWorld)
            {
                seed = SeededWorldEngine.LayerSeed() + _noiseGenStep * 8121;
                _noiseGenStep++;
            }
        }

        private static void GenerateTree_Prefix(Vector2Int pos, out RngScope __state)
        {
            __state = On ? EnterScope(SeededWorldEngine.LayerSeed() + 5500 + pos.x * 31 + pos.y * 17) : default;
        }

        private static void GenerateBigMushroom_Prefix(Vector2Int pos, out RngScope __state)
        {
            __state = On ? EnterScope(SeededWorldEngine.LayerSeed() + 5600 + pos.x * 31 + pos.y * 17) : default;
        }

        private static void DistributeEntities_Prefix(GameObject basObj, float minPerChunk, float maxPerChunk, out RngScope __state)
        {
            __state = (On && basObj != null)
                ? EnterScope(ComposeDistributeEntitySeed(basObj, minPerChunk, maxPerChunk, _distributeEntitiesCallIndex++))
                : default;
        }

        private static void PlaceLiquids_Prefix(byte type, int maxFill, out RngScope __state)
        {
            __state = On ? EnterScope(SeededWorldEngine.LayerSeed() + 9000 + type * 100 + maxFill) : default;
        }

        private static void GenerateLifePods_Prefix(float amt, out RngScope __state)
        {
            __state = On ? EnterScope(SeededWorldEngine.LayerSeed() + 5100 + (int)(amt * 1000f)) : default;
        }

        private static void GenerateDropCapsules_Prefix(float amt, out RngScope __state)
        {
            __state = On ? EnterScope(SeededWorldEngine.LayerSeed() + 5200 + (int)(amt * 1000f)) : default;
        }

        private static void GenerateCollapsedPods_Prefix(float amt, out RngScope __state)
        {
            __state = On ? EnterScope(SeededWorldEngine.LayerSeed() + 5300 + (int)(amt * 1000f)) : default;
        }

        private static void ApplyLayerModifiers_Prefix(out RngScope __state)
        {
            __state = On ? EnterScope(SeededWorldEngine.LayerSeed() + 10000) : default;
        }

        private static void ContinueRun_Prefix(out RngScope __state)
        {
            __state = default;
            if (On)
            {
                _distributeEntitiesCallIndex = 0;
                _placeCrystalsCallIndex = 0;
                __state = EnterScope(SeededWorldEngine.LayerSeed());
            }
        }

        private static void ResetCounters_Prefix()
        {
            if (On)
            {
                ResetGenerationCounters();
            }
        }

        private static void Clear_Postfix()
        {
            if (On)
            {
                ResetGenerationCounters();
            }
        }

        private static void Trader_GenerateInv_Prefix(TraderScript __instance, out RngScope __state)
        {
            __state = default;
            if (On && __instance != null)
            {
                Vector3 p = __instance.transform.position;
                __state = EnterScope(SeededWorldEngine.LayerSeed() + __instance.character * 1000 + (int)(p.x * 10f) + (int)(p.y * 10f));
            }
        }

        private static void Openable_OnUse_Prefix(Openable __instance, out RngScope __state)
        {
            __state = default;
            if (On && __instance != null)
            {
                Vector3 p = __instance.transform.position;
                int num = (int)(p.x * 100f) ^ (int)(p.y * 100f);
                __state = EnterScope(SeededWorldEngine.LayerSeed() ^ num);
            }
        }
    }
}
