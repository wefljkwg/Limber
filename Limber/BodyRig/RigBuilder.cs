using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace Limber.BodyRig;

/// <summary>
/// Builds the "Limber Breast Rig" Penumbra mod: every adult body skeleton as currently active (whatever skeleton mod
/// is on, e.g. an IVCS or YAS skeleton), with each rigged part's grid added at the end wherever the skeleton has that part's bones. Nothing else in the skeleton changes.
/// Game thread only (uses the game's Havok code).
/// </summary>
public static class RigBuilder
{
    public const string ModName = "Limber Breast Rig";

    /// <summary>
    /// Every adult body skeleton the game has: chara/human/cNN01 (NN = 01..99; the "01" variant is the base adult
    /// body). Found by asking for each one, so no list of races is kept here.
    /// </summary>
    private static IEnumerable<string> BodySkeletonCodes()
    {
        for (int n = 1; n <= 99; n++)
        {
            if (!Limber.Koi.Safety.AdultAllowedSkeleton(n))
                continue;
            yield return $"c{n:00}01";
        }
    }

    public static string GamePath(string code) => $"chara/human/{code}/skeleton/base/b0001/skl_{code}b0001.sklb";

    // The mod's files: "skeletons/{code}/..." for each race's skeleton, and the racial deformers.
    private const string PbdKey = "deformer/human.pbd";
    private static string GameFor(string key) => key == PbdKey ? Deformers.GamePath : GamePath(key.Split('/')[1]);

    /// <summary>
    /// What was built: the package, a report, whether Penumbra took it, and per skeleton (e.g. "c0201") where every
    /// added bone sits in the skeleton's model space and its parent bone (to weight models to it).
    /// </summary>
    public sealed record Result(string? PackagePath, List<string> Lines, bool Installed,
        Dictionary<string, Dictionary<string, (System.Numerics.Vector3 Position, string Parent)>>? Bones = null);

    public const string Description = "Limber's body rig: a lattice of extra bones per breast (lmb_lat_mune_*), testicle (lmb_lat_kougan_*) and butt cheek (lmb_lat_shiri_*), " +
                                      "plus the older surface grids (lmb_mune_* / lmb_kougan_*), added to your active skeleton for Limber's soft " +
                                      "breasts and soft testicles. Put this mod above your skeleton mod. Rebuild it in Limber whenever your " +
                                      "skeleton mod changes.";

    /// <summary>Build the rigged skeletons without packing them (game thread).</summary>
    public static (Dictionary<string, byte[]> Files, List<string> Lines, Dictionary<string, Dictionary<string, (Vector3 Position, string Parent)>> Bones)
        BuildFiles(IReadOnlyDictionary<string, BreastRigShape> shapes)
    {
        var (files, lines, bones) = BuildSkeletons(shapes);
        return (files.ToDictionary(kv => GameFor(kv.Key), kv => kv.Value), lines, bones);
    }

    /// <summary>
    /// Build the mod package into <paramref name="outputDir"/>, with each part's placement (parts missing from
    /// <paramref name="shapes"/> use their default), and send it to Penumbra.
    /// </summary>
    public static Result Build(IReadOnlyDictionary<string, BreastRigShape> shapes, string outputDir)
    {
        var (files, lines, placed) = BuildSkeletons(shapes);
        if (files.Count == 0)
            return new Result(null, lines, false, placed);

        Directory.CreateDirectory(outputDir);
        var package = Path.Combine(outputDir, ModName + ".pmp");
        WritePackage(package, files);
        lines.Add($"Saved {Path.GetFileName(package)} ({files.Count} skeletons).");

        bool installed = false;
        try
        {
            var install = Plugin.PluginInterface.GetIpcSubscriber<string, int>("Penumbra.InstallMod.V5");
            installed = install.InvokeFunc(package) == 0; // 0 = Success (queued for install)
        }
        catch (Exception e)
        {
            Plugin.Log.Information($"Limber: Penumbra install not available ({e.Message})");
        }
        return new Result(package, lines, installed, placed);
    }

    private static (Dictionary<string, byte[]>, List<string>, Dictionary<string, Dictionary<string, (Vector3 Position, string Parent)>>)
        BuildSkeletons(IReadOnlyDictionary<string, BreastRigShape> shapes)
    {
        var lines = new List<string>();
        var files = new Dictionary<string, byte[]>();
        var placed = new Dictionary<string, Dictionary<string, (Vector3 Position, string Parent)>>();
        var parentOf = new Dictionary<string, string>();
        bool rebuiltFromRig = false;

        foreach (var code in BodySkeletonCodes())
        {
            var gamePath = GamePath(code);
            try
            {
                var source = ReadActive(gamePath, out var from);
                if (source == null)
                    continue; // no such skeleton
                var built = AddRig(source, RigParts.Built, shapes, out var added, out int replaced, out var bones, out var parents);
                foreach (var (child, parent) in parents)
                    parentOf.TryAdd(child, parent);
                if (built == null)
                {
                    lines.Add($"{code}: none of the rigged parts' bones are in this skeleton, skipped.");
                    continue;
                }
                files[$"skeletons/{code}/skl_{code}b0001.sklb"] = built;
                placed[code] = bones;
                lines.Add($"{code}: {added}" + (replaced > 0 ? $" (replaced {replaced} from an earlier build)" : "") + $" - from {from}.");
                rebuiltFromRig |= replaced > 0;
            }
            catch (Exception e)
            {
                lines.Add($"{code}: failed - {e.Message}");
                Plugin.Log.Warning(e, $"Limber: body rig for {code} failed");
            }
        }

        // The racial deformers: each rig bone fitted to another race's body like the bone it hangs from (else gear
        // shared between races leaves the rigged parts in the wrong race's proportions).
        if (files.Count > 0)
        {
            try
            {
                var pbd = ReadActive(Deformers.GamePath, out var pbdFrom);
                int fitted = 0;
                var withRig = pbd != null ? Deformers.WithRig(pbd, parentOf, RigParts.IsRigBone, out fitted) : null;
                if (withRig != null)
                {
                    files[PbdKey] = withRig;
                    lines.Add($"Racial deformers: rig bones fitted like their parents ({fitted} entries) - from {pbdFrom}.");
                }
                else
                    lines.Add("Racial deformers: couldn't be read, left as they are (gear made for another race may not line up).");
            }
            catch (Exception e)
            {
                lines.Add($"Racial deformers: failed - {e.Message}");
                Plugin.Log.Warning(e, "Limber: racial deformers failed");
            }
        }

        if (rebuiltFromRig)
            lines.Add("Note: some skeletons came from an earlier build of this mod (it is still active). If you changed " +
                      "skeleton mods, disable this mod in Penumbra first, then rebuild.");

        return (files, lines, placed);
    }

    /// <summary>
    /// Every bone of a race's body skeleton (as your active mods have it) where it is at rest, in model space, and its
    /// parent's name ("" for the root). Null if there's no such skeleton. Game thread (uses the game's Havok).
    /// </summary>
    public static Dictionary<string, (Vector3 Position, Quaternion Rotation, Vector3 Scale, string Parent)>? RestPose(string code)
    {
        var source = ReadActive(GamePath(code), out _);
        if (source == null)
            return null;
        var file = SklbFile.Read(source);
        var skeleton = new SkeletonXml(HavokXml.FromBinary(file.Havok));
        var model = skeleton.ModelPose();
        var result = new Dictionary<string, (Vector3, Quaternion, Vector3, string)>(skeleton.Count);
        for (int i = 0; i < skeleton.Count; i++)
        {
            int parent = skeleton.Parents[i];
            result[skeleton.Names[i]] = (model[i].Position, model[i].Rotation, model[i].Scale,
                parent >= 0 && parent < skeleton.Count ? skeleton.Names[parent] : "");
        }
        return result;
    }

    /// <summary>
    /// The skeleton the game would load for the player right now (Penumbra's active mods included); null if none.
    /// </summary>
    private static byte[]? ReadActive(string gamePath, out string from)
    {
        from = "the game";
        try
        {
            var resolve = Plugin.PluginInterface.GetIpcSubscriber<string, string>("Penumbra.ResolvePlayerPath");
            var path = resolve.InvokeFunc(gamePath);
            if (!string.IsNullOrEmpty(path) && !path.Equals(gamePath, StringComparison.OrdinalIgnoreCase))
            {
                if (Path.IsPathRooted(path) && File.Exists(path))
                {
                    from = "your active mods";
                    return File.ReadAllBytes(path);
                }
                // A file swap to another game file.
                if (!Path.IsPathRooted(path) && Plugin.DataManager.GetFile(path)?.Data is { } swapped)
                {
                    from = "your active mods (file swap)";
                    return swapped;
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Log.Information($"Limber: Penumbra path lookup not available ({e.Message})");
        }
        return Plugin.DataManager.GetFile(gamePath)?.Data;
    }

    /// <summary>
    /// The skeleton with each part's grid added (any grid from an earlier build is replaced); null if none of the
    /// parts' bones are in this skeleton.
    /// </summary>
    public static byte[]? AddRig(byte[] sklb, RigPart[] parts, IReadOnlyDictionary<string, BreastRigShape> shapes,
        out string added, out int replaced)
        => AddRig(sklb, parts, shapes, out added, out replaced, out _);

    public static byte[]? AddRig(byte[] sklb, RigPart[] parts, IReadOnlyDictionary<string, BreastRigShape> shapes,
        out string added, out int replaced, out Dictionary<string, (Vector3 Position, string Parent)> bones)
        => AddRig(sklb, parts, shapes, out added, out replaced, out bones, out _);

    /// <param name="parentOf">Every bone's parent (by name) in the rigged skeleton.</param>
    public static byte[]? AddRig(byte[] sklb, RigPart[] parts, IReadOnlyDictionary<string, BreastRigShape> shapes,
        out string added, out int replaced, out Dictionary<string, (Vector3 Position, string Parent)> bones,
        out Dictionary<string, string> parentOf)
    {
        bones = new Dictionary<string, (Vector3, string)>();
        parentOf = new Dictionary<string, string>();
        var file = SklbFile.Read(sklb);
        var skeleton = new SkeletonXml(HavokXml.FromBinary(file.Havok));
        replaced = skeleton.RemoveTrailing(RigParts.IsRigBone);
        var model = skeleton.ModelPose();
        var report = new List<string>();

        foreach (var part in parts)
        {
            var shape = shapes.TryGetValue(part.Key, out var given) ? given : part.DefaultShape();
            var leftName = part.ParentBone(n => skeleton.IndexOf(n) >= 0, left: true);
            var rightName = part.ParentBone(n => skeleton.IndexOf(n) >= 0, left: false);
            if (leftName == null || rightName == null)
                continue;
            int leftParent = skeleton.IndexOf(leftName), rightParent = skeleton.IndexOf(rightName);
            float separation = Vector3.Distance(model[leftParent].Position, model[rightParent].Position);
            if (!(separation > 0.005f && separation < 1f))
                separation = part.FallbackSeparation;
            // A limb's grid lines up along it: where its next joint is.
            Vector3? ChildAt(bool left)
            {
                if (part.Child == null)
                    return null;
                int c = skeleton.IndexOf(part.Child.Replace("{side}", left ? "l" : "r"));
                return c >= 0 ? model[c].Position : null;
            }
            if (part.Child != null && (ChildAt(true) == null || ChildAt(false) == null))
                continue;

            // Where the part's own bones are (for fitting its box to a model): "#breasts:l" / ":r", ":sep", and a
            // limb's next joint ("#thigh:lc" / ":rc").
            bones[$"#{part.Key}:l"] = (model[leftParent].Position, leftName);
            bones[$"#{part.Key}:r"] = (model[rightParent].Position, rightName);
            bones[$"#{part.Key}:sep"] = (new Vector3(separation, 0, 0), "");
            if (ChildAt(true) is { } lc && ChildAt(false) is { } rc)
            {
                bones[$"#{part.Key}:lc"] = (lc, "");
                bones[$"#{part.Key}:rc"] = (rc, "");
            }

            foreach (var (parent, left) in part.Single ? new[] { (leftParent, true) } : new[] { (leftParent, true), (rightParent, false) })
            {
                var p = model[parent];
                float outward = part.Single ? 1f : MathF.Sign(p.Position.X);
                if (outward == 0)
                    outward = left ? 1f : -1f;
                var axes = part.Axes(p.Position, ChildAt(left), outward);
                var inverse = Quaternion.Inverse(p.Rotation);
                for (int i = 0; i < part.PerSide; i++)
                {
                    var offset = part.PlacedOffset(i, shape, separation, axes);
                    var local = Vector3.Transform(offset, inverse) / Safe(p.Scale);
                    skeleton.Add(part.BoneName(left, i), parent, new SkeletonXml.Transform(local, Quaternion.Identity, Vector3.One));
                    bones[part.BoneName(left, i)] = (p.Position + offset, skeleton.Names[parent]);
                }
                if (part.Nipples)
                {
                    var n = part.NippleOffset(shape, separation);
                    var offset = new Vector3(n.X * outward, n.Y, n.Z);
                    var local = Vector3.Transform(offset, inverse) / Safe(p.Scale);
                    skeleton.Add(part.NippleName(left), parent, new SkeletonXml.Transform(local, Quaternion.Identity, Vector3.One));
                    bones[part.NippleName(left)] = (p.Position + offset, skeleton.Names[parent]);
                }
            }
            report.Add($"{part.Label.ToLowerInvariant()} {(part.PerSide + (part.Nipples ? 1 : 0)) * (part.Single ? 1 : 2)} bones");
        }
        added = report.Count > 0 ? "added " + string.Join(", ", report) : "";
        if (report.Count == 0)
            return null;
        for (int i = 0; i < skeleton.Count; i++)
            if (skeleton.Parents[i] >= 0 && skeleton.Parents[i] < skeleton.Count)
                parentOf[skeleton.Names[i]] = skeleton.Names[skeleton.Parents[i]];

        // Back to binary through the game's Havok, then read it back as a check before anything is saved.
        var havok = HavokXml.ToBinary(skeleton.ToXml());
        var check = new SkeletonXml(HavokXml.FromBinary(havok));
        if (check.Count != skeleton.Count || !check.Names.SequenceEqual(skeleton.Names) || !check.Parents.SequenceEqual(skeleton.Parents))
            throw new InvalidDataException("the rebuilt skeleton didn't read back the same; nothing was saved.");

        file.Havok = havok;
        return file.ToBytes();
    }

    private static Vector3 Safe(Vector3 scale) => new(
        MathF.Abs(scale.X) > 1e-4f ? scale.X : 1f,
        MathF.Abs(scale.Y) > 1e-4f ? scale.Y : 1f,
        MathF.Abs(scale.Z) > 1e-4f ? scale.Z : 1f);

    private static void WritePackage(string path, Dictionary<string, byte[]> files)
    {
        var temp = path + ".part";
        using (var stream = File.Create(temp))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            void Text(string name, string text)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                w.Write(text);
            }

            var json = new JsonSerializerOptions { WriteIndented = true };
            Text("meta.json", JsonSerializer.Serialize(new
            {
                FileVersion = 3,
                Name = ModName,
                Author = "Limber",
                Description = Description,
                Version = "1.0.0",
                Website = "",
                Image = "",
                ModTags = Array.Empty<string>(),
            }, json));
            Text("default_mod.json", JsonSerializer.Serialize(new
            {
                Version = 0,
                Files = files.Keys.ToDictionary(
                    k => GameFor(k),
                    k => k.Replace('/', '\\')),
                FileSwaps = new Dictionary<string, string>(),
                Manipulations = Array.Empty<object>(),
            }, json));
            foreach (var (name, data) in files)
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(data);
            }
        }
        File.Move(temp, path, overwrite: true);
    }
}
