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
public sealed record HousingItem(string Name, ushort ModelKey, bool Indoors, uint Icon)
{
    private string Folder => Indoors ? $"bgcommon/hou/indoor/general/{ModelKey:D4}" : $"bgcommon/hou/outdoor/general/{ModelKey:D4}";
    private string Stem => Indoors ? $"fun_b0_m{ModelKey:D4}" : $"gar_b0_m{ModelKey:D4}";
    public string GroupPath => $"{Folder}/asset/{Stem}.sgb";
    public string MainModelPath => $"{Folder}/bgparts/{Stem}.mdl";
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
                list.Add(new HousingItem(name, (ushort)row.ModelKey, true, item.Value.Icon));
        }
        var outdoor = _data.GetExcelSheet<Lumina.Excel.Sheets.HousingYardObject>();
        foreach(var row in outdoor)
        {
            var item = row.Item.ValueNullable;
            if(item == null || row.ModelKey == 0)
                continue;
            var name = item.Value.Name.ExtractText();
            if(!string.IsNullOrWhiteSpace(name))
                list.Add(new HousingItem(name, (ushort)row.ModelKey, false, item.Value.Icon));
        }
        return list.GroupBy(i => (i.ModelKey, i.Indoors)).Select(g => g.First()).OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    // ---- The models an item is made of (from its shared group file; just the main model if that can't be read)

    public HousingPart[] PartsOf(HousingItem item)
        => _partsCache.GetOrAdd(item.GroupPath, _ =>
        {
            var parts = new List<HousingPart>();
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
