using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CasualtiesUnknown.SaveManager.Interop;
using HarmonyLib;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>在 Together.ServerMain.Server_AnnounceSeed 公告包尾追加 4 字节 self 引擎种子，供客户端同步逐阶段确定化。</summary>
    internal static class MpSeedBroadcast
    {
        private static bool _patched;

        private static FieldInfo _fFirstParams;
        private static MethodInfo _mCreateWriter;
        private static MethodInfo _mPutState;
        private static MethodInfo _mWriterPutInt;
        private static MethodInfo _mSendToClients;
        private static MethodInfo _mReaderGetInt;
        private static PropertyInfo _pAvailableBytes;
        private static MethodInfo _mAnnounceSeed;
        private static MethodInfo _mSeedReceiver;
        private static byte _seedMsgId;

        internal static void TryPatch()
        {
            if (_patched) return;
            TogetherModHooksBridge.TryBindAll();
            if (TogetherModHooksBridge.HasWorldSeedHook)
            {
                _patched = true;
                ModLog.Info("多人种子广播已由 Together.ModHooks 世界种子钩子原生接管");
                return;
            }

            try
            {
                Type tNet = TogetherReflection.FindType("Net");
                Type tExtensions = TogetherReflection.FindType("MyLiteNetLibExtensions");
                Type tServer = TogetherReflection.FindType("ServerMain");
                Type tClient = TogetherReflection.FindType("ClientMain");
                Type tWorldGenPatch = TogetherReflection.FindType("WorldGeneration_GenerateWorld_MultiplayerPatch");
                Type tState = TogetherReflection.FindType("LastBeforeGenerationState");
                Type tMsgId = TogetherReflection.FindType("NetmsgId");
                Type tKnetid = TogetherReflection.FindType("knetid");
                if (tNet == null || tExtensions == null || tServer == null || tClient == null
                    || tWorldGenPatch == null || tState == null || tMsgId == null || tKnetid == null)
                {
                    return;
                }

                List<string> missing = new List<string>();

                _fFirstParams = InteropReflection.FindField(tWorldGenPatch, "firstworldgenparams");
                if (_fFirstParams == null || _fFirstParams.FieldType != tState)
                {
                    missing.Add("Together.WorldGeneration_GenerateWorld_MultiplayerPatch.firstworldgenparams");
                }

                _mAnnounceSeed = InteropReflection.FindMethod(
                    tServer,
                    "Server_AnnounceSeed",
                    typeof(IReadOnlyList<>).MakeGenericType(tKnetid));
                if (_mAnnounceSeed == null)
                {
                    missing.Add("Together.ServerMain.Server_AnnounceSeed(IReadOnlyList<knetid>)");
                }

                _mSeedReceiver = InteropReflection.FindMethodByCount(tClient, "ClientReceiver__GenerateWorld_SeedAnnounce", 2);
                if (_mSeedReceiver == null)
                {
                    missing.Add("Together.ClientMain.ClientReceiver__GenerateWorld_SeedAnnounce(knetid, NetDataReader)");
                }

                _seedMsgId = ResolveSeedMessageId(tMsgId, missing);

                Type msgIdType = Enum.GetUnderlyingType(tMsgId);
                _mCreateWriter = InteropReflection.FindMethod(tNet, "CreateWriter", msgIdType);
                if (_mCreateWriter == null)
                {
                    missing.Add("Together.Net.CreateWriter(NetmsgId)");
                }

                Type writerType = _mCreateWriter?.ReturnType;
                Type readerType = _mSeedReceiver?.GetParameters()[1].ParameterType;
                readerType = InteropReflection.StripByRef(readerType);

                _mPutState = FindStateWriter(tExtensions, writerType, tState);
                if (_mPutState == null)
                {
                    missing.Add("Together.MyLiteNetLibExtensions.Put<LastBeforeGenerationState>(NetDataWriter, T)");
                }

                _mSendToClients = writerType == null
                    ? null
                    : InteropReflection.FindMethod(
                        tNet,
                        "Server_SendToClientsVeryReliable",
                        writerType,
                        typeof(IEnumerable<>).MakeGenericType(tKnetid));
                if (_mSendToClients == null)
                {
                    missing.Add("Together.Net.Server_SendToClientsVeryReliable(NetDataWriter, IEnumerable<knetid>)");
                }

                _mWriterPutInt = writerType == null ? null : InteropReflection.FindMethod(writerType, "Put", typeof(int));
                if (_mWriterPutInt == null)
                {
                    missing.Add("NetDataWriter.Put(int)");
                }

                _mReaderGetInt = readerType == null ? null : InteropReflection.FindMethod(readerType, "GetInt");
                if (_mReaderGetInt == null)
                {
                    missing.Add("NetDataReader.GetInt()");
                }

                _pAvailableBytes = readerType == null ? null : InteropReflection.FindProperty(readerType, "AvailableBytes");
                if (_pAvailableBytes == null)
                {
                    missing.Add("NetDataReader.AvailableBytes");
                }

                if (missing.Count > 0)
                {
                    InteropDiagnostics.WarnOnce("MpSeedBroadcast.Members",
                        "多人固定世界种子广播不可用，缺失成员：" + string.Join("，", missing) + "（多人世界按联机模组原生种子生成）");
                    _patched = true;
                    return;
                }

                Harmony harmony = HarmonyBootstrap.Instance;
                Type self = typeof(MpSeedBroadcast);
                if (!HasPatch(Harmony.GetPatchInfo(_mAnnounceSeed)?.Prefixes, harmony.Id, nameof(AnnounceSeed_Prefix)))
                {
                    harmony.Patch(_mAnnounceSeed, prefix: new HarmonyMethod(
                        self.GetMethod(nameof(AnnounceSeed_Prefix), BindingFlags.Static | BindingFlags.NonPublic)));
                }
                if (!HasPatch(Harmony.GetPatchInfo(_mSeedReceiver)?.Postfixes, harmony.Id, nameof(SeedAnnounce_Postfix)))
                {
                    harmony.Patch(_mSeedReceiver, postfix: new HarmonyMethod(
                        self.GetMethod(nameof(SeedAnnounce_Postfix), BindingFlags.Static | BindingFlags.NonPublic)));
                }
                _patched = true;
                ModLog.Info("多人种子广播已挂载（Together.ServerMain.Server_AnnounceSeed / ClientReceiver）");
            }
            catch (Exception ex)
            {
                ModLog.Warning($"MpSeedBroadcast.TryPatch 失败：{ex.Message}");
            }
        }

        internal static void ResetPatchState() => _patched = false;

        private static bool HasPatch(IReadOnlyCollection<Patch> patches, string ownerId, string methodName)
        {
            if (patches == null) return false;
            foreach (Patch p in patches)
            {
                if (p.owner == ownerId && p.PatchMethod?.Name == methodName) return true;
            }
            return false;
        }

        private static byte ResolveSeedMessageId(Type msgIdType, List<string> missing)
        {
            try
            {
                object value = Enum.Parse(msgIdType, "GenerateWorld_SeedAnnounce", false);
                return Convert.ToByte(value);
            }
            catch (Exception)
            {
                missing.Add("Together.NetmsgId.GenerateWorld_SeedAnnounce");
                return 0;
            }
        }

        private static MethodInfo FindStateWriter(Type extensionsType, Type writerType, Type stateType)
        {
            if (extensionsType == null || writerType == null) return null;

            MethodInfo definition = extensionsType
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "Put"
                    && m.IsGenericMethodDefinition
                    && m.GetParameters().Length == 2
                    && InteropReflection.StripByRef(m.GetParameters()[0].ParameterType) == writerType);
            if (definition == null) return null;

            try
            {
                return definition.MakeGenericMethod(stateType);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool AnnounceSeed_Prefix(object[] __args)
        {
            if (TogetherModHooksBridge.HasWorldSeedHook) return true;
            object toWho = (__args != null && __args.Length > 0) ? __args[0] : null;
            if (!(SeededWorldEngine.IsActive && MultiplayerBridge.IsServer()))
            {
                SeededWorldEngine.MpReseedEnabled = false;
                return true;
            }

            try
            {
                int seed = SeededWorldEngine.CurrentSeed;
                object writer = _mCreateWriter.Invoke(null, new object[] { _seedMsgId });
                object state = _fFirstParams.GetValue(null);
                _mPutState.Invoke(null, new object[] { writer, state });
                _mWriterPutInt.Invoke(writer, new object[] { seed });
                _mSendToClients.Invoke(null, new object[] { writer, toWho });
                SeededWorldEngine.MpReseedEnabled = true;
                ModLog.Info($"多人种子广播：随种子公告包追加 saveManagerSeed={seed}");
                return false;
            }
            catch (Exception ex)
            {
                SeededWorldEngine.MpReseedEnabled = false;
                ModLog.Warning($"MpSeedBroadcast.AnnounceSeed_Prefix 异常，回落原版广播：{ex.Message}");
                return true;
            }
        }

        /// <summary>客户端读取包尾追加种子；原版包或种子为 0 时关闭 self 引擎以清除上一局残留状态。</summary>
        private static void SeedAnnounce_Postfix(object[] __args)
        {
            if (TogetherModHooksBridge.HasWorldSeedHook) return;
            try
            {
                if (MultiplayerBridge.IsServer()) return;
                if (__args == null || __args.Length < 2) return;

                object reader = __args[1];
                if (reader == null) return;

                int available = Convert.ToInt32(_pAvailableBytes.GetValue(reader));
                if (available < sizeof(int))
                {
                    SeededWorldEngine.Deactivate();
                    return;
                }

                int seed = (int)_mReaderGetInt.Invoke(reader, null);
                if (seed != 0)
                {
                    SeededWorldEngine.Activate(seed, "");
                    SeededWorldEngine.MpReseedEnabled = true;
                    ModLog.Info($"多人种子接收：激活逐阶段重置 seed={seed}");
                }
                else
                {
                    SeededWorldEngine.Deactivate();
                }
            }
            catch (Exception ex)
            {
                ModLog.Warning($"MpSeedBroadcast.SeedAnnounce_Postfix 异常：{ex.Message}");
            }
        }
    }
}
