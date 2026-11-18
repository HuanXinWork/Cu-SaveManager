// CuSaveManager Interop
// Copyright (c) 2026 huanxin886
// SPDX-License-Identifier: MIT

namespace CasualtiesUnknown.SaveManager.Interop
{
    /// <summary>联机玩家身份快照，用于映射 mp_save 玩家子目录与 mp_rules.json 的 PLRPOS 键。</summary>
    /// <remarks>PersistentId 在 Steam 传输下为 STEAM_&lt;steamId&gt;，直连下为 NAME_&lt;玩家名 UTF-8 十六进制&gt;。</remarks>
    internal readonly struct InteropPlayerIdentity
    {
        /// <summary>构造玩家身份快照；玩家名不可用时传 null。</summary>
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

        /// <summary>Steam ID；非 Steam 传输时为 0。</summary>
        internal ulong SteamId { get; }

        /// <summary>玩家名；不可用时为 null。</summary>
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
