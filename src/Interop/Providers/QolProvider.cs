// CuSaveManager Interop
// Copyright (c) 2026 huanxin886
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace CasualtiesUnknown.SaveManager.Interop
{
    /// <summary>QoL.Unknown 的提供方适配器，全程通过反射访问目标程序集。</summary>
    /// <remarks>
    /// 目标程序集：程序集简单名含 "qol"（忽略大小写）的插件程序集；本次对接 QoL.Unknown。
    /// 依赖的目标成员：
    /// QoL_Unknown.SeedManager.IsSeeded（静态字段，bool）、
    /// QoL_Unknown.SeedManager.CurrentSeed（静态字段，int）、
    /// QoL_Unknown.SeedManager.InputString（静态字段，string）、
    /// QoL_Unknown.SaveManager.AutosaveEnabled（静态字段，bool）、
    /// QoL_Unknown.SaveSystemPatcher.PendingLoadPosition（静态字段，System.Nullable&lt;UnityEngine.Vector3&gt;）。
    /// QoL_Unknown.SaveSystemPatcher 与 QoL_Unknown.SeedManager 是 public 类型，QoL_Unknown.MultiplayerCompat 是 internal 类型；
    /// 后者及其它 internal 类型的成员在编译期不可见，本适配器不依赖它们，但类型解析一律走运行期反射，与可见性无关。
    /// 目标程序集尚未加载时不计为不可用缓存，下次访问重试；成员缺失项只在首次解析后警告一次。
    /// </remarks>
    internal sealed class QolProvider : IInteropProvider, IInteropMemberAccess, IInteropTypeRegistry, IInteropWorldGenHook
    {
        private const string TargetAssemblyNamePart = "qol";

        private const string SeedManagerTypeName = "QoL_Unknown.SeedManager";
        private const string SaveManagerTypeName = "QoL_Unknown.SaveManager";
        private const string SaveSystemPatcherTypeName = "QoL_Unknown.SaveSystemPatcher";

        private readonly object _sync = new object();

        // 标志在锁内写、锁外读（EnsureResolved 的双检锁），必须带 volatile：
        // 锁外读取只做 acquire，否则看到 _resolved 为 true 时其余成员字段仍可能不可见。
        private volatile bool _resolved;
        private volatile bool _available;

        private Assembly _assembly;
        private Type _seedManagerType;
        private Type _saveManagerType;
        private Type _saveSystemPatcherType;

        private FieldInfo _seedIsSeeded;
        private FieldInfo _seedCurrentSeed;
        private FieldInfo _seedInputString;
        private FieldInfo _autosaveEnabled;
        private FieldInfo _pendingLoadPosition;

        /// <inheritdoc/>
        string IInteropProvider.Name => "QoL";

        /// <inheritdoc/>
        bool IInteropProvider.IsAvailable => EnsureResolved();

        /// <inheritdoc/>
        InteropCapability IInteropProvider.Capabilities =>
            InteropCapability.MemberAccess
            | InteropCapability.WorldGenHook;

        /// <inheritdoc/>
        int IInteropProvider.Priority => 10;

        /// <summary>目标插件是否在场且可访问。</summary>
        internal bool IsPresent => EnsureResolved();

        /// <summary>目标插件程序集；尚未加载时为 null。</summary>
        internal Assembly ModAssembly
        {
            get
            {
                EnsureResolved();
                return _assembly;
            }
        }

        /// <summary>读取 QoL.Unknown 的种子状态；字段缺失时对应输出为默认值。</summary>
        /// <remarks>链路：QoL_Unknown.SeedManager.IsSeeded / CurrentSeed / InputString。</remarks>
        internal bool TryReadSeed(out bool seeded, out int currentSeed, out string inputString)
        {
            seeded = false;
            currentSeed = 0;
            inputString = string.Empty;
            if (!EnsureResolved())
            {
                return false;
            }

            object seededValue = InteropReflection.ReadMember(_seedIsSeeded, null);
            object currentValue = InteropReflection.ReadMember(_seedCurrentSeed, null);
            object inputValue = InteropReflection.ReadMember(_seedInputString, null);
            seeded = seededValue is bool flag && flag;
            currentSeed = currentValue is int seed ? seed : 0;
            inputString = inputValue as string ?? string.Empty;
            return _seedCurrentSeed != null;
        }

        /// <summary>把种子状态写回 QoL.Unknown；三个字段按给定值整体覆盖。</summary>
        /// <remarks>链路：QoL_Unknown.SeedManager.CurrentSeed / InputString / IsSeeded。</remarks>
        internal bool TryWriteSeed(bool seeded, int currentSeed, string inputString)
        {
            if (!EnsureResolved() || _seedCurrentSeed == null)
            {
                return false;
            }

            bool written = InteropReflection.TryWriteMember(_seedCurrentSeed, null, currentSeed);
            if (_seedInputString != null)
            {
                written &= InteropReflection.TryWriteMember(_seedInputString, null, inputString ?? string.Empty);
            }

            if (_seedIsSeeded != null)
            {
                written &= InteropReflection.TryWriteMember(_seedIsSeeded, null, seeded);
            }

            return written;
        }

        /// <summary>读取 QoL.Unknown 的自动存档开关。</summary>
        /// <remarks>链路：QoL_Unknown.SaveManager.AutosaveEnabled。</remarks>
        internal bool TryReadAutosaveEnabled(out bool enabled)
        {
            enabled = false;
            if (!EnsureResolved())
            {
                return false;
            }

            object value = InteropReflection.ReadMember(_autosaveEnabled, null);
            enabled = value is bool flag && flag;
            return _autosaveEnabled != null;
        }

        /// <summary>写入 QoL.Unknown 的自动存档开关。</summary>
        /// <remarks>链路：QoL_Unknown.SaveManager.AutosaveEnabled。</remarks>
        internal bool TryWriteAutosaveEnabled(bool enabled)
        {
            if (!EnsureResolved() || _autosaveEnabled == null)
            {
                return false;
            }

            return InteropReflection.TryWriteMember(_autosaveEnabled, null, enabled);
        }

        /// <summary>写入读档后要还原的玩家坐标。</summary>
        /// <remarks>
        /// 链路：QoL_Unknown.SaveSystemPatcher.PendingLoadPosition（System.Nullable&lt;UnityEngine.Vector3&gt;）。
        /// 目标字段由 QoL 在 TryLoadGame 的 postfix 读取并写回玩家身体，本方法只负责把值放进去。
        /// </remarks>
        internal bool TryWritePendingLoadPosition(Vector3 position)
        {
            if (!EnsureResolved() || _pendingLoadPosition == null)
            {
                return false;
            }

            Vector3? nullable = position;
            return InteropReflection.TryWriteMember(_pendingLoadPosition, null, nullable);
        }

        /// <summary>一行摘要，列出各依赖成员的解析结果，用于启动日志与版本变动时的排查。</summary>
        internal string DescribeResolvedMembers()
        {
            EnsureResolved();
            return "assembly=" + State(_assembly)
                + " QoL_Unknown.SeedManager.IsSeeded=" + State(_seedIsSeeded)
                + " QoL_Unknown.SeedManager.CurrentSeed=" + State(_seedCurrentSeed)
                + " QoL_Unknown.SeedManager.InputString=" + State(_seedInputString)
                + " QoL_Unknown.SaveManager.AutosaveEnabled=" + State(_autosaveEnabled)
                + " QoL_Unknown.SaveSystemPatcher.PendingLoadPosition=" + State(_pendingLoadPosition);
        }

        /// <remarks>只查目标程序集，不扫描 AppDomain。</remarks>
        bool IInteropTypeRegistry.TryResolveType(string fullName, out Type type)
        {
            return TryResolveType(fullName, out type);
        }

        /// <summary>在目标程序集内按全名解析类型；未加载或未找到时返回 false。</summary>
        internal bool TryResolveType(string fullName, out Type type)
        {
            type = null;
            EnsureResolved();
            if (_assembly == null)
            {
                return false;
            }

            type = InteropReflection.FindType(_assembly, fullName);
            return type != null;
        }

        /// <remarks>与目标程序集无关的通用实现：成员按名称在传入的类型上解析。</remarks>
        bool IInteropMemberAccess.TryReadStatic(Type type, string member, out object value)
        {
            value = null;
            MemberInfo target = FindStaticMember(type, member);
            if (target == null)
            {
                return false;
            }

            value = InteropReflection.ReadMember(target, null);
            return value != null;
        }

        /// <remarks>字段与可写属性都支持；目标成员缺失或写入抛异常时返回 false。</remarks>
        bool IInteropMemberAccess.TryWriteStatic(Type type, string member, object value)
        {
            MemberInfo target = FindStaticMember(type, member);
            if (target == null)
            {
                return false;
            }

            return InteropReflection.TryWriteMember(target, null, value);
        }

        /// <remarks>与目标程序集无关的通用实现：成员按名称在传入对象的类型上解析。</remarks>
        bool IInteropMemberAccess.TryReadInstance(object target, string member, out object value)
        {
            value = null;
            MemberInfo info = target == null ? null : FindInstanceMember(target.GetType(), member);
            if (info == null)
            {
                return false;
            }

            value = InteropReflection.ReadMember(info, target);
            return value != null;
        }

        /// <remarks>字段与可写属性都支持；目标成员缺失或写入抛异常时返回 false。</remarks>
        bool IInteropMemberAccess.TryWriteInstance(object target, string member, object value)
        {
            MemberInfo info = target == null ? null : FindInstanceMember(target.GetType(), member);
            return info != null && InteropReflection.TryWriteMember(info, target, value);
        }

        /// <remarks>重载按实参类型匹配（可赋值判定），不再用「名称 + 形参个数」；out 形参的读回值留在传入的 args 数组里。</remarks>
        bool IInteropMemberAccess.TryInvokeStatic(Type type, string method, out object result, params object[] args)
        {
            object[] arguments = args ?? Array.Empty<object>();
            result = null;
            return InteropReflection.TryInvoke(InteropReflection.FindMethodByArgs(type, method, true, arguments), null, arguments, out result);
        }

        /// <remarks>重载按实参类型匹配（可赋值判定），不再用「名称 + 形参个数」；out 形参的读回值留在传入的 args 数组里。</remarks>
        bool IInteropMemberAccess.TryInvokeInstance(object target, string method, out object result, params object[] args)
        {
            result = null;
            if (target == null)
            {
                return false;
            }

            object[] arguments = args ?? Array.Empty<object>();
            return InteropReflection.TryInvoke(InteropReflection.FindMethodByArgs(target.GetType(), method, false, arguments), target, arguments, out result);
        }

        /// <remarks>种子取自 QoL_Unknown.SeedManager.CurrentSeed；当前种子为 0 视为未设置。</remarks>
        bool IInteropWorldGenHook.TryGetSeed(out int seed)
        {
            seed = 0;
            if (!TryReadSeed(out bool seeded, out int current, out string _))
            {
                return false;
            }

            seed = current;
            return seeded && current != 0;
        }

        /// <remarks>目标插件未提供世界生成阶段的可观察状态，固定为 false。</remarks>
        bool IInteropWorldGenHook.IsGenerating => false;

        /// <remarks>目标插件未提供世界生成阶段的事件源，固定返回 false。</remarks>
        bool IInteropWorldGenHook.TrySubscribeGeneration(Action<bool> handler) => false;

        /// <summary>按名解析静态字段或静态属性；都没有时返回 null。</summary>
        private static MemberInfo FindStaticMember(Type type, string member)
        {
            if (type == null || string.IsNullOrEmpty(member))
            {
                return null;
            }

            MemberInfo field = InteropReflection.FindField(type, member);
            return field ?? InteropReflection.FindProperty(type, member);
        }

        /// <summary>按名解析实例字段或实例属性；都没有时返回 null。</summary>
        private static MemberInfo FindInstanceMember(Type type, string member)
        {
            if (type == null || string.IsNullOrEmpty(member))
            {
                return null;
            }

            MemberInfo field = InteropReflection.FindField(type, member);
            return field ?? InteropReflection.FindProperty(type, member);
        }

        /// <summary>把成员解析结果格式化为 ok / missing。</summary>
        private static string State(object member)
        {
            return member == null ? "missing" : "ok";
        }

        /// <summary>首次访问时解析目标程序集及全部依赖成员并缓存；目标程序集尚未加载时不置缓存，下次访问重试。</summary>
        /// <remarks>目标程序集不存在属于可选场景，不计失败也不记警告；成员解析失败后缓存并只警告一次。</remarks>
        private bool EnsureResolved()
        {
            if (_resolved)
            {
                return _available;
            }

            lock (_sync)
            {
                if (_resolved)
                {
                    return _available;
                }

                Assembly assembly = FindTargetAssembly();
                if (assembly == null)
                {
                    return false;
                }

                List<string> missing = new List<string>();
                _available = Resolve(assembly, missing);
                if (missing.Count > 0)
                {
                    InteropDiagnostics.WarnOnce("QolProvider", "目标程序集成员缺失：" + string.Join(", ", missing));
                }

                // 标志最后发布：锁外的读者看到 _resolved 为 true 时，此前写入的成员字段均已可见。
                _resolved = true;
            }

            return _available;
        }

        /// <summary>丢弃已缓存的解析结果并重新解析，用于目标插件晚加载或成员变化后的显式重试。</summary>
        internal bool TryRefresh()
        {
            ModLog.Debug("QoL：TryRefresh 被调用，丢弃已缓存的解析结果并重新解析");
            lock (_sync)
            {
                _resolved = false;
                _available = false;
            }

            return EnsureResolved();
        }

        /// <summary>解析目标程序集的全部依赖成员，并把缺失项写进 <paramref name="missing"/>。</summary>
        /// <remarks>在场判定与旧桥一致：SaveSystemPatcher 或 SeedManager 任一解析到即视为在场；成员缺失只关闭对应能力。</remarks>
        private bool Resolve(Assembly assembly, List<string> missing)
        {
            _assembly = assembly;
            _seedManagerType = InteropReflection.FindType(_assembly, SeedManagerTypeName);
            _saveManagerType = InteropReflection.FindType(_assembly, SaveManagerTypeName);
            _saveSystemPatcherType = InteropReflection.FindType(_assembly, SaveSystemPatcherTypeName);
            bool present = _seedManagerType != null || _saveSystemPatcherType != null;
            if (!present)
            {
                missing.Add(SeedManagerTypeName + " 与 " + SaveSystemPatcherTypeName);
                ModLog.Debug("QoL：assembly='" + assembly.GetName().Name + "' available=false capabilities=[] missing=[" + string.Join(", ", missing) + "]");
                return false;
            }

            _seedIsSeeded = Require(_seedManagerType, "IsSeeded", missing);
            _seedCurrentSeed = Require(_seedManagerType, "CurrentSeed", missing);
            _seedInputString = Require(_seedManagerType, "InputString", missing);
            _autosaveEnabled = Require(_saveManagerType, "AutosaveEnabled", missing);
            _pendingLoadPosition = Require(_saveSystemPatcherType, "PendingLoadPosition", missing);
            // 历史上出现过 QoL 程序集已加载但成员全部解析不到的情况，这里逐成员列出结果以便直接定位。
            ModLog.Debug("QoL：assembly='" + assembly.GetName().Name + "' available=true capabilities=["
                + InteropDiagnostics.FormatCapabilities(InteropCapability.MemberAccess | InteropCapability.WorldGenHook) + "]"
                + " 已解析 " + CountResolved(_seedIsSeeded, _seedCurrentSeed, _seedInputString, _autosaveEnabled, _pendingLoadPosition) + " 个成员"
                + " SeedManager.IsSeeded=" + State(_seedIsSeeded)
                + " SeedManager.CurrentSeed=" + State(_seedCurrentSeed)
                + " SeedManager.InputString=" + State(_seedInputString)
                + " SaveManager.AutosaveEnabled=" + State(_autosaveEnabled)
                + " SaveSystemPatcher.PendingLoadPosition=" + State(_pendingLoadPosition)
                + " missing=[" + string.Join(", ", missing) + "]");
            return true;
        }

        /// <summary>Debug 构建下统计已解析成员数；Release 中调用点被整体移除，不会分配数组。</summary>
        private static int CountResolved(params object[] members)
        {
            int resolved = 0;
            for (int i = 0; i < members.Length; i++)
            {
                if (members[i] != null)
                {
                    resolved++;
                }
            }

            return resolved;
        }

        /// <summary>在已加载程序集里按简单名匹配目标程序集；不加载新程序集。</summary>
        private static Assembly FindTargetAssembly()
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                string name = assemblies[i].GetName().Name;
                if (!string.IsNullOrEmpty(name) && name.IndexOf(TargetAssemblyNamePart, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return assemblies[i];
                }
            }

            return null;
        }

        /// <summary>按名称取静态字段；类型或成员缺失时记录缺失项并返回 null。</summary>
        private static FieldInfo Require(Type type, string member, List<string> missing)
        {
            FieldInfo field = type == null ? null : InteropReflection.FindField(type, member);
            if (field == null)
            {
                missing.Add((type == null ? "<type>" : type.Name + "." + member));
            }

            return field;
        }
    }
}
