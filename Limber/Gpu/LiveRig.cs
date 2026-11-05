using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Limber.BodyRig;
using Limber.Core;

namespace Limber.Gpu;

/// <summary>
/// The live rig: soft body physics and the Body Shape tab's detail sliders on a character with no redraw, no reload and
/// no mods to build. The rig mod's way needs the game to load new files (lattice bones in the skeleton, models weighted
/// to them), which only happens when the character is drawn again. Here nothing is loaded:
/// <list type="bullet">
/// <item>the lattice bones are Limber's own (<see cref="VirtualRig"/>), placed on the skeleton the character has now
/// and moved by the soft bodies like real ones;</item>
/// <item>every model the character wears is weighted onto them in memory (the same weighting the rig mod bakes);</item>
/// <item>each frame, every vertex that's on the lattice is moved on the GPU so the game's own skinning draws it where
/// the lattice puts it (its rest spot changed by how far the lattice moved it from the bones it really has), and the
/// detail sliders' sculpts (blend shapes) are written into the vertices once whenever they change.</item>
/// </list>
/// The game keeps one copy of a model's vertices for everyone wearing the same file, so a model goes to one character
/// at a time (the one chosen at the top first). Only this game client's own memory and GPU are touched.
/// </summary>
public static unsafe class LiveRig
{
    // ---- Set by the plugin

    /// <summary>The live rig is on (its setting), and Limber runs here (GPose, or outside it when allowed).</summary>
    public static Func<bool> On = () => false;
    /// <summary>Where each part's box goes (the Rigging tab's placements).</summary>
    public static Func<IReadOnlyDictionary<string, BreastRigShape>> Placements = () => new Dictionary<string, BreastRigShape>();
    /// <summary>Fit the boxes to the worn models (the Rigging tab's "Fit the boxes" setting).</summary>
    public static Func<bool> AutoFit = () => true;
    public static Func<string, byte[]?> ReadFile = _ => null;
    /// <summary>Separate wobbles: characters wearing a model someone else has get their own copy of it (experimental).</summary>
    public static Func<bool> Separate = () => false;
    /// <summary>A part's box is wanted now (by part key): one nothing uses (the belly, until it's soft or grown) isn't
    /// put on the models at all, so its vertices cost nothing.</summary>
    public static Func<string, bool> PartWanted = _ => true;

    /// <summary>One character the live rig is wanted on, and what for.</summary>
    public readonly record struct Want(nint Address, ushort Index, bool Main, bool Physics,
        IReadOnlyDictionary<string, float>? Sculpt, IReadOnlyDictionary<string, float>? Built);

    /// <summary>Set from the framework thread.</summary>
    public static volatile Want[] Wanted = [];

    /// <summary>What it's doing, for the pages (any thread).</summary>
    public static volatile string Status = "";

    // ---- What's prepared for each character

    private sealed class Person
    {
        public required nint Address;
        public Want Want;
        public string Key = "";           // what the plan in hand is for
        public string MakingKey = "";
        public Task<Plan?>? Making;
        public volatile Plan? Plan;
        public volatile Snapshot? Snapshot;
        public long NextLook;
        public string Report = "";
    }

    private sealed class Plan
    {
        public required SkeletonInfo Info;
        public VirtualRig? Rig;
        public required int[] RealBone;           // per slot: the skeleton's bone (-1: one of the rig's)
        public required int[] VirtualBone;        // per slot: the rig's bone (-1: the skeleton's)
        public required Matrix4x4[] RestInverse;  // per slot (the skeleton's bones): its rest pose, inverted
        public required List<ModelPlan> Models;
        public required string Report;
    }

    private sealed class ModelPlan
    {
        public required string File;
        public required byte[] Original;   // the vertex block (LoD 0) as the file has it: what the GPU has
        public required byte[] Base;       // with the sculpts in: what's shown when nothing moves
        public required MeshPlan[] Meshes; // the meshes with vertices on the lattice
        public (int From, int To)[] Ranges = []; // the bytes those vertices' positions and normals span (written each frame)
        public bool Changed;               // Base differs from Original
        public int Moving => Meshes.Sum(m => m.Vertex.Length);
    }

    private sealed class MeshPlan
    {
        public required MdlRigger.MeshLayout Layout;
        public required int[] Vertex;      // its vertices on the lattice
        public required Vector3[] Rest;    // where each is at rest (sculpts in)
        public required Vector3[] Normal;  // its normal at rest
        // Each vertex's weights: its own (as the model has them) from Start[i] to Mid[i], and on the lattice from Mid[i]
        // to Start[i + 1], as (snapshot slot, weight).
        public required int[] Start;
        public required int[] Mid;
        public required int[] Slot;
        public required float[] Weight;
        public (int From, int To)[] Ranges = [];
        public Vector3[] Last = [];
        // How much of each vertex is on the lattice, and how fast that changes over the surface (per metre): where a
        // part fades into the body (the belly's oval), moving the lattice tilts the surface, and the normal is tilted
        // to match (else a grown belly is lit as if it were still flat).
        public float[] Share = [];
        public Vector3[] Grad = [];
    }

    // Byte ranges in order, ones close together joined (fewer, slightly bigger writes).
    private static (int From, int To)[] MergeRanges(List<(int From, int To)> ranges)
    {
        var sorted = ranges.Where(r => r.To > r.From && r.From != int.MaxValue).OrderBy(r => r.From).ToList();
        var merged = new List<(int From, int To)>();
        foreach (var r in sorted)
        {
            if (merged.Count > 0 && r.From <= merged[^1].To + 4096)
                merged[^1] = (merged[^1].From, Math.Max(merged[^1].To, r.To));
            else
                merged.Add(r);
        }
        return merged.ToArray();
    }

    private sealed class Snapshot
    {
        public required Plan Plan;
        public required Matrix4x4[] Skin;
    }

    private static readonly ConcurrentDictionary<nint, Person> People = new();

    /// <summary>The live rig is moving (or shaping) this character's models (any thread).</summary>
    public static bool IsOn(nint address) => People.TryGetValue(address, out var p) && p.Plan is { Models.Count: > 0 };

    /// <summary>The live rig has the lattice bones for this character (its soft bodies can run with no rig mod).</summary>
    public static bool HasRig(nint address) => People.TryGetValue(address, out var p) && p.Plan?.Rig != null;

    public static string ReportFor(nint address) => People.TryGetValue(address, out var p) ? p.Report : "";

    // ---- Bone update (after the soft bodies): plans started and finished, and this frame's bones taken for the GPU

    private static readonly SkeletonInfoCache Cache = new();

    public static void Capture()
    {
        if (People.IsEmpty)
            return;
        long now = Environment.TickCount64;
        foreach (var person in People.Values)
        {
            try
            {
                CaptureOne(person, now);
            }
            catch (Exception e)
            {
                person.Report = "Live rig: " + e.Message;
            }
        }
    }

    private static void CaptureOne(Person person, long now)
    {
        var want = person.Want;
        if (!ActorSkeleton.TryGet(person.Address, want.Index, Cache, out var sk) || sk.Info.Count == 0
            || !Limber.Koi.Safety.AdultAllowed(person.Address))
            return;

        // A plan that's finished: in use from now on.
        if (person.Making is { IsCompleted: true } making)
        {
            Plan? made = null;
            try
            {
                made = making.Result;
            }
            catch (Exception e)
            {
                person.Report = "Live rig: " + (e.GetBaseException().Message);
            }
            person.Making = null;
            person.Key = person.MakingKey;
            person.Plan = made;
            if (made != null)
                person.Report = made.Report;
            if (made?.Rig != null)
                VirtualRigs.Set(person.Address, made.Rig);
            else
                VirtualRigs.Remove(person.Address);
        }

        // What it needs now (looked at twice a second: the files it wears, its skeleton, the settings).
        if (now >= person.NextLook && person.Making == null)
        {
            person.NextLook = now + 500;
            var inputs = Inputs.Read(sk, want);
            if (inputs != null && inputs.Key != person.Key)
            {
                person.MakingKey = inputs.Key;
                person.Making = Task.Run(() => Make(inputs));
                person.Report = "Live rig: getting it ready (the game keeps running)...";
            }
        }

        // This frame's skinning matrices.
        var plan = person.Plan;
        if (plan == null || !ReferenceEquals(plan.Info, sk.Info) || plan.Models.Count == 0)
            return;
        var skin = new Matrix4x4[plan.RealBone.Length];
        for (int s = 0; s < skin.Length; s++)
        {
            int real = plan.RealBone[s];
            if (real >= 0)
            {
                var frame = sk.GetModelFrame(real);
                var scale = sk.GetModelScale(real);
                skin[s] = plan.RestInverse[s] * (Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(frame.Rotation)
                    * Matrix4x4.CreateTranslation(frame.Position));
            }
            else if (plan.Rig != null && plan.VirtualBone[s] >= 0)
                skin[s] = plan.Rig.Skinning(plan.VirtualBone[s], sk, now);
            else
                skin[s] = Matrix4x4.Identity;
        }
        person.Snapshot = new Snapshot { Plan = plan, Skin = skin };
    }

    // ---- What a plan is made from (read in the bone update, used in the background)

    private sealed class Inputs
    {
        public required SkeletonInfo Info;
        public required (Vector3 Position, Quaternion Rotation, Vector3 Scale)[] Rest; // the skeleton at rest, model space
        public required List<string> Files;
        public required Dictionary<string, BreastRigShape> Placements;
        public required bool Fit, Physics;
        public required HashSet<string> RealParts; // parts whose lattice the skeleton already has (the rig mod)
        public required RigPart[] Parts;           // the parts wanted now
        public required Dictionary<string, float> Sculpt, Built;
        public required string Key;

        public static Inputs? Read(in ActorSkeleton sk, Want want)
        {
            var obj = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)want.Address;
            if (obj == null || obj->DrawObject == null)
                return null;
            var files = LiveMesh.ModelFiles((nint)obj->DrawObject).Select(f => f.File).Where(Usable).Distinct().ToList();
            if (files.Count == 0)
                return null;
            var info = sk.Info;
            // The skeleton at rest (from its reference pose, parent by parent).
            var rest = new (Vector3, Quaternion, Vector3)[info.Count];
            var reference = sk.Pose->Skeleton->ReferencePose;
            for (int i = 0; i < info.Count; i++)
            {
                var local = (Vector3.Zero, Quaternion.Identity, Vector3.One);
                if (i < reference.Length)
                {
                    var t = reference[i];
                    local = (*(Vector3*)&t.Translation, Quaternion.Normalize(*(Quaternion*)&t.Rotation), *(Vector3*)&t.Scale);
                }
                int parent = info.Parents[i];
                if (parent >= 0 && parent < i)
                {
                    var (pp, pr, ps) = rest[parent];
                    rest[i] = (pp + Vector3.Transform(ps * local.Item1, pr), Quaternion.Normalize(pr * local.Item2), ps * local.Item3);
                }
                else
                    rest[i] = local;
            }
            var real = new HashSet<string>(RigParts.All.Where(p => info.IndexOf(p.BoneName(true, 0)) >= 0).Select(p => p.Key));
            var parts = RigParts.All.Where(p => PartWanted(p.Key)).ToArray();
            var placements = new Dictionary<string, BreastRigShape>();
            foreach (var (k, v) in Placements())
                placements[k] = v.Clone();
            // Only the detail sliders (the rest of the Body Shape tab is done on the bones, live).
            static Dictionary<string, float> Sculpts(IReadOnlyDictionary<string, float>? values)
            {
                var d = new Dictionary<string, float>();
                if (values != null)
                    foreach (var (k, v) in values)
                        if (v != 0f && float.IsFinite(v) && Limber.BodyShape.BodySliders.ByKey.TryGetValue(k, out var s) && s.Morphs.Length > 0)
                            d[k] = v;
                return d;
            }
            var sculpt = Sculpts(want.Sculpt);
            var built = Sculpts(want.Built);
            bool fit = AutoFit();
            static string Of(Dictionary<string, float> d) => string.Join(",", d.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value:0.###}"));
            string key = string.Join("|",
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(info), info.Count,
                string.Join(";", files.OrderBy(f => f, StringComparer.Ordinal)),
                want.Physics ? "p" : "-", fit ? "f" : "-", string.Join(",", real.OrderBy(x => x)), string.Join(",", parts.Select(p => p.Key)),
                string.Join(";", placements.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + ":" + ShapeKey(kv.Value))),
                Of(sculpt), Of(built));
            return new Inputs
            {
                Info = info, Rest = rest, Files = files, Placements = placements, Fit = fit, Physics = want.Physics, RealParts = real, Parts = parts,
                Sculpt = sculpt, Built = built, Key = key,
            };
        }
    }

    private static string ShapeKey(BreastRigShape s)
        => $"{s.Size:0.####},{s.Height:0.####},{s.Out:0.####},{s.Up:0.####},{s.Forward:0.####},{s.Depth:0.####},{s.HasNipple},{s.NippleOut:0.####},{s.NippleUp:0.####},{s.NippleForward:0.####}";

    // Models the live rig leaves alone: the face (the face rig and lips have it) and the hair.
    private static bool Usable(string file)
    {
        var f = file.Replace('\\', '/').ToLowerInvariant();
        if (!f.EndsWith(".mdl", StringComparison.Ordinal))
            return false;
        var name = f[(f.LastIndexOf('/') + 1)..];
        return !(f.Contains("/obj/face/") || f.Contains("/obj/hair/") || name.Contains("_fac") || name.Contains("_hir") || name.Contains("_zer"));
    }

    // ---- Making a plan (background)

    // The models as read, and as weighted onto a lattice (by what the lattice was), kept: making a plan again for a
    // slider change shouldn't read and weight every model again.
    private static readonly ConcurrentDictionary<string, byte[]?> FileCache = new();
    private static readonly ConcurrentDictionary<string, byte[]?> RigCache = new();

    private static byte[]? Read(string file)
    {
        string stamp = "";
        try
        {
            var path = file.StartsWith('|') && file.IndexOf('|', 1) > 0 ? file[(file.IndexOf('|', 1) + 1)..] : file;
            if (System.IO.Path.IsPathRooted(path) && System.IO.File.Exists(path))
            {
                var fi = new System.IO.FileInfo(path);
                stamp = fi.Length + "@" + fi.LastWriteTimeUtc.Ticks;
            }
        }
        catch
        {
        }
        if (FileCache.Count > 48)
            FileCache.Clear();
        return FileCache.GetOrAdd(file + "|" + stamp, _ => ReadFile(file));
    }

    private static Plan? Make(Inputs inp)
    {
        var info = inp.Info;
        var report = new List<string>();
        var models = new List<(string File, byte[] Data)>();
        foreach (var file in inp.Files)
            if (Read(file) is { } data)
                models.Add((file, data));
        if (models.Count == 0)
            return null;

        var rest = new Dictionary<string, (Vector3 Position, Quaternion Rotation, Vector3 Scale, string Parent)>(info.Count);
        for (int i = 0; i < info.Count; i++)
        {
            int p = info.Parents[i];
            rest[info.Names[i]] = (inp.Rest[i].Position, inp.Rest[i].Rotation, inp.Rest[i].Scale, p >= 0 && p < info.Count ? info.Names[p] : "");
        }
        Matrix4x4 RestModel(int i)
        {
            var (pos, rot, scale) = inp.Rest[i];
            return Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rot) * Matrix4x4.CreateTranslation(pos);
        }

        // 1. The lattices: where they go on this skeleton (fitted to the models), and which ones are Limber's own.
        VirtualRig? rig = null;
        var bones = new Dictionary<string, (Vector3 Position, string Parent)>();
        var sides = new List<MdlRigger.Side>();
        if (inp.Physics)
        {
            var shapes = inp.Placements;
            List<RigBuilder.PlacedBone> Place()
            {
                bones.Clear();
                return RigBuilder.Place(inp.Parts, shapes, info.IndexOf, i => inp.Rest[i], i => info.Names[i], bones, out _, out _);
            }
            var placed = Place();
            var own = inp.Parts.Where(p => !inp.RealParts.Contains(p.Key) && bones.ContainsKey(p.BoneName(true, 0))).ToArray();
            if (inp.Fit && own.Length > 0)
            {
                bool changed = false;
                var fitModels = models.Select(m => (m.File, "live", m.Data)).ToList();
                var skeletons = new Dictionary<string, Dictionary<string, (Vector3 Position, string Parent)>> { ["live"] = bones };
                foreach (var part in own)
                {
                    BreastRigShape? fitted = null;
                    try
                    {
                        fitted = OneClickRig.Fit(part, fitModels, skeletons);
                    }
                    catch
                    {
                    }
                    if (fitted != null)
                    {
                        shapes[part.Key] = fitted;
                        changed = true;
                    }
                }
                if (changed)
                    placed = Place();
            }
            // The parts the skeleton already has (the rig mod): where its own bones are.
            foreach (var part in RigParts.All.Where(p => inp.RealParts.Contains(p.Key)))
                foreach (var left in part.Single ? new[] { true } : new[] { true, false })
                {
                    var names = Enumerable.Range(0, part.PerSide).Select(i => part.BoneName(left, i)).ToList();
                    if (part.Nipples)
                        names.Add(part.NippleName(left));
                    foreach (var name in names)
                    {
                        if (rest.TryGetValue(name, out var r))
                            bones[name] = (r.Position, r.Parent);
                        else
                            bones.Remove(name);
                    }
                }
            var ownKeys = new HashSet<string>(own.Select(p => p.Key));
            var mine = placed.Where(b => own.Any(p => p.IsMine(b.Name))).ToList();
            if (mine.Count > 0)
                rig = new VirtualRig(info, mine, RestModel, ownKeys);
            var parts = RigParts.Rigged.Where(p => bones.ContainsKey(p.BoneName(true, 0))).ToArray();
            sides = OneClickRig.Sides(bones, parts, shapes);
            if (own.Length > 0)
                report.Add($"lattice bones for {string.Join(", ", own.Select(p => p.Label.ToLowerInvariant()))} (no rig mod needed)");
        }
        string sidesKey = string.Join(";", sides.Select(sd => sd.Bones[0] + "@" + string.Join(",", sd.Positions.Select(p => $"{p.X:0.####} {p.Y:0.####} {p.Z:0.####}"))
            + (sd.Nipple != null ? $"n{sd.NipplePosition.X:0.####} {sd.NipplePosition.Y:0.####} {sd.NipplePosition.Z:0.####}" : "")));

        // 2. The detail sliders' sculpts (less what's already built into the models).
        var sculpt = inp.Sculpt.Count > 0 ? Limber.BodyShape.BodyShapeBuild.SculptMove(inp.Sculpt, rest) : null;
        var builtSculpt = inp.Built.Count > 0 ? Limber.BodyShape.BodyShapeBuild.SculptMove(inp.Built, rest) : null;

        // 3. Each model: what changes in its vertices.
        var slots = new Dictionary<string, int>(StringComparer.Ordinal);
        var slotNames = new List<string>();
        int SlotOf(string name)
        {
            if (slots.TryGetValue(name, out var s))
                return s;
            bool known = info.IndexOf(name) >= 0 || (rig != null && rig.IndexOf(name) >= 0);
            if (!known)
                return -1;
            slots[name] = slotNames.Count;
            slotNames.Add(name);
            return slotNames.Count - 1;
        }

        var plans = new List<ModelPlan>();
        int movingTotal = 0, shapedTotal = 0;
        var idx = new byte[8];
        var w = new float[8];
        foreach (var (file, data) in models)
        {
            try
            {
                var plan = MakeModel(file, data, sides, sidesKey, sculpt, builtSculpt, SlotOf, idx, w, ref movingTotal, ref shapedTotal);
                if (plan != null)
                    plans.Add(plan);
            }
            catch (Exception e)
            {
                report.Add($"{ShortName(file)}: couldn't be read ({e.Message})");
            }
        }

        var realBone = new int[slotNames.Count];
        var virtualBone = new int[slotNames.Count];
        var restInverse = new Matrix4x4[slotNames.Count];
        for (int s = 0; s < slotNames.Count; s++)
        {
            realBone[s] = info.IndexOf(slotNames[s]);
            virtualBone[s] = realBone[s] < 0 && rig != null ? rig.IndexOf(slotNames[s]) : -1;
            restInverse[s] = Matrix4x4.Identity;
            if (realBone[s] >= 0 && !Matrix4x4.Invert(RestModel(realBone[s]), out restInverse[s]))
                restInverse[s] = Matrix4x4.Identity;
        }
        if (movingTotal > 0)
            report.Add($"{movingTotal} vertices on the lattice in {plans.Count(p => p.Moving > 0)} model(s)");
        if (shapedTotal > 0)
            report.Add($"{shapedTotal} vertices sculpted");
        if (plans.Count == 0 && inp.Physics && sides.Count == 0)
            report.Add("none of the worn models has breast, butt, testicle or belly bones Limber knows");
        return new Plan
        {
            Info = info, Rig = rig, RealBone = realBone, VirtualBone = virtualBone, RestInverse = restInverse, Models = plans,
            Report = "Live rig: " + (report.Count > 0 ? string.Join("; ", report) : "nothing to do on what's worn") + ".",
        };
    }

    private static string ShortName(string file) => System.IO.Path.GetFileName(file.Split('|').Last());

    private static ModelPlan? MakeModel(string file, byte[] data, List<MdlRigger.Side> sides, string sidesKey,
        Func<Vector3, string[], byte[], float[], int, Vector3>? sculpt, Func<Vector3, string[], byte[], float[], int, Vector3>? builtSculpt,
        Func<string, int> slotOf, byte[] idx, float[] w, ref int movingTotal, ref int shapedTotal)
    {
        var (offset, size, layouts) = MdlRigger.Layout(data);
        if (size[0] == 0 || offset[0] + size[0] > data.Length)
            return null;
        var original = data.AsSpan((int)offset[0], (int)size[0]).ToArray();
        var skins = MdlRigger.Skin(data, 0);
        // Already weighted onto Limber's lattice (the rig mod's copy): the skeleton moves those parts, nothing to do
        // for them here; a part it wasn't rigged for (the belly, on a copy rigged before there was one) still goes on.
        var have = skins.SelectMany(k => k.Bones).Where(RigParts.IsRigBone).ToHashSet(StringComparer.Ordinal);
        var todo = have.Count == 0 ? sides
            : sides.Where(sd => RigParts.All.FirstOrDefault(p => p.Key == sd.PartKey) is not { } part || !have.Any(part.IsMine)).ToList();
        List<MdlRigger.MeshSkin>? onLattice = null;
        if (todo.Count > 0)
        {
            var key = file + "|" + data.Length + "|" + sidesKey + "|" + string.Join(",", todo.Select(sd => sd.PartKey).Distinct());
            if (RigCache.Count > 32)
                RigCache.Clear();
            var weighted = RigCache.GetOrAdd(key, _ =>
            {
                try
                {
                    return MdlRigger.Rig(data, todo, new List<string>());
                }
                catch
                {
                    return null;
                }
            });
            if (weighted != null)
                onLattice = MdlRigger.Skin(weighted, 0);
        }
        if (onLattice == null && sculpt == null && builtSculpt == null)
            return null;

        var work = (byte[])original.Clone();
        bool changed = false;
        var meshes = new List<MeshPlan>();
        foreach (var layout in layouts.Where(l => l.Lod == 0))
        {
            var skin = skins.FirstOrDefault(k => k.Mesh == layout.Mesh);
            if (skin == null)
                continue;
            var rskin = onLattice?.FirstOrDefault(k => k.Mesh == layout.Mesh);
            bool[]? latticeBone = rskin?.Bones.Select(RigParts.IsRigBone).ToArray();
            var vertex = new List<int>();
            var restList = new List<Vector3>();
            var normals = new List<Vector3>();
            var start = new List<int>();
            var mid = new List<int>();
            var slot = new List<int>();
            var weight = new List<float>();
            var diff = new Dictionary<int, float>();
            var allPos = new Vector3[layout.VertexCount];
            var allShare = new float[layout.VertexCount];
            var shares = new List<float>();
            int posFrom = int.MaxValue, posTo = 0, nrmFrom = int.MaxValue, nrmTo = 0;
            for (int v = 0; v < layout.VertexCount; v++)
            {
                int pa = layout.PositionAt + v * layout.PositionStride;
                if (pa + 12 > original.Length)
                    break;
                var p = MdlRigger.ReadVector(original, pa, layout.PositionType);
                for (int k = 0; k < 8; k++)
                {
                    idx[k] = skin.Indices[v * 8 + k];
                    w[k] = skin.Weights[v * 8 + k];
                }
                // The sculpts (a blend shape: the same move for this vertex every frame).
                var d = (sculpt?.Invoke(p, skin.Bones, idx, w, skin.Slots) ?? Vector3.Zero)
                    - (builtSculpt?.Invoke(p, skin.Bones, idx, w, skin.Slots) ?? Vector3.Zero);
                if (d.LengthSquared() > 1e-12f && MathUtil.IsFinite(d))
                {
                    p += d;
                    MdlRigger.WriteVector(work, pa, layout.PositionType, p);
                    changed = true;
                    shapedTotal++;
                }

                allPos[v] = p;
                // On the lattice: weighted to its bones now.
                if (rskin == null || latticeBone == null)
                    continue;
                float lat = 0f;
                for (int k = 0; k < rskin.Slots; k++)
                    if (rskin.Weights[v * 8 + k] > 0f && rskin.Indices[v * 8 + k] < latticeBone.Length && latticeBone[rskin.Indices[v * 8 + k]])
                        lat += rskin.Weights[v * 8 + k];
                allShare[v] = lat;
                if (lat <= 0f)
                    continue;
                int s0 = slot.Count;
                bool ok = true;
                for (int k = 0; k < skin.Slots && ok; k++)
                {
                    float wk = skin.Weights[v * 8 + k];
                    int bone = skin.Indices[v * 8 + k];
                    if (wk <= 0f || bone >= skin.Bones.Length)
                        continue;
                    int sl = slotOf(skin.Bones[bone]);
                    ok = sl >= 0;
                    slot.Add(sl);
                    weight.Add(wk);
                }
                int m0 = slot.Count;
                for (int k = 0; k < rskin.Slots && ok; k++)
                {
                    float wk = rskin.Weights[v * 8 + k];
                    int bone = rskin.Indices[v * 8 + k];
                    if (wk <= 0f || bone >= rskin.Bones.Length)
                        continue;
                    int sl = slotOf(rskin.Bones[bone]);
                    ok = sl >= 0;
                    slot.Add(sl);
                    weight.Add(wk);
                }
                if (ok && m0 > s0 && slot.Count > m0)
                {
                    // Kept as the change of weights only (on the lattice, less its own): all that moves it, so the
                    // frame's work is a few bones per vertex, not two full blends.
                    diff.Clear();
                    // (the new weights scaled to add up to the same as its own: the bones' moves then cancel exactly
                    // where nothing changed, whatever the rounding of the weights in the file)
                    float own = 0f, now = 0f;
                    for (int k = s0; k < m0; k++)
                        own += weight[k];
                    for (int k = m0; k < slot.Count; k++)
                        now += weight[k];
                    float scale = now > 1e-6f ? own / now : 1f;
                    for (int k = m0; k < slot.Count; k++)
                        diff[slot[k]] = diff.GetValueOrDefault(slot[k]) + weight[k] * scale;
                    for (int k = s0; k < m0; k++)
                        diff[slot[k]] = diff.GetValueOrDefault(slot[k]) - weight[k];
                    slot.RemoveRange(m0, slot.Count - m0);
                    weight.RemoveRange(m0, weight.Count - m0);
                    foreach (var (sl, dw) in diff)
                        if (MathF.Abs(dw) > 1e-4f)
                        {
                            slot.Add(sl);
                            weight.Add(dw);
                        }
                }
                if (!ok || m0 == s0 || slot.Count == m0)
                {
                    // (a bone that isn't in the body skeleton, e.g. one of an outfit's own, or nothing changed: left as it is)
                    slot.RemoveRange(s0, slot.Count - s0);
                    weight.RemoveRange(s0, weight.Count - s0);
                    continue;
                }
                vertex.Add(v);
                restList.Add(p);
                shares.Add(lat);
                normals.Add(layout.NormalAt >= 0 ? MdlRigger.ReadVector(original, layout.NormalAt + v * layout.NormalStride, layout.NormalType) : Vector3.Zero);
                start.Add(s0);
                mid.Add(m0);
                posFrom = Math.Min(posFrom, pa);
                posTo = Math.Max(posTo, pa + 16);
                if (layout.NormalAt >= 0)
                {
                    int na = layout.NormalAt + v * layout.NormalStride;
                    nrmFrom = Math.Min(nrmFrom, na);
                    nrmTo = Math.Max(nrmTo, na + 16);
                }
            }
            if (vertex.Count == 0)
                continue;
            start.Add(slot.Count);
            meshes.Add(new MeshPlan
            {
                Layout = layout, Vertex = vertex.ToArray(), Rest = restList.ToArray(), Normal = normals.ToArray(),
                Start = start.ToArray(), Mid = mid.ToArray(), Slot = slot.ToArray(), Weight = weight.ToArray(),
                Ranges = MergeRanges([(posFrom, Math.Min(posTo, original.Length)), (nrmFrom, Math.Min(nrmTo, original.Length))]),
                Share = shares.ToArray(), Grad = ShareGradients(allPos, allShare, vertex, normals),
            });
            movingTotal += vertex.Count;
        }
        if (!changed && meshes.Count == 0)
            return null;
        return new ModelPlan
        {
            File = file, Original = original, Base = work, Meshes = meshes.ToArray(), Changed = changed,
            Ranges = MergeRanges(meshes.SelectMany(m => m.Ranges).ToList()),
        };
    }

    // ---- Every frame on the render thread: the vertices written

    private sealed class Target
    {
        public required string File;
        public required nint Owner;
        public required LiveMesh.Found Found;
        public nint Replacement;
        public required ModelPlan Model;
        public required byte[] Work;
        public bool BaseWritten;
        public Snapshot? Written;
    }

    // One per model file (the game shares a model's vertices between everyone wearing it).
    private static readonly Dictionary<string, Target> Targets = new();
    private static readonly Dictionary<string, long> NextTry = new();
    private static readonly Dictionary<string, int> Fails = new();

    public static int Count => Targets.Count;

    /// <summary>Anything to let go of (a character being prepared, or a model being written).</summary>
    public static bool Active => !People.IsEmpty || Targets.Count > 0 || VirtualRigs.Count > 0 || Copies.Count > 0;

    /// <summary>A model file the live rig is writing (render thread).</summary>
    public static bool Owns(string file) => Targets.ContainsKey(file);

    public static void Update()
    {
        bool on = On();
        var wanted = on ? Wanted : [];
        // Who: new ones added, ones no longer wanted let go of.
        var keep = new HashSet<nint>();
        foreach (var want in wanted)
        {
            if (want.Address == 0 || LiveSoftBody.IsOn(want.Address)) // (the experimental GPU soft body has that model)
                continue;
            keep.Add(want.Address);
            var person = People.GetOrAdd(want.Address, a => new Person { Address = a });
            person.Want = want;
        }
        foreach (var (address, person) in People.ToArray())
            if (!keep.Contains(address))
            {
                People.TryRemove(address, out _);
                VirtualRigs.Remove(address);
            }

        // Each model file to one character (the main one first): it bends the game's own copy. Everyone else wearing it
        // gets a copy of their own (separate wobbles), when that's on.
        var claims = new Dictionary<string, (Person Person, Snapshot Snap, ModelPlan Model)>();
        var others = new List<(string File, Person Person, Snapshot Snap, ModelPlan Model)>();
        foreach (var person in People.Values.OrderByDescending(p => p.Want.Main))
        {
            var snap = person.Snapshot;
            if (snap == null || !ReferenceEquals(snap.Plan, person.Plan))
                continue;
            foreach (var model in snap.Plan.Models)
            {
                if (BustShapes.Owns(model.File))
                    continue;
                if (!claims.ContainsKey(model.File))
                    claims[model.File] = (person, snap, model);
                else
                    others.Add((model.File, person, snap, model));
            }
        }
        foreach (var file in Targets.Keys.ToList())
            if (!claims.TryGetValue(file, out var c) || c.Person.Address != Targets[file].Owner)
                Drop(file);

        long now = Environment.TickCount64;
        int moving = 0;
        foreach (var (file, (person, snap, model)) in claims)
        {
            try
            {
                if (!Targets.TryGetValue(file, out var target))
                {
                    if (NextTry.TryGetValue(file, out var when) && now < when)
                        continue;
                    NextTry[file] = now + 2000L * (1 + Math.Min(Fails.GetValueOrDefault(file), 5));
                    target = Start(person.Address, file, model);
                    if (target == null)
                    {
                        Fails[file] = Fails.GetValueOrDefault(file) + 1;
                        continue;
                    }
                    Targets[file] = target;
                }
                var buffer = target.Replacement != 0 ? target.Replacement : target.Found.Buffer;
                if (!LiveMesh.StillHolds(target.Found, buffer))
                {
                    // Reloaded (an outfit change, a redraw): found again later.
                    Targets.Remove(file);
                    Fails[file] = Fails.GetValueOrDefault(file) + 1;
                    NextTry[file] = now + 1000L * Math.Min(Fails[file], 6);
                    continue;
                }
                Fails.Remove(file);
                // A new plan for the same model (a slider change): its new rest shape.
                if (!ReferenceEquals(target.Model, model))
                {
                    if (model.Original.Length != target.Work.Length)
                    {
                        Drop(file);
                        continue;
                    }
                    target.Model = model;
                    target.BaseWritten = false;
                }
                if (!target.BaseWritten)
                {
                    Buffer.BlockCopy(model.Base, 0, target.Work, 0, model.Base.Length);
                    foreach (var mesh in model.Meshes)
                        mesh.Last = [];
                    Deform(model, target.Work, snap);
                    LiveMesh.Upload(buffer, target.Found.Desc, target.Work);
                    target.BaseWritten = true;
                    target.Written = snap;
                }
                else if (model.Meshes.Length > 0 && !ReferenceEquals(target.Written, snap))
                {
                    if (Deform(model, target.Work, snap))
                        UploadRanges(buffer, target.Found.Desc, target.Work, model.Ranges);
                    target.Written = snap;
                }
                moving += model.Moving;
            }
            catch (Exception e)
            {
                Status = "Live rig: " + e.Message;
            }
        }
        UpdateCopies(others, now);
        if (Targets.Count > 0)
            Status = $"Live rig: on {Targets.Values.Select(t => t.Owner).Concat(Copies.Values.Select(c => c.Owner)).Distinct().Count()} character(s), "
                + $"{Targets.Count + Copies.Count} model(s), {moving} moving vertices"
                + (Copies.Count > 0 ? $"; {Copies.Count} own cop{(Copies.Count == 1 ? "y" : "ies")} (draws redirected: {LiveRigInstances.Swapped} of {LiveRigInstances.Seen} looked at)" : "")
                + ".";
        else if (People.IsEmpty)
            Status = "";
    }

    // ---- Separate wobbles: a character's own copy of a model someone else is bending

    private sealed class Copy
    {
        public required string File;
        public required nint Owner;
        public required ModelPlan Model;
        public required nint Buffer;           // its own vertex buffer
        public required LiveMesh.BufferDesc Desc;
        public required byte[] Work;
        public required nint RenderModel;      // its Render::Model wearing the file (its draws go to Buffer)
        public required int Slot;
        public bool BaseWritten;
        public Snapshot? Written;
    }

    private static readonly Dictionary<(string File, nint Owner), Copy> Copies = new();
    // Buffers let go of, freed a moment later (the game may still have draws recorded with them).
    private static readonly List<(nint Buffer, long When)> ToFree = new();
    private static readonly Dictionary<(string, nint), long> NextCopy = new();
    private static long _redirectKey;

    public static string SeparateProblem => LiveRigInstances.Problem;

    private static void UpdateCopies(List<(string File, Person Person, Snapshot Snap, ModelPlan Model)> others, long now)
    {
        bool separate = Separate() && others.Count > 0 && LiveRigInstances.Ensure();
        var wanted = new HashSet<(string, nint)>();
        if (separate)
            foreach (var (file, person, snap, model) in others)
            {
                var key = (file, person.Address);
                // (only next to a model the main character bends: the copy starts from it and swaps in for it)
                if (!Targets.TryGetValue(file, out var shared))
                    continue;
                wanted.Add(key);
                try
                {
                    if (!Copies.TryGetValue(key, out var copy))
                    {
                        if (NextCopy.TryGetValue(key, out var when) && now < when)
                            continue;
                        NextCopy[key] = now + 3000;
                        copy = MakeCopy(file, person.Address, model, shared);
                        if (copy == null)
                            continue;
                        Copies[key] = copy;
                    }
                    // Its model still there and still this file (else let go of, made again later).
                    if (!StillWears(copy))
                    {
                        DropCopy(key);
                        continue;
                    }
                    if (!ReferenceEquals(copy.Model, model))
                    {
                        if (model.Original.Length != copy.Work.Length)
                        {
                            DropCopy(key);
                            continue;
                        }
                        copy.Model = model;
                        copy.BaseWritten = false;
                    }
                    if (!copy.BaseWritten)
                    {
                        Buffer.BlockCopy(model.Base, 0, copy.Work, 0, model.Base.Length);
                        foreach (var mesh in model.Meshes)
                            mesh.Last = [];
                        Deform(model, copy.Work, snap);
                        LiveMesh.Upload(copy.Buffer, copy.Desc, copy.Work);
                        copy.BaseWritten = true;
                        copy.Written = snap;
                    }
                    else if (model.Meshes.Length > 0 && !ReferenceEquals(copy.Written, snap))
                    {
                        if (Deform(model, copy.Work, snap))
                            UploadRanges(copy.Buffer, copy.Desc, copy.Work, model.Ranges);
                        copy.Written = snap;
                    }
                }
                catch (Exception e)
                {
                    Status = "Live rig: " + e.Message;
                }
            }
        foreach (var key in Copies.Keys.ToList())
            if (!wanted.Contains(key))
                DropCopy(key);

        // Which draws go where: each copy's model, from the buffer everyone shares to its own (handed over only when
        // that changes).
        long redirectKey = 17;
        foreach (var copy in Copies.Values)
            if (copy.BaseWritten && Targets.TryGetValue(copy.File, out var shared))
                redirectKey = redirectKey * 31 + ((long)copy.RenderModel ^ ((long)(shared.Replacement != 0 ? shared.Replacement : shared.Found.Buffer) << 1) ^ ((long)copy.Buffer << 2));
        if (redirectKey != _redirectKey)
        {
            _redirectKey = redirectKey;
            var redirects = new Dictionary<nint, (nint Shared, nint Own)[]>();
            foreach (var copy in Copies.Values)
            {
                if (!copy.BaseWritten || !Targets.TryGetValue(copy.File, out var shared))
                    continue;
                var from = shared.Replacement != 0 ? shared.Replacement : shared.Found.Buffer;
                Add(redirects, copy.RenderModel, (from, copy.Buffer));
            }
            LiveRigInstances.Set(redirects);
        }

        // Buffers let go of a moment ago: freed now.
        for (int i = ToFree.Count - 1; i >= 0; i--)
            if (now - ToFree[i].When > 1000)
            {
                LiveMesh.Free(ToFree[i].Buffer);
                ToFree.RemoveAt(i);
            }
    }

    private static void Add(Dictionary<nint, (nint Shared, nint Own)[]> redirects, nint model, (nint Shared, nint Own) entry)
    {
        if (redirects.TryGetValue(model, out var list))
        {
            var longer = new (nint Shared, nint Own)[list.Length + 1];
            list.CopyTo(longer, 0);
            longer[^1] = entry;
            redirects[model] = longer;
        }
        else
            redirects[model] = new[] { entry };
    }

    private static Copy? MakeCopy(string file, nint owner, ModelPlan model, Target shared)
    {
        var obj = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)owner;
        if (obj == null || obj->DrawObject == null || shared.Work.Length != model.Original.Length)
            return null;
        var drawObject = (nint)obj->DrawObject;
        int slot = -1;
        foreach (var (s, f) in LiveMesh.ModelFiles(drawObject))
            if (f == file)
            {
                slot = s;
                break;
            }
        var renderModel = ModelIn(drawObject, slot);
        if (renderModel == 0)
            return null;
        var buffer = LiveMesh.CreateCopy(shared.Found.Desc, model.Base);
        if (buffer == 0)
            return null;
        var desc = shared.Found.Desc;
        desc.Usage = 0;
        desc.CPUAccessFlags = 0;
        return new Copy
        {
            File = file, Owner = owner, Model = model, Buffer = buffer, Desc = desc, Work = (byte[])model.Base.Clone(),
            RenderModel = renderModel, Slot = slot,
        };
    }

    // A character's Render::Model in a slot (0 if none).
    private static nint ModelIn(nint drawObject, int slot)
    {
        var cb = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase*)drawObject;
        if (cb == null || slot < 0 || slot >= cb->SlotCount || cb->Models == null)
            return 0;
        return (nint)cb->Models[slot];
    }

    private static bool StillWears(Copy copy)
    {
        var obj = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)copy.Owner;
        return obj != null && obj->DrawObject != null && ModelIn((nint)obj->DrawObject, copy.Slot) == copy.RenderModel;
    }

    private static void DropCopy((string, nint) key)
    {
        if (!Copies.Remove(key, out var copy))
            return;
        // (no more draws sent to it from now on; freed once the ones already recorded have run)
        var redirects = new Dictionary<nint, (nint Shared, nint Own)[]>();
        foreach (var c in Copies.Values)
            if (c.BaseWritten && Targets.TryGetValue(c.File, out var shared))
            {
                var entry = (shared.Replacement != 0 ? shared.Replacement : shared.Found.Buffer, c.Buffer);
                Add(redirects, c.RenderModel, entry);
            }
        LiveRigInstances.Set(redirects);
        _redirectKey = 0;
        ToFree.Add((copy.Buffer, Environment.TickCount64));
    }

    private static Target? Start(nint owner, string file, ModelPlan model)
    {
        var obj = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)owner;
        if (obj == null || obj->DrawObject == null)
            return null;
        var log = new List<string>();
        foreach (var found in LiveMesh.Probe((nint)obj->DrawObject, log, f => f == file))
        {
            if (!found.IsVertex || found.Desc.ByteWidth != model.Original.Length)
                continue;
            nint replacement = 0;
            if (found.Desc.Usage == 1) // immutable: an identical writable buffer in its place
            {
                replacement = LiveMesh.Replace(found, model.Original);
                if (replacement == 0)
                    continue;
            }
            return new Target
            {
                File = file, Owner = owner, Found = found, Replacement = replacement, Model = model, Work = (byte[])model.Base.Clone(),
            };
        }
        return null;
    }

    private static void Drop(string file)
    {
        if (!Targets.Remove(file, out var target))
            return;
        try
        {
            if (target.Replacement != 0)
                LiveMesh.Restore(target.Found, target.Replacement);
            else if (LiveMesh.StillHolds(target.Found, target.Found.Buffer))
                LiveMesh.Upload(target.Found.Buffer, target.Found.Desc, target.Model.Original);
        }
        catch
        {
            // the model is gone already
        }
    }

    /// <summary>Every model back as its file has it, and the live rig's bones gone.</summary>
    public static void Stop()
    {
        foreach (var key in Copies.Keys.ToList())
            DropCopy(key);
        LiveRigInstances.Set(new());
        NextCopy.Clear();
        foreach (var file in Targets.Keys.ToList())
            Drop(file);
        NextTry.Clear();
        Fails.Clear();
        People.Clear();
        VirtualRigs.Clear();
        Status = "";
    }

    /// <summary>
    /// The lattice's vertices where this frame's bones put them: each one's rest spot moved so that the game, skinning
    /// it with the bones the model really has, draws it where the lattice (and the rest of its new weights) would.
    /// With S its own bones' blend and Δ the change its new weights make (on the lattice, less its own), it's drawn at
    /// p·S + p·Δ, so its rest spot goes to p + (p·Δ)·S⁻¹. Spread over the cores for big models. False if nothing moved.
    /// </summary>
    private static bool Deform(ModelPlan model, byte[] work, Snapshot snap)
    {
        var m = snap.Skin;
        // Each bone's own inverse (the turn and size only), once a frame: most vertices are on one bone of their own.
        var inverse = new Matrix4x4[m.Length];
        var hasInverse = new bool[m.Length];
        for (int s = 0; s < m.Length; s++)
        {
            var linear = m[s];
            linear.M41 = linear.M42 = linear.M43 = 0f;
            hasInverse[s] = Matrix4x4.Invert(linear, out inverse[s]);
        }
        int moved = 0;
        foreach (var mesh in model.Meshes)
        {
            if (mesh.Last.Length != mesh.Vertex.Length)
            {
                mesh.Last = new Vector3[mesh.Vertex.Length];
                Array.Fill(mesh.Last, new Vector3(float.NaN));
            }
            int count = mesh.Vertex.Length;
            const int Chunk = 2048;
            if (count <= Chunk)
                moved += DeformRange(mesh, work, m, inverse, hasInverse, 0, count);
            else
            {
                int chunks = (count + Chunk - 1) / Chunk;
                Parallel.For(0, chunks, c =>
                {
                    int n = DeformRange(mesh, work, m, inverse, hasInverse, c * Chunk, Math.Min(count, (c + 1) * Chunk));
                    if (n > 0)
                        System.Threading.Interlocked.Add(ref moved, n);
                });
            }
        }
        return moved > 0;
    }

    private static int DeformRange(MeshPlan mesh, byte[] work, Matrix4x4[] m, Matrix4x4[] inverse, bool[] hasInverse, int from, int to)
    {
        var L = mesh.Layout;
        bool normals = L.NormalAt >= 0 && L.NormalType is (2 or 3 or 14);
        int moved = 0;
        for (int i = from; i < to; i++)
        {
            int a = mesh.Start[i], b = mesh.Mid[i], c = mesh.Start[i + 1];
            var p = mesh.Rest[i];
            var n = mesh.Normal[i];
            // How far the new weights move it from where its own put it (model space), and how they turn its normal.
            var delta = Vector3.Zero;
            var turn = Vector3.Zero;
            for (int k = b; k < c; k++)
            {
                var bone = m[mesh.Slot[k]];
                float w = mesh.Weight[k];
                delta += Vector3.Transform(p, bone) * w;
                if (normals)
                    turn += Vector3.TransformNormal(n, bone) * w;
            }
            // Back through its own bones (their turn and size): where its rest spot must be.
            Matrix4x4 back;
            if (b - a == 1 && mesh.Weight[a] > 0.995f)
            {
                if (!hasInverse[mesh.Slot[a]])
                    continue;
                back = inverse[mesh.Slot[a]];
            }
            else
            {
                var blend = new Matrix4x4();
                float total = 0f;
                for (int k = a; k < b; k++)
                {
                    blend += m[mesh.Slot[k]] * mesh.Weight[k];
                    total += mesh.Weight[k];
                }
                if (total <= 1e-6f)
                    continue;
                blend *= 1f / total;
                blend.M41 = blend.M42 = blend.M43 = 0f;
                blend.M44 = 1f;
                if (!Matrix4x4.Invert(blend, out back))
                    continue;
            }
            var q = p + Vector3.TransformNormal(delta, back);
            if (!MathUtil.IsFinite(q) || Vector3.DistanceSquared(q, mesh.Last[i]) < 1e-12f)
                continue;
            mesh.Last[i] = q;
            moved++;
            int v = mesh.Vertex[i];
            MdlRigger.WriteVector(work, L.PositionAt + v * L.PositionStride, L.PositionType, q);
            if (normals && n.LengthSquared() > 1e-12f)
            {
                var nn = n + Vector3.TransformNormal(turn, back);
                // Where it fades into the body, the move tilts the surface (it moves this much times its share, and
                // the share changes along the surface): the normal tilts with it.
                if (i < mesh.Grad.Length)
                {
                    var g = mesh.Grad[i];
                    if (g.LengthSquared() > 1e-4f)
                    {
                        var u = (q - p) / MathF.Max(mesh.Share[i], 0.05f);
                        float gu = MathF.Max(1f + Vector3.Dot(g, u), 0.2f);
                        nn -= g * (Vector3.Dot(u, nn) / gu);
                    }
                }
                float len = nn.Length();
                if (len > 1e-6f)
                    MdlRigger.WriteVector(work, L.NormalAt + v * L.NormalStride, L.NormalType, nn / len);
            }
        }
        return moved;
    }

    /// <summary>
    /// How fast each lattice vertex's share of the lattice changes along the surface (per metre, in the surface's
    /// plane), from the vertices around it (within 2 cm, the mesh's own and those not on the lattice): a least-squares
    /// slope. Zero where it doesn't change (fully on the lattice, or off it).
    /// </summary>
    private static Vector3[] ShareGradients(Vector3[] pos, float[] share, List<int> vertices, List<Vector3> normals)
    {
        const float radius = 0.02f, cell = 0.02f;
        var grid = new Dictionary<(int, int, int), List<int>>();
        (int, int, int) Key(Vector3 p) => ((int)MathF.Floor(p.X / cell), (int)MathF.Floor(p.Y / cell), (int)MathF.Floor(p.Z / cell));
        for (int v = 0; v < pos.Length; v++)
        {
            if (!MathUtil.IsFinite(pos[v]))
                continue;
            var k = Key(pos[v]);
            if (!grid.TryGetValue(k, out var list))
                grid[k] = list = new List<int>();
            list.Add(v);
        }
        var result = new Vector3[vertices.Count];
        for (int i = 0; i < vertices.Count; i++)
        {
            int v = vertices[i];
            var p = pos[v];
            var (kx, ky, kz) = Key(p);
            // Normal equations of the slope: A g = b.
            float a00 = 0, a01 = 0, a02 = 0, a11 = 0, a12 = 0, a22 = 0;
            var b = Vector3.Zero;
            bool varies = false;
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (!grid.TryGetValue((kx + dx, ky + dy, kz + dz), out var list))
                            continue;
                        foreach (int j in list)
                        {
                            var d = pos[j] - p;
                            float l2 = d.LengthSquared();
                            if (l2 < 1e-10f || l2 > radius * radius)
                                continue;
                            float ds = share[j] - share[v];
                            if (MathF.Abs(ds) > 1e-4f)
                                varies = true;
                            a00 += d.X * d.X; a01 += d.X * d.Y; a02 += d.X * d.Z;
                            a11 += d.Y * d.Y; a12 += d.Y * d.Z; a22 += d.Z * d.Z;
                            b += d * ds;
                        }
                    }
            if (!varies)
                continue;
            var n = normals[i];
            if (n.LengthSquared() > 1e-12f)
                n = Vector3.Normalize(n);
            // (a surface has no slope across itself: that direction is held at zero)
            float reg = 1e-6f + 0.5f * (a00 + a11 + a22);
            var m = new Matrix4x4(
                a00 + reg * n.X * n.X, a01 + reg * n.X * n.Y, a02 + reg * n.X * n.Z, 0,
                a01 + reg * n.X * n.Y, a11 + reg * n.Y * n.Y, a12 + reg * n.Y * n.Z, 0,
                a02 + reg * n.X * n.Z, a12 + reg * n.Y * n.Z, a22 + reg * n.Z * n.Z, 0,
                0, 0, 0, 1);
            if (!Matrix4x4.Invert(m, out var inv))
                continue;
            var g = Vector3.TransformNormal(b, inv);
            g -= n * Vector3.Dot(g, n);
            if (MathUtil.IsFinite(g) && g.LengthSquared() < 1e6f)
                result[i] = g;
        }
        return result;
    }

    // Only the parts that move go to the GPU.
    private static void UploadRanges(nint buffer, LiveMesh.BufferDesc desc, byte[] work, (int From, int To)[] ranges)
    {
        foreach (var (from, to) in ranges)
            LiveMesh.UploadRange(buffer, desc, work, from, to);
    }
}
