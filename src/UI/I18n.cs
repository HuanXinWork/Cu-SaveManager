using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>
    /// 本地化：每个 *.json 语言包对应一种语言，文件名（不含扩展名）即语言代码，例如 zh-CN.json、en.json。
    /// 目录为空时释放内置的 en.json、zh.json。
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

        private static readonly string[] DefaultLanguageFiles = { "en.json", "zh.json" };

        /// <summary>扫描本地化目录并注册全部语言包。</summary>
        internal static void Init(string localizationDir)
        {
            Directory.CreateDirectory(localizationDir);
            bool hasAnyJson = Directory.GetFiles(localizationDir, "*.json").Length > 0;
            if (!hasAnyJson)
            {
                EnsureDefaultLanguages(localizationDir);
            }

            _languages.Clear();

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

        /// <summary>返回当前应使用的语言代码；没有可用语言包时返回 null。</summary>
        private static string ResolveLanguageCode()
        {
            string mode = NormalizeLanguageMode(Plugin.PreferredLanguageMode);
            if (mode != "auto")
            {
                return mode;
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
            if (string.IsNullOrEmpty(name))
            {
                try
                {
                    SystemLanguage sys = Application.systemLanguage;
                    if (sys == SystemLanguage.Chinese
                        || sys == SystemLanguage.ChineseSimplified
                        || sys == SystemLanguage.ChineseTraditional)
                    {
                        return "zh-CN";
                    }
                }
                catch { }
            }
            return name;
        }

        /// <summary>规范化语言模式：auto 或任意已加载语言代码；旧值 zh 映射到第一个 zh* 语言包。</summary>
        internal static string NormalizeLanguageMode(string mode)
        {
            if (string.IsNullOrWhiteSpace(mode)) return "auto";

            string value = mode.Trim();
            if (string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase))
            {
                return "auto";
            }

            foreach (string code in GetAvailableLanguageCodes())
            {
                if (string.Equals(code, value, StringComparison.OrdinalIgnoreCase))
                {
                    return code;
                }
            }

            // 兼容旧配置里的 "zh"：取第一个以 zh 开头的语言包
            if (string.Equals(value, "zh", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string code in GetAvailableLanguageCodes())
                {
                    if (code.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
                    {
                        return code;
                    }
                }
            }

            return "auto";
        }

        private static bool IsChineseLocaleName(string name)
        {
            string normalized = StripRichText(name).Trim();
            if (string.IsNullOrEmpty(normalized))
            {
                return false;
            }

            if (normalized.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith("WC", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            for (int i = 0; i < ChineseKeywords.Length; i++)
            {
                if (normalized.IndexOf(ChineseKeywords[i], StringComparison.OrdinalIgnoreCase) >= 0)
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

            var sb = new StringBuilder(value.Length);
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
            try
            {
                return string.Format(fmt, args);
            }
            catch (FormatException ex)
            {
                ModLog.Warning($"I18n.F 格式化失败 key={key}：{ex.Message}");
                return fmt;
            }
            catch
            {
                return fmt;
            }
        }

        public static IEnumerable<string> GetAvailableLanguageCodes()
        {
            return _languages.Keys
                .Where(k => !string.Equals(k, "auto", StringComparison.OrdinalIgnoreCase))
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase);
        }

        public static string GetLanguageDisplayName(string code)
        {
            if (_languages.TryGetValue(code, out Dictionary<string, string> dict)
                && dict.TryGetValue("lang.name", out string name)
                && !string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
            return code;
        }

        /// <summary>首次运行且没有任何语言包时，释放内置的 en.json、zh.json（不覆盖已有文件）。</summary>
        private static void EnsureDefaultLanguages(string localizationDir)
        {
            Assembly asm = typeof(I18n).Assembly;
            string[] resources = asm.GetManifestResourceNames();

            foreach (string fileName in DefaultLanguageFiles)
            {
                string target = Path.Combine(localizationDir, fileName);
                if (File.Exists(target))
                {
                    continue;
                }

                string resourceName = resources.FirstOrDefault(n =>
                    n.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase));

                if (resourceName == null)
                {
                    Debug.LogWarning("[SaveManager] The built-in resource " + fileName + " was not found in the assembly.");
                    continue;
                }

                using (Stream stream = asm.GetManifestResourceStream(resourceName))
                using (FileStream fs = File.Create(target))
                {
                    stream.CopyTo(fs);
                }
                Debug.LogWarning("[SaveManager] A localization file has been created: " + target);
            }
        }
    }
}
