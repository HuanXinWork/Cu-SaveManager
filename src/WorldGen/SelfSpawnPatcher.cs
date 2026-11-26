using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>单机 self 引擎下在 FinishWorldGeneration 协程跑完后执行回档位置写回或开局出生防卡。</summary>
    [HarmonyPatch(typeof(WorldGeneration), "FinishWorldGeneration")]
    internal static class SelfSpawnPatcher
    {
        private const float ElderSpawnSafeRadius = 45f;

        private static readonly FieldInfo WorldBlocksField = AccessTools.Field(typeof(WorldGeneration), "worldBlocks");

        private static readonly HashSet<string> TrapEntityIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "antirad", "barbedwirefence", "beartrap", "caveticks", "coil", "drillpod", "grabberplant", "grabbershroom", "gunmine", "jumppad",
            "landmine", "overgrowntick", "radbarrel", "shadecrawler", "sidestabber", "sidestabberflip", "snowstrider", "soundcannon", "spentfuel", "spikestabber",
            "thornbackyoung", "wallbiter"
        };

        // 显式锁定 Priority.Normal (400) 并与 FinishWorldGenerationPendingFlagsCleanupPatch (Priority.Last) 约定内外层嵌套：
        // 本 Postfix 先执行并包在内层，保证 CompletionEnumerator 回调消费 PendingLoadContext.SpawnPosition 时外层 finally 尚未清零。
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Normal)]
        private static void Postfix(ref IEnumerator __result)
        {
            if (__result == null)
            {
                OnFinishWorldGenerationCompleted();
                return;
            }
            __result = new CompletionEnumerator(__result, OnFinishWorldGenerationCompleted);
        }

        private static void OnFinishWorldGenerationCompleted()
        {
            if (MultiplayerBridge.IsMultiplayerRunning() || !SeededWorldEngine.IsActive)
            {
                PendingLoadContext.SpawnPosition = null;
                return;
            }
            if (WorldGeneration.world == null || PlayerCamera.main == null || PlayerCamera.main.body == null)
            {
                PendingLoadContext.SpawnPosition = null;
                return;
            }
            try
            {
                if (PendingLoadContext.SpawnPosition.HasValue)
                {
                    ApplyPendingPosition();
                    return;
                }
                if (!ShouldSkipSpawnSafetyForFreshRunStart())
                {
                    Vector2 spawnPos = PlayerCamera.main.body.transform.position;
                    KeepEldersAwayFromSpawn(spawnPos, ElderSpawnSafeRadius);
                    EnsurePlayerOnSolidGroundLikeVanilla();
                    RemoveNearbyTrapsLikeVanilla();
                }
            }
            catch (Exception ex)
            {
                ModLog.Warning($"SelfSpawnPatcher 异常：{ex.Message}");
            }
        }

        private static bool ShouldSkipSpawnSafetyForFreshRunStart()
        {
            if (SaveSystem.loadedRun) return false;
            if (WorldGeneration.world == null) return false;
            // totalTraveled 单位为累计米数（过首层即 >= 76m），仅第 0 层新开局跳过。
            return WorldGeneration.world.totalTraveled < 50;
        }

        private static void ApplyPendingPosition()
        {
            if (!PendingLoadContext.SpawnPosition.HasValue) return;
            Vector3 pos = PendingLoadContext.SpawnPosition.Value;
            PendingLoadContext.SpawnPosition = null;
            if (LocalPlayerTeleporter.TryMoveTo(pos, forceZeroZ: false))
            {
                Vector3 actual = PlayerCamera.main.body.transform.position;
                ModLog.Info($"回档位置写回：目标({pos.x:0.0},{pos.y:0.0}) 实际({actual.x:0.0},{actual.y:0.0})");
            }
        }

        private static void KeepEldersAwayFromSpawn(Vector2 spawnPos, float minRadius)
        {
            var world = WorldGeneration.world;
            ushort[,] blocks = ReadWorldBlocks(world);
            if (world == null || blocks == null) return;

            int w = (int)world.width;
            int h = (int)world.height;
            float minRadiusSqr = minRadius * minRadius;
            var bodies = UnityEngine.Object.FindObjectsOfType<Body>();
            foreach (var b in bodies)
            {
                if (b == null || b.gameObject == null) continue;
                if (b.gameObject.name.IndexOf("thornbackelder", StringComparison.OrdinalIgnoreCase) < 0) continue;
                Transform t = b.transform;
                Vector2 d = (Vector2)t.position - spawnPos;
                if (d.sqrMagnitude >= minRadiusSqr) continue;
                if (TryFindValidElderPositionAwayFromSpawn(blocks, w, h, spawnPos, minRadius, out var pos))
                {
                    t.position = new Vector3(pos.x, pos.y, t.position.z);
                }
            }
        }

        private static bool TryFindValidElderPositionAwayFromSpawn(ushort[,] blocks, int w, int h, Vector2 spawnPos, float minRadius, out Vector2 pos)
        {
            pos = spawnPos;
            int hw = w / 2;
            int hh = h / 2;
            float maxDist = Mathf.Min(w, h) * 0.45f;
            UnityEngine.Random.State savedRng = UnityEngine.Random.state;
            try
            {
                for (int i = 0; i < 96; i++)
                {
                    float ang = UnityEngine.Random.Range(0f, 360f) * (Mathf.PI / 180f);
                    float dist = UnityEngine.Random.Range(minRadius + 10f, maxDist);
                    int x = Mathf.RoundToInt(spawnPos.x + Mathf.Cos(ang) * dist);
                    int y = Mathf.RoundToInt(spawnPos.y + Mathf.Sin(ang) * dist);
                    if (x >= -hw + 2 && x <= hw - 2 && y >= -hh + 2 && y <= hh - 2
                        && IsAir(blocks, w, h, x, y) && !IsAir(blocks, w, h, x, y - 1))
                    {
                        pos = new Vector2(x, y);
                        return true;
                    }
                }
                return false;
            }
            finally
            {
                UnityEngine.Random.state = savedRng;
            }
        }

        private static void EnsurePlayerOnSolidGroundLikeVanilla()
        {
            var world = WorldGeneration.world;
            if (PlayerCamera.main == null || PlayerCamera.main.body == null || world == null) return;
            ushort[,] blocks = ReadWorldBlocks(world);
            if (blocks == null) return;
            int w = (int)world.width;
            int h = (int)world.height;

            Vector3 p = PlayerCamera.main.body.transform.position;
            int x = Mathf.RoundToInt(p.x);
            int y = Mathf.RoundToInt(p.y);
            if (IsStandLocationValid(blocks, w, h, x, y)) return;

            PlayerCamera.main.body.PlaceBody();
            p = PlayerCamera.main.body.transform.position;
            x = Mathf.RoundToInt(p.x);
            y = Mathf.RoundToInt(p.y);
            if (!IsStandLocationValid(blocks, w, h, x, y))
            {
                MovePlayerToNearestOpenLandIfEmbedded(blocks, w, h);
            }
        }

        internal static void RemoveNearbyTrapsLikeVanilla()
        {
            if (PlayerCamera.main == null || PlayerCamera.main.body == null) return;
            Vector2 origin = PlayerCamera.main.body.transform.position;
            bool inTopSpawnZone = WorldGeneration.world != null && origin.y >= WorldGeneration.world.height * 0.25f;

            var buildings = UnityEngine.Object.FindObjectsOfType<BuildingEntity>();
            foreach (var be in buildings)
            {
                if (be == null || be.gameObject == null || !IsTrapBuilding(be)) continue;
                var attachedItem = be.GetComponent<Item>();
                if (attachedItem != null && ShouldPreserveHeldOrContainerItem(attachedItem)) continue;
                if (ShouldDestroyTrapAt(be.transform.position, origin, inTopSpawnZone))
                {
                    UnityEngine.Object.Destroy(be.gameObject);
                }
            }

            var items = UnityEngine.Object.FindObjectsOfType<Item>();
            foreach (var item in items)
            {
                if (item == null || item.gameObject == null || !IsTrapItem(item)) continue;
                // TrapEntityIds 含抗辐射药（antirad），须跳过玩家手持、穿戴及任意层级容器内的随身物品。
                if (ShouldPreserveHeldOrContainerItem(item)) continue;
                if (ShouldDestroyTrapAt(item.transform.position, origin, inTopSpawnZone))
                {
                    UnityEngine.Object.Destroy(item.gameObject);
                }
            }
        }

        private static bool ShouldDestroyTrapAt(Vector3 pos, Vector2 origin, bool inTopSpawnZone)
        {
            const float sqrRadius = 144f;
            Vector2 d = (Vector2)pos - origin;
            bool near = d.sqrMagnitude <= sqrRadius;
            if (inTopSpawnZone)
            {
                bool aboveFloor = pos.y > origin.y - 5f;
                return near || aboveFloor;
            }
            // 位于地图中下部落地时仅清理近身与头顶局部安全柱，防止落石砸脸且避免误删上方整层陷阱。
            return near || (Mathf.Abs(d.x) <= 12f && pos.y > origin.y - 5f && pos.y <= origin.y + 24f);
        }

        private static bool ShouldPreserveHeldOrContainerItem(Item item)
        {
            if (item == null) return false;
            Transform t = item.transform;
            return t.GetComponentInParent<Body>() != null || t.GetComponentInParent<Container>() != null;
        }

        private static bool IsTrapBuilding(BuildingEntity be)
        {
            if (be == null || be.gameObject == null) return false;
            if (!string.IsNullOrEmpty(be.id) && TrapEntityIds.Contains(NormalizeEntityId(be.id))) return true;
            return TrapEntityIds.Contains(NormalizeEntityId(be.gameObject.name));
        }

        private static bool IsTrapItem(Item item)
        {
            if (item == null || item.gameObject == null) return false;
            if (!string.IsNullOrEmpty(item.id) && TrapEntityIds.Contains(NormalizeEntityId(item.id))) return true;
            return TrapEntityIds.Contains(NormalizeEntityId(item.gameObject.name));
        }

        private static string NormalizeEntityId(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            string s = value.Replace("(Clone)", "").Trim();
            int slash = s.LastIndexOf('/');
            if (slash >= 0 && slash < s.Length - 1) s = s.Substring(slash + 1);
            return s;
        }

        private static void MovePlayerToNearestOpenLandIfEmbedded(ushort[,] blocks, int w, int h)
        {
            if (PlayerCamera.main == null || PlayerCamera.main.body == null) return;
            Vector3 p = PlayerCamera.main.body.transform.position;
            var start = new Vector2Int(Mathf.RoundToInt(p.x), Mathf.RoundToInt(p.y));
            if (IsStandLocationValid(blocks, w, h, start.x, start.y)) return;
            if (!TryFindNearestOpenLand(blocks, w, h, start, 80, out var found)) return;
            var dst = new Vector3(found.x, found.y, p.z);
            LocalPlayerTeleporter.TryMoveTo(dst, forceZeroZ: false);
        }

        private static bool TryFindNearestOpenLand(ushort[,] blocks, int w, int h, Vector2Int start, int maxRadius, out Vector2Int found)
        {
            found = start;
            var visited = new HashSet<Vector2Int> { start };
            var queue = new Queue<Vector2Int>();
            queue.Enqueue(start);
            var dirs = new[] { new Vector2Int(-1, 0), new Vector2Int(1, 0), new Vector2Int(0, 1), new Vector2Int(0, -1) };
            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                if (Mathf.Abs(cur.x - start.x) + Mathf.Abs(cur.y - start.y) > maxRadius) continue;
                if (IsStandLocationValid(blocks, w, h, cur.x, cur.y)) { found = cur; return true; }
                foreach (var dir in dirs)
                {
                    var next = cur + dir;
                    if (!visited.Contains(next) && IsInside(w, h, next.x, next.y))
                    {
                        visited.Add(next);
                        queue.Enqueue(next);
                    }
                }
            }
            return false;
        }

        private static ushort[,] ReadWorldBlocks(WorldGeneration world)
        {
            if (world == null || WorldBlocksField == null) return null;
            try { return WorldBlocksField.GetValue(world) as ushort[,]; }
            catch { return null; }
        }

        private static bool IsStandLocationValid(ushort[,] blocks, int w, int h, int x, int y)
        {
            if (!IsInside(w, h, x, y) || !IsInside(w, h, x, y + 1) || !IsInside(w, h, x, y - 1)) return false;
            return IsAir(blocks, w, h, x, y) && IsAir(blocks, w, h, x, y + 1) && !IsAir(blocks, w, h, x, y - 1);
        }

        private static bool IsAir(ushort[,] blocks, int w, int h, int x, int y)
        {
            int bx = x + w / 2;
            int by = y + h / 2;
            if (bx < 0 || by < 0 || bx >= w || by >= h) return true;
            return blocks[bx, by] == 0;
        }

        private static bool IsInside(int w, int h, int x, int y)
        {
            int hw = w / 2;
            int hh = h / 2;
            return x >= -hw && x < hw && y >= -hh && y < hh;
        }
    }
}
