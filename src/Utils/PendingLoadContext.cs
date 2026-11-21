using UnityEngine;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>收口单次读档或回档跨补丁传递的待消费标志与坐标，异常或世界生成完成时统一清空。</summary>
    internal static class PendingLoadContext
    {
        internal static bool SkipIntroLifepod { get; set; }
        internal static int LayerModifierIndex { get; set; } = -2;
        internal static Vector3? SpawnPosition { get; set; }

        internal static void ClearAll()
        {
            SkipIntroLifepod = false;
            LayerModifierIndex = -2;
            SpawnPosition = null;
        }
    }
}
