using Limber.Core;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Limber.Rig;

/// <summary>
/// Grab: each finger lies along the skin it's over. Every joint, knuckle to tip, is turned so the next one sits on
/// the skin (the nearest bit of it, offset by the finger's thickness): it bends in or straightens, and the knuckle
/// spreads a little, so the finger follows the curve of what it holds. A few passes a frame, starting from last
/// frame's answer; what's shown eases toward it (never more than a few degrees a frame), so it never snaps.
/// The skin is held still (shape only, no jiggle, collisions or dents), so nothing the fingers do moves it.
/// </summary>
public sealed class FingerGrab
{
    public sealed class Hand
    {
        public bool Left, Right;
        public float Grip = 4f; // mm into the skin
        /// <summary>0..1: how firmly each finger keeps the spot of skin it holds and resists turning (less sliding,
        /// steadier, slower to follow the hand).</summary>
        public float Rigidity = 0.3f;
        /// <summary>0..1: the four fingers fanned out evenly across the hand, wider as it goes up.</summary>
        public float Spread;
        /// <summary>Lock: the breast under this hand is carried with it (its soft body bones follow the hand).</summary>
        public bool LockLeft, LockRight;
        public float LockStrength = 1f;
        public bool LockWhole = true;   // the whole breast the hand is on (else just the part under it)
        public ushort Index; // the character's object index (to find its skeleton)
    }

    private static readonly string[] Fingers = ["oya", "hito", "naka", "kusu", "ko"];
    // The finger's movement (radians, on top of the pose): knuckle bend, curl (middle joint; the tip joint follows at
    // Couple of it), spread at the knuckle (wider for the thumb).
    private const float KnuckleMin = -0.3f, KnuckleMax = 1.5f, CurlMax = 1.7f, Couple = 0.7f;
    private const float SpreadMax = 0.3f, ThumbSpread = 0.4f;
    private const float Reach = 0.06f;                      // skin further than this from a point of the finger isn't seen
    private const float Range = 0.045f;                     // skin this far beyond touching draws the finger onto it
    private const float PoseWeight = 0.002f, NeighbourWeight = 0.02f, RigidWeight = 0.05f;
    private const float Window = 0.25f;                     // a frame's search either side of last frame's answer
    private const int Golden = 8, Nearest = 8;
    private const float MinGap = 0.08f;                     // radians kept between neighbouring fingers (no crossing)
    // What's shown eases toward the answer: a share of the way a frame and at most so far, both less when rigid.
    private const float EaseLoose = 0.35f, EaseRigid = 0.06f, StepLoose = 0.07f, StepRigid = 0.012f;
    private const float Deadband = 0.03f;                   // radians, at full rigidity: smaller changes are ignored
    private const float FanBase = 0.10f, FanWide = 0.12f;   // radians between neighbouring fingers (Spread)
    private const float Cell = 0.03f;
    /// <summary>A finger's own thickness (m, before the hand collider size): what touches the skin when grabbing.</summary>
    public const float VisibleFinger = 0.0075f;

    /// <summary>Grab settings by character.</summary>
    public readonly Dictionary<nint, Hand> Hands = new();
    /// <summary>The skin that can be held (world points and their outward normals; set by the plugin).</summary>
    public Func<(Vector3[] Points, Vector3[] Normals)> Surface = () => ([], []);
    /// <summary>The fingers' thickness (m) for touching.</summary>
    public Func<float> FingerRadius = () => VisibleFinger;

    private sealed class State
    {
        public readonly float[] Bend = new float[3], Spread = new float[3];     // the solution (warm start)
        public readonly float[] ShownBend = new float[3], ShownSpread = new float[3];
        public readonly float[] P = new float[3];   // knuckle bend, curl, spread: the fitted answer (warm start)
        public bool Started;
        public float Fan;                            // the sideways turn Spread adds at the knuckle (eased)
    }

    private readonly Dictionary<(nint, int), State> _states = new();
    private readonly Dictionary<(int, int, int), List<int>> _grid = new();
    private Vector3[] _points = [];
    private Vector3[] _normals = [];

    // Bone update (posing done), under the rig lock.
    public void Apply(nint address, in ActorSkeleton skeleton)
    {
        if(!Hands.TryGetValue(address, out var hand) || (!hand.Left && !hand.Right))
            return;
        var (points, normals) = Surface();
        if(points == null || normals == null || points.Length == 0 || normals.Length != points.Length)
            return;
        if(!ReferenceEquals(points, _points))
        {
            _points = points;
            _normals = normals;
            _grid.Clear();
            for(int i = 0; i < points.Length; i++)
            {
                var key = Key(points[i]);
                if(!_grid.TryGetValue(key, out var list))
                    _grid[key] = list = new List<int>();
                list.Add(i);
            }
        }
        float scale = skeleton.RootScale.Y > 0.01f ? skeleton.RootScale.Y : 1f;
        float reach = (Math.Clamp(FingerRadius(), 0.002f, 0.05f) - Math.Clamp(hand.Grip, 0f, 30f) * 0.001f) * scale;
        for(int s = 0; s < 2; s++)
        {
            if(s == 0 ? !hand.Left : !hand.Right)
                continue;
            string side = s == 0 ? "_l" : "_r";
            // Locked: the breast is carried with the hand, so the fingers keep the hold they have (not re-aimed).
            bool frozen = s == 0 ? hand.LockLeft : hand.LockRight;
            for(int f = 0; f < Fingers.Length; f++)
                Finger(address, skeleton, Fingers[f], side, s * 8 + f, reach, scale, hand.Rigidity, frozen);
            FanOut(address, skeleton, side, s * 8, hand.Spread, hand.Rigidity);
        }
    }

    // Spread: the four fingers (not the thumb) fanned out evenly across the hand around their middle, every gap the
    // same, wider with the slider. Measured on the fingers as they are (after the grab), turned at the knuckle about
    // the hand's own normal.
    private void FanOut(nint address, in ActorSkeleton skeleton, string side, int slot, float spread, float rigidity)
    {
        spread = Math.Clamp(spread, 0f, 1f);
        var info = skeleton.Info;
        var knuckle = new int[4];
        var last = new int[4];
        for(int f = 0; f < 4; f++)
        {
            string finger = Fingers[f + 1];
            knuckle[f] = info.IndexOf($"j_{finger}_a{side}");
            last[f] = info.IndexOf($"iv_{finger}_c{side}");
            if(last[f] < 0)
                last[f] = info.IndexOf($"j_{finger}_b{side}");
            if(knuckle[f] < 0 || last[f] < 0)
                return;
        }
        var dir = new Vector3[4];
        var forward = Vector3.Zero;
        for(int f = 0; f < 4; f++)
        {
            var end = skeleton.GetModelFrame(last[f]);
            var tipAt = end.Position + Vector3.Transform(new Vector3(0.014f, 0f, 0f), end.Rotation);
            var d = tipAt - skeleton.GetModelPosition(knuckle[f]);
            if(d.LengthSquared() < 1e-10f)
                return;
            dir[f] = Vector3.Normalize(d);
            forward += dir[f];
        }
        var across = skeleton.GetModelPosition(knuckle[3]) - skeleton.GetModelPosition(knuckle[0]); // index -> little
        if(forward.LengthSquared() < 1e-10f || across.LengthSquared() < 1e-10f)
            return;
        forward = Vector3.Normalize(forward);
        across = Vector3.Normalize(across - forward * Vector3.Dot(across, forward));
        var normal = Vector3.Normalize(Vector3.Cross(forward, across)); // turning about it moves forward toward across

        var angle = new float[4];
        for(int f = 0; f < 4; f++)
            angle[f] = MathF.Atan2(Vector3.Dot(dir[f], across), Vector3.Dot(dir[f], forward));
        float centre = (angle[0] + angle[1] + angle[2] + angle[3]) * 0.25f;
        float gap = (angle[3] - angle[0]) / 3f;
        float wanted = gap + spread * (FanWide + MathF.Max(0f, FanBase - gap));
        float ease = Lerp(EaseLoose, EaseRigid, rigidity), step = Lerp(StepLoose, StepRigid, rigidity);
        // Where each finger should point: fanned with the slider, then kept at least MinGap from its neighbours (even
        // at Spread 0) so fingers fitted to the skin one by one never cross or sit inside each other.
        var aim = new float[4];
        for(int f = 0; f < 4; f++)
            aim[f] = angle[f] + (centre + (f - 1.5f) * wanted - angle[f]) * spread;
        for(int pass = 0; pass < 4; pass++)
            for(int f = 0; f < 3; f++)
            {
                float short_ = MinGap - (aim[f + 1] - aim[f]);
                if(short_ > 0f)
                {
                    aim[f] -= short_ * 0.5f;
                    aim[f + 1] += short_ * 0.5f;
                }
            }
        for(int f = 0; f < 4; f++)
        {
            var key = (address, slot + f + 1);
            if(!_states.TryGetValue(key, out var state))
                continue;
            // (measured on this frame's pose, which doesn't have the fan in it yet)
            float target = Math.Clamp(aim[f] - angle[f], -0.35f, 0.35f);
            state.Fan += Math.Clamp((target - state.Fan) * ease, -step, step);
            if(!float.IsFinite(state.Fan))
                state.Fan = 0f;
            if(MathF.Abs(state.Fan) < 1e-5f)
                continue;
            var current = skeleton.GetModelFrame(knuckle[f]).Rotation;
            skeleton.SetModelRotation(knuckle[f], Quaternion.Normalize(Quaternion.CreateFromAxisAngle(normal, state.Fan) * current));
        }
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * Math.Clamp(t, 0f, 1f);

    private void Finger(nint address, in ActorSkeleton skeleton, string finger, string side, int slot, float reach, float scale, float rigidity, bool frozen)
    {
        var info = skeleton.Info;
        var bones = new int[3];
        int n = 0;
        foreach(var name in new[] { $"j_{finger}_a{side}", $"j_{finger}_b{side}", $"iv_{finger}_c{side}" })
        {
            int b = info.IndexOf(name);
            if(b < 0)
                break;
            bones[n++] = b;
        }
        if(n < 2)
            return;

        // The chain as posed (model space), each joint relative to the one before, and the world transform.
        var rot = new Quaternion[3];
        var pos = new Vector3[3];
        for(int k = 0; k < n; k++)
        {
            var frame = skeleton.GetModelFrame(bones[k]);
            rot[k] = frame.Rotation;
            pos[k] = frame.Position;
        }
        var rel = new Quaternion[3];
        var off = new Vector3[3];
        for(int k = 1; k < n; k++)
        {
            rel[k] = Quaternion.Normalize(Quaternion.Inverse(rot[k - 1]) * rot[k]);
            off[k] = Vector3.Transform(pos[k] - pos[k - 1], Quaternion.Inverse(rot[k - 1]));
        }
        var tip = new Vector3(finger == "oya" ? 0.022f : 0.014f, 0f, 0f);
        var toWorld = skeleton.ModelToWorld;
        Matrix4x4.Invert(toWorld, out var toModel);

        var key = (address, slot);
        if(!_states.TryGetValue(key, out var state))
            _states[key] = state = new State();

        // Forward: joint k's model rotation and every joint's model position with these turns.
        var r = new Quaternion[3];
        var j = new Vector3[4];
        void Forward(float[] bend, float[] spread)
        {
            var q = rot[0] * Local(bend[0], spread[0]);
            j[0] = pos[0];
            r[0] = q;
            for(int k = 1; k < n; k++)
            {
                j[k] = j[k - 1] + Vector3.Transform(off[k], q);
                q = q * rel[k] * Local(bend[k], spread[k]);
                r[k] = q;
            }
            j[n] = j[n - 1] + Vector3.Transform(tip, q);
        }

        // The finger as a whole: a knuckle bend m, a curl c (the middle joint c, the tip joint Couple x c, as a real
        // finger moves) and a spread s at the knuckle. Fitted so its pads lie on the skin: pushed out where they're
        // in it, drawn onto it where it's within reach, with a light pull toward the pose, toward the neighbouring
        // fingers' curl (the hand closes as one), and (Rigidity) toward last frame's answer.
        bool thumb = finger == "oya";
        float neighbour = float.NaN;
        if(!thumb)
        {
            float sum = 0f;
            int count = 0;
            foreach(int other in new[] { slot - 1, slot + 1 })
                if(other % 8 >= 1 && other % 8 <= 4 && _states.TryGetValue((address, other), out var near) && near.Started)
                {
                    sum += near.P[1];
                    count++;
                }
            if(count > 0)
                neighbour = sum / count;
        }
        var bendTry = new float[3];
        var spreadTry = new float[3];
        float pm = state.P[0], pc = state.P[1], ps = state.P[2];
        int touching = 0;
        float Energy(float m, float c, float sp, bool count)
        {
            bendTry[0] = m;
            bendTry[1] = c;
            bendTry[2] = n == 3 ? c * Couple : 0f;
            spreadTry[0] = sp;
            Forward(bendTry, spreadTry);
            float e = 0f;
            for(int k = 0; k < n; k++)
            {
                for(int h = 1; h <= 2; h++)
                {
                    var at = j[k] + (j[k + 1] - j[k]) * (h * 0.5f);
                    var world = Vector3.Transform(at, toWorld);
                    if(!Near(world, scale, out var skin, out var normal))
                        continue;
                    if(count)
                        touching++;
                    float d = (Vector3.Dot(world - skin, normal) - reach) / scale;
                    if(d < 0f)
                        e += 200f * d * d;                              // in the skin: out
                    else if(d < Range)
                        e += 4f * d * (1f - 0.5f * d / Range);          // near: onto it
                }
            }
            e += PoseWeight * (m * m + c * c + sp * sp);
            if(!float.IsNaN(neighbour))
                e += NeighbourWeight * (c - neighbour) * (c - neighbour);
            e += rigidity * RigidWeight * ((m - pm) * (m - pm) + (c - pc) * (c - pc) + (sp - ps) * (sp - ps));
            return e;
        }

        bool any = frozen;
        if(!frozen)
        {
            var p = state.P;
            float window = state.Started ? Window : 10f;
            for(int i = 0; i < 3; i++)
            {
                float lo = i == 0 ? KnuckleMin : i == 1 ? 0f : -(thumb ? ThumbSpread : SpreadMax);
                float hi = i == 0 ? KnuckleMax : i == 1 ? CurlMax : (thumb ? ThumbSpread : SpreadMax);
                float a = MathF.Max(lo, p[i] - window), b = MathF.Min(hi, p[i] + window);
                float E(float v)
                {
                    float m = i == 0 ? v : p[0], c = i == 1 ? v : p[1], sp = i == 2 ? v : p[2];
                    return Energy(m, c, sp, false);
                }
                // Golden-section search for this one, the others held.
                const float G = 0.618034f;
                float x1 = b - G * (b - a), x2 = a + G * (b - a), f1 = E(x1), f2 = E(x2);
                for(int it = 0; it < Golden; it++)
                {
                    if(f1 < f2)
                    {
                        b = x2; x2 = x1; f2 = f1; x1 = b - G * (b - a); f1 = E(x1);
                    }
                    else
                    {
                        a = x1; x1 = x2; f1 = f2; x2 = a + G * (b - a); f2 = E(x2);
                    }
                }
                float best = (a + b) * 0.5f;
                if(E(best) < E(p[i]))
                    p[i] = best;
            }
            Energy(p[0], p[1], p[2], true);
            any = touching > 0;
            state.Started = true;
            state.Bend[0] = p[0];
            state.Bend[1] = p[1];
            state.Bend[2] = n == 3 ? p[1] * Couple : 0f;
            state.Spread[0] = p[2];
            state.Spread[1] = state.Spread[2] = 0f;
        }
        if(!any)
        {
            // Nothing in reach: back to the pose (eased).
            Array.Clear(state.Bend);
            Array.Clear(state.Spread);
            Array.Clear(state.P);
            state.Started = false;
        }
        // Rigid: slower, and changes too small to matter are ignored (no creeping).
        float ease = Lerp(EaseLoose, EaseRigid, rigidity), step = Lerp(StepLoose, StepRigid, rigidity);
        float dead = Deadband * Math.Clamp(rigidity, 0f, 1f);
        for(int k = 0; k < 3 && !frozen; k++)
        {
            float db = state.Bend[k] - state.ShownBend[k], ds = state.Spread[k] - state.ShownSpread[k];
            if(MathF.Abs(db) > dead)
                state.ShownBend[k] += Math.Clamp(db * ease, -step, step);
            if(MathF.Abs(ds) > dead)
                state.ShownSpread[k] += Math.Clamp(ds * ease, -step, step);
            if(!float.IsFinite(state.ShownBend[k]) || !float.IsFinite(state.ShownSpread[k]))
            {
                state.ShownBend[k] = state.ShownSpread[k] = 0f;
                state.Bend[k] = state.Spread[k] = 0f;
            }
        }

        // Show it: each joint turned on top of its pose, knuckle first (the ones after follow).
        for(int k = 0; k < n; k++)
        {
            if(MathF.Abs(state.ShownBend[k]) < 1e-5f && MathF.Abs(state.ShownSpread[k]) < 1e-5f)
                continue;
            var current = skeleton.GetModelFrame(bones[k]).Rotation;
            skeleton.SetModelRotation(bones[k], Quaternion.Normalize(current * Local(state.ShownBend[k], state.ShownSpread[k])));
        }
    }

    private static Quaternion Local(float bend, float spread)
        => Quaternion.CreateFromAxisAngle(Vector3.UnitY, spread) * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -bend);

    private static float Wrap(float a)
    {
        while(a > MathF.PI)
            a -= MathF.Tau;
        while(a < -MathF.PI)
            a += MathF.Tau;
        return a;
    }

    // The skin near a point: a smooth patch of the nearest few points (weighted, so it doesn't snap from point to
    // point) and its normal.
    private readonly List<(float D, int I)> _found = new();
    private bool Near(Vector3 p, float scale, out Vector3 skin, out Vector3 normal)
    {
        skin = normal = Vector3.Zero;
        float limit = Reach * scale;
        _found.Clear();
        var (x, y, z) = Key(p);
        for(int dx = -1; dx <= 1; dx++)
            for(int dy = -1; dy <= 1; dy++)
                for(int dz = -1; dz <= 1; dz++)
                    if(_grid.TryGetValue((x + dx, y + dy, z + dz), out var list))
                        foreach(int i in list)
                        {
                            float d = Vector3.DistanceSquared(p, _points[i]);
                            if(d >= limit * limit)
                                continue;
                            // keep the nearest few, closest first
                            if(_found.Count < Nearest)
                            {
                                int at = _found.Count;
                                _found.Add((d, i));
                                while(at > 0 && _found[at - 1].D > d)
                                {
                                    _found[at] = _found[at - 1];
                                    at--;
                                }
                                _found[at] = (d, i);
                            }
                            else if(d < _found[Nearest - 1].D)
                            {
                                int at = Nearest - 1;
                                while(at > 0 && _found[at - 1].D > d)
                                {
                                    _found[at] = _found[at - 1];
                                    at--;
                                }
                                _found[at] = (d, i);
                            }
                        }
        if(_found.Count == 0)
            return false;
        float first = MathF.Max(MathF.Sqrt(_found[0].D), 0.004f * scale);
        float total = 0f;
        foreach(var (d2, i) in _found)
        {
            float d = MathF.Sqrt(d2) / first;
            float w = MathF.Exp(-d * d);
            skin += _points[i] * w;
            normal += _normals[i] * w;
            total += w;
        }
        if(total <= 1e-12f || normal.LengthSquared() < 1e-12f)
            return false;
        skin /= total;
        normal = Vector3.Normalize(normal);
        return true;
    }

    private static (int, int, int) Key(Vector3 p)
        => ((int)MathF.Floor(p.X / Cell), (int)MathF.Floor(p.Y / Cell), (int)MathF.Floor(p.Z / Cell));

    /// <summary>That hand of that character is grabbing (any thread; reads under the rig lock).</summary>
    public bool IsGrabbing(nint address, int side)
        => Hands.TryGetValue(address, out var hand) && (side == 0 ? hand.Left : hand.Right);

    /// <summary>Characters gone: forget their state.</summary>
    public void Forget(nint address)
    {
        Hands.Remove(address);
        var stale = new List<(nint, int)>();
        foreach(var key in _states.Keys)
            if(key.Item1 == address)
                stale.Add(key);
        foreach(var key in stale)
            _states.Remove(key);
    }
}
