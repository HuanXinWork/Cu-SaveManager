using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>读写联机存档头文件 mp_rules.json 的顶层状态字段与玩家坐标/角色键映射，保留其余字节不变。</summary>
    internal static class MpRulesJson
    {
        internal const string SaveIdField = "SAVEID";
        internal const string LevelField = "LEVEL";
        internal const string ModifierField = "MODIFIER";
        internal const string PlrPosField = "PLRPOS";
        internal const string PlrCharsField = "PLRCHARS";

        /// <summary>读取 mp_rules.json 的 LEVEL、MODIFIER 与 PLRPOS 玩家数，生成单行诊断摘要。</summary>
        internal static string Describe(string rulesPath)
        {
            try
            {
                if (string.IsNullOrEmpty(rulesPath) || !File.Exists(rulesPath)) return "mp_rules.json 不存在";
                string json = File.ReadAllText(rulesPath, Encoding.UTF8);
                string level = TryReadBool(json, LevelField, out bool levelValue)
                    ? (levelValue ? "true" : "false")
                    : "缺失";
                string modifier = TryReadInt(json, ModifierField, out int modifierValue)
                    ? modifierValue.ToString(CultureInfo.InvariantCulture)
                    : "缺失";
                return "LEVEL=" + level + " MODIFIER=" + modifier + " PLRPOS=" + ReadPlrPosKeys(json).Count + " 名玩家";
            }
            catch (Exception ex)
            {
                return "mp_rules.json 读取失败：" + ex.Message;
            }
        }

        /// <summary>读取顶层布尔字段；字段缺失或非布尔字面量时返回 false。</summary>
        internal static bool TryReadBool(string json, string field, out bool value)
            => TopJson.TryReadBool(json, field, out value);

        /// <summary>读取顶层整数字段；字段缺失或非整数值时返回 false。</summary>
        internal static bool TryReadInt(string json, string field, out int value)
            => TopJson.TryReadInt(json, field, out value);

        /// <summary>判断 Together.SaveSystem_LoadGame_MultiplayerPatch 读档时是否会按 LEVEL=true 与 MODIFIER&gt;=0 自行还原层词条。</summary>
        internal static bool WillRestoreLayerModifier(string rulesPath)
        {
            try
            {
                if (string.IsNullOrEmpty(rulesPath) || !File.Exists(rulesPath)) return false;
                string json = File.ReadAllText(rulesPath, Encoding.UTF8);
                return TryReadBool(json, LevelField, out bool level) && level
                    && TryReadInt(json, ModifierField, out int modifier) && modifier >= 0;
            }
            catch (Exception ex)
            {
                ModLog.Warning($"读取 mp_rules 层词条失败：{ex.Message}");
                return false;
            }
        }

        /// <summary>按出现顺序读取顶层 PLRPOS 对象的全部玩家持久 ID 键（已做 JSON 字符串反转义）。</summary>
        internal static List<string> ReadPlrPosKeys(string json)
            => ReadObjectKeys(json, PlrPosField);

        /// <summary>按出现顺序读取顶层 PLRCHARS 对象的全部玩家持久 ID 键（Together 5.5+）。</summary>
        internal static List<string> ReadPlrCharsKeys(string json)
            => ReadObjectKeys(json, PlrCharsField);

        /// <summary>解析顶层 PLRPOS 玩家坐标表（兼容 Item1/Item2 与 x/y 字段命名）。</summary>
        internal static Dictionary<string, Vector2> ReadPlrPos(string json)
        {
            var result = new Dictionary<string, Vector2>(StringComparer.Ordinal);
            if (!TryFindPlrPosBlock(json, out int objStart, out int objEnd)) return result;

            int depth = 0;
            int p = objStart + 1;
            while (p < objEnd)
            {
                char c = json[p];
                if (c == '"')
                {
                    if (depth == 0)
                    {
                        int pos = p;
                        if (!TopJson.TryReadQuotedAt(json, ref pos, out string id)) break;
                        int colon = TopJson.SkipWhitespace(json, pos);
                        if (colon >= objEnd || json[colon] != ':')
                        {
                            p = pos;
                            continue;
                        }
                        int valStart = TopJson.SkipWhitespace(json, colon + 1);
                        if (valStart < objEnd && json[valStart] == '{')
                        {
                            int valEnd = TopJson.FindMatchingBrace(json, valStart, '{', '}');
                            if (valEnd < 0 || valEnd > objEnd) break;
                            string inner = json.Substring(valStart, valEnd - valStart + 1);
                            bool hasX = TopJson.TryReadFloat(inner, "Item1", out float x) || TopJson.TryReadFloat(inner, "x", out x);
                            bool hasY = TopJson.TryReadFloat(inner, "Item2", out float y) || TopJson.TryReadFloat(inner, "y", out y);
                            if (!string.IsNullOrEmpty(id) && (hasX || hasY))
                            {
                                result[id] = new Vector2(x, y);
                            }
                            p = valEnd + 1;
                            continue;
                        }
                        p = valStart;
                        continue;
                    }
                    int q2 = TopJson.FindStringEnd(json, p);
                    if (q2 < 0) break;
                    p = q2 + 1;
                    continue;
                }
                if (c == '{' || c == '[') depth++;
                else if (c == '}' || c == ']') depth--;
                p++;
            }
            return result;
        }

        private static List<string> ReadObjectKeys(string json, string field)
        {
            var keys = new List<string>();
            if (!TryFindTopObjectBlock(json, field, out int objStart, out int objEnd)) return keys;
            int depth = 0;
            int p = objStart + 1;
            while (p < objEnd)
            {
                char c = json[p];
                if (c == '"')
                {
                    if (depth == 0)
                    {
                        int pos = p;
                        if (!TopJson.TryReadQuotedAt(json, ref pos, out string key)) break;
                        int after = TopJson.SkipWhitespace(json, pos);
                        if (after < objEnd && json[after] == ':')
                        {
                            keys.Add(key);
                        }
                        p = pos;
                        continue;
                    }
                    int q2 = TopJson.FindStringEnd(json, p);
                    if (q2 < 0) break;
                    p = q2 + 1;
                    continue;
                }
                if (c == '{' || c == '[') depth++;
                else if (c == '}' || c == ']') depth--;
                p++;
            }
            return keys;
        }

        /// <summary>按映射表原地替换顶层 PLRPOS 与 PLRCHARS 对象中的玩家持久 ID 键名，未命中时返回原文本。</summary>
        internal static string RewritePlrPosKeys(string json, IReadOnlyDictionary<string, string> mapping)
        {
            string step = RewriteObjectKeys(json, PlrPosField, mapping);
            return RewriteObjectKeys(step, PlrCharsField, mapping);
        }

        private static string RewriteObjectKeys(string json, string field, IReadOnlyDictionary<string, string> mapping)
        {
            if (string.IsNullOrEmpty(json) || mapping == null || mapping.Count == 0) return json;
            if (!TryFindTopObjectBlock(json, field, out int objStart, out int objEnd)) return json;

            var sb = new StringBuilder(json.Length + 64);
            bool changed = false;
            int copied = 0;
            int depth = 0;
            int p = objStart + 1;
            while (p < objEnd)
            {
                char c = json[p];
                if (c == '"')
                {
                    if (depth == 0)
                    {
                        int quoteStart = p;
                        int pos = p;
                        if (!TopJson.TryReadQuotedAt(json, ref pos, out string key)) break;
                        int after = TopJson.SkipWhitespace(json, pos);
                        if (after < objEnd && json[after] == ':'
                            && mapping.TryGetValue(key, out string newKey)
                            && !string.IsNullOrEmpty(newKey)
                            && !string.Equals(key, newKey, StringComparison.Ordinal))
                        {
                            sb.Append(json, copied, quoteStart + 1 - copied);
                            TopJson.AppendEscapedContent(sb, newKey);
                            sb.Append('"');
                            copied = pos;
                            changed = true;
                        }
                        p = pos;
                        continue;
                    }
                    int q2 = TopJson.FindStringEnd(json, p);
                    if (q2 < 0) break;
                    p = q2 + 1;
                    continue;
                }
                if (c == '{' || c == '[') depth++;
                else if (c == '}' || c == ']') depth--;
                p++;
            }

            if (!changed) return json;
            sb.Append(json, copied, json.Length - copied);
            return sb.ToString();
        }

        /// <summary>定位顶层 PLRPOS 值对象的开闭花括号下标；缺失或结构不完整时返回 false。</summary>
        internal static bool TryFindPlrPosBlock(string json, out int openBrace, out int closeBrace)
            => TryFindTopObjectBlock(json, PlrPosField, out openBrace, out closeBrace);

        private static bool TryFindTopObjectBlock(string json, string field, out int openBrace, out int closeBrace)
        {
            openBrace = -1;
            closeBrace = -1;
            int start = TopJson.FindValueStart(json, field);
            if (start < 0 || start >= json.Length || json[start] != '{') return false;
            int end = TopJson.FindMatchingBrace(json, start, '{', '}');
            if (end < 0) return false;
            openBrace = start;
            closeBrace = end;
            return true;
        }

        /// <summary>从 mp_rules.json 文本提取 RunId：优先读顶层 SAVEID，为 0 时回退首个 STEAM_ 持久 ID 的 FNV-1a 哈希。</summary>
        internal static int ParseRunId(string json)
        {
            if (string.IsNullOrEmpty(json)) return 0;
            if (TryReadInt(json, SaveIdField, out int sid) && sid != 0) return sid;
            foreach (string key in ReadPlrPosKeys(json))
            {
                if (key.StartsWith("STEAM_", StringComparison.OrdinalIgnoreCase))
                {
                    uint h = 2166136261u;
                    for (int i = 0; i < key.Length; i++)
                    {
                        h ^= key[i];
                        h *= 16777619u;
                    }
                    int v = (int)h;
                    return v == 0 ? 1 : v;
                }
            }
            return 0;
        }

        internal static int ReadRunIdFromFile(string rulesPath)
        {
            try
            {
                return !string.IsNullOrEmpty(rulesPath) && File.Exists(rulesPath)
                    ? ParseRunId(File.ReadAllText(rulesPath, Encoding.UTF8))
                    : 0;
            }
            catch
            {
                return 0;
            }
        }
    }

    /// <summary>轻量状态机式顶层 JSON 扫描与原语解析工具，严格跳过字符串字面量与嵌套容器。</summary>
    internal static class TopJson
    {
        internal static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        /// <summary>查找根对象指定顶层键的值起始下标；未命中或结构非法时返回 -1。</summary>
        internal static int FindValueStart(string json, string field)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(field)) return -1;
            int p = SkipWhitespace(json, 0);
            if (p >= json.Length || json[p] != '{') return -1;

            int depth = 1;
            bool atMemberKey = true;
            p++;
            while (p < json.Length)
            {
                char c = json[p];
                if (c == '"')
                {
                    if (depth == 1 && atMemberKey)
                    {
                        if (!TryReadQuotedAt(json, ref p, out string key)) return -1;
                        int colon = SkipWhitespace(json, p);
                        if (colon >= json.Length || json[colon] != ':') return -1;
                        if (string.Equals(key, field, StringComparison.Ordinal))
                        {
                            int valStart = SkipWhitespace(json, colon + 1);
                            return valStart < json.Length ? valStart : -1;
                        }
                        p = colon + 1;
                        atMemberKey = false;
                        continue;
                    }
                    int q2 = FindStringEnd(json, p);
                    if (q2 < 0) return -1;
                    p = q2 + 1;
                    continue;
                }
                if (c == '{' || c == '[')
                {
                    depth++;
                    p++;
                    continue;
                }
                if (c == '}' || c == ']')
                {
                    depth--;
                    if (depth <= 0) return -1;
                    p++;
                    continue;
                }
                if (depth == 1)
                {
                    if (c == ',') atMemberKey = true;
                    else if (c == ':') atMemberKey = false;
                }
                p++;
            }
            return -1;
        }

        internal static bool HasKey(string json, string field)
            => FindValueStart(json, field) >= 0;

        internal static bool TryReadString(string json, string field, out string value)
        {
            value = null;
            int p = FindValueStart(json, field);
            if (p < 0 || json[p] != '"') return false;
            return TryReadQuotedAt(json, ref p, out value);
        }

        internal static bool TryReadBool(string json, string field, out bool value)
        {
            value = false;
            int p = FindValueStart(json, field);
            if (p < 0) return false;
            if (p + 4 <= json.Length
                && string.CompareOrdinal(json, p, "true", 0, 4) == 0
                && IsValueTerminator(json, p + 4))
            {
                value = true;
                return true;
            }
            if (p + 5 <= json.Length
                && string.CompareOrdinal(json, p, "false", 0, 5) == 0
                && IsValueTerminator(json, p + 5))
            {
                value = false;
                return true;
            }
            return false;
        }

        internal static bool TryReadInt(string json, string field, out int value)
        {
            value = 0;
            int p = FindValueStart(json, field);
            if (p < 0 || !TryScanNumber(json, p, out int end, out bool hasFloatPart)) return false;
            string token = json.Substring(p, end - p);
            if (!hasFloatPart)
            {
                return int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
            }
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double dv)
                && !double.IsNaN(dv) && !double.IsInfinity(dv)
                && dv >= int.MinValue && dv <= int.MaxValue
                && Math.Floor(dv) == dv)
            {
                value = (int)dv;
                return true;
            }
            return false;
        }

        internal static bool TryReadLong(string json, string field, out long value)
        {
            value = 0L;
            int p = FindValueStart(json, field);
            if (p < 0 || !TryScanNumber(json, p, out int end, out bool hasFloatPart)) return false;
            string token = json.Substring(p, end - p);
            if (!hasFloatPart)
            {
                return long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
            }
            if (decimal.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal dm)
                && dm >= long.MinValue && dm <= long.MaxValue
                && decimal.Truncate(dm) == dm)
            {
                value = (long)dm;
                return true;
            }
            return false;
        }

        internal static bool TryReadFloat(string json, string field, out float value)
        {
            value = 0f;
            int p = FindValueStart(json, field);
            if (p < 0 || !TryScanNumber(json, p, out int end, out _)) return false;
            return float.TryParse(json.Substring(p, end - p), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        internal static string ReplaceInt(string json, string field, int newValue)
        {
            int p = FindValueStart(json, field);
            if (p < 0 || !TryScanNumber(json, p, out int end, out _)) return json;
            return json.Substring(0, p) + newValue.ToString(CultureInfo.InvariantCulture) + json.Substring(end);
        }

        internal static List<string> ReadStringArray(string json, string field)
        {
            var result = new List<string>();
            int p = FindValueStart(json, field);
            if (p < 0 || p >= json.Length || json[p] != '[') return result;
            int end = FindMatchingBrace(json, p, '[', ']');
            if (end < 0) end = json.Length;
            int depth = 0;
            p++;
            while (p < end)
            {
                char c = json[p];
                if (c == '"')
                {
                    if (depth == 0)
                    {
                        if (!TryReadQuotedAt(json, ref p, out string item)) break;
                        result.Add(item);
                        continue;
                    }
                    int q2 = FindStringEnd(json, p);
                    if (q2 < 0) break;
                    p = q2 + 1;
                    continue;
                }
                if (c == '{' || c == '[') depth++;
                else if (c == '}' || c == ']')
                {
                    if (depth == 0) break;
                    depth--;
                }
                p++;
            }
            return result;
        }

        internal static bool TryScanNumber(string text, int start, out int end, out bool hasFloatPart)
        {
            end = start;
            hasFloatPart = false;
            if (start < 0 || start >= text.Length) return false;
            int p = start;
            if (text[p] == '-' || text[p] == '+') p++;
            int intStart = p;
            while (p < text.Length && char.IsDigit(text[p])) p++;
            if (p == intStart) return false;

            if (p < text.Length && text[p] == '.')
            {
                hasFloatPart = true;
                p++;
                int fracStart = p;
                while (p < text.Length && char.IsDigit(text[p])) p++;
                if (p == fracStart) return false;
            }

            if (p < text.Length && (text[p] == 'e' || text[p] == 'E'))
            {
                hasFloatPart = true;
                p++;
                if (p < text.Length && (text[p] == '+' || text[p] == '-')) p++;
                int expStart = p;
                while (p < text.Length && char.IsDigit(text[p])) p++;
                if (p == expStart) return false;
            }

            if (!IsValueTerminator(text, p)) return false;
            end = p;
            return true;
        }

        internal static bool IsValueTerminator(string text, int index)
        {
            if (index >= text.Length) return true;
            char c = text[index];
            return char.IsWhiteSpace(c) || c == ',' || c == '}' || c == ']';
        }

        internal static int SkipWhitespace(string text, int position)
        {
            int p = position;
            while (p < text.Length && char.IsWhiteSpace(text[p])) p++;
            return p;
        }

        internal static int FindStringEnd(string text, int quoteIndex)
        {
            int p = quoteIndex + 1;
            while (p < text.Length)
            {
                char c = text[p];
                if (c == '\\')
                {
                    p += 2;
                    continue;
                }
                if (c == '"') return p;
                p++;
            }
            return -1;
        }

        internal static int FindMatchingBrace(string text, int openIndex, char openChar, char closeChar)
        {
            int depth = 0;
            int p = openIndex;
            while (p < text.Length)
            {
                char c = text[p];
                if (c == '"')
                {
                    int q2 = FindStringEnd(text, p);
                    if (q2 < 0) return -1;
                    p = q2 + 1;
                    continue;
                }
                if (c == openChar) depth++;
                else if (c == closeChar)
                {
                    depth--;
                    if (depth == 0) return p;
                }
                p++;
            }
            return -1;
        }

        internal static bool TryReadQuotedAt(string text, ref int position, out string value)
        {
            value = null;
            if (position < 0 || position >= text.Length || text[position] != '"') return false;
            int p = position + 1;
            int plainEnd = p;
            while (plainEnd < text.Length && text[plainEnd] != '"' && text[plainEnd] != '\\') plainEnd++;
            if (plainEnd < text.Length && text[plainEnd] == '"')
            {
                value = text.Substring(p, plainEnd - p);
                position = plainEnd + 1;
                return true;
            }

            var sb = new StringBuilder(plainEnd - p + 16);
            if (plainEnd > p) sb.Append(text, p, plainEnd - p);
            p = plainEnd;
            while (p < text.Length)
            {
                char c = text[p++];
                if (c == '"')
                {
                    position = p;
                    value = sb.ToString();
                    return true;
                }
                if (c == '\\')
                {
                    if (p >= text.Length) return false;
                    char n = text[p++];
                    switch (n)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (p + 4 <= text.Length
                                && int.TryParse(text.Substring(p, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code))
                            {
                                sb.Append((char)code);
                                p += 4;
                            }
                            else
                            {
                                return false;
                            }
                            break;
                        default: sb.Append(n); break;
                    }
                    continue;
                }
                sb.Append(c);
            }
            return false;
        }

        internal static string EscapeString(string raw)
        {
            if (raw == null) return "\"\"";
            var sb = new StringBuilder(raw.Length + 2);
            sb.Append('"');
            AppendEscapedContent(sb, raw);
            sb.Append('"');
            return sb.ToString();
        }

        internal static void AppendEscapedContent(StringBuilder sb, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
        }
    }
}
