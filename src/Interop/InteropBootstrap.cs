// CuSaveManager Interop
// Copyright (c) 2026 huanxin886
// SPDX-License-Identifier: MIT

#if DEBUG
using System.Collections.Generic;
#endif

namespace CasualtiesUnknown.SaveManager.Interop
{
    /// <summary>管理互操作登记表与目标模组提供方单例，首次初始化时输出一次自检摘要。</summary>
    internal static class InteropBootstrap
    {
        // 所对接外部模组均为可选软依赖，不将缺失项记入启动告警。
        private const InteropCapability RequiredCapabilities = InteropCapability.None;

        private static readonly object Sync = new object();

        private static volatile IInteropRegistry _registry;
        private static TogetherProvider _together;
        private static QolProvider _qol;
        private static LayerUnlockProvider _layerUnlock;

        /// <summary>所有 BepInEx 插件 Awake 是否已完成；为 true 后未检出的可选模组可缓存负向结果。</summary>
        internal static bool PluginScanComplete { get; private set; }

        /// <summary>标记插件加载阶段已结束，允许各提供方缓存“目标模组未安装”结论以消除后续重复扫描。</summary>
        internal static void MarkPluginScanComplete() => PluginScanComplete = true;

        /// <summary>首次调用时创建登记表、注册提供方并输出自检摘要；后续调用返回同一实例。</summary>
        internal static IInteropRegistry Initialize()
        {
            if (_registry != null)
            {
                return _registry;
            }

            IInteropRegistry created = null;
            lock (Sync)
            {
                if (_registry == null)
                {
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
                    created = registry;
                }
            }

            if (created != null)
            {
                InteropDiagnostics.Report(created, RequiredCapabilities);
#if DEBUG
                LogProvidersForDebug(created, RequiredCapabilities);
#endif
            }

            return _registry;
        }

#if DEBUG
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

        /// <summary>Casualties: Together 提供方。</summary>
        internal static TogetherProvider Together
        {
            get
            {
                Initialize();
                return _together;
            }
        }

        /// <summary>QoL.Unknown 提供方。</summary>
        internal static QolProvider Qol
        {
            get
            {
                Initialize();
                return _qol;
            }
        }

        /// <summary>LayerUnlock 提供方。</summary>
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
