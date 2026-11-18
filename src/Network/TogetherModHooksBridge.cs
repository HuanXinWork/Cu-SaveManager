using System;
using System.Collections.Generic;
using System.Reflection;
using CasualtiesUnknown.SaveManager.Interop;
using UnityEngine;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>软依赖对接 Together 官方 <c>Together.ModHooks</c>、<c>TogetherPanelApi</c> 与 <c>GamemodeManager</c>，未提供时平滑回退兼容补丁。</summary>
    internal static class TogetherModHooksBridge
    {
        private const string ExternalBlockerModId = "com.casualtiesUnknown.saveManager";

        private static bool _bindResolved;
        private static bool _gamemodeResolved;
        private static MethodInfo _mHasGamemode;

        private static MemberInfo _mAllowSpPassthrough;
        private static MemberInfo _mRestoreSavedPositions;
        private static MemberInfo _mCustomWorldSeedProvider;
        private static MemberInfo _mSpawnPositionOverride;
        private static MemberInfo _mShouldBlockAutoExit;
        private static EventInfo _eWorldSeedApplied;
        private static Delegate _dWorldSeedApplied;
        private static MethodInfo _mUnregisterExternalInputBlocker;

        // 保活已绑定给 Together 静态成员的委托，防止跨程序集弱引用回收。
        private static readonly List<Delegate> PinnedDelegates = new List<Delegate>();

        internal static bool HasSingleplayerPassthrough { get; private set; }
        internal static bool HasWorldSeedHook { get; private set; }
        internal static bool HasSpawnPositionHook { get; private set; }
        internal static bool HasAutoExitGuardHook { get; private set; }
        internal static bool HasExternalInputBlocker { get; private set; }

        /// <summary>尝试解析并绑定 <c>Together.ModHooks</c> 与 <c>TogetherPanelApi</c> 公开扩展点；目标程序集尚未加载时留待后续重试。</summary>
        internal static void TryBindAll()
        {
            if (_bindResolved) return;
            Assembly asm = TogetherReflection.ModAssembly;
            if (asm == null) return;

            _bindResolved = true;
            BindExternalInputBlocker(asm);

            Type hooksType = InteropReflection.FindType(asm, "Together.ModHooks");
            if (hooksType == null)
            {
                ModLog.Debug("Together.ModHooks 未在当前 CasualtiesMP 程序集中提供，启用反射补丁兼容模式");
                return;
            }

            BindSingleplayerPassthrough(hooksType);
            BindWorldSeedHooks(hooksType);
            BindSpawnPositionHooks(hooksType);
            BindAutoExitGuardHook(hooksType);

            ModLog.Info("Together.ModHooks 已对接：spPassthrough=" + HasSingleplayerPassthrough
                + " worldSeed=" + HasWorldSeedHook
                + " spawnPos=" + HasSpawnPositionHook
                + " autoExitGuard=" + HasAutoExitGuardHook
                + " inputBlocker=" + HasExternalInputBlocker);
        }

        /// <summary>同步官方存档坐标恢复开关状态（无钩子时静默跳过）。</summary>
        internal static void SetRestoreSavedPositions(bool enabled)
        {
            if (_mRestoreSavedPositions == null) return;
            InteropReflection.TryWriteMember(_mRestoreSavedPositions, null, enabled);
        }

        /// <summary>查询联机房间当前是否激活了特殊竞技模式（如 BattleRoyale）。</summary>
        internal static bool IsGamemodeActive()
        {
            if (!MultiplayerBridge.IsMultiplayerRunning()) return false;
            if (!_gamemodeResolved)
            {
                Assembly asm = TogetherReflection.ModAssembly;
                if (asm == null) return false;
                _gamemodeResolved = true;
                // 原版、main 与 Feature 版中 GamemodeManager 均位于 CasualtiesMultiplayerAPI 命名空间，保留 Together.* 兜底
                Type gmType = InteropReflection.FindType(asm, "CasualtiesMultiplayerAPI.GamemodeManager")
                    ?? InteropReflection.FindType(asm, "Together.GamemodeManager");
                if (gmType != null)
                {
                    _mHasGamemode = InteropReflection.FindMethod(gmType, "HasGamemode");
                }
            }

            return _mHasGamemode != null
                && InteropReflection.TryInvoke(_mHasGamemode, null, Array.Empty<object>(), out object res)
                && res is bool active && active;
        }

        /// <summary>卸载跨程序集委托、事件与输入阻塞锁，将 <c>Together.ModHooks</c> 静态属性还原为初始值，防止热重载残留死委托。</summary>
        internal static void Dispose()
        {
            try
            {
                if (_eWorldSeedApplied != null && _dWorldSeedApplied != null)
                {
                    _eWorldSeedApplied.RemoveEventHandler(null, _dWorldSeedApplied);
                }
            }
            catch { }

            if (_mUnregisterExternalInputBlocker != null)
            {
                InteropReflection.TryInvoke(_mUnregisterExternalInputBlocker, null, new object[] { ExternalBlockerModId }, out _);
            }

            InteropReflection.TryWriteMember(_mAllowSpPassthrough, null, false);
            InteropReflection.TryWriteMember(_mRestoreSavedPositions, null, false);
            InteropReflection.TryWriteMember(_mCustomWorldSeedProvider, null, null);
            InteropReflection.TryWriteMember(_mSpawnPositionOverride, null, null);
            InteropReflection.TryWriteMember(_mShouldBlockAutoExit, null, null);

            _dWorldSeedApplied = null;
            _eWorldSeedApplied = null;
            _mUnregisterExternalInputBlocker = null;
            _mAllowSpPassthrough = null;
            _mRestoreSavedPositions = null;
            _mCustomWorldSeedProvider = null;
            _mSpawnPositionOverride = null;
            _mShouldBlockAutoExit = null;
            _mHasGamemode = null;
            PinnedDelegates.Clear();

            HasSingleplayerPassthrough = false;
            HasWorldSeedHook = false;
            HasSpawnPositionHook = false;
            HasAutoExitGuardHook = false;
            HasExternalInputBlocker = false;
            _gamemodeResolved = false;
            _bindResolved = false;
        }

        /// <summary>
        /// 可选对接 <c>Together.Api.TogetherPanelApi</c> 外部输入阻塞锁：
        /// 解决局内打开存档面板（或嵌入其中的 <c>SkinSync</c> 子页签）时打字或点击穿透触发 Together 交互快捷键的问题；
        /// 原版 <c>v5.5.3.0</c> 与普通版 <c>main</c> 不含该类时静默跳过。
        /// </summary>
        private static void BindExternalInputBlocker(Assembly asm)
        {
            Type panelApiType = InteropReflection.FindType(asm, "Together.Api.TogetherPanelApi");
            if (panelApiType == null) return;

            MethodInfo register = InteropReflection.FindMethod(
                panelApiType,
                "RegisterExternalInputBlocker",
                typeof(string),
                typeof(Func<bool>));
            if (register == null) return;

            MethodInfo unregister = InteropReflection.FindMethod(
                panelApiType,
                "UnregisterExternalInputBlocker",
                typeof(string));

            Func<bool> blocker = IsUiBlocking;
            if (InteropReflection.TryInvoke(register, null, new object[] { ExternalBlockerModId, blocker }, out _))
            {
                PinnedDelegates.Add(blocker);
                _mUnregisterExternalInputBlocker = unregister;
                HasExternalInputBlocker = true;
            }
        }

        private static bool IsUiBlocking() => UiBlocker.IsBlocking;

        private static void BindSingleplayerPassthrough(Type hooksType)
        {
            _mAllowSpPassthrough = FindStaticMember(hooksType, "AllowSingleplayerPassthrough");
            if (_mAllowSpPassthrough != null && InteropReflection.TryWriteMember(_mAllowSpPassthrough, null, true))
            {
                HasSingleplayerPassthrough = true;
            }
        }

        private static void BindWorldSeedHooks(Type hooksType)
        {
            MemberInfo providerMember = FindStaticMember(hooksType, "CustomWorldSeedProvider");
            EventInfo appliedEvent = hooksType.GetEvent("OnWorldSeedApplied", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                ?? hooksType.GetEvent("OnCustomWorldSeedReceived", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (providerMember == null || appliedEvent == null) return;

            Func<int?> provider = ProvideCustomWorldSeed;
            if (!InteropReflection.TryWriteMember(providerMember, null, provider)) return;

            Delegate handler = BuildSeedAppliedHandler(appliedEvent.EventHandlerType);
            if (handler == null)
            {
                InteropReflection.TryWriteMember(providerMember, null, null);
                return;
            }

            try
            {
                appliedEvent.AddEventHandler(null, handler);
                PinnedDelegates.Add(provider);
                PinnedDelegates.Add(handler);
                _mCustomWorldSeedProvider = providerMember;
                _eWorldSeedApplied = appliedEvent;
                _dWorldSeedApplied = handler;
                HasWorldSeedHook = true;
            }
            catch (Exception ex)
            {
                InteropReflection.TryWriteMember(providerMember, null, null);
                ModLog.Warning("绑定 Together.ModHooks 世界种子事件失败：" + ex.Message);
            }
        }

        private static Delegate BuildSeedAppliedHandler(Type handlerType)
        {
            if (handlerType == typeof(Action<int, bool>))
                return (Action<int, bool>)OnWorldSeedAppliedTwoArgs;
            if (handlerType == typeof(Action<int>))
                return (Action<int>)(seed => OnWorldSeedAppliedTwoArgs(seed, MultiplayerBridge.IsServer()));
            if (handlerType == typeof(Action<int?>))
                return (Action<int?>)(seed => OnWorldSeedAppliedTwoArgs(seed ?? 0, MultiplayerBridge.IsServer()));
            return null;
        }

        private static void BindSpawnPositionHooks(Type hooksType)
        {
            _mRestoreSavedPositions = FindStaticMember(hooksType, "RestoreSavedPositionsOnLoad");
            MemberInfo overrideMember = FindStaticMember(hooksType, "SpawnPositionOverride");
            if (overrideMember == null) return;

            Func<ushort, string, Body, Vector2?> callback = ResolveSpawnPositionOverride;
            if (InteropReflection.TryWriteMember(overrideMember, null, callback))
            {
                PinnedDelegates.Add(callback);
                _mSpawnPositionOverride = overrideMember;
                HasSpawnPositionHook = true;
            }
        }

        private static void BindAutoExitGuardHook(Type hooksType)
        {
            MemberInfo guardMember = FindStaticMember(hooksType, "ShouldBlockAutoExitOnAllDead");
            if (guardMember == null) return;

            Func<bool> guard = PlayerCameraToMainMenuGuard.ShouldBlock;
            if (InteropReflection.TryWriteMember(guardMember, null, guard))
            {
                PinnedDelegates.Add(guard);
                _mShouldBlockAutoExit = guardMember;
                HasAutoExitGuardHook = true;
            }
        }

        private static MemberInfo FindStaticMember(Type type, string name)
            => (MemberInfo)InteropReflection.FindProperty(type, name) ?? InteropReflection.FindField(type, name);

        /// <summary>
        /// 向 Feature 版 <c>Together.ModHooks.CustomWorldSeedProvider</c> 提供未叠加层偏移的基础种子：
        /// 层偏移 <c>+ traveled * 265443576</c> 由宿主在快照 <c>firstworldgenparams</c> 前统一计算，
        /// 同时基础种子随 <c>Server_AnnounceSeed</c> 发往客机，与原版/<c>main</c> 的 <c>MpWorldSeedInjector</c> + <c>MpSeedBroadcast</c> 语义严格对齐，
        /// 避免全目录扫描 <c>mp_save/</c> 误读历史孤儿玩家存档或污染运行期 <c>world.totalTraveled</c>。
        /// </summary>
        private static int? ProvideCustomWorldSeed()
        {
            if (WorldEngineArbiter.Current == WorldEngine.Krok
                || !SeededWorldEngine.IsActive
                || !MultiplayerBridge.IsServer())
            {
                SeededWorldEngine.MpReseedEnabled = false;
                return null;
            }

            int seed = SeededWorldEngine.CurrentSeed;
            return seed != 0 ? (int?)seed : null;
        }

        private static void OnWorldSeedAppliedTwoArgs(int seed, bool isHost)
        {
            if (seed != 0)
            {
                if (!isHost)
                {
                    SeededWorldEngine.Activate(seed, "");
                }
                SeededWorldEngine.MpReseedEnabled = true;
                ModLog.Info($"ModHooks 种子同步生效：seed={seed} isHost={isHost}");
            }
            else
            {
                if (!isHost)
                {
                    SeededWorldEngine.Deactivate();
                }
                SeededWorldEngine.MpReseedEnabled = false;
            }
        }

        private static Vector2? ResolveSpawnPositionOverride(ushort playerId, string persistentId, Body body)
            => MpPositionRestorer.TryResolveHookPosition(persistentId);
    }
}
