using System;
using System.Collections.Generic;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>外部模组设置分页注册表。</summary>
    public static class ExternalTabRegistry
    {
        public sealed class Entry
        {
            public Func<string> TitleFn;
            public Action Draw;
            public Func<string> StatusFn;
            public string Title => TitleFn != null ? TitleFn() : "";
            public string Status => StatusFn != null ? (StatusFn() ?? "") : "";
        }

        private static readonly List<Entry> _entries = new List<Entry>();

        public static void Register(string title, Action draw)
            => Register(() => title, draw, null);

        public static void Register(Func<string> titleFn, Action draw)
            => Register(titleFn, draw, null);

        public static void Register(Func<string> titleFn, Action draw, Func<string> statusFn)
        {
            if (titleFn == null || draw == null) return;
            string key = titleFn();
            foreach (var e in _entries)
            {
                if (e.Title == key) { e.TitleFn = titleFn; e.Draw = draw; e.StatusFn = statusFn; return; }
            }
            _entries.Add(new Entry { TitleFn = titleFn, Draw = draw, StatusFn = statusFn });
        }

        internal static IReadOnlyList<Entry> Entries => _entries;
        internal static int Count => _entries.Count;
    }
}
