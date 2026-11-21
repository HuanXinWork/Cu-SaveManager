using UnityEngine;

namespace CasualtiesUnknown.SaveManager
{
    internal static partial class BlackWhiteSkin
    {
        private static readonly string[] PencilBitmap16 =
        {
            "................",
            "...........###..",
            "..........#####.",
            ".........###.##.",
            "........###.###.",
            ".......###..##..",
            "......###..###..",
            ".....###..###...",
            "....###..###....",
            "...###..###.....",
            "..###..###......",
            "..##..###.......",
            ".######.........",
            ".####...........",
            ".##.............",
            "................",
        };

        private static void EnsureBuilt()
        {
            if (_skin != null) return;

            _bgPanel = MakeColorTex(0, 0, 0, 125);
            _bgCard = MakeBorderedTex(15, 15, 15, 105, 255, 255, 255, 100, 1);
            _bgCardCurrent = MakeBorderedTex(15, 15, 15, 125, 255, 255, 255, 255, 2);
            _bgCardWarn = MakeBorderedTex(15, 15, 15, 125, 255, 255, 255, 255, 2);
            _bgInput = MakeBorderedTex(0, 0, 0, 150, 255, 255, 255, 130, 1);
            _bgInputFocus = MakeBorderedTex(0, 0, 0, 165, 255, 255, 255, 255, 2);

            _bgButton = MakeBorderedTex(0, 0, 0, 150, 255, 255, 255, 220, 2);
            _bgButtonHover = MakeBorderedTex(60, 60, 60, 190, 255, 255, 255, 255, 2);
            _bgButtonActive = MakeBorderedTex(90, 90, 90, 210, 255, 255, 255, 255, 2);

            _bgBtnSubtle = MakeBorderedTex(0, 0, 0, 130, 255, 255, 255, 160, 1);
            _bgBtnSubtleHover = MakeBorderedTex(60, 60, 60, 190, 255, 255, 255, 240, 1);
            _bgBtnSubtleActive = MakeBorderedTex(90, 90, 90, 210, 255, 255, 255, 255, 1);

            _bgInverted2 = MakeBorderedTex(255, 255, 255, 235, 255, 255, 255, 255, 2);
            _bgInverted2Hover = MakeBorderedTex(255, 255, 255, 250, 255, 255, 255, 255, 2);
            _bgInverted2Active = MakeBorderedTex(220, 220, 220, 240, 255, 255, 255, 255, 2);

            _bgInverted1 = MakeBorderedTex(255, 255, 255, 235, 255, 255, 255, 255, 1);
            _bgInverted1Hover = MakeBorderedTex(255, 255, 255, 250, 255, 255, 255, 255, 1);
            _bgInverted1Active = MakeBorderedTex(220, 220, 220, 240, 255, 255, 255, 255, 1);

            _bgTab = MakeBorderedTex(0, 0, 0, 150, 255, 255, 255, 220, 2);
            _bgTabHover = MakeBorderedTex(60, 60, 60, 190, 255, 255, 255, 255, 2);

            _bgBadge = MakeBorderedTex(0, 0, 0, 150, 255, 255, 255, 160, 1);
            _bgBadgePinned = MakeBorderedTex(0, 0, 0, 150, 255, 255, 255, 255, 2);
            _bgValueBox = MakeBorderedTex(0, 0, 0, 150, 255, 255, 255, 160, 1);
            _bgSliderTrack = MakeBorderedTex(0, 0, 0, 150, 255, 255, 255, 130, 1);

            _line = MakeColorTex(255, 255, 255, 255);
            _thumbActive = MakeColorTex(220, 220, 220, 255);
            _iconPencil = MakeBitmapTex16(PencilBitmap16);

            var border1 = new RectOffset(1, 1, 1, 1);
            var border2 = new RectOffset(2, 2, 2, 2);
            var zeroMargin = new RectOffset(0, 0, 0, 0);

            var textBodyLong = new Color(1f, 1f, 1f, 0.88f);
            var textMuted = new Color(1f, 1f, 1f, 0.80f);

            _skin = ScriptableObject.CreateInstance<GUISkin>();
            _skin.hideFlags = HideFlags.HideAndDontSave;
            CopyDefaultsInto(_skin);

            ApplyStateful(_skin.box, _bgPanel, Color.white);
            ApplyStateful(_skin.window, _bgPanel, Color.white);
            ApplyStateful(_skin.button, _bgButton, Color.white, _bgButtonHover, _bgButtonActive);
            ApplyStateful(_skin.label, null, Color.white);
            ApplyInput(_skin.textField, _bgInput, _bgInputFocus, Color.white, border2);
            ApplyInput(_skin.textArea, _bgInput, _bgInputFocus, Color.white, border2);
            ApplyStateful(_skin.toggle, null, Color.white);
            ApplyStateful(_skin.horizontalSlider, _bgSliderTrack, Color.white);
            ApplyStateful(_skin.horizontalSliderThumb, _line, Color.black, _line, _thumbActive);
            ApplyStateful(_skin.verticalSlider, _bgSliderTrack, Color.white);
            ApplyStateful(_skin.verticalSliderThumb, _line, Color.black, _line, _thumbActive);
            ApplyStateful(_skin.horizontalScrollbar, _bgSliderTrack, Color.white);
            ApplyStateful(_skin.horizontalScrollbarThumb, _line, Color.black, _line, _thumbActive);
            ApplyStateful(_skin.verticalScrollbar, _bgSliderTrack, Color.white);
            ApplyStateful(_skin.verticalScrollbarThumb, _line, Color.black, _line, _thumbActive);
            ClearBackgrounds(_skin.scrollView);

            // 14px 轨道配上下 -7 padding，使 28px 纯白滑块头垂直居中跨骑于导轨。
            _skin.horizontalSlider.fixedHeight = 14f;
            _skin.horizontalSlider.border = border1;
            _skin.horizontalSlider.margin = new RectOffset(4, 4, 12, 12);
            _skin.horizontalSlider.padding = new RectOffset(0, 0, -7, -7);
            _skin.horizontalSliderThumb.fixedWidth = 16f;
            _skin.horizontalSliderThumb.fixedHeight = 28f;
            _skin.horizontalSliderThumb.margin = zeroMargin;
            _skin.horizontalSliderThumb.overflow = zeroMargin;

            _skin.verticalSlider.border = border1;
            _skin.verticalSlider.fixedWidth = 14f;
            _skin.verticalSliderThumb.fixedWidth = 28f;
            _skin.verticalSliderThumb.fixedHeight = 16f;

            _skin.horizontalScrollbar.fixedHeight = 14f;
            _skin.horizontalScrollbar.border = border1;
            _skin.horizontalScrollbarThumb.fixedHeight = 14f;
            _skin.verticalScrollbar.fixedWidth = 14f;
            _skin.verticalScrollbar.border = border1;
            _skin.verticalScrollbarThumb.fixedWidth = 14f;

            _skin.button.alignment = TextAnchor.MiddleCenter;
            _skin.button.fontSize = 20;
            _skin.button.fontStyle = FontStyle.Bold;
            _skin.button.padding = new RectOffset(12, 12, 5, 5);
            _skin.button.margin = zeroMargin;
            _skin.button.border = border2;
            _skin.button.wordWrap = false;
            _skin.button.clipping = TextClipping.Clip;

            // 保留宿主默认 label 的 wordWrap=true，供 ExternalTabRegistry 嵌入的第三方模组直接排长句说明。
            _skin.label.fontSize = 18;
            _skin.label.fontStyle = FontStyle.Bold;
            _skin.label.alignment = TextAnchor.MiddleLeft;
            _skin.label.wordWrap = true;
            _skin.label.clipping = TextClipping.Clip;
            _skin.label.padding = new RectOffset(2, 2, 2, 2);

            _skin.textField.fontSize = 20;
            _skin.textField.fontStyle = FontStyle.Bold;
            _skin.textField.alignment = TextAnchor.MiddleLeft;
            _skin.textField.wordWrap = false;
            _skin.textField.clipping = TextClipping.Clip;
            _skin.textField.padding = new RectOffset(10, 10, 5, 5);
            _skin.textField.margin = zeroMargin;

            _skin.textArea.fontSize = 18;
            _skin.textArea.fontStyle = FontStyle.Bold;
            _skin.textArea.alignment = TextAnchor.UpperLeft;
            _skin.textArea.wordWrap = true;
            _skin.textArea.padding = new RectOffset(10, 10, 6, 6);
            _skin.textArea.margin = zeroMargin;

            _skin.toggle.fontSize = 20;
            _skin.toggle.fontStyle = FontStyle.Bold;
            _skin.toggle.wordWrap = false;
            _skin.box.fontSize = 18;
            _skin.box.fontStyle = FontStyle.Bold;
            _skin.box.wordWrap = false;

            _hStyle = new GUIStyle(_skin.label)
            {
                fontSize = 32,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                wordWrap = false,
                clipping = TextClipping.Overflow,
                padding = new RectOffset(0, 0, 2, 2),
                margin = zeroMargin,
            };
            ApplyTextColor(_hStyle, Color.white);

            _sectionHeaderStyle = new GUIStyle(_skin.label)
            {
                fontSize = 22,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                wordWrap = false,
                clipping = TextClipping.Overflow,
                padding = new RectOffset(2, 2, 2, 2),
                margin = zeroMargin,
            };
            ApplyTextColor(_sectionHeaderStyle, Color.white);

            _cardTitleStyle = new GUIStyle(_skin.label)
            {
                fontSize = 22,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                wordWrap = false,
                clipping = TextClipping.Clip,
                padding = new RectOffset(0, 4, 2, 2),
                margin = zeroMargin,
            };
            ApplyTextColor(_cardTitleStyle, Color.white);

            _singleLineLabelStyle = new GUIStyle(_skin.label)
            {
                fontSize = 20,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                wordWrap = false,
                clipping = TextClipping.Clip,
                padding = new RectOffset(0, 6, 2, 2),
                margin = zeroMargin,
            };
            ApplyTextColor(_singleLineLabelStyle, Color.white);

            _rowLabelStyle = new GUIStyle(_skin.label)
            {
                fontSize = 20,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                wordWrap = false,
                clipping = TextClipping.Clip,
                padding = new RectOffset(0, 6, 3, 3),
                margin = zeroMargin,
            };
            ApplyTextColor(_rowLabelStyle, Color.white);

            _bodyLabelStyle = new GUIStyle(_skin.label)
            {
                fontSize = 18,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                wordWrap = true,
                clipping = TextClipping.Clip,
                padding = new RectOffset(2, 2, 3, 3),
                margin = zeroMargin,
            };
            ApplyTextColor(_bodyLabelStyle, textBodyLong);

            _mutedLabelStyle = new GUIStyle(_skin.label)
            {
                fontSize = 18,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                wordWrap = false,
                clipping = TextClipping.Clip,
                padding = new RectOffset(0, 4, 2, 2),
                margin = zeroMargin,
            };
            ApplyTextColor(_mutedLabelStyle, textMuted);

            _mutedRightLabelStyle = new GUIStyle(_mutedLabelStyle)
            {
                alignment = TextAnchor.MiddleRight,
                padding = new RectOffset(4, 0, 2, 2),
                margin = zeroMargin,
                wordWrap = false,
            };

            _cardStyle = BuildCardStyle(_bgCard, border1, Color.white);
            _cardCurrentStyle = BuildCardStyle(_bgCardCurrent, border2, Color.white);
            _cardWarnStyle = BuildCardStyle(_bgCardWarn, border2, Color.white);

            _progressTrackStyle = new GUIStyle(_skin.box)
            {
                border = border1,
                padding = zeroMargin,
                margin = zeroMargin,
            };
            ApplyStateful(_progressTrackStyle, _bgSliderTrack, Color.white);

            _hButtonStyle = new GUIStyle(_skin.button)
            {
                fontSize = 20,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                border = border1,
                padding = new RectOffset(14, 14, 5, 5),
                margin = zeroMargin,
                wordWrap = false,
                clipping = TextClipping.Clip,
            };
            ApplyStateful(_hButtonStyle, _bgBtnSubtle, Color.white, _bgBtnSubtleHover, _bgBtnSubtleActive);

            _primaryBtnStyle = new GUIStyle(_skin.button)
            {
                border = border2,
                margin = zeroMargin,
                wordWrap = false,
            };
            ApplyStateful(_primaryBtnStyle, _bgInverted2, Color.black, _bgInverted2Hover, _bgInverted2Active);

            // 危险二次确认统一使用黑白反白样式，不使用红色。
            _dangerBtnStyle = new GUIStyle(_primaryBtnStyle);

            _subtleBtnStyle = new GUIStyle(_skin.button)
            {
                fontSize = 20,
                fontStyle = FontStyle.Bold,
                border = border1,
                padding = new RectOffset(12, 12, 5, 5),
                margin = zeroMargin,
                wordWrap = false,
                clipping = TextClipping.Clip,
            };
            ApplyStateful(_subtleBtnStyle, _bgBtnSubtle, Color.white, _bgBtnSubtleHover, _bgBtnSubtleActive);

            _iconBtnStyle = new GUIStyle(_skin.button)
            {
                fontSize = 20,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                border = border1,
                padding = new RectOffset(4, 4, 4, 4),
                margin = zeroMargin,
                wordWrap = false,
                clipping = TextClipping.Clip,
            };
            ApplyStateful(_iconBtnStyle, _bgBtnSubtle, Color.white, _bgBtnSubtleHover, _bgBtnSubtleActive);

            _iconPrimaryBtnStyle = new GUIStyle(_iconBtnStyle);
            ApplyStateful(_iconPrimaryBtnStyle, _bgInverted1, Color.black, _bgInverted1Hover, _bgInverted1Active);

            _overlayBtnStyle = new GUIStyle(_skin.button)
            {
                fontSize = 24,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                border = border2,
                padding = new RectOffset(16, 16, 8, 8),
                margin = zeroMargin,
                wordWrap = false,
                clipping = TextClipping.Clip,
            };
            ApplyStateful(_overlayBtnStyle, _bgButton, Color.white, _bgButtonHover, _bgButtonActive);

            _tabStyle = new GUIStyle(_skin.button)
            {
                fontSize = 20,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                border = border2,
                padding = new RectOffset(10, 10, 5, 5),
                margin = zeroMargin,
                wordWrap = false,
                clipping = TextClipping.Clip,
            };
            ApplyStateful(_tabStyle, _bgTab, Color.white, _bgTabHover, _bgButtonActive);

            _tabActiveStyle = new GUIStyle(_tabStyle);
            ApplyStateful(_tabActiveStyle, _bgInverted2, Color.black, _bgInverted2Hover, _bgInverted2Active);

            _sidebarTabStyle = new GUIStyle(_tabStyle)
            {
                fontSize = 19,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                border = border2,
                padding = new RectOffset(16, 12, 6, 6),
                margin = zeroMargin,
                wordWrap = false,
                clipping = TextClipping.Clip,
            };
            ApplyStateful(_sidebarTabStyle, _bgButton, Color.white, _bgButtonHover, _bgButtonActive);

            _sidebarTabActiveStyle = new GUIStyle(_sidebarTabStyle);
            ApplyStateful(_sidebarTabActiveStyle, _bgInverted2, Color.black, _bgInverted2Hover, _bgInverted2Active);

            _segmentStyle = new GUIStyle(_skin.button)
            {
                fontSize = 18,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                border = border1,
                padding = new RectOffset(10, 10, 4, 4),
                margin = zeroMargin,
                wordWrap = false,
                clipping = TextClipping.Clip,
            };
            ApplyStateful(_segmentStyle, _bgBtnSubtle, Color.white, _bgBtnSubtleHover, _bgBtnSubtleActive);

            _segmentActiveStyle = new GUIStyle(_segmentStyle);
            ApplyStateful(_segmentActiveStyle, _bgInverted1, Color.black, _bgInverted1Hover, _bgInverted1Active);

            _scaleBtnStyle = new GUIStyle(_segmentStyle);
            _scaleBtnActiveStyle = new GUIStyle(_segmentStyle)
            {
                border = border2,
            };
            ApplyStateful(_scaleBtnActiveStyle, _bgInverted2, Color.black, _bgInverted2Hover, _bgInverted2Active);

            _valueBoxStyle = new GUIStyle(_skin.label)
            {
                fontSize = 18,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                border = border1,
                padding = new RectOffset(10, 10, 4, 4),
                margin = zeroMargin,
                wordWrap = false,
                clipping = TextClipping.Clip,
            };
            ApplyStateful(_valueBoxStyle, _bgValueBox, Color.white);

            _badgeStyle = BuildBadgeStyle(_bgBadge, border1, Color.white);
            _badgeHighlightStyle = BuildBadgeStyle(_bgInverted1, border1, Color.black);
            _badgePinnedStyle = BuildBadgeStyle(_bgBadgePinned, border2, Color.white);
        }

        private static GUIStyle BuildCardStyle(Texture2D bg, RectOffset border, Color text)
        {
            var s = new GUIStyle(_skin.box)
            {
                border = border,
                padding = new RectOffset(16, 16, 12, 12),
                margin = new RectOffset(0, 0, 0, 0),
                wordWrap = false,
            };
            ApplyStateful(s, bg, text);
            return s;
        }

        private static GUIStyle BuildBadgeStyle(Texture2D bg, RectOffset border, Color text)
        {
            var s = new GUIStyle(_skin.label)
            {
                fontSize = 16,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                border = border,
                padding = new RectOffset(10, 10, 4, 4),
                margin = new RectOffset(0, 6, 0, 0),
                wordWrap = false,
                clipping = TextClipping.Clip,
            };
            ApplyStateful(s, bg, text);
            return s;
        }

        private static void CopyDefaultsInto(GUISkin dst)
        {
            var defaults = GUI.skin;
            dst.font = defaults.font;
            dst.box = new GUIStyle(defaults.box);
            dst.window = new GUIStyle(defaults.window);
            dst.button = new GUIStyle(defaults.button);
            dst.label = new GUIStyle(defaults.label);
            dst.textField = new GUIStyle(defaults.textField);
            dst.textArea = new GUIStyle(defaults.textArea);
            dst.toggle = new GUIStyle(defaults.toggle);
            dst.horizontalSlider = new GUIStyle(defaults.horizontalSlider);
            dst.horizontalSliderThumb = new GUIStyle(defaults.horizontalSliderThumb);
            dst.verticalSlider = new GUIStyle(defaults.verticalSlider);
            dst.verticalSliderThumb = new GUIStyle(defaults.verticalSliderThumb);
            dst.horizontalScrollbar = new GUIStyle(defaults.horizontalScrollbar);
            dst.horizontalScrollbarThumb = new GUIStyle(defaults.horizontalScrollbarThumb);
            dst.horizontalScrollbarLeftButton = new GUIStyle(defaults.horizontalScrollbarLeftButton);
            dst.horizontalScrollbarRightButton = new GUIStyle(defaults.horizontalScrollbarRightButton);
            dst.verticalScrollbar = new GUIStyle(defaults.verticalScrollbar);
            dst.verticalScrollbarThumb = new GUIStyle(defaults.verticalScrollbarThumb);
            dst.verticalScrollbarUpButton = new GUIStyle(defaults.verticalScrollbarUpButton);
            dst.verticalScrollbarDownButton = new GUIStyle(defaults.verticalScrollbarDownButton);
            dst.scrollView = new GUIStyle(defaults.scrollView);
        }

        private static void ClearBackgrounds(GUIStyle s)
        {
            s.normal.background = null;
            s.onNormal.background = null;
            s.active.background = null;
            s.onActive.background = null;
            s.focused.background = null;
            s.onFocused.background = null;
            s.hover.background = null;
            s.onHover.background = null;
        }

        private static void ApplyStateful(GUIStyle s, Texture2D normalBg, Color text,
            Texture2D hoverBg = null, Texture2D activeBg = null)
        {
            s.normal.background = normalBg;
            s.onNormal.background = normalBg;
            s.hover.background = hoverBg ?? normalBg;
            s.onHover.background = hoverBg ?? normalBg;
            s.active.background = activeBg ?? hoverBg ?? normalBg;
            s.onActive.background = activeBg ?? hoverBg ?? normalBg;
            s.focused.background = normalBg;
            s.onFocused.background = normalBg;
            ApplyTextColor(s, text);
        }

        private static void ApplyInput(GUIStyle s, Texture2D normalBg, Texture2D focusBg, Color text, RectOffset border)
        {
            s.border = border;
            s.normal.background = normalBg;
            s.onNormal.background = normalBg;
            s.hover.background = normalBg;
            s.onHover.background = normalBg;
            s.active.background = focusBg;
            s.onActive.background = focusBg;
            s.focused.background = focusBg;
            s.onFocused.background = focusBg;
            ApplyTextColor(s, text);
        }

        private static void ApplyTextColor(GUIStyle s, Color text)
        {
            s.normal.textColor = text;
            s.onNormal.textColor = text;
            s.active.textColor = text;
            s.onActive.textColor = text;
            s.focused.textColor = text;
            s.onFocused.textColor = text;
            s.hover.textColor = text;
            s.onHover.textColor = text;
        }

        private static Texture2D MakeColorTex(byte r, byte g, byte b, byte a)
        {
            var t = new Texture2D(2, 2, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            var c = new Color32(r, g, b, a);
            t.SetPixels32(new[] { c, c, c, c });
            t.Apply();
            return t;
        }

        /// <summary>按 16x16 字符栅格预生成白色图标纹理；Unity 纹理 y=0 在左下角，故顶行对应 y=15。</summary>
        private static Texture2D MakeBitmapTex16(string[] rowsTopToBottom)
        {
            const int Size = 16;
            var t = new Texture2D(Size, Size, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            var clear = new Color32(0, 0, 0, 0);
            var solid = new Color32(255, 255, 255, 255);
            var pixels = new Color32[Size * Size];

            for (int row = 0; row < Size; row++)
            {
                string line = rowsTopToBottom[row];
                int y = Size - 1 - row;
                for (int x = 0; x < Size; x++)
                {
                    pixels[y * Size + x] = line[x] == '#' ? solid : clear;
                }
            }

            t.SetPixels32(pixels);
            t.Apply();
            return t;
        }

        /// <summary>保留中间 4x4 纯色区供九宫格拉伸，确保 1px/2px 边框在任意矩形尺寸下不产生模糊晕染。</summary>
        private static Texture2D MakeBorderedTex(byte fr, byte fg, byte fb, byte fa,
            byte br, byte bg, byte bb, byte ba, int borderPx)
        {
            int size = borderPx * 2 + 4;
            var t = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            var fill = new Color32(fr, fg, fb, fa);
            var edge = new Color32(br, bg, bb, ba);
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    bool isEdge = x < borderPx || x >= size - borderPx || y < borderPx || y >= size - borderPx;
                    pixels[y * size + x] = isEdge ? edge : fill;
                }
            }
            t.SetPixels32(pixels);
            t.Apply();
            return t;
        }

        private static void DestroyTexture(ref Texture2D tex)
        {
            if (tex != null)
            {
                try { Object.Destroy(tex); } catch { }
                tex = null;
            }
        }

        private static void DestroyObject<T>(ref T obj) where T : Object
        {
            if (obj != null)
            {
                try { Object.Destroy(obj); } catch { }
                obj = null;
            }
        }
    }
}
