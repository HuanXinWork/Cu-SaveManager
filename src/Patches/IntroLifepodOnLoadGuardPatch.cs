using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>读档/回档第 0 层时仅在 <c>WorldPlacePlayer</c> 作用域内跳过重复生成的入场 Lifepod，不影响野外散布救生舱。</summary>
    [HarmonyPatch]
    internal static class IntroLifepodOnLoadGuardPatch
    {
        internal static bool Enabled = true;
        internal static bool InWorldPlacePlayer;

        private static MethodBase TargetMethod()
            => AccessTools.Method(typeof(WorldGeneration), "GenerateEntityAtPos");

        private static bool Prepare() => TargetMethod() != null;

        private static bool Prefix(GameObject basObj)
        {
            if (!InWorldPlacePlayer) return true;
            if (!Enabled || !SaveSystem.loadedRun || !PendingLoadContext.SkipIntroLifepod) return true;
            if (basObj == null || basObj.name != "Lifepod") return true;
            var world = WorldGeneration.world;
            if (world == null || world.totalTraveled > 0) return true;
            return false;
        }
    }

    /// <summary>读档/回档第 0 层时拦截 <c>WorldPlacePlayer</c> 内生成的节日礼物、装饰树、糖果与南瓜灯，防止反复读档刷出多份。</summary>
    [HarmonyPatch]
    internal static class SeasonalItemsOnLoadGuardPatch
    {
        // 对应 WorldGeneration.WorldPlacePlayer (2515-2525) Holidays 与 Halloween 分支的全部精确 prefab 名。
        private static readonly HashSet<string> SeasonalPrefabIds = new HashSet<string>(StringComparer.Ordinal)
        {
            "present",
            "Special/holidaytree",
            "candybar",
            "hardcandy",
            "Special/jackolantern",
        };

        private static MethodBase TargetMethod()
            => AccessTools.Method(typeof(Utils), nameof(Utils.Create), new[] { typeof(string), typeof(Vector2), typeof(float) });

        private static bool Prepare() => TargetMethod() != null;

        private static bool Prefix(string id, ref GameObject __result)
        {
            if (!IntroLifepodOnLoadGuardPatch.InWorldPlacePlayer) return true;
            if (!IntroLifepodOnLoadGuardPatch.Enabled || !SaveSystem.loadedRun || !PendingLoadContext.SkipIntroLifepod) return true;
            if (string.IsNullOrEmpty(id) || !SeasonalPrefabIds.Contains(id)) return true;
            var world = WorldGeneration.world;
            if (world == null || world.totalTraveled > 0) return true;
            __result = null;
            return false;
        }
    }

    /// <summary>包装 <c>WorldGeneration.WorldPlacePlayer</c> 协程步进作用域，供入场救生舱与节日生成物拦截补丁精准识别调用来源。</summary>
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
            try
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
            finally
            {
                // 延至整个 WorldPlacePlayer 协程跑完再复位，确保 Lifepod 之后的同帧节日物品创建仍处于跳过窗口内。
                PendingLoadContext.SkipIntroLifepod = false;
            }
        }
    }

    /// <summary>在世界生成收尾与新开局时兜底复位未消费的读档一次性标志，防止高楼层或中止读档残留污染后续局。</summary>
    [HarmonyPatch(typeof(WorldGeneration), "FinishWorldGeneration")]
    internal static class FinishWorldGenerationPendingFlagsCleanupPatch
    {
        // 必须标在方法而非类上：0Harmony 2.9.0 的 HarmonyMethodExtensions.Merge 在方法未标优先级时
        // （detail.priority == -1）会直接跳过赋值，导致类级 [HarmonyPriority] 静默退化为 Normal (400)，
        // 进而在元数据顺序靠前时先于 SelfSpawnPatcher 包装并在内层 finally 中提前清零 SpawnPosition。
        // 独占 Priority.Last (0) 确保本包装处于 SelfSpawnPatcher (Normal) 与 MpPositionFinishPatch (VeryLow) 最外层。
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(ref IEnumerator __result)
        {
            if (__result == null)
            {
                PendingLoadContext.ClearAll();
                return;
            }
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
                PendingLoadContext.ClearAll();
            }
        }
    }

    /// <summary>新开局入口兜底清理读档遗留的一次性救生舱、词条与坐标还原标志。</summary>
    [HarmonyPatch(typeof(PreRunScript), "StartRun")]
    internal static class StartRunPendingFlagsCleanupPatch
    {
        // 必须标在方法而非类上以规避 0Harmony 2.9.0 Merge 丢弃类级优先级，确保先于其他 StartRun 前置补丁清空残留读档标志。
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static void Prefix()
        {
            PendingLoadContext.ClearAll();
        }
    }
}
