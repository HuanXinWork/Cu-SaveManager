using System.Collections.Generic;
using System.Reflection;
using CasualtiesUnknown.SaveManager.Interop;
using UnityEngine;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>Casualties: Together 联机模组业务桥接层，缺失时静默回退。</summary>
    internal static class MultiplayerBridge
    {
        private static TogetherProvider Provider => InteropBootstrap.Together;

        internal static bool IsMultiplayerRunning() => Provider.IsRunning;

        /// <summary>联机模组是否已加载且契约成员可用。</summary>
        internal static bool IsModPresent() => Provider.IsPresent;

        /// <summary>联机模式是否已初始化且未被强制禁用。</summary>
        internal static bool IsMultiplayerEnabled() => Provider.IsModInitialized;

        internal static bool IsServer() => Provider.IsServer;

        internal static bool IsDedicatedServer() => Provider.IsDedicatedServer;

        /// <summary>判断传入的角色身体对象是否属于本机玩家；非联机时返回 true，专用服务端返回 false。</summary>
        internal static bool IsLocalPlayerBody(object body) => Provider.IsLocalPlayerBody(body);

        /// <summary>设置 Together.SavesystemPatch.PendingLoadMpSave 标志。</summary>
        internal static void SetPendingLoadMpSave(bool value) => Provider.SetPendingLoadMpSave(value);

        /// <summary>服务端查询全队是否满足层底过层人数阈值。</summary>
        internal static bool TryCheckTeamAtLayerFinish(out bool atFinish) => Provider.TryCheckTeamAtLayerFinish(out atFinish);

        /// <summary>联机房间当前是否激活了特殊竞技模式（如大逃杀）。</summary>
        internal static bool IsGamemodeActive() => TogetherModHooksBridge.IsGamemodeActive();

        /// <summary>联机模组程序集；未加载时为 null。</summary>
        internal static Assembly GetKrokAssembly() => Provider.ModAssembly;

        /// <summary>本机玩家在联机模组中的持久 ID；不可用时返回 null。</summary>
        internal static string GetLocalPersistentId()
            => Provider.TryGetLocalPersistentId(out string persistentId) ? persistentId : null;

        /// <summary>获取当前联机会话内全部玩家的身份快照；不可用时返回空列表。</summary>
        internal static List<InteropPlayerIdentity> GetSessionPlayerIdentities()
            => Provider.GetPlayerIdentities();

        /// <summary>服务端按持久 ID 将在线玩家传送到目标回档坐标，返回实际传送人数。</summary>
        internal static int RestorePlayerPositions(Dictionary<string, Vector2> positions)
            => positions == null || positions.Count == 0 ? 0 : Provider.RestorePlayerPositions(positions);

        /// <summary>触发存档流程把当前联机状态写入 mp_save。</summary>
        internal static bool TrySaveMpGame() => GameSaveBridge.TrySaveGame();

        /// <summary>当目标槽位采用联机原生引擎（krok）时记录回档准备日志与联机状态消息。</summary>
        internal static void PrepareMpRollback(SlotSidecar sidecar)
        {
            if (sidecar == null || WorldEngineArbiter.ResolveSidecarEngine(sidecar) != "krok")
            {
                return;
            }

            string message = I18n.T("mp.krok_continue_handoff");
            ModLog.Info(message);
            Provider.TryLogStatusMessage(message);
        }

        /// <summary>通过 Together.WorldMP.LoadVanillaGeneratedWorld(bool) 执行联机继续游戏；仅服务端或未开房时可用。</summary>
        internal static bool TryLoadMultiplayerContinue(bool loadsave = true)
        {
            ModLog.Debug("TryLoadMultiplayerContinue：loadsave=" + loadsave
                + " modInitialized=" + Provider.IsModInitialized
                + " running=" + Provider.IsRunning
                + " isServer=" + Provider.IsServer);

            if (!Provider.IsModInitialized)
            {
                ModLog.Debug("TryLoadMultiplayerContinue：联机模组未初始化 → 跳过");
                return false;
            }

            if (Provider.IsRunning && !Provider.IsServer)
            {
                ModLog.Debug("TryLoadMultiplayerContinue：本端为客户端 → 跳过");
                return false;
            }

            if (!Provider.TryContinueGame(loadsave))
            {
                ModLog.Warning("TryLoadMultiplayerContinue：联机模组的读档继续游戏不可用，本次不改变场景");
                return false;
            }

            ModLog.Info($"已通过联机模组继续游戏（loadsave:{loadsave}）");
            return true;
        }

        /// <summary>获取当前联机会话已死亡玩家人数；不可用时返回 0。</summary>
        internal static int DeadPlayerCount() => Provider.CountDeadPlayers();

        /// <summary>获取当前联机会话存活玩家人数；不可用时返回 -1。</summary>
        internal static int LivingPlayerCount() => Provider.CountLivingPlayers();

        /// <summary>服务端向全员广播屏幕中央提示；失败返回 false。</summary>
        internal static bool TryAnnounceAlert(string msg) => Provider.TryAnnounceAlert(msg);
    }
}
