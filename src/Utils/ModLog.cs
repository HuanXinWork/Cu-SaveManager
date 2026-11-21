using System.Diagnostics;
using System.Reflection;
using BepInEx.Logging;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>统一日志出口：BepInEx 通道直接利用源标签避免重复前缀，游戏内控制台统一加 [SaveManager] 前缀。</summary>
    internal static class ModLog
    {
        private const string ConsolePrefix = "[SaveManager] ";

        private static ManualLogSource _sink;
        private static MethodInfo _consoleLog;
        private static bool _consoleResolved;

        internal static bool ShowInConsole { get; set; }

        internal static void Init(ManualLogSource sink) => _sink = sink;

        internal static void Info(string message)
        {
            _sink?.LogInfo(message);
            WriteToConsole(message, null);
        }

        internal static void Warning(string message)
        {
            _sink?.LogWarning(message);
            WriteToConsole(message, "yellow");
        }

        internal static void Error(string message)
        {
            _sink?.LogError(message);
            WriteToConsole(message, "red");
        }

        /// <summary>BepInEx 默认屏蔽 LogDebug，故走 LogInfo 加 [DBG] 标记落盘。</summary>
        [Conditional("DEBUG")]
        internal static void Debug(string message)
        {
#if DEBUG
            _sink?.LogInfo("[DBG] " + message);
#endif
        }

        [Conditional("DEBUG")]
        internal static void Flow(string scope, string step, string message)
        {
#if DEBUG
            _sink?.LogInfo("[DBG][" + scope + "] " + step + " " + message);
#endif
        }

        private static void WriteToConsole(string message, string color)
        {
            if (!ShowInConsole) return;
            try
            {
                var instance = ConsoleScript.instance;
                if (instance == null || !ResolveConsoleLog()) return;
                string text = string.IsNullOrEmpty(color)
                    ? ConsolePrefix + message
                    : $"<color={color}>{ConsolePrefix}{message}</color>";
                _consoleLog.Invoke(instance, new object[] { text });
            }
            catch { }
        }

        private static bool ResolveConsoleLog()
        {
            if (_consoleResolved) return _consoleLog != null;
            _consoleResolved = true;
            _consoleLog = typeof(ConsoleScript).GetMethod("LogToConsole",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                null, new[] { typeof(string) }, null);
            return _consoleLog != null;
        }
    }
}
