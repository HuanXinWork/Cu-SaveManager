using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>
    /// mp_rules.json 的文本级诊断与定点改写。
    /// 该文件由联机模组用 JSON 序列化写出（顶层键 hello / MPVERSION / GAMEVERSION / SAVEID / LEVEL / RULES / MODIFIER / PLRPOS），
    /// 本类不做整份反序列化，只读取 LEVEL / MODIFIER / PLRPOS 键名，并把 PLRPOS 的键名按映射改写，其余字段逐字节保留。
    /// </summary>
    internal static class MpRulesJson
    {
        /// <summary>层内状态标志字段：true=存档于层中，false=存档于层底（读档后进下一层）。</summary>
        internal const string LevelField = "LEVEL";

        /// <summary>本层 LayerModifier 索引字段；-1 表示本层无词条。</summary>
        internal const string ModifierField = "MODIFIER";

        /// <summary>玩家坐标表字段：键为玩家持久 ID，值为 (Item1, Item2) 坐标对。</summary>
        internal const string PlrPosField = "PLRPOS";

        private static readonly string PlrPosKey = "\"" + PlrPosField + "\"";

        /// <summary>读取指定 mp_rules.json 的 LEVEL / MODIFIER / PLRPOS 条目数，拼成一行诊断文本；文件不存在或读取失败时返回可读说明。</summary>
        /// <param name="rulesPath">mp_rules.json 全路径</param>
        internal static string Describe(string rulesPath)
        {
            try
            {
                if (string.IsNullOrEmpty(rulesPath) || !File.Exists(rulesPath)) return "mp_rules.json 不存在";
                string json = File.ReadAllText(rulesPath);
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

        /// <summary>读取顶层布尔字段；字段缺失或不是布尔字面量时返回 false。</summary>
        /// <param name="json">mp_rules.json 全文</param>
        /// <param name="field">字段名（不含引号）</param>
        /// <param name="value">解析结果</param>
        internal static bool TryReadBool(string json, string field, out bool value)
        {
            value = false;
            int p = FindValueStart(json, field);
            if (p < 0) return false;
            if (p + 4 <= json.Length && string.CompareOrdinal(json, p, "true", 0, 4) == 0)
            {
                value = true;
                return true;
            }
            return p + 5 <= json.Length && string.CompareOrdinal(json, p, "false", 0, 5) == 0;
        }

        /// <summary>读取顶层整数字段；字段缺失或不是整数字面量时返回 false。</summary>
        /// <param name="json">mp_rules.json 全文</param>
        /// <param name="field">字段名（不含引号）</param>
        /// <param name="value">解析结果</param>
        internal static bool TryReadInt(string json, string field, out int value)
        {
            value = 0;
            int p = FindValueStart(json, field);
            if (p < 0) return false;
            int start = p;
            if (p < json.Length && (json[p] == '-' || json[p] == '+')) p++;
            int digits = p;
            while (p < json.Length && char.IsDigit(json[p])) p++;
            if (p == digits) return false;
            return int.TryParse(json.Substring(start, p - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>判断联机模组读档时是否会自己还原层词条：mp_rules.json 里 LEVEL=true 且 MODIFIER&gt;=0。</summary>
        /// <remarks>对应 Together.Patches.SavesystemPatch.LoadGame_MultiplayerPatch 里按 MIDLEVEL 与 MODIFIER
        /// 调 LayerModifier.availableModifiers[MODIFIER] 的分支；为真时本 mod 无需再按 sidecar 还原一次。
        /// 文件缺失或读取失败时返回 false。</remarks>
        /// <param name="rulesPath">mp_rules.json 全路径</param>
        internal static bool WillRestoreLayerModifier(string rulesPath)
        {
            try
            {
                if (string.IsNullOrEmpty(rulesPath) || !File.Exists(rulesPath)) return false;
                string json = File.ReadAllText(rulesPath);
                return TryReadBool(json, LevelField, out bool level) && level
                    && TryReadInt(json, ModifierField, out int modifier) && modifier >= 0;
            }
            catch (Exception ex)
            {
                ModLog.Warning($"读取 mp_rules 层词条失败：{ex.Message}");
                return false;
            }
        }

        /// <summary>读取 PLRPOS 对象里的键（即各玩家的持久 ID），按出现顺序返回；对象不存在时返回空列表。</summary>
        /// <remarks>持久 ID 形如 STEAM_&lt;数字&gt; 或 NAME_&lt;十六进制&gt;，不含需要转义的字符，因此按键名原文返回。</remarks>
        /// <param name="json">mp_rules.json 全文</param>
        internal static List<string> ReadPlrPosKeys(string json)
        {
            var keys = new List<string>();
            if (!TryFindPlrPosBlock(json, out int objStart, out int objEnd)) return keys;
            int depth = 0;
            int p = objStart + 1;
            while (p < objEnd)
            {
                char c = json[p];
                if (c == '"')
                {
                    int q2 = FindStringEnd(json, p);
                    if (q2 < 0) break;
                    if (depth == 0)
                    {
                        int after = SkipWhitespace(json, q2 + 1);
                        if (after < json.Length && json[after] == ':')
                        {
                            keys.Add(json.Substring(p + 1, q2 - p - 1));
                        }
                    }
                    p = q2 + 1;
                    continue;
                }
                if (c == '{' || c == '[') depth++;
                else if (c == '}' || c == ']') depth--;
                p++;
            }
            return keys;
        }

        /// <summary>把 PLRPOS 对象的键按映射改写；没有任何键命中或找不到 PLRPOS 时原样返回传入文本。</summary>
        /// <remarks>只替换键名，坐标与其余顶层字段保持原字节；键名按 JSON 字符串转义写出，保证结果仍是合法 JSON。</remarks>
        /// <param name="json">mp_rules.json 全文</param>
        /// <param name="mapping">旧持久 ID → 新持久 ID；只命中存在的键</param>
        internal static string RewritePlrPosKeys(string json, IReadOnlyDictionary<string, string> mapping)
        {
            if (string.IsNullOrEmpty(json) || mapping == null || mapping.Count == 0) return json;
            if (!TryFindPlrPosBlock(json, out int objStart, out int objEnd)) return json;

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
                    int q2 = FindStringEnd(json, p);
                    if (q2 < 0) break;
                    if (depth == 0)
                    {
                        int after = SkipWhitespace(json, q2 + 1);
                        if (after < json.Length && json[after] == ':')
                        {
                            string key = json.Substring(p + 1, q2 - p - 1);
                            if (mapping.TryGetValue(key, out string newKey)
                                && !string.IsNullOrEmpty(newKey)
                                && !string.Equals(key, newKey, StringComparison.Ordinal))
                            {
                                sb.Append(json, copied, p + 1 - copied);
                                AppendJsonStringContent(sb, newKey);
                                sb.Append('"');
                                copied = q2 + 1;
                                changed = true;
                            }
                        }
                    }
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

        /// <summary>定位 PLRPOS 的值对象：返回开括号与匹配闭括号的下标；字段缺失或结构不完整时返回 false。</summary>
        /// <param name="json">mp_rules.json 全文</param>
        /// <param name="openBrace">PLRPOS 对象开括号下标</param>
        /// <param name="closeBrace">PLRPOS 对象匹配闭括号下标</param>
        internal static bool TryFindPlrPosBlock(string json, out int openBrace, out int closeBrace)
        {
            openBrace = -1;
            closeBrace = -1;
            if (string.IsNullOrEmpty(json)) return false;
            int keyIdx = json.IndexOf(PlrPosKey, StringComparison.Ordinal);
            if (keyIdx < 0) return false;
            int colon = json.IndexOf(':', keyIdx + PlrPosKey.Length);
            if (colon < 0) return false;
            int start = SkipWhitespace(json, colon + 1);
            if (start >= json.Length || json[start] != '{') return false;
            int end = FindMatchingBrace(json, start);
            if (end < 0) return false;
            openBrace = start;
            closeBrace = end;
            return true;
        }

        /// <summary>跳过空白，返回第一个非空白字符下标；越界时返回字符串长度。</summary>
        private static int SkipWhitespace(string text, int position)
        {
            int p = position;
            while (p < text.Length && char.IsWhiteSpace(text[p])) p++;
            return p;
        }

        /// <summary>从开引号出发找到闭合引号下标，跳过转义字符；找不到时返回 -1。</summary>
        private static int FindStringEnd(string text, int quoteIndex)
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

        /// <summary>从开括号出发找到匹配的闭括号下标，跳过字符串内容；找不到时返回 -1。</summary>
        private static int FindMatchingBrace(string text, int openIndex)
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
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) return p;
                }
                p++;
            }
            return -1;
        }

        /// <summary>按 JSON 字符串规则转义并写出内容（不含首尾引号）。</summary>
        private static void AppendJsonStringContent(StringBuilder sb, string value)
        {
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

        /// <summary>找到字段冒号后的值起始下标；字段不存在时返回 -1。</summary>
        private static int FindValueStart(string json, string field)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(field)) return -1;
            string key = "\"" + field + "\"";
            int idx = json.IndexOf(key, StringComparison.Ordinal);
            if (idx < 0) return -1;
            int colon = json.IndexOf(':', idx + key.Length);
            if (colon < 0) return -1;
            return SkipWhitespace(json, colon + 1);
        }
    }
}
