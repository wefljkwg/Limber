using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading;
using Limber.BodyRig;
using Limber.Core;

namespace Limber.Gpu;

/// <summary>The live mesh's settings (saved with the plugin's settings).</summary>
public sealed class LiveMeshSettings
{
    public bool UsePhysics { get; set; } = true;
    public float Strength { get; set; } = 1f;      // how hard colliders push the skin (1 = all the way out)
    public float Firmness { get; set; } = 0.5f;    // how firmly it holds its shape
    public float Bounce { get; set; } = 0.4f;      // how long it keeps wobbling
    public float PullBack { get; set; } = 0.25f;   // how strongly it's pulled back to where the body puts it
    public float Friction { get; set; } = 0.5f;    // how much touching skin is held still against what touches it
    public float BreastPush { get; set; } = 1f;    // how hard the two breasts push on each other
    public int Accuracy { get; set; } = 5;         // solver passes a step (more: steadier, slower)
    public float Steadiness { get; set; } = 0.3f;  // smooths frame-to-frame jitter of the skin
    public float SkinMargin { get; set; } = 2f;    // mm kept between the skin and colliders
    public float ImprintDepth { get; set; } = 3f;  // cm: how deep the exact shape of what touches is pressed in
    public float ClingRange { get; set; } = 3f;    // cm: how far skin reaches to cling onto a collider with Skin cling
    // The older skin-only dent.
    public float Spread { get; set; } = 3f;
    public float Bulge { get; set; } = 0.6f;
}

/// <summary>
/// Experimental per-vertex soft body for the breasts: every breast vertex of one worn model is moved on the GPU,
/// so anything touching it (props for now) pushes the skin in exactly where it touches and the flesh around it gives
/// way smoothly, along the surface (not across to the other breast, not the nipple unless that's touched).
///
/// Each frame: the bone hook takes a snapshot of the character's skinning matrices and the colliders (in model
/// space); the render thread skins each breast vertex on the CPU to find where it is, pushes it out of the colliders,
/// spreads that push over the surface (a few passes over the mesh's own edges, fading with distance), lets it spring
/// back with a little wobble when released, turns the offset back into the model's rest space and writes the
/// vertices into the model's vertex buffer (see <see cref="LiveMesh"/>).
/// </summary>
public static unsafe partial class LiveSoftBody
{
    // ---- Settings (the Rigging tab's Live mesh section)
    public static Func<LiveMeshSettings> Settings = () => Fallback;
    private static readonly LiveMeshSettings Fallback = new();
    public static float Spread { get => Settings().Spread; set => Settings().Spread = value; }
    public static float Bulge { get => Settings().Bulge; set => Settings().Bulge = value; }
    public static float Strength { get => Settings().Strength; set => Settings().Strength = value; }
    public static float Firmness { get => Settings().Firmness; set => Settings().Firmness = value; }
    public static float Bounce { get => Settings().Bounce; set => Settings().Bounce = value; }
    // Extra gap kept from colliders, in metres (twice the skin margin; the soft body keeps half of it).
    public static float Skin => Math.Clamp(Settings().SkinMargin, 0f, 20f) * 0.002f;
    /// <summary>The breasts as a real soft body (particles through the whole breast, see Physics); off = the older
    /// dent-and-spring on the skin only.</summary>
    public static bool UsePhysics { get => Settings().UsePhysics; set => Settings().UsePhysics = value; }

    public static Func<PropWorld[]> Props = () => [];
    /// <summary>A collider on a hand that's grabbing (its fingers then dent the skin only as thick as the finger is).</summary>
    public static Func<PropWorld, bool> Grabbing = _ => false;
    /// <summary>The finger's own thickness (m, world) for grabbing hands.</summary>
    public static Func<float> GrabFinger = () => 0.0075f;
    /// <summary>How much the skin clings to a grabbing finger.</summary>
    public static float GrabCling = 0.12f;
    public static Func<bool> Allowed = () => true; // GPose, or outside it with "Also run outside GPose" 

    // ---- What's being simulated (set from the UI)

    private sealed class Owner
    {
        public required nint Address;
        public required ushort Index;
        public required string[] Bones;   // every bone the breast vertices use (index = slot in the snapshot)
    }

    private sealed class Group  // one surface point (vertices at the same spot, split by UV seams, move as one)
    {
        public Vector3 Rest;                 // rest (bind) position
        public int[] Members = [];           // vertex numbers in the mesh
        public int[] Bone = [];              // snapshot bone slots
        public float[] Weight = [];
        public float Mask;                   // how much of it is breast / butt (fades the effect out into the body)
        public int[] Neighbours = [];
        public float[] Lengths = [];         // rest distance to each neighbour
        public Vector3 RestNormal;           // the surface's normal at rest (from the triangles, welded)
        public int Side;                     // 0 / 1 = left / right breast, 2 / 3 = left / right butt cheek
        public float Area;                   // the bit of skin this point stands for (m²)
        public float Nipple;                 // 1 = part of the nipple (moves as one solid piece), fading to 0 at its base
        public Vector3 Offset, Velocity;     // what's shown (model space), and its speed
    }

    // Breasts (sides 0, 1) and butt cheeks (2, 3): each its own soft body; nipples only on the breasts.
    private const int Sides = 4;

    private sealed class MeshState
    {
        public required MdlRigger.MeshLayout Layout;
        public required Group[] Groups;
        public int[]? Edges;   // pairs of group numbers
        public int[] Triangles = [];   // in group numbers (only triangles wholly on the breasts)
    }

    // Nipple core / base sizes (m) and how far a nipple may be pushed as one piece.
    private const float NippleCore = 0.008f, NippleBase = 0.016f, NippleInward = 0.010f, NippleAside = 0.005f;

    // Which points are the nipples: from the rig's nipple bones when the model has them, else the most forward
    // point of each breast and what's around it.
    private static void MarkNipples(List<Group> groups, IReadOnlyList<string> bones)
    {
        var nippleSlots = new HashSet<int>();
        for (int i = 0; i < bones.Count; i++)
            if (bones[i].EndsWith("_nip", StringComparison.Ordinal))
                nippleSlots.Add(i);
        var tip = new Vector3?[2];
        if (nippleSlots.Count > 0)
        {
            // Centre of each side's nipple-weighted points.
            var sum = new Vector3[2];
            var weight = new float[2];
            foreach (var g in groups)
            {
                if (g.Side >= 2)
                    continue; // (the butt has none)
                float w = 0f;
                for (int b = 0; b < g.Bone.Length; b++)
                    if (nippleSlots.Contains(g.Bone[b]))
                        w += g.Weight[b];
                if (w > 0.5f)
                {
                    sum[g.Side] += g.Rest * w;
                    weight[g.Side] += w;
                }
            }
            for (int side = 0; side < 2; side++)
                if (weight[side] > 0f)
                    tip[side] = sum[side] / weight[side];
        }
        for (int side = 0; side < 2; side++)
        {
            if (tip[side] != null)
                continue;
            Group? best = null;
            foreach (var g in groups)
                if (g.Side == side && g.Mask > 0.5f && (best == null || g.Rest.Z > best.Rest.Z))
                    best = g;
            if (best != null)
                tip[side] = best.Rest;
        }
        foreach (var g in groups)
        {
            if (g.Side >= 2 || tip[g.Side] is not Vector3 t)
                continue;
            float d = Vector3.Distance(g.Rest, t);
            g.Nipple = d <= NippleCore ? 1f : d >= NippleBase ? 0f : 1f - SmoothStep((d - NippleCore) / (NippleBase - NippleCore));
        }
    }

    // Nipples stay solid: the core moves as one piece with where the skin around it goes, plus at most a little
    // push of its own (further inward than to the side), and the base blends from that into the skin around.
    private static void SolidNipples(Group[] groups, Vector3[] want, Vector3[] facing)
    {
        for (int side = 0; side < 2; side++)
        {
            Vector3 core = Vector3.Zero, ring = Vector3.Zero, normal = Vector3.Zero;
            int coreCount = 0;
            float ringWeight = 0f;
            for (int g = 0; g < groups.Length; g++)
            {
                var grp = groups[g];
                if (grp.Side != side || grp.Nipple <= 0f)
                    continue;
                if (grp.Nipple >= 1f)
                {
                    core += want[g];
                    normal += facing[g];
                    coreCount++;
                }
                else
                {
                    float w = 1f - grp.Nipple; // the outer edge of the base is most like the skin around
                    ring += want[g] * w;
                    ringWeight += w;
                }
            }
            if (coreCount == 0)
                continue;
            core /= coreCount;
            var baseMove = ringWeight > 0f ? ring / ringWeight : Vector3.Zero;
            // Its own push on top of the skin's: split into in / out along the nipple and to the side, each limited.
            var own = core - baseMove;
            var n = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : Vector3.UnitZ;
            float along = Vector3.Dot(own, n);
            var aside = own - n * along;
            along = Math.Clamp(along, -NippleInward, NippleAside);
            if (aside.Length() > NippleAside)
                aside = Vector3.Normalize(aside) * NippleAside;
            var solid = baseMove + n * along + aside;
            for (int g = 0; g < groups.Length; g++)
            {
                var grp = groups[g];
                if (grp.Side != side || grp.Nipple <= 0f)
                    continue;
                want[g] = Vector3.Lerp(want[g], solid, grp.Nipple);
            }
        }
    }

    private static int[] BuildEdges(Group[] groups)
    {
        var list = new List<int>();
        for (int g = 0; g < groups.Length; g++)
            foreach (var o in groups[g].Neighbours)
                if (o > g)
                {
                    list.Add(g);
                    list.Add(o);
                }
        return list.ToArray();
    }

    private sealed class Sim
    {
        public required LiveMesh.Found Target;
        public required byte[] Original;
        public required byte[] Work;
        public required List<MeshState> Meshes;
        public required Owner Owner;
        public nint Replacement;
        public nint DrawObject, Model;  // the character's model it's on (a redraw makes new ones)
        public long LastTicks;
        public bool Idle;
        public object? LastSnapshot;   // the snapshot already used (the render thread can run more often than the game)
        public int Quiet;              // frames in a row with nothing moving
        public Physics? Physics;       // the soft body (null if the breasts were too small to build one)
        public int Busy;               // a physics step is running on a worker
        public Result? Done;           // its latest result, for the render thread to write
        public long PhysicsTicks;
    }

    private sealed class Result
    {
        public required Snapshot Snapshot;
        public required Vector3[][] Offsets;   // per mesh, per surface point (posed model space)
    }

    private sealed class Snapshot
    {
        public required Matrix4x4[] Skinning;    // per bone slot: rest model space -> posed model space
        public required Shape[] Colliders; // model space
        public required Guid[] Ids;                // which prop each collider is
        public required float[] Own;               // each collider's own mesh strength
        public required float[] Cling;             // each collider's skin cling (0 = none)
        public required float[] Share;             // how much of a particle's size it keeps from this collider (1 = all)
        public Matrix4x4 World;                    // model space -> world
    }

    private static volatile Owner? _owner;
    private static volatile Snapshot? _snapshot;
    private static Sim? _sim;
    public static bool Running => _sim != null;

    /// <summary>The character the per-vertex soft body is on (0 = none).</summary>
    public static bool IsOn(nint address) => _owner is { } o && o.Address == address;
    public static string? LastError;
    public static int LastTouched;

    // ---- Start / stop (UI, render thread)

    /// <summary><paramref name="partOf"/>: which soft part a bone moves (0 breasts, 1 butt, -1 neither).</summary>
    public static string Start(LiveMesh.Found target, nint address, ushort index, Func<string, byte[]?> readFile, Func<string, int> partOf)
    {
        Stop();
        LiveMesh.StopWobble();
        if (!target.IsVertex)
            return "That's not a vertex buffer.";
        var data = readFile(target.File);
        if (data == null)
            return "Couldn't read the model file.";
        var (offset, size, layouts) = MdlRigger.Layout(data);
        int lod = Array.FindIndex(size, s => s == target.Desc.ByteWidth);
        if (lod < 0)
            return $"No vertex block in the file is {target.Desc.ByteWidth} bytes; try another vertices line.";
        var original = data.AsSpan((int)offset[lod], (int)size[lod]).ToArray();
        var skins = MdlRigger.Skin(data, lod);
        var bones = new List<string>();
        int BoneSlot(string name)
        {
            int i = bones.IndexOf(name);
            if (i >= 0)
                return i;
            bones.Add(name);
            return bones.Count - 1;
        }

        var meshes = new List<MeshState>();
        int total = 0;
        foreach (var layout in layouts.Where(l => l.Lod == lod))
        {
            var skin = skins.FirstOrDefault(s => s.Mesh == layout.Mesh);
            if (skin == null)
                continue;
            // Each vertex's breast / butt share, and which of the two it mostly is.
            var mask = new float[layout.VertexCount];
            var partOfVertex = new int[layout.VertexCount];
            var bonePart = skin.Bones.Select(partOf).ToArray();
            bool any = false;
            for (int v = 0; v < layout.VertexCount; v++)
            {
                float breast = 0f, butt = 0f;
                for (int k = 0; k < skin.Slots; k++)
                {
                    int slot = skin.Indices[v * 8 + k];
                    float wk = skin.Weights[v * 8 + k];
                    if (wk <= 0f || slot >= skin.Bones.Length)
                        continue;
                    if (bonePart[slot] == 0)
                        breast += wk;
                    else if (bonePart[slot] == 1)
                        butt += wk;
                }
                float w = breast + butt;
                mask[v] = Math.Min(w, 1f);
                partOfVertex[v] = butt > breast ? 1 : 0;
                any |= w > 0.02f;
            }
            if (!any)
                continue;

            // Welded surface points of the breast vertices.
            var groupOf = new int[layout.VertexCount];
            Array.Fill(groupOf, -1);
            var byPosition = new Dictionary<(int, int, int), int>();
            var groups = new List<Group>();
            var members = new List<List<int>>();
            for (int v = 0; v < layout.VertexCount; v++)
            {
                if (mask[v] <= 0.02f)
                    continue;
                int pa = layout.PositionAt + v * layout.PositionStride;
                if (pa + 12 > original.Length)
                    break;
                var p = MdlRigger.ReadVector(original, pa, layout.PositionType);
                var key = ((int)MathF.Round(p.X * 20000f), (int)MathF.Round(p.Y * 20000f), (int)MathF.Round(p.Z * 20000f));
                if (!byPosition.TryGetValue(key, out int g))
                {
                    g = groups.Count;
                    byPosition[key] = g;
                    var bonesHere = new List<int>();
                    var weightsHere = new List<float>();
                    for (int k = 0; k < skin.Slots; k++)
                    {
                        float w = skin.Weights[v * 8 + k];
                        int slot = skin.Indices[v * 8 + k];
                        if (w <= 0f || slot >= skin.Bones.Length)
                            continue;
                        bonesHere.Add(BoneSlot(skin.Bones[slot]));
                        weightsHere.Add(w);
                    }
                    groups.Add(new Group { Rest = p, Bone = bonesHere.ToArray(), Weight = weightsHere.ToArray(), Mask = mask[v], Side = partOfVertex[v] * 2 });
                    members.Add(new List<int>());
                }
                groupOf[v] = g;
                members[g].Add(v);
            }
            // Neighbours along the mesh's edges.
            var links = new HashSet<(int, int)>();
            var t = skin.Triangles;
            for (int i = 0; i + 2 < t.Length; i += 3)
            {
                int a = t[i] < groupOf.Length ? groupOf[t[i]] : -1, b = t[i + 1] < groupOf.Length ? groupOf[t[i + 1]] : -1, c = t[i + 2] < groupOf.Length ? groupOf[t[i + 2]] : -1;
                foreach (var (x, y) in new[] { (a, b), (b, c), (c, a) })
                    if (x >= 0 && y >= 0 && x != y)
                        links.Add(x < y ? (x, y) : (y, x));
            }
            var neighbours = groups.Select(_ => new List<int>()).ToArray();
            foreach (var (x, y) in links)
            {
                neighbours[x].Add(y);
                neighbours[y].Add(x);
            }
            var triangles = new List<int>();
            var normals = new Vector3[groups.Count];
            for (int i = 0; i + 2 < t.Length; i += 3)
            {
                int a = t[i] < groupOf.Length ? groupOf[t[i]] : -1, b = t[i + 1] < groupOf.Length ? groupOf[t[i + 1]] : -1, c = t[i + 2] < groupOf.Length ? groupOf[t[i + 2]] : -1;
                if (a < 0 || b < 0 || c < 0 || a == b || b == c || a == c)
                    continue;
                triangles.Add(a);
                triangles.Add(b);
                triangles.Add(c);
                var face = Vector3.Cross(groups[b].Rest - groups[a].Rest, groups[c].Rest - groups[a].Rest);
                normals[a] += face;
                normals[b] += face;
                normals[c] += face;
            }
            for (int g = 0; g < groups.Count; g++)
            {
                groups[g].Members = members[g].ToArray();
                groups[g].Neighbours = neighbours[g].ToArray();
                groups[g].Lengths = neighbours[g].Select(o => Vector3.Distance(groups[g].Rest, groups[o].Rest)).ToArray();
                groups[g].RestNormal = normals[g].LengthSquared() > 1e-20f ? Vector3.Normalize(normals[g]) : Vector3.UnitZ;
                groups[g].Side += groups[g].Rest.X >= 0f ? 0 : 1; // (breast 0 / 1, butt 2 / 3)
                float spacing = groups[g].Lengths.Length > 0 ? groups[g].Lengths.Average() : 0.003f;
                groups[g].Area = spacing * spacing;
            }
            MarkNipples(groups, bones);
            meshes.Add(new MeshState { Layout = layout, Groups = groups.ToArray(), Triangles = triangles.ToArray() });
            total += groups.Count;
        }
        if (meshes.Count == 0)
            return "No breast or butt vertices in this model (pick the body / top model's vertices line).";

        var sim = new Sim
        {
            Target = target,
            Original = original,
            Work = (byte[])original.Clone(),
            Meshes = meshes,
            Owner = new Owner { Address = address, Index = index, Bones = bones.ToArray() },
        };
        if (target.Desc.Usage == 1) // immutable: an identical writable buffer in its place
        {
            sim.Replacement = LiveMesh.Replace(target, original);
            if (sim.Replacement == 0)
                return "Couldn't swap in a writable copy of the buffer.";
        }
        (sim.DrawObject, sim.Model) = ModelOf(address, index, target.Slot);
        sim.Physics = Physics.Build(meshes);
        _snapshot = null;
        _owner = sim.Owner;
        _sim = sim;
        _resume = new Resume { Slot = target.Slot, Path = target.Path, Width = target.Desc.ByteWidth, Address = address, Index = index,
            ReadFile = readFile, PartOf = partOf };
        LastError = null;
        string physics = sim.Physics != null ? $", soft body of {sim.Physics.Count} particles" : ", no soft body (too few points)";
        return $"Live soft body on {total} breast / butt points{physics} ({meshes.Count} mesh(es), LoD {lod}). Touch it with a prop.";
    }

    // ---- Starting again by itself after a redraw (a Body Shape build, gear change, ...)

    private sealed class Resume
    {
        public int Slot;
        public required string Path;
        public uint Width;
        public nint Address;
        public ushort Index;
        public required Func<string, byte[]?> ReadFile;
        public required Func<string, int> PartOf;
        public long Since, Next;
    }

    private static Resume? _resume;

    /// <summary>Whether it's waiting for the model to come back after a redraw, to start again.</summary>
    public static bool Resuming => _sim == null && _resume != null;

    private static void Reloaded()
    {
        _sim = null;
        _owner = null;
        _snapshot = null;
        if (_resume is { } r)
        {
            r.Since = Environment.TickCount64;
            r.Next = r.Since + 300;
            LastError = "The model was reloaded; starting the live soft body again...";
        }
        else
            LastError = "The model was reloaded; the live soft body stopped (start it again after probing).";
    }

    // The character's draw object and its model in this slot (0s if it isn't there).
    private static (nint DrawObject, nint Model) ModelOf(nint address, ushort index, int slot)
    {
        var obj = ActorSkeleton.GetObject(index);
        if (obj == null || (nint)obj != address || obj->DrawObject == null)
            return (0, 0);
        var cb = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase*)obj->DrawObject;
        if (slot < 0 || slot >= cb->SlotCount || cb->Models == null)
            return ((nint)cb, 0);
        return ((nint)cb, (nint)cb->Models[slot]);
    }

    private static void TryResume()
    {
        var r = _resume;
        if (r == null)
            return;
        long now = Environment.TickCount64;
        if (r.Since == 0)
            r.Since = now;
        if (now < r.Next)
            return;
        if (now - r.Since > 30000)
        {
            _resume = null;
            LastError = "The model was reloaded and didn't come back; probe and start the live soft body again.";
            return;
        }
        r.Next = now + 500;
        try
        {
            var obj = ActorSkeleton.GetObject(r.Index);
            if (obj == null || (nint)obj != r.Address || !Limber.Koi.Safety.AdultAllowed(r.Address) || obj->DrawObject == null)
                return;
            var found = LiveMesh.Probe((nint)obj->DrawObject, new List<string>());
            var candidates = found.Where(f => f.IsVertex && f.Slot == r.Slot)
                .OrderBy(f => f.Path == r.Path ? 0 : 1).ThenBy(f => f.Desc.ByteWidth == r.Width ? 0 : 1).ToList();
            foreach (var f in candidates)
            {
                Start(f, r.Address, r.Index, r.ReadFile, r.PartOf);
                if (Running)
                    return; // (Start keeps it for the next redraw)
            }
            _resume = r; // (each failed Start forgot it)
        }
        catch (Exception e)
        {
            _resume = r;
            LastError = "Starting the live soft body again failed: " + e.Message;
        }
    }

    public static void Stop()
    {
        _resume = null;
        var sim = _sim;
        _sim = null;
        _owner = null;
        _snapshot = null;
        Grab = new GrabSkin();
        if (sim == null)
            return;
        try
        {
            if (sim.Replacement != 0)
                LiveMesh.Restore(sim.Target, sim.Replacement);
            else if (LiveMesh.StillHolds(sim.Target, sim.Target.Buffer))
                LiveMesh.Upload(sim.Target.Buffer, sim.Target.Desc, sim.Original);
        }
        catch (Exception e)
        {
            LastError = e.Message;
        }
    }

    // ---- The bone hook: this frame's skinning matrices and colliders (framework thread)

    private static readonly SkeletonInfoCache Cache = new();
    private static readonly Dictionary<SkeletonInfo, Matrix4x4[]> RestInverse = new();

    public static void Capture()
    {
        var owner = _owner;
        if (owner == null || !Allowed())
            return;
        if (!ActorSkeleton.TryGet(owner.Address, owner.Index, Cache, out var sk) || !Limber.Koi.Safety.AdultAllowed(owner.Address))
            return;
        var info = sk.Info;
        if (!RestInverse.TryGetValue(info, out var restInverse))
        {
            restInverse = RestModelInverse(sk);
            if (RestInverse.Count > 16)
                RestInverse.Clear();
            RestInverse[info] = restInverse;
        }
        var skinning = new Matrix4x4[owner.Bones.Length];
        for (int i = 0; i < skinning.Length; i++)
        {
            int bone = info.IndexOf(owner.Bones[i]);
            if (bone < 0 || bone >= restInverse.Length)
            {
                skinning[i] = Matrix4x4.Identity;
                continue;
            }
            var frame = sk.GetModelFrame(bone);
            var scale = sk.GetModelScale(bone);
            var now = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(frame.Rotation) * Matrix4x4.CreateTranslation(frame.Position);
            skinning[i] = restInverse[bone] * now;
        }
        var colliders = new List<Shape>();
        var ids = new List<Guid>();
        var own = new List<float>();
        var cling = new List<float>();
        var share = new List<float>();
        float toModel = 1f / MathF.Max(sk.RootScale.Y, 0.01f);
        var worldToModel = sk.WorldToModel;
        foreach (var prop in Props() ?? Array.Empty<PropWorld>())
            if (prop.Mesh)
            {
                // A grabbing finger dents the skin as thick as the finger itself (not the collider's size), so the
                // finger lies in its dent instead of floating above it.
                float radius = prop.Radius;
                bool grabbing = radius < 0.02f * MathF.Max(sk.RootScale.Y, 0.01f) && Grabbing(prop);
                if (grabbing)
                    radius = MathF.Min(radius, GrabFinger() * MathF.Max(sk.RootScale.Y, 0.01f));
                colliders.Add(prop.Shape.WithRadius(radius).Moved(x => Vector3.Transform(x, worldToModel), toModel));
                ids.Add(prop.Id);
                own.Add(prop.MeshStrength);
                // A grabbing finger: the skin clings to it and comes right up to it (it holds on).
                cling.Add(grabbing ? MathF.Max(prop.Cling, GrabCling) : prop.Cling);
                share.Add(grabbing ? 0.5f : 1f);
            }
        _snapshot = new Snapshot { Skinning = skinning, Colliders = colliders.ToArray(), Ids = ids.ToArray(), Own = own.ToArray(), Cling = cling.ToArray(), Share = share.ToArray(), World = sk.ModelToWorld };
    }

    // The skeleton's rest pose in model space, inverted (what the model's vertices are bound to).
    private static Matrix4x4[] RestModelInverse(in ActorSkeleton sk)
    {
        var info = sk.Info;
        var model = new Matrix4x4[info.Count];
        var inverse = new Matrix4x4[info.Count];
        var reference = sk.Pose->Skeleton->ReferencePose;
        for (int i = 0; i < info.Count; i++)
        {
            Matrix4x4 local = Matrix4x4.Identity;
            if (i < reference.Length)
            {
                var t = reference[i];
                var position = *(Vector3*)&t.Translation;
                var rotation = Quaternion.Normalize(*(Quaternion*)&t.Rotation);
                var scale = *(Vector3*)&t.Scale;
                local = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(position);
            }
            int parent = info.Parents[i];
            model[i] = parent >= 0 && parent < i ? local * model[parent] : local;
            if (!Matrix4x4.Invert(model[i], out inverse[i]))
                inverse[i] = Matrix4x4.Identity;
        }
        return inverse;
    }

    // ---- Every frame on the render thread

    /// <summary>The soft body (physics) is on this character's breasts.</summary>
    public static bool PhysicsOn(nint address) => UsePhysics && _sim is { Physics: not null } sim && sim.Owner.Address == address;
    public static int PhysicsParticles => _sim?.Physics?.Count ?? 0;
    /// <summary>
    /// The breasts' skin as their shape sliders make it, without what moves it (the soft body bones' jiggle and
    /// collisions, the live mesh's dents): steady, for grabbing fingers to hold onto. World points and normals.
    /// </summary>
    public sealed class GrabSkin
    {
        public Vector3[] Points = [];
        public Vector3[] Normals = [];
    }
    public static volatile GrabSkin Grab = new();
    /// <summary>How far the soft body bones moved each bone off its resting spot (model space, by name; set by the plugin).</summary>
    public static Func<nint, IReadOnlyDictionary<string, Vector3>?> LatticeShift = _ => null;
    public static float PhysicsMilliseconds => _sim?.Physics?.LastMilliseconds ?? 0f;

    public static void Update()
    {
        var sim = _sim;
        var snap = _snapshot;
        if (sim == null && _resume != null)
            TryResume();
        if (sim == null)
            return;
        // Redrawn (a Body Shape build, gear change)? The old model's memory can still look right for a while, so
        // its draw object and model are checked, not just the buffer.
        if (sim.DrawObject != 0 && ModelOf(sim.Owner.Address, sim.Owner.Index, sim.Target.Slot) != (sim.DrawObject, sim.Model))
        {
            Reloaded();
            return;
        }
        if (snap == null)
            return;
        if (UsePhysics && sim.Physics != null)
        {
            UpdatePhysics(sim, snap);
            return;
        }
        if (ReferenceEquals(snap, sim.LastSnapshot))
            return; // nothing new from the game since the last write: keep what's there
        sim.LastSnapshot = snap;
        try
        {
            var buffer = sim.Replacement != 0 ? sim.Replacement : sim.Target.Buffer;
            if (!LiveMesh.StillHolds(sim.Target, buffer))
            {
                Reloaded();
                return;
            }
            long now = Stopwatch.GetTimestamp();
            float dt = sim.LastTicks == 0 ? 1f / 60f : Math.Clamp((float)((now - sim.LastTicks) / (double)Stopwatch.Frequency), 1f / 240f, 0.1f);
            sim.LastTicks = now;
            var m = snap.Skinning;
            // Each collider's own strength: thinner by that much (off at 0).
            var colliders = new List<Shape>(snap.Colliders.Length);
            for (int c = 0; c < snap.Colliders.Length; c++)
            {
                float own = c < snap.Own.Length ? snap.Own[c] : 1f;
                if (own > 0f)
                    colliders.Add(snap.Colliders[c].WithRadius(snap.Colliders[c].R * own));
            }
            var shapes = colliders.ToArray();
            float spread = Math.Clamp(Spread, 0.3f, 15f) * 0.01f; // cm -> m
            float k = Lerp(60f, 900f, Math.Clamp(Firmness, 0f, 1f));
            float damping = Lerp(45f, 7f, Math.Clamp(Bounce, 0f, 1f));
            bool moving = false;
            int touched = 0;

            foreach (var mesh in sim.Meshes)
            {
                var groups = mesh.Groups;
                int n = groups.Length;
                var posed = new Vector3[n];
                var facing = new Vector3[n];
                for (int g = 0; g < n; g++)
                {
                    var grp = groups[g];
                    var q = Vector3.Zero;
                    var nn = Vector3.Zero;
                    float total = 0f;
                    for (int b = 0; b < grp.Bone.Length; b++)
                    {
                        q += Vector3.Transform(grp.Rest, m[grp.Bone[b]]) * grp.Weight[b];
                        nn += Vector3.TransformNormal(grp.RestNormal, m[grp.Bone[b]]) * grp.Weight[b];
                        total += grp.Weight[b];
                    }
                    posed[g] = total > 0f ? q / total : grp.Rest;
                    facing[g] = nn.LengthSquared() > 1e-20f ? Vector3.Normalize(nn) : grp.RestNormal;
                }
                int hits = 0;
                var want = shapes.Length > 0 ? Contact(groups, posed, facing, shapes, spread, out hits) : null;
                if (want != null)
                    SolidNipples(groups, want, facing);
                touched += hits;

                // What's shown follows the wanted shape: straight there where pressed, springing back (with a
                // little wobble) where let go.
                for (int g = 0; g < n; g++)
                {
                    var grp = groups[g];
                    var target = want?[g] ?? Vector3.Zero;
                    if (grp.Mask <= 0.35f)
                        target = Vector3.Zero;
                    bool pressed = target.LengthSquared() > grp.Offset.LengthSquared();
                    if (pressed)
                    {
                        // Quickly (not in one jump) out to where it's pushed: no frame-to-frame popping.
                        grp.Offset += (target - grp.Offset) * Math.Min(1f, 25f * dt);
                        grp.Velocity = Vector3.Zero;
                    }
                    else
                    {
                        grp.Velocity += (target - grp.Offset) * (k * dt);
                        grp.Velocity *= MathF.Exp(-damping * dt);
                        grp.Offset += grp.Velocity * dt;
                    }
                    if (!MathUtil.IsFinite(grp.Offset) || grp.Offset.LengthSquared() > 0.04f)
                    {
                        grp.Offset = Vector3.Zero;
                        grp.Velocity = Vector3.Zero;
                    }
                    if (grp.Offset.LengthSquared() > 1e-10f || grp.Velocity.LengthSquared() > 1e-8f)
                        moving = true;
                }
            }
            LastTouched = touched;
            sim.Quiet = moving ? 0 : sim.Quiet + 1;
            if (sim.Quiet > 0)
            {
                // Settled: the model's own vertices back once (after a short quiet spell, so a contact that comes
                // and goes doesn't make it flicker).
                if (sim.Quiet > 30 && !sim.Idle)
                {
                    LiveMesh.Upload(buffer, sim.Target.Desc, sim.Original);
                    sim.Idle = true;
                }
                if (sim.Quiet > 30 || sim.Idle)
                    return;
            }
            sim.Idle = false;

            // Back into the model's rest space (what the vertex buffer holds), with the normals turned to match.
            Buffer.BlockCopy(sim.Original, 0, sim.Work, 0, sim.Original.Length);
            foreach (var mesh in sim.Meshes)
                WriteMesh(sim, mesh, m);
            LiveMesh.Upload(buffer, sim.Target.Desc, sim.Work);
        }
        catch (Exception e)
        {
            LastError = e.Message;
            _sim = null;
            _owner = null;
        }
    }

    /// <summary>
    /// The shape the skin wants with these colliders (model space offsets per point): each pressed point goes out to
    /// the collider's surface; around it the skin follows that point's push, faded smoothly with distance over the
    /// skin (not through the air, so it never jumps to the other breast); repeated so the rim is pushed out too; then
    /// smoothed so the edge of a dent is a soft curve (no stair steps along a corner); and what's pressed in swells
    /// out around the dent (volume kept).
    /// </summary>
    private static Vector3[]? Contact(Group[] groups, Vector3[] posed, Vector3[] facing, Shape[] colliders, float spread, out int hits)
    {
        int n = groups.Length;
        float strength = Math.Clamp(Strength, 0f, 1f);
        var d = new Vector3[n];
        var target = new Vector3[n];
        var pressed = new bool[n];
        hits = 0;
        bool any = false;
        for (int round = 0; round < 3; round++)
        {
            bool fresh = false;
            for (int g = 0; g < n; g++)
            {
                if (groups[g].Mask <= 0.35f)
                    continue;
                var x = posed[g] + d[g];
                foreach (var shape in colliders)
                {
                    float r = shape.Box ? shape.Thickness : shape.R;
                    float dist = shape.Gap(x);
                    float reach = shape.R + Skin;
                    if (dist >= reach)
                        continue;
                    // Only part of the way out at lower strength: the skin sinks into the collider that much.
                    // (measured from where the body puts it, so repeated rounds don't add up)
                    var t = (r < SmallCollider ? Dent(posed[g], facing[g], colliders) : OutOf(x, facing[g], shape, reach)) - posed[g];
                    t *= strength;
                    if (!pressed[g] || t.LengthSquared() > target[g].LengthSquared())
                        target[g] = t;
                    if (!pressed[g])
                        fresh = true;
                    pressed[g] = true;
                    any = true;
                }
            }
            if (!any)
                return null;
            if (!fresh && round > 0)
                break;
            // Spread by distance over the skin from the nearest pressed point.
            var dist2 = new float[n];
            var source = new int[n];
            Array.Fill(dist2, float.PositiveInfinity);
            Array.Fill(source, -1);
            var queue = new PriorityQueue<int, float>();
            for (int g = 0; g < n; g++)
                if (pressed[g])
                {
                    dist2[g] = 0f;
                    source[g] = g;
                    queue.Enqueue(g, 0f);
                }
            float limit = spread * 3f;
            while (queue.TryDequeue(out int g, out float at))
            {
                if (at > dist2[g])
                    continue;
                var nb = groups[g].Neighbours;
                var len = groups[g].Lengths;
                for (int i = 0; i < nb.Length; i++)
                {
                    float nd = at + len[i];
                    int o = nb[i];
                    if (nd < dist2[o] && nd < limit)
                    {
                        dist2[o] = nd;
                        source[o] = source[g];
                        queue.Enqueue(o, nd);
                    }
                }
            }
            for (int g = 0; g < n; g++)
            {
                if (source[g] < 0 || groups[g].Mask <= 0.35f)
                {
                    d[g] = Vector3.Zero;
                    continue;
                }
                float f = dist2[g] / spread;
                d[g] = target[source[g]] * MathF.Exp(-f * f);
            }
        }
        // Smooth it: blends where two contacts' areas meet, and rounds the dent's edge (the pressed points are
        // smoothed too, then put back out of the colliders, so the skin follows a sharp corner as a soft curve).
        var next = new Vector3[n];
        for (int pass = 0; pass < 8; pass++)
        {
            for (int g = 0; g < n; g++)
            {
                var nb = groups[g].Neighbours;
                if (nb.Length == 0 || groups[g].Mask <= 0.35f)
                {
                    next[g] = d[g];
                    continue;
                }
                var sum = Vector3.Zero;
                foreach (var o in nb)
                    sum += d[o];
                next[g] = d[g] * 0.5f + sum / nb.Length * 0.5f;
            }
            (d, next) = (next, d);
        }
        for (int g = 0; g < n; g++)
        {
            if (groups[g].Mask <= 0.35f)
                continue;
            var x = posed[g] + d[g];
            foreach (var shape in colliders)
            {
                float r = shape.Box ? shape.Thickness : shape.R;
                float reach = shape.R + Skin * 0.5f;
                if (shape.Gap(x) < reach)
                {
                    var want = posed[g] + ((r < SmallCollider ? Dent(posed[g], facing[g], colliders) : OutOf(x, facing[g], shape, reach)) - posed[g]) * strength;
                    if ((want - posed[g]).LengthSquared() > (x - posed[g]).LengthSquared())
                        x = want;
                }
            }
            d[g] = x - posed[g];
            if (pressed[g])
                hits++;
        }
        // Volume kept: the pressed-in volume swells out in a ring just beyond the dent, on the same breast (by
        // volume: depth x area in, the same out over the ring's area; never more than half the dent's depth).
        if (Bulge > 0f)
        {
            var lost = new float[Sides];
            var ring = new float[Sides];
            var w = new float[n];
            float max = 0f;
            for (int g = 0; g < n; g++)
            {
                float into = -Vector3.Dot(d[g], groups[g].RestNormal);
                if (into > 0f)
                    lost[groups[g].Side] += into * groups[g].Area;
                max = MathF.Max(max, d[g].Length());
            }
            for (int g = 0; g < n; g++)
            {
                if (groups[g].Mask <= 0.35f || d[g].Length() > 0.3f * max)
                    continue;
                // Strongest where the dent has faded out.
                float near = 0f;
                foreach (var o in groups[g].Neighbours)
                    near = MathF.Max(near, d[o].Length());
                float x = max > 0f ? near / max : 0f;
                w[g] = x * (1f - x) * 4f;
                ring[groups[g].Side] += w[g] * groups[g].Area;
            }
            float cap = 0.5f * max;
            for (int g = 0; g < n; g++)
            {
                if (w[g] <= 0f || ring[groups[g].Side] <= 0f)
                    continue;
                float up = MathF.Min(Bulge * lost[groups[g].Side] * w[g] / ring[groups[g].Side], cap);
                d[g] += groups[g].RestNormal * up;
            }
        }
        return d;
    }

    // The soft body steps on a worker (it takes a few milliseconds); the render thread only writes what it made.
    private static void UpdatePhysics(Sim sim, Snapshot snap)
    {
        if (!ReferenceEquals(snap, sim.LastSnapshot) && Interlocked.CompareExchange(ref sim.Busy, 1, 0) == 0)
        {
            sim.LastSnapshot = snap;
            var tuning = Physics.Tuning.From(Settings());
            ThreadPool.UnsafeQueueUserWorkItem(_ =>
            {
                try
                {
                    Solve(sim, snap, tuning);
                }
                catch (Exception e)
                {
                    LastError = e.Message;
                }
                finally
                {
                    Volatile.Write(ref sim.Busy, 0);
                }
            }, null);
        }

        var done = Interlocked.Exchange(ref sim.Done, null);
        if (done == null)
            return;
        try
        {
            var buffer = sim.Replacement != 0 ? sim.Replacement : sim.Target.Buffer;
            if (!LiveMesh.StillHolds(sim.Target, buffer))
            {
                Reloaded();
                return;
            }
            bool moving = false;
            for (int mi = 0; mi < sim.Meshes.Count; mi++)
            {
                var groups = sim.Meshes[mi].Groups;
                var offsets = done.Offsets[mi];
                for (int g = 0; g < groups.Length; g++)
                {
                    groups[g].Offset = offsets[g];
                    if (offsets[g].LengthSquared() > 1e-10f)
                        moving = true;
                }
            }
            sim.Quiet = moving ? 0 : sim.Quiet + 1;
            if (sim.Quiet > 0)
            {
                if (sim.Quiet > 30 && !sim.Idle)
                {
                    LiveMesh.Upload(buffer, sim.Target.Desc, sim.Original);
                    sim.Idle = true;
                }
                if (sim.Quiet > 30 || sim.Idle)
                    return;
            }
            sim.Idle = false;
            Buffer.BlockCopy(sim.Original, 0, sim.Work, 0, sim.Original.Length);
            foreach (var mesh in sim.Meshes)
                WriteMesh(sim, mesh, done.Snapshot.Skinning);
            LiveMesh.Upload(buffer, sim.Target.Desc, sim.Work);
        }
        catch (Exception e)
        {
            LastError = e.Message;
            _sim = null;
            _owner = null;
        }
    }

    // One physics step and the offsets it makes (worker thread).
    private static void Solve(Sim sim, Snapshot snap, Physics.Tuning tuning)
    {
        float strength = tuning.Strength;
        var physics = sim.Physics;
        if (physics == null || !ReferenceEquals(_sim, sim))
            return;
        long now = Stopwatch.GetTimestamp();
        double since = sim.PhysicsTicks == 0 ? 1.0 / 60.0 : (now - sim.PhysicsTicks) / (double)Stopwatch.Frequency;
        sim.PhysicsTicks = now;
        if (since > 0.25)
            physics.Reset(); // paused, or the game hitched: start from where the body is
        float dt = (float)Math.Clamp(since, 1.0 / 240.0, 1.0 / 20.0);
        var m = snap.Skinning;
        var colliders = snap.Colliders;
        physics.Step(m, colliders, snap.Ids, snap.Own, snap.Cling, snap.Share, dt, tuning);

        var result = new Vector3[sim.Meshes.Count][];
        int touched = 0;
        // Each bone slot's physics shift, to take back out of the grab surface.
        var shifts = LatticeShift(sim.Owner.Address);
        var slotShift = new Vector3[sim.Owner.Bones.Length];
        if (shifts != null)
            for (int i = 0; i < slotShift.Length; i++)
                if (shifts.TryGetValue(sim.Owner.Bones[i], out var shift))
                    slotShift[i] = shift;
        var grabPoints = new List<Vector3>();
        var grabNormals = new List<Vector3>();
        var worldTurn = snap.World;
        worldTurn.Translation = Vector3.Zero;
        for (int mi = 0; mi < sim.Meshes.Count; mi++)
        {
            var groups = sim.Meshes[mi].Groups;
            int n = groups.Length;
            var offsets = new Vector3[n];
            var facing = new Vector3[n];
            for (int g = 0; g < n; g++)
            {
                var grp = groups[g];
                var q = Vector3.Zero;
                var nn = Vector3.Zero;
                float total = 0f;
                for (int b = 0; b < grp.Bone.Length; b++)
                {
                    q += Vector3.Transform(grp.Rest, m[grp.Bone[b]]) * grp.Weight[b];
                    nn += Vector3.TransformNormal(grp.RestNormal, m[grp.Bone[b]]) * grp.Weight[b];
                    total += grp.Weight[b];
                }
                var posed = total > 0f ? q / total : grp.Rest;
                facing[g] = nn.LengthSquared() > 1e-20f ? Vector3.Normalize(nn) : grp.RestNormal;
                if (grp.Mask > 0.3f && total > 0f)
                {
                    var still = posed;
                    for (int b = 0; b < grp.Bone.Length; b++)
                        still -= slotShift[grp.Bone[b]] * (grp.Weight[b] / total);
                    grabPoints.Add(Vector3.Transform(still, snap.World));
                    var outward = Vector3.TransformNormal(facing[g], worldTurn);
                    grabNormals.Add(outward.LengthSquared() > 1e-20f ? Vector3.Normalize(outward) : Vector3.UnitZ);
                }
                var off = physics.Steady(mi, g, physics.Offset(mi, g), tuning.Steadiness);
                float need = 0f;
                if (colliders.Length > 0 && strength > 0f)
                {
                    var at = posed + off;
                    var pressed = PressIn(at, facing[g], colliders, snap.Own, strength);
                    need = Vector3.Dot(at - pressed, facing[g]);
                }
                float depth = physics.Press(mi, g, need, dt, tuning.ImprintDepth);
                if (depth > 0f)
                {
                    off -= facing[g] * depth;
                    if (need > 0f)
                        touched++;
                }
                offsets[g] = MathUtil.IsFinite(off) && off.LengthSquared() < 0.04f ? off : Vector3.Zero;
            }
            SolidNipples(groups, offsets, facing);
            result[mi] = offsets;
        }
        LastTouched = touched;
        if (ReferenceEquals(_sim, sim))
            Grab = new GrabSkin { Points = grabPoints.ToArray(), Normals = grabNormals.ToArray() };
        if (ReferenceEquals(_sim, sim))
            Volatile.Write(ref sim.Done, new Result { Snapshot = snap, Offsets = result });
    }

    // Positions (and normals) of one mesh's moved points into the work copy of the vertex buffer.
    private static void WriteMesh(Sim sim, MeshState mesh, Matrix4x4[] m)
    {
        var layout = mesh.Layout;
        var groups = mesh.Groups;
        int n = groups.Length;
        var local = new Vector3[n];
        var dirty = new bool[n];
        bool any = false;
        for (int g = 0; g < n; g++)
        {
            var grp = groups[g];
            if (grp.Offset.LengthSquared() < 1e-12f)
                continue;
            var linear = new Matrix4x4();
            float total = 0f;
            for (int b = 0; b < grp.Bone.Length; b++)
            {
                var s = m[grp.Bone[b]];
                float w = grp.Weight[b];
                linear.M11 += s.M11 * w; linear.M12 += s.M12 * w; linear.M13 += s.M13 * w;
                linear.M21 += s.M21 * w; linear.M22 += s.M22 * w; linear.M23 += s.M23 * w;
                linear.M31 += s.M31 * w; linear.M32 += s.M32 * w; linear.M33 += s.M33 * w;
                total += w;
            }
            Matrix4x4 inverse = Matrix4x4.Identity;
            if (total > 0f)
            {
                var blended = linear * (1f / total);
                blended.M44 = 1f;
                if (!Matrix4x4.Invert(blended, out inverse))
                    inverse = Matrix4x4.Identity;
            }
            local[g] = Vector3.TransformNormal(grp.Offset, inverse) * SmoothStep(grp.Mask / 0.5f);
            dirty[g] = true;
            any = true;
            foreach (var o in grp.Neighbours)
                dirty[o] = true; // its normal changes too
            var p = grp.Rest + local[g];
            foreach (var v in grp.Members)
            {
                int pa = layout.PositionAt + v * layout.PositionStride;
                if (pa + 12 <= sim.Work.Length)
                    MdlRigger.WriteVector(sim.Work, pa, layout.PositionType, p);
            }
        }
        if (!any || layout.NormalAt < 0 || layout.NormalType is not (2 or 3 or 14))
            return;
        // Normals: the surface's turn at each moved point (from the triangles, rest vs. moved), applied to the
        // model's own normal there, so the lighting follows the dent.
        var turned = new Vector3[n];
        var t = mesh.Triangles;
        for (int i = 0; i + 2 < t.Length; i += 3)
        {
            int a = t[i], b = t[i + 1], c = t[i + 2];
            if (!dirty[a] && !dirty[b] && !dirty[c])
                continue;
            var pa = groups[a].Rest + local[a];
            var pb = groups[b].Rest + local[b];
            var pc = groups[c].Rest + local[c];
            var face = Vector3.Cross(pb - pa, pc - pa);
            turned[a] += face;
            turned[b] += face;
            turned[c] += face;
        }
        for (int g = 0; g < n; g++)
        {
            if (!dirty[g] || turned[g].LengthSquared() < 1e-20f)
                continue;
            var rot = MathUtil.FromTo(groups[g].RestNormal, Vector3.Normalize(turned[g]));
            foreach (var v in groups[g].Members)
            {
                int na = layout.NormalAt + v * layout.NormalStride;
                if (na + 12 > sim.Work.Length)
                    continue;
                var normal = MdlRigger.ReadVector(sim.Original, na, layout.NormalType);
                if (normal.LengthSquared() < 1e-12f)
                    continue;
                MdlRigger.WriteVector(sim.Work, na, layout.NormalType, Vector3.Transform(normal, rot));
            }
        }
    }

    /// <summary>
    /// Where a point of skin inside a collider goes: out of it, but never outward off the body (that would stretch
    /// the skin over the collider like a tent): the outward part of the way out is dropped, so the skin slides aside
    /// or sinks in, the smallest move that clears the collider that way.
    /// </summary>
    // Colliders thinner than this (fingers) press the skin straight in instead of the skin going around them:
    // sideways the points would pop up between the fingers and through the hand.
    public static float SmallCollider = 0.025f;
    // Extra room around them, so the skin clears the finger's own mesh (thicker than its bone collider).
    public static float SmallPad = 0.003f;

    /// <summary>A dent: straight into the body (against the skin's facing) from where the body puts the point,
    /// until it's clear of every collider at once (so it doesn't bounce from one finger into the next).
    /// Pressed in deeper than 10 cm the skin lets the collider through rather than jumping.</summary>
    private static Vector3 Dent(Vector3 from, Vector3 facing, Shape[] colliders)
    {
        bool Inside(Vector3 y)
        {
            // Only the small ones (all the fingers together); a big collider around them has its own push.
            foreach (var shape in colliders)
                if (!shape.Box && shape.R < SmallCollider && shape.Gap(y) < shape.R + Skin + SmallPad)
                    return true;
            return false;
        }
        if (!Inside(from))
            return from;
        var way = -facing;
        const float Step = 0.004f;
        float hi = -1f;
        for (int k = 1; k <= 25; k++)
        {
            if (!Inside(from + way * (Step * k)))
            {
                hi = Step * k;
                break;
            }
        }
        if (hi < 0f)
            return from;
        float lo = hi - Step;
        for (int i = 0; i < 10; i++)
        {
            float mid = (lo + hi) * 0.5f;
            if (Inside(from + way * mid))
                lo = mid;
            else
                hi = mid;
        }
        return from + way * hi;
    }

    private static Vector3 OutOf(Vector3 x, Vector3 facing, in Shape shape, float reach)
    {
        shape.Gap(x, out var c, out var n);
        var radial = n.LengthSquared() > 0.5f ? n : facing;
        if (shape.Box)
            return x + radial * shape.Exit(x, radial, reach); // flat: straight out of the face
        var way = radial - facing * MathF.Max(0f, Vector3.Dot(radial, facing));
        way = way.Length() > 0.2f ? Vector3.Normalize(way) : -facing;
        var copy = shape;
        bool Outside(Vector3 y) => copy.Gap(y) >= reach;
        float hi = -1f;
        for (int k = 1; k <= 8; k++)
        {
            float t = reach * 0.5f * k;
            if (Outside(x + way * t))
            {
                hi = t;
                break;
            }
        }
        if (hi < 0f)
            return c + radial * reach; // no way out sideways: straight out
        float lo = 0f;
        for (int i = 0; i < 12; i++)
        {
            float mid = (lo + hi) * 0.5f;
            if (Outside(x + way * mid))
                hi = mid;
            else
                lo = mid;
        }
        return x + way * hi;
    }

    private static Vector3 Closest(Vector3 a, Vector3 b, Vector3 p)
    {
        var ab = b - a;
        float len = ab.LengthSquared();
        if (len < 1e-12f)
            return a;
        float t = Math.Clamp(Vector3.Dot(p - a, ab) / len, 0f, 1f);
        return a + ab * t;
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static float SmoothStep(float x)
    {
        x = Math.Clamp(x, 0f, 1f);
        return x * x * (3f - 2f * x);
    }
}
