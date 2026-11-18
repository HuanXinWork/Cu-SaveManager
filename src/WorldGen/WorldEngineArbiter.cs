namespace CasualtiesUnknown.SaveManager
{
    /// <summary>世界生成引擎类型。</summary>
    internal enum WorldEngine
    {
        Qol,
        Krok,
        Self,
    }

    /// <summary>按用户配置与在场模组裁决并切换世界生成引擎（qol / krok / self）。</summary>
    internal static class WorldEngineArbiter
    {
        internal static WorldEngine Current { get; private set; } = WorldEngine.Self;

        internal static string PreferredEngine { get; private set; } = "qol";
        internal static string ManualSeedInput { get; private set; } = "";
        internal static string PositionMode { get; private set; } = "lastPos";
        internal static float FixedX { get; private set; }
        internal static float FixedY { get; private set; }

        internal static void SyncPreference(string preferredEngine, string manualSeed,
            string positionMode = null, float fixedX = 0f, float fixedY = 0f)
        {
            PreferredEngine = NormalizeEngineName(preferredEngine);
            ManualSeedInput = manualSeed ?? "";
            if (positionMode != null)
            {
                PositionMode = positionMode;
                FixedX = fixedX;
                FixedY = fixedY;
            }
        }

        internal static string NormalizeEngineName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "self";
            name = name.Trim().ToLowerInvariant();
            return name == "qol" || name == "krok" ? name : "self";
        }

        internal static string ResolveEffectiveEngineName(string preferred = null)
        {
            string pref = NormalizeEngineName(preferred ?? PreferredEngine);
            bool mpEnabled = MultiplayerBridge.IsMultiplayerEnabled();
            bool qol = QolBridge.IsQolPresent();
            string result;
            if (pref == "self") result = "self";
            else if (pref == "krok" && mpEnabled) result = "krok";
            else if (pref == "qol" && qol) result = "qol";
            else result = mpEnabled ? "krok" : "self";
            ModLog.Flow("Engine", "01", "pref='" + pref + "' mpEnabled=" + mpEnabled + " qol=" + qol + " → " + result);
            return result;
        }

        internal static WorldEngine ResolveEffectiveEngine(string preferred = null)
        {
            string name = ResolveEffectiveEngineName(preferred);
            if (name == "krok") return WorldEngine.Krok;
            if (name == "qol") return WorldEngine.Qol;
            return WorldEngine.Self;
        }

        internal static void ApplyForFreshRun()
        {
            var engine = ResolveEffectiveEngine();
            if (engine == WorldEngine.Krok)
            {
                SeededWorldEngine.Deactivate();
                QolBridge.DisableQolIntervention();
                Current = WorldEngine.Krok;
                ModLog.Info("固定世界引擎=联机模组（原生世界生成）");
                return;
            }
            if (engine == WorldEngine.Self)
            {
                QolBridge.DisableQolIntervention();
                if (!string.IsNullOrWhiteSpace(ManualSeedInput)) SeededWorldEngine.SetManualSeed(ManualSeedInput);
                else SeededWorldEngine.EnsureFreshSeed();
                Current = WorldEngine.Self;
                return;
            }
            SeededWorldEngine.Deactivate();
            QolBridge.EnsureQolSeeded();
            Current = WorldEngine.Qol;
        }

        /// <summary>多人回档前按 sidecar 激活对应引擎；self 引擎下保持 QoL 禁用，避免触发其联机广播与二次传送。</summary>
        internal static void PrepareMpRollback(SlotSidecar sidecar)
        {
            string engine = ResolveSidecarEngine(sidecar);
            int seed = sidecar?.QolSeed ?? 0;
            string input = sidecar?.QolSeedInput ?? "";
            ModLog.Info($"多人回档引擎={engine} seed={seed} (sidecar.worldEngine='{sidecar?.WorldEngine}' mpWorldEngine='{sidecar?.MpWorldEngine}')");
            if (engine == "krok")
            {
                SeededWorldEngine.Deactivate();
                QolBridge.DisableQolIntervention();
                Current = WorldEngine.Krok;
                return;
            }
            if (engine == "qol")
            {
                SeededWorldEngine.Deactivate();
                Current = WorldEngine.Qol;
                QolBridge.PrepareRollback(sidecar);
                return;
            }
            Apply(WorldEngine.Self, seed, input);
        }

        /// <summary>返回当前世界生成种子来源的诊断描述。</summary>
        internal static string DescribeSeedSource()
        {
            if (Current == WorldEngine.Krok)
            {
                return "krok（世界随机状态由联机模组主机广播，本模组不注入种子）";
            }

            if (Current == WorldEngine.Self)
            {
                if (!SeededWorldEngine.IsActive)
                {
                    return "无（self 引擎未激活，世界按原随机状态生成）";
                }

                return "self 引擎（seed=" + SeededWorldEngine.CurrentSeed + " input='" + SeededWorldEngine.InputString + "'，" + DescribeSelfSeedBasis() + "）";
            }

            return "QoL：" + QolBridge.DescribeSeedSource();
        }

        private static string DescribeSelfSeedBasis()
        {
            if (!string.IsNullOrEmpty(ManualSeedInput) && ManualSeedInput == SeededWorldEngine.InputString)
            {
                return "取自配置里的手输种子";
            }

            try
            {
                SlotSidecar sidecar = SlotSidecar.LoadOrEmpty(SaveStore.GameSavePath);
                if (sidecar.QolSeed != 0 && sidecar.QolSeed == SeededWorldEngine.CurrentSeed)
                {
                    return "与当前 save.sv sidecar 记录的种子一致，来源于 sidecar";
                }
            }
            catch { }

            return "不等同手输种子也不同于当前 sidecar，按新开局随机种子/多人回放处理";
        }

        private static string ResolveSidecarEngine(SlotSidecar sidecar)
        {
            if (!string.IsNullOrEmpty(sidecar?.WorldEngine))
                return ResolveEffectiveEngineName(sidecar.WorldEngine);
            if (!string.IsNullOrEmpty(sidecar?.MpWorldEngine))
                return ResolveEffectiveEngineName(sidecar.MpWorldEngine);
            return ResolveEffectiveEngineName();
        }

        internal static void Apply(WorldEngine engine, int seed, string seedInput)
        {
            Current = engine;
            ModLog.Flow("Engine", "02", "Apply " + engine + " seed=" + seed + " input='" + seedInput + "'");
            if (engine == WorldEngine.Self)
            {
                QolBridge.DisableQolIntervention();
                SeededWorldEngine.Activate(seed, seedInput);
            }
            else
            {
                SeededWorldEngine.Deactivate();
            }
        }
    }
}
