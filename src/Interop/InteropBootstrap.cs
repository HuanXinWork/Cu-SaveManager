// CuSaveManager Interop
// Copyright (c) 2026 huanxin886
// SPDX-License-Identifier: MIT

using System.Collections.Generic;

namespace CasualtiesUnknown.SaveManager.Interop
{
    /// <summary>探测目标插件、登记提供方并输出一次自检摘要，重复调用幂等。</summary>
    /// <remarks>
    /// CuSaveManager 登记三个提供方：TogetherProvider（联机会话、名册、通道、事件、类型解析）、
    /// QolProvider（跨程序集成员访问、类型解析、世界种子）、LayerUnlockProvider（LayerUnlock 协程状态机解析）。
    /// 新增对接目标时在此追加提供方，并同步 <see cref="RequiredCapabilities"/>。
    /// 业务侧优先消费契约接口；契约未覆盖的联机模组专有能力通过 <see cref="Together"/> 取用。
    /// </remarks>
    internal static class InteropBootstrap
    {
        /// <summary>本 mod 消费的能力位。</summary>
        private const InteropCapability RequiredCapabilities =
            InteropCapability.Session
            | InteropCapability.Roster
            | InteropCapability.TypeRegistry
            | InteropCapability.MemberAccess
            | InteropCapability.WorldGenHook;

        private static readonly object Sync = new object();

        private static IInteropRegistry _registry;
        private static TogetherProvider _together;
        private static QolProvider _qol;
        private static LayerUnlockProvider _layerUnlock;

        /// <summary>首次调用时创建登记表、登记提供方并输出自检摘要；后续调用返回同一实例。</summary>
        internal static IInteropRegistry Initialize()
        {
            if (_registry != null)
            {
                return _registry;
            }

            lock (Sync)
            {
                if (_registry != null)
                {
                    return _registry;
                }

                TogetherProvider together = new TogetherProvider();
                QolProvider qol = new QolProvider();
                LayerUnlockProvider layerUnlock = new LayerUnlockProvider();

                IInteropRegistry registry = new InteropRegistry();
                registry.Register(together);
                registry.Register(qol);
                registry.Register(layerUnlock);

                _together = together;
                _qol = qol;
                _layerUnlock = layerUnlock;
                _registry = registry;
                InteropDiagnostics.Report(registry, RequiredCapabilities);
#if DEBUG
                LogProvidersForDebug(registry, RequiredCapabilities);
#endif
                return _registry;
            }
        }

#if DEBUG
        /// <summary>Debug 构建下逐提供方输出可用性 / 能力集 / 未满足的必需能力；只随首次 Initialize 记录一次，不会逐帧刷屏。</summary>
        private static void LogProvidersForDebug(IInteropRegistry registry, InteropCapability required)
        {
            IReadOnlyList<IInteropProvider> providers = registry.Providers;
            for (int i = 0; i < providers.Count; i++)
            {
                IInteropProvider provider = providers[i];
                if (provider == null)
                {
                    continue;
                }

                bool available = provider.IsAvailable;
                InteropCapability granted = available ? provider.Capabilities : InteropCapability.None;
                ModLog.Debug("Interop：provider=" + provider.Name
                    + " available=" + available
                    + " capabilities=[" + InteropDiagnostics.FormatCapabilities(granted) + "]"
                    + " missing=[" + InteropDiagnostics.FormatCapabilities(provider.Capabilities & required & ~granted) + "]");
            }
        }
#endif

        /// <summary>已初始化的登记表；未初始化时返回空实现。</summary>
        internal static IInteropRegistry Current => _registry ?? NullInteropRegistry.Instance;

        /// <summary>联机模组提供方；首次访问时完成登记表的初始化。</summary>
        internal static TogetherProvider Together
        {
            get
            {
                Initialize();
                return _together;
            }
        }

        /// <summary>QoL.Unknown 提供方；首次访问时完成登记表的初始化。</summary>
        internal static QolProvider Qol
        {
            get
            {
                Initialize();
                return _qol;
            }
        }

        /// <summary>LayerUnlock 提供方；首次访问时完成登记表的初始化。</summary>
        internal static LayerUnlockProvider LayerUnlock
        {
            get
            {
                Initialize();
                return _layerUnlock;
            }
        }
    }
}
