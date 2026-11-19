using System.Diagnostics;
using System.Reflection;
using BepInEx.Logging;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>统一日志出口，输出到 BepInEx 日志并按配置同步到游戏内控制台。</summary>
    internal static class ModLog
    {
        private const string Prefix = "[SaveManager] ";

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

        /// <summary>Debug 构建诊断日志；BepInEx 默认屏蔽 LogDebug，故走 LogInfo 写入日志文件。</summary>
        [Conditional("DEBUG")]
        internal static void Debug(string message)
        {
#if DEBUG
            _sink?.LogInfo(Prefix + "[DBG] " + message);
#endif
        }

        /// <summary>Debug 构建流程步骤日志。</summary>
        [Conditional("DEBUG")]
        internal static void Flow(string scope, string step, string message)
        {
#if DEBUG
            _sink?.LogInfo(Prefix + "[DBG][" + scope + "] " + step + " " + message);
#endif
        }

        private static void WriteToConsole(string message, string color)
        {
            if (!ShowInConsole) return;
            try
            {
                var instance = ConsoleScript.instance;
                if (instance == null || !ResolveConsoleLog()) return;
                string text = string.IsNullOrEmpty(color) ? Prefix + message : $"<color={color}>{Prefix}{message}</color>";
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
