using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>槽位记录的玩家身份：持久 ID、玩家名与 Steam ID。</summary>
    /// <remarks>
    /// 联机模组按持久 ID 建 mp_save/&lt;persistentId&gt;/save.sv 目录与 mp_rules.json 里 PLRPOS 的键，
    /// 而持久 ID 随传输方式（Steam / 直连）与玩家名变化。回档时靠玩家名与 Steam ID 把旧持久 ID 认回同一个人。
    /// </remarks>
    internal readonly struct SlotMpPlayer
    {
        /// <summary>用身份数据构造记录；名称或 Steam ID 未知时分别传空字符串与 0。</summary>
        internal SlotMpPlayer(string persistentId, string name, ulong steamId)
        {
            PersistentId = persistentId ?? "";
            Name = name ?? "";
            SteamId = steamId;
        }

        /// <summary>保存时的持久 ID（mp_save 子目录名 / PLRPOS 键）。</summary>
        internal string PersistentId { get; }

        /// <summary>保存时的玩家名；未知时为空字符串。</summary>
        internal string Name { get; }

        /// <summary>保存时的 Steam ID；未知（直连或旧槽位）时为 0。</summary>
        internal ulong SteamId { get; }
    }

    /// <summary>
    /// 槽位伴生元数据，落到 &lt;slot&gt;.sv.json：
    /// { "nickname":"…", "pinned":false, "isAuto":true, "biome":3, "runId":12345, "lastPlayedAtUnixMs":… }
    /// 缺失字段走默认值；序列化与解析手写完成。
    /// runId 取自 save.sv 内的 cId 字段；旧版字段名 "seed" 仍可读取。
    /// 多人槽位额外记录存档内各玩家的持久 ID / 玩家名 / Steam ID（mpPlayerIds / mpPlayerNames / mpPlayerSteamIds，按索引对齐）。
    /// </summary>
    internal sealed class SlotSidecar
    {
        internal string Nickname { get; set; } = "";
        internal bool Pinned { get; set; }
        internal bool IsAuto { get; set; }
        internal int Biome { get; set; }
        internal int RunId { get; set; }
        /// <summary>系统时间戳（Unix 毫秒），代表"最后游玩时间"= 这个槽位被生成的时刻。</summary>
        internal long LastPlayedAtUnixMs { get; set; }

        // —— QoL mod 联动字段：保存时抓 body.transform.position 与 QoL 的 SeedManager 静态字段；
        //    回档时通过 QolBridge 反射写回 QoL.SaveSystemPatcher.PendingLoadPosition / SeedManager.IsSeeded / CurrentSeed。
        //    QoL 不在场时这些字段也照写，下次装上 QoL 即可生效。
        internal float PlayerX { get; set; }
        internal float PlayerY { get; set; }
        internal int QolSeed { get; set; }
        internal string QolSeedInput { get; set; } = "";
        internal bool HasPlayerPos { get; set; }

        // —— 固定世界 / 位置模式 ——
        //    WorldEngine: "qol" | "self"，标识该存档世界由哪套引擎生成，两套不可互转。
        //    MpWorldEngine: "krok" | "self"，多人存档的世界/位置由 Together 或本 mod 种子注入。
        //    PosMode: "lastPos"（回保存时坐标）| "fixedPos"（回 FixedX/FixedY）。
        internal string WorldEngine { get; set; } = "";
        internal string MpWorldEngine { get; set; } = "";
        internal string PosMode { get; set; } = "";
        internal float FixedX { get; set; }
        internal float FixedY { get; set; }
        internal bool IsMultiplayer { get; set; }

        /// <summary>本层激活的 LayerModifier 索引；-2=未记录（旧档/游戏未加载，回档时交给游戏自滚），-1=本层无词条，>=0=对应 modifierIndex。读档时由 ApplyLayerModifiersRestorePatch 还原。</summary>
        internal int ActiveLayerModifierIndex { get; set; } = -2;

        /// <summary>保存时存档内各玩家的身份（持久 ID / 玩家名 / Steam ID）；非多人槽或旧槽位为空列表。</summary>
        /// <remarks>回档时由 <see cref="MpPersistentIdMapper"/> 用来把旧持久 ID 映射到当前会话的持久 ID。</remarks>
        internal List<SlotMpPlayer> MpPlayers { get; set; } = new List<SlotMpPlayer>();

        /// <summary>Debug 日志用的紧凑描述。</summary>
        internal string Describe()
        {
            string pos = HasPlayerPos ? "(" + PlayerX.ToString("0.0") + "," + PlayerY.ToString("0.0") + ")" : "none";
            return "runId=" + RunId + " biome=" + Biome
                + " engine='" + WorldEngine + "' mpEngine='" + MpWorldEngine + "'"
                + " seed=" + QolSeed + " input='" + QolSeedInput + "'"
                + " pos=" + pos + " posMode='" + PosMode + "'"
                + " auto=" + IsAuto + " pinned=" + Pinned + " mp=" + IsMultiplayer
                + " layerMod=" + ActiveLayerModifierIndex + " players=" + (MpPlayers?.Count ?? 0);
        }

        /// <summary>
        /// 从指定路径加载.json元数据；若文件不存在或解析失败，返回空实例。
        /// </summary>
        /// <param name="slotPath">上游传入的存档路径</param>
        /// <returns></returns>
        internal static SlotSidecar LoadOrEmpty(string slotPath)
        {
            try
            {
                var p = SidecarPath(slotPath);
                if (!File.Exists(p)) return new SlotSidecar();
                return Parse(File.ReadAllText(p, Encoding.UTF8));
            }
            catch
            {
                return new SlotSidecar();
            }
        }

        internal void Save(string slotPath)
        {
            try
            {
                File.WriteAllText(SidecarPath(slotPath), Serialize(), Encoding.UTF8);
            }
            catch
            {
                // 保存元数据失败不致命，主存档已经在磁盘上。
            }
        }

        internal static void DeleteFor(string slotPath)
        {
            try
            {
                var p = SidecarPath(slotPath);
                if (File.Exists(p)) File.Delete(p);
            }
            catch { }
        }

        internal static string SidecarPath(string slotPath) => slotPath + ".json";

        // —— 极简 JSON 序列化 —— //

        private string Serialize()
        {
            var sb = new StringBuilder(128);
            sb.Append('{');
            sb.Append("\"nickname\":").Append(EscapeJsonString(Nickname)).Append(',');
            sb.Append("\"pinned\":").Append(Pinned ? "true" : "false").Append(',');
            sb.Append("\"isAuto\":").Append(IsAuto ? "true" : "false").Append(',');
            sb.Append("\"biome\":").Append(Biome.ToString()).Append(',');
            sb.Append("\"runId\":").Append(RunId.ToString()).Append(',');
            sb.Append("\"lastPlayedAtUnixMs\":").Append(LastPlayedAtUnixMs.ToString()).Append(',');
            sb.Append("\"hasPlayerPos\":").Append(HasPlayerPos ? "true" : "false").Append(',');
            sb.Append("\"playerX\":").Append(PlayerX.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"playerY\":").Append(PlayerY.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"qolSeed\":").Append(QolSeed.ToString()).Append(',');
            sb.Append("\"qolSeedInput\":").Append(EscapeJsonString(QolSeedInput)).Append(',');
            sb.Append("\"worldEngine\":").Append(EscapeJsonString(WorldEngine)).Append(',');
            sb.Append("\"mpWorldEngine\":").Append(EscapeJsonString(MpWorldEngine)).Append(',');
            sb.Append("\"posMode\":").Append(EscapeJsonString(PosMode)).Append(',');
            sb.Append("\"fixedX\":").Append(FixedX.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"fixedY\":").Append(FixedY.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"isMultiplayer\":").Append(IsMultiplayer ? "true" : "false").Append(',');
            sb.Append("\"activeLayerModifierIndex\":").Append(ActiveLayerModifierIndex.ToString()).Append(',');
            sb.Append("\"mpPlayerIds\":").Append(StringArray(MpPlayers, p => p.PersistentId)).Append(',');
            sb.Append("\"mpPlayerNames\":").Append(StringArray(MpPlayers, p => p.Name)).Append(',');
            sb.Append("\"mpPlayerSteamIds\":").Append(StringArray(MpPlayers, p => p.SteamId == 0UL
                ? ""
                : p.SteamId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            sb.Append('}');
            return sb.ToString();
        }

        private static SlotSidecar Parse(string text)
        {
            var s = new SlotSidecar();
            if (string.IsNullOrEmpty(text)) return s;
            s.Nickname = ReadString(text, "nickname") ?? "";
            s.Pinned = ReadBool(text, "pinned");
            s.IsAuto = ReadBool(text, "isAuto");
            s.Biome = ReadInt(text, "biome");
            // runId 是新字段；老 sidecar 用的是 seed，向后兼容读取
            int rid = ReadInt(text, "runId");
            if (rid == 0) rid = ReadInt(text, "seed");
            s.RunId = rid;
            s.LastPlayedAtUnixMs = ReadLong(text, "lastPlayedAtUnixMs");
            s.HasPlayerPos = ReadBool(text, "hasPlayerPos");
            s.PlayerX = ReadFloat(text, "playerX");
            s.PlayerY = ReadFloat(text, "playerY");
            s.QolSeed = ReadInt(text, "qolSeed");
            s.QolSeedInput = ReadString(text, "qolSeedInput") ?? "";
            s.WorldEngine = ReadString(text, "worldEngine") ?? "";
            s.MpWorldEngine = ReadString(text, "mpWorldEngine") ?? "";
            s.PosMode = ReadString(text, "posMode") ?? "";
            s.FixedX = ReadFloat(text, "fixedX");
            s.FixedY = ReadFloat(text, "fixedY");
            s.IsMultiplayer = ReadBool(text, "isMultiplayer");
            int idx = ReadInt(text, "activeLayerModifierIndex");
            // 老 sidecar 没这字段：ReadInt 返 0；用 FindKey 区分"无字段"（-2 未记录）vs"显式值"
            s.ActiveLayerModifierIndex = FindKey(text, "activeLayerModifierIndex") < 0 ? -2 : idx;
            s.MpPlayers = ReadMpPlayers(text);
            return s;
        }

        /// <summary>按 mpPlayerIds / mpPlayerNames / mpPlayerSteamIds 三个平行数组重建玩家身份列表；缺字段或对不齐时按可用部分尽力拼装。</summary>
        private static List<SlotMpPlayer> ReadMpPlayers(string text)
        {
            var result = new List<SlotMpPlayer>();
            var ids = ReadStringArray(text, "mpPlayerIds");
            if (ids.Count == 0) return result;
            var names = ReadStringArray(text, "mpPlayerNames");
            var steamIds = ReadStringArray(text, "mpPlayerSteamIds");
            for (int i = 0; i < ids.Count; i++)
            {
                if (string.IsNullOrEmpty(ids[i])) continue;
                ulong steamId = 0UL;
                if (i < steamIds.Count) ulong.TryParse(steamIds[i], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out steamId);
                result.Add(new SlotMpPlayer(ids[i], i < names.Count ? names[i] : "", steamId));
            }
            return result;
        }

        /// <summary>把玩家身份列表按字段序列化成 JSON 字符串数组。</summary>
        private static string StringArray(List<SlotMpPlayer> players, Func<SlotMpPlayer, string> selector)
        {
            if (players == null || players.Count == 0) return "[]";
            var sb = new StringBuilder(players.Count * 24 + 2);
            sb.Append('[');
            for (int i = 0; i < players.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(EscapeJsonString(selector(players[i])));
            }
            sb.Append(']');
            return sb.ToString();
        }

        /// <summary>读取 JSON 字符串数组；字段缺失、不是数组或解析中断时返回已读到的部分。</summary>
        private static List<string> ReadStringArray(string text, string key)
        {
            var result = new List<string>();
            int p = FindKey(text, key);
            if (p < 0 || p >= text.Length || text[p] != '[') return result;
            p++;
            while (p < text.Length)
            {
                char c = text[p];
                if (c == ']') break;
                if (c == '"')
                {
                    string item = ReadQuotedAt(text, ref p);
                    if (item == null) break;
                    result.Add(item);
                    continue;
                }
                p++;
            }
            return result;
        }

        private static string EscapeJsonString(string raw)
        {
            if (raw == null) return "\"\"";
            var sb = new StringBuilder(raw.Length + 2);
            sb.Append('"');
            foreach (var c in raw)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append($"\\u{(int)c:X4}");
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        private static int FindKey(string text, string key)
        {
            string needle = "\"" + key + "\"";
            int idx = text.IndexOf(needle, StringComparison.Ordinal);
            if (idx < 0) return -1;
            int colon = text.IndexOf(':', idx + needle.Length);
            if (colon < 0) return -1;
            // 跳过空白
            int p = colon + 1;
            while (p < text.Length && char.IsWhiteSpace(text[p])) p++;
            return p;
        }

        private static string ReadString(string text, string key)
        {
            int p = FindKey(text, key);
            if (p < 0) return null;
            return ReadQuotedAt(text, ref p);
        }

        /// <summary>从 <paramref name="position"/> 处的开引号读一个 JSON 字符串，读完把位置推到闭合引号之后；起始不是引号时返回 null。</summary>
        private static string ReadQuotedAt(string text, ref int position)
        {
            if (position < 0 || position >= text.Length || text[position] != '"') return null;
            var sb = new StringBuilder();
            int p = position + 1;
            while (p < text.Length)
            {
                char c = text[p++];
                if (c == '"')
                {
                    position = p;
                    return sb.ToString();
                }
                if (c == '\\' && p < text.Length)
                {
                    char n = text[p++];
                    switch (n)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (p + 4 <= text.Length
                                && int.TryParse(text.Substring(p, 4), System.Globalization.NumberStyles.HexNumber,
                                    System.Globalization.CultureInfo.InvariantCulture, out int code))
                            {
                                sb.Append((char)code);
                                p += 4;
                            }
                            break;
                        default: sb.Append(n); break;
                    }
                    continue;
                }
                sb.Append(c);
            }
            position = p;
            return sb.ToString();
        }

        private static bool ReadBool(string text, string key)
        {
            int p = FindKey(text, key);
            if (p < 0) return false;
            return p + 4 <= text.Length && text.Substring(p, 4) == "true";
        }

        private static int ReadInt(string text, string key)
        {
            int p = FindKey(text, key);
            if (p < 0) return 0;
            int start = p;
            if (start < text.Length && text[start] == '-') start++;
            int end = start;
            while (end < text.Length && char.IsDigit(text[end])) end++;
            if (end == start) return 0;
            int.TryParse(text.Substring(p, end - p), out int v);
            return v;
        }

        private static long ReadLong(string text, string key)
        {
            int p = FindKey(text, key);
            if (p < 0) return 0L;
            int start = p;
            if (start < text.Length && text[start] == '-') start++;
            int end = start;
            while (end < text.Length && char.IsDigit(text[end])) end++;
            if (end == start) return 0L;
            long.TryParse(text.Substring(p, end - p), out long v);
            return v;
        }

        private static float ReadFloat(string text, string key)
        {
            int p = FindKey(text, key);
            if (p < 0) return 0f;
            int start = p;
            if (start < text.Length && text[start] == '-') start++;
            int end = start;
            while (end < text.Length && (char.IsDigit(text[end]) || text[end] == '.' || text[end] == 'e' || text[end] == 'E' || text[end] == '+' || text[end] == '-'))
                end++;
            if (end == start) return 0f;
            float.TryParse(text.Substring(p, end - p),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float v);
            return v;
        }
    }
}
