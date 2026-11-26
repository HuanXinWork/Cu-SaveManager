using System;
using HarmonyLib;
using UnityEngine;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>按配置将单机局内暂停菜单原版放弃圆形按钮热替换为门形退出按钮（支持存档并退出或只退出不存档）。</summary>
    internal static class PauseQuitButtonReplacer
    {
        private static readonly EventHandler ConfigChangedHandler = OnConfigChanged;
        // 待机柔和警示红与二次点击待确认亮红，用于与纯白存档退出按钮形成明确视觉区分。
        private static readonly Color ExitOnlyIdleColor = new Color(1f, 0.58f, 0.52f, 1f);
        private static readonly Color ExitOnlyArmedColor = new Color(1f, 0.22f, 0.22f, 1f);

        private static HotkeyConfig _cfg;
        private static Action _onSaveAndExit;
        private static Action _onExitWithoutSave;

        private static PauseHandler _boundHandler;
        private static Sprite _originalQuitSprite;
        private static bool _isReplaced;
        private static bool _exitOnlyArmed;

        internal static bool IsExitOnlyArmed => _exitOnlyArmed;
        internal static bool IsReplaced => _isReplaced;

        internal static void Setup(HotkeyConfig cfg, Action onSaveAndExit, Action onExitWithoutSave)
        {
            UnsubscribeConfigEvents();
            _cfg = cfg;
            _onSaveAndExit = onSaveAndExit;
            _onExitWithoutSave = onExitWithoutSave;
            SubscribeConfigEvents();

            if (PauseHandler.main != null)
            {
                BindAndApply(PauseHandler.main);
            }
        }

        internal static void Dispose()
        {
            UnsubscribeConfigEvents();

            PauseHandler handler = PauseHandler.main ?? _boundHandler;
            if (_isReplaced && handler != null)
            {
                try { RestoreVanilla(handler); } catch { }
            }

            _cfg = null;
            _onSaveAndExit = null;
            _onExitWithoutSave = null;
            _boundHandler = null;
            _originalQuitSprite = null;
            _isReplaced = false;
            _exitOnlyArmed = false;
        }

        internal static void RefreshCurrent()
        {
            _exitOnlyArmed = false;
            if (PauseHandler.main == null) return;
            BindAndApply(PauseHandler.main);
        }

        internal static void OnPauseHandlerStarted(PauseHandler handler)
        {
            try
            {
                BindAndApply(handler);
            }
            catch (Exception ex)
            {
                ModLog.Warning($"暂停菜单放弃按钮替换初始化失败：{ex.Message}");
            }
        }

        internal static void OnResetQuitPressed(PauseHandler handler)
        {
            try
            {
                _exitOnlyArmed = false;
                if (!ShouldReplace(handler))
                {
                    // 条件不再满足（如切入联机、调试关或关闭开关）但仍残留替换态时，主动自愈还原原版图标与文案。
                    if (_isReplaced)
                    {
                        BindAndApply(handler);
                    }
                    return;
                }
                BindAndCaptureOriginal(handler);
                ApplyReplacement(handler);
            }
            catch (Exception ex)
            {
                ModLog.Warning($"暂停菜单退出按钮重刷失败：{ex.Message}");
            }
        }

        internal static void OnPauseToggled()
        {
            // 鼠标未移出按钮直接按 ESC 关闭暂停菜单时，原版可能未触发 ResetQuitPressed，需主动复位待确认态。
            if (PauseHandler.paused || !_exitOnlyArmed) return;

            _exitOnlyArmed = false;
            PauseHandler handler = PauseHandler.main;
            if (handler != null && ShouldReplace(handler))
            {
                ApplyReplacement(handler);
            }
        }

        internal static bool HandleTryQuitPrefix(PauseHandler handler)
        {
            if (!ShouldReplace(handler))
            {
                return true;
            }

            HandleReplacedQuitClick(handler);
            return false;
        }

        private static bool IsExitWithoutSaveEnabled()
        {
            return _cfg != null && _cfg.PauseQuitExitWithoutSave.Value;
        }

        private static void HandleReplacedQuitClick(PauseHandler handler)
        {
            try
            {
                if (handler != null)
                {
                    handler.timesQuitPressed = 0;
                }

                if (!IsExitWithoutSaveEnabled())
                {
                    _exitOnlyArmed = false;
                    _onSaveAndExit?.Invoke();
                    return;
                }

                if (!_exitOnlyArmed)
                {
                    ArmExitOnlyConfirmation(handler);
                    return;
                }

                // 二次确认后先复位状态与待机外观，再执行不存档退出，防止退出中断时残留亮红确认态。
                _exitOnlyArmed = false;
                if (handler != null)
                {
                    ApplyReplacement(handler);
                }
                _onExitWithoutSave?.Invoke();
            }
            catch (Exception ex)
            {
                ModLog.Warning($"暂停菜单退出按钮触发异常：{ex.Message}");
            }
        }

        private static void ArmExitOnlyConfirmation(PauseHandler handler)
        {
            _exitOnlyArmed = true;
            if (handler != null && handler.quitButton != null)
            {
                handler.quitButton.color = ExitOnlyArmedColor;
                SetTooltip(handler, "tip.pause_exit_only_confirm_name", "tip.pause_exit_only_confirm_desc");
            }
            try { PlayerCamera.PlayUISound("selfharmtap"); } catch { }
            ModLog.Debug("暂停菜单只退出不存档按钮进入二次确认态，等待再次点击");
        }

        private static void SubscribeConfigEvents()
        {
            if (_cfg == null) return;
            _cfg.ReplacePauseQuitWithSaveExit.SettingChanged += ConfigChangedHandler;
            _cfg.PauseQuitExitWithoutSave.SettingChanged += ConfigChangedHandler;
        }

        private static void UnsubscribeConfigEvents()
        {
            if (_cfg == null) return;
            _cfg.ReplacePauseQuitWithSaveExit.SettingChanged -= ConfigChangedHandler;
            _cfg.PauseQuitExitWithoutSave.SettingChanged -= ConfigChangedHandler;
        }

        private static void OnConfigChanged(object sender, EventArgs e)
        {
            _exitOnlyArmed = false;
            RefreshCurrent();
        }

        private static bool IsTutorialScene()
        {
            return WorldGeneration.world != null
                && WorldGeneration.world.biomeOverride == WorldGeneration.OverrideSceneType.Tutorial;
        }

        private static bool IsNonStandardScene()
        {
            return WorldGeneration.world != null
                && WorldGeneration.world.biomeOverride != WorldGeneration.OverrideSceneType.None;
        }

        private static bool ShouldReplace(PauseHandler handler)
        {
            if (_cfg == null || !_cfg.ReplacePauseQuitWithSaveExit.Value) return false;
            if (handler == null || handler.quitButton == null) return false;
            // 教程关与调试关保持游戏原版退出流程，避免干扰原版特殊场景逻辑。
            if (IsNonStandardScene()) return false;
            // 联机运行期间房主与客户端双端均交还 Together 与原版处理。
            if (MultiplayerBridge.IsMultiplayerRunning()) return false;
            return true;
        }

        private static void BindAndCaptureOriginal(PauseHandler handler)
        {
            if (handler == null) return;

            if (_boundHandler != handler)
            {
                _boundHandler = handler;
                _originalQuitSprite = null;
                _isReplaced = false;
                _exitOnlyArmed = false;
            }

            // 仅在非教程关且尚未被替换为 exitSprite 时记录原版骷髅贴图，防止热切换后丢失原图标。
            if (!IsTutorialScene()
                && _originalQuitSprite == null
                && handler.quitButton != null
                && handler.quitButton.sprite != null
                && handler.quitButton.sprite != handler.exitSprite)
            {
                _originalQuitSprite = handler.quitButton.sprite;
            }
        }

        private static void BindAndApply(PauseHandler handler)
        {
            _exitOnlyArmed = false;
            if (handler == null || handler.quitButton == null) return;

            BindAndCaptureOriginal(handler);
            if (ShouldReplace(handler))
            {
                ApplyReplacement(handler);
                return;
            }

            // 仅还原曾由本模块替换过的实例，避免在未启用或联机场景误覆写 Together 等其他模组的按钮改动。
            if (_isReplaced)
            {
                RestoreVanilla(handler);
            }
        }

        private static void ApplyReplacement(PauseHandler handler)
        {
            if (handler == null || handler.quitButton == null) return;

            _exitOnlyArmed = false;
            if (handler.exitSprite != null)
            {
                handler.quitButton.sprite = handler.exitSprite;
            }

            bool exitOnly = IsExitWithoutSaveEnabled();
            handler.quitButton.color = exitOnly ? ExitOnlyIdleColor : Color.white;
            handler.timesQuitPressed = 0;

            SetTooltip(
                handler,
                exitOnly ? "tip.pause_exit_only_name" : "tip.pause_save_exit_name",
                exitOnly ? "tip.pause_exit_only_desc" : "tip.pause_save_exit_desc");

            _isReplaced = true;
        }

        private static void SetTooltip(PauseHandler handler, string nameKey, string descKey)
        {
            if (handler == null || handler.quitButton == null) return;
            UITooltip tooltip = handler.quitButton.GetComponent<UITooltip>();
            if (tooltip == null) return;

            tooltip.skipLocale = true;
            tooltip.tipName = I18n.T(nameKey);
            tooltip.tipDesc = I18n.T(descKey);
        }

        private static void RestoreVanilla(PauseHandler handler)
        {
            _exitOnlyArmed = false;
            // 仅跳过教程关（原版教程关本身使用门形图标），调试关与常规关均需干净还原原版骷髅与放弃文案。
            if (handler == null || handler.quitButton == null || IsTutorialScene()) return;

            if (_originalQuitSprite != null)
            {
                handler.quitButton.sprite = _originalQuitSprite;
            }
            handler.quitButton.color = Color.white;
            handler.timesQuitPressed = 0;

            UITooltip tooltip = handler.quitButton.GetComponent<UITooltip>();
            if (tooltip != null)
            {
                tooltip.skipLocale = false;
                try
                {
                    tooltip.tipName = Locale.GetOther("pausegiveup");
                    tooltip.tipDesc = Locale.GetOther("pausegiveupdsc");
                }
                catch (Exception ex)
                {
                    ModLog.Warning($"恢复原版放弃按钮本地化文案失败：{ex.Message}");
                }
            }

            _isReplaced = false;
        }
    }

    [HarmonyPatch(typeof(PauseHandler), "Start")]
    internal static class PauseQuitReplacerStartPatch
    {
        [HarmonyPostfix]
        private static void Postfix(PauseHandler __instance)
        {
            PauseQuitButtonReplacer.OnPauseHandlerStarted(__instance);
        }
    }

    // 鼠标移出放弃按钮或角色复活时原版会调用 ResetQuitPressed 刷回原版文案，放在最后补刷以维持样式一致。
    [HarmonyPatch(typeof(PauseHandler), nameof(PauseHandler.ResetQuitPressed))]
    internal static class PauseQuitReplacerResetPatch
    {
        // 优先级与类型特性必须标在方法上，规避 0Harmony 2.9.0 Merge 忽略类级 Priority。
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(PauseHandler __instance)
        {
            PauseQuitButtonReplacer.OnResetQuitPressed(__instance);
        }
    }

    [HarmonyPatch(typeof(PauseHandler), nameof(PauseHandler.TryQuit))]
    internal static class PauseQuitReplacerTryQuitPatch
    {
        // 优先级与类型特性必须标在方法上，规避 0Harmony 2.9.0 Merge 忽略类级 Priority。
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(PauseHandler __instance, bool __runOriginal = true)
        {
            if (!__runOriginal) return false;
            return PauseQuitButtonReplacer.HandleTryQuitPrefix(__instance);
        }
    }

    [HarmonyPatch(typeof(PauseHandler), nameof(PauseHandler.TogglePause))]
    internal static class PauseQuitReplacerTogglePausePatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            PauseQuitButtonReplacer.OnPauseToggled();
        }
    }
}
