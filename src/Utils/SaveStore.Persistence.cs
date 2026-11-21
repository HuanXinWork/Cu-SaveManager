using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using UnityEngine;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>单次解压提取的存档顶层头字段快照。</summary>
    internal struct ParsedSvHeader
    {
        internal int RawBiome;
        internal int TotalTraveled;
        internal int? Character;
        internal int RunId;
    }

    internal sealed partial class SaveStore
    {
        private static bool _startupMigrationsDone;
        private static int _cachedRunId;
        private static bool _runIdCacheValid;
        private static float _runIdRetryAt;
        private const float RunIdRetryIntervalSec = 5f;
        private static int _lastLoggedRunId = int.MinValue;
        private static readonly HashSet<string> _cidZeroLogged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static string GetLegacyPluginSlotsRoot()
        {
            string asmDir = Path.GetDirectoryName(typeof(SaveStore).Assembly.Location);
            if (string.IsNullOrEmpty(asmDir)) asmDir = Path.Combine(Paths.PluginPath, "SaveManager");
            return Path.Combine(asmDir, "slots");
        }

        private static string ResolveSlotsRoot()
        {
            try
            {
                string dataRoot = Application.persistentDataPath;
                if (!string.IsNullOrEmpty(dataRoot))
                {
                    return Path.Combine(dataRoot, "CuSaveManager", "slots");
                }
            }
            catch (Exception ex)
            {
                ModLog.Warning($"读取 persistentDataPath 失败，回退旧插件目录：{ex.Message}");
            }
            return GetLegacyPluginSlotsRoot();
        }

        /// <summary>启动期执行一次幂等恢复与迁移：恢复中断残留 mp_save、搬移旧插件目录槽位并归档无 RunId 槽位。</summary>
        private static void EnsureStartupMigrations(string slotsRoot)
        {
            if (_startupMigrationsDone) return;
            _startupMigrationsDone = true;

            MpSaveLocator.RecoverInterruptedRestoreIfNeeded();
            SlotMigrationHelper.MigrateLegacyPluginDirectory(GetLegacyPluginSlotsRoot(), slotsRoot);
            SlotMigrationHelper.MigrateUnorganizedSlotsOnce(slotsRoot);
        }

        /// <summary>先写入同目录临时文件再通过 File.Replace 原子替换目标存档，杜绝中断产生半截文件。</summary>
        internal static void WriteAllBytesAtomic(string path, byte[] data)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException(nameof(path));
            if (data == null) throw new ArgumentNullException(nameof(data));
            ReplaceTargetViaTemp(path, tmp => File.WriteAllBytes(tmp, data), () => File.WriteAllBytes(path, data));
        }

        /// <summary>先复制到同目录临时文件再通过 File.Replace 原子覆盖目标文件，失败时回退常规复制。</summary>
        internal static void CopyFileAtomic(string src, string dst)
        {
            if (string.IsNullOrEmpty(src)) throw new ArgumentNullException(nameof(src));
            if (string.IsNullOrEmpty(dst)) throw new ArgumentNullException(nameof(dst));
            if (PathsEqual(src, dst)) return;
            ReplaceTargetViaTemp(dst, tmp => File.Copy(src, tmp, overwrite: true), () => File.Copy(src, dst, overwrite: true));
        }

        private static void ReplaceTargetViaTemp(string dst, Action<string> writeTemp, Action fallbackWrite)
        {
            string dir = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            string tmp = dst + ".tmp." + Guid.NewGuid().ToString("N");
            try
            {
                writeTemp(tmp);
                if (File.Exists(dst))
                {
                    try { File.Replace(tmp, dst, null); }
                    catch { fallbackWrite(); }
                    return;
                }
                try { File.Move(tmp, dst); }
                catch { fallbackWrite(); }
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        internal static bool PathsEqual(string a, string b)
            => !string.IsNullOrEmpty(a)
                && !string.IsNullOrEmpty(b)
                && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

        internal static string ComputeFileHash(string fullPath)
        {
            try
            {
                if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath)) return null;
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

        /// <summary>单次读盘并 GZip 解压存档，一次提取顶层 biome、totalTraveled、character 与 cId。</summary>
        internal static bool TryParseSvHeader(string path, out ParsedSvHeader header)
            => TryReadSvWithHeader(path, out header, out _, out _);

        private static bool TryReadSvWithHeader(string path, out ParsedSvHeader header, out byte[] rawBytes, out string unzippedJson)
        {
            header = default;
            rawBytes = null;
            unzippedJson = null;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
            try
            {
                if (IsZipArchiveFile(path))
                {
                    header.RunId = SafeReadRunIdFromZip(path);
                    return header.RunId != 0;
                }

                rawBytes = File.ReadAllBytes(path);
                if (rawBytes == null || rawBytes.Length < 2) return false;

                try { unzippedJson = SaveSystem.Unzip(rawBytes); }
                catch (Exception ex)
                {
                    ModLog.Warning($"TryParseSvHeader: Unzip 失败 {path}: {ex.Message}");
                    unzippedJson = null;
                }

                if (!string.IsNullOrEmpty(unzippedJson))
                {
                    if (TopJson.TryReadInt(unzippedJson, "biome", out int rawBiome)) header.RawBiome = rawBiome;
                    if (TopJson.TryReadInt(unzippedJson, "totalTraveled", out int traveled)) header.TotalTraveled = traveled;
                    if (TopJson.TryReadInt(unzippedJson, "character", out int ch)) header.Character = ch;
                    if (TopJson.TryReadInt(unzippedJson, "cId", out int cid) && cid != 0)
                    {
                        header.RunId = cid;
                        return true;
                    }
                }

                if (_cidZeroLogged.Add(path))
                {
                    ModLog.Debug($"TryParseSvHeader: {Path.GetFileName(path)} 未解出非零 cId，尝试 mp_rules.json 兜底");
                }
                string rulesPath = FindNearbyMpRulesPath(Path.GetDirectoryName(path));
                if (rulesPath != null) header.RunId = MpRulesJson.ReadRunIdFromFile(rulesPath);
                return !string.IsNullOrEmpty(unzippedJson) || header.RunId != 0;
            }
            catch (Exception ex)
            {
                ModLog.Warning($"TryParseSvHeader: 处理 {path} 异常 {ex.Message}");
                return false;
            }
        }

        private static bool IsZipArchiveFile(string fullPath)
        {
            try
            {
                using (var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    return fs.Length >= 4 && fs.ReadByte() == 0x50 && fs.ReadByte() == 0x4B;
                }
            }
            catch
            {
                return false;
            }
        }

        private static int SafeReadRunIdFromZip(string zipPath)
        {
            try
            {
                return MpSaveLocator.ReadRunIdFromZipFile(zipPath);
            }
            catch (Exception ex)
            {
                if (_cidZeroLogged.Add(zipPath))
                {
                    ModLog.Warning($"ReadRunIdFromSv: 解析多人 zip 槽失败 {Path.GetFileName(zipPath)}: {ex.Message}");
                }
                return 0;
            }
        }

        /// <summary>从单机 GZip 存档或多人 Zip 槽位解析 RunId（Zip 走流式读取 mp_rules.json，不整包读入内存）。</summary>
        internal static int ReadRunIdFromSv(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath)) return 0;
            if (IsZipArchiveFile(fullPath)) return SafeReadRunIdFromZip(fullPath);
            return TryParseSvHeader(fullPath, out ParsedSvHeader header) ? header.RunId : 0;
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

        /// <summary>读取存档顶层 character 字段（0=Experiment，1=Milky，2=Dune）；缺失或失败时返回 null。</summary>
        internal static int? TryReadCharacterFromSv(string fullPath)
            => TryParseSvHeader(fullPath, out ParsedSvHeader header) ? header.Character : null;

        /// <summary>单次解压读取存档角色与 RunId；主存档读不到或被写坏为 0 时，用同局 sidecar 记录的 character 兜底。</summary>
        internal static int? TryReadCharacterWithSidecar(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return null;
            TryParseSvHeader(fullPath, out ParsedSvHeader header);
            int? fromSave = header.Character;
            if (fromSave.HasValue && fromSave.Value > 0) return fromSave;

            SlotSidecar sidecar = SlotSidecar.LoadOrEmpty(fullPath);
            if (sidecar.Character < 0) return fromSave;

            int runIdFromSave = header.RunId;
            if (sidecar.RunId != 0 && runIdFromSave != 0 && sidecar.RunId != runIdFromSave)
            {
                ModLog.Debug($"角色兜底跳过：sidecar RunId={sidecar.RunId} 与存档 RunId={runIdFromSave} 不一致");
                return fromSave;
            }
            if (fromSave.HasValue && (sidecar.RunId == 0 || runIdFromSave == 0))
            {
                return fromSave;
            }

            if (!fromSave.HasValue || fromSave.Value != sidecar.Character)
            {
                ModLog.Debug($"读档角色兜底：主存档 character={fromSave?.ToString(CultureInfo.InvariantCulture) ?? "?"} sidecar={sidecar.Character}，采用 sidecar path={fullPath}");
            }
            return sidecar.Character;
        }

        /// <summary>结合实时世界状态还原层深；原版在末层存盘会将 biome 回绕写为 1（live &gt; 1），此时直接采用内存层深。</summary>
        private static int ResolveBiomeDepth(int raw)
        {
            if (raw <= 0)
            {
                try { return WorldGeneration.world?.biomeDepth ?? 0; }
                catch { return 0; }
            }
            try
            {
                int live = WorldGeneration.world?.biomeDepth ?? -1;
                if (live >= 0 && (raw == live || raw == live + 1 || (raw == 1 && live > 1)))
                {
                    return live;
                }
            }
            catch { }
            return Math.Max(0, raw - 1);
        }

        /// <summary>将指定 save.sv 的顶层 biome 字段原子改写为目标层深，内容未变时不写盘。</summary>
        internal static void NormalizeSaveBiome(string fullPath, int biome)
        {
            try
            {
                if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath)) return;
                byte[] bytes = File.ReadAllBytes(fullPath);
                if (bytes == null || bytes.Length == 0) return;
                NormalizeSaveBiomeFromJson(fullPath, SaveSystem.Unzip(bytes), biome);
            }
            catch (Exception ex)
            {
                ModLog.Warning($"NormalizeSaveBiome 失败 {fullPath}: {ex.Message}");
            }
        }

        private static void NormalizeSaveBiomeFromJson(string fullPath, string json, int biome)
        {
            try
            {
                if (string.IsNullOrEmpty(fullPath) || string.IsNullOrEmpty(json)) return;
                string rewritten = TopJson.ReplaceInt(json, "biome", biome);
                if (string.Equals(rewritten, json, StringComparison.Ordinal)) return;
                WriteAllBytesAtomic(fullPath, SaveSystem.Zip(rewritten));
            }
            catch (Exception ex)
            {
                ModLog.Warning($"NormalizeSaveBiome 失败 {fullPath}: {ex.Message}");
            }
        }

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
                ModLog.Debug("ComputeCurrentRunId: 当前模式候选存档与 sidecar 均未解出 runId，返回 0");
            }
            return 0;
        }

        private static void LogResolvedRunId(int runId, string source)
        {
            if (_lastLoggedRunId == runId) return;
            _lastLoggedRunId = runId;
            ModLog.Debug($"ComputeCurrentRunId: 通过 {source} 解出 runId={runId}");
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

        private string WriteSlotFile(ref GameContext ctx, string prefix, string spSourceOverride = null, bool forceMp = false)
        {
            bool mp = forceMp || (string.IsNullOrEmpty(spSourceOverride) && ShouldUseMpSave());
            string spSource = mp ? null : (spSourceOverride ?? ResolveActiveSingleplayerSavePath());
            if (!mp && !File.Exists(spSource)) throw new FileNotFoundException("当前没有单机存档可复制");

            int runId = ctx.RunId != 0 ? ctx.RunId : (mp ? ReadRunIdFromMpRules() : ReadRunIdFromSv(spSource));
            if (ctx.RunId == 0 && runId != 0) ctx.RunId = runId;

            string runFolder = runId == 0 ? "0" : runId.ToString(CultureInfo.InvariantCulture);
            string dateFolder = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
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
                else CopyFileAtomic(spSource, dst);
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

        private GameContext SnapshotMpSaveContextForBackup()
        {
            var ctx = SnapshotGameContext();
            if (ctx.RunId == 0) ctx.RunId = ReadRunIdFromMpRules();

            string sv = MpSaveLocator.ResolveOwnSavePath() ?? MpSaveLocator.ResolveLocalPlayerSavePath();
            if (!string.IsNullOrEmpty(sv) && TryParseSvHeader(sv, out ParsedSvHeader header))
            {
                if (WorldGeneration.world == null || ctx.Biome == 0)
                {
                    ctx.Biome = ResolveBiomeDepth(header.RawBiome);
                }
                if (ctx.RunId == 0 && header.RunId != 0) ctx.RunId = header.RunId;
                if (ctx.Character < 0 && header.Character.HasValue) ctx.Character = header.Character.Value;
            }
            if (!string.IsNullOrEmpty(sv) && ctx.QolSeed == 0)
            {
                var sc = SlotSidecar.LoadOrEmpty(sv);
                if (sc.QolSeed != 0)
                {
                    ctx.QolSeed = sc.QolSeed;
                    ctx.QolSeedInput = sc.QolSeedInput ?? "";
                }
                if (!string.IsNullOrEmpty(sc.WorldEngine)) ctx.WorldEngine = sc.WorldEngine;
            }
            return ctx;
        }

        private GameContext SnapshotGameContextFromSavedGame(out string preReadSpPath, out string preUnzippedSpJson)
        {
            preReadSpPath = null;
            preUnzippedSpJson = null;

            var ctx = SnapshotGameContext();
            bool mp = ShouldUseMpSave();
            string spPath = mp ? null : ResolveActiveSingleplayerSavePath();
            string ownSv = mp ? MpSaveLocator.ResolveOwnSavePath() : spPath;
            string sharedSv = mp ? (ownSv ?? MpSaveLocator.ResolveLocalPlayerSavePath()) : spPath;

            if (!string.IsNullOrEmpty(sharedSv)
                && TryReadSvWithHeader(sharedSv, out ParsedSvHeader header, out byte[] rawBytes, out string unzippedJson))
            {
                if (!mp)
                {
                    preReadSpPath = sharedSv;
                    preUnzippedSpJson = unzippedJson;
                }

                int depth = ResolveBiomeDepth(header.RawBiome);
                ctx.Biome = depth;
                if (ctx.RunId == 0 && header.RunId != 0) ctx.RunId = header.RunId;
                if (ctx.Character < 0 && !string.IsNullOrEmpty(ownSv))
                {
                    if (PathsEqual(ownSv, sharedSv))
                    {
                        ctx.Character = header.Character ?? -1;
                    }
                    else if (TryParseSvHeader(ownSv, out ParsedSvHeader ownHeader))
                    {
                        ctx.Character = ownHeader.Character ?? -1;
                    }
                }
                ModLog.Debug($"存档层数：path={sharedSv} biomeDepth={depth} totalTraveled={header.TotalTraveled} rawBiome={header.RawBiome} liveDepth={WorldGeneration.world?.biomeDepth}");

                if (!mp && ctx.QolSeed == 0 && ctx.WorldEngine == "qol")
                {
                    int seed = QolBridge.ComputeDeterministicSeedFromBytes(rawBytes);
                    if (seed != 0)
                    {
                        ctx.QolSeed = seed;
                        ctx.QolSeedInput = "";
                        QolBridge.PinSeed(seed, "");
                        ModLog.Debug($"备份种子补记：QoL 实时种子为 0，用存档 FNV 补记并钉回 seed={seed}");
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

        private GameContext SnapshotGameContext()
        {
            int biome = 0;
            float px = 0f, py = 0f;
            bool hasPos = false;
            int character = -1;
            try { if (WorldGeneration.world != null) biome = WorldGeneration.world.biomeDepth; }
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
                if (!hasPos && pre.HasPlayerPos) { px = pre.PlayerX; py = pre.PlayerY; hasPos = true; }
                if (qolSeed == 0 && pre.QolSeed != 0) { qolSeed = pre.QolSeed; qolInput = pre.QolSeedInput; }
                if (runId == 0 && pre.RunId != 0) runId = pre.RunId;
                if (layerModIndex == -2 && pre.ActiveLayerModifierIndex != -2) layerModIndex = pre.ActiveLayerModifierIndex;
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

        private static bool ShouldUseMpSave()
            => MultiplayerBridge.IsMultiplayerRunning() && MpSaveLocator.HasMpSave();

        private static string ResolveCurrentPreferredEngineName()
            => WorldEngineArbiter.ResolveEffectiveEngineName();

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
    }
}
