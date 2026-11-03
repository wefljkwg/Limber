using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Layer;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

namespace Limber.BodyRig;

/// <summary>One housing item (indoor furnishing or outdoor yard item) from the game's data.</summary>
/// <summary>
/// One placeable prop: a housing item (indoor furnishing or outdoor yard item), or a world prop (one of the game's
/// shared groups, by its .sgb path in <paramref name="Sgb"/>), with the section it's listed under.
/// </summary>
public sealed record HousingItem(string Name, ushort ModelKey, bool Indoors, uint Icon, string Category = "", string? Sgb = null, string Kind = "")
{
    private string Folder => Indoors ? $"bgcommon/hou/indoor/general/{ModelKey:D4}" : $"bgcommon/hou/outdoor/general/{ModelKey:D4}";
    private string Stem => Indoors ? $"fun_b0_m{ModelKey:D4}" : $"gar_b0_m{ModelKey:D4}";
    public string GroupPath => Sgb ?? $"{Folder}/asset/{Stem}.sgb";
    public string MainModelPath => Sgb != null ? Sgb.Replace("/asset/", "/bgparts/").Replace(".sgb", ".mdl") : $"{Folder}/bgparts/{Stem}.mdl";
    public bool World => Sgb != null;
}

/// <summary>One model of a housing item, placed relative to the item.</summary>
public readonly record struct HousingPart(string Path, Vector3 Position, Quaternion Rotation, Vector3 Scale);

/// <summary>A housing item placed in the scene (only on your screen: plain render objects the server never hears of).</summary>
public sealed unsafe class SpawnedProp
{
    public Guid Id { get; } = Guid.NewGuid();
    public required HousingItem Item { get; init; }
    public Vector3 Position;
    public Vector3 Turn;   // degrees: yaw, pitch, roll
    public float Scale = 1f;
    public bool Visible = true;

    internal HousingPart[] Parts = [];
    internal BgObject*[] Objects = [];
    internal bool[] Ready = [];
    internal bool Dirty = true;
    internal bool AppliedVisible = true;
}

/// <summary>
/// The housing items you can place (Props tab): the catalogue from the game's data, the parts each one is made of,
/// and the placed ones. Placed items are render objects created on this client only; they're removed when you
/// leave GPose (unless Limber runs outside it), turn Limber off, or unload it.
/// </summary>
public sealed unsafe class HousingProps : IDisposable
{
    private readonly IDataManager _data;
    private readonly IPluginLog _log;

    private readonly List<SpawnedProp> _spawned = [];
    private readonly ConcurrentQueue<Action> _jobs = new(); // run on the framework thread
    private readonly ConcurrentDictionary<string, HousingPart[]> _partsCache = new();

    public HousingProps(IDataManager data, IPluginLog log)
    {
        _data = data;
        _log = log;
    }

    // ---- Catalogue

    private Task<HousingItem[]>? _catalog;

    public HousingItem[]? Catalog
    {
        get
        {
            _catalog ??= Task.Run(LoadCatalog);
            return _catalog.IsCompletedSuccessfully ? _catalog.Result : null;
        }
    }

    public bool CatalogFailed => _catalog?.IsFaulted == true;

    private HousingItem[] LoadCatalog()
    {
        var list = new List<HousingItem>(4000);
        var indoor = _data.GetExcelSheet<Lumina.Excel.Sheets.HousingFurniture>();
        foreach(var row in indoor)
        {
            var item = row.Item.ValueNullable;
            if(item == null || row.ModelKey == 0)
                continue;
            var name = item.Value.Name.ExtractText();
            if(!string.IsNullOrWhiteSpace(name))
                list.Add(new HousingItem(name, (ushort)row.ModelKey, true, item.Value.Icon, CategoryOf(item.Value, "Furnishings")));
        }
        var outdoor = _data.GetExcelSheet<Lumina.Excel.Sheets.HousingYardObject>();
        foreach(var row in outdoor)
        {
            var item = row.Item.ValueNullable;
            if(item == null || row.ModelKey == 0)
                continue;
            var name = item.Value.Name.ExtractText();
            if(!string.IsNullOrWhiteSpace(name))
                list.Add(new HousingItem(name, (ushort)row.ModelKey, false, item.Value.Icon, CategoryOf(item.Value, "Outdoor Furnishings")));
        }
        return list.GroupBy(i => (i.ModelKey, i.Indoors)).Select(g => g.First()).OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    // An item's section: its own category in the game (Table, Tabletop, Wall-mounted, Rug, Ceiling Light...).
    private static string CategoryOf(Lumina.Excel.Sheets.Item item, string fallback)
    {
        try
        {
            var name = item.ItemUICategory.ValueNullable?.Name.ExtractText();
            return string.IsNullOrWhiteSpace(name) ? fallback : name!;
        }
        catch
        {
            return fallback;
        }
    }

    // ---- World props (every shared group the game lists: event objects, field objects, each region's props)

    private Task<HousingItem[]>? _world;

    public HousingItem[]? WorldCatalog
    {
        get
        {
            _world ??= Task.Run(LoadWorld);
            return _world.IsCompletedSuccessfully ? _world.Result : null;
        }
    }

    public bool WorldFailed => _world?.IsFaulted == true;

    private static ulong Num(object? v)
    {
        switch(v)
        {
            case null: return 0;
            case byte b: return b;
            case ushort us: return us;
            case short sh: return (ushort)sh;
            case uint ui: return ui;
            case int i: return (uint)i;
            case ulong ul: return ul;
        }
        var rowId = v.GetType().GetProperty("RowId");
        return rowId != null && rowId.GetValue(v) is { } r ? Num(r) : 0;
    }

    private HousingItem[] LoadWorld()
    {
        var groups = _data.GetExcelSheet<Lumina.Excel.Sheets.ExportedSG>();
        // Names: the event objects that use a group (EObj -> its shared group; EObjName: what the game calls it).
        var names = new Dictionary<uint, string>();
        try
        {
            var eobj = _data.GetExcelSheet<Lumina.Excel.Sheets.EObj>();
            var eobjNames = _data.GetExcelSheet<Lumina.Excel.Sheets.EObjName>();
            var link = typeof(Lumina.Excel.Sheets.EObj).GetProperties().FirstOrDefault(p => p.PropertyType.ToString().Contains("ExportedSG"))
                ?? typeof(Lumina.Excel.Sheets.EObj).GetProperty("SgbPath");
            if(link != null)
            {
                var votes = new Dictionary<uint, Dictionary<string, int>>();
                foreach(var row in eobj)
                {
                    uint sg = (uint)Num(link.GetValue(row));
                    if(sg == 0 || !eobjNames.TryGetRow(row.RowId, out var n))
                        continue;
                    string name = n.Singular.ExtractText();
                    if(string.IsNullOrWhiteSpace(name))
                        continue;
                    if(!votes.TryGetValue(sg, out var v))
                        votes[sg] = v = new Dictionary<string, int>();
                    v[name] = v.TryGetValue(name, out var c) ? c + 1 : 1;
                }
                foreach(var (sg, v) in votes)
                    names[sg] = v.OrderByDescending(kv => kv.Value).First().Key;
            }
        }
        catch(Exception e)
        {
            _log.Debug(e, "Limber: couldn't read the world props' names");
        }

        var list = new List<HousingItem>(4600);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var row in groups)
        {
            string path = row.SgbPath.ExtractText();
            if(path.Length == 0 || !path.EndsWith(".sgb", StringComparison.OrdinalIgnoreCase) || path.Contains("/for_vfx/", StringComparison.OrdinalIgnoreCase)
               || !seen.Add(path))
                continue;
            string file = path[(path.LastIndexOf('/') + 1)..^4];
            string name = names.TryGetValue(row.RowId, out var nice) ? Capitalise(nice) + $"  ({file})" : Readable(file);
            list.Add(new HousingItem(name, 0, false, 0, WorldSection(path), path, KindOf(path, name)));
        }

        // Everything the zones themselves place: each zone's layout files list its models and prop groups.
        try
        {
            AddZoneProps(list, seen);
        }
        catch(Exception e)
        {
            _log.Debug(e, "Limber: couldn't read the zones' layouts");
        }
        return list.OrderBy(i => i.Category, StringComparer.Ordinal).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static readonly string[] LayoutFiles = ["bg.lgb", "planevent.lgb", "planlive.lgb", "planmap.lgb", "planner.lgb"];

    private void AddZoneProps(List<HousingItem> list, HashSet<string> seen)
    {
        var zones = _data.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>();
        if(zones == null)
            return;
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var zone in zones)
        {
            string bg = zone.Bg.ExtractText();
            int slash = bg.LastIndexOf('/');
            if(bg.Length == 0 || slash <= 0)
                continue;
            string folder = "bg/" + bg[..slash] + "/";
            if(!done.Add(folder))
                continue;
            string place = "";
            try
            {
                place = zone.PlaceName.ValueNullable?.Name.ExtractText() ?? "";
            }
            catch
            {
            }
            foreach(var name in LayoutFiles)
            {
                LgbFile? file;
                try
                {
                    file = _data.GetFile<LgbFile>(folder + name);
                }
                catch
                {
                    continue;
                }
                if(file == null)
                    continue;
                foreach(var layer in file.Layers)
                    foreach(var entry in layer.InstanceObjects)
                    {
                        string? path = entry.AssetType == LayerEntryType.BG && entry.Object is LayerCommon.BGInstanceObject b ? b.AssetPath
                            : entry.AssetType == LayerEntryType.SharedGroup && entry.Object is LayerCommon.SharedGroupInstanceObject g ? g.AssetPath
                            : null;
                        if(string.IsNullOrEmpty(path) || path.Contains("/for_vfx/", StringComparison.OrdinalIgnoreCase)
                           || path.Contains("/collision/", StringComparison.OrdinalIgnoreCase) || !seen.Add(path))
                            continue;
                        string file2 = path[(path.LastIndexOf('/') + 1)..];
                        file2 = file2[..(file2.LastIndexOf('.') is int dot and > 0 ? dot : file2.Length)];
                        string area = path.StartsWith("bgcommon/", StringComparison.OrdinalIgnoreCase) || place.Length == 0
                            ? WorldSection(path)
                            : $"{WorldSection(path).Split(':')[0]}: {place}";
                        string shown = Readable(file2);
                        list.Add(new HousingItem(shown, 0, false, 0, area, path, KindOf(path, shown)));
                    }
            }
        }
    }

    // What a prop is, from its file's name and folder: plain words a person would look for.
    private static readonly (string Kind, string[] Words)[] Kinds =
    [
        ("Trees and plants", ["tree", "tre", "bush", "grs", "grass", "flw", "flower", "plant", "leaf", "vine", "ivy", "moss", "palm", "bamboo", "fern", "kusa", "hana"]),
        ("Rocks and cliffs", ["rock", "rok", "stone", "ston", "cliff", "clf", "boulder", "iwa"]),
        ("Ground and terrain", ["trr", "terrain", "ground", "gnd", "soil", "sand", "snow", "dirt", "road", "path"]),
        ("Water features", ["fountain", "well", "water", "wtr", "pond", "fall", "spring"]),
        ("Bridges", ["bridge", "brid", "brg"]),
        ("Walls", ["wall", "wal", "rampart"]),
        ("Floors and stairs", ["floor", "flr", "stair", "step", "kaidan", "deck", "plat"]),
        ("Roofs and pillars", ["roof", "pillar", "pil", "column", "beam", "arch"]),
        ("Doors and gates", ["door", "dor", "gate", "gte", "portal"]),
        ("Windows", ["window", "win", "glass"]),
        ("Buildings", ["house", "hou", "build", "bld", "tower", "twr", "castle", "temple", "shrine", "hut", "inn", "shop", "fort"]),
        ("Fences and railings", ["fence", "fnc", "rail", "barrier", "palisade"]),
        ("Lights", ["lamp", "lmp", "lantern", "torch", "candle", "light", "lgt", "brazier", "chandelier"]),
        ("Furniture", ["table", "tbl", "chair", "chr", "bench", "bed", "shelf", "desk", "stool", "sofa", "cabinet", "counter", "throne"]),
        ("Barrels, crates and boxes", ["barrel", "bar", "taru", "crate", "crt", "box", "chest", "sack", "bag", "pot", "jar", "basket", "bucket"]),
        ("Banners, flags and signs", ["banner", "flag", "flg", "sign", "board", "poster", "curtain", "cloth"]),
        ("Statues and monuments", ["statue", "sta", "monument", "pillar", "obelisk", "idol", "bust", "grave", "tomb"]),
        ("Tents and stalls", ["tent", "tnt", "stall", "booth", "awning", "parasol"]),
        ("Ships, carts and airships", ["ship", "boat", "airship", "cart", "wagon", "carriage", "sail", "anchor", "dock"]),
        ("Machines and tools", ["machine", "gear", "pipe", "magitek", "mtk", "tool", "anvil", "forge", "crane", "wheel"]),
        ("Crystals and aetherytes", ["crystal", "cry", "aether", "aet", "shard"]),
        ("Weapons and armour props", ["sword", "spear", "shield", "weapon", "cannon", "armor", "armour"]),
        ("Food and dishes", ["food", "dish", "plate", "cup", "bottle", "bread", "fruit", "meat", "wine"]),
        ("Bones and remains", ["bone", "skull", "corpse", "remains", "fossil"]),
    ];

    private static string KindOf(string path, string name)
    {
        string file = path[(path.LastIndexOf('/') + 1)..].ToLowerInvariant();
        string lower = name.ToLowerInvariant() + " " + file;
        // whole-word-ish matches on the name's pieces first (so "bar" isn't found inside "barrier")
        var pieces = lower.Split([' ', '_', '-', '.', '(', ')'], StringSplitOptions.RemoveEmptyEntries);
        foreach(var (kind, words) in Kinds)
            foreach(var w in words)
                foreach(var piece in pieces)
                    if(piece == w || (w.Length >= 4 && piece.Contains(w)) || (piece.Length > w.Length && piece.StartsWith(w) && char.IsDigit(piece[w.Length])))
                        return kind;
        // then by the folder it's kept in
        if(path.Contains("/evt/", StringComparison.OrdinalIgnoreCase))
            return "Event objects";
        if(path.Contains("/hou/", StringComparison.OrdinalIgnoreCase))
            return "Housing pieces";
        if(path.Contains("/bgparts/", StringComparison.OrdinalIgnoreCase))
            return "Other models";
        return "Other prop groups";
    }

    private static string Capitalise(string s) => s.Length > 0 ? char.ToUpperInvariant(s[0]) + s[1..] : s;

    // A shared group's file name made easier to read (its usual prefixes off, words apart).
    private static string Readable(string file)
    {
        string s = file;
        foreach(var prefix in new[] { "sgbg_", "sgpl_a_", "sgpl_w_", "sgpl_", "sgvf_", "sg_" })
            if(s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                s = s[prefix.Length..];
                break;
            }
        return s.Replace('_', ' ') + $"  ({file})";
    }

    // The section a world prop goes under: common props by kind, the rest by region and expansion.
    private static string WorldSection(string path)
    {
        var p = path.Split('/');
        if(p.Length > 2 && p[0] == "bgcommon")
        {
            string kind = p.Length > 2 ? p[2] : "";
            if(p[1] == "mji")
                return "Common: Island Sanctuary";
            if(p[1] == "hou")
                return "Common: Housing";
            return "Common: " + (kind switch
            {
                "evt" => "Event objects",
                "btl" => "Battle",
                "lvd" => "Field objects",
                "cnt" => "Content",
                "tbx" => "Treasure and boxes",
                "pvp" => "PvP",
                "xma" or "hal" or "emp" or "mkd" => "Seasonal events",
                "aet" => "Aetherytes",
                "gld" => "Guilds",
                "gte" => "Gates",
                "sys" => "System",
                _ => kind.ToUpperInvariant(),
            });
        }
        if(p.Length > 2 && p[0] == "bg")
        {
            string ex = p[1], zone = p[2];
            string area = zone.Contains('_') ? zone[(zone.IndexOf('_') + 1)..] : zone;
            string code = area.Length >= 3 ? area[..3] : area;
            if(zone.Length > 3 && char.IsDigit(zone[0]) && zone.IndexOf('_') > 0)
            {
                code = zone[(zone.IndexOf('_') + 1)..];
                code = code.Length >= 3 ? code[..3] : code;
            }
            string region = code switch
            {
                "sea" => "La Noscea",
                "fst" => "The Black Shroud",
                "wil" => "Thanalan",
                "roc" => "Coerthas",
                "lak" => "Mor Dhona",
                "hou" => "Residential areas",
                "zon" => "Dungeons and trials",
                "pvp" => "PvP",
                "dra" => "Dravania",
                "abr" => "Abalathia's Spine",
                "gyr" => "Gyr Abania",
                "nvt" => "Norvrandt",
                "xkt" => "Xak Tural",
                "ykt" => "Yok Tural",
                _ => "area " + code.ToUpperInvariant(),
            };
            string expansion = ex switch
            {
                "ffxiv" => "A Realm Reborn",
                "ex1" => "Heavensward",
                "ex2" => "Stormblood",
                "ex3" => "Shadowbringers",
                "ex4" => "Endwalker",
                "ex5" => "Dawntrail",
                _ => ex,
            };
            return $"{expansion}: {region}";
        }
        return "Other";
    }

    // ---- The models an item is made of (from its shared group file; just the main model if that can't be read)

    public HousingPart[] PartsOf(HousingItem item)
        => _partsCache.GetOrAdd(item.GroupPath, _ =>
        {
            var parts = new List<HousingPart>();
            // (a world prop that's one model on its own, from a zone's layout)
            if(item.GroupPath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
            {
                parts.Add(new HousingPart(item.GroupPath, Vector3.Zero, Quaternion.Identity, Vector3.One));
                return parts.ToArray();
            }
            try
            {
                CollectParts(item.GroupPath, Vector3.Zero, Quaternion.Identity, Vector3.One, parts, 0);
            }
            catch(Exception e)
            {
                _log.Debug(e, $"Limber: couldn't read {item.GroupPath}");
            }
            if(parts.Count == 0 && _data.FileExists(item.MainModelPath))
                parts.Add(new HousingPart(item.MainModelPath, Vector3.Zero, Quaternion.Identity, Vector3.One));
            return parts.ToArray();
        });

    private void CollectParts(string sgbPath, Vector3 position, Quaternion rotation, Vector3 scale, List<HousingPart> parts, int depth)
    {
        if(depth > 3 || parts.Count > 64)
            return;
        var file = _data.GetFile<SgbFile>(sgbPath);
        if(file == null)
            return;
        foreach(var group in file.LayerGroups)
        {
            foreach(var layer in group.Layers)
            {
                foreach(var entry in layer.InstanceObjects)
                {
                    var t = entry.Transform;
                    var localPosition = new Vector3(t.Translation.X, t.Translation.Y, t.Translation.Z);
                    var localRotation = Quaternion.CreateFromYawPitchRoll(t.Rotation.Y, t.Rotation.X, t.Rotation.Z);
                    var localScale = new Vector3(t.Scale.X, t.Scale.Y, t.Scale.Z);
                    if(localScale == Vector3.Zero)
                        localScale = Vector3.One;
                    var worldPosition = position + Vector3.Transform(localPosition * scale, rotation);
                    var worldRotation = Quaternion.Normalize(rotation * localRotation);
                    var worldScale = scale * localScale;

                    if(entry.AssetType == LayerEntryType.BG && entry.Object is LayerCommon.BGInstanceObject bg && !string.IsNullOrEmpty(bg.AssetPath))
                        parts.Add(new HousingPart(bg.AssetPath, worldPosition, worldRotation, worldScale));
                    else if(entry.AssetType == LayerEntryType.SharedGroup && entry.Object is LayerCommon.SharedGroupInstanceObject sg && !string.IsNullOrEmpty(sg.AssetPath))
                        CollectParts(sg.AssetPath, worldPosition, worldRotation, worldScale, parts, depth + 1);
                }
            }
        }
    }

    // ---- Placed items (the list is only changed on the framework thread; the UI reads a copy)

    public IReadOnlyList<SpawnedProp> Spawned
    {
        get
        {
            lock(_spawned)
                return _spawned.ToArray();
        }
    }

    public void Spawn(HousingItem item, Vector3 position, float yawDegrees)
        => _jobs.Enqueue(() =>
        {
            var prop = new SpawnedProp { Item = item, Position = position, Turn = new Vector3(yawDegrees, 0f, 0f) };
            prop.Parts = PartsOf(item);
            prop.Objects = new BgObject*[prop.Parts.Length];
            prop.Ready = new bool[prop.Parts.Length];
            for(int i = 0; i < prop.Parts.Length; i++)
            {
                try
                {
                    prop.Objects[i] = BgObject.Create(prop.Parts[i].Path, string.Empty);
                }
                catch(Exception e)
                {
                    _log.Warning(e, $"Limber: couldn't create {prop.Parts[i].Path}");
                }
            }
            lock(_spawned)
                _spawned.Add(prop);
        });

    public void Remove(Guid id)
        => _jobs.Enqueue(() =>
        {
            SpawnedProp? prop;
            lock(_spawned)
            {
                prop = _spawned.Find(p => p.Id == id);
                if(prop != null)
                    _spawned.Remove(prop);
            }
            if(prop != null)
                Destroy(prop);
        });

    public void RemoveAll()
        => _jobs.Enqueue(DestroyAllNow);

    /// <summary>Changed in the UI: place it again on the next update.</summary>
    public static void MarkChanged(SpawnedProp prop) => prop.Dirty = true;

    /// <summary>Framework thread: create / place / remove.</summary>
    public void Update(bool allowed)
    {
        if(!allowed)
        {
            bool any;
            lock(_spawned)
                any = _spawned.Count > 0;
            if(any)
                DestroyAllNow();
            _jobs.Clear();
            return;
        }

        while(_jobs.TryDequeue(out var job))
        {
            try { job(); }
            catch(Exception e) { _log.Warning(e, "Limber: prop job failed"); }
        }

        SpawnedProp[] props;
        lock(_spawned)
            props = _spawned.ToArray();
        foreach(var prop in props)
        {
            for(int i = 0; i < prop.Objects.Length; i++)
            {
                var bg = prop.Objects[i];
                if(bg == null || prop.Ready[i])
                    continue;
                var handle = bg->ModelResourceHandle;
                if(handle != null && ((ResourceHandle*)handle)->LoadState == 7)
                {
                    prop.Ready[i] = true;
                    prop.Dirty = true;
                }
            }
            if(prop.Dirty)
                Place(prop);
        }
    }

    private static void Place(SpawnedProp prop)
    {
        prop.Dirty = false;
        var t = prop.Turn * (MathF.PI / 180f);
        var rotation = Quaternion.CreateFromYawPitchRoll(t.X, t.Y, t.Z);
        float scale = Math.Clamp(prop.Scale, 0.01f, 100f);
        for(int i = 0; i < prop.Objects.Length; i++)
        {
            var bg = prop.Objects[i];
            if(bg == null || !prop.Ready[i])
                continue;
            var part = prop.Parts[i];
            var obj = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object*)bg;
            obj->Position = prop.Position + Vector3.Transform(part.Position * scale, rotation);
            obj->Rotation = Quaternion.Normalize(rotation * part.Rotation);
            obj->Scale = part.Scale * scale;
            var draw = (DrawObject*)bg;
            draw->SetTransparency(prop.Visible ? 0f : 1f);
            draw->UpdateTransforms(false);
            draw->UpdateCulling();
        }
    }

    private void DestroyAllNow()
    {
        SpawnedProp[] props;
        lock(_spawned)
        {
            props = _spawned.ToArray();
            _spawned.Clear();
        }
        foreach(var prop in props)
            Destroy(prop);
    }

    private void Destroy(SpawnedProp prop)
    {
        for(int i = 0; i < prop.Objects.Length; i++)
        {
            var bg = prop.Objects[i];
            prop.Objects[i] = null;
            if(bg == null)
                continue;
            try
            {
                var obj = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object*)bg;
                obj->CleanupRender();
                obj->Dtor(1);
            }
            catch(Exception e)
            {
                _log.Warning(e, "Limber: couldn't remove a prop");
            }
        }
    }

    // ---- Preview mesh (any thread): the item's triangles in its own space

    public sealed class PreviewMesh
    {
        public required Vector3[] Points;
        public required int[] Triangles;
        public Vector3 Min, Max;
    }

    private readonly ConcurrentDictionary<string, Task<PreviewMesh?>> _previews = new();

    public PreviewMesh? Preview(HousingItem item, out bool loading)
    {
        var task = _previews.GetOrAdd(item.GroupPath, _ => Task.Run(() => BuildPreview(item)));
        loading = !task.IsCompleted;
        return task.IsCompletedSuccessfully ? task.Result : null;
    }

    private PreviewMesh? BuildPreview(HousingItem item)
    {
        var points = new List<Vector3>();
        var triangles = new List<int>();
        foreach(var part in PartsOf(item))
        {
            Lumina.Models.Models.Model model;
            try
            {
                model = new Lumina.Models.Models.Model(_data.GameData, part.Path);
            }
            catch(Exception)
            {
                continue;
            }
            foreach(var mesh in model.Meshes)
            {
                int start = points.Count;
                foreach(var v in mesh.Vertices)
                {
                    var p = v.Position ?? Vector4.Zero;
                    points.Add(part.Position + Vector3.Transform(new Vector3(p.X, p.Y, p.Z) * part.Scale, part.Rotation));
                }
                for(int i = 0; i + 2 < mesh.Indices.Length; i += 3)
                {
                    triangles.Add(start + mesh.Indices[i]);
                    triangles.Add(start + mesh.Indices[i + 1]);
                    triangles.Add(start + mesh.Indices[i + 2]);
                }
                if(triangles.Count > 3 * 40000)
                    break;
            }
        }
        if(points.Count == 0)
            return null;
        // Keep the preview light: at most ~12k triangles.
        int count = triangles.Count / 3;
        int[] tris;
        if(count > 12000)
        {
            int step = (int)Math.Ceiling(count / 12000.0);
            var kept = new List<int>(12000 * 3);
            for(int t = 0; t < count; t += step)
            {
                kept.Add(triangles[t * 3]);
                kept.Add(triangles[t * 3 + 1]);
                kept.Add(triangles[t * 3 + 2]);
            }
            tris = kept.ToArray();
        }
        else
        {
            tris = triangles.ToArray();
        }
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach(var p in points)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        return new PreviewMesh { Points = points.ToArray(), Triangles = tris, Min = min, Max = max };
    }

    public void Dispose()
    {
        try { DestroyAllNow(); }
        catch { }
    }
}
