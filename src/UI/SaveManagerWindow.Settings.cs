using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using UnityEngine;

namespace CasualtiesUnknown.SaveManager
{
    internal sealed partial class SaveManagerWindow
    {
        private const float FloatFieldLimit = 100000f;
        private const float SwitchGap = 6f;
        private const float SwitchRowMinHeight = 46f;
        private const float SwitchBtnHeight = 40f;
        private const float ValueBoxHeight = 40f;
        private const float SubIndentW = 18f;

        private static readonly KeyCode[] ModifierKeys =
        {
            KeyCode.LeftControl,
            KeyCode.RightControl,
            KeyCode.LeftShift,
            KeyCode.RightShift,
            KeyCode.LeftAlt,
            KeyCode.RightAlt,
        };

        private static readonly string[] FormLabelKeys =
        {
            "lbl.alias_optional",
            "lbl.world_engine",
            "lbl.seed_input",
            "lbl.pos_mode",
            "lbl.fixed_x",
            "lbl.fixed_y",
        };

        private Vector2 _settingsScroll;
        private string _quickNickname = "";
        private bool _capturingSettingsKey;
        private bool _capturingSlotsKey;
        private bool _capturingQuickKey;
        private string _seedInputCache;
        private string _fixedXCache;
        private string _fixedYCache;

        private void ResetSettingsFieldCaches()
        {
            _seedInputCache = null;
            _fixedXCache = null;
            _fixedYCache = null;
            _centerTitle = null;
            _centerSection = null;
            _centerLabel = null;
            _centerMuted = null;
        }

        internal static void ClearTextFocus()
        {
            GUI.FocusControl(null);
            GUIUtility.keyboardControl = 0;
        }

        internal void CancelKeyCapture()
        {
            bool hadHotkeyCapture = IsCapturingHotkey;
            _capturingSettingsKey = false;
            _capturingSlotsKey = false;
            _capturingQuickKey = false;
            if (!hadHotkeyCapture && !string.IsNullOrEmpty(_nicknameEditingPath))
            {
                CancelNicknameEdit();
            }
        }

        private static float ResolveFormLabelWidth()
        {
            float maxW = DefaultLabelColW;
            GUIStyle style = BlackWhiteSkin.SingleLineLabelStyle;
            for (int i = 0; i < FormLabelKeys.Length; i++)
            {
                float w = BlackWhiteSkin.CalcWidth(style, I18n.T(FormLabelKeys[i]), DefaultLabelColW, 14f);
                if (w > maxW) maxW = w;
            }
            return maxW;
        }

        private void DrawSettingsTab()
        {
            float formLabelW = ResolveFormLabelWidth();

            _settingsScroll = GUILayout.BeginScrollView(_settingsScroll,
                GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            try
            {
                DrawSectionHeader(I18n.T("sec.save_now"), first: true);
                GUILayout.BeginVertical(BlackWhiteSkin.CardStyle);
                GUILayout.BeginHorizontal();
                GUILayout.Label(I18n.T("lbl.alias_optional"), BlackWhiteSkin.SingleLineLabelStyle,
                    GUILayout.Width(formLabelW), GUILayout.MinHeight(RowMinHeight));
                GUILayout.Space(8f);
                _quickNickname = GUILayout.TextField(_quickNickname ?? "",
                    GUILayout.MinWidth(180f), GUILayout.ExpandWidth(true), GUILayout.Height(RowMinHeight));
                GUILayout.Space(10f);

                string saveNowText = I18n.T("btn.save_now");
                float saveNowW = BlackWhiteSkin.CalcWidth(BlackWhiteSkin.PrimaryBtnStyle, saveNowText, 140f, 10f);
                if (GUILayout.Button(saveNowText, BlackWhiteSkin.PrimaryBtnStyle,
                    GUILayout.Width(saveNowW), GUILayout.Height(RowMinHeight)))
                {
                    if (string.IsNullOrEmpty(_quickNickname)) _onSaveNow?.Invoke();
                    else _onSaveNowAs?.Invoke(_quickNickname);
                    _quickNickname = "";
                    InvalidateSlotCache();
                    ClearTextFocus();
                }
                GUILayout.Space(8f);

                string saveExitText = I18n.T("btn.save_exit");
                float saveExitW = BlackWhiteSkin.CalcWidth(GUI.skin.button, saveExitText, 160f, 10f);
                if (GUILayout.Button(saveExitText, GUI.skin.button,
                    GUILayout.Width(saveExitW), GUILayout.Height(RowMinHeight)))
                {
                    _quickNickname = "";
                    ClearTextFocus();
                    _onSaveAndExit?.Invoke();
                    InvalidateSlotCache();
                    GUIUtility.ExitGUI();
                }
                GUILayout.EndHorizontal();
                GUILayout.EndVertical();

                DrawSectionHeader(I18n.T("sec.auto_backup"));
                GUILayout.BeginVertical(BlackWhiteSkin.CardStyle);
                bool newAuto = DrawSwitch(I18n.T("sw.auto_backup_enabled"), _cfg.AutoBackupEnabled.Value);
                if (newAuto != _cfg.AutoBackupEnabled.Value)
                {
                    _cfg.AutoBackupEnabled.Value = newAuto;
                    if (newAuto) _onIntervalChanged?.Invoke();
                }
                GUILayout.Space(10f);
                DrawFloatSlider(I18n.F("fmt.interval_minutes", _cfg.AutoBackupIntervalMinutes.Value),
                    _cfg.AutoBackupIntervalMinutes, 0.5f, 240f,
                    snap: 0.5f, onChanged: _onIntervalChanged, fallbackBadge: _cfg.AutoBackupIntervalMinutes.Value.ToString("0.#", CultureInfo.InvariantCulture) + "m");
                DrawIntSlider(I18n.F("fmt.auto_keep_count", _cfg.AutoBackupKeep.Value),
                    _cfg.AutoBackupKeep, 1, 100);
                GUILayout.Space(10f);
                bool newGuard = DrawSwitch(I18n.T("sw.backup_before_overwrite"), _cfg.BackupBeforeOverwrite.Value);
                if (newGuard != _cfg.BackupBeforeOverwrite.Value) _cfg.BackupBeforeOverwrite.Value = newGuard;
                GUILayout.EndVertical();

                DrawSectionHeader(I18n.T("sec.auto_rollback"));
                GUILayout.BeginVertical(BlackWhiteSkin.CardStyle);
                bool newAutoRollback = DrawSwitch(I18n.T("sw.auto_rollback_enabled"), _cfg.AutoRollbackOnDeath.Value);
                if (newAutoRollback != _cfg.AutoRollbackOnDeath.Value) _cfg.AutoRollbackOnDeath.Value = newAutoRollback;

                bool newAllowPartial = DrawSwitch(I18n.T("sw.allow_partial_mp_rollback"), _cfg.AllowPartialMultiplayerRollback.Value);
                if (newAllowPartial != _cfg.AllowPartialMultiplayerRollback.Value) _cfg.AllowPartialMultiplayerRollback.Value = newAllowPartial;

                GUILayout.Space(10f);
                DrawFloatSlider(I18n.F("fmt.rollback_delay", _cfg.RollbackDelaySeconds.Value),
                    _cfg.RollbackDelaySeconds, 1f, 10f,
                    snap: 0.5f, onChanged: null, fallbackBadge: _cfg.RollbackDelaySeconds.Value.ToString("0.#", CultureInfo.InvariantCulture) + "s");

                string mpThresholdLabel = _cfg.MultiplayerDeathThreshold.Value <= 0
                    ? I18n.T("lbl.mp_death_all")
                    : I18n.F("fmt.mp_death_threshold", _cfg.MultiplayerDeathThreshold.Value);
                DrawIntSlider(mpThresholdLabel, _cfg.MultiplayerDeathThreshold, 0, 16);

                DrawFloatSlider(I18n.F("fmt.mp_stage_two_delay", _cfg.MpStageTwoDelaySeconds.Value),
                    _cfg.MpStageTwoDelaySeconds, 0.2f, 5.0f,
                    snap: 0.05f, onChanged: null, fallbackBadge: _cfg.MpStageTwoDelaySeconds.Value.ToString("0.00", CultureInfo.InvariantCulture) + "s");

                DrawIntSlider(I18n.F("fmt.recent_slots_limit", _cfg.RecentSlotsLimit.Value),
                    _cfg.RecentSlotsLimit, 5, 100);
                GUILayout.EndVertical();

                DrawSectionHeader(I18n.T("sec.world"));
                GUILayout.BeginVertical(BlackWhiteSkin.CardStyle);
                DrawWorldGroup(formLabelW);
                GUILayout.EndVertical();

                DrawSectionHeader(I18n.T("sec.hotkeys"));
                GUILayout.BeginVertical(BlackWhiteSkin.CardStyle);
                DrawHotkeyRow(I18n.T("lbl.hotkey_toggle_panel"), _cfg.ToggleSlotsHotkey, ref _capturingSlotsKey,
                    () => { _capturingSettingsKey = false; _capturingQuickKey = false; });
                DrawHotkeyRow(I18n.T("lbl.hotkey_quick_save"), _cfg.QuickSaveHotkey, ref _capturingQuickKey,
                    () => { _capturingSettingsKey = false; _capturingSlotsKey = false; });
                DrawHotkeyRow(I18n.T("lbl.hotkey_settings_tab"), _cfg.ToggleSettingsHotkey, ref _capturingSettingsKey,
                    () => { _capturingSlotsKey = false; _capturingQuickKey = false; }, lastRow: true);
                GUILayout.EndVertical();

                DrawSectionHeader(I18n.T("sec.misc"));
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

                GUILayout.Space(10f);
                DrawLanguageModeRow(I18n.T("lbl.language"), _cfg.PreferredLanguage);
                GUILayout.EndVertical();

                GUILayout.Space(20f);
            }
            finally
            {
                GUILayout.EndScrollView();
            }
        }

        private static void DrawSectionHeader(string title, bool first = false)
        {
            GUILayout.Space(first ? 4f : 16f);
            Rect row = GUILayoutUtility.GetRect(200f, 42f, GUILayout.ExpandWidth(true), GUILayout.Height(42f));
            BlackWhiteSkin.DrawHLine(new Rect(row.x, row.y + 7f, 4f, row.height - 14f));
            BlackWhiteSkin.DrawShadowedLabel(new Rect(row.x + 14f, row.y, row.width - 14f, row.height), title, BlackWhiteSkin.SectionHeaderStyle);
            GUILayout.Space(6f);
        }

        private static void BeginSubBlock()
        {
            GUILayout.Space(6f);
            GUILayout.BeginHorizontal();
            GUILayout.Space(SubIndentW);
            GUILayout.BeginVertical();
        }

        private static void EndSubBlock()
        {
            GUILayout.EndVertical();
            Rect subRect = GUILayoutUtility.GetLastRect();
            GUILayout.EndHorizontal();
            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                BlackWhiteSkin.DrawSoftVLine(new Rect(
                    subRect.x - 10f,
                    subRect.y + 2f,
                    2f,
                    Mathf.Max(0f, subRect.height - 4f)));
            }
            GUILayout.Space(4f);
        }

        private void SyncWorldPreference()
        {
            WorldEngineArbiter.SyncPreference(_cfg.PreferredEngine.Value, _cfg.SeedInput.Value,
                _cfg.PositionMode.Value, _cfg.FixedX.Value, _cfg.FixedY.Value);
        }

        private void DrawWorldGroup(float formLabelW)
        {
            bool qolPresent = QolBridge.IsQolPresent();
            bool krokPresent = MultiplayerBridge.IsMultiplayerEnabled();
            string engine = WorldEngineArbiter.ResolveEffectiveEngineName(_cfg.PreferredEngine.Value);
            float subLabelW = Mathf.Max(236f, formLabelW - SubIndentW);

            GUILayout.BeginHorizontal();
            GUILayout.Label(I18n.T("lbl.world_engine"), BlackWhiteSkin.SingleLineLabelStyle,
                GUILayout.Width(formLabelW), GUILayout.MinHeight(RowMinHeight));
            GUILayout.FlexibleSpace();

            if (qolPresent)
            {
                string qolText = I18n.T("opt.engine_qol");
                GUIStyle qolStyle = engine == "qol" ? BlackWhiteSkin.SegmentActiveStyle : BlackWhiteSkin.SegmentStyle;
                float qolW = BlackWhiteSkin.CalcWidth(qolStyle, qolText, 148f, 10f);
                if (GUILayout.Button(qolText, qolStyle, GUILayout.Width(qolW), GUILayout.Height(SwitchBtnHeight)))
                {
                    ClearTextFocus();
                    if (engine != "qol")
                    {
                        _cfg.PreferredEngine.Value = "qol";
                        SyncWorldPreference();
                        GUIUtility.ExitGUI();
                    }
                }
                GUILayout.Space(6f);
            }

            if (krokPresent)
            {
                string krokText = I18n.T("opt.engine_krok");
                GUIStyle krokStyle = engine == "krok" ? BlackWhiteSkin.SegmentActiveStyle : BlackWhiteSkin.SegmentStyle;
                float krokW = BlackWhiteSkin.CalcWidth(krokStyle, krokText, 160f, 10f);
                if (GUILayout.Button(krokText, krokStyle, GUILayout.Width(krokW), GUILayout.Height(SwitchBtnHeight)))
                {
                    ClearTextFocus();
                    if (engine != "krok")
                    {
                        _cfg.PreferredEngine.Value = "krok";
                        SyncWorldPreference();
                        GUIUtility.ExitGUI();
                    }
                }
                GUILayout.Space(6f);
            }

            string selfText = I18n.T("opt.engine_self");
            GUIStyle selfStyle = engine == "self" ? BlackWhiteSkin.SegmentActiveStyle : BlackWhiteSkin.SegmentStyle;
            float selfW = BlackWhiteSkin.CalcWidth(selfStyle, selfText, 132f, 10f);
            if (GUILayout.Button(selfText, selfStyle, GUILayout.Width(selfW), GUILayout.Height(SwitchBtnHeight)))
            {
                ClearTextFocus();
                if (engine != "self")
                {
                    _cfg.PreferredEngine.Value = "self";
                    SyncWorldPreference();
                    GUIUtility.ExitGUI();
                }
            }
            GUILayout.EndHorizontal();

            if (!qolPresent && !krokPresent)
            {
                GUILayout.Space(6f);
                GUILayout.Label(I18n.T("hint.qol_absent"), BlackWhiteSkin.MutedLabelStyle, GUILayout.Height(36f));
            }
            else if (engine == "krok")
            {
                GUILayout.Space(6f);
                GUILayout.Label(I18n.T("hint.mp_krok_engine"), BlackWhiteSkin.MutedLabelStyle, GUILayout.Height(36f));
            }

            if (engine == "self")
            {
                BeginSubBlock();
                _seedInputCache = _seedInputCache ?? _cfg.SeedInput.Value;
                GUILayout.BeginHorizontal();
                GUILayout.Label(I18n.T("lbl.seed_input"), BlackWhiteSkin.SingleLineLabelStyle,
                    GUILayout.Width(subLabelW), GUILayout.MinHeight(RowMinHeight));
                GUILayout.Space(8f);
                string newSeed = GUILayout.TextField(_seedInputCache ?? "",
                    GUILayout.MinWidth(220f), GUILayout.ExpandWidth(true), GUILayout.Height(RowMinHeight));
                if (newSeed != _seedInputCache)
                {
                    _seedInputCache = newSeed;
                    _cfg.SeedInput.Value = newSeed;
                    SyncWorldPreference();
                }
                GUILayout.EndHorizontal();
                EndSubBlock();
            }

            GUILayout.Space(12f);

            bool posFixed = string.Equals(_cfg.PositionMode.Value, "fixedPos", StringComparison.OrdinalIgnoreCase);
            GUILayout.BeginHorizontal();
            GUILayout.Label(I18n.T("lbl.pos_mode"), BlackWhiteSkin.SingleLineLabelStyle,
                GUILayout.Width(formLabelW), GUILayout.MinHeight(RowMinHeight));
            GUILayout.FlexibleSpace();

            string lastPosText = I18n.T("opt.pos_last");
            GUIStyle lastPosStyle = posFixed ? BlackWhiteSkin.SegmentStyle : BlackWhiteSkin.SegmentActiveStyle;
            float lastPosW = BlackWhiteSkin.CalcWidth(lastPosStyle, lastPosText, 144f, 10f);
            if (GUILayout.Button(lastPosText, lastPosStyle, GUILayout.Width(lastPosW), GUILayout.Height(SwitchBtnHeight)))
            {
                ClearTextFocus();
                if (posFixed)
                {
                    _cfg.PositionMode.Value = "lastPos";
                    SyncWorldPreference();
                    GUIUtility.ExitGUI();
                }
            }
            GUILayout.Space(6f);

            string fixedPosText = I18n.T("opt.pos_fixed");
            GUIStyle fixedPosStyle = posFixed ? BlackWhiteSkin.SegmentActiveStyle : BlackWhiteSkin.SegmentStyle;
            float fixedPosW = BlackWhiteSkin.CalcWidth(fixedPosStyle, fixedPosText, 144f, 10f);
            if (GUILayout.Button(fixedPosText, fixedPosStyle, GUILayout.Width(fixedPosW), GUILayout.Height(SwitchBtnHeight)))
            {
                ClearTextFocus();
                if (!posFixed)
                {
                    _cfg.PositionMode.Value = "fixedPos";
                    SyncWorldPreference();
                    GUIUtility.ExitGUI();
                }
            }
            GUILayout.EndHorizontal();

            if (posFixed)
            {
                BeginSubBlock();
                _fixedXCache = _fixedXCache ?? _cfg.FixedX.Value.ToString(CultureInfo.InvariantCulture);
                _fixedYCache = _fixedYCache ?? _cfg.FixedY.Value.ToString(CultureInfo.InvariantCulture);
                DrawFloatField(I18n.T("lbl.fixed_x"), subLabelW, ref _fixedXCache, _cfg.FixedX, SyncWorldPreference);
                DrawFloatField(I18n.T("lbl.fixed_y"), subLabelW, ref _fixedYCache, _cfg.FixedY, SyncWorldPreference, lastField: true);
                EndSubBlock();
            }
        }

        private static void DrawFloatField(string label, float labelW, ref string cache, ConfigEntry<float> entry, Action onChanged = null, bool lastField = false)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, BlackWhiteSkin.SingleLineLabelStyle,
                GUILayout.Width(labelW), GUILayout.MinHeight(RowMinHeight));
            GUILayout.Space(8f);
            string next = GUILayout.TextField(cache ?? "",
                GUILayout.Width(240f), GUILayout.Height(RowMinHeight));
            if (next != cache)
            {
                cache = next;
                if (float.TryParse(next, NumberStyles.Float, CultureInfo.InvariantCulture, out float v)
                    && !float.IsNaN(v)
                    && !float.IsInfinity(v))
                {
                    float clamped = Mathf.Clamp(v, -FloatFieldLimit, FloatFieldLimit);
                    if (clamped != entry.Value)
                    {
                        entry.Value = clamped;
                        onChanged?.Invoke();
                    }
                }
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            if (!lastField) GUILayout.Space(6f);
        }

        private static bool DrawSwitch(string label, bool value)
        {
            string onText = CleanSwitchGlyphs(I18n.T("sw.on"));
            string offText = CleanSwitchGlyphs(I18n.T("sw.off"));
            float halfW = Mathf.Max(
                BlackWhiteSkin.CalcWidth(BlackWhiteSkin.SegmentStyle, onText, 80f, 8f),
                BlackWhiteSkin.CalcWidth(BlackWhiteSkin.SegmentStyle, offText, 80f, 8f));
            float totalW = halfW * 2f + SwitchGap;

            GUILayout.BeginHorizontal();
            GUILayout.Label(label, BlackWhiteSkin.RowLabelStyle, GUILayout.ExpandWidth(true), GUILayout.MinHeight(SwitchRowMinHeight));
            GUILayout.Space(16f);

            Rect slot = GUILayoutUtility.GetRect(totalW, SwitchRowMinHeight,
                GUILayout.Width(totalW), GUILayout.ExpandHeight(true), GUILayout.ExpandWidth(false));
            float btnY = slot.y + Mathf.Max(0f, Mathf.Floor((slot.height - SwitchBtnHeight) * 0.5f));
            Rect onRect = new Rect(slot.x, btnY, halfW, SwitchBtnHeight);
            Rect offRect = new Rect(slot.x + halfW + SwitchGap, btnY, halfW, SwitchBtnHeight);

            bool result = value;
            if (GUI.Button(onRect, onText, value ? BlackWhiteSkin.SegmentActiveStyle : BlackWhiteSkin.SegmentStyle))
            {
                result = true;
                ClearTextFocus();
            }
            if (GUI.Button(offRect, offText, value ? BlackWhiteSkin.SegmentStyle : BlackWhiteSkin.SegmentActiveStyle))
            {
                result = false;
                ClearTextFocus();
            }

            GUILayout.EndHorizontal();
            GUILayout.Space(6f);
            return result;
        }

        private static string CleanSwitchGlyphs(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            string cleaned = raw.Replace("◀", "").Replace("▶", "").Trim();
            return string.IsNullOrEmpty(cleaned) ? raw : cleaned;
        }

        private static List<KeyValuePair<string, string>> BuildLanguageOptions()
        {
            string[] codes = I18n.GetAvailableLanguageCodes();
            var list = new List<KeyValuePair<string, string>>(codes.Length + 1)
            {
                new KeyValuePair<string, string>("auto", I18n.T("opt.language_auto"))
            };
            for (int i = 0; i < codes.Length; i++)
            {
                string code = codes[i];
                list.Add(new KeyValuePair<string, string>(code, I18n.GetLanguageDisplayName(code)));
            }
            return list;
        }

        private void DrawLanguageModeRow(string label, ConfigEntry<string> entry)
        {
            string mode = I18n.NormalizeLanguageMode(entry.Value);
            List<KeyValuePair<string, string>> options = BuildLanguageOptions();
            const int perRow = 4;

            GUILayout.BeginHorizontal();
            GUILayout.Label(label, BlackWhiteSkin.RowLabelStyle, GUILayout.ExpandWidth(true), GUILayout.MinHeight(RowMinHeight));
            GUILayout.Space(16f);
            GUILayout.BeginVertical(GUILayout.ExpandWidth(false));
            for (int i = 0; i < options.Count; i += perRow)
            {
                GUILayout.BeginHorizontal();
                int end = Math.Min(i + perRow, options.Count);
                for (int j = i; j < end; j++)
                {
                    KeyValuePair<string, string> opt = options[j];
                    if (j > i) GUILayout.Space(6f);

                    bool active = string.Equals(mode, opt.Key, StringComparison.OrdinalIgnoreCase);
                    GUIStyle optStyle = active ? BlackWhiteSkin.SegmentActiveStyle : BlackWhiteSkin.SegmentStyle;
                    float optW = BlackWhiteSkin.CalcWidth(optStyle, opt.Value, 120f, 10f);
                    if (GUILayout.Button(opt.Value, optStyle, GUILayout.Width(optW), GUILayout.Height(SwitchBtnHeight)))
                    {
                        ClearTextFocus();
                        if (!active)
                        {
                            entry.Value = opt.Key;
                            I18n.InvalidateCache();
                            InvalidateSlotCache();
                            MenuButtonUiInjector.RefreshButtonText();
                            GUIUtility.ExitGUI();
                        }
                    }
                }
                GUILayout.EndHorizontal();
                if (end < options.Count) GUILayout.Space(6f);
            }
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
        }

        private static void SplitLabelAndValueBadge(string formattedLabel, string fallbackBadge, out string titlePart, out string badgePart)
        {
            if (!string.IsNullOrEmpty(formattedLabel))
            {
                int idxFull = formattedLabel.LastIndexOf('：');
                int idxHalf = formattedLabel.LastIndexOf(':');
                int splitAt = Math.Max(idxFull, idxHalf);
                if (splitAt > 0 && splitAt < formattedLabel.Length - 1)
                {
                    string candidateBadge = formattedLabel.Substring(splitAt + 1).Trim();
                    if (candidateBadge.Length > 0 && candidateBadge.Length <= 32)
                    {
                        titlePart = formattedLabel.Substring(0, splitAt).Trim();
                        badgePart = candidateBadge;
                        return;
                    }
                }
            }
            titlePart = formattedLabel ?? "";
            badgePart = fallbackBadge ?? "";
        }

        private static void DrawFloatSlider(string label, ConfigEntry<float> entry, float min, float max,
            float snap, Action onChanged, string fallbackBadge = null)
        {
            string defaultBadge = fallbackBadge ?? entry.Value.ToString("0.##", CultureInfo.InvariantCulture);
            SplitLabelAndValueBadge(label, defaultBadge, out string title, out string badge);

            GUILayout.BeginHorizontal();
            GUILayout.Label(title, BlackWhiteSkin.RowLabelStyle, GUILayout.ExpandWidth(true), GUILayout.MinHeight(ValueBoxHeight));
            GUILayout.Space(12f);
            float badgeW = BlackWhiteSkin.CalcWidth(BlackWhiteSkin.ValueBoxStyle, badge, 90f, 8f);
            GUILayout.Label(badge, BlackWhiteSkin.ValueBoxStyle, GUILayout.Width(badgeW), GUILayout.Height(ValueBoxHeight));
            GUILayout.EndHorizontal();

            float next = GUILayout.HorizontalSlider(entry.Value, min, max, GUILayout.ExpandWidth(true));
            if (snap > 0f) next = Mathf.Round(next / snap) * snap;
            if (Mathf.Abs(next - entry.Value) > 0.0005f)
            {
                entry.Value = Mathf.Clamp(next, min, max);
                ClearTextFocus();
                onChanged?.Invoke();
            }
            GUILayout.Space(6f);
        }

        private static void DrawIntSlider(string label, ConfigEntry<int> entry, int min, int max)
        {
            SplitLabelAndValueBadge(label, entry.Value.ToString(CultureInfo.InvariantCulture), out string title, out string badge);

            GUILayout.BeginHorizontal();
            GUILayout.Label(title, BlackWhiteSkin.RowLabelStyle, GUILayout.ExpandWidth(true), GUILayout.MinHeight(ValueBoxHeight));
            GUILayout.Space(12f);
            float badgeW = BlackWhiteSkin.CalcWidth(BlackWhiteSkin.ValueBoxStyle, badge, 90f, 8f);
            GUILayout.Label(badge, BlackWhiteSkin.ValueBoxStyle, GUILayout.Width(badgeW), GUILayout.Height(ValueBoxHeight));
            GUILayout.EndHorizontal();

            int next = Mathf.RoundToInt(GUILayout.HorizontalSlider(entry.Value, min, max, GUILayout.ExpandWidth(true)));
            if (next != entry.Value)
            {
                entry.Value = Mathf.Clamp(next, min, max);
                ClearTextFocus();
            }
            GUILayout.Space(6f);
        }

        private void DrawHotkeyRow(string label, ConfigEntry<KeyboardShortcut> entry,
            ref bool capturing, Action onStart, bool lastRow = false)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, BlackWhiteSkin.RowLabelStyle, GUILayout.ExpandWidth(true), GUILayout.MinHeight(RowMinHeight));
            GUILayout.Space(12f);

            bool bound = HotkeyConfig.IsBound(entry.Value);
            string text;
            GUIStyle keyBtnStyle;
            if (capturing)
            {
                text = "●  " + I18n.T("btn.press_a_key") + " (ESC)";
                keyBtnStyle = BlackWhiteSkin.PrimaryBtnStyle;
            }
            else if (bound)
            {
                text = entry.Value.ToString();
                keyBtnStyle = GUI.skin.button;
            }
            else
            {
                text = I18n.T("btn.unbound");
                keyBtnStyle = BlackWhiteSkin.SubtleBtnStyle;
            }

            float keyBtnW = BlackWhiteSkin.CalcWidth(keyBtnStyle, text, 240f, 12f);
            if (GUILayout.Button(text, keyBtnStyle, GUILayout.Width(keyBtnW), GUILayout.Height(RowMinHeight)))
            {
                capturing = true;
                onStart();
                ClearTextFocus();
            }
            GUILayout.Space(8f);

            string clearText = I18n.T("btn.clear");
            float clearW = BlackWhiteSkin.CalcWidth(BlackWhiteSkin.SubtleBtnStyle, clearText, 88f, 10f);
            if (GUILayout.Button(clearText, BlackWhiteSkin.SubtleBtnStyle, GUILayout.Width(clearW), GUILayout.Height(RowMinHeight)))
            {
                entry.Value = new KeyboardShortcut(KeyCode.None);
                capturing = false;
                ClearTextFocus();
            }
            GUILayout.EndHorizontal();
            if (!lastRow) GUILayout.Space(8f);
        }

        private static bool IsPureModifierKey(KeyCode key)
        {
            for (int i = 0; i < ModifierKeys.Length; i++)
            {
                if (ModifierKeys[i] == key) return true;
            }
            return false;
        }

        private void CaptureKeyDownIfNeeded()
        {
            if (!IsCapturingHotkey) return;
            var ev = Event.current;
            if (ev == null || ev.type != EventType.KeyDown || ev.keyCode == KeyCode.None) return;

            if (ev.keyCode == KeyCode.Escape)
            {
                CancelKeyCapture();
                ev.Use();
                return;
            }

            // 忽略单独按下的纯修饰键，保持捕获态等待主键按下以完成组合键录制。
            if (IsPureModifierKey(ev.keyCode))
            {
                ev.Use();
                return;
            }

            var mods = new List<KeyCode>(4);
            for (int i = 0; i < ModifierKeys.Length; i++)
            {
                KeyCode mod = ModifierKeys[i];
                if (Input.GetKey(mod))
                {
                    mods.Add(mod);
                }
            }

            var sc = mods.Count > 0
                ? new KeyboardShortcut(ev.keyCode, mods.ToArray())
                : new KeyboardShortcut(ev.keyCode);

            if (_capturingSettingsKey) _cfg.ToggleSettingsHotkey.Value = sc;
            else if (_capturingSlotsKey) _cfg.ToggleSlotsHotkey.Value = sc;
            else if (_capturingQuickKey) _cfg.QuickSaveHotkey.Value = sc;
            CancelKeyCapture();
            ev.Use();
        }
    }
}
