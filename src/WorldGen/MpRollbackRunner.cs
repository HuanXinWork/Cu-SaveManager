using System;
using System.Collections;
using System.Reflection;
using CasualtiesUnknown.SaveManager.Interop;
using UnityEngine;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>多人回档主线程协程调度器：负责阶段二进局延迟缓冲与世界生成后的玩家坐标持续写回。</summary>
    internal sealed class MpRollbackRunner : MonoBehaviour
    {
        private static bool _resolveTried;
        private static MethodInfo _mIsWorldGenerated;

        private Coroutine _positionCoroutine;
        private Coroutine _reloadCoroutine;

        internal static MpRollbackRunner Instance { get; private set; }

        internal static void Ensure(Plugin plugin)
        {
            if (Instance != null || plugin == null) return;
            Instance = plugin.gameObject.AddComponent<MpRollbackRunner>();
        }

        private void Awake()
        {
            Instance = this;
            hideFlags = HideFlags.HideAndDontSave;
        }

        internal void RunOnceAfterWorldGenerated(Action apply, Action onComplete = null)
        {
            if (apply == null) return;
            StartCoroutine(ApplyOnceCoroutine(apply, onComplete));
        }

        /// <summary>等待世界生成完成后按固定间隔重试尚未就绪的玩家坐标，直至全员还原、超时或退出联机。</summary>
        internal void RunPositionRestoreWindow(
            Func<bool> stepRestore,
            Action onComplete,
            float timeoutSeconds = 15f,
            float retryIntervalSeconds = 0.25f)
        {
            if (stepRestore == null)
            {
                onComplete?.Invoke();
                return;
            }

            if (_positionCoroutine != null)
            {
                StopCoroutine(_positionCoroutine);
                _positionCoroutine = null;
            }

            _positionCoroutine = StartCoroutine(PositionRestoreWindowCoroutine(
                stepRestore, onComplete, timeoutSeconds, retryIntervalSeconds));
        }

        /// <summary>延迟等待客户端切回主菜单后再触发联机继续游戏，避免客户端仍在旧场景时丢弃开局广播。</summary>
        internal void ScheduleStageTwoReload(Action execute, float delaySeconds = 0.75f, float maxWaitSeconds = 2f)
        {
            if (execute == null) return;

            if (_reloadCoroutine != null)
            {
                StopCoroutine(_reloadCoroutine);
                _reloadCoroutine = null;
            }

            _reloadCoroutine = StartCoroutine(StageTwoReloadCoroutine(execute, delaySeconds, maxWaitSeconds));
        }

        private static IEnumerator ApplyOnceCoroutine(Action apply, Action onComplete)
        {
            yield return new WaitUntil(IsWorldGenerated);
            apply();
            onComplete?.Invoke();
        }

        private IEnumerator PositionRestoreWindowCoroutine(
            Func<bool> stepRestore,
            Action onComplete,
            float timeoutSeconds,
            float retryIntervalSeconds)
        {
            float elapsed = 0f;
            try
            {
                while (!IsWorldGenerated())
                {
                    if (!MultiplayerBridge.IsMultiplayerRunning() || elapsed >= timeoutSeconds)
                    {
                        yield break;
                    }
                    elapsed += Time.unscaledDeltaTime;
                    yield return null;
                }

                while (MultiplayerBridge.IsMultiplayerRunning())
                {
                    bool allRestored = false;
                    try
                    {
                        allRestored = stepRestore();
                    }
                    catch (Exception ex)
                    {
                        ModLog.Warning($"MpRollbackRunner 坐标重试异常：{ex.Message}");
                    }

                    if (allRestored || elapsed >= timeoutSeconds)
                    {
                        break;
                    }

                    float slice = 0f;
                    while (slice < retryIntervalSeconds)
                    {
                        if (!MultiplayerBridge.IsMultiplayerRunning())
                        {
                            yield break;
                        }
                        float dt = Time.unscaledDeltaTime;
                        slice += dt;
                        elapsed += dt;
                        yield return null;
                    }
                }
            }
            finally
            {
                _positionCoroutine = null;
                onComplete?.Invoke();
            }
        }

        private IEnumerator StageTwoReloadCoroutine(Action execute, float delaySeconds, float maxWaitSeconds)
        {
            float elapsed = 0f;
            while (elapsed < delaySeconds)
            {
                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }

            while (PreRunScript.instance == null && elapsed < maxWaitSeconds)
            {
                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }

            _reloadCoroutine = null;
            execute();
        }

        private static bool IsWorldGenerated()
        {
            MethodInfo method = ResolveIsWorldGenerated();
            if (method != null
                && InteropReflection.TryInvoke(method, null, Array.Empty<object>(), out object result)
                && result is bool generated)
            {
                return generated;
            }

            return WorldGeneration.world != null && !WorldGeneration.world.generatingWorld;
        }

        private static MethodInfo ResolveIsWorldGenerated()
        {
            if (_resolveTried) return _mIsWorldGenerated;
            if (TogetherReflection.ModAssembly == null) return null;

            _resolveTried = true;
            if (TogetherReflection.TryResolveType("CasualtiesTogetherUtils.Util", out Type utilType))
            {
                _mIsWorldGenerated = InteropReflection.FindMethod(utilType, "IsWorldGenerated");
            }

            if (_mIsWorldGenerated == null)
            {
                InteropDiagnostics.WarnOnce("MpRollbackRunner.IsWorldGenerated",
                    "CasualtiesTogetherUtils.Util.IsWorldGenerated() 未解析到，多人回档位置写回改用 WorldGeneration.world != null && !generatingWorld");
            }

            return _mIsWorldGenerated;
        }
    }
}
