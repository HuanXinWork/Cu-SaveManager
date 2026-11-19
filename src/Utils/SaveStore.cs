using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using UnityEngine;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>管理单机与多人存档槽位（slots/&lt;runId&gt;/&lt;YYYY-MM-DD&gt;/*.sv）的快照、迁移、回档准备与续玩元数据。</summary>
    internal sealed class SaveStore
    {
        private readonly string _slotsRoot;
        private static GameContext? _pendingSaveExitCtx;
        private static bool _pendingSaveExitAtBoundary;

        internal SaveStore()
        {
            _slotsRoot = ResolveSlotsRoot();
            try { Directory.CreateDirectory(_slotsRoot); }
            catch (Exception ex) { ModLog.Warning($"创建 slots 根目录失败：{ex.Message}"); }
        }

        internal string SlotsRoot => _slotsRoot;

        internal static string GameSavePath => MpSaveLocator.VanillaSavePath;

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
            string mode = mpRunning ? I18n.T("mode.multiplayer") : I18n.T("mode.singleplayer");
            string chosen = MpSaveLocator.IsMultiplayerSaveActive() ? MpSaveLocator.MpSaveDir : vanilla;
            return I18n.F("fmt.save_path_diag", mode, chosen, mpSave, File.Exists(vanilla))
                + $" [modPresent={mpModPresent} enabled={mpEnabled} running={mpRunning}]";
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
            var ctx = SnapshotGameContextFromSavedGame();
            PersistCurrentSaveForContinue(ref ctx);
            string path = WriteSlotFile(ref ctx, prefix: null);
            BuildSidecar(ctx, nickname ?? "", isAuto: false).Save(path);
            ModLog.Flow("SaveSlot", "02", "手动保存完成 → " + path);
            return path;
        }

        /// <summary>定时自动备份已落盘存档，并按 keep 上限淘汰同局最旧的非标记自动槽。</summary>
        internal string AutoBackup(int keep)
        {
            ModLog.Flow("SaveSlot", "11", "自动备份 keep=" + keep + " mode=" + (ShouldUseMpSave() ? "mp" : "single"));
            var ctx = SnapshotGameContextFromSavedGame();
            PersistCurrentSaveForContinue(ref ctx);
            string path = WriteSlotFile(ref ctx, prefix: null);
            BuildSidecar(ctx, "", isAuto: true).Save(path);
            PruneAutoBackups(keep);
            ModLog.Flow("SaveSlot", "12", "自动备份完成 → " + path);
            return path;
        }

        /// <summary>扫描全部槽位并按保存时间倒序返回，顺带将缺少 RunId 的旧槽位迁移至对应分组目录。</summary>
        internal List<SlotInfo> ListSlots()
        {
            var migrated = new List<(string oldPath, string newPath)>();
            var result = new List<SlotInfo>();
            try
            {
                var root = new DirectoryInfo(_slotsRoot);
                if (!root.Exists) return result;
                foreach (var topDir in root.GetDirectories())
                {
                    foreach (var dateDir in topDir.GetDirectories())
                    {
                        foreach (var f in dateDir.GetFiles("*.sv"))
                        {
                            CollectOrMigrate(f, dateDir.Name, topDir.Name, result, migrated);
                        }
                    }
                    foreach (var f in topDir.GetFiles("*.sv"))
                    {
                        CollectOrMigrate(f, "(无日期)", topDir.Name, result, migrated);
                    }
                }
            }
            catch (Exception ex)
            {
                ModLog.Warning($"扫描 slots 失败：{ex.Message}");
            }
            if (migrated.Count > 0)
            {
                ModLog.Info($"为 {migrated.Count} 个旧槽位迁移目录到 runId 分组");
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

        private void CollectOrMigrate(FileInfo fi, string dateFolder, string topFolder,
            List<SlotInfo> result, List<(string, string)> migrated)
        {
            string originalPath = fi.FullName;
            var sidecar = SlotSidecar.LoadOrEmpty(originalPath);
            if (sidecar.RunId != 0 && topFolder == sidecar.RunId.ToString(CultureInfo.InvariantCulture))
            {
                result.Add(new SlotInfo(fi, sidecar, dateFolder, topFolder));
                return;
            }

            int runId = sidecar.RunId != 0 ? sidecar.RunId : ReadRunIdFromSv(originalPath);
            if (runId == 0)
            {
                result.Add(new SlotInfo(fi, sidecar, dateFolder, topFolder));
                return;
            }

            string runIdStr = runId.ToString(CultureInfo.InvariantCulture);
            string targetDateFolder = dateFolder == "(无日期)" ? DateTime.Now.ToString("yyyy-MM-dd") : dateFolder;
            string targetDir = Path.Combine(_slotsRoot, runIdStr, targetDateFolder);
            string movedTo = null;
            try
            {
                Directory.CreateDirectory(targetDir);
                string targetPath = Path.Combine(targetDir, fi.Name);
                int n = 1;
                while (File.Exists(targetPath) && !PathsEqual(targetPath, originalPath))
                {
                    targetPath = Path.Combine(targetDir, Path.GetFileNameWithoutExtension(fi.Name) + "-" + n + ".sv");
                    n++;
                }
                if (!PathsEqual(targetPath, originalPath))
                {
                    File.Move(originalPath, targetPath);
                    movedTo = targetPath;
                    string oldSidecar = SlotSidecar.SidecarPath(originalPath);
                    string newSidecar = SlotSidecar.SidecarPath(targetPath);
                    if (File.Exists(oldSidecar)) File.Move(oldSidecar, newSidecar);
                    migrated.Add((originalPath, targetPath));
                    fi = new FileInfo(targetPath);
                }
                if (sidecar.RunId == 0)
                {
                    sidecar.RunId = runId;
                    sidecar.Save(fi.FullName);
                }
                result.Add(new SlotInfo(fi, sidecar, targetDateFolder, runIdStr));
            }
            catch (Exception ex)
            {
                if (movedTo != null && File.Exists(movedTo) && !File.Exists(originalPath))
                {
                    try { File.Move(movedTo, originalPath); } catch { fi = new FileInfo(movedTo); }
                }
                ModLog.Warning($"迁移槽位失败 {originalPath} → runId={runId}: {ex.Message}");
                result.Add(new SlotInfo(fi, sidecar, dateFolder, topFolder));
            }
        }

        private static bool PathsEqual(string a, string b)
            => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

        /// <summary>还原指定槽位到当前游戏存档，并配置对应的世界生成引擎与出生位置。</summary>
        internal void RestoreSlotToSave(string slotFullPath, bool backupBefore)
        {
            if (!File.Exists(slotFullPath))
            {
                throw new FileNotFoundException("槽位文件不存在", slotFullPath);
            }
            ModLog.Info(DescribeSavePathDecision());
            var sidecar = SlotSidecar.LoadOrEmpty(slotFullPath);
            bool mpRunning = MultiplayerBridge.IsMultiplayerRunning();
            ModLog.Flow("SwitchSlot", "01", "slot=" + slotFullPath + " backupBefore=" + backupBefore
                + " mpRunning=" + mpRunning + " sidecar=" + sidecar.Describe());

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
            if (backupBefore && File.Exists(activeSp))
            {
                var ctx = SnapshotGameContext();
                string bakPath = WriteSlotFile(ref ctx, prefix: "beforeLoad-");
                BuildSidecar(ctx, I18n.T("lbl.before_load_alias"), isAuto: false).Save(bakPath);
            }

            string game = GameSavePath;
            File.Copy(slotFullPath, game, overwrite: true);
            QolBridge.ResetCurrentSaveFileToVanilla();
            if (!IsEmptySidecar(sidecar) && !QolBridge.IsQolPresent() && sidecar.HasPlayerPos)
            {
                NormalizeSaveBiome(game, sidecar.Biome);
            }
            InvalidateRunIdCache();
            ModLog.Flow("SwitchSlot", "04", "save.sv 已写入 cId=" + ReadRunIdFromSv(game)
                + " character=" + (TryReadCharacterFromSv(game)?.ToString(CultureInfo.InvariantCulture) ?? "?"));

            PrepareWorldForSlot(sidecar, game);
            PersistCurrentSaveSidecar(sidecar);
            ModLog.Flow("SwitchSlot", "05", "世界准备完成 sidecar=" + sidecar.Describe());
        }

        private void RestoreMultiplayerSlot(string slotFullPath, bool backupBefore)
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
                if (MpSaveLocator.HasMpSave())
                {
                    var ctx = SnapshotGameContext();
                    string bakPath = WriteSlotFile(ref ctx, prefix: "beforeLoad-");
                    BuildSidecar(ctx, I18n.T("lbl.before_load_alias"), isAuto: false).Save(bakPath);
                }
            }

            MpSaveLocator.RestoreMpSaveFrom(slotFullPath,
                stagedDir => MpPersistentIdMapper.RewriteRestoredSave(stagedDir, slotFullPath, sidecar));
            InvalidateRunIdCache();
            IntroLifepodOnLoadGuardPatch.PendingSkipIntroLifepod = false;
            SelfSpawnPatcher.PendingPosition = null;
            ApplyLayerModifiersRestorePatch.PendingIndex = MpRulesJson.WillRestoreLayerModifier(MpSaveLocator.MpRulesPath)
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
                    ModLog.Info($"多人回档种子补记：sidecar 无种子，用存档 FNV 补记 seed={seed}");
                }
            }

            WorldEngineArbiter.PrepareMpRollback(sidecar);
            MultiplayerBridge.PrepareMpRollback(sidecar);
            MpPositionRestorer.PrepareForRollback(sidecar);
            ModLog.Info("多人回档：mp_save 已还原，触发主机重新加载");
            MpRollbackController.TriggerHostReload();
        }

        private static bool ShouldUseMpSave()
            => MultiplayerBridge.IsMultiplayerRunning() && MpSaveLocator.HasMpSave();

        private static SlotSidecar BuildSidecar(GameContext ctx, string nickname, bool isAuto)
        {
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
                IsMultiplayer = ShouldUseMpSave(),
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
            string spPath = string.IsNullOrEmpty(singleplayerSavePath) ? ResolveActiveSingleplayerSavePath() : singleplayerSavePath;
            string svPath = sidecar.IsMultiplayer
                ? (MpSaveLocator.ResolveOwnSavePath() ?? MpSaveLocator.ResolveLocalPlayerSavePath())
                : spPath;
            ModLog.Flow("PrepareWorld", "01", "sidecar=" + sidecar.Describe());
            ApplyLayerModifiersRestorePatch.PendingIndex = sidecar.ActiveLayerModifierIndex;
            RunSettingsBridge.RestoreFromSaveFile(svPath);

            string eff = WorldEngineArbiter.ResolveEffectiveEngineName(sidecar.WorldEngine);
            if (eff == "krok")
            {
                ModLog.Warning("单机回档忽略 krok 引擎，回落 self");
                eff = "self";
            }
            bool wantSelf = eff == "self";
            ModLog.Info($"回档引擎={eff} (sidecar.WorldEngine='{sidecar.WorldEngine}')");
            ModLog.Debug("回档种子来源=" + DescribeRestoreSeedSource(sidecar, eff, svPath));

            if (wantSelf)
            {
                WorldEngineArbiter.Apply(WorldEngine.Self, sidecar.QolSeed, sidecar.QolSeedInput);
                IntroLifepodOnLoadGuardPatch.PendingSkipIntroLifepod = true;
                var pos = ResolveSpawnPosition(sidecar);
                SelfSpawnPatcher.PendingPosition = pos;
                if (pos.HasValue)
                {
                    ModLog.Info($"回档准备：位置模式={sidecar.PosMode} 目标=({pos.Value.x:0.0},{pos.Value.y:0.0})");
                }
                else
                {
                    ModLog.Info($"回档准备：无层内坐标还原（hasPos={sidecar.HasPlayerPos} posMode={sidecar.PosMode}）");
                }
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
                        ModLog.Info($"回档种子补记：sidecar 无种子，用存档 FNV 补记 seed={seed}");
                    }
                }
                WorldEngineArbiter.Apply(WorldEngine.Qol, sidecar.QolSeed, sidecar.QolSeedInput);
                QolBridge.PrepareRollback(sidecar);
            }
        }

        private static string DescribeRestoreSeedSource(SlotSidecar sidecar, string eff, string svPath)
        {
            if (sidecar.QolSeed != 0 || !string.IsNullOrEmpty(sidecar.QolSeedInput))
            {
                return "sidecar（seed=" + sidecar.QolSeed + " input='" + sidecar.QolSeedInput + "'）";
            }
            if (eff == "self")
            {
                return "无（sidecar 未记录种子，self 引擎按默认种子激活）";
            }
            int fnv = QolBridge.ComputeDeterministicSeedFromSaveFile(svPath);
            return fnv != 0
                ? "存档文件 FNV（seed=" + fnv + "）"
                : "无（sidecar 未记录种子且存档文件不可读）";
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

            if (string.IsNullOrEmpty(sidecar.WorldEngine))
            {
                sidecar.WorldEngine = ResolveCurrentPreferredEngineName();
            }
            sidecar.PosMode = WorldEngineArbiter.PositionMode;
            sidecar.FixedX = WorldEngineArbiter.FixedX;
            sidecar.FixedY = WorldEngineArbiter.FixedY;

            InvalidateRunIdCache();
            PrepareWorldForSlot(sidecar, activeSave);
            sidecar.Save(activeSave);
            ModLog.Flow("Sidecar", "01", "写回 " + activeSave + ".json " + sidecar.Describe());
            if (!PathsEqual(activeSave, GameSavePath))
            {
                PersistCurrentSaveSidecar(sidecar);
            }
            ModLog.Flow("ContinuePrep", "04", "完成 path='" + activeSave + "' engine='" + sidecar.WorldEngine + "' seed=" + sidecar.QolSeed);
        }

        private static Vector3? ResolveSpawnPosition(SlotSidecar sidecar)
        {
            if (sidecar == null || !sidecar.HasPlayerPos) return null;
            if (string.Equals(sidecar.PosMode, "fixedPos", StringComparison.OrdinalIgnoreCase))
            {
                return new Vector3(sidecar.FixedX, sidecar.FixedY, 0f);
            }
            return new Vector3(sidecar.PlayerX, sidecar.PlayerY, 0f);
        }

        internal void PersistCurrentSaveForContinue()
        {
            var ctx = SnapshotGameContext();
            PersistCurrentSaveForContinue(ref ctx);
        }

        private void PersistCurrentSaveForContinue(ref GameContext ctx)
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

        private static void ClearPlayerPos(ref GameContext ctx)
        {
            ctx.HasPlayerPos = false;
            ctx.PlayerX = 0f;
            ctx.PlayerY = 0f;
        }

        private static void PersistCurrentSaveSidecar(SlotSidecar sidecar)
        {
            if (sidecar == null || ShouldUseMpSave()) return;
            sidecar.Save(GameSavePath);
            ModLog.Flow("Sidecar", "01", "写回 " + GameSavePath + ".json " + sidecar.Describe());
        }

        private static void PersistMpLocalPlayerSeed(ref GameContext ctx)
        {
            if (IsMpAtLayerBoundary()) ClearPlayerPos(ref ctx);
            if (!MultiplayerBridge.IsServer() || MultiplayerBridge.IsDedicatedServer()) return;

            try
            {
                var cam = PlayerCamera.main;
                if (cam == null || cam.body == null || !MultiplayerBridge.IsLocalPlayerBody(cam.body))
                {
                    return;
                }

                string sv = MpSaveLocator.ResolveOwnSavePath();
                if (string.IsNullOrEmpty(sv) || !File.Exists(sv))
                {
                    ModLog.Info("多人续玩种子持久化跳过：本机玩家 save.sv 暂不存在");
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
                ModLog.Info($"多人续玩种子持久化：path={sv} seed={sc.QolSeed} engine={sc.WorldEngine} firstSeed={firstSeed}");
            }
            catch (Exception ex)
            {
                ModLog.Warning($"PersistMpLocalPlayerSeed 失败：{ex.Message}");
            }
        }

        private static bool IsMpAtLayerBoundary()
        {
            try
            {
                if (File.Exists(MpSaveLocator.MpRulesPath))
                {
                    string json = File.ReadAllText(MpSaveLocator.MpRulesPath, Encoding.UTF8);
                    if (MpRulesJson.TryReadBool(json, MpRulesJson.LevelField, out bool midLevel))
                    {
                        return !midLevel;
                    }
                }
            }
            catch { }
            return IsPlayerAtLayerBoundary();
        }

        private static SlotSidecar BuildFallbackCurrentSaveSidecar(string savePath)
        {
            int seed = QolBridge.ComputeDeterministicSeedFromSaveFile(savePath);
            return new SlotSidecar
            {
                Biome = ReadBiomeFromSv(savePath),
                RunId = ReadRunIdFromSv(savePath),
                LastPlayedAtUnixMs = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                QolSeed = seed,
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

        private static string ResolveCurrentPreferredEngineName()
            => WorldEngineArbiter.ResolveEffectiveEngineName();

        internal void DeleteSlot(string slotFullPath)
        {
            ModLog.Flow("SlotIO", "01", "删除槽位 " + slotFullPath);
            try { if (File.Exists(slotFullPath)) File.Delete(slotFullPath); } catch { }
            SlotSidecar.DeleteFor(slotFullPath);
            try
            {
                string rulesBackup = MpPersistentIdMapper.RulesBackupPath(slotFullPath);
                if (!string.IsNullOrEmpty(rulesBackup) && File.Exists(rulesBackup)) File.Delete(rulesBackup);
            }
            catch { }
        }

        internal void UpdateSidecar(string slotFullPath, SlotSidecar sidecar) => sidecar.Save(slotFullPath);

        /// <summary>查找当前 RunId 下最新的可回档槽位（优先自动槽，排除 beforeLoad- 备份）。</summary>
        internal SlotInfo FindLatestRollbackTarget()
        {
            int currentRunId = ComputeCurrentRunId();
            if (currentRunId == 0) return null;
            var slots = ListSlots().Where(s => s.Sidecar.RunId == currentRunId).ToList();
            bool NotBeforeLoad(SlotInfo s) => !s.File.Name.StartsWith("beforeLoad-", StringComparison.Ordinal);
            return slots.FirstOrDefault(s => s.Sidecar.IsAuto && NotBeforeLoad(s))
                ?? slots.FirstOrDefault(s => !s.Sidecar.IsAuto && NotBeforeLoad(s));
        }

        internal static string ComputeFileHash(string fullPath)
        {
            try
            {
                if (!File.Exists(fullPath)) return null;
                using (var md5 = MD5.Create())
                using (var stream = File.OpenRead(fullPath))
                {
                    byte[] buf = md5.ComputeHash(stream);
                    var sb = new StringBuilder(32);
                    for (int i = 0; i < buf.Length; i++) sb.Append(buf[i].ToString("x2"));
                    return sb.ToString();
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>从单机 GZip 存档或多人 Zip 槽位解析 RunId（cId 或 mp_rules.json 的 SAVEID）。</summary>
        internal static int ReadRunIdFromSv(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath)) return 0;
            try
            {
                byte[] bytes = File.ReadAllBytes(fullPath);
                if (bytes == null || bytes.Length < 2) return 0;

                if (bytes.Length >= 4 && bytes[0] == 0x50 && bytes[1] == 0x4B)
                {
                    try { return MpSaveLocator.ReadRunIdFromZipBytes(bytes); }
                    catch (Exception ex)
                    {
                        if (_cidZeroLogged.Add(fullPath))
                            ModLog.Warning($"ReadRunIdFromSv: 解析多人 zip 槽失败 {Path.GetFileName(fullPath)}: {ex.Message}");
                        return 0;
                    }
                }

                string json;
                try { json = SaveSystem.Unzip(bytes); }
                catch (Exception ex)
                {
                    ModLog.Warning($"ReadRunIdFromSv: Unzip 失败 {fullPath}: {ex.Message}");
                    json = null;
                }

                if (!string.IsNullOrEmpty(json) && TopJson.TryReadInt(json, "cId", out int cid) && cid != 0)
                {
                    return cid;
                }

                if (_cidZeroLogged.Add(fullPath))
                {
                    ModLog.Info($"ReadRunIdFromSv: {Path.GetFileName(fullPath)} 未解出非零 cId，尝试 mp_rules.json 兜底");
                }

                string rulesPath = FindNearbyMpRulesPath(Path.GetDirectoryName(fullPath));
                return rulesPath != null ? MpRulesJson.ReadRunIdFromFile(rulesPath) : 0;
            }
            catch (Exception ex)
            {
                ModLog.Warning($"ReadRunIdFromSv: 处理 {fullPath} 异常 {ex.Message}");
                return 0;
            }
        }

        private static string FindNearbyMpRulesPath(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return null;
            string direct = Path.Combine(dir, MpSaveLocator.MpRulesFile);
            if (File.Exists(direct)) return direct;
            string parent = Path.GetDirectoryName(dir);
            if (!string.IsNullOrEmpty(parent))
            {
                string upper = Path.Combine(parent, MpSaveLocator.MpRulesFile);
                if (File.Exists(upper)) return upper;
            }
            return null;
        }

        private static int ReadBiomeFromSv(string fullPath)
        {
            try
            {
                if (!File.Exists(fullPath)) return 0;
                string json = SaveSystem.Unzip(File.ReadAllBytes(fullPath));
                return !string.IsNullOrEmpty(json) && TopJson.TryReadInt(json, "biome", out int v) ? v : 0;
            }
            catch (Exception ex)
            {
                ModLog.Warning($"ReadBiomeFromSv: 处理 {fullPath} 异常 {ex.Message}");
                return 0;
            }
        }

        /// <summary>读取存档顶层 character 字段（0=Experiment，1=Milky，2=Dune）；缺失或失败时返回 null。</summary>
        internal static int? TryReadCharacterFromSv(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath)) return null;
            try
            {
                string json = SaveSystem.Unzip(File.ReadAllBytes(fullPath));
                if (string.IsNullOrEmpty(json)) return null;
                return TopJson.TryReadInt(json, "character", out int value) ? value : (int?)null;
            }
            catch (Exception ex)
            {
                ModLog.Warning($"TryReadCharacterFromSv: 处理 {fullPath} 异常 {ex.Message}");
                return null;
            }
        }

        /// <summary>读取存档角色；主存档读不到或被写坏为 0 时，使用同局 sidecar 记录的 character 兜底。</summary>
        internal static int? TryReadCharacterWithSidecar(string fullPath)
        {
            int? fromSave = TryReadCharacterFromSv(fullPath);
            if (fromSave.HasValue && fromSave.Value > 0) return fromSave;
            if (string.IsNullOrEmpty(fullPath)) return fromSave;

            SlotSidecar sidecar = SlotSidecar.LoadOrEmpty(fullPath);
            if (sidecar.Character < 0) return fromSave;

            int runIdFromSave = ReadRunIdFromSv(fullPath);
            if (fromSave.HasValue)
            {
                if (sidecar.RunId == 0 || runIdFromSave == 0 || sidecar.RunId != runIdFromSave)
                {
                    if (sidecar.RunId != runIdFromSave && sidecar.RunId != 0 && runIdFromSave != 0)
                    {
                        ModLog.Info($"角色兜底跳过：sidecar RunId={sidecar.RunId} 与存档 RunId={runIdFromSave} 不一致");
                    }
                    return fromSave;
                }
            }
            else if (sidecar.RunId != 0 && runIdFromSave != 0 && sidecar.RunId != runIdFromSave)
            {
                ModLog.Info($"角色兜底跳过：sidecar RunId={sidecar.RunId} 与存档 RunId={runIdFromSave} 不一致");
                return fromSave;
            }

            if (!fromSave.HasValue || fromSave.Value != sidecar.Character)
            {
                ModLog.Info($"读档角色兜底：主存档 character={fromSave?.ToString(CultureInfo.InvariantCulture) ?? "?"} sidecar={sidecar.Character}，采用 sidecar path={fullPath}");
            }

            return sidecar.Character;
        }

        private static int BiomeDepthFromSaveFile(string fullPath)
        {
            int raw = ReadBiomeFromSv(fullPath);
            if (raw <= 0)
            {
                try { return WorldGeneration.world?.biomeDepth ?? 0; }
                catch { return 0; }
            }
            try
            {
                int live = WorldGeneration.world?.biomeDepth ?? -1;
                if (live >= 0 && (raw == live || raw == live + 1)) return live;
            }
            catch { }
            return Math.Max(0, raw - 1);
        }

        private GameContext SnapshotGameContextFromSavedGame()
        {
            var ctx = SnapshotGameContext();
            bool mp = ShouldUseMpSave();
            string spPath = mp ? null : ResolveActiveSingleplayerSavePath();
            string ownSv = mp ? MpSaveLocator.ResolveOwnSavePath() : spPath;
            string sharedSv = mp ? (ownSv ?? MpSaveLocator.ResolveLocalPlayerSavePath()) : spPath;

            if (!string.IsNullOrEmpty(sharedSv) && File.Exists(sharedSv))
            {
                int depth = BiomeDepthFromSaveFile(sharedSv);
                ctx.Biome = depth;
                if (ctx.Character < 0 && !string.IsNullOrEmpty(ownSv) && File.Exists(ownSv))
                {
                    ctx.Character = TryReadCharacterFromSv(ownSv) ?? -1;
                }
                int traveled = MpSaveLayerHelper.ReadTotalTraveledFromSv(sharedSv);
                ModLog.Info($"存档层数：path={sharedSv} biomeDepth={depth} totalTraveled={traveled} rawBiome={ReadBiomeFromSv(sharedSv)} liveDepth={WorldGeneration.world?.biomeDepth}");

                if (!mp && ctx.QolSeed == 0 && ctx.WorldEngine == "qol")
                {
                    int seed = QolBridge.ComputeDeterministicSeedFromSaveFile(sharedSv);
                    if (seed != 0)
                    {
                        ctx.QolSeed = seed;
                        ctx.QolSeedInput = "";
                        QolBridge.PinSeed(seed, "");
                        ModLog.Info($"备份种子补记：QoL 实时种子为 0，用存档 FNV 补记并钉回 seed={seed}");
                    }
                }
            }
            if (mp)
            {
                bool rewriteBiome = !QolBridge.IsQolPresent() && !IsMpAtLayerBoundary();
                MpSaveLayerHelper.NormalizeAfterSnapshot(ctx.Biome, rewriteBiome);
            }
            return ctx;
        }

        /// <summary>玩家身体是否位于当前层底出口区域（y &lt; -halfHeight + 3.1f）。</summary>
        internal static bool IsPlayerAtLayerBoundary()
        {
            try
            {
                var w = WorldGeneration.world;
                var cam = PlayerCamera.main;
                if (w == null || cam == null || cam.body == null) return false;
                return cam.body.transform.position.y < (float)(0L - (long)w.halfHeight) + 3.1f;
            }
            catch
            {
                return false;
            }
        }

        private static void NormalizeSaveBiome(string fullPath, int biome)
        {
            try
            {
                if (!File.Exists(fullPath)) return;
                byte[] bytes = File.ReadAllBytes(fullPath);
                if (bytes == null || bytes.Length == 0) return;
                string json = SaveSystem.Unzip(bytes);
                if (string.IsNullOrEmpty(json)) return;
                string rewritten = TopJson.ReplaceInt(json, "biome", biome);
                if (string.Equals(rewritten, json, StringComparison.Ordinal)) return;
                File.WriteAllBytes(fullPath, SaveSystem.Zip(rewritten));
            }
            catch (Exception ex)
            {
                ModLog.Warning($"NormalizeSaveBiome 失败 {fullPath}: {ex.Message}");
            }
        }

        private static int _cachedRunId;
        private static bool _runIdCacheValid;
        private static float _runIdRetryAt;
        private const float RunIdRetryIntervalSec = 5f;
        private static int _lastLoggedRunId = int.MinValue;
        private static readonly HashSet<string> _cidZeroLogged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        internal static void InvalidateRunIdCache()
        {
            _cachedRunId = 0;
            _runIdCacheValid = false;
            _runIdRetryAt = 0f;
        }

        internal static int ComputeCurrentRunId()
        {
            try
            {
                var view = WoundView.view;
                if (view != null && view.cInfo != null && view.cInfo.Length >= 3)
                {
                    int v = view.cInfo[2];
                    if (v != 0)
                    {
                        _cachedRunId = v;
                        _runIdCacheValid = true;
                        if (_lastLoggedRunId != v)
                        {
                            _lastLoggedRunId = v;
                            ModLog.Flow("RunId", "01", "WoundView.cInfo → runId=" + v);
                        }
                        return v;
                    }
                }
            }
            catch (Exception ex)
            {
                ModLog.Warning($"ComputeCurrentRunId: 读 WoundView.cInfo[2] 异常 {ex.Message}");
            }

            if (_runIdCacheValid && (_cachedRunId != 0 || Time.realtimeSinceStartup < _runIdRetryAt))
                return _cachedRunId;

            int resolved = ResolveRunIdFromFiles();
            _cachedRunId = resolved;
            _runIdCacheValid = true;
            _runIdRetryAt = Time.realtimeSinceStartup + RunIdRetryIntervalSec;
            return resolved;
        }

        private static int ResolveRunIdFromFiles()
        {
            string root = Application.persistentDataPath;
            if (MpSaveLocator.IsMultiplayerSaveActive())
            {
                int fromRules = ReadRunIdFromMpRules();
                if (fromRules != 0)
                {
                    LogResolvedRunId(fromRules, "mp_save/mp_rules.json");
                    return fromRules;
                }

                string ownSv = MpSaveLocator.ResolveOwnSavePath();
                if (!string.IsNullOrEmpty(ownSv))
                {
                    int fromOwn = TryRunIdFromCandidate(ownSv, root);
                    if (fromOwn != 0) return fromOwn;
                    if (SaveSystem.loadedRun)
                    {
                        int scId = SlotSidecar.LoadOrEmpty(ownSv).RunId;
                        if (scId != 0)
                        {
                            LogResolvedRunId(scId, "own save.sv.json");
                            return scId;
                        }
                    }
                }

                string anySv = MpSaveLocator.ResolveLocalPlayerSavePath();
                if (!string.IsNullOrEmpty(anySv) && !string.Equals(anySv, ownSv, StringComparison.OrdinalIgnoreCase))
                {
                    int fromAny = TryRunIdFromCandidate(anySv, root);
                    if (fromAny != 0) return fromAny;
                }
            }
            else
            {
                string currentSp = QolBridge.GetCurrentSaveFilePath();
                int v = TryRunIdFromCandidate(currentSp, root);
                if (v != 0) return v;

                if (!PathsEqual(currentSp, GameSavePath))
                {
                    v = TryRunIdFromCandidate(GameSavePath, root);
                    if (v != 0) return v;
                }

                if (SaveSystem.loadedRun)
                {
                    int sc = SlotSidecar.LoadOrEmpty(currentSp).RunId;
                    if (sc == 0 && !PathsEqual(currentSp, GameSavePath))
                        sc = SlotSidecar.LoadOrEmpty(GameSavePath).RunId;
                    if (sc != 0)
                    {
                        LogResolvedRunId(sc, "save.sv.json");
                        return sc;
                    }
                }

                int fromAuto = TryRunIdFromCandidate(Path.Combine(root, "autosave.sv"), root);
                if (fromAuto != 0) return fromAuto;
            }

            if (_lastLoggedRunId != 0)
            {
                _lastLoggedRunId = 0;
                ModLog.Warning("ComputeCurrentRunId: 当前模式候选存档与 sidecar 均未解出 runId，返回 0");
            }
            return 0;
        }

        private static void LogResolvedRunId(int runId, string source)
        {
            if (_lastLoggedRunId == runId) return;
            _lastLoggedRunId = runId;
            ModLog.Info($"ComputeCurrentRunId: 通过 {source} 解出 runId={runId}");
        }

        private static int TryRunIdFromCandidate(string path, string root)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return 0;
            int v = ReadRunIdFromSv(path);
            if (v == 0) return 0;
            string rel = !string.IsNullOrEmpty(root) && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? path.Substring(root.Length).TrimStart('\\', '/')
                : Path.GetFileName(path);
            LogResolvedRunId(v, rel);
            return v;
        }

        internal static string ComputeCurrentSaveHash()
            => MpSaveLocator.IsMultiplayerSaveActive()
                ? ComputeFileHash(MpSaveLocator.MpRulesPath)
                : ComputeFileHash(GameSavePath);

        private void PruneAutoBackups(int keep)
        {
            try
            {
                int currentRunId = ComputeCurrentRunId();
                var autos = ListSlots()
                    .Where(s => s.Sidecar.IsAuto && !s.Sidecar.Pinned && s.Sidecar.RunId == currentRunId)
                    .OrderByDescending(s => s.Sidecar.LastPlayedAtUnixMs)
                    .ThenByDescending(s => s.File.LastWriteTimeUtc)
                    .Skip(Math.Max(1, keep))
                    .ToList();
                foreach (var s in autos)
                {
                    try { DeleteSlot(s.FullSlotPath); } catch { }
                }
            }
            catch { }
        }

        private string WriteSlotFile(ref GameContext ctx, string prefix)
        {
            bool mp = ShouldUseMpSave();
            string spSource = mp ? null : ResolveActiveSingleplayerSavePath();
            if (!mp && !File.Exists(spSource))
            {
                throw new FileNotFoundException("当前没有单机存档可复制");
            }

            int runId = ctx.RunId != 0
                ? ctx.RunId
                : (mp ? ReadRunIdFromMpRules() : ReadRunIdFromSv(spSource));
            if (ctx.RunId == 0 && runId != 0)
            {
                ctx.RunId = runId;
            }

            string runFolder = runId == 0 ? "0" : runId.ToString(CultureInfo.InvariantCulture);
            string dateFolder = DateTime.Now.ToString("yyyy-MM-dd");
            string fullDir = Path.Combine(_slotsRoot, runFolder, dateFolder);
            Directory.CreateDirectory(fullDir);

            long unixSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string baseName = $"{runFolder}-{ctx.Biome}-{unixSec}";
            if (!string.IsNullOrEmpty(prefix)) baseName = prefix + baseName;

            string dst = Path.Combine(fullDir, baseName + ".sv");
            int n = 1;
            while (File.Exists(dst))
            {
                dst = Path.Combine(fullDir, $"{baseName}-{n}.sv");
                n++;
            }

            try
            {
                if (mp) MpSaveLocator.PackMpSaveTo(dst);
                else File.Copy(spSource, dst, overwrite: false);
            }
            catch
            {
                try { if (File.Exists(dst)) File.Delete(dst); } catch { }
                throw;
            }

            ModLog.Flow("SlotIO", "11", (mp ? "打包 mp_save" : "复制 " + Path.GetFileName(spSource)) + " → " + dst + " runId=" + runId);
            return dst;
        }

        private static int ReadRunIdFromMpRules()
            => MpRulesJson.ReadRunIdFromFile(MpSaveLocator.MpRulesPath);

        private GameContext SnapshotGameContext()
        {
            int biome = 0;
            float px = 0f, py = 0f;
            bool hasPos = false;
            int character = -1;
            try
            {
                if (WorldGeneration.world != null) biome = WorldGeneration.world.biomeDepth;
            }
            catch { }
            try
            {
                var cam = PlayerCamera.main;
                if (cam != null && cam.body != null
                    && !MultiplayerBridge.IsDedicatedServer()
                    && MultiplayerBridge.IsLocalPlayerBody(cam.body))
                {
                    character = cam.body.charType;
                    if (cam.body.alive)
                    {
                        var p = cam.body.transform.position;
                        px = p.x;
                        py = p.y;
                        hasPos = true;
                    }
                }
            }
            catch { }

            QolBridge.ReadCurrentSeed(out int qolSeed, out string qolInput);
            if (SeededWorldEngine.IsActive)
            {
                qolSeed = SeededWorldEngine.CurrentSeed;
                qolInput = SeededWorldEngine.InputString;
            }

            int runId = ComputeCurrentRunId();
            int layerModIndex = LayerModifierSnapshot.CurrentActiveIndex();
            if (_pendingSaveExitCtx.HasValue)
            {
                var pre = _pendingSaveExitCtx.Value;
                if (biome == 0 && pre.Biome != 0) biome = pre.Biome;
                if (!hasPos && pre.HasPlayerPos)
                {
                    px = pre.PlayerX;
                    py = pre.PlayerY;
                    hasPos = true;
                }
                if (qolSeed == 0 && pre.QolSeed != 0)
                {
                    qolSeed = pre.QolSeed;
                    qolInput = pre.QolSeedInput;
                }
                if (runId == 0 && pre.RunId != 0) runId = pre.RunId;
                if (layerModIndex == -2 && pre.ActiveLayerModifierIndex != -2)
                {
                    layerModIndex = pre.ActiveLayerModifierIndex;
                }
                if (character < 0 && pre.Character >= 0) character = pre.Character;
            }

            return new GameContext
            {
                Biome = biome,
                RunId = runId,
                PlayerX = px,
                PlayerY = py,
                HasPlayerPos = hasPos,
                QolSeed = qolSeed,
                QolSeedInput = qolInput,
                WorldEngine = ResolveCurrentPreferredEngineName(),
                ActiveLayerModifierIndex = layerModIndex,
                Character = character,
            };
        }

        private struct GameContext
        {
            public int Biome;
            public int RunId;
            public float PlayerX;
            public float PlayerY;
            public bool HasPlayerPos;
            public int QolSeed;
            public string QolSeedInput;
            public string WorldEngine;
            public int ActiveLayerModifierIndex;
            public int Character;
        }

        private static string ResolveSlotsRoot()
        {
            string asmDir = Path.GetDirectoryName(typeof(SaveStore).Assembly.Location)
                ?? Path.Combine(Paths.PluginPath, "SaveManager");
            return Path.Combine(asmDir, "slots");
        }
    }
}
