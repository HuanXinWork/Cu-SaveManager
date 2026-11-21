using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>面板阻挡或光标位于注入的主菜单按钮上时，令 <c>AdaptiveButton.overlayActive</c> 返回 true 以抑制底层按钮悬停响应。</summary>
    [HarmonyPatch(typeof(AdaptiveButton), "overlayActive", MethodType.Getter)]
    internal static class AdaptiveButtonOverlayActiveGuard
    {
        private static int _cachedFrame = -1;
        private static bool _cachedPointerOver;
        private static RectTransform _cachedRect;
        private static Canvas _cachedRootCanvas;

        private static void Postfix(ref bool __result)
        {
            if (UiBlocker.IsBlocking || IsPointerOverInjectedButton())
                __result = true;
        }

        internal static bool IsPointerOverInjectedButton()
        {
            RectTransform rt = MenuButtonUiInjector.InjectedRect;
            if (rt == null)
            {
                _cachedRect = null;
                _cachedRootCanvas = null;
                return false;
            }

            int frame = Time.frameCount;
            if (_cachedFrame == frame && ReferenceEquals(_cachedRect, rt))
            {
                return _cachedPointerOver;
            }

            _cachedFrame = frame;
            try
            {
                if (!ReferenceEquals(_cachedRect, rt) || _cachedRootCanvas == null)
                {
                    _cachedRect = rt;
                    Canvas canvas = rt.GetComponentInParent<Canvas>();
                    _cachedRootCanvas = canvas != null ? canvas.rootCanvas : null;
                }

                Camera cam = _cachedRootCanvas == null || _cachedRootCanvas.renderMode == RenderMode.ScreenSpaceOverlay
                    ? null
                    : _cachedRootCanvas.worldCamera;
                _cachedPointerOver = RectTransformUtility.RectangleContainsScreenPoint(rt, Input.mousePosition, cam);
                return _cachedPointerOver;
            }
            catch
            {
                _cachedPointerOver = false;
                return false;
            }
        }
    }

    /// <summary>面板阻挡或光标位于注入的主菜单按钮上时拦截 <c>AdaptiveButton.Clicked</c> 点击穿透。</summary>
    [HarmonyPatch]
    internal static class AdaptiveButtonClickedGuard
    {
        private static bool _logged;
        private static bool _patchLogged;

        private static MethodBase TargetMethod()
        {
            var m = AccessTools.Method(typeof(AdaptiveButton), "Clicked");
            if (m != null && !_patchLogged)
            {
                _patchLogged = true;
                ModLog.Debug("AdaptiveButtonClickedGuard.TargetMethod 命中 AdaptiveButton.Clicked");
            }
            return m;
        }

        private static bool Prefix()
        {
            if (UiBlocker.IsBlocking) return false;
            if (AdaptiveButtonOverlayActiveGuard.IsPointerOverInjectedButton())
            {
                if (!_logged)
                {
                    _logged = true;
                    ModLog.Debug("AdaptiveButtonClickedGuard 首次拦截穿透点击");
                }
                return false;
            }
            return true;
        }
    }

    /// <summary>面板阻挡期间拦截 <c>PlayerCamera.HandleInput</c> 角色操作与按键，仅保留视线跟随鼠标。</summary>
    [HarmonyPatch(typeof(PlayerCamera), "HandleInput")]
    internal static class PlayerCameraHandleInputGuard
    {
        [HarmonyPrefix]
        private static bool Prefix()
        {
            if (!UiBlocker.IsBlocking) return true;
            try
            {
                if ((ConsoleScript.instance == null || !ConsoleScript.instance.active)
                    && PlayerCamera.main != null && PlayerCamera.main.body != null && Camera.main != null)
                {
                    PlayerCamera.main.body.targetLookPos = Camera.main.ScreenToWorldPoint(Input.mousePosition);
                }
            }
            catch { }
            return false;
        }
    }

    /// <summary>面板阻挡期间拦截 <c>PlayerCamera.HandleMouseOver</c> 与 <c>HandleDragging</c>，并清理悬停框与拖拽残留状态。</summary>
    [HarmonyPatch]
    internal static class PlayerCameraMouseGuard
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            MethodInfo mouseOver = AccessTools.Method(typeof(PlayerCamera), "HandleMouseOver");
            if (mouseOver != null) yield return mouseOver;

            MethodInfo dragging = AccessTools.Method(typeof(PlayerCamera), "HandleDragging");
            if (dragging != null) yield return dragging;
        }

        [HarmonyPrefix]
        private static bool Prefix(PlayerCamera __instance)
        {
            if (!UiBlocker.IsBlocking) return true;
            if (__instance != null)
            {
                __instance.dragItem = null;
                if (__instance.hoverSquare != null) __instance.hoverSquare.enabled = false;
                if (__instance.dragImage != null) __instance.dragImage.enabled = false;
            }
            return false;
        }
    }
}
