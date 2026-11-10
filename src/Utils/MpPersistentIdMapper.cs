using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CasualtiesUnknown.SaveManager.Interop;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>
    /// 多人存档的玩家持久 ID 记录与回档重映射。
    /// 联机模组按「当前持久 ID」拼 mp_save/&lt;persistentId&gt;/save.sv 与 mp_rules.json 里 PLRPOS 的键：
    /// Steam 传输下是 STEAM_&lt;steamId&gt;，直连下是 NAME_&lt;玩家名 UTF8 十六进制&gt;。
    /// 换传输方式或改玩家名后旧 id 全部失配，联机模组找不到 save.sv、PLRPOS 查不到坐标（只写 warn 日志）。
    /// 本类在保存时把当时的玩家身份写进槽位 sidecar，回档解包后把槽位里的旧 id 换成当前会话的 id；
    /// 对不上时显式拒绝回档，不做静默降级。
    /// </summary>
    internal static class MpPersistentIdMapper
    {
        /// <summary>mp_rules.json 诊断副本的扩展名，落在槽位 sidecar 同目录。</summary>
        private const string RulesBackupSuffix = ".mprules.json";

        /// <summary>两阶段重命名用的临时目录后缀。</summary>
        private const string RenameTempSuffix = ".pidtmp";

        /// <summary>
        /// 保存时记录存档内各玩家身份（持久 ID / 玩家名 / Steam ID）到槽位 sidecar。
        /// 持久 ID 集合取自当前 mp_save 的 mp_rules.json 里 PLRPOS 的键（缺失时回落玩家子目录名），
        /// 玩家名与 Steam ID 取自联机模组名册，供回档时跨传输方式认人。
        /// </summary>
        /// <param name="sidecar">待写入的槽位伴生元数据</param>
        internal static void CaptureForSlot(SlotSidecar sidecar)
        {
            if (sidecar == null) return;

            List<InteropPlayerIdentity> session = MultiplayerBridge.GetSessionPlayerIdentities();
            var players = new List<SlotMpPlayer>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string persistentId in MpPositionRestorer.ReadPlrPosFromMpRules().Keys)
            {
                AddCaptured(players, seen, persistentId, session);
            }

            if (players.Count == 0)
            {
                // mp_rules.json 还没写出 PLRPOS 时回落 mp_save 的玩家子目录名，至少把旧 id 记下来
                foreach (string persistentId in MpSaveLocator.ListPersistentIdDirs())
                {
                    AddCaptured(players, seen, persistentId, session);
                }
            }

            sidecar.MpPlayers = players;
            if (players.Count == 0)
            {
                ModLog.Warning("多人存档 pid 记录：mp_save 内没找到玩家 pid（PLRPOS 与玩家子目录都为空）");
                return;
            }

            ModLog.Info($"多人存档 pid 记录：{players.Count} 名玩家（{DescribePlayers(players)}）");
        }

        /// <summary>mp_rules.json 诊断副本路径：与槽位 sidecar（&lt;slot&gt;.sv.json）同目录。</summary>
        /// <param name="slotPath">槽位文件全路径</param>
        internal static string RulesBackupPath(string slotPath)
            => string.IsNullOrEmpty(slotPath) ? null : slotPath + RulesBackupSuffix;

        /// <summary>
        /// 回档解包后调用：把暂存目录里的旧玩家持久 ID 换成当前会话的持久 ID。
        /// 先把槽位记录的玩家映射到当前会话：无法判定身份时直接抛 <see cref="InvalidOperationException"/>；
        /// 已确认不在场的玩家不参与改写，其旧子目录与 PLRPOS 键保持原样。
        /// 然后才把 mp_save/&lt;旧 pid&gt; 子目录改名、把 mp_rules.json 里 PLRPOS 的键改成新 pid；其余字段原值保留。
        /// 旧版本槽位没有 pid 记录时只能按 pid 完全相同匹配，匹配不上就记警告并按原样还原。
        /// </summary>
        /// <param name="extractedDir">槽位 zip 解包后的暂存目录</param>
        /// <param name="slotPath">槽位文件全路径（诊断副本与日志用）</param>
        /// <param name="sidecar">槽位伴生元数据，含保存时记录的玩家身份</param>
        internal static void RewriteRestoredSave(string extractedDir, string slotPath, SlotSidecar sidecar)
        {
            if (string.IsNullOrEmpty(extractedDir) || !Directory.Exists(extractedDir))
            {
                throw new InvalidOperationException("多人回档已中止：mp_save 暂存目录不存在（" + extractedDir + "）");
            }

            string rulesPath = Path.Combine(extractedDir, MpSaveLocator.MpRulesFile);
            if (!File.Exists(rulesPath))
            {
                throw new InvalidOperationException("多人回档已中止：槽位内缺少 " + MpSaveLocator.MpRulesFile);
            }

            string rules = File.ReadAllText(rulesPath);
            // LEVEL / MODIFIER 只做诊断，不改写：它们是联机模组按存档时刻的层内状态写下的，
            // 读档时联机模组按 MODIFIER 还原本层 LayerModifier，改掉会让层内回档永久丢词条。
            ModLog.Info("多人回档 mp_rules 原值（" + Path.GetFileName(slotPath) + "）：" + MpRulesJson.Describe(rulesPath)
                + "；本 mod 保持联机模组写入的 LEVEL/MODIFIER 原值");
            BackupRules(slotPath, rules);

            bool legacy = sidecar == null || sidecar.MpPlayers == null || sidecar.MpPlayers.Count == 0;
            List<SlotMpPlayer> saved = legacy ? BuildLegacyPlayers(rules) : DedupeSidecarPlayers(sidecar.MpPlayers);
            if (saved.Count == 0)
            {
                ModLog.Warning("多人回档：槽位既没有玩家 pid 记录也没有 PLRPOS，跳过 pid 映射（按原样还原）");
                return;
            }
            if (legacy)
            {
                ModLog.Warning("多人回档：该槽位是旧版本槽位（没有玩家 pid 记录），只能按 pid 完全相同匹配；"
                    + "若传输方式或玩家名已变，联机模组仍会找不到 save.sv 且 PLRPOS 查不到坐标");
            }

            List<InteropPlayerIdentity> session = MultiplayerBridge.GetSessionPlayerIdentities();
            if (session.Count == 0)
            {
                throw new InvalidOperationException(I18n.T("mp.pid_session_unavailable"));
            }

            var problems = new List<string>();
            Dictionary<string, string> mapping = BuildMapping(saved, session, problems, out List<SlotMpPlayer> absent);
            if (problems.Count > 0)
            {
                string detail = string.Join("；", problems);
                if (legacy)
                {
                    ModLog.Warning("多人回档：旧槽位玩家 pid 无法与当前会话对应，按原样还原（不改写 pid）。" + detail);
                    return;
                }

                throw new InvalidOperationException(I18n.F("mp.pid_map_failed", detail, DescribePlayers(session)));
            }
            if (absent.Count > 0)
            {
                ModLog.Warning($"多人回档 pid 映射：存档里的 {absent.Count} 名玩家不在当前会话（{DescribePlayers(absent)}），"
                    + "其子目录与 PLRPOS 键保持原样；该玩家重新进房时若 pid 已变，需再次回档才能取回角色");
            }

            if (!HasChanges(mapping))
            {
                ModLog.Info($"多人回档 pid 映射：已映射的 {mapping.Count} 名玩家 pid 与当前会话一致，无需改写子目录与 PLRPOS");
                return;
            }

            LogMapping(saved, mapping);
            ApplyDirectoryRenames(extractedDir, saved, mapping);
            ApplyPlrPosKeys(rulesPath, rules, mapping);
            ModLog.Info("多人回档 pid 改写完成：玩家子目录与 mp_rules.json 的 PLRPOS 键已对齐当前会话");
        }

        /// <summary>把旧槽位的 PLRPOS 键当作玩家记录（无名字、无 Steam ID）。</summary>
        private static List<SlotMpPlayer> BuildLegacyPlayers(string rules)
        {
            var players = new List<SlotMpPlayer>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string persistentId in MpRulesJson.ReadPlrPosKeys(rules))
            {
                if (string.IsNullOrEmpty(persistentId) || !seen.Add(persistentId)) continue;
                players.Add(new SlotMpPlayer(persistentId, "", 0UL));
            }
            return players;
        }

        /// <summary>去掉 sidecar 里重复或空的持久 ID，避免映射时同一玩家被算两次。</summary>
        private static List<SlotMpPlayer> DedupeSidecarPlayers(List<SlotMpPlayer> recorded)
        {
            var players = new List<SlotMpPlayer>(recorded.Count);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var record in recorded)
            {
                if (string.IsNullOrEmpty(record.PersistentId) || !seen.Add(record.PersistentId)) continue;
                players.Add(record);
            }
            return players;
        }

        /// <summary>把当前会话里的玩家身份补进捕获列表。
        /// 名册里找不到同名 pid 时，从 pid 本身反推：NAME_&lt;十六进制&gt; 能解出玩家名，STEAM_&lt;数字&gt; 能解出 Steam ID。</summary>
        private static void AddCaptured(List<SlotMpPlayer> into, HashSet<string> seen, string persistentId,
            IReadOnlyList<InteropPlayerIdentity> session)
        {
            if (string.IsNullOrEmpty(persistentId) || !seen.Add(persistentId)) return;
            string name = "";
            ulong steamId = 0UL;
            for (int i = 0; i < session.Count; i++)
            {
                if (!string.Equals(session[i].PersistentId, persistentId, StringComparison.Ordinal)) continue;
                name = session[i].PlayerName ?? "";
                steamId = session[i].SteamId;
                break;
            }
            if (string.IsNullOrEmpty(name)) name = DecodeNameFromPersistentId(persistentId);
            if (steamId == 0UL) steamId = ParseSteamIdFromPersistentId(persistentId);
            if (string.IsNullOrEmpty(name) && steamId == 0UL)
            {
                ModLog.Warning($"多人存档 pid 记录：{persistentId} 既无玩家名也无 Steam ID，换传输方式后将无法把旧 pid 认回本人");
            }
            into.Add(new SlotMpPlayer(persistentId, name, steamId));
        }

        /// <summary>从 NAME_&lt;十六进制&gt; 形式的持久 ID 解出玩家名；不是该形式或解码失败时返回空字符串。</summary>
        private static string DecodeNameFromPersistentId(string persistentId)
        {
            const string Prefix = "NAME_";
            if (string.IsNullOrEmpty(persistentId)
                || !persistentId.StartsWith(Prefix, StringComparison.Ordinal)) return "";
            string hex = persistentId.Substring(Prefix.Length);
            if (hex.Length == 0 || hex.Length % 2 != 0) return "";
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                if (!byte.TryParse(hex.Substring(i * 2, 2), System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out bytes[i]))
                {
                    return "";
                }
            }
            return Encoding.UTF8.GetString(bytes);
        }

        /// <summary>从 STEAM_&lt;数字&gt; 形式的持久 ID 解出 Steam ID；不是该形式时返回 0。</summary>
        private static ulong ParseSteamIdFromPersistentId(string persistentId)
        {
            const string Prefix = "STEAM_";
            if (string.IsNullOrEmpty(persistentId)
                || !persistentId.StartsWith(Prefix, StringComparison.Ordinal)) return 0UL;
            return ulong.TryParse(persistentId.Substring(Prefix.Length), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out ulong steamId) ? steamId : 0UL;
        }

        /// <summary>
        /// 把槽位记录的玩家映射到当前会话的玩家：先按持久 ID 完全相同匹配，再按 Steam ID，最后按玩家名。
        /// 每一轮只接受无争议的配对（一个存档玩家恰好认领一个当前玩家，且该玩家只被一个存档玩家认领）。
        /// 剩下的分两种：
        /// 1) 当前会话还有未被认领的玩家 → 无法判定是存档玩家改了名还是新加入的人 → 写进 <paramref name="problems"/> 拒绝回档；
        /// 2) 当前会话的玩家已全部配对 → 剩下的存档玩家确实不在场，归入 <paramref name="absent"/>，保留其旧目录与 PLRPOS 键即可。
        /// 另会校验改写后的持久 ID 不重复，否则子目录与 PLRPOS 会互相覆盖。
        /// </summary>
        /// <param name="saved">槽位记录的玩家</param>
        /// <param name="session">当前会话的玩家</param>
        /// <param name="problems">无法安全改写的原因；非空时调用方应拒绝回档</param>
        /// <param name="absent">不在当前会话的存档玩家（不参与改写）</param>
        private static Dictionary<string, string> BuildMapping(List<SlotMpPlayer> saved,
            IReadOnlyList<InteropPlayerIdentity> session, List<string> problems, out List<SlotMpPlayer> absent)
        {
            var mapping = new Dictionary<string, string>(StringComparer.Ordinal);
            var used = new HashSet<string>(StringComparer.Ordinal);
            var pending = new List<SlotMpPlayer>(saved);
            var ambiguous = new HashSet<string>(StringComparer.Ordinal);

            MatchTier(pending, session, used, mapping, ambiguous, SamePersistentId);
            MatchTier(pending, session, used, mapping, ambiguous, SameSteamId);
            MatchTier(pending, session, used, mapping, ambiguous, SameName);

            var unmatchedPresent = new List<InteropPlayerIdentity>();
            for (int i = 0; i < session.Count; i++)
            {
                if (!used.Contains(session[i].PersistentId)) unmatchedPresent.Add(session[i]);
            }

            absent = pending;
            string presentText = DescribePlayers(unmatchedPresent);
            foreach (var record in absent)
            {
                string who = DescribePlayer(record.Name, record.PersistentId);
                if (ambiguous.Contains(record.PersistentId))
                {
                    // 同一存档玩家能对上多名当前玩家，或者多名存档玩家抢同一人：无论对方在不在场都不能猜
                    problems.Add(I18n.F("mp.pid_map_ambiguous", who));
                }
                else if (unmatchedPresent.Count > 0)
                {
                    problems.Add(I18n.F("mp.pid_map_unresolved", who, presentText));
                }
            }

            // 改写后的持久 ID 必须互不重复，否则子目录会互相覆盖、PLRPOS 会出重复键
            var finals = new HashSet<string>(StringComparer.Ordinal);
            var collisions = new HashSet<string>(StringComparer.Ordinal);
            foreach (var record in saved)
            {
                string target = mapping.TryGetValue(record.PersistentId, out string mapped) ? mapped : record.PersistentId;
                if (!finals.Add(target)) collisions.Add(target);
            }
            foreach (string target in collisions)
            {
                problems.Add(I18n.F("mp.pid_map_collision", target));
            }

            return mapping;
        }

        /// <summary>在一轮匹配里只接受无争议的配对，其余留给下一轮。</summary>
        private static void MatchTier(List<SlotMpPlayer> pending, IReadOnlyList<InteropPlayerIdentity> session,
            HashSet<string> used, Dictionary<string, string> mapping, HashSet<string> ambiguous,
            Func<SlotMpPlayer, InteropPlayerIdentity, bool> match)
        {
            if (pending.Count == 0) return;

            var candidates = new List<List<InteropPlayerIdentity>>(pending.Count);
            var claims = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var record in pending)
            {
                var matched = new List<InteropPlayerIdentity>();
                for (int i = 0; i < session.Count; i++)
                {
                    if (used.Contains(session[i].PersistentId)) continue;
                    if (match(record, session[i])) matched.Add(session[i]);
                }

                candidates.Add(matched);
                if (matched.Count > 1) ambiguous.Add(record.PersistentId);
                if (matched.Count == 1)
                {
                    string target = matched[0].PersistentId;
                    claims.TryGetValue(target, out int count);
                    claims[target] = count + 1;
                }
            }

            var accepted = new List<KeyValuePair<SlotMpPlayer, InteropPlayerIdentity>>();
            for (int i = 0; i < pending.Count; i++)
            {
                var matched = candidates[i];
                if (matched.Count != 1) continue;
                if (claims[matched[0].PersistentId] != 1)
                {
                    // 多个存档玩家同时认领同一个当前玩家：无法判定谁是谁
                    ambiguous.Add(pending[i].PersistentId);
                    continue;
                }
                accepted.Add(new KeyValuePair<SlotMpPlayer, InteropPlayerIdentity>(pending[i], matched[0]));
            }

            foreach (var pair in accepted)
            {
                mapping[pair.Key.PersistentId] = pair.Value.PersistentId;
                used.Add(pair.Value.PersistentId);
                pending.RemoveAll(record => string.Equals(record.PersistentId, pair.Key.PersistentId, StringComparison.Ordinal));
            }
        }

        private static bool SamePersistentId(SlotMpPlayer record, InteropPlayerIdentity player)
            => string.Equals(record.PersistentId, player.PersistentId, StringComparison.Ordinal);

        private static bool SameSteamId(SlotMpPlayer record, InteropPlayerIdentity player)
            => record.SteamId != 0UL && record.SteamId == player.SteamId;

        private static bool SameName(SlotMpPlayer record, InteropPlayerIdentity player)
            => !string.IsNullOrEmpty(record.Name) && string.Equals(record.Name, player.PlayerName, StringComparison.Ordinal);

        private static bool HasChanges(Dictionary<string, string> mapping)
        {
            foreach (var pair in mapping)
            {
                if (!string.Equals(pair.Key, pair.Value, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private static void LogMapping(List<SlotMpPlayer> saved, Dictionary<string, string> mapping)
        {
            foreach (var record in saved)
            {
                if (!mapping.TryGetValue(record.PersistentId, out string target)) continue;
                if (string.Equals(record.PersistentId, target, StringComparison.Ordinal)) continue;
                ModLog.Info($"多人回档 pid 改写：{DescribePlayer(record.Name, record.PersistentId)} → \"{target}\"");
            }
        }

        /// <summary>
        /// 把 mp_save 下的玩家子目录从旧持久 ID 改名成新持久 ID。
        /// 先全部改成临时名再改成目标名，避免 A→B、B→A 互换时互相覆盖；
        /// 目标目录若是同一玩家早先用另一种传输方式留下的旧目录（不属于本槽位），先让位给槽位里的进度并记警告。
        /// </summary>
        private static void ApplyDirectoryRenames(string extractedDir, List<SlotMpPlayer> saved,
            Dictionary<string, string> mapping)
        {
            var renames = new List<KeyValuePair<string, string>>();
            var renamedFrom = new HashSet<string>(StringComparer.Ordinal);
            foreach (var record in saved)
            {
                if (!mapping.TryGetValue(record.PersistentId, out string target)) continue;
                if (string.Equals(record.PersistentId, target, StringComparison.Ordinal)) continue;
                string from = Path.Combine(extractedDir, record.PersistentId);
                if (!Directory.Exists(from))
                {
                    ModLog.Debug($"多人回档 pid 改写：槽位内没有 {record.PersistentId} 子目录，跳过目录重命名");
                    continue;
                }
                if (string.Equals(record.PersistentId, target, StringComparison.OrdinalIgnoreCase))
                {
                    // Windows 文件名不区分大小写：同一个目录，联机模组按新 id 也能找到 save.sv
                    ModLog.Debug($"多人回档 pid 改写：{record.PersistentId} → {target} 仅大小写不同，目录无需重命名");
                    continue;
                }
                renames.Add(new KeyValuePair<string, string>(from, target));
                renamedFrom.Add(record.PersistentId);
            }

            if (renames.Count == 0) return;

            // 目标名已被占用：要么是本轮要改名的旧目录（下面两阶段重命名会让位），
            // 要么是同一玩家早先留下的旧目录，它不属于本槽位，让位后由槽位内容取代。
            foreach (var pair in renames)
            {
                string to = Path.Combine(extractedDir, pair.Value);
                if (!Directory.Exists(to) || renamedFrom.Contains(pair.Value)) continue;
                ModLog.Warning($"多人回档 pid 改写：mp_save 内已有旧目录 {pair.Value}（同一玩家早先的进度），已被槽位内容取代");
                Directory.Delete(to, recursive: true);
            }

            var staged = new List<KeyValuePair<string, string>>(renames.Count);
            foreach (var pair in renames)
            {
                string temp = pair.Key + RenameTempSuffix;
                if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
                Directory.Move(pair.Key, temp);
                staged.Add(new KeyValuePair<string, string>(temp, Path.Combine(extractedDir, pair.Value)));
            }

            foreach (var pair in staged)
            {
                if (Directory.Exists(pair.Value))
                {
                    throw new InvalidOperationException(I18n.F("mp.pid_map_dir_taken", Path.GetFileName(pair.Value)));
                }
                Directory.Move(pair.Key, pair.Value);
            }
        }

        /// <summary>改写 mp_rules.json 里 PLRPOS 的键，并在写盘后校验键集合，改写结果不合法时抛异常拒绝回档。</summary>
        private static void ApplyPlrPosKeys(string rulesPath, string rules, Dictionary<string, string> mapping)
        {
            var originalKeys = MpRulesJson.ReadPlrPosKeys(rules);
            if (originalKeys.Count == 0)
            {
                ModLog.Warning("多人回档：mp_rules.json 没有 PLRPOS，跳过坐标表键名改写");
                return;
            }

            string rewritten = MpRulesJson.RewritePlrPosKeys(rules, mapping);
            if (string.Equals(rewritten, rules, StringComparison.Ordinal))
            {
                ModLog.Info("多人回档：PLRPOS 键与当前会话一致，未改写 mp_rules.json");
                return;
            }

            File.WriteAllText(rulesPath, rewritten);

            var expected = new HashSet<string>(StringComparer.Ordinal);
            foreach (string key in originalKeys)
            {
                expected.Add(mapping.TryGetValue(key, out string target) ? target : key);
            }
            var actual = new HashSet<string>(StringComparer.Ordinal);
            foreach (string key in MpRulesJson.ReadPlrPosKeys(rewritten))
            {
                actual.Add(key);
            }
            if (!expected.SetEquals(actual))
            {
                throw new InvalidOperationException("多人回档已中止：mp_rules.json 的 PLRPOS 键改写后校验失败（预期 "
                    + expected.Count + " 个键，实际 " + actual.Count + " 个）");
            }

            foreach (string key in originalKeys)
            {
                if (mapping.TryGetValue(key, out string target) && !string.Equals(key, target, StringComparison.Ordinal))
                {
                    ModLog.Info($"mp_rules PLRPOS 键改写：\"{key}\" → \"{target}\"");
                }
            }
        }

        /// <summary>把改写前的 mp_rules.json 原文写到槽位 sidecar 同目录，便于事后排查；写失败只记警告。</summary>
        private static void BackupRules(string slotPath, string rules)
        {
            try
            {
                string backupPath = RulesBackupPath(slotPath);
                if (string.IsNullOrEmpty(backupPath)) return;
                File.WriteAllText(backupPath, rules);
                ModLog.Info("mp_rules 原始副本已写入 sidecar 同目录：" + backupPath);
            }
            catch (Exception ex)
            {
                ModLog.Warning($"mp_rules 原始副本写入失败（不影响回档）：{ex.Message}");
            }
        }

        private static string DescribePlayers(List<SlotMpPlayer> players)
        {
            var parts = new List<string>(players.Count);
            foreach (var record in players) parts.Add(DescribePlayer(record.Name, record.PersistentId));
            return parts.Count == 0 ? "（无）" : string.Join("、", parts);
        }

        private static string DescribePlayers(IReadOnlyList<InteropPlayerIdentity> session)
        {
            var parts = new List<string>(session.Count);
            for (int i = 0; i < session.Count; i++) parts.Add(DescribePlayer(session[i].PlayerName, session[i].PersistentId));
            return parts.Count == 0 ? "（无）" : string.Join("、", parts);
        }

        private static string DescribePlayer(string name, string persistentId)
            => string.IsNullOrEmpty(name) ? persistentId : name + "（" + persistentId + "）";
    }
}
