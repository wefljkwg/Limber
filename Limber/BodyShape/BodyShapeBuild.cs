using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Limber.BodyRig;

namespace Limber.BodyShape;

/// <summary>
/// Build (like BodySlide's): every model the character wears (the body and every outfit piece) gets the sliders'
/// shape baked into its vertices, saved as a Penumbra mod ("Limber Body Shape (collection)") above everything else in
/// the character's collection, and the character is redrawn. Clothes follow the body because each vertex moves with
/// the bones it's weighted to, the same way the body's do. Your own mods are only read; each model's shape is made
/// from the copy it had before any build (so building again never piles up).
/// </summary>
public static class BodyShapeBuild
{
    public const string ModName = "Limber Body Shape";
    private const int Priority = 10000; // above Limber's rigged models (9999)

    private static string? _workDir;
    /// <summary>Where the unshaped copies are kept (set by the plugin: its config folder).</summary>
    public static void SetWorkDir(string dir) => _workDir = dir;

    private static string UnshapedDir(string collection) => Path.Combine(_workDir ?? ".", "BodyShape", "unshaped", OneClickRig.Safe(collection));

    /// <summary>The newest unshaped copy of a model (any collection), or null.</summary>
    public static string? UnshapedFile(string game)
    {
        var root = Path.Combine(_workDir ?? ".", "BodyShape", "unshaped");
        if (!Directory.Exists(root))
            return null;
        string? best = null;
        DateTime bestTime = DateTime.MinValue;
        foreach (var dir in Directory.GetDirectories(root))
        {
            var path = Path.Combine(dir, game.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path) && File.GetLastWriteTimeUtc(path) > bestTime)
            {
                best = path;
                bestTime = File.GetLastWriteTimeUtc(path);
            }
        }
        return best;
    }

    /// <summary>Build (background); the result says which collection it went into, or null if it failed.</summary>
    public static OneClickRig.Progress Start(ushort index, nint address, IReadOnlyDictionary<string, float> values, Action<string>? built)
    {
        var progress = new OneClickRig.Progress();
        var copy = new Dictionary<string, float>(values);
        _ = Task.Run(async () =>
        {
            try
            {
                var collection = await Run(index, address, copy, progress);
                if (collection != null)
                    built?.Invoke(collection);
            }
            catch (Exception e)
            {
                progress.Failed = true;
                progress.Add("Stopped: " + e.Message);
                Plugin.Log.Warning(e, "Limber: body shape build failed");
            }
            finally
            {
                progress.Done = true;
            }
        });
        return progress;
    }

    /// <summary>Take the built shape off (the mod turned off in the character's collection) and redraw.</summary>
    public static OneClickRig.Progress Remove(ushort index, nint address, Action<string>? removed)
    {
        var progress = new OneClickRig.Progress();
        _ = Task.Run(async () =>
        {
            try
            {
                var collection = await OneClickRig.Game(() =>
                    Plugin.PluginInterface.GetIpcSubscriber<int, (bool, bool, (Guid, string))>("Penumbra.GetCollectionForObject.V5").InvokeFunc(index));
                if (!collection.Item1)
                    throw new InvalidOperationException("Penumbra doesn't know this character right now");
                var (id, name) = collection.Item3;
                var mod = $"{ModName} ({OneClickRig.Safe(name)})";
                await OneClickRig.Game(() => Plugin.PluginInterface.GetIpcSubscriber<Guid, string, string, bool, int>("Penumbra.TrySetMod.V5")
                    .InvokeFunc(id, mod, "", false));
                await OneClickRig.Game(() =>
                {
                    Plugin.PluginInterface.GetIpcSubscriber<int, int, object?>("Penumbra.RedrawObject.V5").InvokeAction(index, 0);
                    return true;
                });
                progress.Add($"Built shape taken off in {name}.");
                removed?.Invoke(name);
            }
            catch (Exception e)
            {
                progress.Failed = true;
                progress.Add("Stopped: " + e.Message);
            }
            finally
            {
                progress.Done = true;
            }
        });
        return progress;
    }

    private static async Task<string?> Run(ushort index, nint address, Dictionary<string, float> values, OneClickRig.Progress progress)
    {
        bool allowed = await OneClickRig.Game(() => OneClickRig.StillThere(index, address) && Limber.Koi.Safety.AdultAllowed(address));
        if (!allowed)
        {
            progress.Failed = true;
            progress.Add("This character's shape can't be built.");
            return null;
        }
        var root = await OneClickRig.Game(() => Plugin.PluginInterface.GetIpcSubscriber<string>("Penumbra.GetModDirectory").InvokeFunc());
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            throw new InvalidOperationException("couldn't find Penumbra's mod folder (is Penumbra running?)");
        var collection = await OneClickRig.Game(() =>
            Plugin.PluginInterface.GetIpcSubscriber<int, (bool, bool, (Guid, string))>("Penumbra.GetCollectionForObject.V5").InvokeFunc(index));
        if (!collection.Item1)
            throw new InvalidOperationException("Penumbra doesn't know this character right now");
        var (collectionId, collectionName) = collection.Item3;
        progress.Add($"Collection: {collectionName}.");

        var paths = await OneClickRig.Game(() =>
        {
            var all = Plugin.PluginInterface
                .GetIpcSubscriber<ushort[], Dictionary<string, HashSet<string>>?[]>("Penumbra.GetGameObjectResourcePaths.V5")
                .InvokeFunc(new[] { index });
            return all.Length > 0 ? all[0] : null;
        });
        if (paths == null)
            throw new InvalidOperationException("Penumbra couldn't list this character's files");

        var modName = $"{ModName} ({OneClickRig.Safe(collectionName)})";
        var modDir = Path.Combine(root, modName);
        var riggedDir = Path.Combine(root, $"{OneClickRig.ModelsModName} ({OneClickRig.Safe(collectionName)})");
        var unshapedDir = UnshapedDir(collectionName);

        // Each race's skeleton at rest (models are in their own race's space; the game fits them to yours).
        var rests = new Dictionary<string, Dictionary<string, (Vector3 Position, Quaternion Rotation, Vector3 Scale, string Parent)>?>();
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        int count = 0;
        foreach (var (actual, games) in paths)
        {
            foreach (var game in games.Where(g => g.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase) && g.StartsWith("chara/", StringComparison.OrdinalIgnoreCase)))
            {
                var code = OneClickRig.RaceCode(game);
                if (code == null)
                    continue;
                if (!rests.TryGetValue(code, out var rest))
                    rests[code] = rest = await OneClickRig.Game(() => RigBuilder.RestPose(code));
                if (rest == null)
                    continue;
                var source = await ReadUnshaped(actual, game, modDir, riggedDir, unshapedDir);
                if (source == null)
                    continue;
                byte[]? shaped;
                int moved;
                try
                {
                    shaped = Bake(source, values, rest, out moved);
                }
                catch (Exception e)
                {
                    progress.Add($"  {Path.GetFileName(game)}: couldn't be read ({e.Message}), skipped.");
                    continue;
                }
                // Nothing to change on this one: only put back as it was if an earlier build changed it.
                if (shaped != null)
                    files[game] = shaped;
                else if (File.Exists(Path.Combine(modDir, game.Replace('/', Path.DirectorySeparatorChar))))
                    files[game] = source;
                if (moved > 0)
                {
                    count++;
                    progress.Add($"  {Path.GetFileName(game)}: {moved} vertices reshaped.");
                }
            }
        }
        if (files.Count == 0)
        {
            progress.Add("No worn model could be reshaped.");
            return null;
        }
        await OneClickRig.PutMod(root, modName, files,
            $"The Body shape page's shape built into the models worn in the \"{collectionName}\" collection (made by Limber from those models; your own mods aren't changed). Build again from Limber's Body shape page after changing them.",
            progress, keepEarlier: true);
        int on = await OneClickRig.Game(() => Plugin.PluginInterface.GetIpcSubscriber<Guid, string, string, bool, int>("Penumbra.TrySetMod.V5")
            .InvokeFunc(collectionId, modName, "", true));
        int at = await OneClickRig.Game(() => Plugin.PluginInterface.GetIpcSubscriber<Guid, string, string, int, int>("Penumbra.TrySetModPriority.V5")
            .InvokeFunc(collectionId, modName, "", Priority));
        if (on is not (0 or 1) || at is not (0 or 1))
            progress.Add($"  Couldn't turn on \"{modName}\" in {collectionName} (Penumbra said {on}/{at}); turn it on there yourself, at the top.");
        await OneClickRig.Game(() =>
        {
            Plugin.PluginInterface.GetIpcSubscriber<int, int, object?>("Penumbra.RedrawObject.V5").InvokeAction(index, 0);
            return true;
        });
        progress.Add($"Done: {count} model(s) reshaped.");
        return collectionName;
    }

    // The model as it is without a built shape: Limber's rigged copy or your own mod's (saved, for building again);
    // for this mod's own copy, the one saved when it was built (or the rigged copy, when that's newer: rigged again).
    private static async Task<byte[]?> ReadUnshaped(string actual, string game, string modDir, string riggedDir, string unshapedDir)
    {
        var path = actual;
        if (path.StartsWith('|'))
        {
            int end = path.IndexOf('|', 1);
            if (end > 0)
                path = path[(end + 1)..];
        }
        var saved = Path.Combine(unshapedDir, game.Replace('/', Path.DirectorySeparatorChar));
        if (Path.IsPathRooted(path))
        {
            var full = Path.GetFullPath(path);
            var relative = Path.GetRelativePath(Path.GetFullPath(modDir), full);
            if (!relative.StartsWith("..", StringComparison.Ordinal))
            {
                var rigged = Path.Combine(riggedDir, game.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(rigged) && (!File.Exists(saved) || File.GetLastWriteTimeUtc(rigged) > File.GetLastWriteTimeUtc(saved)))
                {
                    var bytes = await File.ReadAllBytesAsync(rigged);
                    await Save(saved, bytes);
                    return bytes;
                }
                return File.Exists(saved) ? await File.ReadAllBytesAsync(saved) : null;
            }
            if (!File.Exists(full))
                return null;
            var data = await File.ReadAllBytesAsync(full);
            await Save(saved, data);
            return data;
        }
        var game0 = await OneClickRig.Game(() => Plugin.DataManager.GetFile(path)?.Data);
        if (game0 != null)
            await Save(saved, game0);
        return game0;
    }

    private static async Task Save(string path, byte[] data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, data);
    }

    /// <summary>
    /// The shape baked into one model: each vertex moved by what the sliders do to each bone it's weighted to (and to
    /// the bones above that carry it), blended by its weights, exactly as the live preview moves it.
    /// </summary>
    public static byte[]? Bake(byte[] model, IReadOnlyDictionary<string, float> values,
        Dictionary<string, (Vector3 Position, Quaternion Rotation, Vector3 Scale, string Parent)> rest, out int moved)
    {
        BoneFrame? Rest(string name) => rest.TryGetValue(name, out var r) ? new BoneFrame(r.Position, r.Rotation, r.Scale) : null;
        var ops = ShapeOps.Resolve(values, Rest, s => !s.LiveOnly);
        var sculpts = Sculpts(values, rest);
        moved = 0;
        if (ops.Count == 0 && sculpts.Count == 0)
            return null;

        // The body bone each bone's skin belongs to: itself, or for Limber's rig bones the body bone they hang from.
        var owners = new Dictionary<string, string>();
        string Owner(string bone)
        {
            if (owners.TryGetValue(bone, out var known))
                return known;
            string at = bone;
            for (int guard = 0; Limber.BodyRig.RigParts.IsRigBone(at) && guard < 64; guard++)
                at = rest.TryGetValue(at, out var r) && r.Parent.Length > 0 ? r.Parent : at;
            return owners[bone] = at;
        }

        // For each bone: the changes that move it, its own first, then each one above that carries it.
        var chains = new Dictionary<string, (ShapeOp Op, BoneFrame At)[]>();
        (ShapeOp, BoneFrame)[] Chain(string bone)
        {
            if (chains.TryGetValue(bone, out var known))
                return known;
            var list = new List<(ShapeOp, BoneFrame)>();
            if (ops.TryGetValue(bone, out var own) && Rest(bone) is { } r0)
                list.Add((own, r0));
            // A bone above changes it unless the child it comes through is one that bone keeps as it was (then only
            // that bone's own change is held off; the ones above it still carry it, as live).
            string child = bone;
            string parent = rest.TryGetValue(bone, out var b) ? b.Parent : "";
            for (int guard = 0; parent.Length > 0 && guard < 256; guard++)
            {
                if (ops.TryGetValue(parent, out var op) && !op.Keeps(child) && Rest(parent) is { } r)
                    list.Add((op, r));
                child = parent;
                parent = rest.TryGetValue(parent, out var pb) ? pb.Parent : "";
            }
            return chains[bone] = list.ToArray();
        }

        return MdlRigger.Reshape(model, (p, names, idx, w, slots) => KeepSide(p, Shaped(p, names, idx, w, slots)), out moved);

        Vector3 Shaped(Vector3 p, string[] names, byte[] idx, float[] w, int slots)
        {
            // The detail sculpts first (on the body as it is at rest), then the bone changes carry the result.
            if (sculpts.Count > 0)
            {
                var push = Vector3.Zero;
                foreach (var sc in sculpts)
                {
                    float carried = 0f;
                    for (int k = 0; k < slots; k++)
                        if (w[k] > 0f && idx[k] < names.Length && Owner(names[idx[k]]) == sc.Bone)
                            carried += w[k];
                    if (carried > 1e-4f)
                        push += sc.Push(p) * MathF.Min(carried, 1f);
                }
                p += push;
            }
            if (ops.Count == 0)
                return p;
            var sum = Vector3.Zero;
            float total = 0f;
            for (int k = 0; k < slots; k++)
            {
                if (w[k] <= 0f || idx[k] >= names.Length)
                    continue;
                var q = p;
                foreach (var (op, r) in Chain(names[idx[k]]))
                    q = op.Apply(q, r);
                sum += q * w[k];
                total += w[k];
            }
            if (total <= 1e-6f)
                return p;
            return sum + p * MathF.Max(0f, 1f - total);
        }
    }

    // A move toward the middle of the body (in x) eased off the nearer it gets, so the skin between the legs is
    // squeezed toward the middle but never pushed across it (left and right sides crossing: folded skin).
    private static Vector3 KeepSide(Vector3 from, Vector3 to)
    {
        float side = MathF.Abs(from.X);
        if (side < 1e-4f)
            return to;
        float sign = MathF.Sign(from.X);
        float inward = side - sign * to.X;
        if (inward <= 0f)
            return to;
        to.X = sign * side * MathF.Exp(-inward / side);
        return to;
    }

    /// <summary>One detail sculpt on one bone (one side), ready to use: where its line is, and how much.</summary>
    private sealed class Sculpt
    {
        public required string Bone;
        public Vector3 A, U;          // the line: from A along U (unit); a limb's length L
        public float L;
        public bool Heights;
        public Vector3 Lobe;          // body axes, x outward for this side (zero: all round)
        public float Side;            // +1 / -1 for a side bone; 0 for the trunk (outward by where the vertex is)
        public float From, To, Amount;

        // How far straight out from the line it pushes this point.
        public Vector3 Push(Vector3 p)
        {
            var d = p - A;
            float along = Vector3.Dot(d, U);
            var radial = d - U * along;
            float r = radial.Length();
            if (r < 1e-5f)
                return Vector3.Zero;
            var dir = radial / r;
            // Over its stretch, fading at both ends.
            float at = Heights ? p.Y - A.Y : (L > 1e-5f ? along / L : 0f);
            float edge = Heights ? 0.03f : 0.15f;
            float window = Smooth((at - From) / edge + 1f) * Smooth((To - at) / edge + 1f);
            if (window <= 0f)
                return Vector3.Zero;
            // Most where the lobe points.
            float lobe = 1f;
            if (Lobe != Vector3.Zero)
            {
                float side = Side != 0f ? Side : (p.X >= 0f ? 1f : -1f);
                var l = new Vector3(Lobe.X * side, Lobe.Y, Lobe.Z);
                l -= U * Vector3.Dot(l, U);
                if (l.LengthSquared() > 1e-8f)
                {
                    float c = MathF.Max(0f, Vector3.Dot(dir, Vector3.Normalize(l)));
                    lobe = c * c;
                }
            }
            float amount = Amount * window * lobe;
            // Thinner only so far: never through its own line.
            amount = MathF.Max(amount, -0.85f * r);
            return dir * amount;
        }

        private static float Smooth(float x)
        {
            x = Math.Clamp(x * 0.5f, 0f, 1f);
            return x * x * (3f - 2f * x);
        }
    }

    // The detail sliders that are set, as sculpts on this race's bones at rest.
    private static List<Sculpt> Sculpts(IReadOnlyDictionary<string, float> values,
        Dictionary<string, (Vector3 Position, Quaternion Rotation, Vector3 Scale, string Parent)> rest)
    {
        var list = new List<Sculpt>();
        foreach (var (key, raw) in values)
        {
            if (!BodySliders.ByKey.TryGetValue(key, out var slider) || slider.Morphs.Length == 0 || !float.IsFinite(raw) || raw == 0f)
                continue;
            float v = Math.Clamp(raw, slider.Min, slider.Max);
            foreach (var m in slider.Morphs)
                foreach (bool left in BodySliders.HasSides(m.Bone) ? new[] { true, false } : new[] { true })
                {
                    string bone = BodySliders.ForSide(m.Bone, left);
                    if (!rest.TryGetValue(bone, out var b))
                        continue;
                    var u = Vector3.UnitY;
                    float length = 0f;
                    if (!m.Heights && m.Next != null && rest.TryGetValue(BodySliders.ForSide(m.Next, left), out var n))
                    {
                        var d = n.Position - b.Position;
                        length = d.Length();
                        if (length > 1e-5f)
                            u = d / length;
                    }
                    list.Add(new Sculpt
                    {
                        Bone = bone, A = b.Position, U = u, L = length, Heights = m.Heights || m.Next == null, Lobe = m.Lobe,
                        Side = BodySliders.HasSides(m.Bone) ? (b.Position.X >= 0f ? 1f : -1f) : 0f,
                        From = m.From, To = m.To, Amount = m.Amount * v,
                    });
                }
        }
        return list;
    }
}
