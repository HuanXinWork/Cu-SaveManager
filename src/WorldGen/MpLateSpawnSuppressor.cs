using System;
using System.Reflection;
using CasualtiesUnknown.SaveManager.Interop;
using HarmonyLib;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>多人回档窗口期内抑制 Together.ServerMain.LateSpawnLocation(NetBody) 的坐标覆盖，并保留装备回收。</summary>
    internal static class MpLateSpawnSuppressor
    {
        private static bool _patched;
        private static MemberInfo _mNetBodyPlr;
        private static MemberInfo _mPlrId;
        private static FieldInfo _fKnetidId;
        private static MethodInfo _mReclaimGear;

        internal static void ResetPatchState() => _patched = false;

        internal static void TryPatch()
        {
            if (_patched) return;
            TogetherModHooksBridge.TryBindAll();
            if (TogetherModHooksBridge.HasSpawnPositionHook)
            {
                _patched = true;
                ModLog.Info("多人 LateSpawnLocation 抑制已由 Together.ModHooks.SpawnPositionOverride 原生接管");
                return;
            }

            try
            {
                Type serverType = TogetherReflection.FindType("ServerMain");
                Type netBodyType = TogetherReflection.FindType("NetBody");
                if (serverType == null || netBodyType == null) return;

                MethodInfo lateSpawn = InteropReflection.FindMethod(serverType, "LateSpawnLocation", netBodyType);
                if (lateSpawn == null)
                {
                    InteropDiagnostics.WarnOnce("MpLateSpawnSuppressor.Member",
                        "Together.ServerMain.LateSpawnLocation(NetBody) 未解析到，回档窗口期无法抑制晚出生位置覆盖");
                    _patched = true;
                    return;
                }

                ResolveReclaimGearSupport(netBodyType);

                Harmony harmony = new Harmony("com.casualtiesUnknown.saveManager.mpLateSpawnSuppressor");
                harmony.Patch(lateSpawn, prefix: new HarmonyMethod(typeof(MpLateSpawnSuppressor), nameof(SkipWhenRestoring)));
                _patched = true;
                ModLog.Info("多人 LateSpawnLocation 抑制已挂载");
            }
            catch (Exception ex)
            {
                ModLog.Warning($"MpLateSpawnSuppressor.TryPatch 失败：{ex.Message}");
            }
        }

        private static void ResolveReclaimGearSupport(Type netBodyType)
        {
            try
            {
                _mNetBodyPlr = (MemberInfo)InteropReflection.FindField(netBodyType, "plr")
                    ?? InteropReflection.FindProperty(netBodyType, "plr");
                Type scavPlayerType = TogetherReflection.FindType("ScavPlayer");
                if (scavPlayerType != null)
                {
                    _mPlrId = (MemberInfo)InteropReflection.FindProperty(scavPlayerType, "playerId")
                        ?? InteropReflection.FindField(scavPlayerType, "playerId");
                }
                Type knetidType = TogetherReflection.FindType("knetid");
                if (knetidType != null)
                {
                    _fKnetidId = InteropReflection.FindField(knetidType, "id");
                }
                Type checkpointType = TogetherReflection.FindType("PlayerCheckpoint");
                if (checkpointType != null)
                {
                    _mReclaimGear = InteropReflection.FindMethod(checkpointType, "ReclaimGear", typeof(ushort));
                }
            }
            catch { }
        }

        private static bool TryUnwrapPlayerId(object rawId, out ushort playerId)
        {
            if (rawId is ushort direct)
            {
                playerId = direct;
                return true;
            }
            if (rawId != null && _fKnetidId != null
                && InteropReflection.ReadMember(_fKnetidId, rawId) is ushort wrapped)
            {
                playerId = wrapped;
                return true;
            }
            playerId = 0;
            return false;
        }

        private static bool SkipWhenRestoring(object __0)
        {
            if (!MpPositionRestorer.ActiveSession || TogetherModHooksBridge.HasSpawnPositionHook) return true;

            try
            {
                if (__0 != null && _mNetBodyPlr != null && _mPlrId != null && _mReclaimGear != null)
                {
                    object plr = InteropReflection.ReadMember(_mNetBodyPlr, __0);
                    object rawId = plr != null ? InteropReflection.ReadMember(_mPlrId, plr) : null;
                    if (TryUnwrapPlayerId(rawId, out ushort playerId))
                    {
                        InteropReflection.TryInvoke(_mReclaimGear, null, new object[] { playerId }, out _);
                    }
                }
            }
            catch { }

            return false;
        }
    }
}
