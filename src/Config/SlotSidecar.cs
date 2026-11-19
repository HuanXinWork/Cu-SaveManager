using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>多人槽位记录的玩家身份快照（持久 ID、名称与 Steam ID），用于跨传输方式重映射。</summary>
    internal readonly struct SlotMpPlayer
    {
        internal SlotMpPlayer(string persistentId, string name, ulong steamId)
        {
            PersistentId = persistentId ?? "";
            Name = name ?? "";
            SteamId = steamId;
        }

        internal string PersistentId { get; }
        internal string Name { get; }
        internal ulong SteamId { get; }
    }

    /// <summary>槽位伴生元数据（&lt;slot&gt;.sv.json），存储世界引擎、种子、坐标、层词条及多人玩家身份。</summary>
    internal sealed class SlotSidecar
    {
        internal string Nickname { get; set; } = "";
        internal bool Pinned { get; set; }
        internal bool IsAuto { get; set; }
        internal int Biome { get; set; }
        internal int RunId { get; set; }
        internal long LastPlayedAtUnixMs { get; set; }

        internal float PlayerX { get; set; }
        internal float PlayerY { get; set; }
        internal int QolSeed { get; set; }
        internal string QolSeedInput { get; set; } = "";
        internal bool HasPlayerPos { get; set; }

        /// <summary>世界生成引擎标识（"qol" | "self" | "krok"）。</summary>
        internal string WorldEngine { get; set; } = "";
        internal string MpWorldEngine { get; set; } = "";
        internal string PosMode { get; set; } = "";
        internal float FixedX { get; set; }
        internal float FixedY { get; set; }
        internal bool IsMultiplayer { get; set; }

        /// <summary>本层激活的 LayerModifier 索引（-2=未记录，-1=无词条，&gt;=0=对应 modifierIndex）。</summary>
        internal int ActiveLayerModifierIndex { get; set; } = -2;

        /// <summary>角色物种备份（-1=未记录，0=Experiment，1=Milky，2=Dune）。</summary>
        internal int Character { get; set; } = -1;

        internal List<SlotMpPlayer> MpPlayers { get; set; } = new List<SlotMpPlayer>();

        internal string Describe()
        {
            string pos = HasPlayerPos
                ? "(" + PlayerX.ToString("0.0", CultureInfo.InvariantCulture) + "," + PlayerY.ToString("0.0", CultureInfo.InvariantCulture) + ")"
                : "none";
            return "runId=" + RunId + " biome=" + Biome
                + " engine='" + WorldEngine + "' mpEngine='" + MpWorldEngine + "'"
                + " seed=" + QolSeed + " input='" + QolSeedInput + "'"
                + " pos=" + pos + " posMode='" + PosMode + "'"
                + " auto=" + IsAuto + " pinned=" + Pinned + " mp=" + IsMultiplayer
                + " layerMod=" + ActiveLayerModifierIndex + " character=" + Character
                + " players=" + (MpPlayers?.Count ?? 0);
        }

        /// <summary>读取指定存档对应的 .json 伴生元数据；文件缺失或解析失败时返回空实例。</summary>
        internal static SlotSidecar LoadOrEmpty(string slotPath)
        {
            try
            {
                string p = SidecarPath(slotPath);
                if (string.IsNullOrEmpty(p) || !File.Exists(p)) return new SlotSidecar();
                return Parse(File.ReadAllText(p, Encoding.UTF8));
            }
            catch
            {
                return new SlotSidecar();
            }
        }

        /// <summary>以无 BOM UTF-8 原子写入伴生元数据文件。</summary>
        internal void Save(string slotPath)
        {
            string p = SidecarPath(slotPath);
            if (string.IsNullOrEmpty(p)) return;
            string tmp = p + ".tmp";
            try
            {
                string content = Serialize();
                File.WriteAllText(tmp, content, TopJson.Utf8NoBom);
                if (File.Exists(p))
                {
                    try
                    {
                        File.Replace(tmp, p, null);
                        return;
                    }
                    catch
                    {
                        File.WriteAllText(p, content, TopJson.Utf8NoBom);
                        return;
                    }
                }
                File.Move(tmp, p);
            }
            catch
            {
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        internal static void DeleteFor(string slotPath)
        {
            try
            {
                string p = SidecarPath(slotPath);
                if (!string.IsNullOrEmpty(p) && File.Exists(p)) File.Delete(p);
            }
            catch { }
        }

        internal static string SidecarPath(string slotPath)
            => string.IsNullOrEmpty(slotPath) ? "" : slotPath + ".json";

        private string Serialize()
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(256);
            sb.Append('{');
            sb.Append("\"nickname\":").Append(TopJson.EscapeString(Nickname)).Append(',');
            sb.Append("\"pinned\":").Append(Pinned ? "true" : "false").Append(',');
            sb.Append("\"isAuto\":").Append(IsAuto ? "true" : "false").Append(',');
            sb.Append("\"biome\":").Append(Biome.ToString(inv)).Append(',');
            sb.Append("\"runId\":").Append(RunId.ToString(inv)).Append(',');
            sb.Append("\"lastPlayedAtUnixMs\":").Append(LastPlayedAtUnixMs.ToString(inv)).Append(',');
            sb.Append("\"hasPlayerPos\":").Append(HasPlayerPos ? "true" : "false").Append(',');
            sb.Append("\"playerX\":").Append(PlayerX.ToString("R", inv)).Append(',');
            sb.Append("\"playerY\":").Append(PlayerY.ToString("R", inv)).Append(',');
            sb.Append("\"qolSeed\":").Append(QolSeed.ToString(inv)).Append(',');
            sb.Append("\"qolSeedInput\":").Append(TopJson.EscapeString(QolSeedInput)).Append(',');
            sb.Append("\"worldEngine\":").Append(TopJson.EscapeString(WorldEngine)).Append(',');
            sb.Append("\"mpWorldEngine\":").Append(TopJson.EscapeString(MpWorldEngine)).Append(',');
            sb.Append("\"posMode\":").Append(TopJson.EscapeString(PosMode)).Append(',');
            sb.Append("\"fixedX\":").Append(FixedX.ToString("R", inv)).Append(',');
            sb.Append("\"fixedY\":").Append(FixedY.ToString("R", inv)).Append(',');
            sb.Append("\"isMultiplayer\":").Append(IsMultiplayer ? "true" : "false").Append(',');
            sb.Append("\"activeLayerModifierIndex\":").Append(ActiveLayerModifierIndex.ToString(inv)).Append(',');
            sb.Append("\"character\":").Append(Character.ToString(inv)).Append(',');
            sb.Append("\"mpPlayerIds\":").Append(StringArray(MpPlayers, p => p.PersistentId)).Append(',');
            sb.Append("\"mpPlayerNames\":").Append(StringArray(MpPlayers, p => p.Name)).Append(',');
            sb.Append("\"mpPlayerSteamIds\":").Append(StringArray(MpPlayers, p => p.SteamId == 0UL
                ? ""
                : p.SteamId.ToString(inv)));
            sb.Append('}');
            return sb.ToString();
        }

        private static SlotSidecar Parse(string text)
        {
            var s = new SlotSidecar();
            if (string.IsNullOrEmpty(text)) return s;
            s.Nickname = TopJson.TryReadString(text, "nickname", out string nick) ? (nick ?? "") : "";
            s.Pinned = TopJson.TryReadBool(text, "pinned", out bool pinned) && pinned;
            s.IsAuto = TopJson.TryReadBool(text, "isAuto", out bool isAuto) && isAuto;
            s.Biome = TopJson.TryReadInt(text, "biome", out int biome) ? biome : 0;
            if (!TopJson.TryReadInt(text, "runId", out int rid) || rid == 0)
            {
                TopJson.TryReadInt(text, "seed", out rid);
            }
            s.RunId = rid;
            s.LastPlayedAtUnixMs = TopJson.TryReadLong(text, "lastPlayedAtUnixMs", out long ms) ? ms : 0L;
            s.HasPlayerPos = TopJson.TryReadBool(text, "hasPlayerPos", out bool hasPos) && hasPos;
            s.PlayerX = TopJson.TryReadFloat(text, "playerX", out float px) ? px : 0f;
            s.PlayerY = TopJson.TryReadFloat(text, "playerY", out float py) ? py : 0f;
            s.QolSeed = TopJson.TryReadInt(text, "qolSeed", out int qseed) ? qseed : 0;
            s.QolSeedInput = TopJson.TryReadString(text, "qolSeedInput", out string qin) ? (qin ?? "") : "";
            s.WorldEngine = TopJson.TryReadString(text, "worldEngine", out string we) ? (we ?? "") : "";
            s.MpWorldEngine = TopJson.TryReadString(text, "mpWorldEngine", out string mpwe) ? (mpwe ?? "") : "";
            s.PosMode = TopJson.TryReadString(text, "posMode", out string pm) ? (pm ?? "") : "";
            s.FixedX = TopJson.TryReadFloat(text, "fixedX", out float fx) ? fx : 0f;
            s.FixedY = TopJson.TryReadFloat(text, "fixedY", out float fy) ? fy : 0f;
            s.IsMultiplayer = TopJson.TryReadBool(text, "isMultiplayer", out bool isMp) && isMp;
            s.ActiveLayerModifierIndex = TopJson.TryReadInt(text, "activeLayerModifierIndex", out int modIdx)
                ? modIdx
                : (TopJson.HasKey(text, "activeLayerModifierIndex") ? 0 : -2);
            s.Character = TopJson.TryReadInt(text, "character", out int ch)
                ? ch
                : (TopJson.HasKey(text, "character") ? 0 : -1);
            s.MpPlayers = ReadMpPlayers(text);
            return s;
        }

        private static List<SlotMpPlayer> ReadMpPlayers(string text)
        {
            var result = new List<SlotMpPlayer>();
            var ids = TopJson.ReadStringArray(text, "mpPlayerIds");
            if (ids.Count == 0) return result;
            var names = TopJson.ReadStringArray(text, "mpPlayerNames");
            var steamIds = TopJson.ReadStringArray(text, "mpPlayerSteamIds");
            for (int i = 0; i < ids.Count; i++)
            {
                if (string.IsNullOrEmpty(ids[i])) continue;
                ulong steamId = 0UL;
                if (i < steamIds.Count)
                {
                    ulong.TryParse(steamIds[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out steamId);
                }
                result.Add(new SlotMpPlayer(ids[i], i < names.Count ? names[i] : "", steamId));
            }
            return result;
        }

        private static string StringArray(List<SlotMpPlayer> players, Func<SlotMpPlayer, string> selector)
        {
            if (players == null || players.Count == 0) return "[]";
            var sb = new StringBuilder(players.Count * 24 + 2);
            sb.Append('[');
            for (int i = 0; i < players.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(TopJson.EscapeString(selector(players[i])));
            }
            sb.Append(']');
            return sb.ToString();
        }
    }
}
