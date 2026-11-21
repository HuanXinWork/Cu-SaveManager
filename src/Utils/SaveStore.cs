using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>管理单机与多人存档槽位（persistentDataPath/CuSaveManager/slots/&lt;runId&gt;/&lt;YYYY-MM-DD&gt;/*.sv）的快照、只读扫描、还原与淘汰清理。</summary>
    internal sealed partial class SaveStore
    {
        internal const string BeforeLoadPrefix = "beforeLoad-";
        internal const string NoDateSentinel = "__no_date__";

        private readonly string _slotsRoot;
        private static GameContext? _pendingSaveExitCtx;
        private static bool _pendingSaveExitAtBoundary;
        private static string _lastActiveMpSlotPath;
        private static long _lastActiveMpRulesTicks;

        internal SaveStore()
        {
            _slotsRoot = ResolveSlotsRoot();
            try { Directory.CreateDirectory(_slotsRoot); }
            catch (Exception ex) { ModLog.Warning($"创建 slots 根目录失败：{ex.Message}"); }
            EnsureStartupMigrations(_slotsRoot);
        }

        internal string SlotsRoot => _slotsRoot;

        internal static string GameSavePath => MpSaveLocator.VanillaSavePath;

        internal static bool IsNoDateFolder(string folder)
            => string.IsNullOrEmpty(folder) || folder == NoDateSentinel || folder == "(无日期)";

        /// <summary>解析当前单机生效存档路径：优先采用 QoL 选中的已落盘存档，否则回退原版 save.sv。</summary>
        internal static string ResolveActiveSingleplayerSavePath()
        {
            string qolPath = QolBridge.GetCurrentSaveFilePath();
            return !string.IsNullOrEmpty(qolPath) && File.Exists(qolPath) ? qolPath : GameSavePath;
        }

        internal static string CurrentSaveDisplayPath
            => MpSaveLocator.IsMultiplayerSaveActive() ? MpSaveLocator.MpSaveDir : ResolveActiveSingleplayerSavePath();

        internal static bool IsMultiplayerContextActive() => MpSaveLocator.IsMultiplayerSaveActive();

        internal static bool CurrentSaveExists()
            => MpSaveLocator.IsMultiplayerSaveActive() ? MpSaveLocator.HasMpSave() : File.Exists(ResolveActiveSingleplayerSavePath());

        internal static string DescribeSavePathDecision()
        {
            bool mpRunning = MultiplayerBridge.IsMultiplayerRunning();
            bool mpEnabled = MultiplayerBridge.IsMultiplayerEnabled();
            bool mpModPresent = MultiplayerBridge.IsModPresent();
            bool mpSave = MpSaveLocator.HasMpSave();
            string vanilla = MpSaveLocator.VanillaSavePath;
            string chosen = MpSaveLocator.IsMultiplayerSaveActive() ? MpSaveLocator.MpSaveDir : ResolveActiveSingleplayerSavePath();
            string mode = mpRunning ? I18n.T("mode.multiplayer") : I18n.T("mode.singleplayer");
            return I18n.F("fmt.save_path_diag", mode, chosen, mpSave, File.Exists(vanilla))
                + $" [modPresent={mpModPresent} enabled={mpEnabled} running={mpRunning}]";
        }

        internal static string ComputeCurrentSaveHash()
            => MpSaveLocator.IsMultiplayerSaveActive()
                ? ComputeFileHash(MpSaveLocator.MpRulesPath)
                : ComputeFileHash(ResolveActiveSingleplayerSavePath());

        /// <summary>判定指定槽位是否为当前活动存档：联机比对最近落盘槽路径与 mp_rules 时间戳，单机先比文件长度再比 MD5。</summary>
        internal static bool IsSlotMatchingCurrentSave(SlotInfo slot, string currentSpSaveHash)
        {
            if (slot == null || slot.Sidecar == null || slot.File == null) return false;
            try
            {
                if (IsMultiplayerContextActive())
                {
                    if (!slot.Sidecar.IsMultiplayer || string.IsNullOrEmpty(_lastActiveMpSlotPath)) return false;
                    if (!PathsEqual(slot.FullSlotPath, _lastActiveMpSlotPath)) return false;
                    string rulesPath = MpSaveLocator.MpRulesPath;
                    return File.Exists(rulesPath)
                        && File.GetLastWriteTimeUtc(rulesPath).Ticks == _lastActiveMpRulesTicks;
                }

                if (slot.Sidecar.IsMultiplayer || string.IsNullOrEmpty(currentSpSaveHash)) return false;
                string activeSp = ResolveActiveSingleplayerSavePath();
                if (string.IsNullOrEmpty(activeSp) || !File.Exists(activeSp)) return false;
                var activeFile = new FileInfo(activeSp);
                return slot.File.Length == activeFile.Length
                    && string.Equals(slot.Hash, currentSpSaveHash, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static void RecordActiveMpSlot(string slotFullPath)
        {
            if (string.IsNullOrEmpty(slotFullPath)) return;
            try
            {
                string rulesPath = MpSaveLocator.MpRulesPath;
                if (!File.Exists(rulesPath)) return;
                _lastActiveMpSlotPath = Path.GetFullPath(slotFullPath);
                _lastActiveMpRulesTicks = File.GetLastWriteTimeUtc(rulesPath).Ticks;
            }
            catch { }
        }

        internal void CaptureBeforeSaveAndExit()
        {
            _pendingSaveExitAtBoundary = IsPlayerAtLayerBoundary();
            _pendingSaveExitCtx = SnapshotGameContext();
        }

        internal void ClearBeforeSaveAndExit()
        {
            _pendingSaveExitCtx = null;
            _pendingSaveExitAtBoundary = false;
        }

        /// <summary>手动保存已落盘的游戏存档到新槽位并写入伴生元数据。</summary>
        internal string SaveManual(string nickname)
        {
            ModLog.Flow("SaveSlot", "01", "手动保存 nickname='" + (nickname ?? "") + "' mode=" + (ShouldUseMpSave() ? "mp" : "single"));
            string path = SaveSnapshotSlot(nickname ?? "", isAuto: false);
            ModLog.Flow("SaveSlot", "02", "手动保存完成 → " + path);
            return path;
        }

        /// <summary>定时自动备份已落盘存档，并按 keep 上限淘汰同局最旧的非置顶自动槽。</summary>
        internal string AutoBackup(int keep)
        {
            ModLog.Flow("SaveSlot", "11", "自动备份 keep=" + keep + " mode=" + (ShouldUseMpSave() ? "mp" : "single"));
            string path = SaveSnapshotSlot("", isAuto: true);
            PruneAutoBackups(keep);
            ModLog.Flow("SaveSlot", "12", "自动备份完成 → " + path);
            return path;
        }

        private string SaveSnapshotSlot(string nickname, bool isAuto)
        {
            var ctx = SnapshotGameContextFromSavedGame(out string spPath, out string spJson);
            PersistCurrentSaveForContinue(ref ctx, spPath, spJson);
            string path = WriteSlotFile(ref ctx, prefix: null);
            var sidecar = BuildSidecar(ctx, nickname, isAuto);
            sidecar.Save(path);
            if (sidecar.IsMultiplayer) RecordActiveMpSlot(path);
            return path;
        }

        /// <summary>纯只读扫描全部槽位并按最后游玩/修改时间倒序返回，不执行任何文件移动或改写。</summary>
        internal List<SlotInfo> ListSlots()
        {
            var result = new List<SlotInfo>();
            try
            {
                var root = new DirectoryInfo(_slotsRoot);
                if (!root.Exists) return result;
                foreach (var f in root.GetFiles("*.sv"))
                {
                    result.Add(new SlotInfo(f, SlotSidecar.LoadOrEmpty(f.FullName), NoDateSentinel, "0"));
                }
                foreach (var topDir in root.GetDirectories())
                {
                    foreach (var dateDir in topDir.GetDirectories())
                    {
                        foreach (var f in dateDir.GetFiles("*.sv"))
                        {
                            result.Add(new SlotInfo(f, SlotSidecar.LoadOrEmpty(f.FullName), dateDir.Name, topDir.Name));
                        }
                    }
                    foreach (var f in topDir.GetFiles("*.sv"))
                    {
                        result.Add(new SlotInfo(f, SlotSidecar.LoadOrEmpty(f.FullName), NoDateSentinel, topDir.Name));
                    }
                }
            }
            catch (Exception ex)
            {
                ModLog.Warning($"扫描 slots 失败：{ex.Message}");
            }

            var ordered = result
                .OrderByDescending(s => s.Sidecar.LastPlayedAtUnixMs)
                .ThenByDescending(s => s.File.LastWriteTimeUtc)
                .ToList();
#if DEBUG
            LogScanDebug(ordered);
#endif
            return ordered;
        }

#if DEBUG
        private static string _lastScanDebugSignature;

        private static void LogScanDebug(List<SlotInfo> slots)
        {
            bool mpRunning = MultiplayerBridge.IsMultiplayerRunning();
            var signature = new StringBuilder();
            signature.Append(slots.Count).Append('|').Append(mpRunning).Append('|');
            for (int i = 0; i < slots.Count; i++)
            {
                FileInfo file = slots[i].File;
                signature.Append(file.FullName).Append(':').Append(file.Length).Append(':').Append(file.LastWriteTimeUtc.Ticks).Append(';');
            }

            string current = signature.ToString();
            if (string.Equals(current, _lastScanDebugSignature, StringComparison.Ordinal)) return;

            _lastScanDebugSignature = current;
            ModLog.Debug($"槽位扫描：共 {slots.Count} 个槽位 mpRunning={mpRunning}");
            for (int i = 0; i < slots.Count; i++)
            {
                SlotInfo slot = slots[i];
                string sidecarPath = SlotSidecar.SidecarPath(slot.FullSlotPath);
                bool sidecarExists = File.Exists(sidecarPath);
                bool sidecarUsable = sidecarExists && !IsEmptySidecar(slot.Sidecar);
                ModLog.Debug($"槽位扫描 [{i}] {slot.File.Name} size={slot.File.Length}B"
                    + $" sidecar={(sidecarUsable ? "有效" : sidecarExists ? "空" : "缺失")}"
                    + $" mp={slot.Sidecar.IsMultiplayer} runId={slot.Sidecar.RunId} 目录={slot.RunIdFolder}/{slot.DateFolder}");
            }
        }
#endif

        /// <summary>还原指定槽位到当前游戏存档，覆盖前按需备份活动存档及异名原版 save.sv，并激活对应的世界引擎。</summary>
        internal void RestoreSlotToSave(string slotFullPath, bool backupBefore)
        {
            if (!File.Exists(slotFullPath)) throw new FileNotFoundException("槽位文件不存在", slotFullPath);
            ModLog.Debug(DescribeSavePathDecision());
            var sidecar = SlotSidecar.LoadOrEmpty(slotFullPath);
            bool mpRunning = MultiplayerBridge.IsMultiplayerRunning();
            ModLog.Flow("SwitchSlot", "01", "slot=" + slotFullPath + " backupBefore=" + backupBefore
                + " mpRunning=" + mpRunning + " sidecar=" + sidecar.Describe());

            try
            {
                if (sidecar.IsMultiplayer)
                {
                    if (!mpRunning)
                    {
                        ModLog.Warning(I18n.T("mp.slot_needs_mp_session"));
                        throw new InvalidOperationException(I18n.T("mp.slot_needs_mp_session"));
                    }
                    RestoreMultiplayerSlot(slotFullPath, backupBefore);
                    return;
                }
                if (mpRunning)
                {
                    ModLog.Warning(I18n.T("mp.slot_is_singleplayer"));
                    throw new InvalidOperationException(I18n.T("mp.slot_is_singleplayer"));
                }

                string activeSp = ResolveActiveSingleplayerSavePath();
                string game = GameSavePath;
                if (backupBefore)
                {
                    bool backedUp = false;
                    if (File.Exists(activeSp))
                    {
                        var ctx = SnapshotGameContext();
                        string bakPath = WriteSlotFile(ref ctx, BeforeLoadPrefix, activeSp);
                        BuildSidecar(ctx, I18n.T("lbl.before_load_alias"), isAuto: false).Save(bakPath);
                        backedUp = true;
                    }
                    if ((!PathsEqual(activeSp, game) || !backedUp) && File.Exists(game))
                    {
                        BackupStandaloneSingleplayerFile(game);
                        backedUp = true;
                    }
                    if (backedUp) PruneBeforeLoadBackups();
                }

                CopyFileAtomic(slotFullPath, game);
                QolBridge.ResetCurrentSaveFileToVanilla();
                if (!IsEmptySidecar(sidecar) && !QolBridge.IsQolPresent() && sidecar.HasPlayerPos)
                {
                    NormalizeSaveBiome(game, sidecar.Biome);
                }
                InvalidateRunIdCache();
#if DEBUG
                TryParseSvHeader(game, out ParsedSvHeader restoredHeader);
                ModLog.Flow("SwitchSlot", "04", "save.sv 已写入 cId=" + restoredHeader.RunId
                    + " character=" + (restoredHeader.Character?.ToString(CultureInfo.InvariantCulture) ?? "?"));
#endif

                PrepareWorldForSlot(sidecar, game);
                PersistCurrentSaveSidecar(sidecar);
                ModLog.Flow("SwitchSlot", "05", "世界准备完成 sidecar=" + sidecar.Describe());
            }
            catch
            {
                PendingLoadContext.ClearAll();
                throw;
            }
        }

        private void BackupStandaloneSingleplayerFile(string savePath)
        {
            var sc = SlotSidecar.LoadOrEmpty(savePath);
            if (IsEmptySidecar(sc)) sc = BuildFallbackCurrentSaveSidecar(savePath);
            else FillMissingContinueFields(sc, savePath);
            sc.Nickname = I18n.T("lbl.before_load_alias");
            sc.Pinned = false;
            sc.IsAuto = false;
            sc.IsMultiplayer = false;
            sc.LastPlayedAtUnixMs = DateTimeOffset.Now.ToUnixTimeMilliseconds();

            var ctx = new GameContext { Biome = sc.Biome, RunId = sc.RunId };
            string bakPath = WriteSlotFile(ref ctx, BeforeLoadPrefix, savePath);
            if (sc.RunId == 0 && ctx.RunId != 0) sc.RunId = ctx.RunId;
            sc.Save(bakPath);
        }

        /// <summary>在覆盖或删除现有 mp_save 前将其快照打包至 beforeLoad- 槽位并限量清理旧备份。</summary>
        internal void BackupCurrentMpSaveBeforeOverwrite()
        {
            if (!MpSaveLocator.HasMpSave()) return;
            try
            {
                var ctx = SnapshotMpSaveContextForBackup();
                string bakPath = WriteSlotFile(ref ctx, BeforeLoadPrefix, spSourceOverride: null, forceMp: true);
                BuildSidecar(ctx, I18n.T("lbl.before_load_alias"), isAuto: false, forceMp: true).Save(bakPath);
                PruneBeforeLoadBackups();
            }
            catch (Exception ex)
            {
                ModLog.Warning($"覆盖前备份 mp_save 失败：{ex.Message}");
            }
        }

        private void RestoreMultiplayerSlot(string slotFullPath, bool backupBefore)
        {
            try
            {
                if (MultiplayerBridge.IsMultiplayerRunning() && !MultiplayerBridge.IsServer())
                {
                    ModLog.Warning(I18n.T("mp.only_host_can_rollback"));
                    throw new InvalidOperationException(I18n.T("mp.only_host_can_rollback"));
                }
                var sidecar = SlotSidecar.LoadOrEmpty(slotFullPath);
                if (backupBefore)
                {
                    MultiplayerBridge.TrySaveMpGame();
                    BackupCurrentMpSaveBeforeOverwrite();
                }

                MpSaveLocator.RestoreMpSaveFrom(slotFullPath,
                    stagedDir => MpPersistentIdMapper.RewriteRestoredSave(stagedDir, slotFullPath, sidecar));
                RecordActiveMpSlot(slotFullPath);
                InvalidateRunIdCache();
                PendingLoadContext.SkipIntroLifepod = false;
                PendingLoadContext.SpawnPosition = null;
                PendingLoadContext.LayerModifierIndex = MpRulesJson.WillRestoreLayerModifier(MpSaveLocator.MpRulesPath)
                    ? -2
                    : sidecar.ActiveLayerModifierIndex;

                string settingsSv = MpSaveLocator.ResolveOwnSavePath() ?? MpSaveLocator.ResolveLocalPlayerSavePath();
                RunSettingsBridge.RestoreFromSaveFile(settingsSv);

                if (sidecar.QolSeed == 0 && string.IsNullOrEmpty(sidecar.QolSeedInput)
                    && string.Equals(WorldEngineArbiter.ResolveEffectiveEngineName(sidecar.WorldEngine), "qol", StringComparison.OrdinalIgnoreCase))
                {
                    int seed = QolBridge.ComputeDeterministicSeedFromSaveFile(settingsSv);
                    if (seed != 0)
                    {
                        sidecar.QolSeed = seed;
                        ModLog.Debug($"多人回档种子补记：sidecar 无种子，用存档 FNV 补记 seed={seed}");
                    }
                }

                WorldEngineArbiter.PrepareMpRollback(sidecar);
                MultiplayerBridge.PrepareMpRollback(sidecar);
                MpPositionRestorer.PrepareForRollback(sidecar);
                ModLog.Info("多人回档：mp_save 已还原，触发主机重新加载");
                MpRollbackController.TriggerHostReload();
            }
            catch
            {
                PendingLoadContext.ClearAll();
                throw;
            }
        }

        private static SlotSidecar BuildSidecar(GameContext ctx, string nickname, bool isAuto, bool forceMp = false)
        {
            bool isMp = forceMp || ShouldUseMpSave();
            var sidecar = new SlotSidecar
            {
                Nickname = nickname ?? "",
                Pinned = false,
                IsAuto = isAuto,
                Biome = ctx.Biome,
                RunId = ctx.RunId,
                LastPlayedAtUnixMs = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                HasPlayerPos = ctx.HasPlayerPos,
                PlayerX = ctx.PlayerX,
                PlayerY = ctx.PlayerY,
                QolSeed = ctx.QolSeed,
                QolSeedInput = ctx.QolSeedInput,
                WorldEngine = ctx.WorldEngine,
                PosMode = WorldEngineArbiter.PositionMode,
                FixedX = WorldEngineArbiter.FixedX,
                FixedY = WorldEngineArbiter.FixedY,
                IsMultiplayer = isMp,
                ActiveLayerModifierIndex = ctx.ActiveLayerModifierIndex,
                Character = ctx.Character,
            };
            if (sidecar.IsMultiplayer) MpPersistentIdMapper.CaptureForSlot(sidecar);
            return sidecar;
        }

        /// <summary>单机读档或回档前按 sidecar 激活对应的世界生成引擎与待还原坐标。</summary>
        internal void PrepareWorldForSlot(SlotSidecar sidecar, string singleplayerSavePath = null)
        {
            if (sidecar == null) return;
            try
            {
                string spPath = string.IsNullOrEmpty(singleplayerSavePath) ? ResolveActiveSingleplayerSavePath() : singleplayerSavePath;
                string svPath = sidecar.IsMultiplayer
                    ? (MpSaveLocator.ResolveOwnSavePath() ?? MpSaveLocator.ResolveLocalPlayerSavePath())
                    : spPath;
                ModLog.Flow("PrepareWorld", "01", "sidecar=" + sidecar.Describe());
                PendingLoadContext.SkipIntroLifepod = false;
                PendingLoadContext.SpawnPosition = null;
                PendingLoadContext.LayerModifierIndex = sidecar.ActiveLayerModifierIndex;
                RunSettingsBridge.RestoreFromSaveFile(svPath);

                string eff = WorldEngineArbiter.ResolveEffectiveEngineName(sidecar.WorldEngine);
                if (eff == "krok")
                {
                    ModLog.Warning("单机回档忽略 krok 引擎，回落 self");
                    eff = "self";
                }
                ModLog.Debug($"回档引擎={eff} (sidecar.WorldEngine='{sidecar.WorldEngine}')");
                ModLog.Debug("回档种子来源=" + DescribeRestoreSeedSource(sidecar, eff, svPath));

                if (eff == "self")
                {
                    WorldEngineArbiter.Apply(WorldEngine.Self, sidecar.QolSeed, sidecar.QolSeedInput);
                    PendingLoadContext.SkipIntroLifepod = true;
                    var pos = ResolveSpawnPosition(sidecar);
                    PendingLoadContext.SpawnPosition = pos;
                    ModLog.Debug(pos.HasValue
                        ? $"回档准备：位置模式={sidecar.PosMode} 目标=({pos.Value.x:0.0},{pos.Value.y:0.0})"
                        : $"回档准备：无层内坐标还原（hasPos={sidecar.HasPlayerPos} posMode={sidecar.PosMode}）");
                }
                else
                {
                    if (!QolBridge.IsQolPresent())
                    {
                        ModLog.Warning("该存档由 QoL 引擎生成，但当前未检测到 QoL，世界可能不一致");
                    }
                    // 种子为空时按当前存档字节 FNV-1a 补记兜底值，保证回档世界可复现。
                    if (sidecar.QolSeed == 0 && string.IsNullOrEmpty(sidecar.QolSeedInput))
                    {
                        int seed = QolBridge.ComputeDeterministicSeedFromSaveFile(svPath);
                        if (seed != 0)
                        {
                            sidecar.QolSeed = seed;
                            ModLog.Debug($"回档种子补记：sidecar 无种子，用存档 FNV 补记 seed={seed}");
                        }
                    }
                    WorldEngineArbiter.Apply(WorldEngine.Qol, sidecar.QolSeed, sidecar.QolSeedInput);
                    QolBridge.PrepareRollback(sidecar);
                }
            }
            catch
            {
                PendingLoadContext.ClearAll();
                throw;
            }
        }

        private static string DescribeRestoreSeedSource(SlotSidecar sidecar, string eff, string svPath)
        {
            if (sidecar.QolSeed != 0 || !string.IsNullOrEmpty(sidecar.QolSeedInput))
            {
                return "sidecar（seed=" + sidecar.QolSeed + " input='" + sidecar.QolSeedInput + "'）";
            }
            if (eff == "self") return "无（sidecar 未记录种子，self 引擎按默认种子激活）";
            int fnv = QolBridge.ComputeDeterministicSeedFromSaveFile(svPath);
            return fnv != 0 ? "存档文件 FNV（seed=" + fnv + "）" : "无（sidecar 未记录种子且存档文件不可读）";
        }

        /// <summary>主菜单 Continue 前读取或补全当前单机存档（含 QoL 自定义存档名）伴生元数据并准备世界引擎。</summary>
        internal void PrepareCurrentSaveForContinue()
        {
            if (ShouldUseMpSave()) return;
            string activeSave = ResolveActiveSingleplayerSavePath();
            if (!File.Exists(activeSave)) return;

            var sidecar = SlotSidecar.LoadOrEmpty(activeSave);
            if (IsEmptySidecar(sidecar)) sidecar = BuildFallbackCurrentSaveSidecar(activeSave);
            else FillMissingContinueFields(sidecar, activeSave);

            if (string.IsNullOrEmpty(sidecar.WorldEngine)) sidecar.WorldEngine = ResolveCurrentPreferredEngineName();
            sidecar.PosMode = WorldEngineArbiter.PositionMode;
            sidecar.FixedX = WorldEngineArbiter.FixedX;
            sidecar.FixedY = WorldEngineArbiter.FixedY;

            InvalidateRunIdCache();
            PrepareWorldForSlot(sidecar, activeSave);
            sidecar.Save(activeSave);
            ModLog.Flow("Sidecar", "01", "写回 " + activeSave + ".json " + sidecar.Describe());
            if (!PathsEqual(activeSave, GameSavePath)) PersistCurrentSaveSidecar(sidecar);
            ModLog.Flow("ContinuePrep", "04", "完成 path='" + activeSave + "' engine='" + sidecar.WorldEngine + "' seed=" + sidecar.QolSeed);
        }

        private static Vector3? ResolveSpawnPosition(SlotSidecar sidecar)
        {
            if (sidecar == null || !sidecar.HasPlayerPos) return null;
            return string.Equals(sidecar.PosMode, "fixedPos", StringComparison.OrdinalIgnoreCase)
                ? new Vector3(sidecar.FixedX, sidecar.FixedY, 0f)
                : new Vector3(sidecar.PlayerX, sidecar.PlayerY, 0f);
        }

        internal void PersistCurrentSaveForContinue()
        {
            var ctx = SnapshotGameContext();
            PersistCurrentSaveForContinue(ref ctx, null, null);
        }

        private void PersistCurrentSaveForContinue(ref GameContext ctx, string preReadSpPath, string preUnzippedJson)
        {
            if (ShouldUseMpSave())
            {
                PersistMpLocalPlayerSeed(ref ctx);
                return;
            }
            string activeSave = ResolveActiveSingleplayerSavePath();
            if (!File.Exists(activeSave)) return;

            bool atBoundary = _pendingSaveExitAtBoundary || IsPlayerAtLayerBoundary();
            if (atBoundary) ClearPlayerPos(ref ctx);
            if (!QolBridge.IsQolPresent() && !atBoundary)
            {
                if (!string.IsNullOrEmpty(preUnzippedJson) && PathsEqual(activeSave, preReadSpPath))
                    NormalizeSaveBiomeFromJson(activeSave, preUnzippedJson, ctx.Biome);
                else
                    NormalizeSaveBiome(activeSave, ctx.Biome);
            }

            var sidecar = BuildSidecar(ctx, "", isAuto: false);
            sidecar.Save(activeSave);
            ModLog.Flow("Sidecar", "01", "写回 " + activeSave + ".json " + sidecar.Describe());
            if (!PathsEqual(activeSave, GameSavePath) && File.Exists(GameSavePath))
            {
                PersistCurrentSaveSidecar(sidecar);
            }
            string qolTarget = QolBridge.GetCurrentSaveFilePath();
            if (!string.IsNullOrEmpty(qolTarget) && !PathsEqual(qolTarget, activeSave))
            {
                sidecar.Save(qolTarget);
            }
        }

        private static void PersistMpLocalPlayerSeed(ref GameContext ctx)
        {
            if (IsMpAtLayerBoundary()) ClearPlayerPos(ref ctx);
            if (!MultiplayerBridge.IsServer() || MultiplayerBridge.IsDedicatedServer()) return;

            try
            {
                var cam = PlayerCamera.main;
                if (cam == null || cam.body == null || !MultiplayerBridge.IsLocalPlayerBody(cam.body)) return;

                string sv = MpSaveLocator.ResolveOwnSavePath();
                if (string.IsNullOrEmpty(sv) || !File.Exists(sv))
                {
                    ModLog.Debug("多人续玩种子持久化跳过：本机玩家 save.sv 暂不存在");
                    return;
                }

                var sc = SlotSidecar.LoadOrEmpty(sv);
                bool firstSeed = sc.QolSeed == 0 && ctx.QolSeed != 0;
                if (firstSeed)
                {
                    sc.QolSeed = ctx.QolSeed;
                    sc.QolSeedInput = ctx.QolSeedInput ?? "";
                }
                if (string.IsNullOrEmpty(sc.WorldEngine)) sc.WorldEngine = ctx.WorldEngine;
                sc.MpWorldEngine = ctx.WorldEngine;
                sc.IsMultiplayer = true;
                if (sc.RunId == 0) sc.RunId = ctx.RunId;
                sc.Biome = ctx.Biome;
                sc.HasPlayerPos = ctx.HasPlayerPos;
                sc.PlayerX = ctx.PlayerX;
                sc.PlayerY = ctx.PlayerY;
                sc.PosMode = WorldEngineArbiter.PositionMode;
                sc.FixedX = WorldEngineArbiter.FixedX;
                sc.FixedY = WorldEngineArbiter.FixedY;
                sc.ActiveLayerModifierIndex = ctx.ActiveLayerModifierIndex;
                if (ctx.Character >= 0) sc.Character = ctx.Character;
                sc.LastPlayedAtUnixMs = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                sc.Save(sv);
                ModLog.Debug($"多人续玩种子持久化：path={sv} seed={sc.QolSeed} engine={sc.WorldEngine} firstSeed={firstSeed}");
            }
            catch (Exception ex)
            {
                ModLog.Warning($"PersistMpLocalPlayerSeed 失败：{ex.Message}");
            }
        }

        private static SlotSidecar BuildFallbackCurrentSaveSidecar(string savePath)
        {
            TryReadSvWithHeader(savePath, out ParsedSvHeader header, out byte[] rawBytes, out _);
            return new SlotSidecar
            {
                Biome = header.RawBiome,
                RunId = header.RunId,
                Character = header.Character ?? -1,
                LastPlayedAtUnixMs = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                QolSeed = QolBridge.ComputeDeterministicSeedFromBytes(rawBytes),
                QolSeedInput = "",
                WorldEngine = ResolveCurrentPreferredEngineName(),
                PosMode = WorldEngineArbiter.PositionMode,
                FixedX = WorldEngineArbiter.FixedX,
                FixedY = WorldEngineArbiter.FixedY,
            };
        }

        private static void FillMissingContinueFields(SlotSidecar sidecar, string savePath)
        {
            if (sidecar == null) return;
            if (string.IsNullOrWhiteSpace(sidecar.PosMode))
            {
                sidecar.PosMode = WorldEngineArbiter.PositionMode;
                sidecar.FixedX = WorldEngineArbiter.FixedX;
                sidecar.FixedY = WorldEngineArbiter.FixedY;
            }
            if (sidecar.QolSeed == 0) sidecar.QolSeed = QolBridge.ComputeDeterministicSeedFromSaveFile(savePath);
            if (sidecar.RunId == 0) sidecar.RunId = ReadRunIdFromSv(savePath);
            sidecar.LastPlayedAtUnixMs = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        }

        internal static bool IsEmptySidecar(SlotSidecar sidecar)
        {
            if (sidecar == null) return true;
            if (sidecar.LoadedFromDisk) return false;
            return !sidecar.Pinned
                && !sidecar.IsAuto
                && sidecar.Biome == 0
                && sidecar.RunId == 0
                && sidecar.LastPlayedAtUnixMs == 0
                && !sidecar.HasPlayerPos
                && sidecar.PlayerX == 0f
                && sidecar.PlayerY == 0f
                && sidecar.QolSeed == 0
                && string.IsNullOrEmpty(sidecar.QolSeedInput)
                && string.IsNullOrEmpty(sidecar.WorldEngine)
                && string.IsNullOrEmpty(sidecar.MpWorldEngine)
                && string.IsNullOrEmpty(sidecar.PosMode)
                && sidecar.FixedX == 0f
                && sidecar.FixedY == 0f
                && !sidecar.IsMultiplayer
                && sidecar.ActiveLayerModifierIndex == -2
                && sidecar.Character < 0
                && (sidecar.MpPlayers == null || sidecar.MpPlayers.Count == 0)
                && string.IsNullOrEmpty(sidecar.Nickname);
        }

        /// <summary>删除指定主槽位及其伴生文件；主文件删除成功或已不存在时返回 true，删除抛错返回 false。</summary>
        internal bool DeleteSlot(string slotFullPath)
        {
            if (string.IsNullOrEmpty(slotFullPath)) return false;
            ModLog.Flow("SlotIO", "01", "删除槽位 " + slotFullPath);
            bool deleted = true;
            try
            {
                if (File.Exists(slotFullPath)) File.Delete(slotFullPath);
            }
            catch (Exception ex)
            {
                ModLog.Warning($"删除槽位失败 {slotFullPath}: {ex.Message}");
                deleted = false;
            }
            SlotSidecar.DeleteFor(slotFullPath);
            try
            {
                string rulesBackup = MpPersistentIdMapper.RulesBackupPath(slotFullPath);
                if (!string.IsNullOrEmpty(rulesBackup) && File.Exists(rulesBackup)) File.Delete(rulesBackup);
            }
            catch { }
            return deleted;
        }

        internal void UpdateSidecar(string slotFullPath, SlotSidecar sidecar) => sidecar.Save(slotFullPath);

        /// <summary>查找当前 RunId 下时间最新的可回档槽位（不区分手动/自动，排除 beforeLoad- 备份）。</summary>
        internal SlotInfo FindLatestRollbackTarget()
        {
            int currentRunId = ComputeCurrentRunId();
            if (currentRunId == 0) return null;
            bool NotBeforeLoad(SlotInfo s) => !s.File.Name.StartsWith(BeforeLoadPrefix, StringComparison.Ordinal);
            return ListSlots().Where(s => s.Sidecar.RunId == currentRunId).FirstOrDefault(NotBeforeLoad);
        }

        internal int CountStaleAutoBackups(int currentRunId)
        {
            int count = 0;
            foreach (var s in ListSlots())
            {
                if (s.Sidecar.IsAuto && !s.Sidecar.Pinned && s.Sidecar.RunId != currentRunId) count++;
            }
            return count;
        }

        internal int PruneStaleAutoBackups(int currentRunId)
        {
            int deleted = 0;
            foreach (var s in ListSlots())
            {
                if (!s.Sidecar.IsAuto || s.Sidecar.Pinned || s.Sidecar.RunId == currentRunId) continue;
                if (DeleteSlot(s.FullSlotPath)) deleted++;
            }
            if (deleted > 0)
            {
                SlotMigrationHelper.CleanupEmptyDirectories(_slotsRoot, includeRoot: false);
            }
            return deleted;
        }

        private void PruneAutoBackups(int keep)
        {
            int currentRunId = ComputeCurrentRunId();
            PruneMatchingSlots(s => s.Sidecar.IsAuto && !s.Sidecar.Pinned && s.Sidecar.RunId == currentRunId, keep, "自动备份");
        }

        /// <summary>限量保留最近 keep 份未置顶的覆盖前备份（beforeLoad-），防止反复切档导致磁盘无限增长。</summary>
        private void PruneBeforeLoadBackups(int keep = 10)
            => PruneMatchingSlots(s => !s.Sidecar.Pinned && s.File.Name.StartsWith(BeforeLoadPrefix, StringComparison.Ordinal), keep, "beforeLoad 备份");

        private void PruneMatchingSlots(Func<SlotInfo, bool> predicate, int keep, string label)
        {
            try
            {
                var stale = ListSlots().Where(predicate).Skip(Math.Max(1, keep)).ToList();
                foreach (var s in stale) DeleteSlot(s.FullSlotPath);
            }
            catch (Exception ex)
            {
                ModLog.Warning($"清理旧{label}失败：{ex.Message}");
            }
        }
    }
}
