using System;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace CasualtiesUnknown.SaveManager
{
    internal sealed partial class SaveManagerWindow
    {
        private const string NicknameControlName = "CuSaveManager_NicknameInput";

        private static void DrawCurrentSaveCard(string currentHash, int runId, int cachedBiomeIndex)
        {
            int activeBiome = WorldGeneration.world != null
                ? WorldGeneration.world.biomeDepth
                : cachedBiomeIndex;

            GUILayout.BeginVertical(GUIStyle.none, GUILayout.ExpandWidth(true));
            Rect anchor = GUILayoutUtility.GetRect(0f, 0f, GUIStyle.none, GUILayout.ExpandWidth(true));
            if (Event.current != null && Event.current.type == EventType.Repaint && activeBiome >= 0 && anchor.width > 8f)
            {
                LayerBannerRenderer.DrawCardBanner(
                    new Rect(anchor.x, anchor.y, anchor.width, CurrentSaveCardHeight),
                    activeBiome,
                    isCurrentCard: true);
            }

            GUILayout.BeginVertical(BlackWhiteSkin.CardCurrentStyle, GUILayout.Height(CurrentSaveCardHeight), GUILayout.ExpandWidth(true));
            GUILayout.BeginHorizontal();
            DrawSlotBadge(StripBadgeBrackets(I18n.T("lbl.current_save")), BlackWhiteSkin.BadgeHighlightStyle);
            bool mpActive = SaveStore.IsMultiplayerContextActive();
            DrawSlotBadge(mpActive ? I18n.T("mode.multiplayer") : I18n.T("mode.singleplayer"), BlackWhiteSkin.BadgeStyle);
            if (activeBiome >= 0)
            {
                DrawSlotBadge(I18n.F("lbl.layer_badge", activeBiome + 1), BlackWhiteSkin.BadgeStyle);
            }
            string displayPath = CompactSinglePathKeepDriveAndTail2(SaveStore.CurrentSaveDisplayPath);
            GUILayout.Label(displayPath, BlackWhiteSkin.CardTitleStyle, GUILayout.ExpandWidth(true), GUILayout.Height(40f));
            GUILayout.EndHorizontal();

            string sub;
            if (mpActive)
                sub = runId != 0 ? I18n.F("fmt.run_id", runId) : I18n.T("mp.current_save_hint");
            else if (currentHash != null)
                sub = runId == 0 ? I18n.T("msg.run_id_unknown") : I18n.F("fmt.run_id", runId);
            else if (WorldGeneration.world != null)
                sub = runId != 0 ? I18n.F("fmt.run_id_in_memory", runId) : I18n.T("msg.progress_in_memory");
            else if (SaveSystem.loadedRun && File.Exists(SlotSidecar.SidecarPath(SaveStore.GameSavePath)))
                sub = runId != 0 ? I18n.F("fmt.run_id_after_load", runId) : I18n.T("msg.after_load_not_saved");
            else
                sub = I18n.T("msg.no_save_yet");

            GUILayout.Space(8f);
            GUILayout.Label(sub.TrimStart(), BlackWhiteSkin.MutedLabelStyle, GUILayout.Height(36f));
            GUILayout.EndVertical();
            GUILayout.EndVertical();
            GUILayout.Space(CurrentSaveCardGap);
        }

        private void DrawSlotCard(CachedSlotEntry item)
        {
            SlotInfo slot = item.Slot;
            bool isCurrent = item.IsCurrent;
            string key = slot.FullSlotPath;
            int biomeIndex = item.LayerNumber - 1;

            GUILayout.BeginHorizontal();
            Rect guideRect = GUILayoutUtility.GetRect(28f, SlotCardHeight, GUILayout.Width(28f), GUILayout.ExpandWidth(false));
            BlackWhiteSkin.DrawSoftVLine(new Rect(guideRect.x + 5f, guideRect.y, 2f, guideRect.height + SlotCardGap));
            BlackWhiteSkin.DrawSoftVLine(new Rect(guideRect.x + 19f, guideRect.y, 2f, guideRect.height));

            // 0px 锚点零边距测量卡片准确坐标，确保叠加层级底图后仍严格保持 SlotCardHeight 步进不漂移。
            GUILayout.BeginVertical(GUIStyle.none, GUILayout.ExpandWidth(true));
            Rect anchor = GUILayoutUtility.GetRect(0f, 0f, GUIStyle.none, GUILayout.ExpandWidth(true));
            if (Event.current != null && Event.current.type == EventType.Repaint && biomeIndex >= 0 && anchor.width > 8f)
            {
                LayerBannerRenderer.DrawCardBanner(
                    new Rect(anchor.x, anchor.y, anchor.width, SlotCardHeight),
                    biomeIndex,
                    isCurrentCard: isCurrent);
            }

            GUIStyle cardStyle = isCurrent ? BlackWhiteSkin.CardCurrentStyle : BlackWhiteSkin.CardStyle;
            GUILayout.BeginVertical(cardStyle, GUILayout.Height(SlotCardHeight), GUILayout.ExpandWidth(true));
            GUILayout.BeginHorizontal(GUILayout.ExpandHeight(true));

            GUILayout.BeginVertical(GUILayout.ExpandWidth(true));

            GUILayout.BeginHorizontal();
            if (slot.Sidecar.Pinned)
                DrawSlotBadge(StripBadgeBrackets(I18n.T("lbl.tag_pinned")), BlackWhiteSkin.BadgePinnedStyle);
            else
                DrawSlotBadge(StripBadgeBrackets(I18n.T(slot.Sidecar.IsAuto ? "lbl.tag_auto" : "lbl.tag_manual")), BlackWhiteSkin.BadgeStyle);

            if (item.LayerNumber > 0)
                DrawSlotBadge(I18n.F("lbl.layer_badge", item.LayerNumber), BlackWhiteSkin.BadgeStyle);

            if (slot.Sidecar.IsMultiplayer)
                DrawSlotBadge(I18n.T("lbl.tag_mp"), BlackWhiteSkin.BadgeStyle);

            if (isCurrent)
                DrawSlotBadge(StripBadgeBrackets(I18n.T("lbl.is_current_save")), BlackWhiteSkin.BadgeHighlightStyle);

            GUILayout.Space(4f);
            GUILayout.Label(item.CompactMetaLine, BlackWhiteSkin.MutedLabelStyle, GUILayout.ExpandWidth(true), GUILayout.Height(36f));
            GUILayout.EndHorizontal();

            GUILayout.Space(6f);

            GUILayout.BeginHorizontal();
            if (_nicknameEditingPath == key)
            {
                HandleNicknameKeyboardShortcuts(slot);

                GUI.SetNextControlName(NicknameControlName);
                _editingNicknameText = GUILayout.TextField(_editingNicknameText ?? "",
                    GUILayout.MinWidth(200f), GUILayout.ExpandWidth(true), GUILayout.Height(44f));

                if (_focusNicknameNextFrame)
                {
                    GUI.FocusControl(NicknameControlName);
                    if (Event.current != null && Event.current.type == EventType.Repaint
                        && GUI.GetNameOfFocusedControl() == NicknameControlName)
                    {
                        _focusNicknameNextFrame = false;
                    }
                }

                GUILayout.Space(6f);
                if (GUILayout.Button("✓", BlackWhiteSkin.IconPrimaryBtnStyle, GUILayout.Width(44f), GUILayout.Height(44f)))
                {
                    CommitNicknameEdit(slot);
                    GUIUtility.ExitGUI();
                }
                GUILayout.Space(6f);
                if (GUILayout.Button("×", BlackWhiteSkin.IconBtnStyle, GUILayout.Width(44f), GUILayout.Height(44f)))
                {
                    CancelNicknameEdit();
                    GUIUtility.ExitGUI();
                }
            }
            else
            {
                bool hasAlias = !string.IsNullOrEmpty(slot.Sidecar.Nickname);
                string titleText = hasAlias ? slot.Sidecar.Nickname : I18n.F("fmt.alias", I18n.T("lbl.alias_unset"));
                GUILayout.Label(titleText, hasAlias ? BlackWhiteSkin.CardTitleStyle : BlackWhiteSkin.MutedLabelStyle,
                    GUILayout.ExpandWidth(true), GUILayout.Height(44f));
                GUILayout.Space(6f);
                Rect penBtn = GUILayoutUtility.GetRect(44f, 44f, GUILayout.Width(44f), GUILayout.Height(44f), GUILayout.ExpandWidth(false));
                if (GUI.Button(penBtn, GUIContent.none, BlackWhiteSkin.IconBtnStyle))
                {
                    _nicknameEditingPath = key;
                    _editingNicknameText = slot.Sidecar.Nickname ?? "";
                    _focusNicknameNextFrame = true;
                    _pendingLoadPath = null;
                    _pendingDeletePath = null;
                    GUIUtility.ExitGUI();
                }
                BlackWhiteSkin.DrawPencil(new Rect(penBtn.x + 14f, penBtn.y + 14f, 16f, 16f), 2f);
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(6f);
            GUILayout.Label(slot.File.Name, BlackWhiteSkin.MutedLabelStyle, GUILayout.ExpandWidth(true), GUILayout.Height(34f));
            GUILayout.EndVertical();

            GUILayout.Space(16f);
            DrawSlotActionArea(slot, key, isCurrent);

            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
            GUILayout.Space(SlotCardGap);
        }

        private void HandleNicknameKeyboardShortcuts(SlotInfo slot)
        {
            var ev = Event.current;
            if (ev == null || ev.type != EventType.KeyDown) return;

            if (ev.keyCode == KeyCode.Return || ev.keyCode == KeyCode.KeypadEnter)
            {
                ev.Use();
                CommitNicknameEdit(slot);
                GUIUtility.ExitGUI();
            }
            else if (ev.keyCode == KeyCode.Escape)
            {
                ev.Use();
                CancelNicknameEdit();
                GUIUtility.ExitGUI();
            }
        }

        private void DrawSlotActionArea(SlotInfo slot, string key, bool isCurrent)
        {
            const float ActionBtnH = 44f;
            bool confirmingLoad = !isCurrent && _pendingLoadPath == key && Time.unscaledTime < _pendingLoadExpireAt;
            bool confirmingDelete = !slot.Sidecar.Pinned && _pendingDeletePath == key && Time.unscaledTime < _pendingDeleteExpireAt;

            GUILayout.BeginVertical(GUILayout.ExpandWidth(false));
            GUILayout.FlexibleSpace();
            GUILayout.BeginHorizontal();

            if (confirmingLoad)
            {
                string confirmText = I18n.T("btn.confirm_load");
                float confirmW = BlackWhiteSkin.CalcWidth(BlackWhiteSkin.PrimaryBtnStyle, confirmText, 140f, 10f);
                if (GUILayout.Button(confirmText, BlackWhiteSkin.PrimaryBtnStyle, GUILayout.Width(confirmW), GUILayout.Height(ActionBtnH)))
                {
                    _pendingLoadPath = null;
                    _onLoadSlot?.Invoke(slot);
                    InvalidateSlotCache();
                    ClearTextFocus();
                    GUIUtility.ExitGUI();
                }
                GUILayout.Space(6f);
                string cancelText = I18n.T("btn.cancel");
                float cancelW = BlackWhiteSkin.CalcWidth(GUI.skin.button, cancelText, 92f, 10f);
                if (GUILayout.Button(cancelText, GUI.skin.button, GUILayout.Width(cancelW), GUILayout.Height(ActionBtnH)))
                {
                    _pendingLoadPath = null;
                    ClearTextFocus();
                    GUIUtility.ExitGUI();
                }
            }
            else if (confirmingDelete)
            {
                string confirmDelText = I18n.T("btn.confirm_delete");
                float confirmDelW = BlackWhiteSkin.CalcWidth(BlackWhiteSkin.PrimaryBtnStyle, confirmDelText, 132f, 10f);
                if (GUILayout.Button(confirmDelText, BlackWhiteSkin.PrimaryBtnStyle, GUILayout.Width(confirmDelW), GUILayout.Height(ActionBtnH)))
                {
                    ResetTransientEditState();
                    _transientTip = null;
                    _onDeleteSlot?.Invoke(slot);
                    InvalidateSlotCache();
                    ClearTextFocus();
                    GUIUtility.ExitGUI();
                }
                GUILayout.Space(6f);
                string cancelText = I18n.T("btn.cancel");
                float cancelW = BlackWhiteSkin.CalcWidth(GUI.skin.button, cancelText, 92f, 10f);
                if (GUILayout.Button(cancelText, GUI.skin.button, GUILayout.Width(cancelW), GUILayout.Height(ActionBtnH)))
                {
                    _pendingDeletePath = null;
                    ClearTextFocus();
                    GUIUtility.ExitGUI();
                }
            }
            else
            {
                if (!isCurrent)
                {
                    string loadText = I18n.T("btn.load_slot");
                    float loadW = BlackWhiteSkin.CalcWidth(GUI.skin.button, loadText, 144f, 10f);
                    if (GUILayout.Button(loadText, GUI.skin.button, GUILayout.Width(loadW), GUILayout.Height(ActionBtnH)))
                    {
                        _pendingDeletePath = null;
                        _pendingLoadPath = key;
                        _pendingLoadExpireAt = Time.unscaledTime + ConfirmWindowSeconds;
                        ClearTextFocus();
                        GUIUtility.ExitGUI();
                    }
                    GUILayout.Space(6f);
                }

                string pinLabel = slot.Sidecar.Pinned ? I18n.T("btn.unpin") : I18n.T("btn.pin");
                float pinW = BlackWhiteSkin.CalcWidth(BlackWhiteSkin.SubtleBtnStyle, pinLabel, 100f, 10f);
                if (GUILayout.Button(pinLabel, BlackWhiteSkin.SubtleBtnStyle, GUILayout.Width(pinW), GUILayout.Height(ActionBtnH)))
                {
                    slot.Sidecar.Pinned = !slot.Sidecar.Pinned;
                    if (_pendingDeletePath == key) _pendingDeletePath = null;
                    ActiveStore?.UpdateSidecar(slot.FullSlotPath, slot.Sidecar);
                    InvalidateSlotCache();
                    ClearTextFocus();
                    GUIUtility.ExitGUI();
                }
                GUILayout.Space(6f);

                string deleteLabel = I18n.T("btn.delete");
                float deleteW = BlackWhiteSkin.CalcWidth(GUI.skin.button, deleteLabel, 100f, 10f);
                if (GUILayout.Button(deleteLabel, GUI.skin.button, GUILayout.Width(deleteW), GUILayout.Height(ActionBtnH)))
                {
                    _pendingLoadPath = null;
                    if (slot.Sidecar.Pinned)
                    {
                        _pendingDeletePath = null;
                        ShowTransientTip(I18n.T("msg.pinned_cannot_delete"));
                    }
                    else
                    {
                        _pendingDeletePath = key;
                        _pendingDeleteExpireAt = Time.unscaledTime + ConfirmWindowSeconds;
                    }
                    ClearTextFocus();
                    GUIUtility.ExitGUI();
                }
            }

            GUILayout.EndHorizontal();
            GUILayout.FlexibleSpace();
            GUILayout.EndVertical();
        }

        private static void DrawSlotBadge(string text, GUIStyle style)
        {
            float w = BlackWhiteSkin.CalcWidth(style, text, 0f, 4f);
            GUILayout.Label(text, style, GUILayout.Width(w), GUILayout.Height(36f));
        }

        internal static string StripBadgeBrackets(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            string t = text.Trim();
            if (t.Length >= 2 && t[0] == '[' && t[t.Length - 1] == ']')
            {
                return t.Substring(1, t.Length - 2).Trim();
            }
            return t;
        }

        internal static string FormatDateFolder(string dateFolder)
            => SaveStore.IsNoDateFolder(dateFolder) ? I18n.T("lbl.no_date") : dateFolder;

        internal static int ResolveSlotLayerNumber(SlotInfo slot)
        {
            if (slot == null || slot.Sidecar == null) return 0;
            if (slot.Sidecar.LoadedFromDisk && slot.Sidecar.Biome >= 0)
            {
                return slot.Sidecar.Biome + 1;
            }

            string name = Path.GetFileNameWithoutExtension(slot.File.Name);
            if (name.StartsWith(SaveStore.BeforeLoadPrefix, StringComparison.Ordinal))
            {
                name = name.Substring(SaveStore.BeforeLoadPrefix.Length);
            }
            string[] parts = name.Split('-');
            if (parts.Length >= 3 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int b) && b >= 0)
            {
                return b + 1;
            }
            return 0;
        }
    }
}
