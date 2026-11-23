using System;
using UnityEngine;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>游戏内暂停菜单打开时在屏幕右下角绘制的快捷入口按钮，并提供全局暂停状态探针。</summary>
    internal sealed class OverlayUI
    {
        private const float ButtonWidth = 320f;
        private const float ButtonHeight = 84f;
        private const float MarginRight = 24f;
        private const float MarginBottom = 24f;
        private const float BorderThickness = 5f;

        /// <summary>强类型读取原版 PauseHandler 判断游戏当前是否处于暂停菜单态。</summary>
        internal static bool IsGamePaused()
        {
            try
            {
                return PauseHandler.paused
                    || (PauseHandler.main != null
                        && PauseHandler.main.pauseContainer != null
                        && PauseHandler.main.pauseContainer.activeSelf);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>仅在暂停表盘按钮未挂载、游戏暂停菜单可见且原生设置页与本面板均未打开时回退显示。</summary>
        internal static bool ShouldShow()
        {
            // 表盘环形按钮正常挂载后由其接管局内入口，此处仅作为挂载异常时的保底回退。
            if (PauseDialButtonInjector.IsMounted) return false;
            if (PlayerCamera.main == null || SettingsMenu.instance != null || UiBlocker.IsBlocking) return false;
            return IsGamePaused();
        }

        internal void Draw(Action onClick)
        {
            if (!ShouldShow()) return;

            float x = Screen.width - ButtonWidth - MarginRight;
            float y = Screen.height - ButtonHeight - MarginBottom;
            var rect = new Rect(x, y, ButtonWidth, ButtonHeight);

            BlackWhiteSkin.Push();
            try
            {
                if (GUI.Button(rect, I18n.T("app.menu_button"), BlackWhiteSkin.OverlayBtnStyle))
                {
                    onClick?.Invoke();
                }
                BlackWhiteSkin.DrawBorder(rect, BorderThickness);
            }
            finally
            {
                BlackWhiteSkin.Pop();
            }
        }
    }
}
