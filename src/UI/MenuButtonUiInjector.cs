using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>在主菜单 PreRunScript 下注入 uGUI“存档管理”按钮，并统一托管运行期 Harmony 补丁。</summary>
    internal static class MenuButtonUiInjector
    {
        private const string InjectedName = "SaveManager_MenuButton";
        private static Action _onClick;
        private static Harmony _harmony;
        private static GameObject _injectedButton;

        /// <summary>注入按钮的 RectTransform，供 PreRunGuardPatch 拦截下层 AdaptiveButton 悬停穿透。</summary>
        internal static RectTransform InjectedRect;

        internal static void Setup(Action onClick)
        {
            _onClick = onClick;
            try
            {
                _harmony = new Harmony("com.casualtiesUnknown.saveManager.menuButton");
                _harmony.PatchAll(typeof(MenuButtonUiInjector).Assembly);
            }
            catch (Exception ex)
            {
                ModLog.Warning($"Harmony PatchAll 失败：{ex.Message}");
            }

            try
            {
                SeededWorldPatcher.TryPatch(_harmony);
            }
            catch (Exception ex)
            {
                ModLog.Warning($"SeededWorldPatcher.TryPatch 失败：{ex.Message}");
            }

            try
            {
                LoadRunPrefixReplacer.TryPatch(_harmony);
            }
            catch (Exception ex)
            {
                ModLog.Warning($"LoadRunPrefixReplacer.TryPatch 失败：{ex.Message}");
            }

            try
            {
                MpCharacterRestore.TryPatch(_harmony);
            }
            catch (Exception ex)
            {
                ModLog.Warning($"MpCharacterRestore.TryPatch 失败：{ex.Message}");
            }

            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private static readonly string[] AuxiliaryHarmonyIds =
        {
            "com.casualtiesUnknown.saveManager.mpCharacter",
            "com.casualtiesUnknown.saveManager.layerUnlockSeed",
            "com.casualtiesUnknown.saveManager.mpLateSpawnSuppressor",
            "com.casualtiesUnknown.saveManager.mpSeedBroadcast",
            "com.casualtiesUnknown.saveManager.mpWorldSeed",
            "com.casualtiesUnknown.saveManager.qolSpawnSuppressor",
            "com.casualtiesUnknown.saveManager.qolTrapClearGuard",
        };

        /// <summary>卸载场景事件监听并移除本模组全部 Harmony 实例挂载的补丁。</summary>
        internal static void Dispose()
        {
            try { SceneManager.sceneLoaded -= OnSceneLoaded; } catch { }
            try { _harmony?.UnpatchSelf(); } catch { }
            _harmony = null;
            for (int i = 0; i < AuxiliaryHarmonyIds.Length; i++)
            {
                try { Harmony.UnpatchID(AuxiliaryHarmonyIds[i]); } catch { }
            }
            MpCharacterRestore.ResetPatchState();
            ApplyLayerModifiersRestorePatch.ResetPatchState();
            SeededWorldPatcher.ResetPatchState();
            LayerUnlockSeedPatcher.ResetPatchState();
            MpLateSpawnSuppressor.ResetPatchState();
            MpSeedBroadcast.ResetPatchState();
            MpWorldSeedInjector.ResetPatchState();
            QolSpawnSuppressor.ResetPatchState();
            QolTrapClearGuard.ResetPatchState();
            TogetherModHooksBridge.Dispose();
            _injectedButton = null;
            InjectedRect = null;
        }

        /// <summary>按当前语言刷新已注入的主菜单按钮文案。</summary>
        internal static void RefreshButtonText()
        {
            if (_injectedButton != null)
            {
                ReplaceAllTexts(_injectedButton, I18n.T("app.menu_button"));
            }
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.name != "PreGen" || PreRunScript.instance == null) return;
            try { InjectOnce(PreRunScript.instance); }
            catch (Exception ex) { ModLog.Warning($"sceneLoaded 注入失败：{ex.Message}"); }
        }

        /// <summary>在 PreRunScript.Start 完成后补齐延迟补丁并注入主菜单按钮。</summary>
        internal static void OnPreRunScriptStarted(PreRunScript pre)
        {
            try
            {
                SeededWorldPatcher.TryPatch(_harmony);
                LoadRunPrefixReplacer.TryPatch(_harmony);
                MpCharacterRestore.TryPatch(_harmony);
                MpWorldSeedInjector.TryPatch();
                MpSeedBroadcast.TryPatch();
                InjectOnce(pre);
            }
            catch (Exception ex)
            {
                ModLog.Warning($"主菜单按钮注入失败：{ex.Message}");
            }
        }

        private static void InjectOnce(PreRunScript pre)
        {
            if (pre == null || pre.loadButton == null)
            {
                ModLog.Warning("PreRunScript / loadButton 为空，跳过注入。");
                return;
            }

            // loadButton 位于默认隐藏的 runSettingsScreen 下，必须直接挂到 PreRunScript 根节点
            var parent = pre.transform;
            for (int i = 0; i < parent.childCount; i++)
            {
                var existing = parent.GetChild(i);
                if (existing.name == InjectedName)
                {
                    _injectedButton = existing.gameObject;
                    InjectedRect = existing as RectTransform;
                    ReplaceAllTexts(_injectedButton, I18n.T("app.menu_button"));
                    return;
                }
            }

            var clone = UnityEngine.Object.Instantiate(pre.loadButton.gameObject, parent, false);
            clone.name = InjectedName;
            clone.SetActive(true);

            var btn = clone.GetComponent<Button>();
            if (btn != null)
            {
                int n = btn.onClick.GetPersistentEventCount();
                for (int i = 0; i < n; i++)
                {
                    btn.onClick.SetPersistentListenerState(i, UnityEngine.Events.UnityEventCallState.Off);
                }
                btn.onClick.RemoveAllListeners();
                btn.interactable = true;
                btn.onClick.AddListener(InvokeOnClick);
            }

            DestroyLocalizers(clone);
            CleanupLoadButtonChildren(clone);
            ReplaceAllTexts(clone, I18n.T("app.menu_button"));
            clone.AddComponent<MenuButtonLabelSync>();

            var rt = clone.GetComponent<RectTransform>();
            if (rt != null)
            {
                rt.anchorMin = new Vector2(0.5f, 1f);
                rt.anchorMax = new Vector2(0.5f, 1f);
                rt.pivot = new Vector2(0.5f, 1f);
                rt.sizeDelta = new Vector2(280f, 96f);
                rt.anchoredPosition = new Vector2(0f, -20f);
                rt.localScale = Vector3.one;
            }

            PlaceBeforeOverlayScreens(pre, parent, clone.transform);
            _injectedButton = clone;
            InjectedRect = rt;

            ModLog.Info($"主菜单按钮已注入：parent={parent.name} sibling={clone.transform.GetSiblingIndex()}");
        }

        /// <summary>将按钮插入到全屏遮罩与子弹窗之前，避免遮挡开场警告、自定义设置与制作人员页。</summary>
        private static void PlaceBeforeOverlayScreens(PreRunScript pre, Transform parent, Transform buttonTransform)
        {
            int minSibling = int.MaxValue;
            ConsiderSibling(pre.creditsScreen != null ? pre.creditsScreen.transform : null, parent, buttonTransform, ref minSibling);
            ConsiderSibling(pre.runSettingsScreen != null ? pre.runSettingsScreen.transform : null, parent, buttonTransform, ref minSibling);
            ConsiderSibling(pre.contentWarning != null ? pre.contentWarning.transform : null, parent, buttonTransform, ref minSibling);
            ConsiderSibling(pre.cover != null ? pre.cover.transform : null, parent, buttonTransform, ref minSibling);
            ConsiderSibling(pre.tutorialWarning != null ? pre.tutorialWarning.transform : null, parent, buttonTransform, ref minSibling);
            ConsiderSibling(parent.Find("Credits"), parent, buttonTransform, ref minSibling);

            if (minSibling < int.MaxValue)
            {
                buttonTransform.SetSiblingIndex(minSibling);
            }
            else
            {
                buttonTransform.SetAsLastSibling();
            }
        }

        private static void ConsiderSibling(Transform candidate, Transform parent, Transform self, ref int minSibling)
        {
            if (candidate == null || candidate == self || candidate.parent != parent) return;
            int idx = candidate.GetSiblingIndex();
            if (idx < minSibling) minSibling = idx;
        }

        private static void InvokeOnClick()
        {
            try { _onClick?.Invoke(); }
            catch (Exception ex) { ModLog.Warning($"存档管理按钮点击异常：{ex.Message}"); }
        }

        /// <summary>移除克隆体上的本地化与悬停提示组件，防止原版逻辑覆盖按钮文案。</summary>
        private static void DestroyLocalizers(GameObject clone)
        {
            var components = clone.GetComponentsInChildren<MonoBehaviour>(includeInactive: true);
            foreach (var c in components)
            {
                if (c == null) continue;
                var name = c.GetType().Name;
                if (name == null) continue;
                if (name.IndexOf("Localiz", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("LocaleText", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("Tooltip", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    UnityEngine.Object.Destroy(c);
                }
            }
        }

        /// <summary>仅保留首个含文本组件的子节点，移除图标缩略图与多余附属子节点。</summary>
        private static void CleanupLoadButtonChildren(GameObject clone)
        {
            var rootTransform = clone.transform;
            Transform primaryTextChild = null;
            for (int i = 0; i < rootTransform.childCount; i++)
            {
                var child = rootTransform.GetChild(i);
                if (HasTextComponent(child))
                {
                    primaryTextChild = child;
                    break;
                }
            }

            for (int i = rootTransform.childCount - 1; i >= 0; i--)
            {
                var child = rootTransform.GetChild(i);
                if (child != primaryTextChild)
                {
                    UnityEngine.Object.Destroy(child.gameObject);
                }
            }
        }

        private static bool HasTextComponent(Transform root)
        {
            if (root.GetComponentInChildren<Text>(true) != null) return true;
            var allMb = root.GetComponentsInChildren<MonoBehaviour>(true);
            for (int i = 0; i < allMb.Length; i++)
            {
                var mb = allMb[i];
                var fn = mb != null ? mb.GetType().FullName : null;
                if (fn != null && fn.StartsWith("TMPro.TextMesh", StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        internal static void ReplaceAllTexts(GameObject clone, string newText)
        {
            bool first = true;

            var legacyTexts = clone.GetComponentsInChildren<Text>(includeInactive: true);
            foreach (var t in legacyTexts)
            {
                if (t == null) continue;
                t.text = first ? newText : "";
                first = false;
            }

            var allMb = clone.GetComponentsInChildren<MonoBehaviour>(includeInactive: true);
            foreach (var mb in allMb)
            {
                if (mb == null) continue;
                var type = mb.GetType();
                if (type.FullName == null || !type.FullName.StartsWith("TMPro.TextMesh", StringComparison.Ordinal)) continue;
                var prop = type.GetProperty("text", BindingFlags.Public | BindingFlags.Instance);
                if (prop == null || !prop.CanWrite) continue;
                try
                {
                    prop.SetValue(mb, first ? newText : "", null);
                    first = false;
                }
                catch { }
            }
        }

        private sealed class MenuButtonLabelSync : MonoBehaviour
        {
            private string _appliedText;
            private float _nextCheckAt;

            private void OnEnable() => SyncNow();

            private void Update()
            {
                if (Time.unscaledTime < _nextCheckAt) return;
                _nextCheckAt = Time.unscaledTime + 0.5f;
                SyncNow();
            }

            private void SyncNow()
            {
                string expected = I18n.T("app.menu_button");
                if (expected == _appliedText) return;
                _appliedText = expected;
                ReplaceAllTexts(gameObject, expected);
            }
        }
    }

    [HarmonyPatch(typeof(PreRunScript), "Start")]
    internal static class PreRunScriptStartPatch
    {
        private static void Postfix(PreRunScript __instance)
        {
            MenuButtonUiInjector.OnPreRunScriptStarted(__instance);
        }
    }
}
