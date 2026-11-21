// CuSaveManager Interop
// Copyright (c) 2026 huanxin886
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Bootstrap;

namespace CasualtiesUnknown.SaveManager.Interop
{
    /// <summary>RemiyamuremodLayerUnlock 反射提供方，解析世界生成协程状态机的 MoveNext()。</summary>
    /// <remarks>
    /// 目标程序集含 "LayerUnlock"（RemiyamuremodLayerUnlock.TerrainPatch / StructuresPatch / EntitiesPatch）。
    /// 优先从声明方法的 <see cref="IteratorStateMachineAttribute"/> 或嵌套类型前缀解析状态机，未命中再回退已知全名。
    /// </remarks>
    internal sealed class LayerUnlockProvider : IInteropProvider, IInteropTypeRegistry
    {
        private const string TargetAssemblyNamePart = "LayerUnlock";

        private const string TerrainPatchTypeName = "RemiyamuremodLayerUnlock.TerrainPatch";
        private const string StructuresPatchTypeName = "RemiyamuremodLayerUnlock.StructuresPatch";
        private const string EntitiesPatchTypeName = "RemiyamuremodLayerUnlock.EntitiesPatch";

        private const BindingFlags AllDeclared =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        private readonly struct CoroutineTarget
        {
            internal readonly int PatchSlot;
            internal readonly string MethodName;
            internal readonly string FallbackFullName;

            internal CoroutineTarget(int patchSlot, string methodName, string fallbackFullName)
            {
                PatchSlot = patchSlot;
                MethodName = methodName;
                FallbackFullName = fallbackFullName;
            }
        }

        private static readonly CoroutineTarget[] Targets =
        {
            new CoroutineTarget(0, "GenerateStructuresAfterTerrain", "RemiyamuremodLayerUnlock.TerrainPatch+<GenerateStructuresAfterTerrain>d__2"),
            new CoroutineTarget(1, "AddStructuresForDepth", "RemiyamuremodLayerUnlock.StructuresPatch+<AddStructuresForDepth>d__1"),
            new CoroutineTarget(2, "SpawnIonHeaters", "RemiyamuremodLayerUnlock.EntitiesPatch+<SpawnIonHeaters>d__3"),
            new CoroutineTarget(2, "SpawnSpaceHeaters", "RemiyamuremodLayerUnlock.EntitiesPatch+<SpawnSpaceHeaters>d__4"),
        };

        private readonly object _sync = new object();

        // 双检锁标志：锁外读取需 acquire 语义以确保已解析成员字段可见。
        private volatile bool _resolved;
        private volatile bool _available;

        private Assembly _assembly;
        private Type _terrainPatchType;
        private Type _structuresPatchType;
        private Type _entitiesPatchType;

        private MethodInfo[] _resolvedBySlot = new MethodInfo[Targets.Length];
        private Type[] _stateMachineTypes = Array.Empty<Type>();
        private MethodInfo[] _moveNextMethods = Array.Empty<MethodInfo>();

        /// <inheritdoc/>
        string IInteropProvider.Name => "LayerUnlock";

        /// <inheritdoc/>
        bool IInteropProvider.IsAvailable => EnsureResolved();

        /// <inheritdoc/>
        InteropCapability IInteropProvider.Capabilities => InteropCapability.None;

        /// <inheritdoc/>
        int IInteropProvider.Priority => 0;

        /// <summary>目标插件是否在场且至少解析到一个目标协程状态机。</summary>
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

        /// <summary>已解析到的协程状态机类型列表。</summary>
        internal IReadOnlyList<Type> StateMachineTypes
        {
            get
            {
                EnsureResolved();
                return _stateMachineTypes;
            }
        }

        /// <summary>与 <see cref="StateMachineTypes"/> 一一对应的 MoveNext() 方法列表。</summary>
        internal IReadOnlyList<MethodInfo> MoveNextMethods
        {
            get
            {
                EnsureResolved();
                return _moveNextMethods;
            }
        }

        /// <summary>按协程方法名获取已解析的状态机 MoveNext()。</summary>
        internal bool TryGetMoveNext(string coroutineName, out MethodInfo moveNext)
        {
            moveNext = null;
            if (string.IsNullOrEmpty(coroutineName))
            {
                return false;
            }

            EnsureResolved();
            if (_assembly == null)
            {
                return false;
            }

            for (int i = 0; i < Targets.Length; i++)
            {
                if (string.Equals(Targets[i].MethodName, coroutineName, StringComparison.Ordinal))
                {
                    moveNext = _resolvedBySlot[i];
                    return moveNext != null;
                }
            }

            return false;
        }

        /// <summary>返回依赖类型与协程状态机的解析状态摘要。</summary>
        internal string DescribeResolvedMembers()
        {
            EnsureResolved();
            return "assembly=" + State(_assembly)
                + " " + TerrainPatchTypeName + "=" + State(_terrainPatchType)
                + " " + StructuresPatchTypeName + "=" + State(_structuresPatchType)
                + " " + EntitiesPatchTypeName + "=" + State(_entitiesPatchType)
                + " " + DescribeCoroutines();
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

        private static string State(object member) => member == null ? "missing" : "ok";

        private static Assembly FindTargetAssembly()
        {
            try
            {
                if (Chainloader.PluginInfos != null)
                {
                    foreach (KeyValuePair<string, PluginInfo> pair in Chainloader.PluginInfos)
                    {
                        PluginInfo info = pair.Value;
                        if (info?.Instance == null)
                        {
                            continue;
                        }

                        string guid = pair.Key;
                        string metaName = info.Metadata?.Name;
                        bool matched = (!string.IsNullOrEmpty(guid) && guid.IndexOf(TargetAssemblyNamePart, StringComparison.OrdinalIgnoreCase) >= 0)
                            || (!string.IsNullOrEmpty(metaName) && metaName.IndexOf(TargetAssemblyNamePart, StringComparison.OrdinalIgnoreCase) >= 0);
                        if (!matched)
                        {
                            continue;
                        }

                        Assembly pluginAsm = info.Instance.GetType().Assembly;
                        if (InteropReflection.FindType(pluginAsm, TerrainPatchTypeName) != null)
                        {
                            return pluginAsm;
                        }
                    }
                }
            }
            catch (Exception)
            {
            }

            Assembly self = typeof(LayerUnlockProvider).Assembly;
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
                    && InteropReflection.FindType(candidate, TerrainPatchTypeName) != null)
                {
                    return candidate;
                }
            }

            return null;
        }

        private Type GetDeclaringPatchType(int patchSlot)
        {
            switch (patchSlot)
            {
                case 0: return _terrainPatchType;
                case 1: return _structuresPatchType;
                default: return _entitiesPatchType;
            }
        }

        private static Type FindStateMachineOnDeclaringType(Type declaringType, string methodName)
        {
            if (declaringType == null || string.IsNullOrEmpty(methodName))
            {
                return null;
            }

            try
            {
                MethodInfo[] methods = declaringType.GetMethods(AllDeclared);
                for (int i = 0; i < methods.Length; i++)
                {
                    if (methods[i].Name != methodName)
                    {
                        continue;
                    }

                    IteratorStateMachineAttribute attr = methods[i].GetCustomAttribute<IteratorStateMachineAttribute>(false);
                    if (attr?.StateMachineType != null)
                    {
                        return attr.StateMachineType;
                    }
                }
            }
            catch
            {
            }

            try
            {
                string prefix = "<" + methodName + ">d__";
                Type[] nestedTypes = declaringType.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic);
                for (int i = 0; i < nestedTypes.Length; i++)
                {
                    if (nestedTypes[i].Name.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        return nestedTypes[i];
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        private static MethodInfo ResolveCoroutineMoveNext(
            Assembly assembly,
            Type declaringType,
            CoroutineTarget target,
            out Type stateMachineType)
        {
            stateMachineType = FindStateMachineOnDeclaringType(declaringType, target.MethodName)
                ?? InteropReflection.FindType(assembly, target.FallbackFullName);
            if (stateMachineType == null)
            {
                return null;
            }

            MethodInfo moveNext = InteropReflection.FindMethod(stateMachineType, "MoveNext");
            if (moveNext == null)
            {
                stateMachineType = null;
            }

            return moveNext;
        }

        private string DescribeCoroutines()
        {
            string[] parts = new string[Targets.Length];
            for (int i = 0; i < Targets.Length; i++)
            {
                parts[i] = Targets[i].MethodName + ".MoveNext=" + State(_resolvedBySlot[i]);
            }

            return string.Join(" ", parts);
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

        /// <summary>首次访问时解析目标程序集与协程状态机；插件扫描完成后对未安装模组缓存负向结果。</summary>
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
                    if (InteropBootstrap.PluginScanComplete)
                    {
                        _available = false;
                        _resolved = true;
                    }
                    return false;
                }

                _assembly = assembly;
                _terrainPatchType = InteropReflection.FindType(assembly, TerrainPatchTypeName);
                _structuresPatchType = InteropReflection.FindType(assembly, StructuresPatchTypeName);
                _entitiesPatchType = InteropReflection.FindType(assembly, EntitiesPatchTypeName);

                MethodInfo[] bySlot = new MethodInfo[Targets.Length];
                List<Type> types = new List<Type>(Targets.Length);
                List<MethodInfo> methods = new List<MethodInfo>(Targets.Length);
                List<string> missing = new List<string>();
                for (int i = 0; i < Targets.Length; i++)
                {
                    CoroutineTarget target = Targets[i];
                    MethodInfo moveNext = ResolveCoroutineMoveNext(
                        assembly,
                        GetDeclaringPatchType(target.PatchSlot),
                        target,
                        out Type stateMachine);
                    bySlot[i] = moveNext;
                    if (moveNext == null)
                    {
                        missing.Add(target.MethodName);
                        continue;
                    }

                    types.Add(stateMachine);
                    methods.Add(moveNext);
                }

                _resolvedBySlot = bySlot;
                _stateMachineTypes = types.ToArray();
                _moveNextMethods = methods.ToArray();
                _available = methods.Count > 0;

                if (!_available)
                {
                    InteropDiagnostics.WarnOnce(
                        "LayerUnlockProvider.Unavailable",
                        "LayerUnlock 已加载但未解析到任何目标协程状态机，层解锁世界种子确定化不生效");
                }
                else if (missing.Count > 0)
                {
                    InteropDiagnostics.WarnOnce("LayerUnlockProvider", "部分协程状态机未解析到：" + string.Join(", ", missing));
                }

                ModLog.Debug("LayerUnlock：assembly='" + assembly.GetName().Name + "' available=" + _available
                    + " capabilities=[]"
                    + " 已解析 " + (CountResolved(_terrainPatchType, _structuresPatchType, _entitiesPatchType) + methods.Count) + " 个成员"
                    + " " + TerrainPatchTypeName + "=" + State(_terrainPatchType)
                    + " " + StructuresPatchTypeName + "=" + State(_structuresPatchType)
                    + " " + EntitiesPatchTypeName + "=" + State(_entitiesPatchType)
                    + " " + DescribeCoroutines()
                    + " missing=[" + string.Join(", ", missing) + "]");

                _resolved = true;
            }

            return _available;
        }
    }
}
