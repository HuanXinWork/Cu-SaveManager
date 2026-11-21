using System;
using System.Collections;
using BepInEx.Bootstrap;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>异步检查 GitHub 最新 Release 版本，存在更新且开关启用时在主菜单左上角绘制跳转提示。</summary>
    [DisallowMultipleComponent]
    public class UpdateChecker : MonoBehaviour
    {
        private const string PluginGuid = "com.casualtiesUnknown.saveManager";
        private const string ApiUrl = "https://api.github.com/repos/HuanXinWork/Cu-SaveManager/releases/latest";
        private const string ReleasesUrl = "https://github.com/HuanXinWork/Cu-SaveManager/releases";
        private const int UpdateNoticeSlot = 1;
        private const string MainMenuScene = "PreGen";
        private const int RequestTimeoutSeconds = 10;

        internal static bool Enabled = true;

        private static bool _checked;
        private static string _currentVersion = "";
        private static string _latestTag = "";
        private static bool _updateAvailable;

        private UnityWebRequest _activeRequest;
        private GUIStyle _noticeStyle;
        private Texture2D _noticeBg;
        private Texture2D _noticeHoverBg;
        private Texture2D _noticeActiveBg;
        private readonly GUIContent _noticeContent = new GUIContent();
        private bool _inPreGenScene;
        private string _cachedTemplate;
        private string _cachedTag;
        private string _cachedNoticeText;

        private void Awake()
        {
            _inPreGenScene = SceneManager.GetActiveScene().name == MainMenuScene;
            SceneManager.activeSceneChanged += OnActiveSceneChanged;
        }

        private void Start()
        {
            TryStartCheck();
        }

        private void OnDestroy()
        {
            SceneManager.activeSceneChanged -= OnActiveSceneChanged;
            DisposeActiveRequest(abort: true);
            DestroyTexture(ref _noticeBg);
            DestroyTexture(ref _noticeHoverBg);
            DestroyTexture(ref _noticeActiveBg);
            _noticeStyle = null;
        }

        private void OnActiveSceneChanged(Scene previousScene, Scene newScene)
        {
            _inPreGenScene = newScene.name == MainMenuScene;
        }

        private void TryStartCheck()
        {
            if (_checked || !Enabled) return;
            _checked = true;
            try
            {
                if (Chainloader.PluginInfos.TryGetValue(PluginGuid, out var info))
                    _currentVersion = info.Metadata.Version.ToString();
            }
            catch (Exception ex)
            {
                ModLog.Warning("UpdateChecker init failed: " + ex.Message);
                return;
            }
            StartCoroutine(CheckForUpdates());
        }

        private IEnumerator CheckForUpdates()
        {
            UnityWebRequest www = UnityWebRequest.Get(ApiUrl);
            _activeRequest = www;
            try
            {
                www.timeout = RequestTimeoutSeconds;
                www.SetRequestHeader("User-Agent", "Cu-SaveManager");
                yield return www.SendWebRequest();

                if (_activeRequest != www) yield break;

                if (www.result != UnityWebRequest.Result.Success)
                {
                    ModLog.Warning("Update check failed: " + www.error);
                    yield break;
                }

                string json = www.downloadHandler?.text;
                if (!TopJson.TryReadString(json, "tag_name", out string tag) || string.IsNullOrEmpty(tag))
                    yield break;

                _latestTag = tag;
                if (IsNewer(_currentVersion, _latestTag))
                {
                    _updateAvailable = true;
                    ModLog.Warning($"Update available: {_currentVersion} -> {_latestTag}");
                }
                else
                {
                    ModLog.Info($"Up to date. Current: {_currentVersion}, Latest: {_latestTag}");
                }
            }
            finally
            {
                if (_activeRequest == www)
                    DisposeActiveRequest(abort: false);
            }
        }

        private void DisposeActiveRequest(bool abort)
        {
            UnityWebRequest req = _activeRequest;
            if (req == null) return;
            _activeRequest = null;
            if (abort)
            {
                try { req.Abort(); } catch { }
            }
            try { req.Dispose(); } catch { }
        }

        private static bool IsNewer(string current, string latest)
            => TryParseNormalizedVersion(current, out Version vc)
               && TryParseNormalizedVersion(latest, out Version vl)
               && vl > vc;

        private static bool TryParseNormalizedVersion(string raw, out Version normalized)
        {
            normalized = null;
            if (string.IsNullOrWhiteSpace(raw)) return false;

            string s = raw.Trim().TrimStart('v', 'V').Trim();
            int suffix = s.IndexOfAny(new[] { '-', '+' });
            if (suffix >= 0)
                s = s.Substring(0, suffix).Trim();

            if (!Version.TryParse(s, out Version parsed)) return false;
            int build = parsed.Build < 0 ? 0 : parsed.Build;
            int revision = parsed.Revision < 0 ? 0 : parsed.Revision;
            normalized = new Version(parsed.Major, parsed.Minor, build, revision);
            return true;
        }

        private void OnGUI()
        {
            if (!Enabled) return;
            if (!_checked) TryStartCheck();
            if (!_updateAvailable || !_inPreGenScene) return;

            EnsureNoticeStyle();

            string template = I18n.T("update.available");
            if (_cachedNoticeText == null
                || !ReferenceEquals(_cachedTemplate, template)
                || !string.Equals(_cachedTag, _latestTag, StringComparison.Ordinal))
            {
                _cachedTemplate = template;
                _cachedTag = _latestTag;
                _cachedNoticeText = I18n.F("update.available", _latestTag);
            }

            float x = 32f;
            float y = Screen.height * 0.12f + UpdateNoticeSlot * 48f;
            _noticeContent.text = _cachedNoticeText;
            Vector2 size = _noticeStyle.CalcSize(_noticeContent);
            float width = Mathf.Ceil(size.x) + 8f;
            float height = Mathf.Max(46f, Mathf.Ceil(size.y) + 4f);
            var rect = new Rect(x, y, width, height);
            if (GUI.Button(rect, _noticeContent, _noticeStyle))
                Application.OpenURL(ReleasesUrl);
        }

        private void EnsureNoticeStyle()
        {
            if (_noticeStyle != null && _noticeBg != null) return;

            DestroyTexture(ref _noticeBg);
            DestroyTexture(ref _noticeHoverBg);
            DestroyTexture(ref _noticeActiveBg);

            // UpdateChecker 独立于主面板运行，没有外层 BlackWhiteSkin.Push，需自建半透明黑白线框纹理与样式。
            _noticeBg = MakeBorderedMonoTex(0, 150, 255, 220, 2);
            _noticeHoverBg = MakeBorderedMonoTex(60, 190, 255, 255, 2);
            _noticeActiveBg = MakeBorderedMonoTex(90, 210, 255, 255, 2);

            _noticeStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 20,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                wordWrap = false,
                clipping = TextClipping.Clip,
                richText = false,
                border = new RectOffset(2, 2, 2, 2),
                padding = new RectOffset(14, 14, 7, 7),
                margin = new RectOffset(0, 0, 0, 0),
            };
            _noticeStyle.normal.background = _noticeBg;
            _noticeStyle.onNormal.background = _noticeBg;
            _noticeStyle.focused.background = _noticeBg;
            _noticeStyle.onFocused.background = _noticeBg;
            _noticeStyle.hover.background = _noticeHoverBg;
            _noticeStyle.onHover.background = _noticeHoverBg;
            _noticeStyle.active.background = _noticeActiveBg;
            _noticeStyle.onActive.background = _noticeActiveBg;

            _noticeStyle.normal.textColor = Color.white;
            _noticeStyle.onNormal.textColor = Color.white;
            _noticeStyle.focused.textColor = Color.white;
            _noticeStyle.onFocused.textColor = Color.white;
            _noticeStyle.hover.textColor = Color.white;
            _noticeStyle.onHover.textColor = Color.white;
            _noticeStyle.active.textColor = Color.white;
            _noticeStyle.onActive.textColor = Color.white;
        }

        private static Texture2D MakeBorderedMonoTex(byte fillLum, byte fillAlpha, byte borderLum, byte borderAlpha, int borderPx)
        {
            int size = borderPx * 2 + 4;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            var fill = new Color32(fillLum, fillLum, fillLum, fillAlpha);
            var edge = new Color32(borderLum, borderLum, borderLum, borderAlpha);
            var pixels = new Color32[size * size];
            for (int py = 0; py < size; py++)
            {
                for (int px = 0; px < size; px++)
                {
                    bool isEdge = px < borderPx || px >= size - borderPx || py < borderPx || py >= size - borderPx;
                    pixels[py * size + px] = isEdge ? edge : fill;
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply();
            return tex;
        }

        private static void DestroyTexture(ref Texture2D tex)
        {
            if (tex == null) return;
            try { UnityEngine.Object.Destroy(tex); } catch { }
            tex = null;
        }
    }
}
