using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using Limber.Core;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Limber.Rig;

public enum MotionKind
{
    Piston, // forward and back along a line
    Wheel,  // round a centre: a full spin, or back and forth over an angle
    Cycle,  // through a list of points
}

/// <summary>How the speed varies over one stroke / turn / lap.</summary>
public enum MotionPattern
{
    Smooth,  // eases in and out
    Steady,  // the same speed all the way
    Thrust,  // quick out, slow back
    Pull,    // slow out, quick back
    Bounce,  // sharp turn at the start, rounded at the far end
    Pause,   // holds a moment at each end / point
    Random,  // smooth, with each stroke a different length / pace
}

public sealed class MotionPoint
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }

    public MotionPoint() { }
    public MotionPoint(Vector3 v) => (X, Y, Z) = (v.X, v.Y, v.Z);

    [Newtonsoft.Json.JsonIgnore]
    public Vector3 Value => new(X, Y, Z);
}

/// <summary>
/// A motion prop (Koikatsu's piston and wheel, VaM's cycle): moves a bone of a character on a loop, around where
/// the bone is held. The bone is held by a Limber goal, so arms and legs bend to follow it. Offsets and axes are
/// in the character's own frame (x: their left, y: up, z: forward), in metres and degrees.
/// </summary>
public sealed class MotionProp
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Motion";
    public MotionKind Kind { get; set; }
    public bool Enabled { get; set; } = true;
    public bool Visible { get; set; } = true;

    public string Character { get; set; } = "";
    public int IndexHint { get; set; } = -1;
    public string Bone { get; set; } = "";

    /// <summary>Strokes / turns / laps per second.</summary>
    public float Speed { get; set; } = 0.5f;
    public MotionPattern Pattern { get; set; }
    /// <summary>Seconds to wait (at the start) after each stroke / turn / lap before the next.</summary>
    public float Delay { get; set; }

    // Direction of the piston's line, and the wheel's axle (degrees, in the character's frame).
    public float Yaw { get; set; }
    public float Pitch { get; set; }

    // Piston
    public float Distance { get; set; } = 0.08f;

    // Wheel
    public float CenterX { get; set; }
    public float CenterY { get; set; } = -0.05f;
    public float CenterZ { get; set; }
    public bool FullSpin { get; set; } = true;
    public float Angle { get; set; } = 90f;
    public bool Reverse { get; set; }
    public bool TurnBone { get; set; }

    // Cycle (offsets from where the bone is held; that spot is the first point)
    public List<MotionPoint> Points { get; set; } = new();
    public bool Curved { get; set; } = true;
    public bool BackAndForth { get; set; }

    // Runtime (not saved)
    [Newtonsoft.Json.JsonIgnore] public double Phase;
    [Newtonsoft.Json.JsonIgnore] public double Waiting;
    [Newtonsoft.Json.JsonIgnore] public volatile Vector3[]? DrawPath;
    [Newtonsoft.Json.JsonIgnore] public Vector3 DrawNow, DrawCenter;
    [Newtonsoft.Json.JsonIgnore] public bool DrawClosed;

    [Newtonsoft.Json.JsonIgnore]
    public Vector3 Center
    {
        get => new(CenterX, CenterY, CenterZ);
        set => (CenterX, CenterY, CenterZ) = (value.X, value.Y, value.Z);
    }

    public Vector3 Axis()
        => Vector3.Transform(Vector3.UnitZ, Quaternion.CreateFromYawPitchRoll(Yaw * MathF.PI / 180f, Pitch * MathF.PI / 180f, 0f));
}

/// <summary>Runs the motion props: finds their characters, keeps their bones held, moves the phase on, and bends
/// the held targets in the bone update.</summary>
public sealed unsafe class MotionService
{
    private readonly RigService _rigs;
    private readonly IObjectTable _objects;
    private readonly IClientState _clientState;
    private readonly IGameGui _gameGui;
    private readonly Func<List<MotionProp>> _source;

    // (character, bone) -> motion, rebuilt every framework update; read in the bone update.
    private volatile Dictionary<(nint, string), MotionProp> _active = new();

    public bool Playing = true;

    public MotionService(RigService rigs, IObjectTable objects, IClientState clientState, IGameGui gameGui, Func<List<MotionProp>> source)
    {
        _rigs = rigs;
        _objects = objects;
        _clientState = clientState;
        _gameGui = gameGui;
        _source = source;
    }

    public MotionProp? For(nint actor, string bone)
        => _active.TryGetValue((actor, bone), out var m) ? m : null;

    // ---- Framework thread

    public void Update(double seconds)
    {
        var active = new Dictionary<(nint, string), MotionProp>();
        bool gposing = _clientState.IsGPosing;
        List<MotionProp> list;
        try { list = new List<MotionProp>(_source()); }
        catch { return; }

        foreach (var m in list)
        {
            if (!m.Enabled || string.IsNullOrEmpty(m.Bone))
                continue;
            if (Playing)
                Advance(m, seconds);
            if (!Find(m, gposing, out var address, out var index, out var name))
                continue;
            if (active.ContainsKey((address, m.Bone)))
                continue; // one motion per bone

            // The bone must be held (where it is now) for the motion to move it.
            lock(_rigs.Sync)
            {
                var rig = _rigs.GetOrCreateRig(address, index, name);
                var goal = rig.Find(m.Bone);
                if (goal == null)
                {
                    goal = rig.GetOrAdd(m.Bone);
                    goal.Enabled = true;
                }
                goal.KeepPosition();
                if (m.Kind == MotionKind.Wheel && m.TurnBone && !goal.LockRotation)
                {
                    goal.LockRotation = true;
                    goal.CaptureRotation = true;
                }
            }
            active[(address, m.Bone)] = m;
        }
        _active = active;
    }

    // Move the phase on; with a delay, each finished stroke / turn / lap holds at the start for that long.
    private static void Advance(MotionProp m, double seconds)
    {
        if (m.Waiting > 0)
        {
            m.Waiting -= seconds;
            if (m.Waiting > 0)
                return;
            seconds = -m.Waiting; // what's left of this frame after the wait
            m.Waiting = 0;
        }
        double before = m.Phase;
        double after = before + seconds * Math.Clamp(m.Speed, 0f, 20f);
        if (m.Delay > 0f && Math.Floor(after) > Math.Floor(before))
        {
            m.Phase = Math.Floor(after); // finished one: wait at the start
            m.Waiting = m.Delay;
            return;
        }
        m.Phase = after;
    }

    private bool Find(MotionProp m, bool gposing, out nint address, out ushort index, out string name)
    {
        address = 0;
        index = 0;
        name = "";
        int best = -1;
        foreach (var obj in _objects)
        {
            if (obj is not ICharacter || obj.Address == 0)
                continue;
            if (((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)obj.Address)->DrawObject == null)
                continue;
            if (!string.Equals(obj.Name.TextValue, m.Character, StringComparison.Ordinal))
                continue;
            int score = ((obj.ObjectIndex >= 200) == gposing ? 2 : 0) + (obj.ObjectIndex == m.IndexHint ? 1 : 0);
            if (score > best)
            {
                best = score;
                address = obj.Address;
                index = obj.ObjectIndex;
                name = obj.Name.TextValue;
            }
        }
        return best >= 0;
    }

    // ---- Bone update: bend a held target (model space)

    public static Frame Apply(MotionProp m, Frame target, in ActorSkeleton skeleton)
    {
        var position = target.Position;
        var rotation = target.Rotation;
        Vector3[]? path = null;
        bool closed = false;
        var center = Vector3.Zero;

        switch (m.Kind)
        {
            case MotionKind.Piston:
            {
                var axis = m.Axis();
                float s = Stroke(m.Pattern, m.Phase);
                position = target.Position + axis * m.Distance * s;
                if (m.Visible)
                    path = [target.Position, target.Position + axis * m.Distance];
                break;
            }

            case MotionKind.Wheel:
            {
                var axis = m.Axis();
                center = target.Position + m.Center;
                float angle = m.FullSpin
                    ? (float)(Spin(m.Pattern, m.Phase) * Math.PI * 2.0)
                    : Stroke(m.Pattern, m.Phase) * m.Angle * MathF.PI / 180f;
                if (m.Reverse)
                    angle = -angle;
                var turn = Quaternion.CreateFromAxisAngle(axis, angle);
                position = center + Vector3.Transform(target.Position - center, turn);
                if (m.TurnBone)
                    rotation = Quaternion.Normalize(turn * target.Rotation);
                if (m.Visible)
                {
                    float sweep = m.FullSpin ? MathF.PI * 2f : m.Angle * MathF.PI / 180f * (m.Reverse ? -1f : 1f);
                    int n = m.FullSpin ? 48 : 24;
                    path = new Vector3[n + 1];
                    for (int i = 0; i <= n; i++)
                        path[i] = center + Vector3.Transform(target.Position - center, Quaternion.CreateFromAxisAngle(axis, sweep * i / n));
                    closed = m.FullSpin;
                }
                break;
            }

            case MotionKind.Cycle:
            {
                var points = m.Points.ToArray();
                if (points.Length == 0)
                    break;
                var nodes = new Vector3[points.Length + 1];
                nodes[0] = target.Position;
                for (int i = 0; i < points.Length; i++)
                    nodes[i + 1] = target.Position + points[i].Value;
                position = Along(nodes, m.Curved, m.BackAndForth, m.Pattern, m.Phase);
                if (m.Visible)
                {
                    int segments = m.BackAndForth ? nodes.Length - 1 : nodes.Length;
                    const int steps = 12;
                    path = new Vector3[segments * steps + 1];
                    for (int i = 0; i <= segments * steps; i++)
                        path[i] = Point(nodes, m.Curved, !m.BackAndForth, (float)i / steps);
                    closed = !m.BackAndForth;
                }
                break;
            }
        }

        if (!MathUtil.IsFinite(position) || !MathUtil.IsFinite(rotation))
            return target;

        if (m.Visible)
        {
            if (path != null)
                for (int i = 0; i < path.Length; i++)
                    path[i] = skeleton.ModelToWorldPoint(path[i]);
            m.DrawNow = skeleton.ModelToWorldPoint(position);
            m.DrawCenter = skeleton.ModelToWorldPoint(center);
            m.DrawClosed = closed;
            m.DrawPath = path;
        }
        else
        {
            m.DrawPath = null;
        }
        return new Frame(position, rotation);
    }

    private static float Ease(float x) => x * x * (3f - 2f * x);

    private static float Hash(long n)
    {
        unchecked
        {
            ulong h = (ulong)n * 0x9E3779B97F4A7C15UL;
            h ^= h >> 31;
            h *= 0xBF58476D1CE4E5B9UL;
            h ^= h >> 29;
            return (h & 0xFFFFFF) / (float)0xFFFFFF;
        }
    }

    /// <summary>Out and back over one stroke: 0 at rest, 1 at the far end.</summary>
    public static float Stroke(MotionPattern pattern, double phase)
    {
        float p = (float)(phase - Math.Floor(phase));
        switch (pattern)
        {
            case MotionPattern.Steady:
                return 1f - MathF.Abs(2f * p - 1f);
            case MotionPattern.Thrust:
                return p < 0.3f ? Ease(p / 0.3f) : 1f - Ease((p - 0.3f) / 0.7f);
            case MotionPattern.Pull:
                return p < 0.7f ? Ease(p / 0.7f) : 1f - Ease((p - 0.7f) / 0.3f);
            case MotionPattern.Bounce:
            {
                float x = 2f * p - 1f;
                return 1f - x * x;
            }
            case MotionPattern.Pause:
                return p < 0.35f ? Ease(p / 0.35f) : p < 0.5f ? 1f : p < 0.85f ? 1f - Ease((p - 0.5f) / 0.35f) : 0f;
            case MotionPattern.Random:
                return (0.5f - 0.5f * MathF.Cos(2f * MathF.PI * p)) * (0.5f + 0.5f * Hash((long)Math.Floor(phase)));
            case MotionPattern.Smooth:
            default:
                return 0.5f - 0.5f * MathF.Cos(2f * MathF.PI * p);
        }
    }

    /// <summary>Turns so far, for a full spin (whole turns plus how far into this one).</summary>
    public static double Spin(MotionPattern pattern, double phase)
    {
        double n = Math.Floor(phase);
        float p = (float)(phase - n);
        float w = 2f * MathF.PI * p;
        float into = pattern switch
        {
            MotionPattern.Thrust => p - 0.9f * MathF.Sin(w) / (2f * MathF.PI),
            MotionPattern.Pull => p + 0.9f * MathF.Sin(w) / (2f * MathF.PI),
            MotionPattern.Pause => p - MathF.Sin(w) / (2f * MathF.PI),
            MotionPattern.Bounce => p + 0.6f * MathF.Sin(2f * w) / (4f * MathF.PI),
            MotionPattern.Random => p - 0.9f * Hash((long)n) * MathF.Sin(w) / (2f * MathF.PI),
            _ => p,
        };
        return n + into;
    }

    // Along the cycle's points: each leg takes the same time; the pattern sets the pace within a leg.
    private static Vector3 Along(Vector3[] nodes, bool curved, bool backAndForth, MotionPattern pattern, double phase)
    {
        float p = (float)(phase - Math.Floor(phase));
        int legs = nodes.Length - 1;
        float u;
        if (backAndForth)
        {
            float there = p < 0.5f ? p * 2f : 2f - p * 2f; // 0 -> 1 -> 0
            u = there * legs;
        }
        else
        {
            legs = nodes.Length;
            u = p * legs;
        }
        int leg = Math.Min((int)MathF.Floor(u), Math.Max(legs - 1, 0));
        float t = Math.Clamp(u - leg, 0f, 1f);
        t = pattern switch
        {
            MotionPattern.Steady => t,
            MotionPattern.Pause => t < 0.7f ? Ease(t / 0.7f) : 1f,
            MotionPattern.Thrust => 1f - (1f - t) * (1f - t),
            MotionPattern.Pull => t * t,
            _ => Ease(t),
        };
        return Point(nodes, curved, !backAndForth, leg + t);
    }

    // A point on the path through the nodes at u (leg index + fraction); closed paths come back to the first node.
    private static Vector3 Point(Vector3[] nodes, bool curved, bool closed, float u)
    {
        int count = nodes.Length;
        int legs = closed ? count : count - 1;
        if (legs <= 0)
            return nodes[0];
        u = Math.Clamp(u, 0f, legs);
        int i = Math.Min((int)MathF.Floor(u), legs - 1);
        float t = u - i;
        Vector3 Node(int k) => closed ? nodes[((k % count) + count) % count] : nodes[Math.Clamp(k, 0, count - 1)];
        var p1 = Node(i);
        var p2 = Node(i + 1);
        if (!curved)
            return Vector3.Lerp(p1, p2, t);
        var p0 = Node(i - 1);
        var p3 = Node(i + 2);
        // Catmull-Rom: passes through every point.
        float t2 = t * t, t3 = t2 * t;
        return 0.5f * (2f * p1 + (p2 - p0) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (3f * p1 - p0 - 3f * p2 + p3) * t3);
    }

    // ---- Drawing (UI thread)

    public void Draw()
    {
        List<MotionProp> list;
        try { list = new List<MotionProp>(_source()); }
        catch { return; }
        var drawList = ImGui.GetBackgroundDrawList();
        foreach (var m in list)
        {
            if (!m.Enabled || !m.Visible)
                continue;
            var path = m.DrawPath;
            if (path == null || path.Length < 2)
                continue;
            uint color = m.Kind switch
            {
                MotionKind.Piston => 0xFF40C0FFu, // orange
                MotionKind.Wheel => 0xFFFFC040u,  // light blue
                _ => 0xFF60FF60u,                 // green
            };
            for (int i = 1; i < path.Length; i++)
            {
                if (_gameGui.WorldToScreen(path[i - 1], out var a) && _gameGui.WorldToScreen(path[i], out var b))
                    drawList.AddLine(a, b, color, 2f);
            }
            if (m.Kind == MotionKind.Wheel && _gameGui.WorldToScreen(m.DrawCenter, out var c))
                drawList.AddCircleFilled(c, 3f, color);
            if (_gameGui.WorldToScreen(m.DrawNow, out var now))
                drawList.AddCircle(now, 6f, color, 0, 2f);
        }
    }
}
