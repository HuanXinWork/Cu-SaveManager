// CuSaveManager Interop
// Copyright (c) 2026 huanxin886
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using UnityEngine;

namespace CasualtiesUnknown.SaveManager.Interop
{
    /// <summary>QoL.Unknown 反射提供方，对接世界种子、自动存档状态与读档坐标恢复字段。</summary>
    /// <remarks>优先按插件 GUID "org.bepinex.plugins.qol_unknown" 定位，回退按核心标志类型验证。</remarks>
    internal sealed class QolProvider : IInteropProvider, IInteropTypeRegistry
    {
        private const string PluginGuid = "org.bepinex.plugins.qol_unknown";
        private const string TargetAssemblyNamePart = "qol";

        private const string SeedManagerTypeName = "QoL_Unknown.SeedManager";
        private const string SaveManagerTypeName = "QoL_Unknown.SaveManager";
        private const string SaveSystemPatcherTypeName = "QoL_Unknown.SaveSystemPatcher";
        private const string SaveSaveMenuPatcherTypeName = "QoL_Unknown.SaveSaveMenuPatcher";

        private bool _resolved;
        private bool _available;
        private bool _initialAutosaveCaptured;
        private bool _initialAutosaveValue;

        private Assembly _assembly;
        private Type _seedManagerType;
        private Type _saveManagerType;
        private Type _saveSystemPatcherType;
        private Type _saveSaveMenuPatcherType;

        private FieldInfo _seedIsSeeded;
        private FieldInfo _seedCurrentSeed;
        private FieldInfo _seedInputString;
        private FieldInfo _autosaveEnabled;
        private FieldInfo _pendingLoadPosition;
        private FieldInfo _isAutosaveOperation;
        private FieldInfo _currentSaveFile;
        private FieldInfo _layerBackgrounds;

        /// <inheritdoc/>
        string IInteropProvider.Name => "QoL";

        /// <inheritdoc/>
        bool IInteropProvider.IsAvailable => EnsureResolved();

        /// <inheritdoc/>
        InteropCapability IInteropProvider.Capabilities => InteropCapability.TypeRegistry;

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

        /// <summary>写入 QoL 自动存档开关（QoL_Unknown.SaveManager.AutosaveEnabled），首次介入前快照原始状态。</summary>
        internal bool TryWriteAutosaveEnabled(bool enabled)
        {
            if (!EnsureResolved() || _autosaveEnabled == null)
            {
                return false;
            }

            if (!_initialAutosaveCaptured
                && InteropReflection.ReadMember(_autosaveEnabled, null) is bool current)
            {
                _initialAutosaveValue = current;
                _initialAutosaveCaptured = true;
            }

            return InteropReflection.TryWriteMember(_autosaveEnabled, null, enabled);
        }

        /// <summary>若曾修改过 QoL 自动存档开关，将其恢复为首次介入前捕获的初始值。</summary>
        internal void RestoreInitialAutosaveState()
        {
            if (!EnsureResolved() || _autosaveEnabled == null || !_initialAutosaveCaptured)
            {
                return;
            }

            if (InteropReflection.TryWriteMember(_autosaveEnabled, null, _initialAutosaveValue))
            {
                _initialAutosaveCaptured = false;
            }
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

        /// <summary>清空 QoL 读档待还原坐标（QoL_Unknown.SaveSystemPatcher.PendingLoadPosition = null）。</summary>
        internal void ClearPendingLoadPosition()
        {
            if (!EnsureResolved() || _pendingLoadPosition == null)
            {
                return;
            }

            InteropReflection.TryWriteMember(_pendingLoadPosition, null, null);
        }

        /// <summary>按 0-based 层索引读取 QoL（含 LayerUnlock 动态注册）的层级背景横幅精灵；不缓存未命中项以兼容运行时延迟注册。</summary>
        internal bool TryGetLayerBackgroundSprite(int biomeIndex, out Sprite sprite)
        {
            sprite = null;
            if (biomeIndex < 0 || !EnsureResolved() || _layerBackgrounds == null)
            {
                return false;
            }

            object raw = InteropReflection.ReadMember(_layerBackgrounds, null);
            if (raw == null) return false;

            if (raw is Dictionary<int, Sprite> typedDict)
            {
                return typedDict.TryGetValue(biomeIndex, out sprite) && sprite != null && sprite.texture != null;
            }

            if (raw is Sprite[] spriteArray)
            {
                if (biomeIndex < spriteArray.Length)
                {
                    sprite = spriteArray[biomeIndex];
                    return sprite != null && sprite.texture != null;
                }
                return false;
            }

            if (raw is System.Collections.IDictionary untypedDict && untypedDict.Contains(biomeIndex))
            {
                sprite = untypedDict[biomeIndex] as Sprite;
                return sprite != null && sprite.texture != null;
            }

            return false;
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
                + " QoL_Unknown.SaveSystemPatcher.IsAutosaveOperation=" + State(_isAutosaveOperation)
                + " QoL_Unknown.SaveSaveMenuPatcher.layerBackgrounds=" + State(_layerBackgrounds);
        }

        /// <summary>在目标程序集内按全名解析类型；未加载或未找到时返回 null。</summary>
        internal Type TryResolveType(string typeName)
        {
            EnsureResolved();
            return _assembly == null || string.IsNullOrEmpty(typeName)
                ? null
                : InteropReflection.FindType(_assembly, typeName);
        }

        /// <inheritdoc/>
        bool IInteropTypeRegistry.TryResolveType(string fullName, out Type type)
        {
            type = TryResolveType(fullName);
            return type != null;
        }

        private static string State(object member) => member == null ? "missing" : "ok";

        /// <summary>首次访问时解析目标程序集及依赖成员并缓存；插件扫描完成后对未安装模组缓存负向结果。</summary>
        private bool EnsureResolved()
        {
            if (_resolved)
            {
                return _available;
            }

            Assembly assembly = FindTargetAssembly();
            if (assembly == null)
            {
                if (InteropBootstrap.PluginScanComplete)
                {
                    _available = false;
                    _resolved = true;
                }
                return false;
            }

            List<string> missing = new List<string>();
            _available = Resolve(assembly, missing);
            if (missing.Count > 0)
            {
                InteropDiagnostics.WarnOnce("QolProvider", "目标程序集成员缺失：" + string.Join(", ", missing));
            }

            _resolved = true;
            return _available;
        }

        private bool Resolve(Assembly assembly, List<string> missing)
        {
            _assembly = assembly;
            _seedManagerType = InteropReflection.FindType(_assembly, SeedManagerTypeName);
            _saveManagerType = InteropReflection.FindType(_assembly, SaveManagerTypeName);
            _saveSystemPatcherType = InteropReflection.FindType(_assembly, SaveSystemPatcherTypeName);
            _saveSaveMenuPatcherType = InteropReflection.FindType(_assembly, SaveSaveMenuPatcherTypeName);
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
            _layerBackgrounds = _saveSaveMenuPatcherType == null ? null : InteropReflection.FindField(_saveSaveMenuPatcherType, "layerBackgrounds");

            bool available = _seedCurrentSeed != null || _pendingLoadPosition != null;
            ModLog.Debug("QoL：assembly='" + assembly.GetName().Name + "' available=" + available + " capabilities=["
                + (available ? InteropDiagnostics.FormatCapabilities(InteropCapability.TypeRegistry) : "") + "]"
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
            try
            {
                if (Chainloader.PluginInfos != null
                    && Chainloader.PluginInfos.TryGetValue(PluginGuid, out PluginInfo pluginInfo)
                    && pluginInfo?.Instance != null)
                {
                    Assembly pluginAsm = pluginInfo.Instance.GetType().Assembly;
                    if (HasCoreMarkerType(pluginAsm))
                    {
                        return pluginAsm;
                    }
                }
            }
            catch (Exception)
            {
            }

            Assembly self = typeof(QolProvider).Assembly;
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Assembly candidate = assemblies[i];
                if (candidate == null || ReferenceEquals(candidate, self))
                {
                    continue;
                }

                string name;
                try { name = candidate.GetName().Name; }
                catch (Exception) { continue; }

                if (!string.IsNullOrEmpty(name)
                    && name.IndexOf(TargetAssemblyNamePart, StringComparison.OrdinalIgnoreCase) >= 0
                    && HasCoreMarkerType(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        private static bool HasCoreMarkerType(Assembly candidate)
        {
            return candidate != null
                && (InteropReflection.FindType(candidate, SeedManagerTypeName) != null
                    || InteropReflection.FindType(candidate, SaveSystemPatcherTypeName) != null);
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
