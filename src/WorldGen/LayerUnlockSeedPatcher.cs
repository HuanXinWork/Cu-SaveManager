using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using CasualtiesUnknown.SaveManager.Interop;
using HarmonyLib;
using UnityEngine;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>隔离 LayerUnlock 解锁层协程每步 MoveNext 的随机流，使其生成结构在确定化世界下可复现。</summary>
    internal static class LayerUnlockSeedPatcher
    {
        private static bool _patched;

        internal static void ResetPatchState() => _patched = false;

        private sealed class CoState
        {
            internal bool Active;
            internal bool Seeded;
            internal int Seed;
            internal UnityEngine.Random.State Saved;
        }

        private struct StepScope
        {
            internal bool Active;
            internal CoState Co;
            internal UnityEngine.Random.State SavedGlobal;
        }

        private static readonly ConditionalWeakTable<object, CoState> _table =
            new ConditionalWeakTable<object, CoState>();

        /// <summary>按协程类型名映射独立盐值，与 SeededWorldPatcher 的阶段偏移区间错开。</summary>
        private static int SaltFor(Type declaringType)
        {
            string n = declaringType != null ? declaringType.Name : "";
            if (n.IndexOf("GenerateStructuresAfterTerrain", StringComparison.Ordinal) >= 0) return 71000;
            if (n.IndexOf("AddStructuresForDepth", StringComparison.Ordinal) >= 0) return 72000;
            if (n.IndexOf("SpawnIonHeaters", StringComparison.Ordinal) >= 0) return 73000;
            if (n.IndexOf("SpawnSpaceHeaters", StringComparison.Ordinal) >= 0) return 74000;
            return 70000;
        }

        internal static void TryPatch()
        {
            if (_patched) return;

            try
            {
                LayerUnlockProvider provider = InteropBootstrap.LayerUnlock;
                if (!provider.IsPresent) return;

                IReadOnlyList<MethodInfo> moveNextMethods = provider.MoveNextMethods;
                if (moveNextMethods.Count == 0) return;

                Harmony harmony = HarmonyBootstrap.Instance;
                Type self = typeof(LayerUnlockSeedPatcher);
                HarmonyMethod prefix = new HarmonyMethod(self.GetMethod(nameof(MoveNext_Prefix),
                    BindingFlags.Static | BindingFlags.NonPublic));
                HarmonyMethod finalizer = new HarmonyMethod(self.GetMethod(nameof(MoveNext_Finalizer),
                    BindingFlags.Static | BindingFlags.NonPublic));

                for (int i = 0; i < moveNextMethods.Count; i++)
                {
                    MethodInfo target = moveNextMethods[i];
                    if (!IsAlreadyPatched(target, harmony.Id, nameof(MoveNext_Prefix)))
                    {
                        harmony.Patch(target, prefix: prefix, finalizer: finalizer);
                    }
                }

                _patched = true;
                ModLog.Info($"LayerUnlock 确定化已挂载（{moveNextMethods.Count} 个协程，仅确定化世界下生效）");
            }
            catch (Exception ex)
            {
                ModLog.Warning($"LayerUnlockSeedPatcher.TryPatch 失败：{ex.Message}");
            }
        }

        private static bool IsAlreadyPatched(MethodBase target, string ownerId, string methodName)
        {
            Patches info = Harmony.GetPatchInfo(target);
            if (info?.Prefixes == null) return false;
            foreach (Patch p in info.Prefixes)
            {
                if (p.owner == ownerId && p.PatchMethod?.Name == methodName) return true;
            }
            return false;
        }

        private static void MoveNext_Prefix(object __instance, MethodBase __originalMethod, out StepScope __state)
        {
            __state = default;
            if (__instance == null) return;
            if (!_table.TryGetValue(__instance, out var state))
            {
                state = CreateState(__instance, __originalMethod);
                _table.Add(__instance, state);
            }
            if (!state.Active) return;

            __state = new StepScope
            {
                Active = true,
                Co = state,
                SavedGlobal = UnityEngine.Random.state,
            };

            if (!state.Seeded)
            {
                state.Seeded = true;
                UnityEngine.Random.InitState(state.Seed);
            }
            else
            {
                UnityEngine.Random.state = state.Saved;
            }
        }

        private static Exception MoveNext_Finalizer(Exception __exception, StepScope __state)
        {
            if (__state.Active)
            {
                __state.Co.Saved = UnityEngine.Random.state;
                UnityEngine.Random.state = __state.SavedGlobal;
            }
            return __exception;
        }

        private static CoState CreateState(object instance, MethodBase originalMethod)
        {
            var co = new CoState { Active = DeterminismActive() };
            if (!co.Active) return co;
            Type declaring = originalMethod != null ? originalMethod.DeclaringType : instance.GetType();
            WorldGeneration wg = ReadWg(instance);
            int depth = ReadDepth(instance, wg);
            co.Seed = ComposeSeed(wg, depth, SaltFor(declaring));
            return co;
        }

        private static bool DeterminismActive()
        {
            try
            {
                if (SeededWorldEngine.IsActive
                    && (!MultiplayerBridge.IsMultiplayerRunning() || SeededWorldEngine.MpReseedEnabled))
                {
                    return true;
                }

                return InteropBootstrap.Qol.TryReadSeed(out bool seeded, out int s, out _) && seeded && s != 0;
            }
            catch
            {
                return false;
            }
        }

        private static int ComposeSeed(WorldGeneration wg, int depth, int salt)
        {
            int baseSeed = ResolveBaseSeed();
            int traveled = 0;
            try { if (wg != null) traveled = wg.totalTraveled; } catch { }
            return baseSeed + traveled * SeededWorldEngine.LayerSeedStep + depth * 7919 + salt;
        }

        private static int ResolveBaseSeed()
        {
            if (SeededWorldEngine.IsActive && SeededWorldEngine.CurrentSeed != 0
                && (!MultiplayerBridge.IsMultiplayerRunning() || SeededWorldEngine.MpReseedEnabled))
            {
                return SeededWorldEngine.CurrentSeed;
            }

            if (InteropBootstrap.Qol.TryReadSeed(out bool seeded, out int s, out _) && seeded && s != 0)
            {
                return s;
            }

            return SeededWorldEngine.CurrentSeed;
        }

        private static WorldGeneration ReadWg(object instance)
        {
            try
            {
                FieldInfo f = instance.GetType().GetField("wg",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return f != null ? f.GetValue(instance) as WorldGeneration : null;
            }
            catch { return null; }
        }

        private static int ReadDepth(object instance, WorldGeneration wg)
        {
            try
            {
                FieldInfo f = instance.GetType().GetField("depth",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (f != null) return (int)f.GetValue(instance);
            }
            catch { }
            try { if (wg != null) return wg.biomeDepth; } catch { }
            return 0;
        }
    }
}
