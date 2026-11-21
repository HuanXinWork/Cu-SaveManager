using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>从存档恢复游戏难度设置（RunSettings），先以 normal 预设打底再覆盖存档键值，防止旧档缺键触发异常。</summary>
    internal static class RunSettingsBridge
    {
        /// <summary>从指定 save.sv 解析顶层 runSettings 并合并写回 WorldGeneration.runSettings。</summary>
        internal static void RestoreFromSaveFile(string savePath)
        {
            try
            {
                if (string.IsNullOrEmpty(savePath) || !File.Exists(savePath)) return;
                string json = SaveSystem.Unzip(File.ReadAllBytes(savePath));
                var list = ParseRunSettings(json);
                if (list.Count == 0) return;

                var baseline = RunSettings.GetPreset("normal")?.presetValues;
                var merged = baseline != null
                    ? new Dictionary<string, object>(baseline)
                    : new Dictionary<string, object>();

                var parsed = SaveSystem.TupleListToDic(list);
                if (parsed != null)
                {
                    foreach (var kv in parsed)
                    {
                        merged[kv.Key] = kv.Value;
                    }
                }

                WorldGeneration.runSettings = merged;
                ModLog.Debug($"已恢复 {list.Count} 项难度 runSettings（总计 {merged.Count} 项）");
            }
            catch (Exception ex)
            {
                ModLog.Warning($"RunSettingsBridge.RestoreFromSaveFile 失败：{ex.Message}");
            }
        }

        private static List<(string, string)> ParseRunSettings(string json)
        {
            var list = new List<(string, string)>();
            if (string.IsNullOrEmpty(json)) return list;

            var root = JObject.Parse(json);
            if (!(root["runSettings"] is JArray arr)) return list;

            for (int i = 0; i < arr.Count; i++)
            {
                if (!(arr[i] is JObject elem)) continue;
                string key = (string)(elem["Item1"] ?? elem["Key"]);
                if (string.IsNullOrEmpty(key) || !IsKnownSetting(key)) continue;

                JToken valToken = elem["Item2"] ?? elem["Value"];
                if (valToken == null || valToken.Type == JTokenType.Null) continue;

                string rawVal = FormatSettingToken(valToken);
                if (rawVal != null) list.Add((key, rawVal));
            }
            return list;
        }

        private static string FormatSettingToken(JToken token)
        {
            if (token.Type == JTokenType.String) return (string)token;
            if (token.Type == JTokenType.Boolean) return (bool)token ? "true" : "false";
            if (token is JValue jv) return jv.ToString(CultureInfo.InvariantCulture);
            return token.ToString(Formatting.None);
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
