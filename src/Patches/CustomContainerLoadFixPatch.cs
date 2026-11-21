using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>以嵌套深度计数跟踪 <c>SaveSystem.TryLoadGame</c> 执行窗口，兼容联机逐玩家递归读档。</summary>
    [HarmonyPatch(typeof(SaveSystem), "TryLoadGame")]
    internal static class SaveLoadWindowPatch
    {
        private static int _activeDepth;

        internal static bool Active => _activeDepth > 0;

        private static void Prefix()
        {
            if (_activeDepth == 0) CustomContainerLoadFixPatch.ResetApplied();
            _activeDepth++;
        }

        private static void Finalizer()
        {
            if (_activeDepth > 0) _activeDepth--;
            if (_activeDepth == 0) CustomContainerLoadFixPatch.ResetApplied();
        }
    }

    /// <summary>读档窗口内在 <c>Container.LoadItem</c> 前提前调用 CUCoreLib 应用自定义容器容量，避免装填时按预制体小容量丢弃物品。</summary>
    [HarmonyPatch(typeof(Container), "LoadItem", new[] { typeof(Item) })]
    internal static class CustomContainerLoadFixPatch
    {
        private static readonly HashSet<int> _applied = new HashSet<int>();
        private static MethodInfo _applyCustomItemRuntime;
        private static bool _resolveTried;

        internal static void ResetApplied() => _applied.Clear();

        private static void Prefix(Container __instance)
        {
            if (!SaveLoadWindowPatch.Active || __instance == null) return;
            MethodInfo apply = ResolveApplyMethod();
            if (apply == null) return;
            try
            {
                if (!_applied.Add(__instance.GetInstanceID())) return;
                var item = __instance.GetComponent<Item>();
                if (item == null || string.IsNullOrEmpty(item.id)) return;
                apply.Invoke(null, new object[] { item, false });
            }
            catch (Exception ex)
            {
                ModLog.Warning($"读档自定义容器容量兜底失败：{ex.Message}");
            }
        }

        private static MethodInfo ResolveApplyMethod()
        {
            if (_resolveTried) return _applyCustomItemRuntime;
            _resolveTried = true;
            try
            {
                Type type = AccessTools.TypeByName("CUCoreLib.Patches.ItemRegistryPatches");
                if (type != null)
                {
                    _applyCustomItemRuntime = AccessTools.Method(
                        type,
                        "ApplyCustomItemRuntime",
                        new[] { typeof(Item), typeof(bool) });
                }
            }
            catch (Exception ex)
            {
                ModLog.Warning($"解析 CUCoreLib ApplyCustomItemRuntime 异常：{ex.Message}");
            }
            if (_applyCustomItemRuntime == null)
                ModLog.Debug("CUCoreLib ApplyCustomItemRuntime 不可用，读档自定义容器容量兜底未启用");
            return _applyCustomItemRuntime;
        }
    }
}
