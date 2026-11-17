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
        public Task<Plan?>? Resculpting;   // the detail sliders' new values put on the plan in hand
        public volatile Plan? Plan;
        public volatile Snapshot? Snapshot;
        public long NextLook;
        public string Report = "";
        // The stomach bulge as it is now (eased toward what's wanted, following the partner's tip).
        public float BulgeLength, BulgeSide, BulgeStrength;
        public long BulgeTicks;
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
        // The detail sliders this plan has terms for, and the values on it now (changing them needs no new plan).
        public string[] SculptKeys = [];
        public float[] SculptMin = [], SculptMax = [];
        public float[] SculptValues = [];
        public Plan? From;                         // (resculpted: the plan it was made from)
        public SkinSurface? Surface;               // the skin the clothing is wrapped onto (null: none)
        public Limber.Rig.Bulge.Marks? Marks;      // the belly's landmarks at rest (for the stomach bulge)
    }

    // The detail sliders' sculpts on one model: every vertex they move, and its terms (see BodyShapeBuild.SculptTerm).
    private sealed class SculptPlan
    {
        public required int[] At;            // its position's byte offset in the vertex block
        public required byte[] Type;         // and how it's stored
        public required Vector3[] Orig;      // where the file has it
        public required Vector3[] Built;     // what's already built into the file (taken off)
        public required int[] Start;         // its terms: Start[e] to Start[e + 1]
        public required Limber.BodyShape.BodyShapeBuild.SculptTerm[] Terms;
    }

    private sealed class ModelPlan
    {
        public required string File;
        public required byte[] Original;   // the vertex block (LoD 0) as the file has it: what the GPU has
        public required byte[] Base;       // with the sculpts in: what's shown when nothing moves
        public required MeshPlan[] Meshes; // the meshes with vertices on the lattice
        public (int From, int To)[] Ranges = []; // the bytes those vertices' positions and normals span (written each frame)
        public bool Changed;               // Base differs from Original
        public SculptPlan? Sculpting;      // (null: nothing sculpted)
        public WrapPlan[] Wraps = [];      // its clothing wrapped onto the skin
        public int Moving => Meshes.Sum(m => m.Vertex.Length) + Wraps.Sum(m => m.Vertex.Length);
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
        // Where each vertex is in the file, and which of the model's sculpted vertices it is (-1: none): Rest is this
        // plus its sculpt.
        public Vector3[] Orig = [];
        public int[] SculptIndex = [];
        // Clothing following the skin: how far out from the skin point it follows it is (at rest), and how much it follows
        // (0: not). The lattice's bones are scaled with the shape sliders, which would scale that gap too (clothing sunk
        // into a smaller breast, floating off a bigger one): it's kept as it was instead.
        public Vector3[] Offset = [];
        public float[] Follow = [];

        public MeshPlan With(Vector3[] rest) => new()
        {
            Layout = Layout, Vertex = Vertex, Rest = rest, Normal = Normal, Start = Start, Mid = Mid, Slot = Slot, Weight = Weight,
            Ranges = Ranges, Share = Share, Grad = Grad, Orig = Orig, SculptIndex = SculptIndex, Offset = Offset, Follow = Follow,
        };
    }

    // The sculpts at these values: a new Base for each model and new rest places for its lattice vertices (new objects:
    // the render thread may be using the old ones).
    private static Plan Resculpt(Plan plan, float[] values)
    {
        var v = new float[values.Length];
        for (int i = 0; i < v.Length; i++)
            v[i] = float.IsFinite(values[i]) ? Math.Clamp(values[i], plan.SculptMin[i], plan.SculptMax[i]) : 0f;
        var models = new List<ModelPlan>(plan.Models.Count);
        foreach (var m in plan.Models)
        {
            if (m.Sculpting is not { } sp)
            {
                models.Add(m);
                continue;
            }
            var work = (byte[])m.Original.Clone();
            var move = new Vector3[sp.At.Length];
            bool changed = false;
            for (int e = 0; e < sp.At.Length; e++)
            {
                var d = -sp.Built[e];
                for (int t = sp.Start[e]; t < sp.Start[e + 1]; t++)
                {
                    var term = sp.Terms[t];
                    float x = term.Slider < v.Length ? v[term.Slider] : 0f;
                    if (x != 0f)
                        d += term.Dir * MathF.Max(term.Unit * x, term.Limit);
                }
                if (d.LengthSquared() <= 1e-12f || !MathUtil.IsFinite(d))
                    continue;
                move[e] = d;
                MdlRigger.WriteVector(work, sp.At[e], sp.Type[e], sp.Orig[e] + d);
                changed = true;
            }
            var meshes = new MeshPlan[m.Meshes.Length];
            for (int k = 0; k < meshes.Length; k++)
            {
                var mesh = m.Meshes[k];
                var rest = new Vector3[mesh.Vertex.Length];
                for (int i = 0; i < rest.Length; i++)
                {
                    int e = i < mesh.SculptIndex.Length ? mesh.SculptIndex[i] : -1;
                    rest[i] = (i < mesh.Orig.Length ? mesh.Orig[i] : mesh.Rest[i]) + (e >= 0 ? move[e] : Vector3.Zero);
                }
                meshes[k] = mesh.With(rest);
            }
            models.Add(new ModelPlan
            {
                File = m.File, Original = m.Original, Base = work, Meshes = meshes, Ranges = m.Ranges, Changed = changed, Sculpting = sp,
                Wraps = m.Wraps,
            });
        }
        return new Plan
        {
            Info = plan.Info, Rig = plan.Rig, RealBone = plan.RealBone, VirtualBone = plan.VirtualBone, RestInverse = plan.RestInverse,
            Models = models, Report = plan.Report, SculptKeys = plan.SculptKeys, SculptMin = plan.SculptMin, SculptMax = plan.SculptMax,
            SculptValues = values, From = plan.From ?? plan, Surface = plan.Surface, Marks = plan.Marks,
        };
    }

    private static float[] SculptValuesOf(string[] keys, IReadOnlyDictionary<string, float>? values)
    {
        var v = new float[keys.Length];
        for (int i = 0; i < keys.Length; i++)
            v[i] = values != null && values.TryGetValue(keys[i], out var x) && float.IsFinite(x) ? x : 0f;
        return v;
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
        public Vector3[]? Surface;   // the skin surface's points where this frame's bones put them (made when first needed)
        public Shape[] Colliders = [];  // colliders with Mesh collision on, in model space, each its strength thick
        public Limber.Rig.Bulge.Now? Bulge; // the stomach bulge this frame (null: none)
    }

    /// <summary>Colliders (Devices) in the world, this frame (set by the plugin).</summary>
    public static Func<PropWorld[]?> Props = () => null;

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

        // The detail sliders moved: the plan in hand with the new values (in the background; no new plan, no redraw).
        if (person.Resculpting is { IsCompleted: true } resculpted)
        {
            try
            {
                if (resculpted.Result is { } r && person.Plan is { } current && ReferenceEquals(r.From, current.From ?? current))
                    person.Plan = r;
            }
            catch
            {
            }
            person.Resculpting = null;
        }
        if (person.Resculpting == null && person.Making == null && person.Plan is { SculptKeys.Length: > 0 } inHand)
        {
            var values = SculptValuesOf(inHand.SculptKeys, want.Sculpt);
            if (!values.AsSpan().SequenceEqual(inHand.SculptValues))
                person.Resculpting = Task.Run(() => (Plan?)Resculpt(inHand, values));
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
        {
            if (Limber.Rig.Bulge.Wanted.ContainsKey(person.Address))
                Limber.Rig.Bulge.Status[person.Address] = "the live rig is getting this character ready" + (person.Report.Length > 0 ? $" ({person.Report})" : "");
            return;
        }
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
        // Colliders with Mesh collision on: the skin the live rig moves is pressed in where they touch it.
        var colliders = new List<Shape>();
        try
        {
            float toModel = 1f / MathF.Max(sk.RootScale.Y, 0.01f);
            var worldToModel = sk.WorldToModel;
            float global = Math.Clamp(LiveSoftBody.Strength, 0f, 1f);
            foreach (var prop in Props() ?? Array.Empty<PropWorld>())
                if (prop.Mesh && prop.MeshStrength * global > 0f)
                {
                    var shape = prop.Shape.Moved(x => Vector3.Transform(x, worldToModel), toModel);
                    colliders.Add(shape.WithRadius(shape.R * prop.MeshStrength * global));
                }
        }
        catch
        {
        }
        person.Snapshot = new Snapshot { Plan = plan, Skin = skin, Colliders = colliders.ToArray(), Bulge = BulgeNow(person, plan, sk, now) };
    }

    // The stomach bulge this frame: the settings, and (following) how deep the partner's tip is, eased.
    private static Limber.Rig.Bulge.Now? BulgeNow(Person person, Plan plan, in ActorSkeleton sk, long now)
    {
        if (!Limber.Rig.Bulge.Wanted.TryGetValue(person.Address, out var bs) || !bs.Enabled)
        {
            person.BulgeStrength = 0f;
            person.BulgeTicks = 0;
            Limber.Rig.Bulge.BreastPush.TryRemove(person.Address, out _);
            return null;
        }
        if (plan.Marks is not { } marks)
        {
            Limber.Rig.Bulge.Status[person.Address] = "couldn't find the hips and spine on this skeleton (j_asi_a_l / _r, j_kosi)";
            return null;
        }
        int bellyVertices = 0;
        foreach (var model in plan.Models)
            foreach (var mesh in model.Meshes)
                bellyVertices += mesh.Vertex.Length;
        if (bellyVertices == 0)
        {
            Limber.Rig.Bulge.Status[person.Address] = "the live rig has no belly skin on this character yet (getting it ready, or no body model it knows)";
            return null;
        }
        float dt = person.BulgeTicks == 0 ? 0f : Math.Clamp((now - person.BulgeTicks) / 1000f, 0f, 0.2f);
        person.BulgeTicks = now;
        float span = marks.NavelY - marks.PubisY;
        float maxLength = MathF.Max(Math.Clamp(bs.Length, 0.05f, 2f) * span, 0.01f);
        float sideSet = Math.Clamp(bs.Side, -1f, 1f) * marks.HipWidth * 0.17f * Math.Clamp(bs.Width, 0.2f, 3f);
        float length = maxLength, side = sideSet, strength = 1f;
        if (bs.Follow)
        {
            // Where the tip is, in the body at rest (through the pelvis: from how it's posed now back to its rest).
            strength = 0f;
            length = person.BulgeLength;
            if (Limber.Rig.Bulge.Tips.TryGetValue(person.Address, out var tip) && marks.PelvisBone >= 0 && marks.PelvisBone < sk.Info.Count)
            {
                var tipModel = Vector3.Transform(tip.Tip, sk.WorldToModel);
                var f = sk.GetModelFrame(marks.PelvisBone);
                var sc = sk.GetModelScale(marks.PelvisBone);
                var posed = Matrix4x4.CreateScale(sc) * Matrix4x4.CreateFromQuaternion(f.Rotation) * Matrix4x4.CreateTranslation(f.Position);
                if (Matrix4x4.Invert(posed, out var inv))
                {
                    var r = Vector3.Transform(tipModel, inv * marks.PelvisRest);
                    float start = marks.PubisY + Math.Clamp(bs.Start, -0.5f, 1f) * span;
                    float depth = r.Y - start;
                    float hw = marks.HipWidth;
                    bool inside = MathUtil.IsFinite(r) && MathF.Abs(r.X - marks.MidX) < hw * 0.8f && r.Z < marks.PelvisZ + hw * 0.9f
                                  && r.Z > marks.PelvisZ - hw * 0.7f && depth > -0.03f && depth < span * 2.5f;
                    if (inside)
                    {
                        length = Math.Clamp(depth, 0f, maxLength);
                        strength = Math.Clamp(depth / MathF.Max(hw * 0.25f, 0.01f), 0f, 1f);
                        side = sideSet + Math.Clamp(r.X - marks.MidX, -hw * 0.35f, hw * 0.35f);
                    }
                }
            }
            // (rises quickly, sinks a little slower, as if the skin lingers)
            float speed = Math.Clamp(bs.FollowSpeed, 0.1f, 5f);
            float Ease(float from, float to, float up, float down) => from + (to - from) * (1f - MathF.Exp(-dt * (to > from ? up : down) * speed));
            person.BulgeLength = Ease(person.BulgeLength, length, 14f, 7f);
            person.BulgeSide = Ease(person.BulgeSide, side, 10f, 10f);
            person.BulgeStrength = Ease(person.BulgeStrength, strength, 14f, 6f);
        }
        else
        {
            person.BulgeLength = length;
            person.BulgeSide = side;
            person.BulgeStrength = strength;
        }
        string follow = !bs.Follow ? "" : Limber.Rig.Bulge.Tips.ContainsKey(person.Address)
            ? $", following: tip {(person.BulgeStrength > 0.05f ? "inside" : "outside")}" : ", following: no partner's tip near the pelvis";
        Limber.Rig.Bulge.Status[person.Address] = $"on: {person.BulgeLength * 100f:0} cm long, {person.BulgeStrength * 100f:0}% strength, {bellyVertices} live skin vertices{follow}";
        if (person.BulgeStrength <= 0.002f || person.BulgeLength <= 0.002f)
        {
            Limber.Rig.Bulge.BreastPush.TryRemove(person.Address, out _);
            return null;
        }
        var made = Limber.Rig.Bulge.Make(marks, bs, person.BulgeLength, person.BulgeSide, person.BulgeStrength);
        // Long enough to reach up between the breasts: they're pushed apart and forward (by how far past the underside
        // of the breasts it reaches).
        if (float.IsFinite(marks.BreastY))
        {
            float reach = made.Y0 + made.Length + made.Width * made.Round * 0.5f;
            float under = marks.BreastY - marks.HipWidth * 0.3f;
            float past = Math.Clamp((reach - under) / MathF.Max(marks.HipWidth * 0.5f, 0.01f), 0f, 1.5f);
            float push = past * made.Amp * 0.8f;
            if (push > 0.0005f)
                Limber.Rig.Bulge.BreastPush[person.Address] = (push, person.Want.Index);
            else
                Limber.Rig.Bulge.BreastPush.TryRemove(person.Address, out _);
        }
        return made;
    }

    // ---- What a plan is made from (read in the bone update, used in the background)

    private sealed class Inputs
    {
        public required SkeletonInfo Info;
        public required (Vector3 Position, Quaternion Rotation, Vector3 Scale)[] Rest; // the skeleton at rest, model space
        // The body models' own rest per bone (the inverse of the bind matrices the game skins them with), when it isn't
        // the skeleton's: a body model made for another race (a body mod's Midlander body on a Roegadyn, a swapped body)
        // is fitted by the game with its racial deformer folded into those matrices, so its vertices are in that
        // race's shape, not this skeleton's. Everything the plan does is then in the model's own space (Rest too).
        public Matrix4x4?[]? Bind;
        public required List<string> Files;
        public required Dictionary<string, BreastRigShape> Placements;
        public required bool Fit, Physics;
        public required HashSet<string> RealParts; // parts whose lattice the skeleton already has (the rig mod)
        public required RigPart[] Parts;           // the parts wanted now
        public required Dictionary<string, float> Sculpt, Built;
        public required string Key;
        public bool Bulge;                         // a stomach bulge on: the belly's skin goes on the plan too
        // The body model the worn top replaces (resolved through the mod manager), read but never drawn: the skin the
        // clothing is wrapped onto, also where the top itself has no skin (under a bra's cups). Null: none.
        public string? BodyFile;

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
            // The body models' own rest (see Bind): the rest pose moved into their space, bone by bone.
            Matrix4x4?[]? bind = null;
            try
            {
                bind = ModelBinds((nint)obj->DrawObject, info, rest);
            }
            catch
            {
            }
            string bindKey = "";
            if (bind != null)
            {
                float sum = 0f;
                for (int i = 0; i < info.Count; i++)
                {
                    if (bind[i] is not { } e)
                        continue;
                    sum += e.M41 * 3.1f + e.M42 * 1.7f + e.M43 * 2.3f + e.M11 + e.M22 + e.M33;
                    if (Matrix4x4.Decompose(e, out var sc, out var ro, out var tr) && MathUtil.IsFinite(sc) && MathUtil.IsFinite(tr))
                        rest[i] = (tr, Quaternion.Normalize(ro), sc);
                    else
                        rest[i] = (e.Translation, rest[i].Item2, rest[i].Item3);
                }
                bindKey = $"fit{sum:0.####}";
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
                        if (float.IsFinite(v) && Limber.BodyShape.BodySliders.ByKey.TryGetValue(k, out var s) && s.Morphs.Length > 0)
                            d[k] = v;
                return d;
            }
            var sculpt = Sculpts(want.Sculpt);
            var built = Sculpts(want.Built);
            foreach (var k in built.Where(kv => kv.Value == 0f).Select(kv => kv.Key).ToList())
                built.Remove(k);
            bool fit = AutoFit();
            string? bodyFile = null;
            try
            {
                if (!files.Any(f => f.Replace('\\', '/').Contains("/obj/body/", StringComparison.OrdinalIgnoreCase)))
                {
                    var top = files.FirstOrDefault(f => System.Text.RegularExpressions.Regex.IsMatch(f, @"c(\d{4})e\d{4}_top", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
                    var race = top == null ? null : System.Text.RegularExpressions.Regex.Match(top, @"c(\d{4})e\d{4}_top", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Groups[1].Value;
                    // (only under real clothing: a top that is all skin, the nude body a body mod puts on the "no top"
                    // slot, is the body itself, and wrapping anything onto another body model would print that
                    // model's shape, its underwear's dents, into it)
                    bool clothing = false;
                    try
                    {
                        clothing = top != null && LiveRig.Read(top) is { } topData && MdlRigger.MeshMaterials(topData).Any(m => !MdlRigger.IsSkinMaterial(m));
                    }
                    catch
                    {
                    }
                    if (race != null && clothing)
                    {
                        var game = $"chara/human/c{race}/obj/body/b0001/model/c{race}b0001_top.mdl";
                        string? resolved = null;
                        try
                        {
                            resolved = Plugin.PluginInterface.GetIpcSubscriber<string, int, string>("Penumbra.ResolveGameObjectPath")
                                .InvokeFunc(game, obj->ObjectIndex);
                        }
                        catch
                        {
                        }
                        bodyFile = string.IsNullOrEmpty(resolved) ? game : resolved;
                    }
                }
            }
            catch
            {
            }
            static string Of(Dictionary<string, float> d) => string.Join(",", d.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value:0.###}"));
            string key = string.Join("|",
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(info), info.Count,
                string.Join(";", files.OrderBy(f => f, StringComparer.Ordinal)),
                want.Physics ? "p" : "-", fit ? "f" : "-", string.Join(",", real.OrderBy(x => x)), string.Join(",", parts.Select(p => p.Key)),
                string.Join(";", placements.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + ":" + ShapeKey(kv.Value))),
                string.Join(",", sculpt.Keys.OrderBy(k => k, StringComparer.Ordinal)), Of(built), bodyFile ?? "", bindKey,
                Limber.Rig.Bulge.Wanted.ContainsKey(want.Address) ? "bulge" : "");
            return new Inputs
            {
                Bulge = Limber.Rig.Bulge.Wanted.ContainsKey(want.Address),
                Info = info, Rest = rest, Bind = bind, Files = files, Placements = placements, Fit = fit, Physics = want.Physics, RealParts = real, Parts = parts,
                Sculpt = sculpt, Built = built, Key = key, BodyFile = bodyFile,
            };
        }
    }

    // The body models' rest per skeleton bone: the inverse of the bind matrices the game skins the body, hands, legs and
    // feet with (each model's bone tables, +0xB8: one 3x4 per bone; +0xC0: which bone, partial << 12 | index). For a
    // model of this skeleton's own race they're its rest pose and nothing changes (null); a model fitted from another
    // race's has the racial deformer in them. Bones none of the models use go with their parent's fitting.
    private static Matrix4x4?[]? ModelBinds(nint draw, SkeletonInfo info, (Vector3 Position, Quaternion Rotation, Vector3 Scale)[] rest)
    {
        var cb = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase*)draw;
        if (cb == null || cb->Skeleton == null || cb->Models == null)
            return null;
        var names = Limber.Rig.BodySwap.BodyBoneNames(cb);
        if (names == null)
            return null;
        int slotCount = cb->SlotCount;
        if (slotCount <= 0 || slotCount > 64)
            return null;
        var found = new Dictionary<int, float[]>();
        // (1 body, 2 hands, 3 legs, 4 feet: the body's models; the head's are its own race's)
        for (int slot = 1; slot <= 4 && slot < slotCount; slot++)
        {
            nint model = SafeMemory.ReadPointer((nint)(cb->Models + slot));
            if (!SafeMemory.Plausible(model))
                continue;
            nint list = SafeMemory.ReadPointer(model + 0x58);
            int tables = SafeMemory.Read<int>(model + 0x60, 0);
            if (!SafeMemory.Plausible(list) || tables <= 0 || tables > 16)
                continue;
            for (int t = 0; t < tables; t++)
            {
                nint table = list + t * 0xE0;
                nint matrices = SafeMemory.ReadPointer(table + 0xB8);
                nint refsAt = SafeMemory.ReadPointer(table + 0xC0);
                int count = SafeMemory.Read<int>(table + 0xC8, 0);
                if (!SafeMemory.Plausible(matrices) || !SafeMemory.Plausible(refsAt) || count <= 0 || count > 1024)
                    continue;
                var floats = new float[count * 12];
                var refs = new ushort[count];
                fixed (float* to = floats)
                    if (!SafeMemory.Copy(matrices, to, count * 48))
                        continue;
                fixed (ushort* to = refs)
                    if (!SafeMemory.Copy(refsAt, to, count * 2))
                        continue;
                for (int e = 0; e < count; e++)
                {
                    int partial = refs[e] >> 12, bone = refs[e] & 0xFFF;
                    if (partial != 0 || bone >= names.Length)
                        continue;
                    int at = info.IndexOf(names[bone]);
                    if (at < 0 || at >= rest.Length || found.ContainsKey(at))
                        continue;
                    var one = new float[12];
                    Array.Copy(floats, e * 12, one, 0, 12);
                    found[at] = one;
                }
            }
        }
        if (found.Count == 0)
            return null;

        Matrix4x4 Own(int i) => Matrix4x4.CreateScale(rest[i].Scale) * Matrix4x4.CreateFromQuaternion(rest[i].Rotation) * Matrix4x4.CreateTranslation(rest[i].Position);
        static float Diff(in Matrix4x4 a, in Matrix4x4 b)
        {
            var d = a - b;
            return MathF.Abs(d.M11) + MathF.Abs(d.M12) + MathF.Abs(d.M13) + MathF.Abs(d.M21) + MathF.Abs(d.M22) + MathF.Abs(d.M23)
                 + MathF.Abs(d.M31) + MathF.Abs(d.M32) + MathF.Abs(d.M33) + MathF.Abs(d.M41) + MathF.Abs(d.M42) + MathF.Abs(d.M43);
        }
        // How the 3x4s are laid out: whichever reads closer to the skeleton's own inverse rest.
        float errA = 0f, errB = 0f;
        foreach (var (i, f) in found)
        {
            if (!Matrix4x4.Invert(Own(i), out var inv))
                continue;
            errA += Diff(Limber.Rig.BodySwap.ToRowVector(f, 0, true), inv);
            errB += Diff(Limber.Rig.BodySwap.ToRowVector(f, 0, false), inv);
        }
        bool columns = errA <= errB;

        var result = new Matrix4x4?[info.Count];
        var fit = new Matrix4x4?[info.Count]; // the model's space -> the skeleton's, per bone (null: the same)
        bool any = false;
        for (int i = 0; i < info.Count && i < rest.Length; i++)
        {
            var own = Own(i);
            if (found.TryGetValue(i, out var f))
            {
                var inverseBind = Limber.Rig.BodySwap.ToRowVector(f, 0, columns);
                if (!MathUtil.IsFinite(inverseBind.Translation) || !Matrix4x4.Invert(inverseBind, out var model))
                    continue;
                var d = inverseBind * own;
                if (Diff(d, Matrix4x4.Identity) < 1e-4f)
                    continue;
                result[i] = model;
                fit[i] = d;
                any = true;
            }
            else
            {
                int p = info.Parents[i];
                if (p >= 0 && p < i && fit[p] is { } pd && Matrix4x4.Invert(pd, out var undo))
                {
                    result[i] = own * undo;
                    fit[i] = pd;
                }
            }
        }
        return any ? result : null;
    }

    private static string ShapeKey(BreastRigShape s)
        => $"{s.Size:0.####},{s.Height:0.####},{s.Out:0.####},{s.Up:0.####},{s.Forward:0.####},{s.Depth:0.####},{s.HasNipple},{s.NippleOut:0.####},{s.NippleUp:0.####},{s.NippleForward:0.####}";

    private static bool IsGenitalBone(string bone)
        => bone.StartsWith("iv_ochinko", StringComparison.Ordinal) || bone.StartsWith("iv_funyachin", StringComparison.Ordinal)
        || bone.StartsWith("iv_kougan", StringComparison.Ordinal) || bone.StartsWith("iv_kintama", StringComparison.Ordinal);

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
    // (the lattice weights in full: every weight a vertex wants, no 8-bone or bone-table limit, see MdlRigger.RigSkins)
    private static readonly ConcurrentDictionary<string, List<MdlRigger.MeshSkin>?> RigCache = new();

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
            if (inp.Bind != null && i < inp.Bind.Length && inp.Bind[i] is { } fitted)
                return fitted;
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
        var sculptKeys = inp.Sculpt.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
        var sculpt = sculptKeys.Length > 0 ? Limber.BodyShape.BodyShapeBuild.SculptBasis(sculptKeys, rest) : null;
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

        // The stomach bulge: the front of the belly, at rest (its skin goes on the plan whether a lattice takes it or not).
        var marks = Limber.Rig.Bulge.MarksFrom(n => rest.TryGetValue(n, out var r) ? ((Vector3, Quaternion, Vector3)?)(r.Position, r.Rotation, r.Scale) : null, info.IndexOf);
        Func<Vector3, bool>? bulgeZone = null;
        if (inp.Bulge && marks != null)
        {
            float span = marks.NavelY - marks.PubisY, hw = marks.HipWidth;
            float y0 = marks.PubisY - span * 0.45f, y1 = marks.PubisY + span * 2.1f, z0 = marks.PelvisZ - hw * 0.05f;
            bulgeZone = pt => pt.Y > y0 && pt.Y < y1 && pt.Z > z0 && MathF.Abs(pt.X - marks.MidX) < hw * 0.95f;
        }
        var plans = new List<ModelPlan>();
        int movingTotal = 0, shapedTotal = 0;
        var idx = new byte[8];
        var w = new float[8];
        // The skin the clothing is wrapped onto: the worn models' skin meshes, and the body model the top replaces (read,
        // never drawn), each as the lattice weights it.
        var builder = new SurfaceBuilder();
        foreach (var (file, data) in models)
            try
            {
                builder.AddSkin(data, LatticeSkins(file, data, sides, sidesKey), SlotOf, onlySkin: true);
            }
            catch
            {
            }
        if (inp.BodyFile != null && Read(inp.BodyFile) is { } bodyData)
            try
            {
                builder.AddSkin(bodyData, LatticeSkins(inp.BodyFile, bodyData, sides, sidesKey), SlotOf, onlySkin: false);
            }
            catch
            {
            }
        var surface = builder.Count > 0 ? builder.Build() : null;
        int wrappedTotal = 0;
        foreach (var (file, data) in models)
        {
            try
            {
                var plan = MakeModel(file, data, sides, sidesKey, sculpt, builtSculpt, SlotOf, idx, w, ref movingTotal, ref shapedTotal, null, surface, bulgeZone);
                if (plan != null)
                    wrappedTotal += plan.Wraps.Sum(x => x.Vertex.Length);
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
        if (wrappedTotal > 0)
            report.Add($"{wrappedTotal} clothing vertices wrapped onto the skin ({surface!.Count} skin triangles{(inp.BodyFile != null ? ", the body under the top included" : "")})");
        if (shapedTotal > 0)
            report.Add($"{shapedTotal} vertices sculpted");
        if (plans.Count == 0 && inp.Physics && sides.Count == 0)
            report.Add("none of the worn models has breast, butt, testicle or belly bones Limber knows");
        var made = new Plan
        {
            Info = info, Rig = rig, RealBone = realBone, VirtualBone = virtualBone, RestInverse = restInverse, Models = plans, Surface = surface,
            Report = "Live rig: " + (report.Count > 0 ? string.Join("; ", report) : "nothing to do on what's worn") + ".",
            Marks = marks,
        };
        if (sculpt == null)
            return Resculpt(made, []);
        made.SculptKeys = sculptKeys;
        made.SculptMin = sculptKeys.Select(k => Limber.BodyShape.BodySliders.ByKey[k].Min).ToArray();
        made.SculptMax = sculptKeys.Select(k => Limber.BodyShape.BodySliders.ByKey[k].Max).ToArray();
        // (the sculpts at the values asked for; later values go on with Resculpt, no new plan)
        return Resculpt(made, SculptValuesOf(sculptKeys, inp.Sculpt));
    }

    private static string ShortName(string file) => System.IO.Path.GetFileName(file.Split('|').Last());

    /// <summary>How many of a model's meshes are skin (the body's own material), not clothing.</summary>
    private static int SkinMeshes(byte[] data)
    {
        try
        {
            return MdlRigger.MeshMaterials(data).Count(MdlRigger.IsSkinMaterial);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// The body's skin as weighted onto the lattice (each vertex: where it is at rest, its lattice bones' weights and their
    /// share), for what's worn over it: a piece of clothing near the skin takes the skin's lattice weights, so it moves and
    /// is shaped (size, blend shapes, physics) with the breast under it instead of by its own weights (whose edges are
    /// often on the spine: the body then came through it).
    /// </summary>
    private sealed class SkinRef
    {
        private readonly List<Vector3> _pos = new();
        private readonly List<(string Bone, float Weight)[]> _lattice = new();
        private readonly List<float> _share = new();
        private readonly Dictionary<(int, int, int), List<int>> _grid = new();
        private const float Cell = 0.03f;
        public const float Full = 0.015f, None = 0.06f;   // full within this of the skin, none past this

        public int Count => _pos.Count;

        public void Add(Vector3 p, (string, float)[] lattice, float share)
        {
            _pos.Add(p);
            _lattice.Add(lattice);
            _share.Add(share);
        }

        public void Finish()
        {
            _grid.Clear();
            for (int i = 0; i < _pos.Count; i++)
            {
                var c = CellOf(_pos[i]);
                if (!_grid.TryGetValue(c, out var list))
                    _grid[c] = list = new List<int>();
                list.Add(i);
            }
        }

        private static (int, int, int) CellOf(Vector3 p) => ((int)MathF.Floor(p.X / Cell), (int)MathF.Floor(p.Y / Cell), (int)MathF.Floor(p.Z / Cell));

        /// <summary>The nearest skin vertex within reach, and how much it governs this point (1 near the skin, 0 past reach).</summary>
        public (float Follow, int Index) Near(Vector3 p)
        {
            var (cx, cy, cz) = CellOf(p);
            int best = -1;
            float bestD = None * None;
            for (int x = cx - 2; x <= cx + 2; x++)
                for (int y = cy - 2; y <= cy + 2; y++)
                    for (int z = cz - 2; z <= cz + 2; z++)
                        if (_grid.TryGetValue((x, y, z), out var list))
                            foreach (int i in list)
                            {
                                float d = Vector3.DistanceSquared(p, _pos[i]);
                                if (d < bestD)
                                {
                                    bestD = d;
                                    best = i;
                                }
                            }
            if (best < 0)
                return (0f, -1);
            float u = Math.Clamp((MathF.Sqrt(bestD) - Full) / (None - Full), 0f, 1f);
            return (1f - u * u * (3f - 2f * u), best);
        }

        public float Share(int i) => _share[i];
        public Vector3 Position(int i) => _pos[i];
        public (string Bone, float Weight)[] Lattice(int i) => _lattice[i];
    }

    private static ModelPlan? MakeModel(string file, byte[] data, List<MdlRigger.Side> sides, string sidesKey,
        Action<Vector3, string[], byte[], float[], int, List<Limber.BodyShape.BodyShapeBuild.SculptTerm>>? sculpt,
        Func<Vector3, string[], byte[], float[], int, Vector3>? builtSculpt, Func<string, int> slotOf, byte[] idx, float[] w, ref int movingTotal, ref int shapedTotal,
        SkinRef? skinRef = null, SkinSurface? surface = null, Func<Vector3, bool>? bulgeZone = null)
    {
        string[] materials;
        try
        {
            materials = MdlRigger.MeshMaterials(data);
        }
        catch
        {
            materials = [];
        }
        bool SkinMesh(int mesh) => mesh < materials.Length && MdlRigger.IsSkinMaterial(materials[mesh]);
        var (offset, size, layouts) = MdlRigger.Layout(data);
        if (size[0] == 0 || offset[0] + size[0] > data.Length)
            return null;
        var original = data.AsSpan((int)offset[0], (int)size[0]).ToArray();
        var skins = MdlRigger.Skin(data, 0);
        // A genital model worn as its own item (an IVCS accessory like the Junker): its own bones move and size it,
        // so it's left to the game. (Put on the lattice, its base was held at the body's size and pose while the
        // shaft was scaled from its base: pinched in at the body.)
        if (!file.Replace('\\', '/').Contains("/obj/body/", StringComparison.OrdinalIgnoreCase)
            && skins.Any(k => k.Bones.Any(IsGenitalBone)))
            return null;
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
            onLattice = RigCache.GetOrAdd(key, _ =>
            {
                try
                {
                    return MdlRigger.RigSkins(data, todo, new List<string>());
                }
                catch
                {
                    return null;
                }
            });
        }
        if (onLattice == null && sculpt == null && builtSculpt == null && surface == null && bulgeZone == null
            && (skinRef == null || (skinRef.Count == 0 && !materials.Any(MdlRigger.IsSkinMaterial))))
            return null;
        var wraps = new List<WrapPlan>();

        var work = (byte[])original.Clone();
        bool changed = false;
        var meshes = new List<MeshPlan>();
        // The sculpted vertices (their values go on in Resculpt).
        var sAt = new List<int>();
        var sType = new List<byte>();
        var sOrig = new List<Vector3>();
        var sBuilt = new List<Vector3>();
        var sStart = new List<int>();
        var sTerms = new List<Limber.BodyShape.BodyShapeBuild.SculptTerm>();
        // (the skin's meshes first, so the clothing's in the same model can follow them)
        foreach (var layout in layouts.Where(l => l.Lod == 0).OrderBy(l => SkinMesh(l.Mesh) ? 0 : 1))
        {
            var skin = skins.FirstOrDefault(k => k.Mesh == layout.Mesh);
            if (skin == null)
                continue;
            bool isSkin = SkinMesh(layout.Mesh);
            var capture = isSkin ? skinRef : null;
            var follow = !isSkin && skinRef is { Count: > 0 } ? skinRef : null;
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
            var origs = new List<Vector3>();
            var sculptIndex = new List<int>();
            var offsets = new List<Vector3>();
            var follows = new List<float>();
            bool anyFollow = false;
            var wrap = surface != null && !isSkin ? new WrapBuilder(layout) : null;
            int posFrom = int.MaxValue, posTo = 0, nrmFrom = int.MaxValue, nrmTo = 0;
            for (int v = 0; v < layout.VertexCount; v++)
            {
                int pa = layout.PositionAt + v * layout.PositionStride;
                if (pa + 12 > original.Length)
                    break;
                var p = MdlRigger.ReadVector(original, pa, layout.PositionType);
                for (int k = 0; k < 8; k++)
                {
                    idx[k] = skin.Indices[v * skin.Stride + k];
                    w[k] = skin.Weights[v * skin.Stride + k];
                }
                // The sculpts (a blend shape: the same move for this vertex every frame), as terms per slider, so new
                // values go on with no new plan.
                int sculptEntry = -1;
                if (sculpt != null || builtSculpt != null)
                {
                    int t0 = sTerms.Count;
                    sculpt?.Invoke(p, skin.Bones, idx, w, skin.Slots, sTerms);
                    var baked = builtSculpt?.Invoke(p, skin.Bones, idx, w, skin.Slots) ?? Vector3.Zero;
                    if (!MathUtil.IsFinite(baked))
                        baked = Vector3.Zero;
                    if (sTerms.Count > t0 || baked.LengthSquared() > 1e-12f)
                    {
                        sculptEntry = sAt.Count;
                        sAt.Add(pa);
                        sType.Add(layout.PositionType);
                        sOrig.Add(p);
                        sBuilt.Add(baked);
                        sStart.Add(t0);
                        changed = true;
                        shapedTotal++;
                    }
                }

                allPos[v] = p;
                // Clothing near the skin: wrapped onto it (see WrapPlan) instead of moved by the lattice itself.
                if (wrap != null)
                {
                    var nrm0 = layout.NormalAt >= 0 ? MdlRigger.ReadVector(original, layout.NormalAt + v * layout.NormalStride, layout.NormalType) : Vector3.Zero;
                    if (wrap.TryAdd(surface!, v, p, nrm0, skin, idx, w, rskin, slotOf))
                    {
                        allShare[v] = 0f;
                        continue;
                    }
                }
                // (worn over the body: how much it follows the skin under it)
                var (fol, near) = follow?.Near(p) ?? (0f, -1);
                // On the lattice: weighted to its bones now.
                float lat = 0f;
                if (rskin != null && latticeBone != null)
                    for (int k = 0; k < rskin.Slots; k++)
                        if (rskin.Weights[v * rskin.Stride + k] > 0f && rskin.Indices[v * rskin.Stride + k] < latticeBone.Length && latticeBone[rskin.Indices[v * rskin.Stride + k]])
                            lat += rskin.Weights[v * rskin.Stride + k];
                if (capture != null)
                {
                    var latW = new List<(string, float)>();
                    if (rskin != null && latticeBone != null)
                        for (int k = 0; k < rskin.Slots; k++)
                            if (rskin.Weights[v * rskin.Stride + k] > 0f && rskin.Indices[v * rskin.Stride + k] < latticeBone.Length && latticeBone[rskin.Indices[v * rskin.Stride + k]])
                                latW.Add((rskin.Bones[rskin.Indices[v * rskin.Stride + k]], rskin.Weights[v * rskin.Stride + k]));
                    // (a body already weighted onto the lattice, by the rig mod: its own weights are the lattice's)
                    float ownLat = 0f;
                    if (rskin == null)
                        for (int k = 0; k < skin.Slots; k++)
                            if (skin.Weights[v * skin.Stride + k] > 0f && skin.Indices[v * skin.Stride + k] < skin.Bones.Length && RigParts.IsRigBone(skin.Bones[skin.Indices[v * skin.Stride + k]]))
                            {
                                latW.Add((skin.Bones[skin.Indices[v * skin.Stride + k]], skin.Weights[v * skin.Stride + k]));
                                ownLat += skin.Weights[v * skin.Stride + k];
                            }
                    capture.Add(p, latW.ToArray(), rskin == null ? ownLat : lat);
                }
                // The stomach bulge's skin: moved by nothing but the bulge (its own bones as they are), if it isn't on
                // the lattice already.
                void BulgeOnly()
                {
                    if (bulgeZone == null || !bulgeZone(p))
                        return;
                    int b0 = slot.Count;
                    for (int k = 0; k < skin.Slots; k++)
                    {
                        float wk = skin.Weights[v * skin.Stride + k];
                        int bone = skin.Indices[v * skin.Stride + k];
                        if (wk <= 0f || bone >= skin.Bones.Length)
                            continue;
                        int sl = slotOf(skin.Bones[bone]);
                        if (sl < 0)
                        {
                            slot.RemoveRange(b0, slot.Count - b0);
                            weight.RemoveRange(b0, weight.Count - b0);
                            return;
                        }
                        slot.Add(sl);
                        weight.Add(wk);
                    }
                    if (slot.Count == b0)
                        return;
                    vertex.Add(v);
                    restList.Add(p);
                    origs.Add(p);
                    offsets.Add(Vector3.Zero);
                    follows.Add(0f);
                    sculptIndex.Add(sculptEntry);
                    shares.Add(0f);
                    normals.Add(layout.NormalAt >= 0 ? MdlRigger.ReadVector(original, layout.NormalAt + v * layout.NormalStride, layout.NormalType) : Vector3.Zero);
                    start.Add(b0);
                    mid.Add(slot.Count);
                    posFrom = Math.Min(posFrom, pa);
                    posTo = Math.Max(posTo, pa + 16);
                    if (layout.NormalAt >= 0)
                    {
                        int na = layout.NormalAt + v * layout.NormalStride;
                        nrmFrom = Math.Min(nrmFrom, na);
                        nrmTo = Math.Max(nrmTo, na + 16);
                    }
                }
                if (near >= 0)
                    lat = lat * (1f - fol) + follow!.Share(near) * fol;
                else if (rskin == null || latticeBone == null)
                {
                    BulgeOnly();
                    continue;
                }
                allShare[v] = lat;
                if (lat <= 0f)
                {
                    BulgeOnly();
                    continue;
                }
                int s0 = slot.Count;
                bool ok = true;
                for (int k = 0; k < skin.Slots && ok; k++)
                {
                    float wk = skin.Weights[v * skin.Stride + k];
                    int bone = skin.Indices[v * skin.Stride + k];
                    if (wk <= 0f || bone >= skin.Bones.Length)
                        continue;
                    int sl = slotOf(skin.Bones[bone]);
                    ok = sl >= 0;
                    slot.Add(sl);
                    weight.Add(wk);
                }
                int m0 = slot.Count;
                // the new weights: the model's own lattice weighting, eased toward the skin's under it
                float keep = near >= 0 ? 1f - fol : 1f;
                if (rskin != null && keep > 0f)
                    for (int k = 0; k < rskin.Slots && ok; k++)
                    {
                        float wk = rskin.Weights[v * rskin.Stride + k];
                        int bone = rskin.Indices[v * rskin.Stride + k];
                        if (wk <= 0f || bone >= rskin.Bones.Length)
                            continue;
                        int sl = slotOf(rskin.Bones[bone]);
                        ok = sl >= 0;
                        slot.Add(sl);
                        weight.Add(wk * keep);
                    }
                else if (keep > 0f)
                    for (int k = s0; k < m0; k++)
                    {
                        slot.Add(slot[k]);
                        weight.Add(weight[k] * keep);
                    }
                if (near >= 0 && fol > 0f && ok)
                {
                    // (its own bones for the part the skin's lattice doesn't take, the skin's lattice bones for the rest)
                    float own = 0f;
                    for (int k = s0; k < m0; k++)
                        own += weight[k];
                    float skinShare = follow!.Share(near);
                    for (int k = s0; k < m0; k++)
                    {
                        slot.Add(slot[k]);
                        weight.Add(weight[k] / MathF.Max(own, 1e-6f) * (1f - skinShare) * fol);
                    }
                    foreach (var (bn, bw) in follow.Lattice(near))
                    {
                        int sl = slotOf(bn);
                        if (sl < 0)
                        {
                            ok = false;
                            break;
                        }
                        slot.Add(sl);
                        weight.Add(bw * fol);
                    }
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
                    BulgeOnly();
                    continue;
                }
                vertex.Add(v);
                restList.Add(p);
                origs.Add(p);
                offsets.Add(near >= 0 ? p - follow!.Position(near) : Vector3.Zero);
                follows.Add(near >= 0 ? fol : 0f);
                anyFollow |= near >= 0 && fol > 0f;
                sculptIndex.Add(sculptEntry);
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
            if (capture != null)
                capture.Finish();
            if (wrap is { Count: > 0 })
                wraps.Add(wrap.Build(original.Length));
            if (vertex.Count == 0)
                continue;
            start.Add(slot.Count);
            meshes.Add(new MeshPlan
            {
                Layout = layout, Vertex = vertex.ToArray(), Rest = restList.ToArray(), Normal = normals.ToArray(),
                Start = start.ToArray(), Mid = mid.ToArray(), Slot = slot.ToArray(), Weight = weight.ToArray(),
                Ranges = MergeRanges([(posFrom, Math.Min(posTo, original.Length)), (nrmFrom, Math.Min(nrmTo, original.Length))]),
                Share = shares.ToArray(), Grad = ShareGradients(allPos, allShare, vertex, normals),
                Orig = origs.ToArray(), SculptIndex = sculptIndex.ToArray(),
                Offset = anyFollow ? offsets.ToArray() : [], Follow = anyFollow ? follows.ToArray() : [],
            });
            movingTotal += vertex.Count;
        }
        if (!changed && meshes.Count == 0 && wraps.Count == 0)
            return null;
        return new ModelPlan
        {
            File = file, Original = original, Base = work, Meshes = meshes.ToArray(), Changed = changed, Wraps = wraps.ToArray(),
            Sculpting = sAt.Count == 0 ? null : new SculptPlan
            {
                At = sAt.ToArray(), Type = sType.ToArray(), Orig = sOrig.ToArray(), Built = sBuilt.ToArray(),
                Start = sStart.Append(sTerms.Count).ToArray(), Terms = sTerms.ToArray(),
            },
            Ranges = MergeRanges(meshes.SelectMany(m => m.Ranges).Concat(wraps.SelectMany(x => x.Ranges)).ToList()),
        };
    }

    // ---- Clothing wrapped onto the skin (as a "surface deform" / skin wrap does in Blender, Maya, VaM)
    //
    // Each piece of clothing near the skin is bound, at rest, to the nearest few skin triangles: where on each triangle it
    // sits and how far off it (in the triangle's own directions). Each frame the skin is placed where its bones (and the
    // lattice) put it, and the clothing goes where its triangles are now, at the same distance off them, turned with
    // them: so it follows everything the rig does to the skin (size, shapes, sculpts, physics) the same way, and never
    // sinks into it or floats off it. The skin is the worn models' own skin, and the body model the top replaces (read,
    // never drawn: many tops have no skin under the clothing).

    /// <summary>The worn models' skin as weighted onto the lattice (or their own weights when nothing is to be done).</summary>
    private static List<MdlRigger.MeshSkin> LatticeSkins(string file, byte[] data, List<MdlRigger.Side> sides, string sidesKey)
    {
        var skins = MdlRigger.Skin(data, 0);
        var have = skins.SelectMany(k => k.Bones).Where(RigParts.IsRigBone).ToHashSet(StringComparer.Ordinal);
        var todo = have.Count == 0 ? sides
            : sides.Where(sd => RigParts.All.FirstOrDefault(p => p.Key == sd.PartKey) is not { } part || !have.Any(part.IsMine)).ToList();
        if (todo.Count == 0)
            return skins;
        var key = file + "|" + data.Length + "|" + sidesKey + "|" + string.Join(",", todo.Select(sd => sd.PartKey).Distinct());
        if (RigCache.Count > 32)
            RigCache.Clear();
        var on = RigCache.GetOrAdd(key, _ =>
        {
            try
            {
                return MdlRigger.RigSkins(data, todo, new List<string>());
            }
            catch
            {
                return null;
            }
        });
        if (on == null)
            return skins;
        // (each mesh the lattice reweighted, the others as they were)
        return skins.Select(k => on.FirstOrDefault(o => o.Mesh == k.Mesh) ?? k).ToList();
    }

    private sealed class SkinSurface
    {
        public required Vector3[] P;        // each point at rest
        public required int[] Tri;          // 3 points per triangle
        public required int[] WStart;       // each point's weights: WStart[v] to WStart[v + 1]
        public required int[] WSlot;
        public required float[] WWeight;
        private readonly Dictionary<(int, int, int), List<int>> _grid = new();
        private const float Cell = 0.03f;

        public int Count => Tri.Length / 3;

        public void Index()
        {
            for (int t = 0; t < Count; t++)
            {
                var c = (P[Tri[t * 3]] + P[Tri[t * 3 + 1]] + P[Tri[t * 3 + 2]]) / 3f;
                var k = CellOf(c);
                if (!_grid.TryGetValue(k, out var list))
                    _grid[k] = list = new List<int>();
                list.Add(t);
            }
        }

        private static (int, int, int) CellOf(Vector3 p) => ((int)MathF.Floor(p.X / Cell), (int)MathF.Floor(p.Y / Cell), (int)MathF.Floor(p.Z / Cell));

        /// <summary>The nearest few triangles within reach: each with how far, and where on it (its corners' shares).</summary>
        public void Nearest(Vector3 p, float reach, List<(int Tri, float Dist, Vector3 Bary)> into, int most)
        {
            into.Clear();
            var (cx, cy, cz) = CellOf(p);
            int span = (int)MathF.Ceiling(reach / Cell) + 1;
            for (int x = cx - span; x <= cx + span; x++)
                for (int y = cy - span; y <= cy + span; y++)
                    for (int z = cz - span; z <= cz + span; z++)
                        if (_grid.TryGetValue((x, y, z), out var list))
                            foreach (int t in list)
                            {
                                var bary = Closest(p, P[Tri[t * 3]], P[Tri[t * 3 + 1]], P[Tri[t * 3 + 2]], out var at);
                                float d = Vector3.Distance(p, at);
                                if (d > reach)
                                    continue;
                                if (into.Count < most)
                                    into.Add((t, d, bary));
                                else
                                {
                                    int worst = 0;
                                    for (int i = 1; i < into.Count; i++)
                                        if (into[i].Dist > into[worst].Dist)
                                            worst = i;
                                    if (d < into[worst].Dist)
                                        into[worst] = (t, d, bary);
                                }
                            }
            into.Sort((a, b) => a.Dist.CompareTo(b.Dist));
        }

        /// <summary>The point of the triangle closest to p, as its corners' shares.</summary>
        public static Vector3 Closest(Vector3 p, Vector3 a, Vector3 b, Vector3 c, out Vector3 at)
        {
            var ab = b - a;
            var ac = c - a;
            var ap = p - a;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f) { at = a; return new Vector3(1, 0, 0); }
            var bp = p - b;
            float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3) { at = b; return new Vector3(0, 1, 0); }
            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f)
            {
                float v = d1 / (d1 - d3);
                at = a + ab * v;
                return new Vector3(1 - v, v, 0);
            }
            var cp = p - c;
            float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6) { at = c; return new Vector3(0, 0, 1); }
            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f)
            {
                float w = d2 / (d2 - d6);
                at = a + ac * w;
                return new Vector3(1 - w, 0, w);
            }
            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f)
            {
                float w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
                at = b + (c - b) * w;
                return new Vector3(0, 1 - w, w);
            }
            float denom = 1f / (va + vb + vc);
            float vv = vb * denom, ww = vc * denom;
            at = a + ab * vv + ac * ww;
            return new Vector3(1 - vv - ww, vv, ww);
        }

        /// <summary>A triangle's directions: along its first edge, out of it, and across.</summary>
        public static bool Frame(Vector3 a, Vector3 b, Vector3 c, out Vector3 e1, out Vector3 n, out Vector3 t)
        {
            e1 = b - a;
            n = Vector3.Cross(b - a, c - a);
            t = Vector3.Zero;
            if (e1.LengthSquared() < 1e-14f || n.LengthSquared() < 1e-18f)
                return false;
            e1 = Vector3.Normalize(e1);
            n = Vector3.Normalize(n);
            t = Vector3.Cross(n, e1);
            return true;
        }

        /// <summary>Each point where this frame's bones put it.</summary>
        public Vector3[] Place(Matrix4x4[] m)
        {
            var at = new Vector3[P.Length];
            void Range(int from, int to)
            {
                for (int v = from; v < to; v++)
                {
                    var sum = Vector3.Zero;
                    float total = 0f;
                    for (int k = WStart[v]; k < WStart[v + 1]; k++)
                    {
                        int sl = WSlot[k];
                        if (sl < 0 || sl >= m.Length)
                            continue;
                        sum += Vector3.Transform(P[v], m[sl]) * WWeight[k];
                        total += WWeight[k];
                    }
                    at[v] = total > 1e-6f ? sum / total : P[v];
                }
            }
            const int Chunk = 4096;
            if (P.Length <= Chunk)
                Range(0, P.Length);
            else
                Parallel.For(0, (P.Length + Chunk - 1) / Chunk, c => Range(c * Chunk, Math.Min(P.Length, (c + 1) * Chunk)));
            return at;
        }
    }

    private sealed class SurfaceBuilder
    {
        private readonly List<Vector3> _p = new();
        private readonly List<int> _tri = new(), _start = new() { 0 }, _slot = new();
        private readonly List<float> _weight = new();

        public int Count => _tri.Count / 3;

        /// <summary>A model's skin meshes (or all its meshes: a body model), LoD 0, with their (lattice) weights.</summary>
        public void AddSkin(byte[] data, List<MdlRigger.MeshSkin> skins, Func<string, int> slotOf, bool onlySkin)
        {
            var materials = MdlRigger.MeshMaterials(data);
            var (offset, size, layouts) = MdlRigger.Layout(data);
            if (size[0] == 0 || offset[0] + size[0] > data.Length)
                return;
            var block = data.AsSpan((int)offset[0], (int)size[0]).ToArray();
            foreach (var layout in layouts.Where(l => l.Lod == 0))
            {
                if (onlySkin && !(layout.Mesh < materials.Length && MdlRigger.IsSkinMaterial(materials[layout.Mesh])))
                    continue;
                var skin = skins.FirstOrDefault(k => k.Mesh == layout.Mesh);
                if (skin == null || skin.Triangles.Length < 3)
                    continue;
                int first = _p.Count;
                for (int v = 0; v < layout.VertexCount; v++)
                {
                    int pa = layout.PositionAt + v * layout.PositionStride;
                    _p.Add(pa + 12 <= block.Length ? MdlRigger.ReadVector(block, pa, layout.PositionType) : Vector3.Zero);
                    for (int k = 0; k < skin.Slots; k++)
                    {
                        float wk = skin.Weights[v * skin.Stride + k];
                        int bone = skin.Indices[v * skin.Stride + k];
                        if (wk <= 0f || bone >= skin.Bones.Length)
                            continue;
                        int sl = slotOf(skin.Bones[bone]);
                        if (sl < 0)
                            continue;
                        _slot.Add(sl);
                        _weight.Add(wk);
                    }
                    _start.Add(_slot.Count);
                }
                for (int i = 0; i + 2 < skin.Triangles.Length; i += 3)
                {
                    int a = skin.Triangles[i], b = skin.Triangles[i + 1], c = skin.Triangles[i + 2];
                    if (a >= layout.VertexCount || b >= layout.VertexCount || c >= layout.VertexCount)
                        continue;
                    _tri.Add(first + a);
                    _tri.Add(first + b);
                    _tri.Add(first + c);
                }
            }
        }

        public SkinSurface Build()
        {
            var made = new SkinSurface
            {
                P = _p.ToArray(), Tri = _tri.ToArray(), WStart = _start.ToArray(), WSlot = _slot.ToArray(), WWeight = _weight.ToArray(),
            };
            made.Index();
            return made;
        }
    }

    // One mesh's clothing wrapped onto the skin.
    private sealed class WrapPlan
    {
        public required MdlRigger.MeshLayout Layout;
        public required int[] Vertex;
        public required Vector3[] Rest, Normal;
        public required int[] OwnStart, OwnSlot;     // its own weights (what the game skins it with)
        public required float[] OwnWeight;
        public required int[] NewStart, NewSlot;     // the lattice's weights for it (where it doesn't follow fully)
        public required float[] NewWeight;
        public required float[] Follow;              // how much it follows the skin (1 near it, less further off)
        public required int[] BStart;                // its bindings: BStart[i] to BStart[i + 1]
        public required int[] BTri;
        public required Vector3[] BBary, BOff, BNrm; // where on the triangle, how far off it and its normal (triangle's directions)
        public required float[] BShare;
        public (int From, int To)[] Ranges = [];
    }

    private sealed class WrapBuilder
    {
        public const float Full = 0.03f, Reach = 0.08f;   // follows fully within this of the skin, not at all past reach
        private readonly MdlRigger.MeshLayout _layout;
        private readonly List<int> _vertex = new(), _ownStart = new(), _ownSlot = new(), _newStart = new(), _newSlot = new(), _bStart = new(), _bTri = new();
        private readonly List<Vector3> _rest = new(), _normal = new(), _bBary = new(), _bOff = new(), _bNrm = new();
        private readonly List<float> _ownWeight = new(), _newWeight = new(), _follow = new(), _bShare = new();
        private readonly List<(int Tri, float Dist, Vector3 Bary)> _near = new();
        private int _posFrom = int.MaxValue, _posTo, _nrmFrom = int.MaxValue, _nrmTo;

        public WrapBuilder(MdlRigger.MeshLayout layout) => _layout = layout;

        public int Count => _vertex.Count;

        public bool TryAdd(SkinSurface surface, int v, Vector3 p, Vector3 normal, MdlRigger.MeshSkin skin, byte[] idx, float[] w,
            MdlRigger.MeshSkin? lattice, Func<string, int> slotOf)
        {
            surface.Nearest(p, Reach, _near, 4);
            if (_near.Count == 0)
                return false;
            // its own weights (every bone must be one the frame has)
            int o0 = _ownSlot.Count;
            for (int k = 0; k < skin.Slots; k++)
            {
                if (w[k] <= 0f || idx[k] >= skin.Bones.Length)
                    continue;
                int sl = slotOf(skin.Bones[idx[k]]);
                if (sl < 0)
                {
                    _ownSlot.RemoveRange(o0, _ownSlot.Count - o0);
                    _ownWeight.RemoveRange(o0, _ownWeight.Count - o0);
                    return false;
                }
                _ownSlot.Add(sl);
                _ownWeight.Add(w[k]);
            }
            if (_ownSlot.Count == o0)
                return false;
            // the lattice's weights (its own where the lattice has none for it)
            int n0 = _newSlot.Count;
            if (lattice != null)
                for (int k = 0; k < lattice.Slots; k++)
                {
                    float wk = lattice.Weights[v * lattice.Stride + k];
                    int bone = lattice.Indices[v * lattice.Stride + k];
                    if (wk <= 0f || bone >= lattice.Bones.Length)
                        continue;
                    int sl = slotOf(lattice.Bones[bone]);
                    if (sl < 0)
                        continue;
                    _newSlot.Add(sl);
                    _newWeight.Add(wk);
                }
            if (_newSlot.Count == n0)
                for (int k = o0; k < _ownSlot.Count; k++)
                {
                    _newSlot.Add(_ownSlot[k]);
                    _newWeight.Add(_ownWeight[k]);
                }
            // the bindings: each triangle's point, the offset and the normal in its directions, shared by nearness
            int b0 = _bTri.Count;
            float total = 0f;
            foreach (var (t, d, bary) in _near)
            {
                var a = surface.P[surface.Tri[t * 3]];
                var b = surface.P[surface.Tri[t * 3 + 1]];
                var c = surface.P[surface.Tri[t * 3 + 2]];
                if (!SkinSurface.Frame(a, b, c, out var e1, out var nn, out var tt))
                    continue;
                var at = a * bary.X + b * bary.Y + c * bary.Z;
                var off = p - at;
                _bTri.Add(t);
                _bBary.Add(bary);
                _bOff.Add(new Vector3(Vector3.Dot(off, e1), Vector3.Dot(off, nn), Vector3.Dot(off, tt)));
                _bNrm.Add(new Vector3(Vector3.Dot(normal, e1), Vector3.Dot(normal, nn), Vector3.Dot(normal, tt)));
                float share = 1f / ((d + 0.004f) * (d + 0.004f));
                _bShare.Add(share);
                total += share;
            }
            if (_bTri.Count == b0)
            {
                _ownSlot.RemoveRange(o0, _ownSlot.Count - o0);
                _ownWeight.RemoveRange(o0, _ownWeight.Count - o0);
                _newSlot.RemoveRange(n0, _newSlot.Count - n0);
                _newWeight.RemoveRange(n0, _newWeight.Count - n0);
                return false;
            }
            for (int i = b0; i < _bShare.Count; i++)
                _bShare[i] /= total;
            _ownStart.Add(o0);
            _newStart.Add(n0);
            _bStart.Add(b0);
            float u = Math.Clamp((_near[0].Dist - Full) / (Reach - Full), 0f, 1f);
            _follow.Add(1f - u * u * (3f - 2f * u));
            _vertex.Add(v);
            _rest.Add(p);
            _normal.Add(normal);
            int pa = _layout.PositionAt + v * _layout.PositionStride;
            _posFrom = Math.Min(_posFrom, pa);
            _posTo = Math.Max(_posTo, pa + 16);
            if (_layout.NormalAt >= 0)
            {
                int na = _layout.NormalAt + v * _layout.NormalStride;
                _nrmFrom = Math.Min(_nrmFrom, na);
                _nrmTo = Math.Max(_nrmTo, na + 16);
            }
            return true;
        }

        public WrapPlan Build(int blockLength) => new()
        {
            Layout = _layout, Vertex = _vertex.ToArray(), Rest = _rest.ToArray(), Normal = _normal.ToArray(),
            OwnStart = _ownStart.Append(_ownSlot.Count).ToArray(), OwnSlot = _ownSlot.ToArray(), OwnWeight = _ownWeight.ToArray(),
            NewStart = _newStart.Append(_newSlot.Count).ToArray(), NewSlot = _newSlot.ToArray(), NewWeight = _newWeight.ToArray(),
            Follow = _follow.ToArray(), BStart = _bStart.Append(_bTri.Count).ToArray(), BTri = _bTri.ToArray(),
            BBary = _bBary.ToArray(), BOff = _bOff.ToArray(), BNrm = _bNrm.ToArray(), BShare = _bShare.ToArray(),
            Ranges = MergeRanges([(_posFrom, Math.Min(_posTo, blockLength)), (_nrmFrom, Math.Min(_nrmTo, blockLength))]),
        };
    }

    /// <summary>One mesh's wrapped clothing where the skin is this frame (render thread). Returns how many moved.</summary>
    private static int Wrap(WrapPlan wrap, byte[] work, Snapshot snap)
    {
        var surface = snap.Plan.Surface;
        if (surface == null)
            return 0;
        var m = snap.Skin;
        var skinNow = snap.Surface ??= surface.Place(m);
        var L = wrap.Layout;
        bool normals = L.NormalAt >= 0 && L.NormalType is (2 or 3 or 14 or 5 or 8);
        int moved = 0;
        void Range(int from, int to)
        {
            int count = 0;
            for (int i = from; i < to; i++)
            {
                // where the skin's triangles put it
                var target = Vector3.Zero;
                var nrm = Vector3.Zero;
                float got = 0f;
                for (int k = wrap.BStart[i]; k < wrap.BStart[i + 1]; k++)
                {
                    int t = wrap.BTri[k];
                    var a = skinNow[surface.Tri[t * 3]];
                    var b = skinNow[surface.Tri[t * 3 + 1]];
                    var c = skinNow[surface.Tri[t * 3 + 2]];
                    if (!SkinSurface.Frame(a, b, c, out var e1, out var nn, out var tt))
                        continue;
                    var bary = wrap.BBary[k];
                    var off = wrap.BOff[k];
                    var bn = wrap.BNrm[k];
                    target += (a * bary.X + b * bary.Y + c * bary.Z + e1 * off.X + nn * off.Y + tt * off.Z) * wrap.BShare[k];
                    nrm += (e1 * bn.X + nn * bn.Y + tt * bn.Z) * wrap.BShare[k];
                    got += wrap.BShare[k];
                }
                if (got < 1e-4f)
                    continue;
                target /= got;
                var p = wrap.Rest[i];
                var n = wrap.Normal[i];
                // further from the skin: eased toward where the lattice itself puts it
                float f = wrap.Follow[i];
                if (f < 0.999f)
                {
                    var lp = Vector3.Zero;
                    var ln = Vector3.Zero;
                    float lw = 0f;
                    for (int k = wrap.NewStart[i]; k < wrap.NewStart[i + 1]; k++)
                    {
                        var bone = m[wrap.NewSlot[k]];
                        lp += Vector3.Transform(p, bone) * wrap.NewWeight[k];
                        ln += Vector3.TransformNormal(n, bone) * wrap.NewWeight[k];
                        lw += wrap.NewWeight[k];
                    }
                    if (lw > 1e-6f)
                    {
                        target = Vector3.Lerp(lp / lw, target, f);
                        nrm = Vector3.Lerp(ln / lw, nrm, f);
                    }
                }
                // back through its own bones: the rest spot the game skins to there
                var own = new Matrix4x4();
                float ow = 0f;
                for (int k = wrap.OwnStart[i]; k < wrap.OwnStart[i + 1]; k++)
                {
                    own += m[wrap.OwnSlot[k]] * wrap.OwnWeight[k];
                    ow += wrap.OwnWeight[k];
                }
                if (ow <= 1e-6f)
                    continue;
                own *= 1f / ow;
                own.M14 = own.M24 = own.M34 = 0f;
                own.M44 = 1f;
                if (!Matrix4x4.Invert(own, out var back))
                    continue;
                var q = Vector3.Transform(target, back);
                if (!MathUtil.IsFinite(q))
                    continue;
                int v = wrap.Vertex[i];
                MdlRigger.WriteVector(work, L.PositionAt + v * L.PositionStride, L.PositionType, q);
                if (normals && n.LengthSquared() > 1e-12f)
                {
                    var nq = Vector3.TransformNormal(nrm, back);
                    float len = nq.Length();
                    if (len > 1e-6f)
                    {
                        nq /= len;
                        if (L.NormalType is 5 or 8)
                        {
                            int na = L.NormalAt + v * L.NormalStride;
                            work[na] = (byte)Math.Clamp((nq.X * 0.5f + 0.5f) * 255f, 0f, 255f);
                            work[na + 1] = (byte)Math.Clamp((nq.Y * 0.5f + 0.5f) * 255f, 0f, 255f);
                            work[na + 2] = (byte)Math.Clamp((nq.Z * 0.5f + 0.5f) * 255f, 0f, 255f);
                        }
                        else
                            MdlRigger.WriteVector(work, L.NormalAt + v * L.NormalStride, L.NormalType, nq);
                    }
                }
                count++;
            }
            if (count > 0)
                System.Threading.Interlocked.Add(ref moved, count);
        }
        int all = wrap.Vertex.Length;
        const int Chunk = 2048;
        if (all <= Chunk)
            Range(0, all);
        else
            Parallel.For(0, (all + Chunk - 1) / Chunk, c => Range(c * Chunk, Math.Min(all, (c + 1) * Chunk)));
        return moved;
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
        Limber.Rig.Bulge.TouchedLast = System.Threading.Interlocked.Exchange(ref Limber.Rig.Bulge.TouchedNow, 0);
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
        // (searches no model is claimed by any more: forgotten, their results never used)
        if (Searches.Count > 0)
            foreach (var file in Searches.Keys.ToList())
                if (!claims.ContainsKey(file) || Targets.ContainsKey(file))
                    Searches.Remove(file);
        // First, the models not found yet are looked for (see below).
        foreach (var (file, (person, snap, model)) in claims)
        {
            if (Targets.ContainsKey(file))
                continue;
            try
            {
                // (perf) The search runs on a worker thread (it used to hitch the frame for 100+ ms each time); its
                // result is taken up here once it's done, the same way the search's own result was before.
                if (Searches.TryGetValue(file, out var search))
                {
                    if (!search.Search.Done)
                        continue;
                    Searches.Remove(file);
                    if (search.Owner != person.Address)
                    {
                        // (the model went to someone else meanwhile: looked for on them instead, straight away)
                        NextTry.Remove(file);
                        continue;
                    }
                    int failedBefore = Fails.GetValueOrDefault(file);
                    var made = Start(person.Address, file, model, search.Search.Take());
                    if (made == null)
                    {
                        Fails[file] = failedBefore + 1;
                        continue;
                    }
                    Targets[file] = made;
                    continue;
                }
                if (NextTry.TryGetValue(file, out var when) && now < when)
                    continue;
                // (looking for a model's vertices in memory takes a long moment: a model it keeps not finding is tried
                // less and less often, down to every 20 seconds, but never given up on)
                int failed = Fails.GetValueOrDefault(file);
                NextTry[file] = now + Math.Min(20000L, 1000L << Math.Min(failed, 5));
                var started = StartSearch(person.Address, file);
                if (started == null)
                {
                    Fails[file] = failed + 1;
                    continue;
                }
                Searches[file] = (person.Address, started);
            }
            catch (Exception e)
            {
                Status = "Live rig: " + e.Message;
            }
        }
        // A character's models change together or not at all: one changed while the one next to it isn't (not found
        // yet) tears the body open where they meet (the waist, between the top and the legs). So while any of a
        // character's models is still being looked for, the others stay as the game has them (for a while: a model
        // never found isn't waited for for ever).
        var waiting = new HashSet<nint>();
        int waitingModels = 0;
        foreach (var (file, (person, _, _)) in claims)
            if (!Targets.ContainsKey(file) && Fails.GetValueOrDefault(file) < 8)
            {
                waiting.Add(person.Address);
                waitingModels++;
            }
        foreach (var (file, (person, snap, model)) in claims)
        {
            try
            {
                if (!Targets.TryGetValue(file, out var target))
                    continue;
                if (waiting.Contains(person.Address))
                {
                    if (target.BaseWritten)
                    {
                        var held = target.Replacement != 0 ? target.Replacement : target.Found.Buffer;
                        if (LiveMesh.StillHolds(target.Found, held) && target.Model.Original.Length == target.Work.Length)
                        {
                            Buffer.BlockCopy(target.Model.Original, 0, target.Work, 0, target.Model.Original.Length);
                            LiveMesh.Upload(held, target.Found.Desc, target.Work);
                        }
                        target.BaseWritten = false;
                    }
                    continue;
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
                else if ((model.Meshes.Length > 0 || model.Wraps.Length > 0) && !ReferenceEquals(target.Written, snap))
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
        if (waitingModels > 0)
            Status = $"Live rig: waiting for {waitingModels} model(s) to be found in memory (the others hold still until then, so nothing tears).";
        else if (Targets.Count > 0)
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
                    else if ((model.Meshes.Length > 0 || model.Wraps.Length > 0) && !ReferenceEquals(copy.Written, snap))
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

    // Model searches running on worker threads, by file (and the character each was started for).
    private static readonly Dictionary<string, (nint Owner, LiveMesh.Search Search)> Searches = new();

    private static LiveMesh.Search? StartSearch(nint owner, string file)
    {
        var obj = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)owner;
        if (obj == null || obj->DrawObject == null)
            return null;
        return LiveMesh.ProbeInBackground((nint)obj->DrawObject, file, owner);
    }

    // The model's vertex buffer among the ones a search found (render thread).
    private static Target? Start(nint owner, string file, ModelPlan model, List<LiveMesh.Found> founds)
    {
        var obj = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)owner;
        if (obj == null || obj->DrawObject == null)
            return null;
        foreach (var found in founds)
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
        Searches.Clear();
        NextTry.Clear();
        Fails.Clear();
        People.Clear();
        VirtualRigs.Clear();
        Status = "";
    }

    // (perf: Deform's per-bone inverses, kept from frame to frame)
    private static Matrix4x4[] _inverse = [];
    private static bool[] _hasInverse = [];

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
        // (perf: kept from frame to frame instead of made anew each time; Deform only runs on the render thread)
        if (_inverse.Length < m.Length)
        {
            _inverse = new Matrix4x4[m.Length];
            _hasInverse = new bool[m.Length];
        }
        var inverse = _inverse;
        var hasInverse = _hasInverse;
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
                moved += DeformRange(mesh, work, m, inverse, hasInverse, 0, count, snap.Colliders, snap.Bulge);
            else
            {
                int chunks = (count + Chunk - 1) / Chunk;
                Parallel.For(0, chunks, c =>
                {
                    int n = DeformRange(mesh, work, m, inverse, hasInverse, c * Chunk, Math.Min(count, (c + 1) * Chunk), snap.Colliders, snap.Bulge);
                    if (n > 0)
                        System.Threading.Interlocked.Add(ref moved, n);
                });
            }
        }
        foreach (var wrap in model.Wraps)
            moved += Wrap(wrap, work, snap);
        return moved > 0;
    }

    private static int DeformRange(MeshPlan mesh, byte[] work, Matrix4x4[] m, Matrix4x4[] inverse, bool[] hasInverse, int from, int to, Shape[] colliders,
        Limber.Rig.Bulge.Now? bulge = null)
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
            // Clothing following the skin: the gap between it and the skin kept as it was at rest (the lattice's bones
            // are scaled with the size and shapes, and that would scale the gap too): its blend's size taken out of it.
            if (i < mesh.Follow.Length && mesh.Follow[i] > 0f)
            {
                var all = new Matrix4x4();
                for (int k = a; k < c; k++)
                    all += m[mesh.Slot[k]] * mesh.Weight[k];
                var d = Vector3.TransformNormal(mesh.Offset[i], all);
                float size = (new Vector3(all.M11, all.M12, all.M13).Length() + new Vector3(all.M21, all.M22, all.M23).Length()
                              + new Vector3(all.M31, all.M32, all.M33).Length()) / 3f;
                if (size > 1e-3f && MathUtil.IsFinite(d))
                    delta -= d * ((1f - 1f / size) * mesh.Follow[i]);
            }
            // Pressed by colliders (Mesh collision): where it would be drawn, pushed out of each one it's inside.
            if (colliders.Length > 0)
            {
                var drawn = delta;
                for (int k = a; k < b; k++)
                    drawn += Vector3.Transform(p, m[mesh.Slot[k]]) * mesh.Weight[k];
                foreach (var shape in colliders)
                {
                    if (shape.R <= 0f)
                        continue;
                    float gap = shape.Gap(drawn, out _, out var outward);
                    if (gap < shape.R && outward != Vector3.Zero)
                    {
                        var push = outward * (shape.R - gap);
                        delta += push;
                        drawn += push;
                    }
                }
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
            // The stomach bulge: the front of the belly pushed out along its normal (at rest, so it moves with the body).
            var bulgeTilt = Vector3.Zero;
            if (bulge != null)
            {
                // (by where it is only, never which way its own normal faces: the navel's dimple and the pubic hair
                // face every way, and seams have two vertices in one place with different normals; moved along their
                // normals they tore apart, so everything is moved the same way, forward)
                float hw = bulge.HipWidth;
                float front = SmoothStep(bulge.FrontZ - 0.01f, bulge.FrontZ + hw * 0.2f, p.Z)
                              * (1f - SmoothStep(bulge.FrontZ + hw * 0.9f, bulge.FrontZ + hw * 1.2f, p.Z));
                float k = front;
                if (k > 1e-3f)
                {
                    float h = Limber.Rig.Bulge.Height(bulge, p.X, p.Y);
                    if (h != 0f && float.IsFinite(h))
                    {
                        if (MathF.Abs(h * k) > 1e-4f)
                            System.Threading.Interlocked.Increment(ref Limber.Rig.Bulge.TouchedNow);
                        q += new Vector3(0f, 0f, h * k);
                        if (normals)
                        {
                            const float e = 0.004f;
                            float gx = (Limber.Rig.Bulge.Height(bulge, p.X + e, p.Y) - Limber.Rig.Bulge.Height(bulge, p.X - e, p.Y)) / (2f * e);
                            float gy = (Limber.Rig.Bulge.Height(bulge, p.X, p.Y + e) - Limber.Rig.Bulge.Height(bulge, p.X, p.Y - e)) / (2f * e);
                            if (float.IsFinite(gx) && float.IsFinite(gy))
                                bulgeTilt = new Vector3(gx, gy, 0f) * k;
                        }
                    }
                }
            }
            if (!MathUtil.IsFinite(q) || Vector3.DistanceSquared(q, mesh.Last[i]) < 1e-12f)
                continue;
            mesh.Last[i] = q;
            moved++;
            int v = mesh.Vertex[i];
            MdlRigger.WriteVector(work, L.PositionAt + v * L.PositionStride, L.PositionType, q);
            if (normals && n.LengthSquared() > 1e-12f)
            {
                var nn = n + Vector3.TransformNormal(turn, back) - bulgeTilt * n.Length();
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

    private static float SmoothStep(float e0, float e1, float x)
    {
        float t = Math.Clamp((x - e0) / (e1 - e0), 0f, 1f);
        return t * t * (3f - 2f * t);
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
