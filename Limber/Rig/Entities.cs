using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Limber.Core;
using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace Limber.Rig;

/// <summary>
/// Any of the game's creatures as a model: minions, mounts, monsters, beast tribes and the rest (the game's list of
/// character models). An actor (or any character) is shown as one by changing which model it's drawn with, the way
/// Brio and Anamnesis do it; the skeleton is the creature's own, so FK / IK posing, the Timeline and Move character
/// work on it as on anyone. Only on your screen.
/// </summary>
public static unsafe class Entities
{
    /// <summary>One model: its number in the game's list, a name, what kind it is (the top group), the groups inside that
    /// it's listed under, and the model's numbers.</summary>
    public sealed record Entry(uint Id, string Name, string Kind, ushort Model, byte Base, byte Variant, byte Type)
    {
        public string Code => $"{(Type == 3 ? "m" : Type == 2 ? "d" : "c")}{Model:0000} b{Base:00} v{Variant:00}";
        public string[] Groups { get; init; } = [];
        /// <summary>Groups by where it's found (region, then area), for those the game's data places (NPCs); null: none.</summary>
        public string[]? AreaGroups { get; init; }
    }

    private static volatile IReadOnlyList<Entry>? _all;
    private static bool _loading;

    /// <summary>Everything, read once in the background; empty until ready.</summary>
    public static IReadOnlyList<Entry> All
    {
        get
        {
            if(_all != null)
                return _all;
            if(!_loading)
            {
                _loading = true;
                System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        _all = Read();
                    }
                    catch(Exception e)
                    {
                        Plugin.Log.Warning(e, "Limber: couldn't read the game's creature list");
                        _all = Array.Empty<Entry>();
                    }
                });
            }
            return Array.Empty<Entry>();
        }
    }

    public static bool Ready => _all != null;

    private static string Title(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
    // (kinds in the order they're listed)

    private static string Letter(string name)
    {
        char c = name.Length > 0 ? char.ToUpperInvariant(name[0]) : '#';
        return c is >= 'A' and <= 'Z' ? c.ToString() : "#";
    }

    // Model numbers in hundreds ("m0100–m0199"), then the model itself.
    private static string[] ByModel(char prefix, ushort model)
    {
        int lo = model / 100 * 100;
        return [$"{prefix}{lo:0000}–{prefix}{lo + 99:0000}", $"{prefix}{model:0000}"];
    }

    private static List<Entry> Read()
    {
        var models = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.ModelChara>();
        var list = new List<Entry>();
        var named = new Dictionary<uint, string>(); // model -> a name something that uses it has
        bool Usable(uint id, out Lumina.Excel.Sheets.ModelChara row)
        {
            row = default;
            if(id == 0 || models.GetRowOrDefault(id) is not { } r || r.Type is not (2 or 3))
                return false;
            row = r;
            return true;
        }
        Entry Make(Lumina.Excel.Sheets.ModelChara r, string name, string kind, string[] groups)
            => new(r.RowId, name, kind, r.Model, r.Base, r.Variant, r.Type) { Groups = groups };

        // Minions and mounts (named).
        foreach(var c in Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Companion>())
        {
            var name = Title(c.Singular.ExtractText());
            if(name.Length > 0 && Usable(c.Model.RowId, out var r))
            {
                list.Add(Make(r, name, "Minions", [Letter(name)]));
                named.TryAdd(r.RowId, name);
            }
        }
        foreach(var m in Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Mount>())
        {
            var name = Title(m.Singular.ExtractText());
            if(name.Length > 0 && Usable(m.ModelChara.RowId, out var r))
            {
                list.Add(Make(r, name, "Mounts", [Letter(name)]));
                named.TryAdd(r.RowId, name);
            }
        }

        // NPCs that aren't people (moogles, chocobos, beasts, the odd talking creature): named.
        var residents = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.ENpcResident>();
        // (where each NPC stands: the game's placements of NPCs, by area)
        var areaOf = new Dictionary<uint, string[]>();
        try
        {
            foreach(var lv in Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Level>())
            {
                if(lv.Type != 8 || areaOf.ContainsKey(lv.Object.RowId) || lv.Territory.ValueNullable is not { } t)
                    continue;
                string zone = t.PlaceName.ValueNullable?.Name.ExtractText() ?? "";
                string region = t.PlaceNameRegion.ValueNullable?.Name.ExtractText() ?? "";
                if(zone.Length == 0)
                    continue;
                areaOf[lv.Object.RowId] = region.Length > 0 ? [region, zone] : [zone];
            }
        }
        catch(Exception e)
        {
            Plugin.Log.Debug(e, "Limber: NPC areas");
        }
        var npcs = new List<(Lumina.Excel.Sheets.ModelChara Row, string Name, string[]? Area)>();
        var seen = new HashSet<(uint, string)>();
        foreach(var e in Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.ENpcBase>())
        {
            if(!Usable(e.ModelChara.RowId, out var r))
                continue;
            var name = residents.GetRowOrDefault(e.RowId) is { } res ? Title(res.Singular.ExtractText()) : "";
            if(name.Length == 0)
                name = $"NPC {e.RowId}";
            if(seen.Add((r.RowId, name)))
                npcs.Add((r, name, areaOf.GetValueOrDefault(e.RowId)));
        }
        var nameCounts = npcs.GroupBy(n => n.Name).ToDictionary(g => g.Key, g => g.Count());
        foreach(var (r, name, area) in npcs)
        {
            // (one name used with several models, "Moogle" say: those together in a group of their own)
            string[] groups = nameCounts[name] > 1 ? [Letter(name), name] : [Letter(name)];
            list.Add(Make(r, nameCounts[name] > 1 ? $"{name} ({r.Model}-{r.Base}-{r.Variant})" : name, "NPCs (not humanoid)", groups)
                with { AreaGroups = area ?? ["(not placed anywhere)"] });
            named.TryAdd(r.RowId, name);
        }

        // Enemies: the models battle NPCs use (how many use each, too).
        var enemyUse = new Dictionary<uint, int>();
        foreach(var b in Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.BNpcBase>())
            if(b.ModelChara.RowId != 0)
                enemyUse[b.ModelChara.RowId] = enemyUse.GetValueOrDefault(b.ModelChara.RowId) + 1;

        // (a name for each creature model: what a minion, mount or NPC with it is called, to tell the families apart)
        var family = new Dictionary<(byte, ushort), string>();
        foreach(var e in list)
            family.TryAdd((e.Type, e.Model), e.Name.Split(" (")[0]);

        // Every model, by kind and number (named after what uses it, where something does).
        foreach(var row in models)
        {
            if(row.RowId == 0 || row.Type is not (2 or 3))
                continue;
            bool enemy = enemyUse.TryGetValue(row.RowId, out var uses);
            string label = $"{(row.Type == 2 ? "d" : "m")}{row.Model:0000} b{row.Base:00} v{row.Variant:00}";
            if(named.TryGetValue(row.RowId, out var like))
                label += $"  ({like})";
            if(enemy)
                label += $"  [{uses} battle NPC{(uses == 1 ? "" : "s")}]";
            string kind = row.Type == 2 ? "Beast tribes and demihumans" : enemy ? "Enemies and battle NPCs" : "Other creatures";
            var groups = ByModel(row.Type == 2 ? 'd' : 'm', row.Model);
            if(family.TryGetValue((row.Type, row.Model), out var fam))
                groups[1] += $"  ({fam})";
            list.Add(Make(row, label, kind, groups));
        }

        list.Sort((a, b) =>
        {
            int k = string.CompareOrdinal(a.Kind, b.Kind);
            if(k != 0)
                return k;
            for(int i = 0; i < Math.Min(a.Groups.Length, b.Groups.Length); i++)
            {
                int g = string.Compare(a.Groups[i], b.Groups[i], StringComparison.OrdinalIgnoreCase);
                if(g != 0)
                    return g;
            }
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        return list;
    }

    private static NativeCharacter* Get(nint address, ushort index)
    {
        var obj = ActorSkeleton.GetObject(index);
        if(obj == null || (nint)obj != address || !obj->IsCharacter())
            return null;
        return (NativeCharacter*)obj;
    }

    /// <summary>Which model the character is drawn with (0: a normal human body from its customize).</summary>
    public static int ModelOf(nint address, ushort index)
    {
        var c = Get(address, index);
        return c == null ? -1 : c->ModelContainer.ModelCharaId;
    }

    // Each character's own model before Limber changed it (by address), to put back.
    private static readonly Dictionary<nint, int> Original = new();

    /// <summary>Draws the character as this model (0: back to its own human body). Redrawn.</summary>
    public static string Become(nint address, ushort index, int model)
    {
        var c = Get(address, index);
        if(c == null)
            return "The character isn't there.";
        if(!Original.ContainsKey(address))
            Original[address] = c->ModelContainer.ModelCharaId;
        if(c->ModelContainer.ModelCharaId == model)
            return "Already that.";
        c->ModelContainer.ModelCharaId = model;
        Appearance.Redraw(index);
        return model == 0 ? "Back to a normal body (redrawn)." : "Changed (redrawn).";
    }

    /// <summary>Back to the model it had before Limber changed it.</summary>
    public static string Restore(nint address, ushort index)
    {
        int model = Original.TryGetValue(address, out var m) ? m : 0;
        Original.Remove(address);
        var c = Get(address, index);
        if(c == null)
            return "The character isn't there.";
        c->ModelContainer.ModelCharaId = model;
        Appearance.Redraw(index);
        return "Back to its own look (redrawn).";
    }

    /// <summary>Whether Limber changed this character's model.</summary>
    public static bool Changed(nint address) => Original.ContainsKey(address);

    /// <summary>Written into a new actor before it's first drawn, so it appears as the creature straight away.</summary>
    public static void WriteUndrawn(ushort index, int model)
    {
        var obj = ActorSkeleton.GetObject(index);
        if(obj == null || !obj->IsCharacter())
            return;
        ((NativeCharacter*)obj)->ModelContainer.ModelCharaId = model;
    }
}

/// <summary>
/// Extras: more minions. Extra client-side creatures (any minion, or any creature) that follow your character
/// around, each in its own spot behind you, walking when they move. Only on your screen; outside GPose they need
/// "Also run outside GPose" (Advanced), as all of Limber's actors do.
/// </summary>
public sealed unsafe class Followers
{
    private readonly ActorSpawner _spawner;
    private readonly RigSettings _config;
    // Which actor each wanted follower is now (index), by its place in the list.
    private readonly Dictionary<int, ushort> _made = new();
    private readonly Dictionary<ushort, (Vector3 Velocity, bool Walking)> _motion = new();
    private int _retry;
    public string Status { get; private set; } = "";

    public Followers(ActorSpawner spawner, RigSettings config)
    {
        _spawner = spawner;
        _config = config;
    }

    /// <summary>Framework update.</summary>
    public void Update(float dt)
    {
        var wanted = _config.Followers ??= new();
        bool on = _config.FollowersOn && wanted.Count > 0;
        // (gone ones, and ones no longer wanted)
        foreach(var (slot, index) in _made.ToArray())
            if(!on || slot >= wanted.Count || !_spawner.StillOurs(index))
            {
                if(_spawner.StillOurs(index))
                    _spawner.Despawn(index);
                _made.Remove(slot);
                _motion.Remove(index);
            }
        if(!on)
            return;
        var me = Plugin.ObjectTable.LocalPlayer;
        if(me == null)
            return;
        // Missing ones made (a couple of times a second at most: after a zone change, they come back).
        if(++_retry % 30 == 0)
            for(int slot = 0; slot < wanted.Count && slot < MaxFollowers; slot++)
            {
                if(_made.ContainsKey(slot))
                    continue;
                if(!_spawner.CanAdd)
                {
                    Status = "Outside GPose, followers need \"Also run outside GPose\" on (Advanced).";
                    break;
                }
                int index = _spawner.SpawnHelper(me);
                if(index < 0)
                {
                    Status = _spawner.LastError ?? "The game wouldn't add another one.";
                    break;
                }
                Entities.WriteUndrawn((ushort)index, (int)wanted[slot].Model);
                _made[slot] = (ushort)index;
                Status = "";
            }

        // Each to its own place behind you (a fan), walking there at up to your pace.
        var at = me.Position;
        float facing = me.Rotation;
        var forward = new Vector3(MathF.Sin(facing), 0f, MathF.Cos(facing));
        var right = new Vector3(forward.Z, 0f, -forward.X);
        int count = Math.Min(wanted.Count, MaxFollowers);
        float spacing = Math.Clamp(_config.FollowerSpacing, 0.4f, 5f);
        foreach(var (slot, index) in _made)
        {
            var obj = ActorSkeleton.GetObject(index);
            if(obj == null)
                continue;
            // rows of three behind you: 0 middle, then left, right; the next row further back
            int row = slot / 3, col = slot % 3;
            float side = col == 0 ? 0f : col == 1 ? -1f : 1f;
            if(count == 2)
                side = slot == 0 ? -0.6f : 0.6f;
            var goal = at - forward * (spacing * (1.2f + row)) + right * (side * spacing);
            Vector3 pos = obj->Position;
            var to = goal - pos;
            to.Y = 0f;
            float dist = to.Length();
            var (velocity, walking) = _motion.TryGetValue(index, out var m) ? m : (Vector3.Zero, false);
            float speed = Math.Clamp(dist * 2.5f, 0f, 7f); // (faster to catch up from further away)
            var want = dist > 0.05f ? to / dist * speed : Vector3.Zero;
            velocity += (want - velocity) * MathF.Min(1f, dt * 6f);
            if(dist > 25f)
            {
                // (far behind, a teleport or a zone line: straight there)
                pos = goal;
                velocity = Vector3.Zero;
            }
            else
                pos += velocity * dt;
            pos.Y += (at.Y - pos.Y) * MathF.Min(1f, dt * 8f); // (keeps to your height: no floor check)
            obj->SetPosition(pos.X, pos.Y, pos.Z);
            float moving = new Vector2(velocity.X, velocity.Z).Length();
            if(moving > 0.3f)
                obj->SetRotation(MathF.Atan2(velocity.X, velocity.Z));
            else if(dist < 0.3f)
            {
                // (stopped: turned the way you face, slowly)
                float r = obj->Rotation;
                float d = MathF.IEEERemainder(facing - r, MathF.Tau);
                obj->SetRotation(r + d * MathF.Min(1f, dt * 2f));
            }
            bool walk = moving > 0.5f;
            if(walk != walking)
            {
                if(walk)
                    Animations.Play((nint)obj, index, WalkTimeline);
                else
                    Animations.Stop((nint)obj, index);
            }
            _motion[index] = (velocity, walk);
        }
    }

    // The game's own walk (each creature plays its own version of it).
    private const ushort WalkTimeline = 13;
    public const int MaxFollowers = 12;

    public void RemoveAll()
    {
        foreach(var index in _made.Values)
            if(_spawner.StillOurs(index))
                _spawner.Despawn(index);
        _made.Clear();
        _motion.Clear();
    }
}

/// <summary>One extra minion that follows you (Extras).</summary>
[Serializable]
public sealed class FollowerSettings
{
    public uint Model { get; set; }
    public string Name { get; set; } = "";
}
