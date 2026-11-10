using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CasualtiesUnknown.SaveManager.Interop;
using HarmonyLib;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>多人固定世界种子跨端广播：复用联机模组的种子公告包，在负载之后追加本模组种子。</summary>
    /// <remarks>
    /// 复用的联机模组成员：
    ///   · Together.ServerMain.Server_AnnounceSeed(IReadOnlyList&lt;knetid&gt;) —— 主机广播种子公告；
    ///   · Together.NetmsgId.GenerateWorld_SeedAnnounce —— 该公告的消息号（byte 枚举）；
    ///   · Together.WorldGeneration_GenerateWorld_MultiplayerPatch.firstworldgenparams —— 公告负载（LastBeforeGenerationState）；
    ///   · Together.Net.CreateWriter(byte) / Together.MyLiteNetLibExtensions.Put&lt;T&gt; / Together.Net.Server_SendToClientsVeryReliable
    ///     —— 主机接管发送时按原实现重建同一个包；
    ///   · Together.ClientMain.ClientReceiver__GenerateWorld_SeedAnnounce(knetid, NetDataReader) —— 客户端读回公告的接收器。
    /// 主机（self 引擎）接管 Server_AnnounceSeed：写入同一结构体后追加 1 个 int 种子再发出；
    /// 客户端在接收器之后读包尾 int，非 0 即激活逐阶段重置。非 self 引擎主机不接管（原版包无尾部 int），
    /// 客户端按 NetDataReader.AvailableBytes 判定并回落联机模组原生世界生成。
    /// 成员缺失时记一次 WarnOnce 并整体降级为「多人固定世界种子广播不可用」，不在运行时静默 no-op。
    /// </remarks>
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
            if (_patched)
            {
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
                // 联机模组尚未加载：不置 _patched，留待世界生成时重试。
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
                    missing.Add("Together.Net.CreateWriter(byte)");
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

                Harmony harmony = new Harmony("com.casualtiesUnknown.saveManager.mpSeedBroadcast");
                Type self = typeof(MpSeedBroadcast);
                harmony.Patch(_mAnnounceSeed, prefix: new HarmonyMethod(
                    self.GetMethod(nameof(AnnounceSeed_Prefix), BindingFlags.Static | BindingFlags.NonPublic)));
                harmony.Patch(_mSeedReceiver, postfix: new HarmonyMethod(
                    self.GetMethod(nameof(SeedAnnounce_Postfix), BindingFlags.Static | BindingFlags.NonPublic)));
                _patched = true;
                ModLog.Info("多人种子广播已挂载（接管 Together.ServerMain.Server_AnnounceSeed 追加种子 / 客户端读取激活）");
            }
            catch (Exception ex)
            {
                ModLog.Warning($"MpSeedBroadcast.TryPatch 失败：{ex.Message}");
            }
        }

        /// <summary>取 Together.NetmsgId.GenerateWorld_SeedAnnounce 的字节值；缺失时记入 <paramref name="missing"/>。</summary>
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

        /// <summary>取 Together.MyLiteNetLibExtensions.Put&lt;T&gt; 并按状态结构体闭合泛型。</summary>
        private static MethodInfo FindStateWriter(Type extensionsType, Type writerType, Type stateType)
        {
            if (extensionsType == null || writerType == null)
            {
                return null;
            }

            MethodInfo definition = extensionsType
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "Put"
                    && m.IsGenericMethodDefinition
                    && m.GetParameters().Length == 2
                    && InteropReflection.StripByRef(m.GetParameters()[0].ParameterType) == writerType);
            if (definition == null)
            {
                return null;
            }

            try
            {
                return definition.MakeGenericMethod(stateType);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 主机 self 引擎：接管公告，原结构体之后追加 CurrentSeed 后整包发出（return false 取代原方法）。
        /// 非 self 引擎或非主机放行原版（无尾部 int，客户端按 AvailableBytes 自然回落）。
        /// </summary>
        private static bool AnnounceSeed_Prefix(object[] __args)
        {
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
                // 接管失败则放行原版（发出不含尾部 int 的干净包），客户端据 AvailableBytes 回落，不会读到脏数据。
                ModLog.Warning($"MpSeedBroadcast.AnnounceSeed_Prefix 异常，回落原版广播：{ex.Message}");
                return true;
            }
        }

        /// <summary>客户端：种子公告包读完后若包尾还有 int 则读出；非 0 即激活逐阶段重置使世界与主机/同种子单机一致。</summary>
        private static void SeedAnnounce_Postfix(object[] __args)
        {
            try
            {
                if (MultiplayerBridge.IsServer())
                {
                    return;
                }

                if (__args == null || __args.Length < 2)
                {
                    return;
                }

                object reader = __args[1];
                if (reader == null)
                {
                    return;
                }

                int available = Convert.ToInt32(_pAvailableBytes.GetValue(reader));
                if (available < sizeof(int))
                {
                    SeededWorldEngine.MpReseedEnabled = false;
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
                    SeededWorldEngine.MpReseedEnabled = false;
                }
            }
            catch (Exception ex)
            {
                ModLog.Warning($"MpSeedBroadcast.SeedAnnounce_Postfix 异常：{ex.Message}");
            }
        }
    }
}
