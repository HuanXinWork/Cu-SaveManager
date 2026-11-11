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
    /// <summary>Casualties: Together 的提供方适配器，全程通过反射访问目标程序集。</summary>
    /// <remarks>
    /// 目标程序集：CasualtiesMP.dll，BepInEx 插件 GUID "CasualtiesMP"，版本 5.5.0。
    /// 依赖的目标类型：Together.Net、Together.Multiplayer、Together.NetBody、Together.ScavPlayer、Together.knetid、Together.Plugin、Together.ServerMain、Together.WorldMP。
    /// 写入器与读取器类型不直接出现：写入器类型取自 Together.Multiplayer.CreateNamedWriter(string) 的返回类型，
    /// 读取器类型取自 Together.Multiplayer.RegisterCustomServerReceiver 第 2 个形参的泛型实参，传输模式枚举取自 Together.Net.Client_Send 的第 1 个形参类型。
    /// 发送方法形参带 in，反射得到的是 by-ref 类型，因此成员一律按「名称 + 形参个数 + 剥掉 in/ref 后的元素类型」匹配。
    /// 契约能力（会话 / 名册 / 通道 / 事件 / 类型解析）依赖的成员缺失时整个提供方判为不可用；
    /// 契约未覆盖的联机模组专有能力（专用服务端、模组初始化状态、持久 ID、传送、死亡计数、广播提示、状态日志、继续游戏）
    /// 依赖的成员单独解析，缺失只关闭对应能力，不影响其余能力。
    /// 目标程序集尚未加载不计入失败，下次访问重试；成员缺失项只在首次解析后警告一次。
    /// 16 位原语（写入器 Put(ushort)、读取器 GetUShort()）例外：缺失时只降级该原语，提供方与其余能力保持可用。
    /// </remarks>
    internal sealed class TogetherProvider : IInteropProvider, IInteropSession, IInteropRoster, IInteropChannel, IInteropEvents, IInteropTypeRegistry
    {
        private const string TargetAssemblyName = "CasualtiesMP";

        private readonly object _sync = new object();
        private readonly List<Delegate> _callbacks = new List<Delegate>();

        // 三个标志在锁内写、锁外读（EnsureResolved 的双检锁），必须带 volatile：
        // 锁外读取只做 acquire，否则看到 _resolved 为 true 时其余成员字段仍可能不可见，表现为静默丢包。
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
        private FieldInfo _scavClientIdToPlayerDict;
        private FieldInfo _scavBodyToPlayerDict;
        private FieldInfo _scavAllLivingPlayers;
        private EventInfo _scavPlayerJoined;
        private EventInfo _scavPlayerLeft;
        private MethodInfo _scavGetPersistentId;
        private MethodInfo _scavTeleportCharacter;

        private PropertyInfo _serverAllClientIds;
        private MethodInfo _serverAnnounceAlert;

        private MethodInfo _multiplayerStatusLog;
        private MethodInfo _worldLoadVanillaGeneratedWorld;

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
        /// <remarks>16 位原语降级时名称附标记，启动自检行据此输出 Together(ushort降级):ok。</remarks>
        string IInteropProvider.Name => _ushortDegraded ? "Together(ushort降级)" : "Together";

        /// <inheritdoc/>
        bool IInteropProvider.IsAvailable => EnsureResolved();

        /// <summary>目标程序集与契约能力依赖的成员是否全部解析到。</summary>
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
                if (!EnsureResolved())
                {
                    return InteropSessionInfo.None;
                }

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
                if (!EnsureResolved())
                {
                    return InteropPlayerRef.None;
                }

                object local = InteropReflection.ReadMember(_scavLocalPlayer, null);
                return local == null ? InteropPlayerRef.None : ToRef(local, true);
            }
        }

        /// <inheritdoc/>
        bool IInteropRoster.TryGetPlayer(ushort clientId, out InteropPlayerRef player)
        {
            player = InteropPlayerRef.None;
            if (!EnsureResolved() || !TryReadPlayer(clientId, out object raw))
            {
                return false;
            }

            player = ToRef(raw, clientId, IsLocalPlayer(raw));
            return true;
        }

        /// <inheritdoc/>
        IReadOnlyList<InteropPlayerRef> IInteropRoster.GetPlayers()
        {
            List<InteropPlayerRef> players = new List<InteropPlayerRef>();
            if (!EnsureResolved())
            {
                return players;
            }

            IDictionary dictionary = ReadClientIdToPlayerDict();
            if (dictionary == null)
            {
                return players;
            }

            object local = InteropReflection.ReadMember(_scavLocalPlayer, null);
            foreach (DictionaryEntry entry in dictionary)
            {
                if (entry.Value == null)
                {
                    continue;
                }

                players.Add(ToRef(entry.Value, ReadClientId(entry.Key), ReferenceEquals(entry.Value, local)));
            }

            return players;
        }

        /// <remarks>角色对象可以是目标插件的 NetBody，也可以是 NetBody.body 指向的身体对象。</remarks>
        bool IInteropRoster.TryGetPlayerByActor(object actor, out InteropPlayerRef player)
        {
            player = InteropPlayerRef.None;
            if (actor == null || !EnsureResolved())
            {
                return false;
            }

            IDictionary dictionary = ReadClientIdToPlayerDict();
            if (dictionary == null)
            {
                return false;
            }

            object local = InteropReflection.ReadMember(_scavLocalPlayer, null);
            foreach (DictionaryEntry entry in dictionary)
            {
                object[] args = { entry.Key, null, null };
                if (!InteropReflection.TryInvoke(_tryGetPlayerAndNetBody, null, args, out object result))
                {
                    continue;
                }

                if (!(result is bool found) || !found || args[2] == null)
                {
                    continue;
                }

                if (!ReferenceEquals(args[2], actor) && !ReferenceEquals(InteropReflection.ReadMember(_netBodyBody, args[2]), actor))
                {
                    continue;
                }

                player = ToRef(args[1], ReadClientId(entry.Key), ReferenceEquals(args[1], local));
                return true;
            }

            return false;
        }

        /// <inheritdoc/>
        IInteropWriter IInteropChannel.CreateWriter(string channel)
        {
            if (string.IsNullOrEmpty(channel) || !EnsureResolved())
            {
                return NullInteropWriter.Instance;
            }

            if (!InteropReflection.TryInvoke(_createNamedWriter, null, new object[] { channel }, out object writer) || writer == null)
            {
                return NullInteropWriter.Instance;
            }

            return new TogetherWriter(this, writer);
        }

        /// <remarks>可靠标志映射到传输模式枚举的 ReliableOrdered 与 Unreliable 成员。</remarks>
        void IInteropChannel.SendToServer(IInteropWriter writer, bool reliable)
        {
            object native = ReadWriter(writer);
            if (native == null || !EnsureResolved())
            {
                return;
            }

            object delivery = reliable ? _deliveryReliable : _deliveryUnreliable;
            InteropReflection.TryInvoke(_netClientSend, null, new object[] { delivery, native }, out object ignored);
        }

        /// <remarks>广播目标优先取自 Together.ServerMain.AllClientIds，该成员缺失时回落到 Together.ScavPlayer.ClientIdToPlayerDict 的键集合。</remarks>
        void IInteropChannel.Broadcast(IInteropWriter writer, bool reliable)
        {
            object native = ReadWriter(writer);
            if (native == null || !EnsureResolved())
            {
                return;
            }

            object clientIds = InteropReflection.ReadMember(_serverAllClientIds, null) ?? BuildClientIdArray();
            if (clientIds == null)
            {
                return;
            }

            object delivery = reliable ? _deliveryReliable : _deliveryUnreliable;
            InteropReflection.TryInvoke(_netServerSendToClients, null, new object[] { delivery, native, clientIds }, out object ignored);
        }

        /// <inheritdoc/>
        void IInteropChannel.SendToClient(ushort clientId, IInteropWriter writer, bool reliable)
        {
            object native = ReadWriter(writer);
            if (native == null || !EnsureResolved())
            {
                return;
            }

            object id = MakeKnetid(clientId);
            if (id == null)
            {
                return;
            }

            object delivery = reliable ? _deliveryReliable : _deliveryUnreliable;
            InteropReflection.TryInvoke(_netServerSendTo, null, new object[] { delivery, native, id }, out object ignored);
        }

        /// <inheritdoc/>
        bool IInteropChannel.Register(string channel, Action<InteropPlayerRef, IInteropReader> onServer, Action<IInteropReader> onClient)
        {
            if (string.IsNullOrEmpty(channel) || (onServer == null && onClient == null) || !EnsureResolved())
            {
                return false;
            }

            bool registered = true;
            if (onServer != null)
            {
                registered &= RegisterServerReceiver(channel, onServer);
            }

            if (onClient != null)
            {
                registered &= RegisterClientReceiver(channel, onClient);
            }

            return registered;
        }

        /// <summary>订阅玩家加入；对应 Together.ScavPlayer.OnPlayerJoined。</summary>
        bool IInteropEvents.TrySubscribePlayerJoined(Action<InteropPlayerRef> handler)
        {
            if (handler == null || !EnsureResolved() || _scavPlayerJoined == null)
            {
                return false;
            }

            PlayerJoinedReceiver receiver = new PlayerJoinedReceiver(this, handler);
            MethodInfo callback = InteropReflection.FindMethod(typeof(PlayerJoinedReceiver), "Invoke", typeof(object));
            Delegate bound = InteropReflection.BindDelegate(_scavPlayerJoined.EventHandlerType, receiver, callback);
            if (bound == null)
            {
                return false;
            }

            try
            {
                _scavPlayerJoined.AddEventHandler(null, bound);
            }
            catch (Exception)
            {
                return false;
            }

            _callbacks.Add(bound);
            return true;
        }

        /// <inheritdoc/>
        bool IInteropEvents.TrySubscribePlayerLeft(Action<InteropPlayerRef> handler)
        {
            if (handler == null || !EnsureResolved())
            {
                return false;
            }

            PlayerLeftReceiver receiver = new PlayerLeftReceiver(this, handler);
            MethodInfo callback = InteropReflection.FindMethod(typeof(PlayerLeftReceiver), "Invoke", typeof(object));
            Delegate bound = InteropReflection.BindDelegate(_scavPlayerLeft.EventHandlerType, receiver, callback);
            if (bound == null)
            {
                return false;
            }

            try
            {
                _scavPlayerLeft.AddEventHandler(null, bound);
            }
            catch (Exception)
            {
                return false;
            }

            _callbacks.Add(bound);
            return true;
        }

        /// <remarks>只用 Assembly.GetType 查目标程序集，不扫描 AppDomain；类型成员缺失时仍可解析类型。</remarks>
        bool IInteropTypeRegistry.TryResolveType(string fullName, out Type type)
        {
            type = null;
            EnsureResolved();
            if (_assembly == null)
            {
                return false;
            }

            type = InteropReflection.FindType(_assembly, fullName);
            return type != null;
        }

        /// <summary>联机会话是否运行；对应 Together.Net.IsRunning。</summary>
        internal bool IsRunning
        {
            get
            {
                EnsureResolved();
                return ReadBool(_netIsRunning);
            }
        }

        /// <summary>本端是否为服务端（主机或专用服务端）；对应 Together.Net.IsServer。</summary>
        internal bool IsServer
        {
            get
            {
                EnsureResolved();
                return ReadBool(_netIsServer);
            }
        }

        /// <summary>本端是否为客户端；对应 Together.Net.IsClient。</summary>
        internal bool IsClient
        {
            get
            {
                EnsureResolved();
                return ReadBool(_netIsClient);
            }
        }

        /// <summary>本端是否为专用服务端；对应 Together.Net.IsDedicatedServer。</summary>
        internal bool IsDedicatedServer
        {
            get
            {
                EnsureResolved();
                return ReadBool(_netIsDedicatedServer);
            }
        }

        /// <summary>联机模组是否初始化成功且未被强制禁用；对应 Together.Plugin.SUCCESFULLY_INITIALIZED 与 Together.Plugin.FORCE_DISABLE_MP_MOD。</summary>
        internal bool IsModInitialized
        {
            get
            {
                EnsureResolved();
                if (!ReadBool(_pluginSuccessfullyInitialized))
                {
                    return false;
                }

                return _pluginForceDisableMod == null || !ReadBool(_pluginForceDisableMod);
            }
        }

        /// <summary>联机模组程序集；目标程序集尚未加载时为 null。</summary>
        internal Assembly ModAssembly
        {
            get
            {
                EnsureResolved();
                return _assembly;
            }
        }

        /// <summary>本机玩家在联机模组里的持久 ID，用于定位 mp_save 下的玩家子目录。</summary>
        /// <remarks>
        /// 链路：Together.ScavPlayer.LocalPlayer → Together.ScavPlayer.GetPersistentId()。
        /// 该方法在 Steam 传输下返回 "STEAM_&lt;steamId&gt;"，其它传输下返回 "NAME_&lt;playerName 的十六进制&gt;"。
        /// 不区分服务端与客户端：主机同样需要自己的存档子目录。
        /// </remarks>
        internal bool TryGetLocalPersistentId(out string persistentId)
        {
            persistentId = null;
            EnsureResolved();
            object local = InteropReflection.ReadMember(_scavLocalPlayer, null);
            if (local == null || _scavGetPersistentId == null)
            {
                return false;
            }

            if (!InteropReflection.TryInvoke(_scavGetPersistentId, local, Array.Empty<object>(), out object result))
            {
                return false;
            }

            persistentId = result as string;
            return !string.IsNullOrEmpty(persistentId);
        }

        /// <summary>当前会话内全部玩家的身份快照（持久 ID / Steam ID / 玩家名）。</summary>
        /// <remarks>
        /// 链路：Together.ScavPlayer.ClientIdToPlayerDict → Together.ScavPlayer.GetPersistentId() /
        /// Together.ScavPlayer.SteamId / Together.ScavPlayer.playerName。
        /// 持久 ID 与 mp_save/&lt;persistentId&gt;/save.sv 的目录名、mp_rules.json 里 PLRPOS 的键同源，
        /// 供回档前把槽位里记录的旧持久 ID 映射到本场会话。提供方不可用、名册为空或持久 ID 取不到时返回空列表。
        /// </remarks>
        internal List<InteropPlayerIdentity> GetPlayerIdentities()
        {
            List<InteropPlayerIdentity> identities = new List<InteropPlayerIdentity>();
            EnsureResolved();
            if (_scavGetPersistentId == null)
            {
                return identities;
            }

            IDictionary players = ReadClientIdToPlayerDict();
            if (players == null)
            {
                return identities;
            }

            object local = InteropReflection.ReadMember(_scavLocalPlayer, null);
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (DictionaryEntry entry in players)
            {
                object player = entry.Value;
                if (player == null)
                {
                    continue;
                }

                if (!InteropReflection.TryInvoke(_scavGetPersistentId, player, Array.Empty<object>(), out object result))
                {
                    continue;
                }

                string persistentId = result as string;
                if (string.IsNullOrEmpty(persistentId) || !seen.Add(persistentId))
                {
                    continue;
                }

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

        /// <summary>服务端按持久 ID 把玩家传送到指定坐标，返回实际传送人数。</summary>
        /// <remarks>链路：Together.ScavPlayer.BodyToPlayerDict → Together.ScavPlayer.GetPersistentId() → Together.ScavPlayer.Server_TeleportCharacter(Vector2)。</remarks>
        internal int RestorePlayerPositions(IReadOnlyDictionary<string, Vector2> positions)
        {
            if (positions == null || positions.Count == 0 || !IsRunning || !IsServer)
            {
                return 0;
            }

            EnsureResolved();
            if (_scavGetPersistentId == null || _scavTeleportCharacter == null)
            {
                return 0;
            }

            IDictionary players = InteropReflection.ReadMember(_scavBodyToPlayerDict, null) as IDictionary;
            if (players == null)
            {
                return 0;
            }

            int restored = 0;
            foreach (DictionaryEntry entry in players)
            {
                object player = entry.Value;
                if (player == null)
                {
                    continue;
                }

                if (!InteropReflection.TryInvoke(_scavGetPersistentId, player, Array.Empty<object>(), out object result))
                {
                    continue;
                }

                string persistentId = result as string;
                if (string.IsNullOrEmpty(persistentId) || !positions.TryGetValue(persistentId, out Vector2 position))
                {
                    continue;
                }

                if (InteropReflection.TryInvoke(_scavTeleportCharacter, player, new object[] { position }, out object ignored))
                {
                    restored++;
                }
            }

            return restored;
        }

        /// <summary>已死亡玩家数 = Together.ScavPlayer.BodyToPlayerDict 的条目数减 Together.ScavPlayer.AllLivingPlayers 的条目数。</summary>
        internal int CountDeadPlayers()
        {
            EnsureResolved();
            IDictionary allPlayers = InteropReflection.ReadMember(_scavBodyToPlayerDict, null) as IDictionary;
            ICollection livingPlayers = InteropReflection.ReadMember(_scavAllLivingPlayers, null) as ICollection;
            if (allPlayers == null || livingPlayers == null)
            {
                return 0;
            }

            return Math.Max(0, allPlayers.Count - livingPlayers.Count);
        }

        /// <summary>服务端广播屏幕中央大字提示；对应 Together.ServerMain.Server_AnnounceAlert(string, bool, bool, IReadOnlyList&lt;knetid&gt;)。</summary>
        /// <remarks>目标接收列表传 null，由目标方法自己回落到全部客户端。仅服务端且在会话运行时发送。</remarks>
        internal bool TryAnnounceAlert(string message)
        {
            if (string.IsNullOrEmpty(message) || !IsRunning || !IsServer)
            {
                return false;
            }

            EnsureResolved();
            if (_serverAnnounceAlert == null)
            {
                return false;
            }

            return InteropReflection.TryInvoke(_serverAnnounceAlert, null, new object[] { message, true, true, null }, out object ignored);
        }

        /// <summary>走联机模组的读档继续游戏流程；对应 Together.WorldMP.LoadVanillaGeneratedWorld(bool)。</summary>
        /// <remarks>
        /// 目标成员存在时直接调用；缺失时记一次警告并返回 false，调用方保持原状（不改变场景）。
        /// </remarks>
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

        /// <summary>把一条状态消息写进联机模组的日志；对应 Together.Multiplayer.DoMultiplayerStatusMessageLog(string)。</summary>
        internal bool TryLogStatusMessage(string message)
        {
            EnsureResolved();
            if (_multiplayerStatusLog == null || string.IsNullOrEmpty(message))
            {
                return false;
            }

            return InteropReflection.TryInvoke(_multiplayerStatusLog, null, new object[] { message }, out object ignored);
        }

        /// <summary>一行摘要，列出各依赖成员的解析结果，用于启动日志与版本变动时的排查。</summary>
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
                + " Together.ScavPlayer.ClientIdToPlayerDict=" + State(_scavClientIdToPlayerDict)
                + " Together.ScavPlayer.BodyToPlayerDict=" + State(_scavBodyToPlayerDict)
                + " Together.ScavPlayer.AllLivingPlayers=" + State(_scavAllLivingPlayers)
                + " Together.ScavPlayer.GetPersistentId=" + State(_scavGetPersistentId)
                + " Together.ScavPlayer.Server_TeleportCharacter=" + State(_scavTeleportCharacter)
                + " Together.ScavPlayer.OnPlayerJoined=" + State(_scavPlayerJoined)
                + " Together.ScavPlayer.OnPlayerLeft=" + State(_scavPlayerLeft)
                + " Together.ServerMain.AllClientIds=" + State(_serverAllClientIds)
                + " Together.ServerMain.Server_AnnounceAlert=" + State(_serverAnnounceAlert)
                + " Together.Multiplayer.DoMultiplayerStatusMessageLog=" + State(_multiplayerStatusLog)
                + " Together.WorldMP.LoadVanillaGeneratedWorld=" + State(_worldLoadVanillaGeneratedWorld);
        }

        /// <summary>首次访问时解析目标程序集及全部依赖成员并缓存；目标程序集尚未加载时不置缓存，下次访问重试。</summary>
        /// <remarks>目标程序集不存在属于可选场景，不计失败也不记警告；成员解析失败后缓存并只警告一次，重新解析走 <see cref="TryRefresh"/> 显式入口。</remarks>
        private bool EnsureResolved()
        {
            if (_resolved)
            {
                return _available;
            }

            lock (_sync)
            {
                if (_resolved)
                {
                    return _available;
                }

                Assembly assembly = FindTargetAssembly();
                if (assembly == null)
                {
                    return false;
                }

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

                // 标志最后发布：锁外的读者看到 _resolved 为 true 时，此前写入的成员字段与降级标志均已可见。
                _resolved = true;
            }

            return _available;
        }

        /// <summary>16 位原语缺失时各警告一次；降级只影响该原语，提供方整体仍可用。</summary>
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

        /// <summary>丢弃已缓存的解析结果并重新解析，用于目标插件晚加载或成员变化后的显式重试。</summary>
        internal bool TryRefresh()
        {
            ModLog.Debug("Together：TryRefresh 被调用，丢弃已缓存的解析结果并重新解析");
            lock (_sync)
            {
                _resolved = false;
                _available = false;
            }

            return EnsureResolved();
        }

        /// <summary>解析目标程序集的全部依赖成员：<paramref name="missing"/> 记失败项（导致提供方不可用），<paramref name="optionalMissing"/> 记只关闭单项能力的缺失。</summary>
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
            _scavBodyToPlayerDict = Optional(InteropReflection.FindField(scavPlayerType, "BodyToPlayerDict"), "Together.ScavPlayer.BodyToPlayerDict", optionalMissing);
            _scavAllLivingPlayers = Optional(InteropReflection.FindField(scavPlayerType, "AllLivingPlayers"), "Together.ScavPlayer.AllLivingPlayers", optionalMissing);
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
                _serverAnnounceAlert = Optional(
                    InteropReflection.FindMethod(serverMainType, "Server_AnnounceAlert", typeof(string), typeof(bool), typeof(bool), typeof(IReadOnlyList<>).MakeGenericType(_knetidType)),
                    "Together.ServerMain.Server_AnnounceAlert(string, bool, bool, IReadOnlyList<knetid>)",
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

            _knetidId = Require(InteropReflection.FindField(_knetidType, "id"), "Together.knetid.id", missing);
            _knetidCtor = Require(InteropReflection.FindConstructor(_knetidType, typeof(ushort)), "Together.knetid(ushort)", missing);

            if (_writerType != null)
            {
                _putInt = Require(InteropReflection.FindMethod(_writerType, "Put", typeof(int)), "写入器 Put(int)", missing);
                _putUInt = Require(InteropReflection.FindMethod(_writerType, "Put", typeof(uint)), "写入器 Put(uint)", missing);
                // 16 位原语可选：缺失时只降级该原语，不把提供方判为不可用。
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
                // 16 位原语可选：缺失时只降级该原语，不把提供方判为不可用。
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
                    _scavPlayerName, _scavBodyToPlayerDict, _scavAllLivingPlayers, _scavPlayerJoined, _scavTeleportCharacter,
                    _serverAllClientIds, _serverAnnounceAlert, _multiplayerStatusLog, _worldLoadVanillaGeneratedWorld,
                    _knetidId, _knetidCtor,
                    _putInt, _putUInt, _putUShort, _putBool, _putFloat, _putString, _putBytes,
                    _getInt, _getUInt, _getUShort, _getBool, _getFloat, _getString, _getBytes, _readerAvailableBytes)
                + " 个成员 missing=[" + string.Join(", ", missing) + "] optionalMissing=[" + string.Join(", ", optionalMissing) + "]");

            return missing.Count == 0;
        }

        /// <summary>Debug 构建下统计已解析成员数；Release 中调用点被整体移除，不会分配数组。</summary>
        private static int CountResolved(params object[] members)
        {
            int resolved = 0;
            for (int i = 0; i < members.Length; i++)
            {
                if (members[i] != null)
                {
                    resolved++;
                }
            }

            return resolved;
        }

        /// <summary>在已加载程序集里按简单名查找目标程序集；不加载新程序集。</summary>
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

        /// <summary>从 RegisterCustomServerReceiver 的第 2 个形参取出读取器类型。</summary>
        private static Type ReceiverReaderType(MethodInfo register)
        {
            if (register == null)
            {
                return null;
            }

            ParameterInfo[] parameters = register.GetParameters();
            if (parameters.Length != 2 || !parameters[1].ParameterType.IsGenericType)
            {
                return null;
            }

            Type[] arguments = parameters[1].ParameterType.GetGenericArguments();
            return arguments.Length == 2 ? arguments[1] : null;
        }

        /// <summary>记录缺失成员并把成员原样返回。</summary>
        private static T Require<T>(T member, string name, List<string> missing) where T : class
        {
            if (member == null)
            {
                missing.Add(name);
            }

            return member;
        }

        /// <summary>记录可选成员缺失并把成员原样返回；缺失不影响提供方可用性。</summary>
        private static T Optional<T>(T member, string name, List<string> missing) where T : class
        {
            if (member == null)
            {
                missing.Add(name);
            }

            return member;
        }

        private static bool ReadBool(MemberInfo member)
        {
            object value = InteropReflection.ReadMember(member, null);
            return value is bool flag && flag;
        }

        /// <summary>把成员解析结果格式化为 ok / missing。</summary>
        private static string State(object member)
        {
            return member == null ? "missing" : "ok";
        }

        private IDictionary ReadClientIdToPlayerDict()
        {
            return InteropReflection.ReadMember(_scavClientIdToPlayerDict, null) as IDictionary;
        }

        private bool TryReadPlayer(ushort clientId, out object player)
        {
            player = null;
            IDictionary dictionary = ReadClientIdToPlayerDict();
            if (dictionary == null)
            {
                return false;
            }

            object key = MakeKnetid(clientId);
            if (key == null || !dictionary.Contains(key))
            {
                return false;
            }

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
            object id = InteropReflection.ReadMember(_knetidId, knetid);
            return id is ushort value ? value : (ushort)0;
        }

        private bool IsLocalPlayer(object player)
        {
            object local = InteropReflection.ReadMember(_scavLocalPlayer, null);
            return local != null && ReferenceEquals(local, player);
        }

        /// <remarks>玩家或其 playerId 读取失败时返回 <see cref="InteropPlayerRef.None"/>，不产出 clientId=0 的幽灵引用。</remarks>
        private InteropPlayerRef ToRef(object player, bool isLocal)
        {
            if (player == null || !InteropReflection.TryReadMember(_scavPlayerId, player, out object playerId) || playerId == null)
            {
                return InteropPlayerRef.None;
            }

            return ToRef(player, ReadClientId(playerId), isLocal);
        }

        /// <remarks>名称取自 Together.ScavPlayer.playerName；该字段缺失或为空时传 null。</remarks>
        private InteropPlayerRef ToRef(object player, ushort clientId, bool isLocal)
        {
            object steamId = InteropReflection.ReadMember(_scavSteamId, player);
            string name = InteropReflection.ReadMember(_scavPlayerName, player) as string;
            return new InteropPlayerRef(clientId, steamId is ulong id ? id : 0UL, string.IsNullOrEmpty(name) ? null : name, isLocal);
        }

        private object ReadWriter(IInteropWriter writer)
        {
            if (writer == null)
            {
                return null;
            }

            object native = writer.NativeHandle;
            return native != null && _writerType != null && _writerType.IsInstanceOfType(native) ? native : null;
        }

        private Array BuildClientIdArray()
        {
            IDictionary dictionary = ReadClientIdToPlayerDict();
            if (dictionary == null || dictionary.Count == 0)
            {
                return null;
            }

            Array ids = Array.CreateInstance(_knetidType, dictionary.Count);
            int index = 0;
            foreach (object key in dictionary.Keys)
            {
                ids.SetValue(key, index);
                index++;
            }

            return ids;
        }

        /// <summary>把写入器交给目标接收器登记方法；委托类型取自目标方法的形参。</summary>
        private static Delegate BindCallback(MethodInfo register, object receiver, MethodInfo callback)
        {
            if (register == null || callback == null)
            {
                return null;
            }

            ParameterInfo[] parameters = register.GetParameters();
            if (parameters.Length != 2)
            {
                return null;
            }

            return InteropReflection.BindDelegate(parameters[1].ParameterType, receiver, callback);
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
            if (callback == null)
            {
                return false;
            }

            if (!InteropReflection.TryInvoke(register, null, new object[] { channel, callback }, out object ignored))
            {
                InteropDiagnostics.WarnOnce("TogetherProvider.Register." + channel, "通道接收器登记失败：" + channel);
                return false;
            }

            _callbacks.Add(callback);
            return true;
        }

        private void Put(object writer, MethodInfo method, object value)
        {
            InteropReflection.TryInvoke(method, writer, new object[] { value }, out object ignored);
        }

        /// <summary>写入 16 位无符号整数；目标写入器缺少该原语时降级为 32 位无符号写入。</summary>
        /// <remarks>降级只影响该原语：会话、名册与通道能力保持可用，降级在解析阶段警告一次。</remarks>
        private void PutUShort(object writer, ushort value)
        {
            if (_putUShort != null)
            {
                Put(writer, _putUShort, value);
                return;
            }

            Put(writer, _putUInt, (uint)value);
        }

        /// <remarks>写入器 Put(string, int) 重载的第 2 个形参传 0，表示不限长度。</remarks>
        private void PutString(object writer, string value)
        {
            InteropReflection.TryInvoke(_putString, writer, new object[] { value, 0 }, out object ignored);
        }

        /// <remarks>写入器 PutBytesWithLength(byte[]) 的长度前缀是 2 字节，超过 65535 字节的负载不写入。</remarks>
        private void PutBytes(object writer, byte[] value)
        {
            if (value == null || value.Length > ushort.MaxValue)
            {
                return;
            }

            InteropReflection.TryInvoke(_putBytes, writer, new object[] { value }, out object ignored);
        }

        private T Get<T>(object reader, MethodInfo method, T fallback)
        {
            return InteropReflection.TryInvoke(method, reader, Array.Empty<object>(), out object result) && result is T value ? value : fallback;
        }

        /// <summary>读取 16 位无符号整数；目标读取器缺少该原语时返回 0，且不推进读取位置。</summary>
        /// <remarks>降级只影响该原语：会话、名册与通道能力保持可用，降级在解析阶段警告一次。</remarks>
        private ushort GetUShort(object reader)
        {
            return _getUShort == null ? (ushort)0 : Get(reader, _getUShort, (ushort)0);
        }

        private int ReadRemainingBytes(object reader)
        {
            object value = InteropReflection.ReadMember(_readerAvailableBytes, reader);
            return value is int count ? count : 0;
        }

        /// <summary>把目标程序集的写入器对象包成契约写入器，写操作转发给一次性解析好的 Put 重载。</summary>
        /// <remarks>字节数组走 PutBytesWithLength(byte[])：先写 2 字节无符号长度，再写原始字节，与读取器 GetBytesWithLength() 对应。</remarks>
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
            /// <remarks>目标写入器缺少 16 位原语时降级为 32 位无符号写入。</remarks>
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

        /// <summary>把目标程序集的读取器对象包成契约读取器，读操作转发给一次性解析好的 Get 重载。</summary>
        /// <remarks>包装器不改变读取位置，读位置由底层读取器维护；底层读取失败时返回默认值。</remarks>
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
            /// <remarks>目标读取器缺少 16 位原语时返回 0，且不推进读取位置。</remarks>
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

        /// <summary>把目标插件的 Action&lt;ScavPlayer, 读取器&gt; 转发到契约处理器。</summary>
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

        /// <summary>把目标插件的 Action&lt;读取器&gt; 转发到契约处理器。</summary>
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

        /// <summary>把 Action&lt;ScavPlayer&gt; 转发到契约处理器（玩家加入）。</summary>
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

        /// <summary>把 Action&lt;ScavPlayer&gt; 转发到契约处理器（玩家离开）。</summary>
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
