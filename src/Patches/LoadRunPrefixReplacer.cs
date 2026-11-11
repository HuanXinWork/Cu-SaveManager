using System;
using System.IO;
using System.Reflection;
using CasualtiesUnknown.SaveManager.Interop;
using HarmonyLib;
using UnityEngine;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>
    /// 接管 PreRunScript.LoadRun / StartRun：摘掉联机模组挂在这两个方法上的 Prefix 后由本模组决定读档/开局路径。
    /// </summary>
    /// <remarks>
    /// 摘掉的补丁：owner 为 <see cref="MpHarmonySilencer.OwnerId"/>（CasualtiesMP）在 PreRunScript.LoadRun 与
    /// PreRunScript.StartRun 上的 Prefix，对应补丁方法 Together.PreRunScript_LoadRun_MultiplayerPatch.Prefix 与
    /// Together.PreRunScript_StartRun_MultiplayerPatch.Prefix；摘除失败时由 <see cref="MpHarmonySilencer"/> 在这两个
    /// Prefix 方法上再挂 return false 的前缀兜底，两者都失败则原 Prefix 继续生效（联机模组原生读档/开局路径）。
    /// 接管后的路径：
    ///   · LoadRun 且联机运行中：客户端写状态日志并拦下；服务端还原本地玩家 sidecar 与固定世界种子后调
    ///     Together.WorldMP.LoadVanillaGeneratedWorld(true)。
    ///   · LoadRun 且联机未开：有存档同上走继续游戏（世界由 ContinueGameLoadPatch 按单机 save.sv 准备），无存档拦下。
    ///   · StartRun 且联机运行中：客户端拦下；服务端调 Together.SavesystemPatch.DeleteMPSave() 清旧 mp_save 后调
    ///     Together.WorldMP.LoadVanillaGeneratedWorld(false)。
    ///   · StartRun 且联机未开：放行原版单机新局。
    ///   · 联机模组未启用（Together.Plugin.SUCCESFULLY_INITIALIZED 为 false 或 FORCE_DISABLE_MP_MOD 为 true）：全部放行原版。
    /// </remarks>
    internal static class LoadRunPrefixReplacer
    {
        private const string ClientCannotStartMessage = "You can't start the game, server can.";
        private const string NoSaveMessage = "No save file to load.";

        /// <summary>缓存解析到的 Together.SavesystemPatch.DeleteMPSave()；null 表示未解析到。</summary>
        private static bool _resolveTried;
        private static MethodInfo _mDeleteMpSave;

        internal static void TryPatch(Harmony harmony)
        {
            if (harmony == null)
            {
                return;
            }

            try
            {
                if (TogetherReflection.ModAssembly == null)
                {
                    return;
                }

                ResolveMpHandles();

                MethodBase loadRun = AccessTools.Method(typeof(PreRunScript), "LoadRun");
                MethodBase startRun = AccessTools.Method(typeof(PreRunScript), "StartRun");
                bool loadRunDetached = UnpatchMpOwner(harmony, loadRun);
                bool startRunDetached = UnpatchMpOwner(harmony, startRun);
                MpHarmonySilencer.TrySilence(harmony, loadRunDetached, startRunDetached);

                bool loadRunPatched = PatchRunMethod(harmony, nameof(LoadRun_Prefix), loadRun);
                bool startRunPatched = PatchRunMethod(harmony, nameof(StartRun_Prefix), startRun);

                ModLog.Debug("LoadRunPrefixReplacer：目标 LoadRun=" + DescribeMethod(loadRun) + " StartRun=" + DescribeMethod(startRun)
                    + "；Unpatch(owner=" + MpHarmonySilencer.OwnerId + ") LoadRun=" + (loadRunDetached ? "已摘净" : "仍有残留")
                    + " StartRun=" + (startRunDetached ? "已摘净" : "仍有残留")
                    + "；摘净后仍需短路=" + (!loadRunDetached || !startRunDetached)
                    + "；本模组 Prefix LoadRun=" + (loadRunPatched ? "已挂上" : "未挂上")
                    + " StartRun=" + (startRunPatched ? "已挂上" : "未挂上"));

                ModLog.Info("LoadRunPrefixReplacer：PreRunScript.LoadRun / StartRun 已接管");
            }
            catch (Exception ex)
            {
                ModLog.Warning($"LoadRunPrefixReplacer.TryPatch 失败：{ex.Message}");
            }
        }

        /// <summary>摘掉联机模组挂在该方法上的 Prefix，并返回摘完后该方法上是否已无该 owner 的 Prefix。</summary>
        private static bool UnpatchMpOwner(Harmony harmony, MethodBase method)
        {
            if (method == null)
            {
                return true;
            }

            try
            {
                harmony.Unpatch(method, HarmonyPatchType.Prefix, MpHarmonySilencer.OwnerId);
            }
            catch (Exception)
            {
                // 目标方法上没有该 owner 的 Prefix 时 Harmony 不抛异常，这里只兜底。
            }

            Patches info = Harmony.GetPatchInfo(method);
            if (info?.Prefixes == null)
            {
                return true;
            }

            foreach (Patch patch in info.Prefixes)
            {
                if (patch.owner != null
                    && patch.owner.StartsWith(MpHarmonySilencer.OwnerId, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool PatchRunMethod(Harmony harmony, string handlerName, MethodBase target)
        {
            if (target == null)
            {
                return false;
            }

            Patches info = Harmony.GetPatchInfo(target);
            if (info?.Prefixes != null)
            {
                foreach (Patch patch in info.Prefixes)
                {
                    if (patch.owner == harmony.Id && patch.PatchMethod?.Name == handlerName)
                    {
                        return true;
                    }
                }
            }

            harmony.Patch(target, prefix: new HarmonyMethod(typeof(LoadRunPrefixReplacer).GetMethod(
                handlerName, BindingFlags.Static | BindingFlags.NonPublic)));
            return true;
        }

        /// <summary>Debug 构建下输出补丁目标的完整签名，便于比对联机模组版本变动；Release 中调用点被整体移除。</summary>
        private static string DescribeMethod(MethodBase method)
            => method == null ? "<未找到>" : method.DeclaringType?.FullName + "." + method.Name + "()";

        /// <summary>解析 Together.SavesystemPatch.DeleteMPSave()；缺失时只关闭「新开联机局清旧 mp_save」这一项。</summary>
        private static void ResolveMpHandles()
        {
            if (_resolveTried)
            {
                return;
            }

            _resolveTried = true;
            Type type = TogetherReflection.FindType("SavesystemPatch");
            if (type != null)
            {
                _mDeleteMpSave = InteropReflection.FindMethod(type, "DeleteMPSave");
            }
        }

        private static bool LoadRun_Prefix()
        {
            if (!MultiplayerBridge.IsMultiplayerEnabled())
            {
                ModLog.Debug("LoadRun_Prefix：联机模组未启用 → 放行原版读档");
                return true;
            }

            bool running = MultiplayerBridge.IsMultiplayerRunning();
            ModLog.Info($"LoadRun_Prefix：mp=true netRunning={running} hasSave={HasAnySaveFile()}");

            if (running)
            {
                if (!MultiplayerBridge.IsServer())
                {
                    ModLog.Debug("LoadRun_Prefix：联机运行且本端为客户端 → 拦截并写状态日志");
                    StatusLog(ClientCannotStartMessage);
                    return false;
                }

                ModLog.Debug("LoadRun_Prefix：联机运行且本端为主机 → 继续游戏（LoadVanillaGeneratedWorld(true)）");
                return ContinueLoadedRun();
            }

            if (!HasAnySaveFile())
            {
                ModLog.Info("LoadRun_Prefix：联机启用但未运行且没有单机 save.sv → 拦截读档（游戏会当作新局重掷身份）");
                StatusLog(NoSaveMessage);
                return false;
            }

            ModLog.Debug("LoadRun_Prefix：联机启用但未运行且有存档 → 继续游戏（单机读档）");
            return ContinueLoadedRun();
        }

        /// <summary>读档分支：联机主机按本地玩家 mp_save 恢复联机状态；单机只让 Together 读根目录 save.sv。</summary>
        private static bool ContinueLoadedRun()
        {
            if (MultiplayerBridge.IsMultiplayerRunning())
            {
                // Together 不恢复 runSettings / 世界引擎 / 种子，由本模组按本地玩家 mp_save 补齐。
                RunSettingsBridge.RestoreFromSaveFile(MpSaveLocator.ResolveLocalPlayerSavePath());
                RestoreMpWorldSeed();
            }

            // 单机不走上面：遗留 mp_save 是上一场联机旧档，用它覆盖会让世界重建后像新开一局。
            MultiplayerBridge.TryLoadMultiplayerContinue(loadsave: true);
            return false;
        }

        /// <summary>新开联机局：清掉旧 mp_save 后走联机模组的无存档世界生成；联机未开时放行原版 StartRun。</summary>
        private static bool StartRun_Prefix(PreRunScript __instance)
        {
            if (!MultiplayerBridge.IsMultiplayerEnabled())
            {
                ModLog.Debug("StartRun_Prefix：联机模组未启用 → 放行原版单机新局");
                return true;
            }

            if (!MultiplayerBridge.IsMultiplayerRunning())
            {
                ModLog.Debug("StartRun_Prefix：联机模组启用但未开房 → 放行原版单机新局");
                return true;
            }

            if (!MultiplayerBridge.IsServer())
            {
                ModLog.Debug("StartRun_Prefix：联机运行且本端为客户端 → 拦截并写状态日志");
                StatusLog(ClientCannotStartMessage);
                return false;
            }

            DeleteMpSave();
            ModLog.Info("StartRun：联机运行中，走 Together.WorldMP.LoadVanillaGeneratedWorld(false)");
            ModLog.Debug("StartRun_Prefix：本端为主机 → 清旧 mp_save 后 LoadVanillaGeneratedWorld(false)");
            SaveSystem.loadedRun = false;
            if (__instance != null && __instance.runSettings != null)
            {
                WorldGeneration.runSettings = __instance.runSettings;
            }

            MultiplayerBridge.TryLoadMultiplayerContinue(loadsave: false);
            return false;
        }

        /// <summary>调 Together.SavesystemPatch.DeleteMPSave()；缺失时记一次警告并保留旧 mp_save。</summary>
        private static void DeleteMpSave()
        {
            if (_mDeleteMpSave == null)
            {
                InteropDiagnostics.WarnOnce("LoadRunPrefixReplacer.DeleteMPSave",
                    "Together.SavesystemPatch.DeleteMPSave() 未解析到，新开联机局不会清空旧 mp_save（可能沿用上一局的 mp_rules.json）");
                return;
            }

            if (!InteropReflection.TryInvoke(_mDeleteMpSave, null, Array.Empty<object>(), out object _))
            {
                ModLog.Warning("Together.SavesystemPatch.DeleteMPSave() 调用失败，旧 mp_save 未清空");
            }
        }

        /// <summary>
        /// 多人续玩（主机 loadsave）按本地玩家存档的 sidecar 还原世界引擎与固定世界种子，
        /// 使 self 引擎多人存档重启后世界可复现；随后 Together.ServerMain.Server_AnnounceSeed 会把种子广播给客户端。
        /// </summary>
        private static void RestoreMpWorldSeed()
        {
            try
            {
                SlotSidecar sidecar = SlotSidecar.LoadOrEmpty(MpSaveLocator.ResolveLocalPlayerSavePath());
                WorldEngineArbiter.PrepareMpRollback(sidecar);
                ModLog.Info($"多人续玩：还原世界引擎/种子 engine='{sidecar.WorldEngine}' seed={sidecar.QolSeed}");
            }
            catch (Exception ex)
            {
                ModLog.Warning($"多人续玩还原世界种子失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 本次读档是否真的读得到存档：与 Together 一致——联机运行看 mp_save，单机只认 save.sv。
        /// </summary>
        private static bool HasAnySaveFile()
        {
            if (MpSaveLocator.IsMultiplayerSaveActive())
            {
                if (MpSaveLocator.HasMpSave())
                {
                    return true;
                }

                string local = MpSaveLocator.ResolveLocalPlayerSavePath();
                if (!string.IsNullOrEmpty(local) && File.Exists(local))
                {
                    return true;
                }

                // 会话刚开、还没写出 mp_save 时，Together 会回落读 persistentDataPath\save.sv
                return File.Exists(MpSaveLocator.VanillaSavePath);
            }

            return File.Exists(MpSaveLocator.VanillaSavePath);
        }

        /// <summary>写联机模组的状态日志；Together.Multiplayer.DoMultiplayerStatusMessageLog(string) 不可用时回落本地日志。</summary>
        private static void StatusLog(string message)
        {
            if (!InteropBootstrap.Together.TryLogStatusMessage(message))
            {
                ModLog.Info(message);
            }
        }
    }
}
