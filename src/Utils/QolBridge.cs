using System;
using System.IO;
using CasualtiesUnknown.SaveManager.Interop;
using UnityEngine;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>封装对 QoL.Unknown 种子管理、自动存档状态与读档坐标恢复的软依赖访问。</summary>
    internal static class QolBridge
    {
        private static QolProvider Provider => InteropBootstrap.Qol;

        internal static bool IsQolPresent() => Provider.IsPresent;

        /// <summary>QoL 是否正在执行后台自动存档。</summary>
        internal static bool IsAutosaveInProgress() => Provider.IsAutosaveInProgress;

        /// <summary>获取当前单机存档完整路径；QoL 文件名越界或为空时回退 VanillaSavePath。</summary>
        internal static string GetCurrentSaveFilePath()
        {
            try
            {
                string root = Application.persistentDataPath;
                if (string.IsNullOrEmpty(root)) return MpSaveLocator.VanillaSavePath;
                string file = Provider.CurrentSaveFile;
                if (string.IsNullOrWhiteSpace(file)) return MpSaveLocator.VanillaSavePath;
                string fullRoot = Path.GetFullPath(root)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                string candidate = Path.GetFullPath(Path.Combine(root, file));
                if (!candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                    return MpSaveLocator.VanillaSavePath;
                return candidate;
            }
            catch
            {
                return MpSaveLocator.VanillaSavePath;
            }
        }

        /// <summary>单机槽位还原写入原版 save.sv 后，将 QoL 活动存档名切回 "save.sv"，防止读档时被旧重定向文件覆盖。</summary>
        internal static void ResetCurrentSaveFileToVanilla()
        {
            try
            {
                if (!Provider.IsPresent) return;
                string current = Provider.CurrentSaveFile;
                if (!string.Equals(current, "save.sv", StringComparison.OrdinalIgnoreCase)
                    && Provider.TryWriteCurrentSaveFile("save.sv"))
                {
                    ModLog.Info($"QoL 活动存档已从 '{current}' 切回 'save.sv'");
                }
            }
            catch (Exception ex)
            {
                ModLog.Warning($"QolBridge.ResetCurrentSaveFileToVanilla 失败：{ex.Message}");
            }
        }

        /// <summary>关闭 QoL 的固定种子与后台自动存档介入。</summary>
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

        /// <summary>QoL 模式新开局时确保拥有非零确定种子并开启自动存档；已有种子时不覆盖。</summary>
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

        /// <summary>读取 QoL 实时种子；为 0 时按当前模式回退读取同模式存档的 sidecar 种子。</summary>
        internal static void ReadCurrentSeed(out int seed, out string input)
        {
            Provider.TryReadSeed(out bool _, out seed, out input);
            if (seed != 0) return;

            try
            {
                string savePath = MultiplayerBridge.IsMultiplayerRunning()
                    ? MpSaveLocator.ResolveOwnSavePath()
                    : GetCurrentSaveFilePath();
                if (string.IsNullOrEmpty(savePath)) return;
                var sc = SlotSidecar.LoadOrEmpty(savePath);
                if (sc != null && sc.QolSeed != 0)
                {
                    seed = sc.QolSeed;
                    input = string.IsNullOrEmpty(input) ? (sc.QolSeedInput ?? "") : input;
                }
            }
            catch { }
        }

        /// <summary>按存档字节计算 32 位 FNV-1a 非零确定性种子。</summary>
        internal static int ComputeDeterministicSeedFromSaveFile(string fullPath)
        {
            try
            {
                if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath)) return 0;
                byte[] bytes = File.ReadAllBytes(fullPath);
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
            catch
            {
                return 0;
            }
        }

        /// <summary>返回当前种子的来源诊断描述。</summary>
        internal static string DescribeSeedSource()
        {
            if (!Provider.IsPresent)
            {
                return "QoL 未解析到，无种子来源";
            }

            Provider.TryReadSeed(out bool seeded, out int seed, out string input);
            if (seeded && seed != 0)
            {
                return "QoL 当前种子（seeded=true seed=" + seed + " input='" + input + "'）";
            }

            string savePath = MultiplayerBridge.IsMultiplayerRunning()
                ? MpSaveLocator.ResolveOwnSavePath()
                : GetCurrentSaveFilePath();
            if (!string.IsNullOrEmpty(savePath))
            {
                SlotSidecar sidecar = SlotSidecar.LoadOrEmpty(savePath);
                if (sidecar.QolSeed != 0 || !string.IsNullOrEmpty(sidecar.QolSeedInput))
                {
                    return "sidecar（seed=" + sidecar.QolSeed + " input='" + sidecar.QolSeedInput + "'）";
                }

                int fnv = ComputeDeterministicSeedFromSaveFile(savePath);
                if (fnv != 0)
                {
                    return "存档文件 FNV（seed=" + fnv + "）";
                }
            }
            return "无（QoL 无种子、sidecar 未记录、存档不可读）";
        }

        /// <summary>将非零种子写入 QoL SeedManager 并置 IsSeeded=true。</summary>
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

        /// <summary>将回档目标坐标与种子写入 QoL；无坐标时显式清空 PendingLoadPosition。</summary>
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
                else
                {
                    ClearPendingLoadPosition();
                    ModLog.Flow("QoL", "12", "PendingLoadPosition=null");
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

        internal static void ClearPendingLoadPosition()
        {
            if (((IInteropTypeRegistry)Provider).TryResolveType("QoL_Unknown.SaveSystemPatcher", out Type patcherType))
            {
                ((IInteropMemberAccess)Provider).TryWriteStatic(patcherType, "PendingLoadPosition", null);
            }
        }
    }
}
