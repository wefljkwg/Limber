using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Limber.BodyRig;

/// <summary>
/// The whole rig in one go, for one character: build the rig mod (lattice bones in the skeleton), find the models the
/// character is wearing, weight every one that has breasts / testicles onto the lattice (<see cref="MdlRigger"/>),
/// save them as a Penumbra mod ("Limber Rigged Models") above everything else in the character's collection, and
/// redraw. Your own mods are never changed: their models are read, and the rigged copies go in Limber's mod.
/// </summary>
public static class OneClickRig
{
    public const string ModelsModName = "Limber Rigged Models";
    private const int RigPriority = 9998, ModelsPriority = 9999;

    public sealed class Progress
    {
        private readonly List<string> _lines = new();
        private volatile bool _done, _failed;
        public bool Done { get => _done; set => _done = value; }
        public bool Failed { get => _failed; set => _failed = value; }
        /// <summary>Box placements fitted to the models (part key -> placement); the UI saves them when done.</summary>
        public Dictionary<string, BreastRigShape> Fitted { get; } = new();
        public bool FittedApplied { get; set; }

        public void Add(string line)
        {
            lock (_lines)
                _lines.Add(line);
            Plugin.Log.Information("Limber rig: " + line);
        }

        public string[] Lines
        {
            get
            {
                lock (_lines)
                    return _lines.ToArray();
            }
        }
    }

    /// <summary>
    /// Rig <paramref name="parts"/> (breasts, testicles or both) on one character. With <paramref name="fit"/>, each
    /// part's box is first fitted to the part in the models it wears.
    /// </summary>
    public static Progress Start(ushort objectIndex, nint address, IReadOnlyDictionary<string, BreastRigShape> shapes, string workDir,
        RigPart[] parts, bool fit, IReadOnlyDictionary<string, (float Roundness, float Fill)>? shaping = null)
    {
        var progress = new Progress();
        var copy = shapes.ToDictionary(kv => kv.Key, kv => kv.Value.Clone());
        _ = Task.Run(async () =>
        {
            try
            {
                await Run(objectIndex, address, copy, workDir, parts, fit, shaping, progress);
            }
            catch (Exception e)
            {
                progress.Failed = true;
                progress.Add("Stopped: " + e.Message);
                Plugin.Log.Warning(e, "Limber: one-click rig failed");
            }
            finally
            {
                progress.Done = true;
            }
        });
        return progress;
    }

    internal static Task<T> Game<T>(Func<T> f) => Plugin.Framework.RunOnFrameworkThread(f);

    private static async Task Run(ushort index, nint address, Dictionary<string, BreastRigShape> shapes, string workDir,
        RigPart[] parts, bool fit, IReadOnlyDictionary<string, (float Roundness, float Fill)>? shaping, Progress progress)
    {
        bool allowed = await Game(() => StillThere(index, address) && Limber.Koi.Safety.AdultAllowed(address));
        if (!allowed)
        {
            progress.Failed = true;
            progress.Add("This character can't be rigged.");
            return;
        }

        var root = await Game(() => Plugin.PluginInterface.GetIpcSubscriber<string>("Penumbra.GetModDirectory").InvokeFunc());
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            throw new InvalidOperationException("couldn't find Penumbra's mod folder (is Penumbra running?)");
        var collection = await Game(() =>
            Plugin.PluginInterface.GetIpcSubscriber<int, (bool, bool, (Guid, string))>("Penumbra.GetCollectionForObject.V5").InvokeFunc(index));
        if (!collection.Item1)
            throw new InvalidOperationException("Penumbra doesn't know this character right now");
        var (collectionId, collectionName) = collection.Item3;
        progress.Add($"Collection: {collectionName}.");

        // 1. Where the parts' bones are in each skeleton (a first build, also used as is when nothing is fitted).
        progress.Add("Reading the skeletons...");
        var build = await Game(() => RigBuilder.BuildFiles(shapes));
        if (build.Files.Count == 0 || build.Bones.Count == 0)
        {
            foreach (var line in build.Lines)
                progress.Add("  " + line);
            throw new InvalidOperationException("no skeleton could be rigged");
        }

        // 2. The models the character wears (as your own mods have them).
        progress.Add("Reading the models your character is wearing...");
        var paths = await Game(() =>
        {
            var all = Plugin.PluginInterface
                .GetIpcSubscriber<ushort[], Dictionary<string, HashSet<string>>?[]>("Penumbra.GetGameObjectResourcePaths.V5")
                .InvokeFunc(new[] { index });
            return all.Length > 0 ? all[0] : null;
        });
        if (paths == null)
            throw new InvalidOperationException("Penumbra couldn't list this character's files");

        var modelsName = $"{ModelsModName} ({Safe(collectionName)})";
        var modelsDir = Path.Combine(root, modelsName);
        var originals = Path.Combine(workDir, "originals");
        var models = new List<(string Game, string Code, byte[] Data)>();
        foreach (var (actual, games) in paths)
        {
            foreach (var game in games.Where(g => g.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase) && g.StartsWith("chara/", StringComparison.OrdinalIgnoreCase)))
            {
                var code = RaceCode(game);
                if (code == null || !build.Bones.ContainsKey(code))
                    continue;
                var source = await ReadOriginal(actual, game, modelsDir, originals);
                if (source != null)
                    models.Add((game, code, source));
            }
        }

        // 3. Fit each part's box to the part in those models, and build the rig with it.
        if (fit)
        {
            bool changed = false;
            foreach (var part in parts)
            {
                var fitted = Fit(part, models, build.Bones);
                if (fitted == null)
                {
                    progress.Add($"  {part.Label}: not in these models, box left as it was.");
                    continue;
                }
                shapes[part.Key] = fitted;
                progress.Fitted[part.Key] = fitted;
                changed = true;
                progress.Add($"  {part.Label}: box fitted (size {fitted.Size:0.00}, outward {fitted.Out:0.00}, up {fitted.Up:0.00}, forward {fitted.Forward:0.00}, depth {fitted.Depth:0.00}).");
            }
            if (changed)
                build = await Game(() => RigBuilder.BuildFiles(shapes));
        }
        progress.Add("Building the rig (skeleton bones)...");
        foreach (var line in build.Lines)
            progress.Add("  " + line);
        var rigDir = await PutMod(root, RigBuilder.ModName, build.Files, RigBuilder.Description, progress);

        // 4. Weight the models onto the boxes.
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (game, code, source) in models)
        {
            var log = new List<string>();
            byte[]? rigged;
            try
            {
                rigged = MdlRigger.Rig(source, Sides(build.Bones[code], RigParts.Rigged, shapes, shaping), log);
            }
            catch (Exception e)
            {
                progress.Add($"  {Path.GetFileName(game)}: couldn't be read ({e.Message}), skipped.");
                continue;
            }
            if (rigged == null)
                continue;
            files[game] = rigged;
            progress.Add($"  {Path.GetFileName(game)}: " + string.Join("; ", log));
        }
        if (files.Count == 0)
            progress.Add("None of the models on this character have weights for that part.");
        else
            await PutMod(root, modelsName, files, $"Models weighted to Limber's rig for the \"{collectionName}\" collection (made by Limber from the models worn there; your own mods aren't changed). Rebuild from Limber's Body physics page after updating them.", progress, keepEarlier: true);

        // 3. On, and above everything else, in this character's collection.
        foreach (var (dir, priority) in new[] { (rigDir, RigPriority), (files.Count > 0 ? modelsName : null, ModelsPriority) })
        {
            if (dir == null)
                continue;
            int on = await Game(() => Plugin.PluginInterface.GetIpcSubscriber<Guid, string, string, bool, int>("Penumbra.TrySetMod.V5")
                .InvokeFunc(collectionId, dir, "", true));
            int at = await Game(() => Plugin.PluginInterface.GetIpcSubscriber<Guid, string, string, int, int>("Penumbra.TrySetModPriority.V5")
                .InvokeFunc(collectionId, dir, "", priority));
            if (on is not (0 or 1) || at is not (0 or 1)) // Success / NothingChanged
                progress.Add($"  Couldn't turn on \"{dir}\" in {collectionName} (Penumbra said {on}/{at}); turn it on there yourself, at the top.");
        }

        // The single shared models mod of earlier versions: off here (each collection now has its own).
        await Game(() => Plugin.PluginInterface.GetIpcSubscriber<Guid, string, string, bool, int>("Penumbra.TrySetMod.V5")
            .InvokeFunc(collectionId, ModelsModName, "", false));

        // 4. Redraw.
        await Game(() =>
        {
            Plugin.PluginInterface.GetIpcSubscriber<int, int, object?>("Penumbra.RedrawObject.V5").InvokeAction(index, 0);
            return true;
        });
        progress.Add(files.Count > 0
            ? $"Done: {files.Count} model(s) rigged. Turn on Soft breasts / Soft testicles / Soft butt to see them move."
            : "Done (rig only).");
    }

    internal static unsafe bool StillThere(ushort index, nint address)
    {
        var obj = Limber.Core.ActorSkeleton.GetObject(index);
        return obj != null && (nint)obj == address;
    }

    // Adult body type only (customize BodyType 1 = adult); children (4) are never rigged.
    internal static string? RaceCode(string gamePath)
    {
        // Only models made for a base adult body (cNN01); anything else (e.g. children's cNN04) is never rigged.
        var match = Regex.Match(Path.GetFileName(gamePath), @"^c(\d{2})01", RegexOptions.IgnoreCase);
        if (!match.Success || !Limber.Koi.Safety.AdultAllowedSkeleton(int.Parse(match.Groups[1].Value)))
            return null;
        return $"c{match.Groups[1].Value}01";
    }

    internal static string Safe(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim();
        return clean.Length > 0 ? clean : "collection";
    }

    // The old grid's bone names for a part (its weights are taken over too); the butt never had one.
    private static string LegacyPrefix(RigPart part) => part == RigParts.Testicles ? RigParts.LegacyTesticles.Prefix
        : part == RigParts.Breasts ? RigParts.LegacyBreasts.Prefix : part.Prefix + "old_";

    private static List<string> SourceNames(RigPart part, string side)
    {
        var names = part.Parents.Select(p => p.Replace("{side}", side)).ToList();
        if (part == RigParts.Testicles)
            names.Add($"iv_kintama_phys_{side}");
        return names;
    }

    /// <summary>
    /// A box around the part as it is in the models: centred on it, as wide / tall as it, from its back to its front
    /// (robust bounds: the outer 2% of vertices on each side are ignored). Null if no model has the part.
    /// </summary>
    private static BreastRigShape? Fit(RigPart part, List<(string Game, string Code, byte[] Data)> models,
        Dictionary<string, Dictionary<string, (Vector3 Position, string Parent)>> skeletons)
    {
        var found = new List<(BreastRigShape Shape, int Count)>();
        var nipples = new List<(Vector3 Offset, int Count)>();
        foreach (var (_, code, data) in models)
        {
            if (!skeletons.TryGetValue(code, out var b) || !b.TryGetValue($"#{part.Key}:sep", out var sep)
                || !b.TryGetValue($"#{part.Key}:l", out var pl) || !b.TryGetValue($"#{part.Key}:r", out var pr))
                continue;
            float separation = sep.Position.X;
            foreach (var (side, pivot) in part.Single ? new[] { ("l", pl.Position) } : new[] { ("l", pl.Position), ("r", pr.Position) })
            {
                var names = SourceNames(part, side);
                string legacy = LegacyPrefix(part) + side + "_", mine = part.Prefix + side + "_";
                List<Vector3> points;
                try
                {
                    points = MdlRigger.Vertices(data, n => names.Contains(n) || n.StartsWith(legacy, StringComparison.Ordinal) || n.StartsWith(mine, StringComparison.Ordinal));
                }
                catch
                {
                    continue;
                }
                if (points.Count < 30)
                    continue;
                float outward = part.Single ? 1f : MathF.Sign(pivot.X);
                if (outward == 0)
                    outward = side == "l" ? 1f : -1f;
                // In the part's own axes (along a limb for a limb's grid): outward, up, forward from its bone.
                Vector3? child = b.TryGetValue($"#{part.Key}:{side}c", out var cb) ? cb.Position : null;
                var axes = part.Axes(pivot, child, outward);
                var local = points.Select(p => new Vector3(Vector3.Dot(p - pivot, axes.Out), Vector3.Dot(p - pivot, axes.Up), Vector3.Dot(p - pivot, axes.Forward))).ToList();
                float Q(Func<Vector3, float> axis, float q)
                {
                    var sorted = local.Select(axis).OrderBy(x => x).ToArray();
                    return sorted[Math.Clamp((int)(q * (sorted.Length - 1)), 0, sorted.Length - 1)];
                }
                float x0 = Q(p => p.X, 0.02f), x1 = Q(p => p.X, 0.98f);
                float y0 = Q(p => p.Y, 0.02f), y1 = Q(p => p.Y, 0.98f);
                float z0 = Q(p => p.Z, 0.02f), z1 = Q(p => p.Z, 0.98f);
                if (part.Nipples)
                {
                    // The nipple: the part's front-most point; the bone goes in the middle of the ring around it.
                    var tip = points.MaxBy(p => p.Z);
                    float radius = part.NippleRadius * separation;
                    var ring = points.Where(p => Vector3.Distance(p, tip) is var d && d > 0.7f * radius && d < radius).ToList();
                    var centre = ring.Count >= 8 ? ring.Aggregate(Vector3.Zero, (a, p) => a + p) / ring.Count : tip;
                    var rel = (centre - pivot) / separation;
                    nipples.Add((new Vector3(rel.X * outward, rel.Y, rel.Z), points.Count));
                }
                found.Add((new BreastRigShape
                {
                    Size = Math.Clamp((part.OwnHeight ? x1 - x0 : MathF.Max(x1 - x0, y1 - y0)) / separation * 1.05f, 0.1f, 8f),
                    Height = part.OwnHeight ? Math.Clamp((y1 - y0) / separation * 1.05f, 0.1f, 12f) : 0f,
                    Out = Math.Clamp((x0 + x1) * 0.5f / separation, -3f, 3f),
                    Up = Math.Clamp((y0 + y1) * 0.5f / separation, -12f, 3f),
                    // (from the body side: the back of a breast, the front of a butt cheek)
                    Forward = Math.Clamp((part.Backward ? -z1 : z0) / separation, -4f, 4f),
                    Depth = Math.Clamp((z1 - z0) / separation, 0.1f, 5f),
                }, points.Count));
            }
        }
        if (found.Count == 0)
            return null;
        float total = found.Sum(f => f.Count);
        float Avg(Func<BreastRigShape, float> get) => found.Sum(f => get(f.Shape) * f.Count) / total;
        var shape = new BreastRigShape { Size = Avg(x => x.Size), Height = Avg(x => x.Height), Out = Avg(x => x.Out), Up = Avg(x => x.Up), Forward = Avg(x => x.Forward), Depth = Avg(x => x.Depth) };
        if (nipples.Count > 0)
        {
            float n = nipples.Sum(x => x.Count);
            var at = nipples.Aggregate(Vector3.Zero, (a, x) => a + x.Offset * x.Count) / n;
            shape.HasNipple = true;
            shape.NippleOut = at.X;
            shape.NippleUp = at.Y;
            shape.NippleForward = at.Z;
        }
        return shape;
    }

    private static List<MdlRigger.Side> Sides(Dictionary<string, (Vector3 Position, string Parent)> bones, RigPart[] parts,
        IReadOnlyDictionary<string, BreastRigShape> shapes, IReadOnlyDictionary<string, (float Roundness, float Fill)>? shaping = null)
    {
        var sides = new List<MdlRigger.Side>();
        foreach (var part in RigParts.Rigged)
        {
            if (!parts.Contains(part))
                continue;
            bool merge = part == RigParts.Testicles;
            foreach (var left in part.Single ? new[] { true } : new[] { true, false })
            {
                var names = Enumerable.Range(0, part.PerSide).Select(i => part.BoneName(left, i)).ToArray();
                if (!names.All(bones.ContainsKey))
                    continue;
                var s = left ? "l" : "r";
                // The nipple bone, once the nipples have been found on the models (fitted box).
                bool nipple = part.Nipples && shapes.TryGetValue(part.Key, out var placed) && placed.HasNipple
                    && bones.ContainsKey(part.NippleName(left));
                float separation = bones.TryGetValue($"#{part.Key}:sep", out var sepBone) ? sepBone.Position.X : part.FallbackSeparation;
                sides.Add(new MdlRigger.Side(names, names.Select(n => bones[n].Position).ToArray(), bones[names[0]].Parent,
                    part.Parents.Select(p => p.Replace("{side}", s)).Concat(part == RigParts.Testicles ? new[] { $"iv_kintama_phys_{s}" } : Array.Empty<string>()).ToArray(),
                    merge, $"{LegacyPrefix(part)}{s}_")
                {
                    PartKey = part.Key,
                    Left = left,
                    Roundness = shaping != null && shaping.TryGetValue(part.Key, out var sh) ? Math.Clamp(sh.Roundness, 0f, 1f) : 0f,
                    Fill = shaping != null && shaping.TryGetValue(part.Key, out var sf) ? Math.Clamp(sf.Fill, 0f, 1f) : 0f,
                    Nipple = nipple ? part.NippleName(left) : null,
                    NipplePosition = nipple ? bones[part.NippleName(left)].Position : default,
                    NippleRadius = part.NippleRadius * separation,
                    Rows = part.Rows,
                    Columns = part.Columns,
                    Steps = part.BackLayerBones ? part.Layers - 1 : part.Layers,
                    Optional = part.Shape,
                });
            }
        }
        return sides;
    }

    // The model as your own mods (or the game) have it: not Limber's rigged copy of it from an earlier run.
    private static async Task<byte[]?> ReadOriginal(string actual, string game, string modelsDir, string originals)
    {
        var path = actual;
        if (path.StartsWith('|'))
        {
            int end = path.IndexOf('|', 1);
            if (end > 0)
                path = path[(end + 1)..];
        }
        var saved = Path.Combine(originals, game.Replace('/', Path.DirectorySeparatorChar));
        if (Path.IsPathRooted(path))
        {
            var full = Path.GetFullPath(path);
            // Limber's own rigged copy (this collection's mod, or any other "Limber Rigged Models ..." one): use the
            // original saved when it was first rigged.
            var root = Path.GetDirectoryName(Path.GetFullPath(modelsDir))!;
            var relative = Path.GetRelativePath(root, full);
            if (!relative.StartsWith("..", StringComparison.Ordinal) && relative.StartsWith(ModelsModName, StringComparison.OrdinalIgnoreCase))
                return File.Exists(saved) ? await File.ReadAllBytesAsync(saved) : null;
            // The Body Shape tab's built copy: the original saved when it was first rigged, else the one it was
            // built from.
            if (!relative.StartsWith("..", StringComparison.Ordinal) && relative.StartsWith(Limber.BodyShape.BodyShapeBuild.ModName, StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(saved))
                    return await File.ReadAllBytesAsync(saved);
                var unshaped = Limber.BodyShape.BodyShapeBuild.UnshapedFile(game);
                return unshaped != null ? await File.ReadAllBytesAsync(unshaped) : null;
            }
            if (!File.Exists(full))
                return null;
            var bytes = await File.ReadAllBytesAsync(full);
            Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
            await File.WriteAllBytesAsync(saved, bytes);
            return bytes;
        }
        var data = await Game(() => Plugin.DataManager.GetFile(path)?.Data);
        if (data != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
            await File.WriteAllBytesAsync(saved, data);
        }
        return data;
    }

    /// <summary>Write a mod straight into Penumbra's mod folder and have Penumbra (re)load it. Returns its folder name.</summary>
    internal static async Task<string> PutMod(string root, string name, Dictionary<string, byte[]> files, string description, Progress progress,
        bool keepEarlier = false)
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);
        var metaPath = Path.Combine(dir, "meta.json");
        var oldDefault = Path.Combine(dir, "default_mod.json");

        // What's there from before: the mod's identifier (kept, so Penumbra sees the same mod), and its files.
        string? identifier = null;
        var earlier = new Dictionary<string, string>();
        void Collect(JsonElement container)
        {
            if (container.ValueKind == JsonValueKind.Object && container.TryGetProperty("Files", out var list) && list.ValueKind == JsonValueKind.Object)
                foreach (var entry in list.EnumerateObject())
                    if (entry.Value.GetString() is { } rel)
                        earlier[entry.Name] = rel;
        }
        try
        {
            if (File.Exists(metaPath))
            {
                using var meta = JsonDocument.Parse(await File.ReadAllTextAsync(metaPath));
                if (meta.RootElement.TryGetProperty("Identifier", out var id) && id.ValueKind == JsonValueKind.String)
                    identifier = id.GetString();
                if (meta.RootElement.TryGetProperty("DefaultData", out var data))
                    Collect(data);
            }
            if (File.Exists(oldDefault))
            {
                using var old = JsonDocument.Parse(await File.ReadAllTextAsync(oldDefault));
                Collect(old.RootElement);
            }
        }
        catch (Exception e)
        {
            Plugin.Log.Information($"Limber: couldn't read the earlier \"{name}\" mod ({e.Message})");
        }

        var map = new Dictionary<string, string>();
        foreach (var (game, data) in files)
        {
            var target = Path.Combine(dir, game.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllBytesAsync(target, data);
            map[game] = game.Replace('/', '\\');
        }
        if (keepEarlier)
        {
            // Models rigged on earlier runs (other outfits) whose files are still there.
            foreach (var (game, rel) in earlier)
                if (!map.ContainsKey(game) && File.Exists(Path.Combine(dir, rel.Replace('\\', Path.DirectorySeparatorChar))))
                    map[game] = rel;
        }

        // Penumbra's current mod format (FileVersion 4): everything in meta.json, the files under "DefaultData".
        var json = new JsonSerializerOptions { WriteIndented = true };
        var metaData = new Dictionary<string, object?>
        {
            ["FileVersion"] = 4,
            ["Identifier"] = identifier ?? Guid.NewGuid().ToString(),
            ["Name"] = name,
            ["Author"] = "Limber",
            ["Description"] = description,
            ["Version"] = "1.0.0",
            ["DefaultData"] = new Dictionary<string, object> { ["Files"] = map },
        };
        await File.WriteAllTextAsync(metaPath, JsonSerializer.Serialize(metaData, json), new UTF8Encoding(false));
        if (File.Exists(oldDefault))
            File.Delete(oldDefault); // the old format's file list, now in meta.json

        var known = await Game(() => Plugin.PluginInterface.GetIpcSubscriber<Dictionary<string, string>>("Penumbra.GetModList").InvokeFunc());
        int result = known.ContainsKey(name)
            ? await Game(() => Plugin.PluginInterface.GetIpcSubscriber<string, string, int>("Penumbra.ReloadMod.V5").InvokeFunc(name, ""))
            : await Game(() => Plugin.PluginInterface.GetIpcSubscriber<string, int>("Penumbra.AddMod.V5").InvokeFunc(name));
        progress.Add(result is 0 or 1 ? $"Saved the \"{name}\" mod ({files.Count} file(s))." : $"Saved \"{name}\", but Penumbra said {result} when loading it; reload it in Penumbra.");
        return name;
    }
}
