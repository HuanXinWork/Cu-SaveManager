using System;
using System.Linq;
using System.Reflection;
using System.Text;
using CasualtiesUnknown.SaveManager.Interop;
using HarmonyLib;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>协调联机模组在场时的 <c>PreRunScript.LoadRun</c> 与 <c>StartRun</c> 分发（Priority 700，介于 <c>ContinueGameLoadPatch</c> 800 与联机模组 400 之间）。</summary>
    internal static class LoadRunPrefixReplacer
    {
        private const string ClientCannotStartMessage = "You can't start the game, server can.";
        private const string ClientCannotLoadMessage = "Only the host can load a save.";
        private const string NoSaveMessage = "No save file to load.";
        private const int PatchPriority = 700;

        private static bool _patched;
        private static bool _resolveTried;
        private static bool _hasCharacterSelectWait;
        private static MethodInfo _mDeleteMpSave;

        internal static void ResetPatchState()
        {
            _patched = false;
            _resolveTried = false;
            _hasCharacterSelectWait = false;
            _mDeleteMpSave = null;
        }

        internal static void TryPatch() => TryPatch(HarmonyBootstrap.Instance);

        internal static void TryPatch(Harmony harmony)
        {
            if (_patched)
            {
                return;
            }

            harmony = harmony ?? HarmonyBootstrap.Instance;

            try
            {
                if (TogetherReflection.ModAssembly == null)
                {
                    return;
                }

                ResolveMpHandles();
                TogetherModHooksBridge.TryBindAll();
                if (!TogetherModHooksBridge.HasSingleplayerPassthrough)
                {
                    MpHarmonySilencer.TrySilence(harmony);
                }

                MethodBase loadRun = AccessTools.Method(typeof(PreRunScript), "LoadRun");
                MethodBase startRun = AccessTools.Method(typeof(PreRunScript), "StartRun");

                bool loadRunPatched = PatchRunMethod(harmony, nameof(LoadRun_Prefix), loadRun);
                bool startRunPatched = PatchRunMethod(harmony, nameof(StartRun_Prefix), startRun);
                _patched = loadRunPatched && startRunPatched;

                ModLog.Debug("LoadRunPrefixReplacer：目标 LoadRun=" + DescribeMethod(loadRun) + " StartRun=" + DescribeMethod(startRun)
                    + "；priority=" + PatchPriority
                    + "；CharacterSelectWait=" + (_hasCharacterSelectWait ? "在场" : "缺失")
                    + "；ModHooks直通=" + TogetherModHooksBridge.HasSingleplayerPassthrough
                    + "；单机旁路守卫 StartRun=" + MpHarmonySilencer.StartRunGuarded + " LoadRun=" + MpHarmonySilencer.LoadRunGuarded
                    + "；本模组 Prefix LoadRun=" + (loadRunPatched ? "已挂上" : "未挂上")
                    + " StartRun=" + (startRunPatched ? "已挂上" : "未挂上"));

                LogPriorityOrder(loadRun, startRun);
                ModLog.Info("LoadRunPrefixReplacer：联机房间内放行 Together 大厅与续玩流程，未开房单机态由本模组旁路接管");
            }
            catch (Exception ex)
            {
                ModLog.Warning($"LoadRunPrefixReplacer.TryPatch 失败：{ex.Message}");
            }
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

            HarmonyMethod prefix = new HarmonyMethod(typeof(LoadRunPrefixReplacer).GetMethod(
                handlerName, BindingFlags.Static | BindingFlags.NonPublic))
            {
                priority = PatchPriority,
            };
            harmony.Patch(target, prefix);
            return true;
        }

        private static void LogPriorityOrder(MethodBase loadRun, MethodBase startRun)
        {
            CheckOne(loadRun, "LoadRun");
            CheckOne(startRun, "StartRun");
        }

        private static void CheckOne(MethodBase method, string label)
        {
            Patches info = Harmony.GetPatchInfo(method);
            if (info?.Prefixes == null || info.Prefixes.Count == 0)
            {
                return;
            }

            Patch[] ordered = info.Prefixes.OrderByDescending(p => p.priority).ToArray();
            StringBuilder chain = new StringBuilder();
            for (int i = 0; i < ordered.Length; i++)
            {
                if (i > 0) chain.Append(" > ");
                chain.Append(ordered[i].owner).Append(':').Append(ordered[i].PatchMethod?.Name)
                    .Append('(').Append(ordered[i].priority).Append(')');
            }

            ModLog.Debug($"{label} Prefix 执行顺序：{chain}");

            int mine = Array.FindIndex(ordered, p => p.owner != null
                && p.owner.StartsWith("com.casualtiesUnknown.saveManager", StringComparison.Ordinal)
                && p.PatchMethod != null && p.PatchMethod.Name.EndsWith("_Prefix", StringComparison.Ordinal));
            int mp = Array.FindIndex(ordered, p => p.owner != null
                && p.owner.StartsWith(MpHarmonySilencer.OwnerId, StringComparison.Ordinal));
            if (mine >= 0 && mp >= 0 && mine > mp)
            {
                ModLog.Warning($"Harmony 排序异常：本模组 {label} Prefix 排在联机模组之后，原方法可能仍被对方的 return false 抑制");
            }
        }

        private static string DescribeMethod(MethodBase method)
            => method == null ? "<未找到>" : method.DeclaringType?.FullName + "." + method.Name + "()";

        private static bool LoadRun_Prefix(PreRunScript __instance)
        {
            bool spPassthroughReady = TogetherModHooksBridge.HasSingleplayerPassthrough || MpHarmonySilencer.LoadRunGuarded;
            if (ContinueGameLoadPatch.ShouldDeferToVanillaGate(__instance))
            {
                if (!spPassthroughReady
                    && MultiplayerBridge.IsMultiplayerEnabled()
                    && !MultiplayerBridge.IsMultiplayerRunning())
                {
                    if (!ContinueGameLoadPatch.IsStartedLoading(__instance) && ContinueGameLoadPatch.IsTutorialIncomplete())
                    {
                        if (__instance.tutorialWarning != null) __instance.tutorialWarning.SetActive(true);
                        if (__instance.runSettingsScreen != null) __instance.runSettingsScreen.SetActive(false);
                    }
                    return false;
                }
                return true;
            }

            if (!MultiplayerBridge.IsMultiplayerEnabled())
            {
                ModLog.Flow("LoadRun", "20", "联机模组未启用 → 放行原版 LoadRun");
                return true;
            }

            if (MultiplayerBridge.IsMultiplayerRunning())
            {
                if (MultiplayerBridge.IsServer())
                {
                    string mp = MpSaveLocator.ResolveOwnSavePath() ?? MpSaveLocator.ResolveLocalPlayerSavePath();
                    ModLog.Flow("LoadRun", "22", "联机主机 → 前置注入难度与世界种子后放行联机模组 path=" + (mp ?? "<none>"));
                    RunSettingsBridge.RestoreFromSaveFile(mp);
                    RestoreMpWorldSeed();
                }
                else if (!_hasCharacterSelectWait)
                {
                    ModLog.Flow("LoadRun", "22", "联机运行（无准备大厅）且本端为客户端 → 拦截");
                    string localized = I18n.T("mp.client_cannot_load_run");
                    StatusLog(string.IsNullOrEmpty(localized) || localized == "mp.client_cannot_load_run"
                        ? ClientCannotLoadMessage
                        : localized);
                    return false;
                }
                else
                {
                    ModLog.Flow("LoadRun", "22", "联机客户端 → 放行联机模组（等待主机继续）");
                }

                return true;
            }

            bool hasSave = HasAnySaveFile();
            ModLog.Flow("LoadRun", "24", "联机未运行 → 单机读档 hasSave=" + hasSave + " passthrough=" + spPassthroughReady);
            if (!hasSave)
            {
                ModLog.Flow("LoadRun", "23", "未运行且没有可用存档 → 拦截");
                string localized = I18n.T("msg.no_save_to_load");
                StatusLog(string.IsNullOrEmpty(localized) || localized == "msg.no_save_to_load"
                    ? NoSaveMessage
                    : localized);
                return false;
            }

            if (spPassthroughReady)
            {
                return true;
            }

            return ContinueLoadedRun(__instance);
        }

        private static bool ContinueLoadedRun(PreRunScript instance)
        {
            ModLog.Flow("LoadRun", "30", "单机续玩：按当前单机存档加载世界");
            ContinueGameLoadPatch.SetStartedLoading(instance, true);
            bool ok = MultiplayerBridge.TryLoadMultiplayerContinue(loadsave: true);
            if (!ok)
            {
                ContinueGameLoadPatch.SetStartedLoading(instance, false);
            }
            ModLog.Flow("LoadRun", "31", "Together 读档调用 ok=" + ok + " loadedRun=" + SaveSystem.loadedRun);
            return false;
        }

        private static bool StartRun_Prefix(PreRunScript __instance)
        {
            if (ContinueGameLoadPatch.ShouldDeferToVanillaGate(__instance))
            {
                return true;
            }

            if (!MultiplayerBridge.IsMultiplayerEnabled())
            {
                ModLog.Flow("StartRun", "01", "联机模组未启用 → 放行原版单机新局");
                return true;
            }

            if (!MultiplayerBridge.IsMultiplayerRunning())
            {
                ModLog.Flow("StartRun", "01", "联机模组启用但未开房 → 放行原版单机新局");
                return true;
            }

            // Together 5.5+ 具备 CharacterSelectWait 准备大厅：客户端点击 Ready up 与主机全员确认开局均经其 StartRun Prefix 处理。
            if (_hasCharacterSelectWait)
            {
                ModLog.Flow("StartRun", "02", "联机运行且具备 CharacterSelectWait → 放行联机模组处理 Ready / 开局");
                return true;
            }

            if (!MultiplayerBridge.IsServer())
            {
                ModLog.Flow("StartRun", "02", "联机运行（无准备大厅）且本端为客户端 → 拦截");
                string localized = I18n.T("mp.client_cannot_start_run");
                StatusLog(string.IsNullOrEmpty(localized) || localized == "mp.client_cannot_start_run"
                    ? ClientCannotStartMessage
                    : localized);
                return false;
            }

            ModLog.Flow("StartRun", "03", "主机开新局（无准备大厅兜底）：清旧 mp_save 后 LoadVanillaGeneratedWorld(false)");
            DeleteMpSave();
            MultiplayerBridge.SetPendingLoadMpSave(false);
            SaveSystem.loadedRun = false;
            if (__instance != null && __instance.runSettings != null)
            {
                WorldGeneration.runSettings = __instance.runSettings;
                ModLog.Flow("StartRun", "04", "runSettings 取菜单预设 count=" + __instance.runSettings.Count);
            }

            ContinueGameLoadPatch.SetStartedLoading(__instance, true);
            bool ok = MultiplayerBridge.TryLoadMultiplayerContinue(loadsave: false);
            if (!ok)
            {
                ContinueGameLoadPatch.SetStartedLoading(__instance, false);
            }
            ModLog.Flow("StartRun", "05", "Together 新局调用 ok=" + ok + " loadedRun=" + SaveSystem.loadedRun);
            return false;
        }

        private static void ResolveMpHandles()
        {
            if (_resolveTried)
            {
                return;
            }

            _resolveTried = true;
            _hasCharacterSelectWait = TogetherReflection.FindType("CharacterSelectWait") != null;
            Type type = TogetherReflection.FindType("SavesystemPatch");
            if (type != null)
            {
                _mDeleteMpSave = InteropReflection.FindMethod(type, "DeleteMPSave");
            }
        }

        private static void DeleteMpSave()
        {
            if (!MpSaveLocator.HasMpSave())
            {
                return;
            }

            if (_mDeleteMpSave == null)
            {
                InteropDiagnostics.WarnOnce("LoadRunPrefixReplacer.DeleteMPSave",
                    "Together.SavesystemPatch.DeleteMPSave() 未解析到，新开联机局不会清空旧 mp_save");
                return;
            }

            if (Plugin.ShouldBackupBeforeOverwrite && MpSaveLocator.HasMpSave())
            {
                try
                {
                    Plugin.SharedStore?.BackupCurrentMpSaveBeforeOverwrite();
                }
                catch (Exception ex)
                {
                    ModLog.Warning($"清空旧 mp_save 前自动备份失败：{ex.Message}");
                }
            }

            ModLog.Info("联机主机开新局（无准备大厅兜底）：正在清空旧 mp_save 存档");
            if (!InteropReflection.TryInvoke(_mDeleteMpSave, null, Array.Empty<object>(), out object _))
            {
                ModLog.Warning("Together.SavesystemPatch.DeleteMPSave() 调用失败，旧 mp_save 未清空");
            }
        }

        private static void RestoreMpWorldSeed()
        {
            try
            {
                string mp = MpSaveLocator.ResolveOwnSavePath();
                SlotSidecar sidecar = !string.IsNullOrEmpty(mp) ? SlotSidecar.LoadOrEmpty(mp) : new SlotSidecar();
                ModLog.Flow("LoadRun", "10", "联机续玩 sidecar: path=" + (mp ?? "<dedicated>") + " " + sidecar.Describe());
                WorldEngineArbiter.PrepareMpRollback(sidecar);
                ModLog.Info($"多人续玩：还原世界引擎/种子 engine='{sidecar.WorldEngine}' seed={sidecar.QolSeed}");
            }
            catch (Exception ex)
            {
                ModLog.Warning($"多人续玩还原世界种子失败：{ex.Message}");
            }
        }

        private static bool HasAnySaveFile()
        {
            bool has = ContinueGameLoadPatch.HasSingleplayerSave();
            ModLog.Flow("LoadRun", "25", "hasSave(单机)=" + has + " qolPath=" + (QolBridge.GetCurrentSaveFilePath() ?? MpSaveLocator.VanillaSavePath));
            return has;
        }

        private static void StatusLog(string message)
        {
            if (!InteropBootstrap.Together.TryLogStatusMessage(message))
            {
                ModLog.Info(message);
            }
        }
    }
}
