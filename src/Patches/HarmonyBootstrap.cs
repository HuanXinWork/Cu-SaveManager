using System;
using System.Reflection;
using HarmonyLib;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>统一托管本模组唯一 Harmony 实例及特性补丁与软依赖延迟补丁的挂载、复位与卸载。</summary>
    internal static class HarmonyBootstrap
    {
        internal const string HarmonyId = "com.casualtiesUnknown.saveManager";

        private static Harmony _instance;

        internal static Harmony Instance => _instance ?? (_instance = new Harmony(HarmonyId));

        internal static void PatchAllIsolated()
        {
            Harmony harmony = Instance;
            Assembly asm = typeof(HarmonyBootstrap).Assembly;

            // 逐类隔离挂载而非调用 PatchAll(asm)，防止单个补丁目标在游戏版本变动时抛错导致其余补丁全盘失效。
            foreach (Type type in AccessTools.GetTypesFromAssembly(asm))
            {
                if (type == null || !type.IsDefined(typeof(HarmonyPatch), inherit: false))
                {
                    continue;
                }

                try
                {
                    harmony.CreateClassProcessor(type).Patch();
                }
                catch (Exception ex)
                {
                    ModLog.Warning($"挂载补丁类 {type.Name} 失败：{ex.Message}");
                }
            }

            ApplyLayerModifiersRestorePatch.BindHarmony(harmony);
            TryPatchCoreDeferred(harmony);
        }

        /// <summary>主菜单就绪时幂等补齐依赖外部模组延迟加载的运行期补丁。</summary>
        internal static void TryPatchMainMenuDeferred()
        {
            Harmony harmony = Instance;
            TryPatchCoreDeferred(harmony);
            MpWorldSeedInjector.TryPatch();
            MpSeedBroadcast.TryPatch();
        }

        private static void TryPatchCoreDeferred(Harmony harmony)
        {
            SeededWorldPatcher.TryPatch(harmony);
            LoadRunPrefixReplacer.TryPatch(harmony);
            MpCharacterRestore.TryPatch(harmony);
        }

        internal static void UnpatchAll()
        {
            try { TogetherModHooksBridge.Dispose(); }
            catch (Exception ex) { ModLog.Warning($"卸载 TogetherModHooksBridge 异常：{ex.Message}"); }

            if (_instance != null)
            {
                try { _instance.UnpatchSelf(); }
                catch (Exception ex) { ModLog.Warning($"卸载 Harmony 补丁异常：{ex.Message}"); }
                finally { _instance = null; }
            }

            SeededWorldPatcher.ResetPatchState();
            LoadRunPrefixReplacer.ResetPatchState();
            MpCharacterRestore.ResetPatchState();
            ApplyLayerModifiersRestorePatch.ResetPatchState();
            LayerUnlockSeedPatcher.ResetPatchState();
            MpHarmonySilencer.ResetPatchState();
            MpLateSpawnSuppressor.ResetPatchState();
            MpSeedBroadcast.ResetPatchState();
            MpWorldSeedInjector.ResetPatchState();
            QolSpawnSuppressor.ResetPatchState();
            QolTrapClearGuard.ResetPatchState();
        }
    }
}
