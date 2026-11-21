using System;
using HarmonyLib;
using UnityEngine;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>多人回档两阶段状态控制：先由主机携全员退回主菜单，待客户端同步回大厅后再发起继续游戏。</summary>
    internal static class MpRollbackController
    {
        private const float PendingReloadTimeoutSeconds = 15f;

        private static bool _pendingHostReload;
        private static float _pendingReloadDeadline;

        /// <summary>主机正在执行回档退回主菜单的窗口标志，供 GameAutoRespawnGuard 放行 ToMainMenu。</summary>
        internal static bool ReturningToMenu { get; private set; }

        /// <summary>多人两阶段回档是否仍处于挂起推进中；阶段一超 15s 未回大厅则自动超时复位。</summary>
        internal static bool IsPendingReload
        {
            get
            {
                if (_pendingHostReload && Time.unscaledTime > _pendingReloadDeadline)
                {
                    _pendingHostReload = false;
                    ModLog.Warning("多人回档阶段一等待主菜单超时，已自动中止挂起回档");
                    AbortPendingRestore();
                }
                return _pendingHostReload || ReturningToMenu;
            }
        }

        internal static void TriggerHostReload()
        {
            _pendingHostReload = true;
            _pendingReloadDeadline = Time.unscaledTime + PendingReloadTimeoutSeconds;
            ModLog.Info(I18n.T("mp.rollback_returning_menu"));
            ModLog.Debug("多人回档阶段一：置待回档标志并调用 ToMainMenu");
            ReturningToMenu = true;
            try
            {
                var cam = PlayerCamera.main;
                if (cam != null)
                {
                    cam.ToMainMenu();
                }
                else
                {
                    _pendingHostReload = false;
                    AbortPendingRestore();
                    ModLog.Warning("PlayerCamera.main 为空，多人回档无法回主菜单");
                }
            }
            catch (Exception ex)
            {
                _pendingHostReload = false;
                AbortPendingRestore();
                ModLog.Warning($"MpRollbackController.TriggerHostReload 失败：{ex.Message}");
            }
            finally
            {
                ReturningToMenu = false;
            }
        }

        internal static void OnPreRunReady()
        {
            if (!_pendingHostReload) return;
            _pendingHostReload = false;
            _pendingReloadDeadline = 0f;
            ModLog.Debug("多人回档阶段二：PreRunScript 就绪 isServer=" + MultiplayerBridge.IsServer());
            if (!MultiplayerBridge.IsServer())
            {
                AbortPendingRestore();
                ModLog.Warning("待多人回档但当前非主机，已取消");
                return;
            }

            var runner = MpRollbackRunner.Instance;
            if (runner != null)
            {
                runner.ScheduleStageTwoReload(ExecuteStageTwoReload);
            }
            else
            {
                ExecuteStageTwoReload();
            }
        }

        private static void ExecuteStageTwoReload()
        {
            if (!MultiplayerBridge.IsServer())
            {
                AbortPendingRestore();
                ModLog.Warning("多人回档阶段二延迟结束后已非主机，已取消");
                return;
            }

            try
            {
                ModLog.Info(I18n.T("mp.rollback_reloading"));
                MultiplayerBridge.SetPendingLoadMpSave(true);
                // 阶段二绕过 PreRunScript.LoadRun 直接进世界，需在切场景前按还原存档写回本地角色选择。
                PlayerCharacterBridge.ApplyForSceneReload();
                bool continued = MultiplayerBridge.TryLoadMultiplayerContinue(loadsave: true);
                ModLog.Debug("多人回档阶段二：继续游戏调用结果=" + continued);
                if (!continued)
                {
                    AbortPendingRestore();
                    ModLog.Warning("多人回档：联机模组继续游戏调用失败");
                }
            }
            catch (Exception ex)
            {
                AbortPendingRestore();
                ModLog.Warning($"MpRollbackController.ExecuteStageTwoReload 失败：{ex.Message}");
            }
        }

        private static void AbortPendingRestore()
        {
            _pendingReloadDeadline = 0f;
            MultiplayerBridge.SetPendingLoadMpSave(false);
            PendingLoadContext.ClearAll();
            MpPositionRestorer.CancelSession();
            RollbackController.NotifyReloadAborted();
        }
    }

    [HarmonyPatch(typeof(PreRunScript), "Start")]
    internal static class PreRunScriptReadyForMpRollback
    {
        private static void Postfix()
        {
            HarmonyBootstrap.TryPatchMainMenuDeferred();
            MpRollbackController.OnPreRunReady();
        }
    }
}
