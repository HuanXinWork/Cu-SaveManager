using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace CasualtiesUnknown.SaveManager
{
    internal sealed partial class SaveManagerWindow
    {
        // 视口裁剪与实际绘制共享同一组零外边距高度常量，消除长列表滚动漂移。
        private const float ViewportCullBuffer = 480f;
        private const float CurrentSaveCardHeight = 112f, CurrentSaveCardGap = 10f, CurrentSaveCardStep = CurrentSaveCardHeight + CurrentSaveCardGap;
        private const float RunHeaderTopSpace = 6f, RunHeaderHeight = 50f, RunHeaderBottomSpace = 4f, RunHeaderStep = RunHeaderTopSpace + RunHeaderHeight + RunHeaderBottomSpace;
        private const float DateHeaderTopSpace = 2f, DateHeaderHeight = 48f, DateHeaderBottomSpace = 4f, DateHeaderStep = DateHeaderTopSpace + DateHeaderHeight + DateHeaderBottomSpace;
        private const float SlotCardHeight = 152f, SlotCardGap = 8f, SlotCardStep = SlotCardHeight + SlotCardGap;

        private sealed class CachedSlotEntry
        {
            internal SlotInfo Slot;
            internal bool IsCurrent;
            internal int LayerNumber;
            internal string CompactMetaLine;
            internal bool DrawInCurrentFrame = true;
        }

        private sealed class CachedDateGroup
        {
            internal string DateFolder, DateKey;
            internal readonly List<CachedSlotEntry> Entries = new List<CachedSlotEntry>();
        }

        private sealed class CachedRunGroup
        {
            internal string RunIdFolder;
            internal int TotalCount;
            internal readonly List<CachedDateGroup> DateGroups = new List<CachedDateGroup>();
        }

        private Vector2 _scroll;
        private float _lastSlotsViewportH = 680f;

        private readonly Dictionary<string, bool> _dateExpanded = new Dictionary<string, bool>(StringComparer.Ordinal);
        private readonly Dictionary<string, bool> _runIdExpanded = new Dictionary<string, bool>(StringComparer.Ordinal);

        private string _nicknameEditingPath, _editingNicknameText;
        private bool _focusNicknameNextFrame;
        private string _pendingLoadPath, _pendingDeletePath;
        private float _pendingLoadExpireAt, _pendingDeleteExpireAt;
        private bool _pendingCleanAutos;
        private float _pendingCleanAutosExpireAt;

        private string _searchInputText = "", _activeSearchFilter = "", _cachedSaveHash;
        private bool _slotCacheDirty = true, _slotFilterDirty;

        private List<SlotInfo> _cachedSlots;
        private readonly List<CachedRunGroup> _cachedRunGroups = new List<CachedRunGroup>();
        private int _cachedRunId, _cachedStaleAutoCount, _filteredSlotCount;
        private int _cachedCurrentSaveBiome = -1;

        internal void InvalidateSlotCache() => _slotCacheDirty = true;

        private void ResetTransientEditState()
        {
            _nicknameEditingPath = null;
            _editingNicknameText = null;
            _focusNicknameNextFrame = false;
            _pendingLoadPath = null;
            _pendingLoadExpireAt = 0f;
            _pendingDeletePath = null;
            _pendingDeleteExpireAt = 0f;
            _pendingCleanAutos = false;
            _pendingCleanAutosExpireAt = 0f;
        }

        private void CancelNicknameEdit()
        {
            _nicknameEditingPath = null;
            _editingNicknameText = null;
            _focusNicknameNextFrame = false;
            ClearTextFocus();
            ImGuiImeRecovery.RequestClear();
        }

        private void CommitNicknameEdit(SlotInfo slot)
        {
            if (slot != null)
            {
                slot.Sidecar.Nickname = _editingNicknameText ?? "";
                ActiveStore?.UpdateSidecar(slot.FullSlotPath, slot.Sidecar);
                InvalidateSlotCache();
            }
            CancelNicknameEdit();
        }

        private void ExpireSlotConfirmationsIfNeeded(float now)
        {
            if (_pendingLoadPath != null && now >= _pendingLoadExpireAt) _pendingLoadPath = null;
            if (_pendingDeletePath != null && now >= _pendingDeleteExpireAt) _pendingDeletePath = null;
            if (_pendingCleanAutos && now >= _pendingCleanAutosExpireAt) _pendingCleanAutos = false;
        }

        private void RefreshSlotCacheIfNeeded()
        {
            if (_sideIdx != 0 || (_tab != 1 && _tab != 2)) return;
            var ev = Event.current;
            bool isLayout = ev != null && ev.type == EventType.Layout;

            if (_cachedSlots == null)
            {
                RebuildSlotCache();
                return;
            }
            // 非空缓存在脏标记或筛选词更新时严格等 Layout 阶段再重建，确保同帧 Layout 与 Repaint 控件树一致。
            if (!isLayout) return;

            if (_slotCacheDirty) RebuildSlotCache();
            else if (_slotFilterDirty) RebuildFilteredGroups();
        }

        private void RebuildSlotCache()
        {
            SaveStore store = ActiveStore;
            _cachedSlots = store != null ? store.ListSlots() : new List<SlotInfo>();
            _cachedSaveHash = SaveStore.ComputeCurrentSaveHash();
            _cachedRunId = SaveStore.ComputeCurrentRunId();
            _cachedStaleAutoCount = store != null ? store.CountStaleAutoBackups(_cachedRunId) : 0;
            _cachedCurrentSaveBiome = LayerBannerRenderer.ResolveActiveSaveBiomeIndex(_cachedSlots, _cachedSaveHash);
            _slotCacheDirty = false;
            RebuildFilteredGroups();
        }

        private void RebuildFilteredGroups()
        {
            _activeSearchFilter = (_searchInputText ?? "").Trim();
            _slotFilterDirty = false;
            _cachedRunGroups.Clear();
            _filteredSlotCount = 0;
            if (_cachedSlots == null) return;

            bool hasQuery = !string.IsNullOrEmpty(_activeSearchFilter);
            var runMap = new Dictionary<string, CachedRunGroup>(StringComparer.Ordinal);
            var dateMap = new Dictionary<string, CachedDateGroup>(StringComparer.Ordinal);

            for (int i = 0; i < _cachedSlots.Count; i++)
            {
                SlotInfo s = _cachedSlots[i];
                if (hasQuery && !MatchesSearch(s, _activeSearchFilter)) continue;
                _filteredSlotCount++;

                if (!runMap.TryGetValue(s.RunIdFolder, out CachedRunGroup runGroup))
                {
                    runGroup = new CachedRunGroup { RunIdFolder = s.RunIdFolder };
                    runMap[s.RunIdFolder] = runGroup;
                    _cachedRunGroups.Add(runGroup);
                    if (!_runIdExpanded.ContainsKey(s.RunIdFolder)) _runIdExpanded[s.RunIdFolder] = true;
                }
                runGroup.TotalCount++;

                string dateKey = s.RunIdFolder + "/" + s.DateFolder;
                if (!dateMap.TryGetValue(dateKey, out CachedDateGroup dateGroup))
                {
                    dateGroup = new CachedDateGroup { DateFolder = s.DateFolder, DateKey = dateKey };
                    dateMap[dateKey] = dateGroup;
                    runGroup.DateGroups.Add(dateGroup);
                    if (!_dateExpanded.ContainsKey(dateKey)) _dateExpanded[dateKey] = true;
                }

                dateGroup.Entries.Add(new CachedSlotEntry
                {
                    Slot = s,
                    IsCurrent = SaveStore.IsSlotMatchingCurrentSave(s, _cachedSaveHash),
                    LayerNumber = ResolveSlotLayerNumber(s),
                    CompactMetaLine = BuildCompactSlotMeta(s),
                });
            }
        }

        /// <summary>去除与父级日期分组重复的日期前缀及重复修改时间，避免槽位元数据行过长被裁切。</summary>
        internal static string BuildCompactSlotMeta(SlotInfo s)
        {
            string sizeLabel = (s.File.Length / 1024f).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
            long unixMs = s.Sidecar.LastPlayedAtUnixMs;
            if (unixMs > 0L)
            {
                DateTime playedLocal = DateTimeOffset.FromUnixTimeMilliseconds(unixMs).LocalDateTime;
                string playedDate = playedLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                string timePart = string.Equals(playedDate, s.DateFolder, StringComparison.Ordinal)
                    ? playedLocal.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                    : playedLocal.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                return I18n.F("fmt.last_played", timePart) + "  ·  " + sizeLabel;
            }

            DateTime writeLocal = s.File.LastWriteTime;
            string writeDate = writeLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string writeTime = string.Equals(writeDate, s.DateFolder, StringComparison.Ordinal)
                ? writeLocal.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                : writeLocal.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            return writeTime + "  ·  " + sizeLabel;
        }

        private static bool MatchesSearch(SlotInfo s, string query)
        {
            if (!string.IsNullOrEmpty(s.Sidecar.Nickname)
                && s.Sidecar.Nickname.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (s.File.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return !string.IsNullOrEmpty(s.RunIdFolder)
                && s.RunIdFolder.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private (List<SlotInfo> slots, int currentRunId) GetRollbackSnapshot()
        {
            if (_cachedSlots == null) RebuildSlotCache();
            return (_cachedSlots, _cachedRunId);
        }

        private void SetAllGroupsExpanded(bool expanded)
        {
            for (int i = 0; i < _cachedRunGroups.Count; i++)
            {
                CachedRunGroup rg = _cachedRunGroups[i];
                _runIdExpanded[rg.RunIdFolder] = expanded;
                for (int j = 0; j < rg.DateGroups.Count; j++)
                    _dateExpanded[rg.DateGroups[j].DateKey] = expanded;
            }
        }

        private void DrawSlotsTab()
        {
            if (_cachedSlots == null) RebuildSlotCache();
            int totalSlots = _cachedSlots.Count;

            DrawSlotsToolbar(totalSlots);
            GUILayout.Space(8f);

            var ev = Event.current;
            if (ev != null && ev.type == EventType.Layout)
            {
                UpdateSlotViewportCulling();
            }

            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            try
            {
                DrawCurrentSaveCard(_cachedSaveHash, _cachedRunId, _cachedCurrentSaveBiome);

                for (int i = 0; i < _cachedRunGroups.Count; i++)
                {
                    CachedRunGroup runGroup = _cachedRunGroups[i];
                    DrawRunIdHeader(runGroup.RunIdFolder, runGroup.TotalCount);
                    if (!_runIdExpanded[runGroup.RunIdFolder]) continue;

                    for (int j = 0; j < runGroup.DateGroups.Count; j++)
                    {
                        CachedDateGroup dateGroup = runGroup.DateGroups[j];
                        DrawDateHeader(dateGroup.DateFolder, dateGroup.DateKey, dateGroup.Entries.Count);
                        if (!_dateExpanded[dateGroup.DateKey]) continue;

                        int skipped = 0;
                        for (int k = 0; k < dateGroup.Entries.Count; k++)
                        {
                            CachedSlotEntry entry = dateGroup.Entries[k];
                            if (!entry.DrawInCurrentFrame)
                            {
                                skipped++;
                                continue;
                            }
                            if (skipped > 0)
                            {
                                GUILayout.Space(skipped * SlotCardStep);
                                skipped = 0;
                            }
                            DrawSlotCard(entry);
                        }
                        if (skipped > 0)
                        {
                            GUILayout.Space(skipped * SlotCardStep);
                        }
                    }
                }

                if (totalSlots == 0)
                {
                    GUILayout.Space(20f);
                    GUILayout.Label(I18n.T("msg.no_slots").TrimStart(), BlackWhiteSkin.BodyLabelStyle, GUILayout.MinHeight(38f));
                }
                else if (_filteredSlotCount == 0)
                {
                    GUILayout.Space(20f);
                    GUILayout.Label(I18n.T("msg.no_rollback_targets_filtered").TrimStart(), BlackWhiteSkin.BodyLabelStyle, GUILayout.MinHeight(38f));
                }
            }
            finally
            {
                GUILayout.EndScrollView();
            }

            if (ev != null && ev.type == EventType.Repaint)
            {
                Rect scrollRect = GUILayoutUtility.GetLastRect();
                if (scrollRect.height > 120f) _lastSlotsViewportH = scrollRect.height;
            }
        }

        private void DrawSlotsToolbar(int totalSlots)
        {
            const float BarH = 44f;

            int badgeCount = string.IsNullOrEmpty(_activeSearchFilter) ? totalSlots : _filteredSlotCount;
            string totalText = I18n.F("fmt.total_slots", badgeCount);
            float totalW = BlackWhiteSkin.CalcWidth(BlackWhiteSkin.BadgeStyle, totalText, 96f, 8f);

            string refreshText = I18n.T("btn.refresh");
            float refreshW = BlackWhiteSkin.CalcWidth(BlackWhiteSkin.SubtleBtnStyle, refreshText, 88f, 10f);
            string expandText = I18n.T("btn.expand_all");
            float expandW = BlackWhiteSkin.CalcWidth(BlackWhiteSkin.SubtleBtnStyle, expandText, 100f, 10f);
            string collapseText = I18n.T("btn.collapse_all");
            float collapseW = BlackWhiteSkin.CalcWidth(BlackWhiteSkin.SubtleBtnStyle, collapseText, 100f, 10f);
            float rightGroupW = refreshW + 6f + expandW + 6f + collapseW;

            bool hasStale = _cachedStaleAutoCount > 0;
            bool confirmingClean = hasStale && _pendingCleanAutos && Time.unscaledTime < _pendingCleanAutosExpireAt;
            float staleGroupW = 0f;
            if (hasStale)
            {
                if (!confirmingClean)
                {
                    staleGroupW = BlackWhiteSkin.CalcWidth(GUI.skin.button, I18n.F("btn.clean_old_autos", _cachedStaleAutoCount), 144f, 10f);
                }
                else
                {
                    float cfmW = BlackWhiteSkin.CalcWidth(BlackWhiteSkin.PrimaryBtnStyle, I18n.F("btn.confirm_clean_autos", _cachedStaleAutoCount), 132f, 10f);
                    float cnlW = BlackWhiteSkin.CalcWidth(GUI.skin.button, I18n.T("btn.cancel"), 88f, 10f);
                    staleGroupW = cfmW + 6f + cnlW;
                }
            }

            float clearSearchW = string.IsNullOrEmpty(_searchInputText) ? 0f : 48f;
            float baseRowNeeded = totalW + 6f + 180f + clearSearchW + 12f + rightGroupW;
            // 130% 窄窗口叠加英文长按钮超出单行容量时，自动将清理旧局备份组折至次行，防止右边缘裁切。
            bool wrapStale = hasStale && (baseRowNeeded + 8f + staleGroupW > _currentMainWidth);

            GUILayout.BeginHorizontal();
            GUILayout.Label(totalText, BlackWhiteSkin.BadgeStyle, GUILayout.Width(totalW), GUILayout.Height(BarH));
            GUILayout.Space(6f);

            string nextSearch = GUILayout.TextField(_searchInputText ?? "",
                GUILayout.MinWidth(180f), GUILayout.MaxWidth(320f), GUILayout.ExpandWidth(true), GUILayout.Height(BarH));
            Rect searchRect = GUILayoutUtility.GetLastRect();
            // 仅在输入文本与输入法正在组字的拼音串均为空时绘制占位提示，避免与 IME 预编辑重叠。
            if (string.IsNullOrEmpty(nextSearch) && string.IsNullOrEmpty(Input.compositionString))
            {
                GUI.Label(new Rect(searchRect.x + 12f, searchRect.y, Mathf.Max(20f, searchRect.width - 20f), searchRect.height),
                    I18n.T("ph.search_slots"), BlackWhiteSkin.MutedLabelStyle);
            }
            if (!string.Equals(nextSearch, _searchInputText, StringComparison.Ordinal))
            {
                _searchInputText = nextSearch;
                _slotFilterDirty = true;
            }

            if (!string.IsNullOrEmpty(_searchInputText))
            {
                GUILayout.Space(4f);
                if (GUILayout.Button("×", BlackWhiteSkin.IconBtnStyle, GUILayout.Width(44f), GUILayout.Height(BarH)))
                {
                    _searchInputText = "";
                    _slotFilterDirty = true;
                    ClearTextFocus();
                    GUIUtility.ExitGUI();
                }
            }

            if (hasStale && !wrapStale)
            {
                GUILayout.Space(8f);
                DrawStaleAutoCleanControls(BarH, confirmingClean);
            }

            GUILayout.FlexibleSpace();
            GUILayout.Space(8f);

            if (GUILayout.Button(refreshText, BlackWhiteSkin.SubtleBtnStyle, GUILayout.Width(refreshW), GUILayout.Height(BarH)))
            {
                ResetTransientEditState();
                InvalidateSlotCache();
                ClearTextFocus();
                GUIUtility.ExitGUI();
            }
            GUILayout.Space(6f);

            if (GUILayout.Button(expandText, BlackWhiteSkin.SubtleBtnStyle, GUILayout.Width(expandW), GUILayout.Height(BarH)))
            {
                SetAllGroupsExpanded(true);
                ClearTextFocus();
                GUIUtility.ExitGUI();
            }
            GUILayout.Space(6f);

            if (GUILayout.Button(collapseText, BlackWhiteSkin.SubtleBtnStyle, GUILayout.Width(collapseW), GUILayout.Height(BarH)))
            {
                SetAllGroupsExpanded(false);
                ClearTextFocus();
                GUIUtility.ExitGUI();
            }
            GUILayout.EndHorizontal();

            if (wrapStale)
            {
                GUILayout.Space(6f);
                GUILayout.BeginHorizontal();
                DrawStaleAutoCleanControls(BarH, confirmingClean);
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
            }
        }

        private void DrawStaleAutoCleanControls(float barH, bool confirmingClean)
        {
            if (!confirmingClean)
            {
                string cleanLabel = I18n.F("btn.clean_old_autos", _cachedStaleAutoCount);
                float cleanW = BlackWhiteSkin.CalcWidth(GUI.skin.button, cleanLabel, 144f, 10f);
                if (GUILayout.Button(cleanLabel, GUI.skin.button, GUILayout.Width(cleanW), GUILayout.Height(barH)))
                {
                    _pendingCleanAutos = true;
                    _pendingCleanAutosExpireAt = Time.unscaledTime + ConfirmWindowSeconds;
                    ClearTextFocus();
                    GUIUtility.ExitGUI();
                }
                return;
            }

            string confirmLabel = I18n.F("btn.confirm_clean_autos", _cachedStaleAutoCount);
            float confirmW = BlackWhiteSkin.CalcWidth(BlackWhiteSkin.PrimaryBtnStyle, confirmLabel, 132f, 10f);
            if (GUILayout.Button(confirmLabel, BlackWhiteSkin.PrimaryBtnStyle, GUILayout.Width(confirmW), GUILayout.Height(barH)))
            {
                _pendingCleanAutos = false;
                int cleaned = ActiveStore != null ? ActiveStore.PruneStaleAutoBackups(_cachedRunId) : 0;
                InvalidateSlotCache();
                ShowTransientTip(I18n.F("fmt.cleaned_old_autos", cleaned));
                ClearTextFocus();
                GUIUtility.ExitGUI();
            }
            GUILayout.Space(6f);
            string cancelLabel = I18n.T("btn.cancel");
            float cancelW = BlackWhiteSkin.CalcWidth(GUI.skin.button, cancelLabel, 88f, 10f);
            if (GUILayout.Button(cancelLabel, GUI.skin.button, GUILayout.Width(cancelW), GUILayout.Height(barH)))
            {
                _pendingCleanAutos = false;
                ClearTextFocus();
                GUIUtility.ExitGUI();
            }
        }

        /// <summary>在 Layout 阶段按与绘制完全一致的零外边距步进常量锁定可见卡片集合，防止长列表偏移与控件数失配。</summary>
        private void UpdateSlotViewportCulling()
        {
            float y = CurrentSaveCardStep;
            float viewMin = _scroll.y - ViewportCullBuffer;
            float viewMax = _scroll.y + _lastSlotsViewportH + ViewportCullBuffer;

            for (int i = 0; i < _cachedRunGroups.Count; i++)
            {
                CachedRunGroup rg = _cachedRunGroups[i];
                y += RunHeaderStep;
                if (!_runIdExpanded[rg.RunIdFolder]) continue;

                for (int j = 0; j < rg.DateGroups.Count; j++)
                {
                    CachedDateGroup dg = rg.DateGroups[j];
                    y += DateHeaderStep;
                    if (!_dateExpanded[dg.DateKey]) continue;

                    for (int k = 0; k < dg.Entries.Count; k++)
                    {
                        CachedSlotEntry entry = dg.Entries[k];
                        float top = y;
                        float bottom = y + SlotCardStep;
                        y = bottom;

                        bool inView = bottom >= viewMin && top <= viewMax;
                        bool editing = !string.IsNullOrEmpty(_nicknameEditingPath)
                            && string.Equals(entry.Slot.FullSlotPath, _nicknameEditingPath, StringComparison.Ordinal);
                        entry.DrawInCurrentFrame = inView || editing;
                    }
                }
            }
        }

        private void DrawRunIdHeader(string runIdFolder, int count)
        {
            GUILayout.Space(RunHeaderTopSpace);
            string prefix = _runIdExpanded[runIdFolder] ? "▼  " : "▶  ";
            string label = runIdFolder == "0"
                ? prefix + I18n.F("fmt.adventure_unknown", count)
                : prefix + I18n.F("fmt.adventure_named", runIdFolder, count);
            if (GUILayout.Button(label, BlackWhiteSkin.HeaderButtonStyle, GUILayout.ExpandWidth(true), GUILayout.Height(RunHeaderHeight)))
            {
                _runIdExpanded[runIdFolder] = !_runIdExpanded[runIdFolder];
                ClearTextFocus();
                GUIUtility.ExitGUI();
            }
            GUILayout.Space(RunHeaderBottomSpace);
        }

        private void DrawDateHeader(string dateFolder, string dateKey, int count)
        {
            GUILayout.Space(DateHeaderTopSpace);
            GUILayout.BeginHorizontal();
            Rect guideRect = GUILayoutUtility.GetRect(16f, DateHeaderHeight, GUILayout.Width(16f), GUILayout.ExpandWidth(false));
            BlackWhiteSkin.DrawSoftVLine(new Rect(guideRect.x + 5f, guideRect.y, 2f, guideRect.height));

            string prefix = _dateExpanded[dateKey] ? "▼  " : "▶  ";
            string displayDate = FormatDateFolder(dateFolder);
            if (GUILayout.Button(prefix + I18n.F("fmt.date_count", displayDate, count),
                BlackWhiteSkin.HeaderButtonStyle, GUILayout.ExpandWidth(true), GUILayout.Height(DateHeaderHeight)))
            {
                _dateExpanded[dateKey] = !_dateExpanded[dateKey];
                ClearTextFocus();
                GUIUtility.ExitGUI();
            }
            GUILayout.EndHorizontal();
            GUILayout.Space(DateHeaderBottomSpace);
        }
    }
}
