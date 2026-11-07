using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Limber.BodyRig;

namespace Limber.Koi.Props;

/// <summary>One Koikatsu studio item: where it's listed and where its model is.</summary>
public sealed record KoiItem(int Id, string Name, string Pack, string Category, string Zip, string Bundle, string Asset)
{
    /// <summary>A name for its files (letters, digits and _ only).</summary>
    public string Key => Safe($"{Pack}_{Id}");

    internal static string Safe(string s)
    {
        var b = new StringBuilder();
        foreach(char c in s.ToLowerInvariant())
            b.Append(c is (>= 'a' and <= 'z') or (>= '0' and <= '9') ? c : '_');
        var r = b.ToString().Trim('_');
        while(r.Contains("__"))
            r = r.Replace("__", "_");
        return r.Length > 48 ? r[..48] : r;
    }
}

/// <summary>A converted prop: its name, section and model (a game path the Penumbra mod provides).</summary>
public sealed class KoiImported
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Pack { get; set; } = "";
    public string Category { get; set; } = "";
    public string Model { get; set; } = "";
    public Dictionary<string, string> Files { get; set; } = new(); // game path -> file on disk
}

/// <summary>
/// Koikatsu > Import props: Koikatsu's studio items (from its sideloader mods) listed, and the chosen ones turned
/// into game models (solid, still models: the prefab's meshes as they rest, with their main textures and colours) in
/// a Penumbra mod, "Limber Koikatsu Props". The Props page then places them like housing items.
/// </summary>
public static class KoiProps
{
    public const string ModName = "Limber Koikatsu Props";
    private const string Root = "bgcommon/limber/koi";
    private const string TempTag = "Limber Koikatsu props";

    static KoiProps()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
        catch
        {
        }
    }

    // ---- The list

    public static volatile IReadOnlyList<KoiItem>? Items;
    public static volatile string ScanStatus = "";
    public static volatile bool Scanning;

    private static string CachePath => Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "koikatsu_items.json");
    private static string ImportedPath => Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "koikatsu_props.json");

    private sealed class Cache
    {
        public Dictionary<string, long> Stamps { get; set; } = new();
        public List<KoiItem> Items { get; set; } = new();
    }

    /// <summary>Reads the studio item lists of every sideloader mod under the game's mods folder (in the background).</summary>
    public static void Scan(string koikatsuFolder)
    {
        if(Scanning)
            return;
        Scanning = true;
        Task.Run(() =>
        {
            try
            {
                Items = ScanNow(koikatsuFolder);
            }
            catch(Exception e)
            {
                ScanStatus = "Couldn't read the folder: " + e.Message;
            }
            finally
            {
                Scanning = false;
            }
        });
    }

    /// <summary>The list as last scanned (from the saved copy), without scanning again.</summary>
    public static void LoadCached()
    {
        if(Items != null)
            return;
        try
        {
            if(File.Exists(CachePath))
                Items = JsonSerializer.Deserialize<Cache>(File.ReadAllText(CachePath))?.Items;
        }
        catch
        {
        }
    }

    private static List<KoiItem> ScanNow(string folder)
    {
        var mods = Path.Combine(folder, "mods");
        if(!Directory.Exists(mods))
            throw new DirectoryNotFoundException("there's no \"mods\" folder there (choose Koikatsu's own folder, the one with Koikatu.exe)");
        Cache old = new();
        try
        {
            if(File.Exists(CachePath))
                old = JsonSerializer.Deserialize<Cache>(File.ReadAllText(CachePath)) ?? new();
        }
        catch
        {
        }
        var byZip = old.Items.GroupBy(i => i.Zip).ToDictionary(g => g.Key, g => g.ToList());
        var fresh = new Cache();
        var zips = Directory.EnumerateFiles(mods, "*.zipmod", SearchOption.AllDirectories).ToList();
        int done = 0, withItems = 0;
        foreach(var zip in zips)
        {
            done++;
            if(done % 50 == 0)
                ScanStatus = $"Reading the mods: {done} of {zips.Count}...";
            long stamp;
            try
            {
                stamp = File.GetLastWriteTimeUtc(zip).Ticks ^ new FileInfo(zip).Length;
            }
            catch
            {
                continue;
            }
            fresh.Stamps[zip] = stamp;
            if(old.Stamps.TryGetValue(zip, out var was) && was == stamp)
            {
                if(byZip.TryGetValue(zip, out var known))
                {
                    fresh.Items.AddRange(known);
                    withItems++;
                }
                continue;
            }
            try
            {
                var found = ReadZip(zip);
                if(found.Count > 0)
                    withItems++;
                fresh.Items.AddRange(found);
            }
            catch
            {
                // (not a zip, or damaged: left out)
            }
        }
        try
        {
            File.WriteAllText(CachePath, JsonSerializer.Serialize(fresh));
        }
        catch
        {
        }
        ScanStatus = $"{fresh.Items.Count} studio items in {withItems} mods.";
        return fresh.Items;
    }

    private static string Text(byte[] b)
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(b);
        }
        catch
        {
            try
            {
                return Encoding.GetEncoding(932).GetString(b);
            }
            catch
            {
                return Encoding.UTF8.GetString(b);
            }
        }
    }

    private static List<KoiItem> ReadZip(string zip)
    {
        var list = new List<KoiItem>();
        using var z = ZipFile.OpenRead(zip);
        string pack = Path.GetFileNameWithoutExtension(zip);
        var categories = new Dictionary<int, string>();
        var lists = new List<ZipArchiveEntry>();
        foreach(var e in z.Entries)
        {
            var name = e.FullName.Replace('\\', '/');
            if(!name.Contains("studio/info/", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                continue;
            var file = Path.GetFileName(name);
            if(file.StartsWith("ItemList", StringComparison.OrdinalIgnoreCase))
                lists.Add(e);
            else if(file.StartsWith("ItemCategory", StringComparison.OrdinalIgnoreCase))
                foreach(var line in Lines(e))
                {
                    var f = line.Split(',');
                    if(f.Length >= 2 && int.TryParse(f[0].Trim(), out var id))
                        categories[id] = f[1].Trim();
                }
        }
        foreach(var e in lists)
            foreach(var line in Lines(e))
            {
                var f = line.Split(',');
                if(f.Length < 7 || !int.TryParse(f[0].Trim(), out var id))
                    continue;
                int.TryParse(f[2].Trim(), out var category);
                string bundle = f[5].Trim(), asset = f[6].Trim();
                if(bundle.Length == 0 || asset.Length == 0)
                    continue;
                list.Add(new KoiItem(id, f[3].Trim(), pack, categories.TryGetValue(category, out var c) ? c : "", zip, bundle, asset));
            }
        return list;
    }

    private static IEnumerable<string> Lines(ZipArchiveEntry e)
    {
        using var s = e.Open();
        using var m = new MemoryStream();
        s.CopyTo(m);
        return Text(m.ToArray()).Split('\n').Select(l => l.TrimEnd('\r'));
    }

    // ---- The props made so far

    public static List<KoiImported> Imported { get; private set; } = new();

    public static void LoadImported()
    {
        try
        {
            if(File.Exists(ImportedPath))
                Imported = JsonSerializer.Deserialize<List<KoiImported>>(File.ReadAllText(ImportedPath)) ?? new();
        }
        catch
        {
            Imported = new();
        }
        Imported.RemoveAll(p => p.Files.Values.Any(f => !File.Exists(f)));
    }

    private static void SaveImported()
    {
        try
        {
            File.WriteAllText(ImportedPath, JsonSerializer.Serialize(Imported, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
        }
    }

    private static HousingItem[]? _asItems;
    private static bool _registered;
    private static long _triedAt;

    /// <summary>The props as items for the Props page (and the mod manager told about them, if it wasn't yet).</summary>
    public static HousingItem[] AsHousingItems()
    {
        if(!_registered && Environment.TickCount64 - _triedAt > 10000)
        {
            _triedAt = Environment.TickCount64;
            Register();
        }
        return _asItems ??= Imported.Select(p => new HousingItem(p.Name, 0, false, 0, p.Pack, p.Model, p.Category.Length > 0 ? p.Category : "Koikatsu")).ToArray();
    }

    /// <summary>Takes the props' files back from the mod manager (Limber unloading).</summary>
    public static void Unregister()
    {
        try
        {
            Plugin.PluginInterface.GetIpcSubscriber<string, int, int>("Penumbra.RemoveTemporaryModAll.V5").InvokeFunc(TempTag, 90);
        }
        catch
        {
        }
    }

    /// <summary>Has the mod manager use the props' files right away, in every collection (also without the mod turned on).</summary>
    public static void Register()
    {
        try
        {
            var map = new Dictionary<string, string>();
            foreach(var p in Imported)
                foreach(var (game, disk) in p.Files)
                    if(File.Exists(disk))
                        map[game] = disk;
            Plugin.PluginInterface.GetIpcSubscriber<string, int, int>("Penumbra.RemoveTemporaryModAll.V5").InvokeFunc(TempTag, 90);
            if(map.Count > 0)
                Plugin.PluginInterface.GetIpcSubscriber<string, Dictionary<string, string>, string, int, int>("Penumbra.AddTemporaryModAll.V5")
                    .InvokeFunc(TempTag, map, "", 90);
            _registered = true;
        }
        catch(Exception e)
        {
            Plugin.Log.Information($"Limber: couldn't hand the Koikatsu props to the mod manager ({e.Message})");
        }
    }

    // ---- Converting

    /// <summary>Converts these items into the mod (in the background). Progress is reported as it goes.</summary>
    public static Task Import(IReadOnlyList<KoiItem> items, int maxTexture, OneClickRig.Progress progress)
        => Task.Run(async () =>
        {
            try
            {
                await ImportNow(items, maxTexture, progress);
            }
            catch(Exception e)
            {
                progress.Failed = true;
                progress.Add("Stopped: " + e.Message);
            }
            finally
            {
                progress.Done = true;
            }
        });

    private static async Task ImportNow(IReadOnlyList<KoiItem> items, int maxTexture, OneClickRig.Progress progress)
    {
        var root = await OneClickRig.Game(() => Plugin.PluginInterface.GetIpcSubscriber<string>("Penumbra.GetModDirectory").InvokeFunc());
        if(string.IsNullOrEmpty(root) || !Directory.Exists(root))
            throw new InvalidOperationException("couldn't find the mod folder (is the mod manager running?)");
        progress.Add("Finding a game model to make the props like...");
        var template = await OneClickRig.Game(FindTemplate);
        if(template == null)
            throw new InvalidOperationException("couldn't find a suitable game model to copy the layout of");
        var (mdl, mtrl, slots) = template.Value;
        progress.Add($"  {Path.GetFileName(mdl.Path)} ({string.Join(", ", slots)}).");

        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        string flatNormal = $"{Root}/common/texture/koi_flat_n.tex", plainSpec = $"{Root}/common/texture/koi_plain_s.tex";
        files[flatNormal] = TexWriter.Solid(128, 128, 255, 255);
        files[plainSpec] = TexWriter.Solid(40, 40, 40, 255);
        var made = new List<KoiImported>();
        foreach(var item in items)
        {
            try
            {
                var one = Convert(item, mdl, mtrl, slots, flatNormal, plainSpec, maxTexture, files, progress);
                if(one != null)
                    made.Add(one);
            }
            catch(Exception e)
            {
                progress.Add($"  {item.Name}: couldn't be converted ({e.Message}).");
            }
        }
        if(made.Count == 0)
        {
            progress.Failed = true;
            progress.Add("Nothing could be converted.");
            return;
        }
        await OneClickRig.PutMod(root, ModName, files,
            "Koikatsu studio items converted by Limber into solid game models, to place from Limber's Props page. Import more from Limber's Koikatsu page.",
            progress, keepEarlier: true);
        var dir = Path.Combine(root, ModName);
        foreach(var m in made)
        {
            m.Files = m.Files.Keys.ToDictionary(g => g, g => Path.Combine(dir, g.Replace('/', Path.DirectorySeparatorChar)));
            // (the shared textures too)
            m.Files[flatNormal] = Path.Combine(dir, flatNormal.Replace('/', Path.DirectorySeparatorChar));
            m.Files[plainSpec] = Path.Combine(dir, plainSpec.Replace('/', Path.DirectorySeparatorChar));
        }
        // (the list is changed on the framework thread, where the pages read it)
        await OneClickRig.Game(() =>
        {
            var list = new List<KoiImported>(Imported);
            foreach(var m in made)
            {
                list.RemoveAll(x => x.Key == m.Key);
                list.Add(m);
            }
            Imported = list;
            _asItems = null;
            SaveImported();
            Register();
            return true;
        });
        progress.Add($"Done: {made.Count} prop(s). Place them from Koikatsu > Props.");
    }

    // A furnishing whose model has the layout props need, and whose material has just a colour, normal and (maybe)
    // specular map: the props copy its layout and material settings.
    private static (MdlTemplate Mdl, byte[] Mtrl, string[] Slots)? FindTemplate()
    {
        for(int k = 1; k < 600; k++)
        {
            string path = $"bgcommon/hou/indoor/general/{k:D4}/bgparts/fun_b0_m{k:D4}.mdl";
            try
            {
                var data = Plugin.DataManager.GetFile(path)?.Data;
                if(data == null)
                    continue;
                var t = MdlTemplate.Read(path, data);
                if(t == null || !t.Material.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase))
                    continue;
                var mtrl = Plugin.DataManager.GetFile(t.Material)?.Data;
                if(mtrl == null || MtrlBuilder.Shader(mtrl) != "bg.shpk")
                    continue;
                var textures = MtrlBuilder.Textures(mtrl);
                var slots = textures.Select(x => x.EndsWith("_d.tex", StringComparison.OrdinalIgnoreCase) ? "d"
                    : x.EndsWith("_n.tex", StringComparison.OrdinalIgnoreCase) ? "n" : x.EndsWith("_s.tex", StringComparison.OrdinalIgnoreCase) ? "s" : "?").ToArray();
                if(slots.Count(s => s == "d") != 1 || slots.Count(s => s == "n") != 1 || slots.Contains("?") || slots.Length > 3)
                    continue;
                return (t, mtrl, slots);
            }
            catch
            {
            }
        }
        return null;
    }

    private sealed class Bundle
    {
        public readonly List<SerializedFile> Files = new();
        public readonly Dictionary<string, byte[]> Resources = new(StringComparer.OrdinalIgnoreCase);
    }

    private static Bundle Load(ZipArchive z, string bundlePath)
    {
        string want = ("abdata/" + bundlePath.Replace('\\', '/').TrimStart('/')).ToLowerInvariant();
        var entry = z.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/').Equals(want, StringComparison.OrdinalIgnoreCase))
            ?? z.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/').EndsWith(bundlePath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
        if(entry == null)
            throw new FileNotFoundException($"{bundlePath} isn't in the mod");
        using var s = entry.Open();
        using var m = new MemoryStream();
        s.CopyTo(m);
        var b = new Bundle();
        foreach(var (name, data) in UnityFs.Open(m.ToArray()))
        {
            if(name.EndsWith(".resS", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".resource", StringComparison.OrdinalIgnoreCase))
                b.Resources[name] = data;
            else
                b.Files.Add(new SerializedFile(data));
        }
        return b;
    }

    private static KoiImported? Convert(KoiItem item, MdlTemplate mdl, byte[] mtrl, string[] slots, string flatNormal, string plainSpec, int maxTexture,
        Dictionary<string, byte[]> files, OneClickRig.Progress progress)
    {
        using var z = ZipFile.OpenRead(item.Zip);
        var bundle = Load(z, item.Bundle);
        // The prefab: by its name in the bundle's list of assets.
        SerializedFile? sf = null;
        long rootId = 0;
        foreach(var f in bundle.Files)
            foreach(var (id, e) in f.Objects)
                if(e.ClassId == 142)
                {
                    var ab = f.Read(id);
                    foreach(var pair in ab.L("m_Container"))
                        if(pair is KeyValuePair<object?, object?> kv && kv.Key is string name
                           && (name.Equals(item.Asset, StringComparison.OrdinalIgnoreCase)
                               || Path.GetFileNameWithoutExtension(name).Equals(item.Asset, StringComparison.OrdinalIgnoreCase)))
                        {
                            var (fileId, pathId) = kv.Value.Ptr("asset");
                            if(fileId == 0 && f.Objects.TryGetValue(pathId, out var target) && target.ClassId == 1)
                            {
                                sf = f;
                                rootId = pathId;
                            }
                        }
                }
        if(sf == null)
            throw new InvalidDataException($"its model ({item.Asset}) isn't in its bundle");

        // Every mesh under it, as it rests, in the prefab's own space.
        static long Id(object? pptr) => ((Dictionary<string, object?>?)pptr)?.I("m_PathID") ?? 0;
        var parts = new List<(Dictionary<string, object?> Mesh, long[] Bones, Matrix4x4 World, List<long> Materials, bool Skinned)>();
        var worlds = new Dictionary<long, Matrix4x4>(); // transform -> its world matrix (relative to the prefab)
        void Walk(long go, Matrix4x4 parent, bool isRoot)
        {
            var g = sf!.Read(go);
            if(g == null || (g.Get("m_IsActive") is bool active && !active))
                return;
            long transform = 0;
            var comps = new List<(int Class, long Id)>();
            foreach(var c in g.L("m_Component"))
            {
                var (fid, pid) = c.Ptr("component");
                if(fid != 0 || !sf.Objects.TryGetValue(pid, out var ce))
                    continue;
                comps.Add((ce.ClassId, pid));
                if(ce.ClassId is 4 or 224)
                    transform = pid;
            }
            var t = transform != 0 ? sf.Read(transform) : null;
            var local = isRoot || t == null ? Matrix4x4.Identity
                : Matrix4x4.CreateScale(t.V3("m_LocalScale")) * Matrix4x4.CreateFromQuaternion(t.Q("m_LocalRotation")) * Matrix4x4.CreateTranslation(t.V3("m_LocalPosition"));
            var world = local * parent;
            if(transform != 0)
                worlds[transform] = world;
            foreach(var (cls, id) in comps)
            {
                if(cls == 23) // mesh renderer: its mesh is the mesh filter's
                {
                    var r = sf.Read(id);
                    if(r == null || (r.Get("m_Enabled") is bool en && !en))
                        continue;
                    var filter = comps.FirstOrDefault(x => x.Class == 33);
                    if(filter.Id == 0)
                        continue;
                    var (mf, mp) = sf.Read(filter.Id).Ptr("m_Mesh");
                    if(mf == 0 && sf.Read(mp) is { } mesh)
                        parts.Add((mesh, [], world, r.L("m_Materials").Select(Id).ToList(), false));
                }
                else if(cls == 137) // skinned mesh renderer
                {
                    var r = sf.Read(id);
                    if(r == null || (r.Get("m_Enabled") is bool en && !en))
                        continue;
                    var (mf, mp) = r.Ptr("m_Mesh");
                    if(mf != 0 || sf.Read(mp) is not { } mesh)
                        continue;
                    parts.Add((mesh, r.L("m_Bones").Select(Id).ToArray(), world, r.L("m_Materials").Select(Id).ToList(), true));
                }
            }
            if(t != null)
                foreach(var child in t.L("m_Children"))
                {
                    long ct = Id(child);
                    var cto = sf.Read(ct);
                    var (gf, gp) = cto.Ptr("m_GameObject");
                    if(gf == 0 && gp != 0)
                        Walk(gp, world, false);
                }
        }
        Walk(rootId, Matrix4x4.Identity, true);
        if(parts.Count == 0)
            throw new InvalidDataException("it has no meshes");

        // Meshes, one per material.
        var byMaterial = new Dictionary<long, PropMesh>();
        var order = new List<long>();
        int skipped = 0;
        foreach(var (mesh, bonesRaw, world, materials, skinned) in parts)
        {
            if(mesh.I("m_MeshCompression") != 0)
            {
                skipped++;
                continue;
            }
            var data = MeshData.Read(mesh);
            if(data == null)
            {
                skipped++;
                continue;
            }
            // Where each vertex rests: skinned ones through their bones as they rest in the prefab, the rest by their
            // object's place.
            var final = new Vector3[data.P.Length];
            var normals = new Vector3[data.P.Length];
            var tangents = new Vector4[data.P.Length];
            Matrix4x4[]? skin = null;
            if(skinned && bonesRaw.Length > 0 && data.Weights != null)
            {
                var binds = mesh.L("m_BindPose").Select(Unity).ToArray();
                skin = new Matrix4x4[bonesRaw.Length];
                for(int b = 0; b < bonesRaw.Length; b++)
                {
                    long boneId = bonesRaw[b];
                    var bw = worlds.TryGetValue(boneId, out var m) ? m : world;
                    skin[b] = (b < binds.Length ? binds[b] : Matrix4x4.Identity) * bw;
                }
            }
            for(int v = 0; v < data.P.Length; v++)
            {
                Matrix4x4 m = world;
                if(skin != null && data.Weights != null)
                {
                    var (idx, w) = data.Weights[v];
                    var acc = new Matrix4x4();
                    float total = 0f;
                    for(int k = 0; k < 4; k++)
                        if(w[k] > 0f && idx[k] < skin.Length)
                        {
                            acc += skin[idx[k]] * w[k];
                            total += w[k];
                        }
                    m = total > 1e-5f ? acc * (1f / total) : world;
                }
                final[v] = Vector3.Transform(data.P[v], m);
                normals[v] = Vector3.Normalize(Vector3.TransformNormal(data.N.Length > v ? data.N[v] : Vector3.UnitY, m));
                var tg = data.T.Length > v ? data.T[v] : new Vector4(1, 0, 0, 1);
                var t3 = Vector3.TransformNormal(new Vector3(tg.X, tg.Y, tg.Z), m);
                tangents[v] = new Vector4(t3.LengthSquared() > 1e-12f ? Vector3.Normalize(t3) : Vector3.UnitX, tg.W);
            }
            for(int s = 0; s < data.Submeshes.Count; s++)
            {
                long material = materials.Count == 0 ? 0 : materials[Math.Min(s, materials.Count - 1)];
                if(!byMaterial.TryGetValue(material, out var pm))
                {
                    byMaterial[material] = pm = new PropMesh { Material = order.Count };
                    order.Add(material);
                }
                var map = new Dictionary<int, int>();
                var (first, count) = data.Submeshes[s];
                for(int i = first; i + 2 < first + count && i + 2 < data.Indices.Length; i += 3)
                {
                    // (Unity's left-handed space into the game's: x mirrored, so the triangles are turned round)
                    int[] tri = [data.Indices[i], data.Indices[i + 2], data.Indices[i + 1]];
                    foreach(var src in tri)
                    {
                        if(src >= final.Length)
                            goto NextTriangle;
                    }
                    foreach(var src in tri)
                    {
                        if(!map.TryGetValue(src, out var dst))
                        {
                            dst = pm.P.Count;
                            map[src] = dst;
                            var p = final[src];
                            var n = normals[src];
                            var tg = tangents[src];
                            pm.P.Add(new Vector3(-p.X, p.Y, p.Z));
                            pm.N.Add(new Vector3(-n.X, n.Y, n.Z));
                            pm.T.Add(new Vector4(-tg.X, tg.Y, tg.Z, -tg.W));
                            pm.UV.Add(data.UV.Length > src ? data.UV[src] : Vector2.Zero);
                        }
                        pm.I.Add(dst);
                    }
                NextTriangle:;
                }
            }
        }
        if(byMaterial.Count == 0)
            throw new InvalidDataException(skipped > 0 ? "its meshes are stored compressed (not supported yet)" : "it has no meshes");

        // Files: the model, a material and a colour texture per Koikatsu material.
        string key = item.Key;
        string folder = $"{Root}/{key}";
        string modelPath = $"{folder}/bgparts/koi_{key}.mdl";
        var materialPaths = new List<string>();
        for(int m = 0; m < order.Count; m++)
        {
            string tex = $"{folder}/texture/koi_{key}_{m}_d.tex";
            string mat = $"{folder}/material/koi_{key}_{m}.mtrl";
            var (rgba, w, h) = Diffuse(sf, bundle, order[m], maxTexture);
            files[tex] = TexWriter.Bgra(rgba, w, h);
            var textures = slots.Select(s => s == "d" ? tex : s == "n" ? flatNormal : plainSpec).ToList();
            files[mat] = MtrlBuilder.With(mtrl, textures);
            materialPaths.Add(mat);
        }
        files[modelPath] = MdlBuilder.Build(mdl, order.Select(o => byMaterial[o]).ToList(), materialPaths);
        int vertices = byMaterial.Values.Sum(p => p.P.Count), triangles = byMaterial.Values.Sum(p => p.I.Count / 3);
        progress.Add($"  {item.Name}: {triangles} triangles, {vertices} vertices, {order.Count} material(s){(skipped > 0 ? $" ({skipped} compressed mesh(es) left out)" : "")}.");
        var made = new KoiImported { Key = key, Name = item.Name.Length > 0 ? item.Name : item.Asset, Pack = item.Pack, Category = item.Category, Model = modelPath };
        foreach(var g in files.Keys.Where(g => g.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase)))
            made.Files[g] = "";
        return made;
    }

    private static Matrix4x4 Unity(object? o)
    {
        // (Unity keeps them column by column for column vectors; System.Numerics wants row vectors: transposed)
        var d = o as Dictionary<string, object?>;
        float E(int r, int c) => d.F($"e{r}{c}");
        return new Matrix4x4(
            E(0, 0), E(1, 0), E(2, 0), E(3, 0),
            E(0, 1), E(1, 1), E(2, 1), E(3, 1),
            E(0, 2), E(1, 2), E(2, 2), E(3, 2),
            E(0, 3), E(1, 3), E(2, 3), E(3, 3));
    }

    // ---- The colour texture: the main texture, coloured as the material's colours say (through its colour mask)

    private static (byte[] Rgba, int W, int H) Diffuse(SerializedFile sf, Bundle bundle, long materialId, int maxSize)
    {
        var mat = materialId != 0 ? sf.Read(materialId) : null;
        var saved = mat.D("m_SavedProperties");
        Texture? Tex(string name)
        {
            foreach(var e in saved.L("m_TexEnvs"))
                if(e is KeyValuePair<object?, object?> kv && kv.Key as string == name)
                {
                    var (fid, pid) = kv.Value.Ptr("m_Texture");
                    if(fid == 0 && pid != 0 && sf.Read(pid) is { } t)
                        return Texture.Decode(t, bundle.Resources);
                }
            return null;
        }
        Vector4 Col(string name)
        {
            foreach(var e in saved.L("m_Colors"))
                if(e is KeyValuePair<object?, object?> kv && kv.Key as string == name && kv.Value is Dictionary<string, object?> c)
                    return new Vector4(c.F("r", 1f), c.F("g", 1f), c.F("b", 1f), c.F("a", 1f));
            return Vector4.One;
        }
        var main = Tex("_MainTex");
        var mask = Tex("_ColorMask");
        Vector4 c1 = Col("_Color"), c2 = Col("_Color2"), c3 = Col("_Color3");
        if(main == null)
        {
            // (no texture: the colour itself)
            main = new Texture(4, 4, Enumerable.Repeat(new byte[] { 255, 255, 255, 255 }, 16).SelectMany(x => x).ToArray());
        }
        var px = (byte[])main.Rgba.Clone();
        int w = main.W, h = main.H;
        for(int y = 0; y < h; y++)
            for(int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                var col = new Vector4(px[i], px[i + 1], px[i + 2], px[i + 3]) / 255f;
                Vector4 tinted;
                if(mask != null)
                {
                    int mx = x * mask.W / w, my = y * mask.H / h;
                    int mi = (my * mask.W + mx) * 4;
                    float r = mask.Rgba[mi] / 255f, g = mask.Rgba[mi + 1] / 255f, b = mask.Rgba[mi + 2] / 255f;
                    tinted = Vector4.Lerp(col, col * c1, r);
                    tinted = Vector4.Lerp(tinted, col * c2, g);
                    tinted = Vector4.Lerp(tinted, col * c3, b);
                }
                else
                    tinted = col * c1;
                px[i] = (byte)Math.Clamp(tinted.X * 255f, 0, 255);
                px[i + 1] = (byte)Math.Clamp(tinted.Y * 255f, 0, 255);
                px[i + 2] = (byte)Math.Clamp(tinted.Z * 255f, 0, 255);
                px[i + 3] = (byte)Math.Clamp(col.W * 255f, 0, 255);
            }
        while((w > maxSize || h > maxSize) && w > 4 && h > 4)
            (px, w, h) = TexWriter.Half(px, w, h);
        return (px, w, h);
    }
}

/// <summary>A mesh's arrays (Unity 5.x layout).</summary>
internal sealed class MeshData
{
    public Vector3[] P = [], N = [];
    public Vector4[] T = [];
    public Vector2[] UV = [];
    public int[] Indices = [];
    public List<(int First, int Count)> Submeshes = new();
    public (int[] Index, float[] Weight)[]? Weights;

    public static MeshData? Read(Dictionary<string, object?> mesh)
    {
        var vd = mesh.D("m_VertexData");
        if(vd == null)
            return null;
        int n = (int)vd.I("m_VertexCount");
        var data = vd.Bytes("m_DataSize");
        var channels = vd.L("m_Channels").Select(c => ((int)c.I("stream"), (int)c.I("offset"), (int)c.I("format"), (int)c.I("dimension") & 0xF)).ToList();
        if(n == 0 || channels.Count == 0 || data.Length == 0)
            return null;
        static int Size(int format) => format switch { 0 => 4, 1 => 2, _ => 1 };
        var strides = new Dictionary<int, int>();
        foreach(var (s, o, f, d) in channels)
            if(d > 0)
                strides[s] = Math.Max(strides.GetValueOrDefault(s), o + Size(f) * d);
        var starts = new Dictionary<int, int>();
        int pos = 0;
        foreach(var s in strides.Keys.OrderBy(k => k))
        {
            starts[s] = pos;
            pos += strides[s] * n;
            pos = (pos + 15) / 16 * 16;
        }
        float[]? Channel(int i, out int dim)
        {
            dim = 0;
            if(i >= channels.Count)
                return null;
            var (s, o, f, d) = channels[i];
            if(d == 0)
                return null;
            dim = d;
            var outv = new float[n * d];
            int stride = strides[s], baseAt = starts[s] + o;
            for(int v = 0; v < n; v++)
                for(int k = 0; k < d; k++)
                {
                    int at = baseAt + v * stride + k * Size(f);
                    if(at + Size(f) > data.Length)
                        return null;
                    outv[v * d + k] = f switch
                    {
                        0 => BitConverter.ToSingle(data, at),
                        1 => (float)BitConverter.ToHalf(data, at),
                        _ => data[at] / 255f,
                    };
                }
            return outv;
        }
        var m = new MeshData();
        var p = Channel(0, out int pd);
        if(p == null || pd < 3)
            return null;
        m.P = Enumerable.Range(0, n).Select(v => new Vector3(p[v * pd], p[v * pd + 1], p[v * pd + 2])).ToArray();
        var nn = Channel(1, out int nd);
        if(nn != null && nd >= 3)
            m.N = Enumerable.Range(0, n).Select(v => new Vector3(nn[v * nd], nn[v * nd + 1], nn[v * nd + 2])).ToArray();
        var uv = Channel(3, out int ud);
        if(uv != null && ud >= 2)
            m.UV = Enumerable.Range(0, n).Select(v => new Vector2(uv[v * ud], uv[v * ud + 1])).ToArray();
        var t = Channel(7, out int td);
        if(t != null && td >= 4)
            m.T = Enumerable.Range(0, n).Select(v => new Vector4(t[v * td], t[v * td + 1], t[v * td + 2], t[v * td + 3])).ToArray();

        var ib = mesh.Bytes("m_IndexBuffer");
        bool wide = mesh.I("m_IndexFormat") == 1;
        m.Indices = wide
            ? Enumerable.Range(0, ib.Length / 4).Select(i => (int)BitConverter.ToUInt32(ib, i * 4)).ToArray()
            : Enumerable.Range(0, ib.Length / 2).Select(i => (int)BitConverter.ToUInt16(ib, i * 2)).ToArray();
        foreach(var sm in mesh.L("m_SubMeshes"))
        {
            if(sm.I("topology") != 0)
                continue;
            m.Submeshes.Add(((int)sm.I("firstByte") / (wide ? 4 : 2), (int)sm.I("indexCount")));
        }
        var skin = mesh.L("m_Skin");
        if(skin.Count == n)
            m.Weights = skin.Select(s => (
                new[] { (int)s.I("boneIndex[0]"), (int)s.I("boneIndex[1]"), (int)s.I("boneIndex[2]"), (int)s.I("boneIndex[3]") },
                new[] { s.F("weight[0]"), s.F("weight[1]"), s.F("weight[2]"), s.F("weight[3]") })).ToArray();
        return m;
    }
}

/// <summary>A texture decoded to RGBA (row 0 first, as Unity keeps it: the same way round the game's UVs read it).</summary>
internal sealed class Texture
{
    public readonly int W, H;
    public readonly byte[] Rgba;

    public Texture(int w, int h, byte[] rgba)
    {
        W = w;
        H = h;
        Rgba = rgba;
    }

    public static Texture? Decode(Dictionary<string, object?> t, Dictionary<string, byte[]> resources)
    {
        int w = (int)t.I("m_Width"), h = (int)t.I("m_Height"), format = (int)t.I("m_TextureFormat");
        if(w <= 0 || h <= 0)
            return null;
        var data = t.Bytes("image data");
        if(data.Length == 0 && t.D("m_StreamData") is { } stream)
        {
            string path = stream.S("path");
            string name = path[(path.LastIndexOf('/') + 1)..];
            if(resources.TryGetValue(name, out var res))
            {
                long off = stream.I("offset"), size = stream.I("size");
                if(off + size <= res.Length)
                    data = res.AsSpan((int)off, (int)size).ToArray();
            }
        }
        if(data.Length == 0)
            return null;
        var px = new byte[w * h * 4];
        switch(format)
        {
            case 1: // Alpha8
                for(int i = 0; i < w * h && i < data.Length; i++)
                {
                    px[i * 4] = px[i * 4 + 1] = px[i * 4 + 2] = 255;
                    px[i * 4 + 3] = data[i];
                }
                break;
            case 3: // RGB24
                for(int i = 0; i < w * h && i * 3 + 2 < data.Length; i++)
                {
                    px[i * 4] = data[i * 3];
                    px[i * 4 + 1] = data[i * 3 + 1];
                    px[i * 4 + 2] = data[i * 3 + 2];
                    px[i * 4 + 3] = 255;
                }
                break;
            case 4: // RGBA32
                Buffer.BlockCopy(data, 0, px, 0, Math.Min(px.Length, data.Length));
                break;
            case 5: // ARGB32
                for(int i = 0; i < w * h && i * 4 + 3 < data.Length; i++)
                {
                    px[i * 4] = data[i * 4 + 1];
                    px[i * 4 + 1] = data[i * 4 + 2];
                    px[i * 4 + 2] = data[i * 4 + 3];
                    px[i * 4 + 3] = data[i * 4];
                }
                break;
            case 14: // BGRA32
                for(int i = 0; i < w * h && i * 4 + 3 < data.Length; i++)
                {
                    px[i * 4] = data[i * 4 + 2];
                    px[i * 4 + 1] = data[i * 4 + 1];
                    px[i * 4 + 2] = data[i * 4];
                    px[i * 4 + 3] = data[i * 4 + 3];
                }
                break;
            case 7: // RGB565
                for(int i = 0; i < w * h && i * 2 + 1 < data.Length; i++)
                {
                    int v = BitConverter.ToUInt16(data, i * 2);
                    px[i * 4] = (byte)(((v >> 11) & 31) * 255 / 31);
                    px[i * 4 + 1] = (byte)(((v >> 5) & 63) * 255 / 63);
                    px[i * 4 + 2] = (byte)((v & 31) * 255 / 31);
                    px[i * 4 + 3] = 255;
                }
                break;
            case 13: // RGBA4444
            case 2: // ARGB4444
                for(int i = 0; i < w * h && i * 2 + 1 < data.Length; i++)
                {
                    int v = BitConverter.ToUInt16(data, i * 2);
                    int a = v >> 12 & 15, b = v >> 8 & 15, c = v >> 4 & 15, d = v & 15;
                    var (r, g, bl, al) = format == 13 ? (a, b, c, d) : (b, c, d, a);
                    px[i * 4] = (byte)(r * 17);
                    px[i * 4 + 1] = (byte)(g * 17);
                    px[i * 4 + 2] = (byte)(bl * 17);
                    px[i * 4 + 3] = (byte)(al * 17);
                }
                break;
            case 10: // DXT1
            case 12: // DXT5
                Blocks(data, px, w, h, format == 12);
                break;
            default:
                return null; // (crunched and other formats: left as plain colour)
        }
        return new Texture(w, h, px);
    }

    private static void Blocks(byte[] data, byte[] px, int w, int h, bool dxt5)
    {
        int bw = (w + 3) / 4, bh = (h + 3) / 4, size = dxt5 ? 16 : 8;
        Span<byte> alpha = stackalloc byte[16];
        Span<int> pal = stackalloc int[8];
        var palette = new (int R, int G, int B, int A)[4];
        for(int by = 0; by < bh; by++)
            for(int bx = 0; bx < bw; bx++)
            {
                int at = (by * bw + bx) * size;
                if(at + size > data.Length)
                    return;
                alpha.Fill(255);
                if(dxt5)
                {
                    int a0 = data[at], a1 = data[at + 1];
                    ulong bits = 0;
                    for(int k = 0; k < 6; k++)
                        bits |= (ulong)data[at + 2 + k] << (8 * k);
                    pal[0] = a0;
                    pal[1] = a1;
                    if(a0 > a1)
                        for(int k = 1; k < 7; k++)
                            pal[k + 1] = ((7 - k) * a0 + k * a1) / 7;
                    else
                    {
                        for(int k = 1; k < 5; k++)
                            pal[k + 1] = ((5 - k) * a0 + k * a1) / 5;
                        pal[6] = 0;
                        pal[7] = 255;
                    }
                    for(int k = 0; k < 16; k++)
                        alpha[k] = (byte)pal[(int)((bits >> (3 * k)) & 7)];
                    at += 8;
                }
                int c0 = BitConverter.ToUInt16(data, at), c1 = BitConverter.ToUInt16(data, at + 2);
                uint idx = BitConverter.ToUInt32(data, at + 4);
                static (int R, int G, int B) Rgb(int c) => (((c >> 11) & 31) * 255 / 31, ((c >> 5) & 63) * 255 / 63, (c & 31) * 255 / 31);
                var (r0, g0, b0) = Rgb(c0);
                var (r1, g1, b1) = Rgb(c1);
                palette[0] = (r0, g0, b0, 255);
                palette[1] = (r1, g1, b1, 255);
                if(c0 > c1 || dxt5)
                {
                    palette[2] = ((2 * r0 + r1) / 3, (2 * g0 + g1) / 3, (2 * b0 + b1) / 3, 255);
                    palette[3] = ((r0 + 2 * r1) / 3, (g0 + 2 * g1) / 3, (b0 + 2 * b1) / 3, 255);
                }
                else
                {
                    palette[2] = ((r0 + r1) / 2, (g0 + g1) / 2, (b0 + b1) / 2, 255);
                    palette[3] = (0, 0, 0, 0);
                }
                for(int k = 0; k < 16; k++)
                {
                    int x = bx * 4 + (k & 3), y = by * 4 + (k >> 2);
                    if(x >= w || y >= h)
                        continue;
                    var c = palette[(idx >> (2 * k)) & 3];
                    int o = (y * w + x) * 4;
                    px[o] = (byte)c.R;
                    px[o + 1] = (byte)c.G;
                    px[o + 2] = (byte)c.B;
                    px[o + 3] = dxt5 ? alpha[k] : (byte)c.A;
                }
            }
    }
}
