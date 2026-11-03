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
    public const string ModelsModName = "Limber Rigged Models"; // (earlier versions' models mod: "Limber Rigged Models (collection)")

    /// <summary>
    /// The rig's Penumbra mod for a collection: "Limber (Breast Rig) (collection)" when one part was rigged,
    /// "Limber (Rig) (collection)" for everything. One mod: the skeleton bones and the weighted models together.
    /// </summary>
    public static string RigModName(IReadOnlyList<RigPart> parts, string collection)
    {
        string what = parts.Count == 1 ? Singular(parts[0].Label) + " Rig" : "Rig";
        return $"Limber ({what}) ({Safe(collection)})";
    }

    private static string Singular(string label) => label switch
    {
        "Breasts" => "Breast",
        "Testicles" => "Testicle",
        _ => label,
    };

    /// <summary>One of Limber's rig mods (any collection, any part, or an earlier version's): by its folder name.</summary>
    public static bool IsRigMod(string folder) =>
        folder.StartsWith(ModelsModName, StringComparison.OrdinalIgnoreCase)
        || folder.Equals(RigBuilder.ModName, StringComparison.OrdinalIgnoreCase)
        || Regex.IsMatch(folder, @"^Limber \((?:[^()]+ )?Rig\) \(", RegexOptions.IgnoreCase);

    /// <summary>
    /// The folder of the rig mod that holds a collection's rigged models (for building on top of them): the
    /// everything-rig if there is one, else a one-part rig, else an earlier version's; null if none.
    /// </summary>
    public static string? RigModFolder(string root, string collection)
    {
        string tail = $" ({Safe(collection)})";
        var names = new List<string>();
        try
        {
            names.AddRange(Directory.GetDirectories(root).Select(d => Path.GetFileName(d)!));
        }
        catch
        {
            return null;
        }
        string? Pick(Func<string, bool> ok) => names.FirstOrDefault(n => n.EndsWith(tail, StringComparison.OrdinalIgnoreCase) && ok(n));
        var found = Pick(n => n.Equals($"Limber (Rig){tail}", StringComparison.OrdinalIgnoreCase))
            ?? Pick(n => Regex.IsMatch(n, @"^Limber \([^()]+ Rig\) \(", RegexOptions.IgnoreCase))
            ?? Pick(n => n.StartsWith(ModelsModName, StringComparison.OrdinalIgnoreCase));
        return found == null ? null : Path.Combine(root, found);
    }
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
        RigPart[] parts, bool fit, IReadOnlyDictionary<string, (float Roundness, float Fill)>? shaping = null, IReadOnlyCollection<string>? only = null)
    {
        var progress = new Progress();
        var copy = shapes.ToDictionary(kv => kv.Key, kv => kv.Value.Clone());
        _ = Task.Run(async () =>
        {
            try
            {
                await Run(objectIndex, address, copy, workDir, parts, fit, shaping, progress, only);
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
        RigPart[] parts, bool fit, IReadOnlyDictionary<string, (float Roundness, float Fill)>? shaping, Progress progress,
        IReadOnlyCollection<string>? only = null)
    {
        // Just some models ("Rig this one"): added to the rig mod already there, with the boxes as they are.
        HashSet<string>? onlySet = only is { Count: > 0 } ? new HashSet<string>(only, StringComparer.OrdinalIgnoreCase) : null;
        if (onlySet != null)
            fit = false;
        bool allowed = await Game(() => StillThere(index, address) && Limber.Koi.Safety.AdultAllowed(address));
        if (!allowed)
        {
            progress.Failed = true;
            progress.Add("This character can't be rigged.");
            return;
        }

        var root = await Game(() => Plugin.PluginInterface.GetIpcSubscriber<string>("Penumbra.GetModDirectory").InvokeFunc());
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            throw new InvalidOperationException("couldn't find the mod folder (is your mod manager running?)");
        var collection = await Game(() =>
            Plugin.PluginInterface.GetIpcSubscriber<int, (bool, bool, (Guid, string))>("Penumbra.GetCollectionForObject.V5").InvokeFunc(index));
        if (!collection.Item1)
            throw new InvalidOperationException("The mod manager doesn't know this character right now (an actor added in GPose by another tool, or by an older Limber: add it again on the Actors page)");
        var (collectionId, collectionName) = collection.Item3;
        progress.Add($"Collection: {collectionName}.");

        // Limber's earlier rig for this collection off while reading (so the skeletons and models read are your own
        // mods', not a rigged copy), and the character redrawn and given time to load, so what's read is exactly what
        // it wears now (a model that hadn't loaded yet, or a mod option just changed, used to be missed).
        // Every one of Limber's rig mods off in this collection while reading (not just the newest: older ones, other
        // parts' and earlier versions' would hand over rigged copies instead of your own mods' models, and which parts
        // got missed depended on which happened to be on). The ones that were on come back if it doesn't finish.
        var turnedOff = new List<string>();
        if (onlySet == null)
        {
            var mods = await Game(() => Plugin.PluginInterface.GetIpcSubscriber<Dictionary<string, string>>("Penumbra.GetModList").InvokeFunc());
            foreach (var other in mods.Keys)
            {
                if (!IsRigMod(other))
                    continue;
                int result = await Game(() => Plugin.PluginInterface.GetIpcSubscriber<Guid, string, string, bool, int>("Penumbra.TrySetMod.V5")
                    .InvokeFunc(collectionId, other, "", false));
                if (result == 0) // Success: it was on
                    turnedOff.Add(other);
            }
            if (turnedOff.Count > 0)
                progress.Add($"Limber's earlier rig mods off while reading your own models ({turnedOff.Count}).");
        }
        try
        {
            await RunRest(index, address, shapes, workDir, parts, fit, shaping, progress, onlySet, root, collectionId, collectionName);
        }
        catch
        {
            // Didn't finish: the rig mods that were on, back on.
            foreach (var dir in turnedOff)
                await Game(() => Plugin.PluginInterface.GetIpcSubscriber<Guid, string, string, bool, int>("Penumbra.TrySetMod.V5")
                    .InvokeFunc(collectionId, dir, "", true));
            throw;
        }
    }

    /// <summary>
    /// The character redrawn, then its files read once they stop changing (everything it wears loaded and resolved
    /// through the current mods). Up to about 10 seconds.
    /// </summary>
    private static async Task<Dictionary<string, HashSet<string>>?> WornFiles(ushort index, Progress progress)
    {
        await Game(() =>
        {
            Plugin.PluginInterface.GetIpcSubscriber<int, int, object?>("Penumbra.RedrawObject.V5").InvokeAction(index, 0);
            return true;
        });
        await Task.Delay(1000);
        Dictionary<string, HashSet<string>>? last = null;
        string lastKey = "";
        int stable = 0;
        long start = Environment.TickCount64;
        while (Environment.TickCount64 - start < 10000)
        {
            var paths = await Game(() =>
            {
                var all = Plugin.PluginInterface
                    .GetIpcSubscriber<ushort[], Dictionary<string, HashSet<string>>?[]>("Penumbra.GetGameObjectResourcePaths.V5")
                    .InvokeFunc(new[] { index });
                return all.Length > 0 ? all[0] : null;
            });
            string key = paths == null ? "" : string.Join("|", paths
                .SelectMany(kv => kv.Value.Where(g => g.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)).Select(g => g + ">" + kv.Key))
                .OrderBy(x => x, StringComparer.Ordinal));
            if (paths != null && key.Length > 0 && key == lastKey)
            {
                if (++stable >= 4) // (the same for 1.6 s: genital and butt mods can load a moment after the body)
                    return paths;
            }
            else
                stable = 0;
            last = paths;
            lastKey = key;
            await Task.Delay(400);
        }
        progress.Add("  (the character's files were still changing after 10 seconds: using what's loaded now)");
        return last;
    }

    private static async Task RunRest(ushort index, nint address, Dictionary<string, BreastRigShape> shapes, string workDir,
        RigPart[] parts, bool fit, IReadOnlyDictionary<string, (float Roundness, float Fill)>? shaping, Progress progress,
        HashSet<string>? onlySet, string root, Guid collectionId, string collectionName)
    {
        // (redrawn without the earlier rig, so the skeletons below are your own mods')
        progress.Add("Redrawing the character and waiting for everything it wears to load...");
        var worn = await WornFiles(index, progress);

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
        var paths = worn;
        if (paths == null)
            throw new InvalidOperationException("The mod manager couldn't list this character's files");

        var modelsName = RigModName(parts, collectionName);
        if (onlySet != null && RigModFolder(root, collectionName) is { } existing)
            modelsName = Path.GetFileName(existing);
        var modelsDir = Path.Combine(root, modelsName);
        var originals = Path.Combine(workDir, "originals");
        var models = new List<(string Game, string Code, byte[] Data)>();
        // The race most of the worn models are made for (the character's own): a model made for another race (a
        // genital or body part mod shared between races, often under the male Midlander's path) is weighted in it.
        var codes = paths.SelectMany(kv => kv.Value).Where(g => g.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase) && g.StartsWith("chara/", StringComparison.OrdinalIgnoreCase))
            .Select(RaceCode).Where(c => c != null && build.Bones.ContainsKey(c!)).GroupBy(c => c!).OrderByDescending(g => g.Count()).Select(g => g.Key).ToList();
        string? ownCode = codes.FirstOrDefault();
        foreach (var (actual, games) in paths)
        {
            foreach (var game in games.Where(g => g.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase) && g.StartsWith("chara/", StringComparison.OrdinalIgnoreCase)))
            {
                if (onlySet != null && !onlySet.Contains(game))
                    continue;
                var code = RaceCode(game);
                if (code == null)
                {
                    if (onlySet != null)
                        progress.Add($"  {Path.GetFileName(game)}: not a model for an adult body Limber can rig, skipped.");
                    continue;
                }
                if (!build.Bones.ContainsKey(code))
                {
                    if (ownCode == null)
                        continue;
                    progress.Add($"  {Path.GetFileName(game)}: made for another body ({code}), weighted as {ownCode}.");
                    code = ownCode;
                }
                var source = await ReadOriginal(actual, game, modelsDir, originals);
                if (source != null)
                    models.Add((game, code, source));
                else
                    progress.Add($"  {Path.GetFileName(game)}: couldn't read its file ({Path.GetFileName(actual.Split('|').Last())}), skipped.");
            }
        }

        // Which parts no worn model has (and the bones that might be them, under names Limber doesn't know yet).
        foreach (var part in parts)
        {
            bool any = false;
            var hints = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var (_, _, data) in models)
            {
                List<MdlRigger.MeshSkin> skins;
                try
                {
                    skins = MdlRigger.Skin(data, 0);
                }
                catch
                {
                    continue;
                }
                var names = new HashSet<string>(skins.SelectMany(k => k.Bones), StringComparer.Ordinal);
                foreach (var side in part.Single ? new[] { "l" } : new[] { "l", "r" })
                    if (SourceNames(part, side).Any(names.Contains) || names.Any(n => n.StartsWith(part.Prefix, StringComparison.Ordinal)))
                        any = true;
                foreach (var n in names)
                    if (MightBe(part, n))
                        hints.Add(n);
            }
            if (!any)
                progress.Add($"  {part.Label}: none of the models worn has {part.Label.ToLowerInvariant()} bones Limber knows"
                    + (hints.Count > 0 ? $" (bones that might be them: {string.Join(", ", hints.Take(12))}; tell Limber's author these names)" : " (is the mod with them on, and loaded?)")
                    + $". Models read: {string.Join(", ", models.Select(m => Path.GetFileName(m.Game)).Distinct().Take(16))}.");
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
        // (the skeleton bones go in the same mod as the models, below)

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
            {
                // (most models a character wears have nothing for these parts: only say so when there was a reason)
                if (log.Count > 0)
                    progress.Add($"  {Path.GetFileName(game)}: not rigged: " + string.Join("; ", log));
                continue;
            }
            files[game] = rigged;
            progress.Add($"  {Path.GetFileName(game)}: " + string.Join("; ", log));
        }
        if (files.Count == 0)
            progress.Add("None of the models on this character have weights for that part.");
        // One mod: the skeleton bones and the weighted models.
        var combined = new Dictionary<string, byte[]>(build.Files, StringComparer.OrdinalIgnoreCase);
        foreach (var (game, data) in files)
            combined[game] = data;
        var rigDir = await PutMod(root, modelsName, combined, $"Limber's rig for the \"{collectionName}\" collection: its skeleton bones, and the models worn there weighted to it (made by Limber; your own mods aren't changed). Rebuild from Limber's Body physics page after updating them.", progress, keepEarlier: true);

        // 3. On, and above everything else, in this character's collection.
        int turnedOn = await Game(() => Plugin.PluginInterface.GetIpcSubscriber<Guid, string, string, bool, int>("Penumbra.TrySetMod.V5")
            .InvokeFunc(collectionId, rigDir, "", true));
        int raised = await Game(() => Plugin.PluginInterface.GetIpcSubscriber<Guid, string, string, int, int>("Penumbra.TrySetModPriority.V5")
            .InvokeFunc(collectionId, rigDir, "", ModelsPriority));
        if (turnedOn is not (0 or 1) || raised is not (0 or 1)) // Success / NothingChanged
            progress.Add($"  Couldn't turn on \"{rigDir}\" in {collectionName} (the mod manager said {turnedOn}/{raised}); turn it on there yourself, at the top.");

        // Limber's other rig mods (other parts, or earlier versions' names): off in this collection, so they don't fight.
        var modList = await Game(() => Plugin.PluginInterface.GetIpcSubscriber<Dictionary<string, string>>("Penumbra.GetModList").InvokeFunc());
        foreach (var other in modList.Keys)
        {
            if (other == rigDir || !IsRigMod(other))
                continue;
            await Game(() => Plugin.PluginInterface.GetIpcSubscriber<Guid, string, string, bool, int>("Penumbra.TrySetMod.V5")
                .InvokeFunc(collectionId, other, "", false));
        }

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

    // A bone name that looks like it could be the part's (for the log, when none Limber knows is found).
    private static bool MightBe(RigPart part, string bone)
    {
        string b = bone.ToLowerInvariant();
        if (b.StartsWith("lmb_", StringComparison.Ordinal))
            return false;
        string[] words = part == RigParts.Testicles ? ["kougan", "tama", "ball", "testic", "scrot", "kinta", "sack", "nuts"]
            : part == RigParts.Butt ? ["shiri", "butt", "glute", "hip", "ass_", "cheek", "oshiri"]
            : part == RigParts.Breasts ? ["mune", "breast", "boob", "chest", "oppai"]
            : [];
        return words.Any(w => b.Contains(w, StringComparison.Ordinal));
    }

    // The old grid's bone names for a part (its weights are taken over too); the butt never had one.
    private static string LegacyPrefix(RigPart part) => part == RigParts.Testicles ? RigParts.LegacyTesticles.Prefix
        : part == RigParts.Breasts ? RigParts.LegacyBreasts.Prefix : part.Prefix + "old_";

    private static List<string> SourceNames(RigPart part, string side)
    {
        var names = part.AllParents.Select(p => SoftBoneMarks.ForSide(p, side == "l")).ToList();
        // The other names body and part mods use for the same bones (any that a model has are taken too).
        string[] extra = part == RigParts.Testicles
            ? ["iv_kintama_phys_{side}", "iv_kintama_{side}", "iv_kougan_phys_{side}", "iv_tama_{side}", "j_kougan_{side}", "iv_balls_{side}", "iv_testicle_{side}"]
            : part == RigParts.Breasts
                ? ["iv_mune_{side}", "iv_c_mune_phys_{side}", "j_mune_phys_{side}", "iv_mune_phys_{side}"]
                : part == RigParts.Butt
                    ? ["iv_shiri_phys_{side}", "j_shiri_{side}", "iv_shiri_b_{side}"]
                    : [];
        foreach (var e in extra)
        {
            var n = SoftBoneMarks.ForSide(e, side == "l");
            if (!names.Contains(n))
                names.Add(n);
        }
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
                    SourceNames(part, s).ToArray(),
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
            if (!relative.StartsWith("..", StringComparison.Ordinal) && IsRigMod(relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0]))
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
        progress.Add(result is 0 or 1 ? $"Saved the \"{name}\" mod ({files.Count} file(s))." : $"Saved \"{name}\", but the mod manager said {result} when loading it; reload it in your mod manager.");
        return name;
    }
}
