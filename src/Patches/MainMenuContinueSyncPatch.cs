using System;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>同步主菜单原版继续按钮与 QoL MainMenuSavePanel 中 ContinueVanilla 的可点击态、图标、时长文案与角色索引。</summary>
    internal static class MainMenuContinueSyncPatch
    {
        private const string QolPanelTypeName = "QoL_Unknown.MainMenuSavePanel";
        private const string QolExpandIconName = "ExpandIcon";
        private const string QolContinueVanillaName = "ContinueVanilla";

        private static readonly FieldInfo PreRunSaveTimeTextField =
            typeof(PreRunScript).GetField("saveTimeText", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        private static readonly FieldInfo PreRunSavedCharacterField =
            typeof(PreRunScript).GetField("savedCharacter", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        private static bool _qolPatched;
        private static bool _qolResolved;
        private static Type _qolPanelType;
        private static FieldInfo _qolPanelObjField;
        private static PropertyInfo _tmpTextProperty;

        internal static void ResetPatchState()
        {
            _qolPatched = false;
            _qolResolved = false;
            _qolPanelType = null;
            _qolPanelObjField = null;
            _tmpTextProperty = null;
        }

        /// <summary>软依赖挂钩 QoL_Unknown.MainMenuSavePanel.Show()；未安装 QoL 时静默跳过。</summary>
        internal static void TryInstallQolPatch(Harmony harmony)
        {
            if (_qolPatched || harmony == null) return;

            try
            {
                Type panelType = ResolveQolPanelType();
                if (panelType == null) return;

                MethodInfo showMethod = panelType.GetMethod(
                    "Show",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                    binder: null,
                    types: Type.EmptyTypes,
                    modifiers: null);
                if (showMethod == null)
                {
                    ModLog.Debug("[MainMenuContinueSync] 未找到 QoL MainMenuSavePanel.Show()，跳过挂钩");
                    return;
                }

                if (!HasPostfix(showMethod, harmony.Id, nameof(OnQolMainMenuSavePanelShowPostfix)))
                {
                    harmony.Patch(
                        showMethod,
                        postfix: new HarmonyMethod(typeof(MainMenuContinueSyncPatch), nameof(OnQolMainMenuSavePanelShowPostfix)));
                }

                _qolPatched = true;
                ModLog.Debug("[MainMenuContinueSync] 已挂钩 QoL MainMenuSavePanel.Show() Postfix");
            }
            catch (Exception ex)
            {
                ModLog.Debug($"[MainMenuContinueSync] 挂钩 QoL MainMenuSavePanel.Show() 失败：{ex.Message}");
            }
        }

        /// <summary>主菜单恢复槽位后调用：按是否由 QoL 接管主菜单，分别刷新原版 loadButton 或 QoL ContinueVanilla。</summary>
        internal static void RefreshMainMenuContinueState()
        {
            try
            {
                PreRunScript preRun = PreRunScript.instance;
                if (preRun == null || preRun.loadButton == null) return;

                bool hasSave = SaveSystem.HasSave();
                SavePreviewData preview = hasSave ? TryReadVanillaSavePreview() : default;

                SyncPreRunSavedCharacter(preRun, preview);

                if (IsQolManagingMainMenu(preRun))
                {
                    // QoL 将主菜单 loadButton 改造为存档面板入口，始终保持可点击，避免误将其置灰。
                    preRun.loadButton.interactable = true;
                    SyncQolEntryVisuals(preRun, hasSave, preview);
                    SyncQolContinueButton(hasSave, preview);
                    return;
                }

                preRun.loadButton.interactable = hasSave;
                ApplyVanillaContinueVisuals(preRun, hasSave, preview);
            }
            catch (Exception ex)
            {
                ModLog.Debug($"[MainMenuContinueSync] 刷新主菜单继续按钮状态失败：{ex.Message}");
            }
        }

        /// <summary>同步已创建的 QoL MainMenuSavePanel 下 ContinueVanilla 按钮的可点击状态与显示内容。</summary>
        internal static void SyncQolContinueButton()
        {
            try
            {
                bool hasSave = SaveSystem.HasSave();
                SavePreviewData preview = hasSave ? TryReadVanillaSavePreview() : default;

                PreRunScript preRun = PreRunScript.instance;
                if (preRun != null)
                {
                    SyncPreRunSavedCharacter(preRun, preview);
                }

                SyncQolContinueButton(hasSave, preview);
            }
            catch (Exception ex)
            {
                ModLog.Debug($"[MainMenuContinueSync] 同步 QoL ContinueVanilla 失败：{ex.Message}");
            }
        }

        [HarmonyPostfix]
        private static void OnQolMainMenuSavePanelShowPostfix()
        {
            SyncQolContinueButton();
        }

        private static void SyncQolContinueButton(bool hasSave, SavePreviewData preview)
        {
            try
            {
                GameObject panelObj = TryGetQolPanelObj();
                if (panelObj == null) return;

                Transform continueTransform = panelObj.transform.Find(QolContinueVanillaName);
                if (continueTransform == null) return;

                Button continueButton = continueTransform.GetComponent<Button>();
                if (continueButton != null)
                {
                    continueButton.interactable = hasSave;
                }

                SyncClonedContinueVisuals(PreRunScript.instance, continueTransform, hasSave, preview);
            }
            catch (Exception ex)
            {
                ModLog.Debug($"[MainMenuContinueSync] 刷新 ContinueVanilla 节点异常：{ex.Message}");
            }
        }

        private static bool IsQolManagingMainMenu(PreRunScript preRun)
        {
            if (preRun != null && preRun.loadButton != null)
            {
                if (preRun.loadButton.transform.Find(QolExpandIconName) != null)
                {
                    return true;
                }
            }
            // 不能仅凭 QoL 类型已加载兜底：玩家关闭 qol_save_menu 时程序集仍在，
            // 必须以 ExpandIcon 或已构建的 panelObj 作为主菜单实际被接管的依据。
            return TryGetQolPanelObj() != null;
        }

        private static Type ResolveQolPanelType()
        {
            if (_qolPanelType != null) return _qolPanelType;
            // 若在插件扫描完成后仍未找到则缓存负向结果，避免每次刷新重复遍历程序集。
            if (_qolResolved && Interop.InteropBootstrap.PluginScanComplete) return null;

            _qolResolved = true;
            try
            {
                Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < assemblies.Length; i++)
                {
                    Assembly asm = assemblies[i];
                    if (asm == null) continue;
                    Type found = asm.GetType(QolPanelTypeName, throwOnError: false);
                    if (found != null)
                    {
                        _qolPanelType = found;
                        _qolPanelObjField = found.GetField("panelObj", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                        return _qolPanelType;
                    }
                }
            }
            catch (Exception ex)
            {
                ModLog.Debug($"[MainMenuContinueSync] 查找 {QolPanelTypeName} 失败：{ex.Message}");
            }
            return null;
        }

        private static GameObject TryGetQolPanelObj()
        {
            if (_qolPanelObjField == null)
            {
                Type panelType = ResolveQolPanelType();
                if (panelType == null || _qolPanelObjField == null) return null;
            }
            try
            {
                return _qolPanelObjField.GetValue(null) as GameObject;
            }
            catch (Exception ex)
            {
                ModLog.Debug($"[MainMenuContinueSync] 读取 MainMenuSavePanel.panelObj 失败：{ex.Message}");
                return null;
            }
        }

        private static bool HasPostfix(MethodBase target, string ownerId, string methodName)
        {
            Patches info = Harmony.GetPatchInfo(target);
            if (info?.Postfixes == null) return false;
            foreach (Patch patch in info.Postfixes)
            {
                if (patch.owner == ownerId && patch.PatchMethod?.Name == methodName)
                {
                    return true;
                }
            }
            return false;
        }

        private static void SyncPreRunSavedCharacter(PreRunScript preRun, SavePreviewData preview)
        {
            if (preRun == null || !preview.Parsed || PreRunSavedCharacterField == null) return;
            try
            {
                PreRunSavedCharacterField.SetValue(preRun, preview.SavedCharacter);
            }
            catch (Exception ex)
            {
                ModLog.Debug($"[MainMenuContinueSync] 同步 PreRunScript.savedCharacter 失败：{ex.Message}");
            }
        }

        private static void SyncQolEntryVisuals(PreRunScript preRun, bool hasSave, SavePreviewData preview)
        {
            if (preRun == null) return;
            // QoL SaveMenuPatcher 强制启用入口图标；仅在解析到有效层图标时更新 sprite，避免把入口图标关闭。
            if (preRun.loadButtonImage != null)
            {
                if (hasSave && preview.Parsed && preview.BiomeSprite != null)
                {
                    preRun.loadButtonImage.sprite = preview.BiomeSprite;
                }
                preRun.loadButtonImage.enabled = true;
            }
            TrySetTmpText(GetSaveTimeTextComponent(preRun), ResolveContinueLabelText(hasSave, preview));
        }

        private static void ApplyVanillaContinueVisuals(PreRunScript preRun, bool hasSave, SavePreviewData preview)
        {
            if (preRun == null) return;
            if (preRun.loadButtonImage != null)
            {
                if (hasSave && preview.Parsed)
                {
                    preRun.loadButtonImage.sprite = preview.BiomeSprite;
                    preRun.loadButtonImage.enabled = true;
                }
                else
                {
                    preRun.loadButtonImage.enabled = false;
                }
            }
            TrySetTmpText(GetSaveTimeTextComponent(preRun), ResolveContinueLabelText(hasSave, preview));
        }

        private static void SyncClonedContinueVisuals(
            PreRunScript preRun,
            Transform continueTransform,
            bool hasSave,
            SavePreviewData preview)
        {
            if (preRun == null || preRun.loadButton == null || continueTransform == null) return;

            Transform sourceRoot = preRun.loadButton.transform;

            if (preRun.loadButtonImage != null)
            {
                Transform clonedImageNode = FindMatchingNode(sourceRoot, preRun.loadButtonImage.transform, continueTransform);
                Image clonedImage = clonedImageNode != null ? clonedImageNode.GetComponent<Image>() : null;
                if (clonedImage != null)
                {
                    if (hasSave && preview.Parsed)
                    {
                        clonedImage.sprite = preview.BiomeSprite;
                        clonedImage.enabled = true;
                    }
                    else
                    {
                        clonedImage.enabled = false;
                    }
                }
            }

            Component sourceText = GetSaveTimeTextComponent(preRun);
            if (sourceText != null)
            {
                Transform clonedTextNode = FindMatchingNode(sourceRoot, sourceText.transform, continueTransform);
                if (clonedTextNode != null)
                {
                    Component clonedText = clonedTextNode.GetComponent(sourceText.GetType());
                    TrySetTmpText(clonedText, ResolveContinueLabelText(hasSave, preview));
                }
            }
        }

        private static Transform FindMatchingNode(Transform sourceRoot, Transform sourceTarget, Transform clonedRoot)
        {
            if (sourceRoot == null || sourceTarget == null || clonedRoot == null) return null;
            if (sourceTarget == sourceRoot) return clonedRoot;

            string relPath = BuildRelativePath(sourceRoot, sourceTarget);
            if (string.IsNullOrEmpty(relPath)) return null;
            return clonedRoot.Find(relPath);
        }

        private static string BuildRelativePath(Transform root, Transform target)
        {
            string path = target.name;
            Transform cursor = target.parent;
            while (cursor != null && cursor != root)
            {
                path = cursor.name + "/" + path;
                cursor = cursor.parent;
            }
            return cursor == root ? path : null;
        }

        private static Component GetSaveTimeTextComponent(PreRunScript preRun)
        {
            if (preRun == null || PreRunSaveTimeTextField == null) return null;
            try
            {
                return PreRunSaveTimeTextField.GetValue(preRun) as Component;
            }
            catch (Exception ex)
            {
                ModLog.Debug($"[MainMenuContinueSync] 读取 PreRunScript.saveTimeText 失败：{ex.Message}");
                return null;
            }
        }

        private static void TrySetTmpText(Component textComponent, string value)
        {
            if (textComponent == null || value == null) return;
            try
            {
                // 项目未直接引用 Unity.TextMeshPro，经反射写 TMP_Text.text 保持零额外编译依赖。
                if (_tmpTextProperty == null)
                {
                    _tmpTextProperty = textComponent.GetType().GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
                }
                _tmpTextProperty?.SetValue(textComponent, value);
            }
            catch (Exception ex)
            {
                ModLog.Debug($"[MainMenuContinueSync] 更新继续按钮文案失败：{ex.Message}");
            }
        }

        private static string ResolveContinueLabelText(bool hasSave, SavePreviewData preview)
        {
            string baseLabel;
            try
            {
                baseLabel = Locale.GetOther("savecontinuemenu");
            }
            catch
            {
                baseLabel = "Continue";
            }

            if (!hasSave) return baseLabel;
            if (!preview.Parsed) return "<color=\"red\">Save is corrupted!";
            return baseLabel + " (" + preview.RunTime.ToString("hh\\:mm\\:ss") + ")";
        }

        private static SavePreviewData TryReadVanillaSavePreview()
        {
            try
            {
                string savePath = Path.Combine(Application.persistentDataPath, "save.sv");
                if (!File.Exists(savePath)) return default;

                JObject obj = JObject.Parse(SaveSystem.Unzip(File.ReadAllBytes(savePath)));
                int biome = (int)obj["biome"];
                float runTimeSeconds = (float)obj["runTime"];
                int character = (int)obj["character"];
                Sprite sprite = Resources.Load<Sprite>("BiomeIcon/" + (biome + 1));

                return new SavePreviewData
                {
                    Parsed = true,
                    BiomeSprite = sprite,
                    RunTime = TimeSpan.FromSeconds(Mathf.Max(0f, runTimeSeconds)),
                    SavedCharacter = character,
                };
            }
            catch (Exception ex)
            {
                ModLog.Debug($"[MainMenuContinueSync] 解析 save.sv 预览信息失败：{ex.Message}");
                return default;
            }
        }

        private struct SavePreviewData
        {
            public bool Parsed;
            public Sprite BiomeSprite;
            public TimeSpan RunTime;
            public int SavedCharacter;
        }
    }
}
