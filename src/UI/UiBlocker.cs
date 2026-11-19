using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>面板打开期间禁用全部 GraphicRaycaster，阻止点击穿透到下层 uGUI。</summary>
    internal static class UiBlocker
    {
        private const float RescanIntervalSeconds = 0.5f;

        private static readonly List<(GraphicRaycaster gr, bool wasEnabled)> _disabled
            = new List<(GraphicRaycaster, bool)>();
        private static readonly HashSet<GraphicRaycaster> _tracked
            = new HashSet<GraphicRaycaster>();

        private static bool _isBlocking;
        private static float _nextRescanAt;

        /// <summary>面板是否处于阻穿透状态。</summary>
        internal static bool IsBlocking => _isBlocking;

        internal static void Block()
        {
            try
            {
                if (!_isBlocking)
                {
                    _disabled.Clear();
                    _tracked.Clear();
                    SceneManager.sceneLoaded -= OnSceneLoaded;
                    SceneManager.sceneLoaded += OnSceneLoaded;
                    _isBlocking = true;
                }

                ScanAndDisableRaycasters();
                _nextRescanAt = Time.unscaledTime + RescanIntervalSeconds;
            }
            catch (System.Exception ex)
            {
                ModLog.Warning($"UiBlocker.Block 失败：{ex.Message}");
            }
        }

        /// <summary>维持所有受管 GraphicRaycaster 为禁用状态，并定期接管动态新建画布。</summary>
        internal static void EnforceBlocked()
        {
            if (!_isBlocking) return;
            try
            {
                if (Time.unscaledTime >= _nextRescanAt)
                {
                    PruneDestroyed();
                    ScanAndDisableRaycasters();
                    _nextRescanAt = Time.unscaledTime + RescanIntervalSeconds;
                    return;
                }

                for (int i = 0; i < _disabled.Count; i++)
                {
                    var gr = _disabled[i].gr;
                    if (gr != null && gr.enabled) gr.enabled = false;
                }
            }
            catch { }
        }

        internal static void Unblock()
        {
            try
            {
                SceneManager.sceneLoaded -= OnSceneLoaded;
                for (int i = 0; i < _disabled.Count; i++)
                {
                    var (gr, was) = _disabled[i];
                    if (gr != null) gr.enabled = was;
                }
                _disabled.Clear();
                _tracked.Clear();
                _isBlocking = false;
            }
            catch (System.Exception ex)
            {
                ModLog.Warning($"UiBlocker.Unblock 失败：{ex.Message}");
            }
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!_isBlocking) return;
            try
            {
                PruneDestroyed();
                ScanAndDisableRaycasters();
                _nextRescanAt = Time.unscaledTime + RescanIntervalSeconds;
            }
            catch { }
        }

        private static void ScanAndDisableRaycasters()
        {
            var all = Object.FindObjectsOfType<GraphicRaycaster>(includeInactive: true);
            for (int i = 0; i < all.Length; i++)
            {
                var gr = all[i];
                if (gr == null) continue;
                if (_tracked.Add(gr))
                {
                    _disabled.Add((gr, gr.enabled));
                }
                gr.enabled = false;
            }
        }

        private static void PruneDestroyed()
        {
            for (int i = _disabled.Count - 1; i >= 0; i--)
            {
                if (_disabled[i].gr == null) _disabled.RemoveAt(i);
            }
            _tracked.RemoveWhere(gr => gr == null);
        }
    }
}
