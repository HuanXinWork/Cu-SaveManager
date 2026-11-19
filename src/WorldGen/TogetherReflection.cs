using System;
using System.Reflection;
using CasualtiesUnknown.SaveManager.Interop;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>在已加载的 CasualtiesMP 程序集内解析联机模组类型，不跨程序集扫描。</summary>
    internal static class TogetherReflection
    {
        private const string NamespacePrefix = "Together.";

        internal static Assembly ModAssembly => InteropBootstrap.Together.ModAssembly;

        internal static Type FindType(string simpleName)
        {
            if (string.IsNullOrEmpty(simpleName))
            {
                return null;
            }

            if (TryResolveType(NamespacePrefix + simpleName, out Type resolved))
            {
                return resolved;
            }

            Assembly assembly = ModAssembly;
            if (assembly == null)
            {
                return null;
            }

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (Exception)
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

        internal static bool TryResolveType(string fullName, out Type type)
        {
            type = null;
            if (string.IsNullOrEmpty(fullName) || ModAssembly == null)
            {
                return false;
            }

            return ((IInteropTypeRegistry)InteropBootstrap.Together).TryResolveType(fullName, out type);
        }
    }
}
