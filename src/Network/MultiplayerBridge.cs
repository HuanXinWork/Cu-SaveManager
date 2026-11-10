using System;
using System.Collections.Generic;
using System.Reflection;
using CasualtiesUnknown.SaveManager.Interop;
using UnityEngine;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>
    /// 联机模组（Casualties: Together，程序集 CasualtiesMP.dll）的软依赖桥。
    /// 所有目标程序集成员名只出现在 <see cref="TogetherProvider"/> 里，本类只做业务判定与日志。
    /// dll 不在 / 成员缺失时静默回落，绝不抛出阻塞主流程。
    /// </summary>
    internal static class MultiplayerBridge
    {
        private static TogetherProvider Provider => InteropBootstrap.Together;

        internal static bool IsMultiplayerRunning()
        {
            return Provider.IsRunning;
        }

        /// <summary>联机 mod 是否已加载（不要求正在联机）。</summary>
        internal static bool IsModPresent()
        {
            return Provider.IsPresent;
        }

        /// <summary>联机模式是否启用：模组初始化成功且未被强制禁用。与是否开房联机无关。</summary>
        internal static bool IsMultiplayerEnabled()
        {
            return Provider.IsModInitialized;
        }

        internal static bool IsServer()
        {
            return Provider.IsServer;
        }

        internal static bool IsDedicatedServer()
        {
            return Provider.IsDedicatedServer;
        }

        /// <summary>联机模组程序集；未加载时为 null。</summary>
        /// <remarks>方法名为既有调用方的约定，语义是「当前联机模组的程序集」。</remarks>
        internal static Assembly GetKrokAssembly()
        {
            return Provider.ModAssembly;
        }

        /// <summary>本地玩家在联机模组里的持久 ID（如 STEAM_7656…），用于定位 mp_save 子目录。</summary>
        internal static string GetLocalPersistentId()
        {
            return Provider.TryGetLocalPersistentId(out string persistentId) ? persistentId : null;
        }

        /// <summary>当前会话内各玩家的身份快照（持久 ID / Steam ID / 玩家名），用于回档时把槽位里的旧持久 ID 换成本场会话的持久 ID。</summary>
        /// <remarks>联机模组不在场或名册为空时返回空列表，不抛异常。</remarks>
        internal static List<InteropPlayerIdentity> GetSessionPlayerIdentities()
        {
            return Provider.GetPlayerIdentities();
        }

        /// <summary>主机按 persistentId 将各玩家传送到回档坐标（含子端同步）。</summary>
        internal static int RestorePlayerPositions(Dictionary<string, Vector2> positions)
        {
            if (positions == null || positions.Count == 0)
            {
                return 0;
            }

            try
            {
                return Provider.RestorePlayerPositions(positions);
            }
            catch (Exception ex)
            {
                ModLog.Warning($"RestorePlayerPositions 失败：{ex.Message}");
                return 0;
            }
        }

        /// <summary>回档前触发联机模组的存档 patch，把当前状态写入 mp_save（含各玩家子目录）。</summary>
        internal static bool TrySaveMpGame()
        {
            return GameSaveBridge.TrySaveGame();
        }

        /// <summary>多人回档准备：联机引擎无需额外写回，位置由联机模组读档时逐人恢复；self 引擎由 WorldEngineArbiter 管种子。</summary>
        internal static void PrepareMpRollback(SlotSidecar sidecar)
        {
            if (sidecar == null)
            {
                return;
            }

            if (WorldEngineArbiter.ResolveEffectiveEngineName(
                    string.IsNullOrEmpty(sidecar.WorldEngine) ? sidecar.MpWorldEngine : sidecar.WorldEngine) != "krok")
            {
                return;
            }

            const string Message = "存档/位置交由联机模组继续游戏恢复";
            ModLog.Info("联机引擎：" + Message);
            Provider.TryLogStatusMessage(Message);
        }

        /// <summary>走联机模组的原生读档继续游戏流程；对应 Together.WorldMP.LoadVanillaGeneratedWorld(bool)。</summary>
        /// <remarks>
        /// 前置条件与旧桥一致：模组已启用，且本端不是纯客户端（客户端不发起了世界生成）。
        /// 目标成员缺失时记一次警告并返回 false，不改动场景与存档，调用方保持原状。
        /// </remarks>
        internal static bool TryLoadMultiplayerContinue(bool loadsave = true)
        {
            ModLog.Debug("TryLoadMultiplayerContinue：loadsave=" + loadsave
                + " modInitialized=" + Provider.IsModInitialized
                + " running=" + Provider.IsRunning
                + " isServer=" + Provider.IsServer);
            try
            {
                if (!Provider.IsModInitialized)
                {
                    ModLog.Debug("TryLoadMultiplayerContinue：联机模组未初始化 → 跳过（不改变场景）");
                    return false;
                }

                if (Provider.IsRunning && !Provider.IsServer)
                {
                    ModLog.Debug("TryLoadMultiplayerContinue：本端为客户端 → 跳过（不改变场景）");
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
            catch (Exception ex)
            {
                ModLog.Warning($"TryLoadMultiplayerContinue 失败：{ex.Message}");
                return false;
            }
        }

        /// <summary>当前已死亡玩家数 = BodyToPlayerDict 条目数 - AllLivingPlayers 条目数。成员缺失时返回 0。</summary>
        internal static int DeadPlayerCount()
        {
            try
            {
                return Provider.CountDeadPlayers();
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>尝试通过联机模组广播屏幕中央大字提示。失败返回 false（外部应自行 fallback 调本地 DoAlert）。</summary>
        internal static bool TryAnnounceAlert(string msg)
        {
            try
            {
                return Provider.TryAnnounceAlert(msg);
            }
            catch (Exception ex)
            {
                ModLog.Warning($"TryAnnounceAlert 失败：{ex.Message}");
                return false;
            }
        }
    }
}
