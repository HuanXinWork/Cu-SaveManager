using System;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CasualtiesUnknown.SaveManager
{
    internal enum RollbackState
    {
        Idle,
        Counting,
        Executing,
    }

    /// <summary>回档状态机：单机按 LocalDeathLatched、多人服务端按死亡人数阈值触发倒计时，仅死亡触发在恢复存活时自动取消。</summary>
    internal sealed class RollbackController
    {
        private readonly HotkeyConfig _cfg;
        private readonly SaveStore _store;

        private RollbackState _state = RollbackState.Idle;
        private float _remaining;
        private SlotInfo _target;
        private string _lastError;
        private int _lastBroadcastSecond = -1;
        private float _nextMpDeathCheckAt;
        private bool _mpDyingCached;
        private bool _deathHandledThisLife;
        private bool _triggeredByDeath;

        internal RollbackController(HotkeyConfig cfg, SaveStore store)
        {
            _cfg = cfg;
            _store = store;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        internal void Dispose()
        {
            try { SceneManager.sceneLoaded -= OnSceneLoaded; } catch { }
            IsActiveGlobal = false;
            IsDeathSuspected = false;
            PlayerCameraDeathDetector.Reset();
        }

        private void OnSceneLoaded(Scene s, LoadSceneMode m)
        {
            _deathHandledThisLife = false;
            _triggeredByDeath = false;
            IsDeathSuspected = false;
            _mpDyingCached = false;
            _nextMpDeathCheckAt = 0f;
            PlayerCameraDeathDetector.Reset();
            if (_state == RollbackState.Executing && !MpRollbackController.IsPendingReload)
            {
                SetState(RollbackState.Idle);
                _target = null;
                _remaining = 0f;
                _lastBroadcastSecond = -1;
            }
        }

        internal RollbackState State => _state;
        internal float Remaining => _remaining;
        internal SlotInfo Target => _target;
        internal string LastError => _lastError;

        /// <summary>倒计时或正在执行回档时为 true，供 Harmony 补丁拦截原生流程。</summary>
        internal static bool IsActiveGlobal { get; private set; }

        /// <summary>满足死亡自动回档条件的首帧置 true，供 GameAutoRespawnGuard 提前拦截原生重生或退主菜单。</summary>
        internal static bool IsDeathSuspected { get; private set; }

        private void SetState(RollbackState s)
        {
            _state = s;
            IsActiveGlobal = s == RollbackState.Counting || s == RollbackState.Executing;
        }

        /// <summary>每帧更新死亡检测、存活恢复复位与倒计时推进。</summary>
        internal void Tick()
        {
            try
            {
                bool dying = IsDying();
                if (_deathHandledThisLife && !dying)
                {
                    _deathHandledThisLife = false;
                }

                IsDeathSuspected = _cfg.AutoRollbackOnDeath.Value
                    && _state == RollbackState.Idle
                    && !_deathHandledThisLife
                    && dying;

                if (_state == RollbackState.Counting) TickCountdown(dying);
                else if (_state == RollbackState.Idle) CheckDeathTrigger(dying);
            }
            catch (Exception ex)
            {
                ModLog.Warning($"RollbackController.Tick 异常：{ex.Message}");
            }
        }

        private bool IsDying()
        {
            try
            {
                if (MultiplayerBridge.IsMultiplayerRunning())
                {
                    if (!MultiplayerBridge.IsServer() || MultiplayerBridge.IsGamemodeActive()) return false;
                    float now = Time.unscaledTime;
                    if (now < _nextMpDeathCheckAt) return _mpDyingCached;
                    _nextMpDeathCheckAt = now + 0.5f;
                    int dead = MultiplayerBridge.DeadPlayerCount();
                    int threshold = _cfg.MultiplayerDeathThreshold.Value;
                    if (threshold <= 0)
                    {
                        int living = MultiplayerBridge.LivingPlayerCount();
                        _mpDyingCached = dead > 0 && living == 0;
                    }
                    else
                    {
                        _mpDyingCached = dead >= threshold;
                    }
                    return _mpDyingCached;
                }
                return PlayerCameraDeathDetector.LocalDeathLatched;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>手动请求回档到指定槽位；前置校验联机主机权限与单人/多人模式匹配。</summary>
        internal void RequestRollback(SlotInfo slot)
        {
            StartRollback(slot, triggeredByDeath: false);
        }

        private bool StartRollback(SlotInfo slot, bool triggeredByDeath)
        {
            if (_state != RollbackState.Idle) return false;
            if (slot == null || !File.Exists(slot.FullSlotPath))
            {
                _lastError = I18n.T("msg.rollback_target_missing");
                return false;
            }

            bool mpRunning = MultiplayerBridge.IsMultiplayerRunning();
            if (mpRunning && !MultiplayerBridge.IsServer())
            {
                _lastError = I18n.T("mp.only_host_can_rollback");
                return false;
            }
            if (slot.Sidecar.IsMultiplayer && !mpRunning)
            {
                _lastError = I18n.T("mp.slot_needs_mp_session");
                return false;
            }
            if (!slot.Sidecar.IsMultiplayer && mpRunning)
            {
                _lastError = I18n.T("mp.slot_is_singleplayer");
                return false;
            }

            _target = slot;
            _triggeredByDeath = triggeredByDeath;
            _remaining = Mathf.Max(1f, _cfg.RollbackDelaySeconds.Value);
            SetState(RollbackState.Counting);
            _lastError = null;
            _lastBroadcastSecond = -1;
            BroadcastCountdown(forceFirst: true);
            return true;
        }

        internal void Cancel(string reason)
        {
            if (_state != RollbackState.Counting) return;
            SetState(RollbackState.Idle);
            _target = null;
            _triggeredByDeath = false;
            _remaining = 0f;
            _lastError = string.IsNullOrEmpty(reason) ? null : reason;
            _lastBroadcastSecond = -1;
            BroadcastLocal(I18n.T("msg.rollback_canceled"));
            TryBroadcastMP(I18n.T("msg.rollback_canceled"));
        }

        private void CheckDeathTrigger(bool dying)
        {
            if (!_cfg.AutoRollbackOnDeath.Value || _deathHandledThisLife || !dying) return;

            var slot = _store.FindLatestRollbackTarget();
            if (slot == null)
            {
                _deathHandledThisLife = true;
                _lastError = I18n.T("msg.rollback_no_target");
                BroadcastLocal(_lastError);
                ModLog.Info("检测到死亡但 FindLatestRollbackTarget 返回 null");
                return;
            }
            _deathHandledThisLife = true;
            StartRollback(slot, triggeredByDeath: true);
        }

        private void TickCountdown(bool dying)
        {
            if (_triggeredByDeath && !dying)
            {
                _deathHandledThisLife = false;
                Cancel(null);
                return;
            }

            _remaining -= Time.unscaledDeltaTime;
            int currentSec = Mathf.CeilToInt(_remaining);
            if (currentSec != _lastBroadcastSecond)
            {
                _lastBroadcastSecond = currentSec;
                BroadcastCountdown(forceFirst: false);
            }
            if (_remaining <= 0f) ExecuteNow();
        }

        private void BroadcastCountdown(bool forceFirst)
        {
            int sec = forceFirst ? Mathf.CeilToInt(_remaining) : _lastBroadcastSecond;
            if (sec < 0) sec = 0;
            string nick = _target == null ? "" : (string.IsNullOrEmpty(_target.Sidecar.Nickname)
                ? _target.File.Name : _target.Sidecar.Nickname);
            string msg = I18n.F("fmt.rollback_in_seconds", sec, nick);
            BroadcastLocal(msg);
            TryBroadcastMP(msg);
        }

        private static void BroadcastLocal(string msg)
        {
            try
            {
                PlayerCamera.main?.DoAlert(msg, important: true);
            }
            catch { }
        }

        private static void TryBroadcastMP(string msg)
        {
            if (!MultiplayerBridge.IsMultiplayerRunning() || !MultiplayerBridge.IsServer()) return;
            MultiplayerBridge.TryAnnounceAlert(msg);
        }

        private void ExecuteNow()
        {
            if (_target == null)
            {
                _triggeredByDeath = false;
                SetState(RollbackState.Idle);
                return;
            }
            SetState(RollbackState.Executing);
            try
            {
                ModLog.Info(SaveStore.DescribeSavePathDecision());
                ModLog.Info($"执行回档：{_target.File.Name}");
                ModLog.Flow("Rollback", "01", "target=" + _target.FullSlotPath + " sidecar=" + _target.Sidecar.Describe()
                    + " mpRunning=" + MultiplayerBridge.IsMultiplayerRunning());
                BroadcastLocal(I18n.T("msg.rollback_loading"));
                TryBroadcastMP(I18n.T("msg.rollback_loading"));

                bool multiplayerSlot = _target.Sidecar.IsMultiplayer;
                Plugin.ClosePanelIfOpen();
                _store.RestoreSlotToSave(_target.FullSlotPath, backupBefore: false);
                ModLog.Flow("Rollback", "02", "RestoreSlotToSave 完成");
                if (!multiplayerSlot)
                {
                    PlayerCharacterBridge.ApplyForSceneReload();
                    SaveSystem.loadedRun = true;
                    ModLog.Flow("Rollback", "03", "loadedRun=true → LoadScene(SampleScene)");
                    StartLoadSampleScene();
                }
                else
                {
                    ModLog.Flow("Rollback", "03", "多人槽 → 主机两阶段重载");
                }
            }
            catch (Exception ex)
            {
                _triggeredByDeath = false;
                SetState(RollbackState.Idle);
                _lastError = I18n.F("fmt.rollback_failed", ex.Message);
                ModLog.Warning(_lastError);
                BroadcastLocal(_lastError);
            }
        }

        /// <summary>触发黑屏遮罩并重新加载游戏主场景。</summary>
        private static void StartLoadSampleScene()
        {
            try
            {
                GlobalDark.main?.Darken();
            }
            catch { }
            SceneManager.LoadScene("SampleScene");
        }
    }
}
