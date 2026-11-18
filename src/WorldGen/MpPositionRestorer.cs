using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>多人回档玩家坐标恢复：在 FinishWorldGeneration 协程跑完后按 persistentId 跟踪并持续还原主客端位置。</summary>
    internal static class MpPositionRestorer
    {
        // 给 ModHooks.SpawnPositionOverride 预留短暂窗口，超时仍未全员恢复则无缝回退至本地写回 + Server_TeleportCharacter 兜底。
        private const float HookFallbackDelaySeconds = 1.5f;

        private static bool _activeSession;
        private static int _sessionId;
        private static Dictionary<string, Vector2> _pending;
        private static HashSet<string> _restored;

        internal static bool ActiveSession => _activeSession;

        /// <summary>挂载多人晚出生抑制或绑定 Together.ModHooks 精确坐标钩子。</summary>
        internal static void TryPatchHarmony()
        {
            TogetherModHooksBridge.TryBindAll();
            MpLateSpawnSuppressor.TryPatch();
        }

        internal static void CancelSession()
        {
            _sessionId++;
            _activeSession = false;
            _pending = null;
            _restored = null;
            TogetherModHooksBridge.SetRestoreSavedPositions(false);
        }

        internal static Vector2? TryResolveHookPosition(string persistentId)
        {
            if (!_activeSession || _pending == null || string.IsNullOrEmpty(persistentId))
                return null;
            if (!_pending.TryGetValue(persistentId, out Vector2 pos))
                return null;
            _restored?.Add(persistentId);
            ModLog.Info($"多人回档 ModHooks.SpawnPositionOverride：{persistentId} → ({pos.x:0.0},{pos.y:0.0})");
            return pos;
        }

        internal static void PrepareForRollback(SlotSidecar sidecar)
        {
            TryPatchHarmony();

            if (IsCrossLayerBoundarySave())
            {
                CancelSession();
                ModLog.Info("多人回档：mp_rules LEVEL=false（跨层进层槽），跳过上一层出口 PLRPOS 坐标还原");
                return;
            }

            var positions = ReadPlrPosFromMpRules();
            if (positions.Count == 0)
            {
                ModLog.Warning("多人回档：mp_rules 无 PLRPOS，仅尝试 sidecar 主机坐标");
            }

            if (sidecar != null && !MultiplayerBridge.IsDedicatedServer())
            {
                var hostPos = ResolveHostPosition(sidecar);
                if (hostPos.HasValue)
                {
                    string hostId = MultiplayerBridge.GetLocalPersistentId();
                    if (string.IsNullOrEmpty(hostId))
                    {
                        ModLog.Warning("多人回档：本地主机 persistentId 为空，跳过 sidecar 主机坐标覆盖");
                    }
                    else
                    {
                        positions[hostId] = hostPos.Value;
                        ModLog.Info($"多人回档：sidecar 覆盖主机 {hostId} ({hostPos.Value.x:0.0},{hostPos.Value.y:0.0})");
                    }
                }
            }
            else if (MultiplayerBridge.IsDedicatedServer())
            {
                ModLog.Info("多人回档：专用服务端跳过 sidecar 主机坐标");
            }

            if (positions.Count == 0)
            {
                CancelSession();
                ModLog.Warning("多人回档：无可恢复的玩家坐标");
                return;
            }

            _sessionId++;
            _pending = positions;
            _restored = new HashSet<string>(StringComparer.Ordinal);
            _activeSession = true;
            TogetherModHooksBridge.SetRestoreSavedPositions(true);
            ModLog.Info($"多人回档：已缓存 {positions.Count} 名玩家位置");
            foreach (var kv in positions)
            {
                ModLog.Info($"  PLRPOS {kv.Key} = ({kv.Value.x:0.0},{kv.Value.y:0.0})");
            }
        }

        private static bool IsCrossLayerBoundarySave()
        {
            try
            {
                if (!File.Exists(MpSaveLocator.MpRulesPath)) return false;
                string json = File.ReadAllText(MpSaveLocator.MpRulesPath, System.Text.Encoding.UTF8);
                return MpRulesJson.TryReadBool(json, MpRulesJson.LevelField, out bool midLevel) && !midLevel;
            }
            catch
            {
                return false;
            }
        }

        internal static void ApplyAfterFinishWorldGen()
        {
            if (!_activeSession || _pending == null || _pending.Count == 0) return;

            int capturedSession = _sessionId;
            if (TogetherModHooksBridge.HasSpawnPositionHook)
            {
                var hookRunner = MpRollbackRunner.Instance;
                if (hookRunner == null)
                {
                    EndSession(capturedSession);
                    return;
                }

                float fallbackAt = -1f;
                bool fallbackActivated = false;
                hookRunner.RunPositionRestoreWindow(
                    () =>
                    {
                        if (_sessionId != capturedSession || !_activeSession || _pending == null)
                            return true;
                        if (AreAllConnectedPendingRestored())
                            return true;

                        if (fallbackAt < 0f)
                        {
                            fallbackAt = Time.unscaledTime + HookFallbackDelaySeconds;
                            return false;
                        }
                        if (Time.unscaledTime < fallbackAt)
                            return false;

                        // 超过宽限期仍有在线玩家未通过 SpawnPositionOverride 落位（如断线重连或极端时序），回退启用主动坐标写回安全网。
                        if (!fallbackActivated)
                        {
                            fallbackActivated = true;
                            ModLog.Info($"多人回档：ModHooks 出生点覆写等待 {HookFallbackDelaySeconds:0.0}s 后仍有未恢复玩家（{_restored?.Count ?? 0}/{_pending.Count}），启用兜底坐标写回");
                            ApplyLocalHostPositionIfPending();
                        }

                        return TryRestoreRemaining();
                    },
                    () => EndSession(capturedSession),
                    15f,
                    0.25f);
                return;
            }

            ApplyLocalHostPosition(_pending);

            var runner = MpRollbackRunner.Instance;
            if (runner != null)
            {
                runner.RunPositionRestoreWindow(TryRestoreRemaining, () => EndSession(capturedSession), 15f, 0.25f);
            }
            else
            {
                TryRestoreRemaining();
                EndSession(capturedSession);
            }
        }

        // 按当前在线会话玩家核对恢复进度，忽略 PLRPOS 中未上线的历史离线玩家与专用服务端本地伪玩家。
        private static bool AreAllConnectedPendingRestored()
        {
            if (_pending == null || _restored == null || _restored.Count >= _pending.Count || !MultiplayerBridge.IsMultiplayerRunning())
                return true;

            var sessionPlayers = MultiplayerBridge.GetSessionPlayerIdentities();
            if (sessionPlayers == null || sessionPlayers.Count == 0)
                return false;

            bool isDed = MultiplayerBridge.IsDedicatedServer();
            int onlinePendingCount = 0;
            foreach (var p in sessionPlayers)
            {
                if (isDed && p.IsLocal) continue;
                if (string.IsNullOrEmpty(p.PersistentId) || !_pending.ContainsKey(p.PersistentId)) continue;
                onlinePendingCount++;
                if (!_restored.Contains(p.PersistentId))
                    return false;
            }

            return onlinePendingCount > 0;
        }

        private static void ApplyLocalHostPositionIfPending()
        {
            if (_pending == null) return;
            string hostId = MultiplayerBridge.GetLocalPersistentId();
            if (string.IsNullOrEmpty(hostId)) return;
            if (_restored != null && _restored.Contains(hostId)) return;
            if (ApplyLocalHostPosition(_pending))
            {
                _restored?.Add(hostId);
            }
        }

        private static void EndSession(int capturedSession)
        {
            if (_sessionId != capturedSession || !_activeSession) return;

            if (_pending != null && _restored != null && !AreAllConnectedPendingRestored() && MultiplayerBridge.IsServer())
            {
                ModLog.Warning($"多人回档位置写回窗口结束：已恢复 {_restored.Count}/{_pending.Count} 名玩家");
            }

            _activeSession = false;
            _pending = null;
            _restored = null;
            TogetherModHooksBridge.SetRestoreSavedPositions(false);
        }

        private static Vector2? ResolveHostPosition(SlotSidecar sidecar)
        {
            if (sidecar == null || !sidecar.HasPlayerPos) return null;
            if (string.Equals(sidecar.PosMode, "fixedPos", StringComparison.OrdinalIgnoreCase))
                return new Vector2(sidecar.FixedX, sidecar.FixedY);
            return new Vector2(sidecar.PlayerX, sidecar.PlayerY);
        }

        private static bool TryRestoreRemaining()
        {
            if (!_activeSession || _pending == null || _pending.Count == 0) return true;
            if (!MultiplayerBridge.IsMultiplayerRunning()) return true;

            if (!MultiplayerBridge.IsServer())
            {
                ApplyLocalHostPosition(_pending);
                return true;
            }

            string localId = MultiplayerBridge.GetLocalPersistentId();
            var probe = new Dictionary<string, Vector2>(1);
            int newlyRestored = 0;

            foreach (var kv in _pending)
            {
                if (_restored != null && _restored.Contains(kv.Key)) continue;

                probe.Clear();
                probe[kv.Key] = kv.Value;

                bool localApplied = false;
                if (!string.IsNullOrEmpty(localId) && kv.Key == localId)
                {
                    localApplied = ApplyLocalHostPosition(probe);
                }

                bool netApplied = MultiplayerBridge.RestorePlayerPositions(probe) > 0;
                // 本地房主写回或网络传送任一成功即记入已恢复，避免 RestorePlayerPositions 返回 0 时每 0.25s 反复拉回房主。
                if (localApplied || netApplied)
                {
                    _restored?.Add(kv.Key);
                    newlyRestored++;
                }
            }

            if (newlyRestored > 0)
            {
                ModLog.Info($"多人回档位置写回（Server_TeleportCharacter）：本次恢复 {newlyRestored} 人（累计 {_restored?.Count ?? 0}/{_pending.Count}）");
            }

            return AreAllConnectedPendingRestored();
        }

        private static bool ApplyLocalHostPosition(Dictionary<string, Vector2> positions)
        {
            if (positions == null) return false;
            try
            {
                string hostId = MultiplayerBridge.GetLocalPersistentId();
                if (string.IsNullOrEmpty(hostId) || !positions.TryGetValue(hostId, out Vector2 pos)) return false;
                var cam = PlayerCamera.main;
                if (cam == null || cam.body == null) return false;
                cam.body.transform.position = new Vector3(pos.x, pos.y, 0f);
                cam.transform.position = new Vector3(pos.x, pos.y, -10f);
                if (cam.body.rb != null) cam.body.rb.velocity = Vector2.zero;
                ModLog.Info($"多人回档本地写回：({pos.x:0.0},{pos.y:0.0})");
                return true;
            }
            catch (Exception ex)
            {
                ModLog.Warning($"ApplyLocalHostPosition 失败：{ex.Message}");
                return false;
            }
        }

        internal static Dictionary<string, Vector2> ReadPlrPosFromMpRules(string rulesPath = null)
        {
            rulesPath = rulesPath ?? MpSaveLocator.MpRulesPath;
            try
            {
                if (!File.Exists(rulesPath)) return new Dictionary<string, Vector2>(StringComparer.Ordinal);
                return MpRulesJson.ReadPlrPos(File.ReadAllText(rulesPath, System.Text.Encoding.UTF8));
            }
            catch (Exception ex)
            {
                ModLog.Warning($"ReadPlrPosFromMpRules 失败：{ex.Message}");
                return new Dictionary<string, Vector2>(StringComparer.Ordinal);
            }
        }
    }

    [HarmonyPatch(typeof(WorldGeneration), "FinishWorldGeneration")]
    [HarmonyPriority(Priority.Last)]
    internal static class MpPositionFinishPatch
    {
        private sealed class CompletionEnumerator : IEnumerator, IDisposable
        {
            private readonly IEnumerator _inner;
            private bool _completed;

            internal CompletionEnumerator(IEnumerator inner)
            {
                _inner = inner;
            }

            public object Current => _inner.Current;

            public bool MoveNext()
            {
                bool hasNext = _inner.MoveNext();
                if (!hasNext && !_completed)
                {
                    _completed = true;
                    InvokeAfterFinish();
                }
                return hasNext;
            }

            public void Reset() => _inner.Reset();
            public void Dispose() => (_inner as IDisposable)?.Dispose();
        }

        internal static void Postfix(ref IEnumerator __result)
        {
            if (__result == null)
            {
                InvokeAfterFinish();
                return;
            }
            __result = new CompletionEnumerator(__result);
        }

        private static void InvokeAfterFinish()
        {
            try { MpPositionRestorer.ApplyAfterFinishWorldGen(); }
            catch (Exception ex) { ModLog.Warning($"MpPositionFinishPatch 异常：{ex.Message}"); }
        }
    }
}
