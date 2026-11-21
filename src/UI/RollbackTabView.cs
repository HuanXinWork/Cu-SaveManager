using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>回档分页视图：展示纯黑白线框倒计时进度条、自适应换行日期筛选与最近可回档槽位列表。</summary>
    internal sealed class RollbackTabView
    {
        private static readonly List<SlotInfo> EmptySlots = new List<SlotInfo>(0);
        private const float RollbackCardHeight = 104f;

        private readonly HotkeyConfig _cfg;
        private readonly RollbackController _rollback;
        private readonly Func<(List<SlotInfo> slots, int currentRunId)> _snapshotProvider;
        private readonly Action _onSlotMutated;

        private Vector2 _scroll;
        private string _dateFilter;
        private float _measuredWidth = 1120f;
        private float _layoutRowWidth = 1120f;

        private List<SlotInfo> _cachedSlotsRef;
        private int _cachedSlotsCount = -1;
        private int _cachedRunId = int.MinValue;
        private readonly List<string> _cachedDates = new List<string>();
        private readonly HashSet<string> _dateDedupSet = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _cachedMetaLabels = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _cachedLayers = new Dictionary<string, int>(StringComparer.Ordinal);

        internal RollbackTabView(
            HotkeyConfig cfg,
            SaveStore store,
            RollbackController rollback,
            Func<(List<SlotInfo> slots, int currentRunId)> snapshotProvider,
            Action onSlotMutated = null)
        {
            _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
            _rollback = rollback ?? throw new ArgumentNullException(nameof(rollback));
            _snapshotProvider = snapshotProvider ?? throw new ArgumentNullException(nameof(snapshotProvider));
            _onSlotMutated = onSlotMutated;
        }

        internal void Draw()
        {
            var ev = Event.current;
            if (ev != null && ev.type == EventType.Layout && _measuredWidth > 240f)
            {
                _layoutRowWidth = _measuredWidth;
            }

            var (rawSlots, currentRunId) = _snapshotProvider();
            List<SlotInfo> slots = rawSlots ?? EmptySlots;
            RefreshSnapshotCacheIfNeeded(slots, currentRunId);

            Rect probe = GUILayoutUtility.GetRect(0f, 0f, GUILayout.ExpandWidth(true));
            if (ev != null && ev.type == EventType.Repaint && probe.width > 240f)
            {
                _measuredWidth = probe.width;
            }

            DrawStatusBar();
            GUILayout.Space(10f);
            DrawDateFilterRows();
            GUILayout.Space(8f);

            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            try
            {
                int limit = Mathf.Max(1, _cfg.RecentSlotsLimit.Value);
                if (currentRunId == 0)
                {
                    GUILayout.BeginVertical(BlackWhiteSkin.CardWarnStyle);
                    GUILayout.Label(I18n.T("msg.run_id_unresolved_mixed").TrimStart(),
                        BlackWhiteSkin.BodyLabelStyle, GUILayout.MinHeight(38f));
                    GUILayout.EndVertical();
                    GUILayout.Space(8f);
                }

                int shown = 0;
                for (int i = 0; i < slots.Count; i++)
                {
                    if (shown >= limit) break;
                    SlotInfo slot = slots[i];
                    if (currentRunId != 0 && slot.Sidecar.RunId != currentRunId) continue;
                    if (_dateFilter != null && slot.DateFolder != _dateFilter) continue;
                    if (slot.File.Name.StartsWith(SaveStore.BeforeLoadPrefix, StringComparison.Ordinal)) continue;
                    DrawSlotRow(slot);
                    shown++;
                }

                if (shown == 0)
                {
                    GUILayout.Space(24f);
                    string emptyMsg = (_dateFilter == null
                        ? I18n.T("msg.no_rollback_targets")
                        : I18n.T("msg.no_rollback_targets_filtered")).TrimStart();
                    GUILayout.Label(emptyMsg, BlackWhiteSkin.BodyLabelStyle, GUILayout.MinHeight(38f));
                }
            }
            finally
            {
                GUILayout.EndScrollView();
            }
        }

        private void RefreshSnapshotCacheIfNeeded(List<SlotInfo> slots, int currentRunId)
        {
            if (ReferenceEquals(slots, _cachedSlotsRef)
                && slots.Count == _cachedSlotsCount
                && currentRunId == _cachedRunId)
            {
                return;
            }

            _cachedSlotsRef = slots;
            _cachedSlotsCount = slots.Count;
            _cachedRunId = currentRunId;
            _cachedDates.Clear();
            _dateDedupSet.Clear();
            _cachedMetaLabels.Clear();
            _cachedLayers.Clear();

            for (int i = 0; i < slots.Count; i++)
            {
                SlotInfo s = slots[i];
                if (currentRunId != 0 && s.Sidecar.RunId != currentRunId) continue;
                if (s.File.Name.StartsWith(SaveStore.BeforeLoadPrefix, StringComparison.Ordinal)) continue;

                if (_dateDedupSet.Add(s.DateFolder))
                {
                    _cachedDates.Add(s.DateFolder);
                }

                long unixMs = s.Sidecar.LastPlayedAtUnixMs;
                string timePart = unixMs > 0L
                    ? DateTimeOffset.FromUnixTimeMilliseconds(unixMs).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                    : I18n.T("lbl.time_unknown");
                _cachedMetaLabels[s.FullSlotPath] = s.File.Name + "  ·  " + timePart;

                int layer = SaveManagerWindow.ResolveSlotLayerNumber(s);
                if (layer > 0)
                {
                    _cachedLayers[s.FullSlotPath] = layer;
                }
            }

            if (_dateFilter != null && !_dateDedupSet.Contains(_dateFilter))
            {
                _dateFilter = null;
            }
        }

        private void DrawStatusBar()
        {
            switch (_rollback.State)
            {
                case RollbackState.Idle:
                    string err = _rollback.LastError;
                    GUIStyle cardStyle = string.IsNullOrEmpty(err) ? BlackWhiteSkin.CardStyle : BlackWhiteSkin.CardWarnStyle;
                    GUILayout.BeginVertical(cardStyle);
                    GUILayout.Label(
                        string.IsNullOrEmpty(err) ? I18n.T("fmt.status_idle") : I18n.F("fmt.status_idle_with_error", err),
                        BlackWhiteSkin.BodyLabelStyle, GUILayout.MinHeight(38f));
                    GUILayout.EndVertical();
                    break;

                case RollbackState.Counting:
                    GUILayout.BeginVertical(BlackWhiteSkin.CardWarnStyle);
                    GUILayout.BeginHorizontal();
                    int sec = Mathf.CeilToInt(Mathf.Max(0f, _rollback.Remaining));
                    string targetName = _rollback.Target == null
                        ? "?"
                        : (string.IsNullOrEmpty(_rollback.Target.Sidecar.Nickname)
                            ? _rollback.Target.File.Name
                            : _rollback.Target.Sidecar.Nickname);
                    GUILayout.Label(I18n.F("fmt.status_counting", sec, targetName),
                        BlackWhiteSkin.CardTitleStyle, GUILayout.ExpandWidth(true), GUILayout.MinHeight(44f));
                    GUILayout.Space(12f);

                    string cancelLabel = I18n.T("btn.cancel_rollback");
                    float cancelW = BlackWhiteSkin.CalcWidth(BlackWhiteSkin.PrimaryBtnStyle, cancelLabel, 156f, 10f);
                    if (GUILayout.Button(cancelLabel, BlackWhiteSkin.PrimaryBtnStyle,
                        GUILayout.Width(cancelW), GUILayout.Height(44f)))
                    {
                        _rollback.Cancel(I18n.T("msg.user_cancel"));
                        SaveManagerWindow.ClearTextFocus();
                        GUIUtility.ExitGUI();
                    }
                    GUILayout.EndHorizontal();

                    GUILayout.Space(8f);
                    float totalDelay = Mathf.Max(1f, _cfg.RollbackDelaySeconds.Value);
                    float ratio = Mathf.Clamp01(_rollback.Remaining / totalDelay);
                    Rect barRect = GUILayoutUtility.GetRect(100f, 10f, GUILayout.Height(10f), GUILayout.ExpandWidth(true));
                    BlackWhiteSkin.DrawProgressBar(barRect, ratio, warn: true);
                    GUILayout.EndVertical();
                    break;

                case RollbackState.Executing:
                    GUILayout.BeginVertical(BlackWhiteSkin.CardCurrentStyle);
                    GUILayout.Label(I18n.T("status.executing"), BlackWhiteSkin.CardTitleStyle, GUILayout.MinHeight(40f));
                    GUILayout.Space(6f);
                    Rect execBar = GUILayoutUtility.GetRect(100f, 10f, GUILayout.Height(10f), GUILayout.ExpandWidth(true));
                    BlackWhiteSkin.DrawProgressBar(execBar, 1f, warn: false);
                    GUILayout.EndVertical();
                    break;
            }
        }

        private void DrawDateFilterRows()
        {
            const float Gap = 6f;
            const float BtnH = 40f;
            float maxW = Mathf.Max(420f, _layoutRowWidth);

            string filterTitle = I18n.T("lbl.date_filter");
            float labelW = BlackWhiteSkin.CalcWidth(BlackWhiteSkin.MutedLabelStyle, filterTitle, 112f, 6f);

            GUILayout.BeginHorizontal();
            GUILayout.Label(filterTitle, BlackWhiteSkin.MutedLabelStyle,
                GUILayout.Width(labelW), GUILayout.Height(BtnH));
            GUILayout.Space(Gap);

            bool allActive = _dateFilter == null;
            string allRaw = allActive ? I18n.T("btn.filter_all_active") : I18n.T("btn.filter_all");
            string allText = SaveManagerWindow.StripBadgeBrackets(allRaw);
            GUIStyle allStyle = allActive ? BlackWhiteSkin.SegmentActiveStyle : BlackWhiteSkin.SegmentStyle;
            float allW = BlackWhiteSkin.CalcWidth(allStyle, allText, 84f, 12f);

            if (GUILayout.Button(allText, allStyle, GUILayout.Width(allW), GUILayout.Height(BtnH)))
            {
                SaveManagerWindow.ClearTextFocus();
                if (!allActive)
                {
                    _dateFilter = null;
                    GUIUtility.ExitGUI();
                }
            }

            float usedW = labelW + Gap + allW;
            int itemsInRow = 1;

            for (int i = 0; i < _cachedDates.Count; i++)
            {
                string rawDate = _cachedDates[i];
                bool active = _dateFilter == rawDate;
                string displayDate = SaveManagerWindow.FormatDateFolder(rawDate);
                GUIStyle btnStyle = active ? BlackWhiteSkin.SegmentActiveStyle : BlackWhiteSkin.SegmentStyle;
                float btnW = BlackWhiteSkin.CalcWidth(btnStyle, displayDate, 124f, 12f);

                if (usedW + Gap + btnW > maxW && itemsInRow > 0)
                {
                    GUILayout.FlexibleSpace();
                    GUILayout.EndHorizontal();
                    GUILayout.Space(6f);
                    GUILayout.BeginHorizontal();
                    GUILayout.Space(labelW + Gap);
                    usedW = labelW + Gap;
                    itemsInRow = 0;
                }
                else
                {
                    GUILayout.Space(Gap);
                    usedW += Gap;
                }

                if (GUILayout.Button(displayDate, btnStyle, GUILayout.Width(btnW), GUILayout.Height(BtnH)))
                {
                    _dateFilter = active ? null : rawDate;
                    SaveManagerWindow.ClearTextFocus();
                    GUIUtility.ExitGUI();
                }

                usedW += btnW;
                itemsInRow++;
            }

            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        private void DrawSlotRow(SlotInfo slot)
        {
            int layerNum = _cachedLayers.TryGetValue(slot.FullSlotPath, out int resolvedLayer) ? resolvedLayer : 0;
            int biomeIndex = layerNum > 0 ? layerNum - 1 : -1;

            GUILayout.BeginVertical(GUIStyle.none, GUILayout.ExpandWidth(true));
            Rect anchor = GUILayoutUtility.GetRect(0f, 0f, GUIStyle.none, GUILayout.ExpandWidth(true));
            if (Event.current != null && Event.current.type == EventType.Repaint && biomeIndex >= 0 && anchor.width > 8f)
            {
                LayerBannerRenderer.DrawCardBanner(
                    new Rect(anchor.x, anchor.y, anchor.width, RollbackCardHeight),
                    biomeIndex,
                    isCurrentCard: false);
            }

            GUILayout.BeginVertical(BlackWhiteSkin.CardStyle, GUILayout.Height(RollbackCardHeight), GUILayout.ExpandWidth(true));
            GUILayout.BeginHorizontal(GUILayout.ExpandHeight(true));

            GUILayout.BeginVertical(GUILayout.ExpandWidth(true));
            GUILayout.BeginHorizontal();

            if (slot.Sidecar.Pinned)
            {
                DrawBadge(SaveManagerWindow.StripBadgeBrackets(I18n.T("lbl.tag_pinned")), BlackWhiteSkin.BadgePinnedStyle);
            }
            else
            {
                string tagKey = slot.Sidecar.IsAuto ? "lbl.tag_auto" : "lbl.tag_manual";
                DrawBadge(SaveManagerWindow.StripBadgeBrackets(I18n.T(tagKey)), BlackWhiteSkin.BadgeStyle);
            }

            if (layerNum > 0)
            {
                DrawBadge(I18n.F("lbl.layer_badge", layerNum), BlackWhiteSkin.BadgeStyle);
            }

            if (slot.Sidecar.IsMultiplayer)
            {
                DrawBadge(I18n.T("lbl.tag_mp"), BlackWhiteSkin.BadgeStyle);
            }

            string title = string.IsNullOrEmpty(slot.Sidecar.Nickname) ? slot.File.Name : slot.Sidecar.Nickname;
            GUILayout.Label(title, BlackWhiteSkin.CardTitleStyle, GUILayout.ExpandWidth(true), GUILayout.Height(40f));
            GUILayout.EndHorizontal();

            GUILayout.Space(4f);
            string metaLabel = _cachedMetaLabels.TryGetValue(slot.FullSlotPath, out string formatted)
                ? formatted
                : slot.File.Name;
            GUILayout.Label(metaLabel, BlackWhiteSkin.MutedLabelStyle, GUILayout.Height(34f));
            GUILayout.EndVertical();

            GUILayout.Space(12f);
            GUILayout.BeginVertical(GUILayout.ExpandWidth(false));
            GUILayout.FlexibleSpace();
            bool busy = _rollback.State != RollbackState.Idle;
            bool prevEnabled = GUI.enabled;
            GUI.enabled = prevEnabled && !busy;
            string rollbackText = I18n.T("btn.rollback");
            float rollbackW = BlackWhiteSkin.CalcWidth(GUI.skin.button, rollbackText, 128f, 10f);
            if (GUILayout.Button(rollbackText, GUI.skin.button, GUILayout.Width(rollbackW), GUILayout.Height(44f)))
            {
                _rollback.RequestRollback(slot);
                _onSlotMutated?.Invoke();
                SaveManagerWindow.ClearTextFocus();
                GUI.enabled = prevEnabled;
                GUIUtility.ExitGUI();
            }
            GUI.enabled = prevEnabled;
            GUILayout.FlexibleSpace();
            GUILayout.EndVertical();

            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
            GUILayout.EndVertical();
            GUILayout.Space(8f);
        }

        private static void DrawBadge(string text, GUIStyle style)
        {
            float w = BlackWhiteSkin.CalcWidth(style, text, 0f, 4f);
            GUILayout.Label(text, style, GUILayout.Width(w), GUILayout.Height(36f));
        }
    }
}
