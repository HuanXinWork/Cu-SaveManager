using System;
using System.Collections;
using System.Reflection;
using CasualtiesUnknown.SaveManager.Interop;
using UnityEngine;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>供 MpPositionRestorer 在主线程延迟写回位置：等世界生成结束后执行一次，再回调结束会话。</summary>
    /// <remarks>
    /// 世界生成结束判定走 CasualtiesTogetherUtils.Util.IsWorldGenerated()；
    /// 该成员缺失时回落到等价的本体判定（WorldGeneration.world 存在且不在 generatingWorld）。
    /// </remarks>
    internal sealed class MpRollbackRunner : MonoBehaviour
    {
        private static bool _resolveTried;
        private static MethodInfo _mIsWorldGenerated;

        internal static MpRollbackRunner Instance { get; private set; }

        internal static void Ensure(Plugin plugin)
        {
            if (Instance != null)
            {
                return;
            }

            if (plugin == null)
            {
                return;
            }

            Instance = plugin.gameObject.AddComponent<MpRollbackRunner>();
        }

        private void Awake()
        {
            Instance = this;
            hideFlags = HideFlags.HideAndDontSave;
        }

        /// <summary>等世界生成结束后执行 <paramref name="apply"/>，随后执行 <paramref name="onComplete"/>。</summary>
        internal void RunOnceAfterWorldGenerated(Action apply, Action onComplete = null)
        {
            if (apply == null)
            {
                return;
            }

            StartCoroutine(ApplyAfterWorldGenerated(apply, onComplete));
        }

        private static IEnumerator ApplyAfterWorldGenerated(Action apply, Action onComplete)
        {
            yield return new WaitUntil(IsWorldGenerated);
            apply();
            onComplete?.Invoke();
        }

        /// <summary>世界生成结束判定；对应 CasualtiesTogetherUtils.Util.IsWorldGenerated()。</summary>
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

        /// <summary>解析 CasualtiesTogetherUtils.Util.IsWorldGenerated()；缺失时记一次 WarnOnce 并返回 null。</summary>
        private static MethodInfo ResolveIsWorldGenerated()
        {
            if (_resolveTried)
            {
                return _mIsWorldGenerated;
            }

            if (TogetherReflection.ModAssembly == null)
            {
                // 联机模组尚未加载：不置解析标记，下次再试。
                return null;
            }

            _resolveTried = true;
            if (TogetherReflection.TryResolveType("CasualtiesTogetherUtils.Util", out Type utilType))
            {
                _mIsWorldGenerated = InteropReflection.FindMethod(utilType, "IsWorldGenerated");
            }

            if (_mIsWorldGenerated == null)
            {
                InteropDiagnostics.WarnOnce("MpRollbackRunner.IsWorldGenerated",
                    "CasualtiesTogetherUtils.Util.IsWorldGenerated() 未解析到，多人回档位置写回改用本体判定 WorldGeneration.world != null && !generatingWorld");
            }

            return _mIsWorldGenerated;
        }
    }
}
