using System;
using System.IO;
using BepInEx;
using BepInEx.Logging;
using CasualtiesUnknown.SaveManager.Interop;
using UnityEngine;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>插件主入口：装配配置、存档仓库、UI 面板与回档状态机，调度快捷键与定时自动备份。</summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency("CasualtiesMP", BepInDependency.DependencyFlags.SoftDependency)]
    // 软依赖确保 QoL 先于本插件加载，Awake 阶段即可解析其静态成员。
    [BepInDependency(QolGuid, BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class Plugin : BaseUnityPlugin
    {
        private const string PluginGuid = "com.casualtiesUnknown.saveManager";
        private const string QolGuid = "org.bepinex.plugins.qol_unknown";
        private const string PluginName = "CuSaveManager";
        internal const string PluginVersion = "1.3.3";

        private static ManualLogSource _log;
        private static Plugin _instance;

        private HotkeyConfig _cfg;
        private SaveStore _store;
        private OverlayUI _overlay;
        private SaveManagerWindow _window;
        private RollbackController _rollback;

        private float _nextAutoBackupAt = float.PositiveInfinity;
        private string _lastMessage;
        private float _lastMessageAt;

        private bool _inPanelSaveExit;
        private bool _panelSaveExitBuiltSlot;
        private bool _inGameSaveExitHook;

        private static bool _quitting;

        internal static string PreferredLanguageMode => _instance?._cfg?.PreferredLanguage?.Value ?? "auto";

        internal static void ClosePanelIfOpen() => _instance?._window?.ClosePanel();

        private void Awake()
        {
            _instance = this;
            _log = Logger;
            gameObject.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(gameObject);
            _cfg = new HotkeyConfig(Config);
            ModLog.Init(_log);
            ModLog.Debug("[Build] Debug 构建，详细日志已启用");
            ModLog.ShowInConsole = _cfg.ShowLogInConsole.Value;
            _cfg.ShowLogInConsole.SettingChanged += (_, __) => ModLog.ShowInConsole = _cfg.ShowLogInConsole.Value;
            UpdateChecker.Enabled = _cfg.AcceptUpdateNotice.Value;
            StartingSuppliesOnLoadGuardPatch.Enabled = _cfg.SuppressStartingSuppliesOnLoad.Value;
            _cfg.SuppressStartingSuppliesOnLoad.SettingChanged += (_, __)
                => StartingSuppliesOnLoadGuardPatch.Enabled = _cfg.SuppressStartingSuppliesOnLoad.Value;
            IntroLifepodOnLoadGuardPatch.Enabled = _cfg.SuppressIntroLifepodOnLoad.Value;
            _cfg.SuppressIntroLifepodOnLoad.SettingChanged += (_, __)
                => IntroLifepodOnLoadGuardPatch.Enabled = _cfg.SuppressIntroLifepodOnLoad.Value;
            WorldEngineArbiter.SyncPreference(_cfg.PreferredEngine.Value, _cfg.SeedInput.Value,
                _cfg.PositionMode.Value, _cfg.FixedX.Value, _cfg.FixedY.Value);
            _store = new SaveStore();
            _rollback = new RollbackController(_cfg, _store);

            _overlay = new OverlayUI();
            _window = new SaveManagerWindow(_cfg, _store,
                onSaveNow: () => SaveManual(""),
                onSaveNowAs: nick => SaveManual(nick),
                onSaveAndExit: SaveAndExitCurrentRun,
                onLoadSlot: LoadFromSlot,
                onDeleteSlot: DeleteSlot,
                onIntervalChanged: ResetAutoBackupTimer,
                getStatus: () => BuildStatusLine(),
                onOpened: OnPanelOpened,
                onClosed: OnPanelClosed,
                rollback: _rollback);

            MenuButtonUiInjector.Setup(() => _window.OpenPanel());

            ResetAutoBackupTimer();
            // 订阅 Application.quitting 而非 OnApplicationQuit，确保在插件对象销毁前先卸载 Harmony 补丁。
            Application.quitting += OnApplicationQuitting;
            ModLog.Info($"{PluginName} ready · slots→{_store.SlotsRoot} · save.sv={SaveStore.GameSavePath}");
            ModLog.Info(MultiplayerBridge.IsMultiplayerRunning() || MultiplayerBridge.IsModPresent()
                ? I18n.T("mp.mod_detected") : I18n.T("mp.mod_not_detected"));
            ModLog.Info(QolBridge.IsQolPresent() ? I18n.T("qol.mod_detected") : I18n.T("qol.mod_not_detected"));
            LogInteropResolution();
            TogetherModHooksBridge.TryBindAll();
            MpWorldSeedInjector.TryPatch();
            MpSeedBroadcast.TryPatch();
            MpRollbackRunner.Ensure(this);
            MpPositionRestorer.TryPatchHarmony();
            QolSpawnSuppressor.TryPatch();
            QolTrapClearGuard.TryPatch();
            gameObject.AddComponent<UpdateChecker>();
        }

        private void LogInteropResolution()
        {
            try
            {
                InteropBootstrap.Initialize();
                ModLog.Info("Interop：" + InteropBootstrap.Together.DescribeResolvedMembers());
                ModLog.Info("Interop：" + InteropBootstrap.Qol.DescribeResolvedMembers());
                ModLog.Info("Interop：" + InteropBootstrap.LayerUnlock.DescribeResolvedMembers());
            }
            catch (Exception ex)
            {
                ModLog.Warning($"Interop 自检失败：{ex.Message}");
            }
        }

        private void OnDestroy() => OnApplicationQuitting();

        private void OnApplicationQuitting()
        {
            if (_quitting) return;
            _quitting = true;
            try { _rollback?.Dispose(); } catch { }
            try { MenuButtonUiInjector.Dispose(); } catch { }
            try { OnPanelClosed(); } catch { }
            try { BlackWhiteSkin.Dispose(); } catch { }
            try { Application.quitting -= OnApplicationQuitting; } catch { }
        }

        private void Update()
        {
            if (_quitting) return;
            UiBlocker.EnforceBlocked();
            _rollback.Tick();

            if (_cfg.AutoBackupEnabled.Value && Time.unscaledTime >= _nextAutoBackupAt)
            {
                DoAutoBackup();
                ResetAutoBackupTimer();
            }
        }

        private void LateUpdate()
        {
            if (_quitting) return;
            bool expectsInput = _window.ExpectsTextInput;
            ImGuiImeRecovery.TickUpdate(expectsInput);

            // 游戏 ESC 已被 PlayerCameraHandleInputGuard 拦截，在此优先取消改键录制或关闭主面板。
            if (_window.Open && Input.GetKeyDown(KeyCode.Escape))
            {
                if (_window.IsCapturingKey) _window.CancelKeyCapture();
                else _window.ClosePanel();
                return;
            }

            if (expectsInput) return;

            if (HotkeyConfig.TriggeredThisFrame(_cfg.ToggleSlotsHotkey))
            {
                if (_window.Open) _window.ClosePanel();
                else _window.OpenPanel();
            }
            if (HotkeyConfig.TriggeredThisFrame(_cfg.ToggleSettingsHotkey))
            {
                _window.OpenSettingsTab();
            }
            if (HotkeyConfig.TriggeredThisFrame(_cfg.QuickSaveHotkey))
            {
                SaveManual("");
            }
        }

        private void OnGUI()
        {
            if (_quitting) return;
            ImGuiImeRecovery.TickOnGui(_window.ExpectsTextInput);
            if (!_window.Open) _overlay.Draw(() => _window.OpenPanel());
            _window.Draw();
        }

        private void OnDisable()
        {
            OnPanelClosed();
        }

        private static bool HasValidLivePlayerForSave(bool dedicatedServer)
        {
            if (RollbackController.IsDeathSuspected || RollbackController.IsActiveGlobal) return false;
            if (WorldGeneration.world == null) return false;
            if (dedicatedServer) return true;
            var cam = PlayerCamera.main;
            return cam != null && cam.body != null && cam.body.alive;
        }

        private static bool IsMpTeamAtLayerFinish()
        {
            if (!MultiplayerBridge.IsMultiplayerRunning() || !MultiplayerBridge.IsServer()) return false;
            return MultiplayerBridge.TryCheckTeamAtLayerFinish(out bool atFinish)
                ? atFinish
                : SaveStore.IsPlayerAtLayerBoundary();
        }

        private void SaveManual(string nickname)
        {
            try
            {
                bool mpRunning = MultiplayerBridge.IsMultiplayerRunning();
                if (mpRunning && !MultiplayerBridge.IsServer())
                {
                    SetMessage(I18n.T("mp.client_cannot_save"));
                    return;
                }
                if (!HasValidLivePlayerForSave(mpRunning && MultiplayerBridge.IsDedicatedServer()))
                {
                    SetMessage(I18n.T("msg.save_exit_unavailable"));
                    return;
                }
                if (!GameSaveBridge.CanSnapshotCurrentLayer())
                {
                    SetMessage(I18n.T("msg.save_layer_transition"));
                    return;
                }

                bool saved = mpRunning
                    ? MultiplayerBridge.TrySaveMpGame()
                    : GameSaveBridge.TrySaveGame();
                if (!saved)
                {
                    SetMessage(I18n.T("msg.save_exit_unavailable"));
                    return;
                }

                string path = _store.SaveManual(nickname);
                _window?.InvalidateSlotCache();
                ModLog.Flow("UI", "11", "手动保存完成 " + path + " nickname='" + (nickname ?? "") + "'");
                SetMessage(I18n.F("fmt.saved_to", Path.GetFileName(path), SaveStore.CurrentSaveDisplayPath));
            }
            catch (Exception ex)
            {
                SetMessage(I18n.F("fmt.save_failed", ex.Message));
                ModLog.Warning(ex.ToString());
            }
        }

        private void DoAutoBackup()
        {
            try
            {
                if (MultiplayerBridge.IsDedicatedServer()) return;
                bool mpRunning = MultiplayerBridge.IsMultiplayerRunning();
                if (mpRunning && !MultiplayerBridge.IsServer())
                {
                    ModLog.Info(I18n.T("mp.client_skip_autosave"));
                    return;
                }
                if (mpRunning && MultiplayerBridge.IsGamemodeActive())
                {
                    return;
                }
                if (!HasValidLivePlayerForSave(dedicatedServer: false)) return;
                if (!GameSaveBridge.CanSnapshotCurrentLayer() || (mpRunning && IsMpTeamAtLayerFinish()))
                {
                    ModLog.Info(I18n.T("msg.save_skip_layer_transition"));
                    return;
                }

                // 先触发游戏落盘当前内存物品与身体状态，成功后再打包槽位。
                bool saved = mpRunning
                    ? MultiplayerBridge.TrySaveMpGame()
                    : GameSaveBridge.TrySaveGame();
                if (!saved) return;

                string path = _store.AutoBackup(_cfg.AutoBackupKeep.Value);
                _window?.InvalidateSlotCache();
                ModLog.Flow("UI", "12", "自动备份完成 " + path + " keep=" + _cfg.AutoBackupKeep.Value);
                ModLog.Info(I18n.F("fmt.auto_backup_done", Path.GetFileName(path)));
            }
            catch (Exception ex)
            {
                ModLog.Warning(I18n.F("fmt.auto_backup_failed", ex.Message));
            }
        }

        private void SaveAndExitCurrentRun()
        {
            try
            {
                bool mpRunning = MultiplayerBridge.IsMultiplayerRunning();
                if (mpRunning && !MultiplayerBridge.IsServer())
                {
                    SetMessage(I18n.T("mp.client_cannot_save"));
                    return;
                }
                if (!HasValidLivePlayerForSave(mpRunning && MultiplayerBridge.IsDedicatedServer()))
                {
                    SetMessage(I18n.T("msg.save_exit_unavailable"));
                    return;
                }
                if (!GameSaveBridge.CanSnapshotCurrentLayer())
                {
                    SetMessage(I18n.T("msg.save_layer_transition"));
                    return;
                }

                if (mpRunning)
                {
                    if (!MultiplayerBridge.TrySaveMpGame())
                    {
                        SetMessage(I18n.T("msg.save_exit_unavailable"));
                        return;
                    }
                    _store.SaveManual(I18n.T("lbl.save_exit_alias"));
                    _window.ClosePanel();
                    PlayerCamera.main.ToMainMenu();
                    SetMessage(I18n.T("msg.save_exit_mp_done"));
                    return;
                }

                // 单机处于层底时转发原生 WorldGeneration.SaveAndExit，完整触发层底结算与 QoL BypassDepthFix。
                if (SaveStore.IsPlayerAtLayerBoundary())
                {
                    _window.ClosePanel();
                    _inPanelSaveExit = true;
                    _panelSaveExitBuiltSlot = false;
                    try
                    {
                        WorldGeneration.world.SaveAndExit();
                        if (!_panelSaveExitBuiltSlot && SaveStore.CurrentSaveExists())
                        {
                            _store.SaveManual(I18n.T("lbl.save_exit_alias"));
                        }
                    }
                    finally
                    {
                        _inPanelSaveExit = false;
                        _panelSaveExitBuiltSlot = false;
                        _store.ClearBeforeSaveAndExit();
                    }
                    SetMessage(I18n.T("msg.save_exit_done"));
                    return;
                }

                if (!GameSaveBridge.TrySaveGame())
                {
                    SetMessage(I18n.T("msg.save_exit_unavailable"));
                    return;
                }

                ModLog.Flow("UI", "21", "保存并退出：SaveSystem.SaveGame 完成");
                _store.SaveManual(I18n.T("lbl.save_exit_alias"));
                ModLog.Flow("UI", "22", "保存并退出：槽位已写入，回主菜单");
                _window.ClosePanel();
                PlayerCamera.main.ToMainMenu();
                SetMessage(I18n.T("msg.save_exit_done"));
            }
            catch (Exception ex)
            {
                SetMessage(I18n.F("fmt.save_exit_failed", ex.Message));
                ModLog.Warning(ex.ToString());
            }
        }

        /// <summary>原生 WorldGeneration.SaveAndExit 执行前抓拍实时种子与上下文，防止 ToMainMenu 先一步清零。</summary>
        internal static void OnBeforeGameSaveAndExit()
        {
            try { _instance?._store?.CaptureBeforeSaveAndExit(); }
            catch (Exception ex) { ModLog.Warning($"保存并退出前预抓拍失败：{ex.Message}"); }
        }

        /// <summary>清除 SaveAndExit 前置抓拍缓存（在被上游补丁拦截或异常中断时防止脏标志残留）。</summary>
        internal static void ClearBeforeGameSaveAndExit()
        {
            _instance?._store?.ClearBeforeSaveAndExit();
        }

        /// <summary>原生 WorldGeneration.SaveAndExit 完成后补建存档槽位。</summary>
        internal static void OnGameSaveAndExit()
        {
            _instance?.SaveSlotForGameSaveExit();
        }

        private void SaveSlotForGameSaveExit()
        {
            if (_inGameSaveExitHook) return;
            _inGameSaveExitHook = true;
            try
            {
                if (MultiplayerBridge.IsMultiplayerRunning() && !MultiplayerBridge.IsServer())
                {
                    ModLog.Flow("UI", "31", "游戏保存并退出：客户端不建槽位");
                    return;
                }
                if (!SaveStore.CurrentSaveExists())
                {
                    ModLog.Flow("UI", "31", "游戏保存并退出：没有 save.sv，跳过");
                    return;
                }
                string path = _store.SaveManual(I18n.T("lbl.save_exit_alias"));
                ModLog.Flow("UI", "32", "游戏保存并退出：已补建槽位 " + path);
                if (_inPanelSaveExit)
                {
                    _panelSaveExitBuiltSlot = true;
                }
                else
                {
                    SetMessage(I18n.F("fmt.saved_to", Path.GetFileName(path), SaveStore.CurrentSaveDisplayPath));
                }
            }
            catch (Exception ex)
            {
                ModLog.Warning($"游戏保存并退出建槽失败：{ex.Message}");
            }
            finally
            {
                _inGameSaveExitHook = false;
                _store.ClearBeforeSaveAndExit();
            }
        }

        private void LoadFromSlot(SlotInfo slot)
        {
            try
            {
                ModLog.Flow("UI", "01", "切换槽位 " + slot.FullSlotPath + " sidecar=" + slot.Sidecar.Describe()
                    + " mpRunning=" + MultiplayerBridge.IsMultiplayerRunning());
                _store.RestoreSlotToSave(slot.FullSlotPath, _cfg.BackupBeforeOverwrite.Value);
                _window?.InvalidateSlotCache();
                ModLog.Flow("UI", "02", "切换槽位完成 " + slot.File.Name);
                SetMessage(I18n.F("fmt.restored_to", slot.File.Name));
            }
            catch (Exception ex)
            {
                SetMessage(I18n.F("fmt.restore_failed", ex.Message));
                ModLog.Warning(ex.ToString());
            }
        }

        private void DeleteSlot(SlotInfo slot)
        {
            try
            {
                _store.DeleteSlot(slot.FullSlotPath);
                _window?.InvalidateSlotCache();
                SetMessage(I18n.F("fmt.deleted", slot.File.Name));
            }
            catch (Exception ex)
            {
                SetMessage(I18n.F("fmt.delete_failed", ex.Message));
                ModLog.Warning(ex.ToString());
            }
        }

        private void SetMessage(string msg)
        {
            _lastMessage = msg;
            _lastMessageAt = Time.unscaledTime;
            ModLog.Info(msg);
        }

        private string BuildStatusLine()
        {
            if (_lastMessage != null && Time.unscaledTime - _lastMessageAt < 8f)
            {
                return _lastMessage;
            }
            return I18n.F("status.save_path", SaveStore.CurrentSaveDisplayPath);
        }

        private void ResetAutoBackupTimer()
        {
            float interval = Mathf.Max(0.5f, _cfg.AutoBackupIntervalMinutes.Value) * 60f;
            _nextAutoBackupAt = Time.unscaledTime + interval;
        }

        private void OnPanelOpened()
        {
            UiBlocker.Block();
            ModLog.Info(SaveStore.DescribeSavePathDecision());
        }

        private void OnPanelClosed()
        {
            UiBlocker.Unblock();
        }
    }
}
