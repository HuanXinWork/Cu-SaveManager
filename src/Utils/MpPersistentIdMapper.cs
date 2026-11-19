using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using CasualtiesUnknown.SaveManager.Interop;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>多人存档玩家持久 ID 捕获与回档重映射，跨传输方式（Steam / 直连）将旧 ID 对齐到当前会话。</summary>
    internal static class MpPersistentIdMapper
    {
        private const string RulesBackupSuffix = ".mprules.json";
        private const string RenameTempSuffix = ".pidtmp";

        /// <summary>捕获当前 mp_save 内所有玩家（PLRPOS 键与玩家子目录并集）的身份信息写入 sidecar。</summary>
        internal static void CaptureForSlot(SlotSidecar sidecar)
        {
            if (sidecar == null) return;

            List<InteropPlayerIdentity> session = MultiplayerBridge.GetSessionPlayerIdentities();
            var players = new List<SlotMpPlayer>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                if (File.Exists(MpSaveLocator.MpRulesPath))
                {
                    string rulesJson = File.ReadAllText(MpSaveLocator.MpRulesPath, Encoding.UTF8);
                    foreach (string persistentId in MpRulesJson.ReadPlrPosKeys(rulesJson))
                    {
                        AddCaptured(players, seen, persistentId, session);
                    }
                    foreach (string persistentId in MpRulesJson.ReadPlrCharsKeys(rulesJson))
                    {
                        AddCaptured(players, seen, persistentId, session);
                    }
                }
            }
            catch { }
            foreach (string persistentId in MpSaveLocator.ListPersistentIdDirs())
            {
                AddCaptured(players, seen, persistentId, session);
            }

            sidecar.MpPlayers = players;
            if (players.Count == 0)
            {
                ModLog.Warning("多人存档 pid 记录：mp_save 内未找到玩家 pid（PLRPOS 与玩家子目录均为空）");
                return;
            }

            ModLog.Info($"多人存档 pid 记录：{players.Count} 名玩家（{DescribePlayers(players)}）");
        }

        internal static string RulesBackupPath(string slotPath)
            => string.IsNullOrEmpty(slotPath) ? null : slotPath + RulesBackupSuffix;

        /// <summary>回档解包后将暂存目录中的旧玩家子目录与 PLRPOS 键重映射为当前会话持久 ID；身份存在歧义或未匹配在场玩家时抛异常中止。</summary>
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

            string rules = File.ReadAllText(rulesPath, Encoding.UTF8);
            ModLog.Info("多人回档 mp_rules 原值（" + Path.GetFileName(slotPath) + "）：" + MpRulesJson.Describe(rulesPath)
                + "；保持 LEVEL/MODIFIER 原值");
            BackupRules(slotPath, rules);

            bool legacy = sidecar == null || sidecar.MpPlayers == null || sidecar.MpPlayers.Count == 0;
            List<SlotMpPlayer> saved = legacy ? BuildLegacyPlayers(rules, extractedDir) : DedupeSidecarPlayers(sidecar.MpPlayers);
            if (saved.Count == 0)
            {
                ModLog.Warning("多人回档：槽位无玩家 pid 记录、PLRPOS 与玩家子目录，跳过 pid 映射");
                return;
            }
            if (legacy)
            {
                ModLog.Warning("多人回档：旧槽位无玩家身份记录，仅按持久 ID 匹配");
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
                    ModLog.Warning("多人回档：旧槽位玩家 pid 无法对应当前会话，按原样还原。" + detail);
                    return;
                }

                throw new InvalidOperationException(I18n.F("mp.pid_map_failed", detail, DescribePlayers(session)));
            }
            if (absent.Count > 0)
            {
                ModLog.Warning($"多人回档 pid 映射：{absent.Count} 名存档玩家不在当前会话（{DescribePlayers(absent)}），保留其原目录与 PLRPOS 键");
            }

            if (!HasChanges(mapping))
            {
                ModLog.Info($"多人回档 pid 映射：{mapping.Count} 名玩家 pid 与当前会话一致，无需改写");
                return;
            }

            LogMapping(saved, mapping);
            ApplyDirectoryRenames(extractedDir, saved, mapping);
            ApplyPlrPosKeys(rulesPath, rules, mapping);
            ModLog.Info("多人回档 pid 改写完成：玩家子目录与 PLRPOS 键已对齐当前会话");
        }

        private static List<SlotMpPlayer> BuildLegacyPlayers(string rules, string extractedDir)
        {
            var players = new List<SlotMpPlayer>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string persistentId in MpRulesJson.ReadPlrPosKeys(rules))
            {
                if (string.IsNullOrEmpty(persistentId) || !seen.Add(persistentId)) continue;
                players.Add(new SlotMpPlayer(persistentId, DecodeNameFromPersistentId(persistentId), ParseSteamIdFromPersistentId(persistentId)));
            }
            foreach (string persistentId in MpRulesJson.ReadPlrCharsKeys(rules))
            {
                if (string.IsNullOrEmpty(persistentId) || !seen.Add(persistentId)) continue;
                players.Add(new SlotMpPlayer(persistentId, DecodeNameFromPersistentId(persistentId), ParseSteamIdFromPersistentId(persistentId)));
            }
            try
            {
                if (Directory.Exists(extractedDir))
                {
                    foreach (string dir in Directory.GetDirectories(extractedDir))
                    {
                        string persistentId = Path.GetFileName(dir);
                        if (string.IsNullOrEmpty(persistentId) || !seen.Add(persistentId)) continue;
                        players.Add(new SlotMpPlayer(persistentId, DecodeNameFromPersistentId(persistentId), ParseSteamIdFromPersistentId(persistentId)));
                    }
                }
            }
            catch { }
            return players;
        }

        private static List<SlotMpPlayer> DedupeSidecarPlayers(List<SlotMpPlayer> recorded)
        {
            var players = new List<SlotMpPlayer>(recorded.Count);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var record in recorded)
            {
                if (string.IsNullOrEmpty(record.PersistentId) || !seen.Add(record.PersistentId)) continue;
                players.Add(record);
            }
            return players;
        }

        private static void AddCaptured(List<SlotMpPlayer> into, HashSet<string> seen, string persistentId,
            IReadOnlyList<InteropPlayerIdentity> session)
        {
            if (string.IsNullOrEmpty(persistentId) || !seen.Add(persistentId)) return;
            string name = "";
            ulong steamId = 0UL;
            for (int i = 0; i < session.Count; i++)
            {
                if (!string.Equals(session[i].PersistentId, persistentId, StringComparison.OrdinalIgnoreCase)) continue;
                name = session[i].PlayerName ?? "";
                steamId = session[i].SteamId;
                break;
            }
            if (string.IsNullOrEmpty(name)) name = DecodeNameFromPersistentId(persistentId);
            if (steamId == 0UL) steamId = ParseSteamIdFromPersistentId(persistentId);
            if (string.IsNullOrEmpty(name) && steamId == 0UL)
            {
                ModLog.Warning($"多人存档 pid 记录：{persistentId} 无玩家名与 Steam ID，跨传输方式后无法自动识别");
            }
            into.Add(new SlotMpPlayer(persistentId, name, steamId));
        }

        private static string DecodeNameFromPersistentId(string persistentId)
        {
            const string Prefix = "NAME_";
            if (string.IsNullOrEmpty(persistentId)
                || !persistentId.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return "";
            string hex = persistentId.Substring(Prefix.Length);
            if (hex.Length == 0 || hex.Length % 2 != 0) return "";
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                if (!byte.TryParse(hex.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
                {
                    return "";
                }
            }
            return Encoding.UTF8.GetString(bytes);
        }

        private static ulong ParseSteamIdFromPersistentId(string persistentId)
        {
            const string Prefix = "STEAM_";
            if (string.IsNullOrEmpty(persistentId)
                || !persistentId.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return 0UL;
            return ulong.TryParse(persistentId.Substring(Prefix.Length), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out ulong steamId) ? steamId : 0UL;
        }

        /// <summary>按持久 ID、Steam ID、玩家名三轮无歧义匹配会话玩家，并做大小写不敏感的目标键碰撞检查。</summary>
        private static Dictionary<string, string> BuildMapping(List<SlotMpPlayer> saved,
            IReadOnlyList<InteropPlayerIdentity> session, List<string> problems, out List<SlotMpPlayer> absent)
        {
            var mapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new List<SlotMpPlayer>(saved);
            var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
                    problems.Add(I18n.F("mp.pid_map_ambiguous", who));
                }
                else if (unmatchedPresent.Count > 0)
                {
                    problems.Add(I18n.F("mp.pid_map_unresolved", who, presentText));
                }
            }

            var finals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var collisions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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

        private static void MatchTier(List<SlotMpPlayer> pending, IReadOnlyList<InteropPlayerIdentity> session,
            HashSet<string> used, Dictionary<string, string> mapping, HashSet<string> ambiguous,
            Func<SlotMpPlayer, InteropPlayerIdentity, bool> match)
        {
            if (pending.Count == 0) return;

            var candidates = new List<List<InteropPlayerIdentity>>(pending.Count);
            var claims = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
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
                    ambiguous.Add(pending[i].PersistentId);
                    continue;
                }
                accepted.Add(new KeyValuePair<SlotMpPlayer, InteropPlayerIdentity>(pending[i], matched[0]));
            }

            foreach (var pair in accepted)
            {
                mapping[pair.Key.PersistentId] = pair.Value.PersistentId;
                used.Add(pair.Value.PersistentId);
                pending.RemoveAll(record => string.Equals(record.PersistentId, pair.Key.PersistentId, StringComparison.OrdinalIgnoreCase));
            }
        }

        private static bool SamePersistentId(SlotMpPlayer record, InteropPlayerIdentity player)
            => string.Equals(record.PersistentId, player.PersistentId, StringComparison.OrdinalIgnoreCase);

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

        /// <summary>两阶段重命名玩家子目录，防止互换映射互相覆盖。</summary>
        private static void ApplyDirectoryRenames(string extractedDir, List<SlotMpPlayer> saved,
            Dictionary<string, string> mapping)
        {
            var renames = new List<KeyValuePair<string, string>>();
            var renamedFrom = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                    ModLog.Debug($"多人回档 pid 改写：{record.PersistentId} → {target} 仅大小写不同，跳过目录重命名");
                    continue;
                }
                renames.Add(new KeyValuePair<string, string>(from, target));
                renamedFrom.Add(record.PersistentId);
            }

            if (renames.Count == 0) return;

            foreach (var pair in renames)
            {
                string to = Path.Combine(extractedDir, pair.Value);
                if (!Directory.Exists(to) || renamedFrom.Contains(pair.Value)) continue;
                ModLog.Warning($"多人回档 pid 改写：mp_save 内已有旧目录 {pair.Value}，已被槽位内容取代");
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

        private static void ApplyPlrPosKeys(string rulesPath, string rules, Dictionary<string, string> mapping)
        {
            var originalPosKeys = MpRulesJson.ReadPlrPosKeys(rules);
            var originalCharKeys = MpRulesJson.ReadPlrCharsKeys(rules);
            if (originalPosKeys.Count == 0 && originalCharKeys.Count == 0)
            {
                ModLog.Warning("多人回档：mp_rules.json 没有 PLRPOS/PLRCHARS，跳过键名改写");
                return;
            }

            string rewritten = MpRulesJson.RewritePlrPosKeys(rules, mapping);
            if (string.Equals(rewritten, rules, StringComparison.Ordinal))
            {
                ModLog.Info("多人回档：PLRPOS/PLRCHARS 键与当前会话一致，未改写 mp_rules.json");
                return;
            }

            File.WriteAllText(rulesPath, rewritten, TopJson.Utf8NoBom);

            if (originalPosKeys.Count > 0)
            {
                VerifyRewrittenKeys("PLRPOS", originalPosKeys, MpRulesJson.ReadPlrPosKeys(rewritten), mapping);
            }
            if (originalCharKeys.Count > 0)
            {
                VerifyRewrittenKeys("PLRCHARS", originalCharKeys, MpRulesJson.ReadPlrCharsKeys(rewritten), mapping);
            }
        }

        private static void VerifyRewrittenKeys(string blockName, List<string> originalKeys, List<string> actualKeys, Dictionary<string, string> mapping)
        {
            var expected = new HashSet<string>(StringComparer.Ordinal);
            foreach (string key in originalKeys)
            {
                expected.Add(mapping.TryGetValue(key, out string target) ? target : key);
            }
            var actual = new HashSet<string>(actualKeys, StringComparer.Ordinal);
            if (!expected.SetEquals(actual))
            {
                throw new InvalidOperationException("多人回档已中止：mp_rules.json 的 " + blockName + " 键改写后校验失败（预期 "
                    + expected.Count + " 个键，实际 " + actual.Count + " 个）");
            }

            foreach (string key in originalKeys)
            {
                if (mapping.TryGetValue(key, out string target) && !string.Equals(key, target, StringComparison.Ordinal))
                {
                    ModLog.Info($"mp_rules {blockName} 键改写：\"{key}\" → \"{target}\"");
                }
            }
        }

        private static void BackupRules(string slotPath, string rules)
        {
            try
            {
                string backupPath = RulesBackupPath(slotPath);
                if (string.IsNullOrEmpty(backupPath)) return;
                File.WriteAllText(backupPath, rules, TopJson.Utf8NoBom);
                ModLog.Info("mp_rules 原始副本已写入：" + backupPath);
            }
            catch (Exception ex)
            {
                ModLog.Warning($"mp_rules 原始副本写入失败：{ex.Message}");
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
