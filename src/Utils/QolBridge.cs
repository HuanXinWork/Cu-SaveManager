using System;
using CasualtiesUnknown.SaveManager.Interop;
using UnityEngine;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>
    /// QoL.Unknown 的软依赖桥。目标程序集成员名只出现在 <see cref="QolProvider"/> 里；
    /// 保存槽位时抓取 QoL 的 SeedManager 状态，回档时把存档里的玩家位置 + 种子推回 QoL，
    /// 让它在下一次 TryLoadGame postfix 还原。QoL 不在场时所有方法静默返回默认值，绝不影响主流程。
    /// </summary>
    internal static class QolBridge
    {
        private static QolProvider Provider => InteropBootstrap.Qol;

        /// <summary>是否检测到 QoL.Unknown（其世界确定化 / 自动存档能力可用）。</summary>
        internal static bool IsQolPresent()
        {
            return Provider.IsPresent;
        }

        /// <summary>暂时禁用 QoL 对世界生成与存档的介入：置 SeedManager.IsSeeded=false + SaveManager.AutosaveEnabled=false。</summary>
        internal static void DisableQolIntervention()
        {
            try
            {
                ModLog.Flow("QoL", "01", "DisableQolIntervention present=" + Provider.IsPresent);
                Provider.TryWriteSeed(false, 0, string.Empty);
                Provider.TryWriteAutosaveEnabled(false);
            }
            catch (Exception ex)
            {
                ModLog.Warning($"QolBridge.DisableQolIntervention 失败：{ex.Message}");
            }
        }

        /// <summary>QoL 模式新开局确保 QoL 有确定种子：未 seeded 时用自生成种子让 QoL 固定世界，并恢复其自动存档。
        /// 已 seeded（玩家在 QoL 输过种子）则不覆盖。让回档可凭 sidecar 种子复现世界。</summary>
        internal static void EnsureQolSeeded()
        {
            if (!Provider.IsPresent)
            {
                ModLog.Warning("EnsureQolSeeded：QoL 未解析到，跳过设种子");
                return;
            }

            try
            {
                Provider.TryWriteAutosaveEnabled(true);
                Provider.TryReadSeed(out bool seeded, out int current, out string _);
                if (seeded && current != 0)
                {
                    ModLog.Info($"QoL 模式新开局：QoL 已有种子 seed={current}，不覆盖");
                    return;
                }

                int seed = Guid.NewGuid().GetHashCode();
                if (seed == 0) seed = 1;
                Provider.TryWriteSeed(true, seed, seed.ToString());
                ModLog.Info($"QoL 模式新开局已设确定种子 seed={seed}（原 seeded={seeded} current={current}）");
            }
            catch (Exception ex)
            {
                ModLog.Warning($"QolBridge.EnsureQolSeeded 失败：{ex.Message}");
            }
        }

        /// <summary>读 QoL.SeedManager 当前实时种子；为 0 时回落到本 mod sidecar 已记录的稳定种子。</summary>
        internal static void ReadCurrentSeed(out int seed, out string input)
        {
            Provider.TryReadSeed(out bool _, out seed, out input);
            if (seed != 0)
            {
                return;
            }

            try
            {
                var sc = SlotSidecar.LoadOrEmpty(SaveStore.GameSavePath);
                if (sc != null && sc.QolSeed != 0)
                {
                    seed = sc.QolSeed;
                    input = string.IsNullOrEmpty(input) ? (sc.QolSeedInput ?? "") : input;
                }
            }
            catch
            {
                // 读取 sidecar 失败时保持 QoL 的实时值，不影响调用方。
            }
        }

        /// <summary>FNV-1a 32 位 hash 当前 save.sv 字节流，与 QoL SeedManager.GetDeterministicSeedFromSaveFile 一致。</summary>
        internal static int ComputeDeterministicSeedFromSaveFile(string fullPath)
        {
            try
            {
                if (!System.IO.File.Exists(fullPath)) return 0;
                byte[] bytes = System.IO.File.ReadAllBytes(fullPath);
                if (bytes == null || bytes.Length == 0) return 0;
                uint h = 2166136261u;
                for (int i = 0; i < bytes.Length; i++)
                {
                    h ^= bytes[i];
                    h *= 16777619u;
                }
                int v = (int)h;
                return v == 0 ? 1 : v;
            }
            catch { return 0; }
        }

        /// <summary>Debug 构建下说明当前种子取自哪里（QoL 实时 / sidecar 回落 / 存档文件 FNV / 无）；Release 中调用点被整体移除。</summary>
        internal static string DescribeSeedSource()
        {
            if (!Provider.IsPresent)
            {
                return "QoL 未解析到，无种子来源（世界按原随机状态生成）";
            }

            Provider.TryReadSeed(out bool seeded, out int seed, out string input);
            if (seeded && seed != 0)
            {
                return "QoL 当前种子（seeded=true seed=" + seed + " input='" + input + "'）";
            }

            SlotSidecar sidecar = SlotSidecar.LoadOrEmpty(SaveStore.GameSavePath);
            if (sidecar.QolSeed != 0 || !string.IsNullOrEmpty(sidecar.QolSeedInput))
            {
                return "sidecar（QoL 未 seeded 或种子为 0，seed=" + sidecar.QolSeed + " input='" + sidecar.QolSeedInput + "'）";
            }

            int fnv = ComputeDeterministicSeedFromSaveFile(SaveStore.GameSavePath);
            return fnv != 0
                ? "存档文件 FNV（QoL 无种子且 sidecar 未记录，save.sv 字节 FNV-1a seed=" + fnv + "）"
                : "无（QoL 无种子、sidecar 未记录、save.sv 不可读；世界不可复现）";
        }

        /// <summary>把种子钉进 QoL：CurrentSeed/InputString + IsSeeded=true。QoL 不在场或 seed=0 时静默跳过。</summary>
        internal static void PinSeed(int seed, string input)
        {
            if (!Provider.IsPresent || seed == 0)
            {
                ModLog.Flow("QoL", "10", "PinSeed 跳过 present=" + Provider.IsPresent + " seed=" + seed);
                return;
            }
            try
            {
                Provider.TryWriteSeed(true, seed, input);
                ModLog.Flow("QoL", "10", "PinSeed seed=" + seed + " input='" + input + "'");
            }
            catch (Exception ex)
            {
                ModLog.Warning($"QolBridge.PinSeed 失败：{ex.Message}");
            }
        }

        /// <summary>把回档目标的玩家位置 + 种子推给 QoL。回档完成后 QoL 的 TryLoadGame postfix 自动塞回 body.transform.position。</summary>
        internal static void PrepareRollback(SlotSidecar sidecar)
        {
            if (sidecar == null) return;
            try
            {
                ModLog.Flow("QoL", "11", "PrepareRollback present=" + Provider.IsPresent
                    + " hasPos=" + sidecar.HasPlayerPos + " seed=" + sidecar.QolSeed + " input='" + sidecar.QolSeedInput + "'");
                if (sidecar.HasPlayerPos)
                {
                    Provider.TryWritePendingLoadPosition(new Vector3(sidecar.PlayerX, sidecar.PlayerY, 0f));
                    ModLog.Flow("QoL", "12", "PendingLoadPosition=(" + sidecar.PlayerX.ToString("0.0") + "," + sidecar.PlayerY.ToString("0.0") + ")");
                }

                if (sidecar.QolSeed != 0 || !string.IsNullOrEmpty(sidecar.QolSeedInput))
                {
                    Provider.TryWriteSeed(true, sidecar.QolSeed, sidecar.QolSeedInput);
                    ModLog.Flow("QoL", "13", "SeedManager←seed=" + sidecar.QolSeed + " input='" + sidecar.QolSeedInput + "'");
                }
            }
            catch (Exception ex)
            {
                ModLog.Warning($"QolBridge.PrepareRollback 失败：{ex.Message}");
            }
        }
    }
}
