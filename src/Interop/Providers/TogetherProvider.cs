// CuSaveManager Interop
// Copyright (c) 2026 huanxin886
// SPDX-License-Identifier: MIT

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using UnityEngine;

namespace CasualtiesUnknown.SaveManager.Interop
{
    /// <summary>Casualties: Together（CasualtiesMP.dll，GUID "CasualtiesMP"）反射提供方适配器。</summary>
    internal sealed class TogetherProvider : IInteropProvider, IInteropSession, IInteropRoster, IInteropTypeRegistry
    {
        private const string TargetAssemblyName = "CasualtiesMP";
        private const string CoreMarkerTypeName = "Together.Net";

        private readonly object _sync = new object();

        // 双检锁标志：锁外读取需 acquire 语义以确保已解析成员字段可见。
        private volatile bool _resolved;
        private volatile bool _available;

        private Assembly _assembly;
        private Type _knetidType;

        private PropertyInfo _netIsRunning;
        private PropertyInfo _netIsServer;
        private PropertyInfo _netIsClient;
        private PropertyInfo _netIsDedicatedServer;
        private PropertyInfo _netIsRunningSteam;
        private PropertyInfo _pluginSuccessfullyInitialized;
        private PropertyInfo _pluginForceDisableMod;

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
        private MethodInfo _scavGetPersistentId;
        private MethodInfo _scavTeleportCharacter;

        private PropertyInfo _serverAllClientIdsExceptHost;
        private MethodInfo _serverAnnounceAlert;
        private MethodInfo _serverCheckLayerFinish;

        private MethodInfo _multiplayerStatusLog;
        private MethodInfo _worldLoadVanillaGeneratedWorld;
        private MemberInfo _savesystemPendingLoadMpSave;

        private FieldInfo _knetidId;
        private ConstructorInfo _knetidCtor;

        /// <inheritdoc/>
        string IInteropProvider.Name => "Together";

        /// <inheritdoc/>
        bool IInteropProvider.IsAvailable => EnsureResolved();

        /// <summary>目标程序集与核心会话及名册成员是否全部可用。</summary>
        internal bool IsPresent => EnsureResolved();

        /// <inheritdoc/>
        InteropCapability IInteropProvider.Capabilities =>
            InteropCapability.Session | InteropCapability.Roster | InteropCapability.TypeRegistry;

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
                if (entry.Value != null)
                    players.Add(ToRef(entry.Value, ReadClientId(entry.Key), ReferenceEquals(entry.Value, local)));
            }
            return players;
        }

        /// <inheritdoc/>
        bool IInteropRoster.TryGetPlayerByActor(object actor, out InteropPlayerRef player)
        {
            player = InteropPlayerRef.None;
            if (actor == null || !EnsureResolved() || _tryGetPlayerAndNetBody == null) return false;

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
        bool IInteropTypeRegistry.TryResolveType(string fullName, out Type type)
        {
            type = null;
            EnsureResolved();
            if (_assembly == null) return false;
            type = InteropReflection.FindType(_assembly, fullName);
            return type != null;
        }

        /// <summary>联机会话是否运行中（Together.Net.IsRunning）。</summary>
        internal bool IsRunning { get { EnsureResolved(); return ReadBool(_netIsRunning); } }

        /// <summary>本端是否为服务端（Together.Net.IsServer）。</summary>
        internal bool IsServer { get { EnsureResolved(); return ReadBool(_netIsServer); } }

        /// <summary>本端是否为客户端（Together.Net.IsClient）。</summary>
        internal bool IsClient { get { EnsureResolved(); return ReadBool(_netIsClient); } }

        /// <summary>本端是否为专用服务端（Together.Net.IsDedicatedServer）。</summary>
        internal bool IsDedicatedServer { get { EnsureResolved(); return ReadBool(_netIsDedicatedServer); } }

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
        internal Assembly ModAssembly { get { EnsureResolved(); return _assembly; } }

        /// <summary>判断角色身体对象是否归属本机玩家；非联机时返回 true，专用服务端返回 false。</summary>
        internal bool IsLocalPlayerBody(object body)
        {
            if (!IsRunning) return true;
            if (IsDedicatedServer || body == null) return false;

            object local = InteropReflection.ReadMember(_scavLocalPlayer, null);
            if (local == null) return false;

            if (InteropReflection.ReadMember(_scavBodyToPlayerDict, null) is IDictionary bodyDict && bodyDict.Contains(body))
                return ReferenceEquals(bodyDict[body], local);

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
            if (member != null) InteropReflection.TryWriteMember(member, null, value);
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

        /// <summary>服务端按持久 ID 传送玩家（Together.ScavPlayer.Server_TeleportCharacter），先拷贝字典快照防传送期间集合变更。</summary>
        internal int RestorePlayerPositions(IReadOnlyDictionary<string, Vector2> positions)
        {
            if (positions == null || positions.Count == 0 || !IsRunning || !IsServer) return 0;

            EnsureResolved();
            if (_scavGetPersistentId == null || _scavTeleportCharacter == null) return 0;

            IDictionary players = InteropReflection.ReadMember(_scavBodyToPlayerDict, null) as IDictionary;
            if (players == null || players.Count == 0) return 0;

            List<DictionaryEntry> snapshot = new List<DictionaryEntry>(players.Count);
            foreach (DictionaryEntry entry in players) snapshot.Add(entry);

            int restored = 0;
            for (int i = 0; i < snapshot.Count; i++)
            {
                object player = snapshot[i].Value;
                if (player == null) continue;
                if (!InteropReflection.TryInvoke(_scavGetPersistentId, player, Array.Empty<object>(), out object result)) continue;

                string persistentId = result as string;
                if (string.IsNullOrEmpty(persistentId) || !positions.TryGetValue(persistentId, out Vector2 position)) continue;

                if (InteropReflection.TryInvoke(_scavTeleportCharacter, player, new object[] { position }, out _))
                    restored++;
            }
            return restored;
        }

        /// <summary>统计已死亡玩家数：优先读 Together.ScavPlayer.AllDeadPlayers，缺失时回退 BodyToPlayerDict 与 AllLivingPlayers 差值。</summary>
        internal int CountDeadPlayers()
        {
            EnsureResolved();
            if (InteropReflection.ReadMember(_scavAllDeadPlayers, null) is ICollection deadPlayers)
                return deadPlayers.Count;

            IDictionary allPlayers = InteropReflection.ReadMember(_scavBodyToPlayerDict, null) as IDictionary;
            ICollection livingPlayers = InteropReflection.ReadMember(_scavAllLivingPlayers, null) as ICollection;
            if (allPlayers == null || livingPlayers == null) return 0;
            return Math.Max(0, allPlayers.Count - livingPlayers.Count);
        }

        /// <summary>统计当前存活玩家数（Together.ScavPlayer.AllLivingPlayers）；不可用时返回 -1。</summary>
        internal int CountLivingPlayers()
        {
            EnsureResolved();
            return InteropReflection.ReadMember(_scavAllLivingPlayers, null) is ICollection livingPlayers
                ? livingPlayers.Count
                : -1;
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
                + " Together.ServerMain.AllClientIdsExceptHost=" + State(_serverAllClientIdsExceptHost)
                + " Together.ServerMain.Server_AnnounceAlert=" + State(_serverAnnounceAlert)
                + " Together.ServerMain.CheckIfEnoughPeopleAreAtLayerFinish=" + State(_serverCheckLayerFinish)
                + " Together.Multiplayer.DoMultiplayerStatusMessageLog=" + State(_multiplayerStatusLog)
                + " Together.WorldMP.LoadVanillaGeneratedWorld=" + State(_worldLoadVanillaGeneratedWorld)
                + " Together.SavesystemPatch.PendingLoadMpSave=" + State(_savesystemPendingLoadMpSave);
        }

        /// <summary>首次访问时解析目标程序集及依赖成员并缓存；插件扫描完成后对未安装模组缓存负向结果。</summary>
        private bool EnsureResolved()
        {
            if (_resolved) return _available;

            lock (_sync)
            {
                if (_resolved) return _available;

                Assembly assembly = FindTargetAssembly();
                if (assembly == null)
                {
                    if (InteropBootstrap.PluginScanComplete)
                    {
                        _available = false;
                        _resolved = true;
                    }
                    return false;
                }

                List<string> missing = new List<string>();
                List<string> optionalMissing = new List<string>();
                _available = Resolve(assembly, missing, optionalMissing);
                if (!_available)
                    InteropDiagnostics.WarnOnce("TogetherProvider", "目标程序集核心成员缺失：" + string.Join(", ", missing));

                if (optionalMissing.Count > 0)
                    InteropDiagnostics.WarnOnce("TogetherProvider.Optional", "可选成员缺失（对应能力降级）：" + string.Join(", ", optionalMissing));

                _resolved = true;
            }
            return _available;
        }

        /// <summary>清空已缓存的反射成员并重新解析。</summary>
        internal bool TryRefresh()
        {
            ModLog.Debug("Together：TryRefresh 清空缓存并重新解析");
            lock (_sync)
            {
                _resolved = false;
                _available = false;
                ClearCachedMembers();
            }
            return EnsureResolved();
        }

        private void ClearCachedMembers()
        {
            _assembly = null;
            _knetidType = null;
            _netIsRunning = _netIsServer = _netIsClient = _netIsDedicatedServer = _netIsRunningSteam = null;
            _pluginSuccessfullyInitialized = _pluginForceDisableMod = null;
            _netBodyBody = null;
            _tryGetPlayerAndNetBody = null;
            _scavLocalPlayer = _scavSteamId = _scavPlayerName = _scavSelectedCharacter = null;
            _scavClientIdToPlayerDict = _scavBodyToPlayerDict = _scavAllLivingPlayers = _scavAllDeadPlayers = null;
            _scavPlayerId = null;
            _scavGetPersistentId = _scavTeleportCharacter = null;
            _serverAllClientIdsExceptHost = null;
            _serverAnnounceAlert = _serverCheckLayerFinish = _multiplayerStatusLog = _worldLoadVanillaGeneratedWorld = null;
            _savesystemPendingLoadMpSave = null;
            _knetidId = null;
            _knetidCtor = null;
        }

        private bool Resolve(Assembly assembly, List<string> missing, List<string> optionalMissing)
        {
            _assembly = assembly;
            Type netType = Track(InteropReflection.FindType(_assembly, CoreMarkerTypeName), CoreMarkerTypeName, missing);
            Type scavPlayerType = Track(InteropReflection.FindType(_assembly, "Together.ScavPlayer"), "Together.ScavPlayer", missing);
            _knetidType = Track(InteropReflection.FindType(_assembly, "Together.knetid"), "Together.knetid", missing);
            if (netType == null || scavPlayerType == null || _knetidType == null)
            {
                ModLog.Debug("Together：assembly='" + assembly.GetName().Name + "' available=false capabilities=[] missing=[" + string.Join(", ", missing) + "]");
                return false;
            }

            _netIsRunning = Track(InteropReflection.FindProperty(netType, "IsRunning"), "Together.Net.IsRunning", missing);
            _netIsServer = Track(InteropReflection.FindProperty(netType, "IsServer"), "Together.Net.IsServer", missing);
            _netIsRunningSteam = Track(InteropReflection.FindProperty(netType, "IsRunningSteam"), "Together.Net.IsRunningSteam", optionalMissing);
            _netIsClient = Track(InteropReflection.FindProperty(netType, "IsClient"), "Together.Net.IsClient", optionalMissing);
            _netIsDedicatedServer = Track(InteropReflection.FindProperty(netType, "IsDedicatedServer"), "Together.Net.IsDedicatedServer", optionalMissing);

            Type pluginType = Track(InteropReflection.FindType(_assembly, "Together.Plugin"), "Together.Plugin", optionalMissing);
            if (pluginType != null)
            {
                _pluginSuccessfullyInitialized = Track(InteropReflection.FindProperty(pluginType, "SUCCESFULLY_INITIALIZED"), "Together.Plugin.SUCCESFULLY_INITIALIZED", optionalMissing);
                _pluginForceDisableMod = Track(InteropReflection.FindProperty(pluginType, "FORCE_DISABLE_MP_MOD"), "Together.Plugin.FORCE_DISABLE_MP_MOD", optionalMissing);
            }

            Type netBodyType = Track(InteropReflection.FindType(_assembly, "Together.NetBody"), "Together.NetBody", optionalMissing);
            if (netBodyType != null)
            {
                _netBodyBody = Track(InteropReflection.FindProperty(netBodyType, "body"), "Together.NetBody.body", optionalMissing);
                _tryGetPlayerAndNetBody = Track(
                    InteropReflection.FindMethod(scavPlayerType, "TryGetPlayerAndNetBodyFromClientId", _knetidType, scavPlayerType, netBodyType),
                    "Together.ScavPlayer.TryGetPlayerAndNetBodyFromClientId(knetid, out ScavPlayer, out NetBody)",
                    optionalMissing);
            }

            _scavLocalPlayer = Track(InteropReflection.FindField(scavPlayerType, "LocalPlayer"), "Together.ScavPlayer.LocalPlayer", missing);
            _scavPlayerId = Track(InteropReflection.FindProperty(scavPlayerType, "playerId"), "Together.ScavPlayer.playerId", missing);
            _scavClientIdToPlayerDict = Track(InteropReflection.FindField(scavPlayerType, "ClientIdToPlayerDict"), "Together.ScavPlayer.ClientIdToPlayerDict", missing);
            _scavGetPersistentId = Track(InteropReflection.FindMethod(scavPlayerType, "GetPersistentId"), "Together.ScavPlayer.GetPersistentId()", missing);
            _scavSteamId = Track(InteropReflection.FindField(scavPlayerType, "SteamId"), "Together.ScavPlayer.SteamId", optionalMissing);
            _scavPlayerName = Track(InteropReflection.FindField(scavPlayerType, "playerName"), "Together.ScavPlayer.playerName", optionalMissing);
            _scavSelectedCharacter = Track(InteropReflection.FindField(scavPlayerType, "selectedCharacter"), "Together.ScavPlayer.selectedCharacter", optionalMissing);
            _scavBodyToPlayerDict = Track(InteropReflection.FindField(scavPlayerType, "BodyToPlayerDict"), "Together.ScavPlayer.BodyToPlayerDict", optionalMissing);
            _scavAllLivingPlayers = Track(InteropReflection.FindField(scavPlayerType, "AllLivingPlayers"), "Together.ScavPlayer.AllLivingPlayers", optionalMissing);
            _scavAllDeadPlayers = Track(InteropReflection.FindField(scavPlayerType, "AllDeadPlayers"), "Together.ScavPlayer.AllDeadPlayers", optionalMissing);
            _scavTeleportCharacter = Track(InteropReflection.FindMethod(scavPlayerType, "Server_TeleportCharacter", typeof(Vector2)), "Together.ScavPlayer.Server_TeleportCharacter(Vector2)", optionalMissing);

            Type serverMainType = Track(InteropReflection.FindType(_assembly, "Together.ServerMain"), "Together.ServerMain", optionalMissing);
            if (serverMainType != null)
            {
                _serverAllClientIdsExceptHost = Track(InteropReflection.FindProperty(serverMainType, "AllClientIdsExceptHost"), "Together.ServerMain.AllClientIdsExceptHost", optionalMissing);
                _serverAnnounceAlert = Track(
                    InteropReflection.FindMethod(serverMainType, "Server_AnnounceAlert", typeof(string), typeof(bool), typeof(bool), typeof(IReadOnlyList<>).MakeGenericType(_knetidType)),
                    "Together.ServerMain.Server_AnnounceAlert(string, bool, bool, IReadOnlyList<knetid>)",
                    optionalMissing);
                _serverCheckLayerFinish = Track(
                    InteropReflection.FindMethod(serverMainType, "CheckIfEnoughPeopleAreAtLayerFinish"),
                    "Together.ServerMain.CheckIfEnoughPeopleAreAtLayerFinish()",
                    optionalMissing);
            }

            Type multiplayerType = Track(InteropReflection.FindType(_assembly, "Together.Multiplayer"), "Together.Multiplayer", optionalMissing);
            if (multiplayerType != null)
                _multiplayerStatusLog = Track(InteropReflection.FindMethod(multiplayerType, "DoMultiplayerStatusMessageLog", typeof(string)), "Together.Multiplayer.DoMultiplayerStatusMessageLog(string)", optionalMissing);

            Type worldMpType = Track(InteropReflection.FindType(_assembly, "Together.WorldMP"), "Together.WorldMP", optionalMissing);
            if (worldMpType != null)
                _worldLoadVanillaGeneratedWorld = Track(InteropReflection.FindMethod(worldMpType, "LoadVanillaGeneratedWorld", typeof(bool)), "Together.WorldMP.LoadVanillaGeneratedWorld(bool)", optionalMissing);

            Type savesystemPatchType = Track(InteropReflection.FindType(_assembly, "Together.SavesystemPatch"), "Together.SavesystemPatch", optionalMissing);
            if (savesystemPatchType != null)
            {
                MemberInfo pendingMember = (MemberInfo)InteropReflection.FindField(savesystemPatchType, "PendingLoadMpSave")
                    ?? InteropReflection.FindProperty(savesystemPatchType, "PendingLoadMpSave");
                _savesystemPendingLoadMpSave = Track(pendingMember, "Together.SavesystemPatch.PendingLoadMpSave", optionalMissing);
            }

            _knetidId = Track(InteropReflection.FindField(_knetidType, "id"), "Together.knetid.id", missing);
            _knetidCtor = Track(InteropReflection.FindConstructor(_knetidType, typeof(ushort)), "Together.knetid(ushort)", missing);

            ModLog.Debug("Together：assembly='" + assembly.GetName().Name + "' available=" + (missing.Count == 0)
                + " capabilities=[" + InteropDiagnostics.FormatCapabilities(
                    InteropCapability.Session | InteropCapability.Roster | InteropCapability.TypeRegistry) + "]"
                + " 已解析 " + CountResolved(
                    _netIsRunning, _netIsServer, _netIsRunningSteam, _netIsClient, _netIsDedicatedServer,
                    _pluginSuccessfullyInitialized, _pluginForceDisableMod,
                    _netBodyBody, _tryGetPlayerAndNetBody,
                    _scavLocalPlayer, _scavPlayerId, _scavClientIdToPlayerDict, _scavGetPersistentId,
                    _scavSteamId, _scavPlayerName, _scavSelectedCharacter, _scavBodyToPlayerDict, _scavAllLivingPlayers, _scavAllDeadPlayers, _scavTeleportCharacter,
                    _serverAllClientIdsExceptHost, _serverAnnounceAlert, _serverCheckLayerFinish, _multiplayerStatusLog, _worldLoadVanillaGeneratedWorld, _savesystemPendingLoadMpSave,
                    _knetidId, _knetidCtor)
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
            try
            {
                if (Chainloader.PluginInfos != null
                    && Chainloader.PluginInfos.TryGetValue(InteropPatchOrder.Together, out PluginInfo pluginInfo)
                    && pluginInfo?.Instance != null)
                {
                    Assembly pluginAsm = pluginInfo.Instance.GetType().Assembly;
                    if (InteropReflection.FindType(pluginAsm, CoreMarkerTypeName) != null)
                        return pluginAsm;
                }
            }
            catch (Exception) { }

            Assembly self = typeof(TogetherProvider).Assembly;
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Assembly candidate = assemblies[i];
                if (candidate == null || ReferenceEquals(candidate, self)) continue;

                string name;
                try { name = candidate.GetName().Name; }
                catch (Exception) { continue; }

                if (string.Equals(name, TargetAssemblyName, StringComparison.Ordinal)
                    && InteropReflection.FindType(candidate, CoreMarkerTypeName) != null)
                {
                    return candidate;
                }
            }
            return null;
        }

        private static T Track<T>(T member, string name, List<string> sink) where T : class
        {
            if (member == null) sink.Add(name);
            return member;
        }

        private static bool ReadBool(MemberInfo member)
            => InteropReflection.ReadMember(member, null) is bool flag && flag;

        private static string State(object member) => member == null ? "missing" : "ok";

        private IDictionary ReadClientIdToPlayerDict()
            => InteropReflection.ReadMember(_scavClientIdToPlayerDict, null) as IDictionary;

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
            => InteropReflection.ReadMember(_knetidId, knetid) is ushort value ? value : (ushort)0;

        private bool IsLocalPlayer(object player)
        {
            object local = InteropReflection.ReadMember(_scavLocalPlayer, null);
            return local != null && ReferenceEquals(local, player);
        }

        private InteropPlayerRef ToRef(object player, bool isLocal)
        {
            if (player == null || !InteropReflection.TryReadMember(_scavPlayerId, player, out object playerId) || playerId == null)
                return InteropPlayerRef.None;
            return ToRef(player, ReadClientId(playerId), isLocal);
        }

        private InteropPlayerRef ToRef(object player, ushort clientId, bool isLocal)
        {
            object steamId = InteropReflection.ReadMember(_scavSteamId, player);
            string name = InteropReflection.ReadMember(_scavPlayerName, player) as string;
            return new InteropPlayerRef(clientId, steamId is ulong id ? id : 0UL, string.IsNullOrEmpty(name) ? null : name, isLocal);
        }
    }
}
