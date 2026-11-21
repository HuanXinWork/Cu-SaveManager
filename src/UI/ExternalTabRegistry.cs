using System;
using System.Collections.Generic;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>外部模组设置分页注册表，隔离第三方委托异常并基于调用方类型或显式 modId 幂等去重。</summary>
    public static class ExternalTabRegistry
    {
        public sealed class Entry
        {
            public Func<string> TitleFn;
            public Action Draw;
            public Func<string> StatusFn;
            internal string ModId;
            internal string CallerKey;

            public string Title
            {
                get
                {
                    if (TitleFn == null) return string.Empty;
                    try
                    {
                        return TitleFn() ?? string.Empty;
                    }
                    catch (Exception ex)
                    {
                        WarnCallbackExceptionOnce("Title", TitleFn, ex);
                        return "[Tab Error]";
                    }
                }
            }

            public string Status
            {
                get
                {
                    if (StatusFn == null) return string.Empty;
                    try
                    {
                        return StatusFn() ?? string.Empty;
                    }
                    catch (Exception ex)
                    {
                        WarnCallbackExceptionOnce("Status", StatusFn, ex);
                        return string.Empty;
                    }
                }
            }
        }

        private static readonly List<Entry> _entries = new List<Entry>();
        private static readonly HashSet<string> _warnedCallbacks = new HashSet<string>(StringComparer.Ordinal);

        private static Func<int> _getScalePercent;
        private static Action<int> _setScalePercent;
        private static int _fallbackScalePercent = 100;

        /// <summary>全局共享界面缩放百分比（75..150），供宿主与已安装附属模组反射同步读写。</summary>
        public static int UiScalePercent
        {
            get => UnityEngine.Mathf.Clamp(_getScalePercent != null ? _getScalePercent() : _fallbackScalePercent, 75, 150);
            set
            {
                int clamped = UnityEngine.Mathf.Clamp(value, 75, 150);
                _fallbackScalePercent = clamped;
                _setScalePercent?.Invoke(clamped);
            }
        }

        internal static void BindUiScaleConfig(Func<int> getter, Action<int> setter)
        {
            _getScalePercent = getter;
            _setScalePercent = setter;
        }

        public static void Register(string title, Action draw)
            => Register(() => title, draw, null);

        public static void Register(Func<string> titleFn, Action draw)
            => Register(titleFn, draw, null);

        public static void Register(Func<string> titleFn, Action draw, Func<string> statusFn)
        {
            if (titleFn == null || draw == null) return;

            // 以委托声明类型全名作为跨语言切换的稳定去重键，避免切换界面语言后再次注册产生重复页签。
            string callerKey = draw.Method?.DeclaringType?.FullName;
            string initialTitle;
            try
            {
                initialTitle = titleFn() ?? string.Empty;
            }
            catch (Exception ex)
            {
                ModLog.Warning($"ExternalTabRegistry.Register 调用 titleFn 失败：{ex.Message}");
                return;
            }

            for (int i = 0; i < _entries.Count; i++)
            {
                Entry e = _entries[i];
                bool matchCaller = !string.IsNullOrEmpty(callerKey)
                    && string.Equals(e.CallerKey, callerKey, StringComparison.Ordinal);
                bool matchTitle = string.IsNullOrEmpty(callerKey)
                    && string.IsNullOrEmpty(e.CallerKey)
                    && string.Equals(e.Title, initialTitle, StringComparison.Ordinal);

                if (matchCaller || matchTitle)
                {
                    e.TitleFn = titleFn;
                    e.Draw = draw;
                    e.StatusFn = statusFn;
                    e.CallerKey = callerKey;
                    return;
                }
            }

            _entries.Add(new Entry
            {
                TitleFn = titleFn,
                Draw = draw,
                StatusFn = statusFn,
                CallerKey = callerKey,
            });
        }

        public static void RegisterWithId(string modId, Func<string> titleFn, Action draw, Func<string> statusFn = null)
        {
            if (string.IsNullOrWhiteSpace(modId))
            {
                Register(titleFn, draw, statusFn);
                return;
            }
            if (titleFn == null || draw == null) return;

            string normalizedId = modId.Trim();
            string callerKey = draw.Method?.DeclaringType?.FullName;

            for (int i = 0; i < _entries.Count; i++)
            {
                Entry e = _entries[i];
                bool matchId = string.Equals(e.ModId, normalizedId, StringComparison.OrdinalIgnoreCase);
                bool matchCaller = string.IsNullOrEmpty(e.ModId)
                    && !string.IsNullOrEmpty(callerKey)
                    && string.Equals(e.CallerKey, callerKey, StringComparison.Ordinal);

                if (matchId || matchCaller)
                {
                    e.ModId = normalizedId;
                    e.CallerKey = callerKey;
                    e.TitleFn = titleFn;
                    e.Draw = draw;
                    e.StatusFn = statusFn;
                    return;
                }
            }

            _entries.Add(new Entry
            {
                ModId = normalizedId,
                CallerKey = callerKey,
                TitleFn = titleFn,
                Draw = draw,
                StatusFn = statusFn,
            });
        }

        public static bool Unregister(string modId)
        {
            if (string.IsNullOrWhiteSpace(modId)) return false;
            string normalizedId = modId.Trim();
            int removed = 0;
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                Entry e = _entries[i];
                if (string.Equals(e.ModId, normalizedId, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(e.CallerKey, normalizedId, StringComparison.Ordinal))
                {
                    _entries.RemoveAt(i);
                    removed++;
                }
            }
            return removed > 0;
        }

        private static void WarnCallbackExceptionOnce(string member, Delegate fn, Exception ex)
        {
            string id = member + ":" + (fn?.Method?.DeclaringType?.FullName ?? "?") + "." + (fn?.Method?.Name ?? "?");
            if (_warnedCallbacks.Add(id))
            {
                ModLog.Warning($"外部模组页签 {id} 委托异常：{ex.Message}");
            }
        }

        internal static IReadOnlyList<Entry> Entries => _entries;
        internal static int Count => _entries.Count;
    }
}
