using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Limber.Core;

namespace Limber.Rig;

/// <summary>
/// Oral's Path style (the default): instead of the head doing all the work, the two meet part way.
///
/// - The mouth has a path through it, measured from the face's own bones in the head's space: the lips' centre (where
///   the shaft goes in), just behind the teeth, the back of the mouth, and on down the throat along the neck.
/// - The head only comes a share of the way to the shaft (and turns a share of the way to face down it); the shaft
///   bends the rest. Its bones outside the mouth lie on the line from its base to the lips, the bone crossing the lips
///   is solved as a triangle (so the bend is right at the lips), and the bones inside follow the mouth's path. It eases
///   onto the path as its tip comes within ~20 cm of the lips, never goes past the back of the mouth (unless the throat
///   is allowed), never turns a joint further than a set angle, and never changes its bend faster than a set speed.
/// - The mouth's shape comes from the shaft's thickness where it crosses the lips (a thickness-by-distance table built
///   once), the cheeks and throat from how deep it is, and the lips are pushed in and pulled out by the change in depth
///   (decaying), and grip the shaft: each lip holds on to a spot on the shaft until it's stretched too far, then slips.
/// The shaft's bend is written in the bone update after posing (the giver's head is where it ends up by then); it is
/// taken back off before posing the next frame if the game didn't rebuild the pose (paused).
/// </summary>
public sealed unsafe partial class OralService
{
    // ---- The mouth's path (in the head's space, model metres)

    private struct MouthPath
    {
        public Frame Head;                  // the head it's measured in (model space)
        public Vector3 E, T, R, N;          // the lips' centre, behind the teeth, the back of the mouth, down the throat
        public Vector3 LocalForward, LocalUp;
        public float Back;                  // lips to the back of the mouth
        public float Total;                 // as far as it may go (the back of the mouth, or down the throat)
        public float FaceSize;              // this mouth against an average one
    }

    private static float Angle(Vector3 a, Vector3 b) => MathF.Acos(Math.Clamp(Vector3.Dot(Norm(a), Norm(b)), -1f, 1f));

    private static Quaternion LimitTurn(Quaternion q, float most)
    {
        q = Quaternion.Normalize(q);
        if(q.W < 0f)
            q = new Quaternion(-q.X, -q.Y, -q.Z, -q.W);
        float angle = 2f * MathF.Acos(Math.Clamp(q.W, -1f, 1f));
        return angle <= most || angle < 1e-6f ? q : Quaternion.Slerp(Quaternion.Identity, q, most / angle);
    }

    private bool LipMeshOn(OralSettings s) => s.MouthRig && s.LipMesh && Limber.Gpu.LipMesh.HasShapes && Limber.Gpu.LipMesh.Owner == _giver;

    private bool TryPath(ActorSkeleton giver, OralSettings s, float fromTip, float shaftLength, float blend, out MouthPath path)
    {
        path = default;
        if(!TryMouth(giver, out var mouthLocal, out var forward, out var up, out var head, out int part))
            return false;
        var info = giver.PartialInfo(part, _cache);
        if(info == null)
            return false;
        // How big this mouth is (the corners' spread against an average ~5 cm).
        float size = 1f;
        int cornerA = -1, cornerB = -1;
        for(int b = 0; b < info.Count; b++)
            if(info.Names[b].StartsWith("j_f_lip", StringComparison.Ordinal))
            {
                if(cornerA < 0) cornerA = b;
                else if(cornerB < 0) cornerB = b;
            }
        if(cornerA >= 0 && cornerB >= 0)
            size = Math.Clamp(Vector3.Distance(giver.GetPartialFrame(part, cornerA).Position, giver.GetPartialFrame(part, cornerB).Position) / 0.05f, 0.6f, 1.6f);

        // Where the shaft goes in: the lips' centre, nudged by the Mouth position sliders, and (with the lips' live
        // mesh) under the upper lip, as in Classic.
        var right = Norm(Vector3.Cross(up, forward));
        var nudge = right * s.OffsetRight + up * s.OffsetUp;
        if(LipMeshOn(s))
        {
            float r = GirthAt(fromTip, shaftLength, s) * Math.Clamp(s.OpenSize, 0.5f, 2f) * Math.Clamp(s.OpenHeight, 0.5f, 2f);
            nudge += up * MathF.Max(0f, r - UpperLipLift) * blend;
        }
        var e = mouthLocal - nudge;
        var inward = -forward;

        // Just behind the teeth (the teeth bone if the face has one).
        float teeth = -1f;
        for(int b = 0; b < info.Count; b++)
            if(info.Names[b].StartsWith("j_f_haguki", StringComparison.Ordinal))
                teeth = MathF.Max(teeth, Vector3.Dot(head.PointToLocal(giver.GetPartialFrame(part, b).Position) - mouthLocal, inward));
        float behindTeeth = teeth > 0f ? Math.Clamp(teeth + 0.006f * size, 0.008f * size, 0.03f * size) : 0.015f * size;
        var t = e + inward * behindTeeth;
        // The back of the mouth: ~8 cm in, a little low.
        float depth = 0.08f * size * Math.Clamp(s.MouthDepth, 0.5f, 1.5f);
        var r0 = e + inward * MathF.Max(depth, behindTeeth + 0.01f * size) - up * 0.01f * size;
        // Down the throat: along the neck (head -> the neck's base), from the back of the mouth.
        var down = -up;
        int neck = giver.Info.IndexOf("j_kubi");
        if(neck >= 0)
        {
            var d = head.PointToLocal(giver.GetModelPosition(neck));
            if(d.LengthSquared() > 1e-8f && Vector3.Dot(Vector3.Normalize(d), -up) > 0.3f)
                down = Vector3.Normalize(d);
        }
        // (not straight down: a mouth and throat only line up so far, and a shaft can't fold at a right angle there)
        var n = r0 + Norm(inward * 0.6f + down * 0.4f) * 0.09f * size;

        path = new MouthPath
        {
            Head = head, E = e, T = t, R = r0, N = n, LocalForward = forward, LocalUp = up, FaceSize = size,
            Back = Vector3.Distance(e, t) + Vector3.Distance(t, r0),
        };
        path.Total = path.Back + (s.Throat ? Vector3.Distance(r0, n) : 0f);
        return MathUtil.IsFinite(e) && MathUtil.IsFinite(n);
    }

    // ---- The shaft's thickness by distance from the tip (built once per shaft), in the giver's model metres

    private float[] _girth = [];
    private string _girthKey = "";
    public volatile string GirthInfo = "";
    private const int GirthSteps = 32;

    private void EnsureGirth(OralSettings s, in Shaft shaft, float giverScale)
    {
        string key = (shaft.Measured ? "m|" + (_shaftVertices?.Key ?? "?") : "b|" + s.Thickness.ToString("0.00000"))
            + "|" + giverScale.ToString("0.000");
        if(key == _girthKey && _girth.Length == GirthSteps + 1)
            return;
        var table = new float[GirthSteps + 1];
        float thick = MathF.Max(0.004f, s.Thickness);
        for(int i = 0; i <= GirthSteps; i++)
        {
            float u = i / (float)GirthSteps; // 0 = the tip, 1 = the base
            float measured = shaft.Measured ? shaft.RadiusAt(u * shaft.Length) : -1f;
            table[i] = measured > 0f
                ? measured / MathF.Max(0.01f, giverScale)
                : thick * (0.72f + 0.28f * Math.Clamp(u / 0.1f, 0f, 1f)); // (thinner right at the tip)
        }
        _girth = table;
        _girthKey = key;
        float widest = 0f;
        foreach(var r in table)
            widest = MathF.Max(widest, r);
        GirthInfo = $"Thickness {(shaft.Measured ? "read from the model" : "from the Thickness slider")}: up to {widest * 200f:0.0} cm across.";
    }

    /// <summary>The shaft's radius <paramref name="fromTip"/> (world metres) down from its tip, in the giver's model metres.</summary>
    private float GirthAt(float fromTip, float length, OralSettings s)
    {
        var table = _girth;
        if(table.Length != GirthSteps + 1 || length <= 1e-4f)
            return MathF.Max(0.004f, s.Thickness);
        float x = Math.Clamp(fromTip / length, 0f, 1f) * GirthSteps;
        int i = Math.Min((int)x, GirthSteps - 1);
        return table[i] + (table[i + 1] - table[i]) * (x - i);
    }

    // ---- Posing stage: where the head goes (only a share of the way)

    private Frame? HeadTargetPath(OralSettings s, Frame held, ActorSkeleton giver, Shaft shaft, float blend)
    {
        float unit = MathF.Max(0.01f, giver.RootScale.Y);
        EnsureGirth(s, shaft, unit);
        float length = shaft.Length;
        float fromTip = _depth * length;
        if(!TryPath(giver, s, fromTip, length, blend, out var path))
        {
            Status = "Can't find this character's lips.";
            return null;
        }
        // Never deeper than the mouth goes (the back of the mouth, or down the throat when that's allowed).
        float limit = MathF.Max(0f, path.Total * unit - 0.004f * unit);
        bool limited = fromTip > limit;
        fromTip = Math.Clamp(fromTip, 0f, limit);

        var bottom = giver.WorldToModelPoint(shaft.Points[0]);
        var onShaft = giver.WorldToModelPoint(shaft.At(fromTip).Point);
        // (outside the mouth the shaft lies straight from its base to the lips: that far from the base)
        float outside = MathF.Max(0.005f, (length - fromTip) / unit);
        var lipsHeld = held.PointFromLocal(path.E);
        var toShaft = Norm(onShaft - bottom);
        var toHead = Norm(lipsHeld - bottom);
        // The head comes this share of the way round (seen from the shaft's base); the shaft swings the rest, but
        // never more than its most: past that the head comes further.
        float most = Math.Clamp(s.MaxShaftBend, 1f, 90f) * MathF.PI / 180f;
        float theta = Angle(toHead, toShaft);
        float share = Math.Clamp(s.HeadShare, 0f, 1f);
        if(theta * (1f - share) > most)
            share = 1f - most / theta;
        var dir = Norm(Vector3.Transform(toHead, Quaternion.Slerp(Quaternion.Identity, MathUtil.FromTo(toHead, toShaft), share)));
        var lips = bottom + dir * outside;

        // Facing down that line (a share of the way; the shaft bends at the lips for the rest).
        var forward = -dir;
        var nowForward = Vector3.Transform(path.LocalForward, held.Rotation);
        var nowUp = Vector3.Transform(path.LocalUp, held.Rotation);
        var up = nowUp - forward * Vector3.Dot(nowUp, forward);
        if(up.LengthSquared() < 1e-6f)
            up = Vector3.UnitY - forward * forward.Y;
        var full = Quaternion.Normalize(MathUtil.AlignFrames(nowForward, nowUp, forward, Norm(up)) * held.Rotation);
        float phi = 2f * MathF.Acos(Math.Clamp(MathF.Abs(Quaternion.Dot(held.Rotation, full)), 0f, 1f));
        float turnShare = Math.Clamp(s.HeadTurnShare, 0f, 1f);
        if(phi * (1f - turnShare) > most)
            turnShare = 1f - most / phi;
        var rotation = Quaternion.Normalize(Quaternion.Slerp(held.Rotation, full, turnShare));
        // Pitch on top (head angle + the nod; a big shaft tips the chin up), as in Classic.
        float pitch = (s.HeadAngle + _nod - Math.Clamp(s.ChinUp, 0f, 40f) * _bigness * blend) * MathF.PI / 180f;
        if(pitch != 0f)
        {
            var side = Norm(Vector3.Cross(Vector3.Transform(path.LocalUp, rotation), Vector3.Transform(path.LocalForward, rotation)));
            rotation = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(side, pitch) * rotation);
        }
        var position = lips - Vector3.Transform(path.E, rotation);
        if(!MathUtil.IsFinite(position) || !MathUtil.IsFinite(rotation))
            return null;

        string numbers = $"shaft {length * 100f:0.0} cm{(shaft.Measured ? " (measured)" : " (bones)")}, head comes {share * 100f:0}% of the way "
            + $"({Vector3.Distance(held.Position, position) * 100f:0} cm), shaft swings {theta * (1f - share) * 180f / MathF.PI:0}° "
            + $"and bends {phi * (1f - turnShare) * 180f / MathF.PI:0}° at the lips";
        Status = Vector3.Distance(held.Position, position) > 0.6f
            ? "Following, but the partner is far away: move the two closer together. (" + numbers + ")"
            : (limited ? "At the back of the mouth: " : "Following: ") + numbers + ".";
        return new Frame(Vector3.Lerp(held.Position, position, blend), Quaternion.Slerp(held.Rotation, rotation, blend));
    }

    // ---- Mouth stage (after posing): the shaft bends onto the mouth's path

    private sealed class BendWrite
    {
        public required nint Receiver;
        public required ushort Index;
        public required (int Part, int Bone)[] Bones;   // parents first (drawn copies, then the body's copies)
        public required Frame[] Before;
        public required Frame[] Written;
    }

    private BendWrite? _bendWrite;          // bone update thread only
    private Vector3[]? _bendOffsets;        // per joint: how far the bend moved it (partner's model space), last frame
    private Vector3[]? _pathWorld;          // this frame's mouth path (world): lips, behind the teeth, back of the mouth[, throat]
    private float _pathBackWorld;           // lips to the back of the mouth (world metres)
    private long _lastBoneTicks;
    private float _lastInside;
    private float _pushPull;                // model metres, + = pushed in
    private float _centreGrip;              // model metres the lips' centre is held along (live mesh)
    private readonly Dictionary<int, (int Joint, Vector3 Local)> _anchors = new(); // lip bone -> where it holds on (shaft joint space)
    private volatile bool _pathActive;
    private volatile float _inside, _back, _shaftLength, _pushPullShown;

    private float BoneDt()
    {
        long now = Stopwatch.GetTimestamp();
        float dt = _lastBoneTicks == 0 ? 1f / 60f : (float)((now - _lastBoneTicks) / (double)Stopwatch.Frequency);
        _lastBoneTicks = now;
        return Math.Clamp(dt, 1e-4f, 0.1f);
    }

    private static bool Near(Vector3 a, Vector3 b) => Vector3.DistanceSquared(a, b) < 1e-10f;
    private static bool Near(Quaternion a, Quaternion b) => MathF.Abs(Quaternion.Dot(a, b)) > 0.9999999f;

    /// <summary>Before posing: if the game didn't rebuild the partner's pose since last frame (paused), the bones still
    /// hold last frame's bend: put back what they had, so it doesn't pile up.</summary>
    private void RestoreBend()
    {
        var w = _bendWrite;
        _bendWrite = null;
        if(w == null || !ActorSkeleton.TryGet(w.Receiver, w.Index, _cache, out var sk) || sk.Info.Count == 0)
            return;
        Frame Get((int Part, int Bone) x) => x.Part == 0 ? sk.GetModelFrame(x.Bone) : sk.GetPartialFrame(x.Part, x.Bone);
        for(int i = 0; i < w.Bones.Length; i++)
        {
            var f = Get(w.Bones[i]);
            if(!Near(f.Position, w.Written[i].Position) || !Near(f.Rotation, w.Written[i].Rotation))
                return; // rebuilt (or changed by something else): leave it
        }
        for(int i = 0; i < w.Bones.Length; i++)
        {
            var x = w.Bones[i];
            if(x.Part == 0)
            {
                sk.SetModelRotation(x.Bone, w.Before[i].Rotation);
                sk.SetModelPosition(x.Bone, w.Before[i].Position);
            }
            else
            {
                sk.SetPartialRotation(x.Part, x.Bone, w.Before[i].Rotation);
                sk.SetPartialPosition(x.Part, x.Bone, w.Before[i].Position);
            }
        }
    }

    /// <summary>Bone update thread, when Oral isn't running: forget the Path style's state.</summary>
    private void ResetPath()
    {
        _bendOffsets = null;
        _pathWorld = null;
        _anchors.Clear();
        _pushPull = 0f;
        _centreGrip = 0f;
        _lastInside = 0f;
        _pathActive = false;
        _inside = 0f;
        _pushPullShown = 0f;
    }

    // The point `along` metres along the path (world), carrying on straight past its end.
    private static Vector3 PathAt(Vector3[] path, int count, float along)
    {
        for(int i = 1; i < count; i++)
        {
            float seg = Vector3.Distance(path[i - 1], path[i]);
            if(along <= seg || i == count - 1)
                return path[i - 1] + Norm(path[i] - path[i - 1]) * along;
            along -= seg;
        }
        return path[0];
    }

    private void BendShaft(OralSettings s, ActorSkeleton giver, float blend, float dt)
    {
        _pathWorld = null;
        if(!ActorSkeleton.TryGet(_receiver, _receiverIndex, _cache, out var rsk) || rsk.Info.Count == 0)
            return;
        if(!TryShaft(out var rest))
            return;
        float unit = MathF.Max(0.01f, giver.RootScale.Y);
        EnsureGirth(s, rest, unit);
        if(!TryPath(giver, s, _depth * rest.Length, rest.Length, blend, out var path))
            return;
        Vector3 World(Vector3 local) => giver.ModelToWorldPoint(path.Head.PointFromLocal(local));
        int count = s.Throat ? 4 : 3;
        var pathWorld = new[] { World(path.E), World(path.T), World(path.R), World(path.N) };
        _pathWorld = pathWorld;
        _pathBackWorld = path.Back * unit;

        // The shaft's joints: the copy the model follows, and the body's own copy when that's a different one.
        var drawn = new List<(int Part, int Bone)>(ShaftBones.Length);
        var bodyCopy = new List<int>(ShaftBones.Length);
        foreach(var name in ShaftBones)
        {
            if(rsk.TryFindExtraBone(name, _cache, out int p, out int b))
            {
                drawn.Add((p, b));
                bodyCopy.Add(rsk.Info.IndexOf(name));
            }
            else if(rsk.Info.IndexOf(name) is int body and >= 0)
            {
                drawn.Add((0, body));
                bodyCopy.Add(-1);
            }
        }
        int n = drawn.Count;
        if(n < 2)
            return;
        Frame Get((int Part, int Bone) x) => x.Part == 0 ? rsk.GetModelFrame(x.Bone) : rsk.GetPartialFrame(x.Part, x.Bone);
        void SetRotation((int Part, int Bone) x, Quaternion q)
        {
            if(x.Part == 0) rsk.SetModelRotation(x.Bone, q);
            else rsk.SetPartialRotation(x.Part, x.Bone, q);
        }

        // As posed (model space), with the tip continued past the last bone (as the shaft is read).
        var posed = new Vector3[n + 1];
        for(int i = 0; i < n; i++)
            posed[i] = Get(drawn[i]).Position;
        posed[n] = posed[n - 1] + (posed[n - 1] - posed[n - 2]) * 0.6f;
        var lastFrame = Get(drawn[n - 1]);
        var tipLocal = Vector3.Transform(posed[n] - lastFrame.Position, Quaternion.Inverse(lastFrame.Rotation));
        var world = new Vector3[n + 1];
        var along = new float[n + 1];
        for(int i = 0; i <= n; i++)
        {
            world[i] = rsk.ModelToWorldPoint(posed[i]);
            if(i > 0)
                along[i] = along[i - 1] + Vector3.Distance(world[i - 1], world[i]);
        }
        float length = along[n];
        if(length < 0.01f)
            return;

        // Where each joint goes on the path: outside on the line from the base to the lips, the one crossing the lips
        // by a triangle (so the bend is at the lips), the rest along the path inside.
        var bottom = world[0];
        var entry = pathWorld[0];
        float reach = Vector3.Distance(bottom, entry);
        var outward = Norm(entry - bottom);
        var inward = Norm(pathWorld[1] - pathWorld[0]);
        var onPath = new Vector3[n + 1];
        int cross = 0;
        while(cross <= n && along[cross] <= reach)
        {
            onPath[cross] = bottom + outward * along[cross];
            cross++;
        }
        if(cross <= n)
        {
            float a = MathF.Max(0f, reach - along[cross - 1]);
            float bone = along[cross] - along[cross - 1];
            float cos = Vector3.Dot(-outward, inward);
            float x = a * cos + MathF.Sqrt(MathF.Max(0f, bone * bone - a * a * (1f - cos * cos)));
            x = MathF.Max(0f, x);
            for(int i = cross; i <= n; i++)
                onPath[i] = PathAt(pathWorld, count, x + along[i] - along[cross]);
        }

        // Eased onto the path as the tip comes within ~20 cm of the lips.
        float gap = reach < length ? 0f : Vector3.Distance(world[n], entry);
        float near = 1f - Smoother(gap / (0.2f * MathF.Max(0.5f, unit)));
        float amount = near * blend * Math.Clamp(s.ShaftFollows, 0f, 1f);

        // How far the bend moves each joint (partner's model space), changing no faster than the tip speed allows.
        float rscale = MathF.Max(0.01f, rsk.RootScale.Y);
        float step = MathF.Max(0.02f, s.TipSpeed) * dt / rscale;
        var offsets = new Vector3[n + 1];
        var last = _bendOffsets != null && _bendOffsets.Length == n + 1 ? _bendOffsets : new Vector3[n + 1];
        for(int i = 0; i <= n; i++)
        {
            var target = rsk.WorldToModelPoint(Vector3.Lerp(world[i], onPath[i], amount)) - posed[i];
            if(!MathUtil.IsFinite(target))
                target = Vector3.Zero;
            var change = target - last[i];
            float len = change.Length();
            offsets[i] = len > step ? last[i] + change * (step / len) : target;
        }
        _bendOffsets = offsets;

        // Before writing: what the pose has (to put back if the game doesn't rebuild it), parents first.
        var copies = new List<(int Part, int Bone)>(n * 2);
        copies.AddRange(drawn);
        for(int i = 0; i < n; i++)
            if(bodyCopy[i] >= 0 && drawn[i].Part != 0)
                copies.Add((0, bodyCopy[i]));
        var before = new Frame[copies.Count];
        for(int i = 0; i < copies.Count; i++)
            before[i] = Get(copies[i]);

        // Each joint turned (parents first) so the next lands where it should; never more than the most a joint may bend.
        float most = Math.Clamp(s.MaxShaftBend, 1f, 90f) * MathF.PI / 180f;
        float swing = 0f;
        for(int i = 0; i < n; i++)
        {
            var at = Get(drawn[i]);
            var child = i + 1 < n ? Get(drawn[i + 1]).Position : at.Position + Vector3.Transform(tipLocal, at.Rotation);
            var want = posed[i + 1] + offsets[i + 1] - at.Position;
            var turn = LimitTurn(MathUtil.FromTo(child - at.Position, want), most);
            if(i == 0)
                swing = 2f * MathF.Acos(Math.Clamp(MathF.Abs(turn.W), 0f, 1f));
            if(!MathUtil.IsFinite(turn))
                continue;
            SetRotation(drawn[i], Quaternion.Normalize(turn * at.Rotation));
        }
        // The body's own copies the same (so nothing that copies the body onto the drawn ones undoes it).
        for(int i = 0; i < n; i++)
            if(bodyCopy[i] >= 0 && drawn[i].Part != 0)
            {
                var f = Get(drawn[i]);
                rsk.SetModelRotation(bodyCopy[i], f.Rotation);
                rsk.SetModelPosition(bodyCopy[i], f.Position);
            }
        var written = new Frame[copies.Count];
        for(int i = 0; i < copies.Count; i++)
            written[i] = Get(copies[i]);
        _bendWrite = new BendWrite { Receiver = _receiver, Index = _receiverIndex, Bones = copies.ToArray(), Before = before, Written = written };
        _shaftSwing = swing * 180f / MathF.PI;
    }

    private volatile float _shaftSwing;
    /// <summary>How far (degrees) the Path style turned the shaft at its base last frame (for the page).</summary>
    public float ShaftSwing => _pathActive ? _shaftSwing : 0f;

    // How far down from the tip the shaft crosses the plane of `point` (world): the point's nearest place on the shaft.
    private static float FromTipAt(in Shaft shaft, Vector3 point, out float distance)
    {
        float best = float.MaxValue, at = 0f;
        for(int i = 1; i < shaft.Points.Length; i++)
        {
            var a = shaft.Points[i - 1];
            var d = shaft.Points[i] - a;
            float len2 = d.LengthSquared();
            float t = len2 > 1e-12f ? Math.Clamp(Vector3.Dot(point - a, d) / len2, 0f, 1f) : 0f;
            float dist = Vector3.DistanceSquared(point, a + d * t);
            if(dist < best)
            {
                best = dist;
                at = shaft.Along[i - 1] + (shaft.Along[i] - shaft.Along[i - 1]) * t;
            }
        }
        distance = MathF.Sqrt(best);
        return Math.Clamp(shaft.Length - at, 0f, shaft.Length);
    }

    // ---- Lip grip: each lip holds on to a spot on the shaft (kept in that shaft joint's own space, so it's dragged
    // along however the shaft moves or bends) until it's stretched too far along it, then slips.

    private bool ShaftJoint(in ActorSkeleton rsk, int joint, out Frame frame)
    {
        frame = Frame.Identity;
        return joint >= 0 && joint < ShaftBones.Length && rsk.TryGetDrawnWorldFrame(ShaftBones[joint], _cache, out frame);
    }

    private void HoldOn(int key, Vector3 world, in ActorSkeleton rsk)
    {
        int best = -1;
        float bestDistance = float.MaxValue;
        Frame bestFrame = Frame.Identity;
        for(int j = 0; j < ShaftBones.Length; j++)
            if(ShaftJoint(rsk, j, out var f) && Vector3.DistanceSquared(f.Position, world) < bestDistance)
            {
                bestDistance = Vector3.DistanceSquared(f.Position, world);
                best = j;
                bestFrame = f;
            }
        if(best >= 0)
            _anchors[key] = (best, bestFrame.PointToLocal(world));
        else
            _anchors.Remove(key);
    }

    /// <summary>How far (model metres, along the shaft) the lip at <paramref name="target"/> is held from there.</summary>
    private float Grip(int key, Vector3 target, Vector3 axis, float slip, in ActorSkeleton giver, in ActorSkeleton rsk)
    {
        var world = giver.ModelToWorldPoint(target);
        if(!_anchors.TryGetValue(key, out var a) || !ShaftJoint(rsk, a.Joint, out var joint))
        {
            HoldOn(key, world, rsk);
            return 0f;
        }
        var held = giver.WorldToModelPoint(joint.PointFromLocal(a.Local));
        float off = Vector3.Dot(held - target, axis);
        if(!float.IsFinite(off))
        {
            _anchors.Remove(key);
            return 0f;
        }
        if(MathF.Abs(off) > slip)
        {
            // Slipped: it holds on again where it's got to.
            off = MathF.CopySign(slip, off);
            HoldOn(key, giver.ModelToWorldPoint(target + axis * off), rsk);
        }
        return off;
    }

    // ---- Teeth tucked behind the lips, as far as the shaft's thickness needs

    private static void TuckTeeth(in ActorSkeleton giver, int part, SkeletonInfo info, Vector3 axisPoint, Vector3 axis, Vector3 up,
        float radius, float scale, float amount, float big)
    {
        if(amount <= 0f)
            return;
        for(int b = 0; b < info.Count; b++)
        {
            if(!info.Names[b].StartsWith("j_f_haguki", StringComparison.Ordinal))
                continue;
            var frame = giver.GetPartialFrame(part, b);
            var v = frame.Position - axisPoint;
            float side = Vector3.Dot(v, up);
            float sign = side >= 0f ? 1f : -1f;
            // (the teeth's edges reach ~7 mm past their bone toward the shaft)
            float lift = Math.Clamp(radius + 0.003f * scale + 0.007f * scale - MathF.Abs(side), 0f, 0.015f * scale);
            var moved = frame.Position + (up * sign * lift + axis * 0.004f * scale) * amount;
            // (this bone only: the lower lip can hang from the teeth, and must stay where it is)
            giver.SetPartialPosition(part, b, moved, children: false);
            giver.SetPartialScale(part, b, giver.GetPartialScale(part, b) * (1f - 0.15f * big * amount), children: false);
        }
    }
}
