using System;
using BepInEx.Configuration;
using UnityEngine;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>集中维护 BepInEx 持久化配置项与快捷键绑定状态。</summary>
    internal sealed class HotkeyConfig
    {
        private static readonly KeyboardShortcut Unbound = new KeyboardShortcut(KeyCode.None);

        // 仅拦截标准修饰键组合误触，放行 W/A/S/D 与空格等常规移动操作键。
        private static readonly KeyCode[] StandardModifiers =
        {
            KeyCode.LeftControl,
            KeyCode.RightControl,
            KeyCode.LeftShift,
            KeyCode.RightShift,
            KeyCode.LeftAlt,
            KeyCode.RightAlt,
        };

        internal ConfigEntry<KeyboardShortcut> ToggleSettingsHotkey { get; }
        internal ConfigEntry<KeyboardShortcut> ToggleSlotsHotkey { get; }
        internal ConfigEntry<KeyboardShortcut> QuickSaveHotkey { get; }

        internal ConfigEntry<bool> AutoBackupEnabled { get; }
        internal ConfigEntry<float> AutoBackupIntervalMinutes { get; }
        internal ConfigEntry<int> AutoBackupKeep { get; }
        internal ConfigEntry<bool> BackupBeforeOverwrite { get; }

        internal ConfigEntry<bool> AutoRollbackOnDeath { get; }
        internal ConfigEntry<float> RollbackDelaySeconds { get; }
        internal ConfigEntry<int> MultiplayerDeathThreshold { get; }
        internal ConfigEntry<int> RecentSlotsLimit { get; }
        internal ConfigEntry<bool> AllowPartialMultiplayerRollback { get; }
        internal ConfigEntry<float> MpStageTwoDelaySeconds { get; }

        internal ConfigEntry<string> PreferredEngine { get; }
        internal ConfigEntry<string> SeedInput { get; }
        internal ConfigEntry<string> PositionMode { get; }
        internal ConfigEntry<float> FixedX { get; }
        internal ConfigEntry<float> FixedY { get; }

        internal ConfigEntry<bool> ShowLogInConsole { get; }
        internal ConfigEntry<bool> AcceptUpdateNotice { get; }
        internal ConfigEntry<bool> SuppressStartingSuppliesOnLoad { get; }
        internal ConfigEntry<bool> SuppressIntroLifepodOnLoad { get; }
        internal ConfigEntry<string> PreferredLanguage { get; }
        internal ConfigEntry<int> UiScalePercent { get; }
        internal ConfigEntry<int> SchemaVersion { get; }

        internal HotkeyConfig(ConfigFile config)
        {
            ToggleSettingsHotkey = config.Bind("Hotkeys", "ToggleSettingsHotkey", Unbound,
                "打开主面板并切到设置页。默认未绑定。");
            ToggleSlotsHotkey = config.Bind("Hotkeys", "ToggleSlotsHotkey", Unbound,
                "打开或关闭主面板。默认未绑定。");
            QuickSaveHotkey = config.Bind("Hotkeys", "QuickSaveHotkey", Unbound,
                "立即保存当前进度到新槽位。默认未绑定。");

            AutoBackupEnabled = config.Bind("AutoBackup", "Enabled", false,
                "是否启用定时备份。开启后每隔 IntervalMinutes 复制当前存档到自动槽。");
            AutoBackupIntervalMinutes = config.Bind("AutoBackup", "IntervalMinutes", 5.0f,
                new ConfigDescription("定时备份间隔（分钟）", new AcceptableValueRange<float>(0.5f, 240.0f)));
            AutoBackupKeep = config.Bind("AutoBackup", "Keep", 10,
                new ConfigDescription("自动槽最多保留份数（不含已标记槽位）", new AcceptableValueRange<int>(1, 100)));
            BackupBeforeOverwrite = config.Bind("Safety", "BackupBeforeOverwrite", true,
                "切回槽位前先把当前存档备份到新槽位。");

            AutoRollbackOnDeath = config.Bind("Rollback", "AutoOnDeath", false,
                "角色死亡（单机进入死亡结算或多人死亡人数达到阈值）时自动倒计时回档到当前局最新备份。");
            RollbackDelaySeconds = config.Bind("Rollback", "DelaySeconds", 3.0f,
                new ConfigDescription("倒计时秒数，结束后执行回档，期间可取消。",
                    new AcceptableValueRange<float>(1.0f, 10.0f)));
            MultiplayerDeathThreshold = config.Bind("Rollback", "MultiplayerDeathThreshold", 0,
                new ConfigDescription("多人模式触发自动回档所需的死亡人数：0=全员死亡（团灭）才触发；1..16=达到指定死亡人数即触发。",
                    new AcceptableValueRange<int>(0, 16)));
            RecentSlotsLimit = config.Bind("Rollback", "RecentSlotsLimit", 20,
                new ConfigDescription("回档页默认显示的最近槽位条数。",
                    new AcceptableValueRange<int>(5, 100)));
            AllowPartialMultiplayerRollback = config.Bind("Rollback", "AllowPartialMultiplayerRollback", false,
                "多人回档时若仅存在离线的旧存档玩家（无身份歧义或目录冲突），允许忽略离线玩家继续回档。");
            MpStageTwoDelaySeconds = config.Bind("Rollback", "MpStageTwoDelaySeconds", 0.75f,
                new ConfigDescription("多人回档返回主菜单后触发阶段二重新加载前的等待秒数。",
                    new AcceptableValueRange<float>(0.2f, 5.0f)));

            PreferredEngine = config.Bind("World", "PreferredEngine", "qol",
                new ConfigDescription(
                    "固定世界生成引擎：qol=QoL；krok=Together（联机模组）；self=本模组。所选引擎不可用时，联机模组在场回落 krok，否则回落 self。",
                    new CaseInsensitiveAcceptableValueList("qol", "krok", "self")));
            SeedInput = config.Bind("World", "SeedInput", "",
                "本模组引擎种子。留空时从存档字节自动派生（FNV-1a）；填写数字或文本则按手动种子，仅 self 引擎生效。");
            PositionMode = config.Bind("World", "PositionMode", "lastPos",
                new ConfigDescription(
                    "读档或回档后的玩家位置模式：lastPos=保存时坐标；fixedPos=FixedX/FixedY。",
                    new CaseInsensitiveAcceptableValueList("lastPos", "fixedPos")));
            FixedX = config.Bind("World", "FixedX", 0f,
                new ConfigDescription("fixedPos 模式下的世界 X 坐标。", new AcceptableValueRange<float>(-100000f, 100000f)));
            FixedY = config.Bind("World", "FixedY", 0f,
                new ConfigDescription("fixedPos 模式下的世界 Y 坐标。", new AcceptableValueRange<float>(-100000f, 100000f)));

            ShowLogInConsole = config.Bind("Misc", "ShowInConsole", false,
                "是否把模组日志同步输出到游戏内控制台。");
            AcceptUpdateNotice = config.Bind("Misc", "AcceptUpdateNotice", true,
                "是否在启动时检查 GitHub 新版本并在游戏内提示。");
            SuppressStartingSuppliesOnLoad = config.Bind("Misc", "SuppressStartingSuppliesOnLoad", true,
                "读档或回档时屏蔽第 1 层起始物资重发。");
            SuppressIntroLifepodOnLoad = config.Bind("Misc", "SuppressIntroLifepodOnLoad", true,
                "读档或回档时屏蔽第 1 层入场救生舱重复生成。");
            PreferredLanguage = config.Bind("I18n", "PreferredLanguage", "auto",
                "界面语言：auto=跟随游戏；zh=强制中文；en=强制英文。");
            UiScalePercent = config.Bind("UI", "UiScalePercent", 100,
                new ConfigDescription("界面缩放百分比（75% ~ 150%）。",
                    new AcceptableValueRange<int>(75, 150)));
            SchemaVersion = config.Bind("Internal", "SchemaVersion", 0,
                "配置迁移版本号（内部维护，请勿手动修改）。");

            // 将旧版默认持久化的 MultiplayerDeathThreshold=1 一次性迁移为 0（全员死亡），避免房主单人阵亡误将存活子机踢回主菜单。
            if (SchemaVersion.Value < 1)
            {
                if (MultiplayerDeathThreshold.Value == 1)
                {
                    MultiplayerDeathThreshold.Value = 0;
                }
                SchemaVersion.Value = 1;
                config.Save();
            }
        }

        internal static bool IsBound(KeyboardShortcut sc) => sc.MainKey != KeyCode.None;

        /// <summary>绕开 BepInEx KeyboardShortcut.IsDown 对移动键的全局互斥封锁，仅校验主键按下与标准修饰键匹配。</summary>
        internal static bool TriggeredThisFrame(ConfigEntry<KeyboardShortcut> entry)
        {
            if (entry == null) return false;
            var sc = entry.Value;
            return IsBound(sc) && Input.GetKeyDown(sc.MainKey) && ModifiersMatch(sc);
        }

        private static bool ModifiersMatch(KeyboardShortcut sc)
        {
            if (sc.Modifiers != null)
            {
                foreach (KeyCode mod in sc.Modifiers)
                {
                    if (mod != KeyCode.None && !Input.GetKey(mod)) return false;
                }
            }

            for (int i = 0; i < StandardModifiers.Length; i++)
            {
                KeyCode stdMod = StandardModifiers[i];
                if (stdMod == sc.MainKey || HasModifier(sc, stdMod)) continue;
                if (Input.GetKey(stdMod)) return false;
            }
            return true;
        }

        private static bool HasModifier(KeyboardShortcut sc, KeyCode target)
        {
            if (sc.Modifiers == null) return false;
            foreach (KeyCode mod in sc.Modifiers)
            {
                if (mod == target) return true;
            }
            return false;
        }

        /// <summary>继承 AcceptableValueList&lt;string&gt; 以兼容配置管理器下拉框，同时对用户手写的不同大小写自动归一化而不误回退默认值。</summary>
        private sealed class CaseInsensitiveAcceptableValueList : AcceptableValueList<string>
        {
            public CaseInsensitiveAcceptableValueList(params string[] acceptableValues) : base(acceptableValues) { }

            public override object Clamp(object value)
            {
                if (value is string s)
                {
                    string trimmed = s.Trim();
                    string[] candidates = AcceptableValues;
                    for (int i = 0; i < candidates.Length; i++)
                    {
                        if (string.Equals(candidates[i], trimmed, StringComparison.OrdinalIgnoreCase))
                            return candidates[i];
                    }
                }
                return AcceptableValues[0];
            }

            public override bool IsValid(object value)
            {
                if (value is string s)
                {
                    string trimmed = s.Trim();
                    string[] candidates = AcceptableValues;
                    for (int i = 0; i < candidates.Length; i++)
                    {
                        if (string.Equals(candidates[i], trimmed, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
                return false;
            }
        }
    }
}
