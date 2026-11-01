using System.Linq;
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
    /// <summary>The bone's turn at this point (degrees, character's axes: around, forward / back, sideways), on top
    /// of how it's held.</summary>
    public float Yaw { get; set; }
    public float Pitch { get; set; }
    public float Roll { get; set; }
    /// <summary>How sharp the path's corner is here: 0 = round, 1 = a straight-line corner.</summary>
    public float Sharpness { get; set; }

    public MotionPoint() { }
    public MotionPoint(Vector3 v) => (X, Y, Z) = (v.X, v.Y, v.Z);

    public MotionPoint With(Vector3 v) => new() { X = v.X, Y = v.Y, Z = v.Z, Yaw = Yaw, Pitch = Pitch, Roll = Roll, Sharpness = Sharpness };

    public MotionPoint Copy() => With(Value);

    [Newtonsoft.Json.JsonIgnore]
    public Vector3 Value => new(X, Y, Z);

    [Newtonsoft.Json.JsonIgnore]
    public bool Turns => Yaw != 0f || Pitch != 0f || Roll != 0f;

    [Newtonsoft.Json.JsonIgnore]
    public Quaternion Turn => Quaternion.CreateFromYawPitchRoll(Yaw * MathF.PI / 180f, Pitch * MathF.PI / 180f, Roll * MathF.PI / 180f);
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
    /// <summary>How sharp the path's corner is at the first point (where the bone is held).</summary>
    public float StartSharpness { get; set; }

    // Runtime (not saved)
    [Newtonsoft.Json.JsonIgnore] public double Phase;
    [Newtonsoft.Json.JsonIgnore] public double Waiting;
    [Newtonsoft.Json.JsonIgnore] public volatile Vector3[]? DrawPath;
    [Newtonsoft.Json.JsonIgnore] public Vector3 DrawNow, DrawCenter;
    [Newtonsoft.Json.JsonIgnore] public bool DrawClosed;
    // The cycle's points on screen (world), and how to turn a move on screen back into the character's axes.
    [Newtonsoft.Json.JsonIgnore] public volatile Vector3[]? DrawNodes;
    [Newtonsoft.Json.JsonIgnore] public Quaternion DrawRootRotation = Quaternion.Identity;
    [Newtonsoft.Json.JsonIgnore] public float DrawRootScale = 1f;

    [Newtonsoft.Json.JsonIgnore]
    public bool CycleTurns => Kind == MotionKind.Cycle && Points.Exists(p => p.Turns);

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

    /// <summary>
    /// Turns off the motion devices holding bones on this character (one bone, or all of them with null), so
    /// "Let go" really lets go: otherwise the device takes hold of the bone again the next frame. Returns how many.
    /// </summary>
    /// <summary>Other things holding bones that "Let go" should stop too (Oral).</summary>
    public Func<nint, string?, int>? AlsoStop;

    public int StopFor(nint actor, string? bone)
    {
        int stopped = AlsoStop?.Invoke(actor, bone) ?? 0;
        foreach (var ((address, b), m) in _active)
        {
            if (address != actor || (bone != null && b != bone) || !m.Enabled)
                continue;
            m.Enabled = false;
            stopped++;
        }
        if (stopped > 0)
        {
            var active = new Dictionary<(nint, string), MotionProp>(_active);
            foreach (var key in active.Keys.ToList())
                if (key.Item1 == actor && (bone == null || key.Item2 == bone))
                    active.Remove(key);
            _active = active;
            Changed?.Invoke();
        }
        return stopped;
    }

    /// <summary>Turns off every motion device (Let go of everything).</summary>
    public void StopAll()
    {
        AlsoStop?.Invoke(0, null);
        bool any = false;
        foreach (var m in _active.Values)
        {
            any |= m.Enabled;
            m.Enabled = false;
        }
        _active = new();
        if (any)
            Changed?.Invoke();
    }

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
                if (((m.Kind == MotionKind.Wheel && m.TurnBone) || m.CycleTurns) && !goal.LockRotation)
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
                var sharp = new float[points.Length + 1];
                var turns = new Quaternion[points.Length + 1];
                nodes[0] = target.Position;
                sharp[0] = m.StartSharpness;
                turns[0] = Quaternion.Identity;
                for (int i = 0; i < points.Length; i++)
                {
                    nodes[i + 1] = target.Position + points[i].Value;
                    sharp[i + 1] = points[i].Sharpness;
                    turns[i + 1] = points[i].Turn;
                }
                var path0 = new Path(nodes, sharp, m.Curved, !m.BackAndForth);
                float u = Along(nodes.Length, m.BackAndForth, m.Pattern, m.Phase);
                position = path0.At(u);
                if (m.CycleTurns)
                    rotation = Quaternion.Normalize(path0.Turn(turns, u) * target.Rotation);
                if (m.Visible)
                {
                    int segments = m.BackAndForth ? nodes.Length - 1 : nodes.Length;
                    const int steps = 16;
                    path = new Vector3[segments * steps + 1];
                    for (int i = 0; i <= segments * steps; i++)
                        path[i] = path0.At((float)i / steps);
                    closed = !m.BackAndForth;
                    var world = new Vector3[nodes.Length];
                    for (int i = 0; i < nodes.Length; i++)
                        world[i] = skeleton.ModelToWorldPoint(nodes[i]);
                    m.DrawRootRotation = skeleton.RootRotation;
                    m.DrawRootScale = MathF.Max(1e-4f, skeleton.RootScale.Y);
                    m.DrawNodes = world;
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

    // Along the cycle's points: each leg takes the same time; the pattern sets the pace within a leg. Returns where
    // on the path (leg index + fraction).
    private static float Along(int count, bool backAndForth, MotionPattern pattern, double phase)
    {
        float p = (float)(phase - Math.Floor(phase));
        int legs = count - 1;
        float u;
        if (backAndForth)
        {
            float there = p < 0.5f ? p * 2f : 2f - p * 2f; // 0 -> 1 -> 0
            u = there * legs;
        }
        else
        {
            legs = count;
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
        return leg + t;
    }

    /// <summary>
    /// The path through a cycle's points: a curve that passes through every point (or straight lines), each point's
    /// corner as round or sharp as it says; closed paths come back round to the first point.
    /// </summary>
    private sealed class Path
    {
        private readonly Vector3[] nodes;
        private readonly float[] sharpness;
        private readonly bool curved, closed;

        public Path(Vector3[] nodes, float[] sharpness, bool curved, bool closed)
        {
            this.nodes = nodes;
            this.sharpness = sharpness;
            this.curved = curved;
            this.closed = closed;
        }

        private int Legs => closed ? nodes.Length : nodes.Length - 1;

        private int Wrap(int k) => closed ? ((k % nodes.Length) + nodes.Length) % nodes.Length : Math.Clamp(k, 0, nodes.Length - 1);

        // The curve's direction at a point: from the one before to the one after, less of it the sharper the point.
        private Vector3 Tangent(int k)
        {
            float keep = curved ? 1f - Math.Clamp(sharpness[Wrap(k)], 0f, 1f) : 0f;
            return (nodes[Wrap(k + 1)] - nodes[Wrap(k - 1)]) * 0.5f * keep;
        }

        private (int Leg, float T) Split(float u)
        {
            int legs = Legs;
            if (legs <= 0)
                return (0, 0f);
            u = Math.Clamp(u, 0f, legs);
            int i = Math.Min((int)MathF.Floor(u), legs - 1);
            return (i, u - i);
        }

        public Vector3 At(float u)
        {
            if (Legs <= 0)
                return nodes[0];
            var (i, t) = Split(u);
            var p1 = nodes[Wrap(i)];
            var p2 = nodes[Wrap(i + 1)];
            if (!curved)
                return Vector3.Lerp(p1, p2, t);
            var m1 = Tangent(i);
            var m2 = Tangent(i + 1);
            // Hermite: passes through every point, with each point's own roundness.
            float t2 = t * t, t3 = t2 * t;
            return (2f * t3 - 3f * t2 + 1f) * p1 + (t3 - 2f * t2 + t) * m1 + (-2f * t3 + 3f * t2) * p2 + (t3 - t2) * m2;
        }

        /// <summary>The bone's turn at <paramref name="u"/>: from one point's turn to the next, smoothly.</summary>
        public Quaternion Turn(Quaternion[] turns, float u)
        {
            if (Legs <= 0)
                return turns[0];
            var (i, t) = Split(u);
            var a = turns[Wrap(i)];
            var b = turns[Wrap(i + 1)];
            if (Quaternion.Dot(a, b) < 0f)
                b = -b;
            return Quaternion.Normalize(Quaternion.Slerp(a, b, Ease(t)));
        }
    }

    // ---- Drawing (UI thread)

    /// <summary>The cycle whose points can be dragged on screen (its editor is open this frame).</summary>
    public Guid? Editing;
    public int EditingFrame;
    /// <summary>A point was dragged (save the settings).</summary>
    public Action? Changed;
    /// <summary>A point is under the mouse or being dragged (last frame): the posing dots leave the mouse alone.</summary>
    public bool OwnsMouse { get; private set; }

    private bool _wantInput;
    private (Guid Id, int Node)? _drag;

    public void Draw()
    {
        DrawPaths();
        DrawPointEditor();
    }

    private void DrawPaths()
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

    // The editing cycle's points as dots on screen (numbered as in its list); drag one to move it.
    private void DrawPointEditor()
    {
        MotionProp? m = null;
        if (Editing is { } id && ImGui.GetFrameCount() - EditingFrame <= 1)
        {
            List<MotionProp> list;
            try { list = new List<MotionProp>(_source()); }
            catch { list = new(); }
            m = list.Find(x => x.Id == id && x.Kind == MotionKind.Cycle && x.Enabled && x.Visible);
        }
        var nodes = m?.DrawNodes;
        if (m == null || nodes == null || nodes.Length < 2)
        {
            if (_drag != null)
                Changed?.Invoke();
            _drag = null;
            _wantInput = false;
            OwnsMouse = false;
            return;
        }

        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.Pos);
        ImGui.SetNextWindowSize(viewport.Size);
        var flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoBringToFrontOnFocus
            | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoDocking;
        if (!_wantInput)
            flags |= ImGuiWindowFlags.NoInputs;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        int hovered = -1;
        try
        {
            if (ImGui.Begin("##limber_motion_points", flags))
            {
                var drawList = ImGui.GetWindowDrawList();
                var mouse = ImGui.GetMousePos();
                bool otherWindow = ImGui.IsWindowHovered(ImGuiHoveredFlags.AnyWindow) && !ImGui.IsWindowHovered();
                const float radius = 7f;
                float best = float.MaxValue;
                for (int i = 0; i < nodes.Length; i++)
                {
                    if (!_gameGui.WorldToScreen(nodes[i], out var at))
                        continue;
                    bool dragging = _drag is { } d && d.Id == m.Id && d.Node == i;
                    // Point 1 is where the bone is held: shown, not dragged (move the bone itself for that).
                    uint fill = i == 0 ? 0xFF808080u : dragging ? 0xFF40FFFFu : 0xFF60FF60u;
                    drawList.AddCircleFilled(at, radius, fill);
                    drawList.AddCircle(at, radius, 0xFF000000u, 0, 1.5f);
                    drawList.AddText(at + new Vector2(radius + 2f, -radius), 0xFFFFFFFFu, (i + 1).ToString());
                    float distance = Vector2.Distance(mouse, at);
                    if (i > 0 && !otherWindow && distance <= radius * 1.6f && distance < best)
                    {
                        best = distance;
                        hovered = i;
                    }
                }

                if (_drag == null && hovered > 0 && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                    _drag = (m.Id, hovered);
                if (_drag is { } drag)
                {
                    if (!ImGui.IsMouseDown(ImGuiMouseButton.Left) || drag.Id != m.Id || drag.Node >= nodes.Length || drag.Node - 1 >= m.Points.Count)
                    {
                        _drag = null;
                        Changed?.Invoke();
                    }
                    else if (_gameGui.WorldToScreen(nodes[drag.Node], out var now))
                    {
                        var world = ScreenMove(nodes[drag.Node], mouse - now);
                        if (world is { } w)
                        {
                            var model = Vector3.Transform(w, Quaternion.Inverse(m.DrawRootRotation)) / m.DrawRootScale;
                            var points = new List<MotionPoint>(m.Points);
                            var p = points[drag.Node - 1];
                            points[drag.Node - 1] = p.With(p.Value + model);
                            m.Points = points;
                        }
                    }
                }
            }
        }
        finally
        {
            ImGui.End();
            ImGui.PopStyleVar(2);
        }
        _wantInput = hovered > 0 || _drag != null;
        OwnsMouse = _wantInput;
        if (_wantInput)
            ImGui.SetNextFrameWantCaptureMouse(true);
    }

    // The smallest move in the world (across the view, at that depth) that moves a point this far on screen.
    private Vector3? ScreenMove(Vector3 point, Vector2 screenDelta)
    {
        if (screenDelta.LengthSquared() < 0.25f || !_gameGui.WorldToScreen(point, out var s0))
            return null;
        const float h = 0.01f;
        var columns = new Vector2[3];
        var axes = new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ };
        for (int k = 0; k < 3; k++)
        {
            if (!_gameGui.WorldToScreen(point + axes[k] * h, out var sk))
                return null;
            columns[k] = (sk - s0) / h;
        }
        // J (2x3) -> move = J^T (J J^T)^-1 delta
        float a = 0f, b = 0f, d = 0f;
        foreach (var c in columns)
        {
            a += c.X * c.X;
            b += c.X * c.Y;
            d += c.Y * c.Y;
        }
        float det = a * d - b * b;
        if (MathF.Abs(det) < 1e-6f)
            return null;
        var y = new Vector2((d * screenDelta.X - b * screenDelta.Y) / det, (-b * screenDelta.X + a * screenDelta.Y) / det);
        var move = new Vector3(columns[0].X * y.X + columns[0].Y * y.Y, columns[1].X * y.X + columns[1].Y * y.Y, columns[2].X * y.X + columns[2].Y * y.Y);
        if (!MathUtil.IsFinite(move))
            return null;
        float length = move.Length();
        return length > 0.25f ? move * (0.25f / length) : move;
    }
}
