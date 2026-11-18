using UnityEngine;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>面板关闭或退出文本输入态时，在 IMGUI Layout 阶段清除残留键盘与输入法焦点。</summary>
    internal static class ImGuiImeRecovery
    {
        private static bool _pendingClear;

        internal static void RequestClear() => _pendingClear = true;

        /// <summary>当面板处于文本输入态时取消待执行的焦点清除请求。</summary>
        internal static void TickUpdate(bool textInputExpected)
        {
            if (textInputExpected) _pendingClear = false;
        }

        /// <summary>在非文本输入态的 Layout 阶段清空键盘控件焦点。</summary>
        internal static void TickOnGui(bool textInputExpected)
        {
            if (textInputExpected || !_pendingClear) return;

            var ev = Event.current;
            if (ev == null || ev.type != EventType.Layout) return;

            GUI.FocusControl(null);
            GUIUtility.keyboardControl = 0;
            _pendingClear = false;
        }
    }
}
