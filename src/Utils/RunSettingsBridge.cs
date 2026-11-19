using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>从存档恢复游戏难度设置（RunSettings），先以 normal 预设打底再覆盖存档键值，防止旧档缺键触发异常。</summary>
    internal static class RunSettingsBridge
    {
        private static MethodInfo _tupleListToDic;
        private static FieldInfo _runSettingsField;
        private static bool _resolved;

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                var wg = AccessTools.TypeByName("WorldGeneration");
                var ss = AccessTools.TypeByName("SaveSystem");
                if (wg != null) _runSettingsField = AccessTools.Field(wg, "runSettings");
                if (ss != null) _tupleListToDic = AccessTools.Method(ss, "TupleListToDic");
            }
            catch (Exception ex)
            {
                ModLog.Warning($"RunSettingsBridge.Resolve 失败：{ex.Message}");
            }
        }

        /// <summary>从指定 save.sv 解析顶层 runSettings 并合并写回 WorldGeneration.runSettings。</summary>
        internal static void RestoreFromSaveFile(string savePath)
        {
            Resolve();
            if (_runSettingsField == null || _tupleListToDic == null) return;
            try
            {
                if (string.IsNullOrEmpty(savePath) || !File.Exists(savePath)) return;
                string json = SaveSystem.Unzip(File.ReadAllBytes(savePath));
                var list = Parse(json);
                if (list.Count == 0) return;

                var baseline = RunSettings.GetPreset("normal")?.presetValues;
                var merged = baseline != null
                    ? new Dictionary<string, object>(baseline)
                    : new Dictionary<string, object>();

                if (_tupleListToDic.Invoke(null, new object[] { list }) is Dictionary<string, object> parsed)
                {
                    foreach (var kv in parsed)
                    {
                        merged[kv.Key] = kv.Value;
                    }
                }

                _runSettingsField.SetValue(null, merged);
                ModLog.Info($"已恢复 {list.Count} 项难度 runSettings（总计 {merged.Count} 项）");
            }
            catch (Exception ex)
            {
                ModLog.Warning($"RunSettingsBridge.RestoreFromSaveFile 失败：{ex.Message}");
            }
        }

        private static List<(string, string)> Parse(string json)
        {
            var list = new List<(string, string)>();
            int arrStart = TopJson.FindValueStart(json, "runSettings");
            if (arrStart < 0 || arrStart >= json.Length || json[arrStart] != '[') return list;
            int arrEnd = TopJson.FindMatchingBrace(json, arrStart, '[', ']');
            if (arrEnd < 0) return list;

            int depth = 0;
            int p = arrStart + 1;
            while (p < arrEnd)
            {
                char c = json[p];
                if (c == '"')
                {
                    int q2 = TopJson.FindStringEnd(json, p);
                    if (q2 < 0) break;
                    p = q2 + 1;
                    continue;
                }
                if (c == '{' && depth == 0)
                {
                    int objEnd = TopJson.FindMatchingBrace(json, p, '{', '}');
                    if (objEnd < 0) break;
                    string elem = json.Substring(p, objEnd - p + 1);
                    if (TryExtractPair(elem, out string key, out string rawVal) && IsKnownSetting(key))
                    {
                        list.Add((key, rawVal));
                    }
                    p = objEnd + 1;
                    continue;
                }
                if (c == '{' || c == '[') depth++;
                else if (c == '}' || c == ']') depth--;
                p++;
            }
            return list;
        }

        private static bool TryExtractPair(string elemJson, out string key, out string rawVal)
        {
            key = null;
            rawVal = null;
            if (!TopJson.TryReadString(elemJson, "Item1", out key) || string.IsNullOrEmpty(key))
            {
                if (!TopJson.TryReadString(elemJson, "Key", out key) || string.IsNullOrEmpty(key))
                    return false;
            }

            int vp = TopJson.FindValueStart(elemJson, "Item2");
            if (vp < 0) vp = TopJson.FindValueStart(elemJson, "Value");
            if (vp < 0 || vp >= elemJson.Length) return false;

            if (elemJson[vp] == '"')
            {
                return TopJson.TryReadQuotedAt(elemJson, ref vp, out rawVal) && rawVal != null;
            }

            int end = vp;
            while (end < elemJson.Length && elemJson[end] != ',' && elemJson[end] != '}') end++;
            string token = elemJson.Substring(vp, end - vp).Trim();
            if (string.IsNullOrEmpty(token) || string.Equals(token, "null", StringComparison.Ordinal)) return false;
            rawVal = token;
            return true;
        }

        private static bool IsKnownSetting(string name)
        {
            var types = RunSettings.settingTypes;
            if (types == null) return true;
            for (int i = 0; i < types.Count; i++)
            {
                if (types[i] != null && string.Equals(types[i].name, name, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }
    }
}
