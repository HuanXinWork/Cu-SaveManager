namespace CasualtiesUnknown.SaveManager.Interop
{
    /// <summary>玩家身份快照：目标插件按它把联机存档落到 mp_save 下的玩家子目录。</summary>
    /// <remarks>
    /// <see cref="PersistentId"/> 取自目标插件的持久 ID 计算（Steam 传输下是 STEAM_&lt;steamId&gt;，直连下是 NAME_&lt;玩家名 UTF8 十六进制&gt;），
    /// 与 mp_save/&lt;persistentId&gt;/save.sv 的目录名、mp_rules.json 里 PLRPOS 的键一致；
    /// <see cref="PlayerName"/> 与 <see cref="SteamId"/> 用来在传输方式或玩家名变化后仍能把旧持久 ID 认回同一个人。
    /// 本类型只服务于存档目录与 PLRPOS 的 pid 映射，不参与跨模组通信，因此放在本模组内而不进 Interop 契约层。
    /// </remarks>
    internal readonly struct InteropPlayerIdentity
    {
        /// <summary>用身份数据构造快照；玩家名不可用时传 null。</summary>
        internal InteropPlayerIdentity(ushort clientId, string persistentId, ulong steamId, string playerName, bool isLocal)
        {
            ClientId = clientId;
            PersistentId = persistentId;
            SteamId = steamId;
            PlayerName = playerName;
            IsLocal = isLocal;
        }

        /// <summary>目标插件的客户端 ID。</summary>
        internal ushort ClientId { get; }

        /// <summary>目标插件的持久 ID（mp_save 子目录名 / PLRPOS 键）。</summary>
        internal string PersistentId { get; }

        /// <summary>Steam ID，非 Steam 传输时为 0。</summary>
        internal ulong SteamId { get; }

        /// <summary>玩家名；提供方拿不到名称时为 null。</summary>
        internal string PlayerName { get; }

        /// <summary>是否为本机玩家。</summary>
        internal bool IsLocal { get; }

        /// <summary>返回持久 ID 与客户端 ID。</summary>
        public override string ToString()
        {
            return "pid=" + (PersistentId ?? string.Empty) + " client=" + ClientId;
        }
    }
}
