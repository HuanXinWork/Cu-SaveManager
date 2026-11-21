using System.Collections.Generic;
using UnityEngine;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>纯黑白线条半透明 GUISkin 栈管理、公开样式入口与几何绘制原语。</summary>
    internal static partial class BlackWhiteSkin
    {
        private static readonly Stack<GUISkin> _skinStack = new Stack<GUISkin>(4);
        private static readonly GUIContent _measureContent = new GUIContent();
        private static GUISkin _skin;

        private static Texture2D _bgPanel;
        private static Texture2D _bgCard;
        private static Texture2D _bgCardCurrent;
        private static Texture2D _bgCardWarn;
        private static Texture2D _bgInput;
        private static Texture2D _bgInputFocus;

        private static Texture2D _bgButton;
        private static Texture2D _bgButtonHover;
        private static Texture2D _bgButtonActive;

        private static Texture2D _bgBtnSubtle;
        private static Texture2D _bgBtnSubtleHover;
        private static Texture2D _bgBtnSubtleActive;

        private static Texture2D _bgInverted2;
        private static Texture2D _bgInverted2Hover;
        private static Texture2D _bgInverted2Active;

        private static Texture2D _bgInverted1;
        private static Texture2D _bgInverted1Hover;
        private static Texture2D _bgInverted1Active;

        private static Texture2D _bgTab;
        private static Texture2D _bgTabHover;

        private static Texture2D _bgBadge;
        private static Texture2D _bgBadgePinned;
        private static Texture2D _bgValueBox;
        private static Texture2D _bgSliderTrack;

        private static Texture2D _line;
        private static Texture2D _thumbActive;
        private static Texture2D _iconPencil;

        private static GUIStyle _hStyle;
        private static GUIStyle _sectionHeaderStyle;
        private static GUIStyle _cardTitleStyle;
        private static GUIStyle _singleLineLabelStyle;
        private static GUIStyle _rowLabelStyle;
        private static GUIStyle _bodyLabelStyle;
        private static GUIStyle _mutedLabelStyle;
        private static GUIStyle _mutedRightLabelStyle;

        private static GUIStyle _hButtonStyle;
        private static GUIStyle _cardStyle;
        private static GUIStyle _cardCurrentStyle;
        private static GUIStyle _cardWarnStyle;
        private static GUIStyle _progressTrackStyle;

        private static GUIStyle _primaryBtnStyle;
        private static GUIStyle _dangerBtnStyle;
        private static GUIStyle _subtleBtnStyle;
        private static GUIStyle _iconBtnStyle;
        private static GUIStyle _iconPrimaryBtnStyle;
        private static GUIStyle _overlayBtnStyle;

        private static GUIStyle _tabStyle;
        private static GUIStyle _tabActiveStyle;
        private static GUIStyle _sidebarTabStyle;
        private static GUIStyle _sidebarTabActiveStyle;
        private static GUIStyle _segmentStyle;
        private static GUIStyle _segmentActiveStyle;
        private static GUIStyle _scaleBtnStyle;
        private static GUIStyle _scaleBtnActiveStyle;
        private static GUIStyle _valueBoxStyle;

        private static GUIStyle _badgeStyle;
        private static GUIStyle _badgeHighlightStyle;
        private static GUIStyle _badgePinnedStyle;

        internal static GUIStyle HeaderStyle { get { EnsureBuilt(); return _hStyle; } }
        internal static GUIStyle SectionHeaderStyle { get { EnsureBuilt(); return _sectionHeaderStyle; } }
        internal static GUIStyle CardTitleStyle { get { EnsureBuilt(); return _cardTitleStyle; } }
        internal static GUIStyle SingleLineLabelStyle { get { EnsureBuilt(); return _singleLineLabelStyle; } }
        internal static GUIStyle RowLabelStyle { get { EnsureBuilt(); return _rowLabelStyle; } }
        internal static GUIStyle BodyLabelStyle { get { EnsureBuilt(); return _bodyLabelStyle; } }
        internal static GUIStyle MutedLabelStyle { get { EnsureBuilt(); return _mutedLabelStyle; } }
        internal static GUIStyle MutedRightLabelStyle { get { EnsureBuilt(); return _mutedRightLabelStyle; } }

        internal static GUIStyle HeaderButtonStyle { get { EnsureBuilt(); return _hButtonStyle; } }
        internal static GUIStyle CardStyle { get { EnsureBuilt(); return _cardStyle; } }
        internal static GUIStyle CardCurrentStyle { get { EnsureBuilt(); return _cardCurrentStyle; } }
        internal static GUIStyle CardWarnStyle { get { EnsureBuilt(); return _cardWarnStyle; } }

        internal static GUIStyle PrimaryBtnStyle { get { EnsureBuilt(); return _primaryBtnStyle; } }
        internal static GUIStyle DangerBtnStyle { get { EnsureBuilt(); return _dangerBtnStyle; } }
        internal static GUIStyle SubtleBtnStyle { get { EnsureBuilt(); return _subtleBtnStyle; } }
        internal static GUIStyle IconBtnStyle { get { EnsureBuilt(); return _iconBtnStyle; } }
        internal static GUIStyle IconPrimaryBtnStyle { get { EnsureBuilt(); return _iconPrimaryBtnStyle; } }
        internal static GUIStyle OverlayBtnStyle { get { EnsureBuilt(); return _overlayBtnStyle; } }

        internal static GUIStyle TabStyle { get { EnsureBuilt(); return _tabStyle; } }
        internal static GUIStyle TabActiveStyle { get { EnsureBuilt(); return _tabActiveStyle; } }
        internal static GUIStyle SidebarTabStyle { get { EnsureBuilt(); return _sidebarTabStyle; } }
        internal static GUIStyle SidebarTabActiveStyle { get { EnsureBuilt(); return _sidebarTabActiveStyle; } }
        internal static GUIStyle SegmentStyle { get { EnsureBuilt(); return _segmentStyle; } }
        internal static GUIStyle SegmentActiveStyle { get { EnsureBuilt(); return _segmentActiveStyle; } }
        internal static GUIStyle ScaleBtnStyle { get { EnsureBuilt(); return _scaleBtnStyle; } }
        internal static GUIStyle ScaleBtnActiveStyle { get { EnsureBuilt(); return _scaleBtnActiveStyle; } }
        internal static GUIStyle ValueBoxStyle { get { EnsureBuilt(); return _valueBoxStyle; } }

        internal static GUIStyle BadgeStyle { get { EnsureBuilt(); return _badgeStyle; } }
        internal static GUIStyle BadgeHighlightStyle { get { EnsureBuilt(); return _badgeHighlightStyle; } }
        internal static GUIStyle BadgePinnedStyle { get { EnsureBuilt(); return _badgePinnedStyle; } }
        internal static Texture2D WhiteTex { get { EnsureBuilt(); return _line; } }

        internal static void Push()
        {
            EnsureBuilt();
            _skinStack.Push(GUI.skin);
            GUI.skin = _skin;
        }

        internal static void Pop()
        {
            if (_skinStack.Count > 0)
            {
                GUI.skin = _skinStack.Pop();
            }
        }

        /// <summary>按文本实际像素宽度计算防裁切固定宽，替代在空间紧张时会压窄控件的 GUILayout.MinWidth。</summary>
        internal static float CalcWidth(GUIStyle style, string text, float minWidth, float extraPad = 8f)
        {
            EnsureBuilt();
            if (style == null) return minWidth;
            _measureContent.text = text ?? string.Empty;
            float measured = Mathf.Ceil(style.CalcSize(_measureContent).x) + extraPad;
            return Mathf.Max(minWidth, measured);
        }

        /// <summary>销毁缓存的 GUISkin、全部程序化纹理并清空嵌套栈。</summary>
        internal static void Dispose()
        {
            LayerBannerRenderer.Dispose();
            _skinStack.Clear();
            DestroyObject(ref _skin);

            DestroyTexture(ref _bgPanel);
            DestroyTexture(ref _bgCard);
            DestroyTexture(ref _bgCardCurrent);
            DestroyTexture(ref _bgCardWarn);
            DestroyTexture(ref _bgInput);
            DestroyTexture(ref _bgInputFocus);

            DestroyTexture(ref _bgButton);
            DestroyTexture(ref _bgButtonHover);
            DestroyTexture(ref _bgButtonActive);

            DestroyTexture(ref _bgBtnSubtle);
            DestroyTexture(ref _bgBtnSubtleHover);
            DestroyTexture(ref _bgBtnSubtleActive);

            DestroyTexture(ref _bgInverted2);
            DestroyTexture(ref _bgInverted2Hover);
            DestroyTexture(ref _bgInverted2Active);

            DestroyTexture(ref _bgInverted1);
            DestroyTexture(ref _bgInverted1Hover);
            DestroyTexture(ref _bgInverted1Active);

            DestroyTexture(ref _bgTab);
            DestroyTexture(ref _bgTabHover);

            DestroyTexture(ref _bgBadge);
            DestroyTexture(ref _bgBadgePinned);
            DestroyTexture(ref _bgValueBox);
            DestroyTexture(ref _bgSliderTrack);

            DestroyTexture(ref _line);
            DestroyTexture(ref _thumbActive);
            DestroyTexture(ref _iconPencil);

            _hStyle = null;
            _sectionHeaderStyle = null;
            _cardTitleStyle = null;
            _singleLineLabelStyle = null;
            _rowLabelStyle = null;
            _bodyLabelStyle = null;
            _mutedLabelStyle = null;
            _mutedRightLabelStyle = null;
            _hButtonStyle = null;
            _cardStyle = null;
            _cardCurrentStyle = null;
            _cardWarnStyle = null;
            _progressTrackStyle = null;
            _primaryBtnStyle = null;
            _dangerBtnStyle = null;
            _subtleBtnStyle = null;
            _iconBtnStyle = null;
            _iconPrimaryBtnStyle = null;
            _overlayBtnStyle = null;
            _tabStyle = null;
            _tabActiveStyle = null;
            _sidebarTabStyle = null;
            _sidebarTabActiveStyle = null;
            _segmentStyle = null;
            _segmentActiveStyle = null;
            _scaleBtnStyle = null;
            _scaleBtnActiveStyle = null;
            _valueBoxStyle = null;
            _badgeStyle = null;
            _badgeHighlightStyle = null;
            _badgePinnedStyle = null;
        }

        internal static void DrawHLine(Rect rect)
        {
            EnsureBuilt();
            GUI.DrawTexture(rect, _line, ScaleMode.StretchToFill);
        }

        internal static void DrawSoftVLine(Rect rect)
        {
            EnsureBuilt();
            Color prev = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, 0.38f);
            GUI.DrawTexture(rect, _line, ScaleMode.StretchToFill);
            GUI.color = prev;
        }

        /// <summary>在半透明底板上先绘 1px 暗影再绘亮白前景字，避免亮色游戏画面透过时降低标题可读性。</summary>
        internal static void DrawShadowedLabel(Rect rect, string text, GUIStyle style)
        {
            EnsureBuilt();
            Color prev = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.82f);
            GUI.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), text, style);
            GUI.color = prev;
            GUI.Label(rect, text, style);
        }

        internal static void DrawBorder(Rect rect) => DrawBorder(rect, 1f);

        internal static void DrawBorder(Rect rect, float thickness)
        {
            EnsureBuilt();
            GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, thickness), _line, ScaleMode.StretchToFill);
            GUI.DrawTexture(new Rect(rect.x, rect.yMax - thickness, rect.width, thickness), _line, ScaleMode.StretchToFill);
            GUI.DrawTexture(new Rect(rect.x, rect.y, thickness, rect.height), _line, ScaleMode.StretchToFill);
            GUI.DrawTexture(new Rect(rect.xMax - thickness, rect.y, thickness, rect.height), _line, ScaleMode.StretchToFill);
        }

        internal static void DrawProgressBar(Rect rect, float ratio, bool warn = false)
        {
            EnsureBuilt();
            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                _progressTrackStyle.Draw(rect, false, false, false, false);
            }
            float clamped = Mathf.Clamp01(ratio);
            if (clamped <= 0.001f) return;

            const float inset = 1f;
            float fillW = Mathf.Max(0f, (rect.width - inset * 2f) * clamped);
            float fillH = Mathf.Max(1f, rect.height - inset * 2f);
            if (fillW > 0f)
            {
                GUI.DrawTexture(new Rect(rect.x + inset, rect.y + inset, fillW, fillH), _line, ScaleMode.StretchToFill);
            }
        }

        internal static void DrawCloseX(Rect rect, float thickness = 5f)
        {
            EnsureBuilt();
            DrawDiagonalLine(rect.x, rect.y, rect.xMax, rect.yMax, thickness);
            DrawDiagonalLine(rect.x, rect.yMax, rect.xMax, rect.y, thickness);
        }

        private static void DrawDiagonalLine(float x1, float y1, float x2, float y2, float thickness)
        {
            float dx = x2 - x1;
            float dy = y2 - y1;
            float length = Mathf.Sqrt(dx * dx + dy * dy);
            // 沿对角线铺轴对齐纯白小方块，避免外层缩放矩阵与旋转复合产生锯齿虚化。
            int steps = Mathf.Max(2, Mathf.CeilToInt(length / Mathf.Max(0.5f, thickness * 0.5f)));
            for (int i = 0; i <= steps; i++)
            {
                float t = i / (float)steps;
                float x = x1 + dx * t;
                float y = y1 + dy * t;
                GUI.DrawTexture(new Rect(x - thickness * 0.5f, y - thickness * 0.5f, thickness, thickness),
                    _line, ScaleMode.StretchToFill);
            }
        }

        internal static void DrawPencil(Rect rect, float thickness = 2f)
        {
            EnsureBuilt();
            GUI.DrawTexture(rect, _iconPencil, ScaleMode.ScaleToFit);
        }
    }
}
