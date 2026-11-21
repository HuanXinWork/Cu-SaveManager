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

        private delegate void ObjectMemberVisitor(string key, int keyQuoteStart, int keyEnd, int valStart, int valEnd);

        /// <summary>读取 mp_rules.json 的 LEVEL、MODIFIER 与 PLRPOS 玩家数，生成单行诊断摘要。</summary>
        internal static string Describe(string rulesPath)
        {
            try
            {
                if (string.IsNullOrEmpty(rulesPath) || !File.Exists(rulesPath)) return "mp_rules.json 不存在";
                string json = File.ReadAllText(rulesPath, Encoding.UTF8);
                string level = TopJson.TryReadBool(json, LevelField, out bool levelValue)
                    ? (levelValue ? "true" : "false")
                    : "缺失";
                string modifier = TopJson.TryReadInt(json, ModifierField, out int modifierValue)
                    ? modifierValue.ToString(CultureInfo.InvariantCulture)
                    : "缺失";
                return "LEVEL=" + level + " MODIFIER=" + modifier + " PLRPOS=" + ReadPlrPosKeys(json).Count + " 名玩家";
            }
            catch (Exception ex)
            {
                return "mp_rules.json 读取失败：" + ex.Message;
            }
        }

        internal static bool TryReadBool(string json, string field, out bool value)
            => TopJson.TryReadBool(json, field, out value);

        /// <summary>判断 Together.SaveSystem_LoadGame_MultiplayerPatch 读档时是否会按 LEVEL=true 与 MODIFIER&gt;=0 自行还原层词条。</summary>
        internal static bool WillRestoreLayerModifier(string rulesPath)
        {
            try
            {
                if (string.IsNullOrEmpty(rulesPath) || !File.Exists(rulesPath)) return false;
                string json = File.ReadAllText(rulesPath, Encoding.UTF8);
                return TopJson.TryReadBool(json, LevelField, out bool level) && level
                    && TopJson.TryReadInt(json, ModifierField, out int modifier) && modifier >= 0;
            }
            catch (Exception ex)
            {
                ModLog.Warning($"读取 mp_rules 层词条失败：{ex.Message}");
                return false;
            }
        }

        internal static List<string> ReadPlrPosKeys(string json)
            => ReadObjectKeys(json, PlrPosField);

        internal static List<string> ReadPlrCharsKeys(string json)
            => ReadObjectKeys(json, PlrCharsField);

        /// <summary>解析顶层 PLRPOS 玩家坐标表（兼容 Item1/Item2 与 x/y 字段命名）。</summary>
        internal static Dictionary<string, Vector2> ReadPlrPos(string json)
        {
            var result = new Dictionary<string, Vector2>(StringComparer.Ordinal);
            ForEachObjectMember(json, PlrPosField, (id, _, __, valStart, valEnd) =>
            {
                if (string.IsNullOrEmpty(id) || json[valStart] != '{') return;
                string inner = json.Substring(valStart, valEnd - valStart + 1);
                bool hasX = TopJson.TryReadFloat(inner, "Item1", out float x) || TopJson.TryReadFloat(inner, "x", out x);
                bool hasY = TopJson.TryReadFloat(inner, "Item2", out float y) || TopJson.TryReadFloat(inner, "y", out y);
                if (hasX || hasY) result[id] = new Vector2(x, y);
            });
            return result;
        }

        private static List<string> ReadObjectKeys(string json, string field)
        {
            var keys = new List<string>();
            ForEachObjectMember(json, field, (key, _, __, ___, ____) => keys.Add(key));
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
            StringBuilder sb = null;
            int copied = 0;
            ForEachObjectMember(json, field, (key, keyQuoteStart, keyEnd, _, __) =>
            {
                if (!mapping.TryGetValue(key, out string newKey)
                    || string.IsNullOrEmpty(newKey)
                    || string.Equals(key, newKey, StringComparison.Ordinal))
                {
                    return;
                }
                if (sb == null) sb = new StringBuilder(json.Length + 64);
                sb.Append(json, copied, keyQuoteStart + 1 - copied);
                TopJson.AppendEscapedContent(sb, newKey);
                sb.Append('"');
                copied = keyEnd;
            });

            if (sb == null) return json;
            sb.Append(json, copied, json.Length - copied);
            return sb.ToString();
        }

        private static void ForEachObjectMember(string json, string field, ObjectMemberVisitor visitor)
        {
            if (!TryFindTopObjectBlock(json, field, out int objStart, out int objEnd)) return;
            int p = TopJson.SkipWhitespace(json, objStart + 1);
            while (p < objEnd)
            {
                if (json[p] == ',')
                {
                    p = TopJson.SkipWhitespace(json, p + 1);
                    continue;
                }
                if (json[p] != '"') break;

                int keyQuoteStart = p;
                if (!TopJson.TryReadQuotedAt(json, ref p, out string key)) break;
                int keyEnd = p;

                int colon = TopJson.SkipWhitespace(json, p);
                if (colon >= objEnd || json[colon] != ':') break;

                int valStart = TopJson.SkipWhitespace(json, colon + 1);
                if (valStart >= objEnd) break;

                int valEnd = FindMemberValueEnd(json, valStart, objEnd);
                if (valEnd < valStart || valEnd >= objEnd) break;

                visitor(key, keyQuoteStart, keyEnd, valStart, valEnd);
                p = TopJson.SkipWhitespace(json, valEnd + 1);
            }
        }

        private static int FindMemberValueEnd(string json, int valStart, int objEnd)
        {
            char first = json[valStart];
            if (first == '{') return TopJson.FindMatchingBrace(json, valStart, '{', '}');
            if (first == '[') return TopJson.FindMatchingBrace(json, valStart, '[', ']');
            if (first == '"') return TopJson.FindStringEnd(json, valStart);

            int p = valStart;
            while (p < objEnd && json[p] != ',') p++;
            return p - 1;
        }

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
            if (TopJson.TryReadInt(json, SaveIdField, out int sid) && sid != 0) return sid;
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
}
