using System;
using System.Reflection;
using CasualtiesUnknown.SaveManager.Interop;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>联机模组（程序集 CasualtiesMP）内类型的解析入口；成员匹配统一交给 <see cref="InteropReflection"/>。</summary>
    /// <remarks>
    /// 程序集取自 <see cref="TogetherProvider.ModAssembly"/>，只在已加载的程序集里查找，不主动加载。
    /// 类型先按 Together 命名空间的规范全名走提供方的类型注册能力，未命中再扫描联机模组程序集比对类型简单名，
    /// 因此联机模组调整命名空间时仍能解析；同名嵌套类型只作兜底。
    /// </remarks>
    internal static class TogetherReflection
    {
        private const string NamespacePrefix = "Together.";

        /// <summary>联机模组程序集；尚未加载时为 null。</summary>
        internal static Assembly ModAssembly => InteropBootstrap.Together.ModAssembly;

        /// <summary>按类型简单名解析联机模组内的类型；联机模组未加载或类型不存在时返回 null。</summary>
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

        /// <summary>按全名解析联机模组内的类型；走提供方的类型注册能力，只查联机模组程序集。</summary>
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
