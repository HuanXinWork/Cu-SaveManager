using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>存档卡片层级画面横幅渲染器：优先读取 QoL 实机横幅（含 LayerUnlock 扩展层），未命中时回退克隆游戏原生平铺背景贴图，并叠加压暗遮罩保证纯白粗体前景可读。</summary>
    internal static class LayerBannerRenderer
    {
        // 与游戏 WorldGeneration.WorldCreateBackground 的 0-based biomeDepth 贴图名逐层对应。
        private static readonly string[] VanillaBiomeBackgroundNames =
        {
            "rockBackground",
            "soilBackground",
            "sandBackground",
            "wastelandBackground",
            "grassBackground",
            "iceBackground",
            "fungalBackground",
            "rockBackground",
            "ventsBackground",
            "wwmrBackground",
            "crimsonback",
            "steelBackground",
        };

        // 叠加上卡片自身半透明黑底（105/255 或 125/255）后，总压暗度均落在约 0.62，对齐 QoL Dimmer(0.60)。
        private const float NormalCardExtraDimAlpha = 0.35f;
        private const float CurrentCardExtraDimAlpha = 0.26f;
        private const float FallbackTilePixelScale = 2f;

        private static readonly Dictionary<string, Texture2D> _fallbackTiledCache =
            new Dictionary<string, Texture2D>(StringComparer.Ordinal);
        private static readonly HashSet<string> _failedFallbackNames =
            new HashSet<string>(StringComparer.Ordinal);

        /// <summary>销毁二级回退克隆出的全部平铺贴图副本。</summary>
        internal static void Dispose()
        {
            foreach (KeyValuePair<string, Texture2D> kv in _fallbackTiledCache)
            {
                if (kv.Value != null)
                {
                    try { UnityEngine.Object.Destroy(kv.Value); } catch { }
                }
            }
            _fallbackTiledCache.Clear();
            _failedFallbackNames.Clear();
        }

        /// <summary>在卡片纯白边框内侧绘制层级背景横幅与半透明纯黑压暗层；仅应在 EventType.Repaint 阶段调用。</summary>
        internal static void DrawCardBanner(Rect cardRect, int biomeIndex, bool isCurrentCard)
        {
            if (biomeIndex < 0) return;
            if (Event.current == null || Event.current.type != EventType.Repaint) return;

            float borderInset = isCurrentCard ? 2f : 1f;
            Rect innerRect = new Rect(
                cardRect.x + borderInset,
                cardRect.y + borderInset,
                cardRect.width - borderInset * 2f,
                cardRect.height - borderInset * 2f);
            if (innerRect.width <= 4f || innerRect.height <= 4f) return;

            bool drawn = TryDrawQolSpriteBanner(innerRect, biomeIndex)
                || TryDrawVanillaFallbackBanner(innerRect, biomeIndex);
            if (!drawn) return;

            float dimAlpha = isCurrentCard ? CurrentCardExtraDimAlpha : NormalCardExtraDimAlpha;
            Color prev = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, dimAlpha);
            GUI.DrawTexture(innerRect, BlackWhiteSkin.WhiteTex, ScaleMode.StretchToFill);
            GUI.color = prev;
        }

        /// <summary>解析当前活动存档对应的 0-based 层索引；未知或无存档时返回 -1。</summary>
        internal static int ResolveActiveSaveBiomeIndex(List<SlotInfo> slots, string currentSaveHash)
        {
            if (WorldGeneration.world != null)
            {
                int liveDepth = WorldGeneration.world.biomeDepth;
                return liveDepth >= 0 ? liveDepth : -1;
            }

            if (slots != null && !string.IsNullOrEmpty(currentSaveHash))
            {
                for (int i = 0; i < slots.Count; i++)
                {
                    SlotInfo slot = slots[i];
                    if (!SaveStore.IsSlotMatchingCurrentSave(slot, currentSaveHash)) continue;
                    int layerNum = SaveManagerWindow.ResolveSlotLayerNumber(slot);
                    if (layerNum > 0) return layerNum - 1;
                }
            }

            return TryReadMainMenuSaveBiome();
        }

        private static int TryReadMainMenuSaveBiome()
        {
            try
            {
                if (SaveStore.IsMultiplayerContextActive())
                {
                    if (!MpSaveLocator.HasMpSave()) return -1;
                    string mpPath = MpSaveLocator.ResolveOwnSavePath() ?? MpSaveLocator.ResolveLocalPlayerSavePath();
                    if (!string.IsNullOrEmpty(mpPath) && File.Exists(mpPath))
                    {
                        SlotSidecar mpSc = SlotSidecar.LoadOrEmpty(mpPath);
                        if (mpSc.LoadedFromDisk && mpSc.Biome >= 0) return mpSc.Biome;
                        if (SaveStore.TryParseSvHeader(mpPath, out ParsedSvHeader mpHeader)
                            && mpHeader.RawBiome >= 0 && mpHeader.RawBiome <= 64)
                        {
                            return mpHeader.RawBiome;
                        }
                    }
                    return -1;
                }

                string spPath = SaveStore.GameSavePath;
                if (string.IsNullOrEmpty(spPath) || !File.Exists(spPath)) return -1;

                SlotSidecar spSc = SlotSidecar.LoadOrEmpty(spPath);
                if (spSc.LoadedFromDisk && spSc.Biome >= 0) return spSc.Biome;

                // 对齐原版 PreRunScript 主菜单读档预览逻辑：主菜单未消费存档的顶层 biome 即目标 0-based 层索引。
                if (SaveStore.TryParseSvHeader(spPath, out ParsedSvHeader spHeader)
                    && spHeader.RawBiome >= 0 && spHeader.RawBiome <= 64)
                {
                    return spHeader.RawBiome;
                }
            }
            catch
            {
            }
            return -1;
        }

        private static bool TryDrawQolSpriteBanner(Rect innerRect, int biomeIndex)
        {
            if (!QolBridge.TryGetLayerBannerSprite(biomeIndex, out Sprite sprite) || sprite == null)
            {
                return false;
            }

            Texture2D tex = sprite.texture;
            if (tex == null || tex.width <= 0 || tex.height <= 0) return false;

            Rect tr = sprite.textureRect;
            if (tr.width <= 0.5f || tr.height <= 0.5f) return false;

            float baseX = tr.x / tex.width;
            float baseY = tr.y / tex.height;
            float baseW = tr.width / tex.width;
            float baseH = tr.height / tex.height;

            // 等比居中铺满裁切（等价 QoL AspectRatioFitter.EnvelopeParent），保证宽扁存档卡上画面不变形。
            float cardAspect = innerRect.width / Mathf.Max(1f, innerRect.height);
            float spriteAspect = tr.width / tr.height;
            Rect uv;
            if (spriteAspect > cardAspect)
            {
                float keep = cardAspect / spriteAspect;
                float w = baseW * keep;
                uv = new Rect(baseX + (baseW - w) * 0.5f, baseY, w, baseH);
            }
            else
            {
                float keep = spriteAspect / cardAspect;
                float h = baseH * keep;
                uv = new Rect(baseX, baseY + (baseH - h) * 0.5f, baseW, h);
            }

            Color prev = GUI.color;
            GUI.color = Color.white;
            GUI.DrawTextureWithTexCoords(innerRect, tex, uv, true);
            GUI.color = prev;
            return true;
        }

        private static bool TryDrawVanillaFallbackBanner(Rect innerRect, int biomeIndex)
        {
            int clamped = Mathf.Clamp(biomeIndex, 0, VanillaBiomeBackgroundNames.Length - 1);
            string bgName = VanillaBiomeBackgroundNames[clamped];
            Texture2D tiledTex = GetOrCreateRepeatableVanillaTexture(bgName);
            if (tiledTex == null || tiledTex.width <= 0 || tiledTex.height <= 0) return false;

            float tileW = tiledTex.width * FallbackTilePixelScale;
            float tileH = tiledTex.height * FallbackTilePixelScale;
            var uv = new Rect(0f, 0f, innerRect.width / tileW, innerRect.height / tileH);

            Color prev = GUI.color;
            GUI.color = Color.white;
            GUI.DrawTextureWithTexCoords(innerRect, tiledTex, uv, true);
            GUI.color = prev;
            return true;
        }

        private static Texture2D GetOrCreateRepeatableVanillaTexture(string resourceName)
        {
            if (string.IsNullOrEmpty(resourceName) || _failedFallbackNames.Contains(resourceName))
            {
                return null;
            }
            if (_fallbackTiledCache.TryGetValue(resourceName, out Texture2D cached) && cached != null)
            {
                return cached;
            }

            Texture2D srcTex = null;
            Rect subRect = default;

            Sprite sp = Resources.Load<Sprite>(resourceName);
            if (sp != null && sp.texture != null)
            {
                srcTex = sp.texture;
                subRect = sp.textureRect;
            }
            else
            {
                srcTex = Resources.Load<Texture2D>(resourceName) ?? FindLoadedTextureByName(resourceName);
                if (srcTex != null)
                {
                    subRect = new Rect(0f, 0f, srcTex.width, srcTex.height);
                }
            }

            if (srcTex == null || subRect.width <= 0.5f || subRect.height <= 0.5f)
            {
                _failedFallbackNames.Add(resourceName);
                return null;
            }

            // 原版背景贴图多为 Clamp 且不可 CPU 直读，通过临时 RenderTexture 拷贝自有 Repeat+Point 副本，不改动原资产。
            Texture2D clone = CloneRepeatablePointTexture(srcTex, subRect);
            if (clone == null)
            {
                _failedFallbackNames.Add(resourceName);
                return null;
            }

            _fallbackTiledCache[resourceName] = clone;
            return clone;
        }

        private static Texture2D FindLoadedTextureByName(string texName)
        {
            Texture2D[] all = Resources.FindObjectsOfTypeAll<Texture2D>();
            for (int i = 0; i < all.Length; i++)
            {
                Texture2D candidate = all[i];
                if (candidate != null && string.Equals(candidate.name, texName, StringComparison.Ordinal))
                {
                    return candidate;
                }
            }
            return null;
        }

        private static Texture2D CloneRepeatablePointTexture(Texture2D srcTex, Rect subRect)
        {
            int w = Mathf.Max(1, Mathf.RoundToInt(subRect.width));
            int h = Mathf.Max(1, Mathf.RoundToInt(subRect.height));

            RenderTexture prevActive = RenderTexture.active;
            RenderTexture rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            try
            {
                Vector2 scale = new Vector2(subRect.width / srcTex.width, subRect.height / srcTex.height);
                Vector2 offset = new Vector2(subRect.x / srcTex.width, subRect.y / srcTex.height);
                Graphics.Blit(srcTex, rt, scale, offset);

                RenderTexture.active = rt;
                var copy = new Texture2D(w, h, TextureFormat.RGBA32, false)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Repeat,
                };
                copy.ReadPixels(new Rect(0f, 0f, w, h), 0, 0, false);
                copy.Apply(false, false);
                return copy;
            }
            catch (Exception ex)
            {
                ModLog.Warning($"克隆原生背景贴图失败 ({srcTex.name})：{ex.Message}");
                return null;
            }
            finally
            {
                RenderTexture.active = prevActive;
                RenderTexture.ReleaseTemporary(rt);
            }
        }
    }
}
