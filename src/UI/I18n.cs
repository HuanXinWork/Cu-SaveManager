using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;
using System.Linq;
using System.Reflection;

namespace CasualtiesUnknown.SaveManager
{
/// <summary>
 /// i18n with external files: each *.json in the localization folder is a separate language.
 /// The file name (without the extension) is the language code, for example, zh-CN.json, en.json.
 /// </summary>
    internal static class I18n
    {
        private const string FallbackLanguage = "en";

        private static readonly string[] ChineseKeywords =
        {
            "中文", "汉化", "简中", "简体", "繁中", "繁体", "繁體", "chinese"
        };

        private static readonly Dictionary<string, Dictionary<string, string>> _languages =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary> Scans the folder and registers all languages. </summary>
        internal static void Init(string localizationDir)
        {
            Directory.CreateDirectory(localizationDir);
            bool hasAnyJson = Directory.GetFiles(localizationDir, "*.json").Length > 0;
            if (!hasAnyJson)
            {
                EnsureDefaultLanguages(localizationDir);
            }

            _languages.Clear();

            if (!Directory.Exists(localizationDir))
            {
                Directory.CreateDirectory(localizationDir);
                Debug.LogWarning("[SaveManager] The localization folder has been created, but there are no files: " + localizationDir);
                return;
            }

            foreach (string path in Directory.GetFiles(localizationDir, "*.json"))
            {
                string code = Path.GetFileNameWithoutExtension(path);
                Dictionary<string, string> dict = ReadFile(path);
                if (dict != null)
                {
                    _languages[code] = dict;
                    Debug.Log("[SaveManager] Loaded language: " + code);
                }
            }

            if (!_languages.ContainsKey(FallbackLanguage))
            {
                Debug.LogWarning("[SaveManager] File not found: " + FallbackLanguage + ".json, show key.");
            }
        }

        private static Dictionary<string, string> ReadFile(string path)
        {
            try
            {
                JObject root = JObject.Parse(File.ReadAllText(path));
                JArray entries = root["entries"] as JArray;
                if (entries == null)
                {
                    Debug.LogWarning("[SaveManager] Invalid translation file: " + path);
                    return null;
                }

                var dict = new Dictionary<string, string>();
                foreach (JToken token in entries)
                {
                    string key = (string)token["key"];
                    if (string.IsNullOrEmpty(key))
                    {
                        continue;
                    }
                    dict[key] = (string)token["value"] ?? string.Empty;
                }
                return dict;
            }
            catch (Exception ex)
            {
                Debug.LogError("[SaveManager] Read error " + path + ": " + ex.Message);
                return null;
            }
        }

        /// <summary> Returns the language code that should be used now, or null if there is no suitable one. </summary>
        private static string ResolveLanguageCode()
        {
            string mode = NormalizeLanguageMode(Plugin.PreferredLanguageMode);
            if (mode != "auto")
            {
                return _languages.ContainsKey(mode) ? mode : null;
            }

            string localeName = StripRichText(ReadCurrentLanguageName()).Trim();

            if (IsChineseLocaleName(localeName))
            {
                foreach (string code in _languages.Keys)
                {
                    if (code.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
                    {
                        return code;
                    }
                }
            }

            if (!string.IsNullOrEmpty(localeName) && _languages.ContainsKey(localeName))
            {
                return localeName;
            }

            return null;
        }

        private static string ReadCurrentLanguageName()
        {
            string name = null;
            try { name = Locale.currentLangName; } catch { }
            if (string.IsNullOrEmpty(name))
            {
                try { name = PlayerPrefs.GetString("locale"); } catch { }
            }
            return name;
        }

        private static string NormalizeLanguageMode(string mode)
        {
            if (string.IsNullOrWhiteSpace(mode)) return "auto";
            return mode.Trim();
        }

        private static bool IsChineseLocaleName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            if (name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("WC", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            for (int i = 0; i < ChineseKeywords.Length; i++)
            {
                if (name.IndexOf(ChineseKeywords[i], StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static string StripRichText(string value)
        {
            if (string.IsNullOrEmpty(value) || value.IndexOf('<') < 0)
            {
                return value ?? string.Empty;
            }

            var sb = new System.Text.StringBuilder(value.Length);
            bool inTag = false;
            foreach (char ch in value)
            {
                if (ch == '<') { inTag = true; continue; }
                if (ch == '>') { inTag = false; continue; }
                if (!inTag) sb.Append(ch);
            }
            return sb.ToString();
        }

        internal static string T(string key)
        {
            string code = ResolveLanguageCode();
            if (code != null && _languages[code].TryGetValue(key, out string value))
            {
                return value;
            }

            if (_languages.TryGetValue(FallbackLanguage, out Dictionary<string, string> en)
                && en.TryGetValue(key, out string fallback))
            {
                return fallback;
            }

            return key;
        }

        internal static string F(string key, params object[] args)
        {
            string fmt = T(key);
            try { return string.Format(fmt, args); }
            catch { return fmt; }
        }

        public static IEnumerable<string> GetAvailableLanguageCodes()
        {
            return _languages.Keys
                .Where(k => !string.Equals(k, "auto", StringComparison.OrdinalIgnoreCase))
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase);
        }

        public static string GetLanguageDisplayName(string code)
        {
            Dictionary<string, string> dict;
            if (_languages.TryGetValue(code, out dict))
            {
                string name;
                if (dict.TryGetValue("lang.name", out name) && !string.IsNullOrWhiteSpace(name))
                    return name;
            }
            return code;
        }
        private static readonly string[] DefaultLanguageFiles = { "en.json", "zh.json" };

        ///<summary> Creates the default localization files (en.json and zh-CN.json) in the given
        ///          folder when they do not already exist. </summary>
        private static void EnsureDefaultLanguages(string localizationDir)
        {
            Assembly asm = typeof(I18n).Assembly;
            string[] resources = asm.GetManifestResourceNames();

            foreach (string fileName in DefaultLanguageFiles)
            {
                string target = Path.Combine(localizationDir, fileName);
                if (File.Exists(target))
                    continue;

                string resourceName = resources.FirstOrDefault(n =>
                    n.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase));

                if (resourceName == null)
                {
                    Debug.LogWarning("[SaveManager] The built‑in resource " + fileName + " was not found in the assembly.");
                    continue;
                }

                using (Stream stream = asm.GetManifestResourceStream(resourceName))
                using (FileStream fs = File.Create(target))
                {
                    stream.CopyTo(fs);
                }
                Debug.LogWarning("[SaveManager] A localization file has been created.: " + target);
            }
        }
    }
}
