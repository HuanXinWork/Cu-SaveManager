using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>
    /// 多语言词条管理：按 *.json 文件名注册语言代码，启动时自动释放并增量补齐内置 en/zh 词条，按帧缓存生效词典。
    /// </summary>
    internal static class I18n
    {
        private const string FallbackLanguage = "en";
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        private static readonly string[] ChineseKeywords =
        {
            "中文", "汉化", "简中", "简体", "繁中", "繁体", "繁體", "chinese"
        };

        private static readonly Dictionary<string, Dictionary<string, string>> _languages =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> _warnedFormatKeys =
            new HashSet<string>(StringComparer.Ordinal);

        private static readonly string[] DefaultLanguageFiles = { "en.json", "zh.json" };

        private static string[] _availableCodesCache = Array.Empty<string>();
        private static Dictionary<string, string> _fallbackDict;

        private static int _cachedFrame = -1;
        private static string _cachedModeInput;
        private static string _cachedLangCode;
        private static Dictionary<string, string> _cachedLangDict;

        /// <summary>同步内置默认语言包并加载本地化目录下的全部语言文件。</summary>
        internal static void Init(string localizationDir)
        {
            Directory.CreateDirectory(localizationDir);
            // 无论目录是否已有第三方语言包，均确保 en.json/zh.json 存在且补齐新版新增词条键。
            EnsureDefaultLanguages(localizationDir);

            _languages.Clear();
            _warnedFormatKeys.Clear();

            string[] files = Directory.GetFiles(localizationDir, "*.json");
            for (int i = 0; i < files.Length; i++)
            {
                string path = files[i];
                string code = Path.GetFileNameWithoutExtension(path);
                if (string.IsNullOrWhiteSpace(code)) continue;

                Dictionary<string, string> dict = ReadFile(path);
                if (dict != null)
                {
                    _languages[code] = dict;
                    ModLog.Debug("已加载语言包：" + code);
                }
            }

            // 磁盘默认语言包若因权限或手改语法错误未能加载，用内置资源在内存兜底以防回退链断裂。
            EnsureBuiltinFallbackInMemory();

            if (!_languages.TryGetValue(FallbackLanguage, out _fallbackDict))
            {
                _fallbackDict = null;
                ModLog.Warning($"未找到默认回退语言包 {FallbackLanguage}.json，缺失词条将直接显示键名。");
            }

            RebuildAvailableCodesCache();
            InvalidateCache();
        }

        /// <summary>切换语言设置后立即失效当帧语言解析缓存。</summary>
        internal static void InvalidateCache()
        {
            _cachedFrame = -1;
            _cachedModeInput = null;
            _cachedLangCode = null;
            _cachedLangDict = null;
        }

        private static void RebuildAvailableCodesCache()
        {
            var list = new List<string>(_languages.Count);
            foreach (string key in _languages.Keys)
            {
                if (!string.Equals(key, "auto", StringComparison.OrdinalIgnoreCase))
                {
                    list.Add(key);
                }
            }
            string[] arr = list.ToArray();
            Array.Sort(arr, StringComparer.OrdinalIgnoreCase);
            _availableCodesCache = arr;
        }

        private static Dictionary<string, string> ReadFile(string path)
        {
            try
            {
                return ParseEntries(File.ReadAllText(path, Encoding.UTF8), path);
            }
            catch (Exception ex)
            {
                ModLog.Warning($"读取语言包失败 {path}：{ex.Message}");
                return null;
            }
        }

        private static Dictionary<string, string> ParseEntries(string jsonText, string sourceLabel)
        {
            JObject root = JObject.Parse(jsonText);
            JArray entries = root["entries"] as JArray;
            if (entries == null)
            {
                ModLog.Warning("无效的语言包文件（缺少 entries 数组）：" + sourceLabel);
                return null;
            }

            var dict = new Dictionary<string, string>(entries.Count, StringComparer.Ordinal);
            foreach (JToken token in entries)
            {
                string key = (string)token["key"];
                if (string.IsNullOrEmpty(key)) continue;
                dict[key] = (string)token["value"] ?? string.Empty;
            }
            return dict;
        }

        private static int TryGetCurrentFrame()
        {
            try { return Time.frameCount; }
            catch { return -1; }
        }

        /// <summary>返回当前应使用的语言代码；按渲染帧与配置输入缓存，避免单帧数百次重复解析。</summary>
        private static string ResolveLanguageCode()
        {
            string rawMode = Plugin.PreferredLanguageMode ?? "auto";
            int frame = TryGetCurrentFrame();
            if (frame >= 0 && frame == _cachedFrame && string.Equals(rawMode, _cachedModeInput, StringComparison.Ordinal))
            {
                return _cachedLangCode;
            }

            string resolved = ComputeLanguageCode(rawMode);
            if (frame >= 0)
            {
                _cachedFrame = frame;
                _cachedModeInput = rawMode;
                _cachedLangCode = resolved;
                _cachedLangDict = resolved != null && _languages.TryGetValue(resolved, out Dictionary<string, string> d)
                    ? d
                    : null;
            }
            return resolved;
        }

        private static string ComputeLanguageCode(string rawMode)
        {
            string mode = NormalizeLanguageMode(rawMode);
            if (mode != "auto")
            {
                return _languages.ContainsKey(mode) ? mode : null;
            }

            string localeName = StripRichText(ReadCurrentLanguageName()).Trim();
            if (IsChineseLocaleName(localeName))
            {
                for (int i = 0; i < _availableCodesCache.Length; i++)
                {
                    string code = _availableCodesCache[i];
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

            for (int i = 0; i < _availableCodesCache.Length; i++)
            {
                string code = _availableCodesCache[i];
                if (string.Equals(code, value, StringComparison.OrdinalIgnoreCase))
                {
                    return code;
                }
            }

            if (string.Equals(value, "zh", StringComparison.OrdinalIgnoreCase))
            {
                for (int i = 0; i < _availableCodesCache.Length; i++)
                {
                    string code = _availableCodesCache[i];
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
            if (string.IsNullOrEmpty(normalized)) return false;

            // 兼容游戏旧版及社区汉化包使用的 "WC" 前缀中文语言码。
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
            for (int i = 0; i < value.Length; i++)
            {
                char ch = value[i];
                if (ch == '<') { inTag = true; continue; }
                if (ch == '>') { inTag = false; continue; }
                if (!inTag) sb.Append(ch);
            }
            return sb.ToString();
        }

        internal static string T(string key)
        {
            if (string.IsNullOrEmpty(key)) return string.Empty;

            string code = ResolveLanguageCode();
            Dictionary<string, string> activeDict = _cachedFrame >= 0
                ? _cachedLangDict
                : (code != null && _languages.TryGetValue(code, out Dictionary<string, string> d) ? d : null);

            if (activeDict != null && activeDict.TryGetValue(key, out string value))
            {
                return value;
            }

            if (_fallbackDict != null && _fallbackDict.TryGetValue(key, out string fallback))
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
                if (_warnedFormatKeys.Add(key ?? string.Empty))
                {
                    ModLog.Warning($"I18n.F 格式化失败 key={key}：{ex.Message}");
                }
                return fmt;
            }
            catch
            {
                return fmt;
            }
        }

        public static string[] GetAvailableLanguageCodes() => _availableCodesCache;

        public static string GetLanguageDisplayName(string code)
        {
            if (code != null
                && _languages.TryGetValue(code, out Dictionary<string, string> dict)
                && dict.TryGetValue("lang.name", out string name)
                && !string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
            return code ?? string.Empty;
        }

        /// <summary>逐个检查内置语言包：缺失时释放文件；已存在时增量合并缺失的新词条键，保留用户自定义翻译不变。</summary>
        private static void EnsureDefaultLanguages(string localizationDir)
        {
            Assembly asm = typeof(I18n).Assembly;
            string[] resources = asm.GetManifestResourceNames();

            for (int i = 0; i < DefaultLanguageFiles.Length; i++)
            {
                string fileName = DefaultLanguageFiles[i];
                string resourceName = FindManifestResource(resources, fileName);
                if (resourceName == null)
                {
                    ModLog.Warning("程序集中未找到内置语言资源：" + fileName);
                    continue;
                }

                string builtinJson = ReadEmbeddedText(asm, resourceName);
                if (string.IsNullOrEmpty(builtinJson)) continue;

                string target = Path.Combine(localizationDir, fileName);
                if (!File.Exists(target))
                {
                    try
                    {
                        File.WriteAllText(target, builtinJson, Utf8NoBom);
                        ModLog.Debug("已释放内置语言包：" + target);
                    }
                    catch (Exception ex)
                    {
                        ModLog.Warning($"写入默认语言包失败 {target}：{ex.Message}");
                    }
                    continue;
                }

                MergeMissingKeysIntoDiskFile(target, builtinJson);
            }
        }

        private static void MergeMissingKeysIntoDiskFile(string targetPath, string builtinJson)
        {
            try
            {
                JObject builtinRoot = JObject.Parse(builtinJson);
                JArray builtinEntries = builtinRoot["entries"] as JArray;
                if (builtinEntries == null || builtinEntries.Count == 0) return;

                JObject diskRoot = JObject.Parse(File.ReadAllText(targetPath, Encoding.UTF8));
                JArray diskEntries = diskRoot["entries"] as JArray;
                if (diskEntries == null) return;

                var existingKeys = new HashSet<string>(StringComparer.Ordinal);
                foreach (JToken token in diskEntries)
                {
                    string key = (string)token["key"];
                    if (!string.IsNullOrEmpty(key)) existingKeys.Add(key);
                }

                int added = 0;
                foreach (JToken token in builtinEntries)
                {
                    string key = (string)token["key"];
                    if (string.IsNullOrEmpty(key) || !existingKeys.Add(key)) continue;
                    diskEntries.Add(token.DeepClone());
                    added++;
                }

                if (added > 0)
                {
                    File.WriteAllText(targetPath, FormatLanguageJson(diskRoot, diskEntries), Utf8NoBom);
                    ModLog.Debug($"已向语言包增量补齐 {added} 个新词条：{targetPath}");
                }
            }
            catch (Exception ex)
            {
                ModLog.Warning($"增量合并语言包失败 {targetPath}：{ex.Message}");
            }
        }

        private static string FormatLanguageJson(JObject root, JArray entries)
        {
            if (root.Count != 1)
            {
                return root.ToString(Formatting.Indented);
            }

            var sb = new StringBuilder(entries.Count * 64 + 32);
            sb.AppendLine("{");
            sb.AppendLine("  \"entries\": [");
            for (int i = 0; i < entries.Count; i++)
            {
                JToken entry = entries[i];
                string keyJson = JsonConvert.ToString((string)entry["key"] ?? string.Empty);
                string valJson = JsonConvert.ToString((string)entry["value"] ?? string.Empty);
                sb.Append("    { \"key\": ").Append(keyJson).Append(", \"value\": ").Append(valJson).Append(" }");
                if (i < entries.Count - 1) sb.Append(',');
                sb.AppendLine();
            }
            sb.AppendLine("  ]");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static void EnsureBuiltinFallbackInMemory()
        {
            Assembly asm = typeof(I18n).Assembly;
            string[] resources = asm.GetManifestResourceNames();

            for (int i = 0; i < DefaultLanguageFiles.Length; i++)
            {
                string fileName = DefaultLanguageFiles[i];
                string code = Path.GetFileNameWithoutExtension(fileName);
                if (_languages.ContainsKey(code)) continue;

                string resourceName = FindManifestResource(resources, fileName);
                if (resourceName == null) continue;

                try
                {
                    string json = ReadEmbeddedText(asm, resourceName);
                    Dictionary<string, string> dict = !string.IsNullOrEmpty(json) ? ParseEntries(json, resourceName) : null;
                    if (dict != null) _languages[code] = dict;
                }
                catch { }
            }
        }

        private static string FindManifestResource(string[] resources, string fileName)
        {
            string suffix = "." + fileName;
            for (int i = 0; i < resources.Length; i++)
            {
                if (resources[i].EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    return resources[i];
                }
            }
            return null;
        }

        private static string ReadEmbeddedText(Assembly asm, string resourceName)
        {
            try
            {
                using (Stream stream = asm.GetManifestResourceStream(resourceName))
                {
                    if (stream == null) return null;
                    using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                    {
                        return reader.ReadToEnd();
                    }
                }
            }
            catch (Exception ex)
            {
                ModLog.Warning($"读取内置语言资源失败 {resourceName}：{ex.Message}");
                return null;
            }
        }
    }
}
