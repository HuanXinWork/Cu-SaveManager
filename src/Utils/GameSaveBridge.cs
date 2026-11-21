using System;
using System.Reflection;
using CasualtiesUnknown.SaveManager.Interop;
using HarmonyLib;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>封装单机 SaveSystem.SaveGame 调用与世界进层过渡态判据。</summary>
    internal static class GameSaveBridge
    {
        private static readonly FieldInfo DoingRegenField = AccessTools.Field(typeof(WorldGeneration), "doingRegen");

        /// <summary>在玩家相机、身体与世界实例齐全时调用 SaveSystem.SaveGame，成功返回 true。</summary>
        internal static bool TrySaveGame()
        {
            try
            {
                if (PlayerCamera.main == null || PlayerCamera.main.body == null || WorldGeneration.world == null)
                    return false;
                SaveSystem.SaveGame();
                return true;
            }
            catch (Exception ex)
            {
                ModLog.Warning($"调用游戏 SaveGame() 失败：{ex.Message}");
                return false;
            }
        }

        /// <summary>世界是否正处于生成、未就绪或进下一层（doingRegen）过渡中。</summary>
        internal static bool IsWorldRegenerating()
        {
            try
            {
                var world = WorldGeneration.world;
                if (world == null) return false;
                if (world.generatingWorld || !world.worldExists) return true;
                if (DoingRegenField == null)
                {
                    InteropDiagnostics.WarnOnce("WorldGeneration.doingRegen",
                        "WorldGeneration.doingRegen not found; layer transition check degraded.");
                    return false;
                }
                if (DoingRegenField.GetValue(world) is bool doing && doing) return true;
            }
            catch { }
            return false;
        }

        internal static bool CanSnapshotCurrentLayer()
            => WorldGeneration.world != null && !IsWorldRegenerating();
    }
}
