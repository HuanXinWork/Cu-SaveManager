using System;
using System.Collections.Generic;
using UnityEngine;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>回档分页视图：展示回档倒计时状态、日期筛选与最近可回档槽位列表。</summary>
    internal sealed class RollbackTabView
    {
        private readonly HotkeyConfig _cfg;
        private readonly SaveStore _store;
        private readonly RollbackController _rollback;
        private readonly Func<(List<SlotInfo> slots, int currentRunId)> _snapshotProvider;
        private readonly Action _onSlotMutated;

        private Vector2 _scroll;
        private string _dateFilter;

        internal RollbackTabView(
            HotkeyConfig cfg,
            SaveStore store,
            RollbackController rollback,
            Func<(List<SlotInfo> slots, int currentRunId)> snapshotProvider = null,
            Action onSlotMutated = null)
        {
            _cfg = cfg;
            _store = store;
            _rollback = rollback;
            _snapshotProvider = snapshotProvider;
            _onSlotMutated = onSlotMutated;
        }

        internal void Draw()
        {
            var (slots, currentRunId) = _snapshotProvider != null
                ? _snapshotProvider()
                : (_store.ListSlots(), SaveStore.ComputeCurrentRunId());

            DrawStatusBar();

            GUILayout.Space(8f);
            DrawDateFilterRow(slots, currentRunId);
            GUILayout.Space(6f);

            _scroll = GUILayout.BeginScrollView(_scroll,
                GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));

            int limit = Mathf.Max(1, _cfg.RecentSlotsLimit.Value);
            if (currentRunId == 0)
            {
                GUILayout.Label(I18n.T("msg.run_id_unresolved_mixed"), BlackWhiteSkin.CardStyle);
                GUILayout.Space(4f);
            }

            int shown = 0;
            foreach (var slot in slots)
            {
                if (shown >= limit) break;
                if (currentRunId != 0 && slot.Sidecar.RunId != currentRunId) continue;
                if (_dateFilter != null && slot.DateFolder != _dateFilter) continue;
                if (slot.File.Name.StartsWith("beforeLoad-", StringComparison.Ordinal)) continue;
                DrawSlotRow(slot);
                shown++;
            }
            if (shown == 0)
            {
                GUILayout.Space(20f);
                GUILayout.Label(_dateFilter == null
                    ? I18n.T("msg.no_rollback_targets")
                    : I18n.T("msg.no_rollback_targets_filtered"));
            }
            GUILayout.EndScrollView();
        }

        private void DrawStatusBar()
        {
            GUILayout.BeginVertical(BlackWhiteSkin.CardStyle);
            switch (_rollback.State)
            {
                case RollbackState.Idle:
                    string err = _rollback.LastError;
                    GUILayout.Label(string.IsNullOrEmpty(err)
                        ? I18n.T("fmt.status_idle")
                        : I18n.F("fmt.status_idle_with_error", err),
                        BlackWhiteSkin.HeaderStyle, GUILayout.MinHeight(48f));
                    break;
                case RollbackState.Counting:
                    int sec = Mathf.CeilToInt(Mathf.Max(0f, _rollback.Remaining));
                    string targetName = _rollback.Target == null ? "?" :
                        (string.IsNullOrEmpty(_rollback.Target.Sidecar.Nickname)
                            ? _rollback.Target.File.Name : _rollback.Target.Sidecar.Nickname);
                    GUILayout.Label(I18n.F("fmt.status_counting", sec, targetName),
                        BlackWhiteSkin.HeaderStyle, GUILayout.MinHeight(48f));
                    GUILayout.Space(6f);
                    if (GUILayout.Button(I18n.T("btn.cancel_rollback"),
                        GUILayout.MinWidth(220f), GUILayout.ExpandWidth(false), GUILayout.MinHeight(48f)))
                    {
                        _rollback.Cancel(I18n.T("msg.user_cancel"));
                        GUIUtility.ExitGUI();
                    }
                    break;
                case RollbackState.Executing:
                    GUILayout.Label(I18n.T("status.executing"),
                        BlackWhiteSkin.HeaderStyle, GUILayout.MinHeight(48f));
                    break;
            }
            GUILayout.EndVertical();
        }

        private void DrawDateFilterRow(List<SlotInfo> slots, int currentRunId)
        {
            var dates = new List<string>();
            foreach (var s in slots)
            {
                if (currentRunId != 0 && s.Sidecar.RunId != currentRunId) continue;
                if (!dates.Contains(s.DateFolder)) dates.Add(s.DateFolder);
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label(I18n.T("lbl.date_filter"), GUILayout.MinWidth(140f), GUILayout.ExpandWidth(false), GUILayout.MinHeight(36f));
            if (GUILayout.Button(_dateFilter == null ? I18n.T("btn.filter_all_active") : I18n.T("btn.filter_all"),
                _dateFilter == null ? BlackWhiteSkin.TabActiveStyle : BlackWhiteSkin.TabStyle,
                GUILayout.MinWidth(120f), GUILayout.ExpandWidth(false), GUILayout.MinHeight(36f)))
            {
                if (_dateFilter != null)
                {
                    _dateFilter = null;
                    GUIUtility.ExitGUI();
                }
            }
            GUILayout.Space(8f);
            foreach (var d in dates)
            {
                string label = _dateFilter == d ? "[" + d + "]" : d;
                var style = _dateFilter == d ? BlackWhiteSkin.TabActiveStyle : BlackWhiteSkin.TabStyle;
                if (GUILayout.Button(label, style,
                    GUILayout.MinWidth(160f), GUILayout.ExpandWidth(false), GUILayout.MinHeight(36f)))
                {
                    _dateFilter = _dateFilter == d ? null : d;
                    GUIUtility.ExitGUI();
                }
                GUILayout.Space(6f);
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        private void DrawSlotRow(SlotInfo slot)
        {
            GUILayout.BeginVertical(BlackWhiteSkin.CardStyle);
            GUILayout.BeginHorizontal();

            string tag = slot.Sidecar.IsAuto ? I18n.T("lbl.tag_auto") : I18n.T("lbl.tag_manual");
            GUILayout.Label(tag, GUILayout.MinWidth(80f), GUILayout.ExpandWidth(false), GUILayout.MinHeight(36f));

            string title = string.IsNullOrEmpty(slot.Sidecar.Nickname)
                ? slot.File.Name : slot.Sidecar.Nickname;
            GUILayout.Label(title, BlackWhiteSkin.HeaderStyle,
                GUILayout.MinWidth(360f), GUILayout.ExpandWidth(true), GUILayout.MinHeight(36f));

            GUILayout.Label(FormatSysTime(slot.Sidecar.LastPlayedAtUnixMs),
                GUILayout.MinWidth(220f), GUILayout.ExpandWidth(false), GUILayout.MinHeight(36f));

            bool busy = _rollback.State != RollbackState.Idle;
            GUI.enabled = !busy;
            if (GUILayout.Button(I18n.T("btn.rollback"),
                GUILayout.MinWidth(120f), GUILayout.ExpandWidth(false), GUILayout.MinHeight(40f)))
            {
                _rollback.RequestRollback(slot);
                _onSlotMutated?.Invoke();
                GUIUtility.ExitGUI();
            }
            GUI.enabled = true;

            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
            GUILayout.Space(6f);
        }

        private static string FormatSysTime(long unixMs)
        {
            if (unixMs <= 0L) return I18n.T("lbl.time_unknown");
            return DateTimeOffset.FromUnixTimeMilliseconds(unixMs).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
        }
    }
}
