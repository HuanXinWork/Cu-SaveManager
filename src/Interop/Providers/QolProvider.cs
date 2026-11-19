// CuSaveManager Interop
// Copyright (c) 2026 huanxin886
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace CasualtiesUnknown.SaveManager.Interop
{
    /// <summary>QoL.Unknown 反射提供方，对接世界种子、自动存档状态与读档坐标恢复字段。</summary>
    /// <remarks>匹配简单名含 "qol" 的插件程序集（QoL_Unknown.SeedManager / SaveManager / SaveSystemPatcher）。</remarks>
    internal sealed class QolProvider : IInteropProvider, IInteropMemberAccess, IInteropTypeRegistry, IInteropWorldGenHook
    {
        private const string TargetAssemblyNamePart = "qol";

        private const string SeedManagerTypeName = "QoL_Unknown.SeedManager";
        private const string SaveManagerTypeName = "QoL_Unknown.SaveManager";
        private const string SaveSystemPatcherTypeName = "QoL_Unknown.SaveSystemPatcher";

        private readonly object _sync = new object();

        // 双检锁标志：锁外读取需 acquire 语义以确保已解析成员字段可见。
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
        private FieldInfo _isAutosaveOperation;
        private FieldInfo _currentSaveFile;

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

        /// <summary>目标插件是否在场且核心能力字段可访问。</summary>
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

        /// <summary>QoL 后台自动存档是否正在执行（QoL_Unknown.SaveSystemPatcher.IsAutosaveOperation）。</summary>
        internal bool IsAutosaveInProgress
        {
            get
            {
                EnsureResolved();
                return InteropReflection.ReadMember(_isAutosaveOperation, null) is bool flag && flag;
            }
        }

        /// <summary>QoL 当前活动存档文件名（QoL_Unknown.SaveManager.CurrentSaveFile）；不可用时返回 "save.sv"。</summary>
        internal string CurrentSaveFile
        {
            get
            {
                EnsureResolved();
                string file = InteropReflection.ReadMember(_currentSaveFile, null) as string;
                return !string.IsNullOrEmpty(file) ? file : "save.sv";
            }
        }

        /// <summary>覆写 QoL 当前活动存档文件名（QoL_Unknown.SaveManager.CurrentSaveFile）。</summary>
        internal bool TryWriteCurrentSaveFile(string fileName)
        {
            if (!EnsureResolved() || _currentSaveFile == null || string.IsNullOrWhiteSpace(fileName))
            {
                return false;
            }

            return InteropReflection.TryWriteMember(_currentSaveFile, null, fileName);
        }

        /// <summary>读取 QoL 种子状态（QoL_Unknown.SeedManager.IsSeeded / CurrentSeed / InputString）。</summary>
        internal bool TryReadSeed(out bool seeded, out int currentSeed, out string inputString)
        {
            seeded = false;
            currentSeed = 0;
            inputString = string.Empty;
            if (!EnsureResolved() || _seedCurrentSeed == null || _seedIsSeeded == null)
            {
                return false;
            }

            object seededValue = InteropReflection.ReadMember(_seedIsSeeded, null);
            object currentValue = InteropReflection.ReadMember(_seedCurrentSeed, null);
            object inputValue = InteropReflection.ReadMember(_seedInputString, null);
            seeded = seededValue is bool flag && flag;
            currentSeed = currentValue is int seed ? seed : 0;
            inputString = inputValue as string ?? string.Empty;
            return true;
        }

        /// <summary>覆写 QoL 种子状态（QoL_Unknown.SeedManager.CurrentSeed / InputString / IsSeeded）。</summary>
        internal bool TryWriteSeed(bool seeded, int currentSeed, string inputString)
        {
            if (!EnsureResolved() || _seedCurrentSeed == null || _seedIsSeeded == null)
            {
                return false;
            }

            bool written = InteropReflection.TryWriteMember(_seedCurrentSeed, null, currentSeed);
            if (_seedInputString != null)
            {
                written &= InteropReflection.TryWriteMember(_seedInputString, null, inputString ?? string.Empty);
            }

            written &= InteropReflection.TryWriteMember(_seedIsSeeded, null, seeded);
            return written;
        }

        /// <summary>读取 QoL 自动存档开关（QoL_Unknown.SaveManager.AutosaveEnabled）。</summary>
        internal bool TryReadAutosaveEnabled(out bool enabled)
        {
            enabled = false;
            if (!EnsureResolved() || _autosaveEnabled == null)
            {
                return false;
            }

            object value = InteropReflection.ReadMember(_autosaveEnabled, null);
            enabled = value is bool flag && flag;
            return true;
        }

        /// <summary>写入 QoL 自动存档开关（QoL_Unknown.SaveManager.AutosaveEnabled）。</summary>
        internal bool TryWriteAutosaveEnabled(bool enabled)
        {
            if (!EnsureResolved() || _autosaveEnabled == null)
            {
                return false;
            }

            return InteropReflection.TryWriteMember(_autosaveEnabled, null, enabled);
        }

        /// <summary>写入 QoL 读档待还原坐标（QoL_Unknown.SaveSystemPatcher.PendingLoadPosition）。</summary>
        internal bool TryWritePendingLoadPosition(Vector3 position)
        {
            if (!EnsureResolved() || _pendingLoadPosition == null)
            {
                return false;
            }

            Vector3? nullable = position;
            return InteropReflection.TryWriteMember(_pendingLoadPosition, null, nullable);
        }

        /// <summary>返回各依赖成员的解析状态摘要。</summary>
        internal string DescribeResolvedMembers()
        {
            EnsureResolved();
            return "assembly=" + State(_assembly)
                + " QoL_Unknown.SeedManager.IsSeeded=" + State(_seedIsSeeded)
                + " QoL_Unknown.SeedManager.CurrentSeed=" + State(_seedCurrentSeed)
                + " QoL_Unknown.SeedManager.InputString=" + State(_seedInputString)
                + " QoL_Unknown.SaveManager.AutosaveEnabled=" + State(_autosaveEnabled)
                + " QoL_Unknown.SaveManager.CurrentSaveFile=" + State(_currentSaveFile)
                + " QoL_Unknown.SaveSystemPatcher.PendingLoadPosition=" + State(_pendingLoadPosition)
                + " QoL_Unknown.SaveSystemPatcher.IsAutosaveOperation=" + State(_isAutosaveOperation);
        }

        /// <inheritdoc/>
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

        /// <inheritdoc/>
        bool IInteropMemberAccess.TryReadStatic(Type type, string member, out object value)
        {
            value = null;
            return InteropReflection.TryReadMember(FindMember(type, member, true), null, out value);
        }

        /// <inheritdoc/>
        bool IInteropMemberAccess.TryWriteStatic(Type type, string member, object value)
        {
            return InteropReflection.TryWriteMember(FindMember(type, member, true), null, value);
        }

        /// <inheritdoc/>
        bool IInteropMemberAccess.TryReadInstance(object target, string member, out object value)
        {
            value = null;
            return target != null
                && InteropReflection.TryReadMember(FindMember(target.GetType(), member, false), target, out value);
        }

        /// <inheritdoc/>
        bool IInteropMemberAccess.TryWriteInstance(object target, string member, object value)
        {
            return target != null
                && InteropReflection.TryWriteMember(FindMember(target.GetType(), member, false), target, value);
        }

        /// <inheritdoc/>
        bool IInteropMemberAccess.TryInvokeStatic(Type type, string method, out object result, params object[] args)
        {
            object[] arguments = args ?? Array.Empty<object>();
            result = null;
            return InteropReflection.TryInvoke(InteropReflection.FindMethodByArgs(type, method, true, arguments), null, arguments, out result);
        }

        /// <inheritdoc/>
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

        /// <inheritdoc/>
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

        /// <inheritdoc/>
        bool IInteropWorldGenHook.IsGenerating => false;

        /// <inheritdoc/>
        bool IInteropWorldGenHook.TrySubscribeGeneration(Action<bool> handler) => false;

        private static MemberInfo FindMember(Type type, string name, bool staticOnly)
        {
            if (type == null || string.IsNullOrEmpty(name))
            {
                return null;
            }

            FieldInfo field = InteropReflection.FindField(type, name);
            if (field != null && field.IsStatic == staticOnly)
            {
                return field;
            }

            PropertyInfo property = InteropReflection.FindProperty(type, name);
            if (property == null)
            {
                return null;
            }

            MethodInfo accessor = property.GetGetMethod(true) ?? property.GetSetMethod(true);
            return accessor != null && accessor.IsStatic == staticOnly ? property : null;
        }

        private static string State(object member) => member == null ? "missing" : "ok";

        /// <summary>首次访问时解析目标程序集及依赖成员并缓存；程序集尚未加载时不缓存以便后续重试。</summary>
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

                _resolved = true;
            }

            return _available;
        }

        /// <summary>清空缓存并重新解析目标程序集成员。</summary>
        internal bool TryRefresh()
        {
            ModLog.Debug("QoL：TryRefresh 清空缓存并重新解析");
            lock (_sync)
            {
                _resolved = false;
                _available = false;
                _assembly = null;
                _seedManagerType = null;
                _saveManagerType = null;
                _saveSystemPatcherType = null;
                _seedIsSeeded = null;
                _seedCurrentSeed = null;
                _seedInputString = null;
                _autosaveEnabled = null;
                _pendingLoadPosition = null;
                _isAutosaveOperation = null;
                _currentSaveFile = null;
            }

            return EnsureResolved();
        }

        private bool Resolve(Assembly assembly, List<string> missing)
        {
            _assembly = assembly;
            _seedManagerType = InteropReflection.FindType(_assembly, SeedManagerTypeName);
            _saveManagerType = InteropReflection.FindType(_assembly, SaveManagerTypeName);
            _saveSystemPatcherType = InteropReflection.FindType(_assembly, SaveSystemPatcherTypeName);
            if (_seedManagerType == null && _saveSystemPatcherType == null)
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
            _isAutosaveOperation = _saveSystemPatcherType == null ? null : InteropReflection.FindField(_saveSystemPatcherType, "IsAutosaveOperation");
            _currentSaveFile = _saveManagerType == null ? null : InteropReflection.FindField(_saveManagerType, "CurrentSaveFile");

            bool available = _seedCurrentSeed != null || _pendingLoadPosition != null;
            ModLog.Debug("QoL：assembly='" + assembly.GetName().Name + "' available=" + available + " capabilities=["
                + (available ? InteropDiagnostics.FormatCapabilities(InteropCapability.MemberAccess | InteropCapability.WorldGenHook) : "") + "]"
                + " 已解析 " + CountResolved(_seedIsSeeded, _seedCurrentSeed, _seedInputString, _autosaveEnabled, _pendingLoadPosition, _isAutosaveOperation, _currentSaveFile) + " 个成员"
                + " SeedManager.IsSeeded=" + State(_seedIsSeeded)
                + " SeedManager.CurrentSeed=" + State(_seedCurrentSeed)
                + " SeedManager.InputString=" + State(_seedInputString)
                + " SaveManager.AutosaveEnabled=" + State(_autosaveEnabled)
                + " SaveManager.CurrentSaveFile=" + State(_currentSaveFile)
                + " SaveSystemPatcher.PendingLoadPosition=" + State(_pendingLoadPosition)
                + " SaveSystemPatcher.IsAutosaveOperation=" + State(_isAutosaveOperation)
                + " missing=[" + string.Join(", ", missing) + "]");
            return available;
        }

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

        private static Assembly FindTargetAssembly()
        {
            Assembly self = typeof(QolProvider).Assembly;
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Assembly candidate = assemblies[i];
                if (candidate == null || ReferenceEquals(candidate, self))
                {
                    continue;
                }

                string name = candidate.GetName().Name;
                if (!string.IsNullOrEmpty(name) && name.IndexOf(TargetAssemblyNamePart, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return candidate;
                }
            }

            return null;
        }

        private static FieldInfo Require(Type type, string member, List<string> missing)
        {
            FieldInfo field = type == null ? null : InteropReflection.FindField(type, member);
            if (field == null)
            {
                missing.Add(type == null ? "<type>." + member : type.Name + "." + member);
            }

            return field;
        }
    }
}
