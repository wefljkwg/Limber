using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Numerics;
using Limber.Core;

namespace Limber.BodyRig;

/// <summary>
/// Soft breasts / soft testicles: moves a rig part's lattice bones (a 3 x 3 x 2 box per side). The whole part swings
/// as a weighted spring about its own bone (keeping its shape and volume), and each lattice point ripples around that
/// on springs to its neighbours, pushed out of capsules around the arms, hands and thighs, the floor, other
/// characters' hands, and props.
///
/// Runs in the bone update, after all posing (game animation, Koikatsu link, Limber's held bones), so it reacts to
/// the final motion. Characters without the rig bones are untouched.
/// </summary>
public sealed unsafe class SoftBody
{
    private readonly RigPart _part;
    private readonly int Side; // grid points per side
    // Substep length. The integration (symplectic Euler) is stable while h * omega < 2; the stiffest mode here
    // (anchorK 420 plus linkK 900 over up to 8 neighbours, at the lightest weight) has omega of at most about 125 rad/s,
    // so 1/120 s keeps h * omega near 1. Enough substeps are always taken to stay at or below this step.
    private const float MaxStep = 1f / 120f;
    private const int MaxSteps = 32; // 32 / 120 s covers everything below the 0.25 s reset threshold

    // Grid neighbours (structural + diagonal), computed once.
    private readonly (int A, int B)[] Links;
    private readonly int _maxNeighbours; // most links on any one point (for the stability cap)

    private sealed class Breast
    {
        public readonly int[] Bones;
        public readonly int[] Parents;
        public readonly Vector3[] RestLocal;
        public Vector3 Origin; // the part's own bone (world)
        public readonly Vector3[] Settled; // slow average of each point's offset (sag, held shape)
        public readonly Vector3[] Display; // what's written: the wobble around Settled, amplified
        public readonly Vector3[] Position; // world
        public readonly Vector3[] Velocity;
        public readonly Vector3[] Target;   // world, where the body puts each point this frame
        public readonly Vector3[] PrevTarget; // world, Target of the previous simulated frame
        public readonly Vector3[] Goal;     // world, where the swing puts each point this frame
        public readonly Vector3[] PrevGoal;
        public readonly Vector3[] RestTarget; // Target before the shape sliders / blend shapes
        // The nipple bone (-1 = none): carried by the lattice around it (fixed corner weights), sized by the shapes.
        public int NippleBone = -1, NippleParent = -1;
        public Vector3 NippleRestLocal, NippleRest;
        public (int Index, float W)[] NippleCorners = [];
        public Vector3 Swing, SwingVelocity, SwingSettled; // the whole breast's swing (its centre), world
        public Vector3 PrevTip, PrevPivot;
        public bool Started;
        // For each point and prop nearby: the side of the prop the point was on (out from its centre line).
        // A point found inside goes back out on that side, so a thin prop moving fast never lets it slip
        // through, and a hand pressing in keeps pushing it the way it's going.
        public readonly Dictionary<(Guid Prop, int Point), Vector3> PropSide = new();
        // How far props have turned the whole breast away (about its base), carried from frame to frame.
        public Quaternion PropTurn = Quaternion.Identity;
        // How far it's flattened on the floor (1 = not at all), eased from frame to frame.
        public float Squash = 1f;
        public Vector3 SquashAxis = Vector3.UnitY, SquashRise;

        public Breast(int n)
        {
            Bones = new int[n];
            Parents = new int[n];
            RestLocal = new Vector3[n];
            Settled = new Vector3[n];
            Display = new Vector3[n];
            Position = new Vector3[n];
            Velocity = new Vector3[n];
            Target = new Vector3[n];
            PrevTarget = new Vector3[n];
            Goal = new Vector3[n];
            PrevGoal = new Vector3[n];
            RestTarget = new Vector3[n];
        }
    }

    private sealed class State
    {
        public SkeletonInfo? Info;
        public readonly Breast Left, Right;
        public State(int n)
        {
            Left = new Breast(n);
            Right = new Breast(n);
        }
        public long LastTicks;
        public Vector3 LastRoot;
        public int SeenFrame;
    }

    private readonly struct Capsule
    {
        public readonly Vector3 A, B;
        public readonly float Radius;
        public Capsule(Vector3 a, Vector3 b, float radius) { A = a; B = b; Radius = radius; }
    }

    private readonly Dictionary<nint, State> _states = new();
    private readonly List<Capsule> _capsules = new();
    private readonly List<Capsule> _otherHands = new();
    private readonly SkeletonInfoCache _infoCache = new();
    private int _frame;

    /// <summary>Characters to consider this frame (set from the framework thread).</summary>
    public volatile (nint Address, ushort Index)[] Candidates = Array.Empty<(nint, ushort)>();

    public Func<SoftBodySettings> Settings = () => new SoftBodySettings();

    /// <summary>Props (pill-shaped colliders) placed this frame; always collided with, whatever the Collisions setting.</summary>
    public Func<PropWorld[]> Props = () => [];

    /// <summary>Props that don't push the bones right now (a hand that's grabbing: its fingers would push the breast
    /// away from themselves and chase it).</summary>
    public Func<PropWorld, bool> Skip = _ => false;

    /// <summary>Hands locked onto the breasts right now: whose hand, which (0 left, 1 right), where it is (world), how
    /// strongly, and whether it carries the whole breast (else just the part under it). Set by the plugin.</summary>
    public Func<(nint Owner, int Side, Frame Hand, float Strength, bool Whole)[]> LockedHands = () => [];
    private (nint Owner, int Side, Frame Hand, float Strength, bool Whole)[] _locks = [];
    // Per lock and breast: where each grid point was in the hand's own space when it locked, and how much it follows.
    private readonly Dictionary<(nint Breasts, int Breast, nint Owner, int Side), (Vector3[] Local, float[] Weight, bool Whole)> _held = new();
    private readonly HashSet<(nint, int, nint, int)> _heldSeen = new();
    // Grid points within this of the hand follow it fully, fading out to nothing at LockFar (m).
    private const float LockNear = 0.06f, LockFar = 0.16f;
    // Whole breast: the breast nearest the hand is taken if any of it is within this of the hand (m).
    private const float LockWholeReach = 0.15f;

    /// <summary>How hard props push the bones (1 = all the way out, 0 = not at all).</summary>
    public Func<float> BoneStrength = () => 1f;
    private float _boneStrength = 1f;
    private PropWorld[] _props = [];
    // How far the skin sits beyond a grid point (the bones are just inside the surface): props collide with
    // that, so they meet the skin instead of passing through it to reach the bones.
    private float _skin;

    public RigPart Part => _part;

    // Points on the part's surface (the front half of an ellipsoid filling the lattice box), each a fixed blend of
    // the 8 lattice bones around it, so it follows the bones the same way the mesh does. Props collide with these,
    // not with the bones themselves (the bones are a box around the part, far apart: corners stuck out past the
    // skin and a finger slipped in between them).
    private readonly (int[] Node, float[] W)[] _surface;
    private const float SurfaceSkin = 0.004f;

    /// <summary>Last frame's collision surface points and body capsules (world), for the "Show body colliders" view.</summary>
    public volatile Vector3[] DebugSurface = [];
    public volatile (Vector3 A, Vector3 B, float R)[] DebugCapsules = [];
    private readonly List<Vector3> _debugSurface = new();
    /// <summary>Last frame's resting surface points (world, where the body puts them: no collisions or jiggle), for
    /// grabbing fingers when the live mesh isn't running.</summary>
    public volatile Vector3[] GrabSurface = [];
    public volatile Vector3[] GrabNormals = [];
    private readonly List<Vector3> _grabSurface = new();
    private readonly List<Vector3> _grabNormals = new();

    /// <summary>
    /// Per character, last frame: how far each grid bone (and the nipple bone) was moved off its resting spot by the
    /// physics (jiggle, collisions), in model space, by bone name. Taken back out, what's left is the shape the
    /// sliders give it, steady: what grabbing fingers hold onto.
    /// </summary>
    public volatile Dictionary<nint, Dictionary<string, Vector3>> Shifts = new();
    private Dictionary<nint, Dictionary<string, Vector3>> _shiftsBuilding = new();

    // Diagnostics for the Rigging tab: characters simulated in the last update, and the largest movement shown.
    public volatile int LastCount;
    public volatile float LastMove;
    private int _count;
    private float _move;
    public long LastTicks;

    public SoftBody(RigPart part)
    {
        _part = part;
        Side = part.PerSide;
        Links = BuildLinks(part);
        var count = new int[part.PerSide];
        foreach (var (a, b) in Links)
        {
            count[a]++;
            count[b]++;
        }
        _maxNeighbours = Math.Max(1, count.Length > 0 ? count.Max() : 1);
        Mobility = BuildMobility(part);
        _surface = BuildSurface(part);
    }

    private static (int[] Node, float[] W)[] BuildSurface(RigPart part)
    {
        if (!part.Lattice || !part.BackLayerBones || part.Rows < 2 || part.Columns < 2 || part.Layers < 2)
            return [];
        var list = new List<(int[], float[])>();
        const int Rings = 7, Around = 14;
        const float Fill = 0.97f;
        void Add(float u, float v, float w)
        {
            // Lattice coordinates (u, v across, -0.5..0.5; w from the back face 0 to the front 1) to the cell's corners.
            float cu = (u + 0.5f) * (part.Columns - 1), cv = (0.5f - v) * (part.Rows - 1), cw = w * (part.Layers - 1);
            int iu = Math.Min((int)cu, part.Columns - 2), iv = Math.Min((int)cv, part.Rows - 2), iw = Math.Min((int)cw, part.Layers - 2);
            float fu = cu - iu, fv = cv - iv, fw = cw - iw;
            var nodes = new int[8];
            var weights = new float[8];
            int k = 0;
            for (int a = 0; a < 2; a++)
                for (int b = 0; b < 2; b++)
                    for (int e = 0; e < 2; e++)
                    {
                        nodes[k] = (iw + e) * part.PerLayer + (iv + b) * part.Columns + (iu + a);
                        weights[k] = (a == 1 ? fu : 1f - fu) * (b == 1 ? fv : 1f - fv) * (e == 1 ? fw : 1f - fw);
                        k++;
                    }
            list.Add((nodes, weights));
        }
        for (int ring = 1; ring <= Rings; ring++)
        {
            float th = MathF.PI / 2f * ring / Rings; // from the front to the side
            for (int j = 0; j < Around; j++)
            {
                float ph = MathF.Tau * j / Around;
                Add(0.5f * MathF.Sin(th) * MathF.Cos(ph) * Fill, 0.5f * MathF.Sin(th) * MathF.Sin(ph) * Fill, MathF.Max(0f, MathF.Cos(th) * Fill));
            }
        }
        Add(0f, 0f, Fill);
        return list.ToArray();
    }

    private static Vector3 At(Vector3[] nodes, in (int[] Node, float[] W) point)
    {
        var p = Vector3.Zero;
        for (int k = 0; k < point.Node.Length; k++)
            p += nodes[point.Node[k]] * point.W[k];
        return p;
    }

    /// <summary>
    /// Pushes the surface points out of the props by moving the lattice bones under them (each bone by the
    /// weighted average of the pushes of the points it carries, less at the rim). Out on the side each point was
    /// on before (or came from, if it went right across a thin prop in one frame), so it never flips through.
    /// </summary>
    private void ProjectSurface(Breast breast, Vector3[] nodes, Vector3[]? velocity, bool remember)
    {
        if (_surface.Length == 0 || _props.Length == 0)
            return;
        // All the way out first (on a copy), then the strength's share of that, once: below 1 the breast gives in
        // to the prop that much (repeating a partial push would end up all the way out anyway).
        var start = nodes;
        nodes = (Vector3[])nodes.Clone();
        Span<Vector3> acc = stackalloc Vector3[Side];
        Span<float> wsum = stackalloc float[Side];
        for (int iteration = 0; iteration < 4; iteration++)
        {
            acc.Clear();
            wsum.Clear();
            bool hit = false;
            for (int j = 0; j < _surface.Length; j++)
            {
                var point = At(nodes, _surface[j]);
                foreach (var prop in _props)
                {
                    if (prop.BoneStrength <= 0f)
                        continue;
                    float radius = prop.Radius * prop.BoneStrength + SurfaceSkin;
                    float dist = prop.Gap(point, out var c, out var n);
                    var key = (prop.Id, j);
                    var side = RememberedSide(breast, prop, j);
                    float along = prop.Shape.Along(point);
                    // Right across it since last frame: along its side only (round its ends it's just sliding).
                    bool crossed = side is { } before && dist > 1e-6f && dist >= radius && along > 0f && along < 1f
                        && Vector3.Dot(n, before) < -0.2f && dist < radius * 2f + 0.02f;
                    if (dist >= radius && !crossed)
                    {
                        if (remember)
                        {
                            if (dist < radius * 3f + 0.03f && dist > 1e-6f)
                                breast.PropSide[key] = n;
                            else
                                breast.PropSide.Remove(key);
                        }
                        continue;
                    }
                    var normal = side ?? OwnSide(breast, prop, j, n);
                    var push = prop.IsBox ? normal * prop.Shape.Exit(point, normal, radius) : c + normal * radius - point;
                    if (remember)
                        breast.PropSide[key] = normal;
                    var sp = _surface[j];
                    for (int k = 0; k < sp.Node.Length; k++)
                    {
                        acc[sp.Node[k]] += push * sp.W[k];
                        wsum[sp.Node[k]] += sp.W[k];
                    }
                    hit = true;
                }
            }
            if (!hit)
                break;
            for (int n = 0; n < Side; n++)
            {
                if (wsum[n] <= 1e-6f)
                    continue;
                var move = acc[n] / wsum[n] * MathF.Min(1f, Mobility[n] * 1.5f);
                nodes[n] += move;
                if (velocity != null)
                {
                    float len = move.Length();
                    if (len > 1e-7f)
                    {
                        var dir = move / len;
                        float into = Vector3.Dot(velocity[n], dir);
                        if (into < 0f)
                            velocity[n] -= dir * into;
                    }
                }
            }
        }
        for (int n = 0; n < Side; n++)
            start[n] += (nodes[n] - start[n]) * _boneStrength;
    }

    // address -> (bone, scale before Limber, scale Limber wrote) for the size fallback.
    private readonly Dictionary<(nint, int), (Vector3 Before, Vector3 Written)> _scaled = new();

    private void ScaleBreastBones(in ActorSkeleton skeleton, nint address, SoftBodySettings s)
    {
        float size = Math.Clamp(s.Size * BodyShapes.ScaleOf(_part, s.Shapes ?? new()), 0.1f, 5f);
        foreach (var name in new[] { "j_mune_l", "j_mune_r" })
        {
            int bone = skeleton.Info.IndexOf(name);
            if (bone < 0)
                continue;
            var now = skeleton.GetModelScale(bone);
            // If nothing rebuilt the pose since last frame (animation paused), it still holds what was written:
            // start from what it was before, so the size doesn't pile up.
            var before = _scaled.TryGetValue((address, bone), out var last) && now == last.Written ? last.Before : now;
            var written = before * size;
            skeleton.SetModelScale(bone, written);
            _scaled[(address, bone)] = (before, skeleton.GetModelScale(bone));
        }
        if (_scaled.Count > 256)
            _scaled.Clear();
    }

    /// <summary>Bone update (after all posing).</summary>
    public void UpdateFrame()
    {
        var settings = Settings();
        if (!settings.Enabled && settings.ShapeIsDefault)
        {
            _states.Clear();
            DebugSurface = [];
            DebugCapsules = [];
            GrabSurface = [];
            GrabNormals = [];
            Shifts = new();
            return;
        }
        _frame++;
        _count = 0;
        _move = 0f;
        _locks = LockedHands() ?? [];
        _heldSeen.Clear();
        var candidates = Candidates;

        // Everyone's hands and forearms, so one character can press into another.
        _otherHands.Clear();
        var skeletons = new List<(nint Address, ActorSkeleton Skeleton)>(candidates.Length);
        foreach (var (address, index) in candidates)
        {
            if (!ActorSkeleton.TryGet(address, index, _infoCache, out var skeleton))
                continue;
            skeletons.Add((address, skeleton));
        }
        if (settings.Enabled && settings.Collisions)
            foreach (var (address, skeleton) in skeletons)
                AddHands(skeleton, _otherHands, address);

        foreach (var (address, skeleton) in skeletons)
        {
            if (!Limber.Koi.Safety.AdultAllowed(address))
                continue;
            var info = skeleton.Info;
            if (info.IndexOf(_part.BoneName(true, 0)) < 0)
            {
                // No rig on this body (a model without Limber's grid): Size still works, by scaling the game's own
                // breast bones, like the game's bust slider does.
                if (_part == RigParts.Breasts)
                    ScaleBreastBones(skeleton, address, settings);
                continue;
            }

            if (!_states.TryGetValue(address, out var state) || state.Info != info)
            {
                state = new State(Side) { Info = info };
                if (!Map(skeleton, state.Left, true) || !Map(skeleton, state.Right, false))
                    continue;
                _states[address] = state;
            }
            state.SeenFrame = _frame;
            _count++;
            Simulate(skeleton, state, settings, address);
        }

        // Locks let go of: forget what they held.
        if (_held.Count > 0)
        {
            List<(nint, int, nint, int)>? gone = null;
            foreach (var key in _held.Keys)
                if (!_heldSeen.Contains(key))
                    (gone ??= new()).Add(key);
            if (gone != null)
                foreach (var key in gone)
                    _held.Remove(key);
        }
        LastCount = _count;
        LastMove = _move;
        DebugSurface = _debugSurface.ToArray();
        _debugSurface.Clear();
        GrabSurface = _grabSurface.ToArray();
        GrabNormals = _grabNormals.ToArray();
        _grabSurface.Clear();
        _grabNormals.Clear();
        Shifts = _shiftsBuilding;
        _shiftsBuilding = new();
        var capsules = new List<(Vector3, Vector3, float)>(_capsules.Count + _otherHands.Count);
        foreach (var c in _capsules)
            capsules.Add((c.A, c.B, c.Radius));
        foreach (var c in _otherHands)
            capsules.Add((c.A, c.B, c.Radius));
        DebugCapsules = capsules.ToArray();
        LastTicks = Environment.TickCount64;

        // Forget characters that are gone.
        if (_states.Count > 0)
        {
            List<nint>? stale = null;
            foreach (var (address, state) in _states)
                if (_frame - state.SeenFrame > 120)
                    (stale ??= new()).Add(address);
            if (stale != null)
                foreach (var a in stale)
                    _states.Remove(a);
        }
    }

    private bool Map(in ActorSkeleton skeleton, Breast breast, bool left)
    {
        var info = skeleton.Info;
        for (int i = 0; i < Side; i++)
        {
            breast.Bones[i] = info.IndexOf(_part.BoneName(left, i));
            if (breast.Bones[i] < 0)
                return false;
            breast.Parents[i] = info.ParentOf(breast.Bones[i]);
            if (breast.Parents[i] < 0 || !skeleton.TryGetReferenceLocal(breast.Bones[i], out breast.RestLocal[i], out _))
                return false;
        }
        breast.NippleBone = -1;
        if (_part.Nipples)
        {
            int nip = info.IndexOf(_part.NippleName(left));
            int parent = nip >= 0 ? info.ParentOf(nip) : -1;
            if (nip >= 0 && parent == breast.Parents[0] && skeleton.TryGetReferenceLocal(nip, out var local, out _))
            {
                breast.NippleBone = nip;
                breast.NippleParent = parent;
                breast.NippleRestLocal = local;
                breast.NippleCorners = LatticeCorners(breast.RestLocal, local);
            }
        }
        return true;
    }

    /// <summary>
    /// Which lattice nodes carry a point, and how much (the same 4-corner weights the rigger gives vertices), from the
    /// nodes' and the point's rest spots under the same parent bone.
    /// </summary>
    private (int Index, float W)[] LatticeCorners(Vector3[] nodes, Vector3 point)
    {
        int cols = _part.Columns, rows = _part.Rows, perLayer = _part.PerLayer, layers = _part.Layers;
        if (!_part.Lattice || nodes.Length < _part.PerSide || cols < 2 || rows < 2 || layers < 2)
            return [];
        var origin = nodes[0];
        var a = (nodes[cols - 1] - origin) / (cols - 1);
        var b = (nodes[(rows - 1) * cols] - origin) / (rows - 1);
        var c = (nodes[(layers - 1) * perLayer] - origin) / (layers - 1);
        var m = new Matrix4x4(a.X, a.Y, a.Z, 0, b.X, b.Y, b.Z, 0, c.X, c.Y, c.Z, 0, 0, 0, 0, 1);
        if (!Matrix4x4.Invert(m, out var inv))
            return [];
        var q = Vector3.Transform(point - origin, inv);
        float u = Math.Clamp(q.X, 0f, cols - 1), v = Math.Clamp(q.Y, 0f, rows - 1), d = Math.Clamp(q.Z, 0f, layers - 1);
        int c0 = Math.Min((int)MathF.Floor(u), cols - 2), r0 = Math.Min((int)MathF.Floor(v), rows - 2), l0 = Math.Min((int)MathF.Floor(d), layers - 2);
        float[] f = [u - c0, v - r0, d - l0];
        int[] axis = [0, 1, 2];
        Array.Sort(axis, (x, y) => f[y].CompareTo(f[x]));
        float[] w = [1f - f[axis[0]], f[axis[0]] - f[axis[1]], f[axis[1]] - f[axis[2]], f[axis[2]]];
        int[] at = [c0, r0, l0];
        var result = new List<(int, float)>(4);
        for (int k = 0; k < 4; k++)
        {
            if (w[k] > 1e-6f)
                result.Add((at[2] * perLayer + at[1] * cols + at[0], w[k]));
            if (k < 3)
                at[axis[k]]++;
        }
        return result.ToArray();
    }

    private void Simulate(in ActorSkeleton skeleton, State state, SoftBodySettings s, nint self)
    {
        var info = skeleton.Info;
        long now = Stopwatch.GetTimestamp();
        float dt = state.LastTicks == 0 ? 0f : (float)((now - state.LastTicks) / (double)Stopwatch.Frequency);
        state.LastTicks = now;

        // Where the body puts every grid point this frame: its rest spot under the breast bone as posed now.
        foreach (var b in new[] { state.Left, state.Right })
        {
            for (int i = 0; i < Side; i++)
            {
                var parent = skeleton.GetModelFrame(b.Parents[i]);
                var scale = skeleton.GetModelScale(b.Parents[i]);
                var model = parent.Position + Vector3.Transform(scale * b.RestLocal[i], parent.Rotation);
                b.Target[i] = skeleton.ModelToWorldPoint(model);
                b.RestTarget[i] = b.Target[i];
            }
            b.Origin = skeleton.ModelToWorldPoint(skeleton.GetModelFrame(b.Parents[0]).Position);
            if (b.NippleBone >= 0)
            {
                var parent = skeleton.GetModelFrame(b.NippleParent);
                var scale = skeleton.GetModelScale(b.NippleParent);
                b.NippleRest = skeleton.ModelToWorldPoint(parent.Position + Vector3.Transform(scale * b.NippleRestLocal, parent.Rotation));
            }
        }

        // Shape sliders: scale / move each grid as a whole (this becomes the rest shape the physics springs toward).
        float uniform = 1f, nippleScale = 1f;
        if (!s.ShapeIsDefault)
        {
            float separation = Math.Clamp(Vector3.Distance(state.Left.Origin, state.Right.Origin), 0.03f, 1f);
            var middle = (state.Left.Origin + state.Right.Origin) * 0.5f;
            // Blend shapes first (so the sliders below scale the shaped form).
            var shapes = s.Shapes;
            float shapeScale = BodyShapes.ScaleOf(_part, shapes);
            nippleScale = BodyShapes.ScaleOf(_part, shapes, nipple: true);
            ApplyShapes(state.Left, true, shapes, middle);
            ApplyShapes(state.Right, false, shapes, middle);
            // Testicles grow as one sac: both sides about the same point (the middle, at the top and the back, where
            // they hang from), so together they're scaled exactly like one rigid shape and keep their modelled form.
            Vector3? shared = null;
            if (_part.GrowsFromTop && Axes(state.Left, middle, out _, out var upAxis, out var forwardAxis))
            {
                var all = state.Left.Target.Concat(state.Right.Target).ToArray();
                var c = Centre(all);
                float top = all.Max(p => Vector3.Dot(p - c, upAxis));
                float back = all.Min(p => Vector3.Dot(p - c, forwardAxis));
                shared = c + upAxis * top + forwardAxis * back;
            }
            Reshape(state.Left, s, separation, middle, shared, shapeScale);
            Reshape(state.Right, s, separation, middle, shared, shapeScale);
            float overall = Math.Clamp(s.Size * shapeScale, 0.1f, 5f);
            uniform = Math.Clamp(overall * MathF.Cbrt(Math.Clamp(s.Width, 0.1f, 5f) * Math.Clamp(s.Height, 0.1f, 5f)
                * Math.Clamp(s.Projection, 0.1f, 5f)), 0.1f, 5f);
        }

        // Size of this body: the distance between the two grids' centres.
        var cl = Centre(state.Left.Target);
        var cr = Centre(state.Right.Target);
        float size = Math.Clamp(Vector3.Distance(cl, cr), 0.03f, 1f);

        // Teleports, loading screens, long hitches: start again from rest.
        var root = skeleton.RootPosition;
        bool reset = state.LastRoot == default || dt > 0.25f || Vector3.Distance(root, state.LastRoot) > 2f;
        bool advance = dt > 1e-4f; // called twice in one frame: just write the same result again
        state.LastRoot = root;

        // Two layers, so it moves like flesh and keeps its form:
        //  1. Swing: each breast as a whole is a weight on a spring, hanging from its own bone (the pivot, like the
        //     game's own breast bones). It lags, sways and bounces as one piece: the grid turns about the pivot (and
        //     stretches / squashes a little along it), so the shape is kept and nothing folds in on itself.
        //  2. Ripple: every grid point is a small mass sprung to where the swing puts it and to its neighbours, so
        //     the surface also ripples and gives way locally (collisions, props) on top of the swing.
        // Sliders go from -3 to 3 (0..1 is the normal range); beyond it they extrapolate, kept physically sane.
        float firm = Math.Clamp(s.Firmness, -3f, 3f);
        float weight = Math.Clamp(s.Weight, -3f, 3f);
        float bounce = Math.Clamp(s.Bounce, -3f, 3f);
        float cohesion = Math.Clamp(s.Cohesion, -3f, 3f);
        float stretch = Math.Clamp(s.MaxStretch, 0f, 3f);

        // Swing: how fast it springs back (Firmness, slower when heavier), how long it keeps going (Bounce).
        float heaviness = Math.Clamp(Lerp(0.6f, 1.8f, weight), 0.3f, 5f);
        float omega = 2f * MathF.PI * Math.Clamp(Lerp(1.0f, 5.0f, firm), 0.4f, 12f) / MathF.Sqrt(heaviness);
        float zeta = Math.Clamp(Lerp(0.9f, 0.12f, bounce), 0.03f, 2f);
        float gravity = 9.8f * 0.35f * weight; // below 0: it floats upward
        // Capped: past this a big swing folds the surface where it meets the chest, at any setting.
        float maxAngle = Math.Clamp(Lerp(6f, 30f, stretch), 3f, 45f) * MathF.PI / 180f;
        float maxSquash = Math.Clamp(0.15f * stretch, 0.02f, 0.6f);

        // Ripple: how tightly each point follows the swing (Cohesion), its neighbours, its damping and its reach.
        float anchorK = Math.Clamp(Lerp(80f, 700f, cohesion), 20f, 3000f);
        float linkK = 300f;
        float invMass = 1f / heaviness;
        float limit = (1.5f / MaxStep) * (1.5f / MaxStep);
        float stiffest = (anchorK + _maxNeighbours * linkK) * invMass;
        if (stiffest > limit)
        {
            float k = limit / stiffest;
            anchorK *= k;
            linkK *= k;
        }
        float rippleDamping = Math.Clamp(Lerp(18f, 4f, bounce), 0.5f, 80f);
        float maxRipple = Math.Clamp(0.2f * stretch, 0.02f, 0.6f) * size;

        if (s.Collisions)
            BuildColliders(skeleton, size);
        else
            _capsules.Clear();
        // Only props with "Bone collision" on push the bones (the live mesh has its own, "Mesh collision").
        _props = Array.FindAll(Props() ?? Array.Empty<PropWorld>(), p => p.Bones && !Skip(p));
        _boneStrength = Math.Clamp(BoneStrength(), 0f, 1f);
        if (_boneStrength <= 0f)
            _props = Array.Empty<PropWorld>();
        float floor = root.Y + 0.01f;
        float jiggle = Math.Clamp(s.Jiggle, 0f, 6f);
        float keepShape = Math.Clamp(s.KeepShape, 0f, 1f);
        Span<Vector3> shown = stackalloc Vector3[Side]; // reused for both sides (no stackalloc inside the loop)
        Span<Vector3> turned = stackalloc Vector3[Side];

        var shifts = new Dictionary<string, Vector3>();
        foreach (var breast in new[] { state.Left, state.Right })
        {
            _skin = SkinDepth(breast);
            var restTip = Centre(breast.Target);
            var pivot = breast.Origin;
            if (reset || !breast.Started || !s.Enabled)
            {
                // Start (or physics off): straight to the rest shape.
                for (int i = 0; i < Side; i++)
                {
                    breast.Position[i] = breast.Target[i];
                    breast.PrevTarget[i] = breast.Target[i];
                    breast.Velocity[i] = Vector3.Zero;
                    breast.Goal[i] = breast.Target[i];
                    breast.PrevGoal[i] = breast.Target[i];
                    breast.Display[i] = breast.Target[i];
                }
                breast.Swing = restTip;
                breast.SwingVelocity = Vector3.Zero;
                breast.SwingSettled = Vector3.Zero;
                breast.Squash = 1f;
                breast.PrevTip = restTip;
                breast.PrevPivot = pivot;
                breast.Started = s.Enabled;
                Write(skeleton, breast, uniform, nippleScale);
                continue;
            }

            int steps = advance ? Math.Clamp((int)MathF.Ceiling(dt / MaxStep), 1, MaxSteps) : 0;
            float h = steps > 0 ? dt / steps : 0f;

            // ---- 1. Swing (the whole breast)
            if (steps > 0)
            {
                var tipVelocity = (restTip - breast.PrevTip) / dt;
                float swingKeep = MathF.Exp(-2f * zeta * omega * h);
                for (int step = 0; step < steps; step++)
                {
                    float frac = (step + 1) / (float)steps;
                    var tip = Vector3.Lerp(breast.PrevTip, restTip, frac);
                    var at = Vector3.Lerp(breast.PrevPivot, pivot, frac);
                    var force = (tip - breast.Swing) * (omega * omega) + new Vector3(0, -gravity, 0);
                    breast.SwingVelocity += force * h;
                    breast.SwingVelocity = tipVelocity + (breast.SwingVelocity - tipVelocity) * swingKeep;
                    breast.Swing += breast.SwingVelocity * h;
                    if (LimitSwing(at, tip, ref breast.Swing, maxAngle, maxSquash))
                        breast.SwingVelocity = tipVelocity + (breast.SwingVelocity - tipVelocity) * 0.5f;
                }
                breast.PrevTip = restTip;
                breast.PrevPivot = pivot;
            }

            // What's shown of the swing: Keep shape drops the resting sag; Jiggle exaggerates the movement.
            var raw = breast.Swing - restTip;
            float settle = advance ? 1f - MathF.Exp(-dt / 0.6f) : 0f;
            breast.SwingSettled += (raw - breast.SwingSettled) * settle;
            var swung = restTip + breast.SwingSettled * (1f - keepShape) + (raw - breast.SwingSettled) * jiggle;
            LimitSwing(pivot, restTip, ref swung, maxAngle, maxSquash);

            // Turn (and stretch) the rest shape about the pivot to follow the swing: the ripple layer's goal.
            var restArm = restTip - pivot;
            float restLength = restArm.Length();
            void Goals(Vector3 swungTip)
            {
                var nowArm = swungTip - pivot;
                var turn = MathUtil.FromTo(restArm, nowArm);
                float lengthen = restLength > 1e-5f ? Math.Clamp(nowArm.Length() / restLength, 1f - maxSquash, 1f + maxSquash) : 1f;
                var axis = nowArm.LengthSquared() > 1e-12f ? Vector3.Normalize(nowArm) : Vector3.UnitY;
                // Volume kept: stretching along the swing makes it thinner across, squashing makes it wider.
                float across = 1f / MathF.Sqrt(lengthen);
                for (int i = 0; i < Side; i++)
                {
                    var q = Vector3.Transform(breast.Target[i] - pivot, turn);
                    float along = Vector3.Dot(q, axis);
                    q = axis * (along * lengthen) + (q - axis * along) * across;
                    var goal = pivot + q;
                    breast.Goal[i] = breast.Target[i] + (goal - breast.Target[i]) * Mobility[i];
                }
            }
            Goals(swung);

            // Pressed onto something flat or big (a box, a large collider, the floor): the breast flattens against
            // it (shorter out from the body, wider across it, keeping its volume) instead of being shoved back into
            // the body. Never shorter than SquashMin of its length: beyond that it meets the surface instead.
            Squash(breast, pivot, floor, s.Collisions, steps > 0 ? dt : 0f);

            // Props turn the whole breast away a little (about its base) as well as denting it: a hand on its side
            // moves it over, two hands push both together. Solved on the surface points (see ProjectSurface), for
            // a share of each push (the rest is the dent), carried from frame to frame and easing back when let go.
            if (_props.Length > 0 && _surface.Length > 0 && steps > 0)
            {
                var turn = Quaternion.Slerp(breast.PropTurn, Quaternion.Identity, 1f - MathF.Exp(-dt / 0.3f));
                float maxTurn = maxAngle * 0.6f;
                for (int pass = 0; pass < 8; pass++)
                {
                    for (int i = 0; i < Side; i++)
                    {
                        var g = pivot + Vector3.Transform(breast.Goal[i] - pivot, turn);
                        turned[i] = breast.Goal[i] + (g - breast.Goal[i]) * Mobility[i];
                    }
                    float deepest = 0f;
                    Vector3 push = Vector3.Zero, at = Vector3.Zero;
                    float carry = 1f;
                    for (int j = 0; j < _surface.Length; j++)
                    {
                        var sp = _surface[j];
                        var point = Vector3.Zero;
                        float mob = 0f;
                        for (int k = 0; k < sp.Node.Length; k++)
                        {
                            point += turned[sp.Node[k]] * sp.W[k];
                            mob += Mobility[sp.Node[k]] * sp.W[k];
                        }
                        foreach (var prop in _props)
                        {
                            if (prop.BoneStrength <= 0f)
                                continue;
                            float radius = prop.Radius * prop.BoneStrength + SurfaceSkin;
                            float dist = prop.Gap(point, out var c, out var n);
                            if (dist >= radius)
                                continue;
                            var normal = RememberedSide(breast, prop, j) ?? OwnSide(breast, prop, j, n);
                            var o = prop.IsBox ? normal * prop.Shape.Exit(point, normal, radius) : c + normal * radius - point;
                            if (o.Length() > deepest)
                            {
                                deepest = o.Length();
                                push = o;
                                at = point;
                                carry = MathF.Max(mob, 0.2f);
                            }
                        }
                    }
                    if (deepest < 1e-4f)
                        break;
                    var arm = at - pivot;
                    float arm2 = arm.LengthSquared();
                    if (arm2 < 1e-6f)
                        break;
                    var spin = Vector3.Cross(arm, push) / arm2 / carry * 0.35f;
                    float angle = spin.Length();
                    if (angle < 1e-6f)
                        break;
                    turn = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(spin / angle, angle) * turn);
                    float total = 2f * MathF.Acos(Math.Clamp(MathF.Abs(turn.W), 0f, 1f));
                    if (total > maxTurn)
                        turn = Quaternion.Slerp(Quaternion.Identity, turn, maxTurn / total);
                }
                breast.PropTurn = turn;
            }
            else if (_props.Length == 0)
                breast.PropTurn = Quaternion.Identity;
            if (breast.PropTurn != Quaternion.Identity)
            {
                var applied = Quaternion.Slerp(Quaternion.Identity, breast.PropTurn, _boneStrength);
                for (int i = 0; i < Side; i++)
                {
                    var g = pivot + Vector3.Transform(breast.Goal[i] - pivot, applied);
                    breast.Goal[i] += (g - breast.Goal[i]) * Mobility[i];
                }
            }

            // ---- 2. Ripple (each point around the swing)
            float keep = MathF.Exp(-rippleDamping * h);
            for (int step = 0; step < steps; step++)
            {
                float frac = (step + 1) / (float)steps;
                for (int i = 0; i < Side; i++)
                {
                    var goal = Vector3.Lerp(breast.PrevGoal[i], breast.Goal[i], frac);
                    breast.Velocity[i] += (goal - breast.Position[i]) * (anchorK * invMass * h);
                }
                foreach (var (a, b) in Links)
                {
                    var rest = Vector3.Distance(breast.Goal[a], breast.Goal[b]);
                    var d = breast.Position[b] - breast.Position[a];
                    float len = d.Length();
                    if (len < 1e-6f)
                        continue;
                    var f = d / len * ((len - rest) * linkK * invMass * h * 0.5f);
                    breast.Velocity[a] += f;
                    breast.Velocity[b] -= f;
                }
                for (int i = 0; i < Side; i++)
                {
                    var goalVelocity = (breast.Goal[i] - breast.PrevGoal[i]) / dt;
                    breast.Velocity[i] = goalVelocity + (breast.Velocity[i] - goalVelocity) * keep;
                    breast.Position[i] += breast.Velocity[i] * h;
                }
            }

            for (int i = 0; i < Side; i++)
            {
                var goal = breast.Goal[i];
                var goalVelocity = steps > 0 ? (goal - breast.PrevGoal[i]) / dt : Vector3.Zero;
                // Never further than the ripple's reach from where the swing puts it.
                var offset = breast.Position[i] - goal;
                float dist = offset.Length();
                if (dist > maxRipple)
                {
                    breast.Position[i] = goal + offset * (maxRipple / dist);
                    var dir = offset / dist;
                    float outward = Vector3.Dot(breast.Velocity[i] - goalVelocity, dir);
                    if (outward > 0)
                        breast.Velocity[i] -= dir * outward;
                }
                Collide(breast, i, floor, s.Collisions);
                breast.PrevTarget[i] = breast.Target[i];
            }
            ProjectSurface(breast, breast.Position, breast.Velocity, remember: true);
            for (int i = 0; i < Side; i++)
                shown[i] = breast.Position[i] - breast.Goal[i]; // the ripple on its own
            if (steps > 0)
                for (int i = 0; i < Side; i++)
                    breast.PrevGoal[i] = breast.Goal[i];

            // Smooth the ripple into one surface, then put it on the swing.
            Smooth(breast, shown, maxRipple * 1.5f);
            for (int i = 0; i < Side; i++)
            {
                breast.Display[i] = breast.Goal[i] + shown[i];
                // What's shown stays out of the props (eased at the rim, which is held to the body).
                if (_props.Length > 0 && _surface.Length == 0)
                {
                    var pushed = PushOutOfProps(breast, i, breast.Display[i]);
                    breast.Display[i] += (pushed - breast.Display[i]) * Mobility[i];
                }
                _move = MathF.Max(_move, Vector3.Distance(breast.Display[i], breast.Target[i]));
            }
            ProjectSurface(breast, breast.Display, null, remember: false);
            Lock(breast, breast == state.Left ? state.Right : state.Left, self, breast == state.Left ? 0 : 1);
            var centre = Vector3.Zero;
            for (int i = 0; i < Side; i++)
                centre += breast.Target[i];
            centre /= Side;
            foreach (var sp in _surface)
            {
                _debugSurface.Add(At(breast.Display, sp));
                var rest = At(breast.Target, sp);
                _grabSurface.Add(rest);
                var outward = rest - centre;
                _grabNormals.Add(outward.LengthSquared() > 1e-12f ? Vector3.Normalize(outward) : Vector3.UnitZ);
            }
            Write(skeleton, breast, uniform, nippleScale);
            for (int i = 0; i < Side; i++)
                if (breast.Bones[i] >= 0 && breast.Bones[i] < info.Count)
                    shifts[info.Names[breast.Bones[i]]] = Vector3.TransformNormal(breast.Display[i] - breast.Target[i], skeleton.WorldToModel);
            if (breast.NippleBone >= 0 && breast.NippleBone < info.Count)
            {
                var nip = Vector3.Zero;
                foreach (var (k, w) in breast.NippleCorners)
                    nip += (breast.Display[k] - breast.Target[k]) * w;
                shifts[info.Names[breast.NippleBone]] = Vector3.TransformNormal(nip, skeleton.WorldToModel);
            }
        }
        _shiftsBuilding[self] = shifts;
    }

    /// <summary>Keep a swing point within the largest turn about the pivot, and the largest stretch / squash.</summary>
    private static bool LimitSwing(Vector3 pivot, Vector3 restTip, ref Vector3 swing, float maxAngle, float maxSquash)
    {
        var rest = restTip - pivot;
        var now = swing - pivot;
        float restLength = rest.Length();
        float nowLength = now.Length();
        if (restLength < 1e-5f)
            return false;
        bool limited = false;
        var dir = nowLength > 1e-6f ? now / nowLength : rest / restLength;
        var turn = MathUtil.FromTo(rest, dir);
        float angle = 2f * MathF.Acos(Math.Clamp(MathF.Abs(turn.W), 0f, 1f));
        if (angle > maxAngle)
        {
            turn = Quaternion.Slerp(Quaternion.Identity, turn, maxAngle / angle);
            dir = Vector3.Transform(rest / restLength, turn);
            limited = true;
        }
        float length = Math.Clamp(nowLength, restLength * (1f - maxSquash), restLength * (1f + maxSquash));
        if (MathF.Abs(length - nowLength) > 1e-6f)
            limited = true;
        swing = pivot + dir * length;
        return limited;
    }

    // How freely each grid point may move: full in the middle, less at the rim where the breast meets the chest
    // (so the edge doesn't tear away from the surrounding skin).
    private readonly float[] Mobility;

    private static float[] BuildMobility(RigPart part)
    {
        var m = new float[part.PerSide];
        for (int layer = 0; layer < part.Layers; layer++)
        {
            // Lattice: layers nearer the chest move less (the back face, the part's own bone, doesn't move at all),
            // so the volume bends smoothly from the body out to the front.
            float depth = !part.Lattice ? 1f
                : part.BackLayerBones ? 0.1f + 0.9f * layer / (float)Math.Max(1, part.Layers - 1)
                : (layer + 1) / (float)part.Layers;
            for (int r = 0; r < part.Rows; r++)
            {
                for (int c = 0; c < part.Columns; c++)
                {
                    bool edgeRow = r == 0 || r == part.Rows - 1;
                    bool edgeCol = c == 0 || c == part.Columns - 1;
                    float rim = part.Lattice
                        ? (edgeRow && edgeCol ? 0.65f : edgeRow || edgeCol ? 0.8f : 1f)
                        : (edgeRow && edgeCol ? 0.45f : edgeRow || edgeCol ? 0.7f : 1f);
                    m[layer * part.PerLayer + r * part.Columns + c] = rim * depth;
                }
            }
        }
        return m;
    }

    /// <summary>
    /// Make the displayed offsets move together as one smooth surface: average each point with its neighbours,
    /// ease the rim, keep neighbours from moving too differently (no folding into itself), cap the reach.
    /// </summary>
    private void Smooth(Breast b, Span<Vector3> offsets, float reach)
    {
        Span<Vector3> next = stackalloc Vector3[Side];
        Span<int> count = stackalloc int[Side];
        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 0; i < Side; i++)
            {
                next[i] = Vector3.Zero;
                count[i] = 0;
            }
            foreach (var (a, c) in Links)
            {
                next[a] += offsets[c];
                next[c] += offsets[a];
                count[a]++;
                count[c]++;
            }
            for (int i = 0; i < Side; i++)
                if (count[i] > 0)
                    offsets[i] = offsets[i] * 0.5f + next[i] / count[i] * 0.5f;
        }

        for (int i = 0; i < Side; i++)
            offsets[i] *= Mobility[i];

        // Neighbours may not differ by more than a share of their spacing: the surface can stretch and ripple but
        // not cross over itself.
        for (int iteration = 0; iteration < 3; iteration++)
        {
            foreach (var (a, c) in Links)
            {
                float rest = Vector3.Distance(b.Target[a], b.Target[c]);
                var diff = offsets[a] - offsets[c];
                float len = diff.Length();
                float allowed = rest * 0.45f;
                if (len > allowed && len > 1e-6f)
                {
                    var fix = diff * ((len - allowed) / len * 0.5f);
                    offsets[a] -= fix;
                    offsets[c] += fix;
                }
            }
        }

        for (int i = 0; i < Side; i++)
        {
            float len = offsets[i].Length();
            if (len > reach)
                offsets[i] *= reach / len;
        }
    }

    /// <summary>Each grid bone's position (its rotation stays as the body set it), and its size from the shape sliders.</summary>
    private void Write(in ActorSkeleton skeleton, Breast breast, float uniform, float nippleScale = 1f)
    {
        // The nipple bone rides on the lattice around it, sized with everything else and by the nipple shapes.
        if (breast.NippleBone >= 0)
        {
            var at = breast.NippleRest;
            foreach (var (k, w) in breast.NippleCorners)
                at += (breast.Display[k] - breast.RestTarget[k]) * w;
            var model = skeleton.WorldToModelPoint(at);
            if (MathUtil.IsFinite(model))
                skeleton.SetModelPosition(breast.NippleBone, model);
            float k2 = uniform * nippleScale;
            if (k2 != 1f)
                skeleton.SetModelScale(breast.NippleBone, skeleton.GetModelScale(breast.NippleParent) * k2);
        }
        var root = skeleton.RootRotation;
        var inverseRoot = Quaternion.Inverse(root);
        for (int i = 0; i < Side; i++)
        {
            var model = skeleton.WorldToModelPoint(breast.Display[i]);
            if (MathUtil.IsFinite(model))
                skeleton.SetModelPosition(breast.Bones[i], model);

            // Turn each bone with the surface around it, so what sits in front of it (the nipple, the outer
            // skin) tilts with the surface instead of being sheared or pushed into it. (Not for a lattice: there
            // every vertex sits between bones, and moving them is all it takes to bend the volume smoothly.)
            var turn = _part.Lattice ? Quaternion.Identity : SurfaceTurn(breast, i);
            if (turn != Quaternion.Identity)
            {
                var modelTurn = Quaternion.Normalize(inverseRoot * turn * root);
                var parent = skeleton.GetModelFrame(breast.Parents[i]).Rotation;
                skeleton.SetModelRotation(breast.Bones[i], Quaternion.Normalize(modelTurn * parent));
            }

            // Scaled with the shape sliders: together with the bones' positions (scaled about one point) every vertex
            // on them is scaled exactly, inside the box or out.
            if (uniform != 1f)
                skeleton.SetModelScale(breast.Bones[i], skeleton.GetModelScale(breast.Parents[i]) * uniform);
        }
    }

    private const float MaxSurfaceTurn = 25f * MathF.PI / 180f;

    /// <summary>How the surface normal at grid point <paramref name="i"/> turned from rest to now (world).</summary>
    private Quaternion SurfaceTurn(Breast b, int i)
    {
        int cols = _part.Columns, rows = _part.Rows;
        int r = i / cols, c = i % cols;
        int left = r * cols + Math.Max(0, c - 1), right = r * cols + Math.Min(cols - 1, c + 1);
        int up = Math.Max(0, r - 1) * cols + c, down = Math.Min(rows - 1, r + 1) * cols + c;
        var restNormal = Vector3.Cross(b.Target[right] - b.Target[left], b.Target[down] - b.Target[up]);
        var nowNormal = Vector3.Cross(b.Display[right] - b.Display[left], b.Display[down] - b.Display[up]);
        if (restNormal.LengthSquared() < 1e-14f || nowNormal.LengthSquared() < 1e-14f)
            return Quaternion.Identity;
        var turn = MathUtil.FromTo(restNormal, nowNormal);
        float angle = 2f * MathF.Acos(Math.Clamp(MathF.Abs(turn.W), 0f, 1f));
        if (angle < 1e-4f)
            return Quaternion.Identity;
        if (angle > MaxSurfaceTurn)
            turn = Quaternion.Slerp(Quaternion.Identity, turn, MaxSurfaceTurn / angle);
        return turn;
    }

    /// <summary>One side's own axes (outward from the middle, up, forward), from its grid as it sits on the body now.</summary>
    private bool Axes(Breast b, Vector3 middle, out Vector3 outward, out Vector3 up, out Vector3 forward)
    {
        int cols = _part.Columns, rows = _part.Rows;
        outward = up = forward = default;
        var across = Vector3.Zero;
        var down = Vector3.Zero;
        for (int r = 0; r < rows; r++)
            across += b.Target[r * cols + cols - 1] - b.Target[r * cols];
        for (int c = 0; c < cols; c++)
            down += b.Target[(rows - 1) * cols + c] - b.Target[c];
        if (across.LengthSquared() < 1e-12f || down.LengthSquared() < 1e-12f)
            return false;
        outward = Vector3.Normalize(across);
        if (Vector3.Dot(outward, b.Origin - middle) < 0)
            outward = -outward;
        up = -down - outward * Vector3.Dot(-down, outward);
        if (up.LengthSquared() < 1e-12f)
            return false;
        up = Vector3.Normalize(up);
        forward = Vector3.Cross(outward, up);
        if (Vector3.Dot(forward, Centre(b.Target) - b.Origin) < 0)
            forward = -forward;
        return true;
    }

    /// <summary>
    /// The blend shapes: each set shape moves every node by its sculpted offset (in the box's own axes and size, so it
    /// fits any body and follows the pose), times its amount.
    /// </summary>
    private void ApplyShapes(Breast b, bool left, IReadOnlyDictionary<string, float>? amounts, Vector3 middle)
    {
        if (!_part.Lattice || amounts == null || amounts.Count == 0 || !Axes(b, middle, out var outward, out var up, out var forward))
            return;
        int cols = _part.Columns, rows = _part.Rows, perLayer = _part.PerLayer, layers = _part.Layers;
        // The box's size: average width (first to last column), height (top to bottom row), depth (back to front).
        float width = 0f, height = 0f, depth = 0f;
        for (int l = 0; l < layers; l++)
            for (int r = 0; r < rows; r++)
                width += Vector3.Distance(b.Target[l * perLayer + r * cols], b.Target[l * perLayer + r * cols + cols - 1]);
        for (int l = 0; l < layers; l++)
            for (int c = 0; c < cols; c++)
                height += Vector3.Distance(b.Target[l * perLayer + c], b.Target[l * perLayer + (rows - 1) * cols + c]);
        for (int k = 0; k < perLayer; k++)
            depth += Vector3.Distance(b.Target[k], b.Target[(layers - 1) * perLayer + k]);
        width /= layers * rows;
        height /= layers * cols;
        depth /= perLayer;
        foreach (var shape in BodyShapes.For(_part))
        {
            float amount = BodyShapes.Amount(amounts, shape.Name);
            if (amount == 0f || (left ? shape.Left : shape.Right) == null)
                continue;
            for (int i = 0; i < Side; i++)
            {
                var d = shape.Node(left, i) * amount;
                b.Target[i] += outward * (d.X * width) + up * (d.Y * height) + forward * (d.Z * depth);
            }
        }
    }

    /// <summary>
    /// Scale one grid in its own frame (across, up, forward, taken from the grid as it sits on the body now, so it
    /// follows the pose) around the breast bone, then move it.
    /// </summary>
    private void Reshape(Breast b, SoftBodySettings s, float separation, Vector3 middle, Vector3? sharedPivot = null, float shapeScale = 1f)
    {
        if (!Axes(b, middle, out var outward, out var up, out var forward))
            return;

        float size = Math.Clamp(s.Size * shapeScale, 0.1f, 5f);
        var scale = new Vector3(size * Math.Clamp(s.Width, 0.1f, 5f), size * Math.Clamp(s.Height, 0.1f, 5f),
            size * Math.Clamp(s.Projection, 0.1f, 5f));
        var shift = outward * (Math.Clamp(s.Spacing, -3f, 3f) * separation) + up * (Math.Clamp(s.Lift, -3f, 3f) * separation);

        // Every point in this part's own axes (outward, up, forward), from its bone.
        Span<Vector3> local = stackalloc Vector3[Side];
        var centre = Vector3.Zero;
        float back = float.MaxValue, top = float.MinValue;
        for (int i = 0; i < Side; i++)
        {
            var rel = b.Target[i] - b.Origin;
            local[i] = new Vector3(Vector3.Dot(rel, outward), Vector3.Dot(rel, up), Vector3.Dot(rel, forward));
            centre += local[i];
            back = MathF.Min(back, local[i].Z);
            top = MathF.Max(top, local[i].Y);
        }
        centre /= Side;

        // What it grows from: breasts from their base on the chest (centred across and up); testicles from one
        // point shared by both sides. The bones are scaled by the same amount (see Write), so with Size alone this
        // is an exact scaling of the modelled shape: no creases, seams or flattening at any size.
        Vector3 pivot;
        if (sharedPivot is { } world)
        {
            var rel = world - b.Origin;
            pivot = new Vector3(Vector3.Dot(rel, outward), Vector3.Dot(rel, up), Vector3.Dot(rel, forward));
        }
        else
            pivot = new Vector3(centre.X, centre.Y, back);

        for (int i = 0; i < Side; i++)
        {
            var p = pivot + (local[i] - pivot) * scale;
            b.Target[i] = b.Origin + outward * p.X + up * p.Y + forward * p.Z + shift;
        }
    }

    // Push a point out of any body collider it has gone into further than it already was at rest (so rough
    // collider sizes never push the breast out of its own resting shape), and fully out of any prop.
    private void Collide(Breast breast, int i, float floor, bool bodyColliders)
    {
        var p = breast.Position[i];
        var rest = breast.Target[i];
        foreach (var prop in _surface.Length > 0 ? Array.Empty<PropWorld>() : _props)
        {
            // Strength: how far of the way out a prop pushes (below 1 the breast gives in to it).
            var velocity = breast.Velocity[i];
            float radius = prop.Radius * prop.BoneStrength + _skin;
            var key = (prop.Id, i);
            float dist = prop.Gap(p, out _, out var n);
            Vector3? side = null;
            if (breast.PropSide.TryGetValue(key, out var was))
            {
                // Across the prop's length as it is now (it may have turned since).
                var across = prop.Shape.Across(was);
                if (across.LengthSquared() > 1e-8f)
                    side = Vector3.Normalize(across);
            }
            if (dist >= radius)
            {
                // Right across it since last frame (out the other side, still close): it slipped through,
                // so back to the side it was on.
                bool crossed = side is { } before && dist > 1e-6f && Vector3.Dot(n, before) < -0.2f && dist < radius * 2f + 0.02f;
                if (!crossed)
                {
                    // Outside: remember the side while it's close, forget it once well away.
                    if (dist < radius * 3f + 0.03f && dist > 1e-6f)
                        breast.PropSide[key] = n;
                    else
                        breast.PropSide.Remove(key);
                    continue;
                }
            }
            var pushed = PushOutFully(p, rest, prop.Shape, radius, ref velocity, side);
            if (prop.Gap(pushed, out _, out var now) > 1e-6f)
                breast.PropSide[key] = now;
            p += (pushed - p) * _boneStrength;
            breast.Velocity[i] += (velocity - breast.Velocity[i]) * _boneStrength;
        }
        if (!bodyColliders)
        {
            breast.Position[i] = p;
            return;
        }
        foreach (var c in _capsules)
            p = PushOut(p, rest, c, ref breast.Velocity[i]);
        foreach (var c in _otherHands)
            p = PushOut(p, rest, c, ref breast.Velocity[i]);
        // (where the swing, flattened on the floor, puts it: a breast squashed as far as it goes isn't pushed further)
        float allowed = MathF.Min(floor, MathF.Min(rest.Y, breast.Goal[i].Y));
        if (p.Y < allowed)
        {
            p.Y = allowed;
            if (breast.Velocity[i].Y < 0)
                breast.Velocity[i].Y = 0;
        }
        breast.Position[i] = p;
    }

    private const float SquashMin = 0.45f;
    // A capsule this thick (m) or more flattens the breast like a box does; thinner ones dent it.
    private const float SquashThick = 0.08f;

    private void Squash(Breast breast, Vector3 pivot, float floor, bool useFloor, float dt)
    {
        var arm = Centre(breast.Goal) - pivot;
        var axis = arm.LengthSquared() > 1e-8f ? Vector3.Normalize(arm) : Vector3.UnitY;

        // What it's pressed on: the deepest one into its rest shape. A plane through it (its surface, facing out).
        float deepest = 0f, strength = 1f, plane = 0f;
        Vector3 up = Vector3.Zero;
        if (useFloor)
            for (int i = 0; i < Side; i++)
            {
                float into = floor - breast.Goal[i].Y;
                if (into > deepest)
                {
                    deepest = into;
                    up = Vector3.UnitY;
                    plane = floor;
                    strength = 1f;
                }
            }
        foreach (var prop in _props)
        {
            if (prop.BoneStrength <= 0f || (!prop.IsBox && prop.Radius < SquashThick))
                continue;
            float radius = prop.Radius * prop.BoneStrength + SurfaceSkin;
            for (int i = 0; i < Side; i++)
            {
                if (Mobility[i] < 0.3f)
                    continue; // the base against the body: where it's attached, not what's pressed
                float gap = prop.Gap(breast.Goal[i], out var core, out var n);
                float into = radius - gap;
                if (into <= deepest || n.LengthSquared() < 0.5f)
                    continue;
                deepest = into;
                up = n;
                plane = Vector3.Dot(core + n * radius, n);
                strength = prop.BoneStrength * _boneStrength;
            }
        }

        // How much it faces what it's pressed on: none side on, all of it from about 35 degrees (faded between,
        // so turning doesn't switch it on or off at once).
        float need = 1f;
        if (deepest > 0f)
        {
            float toward = -Vector3.Dot(axis, up);
            float facing = Math.Clamp((toward - 0.15f) / 0.45f, 0f, 1f);
            // Flattening: shorter along its length, wider only along the surface (the across direction level with
            // it), so how far in any point goes depends only on the flattening: one answer, no jumping.
            var rise = up - axis * Vector3.Dot(axis, up);
            bool level = rise.LengthSquared() < 1e-6f;
            if (!level)
                rise = Vector3.Normalize(rise);
            _squashAxis = axis;
            _squashRise = level ? Vector3.Zero : rise;
            float Low(float k)
            {
                float low = float.MaxValue;
                for (int i = 0; i < Side; i++)
                    low = MathF.Min(low, Vector3.Dot(SquashAt(breast, pivot, i, k), up));
                return low;
            }
            if (facing > 0f && Low(1f) < plane)
            {
                float lo = SquashMin, hi = 1f;
                if (Low(lo) < plane)
                    need = lo;
                else
                {
                    for (int it = 0; it < 14; it++)
                    {
                        float mid = (lo + hi) * 0.5f;
                        if (Low(mid) >= plane)
                            lo = mid;
                        else
                            hi = mid;
                    }
                    need = lo;
                }
                need = 1f + (need - 1f) * facing * Math.Clamp(strength, 0f, 1f);
                breast.SquashAxis = axis;
                breast.SquashRise = _squashRise;
            }
        }
        // Eased: quick onto it (the collision covers the moment between), slower letting go, so it fills back out
        // instead of springing (the ripple doesn't get a kick from a sudden change of shape).
        if (dt > 0f)
        {
            float rate = need < breast.Squash ? 1f - MathF.Exp(-dt / 0.04f) : 1f - MathF.Exp(-dt / 0.18f);
            breast.Squash += (need - breast.Squash) * rate;
        }
        breast.Squash = float.IsFinite(breast.Squash) ? Math.Clamp(breast.Squash, SquashMin, 1f) : 1f;
        if (breast.Squash >= 0.9999f)
            return;
        // (letting go: the way it was last flattened, so it fills back out the way it went)
        _squashAxis = breast.SquashAxis;
        _squashRise = breast.SquashRise;
        Span<Vector3> squashed = stackalloc Vector3[Side];
        for (int i = 0; i < Side; i++)
            squashed[i] = SquashAt(breast, pivot, i, breast.Squash);
        for (int i = 0; i < Side; i++)
            breast.Goal[i] = squashed[i];
    }

    private Vector3 _squashAxis, _squashRise;
    private Vector3 SquashAt(Breast breast, Vector3 pivot, int i, float k)
    {
        var q = breast.Goal[i] - pivot;
        float along = Vector3.Dot(q, _squashAxis);
        var lateral = q - _squashAxis * along;
        var upright = _squashRise * Vector3.Dot(lateral, _squashRise);
        float wide = 1f + (1f / MathF.Sqrt(k) - 1f) * Mobility[i];
        return pivot + _squashAxis * (along * (along > 0f ? k : 1f)) + upright + (lateral - upright) * wide;
    }

    // Props are solid: a point inside one goes all the way out to its surface, the nearest way (or, from right on
    // its centre line, back toward where the body puts the point).
    private static Vector3 PushOutFully(Vector3 p, Vector3 rest, in Shape shape, float radius, ref Vector3 velocity, Vector3? side = null)
    {
        float dist = shape.Gap(p, out var closest, out var away);
        if (dist >= radius && side == null)
            return p;
        Vector3 normal;
        if (side is { } known)
        {
            normal = known; // the side it was on before it got in
        }
        else if (away.LengthSquared() > 0.5f)
            normal = away;
        else
        {
            var back = rest - closest;
            normal = back.LengthSquared() > 1e-12f ? Vector3.Normalize(back) : Vector3.UnitY;
        }
        float into = Vector3.Dot(velocity, normal);
        if (into < 0)
            velocity -= normal * into;
        // A box: straight out that way (its faces are flat, so its nearest point isn't the way out).
        return shape.Box ? p + normal * shape.Exit(p, normal, radius) : closest + normal * radius;
    }

    // A surface point that's inside a prop with no side remembered (the prop arrived on it, e.g. placed between the
    // breasts): out toward its own breast, the side of the prop where the body puts the point, never through to the
    // other one.
    private Vector3 OwnSide(Breast breast, in PropWorld prop, int j, Vector3 normal)
    {
        var home = At(breast.Target, _surface[j]);
        prop.Gap(home, out _, out var fromAxis);
        if (fromAxis.LengthSquared() > 0.5f)
            return fromAxis;
        return normal.LengthSquared() > 0.5f ? normal : Vector3.UnitY;
    }

    // Locked hands carry the grid points: each goes where it was in the hand's space when the lock was made. Just
    // the part under the hand (fully near it, fading out further away and toward the chest), or the whole breast
    // the hand is on (all of it, the chest side a little less so it stays attached and stretches). The physics goes
    // on from there, so it still wobbles and the live mesh still dents on top.
    private void Lock(Breast breast, Breast other, nint self, int which)
    {
        static float Closest(Breast b, Vector3 at)
        {
            float best = float.MaxValue;
            for (int i = 0; i < b.Display.Length; i++)
                best = MathF.Min(best, Vector3.DistanceSquared(b.Display[i], at));
            return MathF.Sqrt(best);
        }
        foreach (var (owner, side, hand, strength, whole) in _locks)
        {
            var key = (self, which, owner, side);
            _heldSeen.Add(key);
            if (_held.TryGetValue(key, out var held) && held.Whole != whole)
                _held.Remove(key); // switched between whole and part: take hold again from where it is now
            if (!_held.TryGetValue(key, out held))
            {
                var local = new Vector3[Side];
                var weight = new float[Side];
                bool any = false;
                if (whole)
                {
                    // Only the breast this hand is on (the nearer one), and only once it's at it.
                    float here = Closest(breast, hand.Position);
                    if (here > LockWholeReach || Closest(other, hand.Position) < here)
                        continue;
                }
                for (int i = 0; i < Side; i++)
                {
                    local[i] = hand.PointToLocal(breast.Display[i]);
                    if (whole)
                        weight[i] = MathF.Min(1f, Mobility[i] * 1.6f);
                    else
                    {
                        float d = Vector3.Distance(breast.Display[i], hand.Position);
                        weight[i] = Math.Clamp(1f - (d - LockNear) / (LockFar - LockNear), 0f, 1f) * Mobility[i];
                    }
                    any |= weight[i] > 0f;
                }
                if (!any)
                    continue; // nowhere near this breast: try again next frame
                _held[key] = held = (local, weight, whole);
            }
            float s = Math.Clamp(strength, 0f, 1f);
            for (int i = 0; i < Side; i++)
            {
                float w = held.Weight[i] * s;
                if (w <= 0f)
                    continue;
                var to = hand.PointFromLocal(held.Local[i]);
                if (!MathUtil.IsFinite(to))
                    continue;
                breast.Display[i] = Vector3.Lerp(breast.Display[i], to, w);
                breast.Position[i] = Vector3.Lerp(breast.Position[i], to, w);
                breast.Velocity[i] *= 1f - w;
            }
        }
    }

    // The side of a prop a point was last on (across the prop's length as it is now), or null.
    private static Vector3? RememberedSide(Breast breast, in PropWorld prop, int i)
    {
        if (!breast.PropSide.TryGetValue((prop.Id, i), out var was))
            return null;
        var across = prop.Shape.Across(was);
        return across.LengthSquared() > 1e-8f ? Vector3.Normalize(across) : null;
    }

    // The shown point, out of the props on the side its simulated point is on (never shown through a finger).
    private Vector3 PushOutOfProps(Breast breast, int i, Vector3 p)
    {
        var none = Vector3.Zero;
        foreach (var prop in _props)
        {
            float radius = prop.Radius * prop.BoneStrength + _skin;
            Vector3? side = null;
            if (breast.PropSide.TryGetValue((prop.Id, i), out var was))
            {
                var across = prop.Shape.Across(was);
                if (across.LengthSquared() > 1e-8f)
                    side = Vector3.Normalize(across);
            }
            float dist = prop.Gap(p, out _, out var n);
            if (dist >= radius && !(side is { } before && dist > 1e-6f && Vector3.Dot(n, before) < -0.2f && dist < radius * 2f + 0.02f))
                continue;
            var pushed = PushOutFully(p, p, prop.Shape, radius, ref none, side);
            p += (pushed - p) * _boneStrength;
        }
        return p;
    }

    private static Vector3 PushOut(Vector3 p, Vector3 rest, in Capsule c, ref Vector3 velocity)
    {
        var closest = ClosestOnSegment(c.A, c.B, p);
        var d = p - closest;
        float dist = d.Length();
        if (dist >= c.Radius)
            return p;
        // How deep the resting point already sits in this collider: allow that much.
        float restDist = Vector3.Distance(rest, ClosestOnSegment(c.A, c.B, rest));
        float minDist = MathF.Min(c.Radius, restDist);
        if (dist >= minDist)
            return p;
        Vector3 normal = dist > 1e-6f ? d / dist : Vector3.Normalize(p - rest + new Vector3(0, 1e-4f, 0));
        var pushed = closest + normal * minDist;
        float into = Vector3.Dot(velocity, normal);
        if (into < 0)
            velocity -= normal * into;
        return pushed;
    }

    private void BuildColliders(in ActorSkeleton skeleton, float size)
    {
        _capsules.Clear();
        foreach (var side in new[] { "l", "r" })
        {
            AddCapsule(skeleton, "j_ude_a_" + side, "j_ude_b_" + side, MathF.Max(0.30f * size, 0.045f));
            AddCapsule(skeleton, "j_asi_a_" + side, "j_asi_b_" + side, MathF.Max(0.45f * size, 0.07f));
        }
    }

    private void AddCapsule(in ActorSkeleton skeleton, string from, string to, float radius)
    {
        int a = skeleton.Info.IndexOf(from), b = skeleton.Info.IndexOf(to);
        if (a < 0 || b < 0)
            return;
        _capsules.Add(new Capsule(
            skeleton.ModelToWorldPoint(skeleton.GetModelPosition(a)),
            skeleton.ModelToWorldPoint(skeleton.GetModelPosition(b)), radius)); // radius is already in world units (size)
    }

    // Forearms and hands (any character's), as capsules: elbow -> wrist, wrist -> middle knuckle.
    private static void AddHands(in ActorSkeleton skeleton, List<Capsule> into, nint owner)
    {
        float scale = skeleton.RootScale.Y;
        foreach (var side in new[] { "l", "r" })
        {
            int elbow = skeleton.Info.IndexOf("j_ude_b_" + side);
            int wrist = skeleton.Info.IndexOf("j_te_" + side);
            int knuckle = skeleton.Info.IndexOf("j_naka_a_" + side);
            if (elbow >= 0 && wrist >= 0)
                into.Add(new Capsule(skeleton.ModelToWorldPoint(skeleton.GetModelPosition(elbow)),
                    skeleton.ModelToWorldPoint(skeleton.GetModelPosition(wrist)), 0.035f * scale));
            if (wrist >= 0 && knuckle >= 0)
                into.Add(new Capsule(skeleton.ModelToWorldPoint(skeleton.GetModelPosition(wrist)),
                    skeleton.ModelToWorldPoint(skeleton.GetModelPosition(knuckle)), 0.04f * scale));
        }
    }

    // Half the average spacing between neighbouring grid points (at rest).
    private float SkinDepth(Breast b)
    {
        float sum = 0f;
        int n = 0;
        foreach (var (a, c) in Links)
        {
            sum += Vector3.Distance(b.Target[a], b.Target[c]);
            n++;
        }
        return n > 0 ? Math.Clamp(sum / n * 0.5f, 0.002f, 0.08f) : 0.01f;
    }

    private static Vector3 ClosestOnSegment(Vector3 a, Vector3 b, Vector3 p)
    {
        var ab = b - a;
        float len2 = ab.LengthSquared();
        if (len2 < 1e-10f)
            return a;
        float t = Math.Clamp(Vector3.Dot(p - a, ab) / len2, 0f, 1f);
        return a + ab * t;
    }

    private static Vector3 Centre(Vector3[] points)
    {
        var sum = Vector3.Zero;
        foreach (var p in points)
            sum += p;
        return sum / points.Length;
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static (int, int)[] BuildLinks(RigPart part)
    {
        var links = new List<(int, int)>();
        int Id(int l, int r, int c) => l * part.PerLayer + r * part.Columns + c;
        for (int l = 0; l < part.Layers; l++)
        {
            for (int r = 0; r < part.Rows; r++)
            {
                for (int c = 0; c < part.Columns; c++)
                {
                    // Within the layer: across, down and both diagonals.
                    if (c + 1 < part.Columns) links.Add((Id(l, r, c), Id(l, r, c + 1)));
                    if (r + 1 < part.Rows) links.Add((Id(l, r, c), Id(l, r + 1, c)));
                    if (r + 1 < part.Rows && c + 1 < part.Columns) links.Add((Id(l, r, c), Id(l, r + 1, c + 1)));
                    if (r + 1 < part.Rows && c > 0) links.Add((Id(l, r, c), Id(l, r + 1, c - 1)));
                    // To the next layer: straight through and slanting, so the box resists shearing.
                    if (l + 1 < part.Layers)
                    {
                        links.Add((Id(l, r, c), Id(l + 1, r, c)));
                        if (r + 1 < part.Rows) links.Add((Id(l, r, c), Id(l + 1, r + 1, c)));
                        if (r > 0) links.Add((Id(l, r, c), Id(l + 1, r - 1, c)));
                        if (c + 1 < part.Columns) links.Add((Id(l, r, c), Id(l + 1, r, c + 1)));
                        if (c > 0) links.Add((Id(l, r, c), Id(l + 1, r, c - 1)));
                    }
                }
            }
        }
        return links.ToArray();
    }
}
