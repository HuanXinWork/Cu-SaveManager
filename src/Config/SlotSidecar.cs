using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

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

        /// <summary>标记实例是否对应磁盘已有伴生文件（运行时状态，不写入 JSON）。</summary>
        internal bool LoadedFromDisk { get; set; }

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
                SlotSidecar loaded = Parse(File.ReadAllText(p, Encoding.UTF8));
                loaded.LoadedFromDisk = true;
                return loaded;
            }
            catch (Exception ex)
            {
                ModLog.Warning($"读取伴生元数据失败 ({slotPath})：{ex.Message}");
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
                        LoadedFromDisk = true;
                        return;
                    }
                    catch
                    {
                        File.WriteAllText(p, content, TopJson.Utf8NoBom);
                        LoadedFromDisk = true;
                        return;
                    }
                }
                File.Move(tmp, p);
                LoadedFromDisk = true;
            }
            catch (Exception ex)
            {
                ModLog.Warning($"写入伴生元数据失败 ({p})：{ex.Message}");
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
            var ids = new JArray();
            var names = new JArray();
            var steamIds = new JArray();
            if (MpPlayers != null)
            {
                for (int i = 0; i < MpPlayers.Count; i++)
                {
                    SlotMpPlayer p = MpPlayers[i];
                    ids.Add(p.PersistentId ?? "");
                    names.Add(p.Name ?? "");
                    steamIds.Add(p.SteamId == 0UL ? "" : p.SteamId.ToString(CultureInfo.InvariantCulture));
                }
            }

            var obj = new JObject
            {
                ["nickname"] = Nickname ?? "",
                ["pinned"] = Pinned,
                ["isAuto"] = IsAuto,
                ["biome"] = Biome,
                ["runId"] = RunId,
                ["lastPlayedAtUnixMs"] = LastPlayedAtUnixMs,
                ["hasPlayerPos"] = HasPlayerPos,
                ["playerX"] = PlayerX,
                ["playerY"] = PlayerY,
                ["qolSeed"] = QolSeed,
                ["qolSeedInput"] = QolSeedInput ?? "",
                ["worldEngine"] = WorldEngine ?? "",
                ["mpWorldEngine"] = MpWorldEngine ?? "",
                ["posMode"] = PosMode ?? "",
                ["fixedX"] = FixedX,
                ["fixedY"] = FixedY,
                ["isMultiplayer"] = IsMultiplayer,
                ["activeLayerModifierIndex"] = ActiveLayerModifierIndex,
                ["character"] = Character,
                ["mpPlayerIds"] = ids,
                ["mpPlayerNames"] = names,
                ["mpPlayerSteamIds"] = steamIds,
            };
            return obj.ToString(Formatting.None);
        }

        private static SlotSidecar Parse(string text)
        {
            var s = new SlotSidecar();
            if (string.IsNullOrWhiteSpace(text)) return s;

            var obj = JObject.Parse(text);
            s.Nickname = ReadString(obj, "nickname");
            s.Pinned = ReadBool(obj, "pinned");
            s.IsAuto = ReadBool(obj, "isAuto");
            s.Biome = ReadInt(obj, "biome", 0);

            // 旧版伴生文件曾用 "seed" 存放对局 ID，runId 缺失或为 0 时回退兼容。
            int rid = ReadInt(obj, "runId", 0);
            if (rid == 0) rid = ReadInt(obj, "seed", 0);
            s.RunId = rid;

            s.LastPlayedAtUnixMs = ReadLong(obj, "lastPlayedAtUnixMs", 0L);
            s.HasPlayerPos = ReadBool(obj, "hasPlayerPos");
            s.PlayerX = ReadFloat(obj, "playerX");
            s.PlayerY = ReadFloat(obj, "playerY");
            s.QolSeed = ReadInt(obj, "qolSeed", 0);
            s.QolSeedInput = ReadString(obj, "qolSeedInput");
            s.WorldEngine = ReadString(obj, "worldEngine");
            s.MpWorldEngine = ReadString(obj, "mpWorldEngine");
            s.PosMode = ReadString(obj, "posMode");
            s.FixedX = ReadFloat(obj, "fixedX");
            s.FixedY = ReadFloat(obj, "fixedY");
            s.IsMultiplayer = ReadBool(obj, "isMultiplayer");
            s.ActiveLayerModifierIndex = ReadInt(obj, "activeLayerModifierIndex", -2);
            s.Character = ReadInt(obj, "character", -1);
            s.MpPlayers = ReadMpPlayers(obj);
            return s;
        }

        private static List<SlotMpPlayer> ReadMpPlayers(JObject obj)
        {
            var result = new List<SlotMpPlayer>();
            if (!(obj["mpPlayerIds"] is JArray ids) || ids.Count == 0) return result;

            var names = obj["mpPlayerNames"] as JArray;
            var steamIds = obj["mpPlayerSteamIds"] as JArray;
            for (int i = 0; i < ids.Count; i++)
            {
                string id = ids[i]?.Type == JTokenType.Null ? null : (string)ids[i];
                if (string.IsNullOrEmpty(id)) continue;

                string name = names != null && i < names.Count && names[i]?.Type != JTokenType.Null
                    ? ((string)names[i] ?? "")
                    : "";

                ulong steamId = 0UL;
                if (steamIds != null && i < steamIds.Count && steamIds[i]?.Type != JTokenType.Null)
                {
                    ulong.TryParse((string)steamIds[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out steamId);
                }
                result.Add(new SlotMpPlayer(id, name, steamId));
            }
            return result;
        }

        private static string ReadString(JObject obj, string key)
        {
            JToken token = obj[key];
            return token == null || token.Type == JTokenType.Null ? "" : ((string)token ?? "");
        }

        private static bool ReadBool(JObject obj, string key)
        {
            JToken token = obj[key];
            if (token == null || token.Type == JTokenType.Null) return false;
            if (token.Type == JTokenType.Boolean) return (bool)token;
            return bool.TryParse(token.ToString(), out bool v) && v;
        }

        private static int ReadInt(JObject obj, string key, int fallback)
        {
            JToken token = obj[key];
            if (token == null || token.Type == JTokenType.Null) return fallback;
            if (token.Type == JTokenType.Integer)
            {
                long lv = (long)token;
                return lv >= int.MinValue && lv <= int.MaxValue ? (int)lv : fallback;
            }
            return int.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
                ? v
                : fallback;
        }

        private static long ReadLong(JObject obj, string key, long fallback)
        {
            JToken token = obj[key];
            if (token == null || token.Type == JTokenType.Null) return fallback;
            if (token.Type == JTokenType.Integer) return (long)token;
            return long.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long v)
                ? v
                : fallback;
        }

        private static float ReadFloat(JObject obj, string key, float fallback = 0f)
        {
            JToken token = obj[key];
            if (token == null || token.Type == JTokenType.Null) return fallback;
            if (token.Type == JTokenType.Float || token.Type == JTokenType.Integer) return (float)token;
            return float.TryParse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v)
                ? v
                : fallback;
        }
    }
}
