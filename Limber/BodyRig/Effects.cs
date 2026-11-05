using Dalamud.Plugin.Services;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using VfxObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.VfxObject;

namespace Limber.BodyRig;

/// <summary>One visual effect (.avfx) from the game's data, with the type and group it's listed under.</summary>
/// <summary>The axes an effect on a bone is offset (and turned) along.</summary>
public enum EffectAxes
{
    Bone,      // the bone's own (it turns with the bone)
    Character, // the character's: forward, sideways, up from the way it faces
    World,     // the world's (only its place follows the bone)
}

public sealed record EffectItem(string Path, string Name, string Type, string Group)
{
    /// <summary>The other things in the game that use it (actions, statuses, emotes), for the tooltip and the search.</summary>
    public string Also { get; init; } = "";
    /// <summary>The file's own name.</summary>
    public string File => System.IO.Path.GetFileNameWithoutExtension(Path);
    /// <summary>What it looks like (smoke, fire, sparkles...), from its names: for finding one by kind.</summary>
    public string[] Kinds { get; init; } = [];
}

/// <summary>An effect placed in the scene (only on your screen: a render object the server never hears of).</summary>
public sealed class SpawnedEffect
{
    public Guid Id { get; } = Guid.NewGuid();
    public required EffectItem Item { get; init; }
    public Vector3 Position;
    public Vector3 Turn;   // degrees: yaw, pitch, roll
    public float Scale = 1f;
    /// <summary>Played again every Every seconds (most effects play once and end).</summary>
    public bool Repeat;
    public float Every = 3f;

    // On a character's bone (0: not): Position and Turn are then its offset from the bone (the bone's own axes, metres
    // at the character's own size), and it moves, turns (and, with AttachSize, sizes) with it every frame.
    public nint AttachTo;
    public ushort AttachIndex;
    public string AttachBone = "";
    public string AttachName = "";
    public bool AttachSize = true;
    public EffectAxes Axes = EffectAxes.Bone;
    internal bool KeepPlace;

    internal nint Vfx;
    internal long Played;
    internal bool Dirty = true;
    internal bool Replay;
    internal bool Unplayed; // made, to be played once it's in its place

    // Played on a character (Caster != 0): the game's own character effect, from the caster at the target (both game
    // objects), so its bind points find their bones (a hand, the head, the target's chest). The game moves it.
    public nint Caster, Target;
    public ushort CasterIndex, TargetIndex;
    public string CasterName = "", TargetName = "";
    public bool OnCharacter => Caster != 0;
    internal nint Actor; // the game's character effect, while it lasts (the game may end it by itself)

    // "Move it": played on a hidden helper instead (a copy of the caster, holding the caster's pose every frame), put
    // MoveOffset away from the caster (along its facing, or the world's axes) and turned by MoveTurn (degrees).
    public bool MoveIt;
    public Vector3 MoveOffset;
    public Vector3 MoveTurn;
    public bool MoveWorldAxes;
    public bool ShowHelper;
    public string MoveStatus = "";
    internal int Helper = -1;      // its object table slot (-1: none)
    internal nint HelperAddress;
    internal bool OnHelper;        // the effect playing now was made on the helper
    internal long NextHelperTry;
    internal volatile bool HelperOk; // checked this frame: still the helper Limber made (the bone update only poses it then)
}

/// <summary>
/// The game's visual effects (Effects page): every one the game data names, in groups by type, to place in the scene
/// or put on a character's bone. They're the game's own effect objects, made on this client only; removed when you
/// leave GPose (unless Limber runs outside it), turn Limber off, or unload it.
/// </summary>
public sealed unsafe class Effects : IDisposable
{
    // The game's "make an effect object" (path, the scheduler pool it's counted under) and "play it" (from a time, a
    // loop count: -1 as it's made). Found in the game by these patterns; a pattern that doesn't match exactly once
    // leaves effects off (a wrong match could call an unrelated function). After a game patch, update them here.
    private const string CreatePattern = "48 89 5C 24 08 57 48 83 EC 20 48 8B 05 ?? ?? ?? ?? 48 8B F9 BA 90 03 00 00 41 B8 10 00 00 00";
    private const string PlayPattern = "48 89 5C 24 10 48 89 74 24 20 57 48 81 EC 90 00 00 00 0F 29 B4 24 80 00 00 00 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 44 24 70 83 89 48 02 00 00 40";

    private readonly delegate* unmanaged<byte*, byte*, nint> _create;
    private readonly delegate* unmanaged<nint, float, int, nint> _play;

    // The game's character effects: made from (path, caster, target, -1, 0, 0, 0) -> the effect (it binds to their
    // bones and plays by itself); ended with its destructor (the first entry of its vtable, found from where its
    // constructor sets it). That destructor is hooked to know when the game ends one by itself (a finished effect,
    // a character gone), so a pointer is never used after it's freed.
    private const string ActorCreatePattern = "40 53 55 56 57 48 81 EC 08 02 00 00 0F 29 B4 24 F0 01 00 00 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 E0 01 00 00 0F B6 AC 24 50 02 00 00 0F 28 F3 49 8B F8";
    private const string ActorVtablePattern = "0F 11 48 10 48 8D 05 ?? ?? ?? ?? 48 89 B3 B8 01 00 00 48 89 03";
    private readonly delegate* unmanaged<byte*, nint, nint, float, byte, ushort, byte, nint> _actorCreate;
    private delegate nint ActorDtor(nint effect, byte flags);
    private Dalamud.Hooking.Hook<ActorDtor>? _actorDtor;
    private readonly Dictionary<nint, SpawnedEffect> _actors = new(); // ours, alive (any thread: locked)

    /// <summary>Set by the plugin: a hidden helper copying a character (slot, or -1), and taking one away again.</summary>
    public Func<nint, int>? SpawnHelper;
    public Action<ushort>? DespawnHelper;
    public Func<string?>? HelperError;
    /// <summary>The helper at that slot is still the one Limber made (the game reuses slots, and their memory).</summary>
    public Func<ushort, bool>? HelperStillOurs;

    /// <summary>Character effects can be played (the game's functions were found).</summary>
    public bool ActorReady => _actorCreate != null && _actorDtor != null;
    private readonly nint _pool; // (the game keeps the pool name's pointer: ours lives as long as the plugin)

    private readonly IDataManager _data;
    private readonly IPluginLog _log;
    private readonly List<SpawnedEffect> _spawned = [];
    private readonly ConcurrentQueue<Action> _jobs = new();
    private Task<EffectItem[]>? _catalog;

    public string Status { get; } = "";
    public bool Ready => _create != null && _play != null;

    public Effects(IDataManager data, ISigScanner scanner, Dalamud.Plugin.Services.IGameInteropProvider interop, IPluginLog log)
    {
        _data = data;
        _log = log;
        _pool = Marshal.StringToHGlobalAnsi("Client.System.Scheduler.Instance.VfxObject");
        try
        {
            var create = scanner.ScanAllText(CreatePattern);
            var play = scanner.ScanAllText(PlayPattern);
            if(create.Length == 1 && play.Length == 1)
            {
                _create = (delegate* unmanaged<byte*, byte*, nint>)create[0];
                _play = (delegate* unmanaged<nint, float, int, nint>)play[0];
            }
            else
            {
                Status = "Couldn't find the game's effect functions (this game version needs an updated build): effects are off.";
                _log.Warning($"Limber: effect patterns matched {create.Length} and {play.Length} places: effects off");
            }
        }
        catch(Exception e)
        {
            Status = "Couldn't search for the game's effect functions: effects are off.";
            _log.Warning(e, "Limber: effect pattern scan failed");
        }
        try
        {
            var create = scanner.ScanAllText(ActorCreatePattern);
            var vtable = scanner.ScanAllText(ActorVtablePattern);
            if(create.Length == 1 && vtable.Length == 1)
            {
                nint lea = vtable[0] + 4; // lea rax, [rip + disp]: the vtable
                nint table = lea + 7 + *(int*)(lea + 3);
                nint dtor = *(nint*)table;
                _actorCreate = (delegate* unmanaged<byte*, nint, nint, float, byte, ushort, byte, nint>)create[0];
                _actorDtor = interop.HookFromAddress<ActorDtor>(dtor, ActorDtorDetour);
                _actorDtor.Enable();
            }
            else
                _log.Warning($"Limber: character effect patterns matched {create.Length} and {vtable.Length} places: character effects off");
        }
        catch(Exception e)
        {
            _actorCreate = null;
            _log.Warning(e, "Limber: couldn't set up character effects");
        }
    }

    private nint ActorDtorDetour(nint effect, byte flags)
    {
        try
        {
            lock(_actors)
                if(_actors.Remove(effect, out var mine))
                    mine.Actor = 0;
        }
        catch
        {
        }
        return _actorDtor!.Original(effect, flags);
    }

    // ---- The list (background, once)

    // (in two goes: the game's sheets first, quick; then the effects placed in every zone, slower, added on)
    private volatile EffectItem[]? _items;

    public EffectItem[]? Catalog
    {
        get
        {
            _catalog ??= Task.Run(() =>
            {
                var items = Load();
                _items = items;
                try
                {
                    var places = LoadPlaces(new HashSet<string>(items.Select(i => i.Path), StringComparer.OrdinalIgnoreCase));
                    if(places.Count > 0)
                        _items = Sorted(items.Concat(places));
                }
                catch(Exception e)
                {
                    _log.Debug(e, "Limber: couldn't read the zones' effects");
                }
                return _items!;
            });
            return _items ?? (_catalog.IsCompletedSuccessfully ? _catalog.Result : null);
        }
    }

    public bool CatalogFailed => _catalog?.IsFaulted == true && _items == null;

    /// <summary>The zones' own effects are still being read (the list grows when they're in).</summary>
    public bool CatalogGrowing => _catalog is { IsCompleted: false } && _items != null;

    private static EffectItem[] Sorted(IEnumerable<EffectItem> items)
        => items.OrderBy(i => i.Type, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Group, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    // ---- The zones' own effects (steam from a hot spring, a chimney's smoke, torches...): every effect placed in each
    // zone's layout and in the groups it places, named after the place

    private static readonly string[] LayoutFiles = ["bg.lgb", "planevent.lgb", "planlive.lgb", "planmap.lgb", "planner.lgb"];

    private List<EffectItem> LoadPlaces(HashSet<string> seen)
    {
        var list = new List<EffectItem>();
        var zones = _data.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>();
        if(zones == null)
            return list;
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var groupsRead = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach(var zone in zones)
        {
            string bg = zone.Bg.ExtractText();
            int slash = bg.LastIndexOf('/');
            if(bg.Length == 0 || slash <= 0)
                continue;
            string folder = "bg/" + bg[..slash] + "/";
            if(!folders.Add(folder))
                continue;
            string place = "";
            try
            {
                place = zone.PlaceName.ValueNullable?.Name.ExtractText() ?? "";
            }
            catch
            {
            }
            if(place.Length == 0)
                place = Title(bg[(bg.LastIndexOf('/') + 1)..]);
            var paths = new List<string>();
            foreach(var name in LayoutFiles)
            {
                Lumina.Data.Files.LgbFile? file;
                try
                {
                    file = _data.GetFile<Lumina.Data.Files.LgbFile>(folder + name);
                }
                catch
                {
                    continue;
                }
                if(file == null)
                    continue;
                foreach(var layer in file.Layers)
                    foreach(var entry in layer.InstanceObjects)
                        Collect(entry.AssetType.ToString(), entry.Object, paths, groupsRead, 0);
            }
            foreach(var path in paths)
            {
                if(!seen.Add(path))
                    continue;
                var item = Describe(path);
                string readable = Readable(item.File);
                var named = new EffectItem(path, readable, "Places", place) { Kinds = KindsOf(readable + " " + path, "") };
                list.Add(named);
            }
        }
        return list;
    }

    // An effect placed in a layout, or the effects in a group it places (each group read once).
    private void Collect(string type, object? obj, List<string> paths, Dictionary<string, List<string>> groups, int depth)
    {
        if(obj == null)
            return;
        string? asset = Get(obj, "AssetPath") as string ?? FieldText(obj, "AssetPath");
        if(string.IsNullOrEmpty(asset))
            return;
        if(type.Equals("VFX", StringComparison.OrdinalIgnoreCase))
        {
            if(asset.EndsWith(".avfx", StringComparison.OrdinalIgnoreCase))
                paths.Add(asset.ToLowerInvariant());
            return;
        }
        if(!type.Equals("SharedGroup", StringComparison.OrdinalIgnoreCase) || depth > 2 || groups.Count > 40000)
            return;
        if(!groups.TryGetValue(asset, out var inside))
        {
            groups[asset] = inside = new List<string>();
            try
            {
                var sgb = _data.GetFile<Lumina.Data.Files.SgbFile>(asset);
                if(sgb != null)
                    foreach(var group in sgb.LayerGroups)
                        foreach(var layer in group.Layers)
                            foreach(var entry in layer.InstanceObjects)
                                Collect(entry.AssetType.ToString(), entry.Object, inside, groups, depth + 1);
            }
            catch
            {
            }
        }
        paths.AddRange(inside);
    }

    // (a field rather than a property: Lumina's layout objects keep their paths in fields)
    private static string? FieldText(object o, string name)
    {
        try
        {
            return o.GetType().GetField(name)?.GetValue(o) as string;
        }
        catch
        {
            return null;
        }
    }

    // A file name in plain words: "s1h0_steam_01" -> "Steam 01".
    private static string Readable(string file)
    {
        var pieces = file.Split(['_', '-', ' '], StringSplitOptions.RemoveEmptyEntries).ToList();
        // (the leading zone / set code, like "s1h0", "w1t2" or "f1f1", says nothing to a person)
        while(pieces.Count > 1 && pieces[0].Length <= 5 && pieces[0].Any(char.IsDigit) && pieces[0].Any(char.IsLetter))
            pieces.RemoveAt(0);
        var text = string.Join(" ", pieces);
        return text.Length == 0 ? file : char.ToUpperInvariant(text[0]) + text[1..];
    }

    // ---- Kinds: what an effect looks like, from the words in its names (English, the game's short forms, and the
    // Japanese words its files use)

    public static readonly (string Kind, string[] Words)[] KindWords =
    [
        ("Smoke, steam and fog", ["smoke", "smk", "steam", "stm", "fog", "mist", "kemuri", "yuge", "kiri", "vapor", "vapour", "haze", "dust", "cloud", "kumo", "soot"]),
        ("Fire and flames", ["fire", "fira", "firaga", "flame", "flm", "burn", "blaze", "ember", "hono", "honoo", "torch", "candle", "inferno", "pyre", "heat", "lava", "magma"]),
        ("Water and bubbles", ["water", "wtr", "watera", "bubble", "awa", "splash", "rain", "wave", "aqua", "mizu", "drop", "ripple", "fountain", "flood", "tide"]),
        ("Ice and snow", ["ice", "blizzard", "blizzara", "frost", "snow", "freeze", "frozen", "yuki", "koori", "glacier", "cold", "chill"]),
        ("Lightning and electricity", ["thunder", "thundara", "lightning", "elec", "electric", "bolt", "zap", "kaminari", "raiden", "volt", "plasma"]),
        ("Wind and air", ["wind", "aero", "aerora", "gust", "tornado", "whirl", "kaze", "storm", "breeze", "cyclone", "vortex"]),
        ("Earth, rock and sand", ["stone", "rock", "earth", "quake", "sand", "iwa", "tsuchi", "tuchi", "dirt", "debris", "rubble", "crystal"]),
        ("Light, holy and glows", ["holy", "light", "glow", "shine", "ray", "beam", "radiant", "hikari", "lumin", "bright", "flash", "halo", "sun"]),
        ("Darkness and shadow", ["dark", "darkness", "shadow", "void", "black", "yami", "abyss", "umbral", "gloom", "death", "doom", "curse"]),
        ("Sparkles and glitter", ["sparkle", "glitter", "kira", "twinkle", "star", "glint", "spark", "shimmer", "glitz", "firefly", "hotaru"]),
        ("Hearts and love", ["heart", "love", "kiss", "charm", "valentione", "valentine", "romance", "lovely"]),
        ("Healing and buffs", ["heal", "cure", "regen", "medica", "benediction", "esuna", "raise", "revive", "buff", "protect", "shell", "barrier"]),
        ("Poison and sickness", ["poison", "bio", "venom", "toxic", "doku", "sick", "disease", "miasma", "acid", "rot"]),
        ("Hits, slashes and blood", ["blood", "hit", "slash", "cut", "strike", "impact", "wound", "stab", "slice", "claw", "bite", "punch", "kick"]),
        ("Explosions", ["explo", "explosion", "bomb", "blast", "burst", "bakuha", "detonate", "nuke", "boom", "eruption"]),
        ("Magic circles and glyphs", ["circle", "glyph", "sigil", "rune", "mahou", "maho", "magic", "seal", "pentagram", "aspect", "ley"]),
        ("Auras", ["aura", "body", "loop", "spirit", "soul", "aether", "ether"]),
        ("Petals, leaves and flowers", ["petal", "flower", "leaf", "leaves", "hana", "sakura", "rose", "blossom", "lotus", "flw"]),
        ("Music and notes", ["music", "note", "song", "onpu", "melody", "dance", "bard", "minuet", "ballad", "paeon"]),
        ("Teleports and warps", ["teleport", "warp", "return", "portal", "gate", "summon", "appear", "vanish", "fade"]),
        ("Weapon trails", ["trail", "kiseki", "weapon", "sword", "blade", "arrow", "bullet", "shot", "gun"]),
        ("Fireworks and celebration", ["firework", "hanabi", "confetti", "party", "celebrat", "festival", "cracker", "ribbon"]),
        ("Animals and creatures", ["bird", "fish", "butterfly", "chou", "bat", "insect", "bug", "chocobo", "moogle", "fairy", "carbuncle"]),
    ];

    public static string[] KindsOf(string text, string type)
    {
        var lower = text.ToLowerInvariant();
        var pieces = lower.Split([' ', '_', '-', '.', '/', '(', ')', ',', '\'', ':'], StringSplitOptions.RemoveEmptyEntries);
        var kinds = new List<string>();
        foreach(var (kind, words) in KindWords)
        {
            bool hit = false;
            foreach(var w in words)
            {
                foreach(var piece in pieces)
                    // (a whole word, or the start of one: "smoke01", "firaga"; a long word anywhere in it)
                    if(piece == w || (piece.StartsWith(w, StringComparison.Ordinal) && (piece.Length == w.Length || !char.IsLetter(piece[w.Length]) || w.Length >= 4))
                       || (w.Length >= 5 && piece.Contains(w, StringComparison.Ordinal)))
                    {
                        hit = true;
                        break;
                    }
                if(hit)
                    break;
            }
            if(hit)
                kinds.Add(kind);
        }
        // (by where it's from)
        if(type == "Area markers (omens)")
            kinds.Add("Area markers");
        else if(type == "Tethers")
            kinds.Add("Tethers");
        else if(type == "Markers over the head")
            kinds.Add("Markers over the head");
        else if(type == "Status effects")
            kinds.Add("Auras");
        if(kinds.Count == 0)
            kinds.Add("Other");
        return kinds.Distinct().ToArray();
    }

    /// <summary>Every kind, in order (for the page's buttons).</summary>
    public static readonly string[] AllKinds = KindWords.Select(k => k.Kind)
        .Concat(["Area markers", "Tethers", "Markers over the head", "Other"]).ToArray();

    // Every effect path the game's sheets name: their own effects (VFX), area markers (Omen), tethers (Channeling)
    // and markers over heads (Lockon); each kept only if the file is there.
    private EffectItem[] Load()
    {
        var found = new Dictionary<string, EffectItem>(StringComparer.OrdinalIgnoreCase);
        foreach(var (sheet, folder) in new[] { ("VFX", ""), ("Omen", "vfx/omen/eff/"), ("Channeling", "vfx/channeling/eff/"), ("Lockon", "vfx/lockon/eff/") })
        {
            try
            {
                var rows = _data.GetExcelSheet<Lumina.Excel.RawRow>(name: sheet);
                if(rows == null)
                    continue;
                foreach(var row in rows)
                {
                    var columns = row.Columns;
                    for(int c = 0; c < columns.Count; c++)
                    {
                        if(columns[c].Type.ToString() != "String")
                            continue;
                        string s;
                        try
                        {
                            s = row.ReadStringColumn(c).ExtractText().Trim();
                        }
                        catch
                        {
                            continue;
                        }
                        if(s.Length < 3 || s.Contains(' '))
                            continue;
                        if(PathFor(s, folder) is { } path && !found.ContainsKey(path))
                            found[path] = Describe(path);
                    }
                }
            }
            catch(Exception e)
            {
                _log.Debug(e, $"Limber: couldn't read the {sheet} sheet for effects");
            }
        }
        // Named by what uses them: actions (by job), statuses and emotes (their effects, and the ones in their
        // animations' timelines).
        try
        {
            Name(found);
        }
        catch(Exception e)
        {
            _log.Debug(e, "Limber: couldn't name the effects");
        }
        return Sorted(found.Values.Select(i => i with { Kinds = KindsOf($"{i.Name} {i.Also} {i.Path}", i.Type) }));
    }

    // ---- Names from what uses each effect (read with reflection over the sheets, so a renamed column only means
    // fewer names, never a broken build)

    private void Name(Dictionary<string, EffectItem> found)
    {
        var users = new Dictionary<string, List<(string Name, string Type, string Group)>>(StringComparer.OrdinalIgnoreCase);
        void Use(string path, string name, string type, string group)
        {
            if(!users.TryGetValue(path, out var list))
                users[path] = list = new();
            if(list.Count < 12 && !list.Any(u => u.Name == name))
                list.Add((name, type, group));
        }

        foreach(var row in _data.GetExcelSheet<Lumina.Excel.Sheets.Action>() ?? Enumerable.Empty<Lumina.Excel.Sheets.Action>())
        {
            string name = Text(row, "Name");
            if(name.Length == 0)
                continue;
            string job = Title(Text(Deref(row, "ClassJob"), "Name"));
            string group = job.Length > 0 ? job : "Other actions";
            foreach(var path in PathsOf(row, 3))
                Use(path, name, "Actions (by job)", group);
        }
        foreach(var row in _data.GetExcelSheet<Lumina.Excel.Sheets.Status>() ?? Enumerable.Empty<Lumina.Excel.Sheets.Status>())
        {
            string name = Text(row, "Name");
            if(name.Length == 0)
                continue;
            foreach(var path in PathsOf(row, 3))
                Use(path, name, "Status effects", Initial(name));
        }
        foreach(var row in _data.GetExcelSheet<Lumina.Excel.Sheets.Emote>() ?? Enumerable.Empty<Lumina.Excel.Sheets.Emote>())
        {
            string name = Text(row, "Name");
            if(name.Length == 0)
                continue;
            foreach(var path in PathsOf(row, 2))
                Use(path, name, "Emotes", Initial(name));
        }

        foreach(var (path, list) in users)
        {
            var (name, type, group) = list[0];
            string also = string.Join(", ", list.Skip(1).Select(u => u.Name));
            found[path] = new EffectItem(path, name, type, group) { Also = also };
        }
        // (one action with several effects: told apart by their files)
        foreach(var same in found.Values.GroupBy(i => (i.Type, i.Group, i.Name)).Where(g => g.Count() > 1).ToList())
            foreach(var item in same)
                found[item.Path] = item with { Name = $"{item.Name} ({item.File})" };
    }

    private static string Initial(string name)
        => name.Length > 0 && char.IsLetter(name[0]) ? char.ToUpperInvariant(name[0]).ToString() : "#";

    private static string Title(string s)
        => s.Length == 0 ? s : System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s);

    private static readonly ConcurrentDictionary<Type, System.Reflection.PropertyInfo[]> Props = new();

    private static System.Reflection.PropertyInfo[] PropsOf(Type t)
        => Props.GetOrAdd(t, x => x.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0).ToArray());

    private static object? Get(object? o, string name)
    {
        if(o == null)
            return null;
        try
        {
            return PropsOf(o.GetType()).FirstOrDefault(p => p.Name == name)?.GetValue(o);
        }
        catch
        {
            return null;
        }
    }

    // A text column's text ("" if it isn't one).
    private static string Text(object? o, string name) => TextOf(Get(o, name));

    private static string TextOf(object? v)
    {
        if(v == null)
            return "";
        if(v is string str)
            return str.Trim();
        if(v.GetType().Name != "ReadOnlySeString")
            return "";
        try
        {
            return (v.GetType().GetMethod("ExtractText", Type.EmptyTypes)?.Invoke(v, null) as string ?? "").Trim();
        }
        catch
        {
            return "";
        }
    }

    // The row a link column points to (null if none).
    private static object? Deref(object? o, string name) => RowOf(Get(o, name));

    private static object? RowOf(object? link)
    {
        if(link == null || !link.GetType().Name.StartsWith("RowRef", StringComparison.Ordinal))
            return null;
        return Get(link, "ValueNullable");
    }

    // The sheets worth following from a row to find its effects (not every link: an action links to its job, which
    // links to everything).
    private static bool Followed(Type rowRef)
    {
        if(!rowRef.IsGenericType)
            return false;
        string t = rowRef.GetGenericArguments()[0].Name;
        return t.Contains("VFX", StringComparison.OrdinalIgnoreCase) || t.Contains("Timeline", StringComparison.OrdinalIgnoreCase)
            || t.Contains("HitEffect", StringComparison.OrdinalIgnoreCase) || t is "Omen" or "Channeling" or "Lockon";
    }

    // Every effect file a row leads to: its text columns that name one, the timelines' own effects (in the .tmb),
    // and the same for the rows its effect / timeline links point to, so many links deep.
    private List<string> PathsOf(object row, int depth)
    {
        var paths = new List<string>();
        Walk(row, depth, paths);
        return paths;
    }

    private void Walk(object row, int depth, List<string> paths)
    {
        foreach(var p in PropsOf(row.GetType()))
        {
            object? v;
            try
            {
                v = p.GetValue(row);
            }
            catch
            {
                continue;
            }
            if(v == null)
                continue;
            var t = v.GetType();
            if(t.Name == "ReadOnlySeString")
            {
                string s = TextOf(v);
                if(s.Length < 3 || s.Contains(' '))
                    continue;
                if(p.Name == "Key" && row.GetType().Name == "ActionTimeline")
                    paths.AddRange(TimelineEffects(s));
                else if(p.Name is "Location" or "Path" or "PathAlly" or "File" && PathFor(s, row.GetType().Name switch
                    {
                        "Omen" => "vfx/omen/eff/",
                        "Channeling" => "vfx/channeling/eff/",
                        "Lockon" => "vfx/lockon/eff/",
                        _ => "",
                    }) is { } path)
                    paths.Add(path);
            }
            else if(depth > 0 && t.Name.StartsWith("RowRef", StringComparison.Ordinal) && Followed(t))
            {
                if(RowOf(v) is { } next)
                    Walk(next, depth - 1, paths);
            }
            else if(depth > 0 && v is System.Collections.IEnumerable many && v is not string && t.Name.StartsWith("Collection", StringComparison.Ordinal))
            {
                int k = 0;
                foreach(var item in many)
                {
                    if(++k > 16)
                        break;
                    if(item != null && item.GetType().Name.StartsWith("RowRef", StringComparison.Ordinal) && Followed(item.GetType()) && RowOf(item) is { } next)
                        Walk(next, depth - 1, paths);
                }
            }
        }
    }

    private readonly ConcurrentDictionary<string, string[]> _timelines = new(StringComparer.OrdinalIgnoreCase);
    private static readonly System.Text.RegularExpressions.Regex AvfxInFile =
        new(@"vfx/[a-z0-9_\-/]+\.avfx", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // The effects an animation's timeline plays (the .avfx paths written in its .tmb).
    private string[] TimelineEffects(string key)
    {
        if(key.Contains('[') || key.Length > 120)
            return [];
        return _timelines.GetOrAdd(key, k =>
        {
            try
            {
                var file = _data.GetFile("chara/action/" + k.ToLowerInvariant() + ".tmb");
                if(file == null)
                    return [];
                string text = System.Text.Encoding.ASCII.GetString(file.Data);
                return AvfxInFile.Matches(text).Select(m => m.Value.ToLowerInvariant()).Distinct()
                    .Where(p => _data.FileExists(p)).ToArray();
            }
            catch
            {
                return [];
            }
        });
    }

    private string? PathFor(string s, string folder)
    {
        s = s.Replace('\\', '/').ToLowerInvariant();
        var tries = new List<string>();
        if(s.EndsWith(".avfx", StringComparison.Ordinal))
        {
            tries.Add(s);
            tries.Add("vfx/" + s);
        }
        else
        {
            if(folder.Length > 0)
                tries.Add(folder + s + ".avfx");
            tries.Add(s + ".avfx");
            tries.Add("vfx/" + s + ".avfx");
            tries.Add("vfx/common/eff/" + s + ".avfx");
        }
        foreach(var t in tries)
            if(t.StartsWith("vfx/", StringComparison.Ordinal) && _data.FileExists(t))
                return t;
        return null;
    }

    private static readonly Dictionary<string, string> TypeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["common"] = "Common effects",
        ["omen"] = "Area markers (omens)",
        ["channeling"] = "Tethers",
        ["lockon"] = "Markers over the head",
        ["monster"] = "Monsters",
        ["weapon"] = "Weapons",
        ["ws"] = "Weaponskills",
        ["action"] = "Actions",
        ["cut"] = "Cutscenes",
        ["bg"] = "Places",
        ["equipment"] = "Gear",
        ["live"] = "Live events",
        ["general"] = "General",
        ["fishing"] = "Fishing",
        ["eff"] = "Effects",
    };

    // Its type (the folder under vfx/), and a group within it (the name's leading letters, e.g. "gl", "chn").
    private static EffectItem Describe(string path)
    {
        var parts = path.Split('/');
        string folder = parts.Length > 1 ? parts[1] : "other";
        string type = TypeNames.TryGetValue(folder, out var t) ? t : char.ToUpperInvariant(folder[0]) + folder[1..];
        string name = System.IO.Path.GetFileNameWithoutExtension(path);
        // (a monster's or a cutscene's effects: by whose folder they're in)
        string group;
        if(parts.Length > 4 && folder is "monster" or "cut" or "bg" or "weapon" or "equipment")
            group = parts[2];
        else
        {
            int end = 0;
            while(end < name.Length && char.IsLetter(name[end]))
                end++;
            group = end > 0 ? name[..end] : "other";
        }
        return new EffectItem(path, name, type, group);
    }

    // ---- Placed effects (the list is only changed on the framework thread; the UI reads a copy)

    public IReadOnlyList<SpawnedEffect> Spawned
    {
        get
        {
            lock(_spawned)
                return _spawned.ToArray();
        }
    }

    public void Spawn(EffectItem item, Vector3 position, float yawDegrees, Action<SpawnedEffect>? then = null)
        => _jobs.Enqueue(() =>
        {
            var effect = new SpawnedEffect { Item = item, Position = position, Turn = new Vector3(yawDegrees, 0f, 0f) };
            then?.Invoke(effect);
            lock(_spawned)
                _spawned.Add(effect);
        });

    /// <summary>Played on a character: from the caster, at the target (itself if the same).</summary>
    public void SpawnOn(EffectItem item, nint caster, ushort casterIndex, string casterName, nint target, ushort targetIndex, string targetName,
        Action<SpawnedEffect>? then = null)
        => _jobs.Enqueue(() =>
        {
            var effect = new SpawnedEffect
            {
                Item = item, Caster = caster, CasterIndex = casterIndex, CasterName = casterName,
                Target = target, TargetIndex = targetIndex, TargetName = targetName,
            };
            then?.Invoke(effect);
            lock(_spawned)
                _spawned.Add(effect);
        });

    public void Remove(Guid id)
        => _jobs.Enqueue(() =>
        {
            SpawnedEffect? effect;
            lock(_spawned)
            {
                effect = _spawned.Find(e => e.Id == id);
                if(effect != null)
                    _spawned.Remove(effect);
            }
            if(effect != null)
                Destroy(effect);
        });

    public void RemoveAll() => _jobs.Enqueue(DestroyAllNow);

    /// <summary>Play it again from the start now.</summary>
    public static void Replay(SpawnedEffect effect) => effect.Replay = true;

    public static void MarkChanged(SpawnedEffect effect) => effect.Dirty = true;

    // ---- On bones

    private readonly record struct BoneWorld(Vector3 Position, Quaternion Rotation, float Scale, Quaternion Facing);
    private readonly ConcurrentDictionary<Guid, BoneWorld> _bones = new();
    private volatile (Guid Id, nint Address, ushort Index, string Bone)[] _attached = [];
    private readonly Limber.Core.SkeletonInfoCache _boneCache = new();

    public static void Attach(SpawnedEffect effect, nint address, ushort index, string name, string bone, bool keep)
    {
        effect.AttachTo = address;
        effect.AttachIndex = index;
        effect.AttachName = name;
        effect.AttachBone = bone;
        effect.KeepPlace = keep;
        if(!keep)
        {
            effect.Position = Vector3.Zero;
            effect.Turn = Vector3.Zero;
        }
        effect.Dirty = true;
    }

    public void Detach(SpawnedEffect effect)
    {
        if(effect.AttachTo != 0 && !effect.KeepPlace && _bones.TryGetValue(effect.Id, out var b))
        {
            var (position, rotation, _) = WorldOf(effect, b);
            effect.Position = position;
            effect.Turn = ToYawPitchRoll(rotation);
        }
        effect.AttachTo = 0;
        effect.KeepPlace = false;
        _bones.TryRemove(effect.Id, out _);
        effect.Dirty = true;
    }

    public bool BoneFound(SpawnedEffect effect) => _bones.ContainsKey(effect.Id);

    /// <summary>Bone update stage (after the posing and physics): where the attached effects' bones are.</summary>
    public void CaptureBones()
    {
        var attached = _attached;
        if(attached.Length == 0)
        {
            if(!_bones.IsEmpty)
                _bones.Clear();
            return;
        }
        foreach(var (id, address, index, bone) in attached)
        {
            try
            {
                if(!Limber.Core.ActorSkeleton.TryGet(address, index, _boneCache, out var sk) || sk.Info.Count == 0)
                {
                    _bones.TryRemove(id, out _);
                    continue;
                }
                int i = sk.Info.IndexOf(bone);
                if(i < 0)
                {
                    _bones.TryRemove(id, out _);
                    continue;
                }
                var f = sk.GetModelFrame(i);
                var world = new BoneWorld(sk.ModelToWorldPoint(f.Position), sk.ModelToWorldRotation(f.Rotation), sk.RootScale.Y, sk.RootRotation);
                if(Limber.Core.MathUtil.IsFinite(world.Position) && Limber.Core.MathUtil.IsFinite(world.Rotation) && float.IsFinite(world.Scale))
                    _bones[id] = world;
            }
            catch
            {
                _bones.TryRemove(id, out _);
            }
        }
    }

    // The axes its offset is in (the bone's, the character's facing, or the world's).
    private static Quaternion AxesOf(SpawnedEffect e, BoneWorld b) => e.Axes switch
    {
        EffectAxes.Character => b.Facing,
        EffectAxes.World => Quaternion.Identity,
        _ => b.Rotation,
    };

    private static (Vector3 Position, Quaternion Rotation, float Scale) WorldOf(SpawnedEffect e, BoneWorld b)
    {
        var t = e.Turn * (MathF.PI / 180f);
        var local = Quaternion.CreateFromYawPitchRoll(t.X, t.Y, t.Z);
        var axes = AxesOf(e, b);
        return (b.Position + Vector3.Transform(e.Position * b.Scale, axes), Quaternion.Normalize(axes * local), e.AttachSize ? b.Scale : 1f);
    }

    /// <summary>Its offset kept where it is in the world while the axes change (attached ones; else just set).</summary>
    public void SetAxes(SpawnedEffect e, EffectAxes axes)
    {
        if(e.Axes == axes)
            return;
        if(e.AttachTo != 0 && !e.KeepPlace && _bones.TryGetValue(e.Id, out var b))
        {
            var (position, rotation, _) = WorldOf(e, b);
            e.Axes = axes;
            var inverse = Quaternion.Inverse(AxesOf(e, b));
            e.Position = Vector3.Transform(position - b.Position, inverse) / MathF.Max(b.Scale, 1e-5f);
            e.Turn = ToYawPitchRoll(Quaternion.Normalize(inverse * rotation));
        }
        else
            e.Axes = axes;
        e.Dirty = true;
    }

    private static Vector3 ToYawPitchRoll(Quaternion q)
    {
        q = Quaternion.Normalize(q);
        float x = q.X, y = q.Y, z = q.Z, w = q.W;
        float sp = Math.Clamp(2f * (w * x - y * z), -1f, 1f);
        float pitch = MathF.Asin(sp);
        float yaw, roll;
        if(MathF.Abs(sp) < 1f - 1e-6f)
        {
            yaw = MathF.Atan2(2f * (w * y + x * z), 1f - 2f * (x * x + y * y));
            roll = MathF.Atan2(2f * (w * z + x * y), 1f - 2f * (x * x + z * z));
        }
        else
        {
            yaw = MathF.Atan2(-2f * (x * z - w * y), 1f - 2f * (y * y + z * z));
            roll = 0f;
        }
        return new Vector3(yaw, pitch, roll) * (180f / MathF.PI);
    }

    // ---- Every frame (framework thread): made, placed, played again, removed

    public void Update(bool allowed)
    {
        if(!allowed || !Ready)
        {
            bool any;
            lock(_spawned)
                any = _spawned.Count > 0;
            if(any)
                DestroyAllNow();
            _jobs.Clear();
            _attached = [];
            return;
        }
        while(_jobs.TryDequeue(out var job))
        {
            try { job(); }
            catch(Exception e) { _log.Warning(e, "Limber: effect job failed"); }
        }

        SpawnedEffect[] effects;
        lock(_spawned)
            effects = _spawned.ToArray();
        var attached = new List<(Guid, nint, ushort, string)>();
        foreach(var e in effects)
            if(e.AttachTo != 0 && e.AttachBone.Length > 0)
                attached.Add((e.Id, e.AttachTo, e.AttachIndex, e.AttachBone));
        _attached = attached.ToArray();

        long now = Environment.TickCount64;
        foreach(var e in effects)
        {
            if(e.OnCharacter)
            {
                try
                {
                    UpdateOnCharacter(e, now);
                }
                catch(Exception ex)
                {
                    _log.Warning(ex, $"Limber: couldn't play the effect {e.Item.Path} on a character");
                }
                continue;
            }
            try
            {
                // Played again: made anew (an effect that has finished is just gone).
                bool again = e.Replay || (e.Repeat && e.Vfx != 0 && now - e.Played >= (long)(Math.Clamp(e.Every, 0.2f, 600f) * 1000f));
                if(again && e.Vfx != 0)
                    Destroy(e);
                e.Replay = false;
                if(e.Vfx == 0)
                {
                    e.Vfx = Make(e.Item.Path);
                    e.Played = now;
                    if(e.Vfx == 0)
                        continue;
                    e.Dirty = true;
                    e.Unplayed = true;
                }

                Vector3 at;
                Quaternion rotation;
                float size = 1f;
                if(e.AttachTo != 0)
                {
                    if(!_bones.TryGetValue(e.Id, out var bone))
                        continue; // (not found now: left where it was)
                    if(e.KeepPlace)
                    {
                        var t0 = e.Turn * (MathF.PI / 180f);
                        var rotation0 = Quaternion.CreateFromYawPitchRoll(t0.X, t0.Y, t0.Z);
                        var inverse = Quaternion.Inverse(AxesOf(e, bone));
                        e.Position = Vector3.Transform(e.Position - bone.Position, inverse) / MathF.Max(bone.Scale, 1e-5f);
                        e.Turn = ToYawPitchRoll(Quaternion.Normalize(inverse * rotation0));
                        if(e.AttachSize)
                            e.Scale /= MathF.Max(bone.Scale, 1e-5f);
                        e.KeepPlace = false;
                    }
                    (at, rotation, size) = WorldOf(e, bone);
                }
                else
                {
                    if(!e.Dirty)
                        continue;
                    var t = e.Turn * (MathF.PI / 180f);
                    at = e.Position;
                    rotation = Quaternion.CreateFromYawPitchRoll(t.X, t.Y, t.Z);
                }
                Place(e.Vfx, at, rotation, Math.Clamp(e.Scale, 0.001f, 1000f) * size);
                e.Dirty = false;
                if(e.Unplayed)
                {
                    e.Unplayed = false;
                    e.Played = now;
                    _play(e.Vfx, 0f, -1);
                }
            }
            catch(Exception ex)
            {
                _log.Warning(ex, $"Limber: couldn't update the effect {e.Item.Path}");
            }
        }
    }

    // A character effect: made (again) when asked, when repeating, or (Repeat) once the game ended the last one.
    private void UpdateOnCharacter(SpawnedEffect e, long now)
    {
        if(!ActorReady)
            return;
        // "Move it": the helper made (once), kept where it's moved to, and the effect made again on it once it's drawn.
        bool helperReady = UpdateHelper(e, now);
        bool onHelper = e.MoveIt && helperReady;
        if(onHelper != e.OnHelper)
            e.Replay = true;
        nint live;
        lock(_actors)
            live = e.Actor;
        // (repeating: again once it has ended by itself, or after Every seconds, whichever comes first; never more than
        // a few times a second, should one end at once)
        bool again = e.Replay || (e.Repeat && now - e.Played >= 300 && (live == 0 || now - e.Played >= (long)(Math.Clamp(e.Every, 0.2f, 600f) * 1000f)));
        e.Replay = false;
        if(!again && (live != 0 || e.Played != 0))
            return;
        EndActor(e);
        var caster = Limber.Core.ActorSkeleton.GetObject(e.CasterIndex);
        var target = Limber.Core.ActorSkeleton.GetObject(e.TargetIndex);
        if(caster == null || (nint)caster != e.Caster || target == null || (nint)target != e.Target || caster->DrawObject == null)
            return; // (not there now: tried again next frame)
        // (moved: from the helper; at itself, the helper is its target too)
        var helperObject = onHelper ? Limber.Core.ActorSkeleton.GetObject((ushort)e.Helper) : null;
        if(helperObject != null && (nint)helperObject == e.HelperAddress)
        {
            var helper = helperObject;
            if(e.Target == e.Caster)
                target = helper;
            caster = helper;
        }
        e.OnHelper = onHelper;
        var bytes = System.Text.Encoding.UTF8.GetBytes(e.Item.Path + "\0");
        nint made;
        fixed(byte* p = bytes)
            made = _actorCreate(p, (nint)caster, (nint)target, -1f, 0, 0, 0);
        e.Played = now;
        if(made == 0)
            return;
        lock(_actors)
        {
            _actors[made] = e;
            e.Actor = made;
        }
    }

    // The helper for "Move it": made when wanted, taken away when not; placed (and hidden) every frame. True once it's
    // drawn and can be played on.
    private bool UpdateHelper(SpawnedEffect e, long now)
    {
        if(!e.MoveIt)
        {
            RemoveHelper(e);
            e.MoveStatus = "";
            return false;
        }
        if(e.Helper < 0)
        {
            if(now < e.NextHelperTry || SpawnHelper == null)
                return false;
            e.NextHelperTry = now + 2000;
            int index = SpawnHelper(e.Caster);
            if(index < 0)
            {
                e.MoveStatus = "Couldn't add the helper: " + (HelperError?.Invoke() ?? "?");
                return false;
            }
            e.Helper = index;
            var made = Limber.Core.ActorSkeleton.GetObject((ushort)index);
            e.HelperAddress = (nint)made;
            e.MoveStatus = "Getting the helper ready...";
        }
        var helper = Limber.Core.ActorSkeleton.GetObject((ushort)e.Helper);
        var caster = Limber.Core.ActorSkeleton.GetObject(e.CasterIndex);
        if(helper == null || (nint)helper != e.HelperAddress || !(HelperStillOurs?.Invoke((ushort)e.Helper) ?? false))
        {
            // (gone: GPose ended, a new zone; maybe someone else in its slot now, who is left alone)
            e.HelperOk = false;
            e.Helper = -1;
            e.HelperAddress = 0;
            e.OnHelper = false;
            e.MoveStatus = "The helper went away: making it again.";
            return false;
        }
        e.HelperOk = true;
        // Hidden (the game's own "don't draw this model" flag), unless asked to show it.
        var flags = (int*)&helper->RenderFlags;
        if(e.ShowHelper)
            *flags &= ~2;
        else
            *flags |= 2;
        if(caster == null || (nint)caster != e.Caster || caster->DrawObject == null || helper->DrawObject == null)
            return false;
        // Where it goes: the caster's place and facing, moved and turned.
        var from = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object*)caster->DrawObject;
        var at = (Vector3)from->Position;
        var facing = (Quaternion)from->Rotation;
        var t = e.MoveTurn * (MathF.PI / 180f);
        var turn = Quaternion.CreateFromYawPitchRoll(t.X, t.Y, t.Z);
        var axes = e.MoveWorldAxes ? Quaternion.Identity : facing;
        var position = at + Vector3.Transform(e.MoveOffset * ((Vector3)from->Scale).Y, axes);
        var rotation = Quaternion.Normalize(e.MoveWorldAxes ? turn * facing : facing * turn);
        if(!Limber.Core.MathUtil.IsFinite(position) || !Limber.Core.MathUtil.IsFinite(rotation))
            return false;
        var to = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object*)helper->DrawObject;
        to->Position = position;
        to->Rotation = rotation;
        to->Scale = from->Scale;
        helper->Position = position;
        float yaw = MathF.Atan2(2f * (rotation.W * rotation.Y + rotation.X * rotation.Z), 1f - 2f * (rotation.Y * rotation.Y + rotation.X * rotation.X));
        helper->Rotation = yaw;
        e.MoveStatus = "";
        return true;
    }

    private void RemoveHelper(SpawnedEffect e)
    {
        e.HelperOk = false;
        if(e.Helper < 0)
            return;
        int index = e.Helper;
        e.Helper = -1;
        e.HelperAddress = 0;
        if(e.OnHelper)
            EndActor(e); // (what's on it goes with it)
        e.OnHelper = false;
        // (only if it's still ours: never anyone the game has put in its slot since)
        if(HelperStillOurs?.Invoke((ushort)index) ?? false)
            DespawnHelper?.Invoke((ushort)index);
    }

    /// <summary>Bone update stage (after the posing and physics): every helper holds its caster's pose.</summary>
    public void CopyPoses()
    {
        SpawnedEffect[] effects;
        lock(_spawned)
            effects = _spawned.ToArray();
        foreach(var e in effects)
        {
            if(!e.MoveIt || e.Helper < 0 || e.HelperAddress == 0 || !e.HelperOk)
                continue;
            try
            {
                if(!Limber.Core.ActorSkeleton.TryGet(e.Caster, e.CasterIndex, _boneCache, out var from)
                    || !Limber.Core.ActorSkeleton.TryGet(e.HelperAddress, (ushort)e.Helper, _boneCache, out var to))
                    continue;
                if(to.RestoreModelPose(from.CaptureModelPose()))
                    to.RefreshAttachedModels();
            }
            catch
            {
            }
        }
    }

    // Ends its character effect (if the game hasn't already).
    private void EndActor(SpawnedEffect e)
    {
        nint live;
        lock(_actors)
        {
            live = e.Actor;
            if(live == 0 || !_actors.Remove(live))
            {
                e.Actor = 0;
                return;
            }
            e.Actor = 0;
        }
        try
        {
            _actorDtor!.Original(live, 1);
        }
        catch(Exception ex)
        {
            _log.Warning(ex, "Limber: couldn't end a character effect");
        }
    }

    private nint Make(string path)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(path + "\0");
        nint vfx;
        fixed(byte* p = bytes)
            vfx = _create(p, (byte*)_pool);
        return vfx;
    }

    // Its place, turn and size (as the game writes them), marked changed for the game to pick up.
    private static void Place(nint vfx, Vector3 at, Quaternion rotation, float scale)
    {
        if(!Limber.Core.MathUtil.IsFinite(at) || !Limber.Core.MathUtil.IsFinite(rotation) || !float.IsFinite(scale))
            return;
        *(Vector3*)(vfx + 0x50) = at;
        *(Quaternion*)(vfx + 0x60) = rotation;
        *(Vector3*)(vfx + 0x70) = new Vector3(scale);
        *(ulong*)(vfx + 0x38) |= 2;
    }

    private void Destroy(SpawnedEffect e)
    {
        if(e.OnCharacter)
        {
            EndActor(e);
            RemoveHelper(e);
        }
        var vfx = e.Vfx;
        e.Vfx = 0;
        if(vfx == 0)
            return;
        try
        {
            var obj = (VfxObject*)vfx;
            obj->CleanupRender();
            obj->Dtor(1);
        }
        catch(Exception ex)
        {
            _log.Warning(ex, "Limber: couldn't remove an effect");
        }
    }

    private void DestroyAllNow()
    {
        SpawnedEffect[] effects;
        lock(_spawned)
        {
            effects = _spawned.ToArray();
            _spawned.Clear();
        }
        foreach(var e in effects)
            Destroy(e);
        _bones.Clear();
    }

    public void Dispose()
    {
        DestroyAllNow();
        _actorDtor?.Disable();
        _actorDtor?.Dispose();
        _actorDtor = null;
        // (the pool name stays: an effect the game still counts under it may outlive this by a frame)
    }
}
