// CuSaveManager Interop
// Copyright (c) 2026 huanxin886
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Reflection;

namespace CasualtiesUnknown.SaveManager.Interop
{
    /// <summary>RemiyamuremodLayerUnlock 的提供方适配器，全程通过反射访问目标程序集。</summary>
    /// <remarks>
    /// 目标程序集：程序集简单名含 "LayerUnlock"（忽略大小写）的插件程序集。
    /// 依赖的目标类型：RemiyamuremodLayerUnlock.TerrainPatch、RemiyamuremodLayerUnlock.StructuresPatch、RemiyamuremodLayerUnlock.EntitiesPatch。
    /// 依赖的目标成员：上述三个类型里 4 个协程的编译器生成状态机的 MoveNext()（bool MoveNext()），状态机全名如下：
    /// RemiyamuremodLayerUnlock.TerrainPatch+&lt;GenerateStructuresAfterTerrain&gt;d__2、
    /// RemiyamuremodLayerUnlock.StructuresPatch+&lt;AddStructuresForDepth&gt;d__1、
    /// RemiyamuremodLayerUnlock.EntitiesPatch+&lt;SpawnIonHeaters&gt;d__3、
    /// RemiyamuremodLayerUnlock.EntitiesPatch+&lt;SpawnSpaceHeaters&gt;d__4。
    /// 状态机类型名里的编号由编译器生成，目标插件重新编译后可能变化；因此先在声明的类型上按名称找协程，再退回到全名解析。
    /// 目标程序集尚未加载时不计为不可用缓存，下次访问重试。
    /// </remarks>
    internal sealed class LayerUnlockProvider : IInteropProvider, IInteropTypeRegistry
    {
        private const string TargetAssemblyNamePart = "LayerUnlock";

        private const string TerrainPatchTypeName = "RemiyamuremodLayerUnlock.TerrainPatch";
        private const string StructuresPatchTypeName = "RemiyamuremodLayerUnlock.StructuresPatch";
        private const string EntitiesPatchTypeName = "RemiyamuremodLayerUnlock.EntitiesPatch";

        /// <summary>协程名与承载它的声明类型全名，按顺序对应 4 个需要确定化的协程。</summary>
        private static readonly string[,] CoroutineTypeNames =
        {
            { "GenerateStructuresAfterTerrain", "RemiyamuremodLayerUnlock.TerrainPatch+<GenerateStructuresAfterTerrain>d__2" },
            { "AddStructuresForDepth", "RemiyamuremodLayerUnlock.StructuresPatch+<AddStructuresForDepth>d__1" },
            { "SpawnIonHeaters", "RemiyamuremodLayerUnlock.EntitiesPatch+<SpawnIonHeaters>d__3" },
            { "SpawnSpaceHeaters", "RemiyamuremodLayerUnlock.EntitiesPatch+<SpawnSpaceHeaters>d__4" },
        };

        private readonly object _sync = new object();

        // 标志在锁内写、锁外读（EnsureResolved 的双检锁），必须带 volatile：
        // 锁外读取只做 acquire，否则看到 _resolved 为 true 时其余成员字段仍可能不可见。
        private volatile bool _resolved;
        private volatile bool _available;

        private Assembly _assembly;
        private Type _terrainPatchType;
        private Type _structuresPatchType;
        private Type _entitiesPatchType;

        private Type[] _stateMachineTypes = Array.Empty<Type>();
        private MethodInfo[] _moveNextMethods = Array.Empty<MethodInfo>();

        /// <inheritdoc/>
        string IInteropProvider.Name => "LayerUnlock";

        /// <inheritdoc/>
        bool IInteropProvider.IsAvailable => EnsureResolved();

        /// <inheritdoc/>
        InteropCapability IInteropProvider.Capabilities => InteropCapability.TypeRegistry;

        /// <inheritdoc/>
        int IInteropProvider.Priority => 0;

        /// <summary>目标插件是否在场。</summary>
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

        /// <summary>已解析到的协程状态机类型，缺失的协程不占位。</summary>
        internal IReadOnlyList<Type> StateMachineTypes
        {
            get
            {
                EnsureResolved();
                return _stateMachineTypes;
            }
        }

        /// <summary>与 <see cref="StateMachineTypes"/> 一一对应的 MoveNext() 方法，缺失的协程不占位。</summary>
        /// <remarks>挂 prefix / postfix 由调用方用自己的 Harmony 实例完成，本提供方只负责解析。</remarks>
        internal IReadOnlyList<MethodInfo> MoveNextMethods
        {
            get
            {
                EnsureResolved();
                return _moveNextMethods;
            }
        }

        /// <summary>按协程名取状态机的 MoveNext()；未解析到时返回 false。</summary>
        internal bool TryGetMoveNext(string coroutineName, out MethodInfo moveNext)
        {
            moveNext = null;
            EnsureResolved();
            for (int i = 0; i < CoroutineTypeNames.GetLength(0); i++)
            {
                if (!string.Equals(CoroutineTypeNames[i, 0], coroutineName, StringComparison.Ordinal))
                {
                    continue;
                }

                Type stateMachine = InteropReflection.FindType(_assembly, CoroutineTypeNames[i, 1]);
                if (stateMachine == null)
                {
                    return false;
                }

                moveNext = InteropReflection.FindMethod(stateMachine, "MoveNext");
                return moveNext != null;
            }

            return false;
        }

        /// <summary>一行摘要，列出各依赖成员的解析结果，用于启动日志与版本变动时的排查。</summary>
        internal string DescribeResolvedMembers()
        {
            EnsureResolved();
            string[] names = new string[CoroutineTypeNames.GetLength(0)];
            for (int i = 0; i < names.Length; i++)
            {
                names[i] = CoroutineTypeNames[i, 0] + ".MoveNext=" + State(ResolveMoveNext(CoroutineTypeNames[i, 1]));
            }

            return "assembly=" + State(_assembly)
                + " " + TerrainPatchTypeName + "=" + State(_terrainPatchType)
                + " " + StructuresPatchTypeName + "=" + State(_structuresPatchType)
                + " " + EntitiesPatchTypeName + "=" + State(_entitiesPatchType)
                + " " + string.Join(" ", names);
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

        /// <summary>把成员解析结果格式化为 ok / missing。</summary>
        private static string State(object member)
        {
            return member == null ? "missing" : "ok";
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

        private MethodInfo ResolveMoveNext(string stateMachineFullName)
        {
            Type stateMachine = InteropReflection.FindType(_assembly, stateMachineFullName);
            return stateMachine == null ? null : InteropReflection.FindMethod(stateMachine, "MoveNext");
        }

        /// <summary>Debug 构建下把 4 个协程的 MoveNext 解析结果拼成一行；Release 中调用点被整体移除。</summary>
        private string DescribeCoroutines()
        {
            string[] parts = new string[CoroutineTypeNames.GetLength(0)];
            for (int i = 0; i < parts.Length; i++)
            {
                parts[i] = CoroutineTypeNames[i, 0] + ".MoveNext=" + State(ResolveMoveNext(CoroutineTypeNames[i, 1]));
            }

            return string.Join(" ", parts);
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

        /// <summary>首次访问时解析目标程序集与 4 个状态机，并在逻辑上标记已解析；目标程序集尚未加载时不置缓存，下次访问重试。</summary>
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

                _assembly = assembly;
                _terrainPatchType = InteropReflection.FindType(assembly, TerrainPatchTypeName);
                _structuresPatchType = InteropReflection.FindType(assembly, StructuresPatchTypeName);
                _entitiesPatchType = InteropReflection.FindType(assembly, EntitiesPatchTypeName);
                _available = _terrainPatchType != null || _structuresPatchType != null || _entitiesPatchType != null;
                if (!_available)
                {
                    InteropDiagnostics.WarnOnce(
                        "LayerUnlockProvider.Unavailable",
                        "LayerUnlock 程序集未在场（" + TerrainPatchTypeName + "/" + StructuresPatchTypeName + "/" + EntitiesPatchTypeName + " 均未解析到），层解锁世界的种子确定化不生效");
                }

                List<Type> types = new List<Type>();
                List<MethodInfo> methods = new List<MethodInfo>();
                List<string> missing = new List<string>();
                for (int i = 0; i < CoroutineTypeNames.GetLength(0); i++)
                {
                    string stateMachineName = CoroutineTypeNames[i, 1];
                    Type stateMachine = InteropReflection.FindType(assembly, stateMachineName);
                    MethodInfo moveNext = stateMachine == null ? null : InteropReflection.FindMethod(stateMachine, "MoveNext");
                    if (moveNext == null)
                    {
                        missing.Add(stateMachineName);
                        continue;
                    }

                    types.Add(stateMachine);
                    methods.Add(moveNext);
                }

                _stateMachineTypes = types.ToArray();
                _moveNextMethods = methods.ToArray();
                if (missing.Count > 0)
                {
                    InteropDiagnostics.WarnOnce("LayerUnlockProvider", "协程状态机未解析到：" + string.Join(", ", missing));
                }

                ModLog.Debug("LayerUnlock：assembly='" + assembly.GetName().Name + "' available=" + _available
                    + " capabilities=[" + (_available ? InteropDiagnostics.FormatCapabilities(InteropCapability.TypeRegistry) : "") + "]"
                    + " 已解析 " + (CountResolved(_terrainPatchType, _structuresPatchType, _entitiesPatchType) + methods.Count) + " 个成员"
                    + " " + TerrainPatchTypeName + "=" + State(_terrainPatchType)
                    + " " + StructuresPatchTypeName + "=" + State(_structuresPatchType)
                    + " " + EntitiesPatchTypeName + "=" + State(_entitiesPatchType)
                    + " " + DescribeCoroutines()
                    + " missing=[" + string.Join(", ", missing) + "]");

                // 标志最后发布：锁外的读者看到 _resolved 为 true 时，此前写入的类型数组与方法数组均已可见。
                _resolved = true;
            }

            return _available;
        }
    }
}
