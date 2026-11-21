using System;
using System.Collections.Generic;
using UnityEngine;
using BepInEx.Configuration;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>存档管理主窗口：负责纯黑白线条模态外框、顶底状态栏、侧栏导航、分页分发与关于页绘制。</summary>
    internal sealed partial class SaveManagerWindow
    {
        private const int WindowId = 0x5A4D5310;
        private const float BaseWinW = 1440f, BaseWinH = 900f;
        private const float MinWinW = 1260f, MinWinH = 740f;
        private const float WindowBorderThickness = 6f;
        private const float TitleBarHeight = 68f, HeaderLineThickness = 4f, CloseBtnSize = 50f;
        private const float ScaleBtnHeight = 40f, ScaleBtnGap = 6f;
        private const float SidebarWidth = 230f, SidebarPadLeft = 14f, SidebarPadRight = 12f, SidebarLineWidth = 3f;
        private const float TopTabBtnHeight = 48f, TopTabBarHeight = 58f, TopTabLineThickness = 2f;
        private const float FooterLineThickness = 4f, FooterHeight = 58f;
        private const float DefaultLabelColW = 260f, RowMinHeight = 44f;
        private const float ConfirmWindowSeconds = 4f, TransientTipSeconds = 5f;

        private static readonly int[] ScalePresets = { 90, 100, 115, 130 };
        private static readonly string[] ScalePresetLabels = { "90%", "100%", "115%", "130%" };
        private static readonly GUIContent _statusMeasureContent = new GUIContent();

        private readonly HotkeyConfig _cfg;
        private readonly ConfigFile _cfgFile;
        private readonly SaveStore _store;
        private readonly Action _onSaveNow, _onSaveAndExit, _onIntervalChanged, _onOpened, _onClosed;
        private readonly Action<string> _onSaveNowAs;
        private readonly Action<SlotInfo> _onLoadSlot, _onDeleteSlot;
        private readonly Func<string> _getStatus;
        private readonly RollbackTabView _rollbackTab;

        private Rect _rect = new Rect(120f, 60f, BaseWinW, BaseWinH);
        private float _winW = BaseWinW, _winH = BaseWinH;
        private float _drawScale = 1f;
        private bool _posInitialized;
        private float _lastDrawScale = 1f, _lastWinW = BaseWinW, _lastWinH = BaseWinH;
        private float _currentMainWidth = 1142f;
        private int _tab, _sideIdx;
        private Vector2 _sidebarScroll, _aboutScroll;

        private string _transientTip;
        private float _transientTipExpireAt;
        private bool _loggedDrawForCurrentOpen;

        private SaveStore ActiveStore => _store ?? Plugin.SharedStore;
        private bool IsCapturingHotkey => _capturingSettingsKey || _capturingSlotsKey || _capturingQuickKey;

        internal bool Open { get; private set; }
        // 槽位改名期间同步向 Plugin.LateUpdate 报告捕获态，使按下 ESC 时仅退出改名而不误关整个主面板。
        internal bool IsCapturingKey => IsCapturingHotkey || !string.IsNullOrEmpty(_nicknameEditingPath);
        internal bool ExpectsTextInput =>
            Open && (IsCapturingKey || GUIUtility.keyboardControl != 0);

        internal SaveManagerWindow(HotkeyConfig cfg, SaveStore store,
            Action onSaveNow, Action<string> onSaveNowAs, Action onSaveAndExit,
            Action<SlotInfo> onLoadSlot, Action<SlotInfo> onDeleteSlot,
            Action onIntervalChanged, Func<string> getStatus, Action onOpened, Action onClosed,
            RollbackController rollback)
        {
            _cfg = cfg;
            _cfgFile = cfg?.ToggleSlotsHotkey?.ConfigFile;
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

        /// <summary>打开主面板、暂挂配置即时落盘并触发外部打开钩子。</summary>
        internal void OpenPanel()
        {
            if (Open) return;
            Open = true;
            _loggedDrawForCurrentOpen = false;
            ResetSettingsFieldCaches();
            ResetTransientEditState();
            _transientTip = null;
            InvalidateSlotCache();

            // 面板打开期间关闭 SaveOnConfigSet，防止拖动滑块或键入文本时每像素同步写磁盘造成卡顿。
            if (_cfgFile != null)
            {
                try { _cfgFile.SaveOnConfigSet = false; }
                catch (Exception ex) { ModLog.Warning($"暂停配置自动写盘失败：{ex.Message}"); }
            }
            ModLog.Debug("SaveManagerWindow.OpenPanel -> Open=true");
            _onOpened?.Invoke();
        }

        internal void OpenSettingsTab()
        {
            _sideIdx = 0;
            _tab = 0;
            OpenPanel();
            CancelKeyCapture();
        }

        /// <summary>关闭主面板、释放焦点并在 finally 中恢复配置自动写盘与持久化。</summary>
        internal void ClosePanel()
        {
            if (!Open) return;
            Open = false;
            _loggedDrawForCurrentOpen = false;
            try
            {
                CancelKeyCapture();
                ResetTransientEditState();
                _transientTip = null;
                InvalidateSlotCache();
                ImGuiImeRecovery.RequestClear();
                _onClosed?.Invoke();
            }
            finally
            {
                if (_cfgFile != null)
                {
                    try { _cfgFile.SaveOnConfigSet = true; _cfgFile.Save(); }
                    catch (Exception ex) { ModLog.Warning($"保存配置文件失败：{ex.Message}"); }
                }
            }
        }

        internal void Draw()
        {
            if (!Open) return;
            RefreshSlotCacheIfNeeded();

            ComputeWindowTransform();
            if (!_loggedDrawForCurrentOpen)
            {
                _loggedDrawForCurrentOpen = true;
                ModLog.Debug($"SaveManagerWindow.Draw screen=({Screen.width}x{Screen.height}) scale={_drawScale:F3} rect=({_rect.x:0},{_rect.y:0},{_rect.width:0},{_rect.height:0})");
            }

            BlackWhiteSkin.Push();
            Matrix4x4 prev = GUI.matrix;
            try
            {
                GUI.matrix = Matrix4x4.Scale(new Vector3(_drawScale, _drawScale, 1f));
                _rect = GUI.ModalWindow(WindowId, _rect, DrawContent, "");
            }
            catch (ExitGUIException) { throw; }
            catch (Exception ex) { ModLog.Warning($"SaveManagerWindow.Draw 失败：{ex}"); ClosePanel(); }
            finally { GUI.matrix = prev; BlackWhiteSkin.Pop(); }
        }

        private void ComputeWindowTransform()
        {
            int effectiveScalePercent = ExternalTabRegistry.UiScalePercent;
            float dpiBase = Mathf.Min(Screen.width / 1706.67f, Screen.height / 1066.67f);
            float desired = dpiBase * (effectiveScalePercent / 100f);
            float maxScaleW = Mathf.Max(0.4f, (Screen.width - 24f) / MinWinW);
            float maxScaleH = Mathf.Max(0.4f, (Screen.height - 24f) / MinWinH);
            float drawScale = Mathf.Clamp(desired, 0.4f, Mathf.Min(maxScaleW, maxScaleH));
            float winW = Mathf.Clamp(Mathf.Floor((Screen.width - 24f) / drawScale), MinWinW, BaseWinW);
            float winH = Mathf.Clamp(Mathf.Floor((Screen.height - 24f) / drawScale), MinWinH, BaseWinH);

            if (!_posInitialized)
            {
                _rect = new Rect(
                    (Screen.width / drawScale - winW) * 0.5f,
                    (Screen.height / drawScale - winH) * 0.5f,
                    winW,
                    winH);
                _posInitialized = true;
            }
            else if (Mathf.Abs(drawScale - _lastDrawScale) > 0.0001f
                || Mathf.Abs(winW - _lastWinW) > 0.5f
                || Mathf.Abs(winH - _lastWinH) > 0.5f)
            {
                float oldScreenCx = (_rect.x + _lastWinW * 0.5f) * _lastDrawScale;
                float oldScreenCy = (_rect.y + _lastWinH * 0.5f) * _lastDrawScale;
                _rect.x = oldScreenCx / drawScale - winW * 0.5f;
                _rect.y = oldScreenCy / drawScale - winH * 0.5f;
            }

            _drawScale = drawScale;
            _winW = winW;
            _winH = winH;
            _lastDrawScale = drawScale;
            _lastWinW = winW;
            _lastWinH = winH;

            _rect.width = winW;
            _rect.height = winH;
            _rect.x = Mathf.Clamp(_rect.x, 0f, Mathf.Max(0f, Screen.width / drawScale - winW));
            _rect.y = Mathf.Clamp(_rect.y, 0f, Mathf.Max(0f, Screen.height / drawScale - winH));
        }

        private void ShowTransientTip(string message)
        {
            _transientTip = message;
            _transientTipExpireAt = Time.unscaledTime + TransientTipSeconds;
            ModLog.Info(message);
        }

        private void DrawContent(int id)
        {
            // GUI.ModalWindow 原生回调会吞掉普通异常而不传出外层 Draw，必须在回调内部捕获并关窗解锁。
            try
            {
                CaptureKeyDownIfNeeded();

                var ev = Event.current;
                if (ev != null && ev.type == EventType.Layout)
                {
                    float now = Time.unscaledTime;
                    ExpireSlotConfirmationsIfNeeded(now);
                    if (_transientTip != null && now >= _transientTipExpireAt) _transientTip = null;
                }

                string appTitle = I18n.T("app.name");
                float titleW = BlackWhiteSkin.CalcWidth(BlackWhiteSkin.HeaderStyle, appTitle, 120f, 6f);
                BlackWhiteSkin.DrawShadowedLabel(new Rect(24f, 8f, titleW, 52f), appTitle, BlackWhiteSkin.HeaderStyle);

                string verBadge = "v" + Plugin.PluginVersion;
                float verW = BlackWhiteSkin.CalcWidth(BlackWhiteSkin.BadgeStyle, verBadge, 68f, 6f);
                GUI.Label(new Rect(24f + titleW + 12f, 16f, verW, 36f), verBadge, BlackWhiteSkin.BadgeStyle);

                var closeRect = new Rect(_winW - CloseBtnSize - 12f, (TitleBarHeight - CloseBtnSize) * 0.5f, CloseBtnSize, CloseBtnSize);
                if (GUI.Button(closeRect, GUIContent.none))
                {
                    ClearTextFocus();
                    ClosePanel();
                    GUIUtility.ExitGUI();
                }
                BlackWhiteSkin.DrawBorder(closeRect, 4f);
                BlackWhiteSkin.DrawCloseX(new Rect(closeRect.x + 13f, closeRect.y + 13f, closeRect.width - 26f, closeRect.height - 26f), 5f);

                float scaleGroupLeftX = DrawTopScaleControls(closeRect.x - 14f);
                BlackWhiteSkin.DrawHLine(new Rect(0f, TitleBarHeight, _winW, HeaderLineThickness));

                float bodyTop = TitleBarHeight + HeaderLineThickness;
                float footerY = _winH - FooterHeight;
                float bodyH = footerY - bodyTop;

                // 基准 1440 宽下保持 230 侧栏与 1142 主内容宽；130% 等高缩放收缩窗口时弹性压缩侧栏与边距，最大化外部页签净宽。
                bool compactFrame = _winW < BaseWinW - 0.5f;
                float sidebarW = compactFrame
                    ? Mathf.Max(184f, SidebarWidth - Mathf.Floor((BaseWinW - _winW) * 0.25f))
                    : SidebarWidth;
                float padL = compactFrame ? 10f : SidebarPadLeft;
                float padR = compactFrame ? 10f : SidebarPadRight;
                float mainGapL = compactFrame ? 14f : 19f;
                float mainPadR = compactFrame ? 14f : 20f;
                float sidebarFrameW = padL + sidebarW + padR;

                DrawSidebar(new Rect(padL, bodyTop + 10f, sidebarW, bodyH - 20f));
                BlackWhiteSkin.DrawHLine(new Rect(sidebarFrameW, bodyTop, SidebarLineWidth, bodyH));

                float mainX = sidebarFrameW + SidebarLineWidth + mainGapL;
                float mainW = _winW - mainX - mainPadR;
                _currentMainWidth = mainW;
                var mainRect = new Rect(mainX, bodyTop + 12f, mainW, bodyH - 22f);
                if (_sideIdx == 0) DrawSelfTabs(mainRect);
                else DrawExternal(mainRect, _sideIdx - 1);

                BlackWhiteSkin.DrawHLine(new Rect(0f, footerY, _winW, FooterLineThickness));
                DrawFooterBar(footerY + FooterLineThickness, FooterHeight - FooterLineThickness);

                BlackWhiteSkin.DrawBorder(new Rect(0f, 0f, _winW, _winH), WindowBorderThickness);
                GUI.DragWindow(new Rect(0f, 0f, Mathf.Max(120f, scaleGroupLeftX - 8f), TitleBarHeight));
            }
            catch (ExitGUIException) { throw; }
            catch (Exception ex) { ModLog.Warning($"SaveManagerWindow.DrawContent 失败：{ex}"); ClosePanel(); }
        }

        private static float DrawTopScaleControls(float rightEdge)
        {
            int currentPct = ExternalTabRegistry.UiScalePercent;
            string scaleLabel = I18n.T("lbl.ui_scale");
            float labelW = BlackWhiteSkin.CalcWidth(BlackWhiteSkin.MutedLabelStyle, scaleLabel, 48f, 6f);

            float y = (TitleBarHeight - ScaleBtnHeight) * 0.5f;
            float w0 = CalcScalePresetWidth(0, currentPct);
            float w1 = CalcScalePresetWidth(1, currentPct);
            float w2 = CalcScalePresetWidth(2, currentPct);
            float w3 = CalcScalePresetWidth(3, currentPct);
            float totalButtonsW = w0 + w1 + w2 + w3 + ScaleBtnGap * 3f;

            float groupLeftX = rightEdge - totalButtonsW - 8f - labelW;
            GUI.Label(new Rect(groupLeftX, y, labelW, ScaleBtnHeight), scaleLabel, BlackWhiteSkin.MutedLabelStyle);

            float x = groupLeftX + labelW + 8f;
            DrawSingleScalePresetButton(ref x, y, w0, 0, currentPct);
            DrawSingleScalePresetButton(ref x, y, w1, 1, currentPct);
            DrawSingleScalePresetButton(ref x, y, w2, 2, currentPct);
            DrawSingleScalePresetButton(ref x, y, w3, 3, currentPct);
            return groupLeftX;
        }

        private static float CalcScalePresetWidth(int index, int currentPct)
        {
            bool active = Mathf.Abs(currentPct - ScalePresets[index]) <= 2;
            GUIStyle style = active ? BlackWhiteSkin.ScaleBtnActiveStyle : BlackWhiteSkin.ScaleBtnStyle;
            return BlackWhiteSkin.CalcWidth(style, ScalePresetLabels[index], 64f, 8f);
        }

        private static void DrawSingleScalePresetButton(ref float x, float y, float w, int index, int currentPct)
        {
            int preset = ScalePresets[index];
            bool active = Mathf.Abs(currentPct - preset) <= 2;
            GUIStyle style = active ? BlackWhiteSkin.ScaleBtnActiveStyle : BlackWhiteSkin.ScaleBtnStyle;
            if (GUI.Button(new Rect(x, y, w, ScaleBtnHeight), ScalePresetLabels[index], style))
            {
                ClearTextFocus();
                ExternalTabRegistry.UiScalePercent = preset;
                GUIUtility.ExitGUI();
            }
            x += w + ScaleBtnGap;
        }

        private void DrawFooterBar(float contentY, float contentH)
        {
            const float labelH = 38f;
            float y = contentY + Mathf.Floor((contentH - labelH) * 0.5f);

            string shortcuts = I18n.T("hint.footer_shortcuts");
            float rightW = BlackWhiteSkin.CalcWidth(BlackWhiteSkin.MutedRightLabelStyle, shortcuts, 220f, 8f);
            float rightX = _winW - 24f - rightW;
            float leftMaxW = Mathf.Max(260f, rightX - 24f - 16f);

            bool hasTip = !string.IsNullOrEmpty(_transientTip) && Time.unscaledTime < _transientTipExpireAt;
            GUIStyle statusStyle = hasTip ? BlackWhiteSkin.SingleLineLabelStyle : BlackWhiteSkin.MutedLabelStyle;
            string rawStatus = hasTip ? ("●  " + _transientTip) : ResolveStatus();
            string displayStatus = FormatFooterStatus(rawStatus, statusStyle, leftMaxW);

            GUI.Label(new Rect(24f, y, leftMaxW, labelH), displayStatus, statusStyle);
            GUI.Label(new Rect(rightX, y, rightW, labelH), shortcuts, BlackWhiteSkin.MutedRightLabelStyle);
        }

        /// <summary>压缩长路径（保留盘符与末两级）并在超宽时做单行中间省略，防止底栏状态折行或硬裁切。</summary>
        internal static string FormatFooterStatus(string raw, GUIStyle style, float maxWidth)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            string singleLine = raw.Replace("\r", " ").Replace("\n", " ");
            string compacted = CompactPathsInText(singleLine);
            if (style == null || maxWidth <= 40f) return compacted;

            _statusMeasureContent.text = compacted;
            if (style.CalcSize(_statusMeasureContent).x <= maxWidth - 4f) return compacted;

            const string ellipsis = "...";
            int len = compacted.Length;
            int lo = 4, hi = len, best = 4;
            while (lo <= hi)
            {
                int keep = (lo + hi) / 2;
                int headLen = (keep + 1) / 2;
                int tailLen = keep - headLen;
                string candidate = compacted.Substring(0, headLen) + ellipsis + compacted.Substring(len - tailLen, tailLen);
                _statusMeasureContent.text = candidate;
                if (style.CalcSize(_statusMeasureContent).x <= maxWidth - 4f)
                {
                    best = keep;
                    lo = keep + 1;
                }
                else
                {
                    hi = keep - 1;
                }
            }

            if (best >= len) return compacted;
            int h = (best + 1) / 2;
            int t = best - h;
            return compacted.Substring(0, h) + ellipsis + compacted.Substring(len - t, t);
        }

        /// <summary>定位文本中的文件系统路径并折叠中间目录层级，保留盘符（或首段）与末两级。</summary>
        internal static string CompactPathsInText(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;

            int driveColon = -1;
            for (int i = 1; i < text.Length - 2; i++)
            {
                char prev = text[i - 1];
                bool isAsciiLetter = (prev >= 'A' && prev <= 'Z') || (prev >= 'a' && prev <= 'z');
                if (text[i] == ':' && isAsciiLetter && (text[i + 1] == '\\' || text[i + 1] == '/'))
                {
                    driveColon = i - 1;
                    break;
                }
            }

            if (driveColon < 0)
            {
                return CompactSinglePathKeepDriveAndTail2(text);
            }

            int end = text.Length;
            for (int i = driveColon + 3; i < text.Length; i++)
            {
                char c = text[i];
                if (c == ')' || c == '）' || c == '"' || c == '\'')
                {
                    end = i;
                    break;
                }
            }

            string prefix = text.Substring(0, driveColon);
            string pathPart = text.Substring(driveColon, end - driveColon);
            string suffix = text.Substring(end);
            return prefix + CompactSinglePathKeepDriveAndTail2(pathPart) + suffix;
        }

        internal static string CompactSinglePathKeepDriveAndTail2(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            int firstSlash = path.IndexOfAny(new[] { '\\', '/' });
            if (firstSlash < 0) return path;

            char sep = path.IndexOf('\\') >= 0 ? '\\' : '/';
            string[] rawParts = path.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (rawParts.Length <= 3) return path;

            string root = rawParts[0];
            if (path[0] == '/' && (root.Length < 2 || root[1] != ':'))
            {
                root = "/" + root;
            }
            string tail2 = rawParts[rawParts.Length - 2] + sep + rawParts[rawParts.Length - 1];
            return root + sep + "..." + sep + tail2;
        }

        private string ResolveStatus()
        {
            if (_sideIdx == 0)
            {
                try { return _getStatus?.Invoke() ?? ""; }
                catch (Exception ex) { ModLog.Warning($"读取状态栏失败：{ex.Message}"); return ""; }
            }

            var ext = ExternalTabRegistry.Entries;
            int idx = _sideIdx - 1;
            if (idx < 0 || idx >= ext.Count) return "";
            try { return ext[idx].Status ?? ""; }
            catch (Exception ex) { ModLog.Warning($"读取外部页签状态失败：{ex.Message}"); return ""; }
        }

        private static string SafeGetExternalTitle(ExternalTabRegistry.Entry entry)
        {
            if (entry == null) return "";
            try { return entry.Title ?? ""; }
            catch (Exception ex) { ModLog.Warning($"读取外部页签标题失败：{ex.Message}"); return "[Tab Error]"; }
        }

        private void DrawSidebar(Rect area)
        {
            const float BtnH = 50f, Gap = 8f, TopPad = 4f;
            var ext = ExternalTabRegistry.Entries;
            int total = 1 + ext.Count;
            float contentH = TopPad + total * BtnH + Mathf.Max(0, total - 1) * Gap + TopPad;
            bool needScroll = contentH > area.height;
            float btnW = needScroll ? area.width - 18f : area.width;

            if (needScroll) _sidebarScroll = GUI.BeginScrollView(area, _sidebarScroll, new Rect(0f, 0f, btnW, contentH));
            try
            {
                float baseX = needScroll ? 0f : area.x;
                float baseY = needScroll ? TopPad : area.y + TopPad;
                for (int i = 0; i < total; i++)
                {
                    var rect = new Rect(baseX, baseY + i * (BtnH + Gap), btnW, BtnH);
                    string label = i == 0 ? I18n.T("app.name") : SafeGetExternalTitle(ext[i - 1]);
                    bool active = i == _sideIdx;
                    var style = active ? BlackWhiteSkin.SidebarTabActiveStyle : BlackWhiteSkin.SidebarTabStyle;
                    if (GUI.Button(rect, label, style) && !active)
                    {
                        _sideIdx = i;
                        ResetTransientEditState();
                        InvalidateSlotCache();
                        ClearTextFocus();
                        CancelKeyCapture();
                        GUIUtility.ExitGUI();
                    }
                }
            }
            finally
            {
                if (needScroll) GUI.EndScrollView();
            }
        }

        private void DrawSelfTabs(Rect mainRect)
        {
            const float Gap = 8f;
            float tabW = (mainRect.width - Gap * 3f) / 4f;
            DrawTabButton(new Rect(mainRect.x, mainRect.y, tabW, TopTabBtnHeight), I18n.T("tab.settings"), 0);
            DrawTabButton(new Rect(mainRect.x + (tabW + Gap), mainRect.y, tabW, TopTabBtnHeight), I18n.T("tab.slots"), 1);
            DrawTabButton(new Rect(mainRect.x + (tabW + Gap) * 2f, mainRect.y, tabW, TopTabBtnHeight), I18n.T("tab.rollback"), 2);
            DrawTabButton(new Rect(mainRect.x + (tabW + Gap) * 3f, mainRect.y, tabW, TopTabBtnHeight), I18n.T("tab.about"), 3);

            BlackWhiteSkin.DrawHLine(new Rect(mainRect.x, mainRect.y + TopTabBarHeight - TopTabLineThickness, mainRect.width, TopTabLineThickness));
            float contentTop = mainRect.y + TopTabBarHeight + 10f;
            GUILayout.BeginArea(new Rect(mainRect.x, contentTop, mainRect.width, mainRect.yMax - contentTop));
            try
            {
                if (_tab == 0) DrawSettingsTab();
                else if (_tab == 1) DrawSlotsTab();
                else if (_tab == 2) _rollbackTab.Draw();
                else if (_tab == 3) DrawAboutTab();
            }
            finally
            {
                GUILayout.EndArea();
            }
        }

        private void DrawExternal(Rect mainRect, int extIdx)
        {
            var ext = ExternalTabRegistry.Entries;
            if (extIdx < 0 || extIdx >= ext.Count) { _sideIdx = 0; GUIUtility.ExitGUI(); return; }
            GUILayout.BeginArea(mainRect);
            try
            {
                ext[extIdx].Draw?.Invoke();
            }
            catch (ExitGUIException) { throw; }
            catch (Exception ex) { GUILayout.Label("external tab error: " + ex.Message, BlackWhiteSkin.BodyLabelStyle); }
            finally
            {
                GUILayout.EndArea();
            }
        }

        private void DrawTabButton(Rect rect, string label, int idx)
        {
            var style = idx == _tab ? BlackWhiteSkin.TabActiveStyle : BlackWhiteSkin.TabStyle;
            if (GUI.Button(rect, label, style))
            {
                ClearTextFocus();
                if (_tab != idx)
                {
                    _tab = idx;
                    ResetTransientEditState();
                    InvalidateSlotCache();
                    CancelKeyCapture();
                    GUIUtility.ExitGUI();
                }
            }
        }

        private void DrawAboutTab()
        {
            float cardW = Mathf.Min(820f, Mathf.Max(680f, _currentMainWidth - 40f));
            _aboutScroll = GUILayout.BeginScrollView(_aboutScroll, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            try
            {
                GUILayout.Space(12f);

                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                GUILayout.BeginVertical(GUILayout.Width(cardW));

                GUILayout.BeginVertical(BlackWhiteSkin.CardCurrentStyle);
                GUILayout.Label(I18n.T("about.title"), CenterTitleStyle, GUILayout.ExpandWidth(true), GUILayout.Height(52f));
                GUILayout.Space(4f);
                GUILayout.Label(I18n.F("about.version", Plugin.PluginVersion), CenterMutedStyle, GUILayout.ExpandWidth(true), GUILayout.Height(36f));
                GUILayout.Space(8f);
                GUILayout.Label(I18n.T("about.desc"), CenterLabelStyle, GUILayout.ExpandWidth(true));
                GUILayout.EndVertical();

                GUILayout.Space(14f);
                GUILayout.BeginVertical(BlackWhiteSkin.CardStyle);
                GUILayout.Label(I18n.T("about.sec_links"), CenterSectionStyle, GUILayout.ExpandWidth(true), GUILayout.Height(40f));
                GUILayout.Space(10f);
                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                OpenUrlButton(I18n.T("about.link_repo"), "https://github.com/HuanXinWork/Cu-SaveManager", 320f, 44f);
                GUILayout.Space(12f);
                OpenUrlButton(I18n.T("about.link_release"), "https://github.com/HuanXinWork/Cu-SaveManager/releases/latest", 320f, 44f);
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                GUILayout.EndVertical();

                GUILayout.Space(14f);
                GUILayout.BeginVertical(BlackWhiteSkin.CardStyle);
                GUILayout.Label(I18n.T("about.sec_credits"), CenterSectionStyle, GUILayout.ExpandWidth(true), GUILayout.Height(40f));
                GUILayout.Space(10f);
                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                OpenUrlButton("huanxin886", "https://github.com/huanxin886", 240f, 44f);
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                GUILayout.EndVertical();

                GUILayout.Space(14f);
                GUILayout.BeginVertical(BlackWhiteSkin.CardStyle);
                GUILayout.Label(I18n.T("about.sec_deps"), CenterSectionStyle, GUILayout.ExpandWidth(true), GUILayout.Height(40f));
                GUILayout.Space(10f);
                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                OpenUrlButton("Casualties: Together", "https://www.nexusmods.com/scavprototype/mods/67", 224f, 44f);
                GUILayout.Space(10f);
                OpenUrlButton("BepInEx", "https://github.com/BepInEx/BepInEx", 216f, 44f);
                GUILayout.Space(10f);
                OpenUrlButton("QoL Unknown", "https://github.com/jimmyking9999999/QoL-Unknown", 216f, 44f);
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                GUILayout.EndVertical();

                GUILayout.EndVertical();
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();

                GUILayout.Space(20f);
            }
            finally
            {
                GUILayout.EndScrollView();
            }
        }

        private static GUIStyle _centerTitle, _centerSection, _centerLabel, _centerMuted;
        private static GUIStyle CenterTitleStyle =>
            _centerTitle ?? (_centerTitle = new GUIStyle(BlackWhiteSkin.HeaderStyle)
            {
                alignment = TextAnchor.MiddleCenter,
                wordWrap = false,
            });
        private static GUIStyle CenterSectionStyle =>
            _centerSection ?? (_centerSection = new GUIStyle(BlackWhiteSkin.SectionHeaderStyle)
            {
                alignment = TextAnchor.MiddleCenter,
                wordWrap = false,
            });
        private static GUIStyle CenterLabelStyle =>
            _centerLabel ?? (_centerLabel = new GUIStyle(BlackWhiteSkin.BodyLabelStyle)
            {
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true,
            });
        private static GUIStyle CenterMutedStyle =>
            _centerMuted ?? (_centerMuted = new GUIStyle(BlackWhiteSkin.MutedLabelStyle)
            {
                alignment = TextAnchor.MiddleCenter,
                wordWrap = false,
            });

        private static void OpenUrlButton(string label, string url, float minWidth, float height)
        {
            float width = BlackWhiteSkin.CalcWidth(BlackWhiteSkin.SubtleBtnStyle, label, minWidth, 12f);
            if (GUILayout.Button(label, BlackWhiteSkin.SubtleBtnStyle, GUILayout.Width(width), GUILayout.Height(height)))
            {
                ClearTextFocus();
                try { Application.OpenURL(url); } catch { }
            }
        }
    }
}
