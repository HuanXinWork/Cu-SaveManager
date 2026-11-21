using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>轻量状态机式顶层 JSON 扫描与原语解析工具，用于在不改动其余字节的前提下快速读写大体积存档头字段。</summary>
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
