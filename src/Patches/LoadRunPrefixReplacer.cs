using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using CasualtiesUnknown.SaveManager.Interop;
using HarmonyLib;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>
    /// 接管 PreRunScript.LoadRun / StartRun：联机读档放行联机模组的原生流程，单机（联机 mod 在场但未开房）与联机新开局由本模组接管。
    /// </summary>
    /// <remarks>
    /// 为什么联机读档不再摘掉联机模组的 Prefix：
    ///   · 联机模组（Together）在 PreRunScript.LoadRun 上挂的 Prefix 是多人读档的唯一入口，
    ///     它内部的 CharacterSelectWait.SeedSavedCharacters / HostProceed 会恢复全员角色、重建物种、
    ///     设置 SavesystemPatch.PendingLoadMpSave 并逐玩家读档。绕开它就会漏步骤（角色丢失、BodyToPlayerDict 为空）。
    ///   · 联机模组在 Net.IsRunning == false 时同样 return false 硬拦单机读档，所以单机分支必须由本模组接管。
    /// HarmonyX 的 Prefix 返回值只汇总成「原方法是否执行」，**不会**阻止后续 Prefix 执行
    /// （HarmonyX 2.9 把所有 Prefix 都调完再统一判断）。因此：
    ///   · LoadRun 的放行/接管只能控制原版方法，无法静默联机模组的多余提示——单机读档时会多一条
    ///     “You can't play singleplayer with MP mod active”，但世界已由本模组加载，不影响功能。
    ///   · StartRun 两边都不需要联机模组的 Prefix（单机时它硬拦、联机时它额外打开 Ready 大厅），
    ///     所以对 StartRun 仍保留 Unpatch，摘不掉时由 MpHarmonySilencer 短路兜底。
    /// Harmony 按 priority 降序执行 Prefix，本模组取 700：低于 ContinueGameLoadPatch（800，负责角色恢复），
    /// 高于联机模组的默认 400，因此总能先完成自己的准备再让联机模组接着执行。
    /// 分路结果：
    ///   · LoadRun 且联机运行中：主机先补 Together 不处理的难度与世界种子，然后【放行】联机模组；
    ///     客户端直接放行（由联机模组显示等待主机的状态）。
    ///   · LoadRun 且联机未运行：有存档交给联机模组读世界，无存档拦下（避免空加载重掷身份）。
    ///   · StartRun 且联机运行中：本模组【接管】——客户端拦下，主机清旧 mp_save 后直接生成世界。
    ///     不放行是因为 Together 的新局分支会先进“准备”界面等全员 Ready，与本模组原有体验不一致；
    ///     两边实际效果（DeleteMPSave + runSettings + LoadVanillaGeneratedWorld(false)）等价。
    ///   · StartRun 且联机未运行：放行原版单机新局。
    ///   · 联机模组未启用（Together.Plugin.SUCCESFULLY_INITIALIZED 为 false 或 FORCE_DISABLE_MP_MOD 为 true）：全部放行原版。
    /// </remarks>
    internal static class LoadRunPrefixReplacer
    {
        private const string ClientCannotStartMessage = "You can't start the game, server can.";
        private const string NoSaveMessage = "No save file to load.";

        /// <summary>本模组 Prefix 的 Harmony 优先级：低于 ContinueGameLoadPatch（800），高于联机模组默认的 400。</summary>
        private const int PatchPriority = 700;

        /// <summary>缓存解析到的 Together.SavesystemPatch.DeleteMPSave()；null 表示未解析到。</summary>
        private static bool _resolveTried;
        private static MethodInfo _mDeleteMpSave;

        /// <summary>挂载两个 Prefix；已挂过时直接返回（主菜单每次 Start 都会重试）。</summary>
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

                // StartRun 上联机模组的 Prefix 两边都不需要，直接摘；LoadRun 必须保留（联机读档放行）。
                bool startRunDetached = UnpatchMpOwner(harmony, startRun);
                MpHarmonySilencer.TrySilence(harmony, startRunDetached);

                bool loadRunPatched = PatchRunMethod(harmony, nameof(LoadRun_Prefix), loadRun);
                bool startRunPatched = PatchRunMethod(harmony, nameof(StartRun_Prefix), startRun);

                ModLog.Debug("LoadRunPrefixReplacer：目标 LoadRun=" + DescribeMethod(loadRun) + " StartRun=" + DescribeMethod(startRun)
                    + "；priority=" + PatchPriority
                    + "；Unpatch(owner=" + MpHarmonySilencer.OwnerId + ") StartRun=" + (startRunDetached ? "已摘净" : "仍有残留")
                    + "；本模组 Prefix LoadRun=" + (loadRunPatched ? "已挂上" : "未挂上")
                    + " StartRun=" + (startRunPatched ? "已挂上" : "未挂上"));

                LogPriorityOrder(loadRun, startRun);
                ModLog.Info("LoadRunPrefixReplacer：联机读档放行原生流程，单机读档与联机开局由本模组接管");
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

        /// <summary>在目标方法上挂本模组的 Prefix；已存在同名同 owner 的 Prefix 时跳过。</summary>
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

        /// <summary>
        /// 输出每个目标方法的 Prefix 执行顺序，并断言本模组排在联机模组之前。
        /// 联机模组若在后续版本调高自己的 Prefix 优先级，这里会在日志里第一时间暴露，而不是等玩家反馈读档失效。
        /// </summary>
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

            ModLog.Info($"{label} Prefix 执行顺序：{chain}");

            int mine = Array.FindIndex(ordered, p => p.owner != null
                && p.owner.StartsWith("com.casualtiesUnknown.saveManager", StringComparison.Ordinal)
                && p.PatchMethod != null && p.PatchMethod.Name.EndsWith("_Prefix", StringComparison.Ordinal));
            int mp = Array.FindIndex(ordered, p => p.owner != null
                && p.owner.StartsWith("CasualtiesMP", StringComparison.Ordinal));
            if (mine >= 0 && mp >= 0 && mine > mp)
            {
                ModLog.Warning($"Harmony 排序异常：本模组 {label} Prefix 排在联机模组之后，原方法可能仍被对方的 return false 抑制");
            }
        }

        /// <summary>Debug 构建下输出补丁目标的完整签名，便于比对联机模组版本变动；Release 中调用点被整体移除。</summary>
        private static string DescribeMethod(MethodBase method)
            => method == null ? "<未找到>" : method.DeclaringType?.FullName + "." + method.Name + "()";

        private static bool LoadRun_Prefix(PreRunScript __instance)
        {
            if (!MultiplayerBridge.IsMultiplayerEnabled())
            {
                ModLog.Flow("LoadRun", "20", "联机模组未启用 → 放行原版 LoadRun");
                return true;
            }

            if (MultiplayerBridge.IsMultiplayerRunning())
            {
                // 联机：角色名册、逐玩家读档、物种重建全部交给联机模组的 Prefix（CharacterSelectWait）。
                // 这里只补它不处理的两项，且只在主机侧做，随后放行。
                if (MultiplayerBridge.IsServer())
                {
                    string mp = MpSaveLocator.ResolveLocalPlayerSavePath();
                    ModLog.Flow("LoadRun", "22", "联机主机 → 前置注入后放行联机模组 path=" + (mp ?? "<none>"));
                    RunSettingsBridge.RestoreFromSaveFile(mp);
                    RestoreMpWorldSeed();
                }
                else
                {
                    ModLog.Flow("LoadRun", "22", "联机客户端 → 放行联机模组（等待主机继续）");
                }

                return true;
            }

            bool hasSave = HasAnySaveFile();
            ModLog.Flow("LoadRun", "24", "联机未运行 → 本模组接管单机读档 hasSave=" + hasSave);
            if (!hasSave)
            {
                ModLog.Flow("LoadRun", "23", "未运行且没有可用存档 → 拦截（避免空加载重掷身份）");
                StatusLog(NoSaveMessage);
                return false;
            }

            return ContinueLoadedRun();
        }

        /// <summary>单机续玩：世界已按 save.sv + sidecar 准备，这里只让联机模组把世界加载起来。</summary>
        private static bool ContinueLoadedRun()
        {
            ModLog.Flow("LoadRun", "30", "单机续玩：世界已按 save.sv + sidecar 准备，不再读 mp_save");
            bool ok = MultiplayerBridge.TryLoadMultiplayerContinue(loadsave: true);
            ModLog.Flow("LoadRun", "31", "Together 读档调用 ok=" + ok + " loadedRun=" + SaveSystem.loadedRun);
            return false;
        }

        /// <summary>联机新开一局：客户端拦下，主机清旧 mp_save 后按菜单预设生成世界。</summary>
        /// <remarks>
        /// 这里保留接管而不是放行：Together 的 HostProceed 新局分支会先进“准备”界面等全员 Ready，
        /// 与本模组原本“点开始就进世界”的体验不一致；两边的实际效果（DeleteMPSave + runSettings + 
        /// LoadVanillaGeneratedWorld(false)）等价，所以继续由本模组直接调用。
        /// </remarks>
        private static bool StartRun_Prefix(PreRunScript __instance)
        {
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

            if (!MultiplayerBridge.IsServer())
            {
                ModLog.Flow("StartRun", "02", "联机运行且本端为客户端 → 拦截");
                StatusLog(ClientCannotStartMessage);
                return false;
            }

            ModLog.Flow("StartRun", "03", "主机开新局：清旧 mp_save 后 LoadVanillaGeneratedWorld(false)");
            DeleteMpSave();
            SaveSystem.loadedRun = false;
            if (__instance != null && __instance.runSettings != null)
            {
                WorldGeneration.runSettings = __instance.runSettings;
                ModLog.Flow("StartRun", "04", "runSettings 取菜单预设 count=" + __instance.runSettings.Count);
            }

            bool ok = MultiplayerBridge.TryLoadMultiplayerContinue(loadsave: false);
            ModLog.Flow("StartRun", "05", "Together 新局调用 ok=" + ok + " loadedRun=" + SaveSystem.loadedRun);
            return false;
        }

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
                string mp = MpSaveLocator.ResolveLocalPlayerSavePath();
                SlotSidecar sidecar = SlotSidecar.LoadOrEmpty(mp);
                ModLog.Flow("LoadRun", "10", "联机续玩 sidecar: path=" + (mp ?? "<none>") + " " + sidecar.Describe());
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
                bool rules = MpSaveLocator.HasMpSave();
                string local = MpSaveLocator.ResolveLocalPlayerSavePath();
                bool localSave = !string.IsNullOrEmpty(local) && File.Exists(local);
                bool vanilla = File.Exists(MpSaveLocator.VanillaSavePath);
                ModLog.Flow("LoadRun", "25", "hasSave(联机) mpRules=" + rules + " localSave=" + localSave + " vanilla=" + vanilla);
                if (rules || localSave)
                {
                    return true;
                }

                // 会话刚开、还没写出 mp_save 时，Together 会回落读 persistentDataPath\save.sv
                return vanilla;
            }

            bool has = File.Exists(MpSaveLocator.VanillaSavePath);
            ModLog.Flow("LoadRun", "25", "hasSave(单机) save.sv=" + has + " path=" + MpSaveLocator.VanillaSavePath);
            return has;
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
