using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>读档/回档第 1 层时仅在 <c>WorldPlacePlayer</c> 作用域内跳过重复生成的入场 Lifepod，不影响野外散布救生舱。</summary>
    [HarmonyPatch]
    internal static class IntroLifepodOnLoadGuardPatch
    {
        internal static bool Enabled = true;
        internal static bool PendingSkipIntroLifepod;
        internal static bool InWorldPlacePlayer;

        private static MethodBase TargetMethod()
            => AccessTools.Method(typeof(WorldGeneration), "GenerateEntityAtPos");

        private static bool Prepare() => TargetMethod() != null;

        private static bool Prefix(GameObject basObj)
        {
            if (!InWorldPlacePlayer) return true;
            if (!Enabled || !SaveSystem.loadedRun || !PendingSkipIntroLifepod) return true;
            if (basObj == null || basObj.name != "Lifepod") return true;
            var world = WorldGeneration.world;
            if (world == null || world.totalTraveled > 0) return true;
            PendingSkipIntroLifepod = false;
            return false;
        }
    }

    /// <summary>包装 <c>WorldGeneration.WorldPlacePlayer</c> 协程步进作用域，供入场救生舱拦截补丁精准识别调用来源。</summary>
    [HarmonyPatch(typeof(WorldGeneration), "WorldPlacePlayer")]
    internal static class WorldPlacePlayerLifepodScopePatch
    {
        private static void Postfix(ref IEnumerator __result)
        {
            if (__result != null)
                __result = Wrap(__result);
        }

        private static IEnumerator Wrap(IEnumerator inner)
        {
            using (inner as IDisposable)
            {
                while (true)
                {
                    bool moved;
                    IntroLifepodOnLoadGuardPatch.InWorldPlacePlayer = true;
                    try { moved = inner.MoveNext(); }
                    finally { IntroLifepodOnLoadGuardPatch.InWorldPlacePlayer = false; }
                    if (!moved) yield break;
                    yield return inner.Current;
                }
            }
        }
    }

    /// <summary>在世界生成收尾与新开局时兜底复位未消费的读档一次性标志，防止高楼层或中止读档残留污染后续局。</summary>
    [HarmonyPatch(typeof(WorldGeneration), "FinishWorldGeneration")]
    internal static class FinishWorldGenerationPendingFlagsCleanupPatch
    {
        private static void Postfix(ref IEnumerator __result)
        {
            if (__result != null)
                __result = Wrap(__result);
        }

        private static IEnumerator Wrap(IEnumerator inner)
        {
            try
            {
                using (inner as IDisposable)
                {
                    while (inner.MoveNext())
                        yield return inner.Current;
                }
            }
            finally
            {
                IntroLifepodOnLoadGuardPatch.PendingSkipIntroLifepod = false;
                ApplyLayerModifiersRestorePatch.PendingIndex = -2;
            }
        }
    }

    /// <summary>新开局入口兜底清理读档遗留的一次性救生舱与词条还原标志。</summary>
    [HarmonyPatch(typeof(PreRunScript), "StartRun")]
    [HarmonyPriority(Priority.First)]
    internal static class StartRunPendingFlagsCleanupPatch
    {
        private static void Prefix()
        {
            IntroLifepodOnLoadGuardPatch.PendingSkipIntroLifepod = false;
            ApplyLayerModifiersRestorePatch.PendingIndex = -2;
        }
    }
}
