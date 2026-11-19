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

        /// <summary>读档或回档前注入的玩家目标世界坐标，协程收尾消费后清空。</summary>
        internal static Vector3? PendingPosition;

        private sealed class CompletionEnumerator : IEnumerator, IDisposable
        {
            private readonly IEnumerator _inner;
            private readonly Action _onCompleted;
            private bool _completed;

            internal CompletionEnumerator(IEnumerator inner, Action onCompleted)
            {
                _inner = inner;
                _onCompleted = onCompleted;
            }

            public object Current => _inner.Current;

            public bool MoveNext()
            {
                bool hasNext = _inner.MoveNext();
                if (!hasNext && !_completed)
                {
                    _completed = true;
                    _onCompleted?.Invoke();
                }
                return hasNext;
            }

            public void Reset() => _inner.Reset();
            public void Dispose() => (_inner as IDisposable)?.Dispose();
        }

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
            if (MultiplayerBridge.IsMultiplayerRunning()) return;
            if (!SeededWorldEngine.IsActive) return;
            if (WorldGeneration.world == null || PlayerCamera.main == null || PlayerCamera.main.body == null) return;
            try
            {
                if (PendingPosition.HasValue)
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
            return WorldGeneration.world.totalTraveled < 50;
        }

        private static void ApplyPendingPosition()
        {
            if (!PendingPosition.HasValue) return;
            Vector3 pos = PendingPosition.Value;
            if (PlayerCamera.main != null && PlayerCamera.main.body != null)
            {
                PlayerCamera.main.body.transform.position = pos;
                PlayerCamera.main.transform.position = new Vector3(pos.x, pos.y, -10f);
                if (PlayerCamera.main.body.rb != null) PlayerCamera.main.body.rb.velocity = Vector2.zero;
                ModLog.Info($"回档位置写回：目标({pos.x:0.0},{pos.y:0.0}) 实际({PlayerCamera.main.body.transform.position.x:0.0},{PlayerCamera.main.body.transform.position.y:0.0})");
            }
            PendingPosition = null;
        }

        private static void KeepEldersAwayFromSpawn(Vector2 spawnPos, float minRadius)
        {
            var world = WorldGeneration.world;
            ushort[,] blocks = ReadWorldBlocks(world);
            if (world == null || blocks == null) return;

            int w = (int)world.width;
            int h = (int)world.height;
            var all = UnityEngine.Object.FindObjectsOfType<Transform>();
            foreach (var t in all)
            {
                if (t == null || t.gameObject == null) continue;
                if (!t.gameObject.name.Contains("thornbackelder")) continue;
                Vector2 d = (Vector2)t.position - spawnPos;
                if (d.sqrMagnitude >= minRadius * minRadius) continue;
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
            const float sqrRadius = 144f;
            var all = UnityEngine.Object.FindObjectsOfType<Transform>();
            foreach (var t in all)
            {
                if (t == null || t.gameObject == null || !IsTrapObject(t.gameObject)) continue;
                // TrapEntityIds 含抗辐射药（antirad），须跳过玩家手持、穿戴及任意层级容器内的随身物品。
                if (ShouldPreserveHeldOrContainerItem(t)) continue;
                Vector2 d = (Vector2)t.position - origin;
                bool near = d.sqrMagnitude <= sqrRadius;
                bool aboveFloor = t.position.y > origin.y - 5f;
                if (near || aboveFloor) UnityEngine.Object.Destroy(t.gameObject);
            }
        }

        private static bool ShouldPreserveHeldOrContainerItem(Transform t)
        {
            if (t.GetComponent<Item>() == null) return false;
            return t.GetComponentInParent<Body>() != null || t.GetComponentInParent<Container>() != null;
        }

        private static bool IsTrapObject(GameObject obj)
        {
            if (obj == null) return false;
            if (TrapEntityIds.Contains(NormalizeEntityId(obj.name))) return true;
            var be = obj.GetComponent<BuildingEntity>();
            if (be == null || string.IsNullOrEmpty(be.id)) return false;
            return TrapEntityIds.Contains(NormalizeEntityId(be.id));
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
            PlayerCamera.main.body.transform.position = dst;
            PlayerCamera.main.transform.position = new Vector3(dst.x, dst.y, -10f);
            if (PlayerCamera.main.body.rb != null) PlayerCamera.main.body.rb.velocity = Vector2.zero;
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
