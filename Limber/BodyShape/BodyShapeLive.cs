using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Limber.Core;

namespace Limber.BodyShape;

/// <summary>
/// The Body Shape tab's live preview: every character with slider values gets them on its bones each frame (in the
/// bone update, right after the Actor tab, before posing), so the shape shows at once and posing and the soft bodies
/// work on it. What's already built into a character's models is taken off first (only the rest is done live).
/// Only your screen's copy of the character is changed.
/// </summary>
public sealed class BodyShapeLive
{
    /// <summary>One character to shape: its slider values, and the ones already built into its models.</summary>
    public readonly record struct Target(nint Address, ushort Index, IReadOnlyDictionary<string, float> Values,
        IReadOnlyDictionary<string, float>? Built, IReadOnlyDictionary<string, BoneScaleEntry>? Bones = null);

    /// <summary>Set from the framework thread.</summary>
    public volatile Target[] Targets = [];

    private sealed class State
    {
        public Dictionary<string, ShapeOp> Applied = new();
        public float Legs = 1f;
        public float Hip;   // metres the hips are moved up (Hip position)
        public float Size = 1f; // Body size applied (the whole skeleton scaled about the feet)
        public bool LegsFeet = true;
        public Dictionary<string, Vector3> Written = new(); // scales as written, to tell a rebuilt pose
        public Dictionary<string, BoneFrame> WrittenFrames = new();
        public int SeenFrame;
        public ushort Index;
        public bool Record; // written this frame: read back what the frame ended with (see AfterFrame)
        public string Signature = ""; // the Bone scale on / off choices the applied changes were made with
        public HashSet<string> OwnSize = new();
    }

    private readonly Dictionary<nint, State> _states = new();
    private readonly SkeletonInfoCache _cache = new();
    private int _frame;
    public Func<bool> KeepFeet = () => true;

    private static readonly string[] LegBones = ["j_asi_a_", "j_asi_b_", "j_asi_c_", "j_asi_d_"]; // hip, knee, shin, ankle

    /// <summary>Bone update stage.</summary>
    public void UpdateFrame()
    {
        _frame++;
        var targets = Targets;
        foreach (var target in targets)
        {
            try
            {
                Shape(target);
            }
            catch (Exception e)
            {
                Plugin.Log.Warning(e, "Limber: body shape failed");
            }
        }
        // Characters no longer shaped: their state goes (the game rebuilds their pose as it was).
        if (_states.Count > 0)
        {
            List<nint>? gone = null;
            foreach (var (address, state) in _states)
                if (state.SeenFrame != _frame)
                    (gone ??= new()).Add(address);
            if (gone != null)
                foreach (var a in gone)
                    _states.Remove(a);
        }
    }

    private void Shape(in Target target)
    {
        if (!ActorSkeleton.TryGet(target.Address, target.Index, _cache, out var skeleton) || skeleton.Info.Count == 0)
            return;
        // The body sliders are for adult bodies only; any character can have bones scaled (the body's own bones).
        bool adult = Limber.Koi.Safety.AdultAllowed(target.Address);
        var values = adult ? target.Values : new Dictionary<string, float>();
        var builtValues = adult ? target.Built : null;
        if (!adult && (target.Bones == null || target.Bones.Count == 0) && !_states.ContainsKey(target.Address))
            return;
        var info = skeleton.Info;
        if (!_states.TryGetValue(target.Address, out var state))
            _states[target.Address] = state = new State();
        state.SeenFrame = _frame;
        state.Index = target.Index;

        // Where every bone is now (read once; the lookups below don't hold on to the skeleton).
        var frames = new Dictionary<string, BoneFrame>(info.Count);
        for (int i = 0; i < info.Count; i++)
        {
            var f = skeleton.GetModelFrame(i);
            frames[info.Names[i]] = new BoneFrame(f.Position, f.Rotation, skeleton.GetModelScale(i));
        }
        Func<string, BoneFrame?> Now = name => frames.TryGetValue(name, out var bf) ? bf : null;
        // Where they are at rest (which of each bone's axes a slider works along is chosen there, so it never
        // switches as the pose turns the bone).
        var rest = RestOf(skeleton);
        Func<string, BoneFrame?> Rest = name => rest.TryGetValue(name, out var rf) ? rf : null;

        // Rebuilt since last frame (the bones aren't as written): what's there has none of it on.
        bool kept = !state.Record && (state.Written.Count > 0 || state.WrittenFrames.Count > 0);
        foreach (var (name, scale) in state.Written)
        {
            int i = info.IndexOf(name);
            if (i < 0 || Vector3.Distance(skeleton.GetModelScale(i), scale) > 1e-4f * MathF.Max(1f, scale.Length()))
            {
                kept = false;
                break;
            }
        }
        if (kept && state.Written.Count == 0)
            foreach (var (name, frame) in state.WrittenFrames)
                if (Now(name) is not { } f || Vector3.Distance(f.Position, frame.Position) > 1e-5f)
                {
                    kept = false;
                    break;
                }
        if (!kept)
        {
            state.Applied.Clear();
            state.Legs = 1f;
            state.Hip = 0f;
            state.Size = 1f;
        }

        // What's wanted: everything live; what's built into the models taken off.
        var wanted = ShapeOps.Resolve(values, Rest, _ => true);
        if (builtValues is { Count: > 0 } built)
        {
            var bakedOps = ShapeOps.Resolve(built, Rest, s => !s.LiveOnly);
            foreach (var (name, baked) in bakedOps)
                wanted[name] = wanted.TryGetValue(name, out var w) ? w.Over(baked) : Inverse(baked);
        }
        // The Bone scale page's own scales, on top.
        foreach (var (name, op) in BoneScales.Ops(target.Bones, adult))
            wanted[name] = wanted.TryGetValue(name, out var w0) ? w0.With(op) : op;
        var ownSize = BoneScales.OwnSize(target.Bones, adult);

        // Body size: the whole character scaled about its feet (taller and bigger, everything in proportion), the
        // head then scaled back so it stays the size it was; Head size on top of that.
        float bodySize = SizeOf(values, "body_size"), headSize = SizeOf(values, "head_size"), thickness = SizeOf(values, "body_thickness");
        // Body thickness: the body's root bone scaled (every part thicker in place), the head scaled back as well.
        string? thickRoot = MathF.Abs(thickness - 1f) > 1e-6f ? ThickRoot(info, Rest) : null;
        if (thickRoot != null)
        {
            var op = new ShapeOp { Bone = thickRoot, Scale = new Vector3(thickness), CarryDescendants = false };
            wanted[thickRoot] = wanted.TryGetValue(thickRoot, out var w1) ? w1.With(op) : op;
        }
        float head = headSize / bodySize / (thickRoot != null ? thickness : 1f);
        if (info.IndexOf("j_kao") >= 0 && MathF.Abs(head - 1f) > 1e-6f)
        {
            var op = new ShapeOp { Bone = "j_kao", Scale = new Vector3(head) };
            wanted["j_kao"] = wanted.TryGetValue("j_kao", out var w2) ? w2.With(op) : op;
        }
        float legs = MathF.Max(0.05f, 1f + (values.TryGetValue("leg_length", out var lv) && float.IsFinite(lv) ? Math.Clamp(lv, -10f, 10f) : 0f));
        bool feet = KeepFeet();
        float hip = values.TryGetValue("hip_position", out var hv) && float.IsFinite(hv) ? Math.Clamp(hv, -10f, 10f) * HipStep : 0f;

        // A Bone scale on / off choice changed: everything applied is taken off the way it was put on, and done
        // again the new way (a change on top of the old one wouldn't come out the same).
        string signature = BoneScales.Signature(target.Bones, adult);
        bool undone = false;
        if (signature != state.Signature)
        {
            if (state.Applied.Count > 0)
            {
                var undo = state.Applied.Values.Select(Inverse).ToList();
                undo.Sort((a, b) => Depth(info, info.IndexOf(b.Bone)).CompareTo(Depth(info, info.IndexOf(a.Bone))));
                foreach (var op in undo)
                    Apply(skeleton, op, state.OwnSize);
                state.Applied.Clear();
                undone = true;
            }
            state.Signature = signature;
        }
        state.OwnSize = ownSize;

        // What's still to do on top of what's there.
        var todo = new List<ShapeOp>();
        foreach (var name in wanted.Keys.Union(state.Applied.Keys))
        {
            var want = wanted.TryGetValue(name, out var w) ? w : new ShapeOp { Bone = name };
            if (!wanted.ContainsKey(name) && state.Applied.TryGetValue(name, out var had))
            {
                // No longer wanted: undo it (keeping its keep rules).
                want.Keep = had.Keep;
                want.KeepNames = had.KeepNames;
                want.Next = had.Next;
                want.StretchAxis = had.StretchAxis;
                want.HoldChildSizes = had.HoldChildSizes;
                want.HoldChildPlaces = had.HoldChildPlaces;
            }
            var step = want.Over(state.Applied.GetValueOrDefault(name));
            if (!step.IsIdentity)
                todo.Add(step);
        }
        float legStep = legs / state.Legs;
        float hipStep = hip - state.Hip;
        float sizeStep = bodySize / state.Size;
        if (!undone && todo.Count == 0 && MathF.Abs(legStep - 1f) < 1e-6f && MathF.Abs(hipStep) < 1e-6f && MathF.Abs(sizeStep - 1f) < 1e-6f)
            return;

        // The whole skeleton (body, face, hair, tail) scaled about the feet, first: the other changes are made on it.
        if (MathF.Abs(sizeStep - 1f) > 1e-6f && float.IsFinite(sizeStep) && sizeStep > 0f)
            skeleton.TransformAllBones(Vector3.Zero, Quaternion.Identity, sizeStep);

        // The face, hair and tail skeletons as they are now, to carry them along exactly (see CarryPartials).
        var partials = CapturePartials(skeleton);

        // Parents first, so a child's change is made in its moved place.
        todo.Sort((a, b) => Depth(info, info.IndexOf(a.Bone)).CompareTo(Depth(info, info.IndexOf(b.Bone))));
        foreach (var op in todo)
            Apply(skeleton, op, ownSize);
        if (MathF.Abs(legStep - 1f) > 1e-6f)
            Lengthen(skeleton, legStep, feet);
        if (MathF.Abs(hipStep) > 1e-6f)
            MoveHips(skeleton, hipStep);

        // The face, hair and other extra skeletons onto the moved body again.
        skeleton.RealignPartials(_cache);
        CarryPartials(skeleton, partials);
        skeleton.RefreshAttachedModels();

        state.Applied = wanted;
        state.Legs = legs;
        state.Hip = hip;
        state.Size = bodySize;
        state.Record = true;
    }

    /// <summary>
    /// Bone update, last stage: what the shaped bones ended the frame as (after posing and the soft bodies, which may
    /// change them too), to tell next frame whether the game rebuilt the pose since.
    /// </summary>
    public void AfterFrame()
    {
        foreach (var (address, state) in _states)
        {
            if (!state.Record)
                continue;
            state.Record = false;
            state.Written.Clear();
            state.WrittenFrames.Clear();
            if (!ActorSkeleton.TryGet(address, state.Index, _cache, out var skeleton) || skeleton.Info.Count == 0)
                continue;
            var info = skeleton.Info;
            var marks = new List<string>();
            if (state.Legs != 1f || state.Hip != 0f)
                marks.Add("j_asi_a_l");
            if (state.Size != 1f)
                marks.Add("j_kosi");
            foreach (var name in state.Applied.Keys.Concat(marks).Distinct())
            {
                int i = info.IndexOf(name);
                if (i < 0)
                    continue;
                var scale = skeleton.GetModelScale(i);
                bool scaled = marks.Contains(name)
                    || (state.Applied.TryGetValue(name, out var o) && (Vector3.DistanceSquared(o.Scale, Vector3.One) > 1e-12f || MathF.Abs(o.Stretch - 1f) > 1e-6f));
                if (scaled)
                    state.Written[name] = scale;
                else
                {
                    var f = skeleton.GetModelFrame(i);
                    state.WrittenFrames[name] = new BoneFrame(f.Position, f.Rotation, scale);
                }
            }
        }
    }

    private readonly Dictionary<SkeletonInfo, Dictionary<string, BoneFrame>> _rest = new();

    // Every bone's rest place (model space), from the skeleton's reference pose; once per skeleton.
    private Dictionary<string, BoneFrame> RestOf(in ActorSkeleton skeleton)
    {
        var info = skeleton.Info;
        if (_rest.TryGetValue(info, out var known))
            return known;
        if (_rest.Count > 32)
            _rest.Clear();
        var pos = new Vector3[info.Count];
        var rot = new Quaternion[info.Count];
        var done = new bool[info.Count];
        var result = new Dictionary<string, BoneFrame>(info.Count);
        // (parents usually come first; a few passes cover any that don't)
        for (int pass = 0; pass < 8; pass++)
        {
            bool all = true;
            for (int i = 0; i < info.Count; i++)
            {
                if (done[i])
                    continue;
                int parent = info.ParentOf(i);
                if (parent >= 0 && !done[parent])
                {
                    all = false;
                    continue;
                }
                skeleton.TryGetReferenceLocal(i, out var p, out var q);
                if (parent >= 0)
                {
                    pos[i] = pos[parent] + Vector3.Transform(p, rot[parent]);
                    rot[i] = Quaternion.Normalize(rot[parent] * q);
                }
                else
                {
                    pos[i] = p;
                    rot[i] = q;
                }
                done[i] = true;
            }
            if (all)
                break;
        }
        for (int i = 0; i < info.Count; i++)
            result[info.Names[i]] = new BoneFrame(pos[i], done[i] ? rot[i] : Quaternion.Identity, Vector3.One);
        return _rest[info] = result;
    }

    private static float SizeOf(IReadOnlyDictionary<string, float> values, string key)
    {
        if (!values.TryGetValue(key, out var v) || !float.IsFinite(v) || !BodySliders.ByKey.TryGetValue(key, out var slider))
            return 1f;
        return MathF.Max(0.05f, 1f + Math.Clamp(v, slider.Min, slider.Max));
    }

    // The bone Body thickness scales: one at the feet (the model's origin).
    private static string? ThickRoot(SkeletonInfo info, Func<string, BoneFrame?> rest)
    {
        foreach (var name in new[] { "n_hara", "n_root" })
            if (info.IndexOf(name) >= 0 && rest(name) is { } f && MathF.Abs(f.Position.Y) < 0.05f)
                return name;
        return null;
    }

    private sealed record PartialCapture(int Partial, int Root, Frame RootFrame, Vector3 RootScale, Frame[] Frames, Vector3[] Scales, bool[] Own);

    private List<PartialCapture> CapturePartials(in ActorSkeleton skeleton)
    {
        var list = new List<PartialCapture>();
        for (int p = 1; p < skeleton.PartialCount; p++)
        {
            var info = skeleton.PartialInfo(p, _cache);
            int root = skeleton.PartialRootBone(p);
            if (info == null || root < 0 || root >= info.Count)
                continue;
            var frames = new Frame[info.Count];
            var scales = new Vector3[info.Count];
            var own = new bool[info.Count];
            for (int b = 0; b < info.Count; b++)
            {
                frames[b] = skeleton.GetPartialFrame(p, b);
                scales[b] = skeleton.GetPartialScale(p, b);
                // (the ones RealignPartials takes from the body itself)
                own[b] = info.Parents[b] < 0 || ActorSkeleton.IsSkeletonModBone(info.Names[b]);
            }
            list.Add(new PartialCapture(p, root, frames[root], scales[root], frames, scales, own));
        }
        return list;
    }

    // The extra skeletons (face, hair, tail) carried exactly with the body bone they hang from: moved, turned and
    // scaled by just as much as it was, every bone written directly. The game would only take the root's new place
    // and leave the face's own bones (eyes, mouth, lips) at their old size, which is what bent the face out of shape
    // when the head was scaled.
    private static void CarryPartials(in ActorSkeleton skeleton, List<PartialCapture> captured)
    {
        foreach (var c in captured)
        {
            var now = skeleton.GetPartialFrame(c.Partial, c.Root);
            var nowScale = skeleton.GetPartialScale(c.Partial, c.Root);
            float k = c.RootScale.Y > 1e-6f ? nowScale.Y / c.RootScale.Y : 1f;
            var turn = Quaternion.Normalize(now.Rotation * Quaternion.Inverse(c.RootFrame.Rotation));
            if (!float.IsFinite(k) || k <= 0f || !MathUtil.IsFinite(turn))
                continue;
            if (MathF.Abs(k - 1f) < 1e-6f && MathF.Abs(turn.W) > 0.9999999f && Vector3.DistanceSquared(now.Position, c.RootFrame.Position) < 1e-12f)
                continue;
            for (int b = 0; b < c.Frames.Length; b++)
            {
                if (c.Own[b])
                    continue;
                var f = c.Frames[b];
                var position = now.Position + Vector3.Transform((f.Position - c.RootFrame.Position) * k, turn);
                skeleton.SetPartialPosition(c.Partial, b, position, children: false);
                skeleton.SetPartialRotation(c.Partial, b, Quaternion.Normalize(turn * f.Rotation), children: false);
                skeleton.SetPartialScale(c.Partial, b, c.Scales[b] * k, children: false);
            }
        }
    }

    private static ShapeOp Inverse(ShapeOp op)
    {
        var r = op.Copy();
        r.Scale = new Vector3(1f / op.Scale.X, 1f / op.Scale.Y, 1f / op.Scale.Z);
        r.Turn = Quaternion.Inverse(op.Turn);
        r.Move = -op.Move;
        r.Stretch = 1f / op.Stretch;
        return r;
    }

    private static int Depth(SkeletonInfo info, int bone)
    {
        int d = 0;
        for (int b = bone; b >= 0 && d < 512; b = info.ParentOf(b))
            d++;
        return d;
    }

    // One bone: scaled in its own axes (and stretched), turned and moved; the children it keeps put back as they
    // were, and the next joint of a stretch moved out along the line.
    private static void Apply(in ActorSkeleton skeleton, ShapeOp op, HashSet<string>? ownSize = null)
    {
        var info = skeleton.Info;
        int bone = info.IndexOf(op.Bone);
        if (bone < 0)
            return;
        var frame = skeleton.GetModelFrame(bone);
        var scale = skeleton.GetModelScale(bone);
        var keep = new List<(int Bone, Frame Frame, Vector3 Scale)>();
        var children = new List<(int Bone, Frame Frame, Vector3 Scale)>(); // the rest of the bones right below it
        for (int b = 0; b < info.Count; b++)
            if (info.ParentOf(b) == bone)
            {
                if (op.Keeps(info.Names[b]) || (op.HoldChildSizes && op.HoldChildPlaces))
                    keep.Add((b, skeleton.GetModelFrame(b), skeleton.GetModelScale(b)));
                else
                    children.Add((b, skeleton.GetModelFrame(b), skeleton.GetModelScale(b)));
            }
        // Every bone further down below those (the game keeps their distances when a parent is scaled, so a scaled
        // shaft or limb wouldn't get longer past its first joint): carried along exactly, parents first.
        var deeper = new List<(int Bone, Frame Frame, Vector3 Scale)>();
        if (op.CarryDescendants && !op.HoldChildPlaces && children.Count > 0)
        {
            var under = new HashSet<int>(children.Select(c => c.Bone));
            bool grew = true;
            while (grew)
            {
                grew = false;
                for (int b = 0; b < info.Count; b++)
                    if (!under.Contains(b) && under.Contains(info.ParentOf(b)))
                    {
                        under.Add(b); grew = true;
                        deeper.Add((b, skeleton.GetModelFrame(b), skeleton.GetModelScale(b)));
                    }
            }
            deeper.Sort((x, y) => Depth(info, x.Bone).CompareTo(Depth(info, y.Bone)));
        }
        // Bones further down that keep their own size whatever is above them (Bone scale: "follow parent" off).
        var own = new List<(int Bone, Vector3 Scale)>();
        if (ownSize is { Count: > 0 })
        {
            var under = new HashSet<int> { bone };
            var kept = new HashSet<int>(keep.Select(k => k.Bone));
            for (int b = bone + 1; b < info.Count; b++)
            {
                int parent = info.ParentOf(b);
                if (parent < 0 || !under.Contains(parent) || kept.Contains(b))
                    continue;
                under.Add(b);
                if (ownSize.Contains(info.Names[b]))
                    own.Add((b, skeleton.GetModelScale(b)));
            }
        }

        var s = op.Scale;
        if (MathF.Abs(op.Stretch - 1f) > 1e-6f)
            s *= ShapeOps.Unit(op.StretchAxis, op.Stretch);
        if (Vector3.DistanceSquared(s, Vector3.One) > 1e-12f)
            skeleton.SetModelScale(bone, scale * s);
        if (MathF.Abs(op.Turn.W) < 0.999999f)
            skeleton.SetModelRotation(bone, Quaternion.Normalize(frame.Rotation * op.Turn));
        var at = frame.Position + Vector3.Transform(op.Move, frame.Rotation);
        // Only moved: everything below it (that isn't kept) moved by just as much, the whole limb with it.
        var carried = new List<(int Bone, Frame Frame)>();
        if (op.Move.LengthSquared() > 1e-14f && Vector3.DistanceSquared(s, Vector3.One) < 1e-12f && MathF.Abs(op.Turn.W) > 0.999999f)
        {
            var under = new HashSet<int> { bone };
            for (int b = bone + 1; b < info.Count; b++)
            {
                int parent = info.ParentOf(b);
                if (parent < 0 || !under.Contains(parent) || (parent == bone && op.Keeps(info.Names[b])))
                    continue;
                under.Add(b);
                carried.Add((b, skeleton.GetModelFrame(b)));
            }
        }
        if (op.Move.LengthSquared() > 1e-14f)
            skeleton.SetModelPosition(bone, at);
        foreach (var (b, f) in carried)
        {
            skeleton.SetModelPosition(b, f.Position + (at - frame.Position));
            skeleton.SetModelRotation(b, f.Rotation);
        }
        foreach (var (b, f, sc) in keep)
        {
            var p = f.Position;
            if (info.Names[b] == op.Next && MathF.Abs(op.Stretch - 1f) > 1e-6f)
                p = at + (f.Position - frame.Position) * op.Stretch;
            skeleton.SetModelPosition(b, p);
            skeleton.SetModelRotation(b, f.Rotation);
            skeleton.SetModelScale(b, sc);
        }
        // The other bones right below it: where the change takes them (or where they were), their own size or
        // its change on top.
        if (children.Count > 0 && (Vector3.DistanceSquared(s, Vector3.One) > 1e-12f || MathF.Abs(op.Turn.W) < 0.999999f))
        {
            var toLocal = Quaternion.Inverse(frame.Rotation);
            foreach (var (b, f, sc) in children)
            {
                if (!op.HoldChildPlaces)
                {
                    var local = Vector3.Transform(f.Position - frame.Position, toLocal) * s;
                    skeleton.SetModelPosition(b, at + Vector3.Transform(Vector3.Transform(local, op.Turn), frame.Rotation));
                }
                else
                    skeleton.SetModelPosition(b, f.Position);
                skeleton.SetModelRotation(b, MathF.Abs(op.Turn.W) < 0.999999f
                    ? Quaternion.Normalize(frame.Rotation * op.Turn * Quaternion.Inverse(frame.Rotation) * f.Rotation)
                    : f.Rotation);
                skeleton.SetModelScale(b, op.HoldChildSizes ? sc : sc * s);
            }
            foreach (var (b, f, sc) in deeper)
            {
                var local = Vector3.Transform(f.Position - frame.Position, toLocal) * s;
                skeleton.SetModelPosition(b, at + Vector3.Transform(Vector3.Transform(local, op.Turn), frame.Rotation));
                skeleton.SetModelRotation(b, MathF.Abs(op.Turn.W) < 0.999999f
                    ? Quaternion.Normalize(frame.Rotation * op.Turn * Quaternion.Inverse(frame.Rotation) * f.Rotation)
                    : f.Rotation);
                skeleton.SetModelScale(b, op.HoldChildSizes ? sc : sc * s);
            }
        }
        foreach (var (b, sc) in own)
            skeleton.SetModelScale(b, sc);
    }

    // Leg length: each of the three pieces down to the ankle stretched along its own length (the bone scaled that
    // way, so its skin stretches evenly) and the next joint moved out with it; the ankle and foot as they are. With
    // the feet kept, the body goes up by what the legs reached down.
    private const float HipStep = 0.05f; // metres per 100% of Hip position

    // Hip position: the hips (and the body above them) moved up or down, the legs made longer or shorter by just
    // enough that the feet stay where they were; the butt stays where it is.
    private static void MoveHips(in ActorSkeleton skeleton, float up)
    {
        var info = skeleton.Info;
        int pelvis = info.IndexOf("j_kosi");
        if (pelvis < 0)
            return;
        var drop = new float[2];
        for (int side = 0; side < 2; side++)
        {
            int hipBone = info.IndexOf("j_asi_a_" + (side == 0 ? "l" : "r")), ankle = info.IndexOf("j_asi_d_" + (side == 0 ? "l" : "r"));
            if (hipBone < 0 || ankle < 0)
                return;
            drop[side] = skeleton.GetModelPosition(hipBone).Y - skeleton.GetModelPosition(ankle).Y;
        }
        // The butt stays where it is (only the hips move): its bones put back after.
        var cheeks = new List<(int Bone, Frame Frame, Vector3 Scale)>();
        foreach (var name in new[] { "iv_shiri_l", "iv_shiri_r" })
        {
            int b = info.IndexOf(name);
            if (b >= 0)
                cheeks.Add((b, skeleton.GetModelFrame(b), skeleton.GetModelScale(b)));
        }
        skeleton.SetModelPosition(pelvis, skeleton.GetModelFrame(pelvis).Position + new Vector3(0f, up, 0f));
        foreach (var (b, f, sc) in cheeks)
        {
            skeleton.SetModelPosition(b, f.Position);
            skeleton.SetModelRotation(b, f.Rotation);
            skeleton.SetModelScale(b, sc);
        }
        // Each leg stretched along itself so its ankle comes back down by as much (a leg that isn't below its hip,
        // e.g. kicked up, is left as it is).
        var r = new float[2];
        for (int side = 0; side < 2; side++)
            r[side] = drop[side] > 0.05f ? Math.Clamp(1f + up / drop[side], 0.05f, 20f) : 1f;
        LengthenLegs(skeleton, r, feet: false);
    }

    private static void Lengthen(in ActorSkeleton skeleton, float r, bool feet) => LengthenLegs(skeleton, [r, r], feet);

    private static void LengthenLegs(in ActorSkeleton skeleton, float[] factors, bool feet)
    {
        var info = skeleton.Info;
        var bones = new int[2, 4];
        for (int side = 0; side < 2; side++)
            for (int k = 0; k < 4; k++)
            {
                bones[side, k] = info.IndexOf(LegBones[k] + (side == 0 ? "l" : "r"));
                if (bones[side, k] < 0)
                    return;
            }
        float rise = 0f;
        var at = new Frame[4];
        var size = new Vector3[4];
        var to = new Vector3[4];
        for (int side = 0; side < 2; side++)
        {
            float r = factors[side];
            if (MathF.Abs(r - 1f) < 1e-6f)
                continue;
            for (int k = 0; k < 4; k++)
            {
                at[k] = skeleton.GetModelFrame(bones[side, k]);
                size[k] = skeleton.GetModelScale(bones[side, k]);
            }
            to[0] = at[0].Position;
            for (int k = 1; k < 4; k++)
                to[k] = to[k - 1] + (at[k].Position - at[k - 1].Position) * r;
            if (!MathUtil.IsFinite(to[3]))
                return;
            for (int k = 0; k < 4; k++)
            {
                int bone = bones[side, k];
                if (k > 0)
                {
                    skeleton.SetModelPosition(bone, to[k]);
                    skeleton.SetModelRotation(bone, at[k].Rotation);
                }
                var scale = size[k];
                if (k < 3)
                    scale *= ShapeOps.Unit(ShapeOps.Largest(Vector3.Abs(Vector3.Transform(at[k + 1].Position - at[k].Position,
                        Quaternion.Inverse(at[k].Rotation)))), r);
                skeleton.SetModelScale(bone, scale);
            }
            rise -= (to[3].Y - at[3].Position.Y) * 0.5f;
        }
        if (feet && MathF.Abs(rise) > 1e-6f)
        {
            int body = info.IndexOf("n_hara");
            if (body < 0)
                body = info.IndexOf("j_kosi");
            if (body >= 0)
                skeleton.SetModelPosition(body, skeleton.GetModelFrame(body).Position + new Vector3(0f, rise, 0f));
        }
    }
}
