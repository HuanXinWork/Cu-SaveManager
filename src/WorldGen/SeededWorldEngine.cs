using System;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>本模组固定世界生成引擎的种子状态与层种子派生。</summary>
    internal static class SeededWorldEngine
    {
        internal static bool IsActive { get; private set; }
        internal static int CurrentSeed { get; private set; }
        internal static string InputString { get; private set; } = "";

        /// <summary>多人模式下本端是否已确认固定世界种子并启用逐阶段随机流隔离。</summary>
        internal static bool MpReseedEnabled { get; set; }

        internal static void SetManualSeed(string input)
        {
            InputString = input ?? "";
            if (string.IsNullOrWhiteSpace(input))
            {
                IsActive = false;
                CurrentSeed = 0;
                ModLog.Flow("Seed", "01", "SetManualSeed 空 → 关闭确定化");
                return;
            }

            int parsed = int.TryParse(input, out int v) ? v : StableHash(input);
            CurrentSeed = parsed != 0 ? parsed : 1;
            IsActive = true;
            ModLog.Flow("Seed", "01", "SetManualSeed input='" + input + "' → seed=" + CurrentSeed);
        }

        internal static void Activate(int seed, string input)
        {
            CurrentSeed = seed != 0 ? seed : 1;
            InputString = input ?? "";
            IsActive = true;
            ModLog.Flow("Seed", "02", "Activate seed=" + CurrentSeed + " input='" + InputString + "'（传入 seed=" + seed + "）");
        }

        internal static void EnsureFreshSeed()
        {
            int seed = Guid.NewGuid().GetHashCode();
            if (seed == 0) seed = 1;
            CurrentSeed = seed;
            InputString = seed.ToString();
            IsActive = true;
            ModLog.Flow("Seed", "03", "EnsureFreshSeed 新开局 seed=" + seed);
        }

        internal static void Deactivate()
        {
            IsActive = false;
            CurrentSeed = 0;
            InputString = "";
            MpReseedEnabled = false;
            ModLog.Flow("Seed", "04", "Deactivate（self 引擎关闭）");
        }

        /// <summary>按基础种子与当前层 totalTraveled 派生层种子；初次读档内存未填充时回退读取磁盘存档。</summary>
        internal static int LayerSeed()
        {
            int traveled = 0;
            try
            {
                if (WorldGeneration.world != null) traveled = WorldGeneration.world.totalTraveled;
            }
            catch { }
            if (traveled <= 0)
            {
                try { traveled = MpSaveLayerHelper.ReadPersistedTotalTraveled(); }
                catch { }
            }
            int seed = CurrentSeed + traveled * 265443576;
            ModLog.Flow("Seed", "05", "LayerSeed base=" + CurrentSeed + " traveled=" + traveled + " → " + seed);
            return seed;
        }

        private static int StableHash(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            int h = 23;
            foreach (char c in s) h = h * 31 + c;
            return h == 0 ? 1 : h;
        }
    }
}
