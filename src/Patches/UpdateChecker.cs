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
        private readonly GUIContent _noticeContent = new GUIContent();

        private void Start()
        {
            TryStartCheck();
        }

        private void OnDestroy()
        {
            DisposeActiveRequest(abort: true);
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
            normalized = new Version(parsed.Major, parsed.Minor, build);
            return true;
        }

        private void OnGUI()
        {
            if (!Enabled) return;
            if (!_checked) TryStartCheck();
            if (!_updateAvailable) return;
            if (SceneManager.GetActiveScene().name != MainMenuScene) return;

            if (_noticeStyle == null)
            {
                _noticeStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 26,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.UpperLeft,
                    richText = false,
                    normal = { textColor = new Color(1f, 0.3f, 0.3f) },
                    hover = { textColor = new Color(1f, 0.55f, 0.55f) },
                    active = { textColor = new Color(1f, 0.2f, 0.2f) },
                };
            }

            string text = I18n.F("update.available", _latestTag);
            float x = 32f;
            float y = Screen.height * 0.12f + UpdateNoticeSlot * 40f;
            _noticeContent.text = text;
            Vector2 size = _noticeStyle.CalcSize(_noticeContent);
            var rect = new Rect(x, y, size.x + 8f, size.y + 4f);
            if (GUI.Button(rect, _noticeContent, _noticeStyle))
                Application.OpenURL(ReleasesUrl);
        }
    }
}
