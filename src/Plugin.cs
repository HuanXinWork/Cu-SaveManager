using System;
using System.IO;
using BepInEx;
using BepInEx.Logging;
using CasualtiesUnknown.SaveManager.Interop;
using UnityEngine;
using UnityEngine.SceneManagement;

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
        internal const string PluginVersion = BuildInfo.Version;

        private static ManualLogSource _log;
        private static Plugin _instance;

        private HotkeyConfig _cfg;
        private SaveStore _store;
        private OverlayUI _overlay;
        private SaveManagerWindow _window;
        private RollbackController _rollback;
        private Action _openPanelAction;

        private float _nextAutoBackupAt = float.PositiveInfinity;
        private string _lastMessage;
        private float _lastMessageAt;

        private bool _inPanelSaveExit;
        private bool _panelSaveExitBuiltSlot;
        private bool _inGameSaveExitHook;
        private bool _isSavingAndExiting;

        private static bool _quitting;

        internal static SaveStore SharedStore => _instance?._store;
        internal static bool ShouldBackupBeforeOverwrite => _instance?._cfg?.BackupBeforeOverwrite?.Value ?? true;
        internal static bool AllowPartialMpRollback => _instance?._cfg?.AllowPartialMultiplayerRollback?.Value ?? false;
        internal static float MpStageTwoDelaySeconds => _instance?._cfg?.MpStageTwoDelaySeconds?.Value ?? 0.75f;
        internal static string PreferredLanguageMode => _instance?._cfg?.PreferredLanguage?.Value ?? "auto";

        internal static void ClosePanelIfOpen() => _instance?._window?.ClosePanel();

        private void Awake()
        {
            _instance = this;
            _log = Logger;
            gameObject.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(gameObject);
            _cfg = new HotkeyConfig(Config);
            ExternalTabRegistry.BindUiScaleConfig(
                () => _cfg.UiScalePercent.Value,
                v =>
                {
                    _cfg.UiScalePercent.Value = v;
                    try { Config.Save(); }
                    catch (Exception ex) { ModLog.Warning($"保存界面缩放配置失败：{ex.Message}"); }
                });
            ModLog.Init(_log);
            ModLog.Debug("[Build] Debug 构建，详细日志已启用");
            ModLog.ShowInConsole = _cfg.ShowLogInConsole.Value;
            _cfg.ShowLogInConsole.SettingChanged += (_, __) => ModLog.ShowInConsole = _cfg.ShowLogInConsole.Value;

            string localizationDir = Path.Combine(Paths.PluginPath, "CuSaveManagerLocales");
            I18n.Init(localizationDir);

            UpdateChecker.Enabled = _cfg.AcceptUpdateNotice.Value;
            _cfg.AcceptUpdateNotice.SettingChanged += (_, __)
                => UpdateChecker.Enabled = _cfg.AcceptUpdateNotice.Value;
            StartingSuppliesOnLoadGuardPatch.Enabled = _cfg.SuppressStartingSuppliesOnLoad.Value;
            _cfg.SuppressStartingSuppliesOnLoad.SettingChanged += (_, __)
                => StartingSuppliesOnLoadGuardPatch.Enabled = _cfg.SuppressStartingSuppliesOnLoad.Value;
            IntroLifepodOnLoadGuardPatch.Enabled = _cfg.SuppressIntroLifepodOnLoad.Value;
            _cfg.SuppressIntroLifepodOnLoad.SettingChanged += (_, __)
                => IntroLifepodOnLoadGuardPatch.Enabled = _cfg.SuppressIntroLifepodOnLoad.Value;

            SyncWorldConfig();
            EventHandler syncWorldHandler = (_, __) => SyncWorldConfig();
            _cfg.PreferredEngine.SettingChanged += syncWorldHandler;
            _cfg.SeedInput.SettingChanged += syncWorldHandler;
            _cfg.PositionMode.SettingChanged += syncWorldHandler;
            _cfg.FixedX.SettingChanged += syncWorldHandler;
            _cfg.FixedY.SettingChanged += syncWorldHandler;

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
            _openPanelAction = () => _window.OpenPanel();

            ResetAutoBackupTimer();
            // 订阅 Application.quitting 而非 OnApplicationQuit，确保在插件对象销毁前先卸载 Harmony 补丁。
            Application.quitting += OnApplicationQuitting;
            SceneManager.sceneLoaded += OnSceneLoaded;
            ModLog.Info($"{PluginName} v{PluginVersion} ready · slots→{_store.SlotsRoot} · save.sv={SaveStore.GameSavePath}");
            ModLog.Info(MultiplayerBridge.IsMultiplayerRunning() || MultiplayerBridge.IsModPresent()
                ? I18n.T("mp.mod_detected") : I18n.T("mp.mod_not_detected"));
            ModLog.Info(QolBridge.IsQolPresent() ? I18n.T("qol.mod_detected") : I18n.T("qol.mod_not_detected"));
            LogInteropResolution();

            HarmonyBootstrap.PatchAllIsolated();
            MainMenuContinueSyncPatch.TryInstallQolPatch(HarmonyBootstrap.Instance);
            MenuButtonUiInjector.Setup(_openPanelAction);
            PauseDialButtonInjector.Setup(_cfg, Config, _openPanelAction);
            PauseQuitButtonReplacer.Setup(_cfg, SaveAndExitCurrentRun, ExitCurrentRunWithoutSave);
            MpRollbackRunner.Ensure(this);

            Action[] deferredPatchers =
            {
                TogetherModHooksBridge.TryBindAll,
                SeededWorldPatcher.TryPatch,
                LoadRunPrefixReplacer.TryPatch,
                MpCharacterRestore.TryPatch,
                MpWorldSeedInjector.TryPatch,
                MpSeedBroadcast.TryPatch,
                MpPositionRestorer.TryPatchHarmony,
                QolSpawnSuppressor.TryPatch,
                QolTrapClearGuard.TryPatch,
            };
            for (int i = 0; i < deferredPatchers.Length; i++)
            {
                try { deferredPatchers[i](); }
                catch (Exception ex) { ModLog.Warning($"延迟补丁初始化失败：{ex.Message}"); }
            }

            gameObject.AddComponent<UpdateChecker>();
        }

        private void Start()
        {
            // 所有 BepInEx 插件 Awake 已执行完毕，通知互操作层可缓存未安装模组的负向结果。
            InteropBootstrap.MarkPluginScanComplete();
        }

        private void SyncWorldConfig()
        {
            float fx = _cfg.FixedX.Value;
            float fy = _cfg.FixedY.Value;
            if (float.IsNaN(fx) || float.IsInfinity(fx)) fx = 0f;
            if (float.IsNaN(fy) || float.IsInfinity(fy)) fy = 0f;
            WorldEngineArbiter.SyncPreference(
                _cfg.PreferredEngine.Value,
                _cfg.SeedInput.Value,
                _cfg.PositionMode.Value,
                fx,
                fy);
        }

        private void LogInteropResolution()
        {
            try
            {
                InteropBootstrap.Initialize();
                ModLog.Debug("Interop：" + InteropBootstrap.Together.DescribeResolvedMembers());
                ModLog.Debug("Interop：" + InteropBootstrap.Qol.DescribeResolvedMembers());
                ModLog.Debug("Interop：" + InteropBootstrap.LayerUnlock.DescribeResolvedMembers());
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
            _isSavingAndExiting = false;
            try { SceneManager.sceneLoaded -= OnSceneLoaded; } catch { }
            try { _rollback?.Dispose(); } catch { }
            try { HarmonyBootstrap.UnpatchAll(); } catch { }
            try { MenuButtonUiInjector.Dispose(); } catch { }
            try { PauseDialButtonInjector.Dispose(); } catch { }
            try { PauseQuitButtonReplacer.Dispose(); } catch { }
            try { OnPanelClosed(); } catch { }
            try { BlackWhiteSkin.Dispose(); } catch { }
            try { Application.quitting -= OnApplicationQuitting; } catch { }
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            _isSavingAndExiting = false;
        }

        private void Update()
        {
            if (_quitting) return;
            UiBlocker.EnforceBlocked();
            _rollback.Tick();

            if (_cfg.AutoBackupEnabled.Value && Time.unscaledTime >= _nextAutoBackupAt)
            {
                if (OverlayUI.IsGamePaused())
                {
                    // 暂停菜单挂机时延后重试，避免世界静止期间反复同步写盘。
                    _nextAutoBackupAt = Time.unscaledTime + 5f;
                }
                else
                {
                    DoAutoBackup();
                    ResetAutoBackupTimer();
                }
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
            if (!_window.Open) _overlay.Draw(_openPanelAction);
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
                    ModLog.Debug(I18n.T("mp.client_skip_autosave"));
                    return;
                }
                if (mpRunning && MultiplayerBridge.IsGamemodeActive())
                {
                    return;
                }
                if (!HasValidLivePlayerForSave(dedicatedServer: false)) return;
                if (!GameSaveBridge.CanSnapshotCurrentLayer() || (mpRunning && IsMpTeamAtLayerFinish()))
                {
                    ModLog.Debug(I18n.T("msg.save_skip_layer_transition"));
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
            // 防止暂停菜单门图标或面板按钮快速连点导致重复写槽或多次触发 SaveAndExit。
            if (_isSavingAndExiting) return;
            _isSavingAndExiting = true;
            bool exitTriggered = false;
            try
            {
                bool mpRunning = MultiplayerBridge.IsMultiplayerRunning();
                if (mpRunning && !MultiplayerBridge.IsServer())
                {
                    NotifySaveExitFailure(I18n.T("mp.client_cannot_save"));
                    return;
                }
                if (PlayerCameraToMainMenuGuard.ShouldBlock())
                {
                    NotifySaveExitFailure(I18n.T("msg.rollback_already_active"));
                    return;
                }
                if (!HasValidLivePlayerForSave(mpRunning && MultiplayerBridge.IsDedicatedServer()))
                {
                    NotifySaveExitFailure(I18n.T("msg.save_exit_unavailable"));
                    return;
                }
                if (!GameSaveBridge.CanSnapshotCurrentLayer())
                {
                    NotifySaveExitFailure(I18n.T("msg.save_layer_transition"));
                    return;
                }

                if (mpRunning)
                {
                    if (!MultiplayerBridge.TrySaveMpGame())
                    {
                        NotifySaveExitFailure(I18n.T("msg.save_exit_unavailable"));
                        return;
                    }
                    _store.SaveManual(I18n.T("lbl.save_exit_alias"));
                    _window.ClosePanel();
                    RestorePauseAndTimeScaleBeforeMainMenu();
                    PlayerCamera mpCam = PlayerCamera.main;
                    if (mpCam != null)
                    {
                        mpCam.ToMainMenu();
                        exitTriggered = true;
                    }
                    SetMessage(I18n.T("msg.save_exit_mp_done"));
                    return;
                }

                // 单机处于层底时转发原生 WorldGeneration.SaveAndExit，完整触发层底结算与 QoL BypassDepthFix。
                if (SaveStore.IsPlayerAtLayerBoundary())
                {
                    if (WorldGeneration.world == null)
                    {
                        NotifySaveExitFailure(I18n.T("msg.save_exit_unavailable"));
                        return;
                    }
                    _window.ClosePanel();
                    RestorePauseAndTimeScaleBeforeMainMenu();
                    _inPanelSaveExit = true;
                    _panelSaveExitBuiltSlot = false;
                    try
                    {
                        WorldGeneration.world.SaveAndExit();
                        exitTriggered = true;
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
                    NotifySaveExitFailure(I18n.T("msg.save_exit_unavailable"));
                    return;
                }

                ModLog.Flow("UI", "21", "保存并退出：SaveSystem.SaveGame 完成");
                _store.SaveManual(I18n.T("lbl.save_exit_alias"));
                ModLog.Flow("UI", "22", "保存并退出：槽位已写入，回主菜单");
                _window.ClosePanel();
                RestorePauseAndTimeScaleBeforeMainMenu();
                PlayerCamera spCam = PlayerCamera.main;
                if (spCam != null)
                {
                    spCam.ToMainMenu();
                    exitTriggered = true;
                }
                SetMessage(I18n.T("msg.save_exit_done"));
            }
            catch (Exception ex)
            {
                NotifySaveExitFailure(I18n.F("fmt.save_exit_failed", ex.Message));
                ModLog.Warning(ex.ToString());
            }
            finally
            {
                if (!exitTriggered)
                {
                    _isSavingAndExiting = false;
                }
            }
        }

        /// <summary>直接退回主菜单而不执行任何存档或 sidecar 写盘，不受角色存活状态限制。</summary>
        private void ExitCurrentRunWithoutSave()
        {
            if (_isSavingAndExiting) return;
            _isSavingAndExiting = true;
            bool exitTriggered = false;
            try
            {
                PlayerCamera cam = PlayerCamera.main;
                if (cam == null)
                {
                    NotifySaveExitFailure(I18n.T("msg.exit_without_save_unavailable"));
                    return;
                }
                if (PlayerCameraToMainMenuGuard.ShouldBlock())
                {
                    NotifySaveExitFailure(I18n.T("msg.rollback_already_active"));
                    return;
                }

                _window?.ClosePanel();
                RestorePauseAndTimeScaleBeforeMainMenu();

                // 跳过退回主菜单时的 sidecar 坐标持久化，但保留会话缓存清理。
                ToMainMenuPersistPositionPatch.SuppressPersistOnExit = true;
                try
                {
                    cam.ToMainMenu();
                    exitTriggered = true;
                }
                finally
                {
                    ToMainMenuPersistPositionPatch.SuppressPersistOnExit = false;
                }

                SetMessage(I18n.T("msg.exit_without_save_done"));
            }
            catch (Exception ex)
            {
                NotifySaveExitFailure(I18n.F("fmt.exit_without_save_failed", ex.Message));
                ModLog.Warning(ex.ToString());
            }
            finally
            {
                if (!exitTriggered)
                {
                    _isSavingAndExiting = false;
                }
            }
        }

        private void NotifySaveExitFailure(string msg)
        {
            SetMessage(msg);
            // 从暂停菜单门按钮直接触发且模组面板未打开时，经游戏内原版横幅提示失败原因。
            if (_window == null || !_window.Open)
            {
                try { PlayerCamera.main?.DoAlert(msg, important: true); }
                catch (Exception ex) { ModLog.Debug($"暂停菜单退出失败提示显示异常：{ex.Message}"); }
            }
        }

        private static void RestorePauseAndTimeScaleBeforeMainMenu()
        {
            // 切回主菜单前解除暂停态并恢复时间缩放，避免主菜单协程或界面动画残留暂停或 0 倍速。
            if (PauseHandler.main != null && PauseHandler.main.isPaused)
            {
                PauseHandler.main.isPaused = false;
            }
            Time.timeScale = 1f;
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
                if (PreRunScript.instance != null)
                {
                    MainMenuContinueSyncPatch.RefreshMainMenuContinueState();
                }
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
                bool deleted = _store.DeleteSlot(slot.FullSlotPath);
                _window?.InvalidateSlotCache();
                SetMessage(I18n.F(deleted ? "fmt.deleted" : "fmt.delete_failed", slot.File.Name));
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
            ModLog.Debug(SaveStore.DescribeSavePathDecision());
        }

        private void OnPanelClosed()
        {
            UiBlocker.Unblock();
            try { Config?.Save(); } catch { }
        }
    }
}
