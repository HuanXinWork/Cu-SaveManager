using System;
using System.Collections.Generic;
using System.Reflection;
using CasualtiesUnknown.SaveManager.Interop;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>在已加载的 CasualtiesMP 程序集内解析联机模组类型并缓存短名映射，不跨程序集扫描。</summary>
    internal static class TogetherReflection
    {
        private const string NamespacePrefix = "Together.";

        private static readonly Dictionary<string, Type> ShortNameCache =
            new Dictionary<string, Type>(StringComparer.Ordinal);
        private static Assembly _cachedAssembly;

        internal static Assembly ModAssembly => InteropBootstrap.Together.ModAssembly;

        internal static Type FindType(string simpleName)
        {
            if (string.IsNullOrEmpty(simpleName))
            {
                return null;
            }

            Assembly assembly = ModAssembly;
            if (assembly == null)
            {
                return null;
            }

            if (!ReferenceEquals(_cachedAssembly, assembly))
            {
                ShortNameCache.Clear();
                _cachedAssembly = assembly;
            }

            if (ShortNameCache.TryGetValue(simpleName, out Type cached))
            {
                return cached;
            }

            Type resolved = InteropReflection.FindType(assembly, NamespacePrefix + simpleName)
                ?? ScanByShortName(assembly, simpleName);
            ShortNameCache[simpleName] = resolved;
            return resolved;
        }

        internal static Type TryResolveType(string fullName)
        {
            if (string.IsNullOrEmpty(fullName))
            {
                return null;
            }

            Assembly assembly = ModAssembly;
            return assembly == null ? null : InteropReflection.FindType(assembly, fullName);
        }

        internal static bool TryResolveType(string fullName, out Type type)
        {
            type = TryResolveType(fullName);
            return type != null;
        }

        private static Type ScanByShortName(Assembly assembly, string simpleName)
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types;
            }
            catch (Exception)
            {
                return null;
            }

            if (types == null)
            {
                return null;
            }

            Type nested = null;
            for (int i = 0; i < types.Length; i++)
            {
                Type candidate = types[i];
                if (candidate == null || candidate.Name != simpleName)
                {
                    continue;
                }

                if (!candidate.IsNested)
                {
                    return candidate;
                }

                if (nested == null)
                {
                    nested = candidate;
                }
            }

            return nested;
        }
    }
}
