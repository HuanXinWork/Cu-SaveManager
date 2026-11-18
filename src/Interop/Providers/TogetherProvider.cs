// CuSaveManager Interop
// Copyright (c) 2026 huanxin886
// SPDX-License-Identifier: MIT

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace CasualtiesUnknown.SaveManager.Interop
{
    /// <summary>Casualties: Together（CasualtiesMP.dll 5.5.3，GUID "CasualtiesMP"）反射提供方适配器。</summary>
    /// <remarks>
    /// 依赖类型：Together.Net / Multiplayer / NetBody / ScavPlayer / knetid / Plugin / ServerMain / WorldMP / SavesystemPatch。
    /// 契约能力缺失时整体不可用；扩展能力与 16 位读写原语缺失时仅单项降级。
    /// </remarks>
    internal sealed class TogetherProvider : IInteropProvider, IInteropSession, IInteropRoster, IInteropChannel, IInteropEvents, IInteropTypeRegistry
    {
        private const string TargetAssemblyName = "CasualtiesMP";

        private readonly object _sync = new object();
        private readonly List<Delegate> _callbacks = new List<Delegate>();

        // 双检锁标志：锁外读取需 acquire 语义以确保已解析成员字段可见。
        private volatile bool _resolved;
        private volatile bool _available;
        private volatile bool _ushortDegraded;

        private Assembly _assembly;
        private Type _knetidType;
        private Type _writerType;

        private PropertyInfo _netIsRunning;
        private PropertyInfo _netIsServer;
        private PropertyInfo _netIsClient;
        private PropertyInfo _netIsDedicatedServer;
        private PropertyInfo _netIsRunningSteam;
        private PropertyInfo _pluginSuccessfullyInitialized;
        private PropertyInfo _pluginForceDisableMod;
        private MethodInfo _netClientSend;
        private MethodInfo _netServerSendTo;
        private MethodInfo _netServerSendToClients;

        private MethodInfo _createNamedWriter;
        private MethodInfo _registerServerReceiver;
        private MethodInfo _registerClientReceiver;

        private PropertyInfo _netBodyBody;
        private MethodInfo _tryGetPlayerAndNetBody;

        private FieldInfo _scavLocalPlayer;
        private PropertyInfo _scavPlayerId;
        private FieldInfo _scavSteamId;
        private FieldInfo _scavPlayerName;
        private FieldInfo _scavSelectedCharacter;
        private FieldInfo _scavClientIdToPlayerDict;
        private FieldInfo _scavBodyToPlayerDict;
        private FieldInfo _scavAllLivingPlayers;
        private FieldInfo _scavAllDeadPlayers;
        private EventInfo _scavPlayerJoined;
        private EventInfo _scavPlayerLeft;
        private MethodInfo _scavGetPersistentId;
        private MethodInfo _scavTeleportCharacter;

        private PropertyInfo _serverAllClientIds;
        private PropertyInfo _serverAllClientIdsExceptHost;
        private MethodInfo _serverAnnounceAlert;
        private MethodInfo _serverCheckLayerFinish;

        private MethodInfo _multiplayerStatusLog;
        private MethodInfo _worldLoadVanillaGeneratedWorld;
        private MemberInfo _savesystemPendingLoadMpSave;

        private FieldInfo _knetidId;
        private ConstructorInfo _knetidCtor;

        private MethodInfo _putInt;
        private MethodInfo _putUInt;
        private MethodInfo _putUShort;
        private MethodInfo _putBool;
        private MethodInfo _putFloat;
        private MethodInfo _putString;
        private MethodInfo _putBytes;
        private MethodInfo _getInt;
        private MethodInfo _getUInt;
        private MethodInfo _getUShort;
        private MethodInfo _getBool;
        private MethodInfo _getFloat;
        private MethodInfo _getString;
        private MethodInfo _getBytes;
        private PropertyInfo _readerAvailableBytes;

        private object _deliveryReliable;
        private object _deliveryUnreliable;

        /// <inheritdoc/>
        string IInteropProvider.Name => _ushortDegraded ? "Together(ushort降级)" : "Together";

        /// <inheritdoc/>
        bool IInteropProvider.IsAvailable => EnsureResolved();

        /// <summary>目标程序集与契约能力依赖成员是否全部可用。</summary>
        internal bool IsPresent => EnsureResolved();

        /// <inheritdoc/>
        InteropCapability IInteropProvider.Capabilities =>
            InteropCapability.Session
            | InteropCapability.Roster
            | InteropCapability.Channel
            | InteropCapability.Events
            | InteropCapability.TypeRegistry;

        /// <inheritdoc/>
        int IInteropProvider.Priority => 0;

        /// <inheritdoc/>
        InteropSessionInfo IInteropSession.Info
        {
            get
            {
                if (!EnsureResolved()) return InteropSessionInfo.None;
                bool running = ReadBool(_netIsRunning);
                bool server = ReadBool(_netIsServer);
                InteropTransport transport = running
                    ? new InteropTransport(ReadBool(_netIsRunningSteam) ? "Steam" : "Direct")
                    : InteropTransport.None;
                return new InteropSessionInfo(running, server, transport);
            }
        }

        /// <inheritdoc/>
        InteropPlayerRef IInteropRoster.LocalPlayer
        {
            get
            {
                if (!EnsureResolved()) return InteropPlayerRef.None;
                object local = InteropReflection.ReadMember(_scavLocalPlayer, null);
                return local == null ? InteropPlayerRef.None : ToRef(local, true);
            }
        }

        /// <inheritdoc/>
        bool IInteropRoster.TryGetPlayer(ushort clientId, out InteropPlayerRef player)
        {
            player = InteropPlayerRef.None;
            if (!EnsureResolved() || !TryReadPlayer(clientId, out object raw)) return false;
            player = ToRef(raw, clientId, IsLocalPlayer(raw));
            return true;
        }

        /// <inheritdoc/>
        IReadOnlyList<InteropPlayerRef> IInteropRoster.GetPlayers()
        {
            List<InteropPlayerRef> players = new List<InteropPlayerRef>();
            if (!EnsureResolved()) return players;

            IDictionary dictionary = ReadClientIdToPlayerDict();
            if (dictionary == null) return players;

            object local = InteropReflection.ReadMember(_scavLocalPlayer, null);
            foreach (DictionaryEntry entry in dictionary)
            {
                if (entry.Value == null) continue;
                players.Add(ToRef(entry.Value, ReadClientId(entry.Key), ReferenceEquals(entry.Value, local)));
            }

            return players;
        }

        /// <inheritdoc/>
        bool IInteropRoster.TryGetPlayerByActor(object actor, out InteropPlayerRef player)
        {
            player = InteropPlayerRef.None;
            if (actor == null || !EnsureResolved()) return false;

            IDictionary dictionary = ReadClientIdToPlayerDict();
            if (dictionary == null) return false;

            object local = InteropReflection.ReadMember(_scavLocalPlayer, null);
            foreach (DictionaryEntry entry in dictionary)
            {
                object[] args = { entry.Key, null, null };
                if (!InteropReflection.TryInvoke(_tryGetPlayerAndNetBody, null, args, out object result)) continue;
                if (!(result is bool found) || !found || args[2] == null) continue;
                if (!ReferenceEquals(args[2], actor) && !ReferenceEquals(InteropReflection.ReadMember(_netBodyBody, args[2]), actor)) continue;

                player = ToRef(args[1], ReadClientId(entry.Key), ReferenceEquals(args[1], local));
                return true;
            }

            return false;
        }

        /// <inheritdoc/>
        IInteropWriter IInteropChannel.CreateWriter(string channel)
        {
            if (string.IsNullOrEmpty(channel) || !EnsureResolved()) return NullInteropWriter.Instance;
            if (!InteropReflection.TryInvoke(_createNamedWriter, null, new object[] { channel }, out object writer) || writer == null)
            {
                return NullInteropWriter.Instance;
            }

            return new TogetherWriter(this, writer);
        }

        /// <inheritdoc/>
        void IInteropChannel.SendToServer(IInteropWriter writer, bool reliable)
        {
            object native = ReadWriter(writer);
            if (native == null || !EnsureResolved()) return;
            object delivery = reliable ? _deliveryReliable : _deliveryUnreliable;
            InteropReflection.TryInvoke(_netClientSend, null, new object[] { delivery, native }, out _);
        }

        /// <inheritdoc/>
        void IInteropChannel.Broadcast(IInteropWriter writer, bool reliable)
        {
            object native = ReadWriter(writer);
            if (native == null || !EnsureResolved()) return;

            object clientIds = InteropReflection.ReadMember(_serverAllClientIds, null) ?? BuildClientIdArray();
            if (clientIds == null) return;

            object delivery = reliable ? _deliveryReliable : _deliveryUnreliable;
            InteropReflection.TryInvoke(_netServerSendToClients, null, new object[] { delivery, native, clientIds }, out _);
        }

        /// <inheritdoc/>
        void IInteropChannel.SendToClient(ushort clientId, IInteropWriter writer, bool reliable)
        {
            object native = ReadWriter(writer);
            if (native == null || !EnsureResolved()) return;

            object id = MakeKnetid(clientId);
            if (id == null) return;

            object delivery = reliable ? _deliveryReliable : _deliveryUnreliable;
            InteropReflection.TryInvoke(_netServerSendTo, null, new object[] { delivery, native, id }, out _);
        }

        /// <inheritdoc/>
        bool IInteropChannel.Register(string channel, Action<InteropPlayerRef, IInteropReader> onServer, Action<IInteropReader> onClient)
        {
            if (string.IsNullOrEmpty(channel) || (onServer == null && onClient == null) || !EnsureResolved())
            {
                return false;
            }

            bool registered = true;
            if (onServer != null) registered &= RegisterServerReceiver(channel, onServer);
            if (onClient != null) registered &= RegisterClientReceiver(channel, onClient);
            return registered;
        }

        /// <inheritdoc/>
        bool IInteropEvents.TrySubscribePlayerJoined(Action<InteropPlayerRef> handler)
        {
            if (handler == null || !EnsureResolved() || _scavPlayerJoined == null) return false;

            PlayerJoinedReceiver receiver = new PlayerJoinedReceiver(this, handler);
            MethodInfo callback = InteropReflection.FindMethod(typeof(PlayerJoinedReceiver), "Invoke", typeof(object));
            Delegate bound = InteropReflection.BindDelegate(_scavPlayerJoined.EventHandlerType, receiver, callback);
            if (bound == null) return false;

            try { _scavPlayerJoined.AddEventHandler(null, bound); }
            catch (Exception) { return false; }

            lock (_sync) { _callbacks.Add(bound); }
            return true;
        }

        /// <inheritdoc/>
        bool IInteropEvents.TrySubscribePlayerLeft(Action<InteropPlayerRef> handler)
        {
            if (handler == null || !EnsureResolved() || _scavPlayerLeft == null) return false;

            PlayerLeftReceiver receiver = new PlayerLeftReceiver(this, handler);
            MethodInfo callback = InteropReflection.FindMethod(typeof(PlayerLeftReceiver), "Invoke", typeof(object));
            Delegate bound = InteropReflection.BindDelegate(_scavPlayerLeft.EventHandlerType, receiver, callback);
            if (bound == null) return false;

            try { _scavPlayerLeft.AddEventHandler(null, bound); }
            catch (Exception) { return false; }

            lock (_sync) { _callbacks.Add(bound); }
            return true;
        }

        /// <inheritdoc/>
        bool IInteropTypeRegistry.TryResolveType(string fullName, out Type type)
        {
            type = null;
            EnsureResolved();
            if (_assembly == null) return false;
            type = InteropReflection.FindType(_assembly, fullName);
            return type != null;
        }

        /// <summary>联机会话是否运行中（Together.Net.IsRunning）。</summary>
        internal bool IsRunning
        {
            get { EnsureResolved(); return ReadBool(_netIsRunning); }
        }

        /// <summary>本端是否为服务端（Together.Net.IsServer）。</summary>
        internal bool IsServer
        {
            get { EnsureResolved(); return ReadBool(_netIsServer); }
        }

        /// <summary>本端是否为客户端（Together.Net.IsClient）。</summary>
        internal bool IsClient
        {
            get { EnsureResolved(); return ReadBool(_netIsClient); }
        }

        /// <summary>本端是否为专用服务端（Together.Net.IsDedicatedServer）。</summary>
        internal bool IsDedicatedServer
        {
            get { EnsureResolved(); return ReadBool(_netIsDedicatedServer); }
        }

        /// <summary>联机模组是否初始化完成且未被禁用（Together.Plugin.SUCCESFULLY_INITIALIZED / FORCE_DISABLE_MP_MOD）。</summary>
        internal bool IsModInitialized
        {
            get
            {
                EnsureResolved();
                if (!ReadBool(_pluginSuccessfullyInitialized)) return false;
                return _pluginForceDisableMod == null || !ReadBool(_pluginForceDisableMod);
            }
        }

        /// <summary>联机模组程序集；尚未加载时为 null。</summary>
        internal Assembly ModAssembly
        {
            get { EnsureResolved(); return _assembly; }
        }

        /// <summary>判断角色身体对象是否归属本机玩家；非联机时返回 true，专用服务端返回 false。</summary>
        internal bool IsLocalPlayerBody(object body)
        {
            if (!IsRunning) return true;
            if (IsDedicatedServer || body == null) return false;

            object local = InteropReflection.ReadMember(_scavLocalPlayer, null);
            if (local == null) return false;

            if (InteropReflection.ReadMember(_scavBodyToPlayerDict, null) is IDictionary bodyDict && bodyDict.Contains(body))
            {
                return ReferenceEquals(bodyDict[body], local);
            }

            return ((IInteropRoster)this).TryGetPlayerByActor(body, out InteropPlayerRef player)
                && player.IsValid
                && player.IsLocal;
        }

        /// <summary>写入 Together.SavesystemPatch.PendingLoadMpSave 静态标志。</summary>
        internal void SetPendingLoadMpSave(bool value)
        {
            EnsureResolved();
            MemberInfo member = _savesystemPendingLoadMpSave;
            if (member == null && _assembly != null)
            {
                Type patchType = InteropReflection.FindType(_assembly, "Together.SavesystemPatch");
                member = (MemberInfo)InteropReflection.FindField(patchType, "PendingLoadMpSave")
                    ?? InteropReflection.FindProperty(patchType, "PendingLoadMpSave");
            }

            if (member != null)
            {
                InteropReflection.TryWriteMember(member, null, value);
            }
        }

        /// <summary>获取本机玩家的联机持久 ID（Together.ScavPlayer.LocalPlayer.GetPersistentId()）。</summary>
        internal bool TryGetLocalPersistentId(out string persistentId)
        {
            persistentId = null;
            EnsureResolved();
            object local = InteropReflection.ReadMember(_scavLocalPlayer, null);
            if (local == null || _scavGetPersistentId == null) return false;
            if (!InteropReflection.TryInvoke(_scavGetPersistentId, local, Array.Empty<object>(), out object result)) return false;
            persistentId = result as string;
            return !string.IsNullOrEmpty(persistentId);
        }

        /// <summary>获取当前会话内全部玩家的身份快照（持久 ID / Steam ID / 玩家名）。</summary>
        internal List<InteropPlayerIdentity> GetPlayerIdentities()
        {
            List<InteropPlayerIdentity> identities = new List<InteropPlayerIdentity>();
            EnsureResolved();
            if (_scavGetPersistentId == null) return identities;

            IDictionary players = ReadClientIdToPlayerDict();
            if (players == null) return identities;

            object local = InteropReflection.ReadMember(_scavLocalPlayer, null);
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (DictionaryEntry entry in players)
            {
                object player = entry.Value;
                if (player == null) continue;
                if (!InteropReflection.TryInvoke(_scavGetPersistentId, player, Array.Empty<object>(), out object result)) continue;

                string persistentId = result as string;
                if (string.IsNullOrEmpty(persistentId) || !seen.Add(persistentId)) continue;

                object steamId = InteropReflection.ReadMember(_scavSteamId, player);
                string playerName = InteropReflection.ReadMember(_scavPlayerName, player) as string;
                identities.Add(new InteropPlayerIdentity(
                    ReadClientId(entry.Key),
                    persistentId,
                    steamId is ulong id ? id : 0UL,
                    playerName,
                    ReferenceEquals(player, local)));
            }

            return identities;
        }

        /// <summary>服务端按持久 ID 传送玩家（Together.ScavPlayer.Server_TeleportCharacter），返回实际成功人数。</summary>
        internal int RestorePlayerPositions(IReadOnlyDictionary<string, Vector2> positions)
        {
            if (positions == null || positions.Count == 0 || !IsRunning || !IsServer) return 0;

            EnsureResolved();
            if (_scavGetPersistentId == null || _scavTeleportCharacter == null) return 0;

            IDictionary players = InteropReflection.ReadMember(_scavBodyToPlayerDict, null) as IDictionary;
            if (players == null) return 0;

            int restored = 0;
            foreach (DictionaryEntry entry in players)
            {
                object player = entry.Value;
                if (player == null) continue;
                if (!InteropReflection.TryInvoke(_scavGetPersistentId, player, Array.Empty<object>(), out object result)) continue;

                string persistentId = result as string;
                if (string.IsNullOrEmpty(persistentId) || !positions.TryGetValue(persistentId, out Vector2 position)) continue;

                if (InteropReflection.TryInvoke(_scavTeleportCharacter, player, new object[] { position }, out _))
                {
                    restored++;
                }
            }

            return restored;
        }

        /// <summary>统计已死亡玩家数：优先读 Together.ScavPlayer.AllDeadPlayers，缺失时回退 BodyToPlayerDict 与 AllLivingPlayers 差值。</summary>
        internal int CountDeadPlayers()
        {
            EnsureResolved();
            if (InteropReflection.ReadMember(_scavAllDeadPlayers, null) is ICollection deadPlayers)
            {
                return deadPlayers.Count;
            }

            IDictionary allPlayers = InteropReflection.ReadMember(_scavBodyToPlayerDict, null) as IDictionary;
            ICollection livingPlayers = InteropReflection.ReadMember(_scavAllLivingPlayers, null) as ICollection;
            if (allPlayers == null || livingPlayers == null) return 0;
            return Math.Max(0, allPlayers.Count - livingPlayers.Count);
        }

        /// <summary>统计当前存活玩家数（Together.ScavPlayer.AllLivingPlayers）；不可用时返回 -1。</summary>
        internal int CountLivingPlayers()
        {
            EnsureResolved();
            if (InteropReflection.ReadMember(_scavAllLivingPlayers, null) is ICollection livingPlayers)
            {
                return livingPlayers.Count;
            }

            return -1;
        }

        /// <summary>服务端向远端客户端广播中央大字提示（Together.ServerMain.Server_AnnounceAlert）；P2P 主机排除自身避免双倍弹窗。</summary>
        internal bool TryAnnounceAlert(string message)
        {
            if (string.IsNullOrEmpty(message) || !IsRunning || !IsServer) return false;
            EnsureResolved();
            if (_serverAnnounceAlert == null) return false;
            object targets = IsDedicatedServer ? null : InteropReflection.ReadMember(_serverAllClientIdsExceptHost, null);
            return InteropReflection.TryInvoke(_serverAnnounceAlert, null, new object[] { message, true, true, targets }, out _);
        }

        /// <summary>调用联机继续游戏入口（Together.WorldMP.LoadVanillaGeneratedWorld）。</summary>
        internal bool TryContinueGame(bool loadsave)
        {
            EnsureResolved();
            if (_worldLoadVanillaGeneratedWorld == null)
            {
                InteropDiagnostics.WarnOnce("TogetherProvider.LoadVanillaGeneratedWorld", "Together.WorldMP.LoadVanillaGeneratedWorld(bool) 未解析到，联机继续游戏不可用");
                return false;
            }

            bool invoked = InteropReflection.TryInvoke(_worldLoadVanillaGeneratedWorld, null, new object[] { loadsave }, out object result);
            ModLog.Debug("Together.WorldMP.LoadVanillaGeneratedWorld(" + loadsave + ") 调用=" + invoked
                + " 返回值=" + (result == null ? "null/void" : result.ToString()));
            return invoked;
        }

        /// <summary>将选定角色编号写入本机玩家（Together.ScavPlayer.selectedCharacter，byte 范围 0..255）。</summary>
        internal bool TryApplyCharacterToLocalPlayer(int character)
        {
            if (character < 0 || character > byte.MaxValue) return false;

            EnsureResolved();
            object local = InteropReflection.ReadMember(_scavLocalPlayer, null);
            if (local == null)
            {
                ModLog.Debug("TryApplyCharacterToLocalPlayer：Together.ScavPlayer.LocalPlayer 不可用，跳过");
                return false;
            }

            if (_scavSelectedCharacter == null)
            {
                InteropDiagnostics.WarnOnce("TogetherProvider.selectedCharacter",
                    "Together.ScavPlayer.selectedCharacter 未解析到，无法同步联机本机玩家角色");
                return false;
            }

            if (!InteropReflection.TryWriteMember(_scavSelectedCharacter, local, (byte)character))
            {
                ModLog.Warning("TryApplyCharacterToLocalPlayer：写入 ScavPlayer.selectedCharacter 失败");
                return false;
            }

            return true;
        }

        /// <summary>写入联机状态消息日志（Together.Multiplayer.DoMultiplayerStatusMessageLog）。</summary>
        internal bool TryLogStatusMessage(string message)
        {
            EnsureResolved();
            if (_multiplayerStatusLog == null || string.IsNullOrEmpty(message)) return false;
            return InteropReflection.TryInvoke(_multiplayerStatusLog, null, new object[] { message }, out _);
        }

        /// <summary>服务端判定全队是否满足层底过层人数阈值（Together.ServerMain.CheckIfEnoughPeopleAreAtLayerFinish()）。</summary>
        internal bool TryCheckTeamAtLayerFinish(out bool atFinish)
        {
            atFinish = false;
            if (!IsRunning || !IsServer) return false;
            EnsureResolved();
            if (_serverCheckLayerFinish == null) return false;
            if (!InteropReflection.TryInvoke(_serverCheckLayerFinish, null, Array.Empty<object>(), out object result)
                || !(result is bool flag))
            {
                return false;
            }

            atFinish = flag;
            return true;
        }

        /// <summary>返回各依赖成员的解析状态摘要。</summary>
        internal string DescribeResolvedMembers()
        {
            EnsureResolved();
            return "assembly=" + State(_assembly)
                + " Together.Net.IsRunning=" + State(_netIsRunning)
                + " Together.Net.IsServer=" + State(_netIsServer)
                + " Together.Net.IsClient=" + State(_netIsClient)
                + " Together.Net.IsDedicatedServer=" + State(_netIsDedicatedServer)
                + " Together.Plugin.SUCCESFULLY_INITIALIZED=" + State(_pluginSuccessfullyInitialized)
                + " Together.Plugin.FORCE_DISABLE_MP_MOD=" + State(_pluginForceDisableMod)
                + " Together.ScavPlayer.LocalPlayer=" + State(_scavLocalPlayer)
                + " Together.ScavPlayer.playerId=" + State(_scavPlayerId)
                + " Together.ScavPlayer.SteamId=" + State(_scavSteamId)
                + " Together.ScavPlayer.playerName=" + State(_scavPlayerName)
                + " Together.ScavPlayer.selectedCharacter=" + State(_scavSelectedCharacter)
                + " Together.ScavPlayer.ClientIdToPlayerDict=" + State(_scavClientIdToPlayerDict)
                + " Together.ScavPlayer.BodyToPlayerDict=" + State(_scavBodyToPlayerDict)
                + " Together.ScavPlayer.AllLivingPlayers=" + State(_scavAllLivingPlayers)
                + " Together.ScavPlayer.AllDeadPlayers=" + State(_scavAllDeadPlayers)
                + " Together.ScavPlayer.GetPersistentId=" + State(_scavGetPersistentId)
                + " Together.ScavPlayer.Server_TeleportCharacter=" + State(_scavTeleportCharacter)
                + " Together.ScavPlayer.OnPlayerJoined=" + State(_scavPlayerJoined)
                + " Together.ScavPlayer.OnPlayerLeft=" + State(_scavPlayerLeft)
                + " Together.ServerMain.AllClientIds=" + State(_serverAllClientIds)
                + " Together.ServerMain.AllClientIdsExceptHost=" + State(_serverAllClientIdsExceptHost)
                + " Together.ServerMain.Server_AnnounceAlert=" + State(_serverAnnounceAlert)
                + " Together.ServerMain.CheckIfEnoughPeopleAreAtLayerFinish=" + State(_serverCheckLayerFinish)
                + " Together.Multiplayer.DoMultiplayerStatusMessageLog=" + State(_multiplayerStatusLog)
                + " Together.WorldMP.LoadVanillaGeneratedWorld=" + State(_worldLoadVanillaGeneratedWorld)
                + " Together.SavesystemPatch.PendingLoadMpSave=" + State(_savesystemPendingLoadMpSave);
        }

        /// <summary>首次访问时解析目标程序集及依赖成员并缓存；程序集尚未加载时不缓存以便后续重试。</summary>
        private bool EnsureResolved()
        {
            if (_resolved) return _available;

            lock (_sync)
            {
                if (_resolved) return _available;

                Assembly assembly = FindTargetAssembly();
                if (assembly == null) return false;

                List<string> missing = new List<string>();
                List<string> optionalMissing = new List<string>();
                _available = Resolve(assembly, missing, optionalMissing);
                if (!_available)
                {
                    InteropDiagnostics.WarnOnce("TogetherProvider", "目标程序集成员缺失：" + string.Join(", ", missing));
                }

                if (optionalMissing.Count > 0)
                {
                    InteropDiagnostics.WarnOnce("TogetherProvider.Optional", "可选成员缺失（对应能力关闭）：" + string.Join(", ", optionalMissing));
                }

                if (_available)
                {
                    ReportPrimitiveDegradation();
                }

                _resolved = true;
            }

            return _available;
        }

        private void ReportPrimitiveDegradation()
        {
            _ushortDegraded = _putUShort == null || _getUShort == null;
            if (_putUShort == null)
            {
                InteropDiagnostics.WarnOnce("TogetherProvider.PutUShort", "目标写入器缺少 Put(ushort)：PutUShort 降级为 32 位写入");
            }

            if (_getUShort == null)
            {
                InteropDiagnostics.WarnOnce("TogetherProvider.GetUShort", "目标读取器缺少 GetUShort()：GetUShort 返回 0");
            }
        }

        /// <summary>清空已缓存的反射成员并重新解析。</summary>
        internal bool TryRefresh()
        {
            ModLog.Debug("Together：TryRefresh 清空缓存并重新解析");
            lock (_sync)
            {
                _resolved = false;
                _available = false;
                _ushortDegraded = false;
                ClearCachedMembers();
            }

            return EnsureResolved();
        }

        private void ClearCachedMembers()
        {
            _assembly = null;
            _knetidType = null;
            _writerType = null;
            _netIsRunning = null;
            _netIsServer = null;
            _netIsClient = null;
            _netIsDedicatedServer = null;
            _netIsRunningSteam = null;
            _pluginSuccessfullyInitialized = null;
            _pluginForceDisableMod = null;
            _netClientSend = null;
            _netServerSendTo = null;
            _netServerSendToClients = null;
            _createNamedWriter = null;
            _registerServerReceiver = null;
            _registerClientReceiver = null;
            _netBodyBody = null;
            _tryGetPlayerAndNetBody = null;
            _scavLocalPlayer = null;
            _scavPlayerId = null;
            _scavSteamId = null;
            _scavPlayerName = null;
            _scavSelectedCharacter = null;
            _scavClientIdToPlayerDict = null;
            _scavBodyToPlayerDict = null;
            _scavAllLivingPlayers = null;
            _scavAllDeadPlayers = null;
            _scavPlayerJoined = null;
            _scavPlayerLeft = null;
            _scavGetPersistentId = null;
            _scavTeleportCharacter = null;
            _serverAllClientIds = null;
            _serverAllClientIdsExceptHost = null;
            _serverAnnounceAlert = null;
            _serverCheckLayerFinish = null;
            _multiplayerStatusLog = null;
            _worldLoadVanillaGeneratedWorld = null;
            _savesystemPendingLoadMpSave = null;
            _knetidId = null;
            _knetidCtor = null;
            _putInt = null;
            _putUInt = null;
            _putUShort = null;
            _putBool = null;
            _putFloat = null;
            _putString = null;
            _putBytes = null;
            _getInt = null;
            _getUInt = null;
            _getUShort = null;
            _getBool = null;
            _getFloat = null;
            _getString = null;
            _getBytes = null;
            _readerAvailableBytes = null;
            _deliveryReliable = null;
            _deliveryUnreliable = null;
        }

        private bool Resolve(Assembly assembly, List<string> missing, List<string> optionalMissing)
        {
            _assembly = assembly;
            Type netType = Require(InteropReflection.FindType(_assembly, "Together.Net"), "Together.Net", missing);
            Type multiplayerType = Require(InteropReflection.FindType(_assembly, "Together.Multiplayer"), "Together.Multiplayer", missing);
            Type netBodyType = Require(InteropReflection.FindType(_assembly, "Together.NetBody"), "Together.NetBody", missing);
            Type scavPlayerType = Require(InteropReflection.FindType(_assembly, "Together.ScavPlayer"), "Together.ScavPlayer", missing);
            _knetidType = Require(InteropReflection.FindType(_assembly, "Together.knetid"), "Together.knetid", missing);
            if (netType == null || multiplayerType == null || netBodyType == null || scavPlayerType == null || _knetidType == null)
            {
                ModLog.Debug("Together：assembly='" + assembly.GetName().Name + "' available=false capabilities=[] missing=[" + string.Join(", ", missing) + "]");
                return false;
            }

            _createNamedWriter = Require(InteropReflection.FindMethod(multiplayerType, "CreateNamedWriter", typeof(string)), "Together.Multiplayer.CreateNamedWriter(string)", missing);
            if (_createNamedWriter != null)
            {
                _writerType = _createNamedWriter.ReturnType;
            }

            _registerServerReceiver = Require(InteropReflection.FindMethodByCount(multiplayerType, "RegisterCustomServerReceiver", 2), "Together.Multiplayer.RegisterCustomServerReceiver", missing);
            Type readerType = ReceiverReaderType(_registerServerReceiver);
            if (readerType == null)
            {
                missing.Add("Together.Multiplayer.RegisterCustomServerReceiver 第 2 个形参的读取器泛型实参");
            }
            else
            {
                _registerClientReceiver = Require(InteropReflection.FindMethod(multiplayerType, "RegisterCustomClientReceiver", typeof(string), typeof(Action<>).MakeGenericType(readerType)), "Together.Multiplayer.RegisterCustomClientReceiver(string, Action<TReader>)", missing);
            }

            if (_writerType != null)
            {
                _netClientSend = Require(InteropReflection.FindMethod(netType, "Client_Send", null, _writerType), "Together.Net.Client_Send(传输模式, 写入器)", missing);
                _netServerSendTo = Require(InteropReflection.FindMethod(netType, "Server_SendTo", null, _writerType, _knetidType), "Together.Net.Server_SendTo(传输模式, 写入器, knetid)", missing);
                _netServerSendToClients = Require(InteropReflection.FindMethod(netType, "Server_SendToClients", null, _writerType, typeof(IEnumerable<>).MakeGenericType(_knetidType)), "Together.Net.Server_SendToClients(传输模式, 写入器, IEnumerable<knetid>)", missing);
            }

            Type deliveryType = _netClientSend == null ? null : InteropReflection.StripByRef(_netClientSend.GetParameters()[0].ParameterType);
            _deliveryReliable = InteropReflection.EnumValue(deliveryType, "ReliableOrdered");
            _deliveryUnreliable = InteropReflection.EnumValue(deliveryType, "Unreliable");
            if (_deliveryReliable == null || _deliveryUnreliable == null)
            {
                missing.Add("传输模式枚举的 ReliableOrdered/Unreliable 成员");
            }

            _netIsRunning = Require(InteropReflection.FindProperty(netType, "IsRunning"), "Together.Net.IsRunning", missing);
            _netIsServer = Require(InteropReflection.FindProperty(netType, "IsServer"), "Together.Net.IsServer", missing);
            _netIsRunningSteam = Require(InteropReflection.FindProperty(netType, "IsRunningSteam"), "Together.Net.IsRunningSteam", missing);
            _netIsClient = Optional(InteropReflection.FindProperty(netType, "IsClient"), "Together.Net.IsClient", optionalMissing);
            _netIsDedicatedServer = Optional(InteropReflection.FindProperty(netType, "IsDedicatedServer"), "Together.Net.IsDedicatedServer", optionalMissing);

            Type pluginType = InteropReflection.FindType(_assembly, "Together.Plugin");
            if (pluginType == null)
            {
                optionalMissing.Add("Together.Plugin");
            }
            else
            {
                _pluginSuccessfullyInitialized = Optional(InteropReflection.FindProperty(pluginType, "SUCCESFULLY_INITIALIZED"), "Together.Plugin.SUCCESFULLY_INITIALIZED", optionalMissing);
                _pluginForceDisableMod = Optional(InteropReflection.FindProperty(pluginType, "FORCE_DISABLE_MP_MOD"), "Together.Plugin.FORCE_DISABLE_MP_MOD", optionalMissing);
            }

            _netBodyBody = Require(InteropReflection.FindProperty(netBodyType, "body"), "Together.NetBody.body", missing);
            _tryGetPlayerAndNetBody = Require(InteropReflection.FindMethod(scavPlayerType, "TryGetPlayerAndNetBodyFromClientId", _knetidType, scavPlayerType, netBodyType), "Together.ScavPlayer.TryGetPlayerAndNetBodyFromClientId(knetid, out ScavPlayer, out NetBody)", missing);

            _scavLocalPlayer = Require(InteropReflection.FindField(scavPlayerType, "LocalPlayer"), "Together.ScavPlayer.LocalPlayer", missing);
            _scavPlayerId = Require(InteropReflection.FindProperty(scavPlayerType, "playerId"), "Together.ScavPlayer.playerId", missing);
            _scavSteamId = Require(InteropReflection.FindField(scavPlayerType, "SteamId"), "Together.ScavPlayer.SteamId", missing);
            _scavClientIdToPlayerDict = Require(InteropReflection.FindField(scavPlayerType, "ClientIdToPlayerDict"), "Together.ScavPlayer.ClientIdToPlayerDict", missing);
            _scavPlayerLeft = Require(InteropReflection.FindEvent(scavPlayerType, "OnPlayerLeft"), "Together.ScavPlayer.OnPlayerLeft", missing);
            _scavGetPersistentId = Require(InteropReflection.FindMethod(scavPlayerType, "GetPersistentId"), "Together.ScavPlayer.GetPersistentId()", missing);
            _scavPlayerName = Optional(InteropReflection.FindField(scavPlayerType, "playerName"), "Together.ScavPlayer.playerName", optionalMissing);
            _scavSelectedCharacter = Optional(InteropReflection.FindField(scavPlayerType, "selectedCharacter"), "Together.ScavPlayer.selectedCharacter", optionalMissing);
            _scavBodyToPlayerDict = Optional(InteropReflection.FindField(scavPlayerType, "BodyToPlayerDict"), "Together.ScavPlayer.BodyToPlayerDict", optionalMissing);
            _scavAllLivingPlayers = Optional(InteropReflection.FindField(scavPlayerType, "AllLivingPlayers"), "Together.ScavPlayer.AllLivingPlayers", optionalMissing);
            _scavAllDeadPlayers = Optional(InteropReflection.FindField(scavPlayerType, "AllDeadPlayers"), "Together.ScavPlayer.AllDeadPlayers", optionalMissing);
            _scavPlayerJoined = Optional(InteropReflection.FindEvent(scavPlayerType, "OnPlayerJoined"), "Together.ScavPlayer.OnPlayerJoined", optionalMissing);
            _scavTeleportCharacter = Optional(InteropReflection.FindMethod(scavPlayerType, "Server_TeleportCharacter", typeof(Vector2)), "Together.ScavPlayer.Server_TeleportCharacter(Vector2)", optionalMissing);

            Type serverMainType = InteropReflection.FindType(_assembly, "Together.ServerMain");
            if (serverMainType == null)
            {
                optionalMissing.Add("Together.ServerMain");
            }
            else
            {
                _serverAllClientIds = Optional(InteropReflection.FindProperty(serverMainType, "AllClientIds"), "Together.ServerMain.AllClientIds", optionalMissing);
                _serverAllClientIdsExceptHost = Optional(InteropReflection.FindProperty(serverMainType, "AllClientIdsExceptHost"), "Together.ServerMain.AllClientIdsExceptHost", optionalMissing);
                _serverAnnounceAlert = Optional(
                    InteropReflection.FindMethod(serverMainType, "Server_AnnounceAlert", typeof(string), typeof(bool), typeof(bool), typeof(IReadOnlyList<>).MakeGenericType(_knetidType)),
                    "Together.ServerMain.Server_AnnounceAlert(string, bool, bool, IReadOnlyList<knetid>)",
                    optionalMissing);
                _serverCheckLayerFinish = Optional(
                    InteropReflection.FindMethod(serverMainType, "CheckIfEnoughPeopleAreAtLayerFinish"),
                    "Together.ServerMain.CheckIfEnoughPeopleAreAtLayerFinish()",
                    optionalMissing);
            }

            _multiplayerStatusLog = Optional(InteropReflection.FindMethod(multiplayerType, "DoMultiplayerStatusMessageLog", typeof(string)), "Together.Multiplayer.DoMultiplayerStatusMessageLog(string)", optionalMissing);

            Type worldMpType = InteropReflection.FindType(_assembly, "Together.WorldMP");
            if (worldMpType == null)
            {
                optionalMissing.Add("Together.WorldMP");
            }
            else
            {
                _worldLoadVanillaGeneratedWorld = Optional(InteropReflection.FindMethod(worldMpType, "LoadVanillaGeneratedWorld", typeof(bool)), "Together.WorldMP.LoadVanillaGeneratedWorld(bool)", optionalMissing);
            }

            Type savesystemPatchType = InteropReflection.FindType(_assembly, "Together.SavesystemPatch");
            if (savesystemPatchType == null)
            {
                optionalMissing.Add("Together.SavesystemPatch");
            }
            else
            {
                MemberInfo pendingMember = (MemberInfo)InteropReflection.FindField(savesystemPatchType, "PendingLoadMpSave")
                    ?? InteropReflection.FindProperty(savesystemPatchType, "PendingLoadMpSave");
                _savesystemPendingLoadMpSave = Optional(pendingMember, "Together.SavesystemPatch.PendingLoadMpSave", optionalMissing);
            }

            _knetidId = Require(InteropReflection.FindField(_knetidType, "id"), "Together.knetid.id", missing);
            _knetidCtor = Require(InteropReflection.FindConstructor(_knetidType, typeof(ushort)), "Together.knetid(ushort)", missing);

            if (_writerType != null)
            {
                _putInt = Require(InteropReflection.FindMethod(_writerType, "Put", typeof(int)), "写入器 Put(int)", missing);
                _putUInt = Require(InteropReflection.FindMethod(_writerType, "Put", typeof(uint)), "写入器 Put(uint)", missing);
                _putUShort = InteropReflection.FindMethod(_writerType, "Put", typeof(ushort));
                _putBool = Require(InteropReflection.FindMethod(_writerType, "Put", typeof(bool)), "写入器 Put(bool)", missing);
                _putFloat = Require(InteropReflection.FindMethod(_writerType, "Put", typeof(float)), "写入器 Put(float)", missing);
                _putString = Require(InteropReflection.FindMethod(_writerType, "Put", typeof(string), typeof(int)), "写入器 Put(string, int)", missing);
                _putBytes = Require(InteropReflection.FindMethod(_writerType, "PutBytesWithLength", typeof(byte[])), "写入器 PutBytesWithLength(byte[])", missing);
            }

            if (readerType != null)
            {
                _getInt = Require(InteropReflection.FindMethod(readerType, "GetInt"), "读取器 GetInt()", missing);
                _getUInt = Require(InteropReflection.FindMethod(readerType, "GetUInt"), "读取器 GetUInt()", missing);
                _getUShort = InteropReflection.FindMethod(readerType, "GetUShort");
                _getBool = Require(InteropReflection.FindMethod(readerType, "GetBool"), "读取器 GetBool()", missing);
                _getFloat = Require(InteropReflection.FindMethod(readerType, "GetFloat"), "读取器 GetFloat()", missing);
                _getString = Require(InteropReflection.FindMethod(readerType, "GetString"), "读取器 GetString()", missing);
                _getBytes = Require(InteropReflection.FindMethod(readerType, "GetBytesWithLength"), "读取器 GetBytesWithLength()", missing);
                _readerAvailableBytes = Require(InteropReflection.FindProperty(readerType, "AvailableBytes"), "读取器 AvailableBytes", missing);
            }

            ModLog.Debug("Together：assembly='" + assembly.GetName().Name + "' available=" + (missing.Count == 0)
                + " capabilities=[" + InteropDiagnostics.FormatCapabilities(
                    InteropCapability.Session | InteropCapability.Roster | InteropCapability.Channel | InteropCapability.Events | InteropCapability.TypeRegistry) + "]"
                + " 已解析 " + CountResolved(
                    _createNamedWriter, _writerType, _registerServerReceiver, _registerClientReceiver,
                    _netClientSend, _netServerSendTo, _netServerSendToClients, _deliveryReliable, _deliveryUnreliable,
                    _netIsRunning, _netIsServer, _netIsRunningSteam, _netIsClient, _netIsDedicatedServer,
                    _pluginSuccessfullyInitialized, _pluginForceDisableMod,
                    _netBodyBody, _tryGetPlayerAndNetBody,
                    _scavLocalPlayer, _scavPlayerId, _scavSteamId, _scavClientIdToPlayerDict, _scavPlayerLeft, _scavGetPersistentId,
                    _scavPlayerName, _scavSelectedCharacter, _scavBodyToPlayerDict, _scavAllLivingPlayers, _scavAllDeadPlayers, _scavPlayerJoined, _scavTeleportCharacter,
                    _serverAllClientIds, _serverAllClientIdsExceptHost, _serverAnnounceAlert, _serverCheckLayerFinish, _multiplayerStatusLog, _worldLoadVanillaGeneratedWorld, _savesystemPendingLoadMpSave,
                    _knetidId, _knetidCtor,
                    _putInt, _putUInt, _putUShort, _putBool, _putFloat, _putString, _putBytes,
                    _getInt, _getUInt, _getUShort, _getBool, _getFloat, _getString, _getBytes, _readerAvailableBytes)
                + " 个成员 missing=[" + string.Join(", ", missing) + "] optionalMissing=[" + string.Join(", ", optionalMissing) + "]");

            return missing.Count == 0;
        }

        private static int CountResolved(params object[] members)
        {
            int resolved = 0;
            for (int i = 0; i < members.Length; i++)
            {
                if (members[i] != null) resolved++;
            }

            return resolved;
        }

        private static Assembly FindTargetAssembly()
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                if (string.Equals(assemblies[i].GetName().Name, TargetAssemblyName, StringComparison.Ordinal))
                {
                    return assemblies[i];
                }
            }

            return null;
        }

        private static Type ReceiverReaderType(MethodInfo register)
        {
            if (register == null) return null;
            ParameterInfo[] parameters = register.GetParameters();
            if (parameters.Length != 2 || !parameters[1].ParameterType.IsGenericType) return null;
            Type[] arguments = parameters[1].ParameterType.GetGenericArguments();
            return arguments.Length == 2 ? arguments[1] : null;
        }

        private static T Require<T>(T member, string name, List<string> missing) where T : class
        {
            if (member == null) missing.Add(name);
            return member;
        }

        private static T Optional<T>(T member, string name, List<string> missing) where T : class
        {
            if (member == null) missing.Add(name);
            return member;
        }

        private static bool ReadBool(MemberInfo member)
        {
            return InteropReflection.ReadMember(member, null) is bool flag && flag;
        }

        private static string State(object member) => member == null ? "missing" : "ok";

        private IDictionary ReadClientIdToPlayerDict()
        {
            return InteropReflection.ReadMember(_scavClientIdToPlayerDict, null) as IDictionary;
        }

        private bool TryReadPlayer(ushort clientId, out object player)
        {
            player = null;
            IDictionary dictionary = ReadClientIdToPlayerDict();
            if (dictionary == null) return false;

            object key = MakeKnetid(clientId);
            if (key == null || !dictionary.Contains(key)) return false;

            player = dictionary[key];
            return player != null;
        }

        private object MakeKnetid(ushort clientId)
        {
            InteropReflection.TryConstruct(_knetidCtor, new object[] { clientId }, out object knetid);
            return knetid;
        }

        private ushort ReadClientId(object knetid)
        {
            return InteropReflection.ReadMember(_knetidId, knetid) is ushort value ? value : (ushort)0;
        }

        private bool IsLocalPlayer(object player)
        {
            object local = InteropReflection.ReadMember(_scavLocalPlayer, null);
            return local != null && ReferenceEquals(local, player);
        }

        private InteropPlayerRef ToRef(object player, bool isLocal)
        {
            if (player == null || !InteropReflection.TryReadMember(_scavPlayerId, player, out object playerId) || playerId == null)
            {
                return InteropPlayerRef.None;
            }

            return ToRef(player, ReadClientId(playerId), isLocal);
        }

        private InteropPlayerRef ToRef(object player, ushort clientId, bool isLocal)
        {
            object steamId = InteropReflection.ReadMember(_scavSteamId, player);
            string name = InteropReflection.ReadMember(_scavPlayerName, player) as string;
            return new InteropPlayerRef(clientId, steamId is ulong id ? id : 0UL, string.IsNullOrEmpty(name) ? null : name, isLocal);
        }

        private object ReadWriter(IInteropWriter writer)
        {
            object native = writer?.NativeHandle;
            return native != null && _writerType != null && _writerType.IsInstanceOfType(native) ? native : null;
        }

        private Array BuildClientIdArray()
        {
            IDictionary dictionary = ReadClientIdToPlayerDict();
            if (dictionary == null || dictionary.Count == 0) return null;

            Array ids = Array.CreateInstance(_knetidType, dictionary.Count);
            int index = 0;
            foreach (object key in dictionary.Keys)
            {
                ids.SetValue(key, index++);
            }

            return ids;
        }

        private static Delegate BindCallback(MethodInfo register, object receiver, MethodInfo callback)
        {
            if (register == null || callback == null) return null;
            ParameterInfo[] parameters = register.GetParameters();
            return parameters.Length == 2 ? InteropReflection.BindDelegate(parameters[1].ParameterType, receiver, callback) : null;
        }

        private bool RegisterServerReceiver(string channel, Action<InteropPlayerRef, IInteropReader> handler)
        {
            ServerReceiver receiver = new ServerReceiver(this, handler);
            MethodInfo callback = InteropReflection.FindMethod(typeof(ServerReceiver), "Invoke", typeof(object), typeof(object));
            return InvokeRegister(_registerServerReceiver, channel, BindCallback(_registerServerReceiver, receiver, callback));
        }

        private bool RegisterClientReceiver(string channel, Action<IInteropReader> handler)
        {
            ClientReceiver receiver = new ClientReceiver(this, handler);
            MethodInfo callback = InteropReflection.FindMethod(typeof(ClientReceiver), "Invoke", typeof(object));
            return InvokeRegister(_registerClientReceiver, channel, BindCallback(_registerClientReceiver, receiver, callback));
        }

        private bool InvokeRegister(MethodInfo register, string channel, Delegate callback)
        {
            if (callback == null) return false;
            if (!InteropReflection.TryInvoke(register, null, new object[] { channel, callback }, out _))
            {
                InteropDiagnostics.WarnOnce("TogetherProvider.Register." + channel, "通道接收器登记失败：" + channel);
                return false;
            }

            lock (_sync) { _callbacks.Add(callback); }
            return true;
        }

        private void Put(object writer, MethodInfo method, object value)
        {
            InteropReflection.TryInvoke(method, writer, new object[] { value }, out _);
        }

        private void PutUShort(object writer, ushort value)
        {
            if (_putUShort != null)
            {
                Put(writer, _putUShort, value);
                return;
            }

            Put(writer, _putUInt, (uint)value);
        }

        private void PutString(object writer, string value)
        {
            InteropReflection.TryInvoke(_putString, writer, new object[] { value, 0 }, out _);
        }

        private void PutBytes(object writer, byte[] value)
        {
            byte[] safe = value == null || value.Length > ushort.MaxValue ? Array.Empty<byte>() : value;
            InteropReflection.TryInvoke(_putBytes, writer, new object[] { safe }, out _);
        }

        private T Get<T>(object reader, MethodInfo method, T fallback)
        {
            return InteropReflection.TryInvoke(method, reader, Array.Empty<object>(), out object result) && result is T value ? value : fallback;
        }

        private ushort GetUShort(object reader)
        {
            return _getUShort == null ? (ushort)0 : Get(reader, _getUShort, (ushort)0);
        }

        private int ReadRemainingBytes(object reader)
        {
            return InteropReflection.ReadMember(_readerAvailableBytes, reader) is int count ? count : 0;
        }

        private sealed class TogetherWriter : IInteropWriter
        {
            private readonly TogetherProvider _provider;
            private readonly object _native;

            internal TogetherWriter(TogetherProvider provider, object native)
            {
                _provider = provider;
                _native = native;
            }

            /// <inheritdoc/>
            object IInteropWriter.NativeHandle => _native;

            /// <inheritdoc/>
            void IInteropWriter.PutInt(int value) => _provider.Put(_native, _provider._putInt, value);

            /// <inheritdoc/>
            void IInteropWriter.PutUInt(uint value) => _provider.Put(_native, _provider._putUInt, value);

            /// <inheritdoc/>
            void IInteropWriter.PutUShort(ushort value) => _provider.PutUShort(_native, value);

            /// <inheritdoc/>
            void IInteropWriter.PutBool(bool value) => _provider.Put(_native, _provider._putBool, value);

            /// <inheritdoc/>
            void IInteropWriter.PutFloat(float value) => _provider.Put(_native, _provider._putFloat, value);

            /// <inheritdoc/>
            void IInteropWriter.PutString(string value) => _provider.PutString(_native, value);

            /// <inheritdoc/>
            void IInteropWriter.PutBytes(byte[] value) => _provider.PutBytes(_native, value);

            /// <inheritdoc/>
            void IInteropWriter.PutVector2(float x, float y)
            {
                _provider.Put(_native, _provider._putFloat, x);
                _provider.Put(_native, _provider._putFloat, y);
            }
        }

        private sealed class TogetherReader : IInteropReader
        {
            private readonly TogetherProvider _provider;
            private readonly object _native;

            internal TogetherReader(TogetherProvider provider, object native)
            {
                _provider = provider;
                _native = native;
            }

            /// <inheritdoc/>
            object IInteropReader.NativeHandle => _native;

            /// <inheritdoc/>
            int IInteropReader.RemainingBytes => _provider.ReadRemainingBytes(_native);

            /// <inheritdoc/>
            int IInteropReader.GetInt() => _provider.Get(_native, _provider._getInt, 0);

            /// <inheritdoc/>
            uint IInteropReader.GetUInt() => _provider.Get(_native, _provider._getUInt, 0u);

            /// <inheritdoc/>
            ushort IInteropReader.GetUShort() => _provider.GetUShort(_native);

            /// <inheritdoc/>
            bool IInteropReader.GetBool() => _provider.Get(_native, _provider._getBool, false);

            /// <inheritdoc/>
            float IInteropReader.GetFloat() => _provider.Get(_native, _provider._getFloat, 0f);

            /// <inheritdoc/>
            string IInteropReader.GetString() => _provider.Get(_native, _provider._getString, string.Empty);

            /// <inheritdoc/>
            byte[] IInteropReader.GetBytes() => _provider.Get(_native, _provider._getBytes, Array.Empty<byte>());

            /// <inheritdoc/>
            (float x, float y) IInteropReader.GetVector2()
            {
                float x = _provider.Get(_native, _provider._getFloat, 0f);
                float y = _provider.Get(_native, _provider._getFloat, 0f);
                return (x, y);
            }
        }

        private sealed class ServerReceiver
        {
            private readonly TogetherProvider _provider;
            private readonly Action<InteropPlayerRef, IInteropReader> _handler;

            internal ServerReceiver(TogetherProvider provider, Action<InteropPlayerRef, IInteropReader> handler)
            {
                _provider = provider;
                _handler = handler;
            }

            private void Invoke(object player, object reader)
            {
                _handler(_provider.ToRef(player, _provider.IsLocalPlayer(player)), new TogetherReader(_provider, reader));
            }
        }

        private sealed class ClientReceiver
        {
            private readonly TogetherProvider _provider;
            private readonly Action<IInteropReader> _handler;

            internal ClientReceiver(TogetherProvider provider, Action<IInteropReader> handler)
            {
                _provider = provider;
                _handler = handler;
            }

            private void Invoke(object reader)
            {
                _handler(new TogetherReader(_provider, reader));
            }
        }

        private sealed class PlayerJoinedReceiver
        {
            private readonly TogetherProvider _provider;
            private readonly Action<InteropPlayerRef> _handler;

            internal PlayerJoinedReceiver(TogetherProvider provider, Action<InteropPlayerRef> handler)
            {
                _provider = provider;
                _handler = handler;
            }

            private void Invoke(object player)
            {
                _handler(_provider.ToRef(player, _provider.IsLocalPlayer(player)));
            }
        }

        private sealed class PlayerLeftReceiver
        {
            private readonly TogetherProvider _provider;
            private readonly Action<InteropPlayerRef> _handler;

            internal PlayerLeftReceiver(TogetherProvider provider, Action<InteropPlayerRef> handler)
            {
                _provider = provider;
                _handler = handler;
            }

            private void Invoke(object player)
            {
                _handler(_provider.ToRef(player, _provider.IsLocalPlayer(player)));
            }
        }
    }
}
