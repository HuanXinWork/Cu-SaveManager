using System;
using System.Reflection;
using CasualtiesUnknown.SaveManager.Interop;
using HarmonyLib;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>读档/回档时按 sidecar 还原当前层词条（Priority 800），并在本次调用内短路 QoL 的同目标词条重掷补丁。</summary>
    [HarmonyPatch(typeof(WorldGeneration), "ApplyLayerModifiers")]
    internal static class ApplyLayerModifiersRestorePatch
    {
        private static readonly FieldInfo PrefixField = AccessTools.Field(typeof(WorldGeneration), "layerPrefix");
        private static readonly FieldInfo DescField = AccessTools.Field(typeof(WorldGeneration), "layerDescription");

        private static Harmony _boundHarmony;
        private static bool _suppressQolThisCall;
        private static bool _qolResolveFailed;

        internal static void BindHarmony(Harmony harmony)
        {
            _boundHarmony = harmony ?? HarmonyBootstrap.Instance;
            EnsureQolPrefixPatched();
        }

        internal static void ResetPatchState()
        {
            _boundHarmony = null;
            _qolResolveFailed = false;
            _suppressQolThisCall = false;
            PendingLoadContext.LayerModifierIndex = -2;
        }

        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(WorldGeneration __instance)
        {
            if (PendingLoadContext.LayerModifierIndex == -2) return true;
            int idx = PendingLoadContext.LayerModifierIndex;
            PendingLoadContext.LayerModifierIndex = -2;
            _suppressQolThisCall = true;
            EnsureQolPrefixPatched();

            try
            {
                __instance.ResetLayerModifiers();
                if (idx < 0)
                {
                    ModLog.Info("LayerModifierRestorePatch 还原本层无词条");
                    return false;
                }

                var mods = LayerModifier.availableModifiers;
                if (mods == null)
                {
                    ModLog.Warning($"LayerModifierRestorePatch: availableModifiers 未就绪 idx={idx}");
                    return false;
                }

                LayerModifier target = null;
                for (int i = 0; i < mods.Length; i++)
                {
                    if (mods[i] != null && mods[i].modifierIndex == idx)
                    {
                        target = mods[i];
                        break;
                    }
                }
                if (target == null)
                {
                    ModLog.Warning($"LayerModifierRestorePatch: 未找到匹配词条 modifierIndex={idx} len={mods.Length}");
                    return false;
                }

                target.Initialize(__instance);
                target.active = true;
                PrefixField?.SetValue(__instance, Locale.GetOther("layermodifier" + target.modifierIndex));
                DescField?.SetValue(__instance, Locale.GetOther("layermodifier" + target.modifierIndex + "dsc"));
                ModLog.Info($"LayerModifierRestorePatch 还原词条 index={target.modifierIndex}");
            }
            catch (Exception ex)
            {
                ModLog.Warning($"LayerModifierRestorePatch 异常：{ex.Message}");
            }
            return false;
        }

        [HarmonyFinalizer]
        private static void Finalizer()
        {
            _suppressQolThisCall = false;
        }

        private static void EnsureQolPrefixPatched()
        {
            if (_qolResolveFailed) return;
            if (!QolBridge.IsQolPresent())
            {
                if (InteropBootstrap.PluginScanComplete)
                {
                    _qolResolveFailed = true;
                }
                return;
            }

            try
            {
                Harmony harmony = _boundHarmony ?? HarmonyBootstrap.Instance;
                Type type = InteropBootstrap.Qol.TryResolveType("QoL_Unknown.LayerModifierPatcher")
                    ?? AccessTools.TypeByName("QoL_Unknown.LayerModifierPatcher");
                MethodInfo qolPrefix = type == null ? null : AccessTools.Method(type, "ApplyLayerModifiers_Prefix");
                if (qolPrefix == null)
                {
                    _qolResolveFailed = true;
                    InteropDiagnostics.WarnOnce(
                        "ApplyLayerModifiersRestorePatch.QolPrefix",
                        "QoL 已加载但未找到 QoL_Unknown.LayerModifierPatcher.ApplyLayerModifiers_Prefix，词条重掷短路守卫未生效");
                    return;
                }

                string handlerName = qolPrefix.ReturnType == typeof(bool)
                    ? nameof(ShortCircuitQolBoolPrefix)
                    : nameof(ShortCircuitQolVoidPrefix);

                Patches info = Harmony.GetPatchInfo(qolPrefix);
                if (info?.Prefixes != null)
                {
                    foreach (Patch patch in info.Prefixes)
                    {
                        if (patch.owner == harmony.Id && patch.PatchMethod?.Name == handlerName)
                            return;
                    }
                }

                MethodInfo guard = typeof(ApplyLayerModifiersRestorePatch).GetMethod(
                    handlerName, BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(qolPrefix, prefix: new HarmonyMethod(guard));
                ModLog.Info("已挂载 QoL LayerModifierPatcher.ApplyLayerModifiers_Prefix 按需短路守卫");
            }
            catch (Exception ex)
            {
                _qolResolveFailed = true;
                ModLog.Warning($"挂载 QoL 词条短路守卫失败：{ex.Message}");
            }
        }

        private static bool ShortCircuitQolBoolPrefix(ref bool __result)
        {
            if (!_suppressQolThisCall) return true;
            __result = false;
            return false;
        }

        private static bool ShortCircuitQolVoidPrefix() => !_suppressQolThisCall;
    }

    /// <summary>读取当前激活的词条索引；<c>availableModifiers</c> 未就绪返回 -2，无激活词条返回 -1。</summary>
    internal static class LayerModifierSnapshot
    {
        internal static int CurrentActiveIndex()
        {
            try
            {
                var mods = LayerModifier.availableModifiers;
                if (mods == null) return -2;
                foreach (var m in mods)
                {
                    if (m != null && m.active) return m.modifierIndex;
                }
            }
            catch
            {
                return -2;
            }
            return -1;
        }
    }
}
