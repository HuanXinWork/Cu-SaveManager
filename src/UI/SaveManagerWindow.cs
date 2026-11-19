using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BepInEx.Configuration;
using UnityEngine;


namespace CasualtiesUnknown.SaveManager
{
    /// <summary>存档管理主窗口，负责设置、槽位管理、回档、关于及外部模组侧栏分页渲染。</summary>
    internal sealed class SaveManagerWindow
    {
        private const int WindowId = 0x5A4D5310;
        private const float WindowWidth = 1440f;
        private const float WindowHeight = 900f;
        private const float TitleBarHeight = 64f;
        private const float CloseBtnSize = 52f;
        private const float LabelColW = 220f;
        private const float RowMinHeight = 52f;
        private const float SlotCacheTtlSeconds = 0.5f;

        private readonly HotkeyConfig _cfg;
        private readonly SaveStore _store;
        private readonly Action _onSaveNow;
        private readonly Action<string> _onSaveNowAs;
        private readonly Action _onSaveAndExit;
        private readonly Action<SlotInfo> _onLoadSlot;
        private readonly Action<SlotInfo> _onDeleteSlot;
        private readonly Action _onIntervalChanged;
        private readonly Func<string> _getStatus;
        private readonly Action _onOpened;
        private readonly Action _onClosed;
        private readonly RollbackTabView _rollbackTab;

        private Rect _rect = new Rect(120f, 60f, WindowWidth, WindowHeight);
        private float _drawScale = 1f;
        private int _tab;
        private int _sideIdx;
        private Vector2 _sidebarScroll;
        private Vector2 _scroll;
        private Vector2 _settingsScroll;
        private Vector2 _aboutScroll;

        private string _quickNickname = "";

        private bool _capturingSettingsKey;
        private bool _capturingSlotsKey;
        private bool _capturingQuickKey;

        private string _seedInputCache;
        private string _fixedXCache;
        private string _fixedYCache;

        private readonly Dictionary<string, string> _editingNicknames = new Dictionary<string, string>();
        private readonly Dictionary<string, bool> _dateExpanded = new Dictionary<string, bool>();
        private readonly Dictionary<string, bool> _runIdExpanded = new Dictionary<string, bool>();

        private string _nicknameEditingPath;
        private bool _loggedDrawForCurrentOpen;

        private List<SlotInfo> _cachedSlots;
        private string _cachedSaveHash;
        private int _cachedRunId;
        private float _nextCacheRefreshAt;
        private bool _slotCacheDirty = true;

        internal bool Open { get; set; }

        /// <summary>当前是否正在等待用户按下新的绑定快捷键。</summary>
        internal bool IsCapturingKey => _capturingSettingsKey || _capturingSlotsKey || _capturingQuickKey;

        /// <summary>当前是否处于文本编辑或按键捕获态。</summary>
        internal bool ExpectsTextInput =>
            Open && (IsCapturingKey
                     || !string.IsNullOrEmpty(_nicknameEditingPath)
                     || GUIUtility.keyboardControl != 0);

        /// <summary>打开主面板并执行打开钩子（阻断下层 uGUI 射线等）。</summary>
        internal void OpenPanel()
        {
            if (Open) return;
            Open = true;
            _loggedDrawForCurrentOpen = false;
            _seedInputCache = null;
            _fixedXCache = null;
            _fixedYCache = null;
            InvalidateSlotCache();
            ModLog.Info("SaveManagerWindow.OpenPanel -> Open=true");
            _onOpened?.Invoke();
        }

        /// <summary>打开主面板并定位到本模组设置分页。</summary>
        internal void OpenSettingsTab()
        {
            _sideIdx = 0;
            _tab = 0;
            OpenPanel();
            CancelKeyCapture();
        }

        /// <summary>关闭主面板、取消按键捕获并释放键盘焦点。</summary>
        internal void ClosePanel()
        {
            if (!Open) return;
            Open = false;
            _loggedDrawForCurrentOpen = false;
            CancelKeyCapture();
            _nicknameEditingPath = null;
            InvalidateSlotCache();
            ImGuiImeRecovery.RequestClear();
            _onClosed?.Invoke();
        }

        internal void InvalidateSlotCache() => _slotCacheDirty = true;

        internal SaveManagerWindow(HotkeyConfig cfg, SaveStore store,
            Action onSaveNow, Action<string> onSaveNowAs, Action onSaveAndExit,
            Action<SlotInfo> onLoadSlot, Action<SlotInfo> onDeleteSlot,
            Action onIntervalChanged, Func<string> getStatus, Action onOpened, Action onClosed,
            RollbackController rollback)
        {
            _cfg = cfg;
            _store = store;
            _onSaveNow = onSaveNow;
            _onSaveNowAs = onSaveNowAs;
            _onSaveAndExit = onSaveAndExit;
            _onLoadSlot = onLoadSlot;
            _onDeleteSlot = onDeleteSlot;
            _onIntervalChanged = onIntervalChanged;
            _getStatus = getStatus;
            _onOpened = onOpened;
            _onClosed = onClosed;
            _rollbackTab = new RollbackTabView(cfg, store, rollback, GetRollbackSnapshot, InvalidateSlotCache);
        }

        internal void Draw()
        {
            if (!Open) return;
            RefreshSlotCacheIfNeeded();

            _drawScale = ComputeScale();
            if (!_loggedDrawForCurrentOpen)
            {
                _loggedDrawForCurrentOpen = true;
                ModLog.Info($"SaveManagerWindow.Draw screen=({Screen.width}x{Screen.height}) scale={_drawScale:F3} rect=({_rect.x:0},{_rect.y:0},{_rect.width:0},{_rect.height:0})");
            }
            _rect.width = WindowWidth;
            _rect.height = WindowHeight;
            float maxX = Mathf.Max(0f, Screen.width / _drawScale - WindowWidth);
            float maxY = Mathf.Max(0f, Screen.height / _drawScale - WindowHeight);
            _rect.x = Mathf.Clamp(_rect.x, 0f, maxX);
            _rect.y = Mathf.Clamp(_rect.y, 0f, maxY);

            BlackWhiteSkin.Push();
            Matrix4x4 prev = GUI.matrix;
            try
            {
                GUI.matrix = Matrix4x4.Scale(new Vector3(_drawScale, _drawScale, 1f));
                _rect = GUI.ModalWindow(WindowId, _rect, DrawContent, "");
            }
            catch (ExitGUIException)
            {
                throw;
            }
            catch (Exception ex)
            {
                ModLog.Warning($"SaveManagerWindow.Draw 失败：{ex}");
                ClosePanel();
            }
            finally
            {
                GUI.matrix = prev;
                BlackWhiteSkin.Pop();
            }
        }

        private void RefreshSlotCacheIfNeeded()
        {
            if (_sideIdx != 0 || (_tab != 1 && _tab != 2)) return;

            var ev = Event.current;
            bool canRefreshNow = _cachedSlots == null || (ev != null && ev.type == EventType.Layout);
            if (!canRefreshNow) return;
            if (!_slotCacheDirty && Time.unscaledTime < _nextCacheRefreshAt) return;

            _cachedSlots = _store.ListSlots();
            _cachedSaveHash = _tab == 1 ? SaveStore.ComputeCurrentSaveHash() : null;
            _cachedRunId = SaveStore.ComputeCurrentRunId();
            _slotCacheDirty = false;
            _nextCacheRefreshAt = Time.unscaledTime + SlotCacheTtlSeconds;
        }

        private (List<SlotInfo> slots, int currentRunId) GetRollbackSnapshot()
        {
            if (_cachedSlots == null)
            {
                _cachedSlots = _store.ListSlots();
                _cachedRunId = SaveStore.ComputeCurrentRunId();
                _slotCacheDirty = false;
                _nextCacheRefreshAt = Time.unscaledTime + SlotCacheTtlSeconds;
            }
            return (_cachedSlots, _cachedRunId);
        }

        private static float ComputeScale()
        {
            float fit = Mathf.Min(Screen.width / WindowWidth, Screen.height / WindowHeight) * 0.92f;
            return Mathf.Clamp(fit, 0.3f, 1f);
        }

        private static void ClearTextFocus()
        {
            GUI.FocusControl(null);
            GUIUtility.keyboardControl = 0;
        }

        internal void CancelKeyCapture()
        {
            _capturingSettingsKey = false;
            _capturingSlotsKey = false;
            _capturingQuickKey = false;
        }

        private void DrawContent(int id)
        {
            BlackWhiteSkin.DrawBorder(new Rect(0f, 0f, WindowWidth, WindowHeight), 6f);

            GUI.Label(new Rect(28f, 14f, WindowWidth - CloseBtnSize - 56f, 40f),
                I18n.T("app.name"), BlackWhiteSkin.HeaderStyle);

            var closeRect = new Rect(WindowWidth - CloseBtnSize - 12f, 8f, CloseBtnSize, CloseBtnSize);
            if (GUI.Button(closeRect, GUIContent.none))
            {
                ClosePanel();
                GUIUtility.ExitGUI();
            }
            BlackWhiteSkin.DrawBorder(closeRect, 4f);
            BlackWhiteSkin.DrawCloseX(new Rect(closeRect.x + 13f, closeRect.y + 13f,
                closeRect.width - 26f, closeRect.height - 26f), 6f);

            BlackWhiteSkin.DrawHLine(new Rect(0f, TitleBarHeight, WindowWidth, 4f));

            const float SidebarWidth = 240f;
            const float SidebarLeftPad = 28f;
            const float SidebarRightPad = 12f;
            const float StatusBarH = 60f;
            float bodyTop = TitleBarHeight + 12f;
            float bodyH = WindowHeight - bodyTop - StatusBarH;

            DrawSidebar(new Rect(SidebarLeftPad, bodyTop, SidebarWidth, bodyH));

            float dividerX = SidebarLeftPad + SidebarWidth + SidebarRightPad;
            float mainX = dividerX + 16f;
            float mainW = WindowWidth - mainX - 16f;
            var mainRect = new Rect(mainX, bodyTop, mainW, bodyH);

            BlackWhiteSkin.DrawHLine(new Rect(dividerX, bodyTop, 4f, bodyH));

            if (_sideIdx == 0) DrawSelfTabs(mainRect);
            else DrawExternal(mainRect, _sideIdx - 1);

            BlackWhiteSkin.DrawHLine(new Rect(0f, WindowHeight - StatusBarH, WindowWidth, 4f));
            GUI.Label(new Rect(28f, WindowHeight - StatusBarH + 16f, WindowWidth - 56f, 32f), ResolveStatus());

            GUI.DragWindow(new Rect(0f, 0f, WindowWidth - CloseBtnSize - 24f, TitleBarHeight));
        }

        private string ResolveStatus()
        {
            if (_sideIdx == 0) return _getStatus?.Invoke() ?? "";
            var ext = ExternalTabRegistry.Entries;
            int idx = _sideIdx - 1;
            return idx >= 0 && idx < ext.Count ? ext[idx].Status : "";
        }

        private void DrawSidebar(Rect area)
        {
            const float BtnH = 64f;
            const float Gap = 8f;
            const float TopPad = 8f;
            var ext = ExternalTabRegistry.Entries;
            int total = 1 + ext.Count;
            float contentH = TopPad + total * BtnH + Mathf.Max(0, total - 1) * Gap + TopPad;
            bool needScroll = contentH > area.height;
            float btnW = needScroll ? area.width - 28f : area.width;

            if (needScroll)
            {
                _sidebarScroll = GUI.BeginScrollView(area, _sidebarScroll, new Rect(0f, 0f, btnW, contentH));
            }

            float baseX = needScroll ? 0f : area.x;
            float baseY = needScroll ? TopPad : area.y + TopPad;
            for (int i = 0; i < total; i++)
            {
                var rect = new Rect(baseX, baseY + i * (BtnH + Gap), btnW, BtnH);
                string label = i == 0 ? I18n.T("app.name") : ext[i - 1].Title;
                var style = i == _sideIdx ? BlackWhiteSkin.TabActiveStyle : BlackWhiteSkin.TabStyle;
                if (GUI.Button(rect, label, style) && _sideIdx != i)
                {
                    _sideIdx = i;
                    InvalidateSlotCache();
                    ClearTextFocus();
                    CancelKeyCapture();
                    if (needScroll) GUI.EndScrollView();
                    GUIUtility.ExitGUI();
                }
            }

            if (needScroll) GUI.EndScrollView();
        }

        private void DrawSelfTabs(Rect mainRect)
        {
            const float TabRowH = 64f;
            DrawSelfTabBar(new Rect(mainRect.x, mainRect.y, mainRect.width, TabRowH));
            BlackWhiteSkin.DrawHLine(new Rect(mainRect.x, mainRect.y + TabRowH + 14f, mainRect.width, 4f));
            float bodyTop = mainRect.y + TabRowH + 26f;
            var bodyRect = new Rect(mainRect.x, bodyTop, mainRect.width, mainRect.height - (bodyTop - mainRect.y));
            GUILayout.BeginArea(bodyRect);
            if (_tab == 0) DrawSettingsTab();
            else if (_tab == 1) DrawSlotsTab();
            else if (_tab == 2) _rollbackTab.Draw();
            else if (_tab == 3) DrawAboutTab();
            GUILayout.EndArea();
        }

        private void DrawSelfTabBar(Rect rowRect)
        {
            const float Gap = 12f;
            float tabW = (rowRect.width - Gap * 3f) / 4f;
            DrawTabButton(new Rect(rowRect.x, rowRect.y, tabW, rowRect.height), I18n.T("tab.settings"), 0);
            DrawTabButton(new Rect(rowRect.x + (tabW + Gap), rowRect.y, tabW, rowRect.height), I18n.T("tab.slots"), 1);
            DrawTabButton(new Rect(rowRect.x + (tabW + Gap) * 2f, rowRect.y, tabW, rowRect.height), I18n.T("tab.rollback"), 2);
            DrawTabButton(new Rect(rowRect.x + (tabW + Gap) * 3f, rowRect.y, tabW, rowRect.height), I18n.T("tab.about"), 3);
        }

        private void DrawExternal(Rect mainRect, int extIdx)
        {
            var ext = ExternalTabRegistry.Entries;
            if (extIdx < 0 || extIdx >= ext.Count)
            {
                _sideIdx = 0;
                GUIUtility.ExitGUI();
                return;
            }
            const float Pad = 20f;
            var inner = new Rect(mainRect.x + Pad, mainRect.y + Pad, mainRect.width - Pad * 2f, mainRect.height - Pad * 2f);
            GUILayout.BeginArea(inner);
            try
            {
                ext[extIdx].Draw?.Invoke();
            }
            catch (ExitGUIException)
            {
                throw;
            }
            catch (Exception ex)
            {
                GUILayout.Label("external tab error: " + ex.Message);
            }
            GUILayout.EndArea();
        }

        private void DrawTabButton(Rect rect, string label, int idx)
        {
            var style = idx == _tab ? BlackWhiteSkin.TabActiveStyle : BlackWhiteSkin.TabStyle;
            if (GUI.Button(rect, label, style) && _tab != idx)
            {
                _tab = idx;
                InvalidateSlotCache();
                ClearTextFocus();
                CancelKeyCapture();
                GUIUtility.ExitGUI();
            }
        }

        private void DrawSettingsTab()
        {
            _settingsScroll = GUILayout.BeginScrollView(_settingsScroll,
                GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));

            GUILayout.Space(8f);
            GUILayout.Label(I18n.T("sec.save_now"), BlackWhiteSkin.HeaderStyle);
            GUILayout.BeginVertical(BlackWhiteSkin.CardStyle);
            GUILayout.BeginHorizontal();
            GUILayout.Label(I18n.T("lbl.alias_optional"),
                GUILayout.MinWidth(LabelColW), GUILayout.ExpandWidth(false), GUILayout.MinHeight(RowMinHeight));
            _quickNickname = GUILayout.TextField(_quickNickname ?? "",
                GUILayout.MinWidth(360f), GUILayout.ExpandWidth(true), GUILayout.MinHeight(RowMinHeight));
            GUILayout.Space(12f);
            if (GUILayout.Button(I18n.T("btn.save_now"),
                GUILayout.MinWidth(140f), GUILayout.ExpandWidth(false), GUILayout.MinHeight(RowMinHeight)))
            {
                if (string.IsNullOrEmpty(_quickNickname)) _onSaveNow?.Invoke();
                else _onSaveNowAs?.Invoke(_quickNickname);
                _quickNickname = "";
                InvalidateSlotCache();
                ClearTextFocus();
            }
            GUILayout.Space(12f);
            if (GUILayout.Button(I18n.T("btn.save_exit"),
                GUILayout.MinWidth(180f), GUILayout.ExpandWidth(false), GUILayout.MinHeight(RowMinHeight)))
            {
                _quickNickname = "";
                ClearTextFocus();
                _onSaveAndExit?.Invoke();
                InvalidateSlotCache();
                GUIUtility.ExitGUI();
            }
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();

            GUILayout.Space(12f);
            GUILayout.Label(I18n.T("sec.auto_backup"), BlackWhiteSkin.HeaderStyle);
            GUILayout.BeginVertical(BlackWhiteSkin.CardStyle);

            bool newAuto = DrawSwitch(I18n.T("sw.auto_backup_enabled"), _cfg.AutoBackupEnabled.Value);
            if (newAuto != _cfg.AutoBackupEnabled.Value)
            {
                _cfg.AutoBackupEnabled.Value = newAuto;
                if (newAuto) _onIntervalChanged?.Invoke();
            }

            DrawFloatSlider(I18n.F("fmt.interval_minutes", _cfg.AutoBackupIntervalMinutes.Value),
                _cfg.AutoBackupIntervalMinutes, 0.5f, Mathf.Max(240f, _cfg.AutoBackupIntervalMinutes.Value),
                snap: 0.5f, onChanged: _onIntervalChanged);

            DrawIntSlider(I18n.F("fmt.auto_keep_count", _cfg.AutoBackupKeep.Value),
                _cfg.AutoBackupKeep, 1, Mathf.Max(100, _cfg.AutoBackupKeep.Value));

            bool newGuard = DrawSwitch(I18n.T("sw.backup_before_overwrite"), _cfg.BackupBeforeOverwrite.Value);
            if (newGuard != _cfg.BackupBeforeOverwrite.Value) _cfg.BackupBeforeOverwrite.Value = newGuard;

            GUILayout.EndVertical();

            GUILayout.Space(12f);
            GUILayout.Label(I18n.T("sec.auto_rollback"), BlackWhiteSkin.HeaderStyle);
            GUILayout.BeginVertical(BlackWhiteSkin.CardStyle);

            bool newAutoRollback = DrawSwitch(I18n.T("sw.auto_rollback_enabled"), _cfg.AutoRollbackOnDeath.Value);
            if (newAutoRollback != _cfg.AutoRollbackOnDeath.Value) _cfg.AutoRollbackOnDeath.Value = newAutoRollback;

            DrawFloatSlider(I18n.F("fmt.rollback_delay", _cfg.RollbackDelaySeconds.Value),
                _cfg.RollbackDelaySeconds, 1f, Mathf.Max(10f, _cfg.RollbackDelaySeconds.Value),
                snap: 0.5f, onChanged: null);

            string mpThresholdLabel = _cfg.MultiplayerDeathThreshold.Value <= 0
                ? I18n.T("lbl.mp_death_all")
                : I18n.F("fmt.mp_death_threshold", _cfg.MultiplayerDeathThreshold.Value);
            DrawIntSlider(mpThresholdLabel,
                _cfg.MultiplayerDeathThreshold, 0, Mathf.Max(16, _cfg.MultiplayerDeathThreshold.Value));

            DrawIntSlider(I18n.F("fmt.recent_slots_limit", _cfg.RecentSlotsLimit.Value),
                _cfg.RecentSlotsLimit, 5, Mathf.Max(100, _cfg.RecentSlotsLimit.Value));

            GUILayout.EndVertical();

            GUILayout.Space(12f);
            GUILayout.Label(I18n.T("sec.world"), BlackWhiteSkin.HeaderStyle);
            GUILayout.BeginVertical(BlackWhiteSkin.CardStyle);
            DrawWorldGroup();
            GUILayout.EndVertical();

            GUILayout.Space(12f);
            GUILayout.Label(I18n.T("sec.hotkeys"), BlackWhiteSkin.HeaderStyle);
            GUILayout.BeginVertical(BlackWhiteSkin.CardStyle);
            DrawHotkeyRow(I18n.T("lbl.hotkey_toggle_panel"), _cfg.ToggleSlotsHotkey, ref _capturingSlotsKey,
                () => { _capturingSettingsKey = false; _capturingQuickKey = false; });
            DrawHotkeyRow(I18n.T("lbl.hotkey_quick_save"), _cfg.QuickSaveHotkey, ref _capturingQuickKey,
                () => { _capturingSettingsKey = false; _capturingSlotsKey = false; });
            DrawHotkeyRow(I18n.T("lbl.hotkey_settings_tab"), _cfg.ToggleSettingsHotkey, ref _capturingSettingsKey,
                () => { _capturingSlotsKey = false; _capturingQuickKey = false; });
            CaptureKeyDownIfNeeded();
            GUILayout.EndVertical();

            GUILayout.Space(12f);
            GUILayout.Label(I18n.T("sec.misc"), BlackWhiteSkin.HeaderStyle);
            GUILayout.BeginVertical(BlackWhiteSkin.CardStyle);
            bool newShowLog = DrawSwitch(I18n.T("sw.show_log_in_console"), _cfg.ShowLogInConsole.Value);
            if (newShowLog != _cfg.ShowLogInConsole.Value) _cfg.ShowLogInConsole.Value = newShowLog;

            bool newSuppressSupplies = DrawSwitch(I18n.T("sw.suppress_starting_supplies_on_load"), _cfg.SuppressStartingSuppliesOnLoad.Value);
            if (newSuppressSupplies != _cfg.SuppressStartingSuppliesOnLoad.Value) _cfg.SuppressStartingSuppliesOnLoad.Value = newSuppressSupplies;

            bool newSuppressLifepod = DrawSwitch(I18n.T("sw.suppress_intro_lifepod_on_load"), _cfg.SuppressIntroLifepodOnLoad.Value);
            if (newSuppressLifepod != _cfg.SuppressIntroLifepodOnLoad.Value) _cfg.SuppressIntroLifepodOnLoad.Value = newSuppressLifepod;

            bool newAcceptUpdate = DrawSwitch(I18n.T("sw.accept_update_notice"), _cfg.AcceptUpdateNotice.Value);
            if (newAcceptUpdate != _cfg.AcceptUpdateNotice.Value)
            {
                _cfg.AcceptUpdateNotice.Value = newAcceptUpdate;
                UpdateChecker.Enabled = newAcceptUpdate;
            }
            DrawLanguageModeRow(I18n.T("lbl.language"), _cfg.PreferredLanguage);
            GUILayout.EndVertical();

            GUILayout.Space(20f);
            GUILayout.EndScrollView();
        }

        private void DrawAboutTab()
        {
            _aboutScroll = GUILayout.BeginScrollView(_aboutScroll,
                GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));

            GUILayout.Space(12f);
            GUILayout.Label(I18n.T("about.title"), CenterTitleStyle, GUILayout.ExpandWidth(true));
            GUILayout.Space(6f);
            GUILayout.Label(I18n.T("about.desc"), CenterLabelStyle, GUILayout.ExpandWidth(true));
            GUILayout.Label(I18n.F("about.version", Plugin.PluginVersion), CenterLabelStyle, GUILayout.ExpandWidth(true));

            GUILayout.Space(16f);
            GUILayout.Label(I18n.T("about.sec_links"), CenterTitleStyle, GUILayout.ExpandWidth(true));
            GUILayout.Space(6f);
            DrawLinkButton(I18n.T("about.link_repo"), "https://github.com/HuanXinWork/Cu-SaveManager");
            DrawLinkButton(I18n.T("about.link_release"), "https://github.com/HuanXinWork/Cu-SaveManager/releases/latest");

            GUILayout.Space(16f);
            GUILayout.Label(I18n.T("about.sec_credits"), CenterTitleStyle, GUILayout.ExpandWidth(true));
            GUILayout.Space(6f);
            DrawNameButton("huanxin886", "https://github.com/huanxin886");

            GUILayout.Space(16f);
            GUILayout.Label(I18n.T("about.sec_deps"), CenterTitleStyle, GUILayout.ExpandWidth(true));
            GUILayout.Space(6f);
            DrawLinkButton("Casualties: Together", "https://www.nexusmods.com/scavprototype/mods/67");
            DrawLinkButton("BepInEx", "https://github.com/BepInEx/BepInEx");
            DrawLinkButton("QoL Unknown", "https://github.com/jimmyking9999999/QoL-Unknown");

            GUILayout.Space(20f);
            GUILayout.EndScrollView();
        }

        private static GUIStyle _centerTitle;
        private static GUIStyle CenterTitleStyle =>
            _centerTitle ?? (_centerTitle = new GUIStyle(BlackWhiteSkin.HeaderStyle) { alignment = TextAnchor.MiddleCenter });

        private static GUIStyle _centerLabel;
        private static GUIStyle CenterLabelStyle =>
            _centerLabel ?? (_centerLabel = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, wordWrap = true });

        private static void DrawLinkButton(string label, string url)
        {
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(label, BlackWhiteSkin.TabStyle,
                GUILayout.MinWidth(560f), GUILayout.ExpandWidth(false), GUILayout.MinHeight(48f)))
            {
                try { Application.OpenURL(url); } catch { }
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(6f);
        }

        private static void DrawNameButton(string name, string url)
        {
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(name,
                GUILayout.MinWidth(280f), GUILayout.ExpandWidth(false), GUILayout.MinHeight(40f)))
            {
                try { Application.OpenURL(url); } catch { }
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(4f);
        }

        private void DrawSlotsTab()
        {
            var slots = _cachedSlots ?? _store.ListSlots();
            string currentHash = _cachedSaveHash;
            int currentRunId = _cachedRunId;

            GUILayout.Space(4f);
            GUILayout.BeginHorizontal();
            GUILayout.Label(I18n.F("fmt.total_slots", slots.Count),
                GUILayout.MinWidth(220f), GUILayout.ExpandWidth(false), GUILayout.MinHeight(36f));
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(I18n.T("btn.expand_all"),
                GUILayout.MinWidth(160f), GUILayout.ExpandWidth(false), GUILayout.MinHeight(36f)))
            {
                foreach (var s in slots)
                {
                    _runIdExpanded[s.RunIdFolder] = true;
                    _dateExpanded[RunDateKey(s)] = true;
                }
                GUIUtility.ExitGUI();
            }
            GUILayout.Space(12f);
            if (GUILayout.Button(I18n.T("btn.collapse_all"),
                GUILayout.MinWidth(160f), GUILayout.ExpandWidth(false), GUILayout.MinHeight(36f)))
            {
                foreach (var s in slots)
                {
                    _runIdExpanded[s.RunIdFolder] = false;
                    _dateExpanded[RunDateKey(s)] = false;
                }
                GUIUtility.ExitGUI();
            }
            GUILayout.EndHorizontal();
            GUILayout.Space(8f);

            _scroll = GUILayout.BeginScrollView(_scroll,
                GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));

            DrawCurrentSaveCard(currentHash, currentRunId);

            // ListSlots 按全局时间倒序，必须先按 runId 与日期聚合以避免重复组头
            foreach (var runGroup in GroupByRunId(slots))
            {
                string runIdFolder = runGroup[0].RunIdFolder;
                if (!_runIdExpanded.ContainsKey(runIdFolder)) _runIdExpanded[runIdFolder] = true;
                DrawRunIdHeader(runIdFolder, runGroup.Count);
                if (!_runIdExpanded[runIdFolder]) continue;

                foreach (var dateGroup in GroupByDate(runGroup))
                {
                    string dateKey = RunDateKey(dateGroup[0]);
                    if (!_dateExpanded.ContainsKey(dateKey)) _dateExpanded[dateKey] = true;
                    DrawDateHeader(dateGroup[0].DateFolder, dateKey, dateGroup.Count);
                    if (!_dateExpanded[dateKey]) continue;

                    foreach (var slot in dateGroup)
                    {
                        // 同时校验 runId 与文件哈希，防止跨局同字节存档误判为当前存档
                        bool isCurrent = currentHash != null
                            && currentRunId != 0
                            && slot.Sidecar.RunId == currentRunId
                            && slot.Hash == currentHash;
                        DrawSlotCard(slot, isCurrent);
                    }
                }
            }
            if (slots.Count == 0)
            {
                GUILayout.Space(20f);
                GUILayout.Label(I18n.T("msg.no_slots"));
            }
            GUILayout.EndScrollView();
        }

        private static string RunDateKey(SlotInfo s) => s.RunIdFolder + "/" + s.DateFolder;

        private static List<List<SlotInfo>> GroupByRunId(List<SlotInfo> slots)
        {
            var groups = new List<List<SlotInfo>>();
            var index = new Dictionary<string, List<SlotInfo>>();
            foreach (var s in slots)
            {
                if (!index.TryGetValue(s.RunIdFolder, out var list))
                {
                    list = new List<SlotInfo>();
                    index[s.RunIdFolder] = list;
                    groups.Add(list);
                }
                list.Add(s);
            }
            return groups;
        }

        private static List<List<SlotInfo>> GroupByDate(List<SlotInfo> slots)
        {
            var groups = new List<List<SlotInfo>>();
            var index = new Dictionary<string, List<SlotInfo>>();
            foreach (var s in slots)
            {
                if (!index.TryGetValue(s.DateFolder, out var list))
                {
                    list = new List<SlotInfo>();
                    index[s.DateFolder] = list;
                    groups.Add(list);
                }
                list.Add(s);
            }
            return groups;
        }

        private static void DrawCurrentSaveCard(string currentHash, int runId)
        {
            GUILayout.BeginVertical(BlackWhiteSkin.CardStyle);
            GUILayout.BeginHorizontal();
            GUILayout.Label(I18n.T("lbl.current_save"), BlackWhiteSkin.HeaderStyle,
                GUILayout.MinWidth(220f), GUILayout.ExpandWidth(false), GUILayout.MinHeight(36f));
            GUILayout.Label(SaveStore.CurrentSaveDisplayPath, GUILayout.ExpandWidth(true), GUILayout.MinHeight(36f));
            GUILayout.EndHorizontal();

            string sub;
            if (SaveStore.IsMultiplayerContextActive())
            {
                sub = runId != 0 ? I18n.F("fmt.run_id", runId) : I18n.T("mp.current_save_hint");
            }
            else if (currentHash != null)
            {
                sub = runId == 0 ? I18n.T("msg.run_id_unknown") : I18n.F("fmt.run_id", runId);
            }
            else if (WorldGeneration.world != null)
            {
                sub = runId != 0 ? I18n.F("fmt.run_id_in_memory", runId) : I18n.T("msg.progress_in_memory");
            }
            else if (SaveSystem.loadedRun && File.Exists(SlotSidecar.SidecarPath(SaveStore.GameSavePath)))
            {
                sub = runId != 0 ? I18n.F("fmt.run_id_after_load", runId) : I18n.T("msg.after_load_not_saved");
            }
            else
            {
                sub = I18n.T("msg.no_save_yet");
            }
            GUILayout.Label(sub);
            GUILayout.EndVertical();
            GUILayout.Space(8f);
        }

        private static string FormatSysTime(long unixMs)
        {
            if (unixMs <= 0L) return I18n.T("lbl.time_unknown");
            return DateTimeOffset.FromUnixTimeMilliseconds(unixMs).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
        }

        private void DrawRunIdHeader(string runIdFolder, int count)
        {
            GUILayout.Space(10f);
            string prefix = _runIdExpanded[runIdFolder] ? "▼  " : "▶  ";
            string label = runIdFolder == "0"
                ? prefix + I18n.F("fmt.adventure_unknown", count)
                : prefix + I18n.F("fmt.adventure_named", runIdFolder, count);
            if (GUILayout.Button(label,
                BlackWhiteSkin.HeaderButtonStyle, GUILayout.ExpandWidth(true), GUILayout.MinHeight(56f)))
            {
                _runIdExpanded[runIdFolder] = !_runIdExpanded[runIdFolder];
                GUIUtility.ExitGUI();
            }
            GUILayout.Space(4f);
        }

        private void DrawDateHeader(string dateFolder, string dateKey, int count)
        {
            GUILayout.Space(4f);
            GUILayout.BeginHorizontal();
            GUILayout.Space(28f);
            string prefix = _dateExpanded[dateKey] ? "▼  " : "▶  ";
            if (GUILayout.Button(prefix + I18n.F("fmt.date_count", dateFolder, count),
                BlackWhiteSkin.HeaderButtonStyle, GUILayout.ExpandWidth(true), GUILayout.MinHeight(44f)))
            {
                _dateExpanded[dateKey] = !_dateExpanded[dateKey];
                GUIUtility.ExitGUI();
            }
            GUILayout.EndHorizontal();
            GUILayout.Space(4f);
        }

        private void DrawSlotCard(SlotInfo slot, bool isCurrent)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Space(56f);
            GUILayout.BeginVertical(BlackWhiteSkin.CardStyle);

            GUILayout.BeginHorizontal();
            string tag = slot.Sidecar.Pinned ? I18n.T("lbl.tag_pinned")
                : (slot.Sidecar.IsAuto ? I18n.T("lbl.tag_auto") : I18n.T("lbl.tag_manual"));
            GUILayout.Label(tag,
                GUILayout.MinWidth(100f), GUILayout.ExpandWidth(false), GUILayout.MinHeight(36f));

            string key = slot.FullSlotPath;
            bool editing = _nicknameEditingPath == key;
            if (!_editingNicknames.ContainsKey(key)) _editingNicknames[key] = slot.Sidecar.Nickname;

            if (editing)
            {
                _editingNicknames[key] = GUILayout.TextField(_editingNicknames[key] ?? "",
                    GUILayout.MinWidth(360f), GUILayout.ExpandWidth(true), GUILayout.MinHeight(36f));
                if (GUILayout.Button("✓", BlackWhiteSkin.IconBtnStyle,
                    GUILayout.Width(40f), GUILayout.MinHeight(36f)))
                {
                    slot.Sidecar.Nickname = _editingNicknames[key] ?? "";
                    _store.UpdateSidecar(slot.FullSlotPath, slot.Sidecar);
                    _nicknameEditingPath = null;
                    InvalidateSlotCache();
                    ClearTextFocus();
                    GUIUtility.ExitGUI();
                }
                if (GUILayout.Button("×", BlackWhiteSkin.IconBtnStyle,
                    GUILayout.Width(40f), GUILayout.MinHeight(36f)))
                {
                    _editingNicknames[key] = slot.Sidecar.Nickname;
                    _nicknameEditingPath = null;
                    ClearTextFocus();
                    GUIUtility.ExitGUI();
                }
            }
            else
            {
                GUILayout.Label(I18n.F("fmt.alias",
                        string.IsNullOrEmpty(slot.Sidecar.Nickname) ? I18n.T("lbl.alias_unset") : slot.Sidecar.Nickname),
                    BlackWhiteSkin.HeaderStyle,
                    GUILayout.MinWidth(360f), GUILayout.ExpandWidth(true), GUILayout.MinHeight(36f));
                Rect penBtn = GUILayoutUtility.GetRect(40f, 36f, GUILayout.Width(40f), GUILayout.MinHeight(36f));
                if (GUI.Button(penBtn, GUIContent.none, BlackWhiteSkin.IconBtnStyle))
                {
                    _nicknameEditingPath = key;
                    GUIUtility.ExitGUI();
                }
                BlackWhiteSkin.DrawPencil(new Rect(penBtn.x + 8f, penBtn.y + 8f,
                    penBtn.width - 16f, penBtn.height - 16f), 2f);
            }

            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("  " + slot.File.Name,
                GUILayout.MinWidth(360f), GUILayout.ExpandWidth(true), GUILayout.MinHeight(28f));
            GUILayout.FlexibleSpace();
            if (isCurrent)
            {
                GUILayout.Label(I18n.T("lbl.is_current_save"),
                    GUILayout.MinWidth(140f), GUILayout.ExpandWidth(false), GUILayout.MinHeight(28f));
            }
            string playTime = I18n.F("fmt.last_played", FormatSysTime(slot.Sidecar.LastPlayedAtUnixMs));
            GUILayout.Label(playTime,
                GUILayout.MinWidth(180f), GUILayout.ExpandWidth(false), GUILayout.MinHeight(28f));
            GUILayout.Label(slot.File.LastWriteTime.ToString("HH:mm:ss")
                + "  ·  " + (slot.File.Length / 1024f).ToString("0.0") + " KB",
                GUILayout.MinWidth(220f), GUILayout.ExpandWidth(false), GUILayout.MinHeight(28f));
            GUILayout.EndHorizontal();

            GUILayout.Space(6f);
            GUILayout.BeginHorizontal();
            if (!isCurrent)
            {
                if (GUILayout.Button(I18n.T("btn.load_slot"),
                    GUILayout.MinWidth(180f), GUILayout.ExpandWidth(false), GUILayout.MinHeight(36f)))
                {
                    _onLoadSlot?.Invoke(slot);
                    InvalidateSlotCache();
                    GUIUtility.ExitGUI();
                }
                GUILayout.Space(8f);
            }
            string pinLabel = slot.Sidecar.Pinned ? I18n.T("btn.unpin") : I18n.T("btn.pin");
            if (GUILayout.Button(pinLabel,
                GUILayout.MinWidth(160f), GUILayout.ExpandWidth(false), GUILayout.MinHeight(36f)))
            {
                slot.Sidecar.Pinned = !slot.Sidecar.Pinned;
                _store.UpdateSidecar(slot.FullSlotPath, slot.Sidecar);
                InvalidateSlotCache();
                GUIUtility.ExitGUI();
            }
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(I18n.T("btn.delete"),
                GUILayout.MinWidth(120f), GUILayout.ExpandWidth(false), GUILayout.MinHeight(36f)))
            {
                _onDeleteSlot?.Invoke(slot);
                _editingNicknames.Remove(key);
                if (_nicknameEditingPath == key) _nicknameEditingPath = null;
                InvalidateSlotCache();
                ClearTextFocus();
                GUIUtility.ExitGUI();
            }
            GUILayout.EndHorizontal();

            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
            GUILayout.Space(8f);
        }

        private void SyncWorldPreference()
        {
            WorldEngineArbiter.SyncPreference(_cfg.PreferredEngine.Value, _cfg.SeedInput.Value,
                _cfg.PositionMode.Value, _cfg.FixedX.Value, _cfg.FixedY.Value);
        }

        private void DrawWorldGroup()
        {
            bool qolPresent = QolBridge.IsQolPresent();
            bool krokPresent = MultiplayerBridge.IsMultiplayerEnabled();
            string pref = WorldEngineArbiter.NormalizeEngineName(_cfg.PreferredEngine.Value);
            string engine = pref == "self" ? "self"
                : (pref == "krok" && krokPresent) ? "krok"
                : (pref == "qol" && qolPresent) ? "qol"
                : (krokPresent ? "krok" : "self");

            GUILayout.BeginHorizontal();
            GUILayout.Label(I18n.T("lbl.world_engine"),
                GUILayout.MinWidth(LabelColW), GUILayout.ExpandWidth(false), GUILayout.MinHeight(RowMinHeight));
            GUILayout.Space(12f);

            if (qolPresent)
            {
                if (GUILayout.Button(I18n.T("opt.engine_qol"),
                    engine == "qol" ? BlackWhiteSkin.TabActiveStyle : BlackWhiteSkin.TabStyle,
                    GUILayout.MinWidth(160f), GUILayout.ExpandWidth(false), GUILayout.MinHeight(RowMinHeight)))
                {
                    _cfg.PreferredEngine.Value = "qol";
                    SyncWorldPreference();
                    ClearTextFocus();
                    GUIUtility.ExitGUI();
                }
                GUILayout.Space(8f);
            }

            if (krokPresent)
            {
                if (GUILayout.Button(I18n.T("opt.engine_krok"),
                    engine == "krok" ? BlackWhiteSkin.TabActiveStyle : BlackWhiteSkin.TabStyle,
                    GUILayout.MinWidth(160f), GUILayout.ExpandWidth(false), GUILayout.MinHeight(RowMinHeight)))
                {
                    _cfg.PreferredEngine.Value = "krok";
                    SyncWorldPreference();
                    ClearTextFocus();
                    GUIUtility.ExitGUI();
                }
                GUILayout.Space(8f);
            }

            if (GUILayout.Button(I18n.T("opt.engine_self"),
                engine == "self" ? BlackWhiteSkin.TabActiveStyle : BlackWhiteSkin.TabStyle,
                GUILayout.MinWidth(160f), GUILayout.ExpandWidth(false), GUILayout.MinHeight(RowMinHeight)))
            {
                _cfg.PreferredEngine.Value = "self";
                SyncWorldPreference();
                ClearTextFocus();
                GUIUtility.ExitGUI();
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(6f);

            if (engine == "self")
            {
                _seedInputCache = _seedInputCache ?? _cfg.SeedInput.Value;
                GUILayout.BeginHorizontal();
                GUILayout.Label(I18n.T("lbl.seed_input"),
                    GUILayout.MinWidth(LabelColW), GUILayout.ExpandWidth(false), GUILayout.MinHeight(RowMinHeight));
                string newSeed = GUILayout.TextField(_seedInputCache ?? "",
                    GUILayout.MinWidth(280f), GUILayout.ExpandWidth(true), GUILayout.MinHeight(RowMinHeight));
                if (newSeed != _seedInputCache)
                {
                    _seedInputCache = newSeed;
                    _cfg.SeedInput.Value = newSeed;
                }
                GUILayout.EndHorizontal();
                GUILayout.Space(6f);
            }

            if (!qolPresent && !krokPresent)
            {
                GUILayout.Label(I18n.T("hint.qol_absent"));
            }
            else if (engine == "krok")
            {
                GUILayout.Label(I18n.T("hint.mp_krok_engine"));
            }

            bool posFixed = string.Equals(_cfg.PositionMode.Value, "fixedPos", StringComparison.OrdinalIgnoreCase);
            GUILayout.BeginHorizontal();
            GUILayout.Label(I18n.T("lbl.pos_mode"),
                GUILayout.MinWidth(LabelColW), GUILayout.ExpandWidth(false), GUILayout.MinHeight(RowMinHeight));
            GUILayout.Space(20f);
            if (GUILayout.Button(I18n.T("opt.pos_last"), posFixed ? BlackWhiteSkin.TabStyle : BlackWhiteSkin.TabActiveStyle,
                GUILayout.MinWidth(180f), GUILayout.ExpandWidth(false), GUILayout.MinHeight(RowMinHeight)) && posFixed)
            {
                _cfg.PositionMode.Value = "lastPos";
                SyncWorldPreference();
                ClearTextFocus();
                GUIUtility.ExitGUI();
            }
            GUILayout.Space(8f);
            if (GUILayout.Button(I18n.T("opt.pos_fixed"), posFixed ? BlackWhiteSkin.TabActiveStyle : BlackWhiteSkin.TabStyle,
                GUILayout.MinWidth(180f), GUILayout.ExpandWidth(false), GUILayout.MinHeight(RowMinHeight)) && !posFixed)
            {
                _cfg.PositionMode.Value = "fixedPos";
                SyncWorldPreference();
                ClearTextFocus();
                GUIUtility.ExitGUI();
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(6f);

            if (posFixed)
            {
                _fixedXCache = _fixedXCache ?? _cfg.FixedX.Value.ToString(CultureInfo.InvariantCulture);
                _fixedYCache = _fixedYCache ?? _cfg.FixedY.Value.ToString(CultureInfo.InvariantCulture);
                DrawFloatField(I18n.T("lbl.fixed_x"), ref _fixedXCache, _cfg.FixedX);
                DrawFloatField(I18n.T("lbl.fixed_y"), ref _fixedYCache, _cfg.FixedY);
            }

            SyncWorldPreference();
        }

        private static void DrawFloatField(string label, ref string cache, ConfigEntry<float> entry)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.MinWidth(LabelColW), GUILayout.ExpandWidth(false), GUILayout.MinHeight(RowMinHeight));
            string next = GUILayout.TextField(cache ?? "",
                GUILayout.MinWidth(200f), GUILayout.ExpandWidth(true), GUILayout.MinHeight(RowMinHeight));
            if (next != cache)
            {
                cache = next;
                if (float.TryParse(next, NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
                {
                    entry.Value = v;
                }
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(6f);
        }

        private static bool DrawSwitch(string label, bool value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.MinWidth(LabelColW), GUILayout.ExpandWidth(false),
                GUILayout.MinHeight(RowMinHeight));
            GUILayout.Space(20f);

            bool result = value;
            var onStyle = value ? BlackWhiteSkin.TabActiveStyle : BlackWhiteSkin.TabStyle;
            var offStyle = value ? BlackWhiteSkin.TabStyle : BlackWhiteSkin.TabActiveStyle;

            if (GUILayout.Button(I18n.T("sw.on"), onStyle,
                GUILayout.MinWidth(120f), GUILayout.ExpandWidth(false), GUILayout.MinHeight(RowMinHeight)))
            {
                result = true;
            }
            GUILayout.Space(8f);
            if (GUILayout.Button(I18n.T("sw.off"), offStyle,
                GUILayout.MinWidth(120f), GUILayout.ExpandWidth(false), GUILayout.MinHeight(RowMinHeight)))
            {
                result = false;
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(6f);
            return result;
        }

        private static List<KeyValuePair<string, string>> BuildLanguageOptions()
        {
            var list = new List<KeyValuePair<string, string>>();
            list.Add(new KeyValuePair<string, string>("auto", I18n.T("opt.language_auto")));
            foreach (string code in I18n.GetAvailableLanguageCodes())
            {
                list.Add(new KeyValuePair<string, string>(code, I18n.GetLanguageDisplayName(code)));
            }
            return list;
        }

        private static void DrawLanguageModeRow(string label, ConfigEntry<string> entry)
        {
            string mode = I18n.NormalizeLanguageMode(entry.Value);
            List<KeyValuePair<string, string>> options = BuildLanguageOptions();
            const int perRow = 4;

            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.MinWidth(LabelColW), GUILayout.ExpandWidth(false),
                GUILayout.MinHeight(RowMinHeight));
            GUILayout.Space(20f);
            GUILayout.BeginVertical();
            for (int i = 0; i < options.Count; i += perRow)
            {
                GUILayout.BeginHorizontal();
                int end = Math.Min(i + perRow, options.Count);
                for (int j = i; j < end; j++)
                {
                    KeyValuePair<string, string> opt = options[j];
                    if (j > i) GUILayout.Space(8f);

                    bool active = string.Equals(mode, opt.Key, StringComparison.OrdinalIgnoreCase);
                    if (GUILayout.Button(opt.Value,
                        active ? BlackWhiteSkin.TabActiveStyle : BlackWhiteSkin.TabStyle,
                        GUILayout.MinWidth(120f), GUILayout.ExpandWidth(false), GUILayout.MinHeight(RowMinHeight))
                        && !active)
                    {
                        entry.Value = opt.Key;
                        MenuButtonUiInjector.RefreshButtonText();
                        GUIUtility.ExitGUI();
                    }
                }
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                GUILayout.Space(4f);
            }
            GUILayout.EndVertical();

            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(6f);
        }


        private static void DrawFloatSlider(string label, ConfigEntry<float> entry, float min, float max,
            float snap, Action onChanged)
        {
            GUILayout.Label(label, GUILayout.MinHeight(36f));
            float next = GUILayout.HorizontalSlider(entry.Value, min, max,
                GUILayout.Height(44f), GUILayout.ExpandWidth(true));
            if (snap > 0f) next = Mathf.Round(next / snap) * snap;
            if (Mathf.Abs(next - entry.Value) > 0.001f)
            {
                entry.Value = Mathf.Clamp(next, min, max);
                onChanged?.Invoke();
            }
            GUILayout.Space(12f);
        }

        private static void DrawIntSlider(string label, ConfigEntry<int> entry, int min, int max)
        {
            GUILayout.Label(label, GUILayout.MinHeight(36f));
            int next = Mathf.RoundToInt(GUILayout.HorizontalSlider(entry.Value, min, max,
                GUILayout.Height(44f), GUILayout.ExpandWidth(true)));
            if (next != entry.Value) entry.Value = Mathf.Clamp(next, min, max);
            GUILayout.Space(12f);
        }

        private void DrawHotkeyRow(string label, ConfigEntry<KeyboardShortcut> entry,
            ref bool capturing, Action onStart)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.MinWidth(LabelColW), GUILayout.ExpandWidth(false),
                GUILayout.MinHeight(RowMinHeight));
            string text = capturing
                ? I18n.T("btn.press_a_key")
                : (HotkeyConfig.IsBound(entry.Value) ? entry.Value.ToString() : I18n.T("btn.unbound"));
            if (GUILayout.Button(text,
                GUILayout.MinWidth(280f), GUILayout.ExpandWidth(false), GUILayout.MinHeight(RowMinHeight)))
            {
                capturing = true;
                onStart();
            }
            GUILayout.Space(12f);
            if (GUILayout.Button(I18n.T("btn.clear"),
                GUILayout.MinWidth(96f), GUILayout.ExpandWidth(false), GUILayout.MinHeight(RowMinHeight)))
            {
                entry.Value = new KeyboardShortcut(KeyCode.None);
                capturing = false;
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(6f);
        }

        private void CaptureKeyDownIfNeeded()
        {
            if (!IsCapturingKey) return;
            var ev = Event.current;
            if (ev.type != EventType.KeyDown || ev.keyCode == KeyCode.None) return;
            if (ev.keyCode == KeyCode.Escape)
            {
                CancelKeyCapture();
                ev.Use();
                return;
            }
            var sc = new KeyboardShortcut(ev.keyCode);
            if (_capturingSettingsKey) _cfg.ToggleSettingsHotkey.Value = sc;
            else if (_capturingSlotsKey) _cfg.ToggleSlotsHotkey.Value = sc;
            else if (_capturingQuickKey) _cfg.QuickSaveHotkey.Value = sc;
            CancelKeyCapture();
            ev.Use();
        }
    }
}
