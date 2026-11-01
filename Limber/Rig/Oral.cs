using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using Limber.Core;

namespace Limber.Rig;

/// <summary>The Oral page's settings (saved).</summary>
public sealed class OralSettings
{
    /// <summary>Running now. Never saved: Oral is always off when Limber starts, and is turned on each time.</summary>
    [Newtonsoft.Json.JsonIgnore]
    public bool Enabled { get; set; }
    /// <summary>The character whose mouth moves (and their place in the object table, as a hint).</summary>
    public string Giver { get; set; } = "";
    public int GiverIndex { get; set; } = -1;
    /// <summary>The partner (whose shaft the mouth follows).</summary>
    public string Receiver { get; set; } = "";
    public int ReceiverIndex { get; set; } = -1;

    /// <summary>Strokes per second.</summary>
    public float Speed { get; set; } = 0.7f;
    /// <summary>How far down the shaft the lips go at the deepest (0 = the tip, 1 = the base).</summary>
    public float Deepest { get; set; } = 0.55f;
    /// <summary>How far down they stay at the shallowest.</summary>
    public float Shallowest { get; set; } = 0.06f;
    /// <summary>0 = every stroke the same; 1 = strokes vary a lot in depth and pace, with the odd pause.</summary>
    public float Variation { get; set; } = 0.5f;
    /// <summary>The shaft's thickness (radius, metres) where Limber can't measure it.</summary>
    public float Thickness { get; set; } = 0.017f;
    /// <summary>Head pitch on top of facing along the shaft (degrees; + = chin down).</summary>
    public float HeadAngle { get; set; }
    /// <summary>How much the head nods with the strokes (degrees).</summary>
    public float Nod { get; set; }
    /// <summary>How much the head follows the shaft's bend at the lips (0 = only its overall direction).</summary>
    public float FollowBend { get; set; } = 0.2f;
    /// <summary>How closely the lips wrap round the shaft (0..1).</summary>
    public float LipFit { get; set; } = 1f;
    /// <summary>How much the lips grip and get pulled along with each stroke (0..1).</summary>
    public float LipGrip { get; set; } = 0.5f;
    /// <summary>How far the jaw opens to fit (0..1.5; 1 = just enough).</summary>
    public float JawOpen { get; set; } = 1f;
    /// <summary>Cheeks drawn in on the way out (0..1).</summary>
    public float Suction { get; set; } = 0.5f;
    /// <summary>The opening's shape: 1 = a circle round the shaft, 0 = the lips keep their own angles (an oval).</summary>
    public float Roundness { get; set; } = 1f;
    /// <summary>The opening's width against its height (1 = as tall as wide).</summary>
    public float MouthWidth { get; set; } = 1f;
    /// <summary>Lips tighter (negative, metres) or looser (positive) round the shaft.</summary>
    public float Tightness { get; set; }
    /// <summary>How much the partner's shaft moves with it (tilts with the strokes, tugged by the lips).</summary>
    public float PartnerMoves { get; set; } = 0.5f;

    /// <summary>How firmly the lips keep their shape in the live mesh (0..1: higher moves the lip as a whole).</summary>
    public float LipFirmness { get; set; } = 0.7f;
    /// <summary>How snugly the mouth corners tuck in against the shaft (0..1).</summary>
    public float CornerTuck { get; set; } = 0.7f;

    /// <summary>Lips pushed forward and rolled outward round the shaft (0..1), so their inside shows all round.</summary>
    public float Pout { get; set; } = 0.5f;
    /// <summary>How full the lips are while it runs (1 = as they are).</summary>
    public float LipThickness { get; set; } = 1f;
    /// <summary>Where the mouth is put on the shaft, nudged (metres, in the head's axes).</summary>
    public float OffsetRight { get; set; }
    public float OffsetUp { get; set; }

    /// <summary>Lip bones placed by hand in the mouth shape editor: bone -> [x across, y up] (1 = the shaft's edge).</summary>
    public Dictionary<string, float[]> LipShape { get; set; } = new();
    /// <summary>Moving a lip bone in the editor moves its other-side partner the mirror way.</summary>
    public bool LipShapeMirror { get; set; } = true;

    /// <summary>The lips as a live mesh: the face model's own lip vertices fitted onto the shaft (exact seal).</summary>
    public bool LipMesh { get; set; } = true;
    /// <summary>How far lip points reach to close onto the shaft (0..1).</summary>
    public float LipSeal { get; set; } = 0.6f;
    /// <summary>The live-mesh mouth opening's size against the shaft (1 = just round it), all round...</summary>
    public float OpenSize { get; set; } = 1f;
    /// <summary>...and its height and width on their own (1 = as the shaft).</summary>
    public float OpenHeight { get; set; } = 1f;
    public float OpenWidth { get; set; } = 1f;
    /// <summary>With a big shaft: how far the chin tips up (degrees at the biggest)...</summary>
    public float ChinUp { get; set; } = 12f;
    /// <summary>...and how much the cheeks fill out (0..1.5).</summary>
    public float Cheeks { get; set; } = 0.6f;

    /// <summary>Draw the shaft as Limber sees it, and where the lips should be (for checking).</summary>
    [Newtonsoft.Json.JsonIgnore]
    public bool ShowGuide { get; set; }

    /// <summary>Read the shaft's shape from the partner's model (experimental; off: its bones and the Thickness slider).</summary>
    public bool MeasureShaft { get; set; }
    /// <summary>Which way the racial refit is read (-1 = not at all; 0..3: the four ways it might be stored).</summary>
    public int FitVariant { get; set; }

    /// <summary>Shrink the teeth away into the mouth while it runs.</summary>
    public bool HideTeeth { get; set; } = true;
}

/// <summary>
/// Oral: one character's mouth follows a partner's shaft. Limber's own design:
///
/// - The shaft is read live from the partner's body-mod chain (iv_ochinko_a..f) as a bent line with its tip
///   continued past the last bone; the lips travel along that curve (not a straight line), measured in arc length.
/// - Depth over time comes from a stroke generator: each stroke gets its own depth, pace and pause from a seeded
///   hash, eased in and out, so the rhythm varies without random jitter; the result is smoothed so changes in the
///   settings never jump.
/// - The head is placed so the centre of the lips (measured from the face's own lip bones) sits on the curve, facing
///   down the shaft, by bending the neck and spine through the head's IK handle.
/// - The jaw opens just enough for the shaft's thickness at the lips; every lip bone is then put on a ring round the
///   shaft at its own angle (upper on top, corners at the sides), pulled along the shaft by the stroke's speed
///   (grip) and the cheeks drawn in on the way out.
/// Everything happens on this PC only, on adult characters only.
/// </summary>
public sealed unsafe class OralService
{
    private const string Head = "j_kao";
    private static readonly string[] ShaftBones = ["iv_ochinko_a", "iv_ochinko_b", "iv_ochinko_c", "iv_ochinko_d", "iv_ochinko_e", "iv_ochinko_f"];
    // (older faces: j_f_ulip_a, j_f_lip_l...; Dawntrail faces: j_f_ulip_01_l, j_f_umlip_01_l, j_f_uslip_l...)
    private static readonly string[] LipPrefixes = ["j_f_ulip", "j_f_umlip", "j_f_uslip", "j_f_dlip", "j_f_dmlip", "j_f_dslip", "j_f_lip"];

    private readonly RigService _rigs;
    private readonly IObjectTable _objects;
    private readonly IClientState _clientState;
    private readonly Func<OralSettings> _settings;
    private readonly SkeletonInfoCache _cache = new(); // bone update thread only

    public Func<bool> Allowed = () => true;

    // Framework thread -> bone update thread.
    private volatile bool _running;
    private nint _giver, _receiver;
    private ushort _giverIndex, _receiverIndex;
    private volatile float _blend;       // 0 = the head as it's held, 1 = following the shaft
    private volatile float _depth;       // 0..1 along the shaft from the tip (smoothed)
    private volatile float _depthSpeed;  // per second
    private volatile float _nod;         // degrees right now
    private volatile float _shape;       // where in the stroke: 0 = shallowest, 1 = deepest
    private bool _madeGoal;

    // The stroke generator (framework thread).
    private double _phase;
    private long _stroke = -1;
    private float _strokeDepth = 1f, _strokePace = 1f, _strokeHold;
    private float _rawDepth;

    // Bone thread -> UI.
    public volatile string Status = "";
    /// <summary>What "Show the shaft" draws (world space).</summary>
    public sealed record GuideShape(Vector3[] Line, float[] Radius, Vector3 LipsShouldBe, Vector3 LipsAre);
    public volatile GuideShape? Guide;
    /// <summary>A sample of the shaft's skinned vertices (world), when it's read from the model.</summary>
    public volatile Vector3[]? GuideDots;

    /// <summary>Render thread: the shaft as Limber sees it (rings along it), where the lips should be (green) and
    /// where they are (orange). For checking the fit.</summary>
    public void DrawGuide(IGameGui gui)
    {
        var g = Guide;
        if(!_running || g == null || !_settings().ShowGuide)
            return;
        var draw = Dalamud.Bindings.ImGui.ImGui.GetBackgroundDrawList();
        bool Screen(Vector3 w, out Vector2 at) => gui.WorldToScreen(w, out at, out bool shown) && shown;
        for(int i = 0; i < g.Line.Length; i++)
        {
            if(i > 0 && Screen(g.Line[i - 1], out var a) && Screen(g.Line[i], out var b))
                draw.AddLine(a, b, 0xFFFFC040, 2f);
            var along = g.Line[Math.Min(i + 1, g.Line.Length - 1)] - g.Line[Math.Max(i - 1, 0)];
            if(along.LengthSquared() < 1e-10f)
                continue;
            along = Vector3.Normalize(along);
            var u = Norm(Vector3.Cross(along, MathF.Abs(along.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
            var v = Vector3.Cross(along, u);
            Vector2? first = null, previous = null;
            for(int k = 0; k <= 16; k++)
            {
                float t = k / 16f * MathF.PI * 2f;
                if(!Screen(g.Line[i] + (u * MathF.Cos(t) + v * MathF.Sin(t)) * g.Radius[i], out var p))
                {
                    previous = null;
                    continue;
                }
                if(previous is { } q)
                    draw.AddLine(q, p, 0xA0FFC040, 1f);
                previous = p;
                first ??= p;
            }
        }
        if(_settings().MeasureShaft && GuideDots is { } dots)
            foreach(var d in dots)
                if(Screen(d, out var at))
                    draw.AddCircleFilled(at, 2f, 0xFFFF60FF);
        if(Screen(g.LipsShouldBe, out var target))
            draw.AddCircleFilled(target, 5f, 0xFF40FF40);
        if(Screen(g.LipsAre, out var lips))
            draw.AddCircle(lips, 7f, 0xFF20A0FF, 16, 2f);
    }

    /// <summary>One lip bone in the mouth shape editor: where it goes (x across, y up; 1 = the shaft's edge).</summary>
    public readonly record struct LipDot(string Bone, float X, float Y, bool Moved);

    /// <summary>The lip bones as they're placed right now, for the mouth shape editor.</summary>
    public volatile LipDot[] LipDots = [];

    /// <summary>Which face bones are used as the lips, and as what (for checking a face's bones).</summary>
    public volatile string MouthBones = "";

    public OralService(RigService rigs, IObjectTable objects, IClientState clientState, Func<OralSettings> settings)
    {
        _rigs = rigs;
        _objects = objects;
        _clientState = clientState;
        _settings = settings;
    }

    public bool Running => _running;

    // ---- Framework thread

    public void Update(double seconds)
    {
        var s = _settings();
        float dt = (float)Math.Clamp(seconds, 0, 0.1);
        bool want = s.Enabled && Allowed();
        nint giver = 0, receiver = 0;
        ushort gi = 0, ri = 0;
        if(want)
        {
            want = Find(s.Giver, s.GiverIndex, out giver, out gi) && Find(s.Receiver, s.ReceiverIndex, out receiver, out ri) && giver != receiver
                && Limber.Koi.Safety.AdultAllowed(giver) && Limber.Koi.Safety.AdultAllowed(receiver);
            if(!want)
                Status = "Choose two adult characters in the scene (the one with the mouth, and the partner).";
        }

        // Ease in when starting, out when stopping (then the head is let go if Limber took hold of it for this).
        float blend = _blend + (want ? dt : -dt) / 1.2f;
        _blend = Math.Clamp(blend, 0f, 1f);
        if(want)
        {
            _giver = giver; _giverIndex = gi;
            _receiver = receiver; _receiverIndex = ri;
            HoldHead(giver, gi, s.Giver);
            if(s.MeasureShaft)
                MeasureShaft(receiver);
            else
                ShaftInfo = "";
        }
        _running = want || _blend > 0f;
        if(!_running && _madeGoal)
            LetGoHead();
        if(!_running)
        {
            _phase = 0;
            _stroke = -1;
            _rawDepth = 0f;
            _depth = 0f;
            return;
        }
        Advance(s, dt);
    }

    private void Advance(OralSettings s, float dt)
    {
        float variation = Math.Clamp(s.Variation, 0f, 1f);
        _phase += dt * Math.Clamp(s.Speed, 0.05f, 4f) / MathF.Max(0.4f, _strokePace);
        long stroke = (long)Math.Floor(_phase);
        if(stroke != _stroke)
        {
            // A new stroke: its own depth, pace and pause at the deep end.
            _stroke = stroke;
            _strokeDepth = 1f - variation * 0.5f * Hash(stroke * 3 + 1);
            _strokePace = 1f + variation * (Hash(stroke * 3 + 2) - 0.4f) * 0.7f;
            _strokeHold = Hash(stroke * 3 + 3) < variation * 0.35f ? 0.12f + 0.2f * variation : 0f;
        }
        float p = (float)(_phase - stroke);
        // In (a little slower), hold at the deep end, out.
        float hold = _strokeHold, inPart = 0.52f * (1f - hold);
        float shape = p < inPart ? Smoother(p / inPart)
            : p < inPart + hold ? 1f
            : 1f - Smoother((p - inPart - hold) / MathF.Max(1e-3f, 1f - inPart - hold));
        float shallow = Math.Clamp(s.Shallowest, 0f, 1f), deep = Math.Clamp(s.Deepest, shallow, 1f);
        float target = shallow + (shallow + (deep - shallow) * _strokeDepth - shallow) * shape;
        float before = _rawDepth;
        _rawDepth += (target - _rawDepth) * MathF.Min(1f, dt * 18f);
        _depth = _rawDepth;
        _depthSpeed = dt > 1e-4f ? (_rawDepth - before) / dt : 0f;
        _nod = s.Nod * (shape - 0.5f);
        _shape = shape;
    }

    private static float Smoother(float x)
    {
        x = Math.Clamp(x, 0f, 1f);
        return x * x * x * (x * (x * 6f - 15f) + 10f);
    }

    private static float Hash(long n)
    {
        unchecked
        {
            ulong h = (ulong)n * 0xD6E8FEB86659FD93UL;
            h ^= h >> 32;
            h *= 0xD6E8FEB86659FD93UL;
            h ^= h >> 29;
            return (h & 0xFFFFFF) / (float)0xFFFFFF;
        }
    }

    private void HoldHead(nint address, ushort index, string name)
    {
        lock(_rigs.Sync)
        {
            var rig = _rigs.GetOrCreateRig(address, index, name);
            var goal = rig.Find(Head);
            if(goal == null)
            {
                goal = rig.GetOrAdd(Head);
                _madeGoal = true;
            }
            goal.Enabled = true;
            goal.KeepPosition();
            if(!goal.LockRotation)
            {
                goal.LockRotation = true;
                goal.CaptureRotation = true;
            }
        }
    }

    private void LetGoHead()
    {
        _madeGoal = false;
        lock(_rigs.Sync)
        {
            var rig = _rigs.GetRig(_giver);
            if(rig == null)
                return;
            rig.Remove(Head);
            if(rig.Goals.Count == 0)
                _rigs.RemoveRig(_giver);
        }
    }

    /// <summary>The partner the mouth follows, for posing them first (so a posed shaft is where the head goes).</summary>
    public nint Follows(nint actor) => _running && actor == _giver ? _receiver : 0;

    /// <summary>Whether this character is Oral's partner right now (its shaft is read as posed).</summary>
    public bool IsPartner(nint actor) => _running && actor == _receiver;

    /// <summary>Stops (eases out) when the head is let go on the Pose page.</summary>
    public int StopFor(nint actor, string? bone)
    {
        var s = _settings();
        if(!s.Enabled || (actor != 0 && actor != _giver) || (bone != null && bone != Head))
            return 0;
        s.Enabled = false;
        _madeGoal = false; // (the Pose page is letting go of it already)
        return 1;
    }

    private bool Find(string name, int hint, out nint address, out ushort index)
    {
        address = 0;
        index = 0;
        if(string.IsNullOrEmpty(name))
            return false;
        bool gposing = _clientState.IsGPosing;
        int best = -1;
        foreach(var obj in _objects)
        {
            if(obj is not ICharacter || obj.Address == 0)
                continue;
            if(((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)obj.Address)->DrawObject == null)
                continue;
            if(!string.Equals(obj.Name.TextValue, name, StringComparison.Ordinal))
                continue;
            int score = ((obj.ObjectIndex >= 200) == gposing ? 2 : 0) + (obj.ObjectIndex == hint ? 1 : 0);
            if(score > best)
            {
                best = score;
                address = obj.Address;
                index = obj.ObjectIndex;
            }
        }
        return best >= 0;
    }

    // ---- The shaft (world space), read from the partner each frame

    private readonly struct Shaft
    {
        public readonly Vector3[] Points;   // base .. tip (the shaft's real middle when measured)
        public readonly float[] Along;      // arc length at each point
        public readonly float[]? Radii;     // its real thickness at each point (world metres), when measured
        public float Length => Along[^1];
        public bool Measured => Radii != null;

        public Shaft(Vector3[] points, float[]? radii = null)
        {
            Points = points;
            Radii = radii;
            Along = new float[points.Length];
            for(int i = 1; i < points.Length; i++)
                Along[i] = Along[i - 1] + Vector3.Distance(points[i - 1], points[i]);
        }

        /// <summary>The measured thickness <paramref name="fromTip"/> metres down from the tip (world), or -1.</summary>
        public float RadiusAt(float fromTip)
        {
            if(Radii == null)
                return -1f;
            float s = Math.Clamp(Length - fromTip, 0f, Length);
            int i = 1;
            while(i < Points.Length - 1 && Along[i] < s)
                i++;
            float seg = MathF.Max(1e-6f, Along[i] - Along[i - 1]);
            float t = Math.Clamp((s - Along[i - 1]) / seg, 0f, 1f);
            return Radii[i - 1] + (Radii[i] - Radii[i - 1]) * t;
        }

        /// <summary>The point <paramref name="fromTip"/> metres down from the tip, and the direction base -> tip there.</summary>
        public (Vector3 Point, Vector3 Tangent) At(float fromTip)
        {
            float s = Math.Clamp(Length - fromTip, 0f, Length);
            int i = 1;
            while(i < Points.Length - 1 && Along[i] < s)
                i++;
            float seg = MathF.Max(1e-6f, Along[i] - Along[i - 1]);
            float t = Math.Clamp((s - Along[i - 1]) / seg, 0f, 1f);
            var point = Vector3.Lerp(Points[i - 1], Points[i], t);
            // Direction blended between neighbouring segments, so it turns smoothly along a bent shaft.
            var here = Points[i] - Points[i - 1];
            var next = i + 1 < Points.Length ? Points[i + 1] - Points[i] : here;
            var prev = i - 2 >= 0 ? Points[i - 1] - Points[i - 2] : here;
            var dir = t < 0.5f ? Vector3.Lerp(Norm(prev), Norm(here), 0.5f + t) : Vector3.Lerp(Norm(here), Norm(next), t - 0.5f);
            return (point, Norm(dir));
        }
    }

    private static Vector3 Norm(Vector3 v) => v.LengthSquared() > 1e-12f ? Vector3.Normalize(v) : Vector3.UnitY;

    private bool TryShaft(out Shaft shaft)
    {
        shaft = default;
        if(!ActorSkeleton.TryGet(_receiver, _receiverIndex, _cache, out var sk) || sk.Info.Count == 0)
        {
            Status = "Can't see the partner's skeleton.";
            return false;
        }
        if(_settings().MeasureShaft && TryMeasuredShaft(sk, out shaft))
            return true;
        var points = new List<Vector3>(ShaftBones.Length + 1);
        foreach(var name in ShaftBones)
            if(sk.TryGetDrawnWorldFrame(name, _cache, out var f))
                points.Add(f.Position);
        if(points.Count < 2)
        {
            Status = "The partner has no shaft bones (needs an IVCS / YAS skeleton with a penis).";
            return false;
        }
        // The last bone sits inside the glans: continue the line a little to the real tip.
        var last = points[^1] - points[^2];
        points.Add(points[^1] + last * 0.6f);
        shaft = new Shaft(points.ToArray());
        return shaft.Length > 0.01f;
    }

    // ---- The shaft's real shape: the partner's own mesh, posed the way the game draws it

    // Framework thread: the partner's model files read (off the game's threads): every shaft vertex with its bone
    // weights, refitted to the partner's body the way the game refits a model made for another body (its racial
    // deformer file). Bone thread: those vertices skinned every frame with the partner's bones, then measured.
    private sealed class ShaftVertices
    {
        public required string Key;                      // which models (and body) it came from
        public required Vector3[] Bind;                  // where each vertex rests on the partner's body (model space)
        public required string[][] Bones;                // what each is weighted to
        public required float[][] Weights;
        public required Dictionary<string, Matrix4x4>? Fitting; // per bone: the game's refit to this body (null: none)
    }

    private sealed class Skinning
    {
        public required ShaftVertices From;
        public required string[] Slots;                  // bone names
        public required Matrix4x4[] RestInverse;         // per slot: the bone's rest pose, inverted
        public required int[][] Slot;                    // per vertex: its bones' slots
        public required Vector3[] Fitted;                // per vertex: refitted to this body's rest shape
    }

    private volatile ShaftVertices? _shaftVertices;
    private System.Threading.Tasks.Task? _measuring;
    private string _measuredKey = "";
    private long _nextMeasureCheck;
    private Skinning? _skinning; // bone update thread only
    private ShaftVertices? _skinningFailed;
    public volatile string ShaftInfo = "";

    // The body id (101 = Midlander man ... 1801 = Viera woman) a character is drawn with.
    private static ushort BodyId(nint character)
    {
        var c = (byte*)&((FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)character)->DrawData.CustomizeData;
        byte race = c[0], sex = c[1], tribe = c[4];
        int pair = race switch
        {
            1 => tribe == 2 ? 3 : 1, // Hyur: Midlander, Highlander
            2 => 5, 3 => 11, 4 => 7, 5 => 9, 6 => 13, 7 => 15, 8 => 17,
            _ => 0,
        };
        return pair == 0 ? (ushort)0 : (ushort)((pair + (sex == 1 ? 1 : 0)) * 100 + 1);
    }

    private void MeasureShaft(nint receiver)
    {
        long now = Environment.TickCount64;
        if(now < _nextMeasureCheck || _measuring is { IsCompleted: false })
            return;
        _nextMeasureCheck = now + 2000;
        var drawObject = (nint)((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)receiver)->DrawObject;
        var files = Limber.Gpu.LiveMesh.ModelFiles(drawObject);
        ushort body = BodyId(receiver);
        int variant = _settings().FitVariant;
        string key = receiver.ToString() + "|" + body + "|" + variant + "|" + string.Join("|", files.ConvertAll(f => f.File));
        if(key == _measuredKey)
            return;
        _measuredKey = key;
        var read = ReadFile;
        _measuring = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var bind = new List<Vector3>();
                var bones = new List<string[]>();
                var weights = new List<float[]>();
                ushort modelBody = 0;
                foreach(var (_, file) in files)
                {
                    var data = read(file);
                    if(data == null)
                        continue;
                    try
                    {
                        var found = Limber.BodyRig.MdlRigger.WeightedVertices(data, n => n.StartsWith("iv_ochinko_", StringComparison.Ordinal), 0.5f);
                        if(found.Count == 0)
                            continue;
                        foreach(var (p, b, w) in found)
                        {
                            bind.Add(p);
                            bones.Add(b);
                            weights.Add(w);
                        }
                        if(modelBody == 0)
                            modelBody = Limber.BodyRig.Deformers.BodyOf(file);
                    }
                    catch
                    {
                        // not a model Limber can read: skip it
                    }
                }
                if(bind.Count < 40)
                {
                    _shaftVertices = null;
                    ShaftInfo = "Couldn't read the shaft from the partner's model: using its bones and the Thickness slider.";
                    return;
                }
                // The game's refit of a model made for another body to this one (none needed for the same body).
                Dictionary<string, Matrix4x4>? fitting = null;
                string fitNote = "";
                if(modelBody != 0 && body != 0 && modelBody != body && variant >= 0)
                {
                    var pbd = Limber.BodyRig.RigBuilder.ReadActive(Limber.BodyRig.Deformers.GamePath, out _);
                    fitting = pbd != null ? Limber.BodyRig.Deformers.Fitting(pbd, modelBody, body, variant) : null;
                    fitNote = fitting != null ? $", refitted from body {modelBody:D4} to {body:D4}" : $" (made for body {modelBody:D4}, couldn't refit it to {body:D4})";
                }
                _shaftVertices = new ShaftVertices { Key = key, Bind = bind.ToArray(), Bones = bones.ToArray(), Weights = weights.ToArray(), Fitting = fitting };
                ShaftInfo = $"Shaft read from the partner's model: {bind.Count} points{fitNote}.";
            }
            catch(Exception e)
            {
                _shaftVertices = null;
                ShaftInfo = "Couldn't read the shaft: " + e.Message;
            }
        });
    }

    /// <summary>Reads a model file the way the game loaded it (set by the plugin).</summary>
    public Func<string, byte[]?> ReadFile = _ => null;

    // A bone's rest frame in model space (the skeleton's reference pose, from the root down).
    private static bool RestFrame(in ActorSkeleton sk, int bone, out Frame frame)
    {
        frame = Frame.Identity;
        var chain = new List<int>();
        for(int b = bone; b >= 0; b = sk.Info.ParentOf(b))
            chain.Add(b);
        var position = Vector3.Zero;
        var rotation = Quaternion.Identity;
        for(int i = chain.Count - 1; i >= 0; i--)
        {
            if(!sk.TryGetReferenceLocal(chain[i], out var p, out var q))
                return false;
            position += Vector3.Transform(p, rotation);
            rotation = Quaternion.Normalize(rotation * q);
        }
        frame = new Frame(position, rotation);
        return true;
    }

    // Once per model: each vertex refitted to this body's rest shape (a bone the refit file doesn't list fits like
    // the nearest bone above it that it does), and the bones' rest poses for skinning.
    private Skinning? BuildSkinning(ActorSkeleton sk, ShaftVertices v)
    {
        var slots = new List<string>();
        var slotOf = new Dictionary<string, int>(StringComparer.Ordinal);
        var perVertex = new int[v.Bind.Length][];
        for(int i = 0; i < v.Bind.Length; i++)
        {
            var b = v.Bones[i];
            perVertex[i] = new int[b.Length];
            for(int k = 0; k < b.Length; k++)
            {
                if(!slotOf.TryGetValue(b[k], out int slot))
                {
                    slot = slots.Count;
                    slots.Add(b[k]);
                    slotOf[b[k]] = slot;
                }
                perVertex[i][k] = slot;
            }
        }
        var restInverse = new Matrix4x4[slots.Count];
        var fit = new Matrix4x4[slots.Count];
        for(int s = 0; s < slots.Count; s++)
        {
            var rest = RestMatrix(sk, slots[s]);
            restInverse[s] = Matrix4x4.Invert(rest, out var inv) ? inv : Matrix4x4.Identity;
            fit[s] = Matrix4x4.Identity;
            if(v.Fitting == null)
                continue;
            // This bone's refit, or the nearest one above it that has one.
            int bone = sk.Info.IndexOf(slots[s]);
            string? name = slots[s];
            for(int guard = 0; name != null && guard < 64; guard++)
            {
                if(v.Fitting.TryGetValue(name, out var m))
                {
                    fit[s] = m;
                    break;
                }
                bone = bone >= 0 ? sk.Info.ParentOf(bone) : -1;
                name = bone >= 0 ? sk.Info.Names[bone] : null;
            }
        }
        var fitted = new Vector3[v.Bind.Length];
        for(int i = 0; i < v.Bind.Length; i++)
        {
            var w = v.Weights[i];
            var at = Vector3.Zero;
            float total = 0f;
            for(int k = 0; k < w.Length; k++)
            {
                at += Vector3.Transform(v.Bind[i], fit[perVertex[i][k]]) * w[k];
                total += w[k];
            }
            fitted[i] = total > 0f ? at / total : v.Bind[i];
        }
        return new Skinning { From = v, Slots = slots.ToArray(), RestInverse = restInverse, Slot = perVertex, Fitted = fitted };
    }

    // The shaft as it's drawn right now: its vertices skinned with the partner's bones (as the game does), then
    // measured in slices along its bones: each slice's own middle (the line) and how far its skin is from that
    // (the thickness), and the tip where the vertices end.
    private bool TryMeasuredShaft(ActorSkeleton sk, out Shaft shaft)
    {
        shaft = default;
        var vertices = _shaftVertices;
        if(vertices == null || ReferenceEquals(_skinningFailed, vertices))
            return false;
        if(_skinning == null || !ReferenceEquals(_skinning.From, vertices))
        {
            _skinning = BuildSkinning(sk, vertices);
            if(_skinning == null)
            {
                _skinningFailed = vertices;
                return false;
            }
        }
        var skin = _skinning;
        // Each bone as drawn now (model space): its copy in a skeleton mod's extra skeleton if it has one.
        var now = new Matrix4x4[skin.Slots.Length];
        for(int s = 0; s < now.Length; s++)
        {
            Frame frame;
            Vector3 size;
            if(sk.TryFindExtraBone(skin.Slots[s], _cache, out int part, out int pb))
            {
                frame = sk.GetPartialFrame(part, pb);
                size = sk.GetPartialScale(part, pb);
            }
            else if(sk.Info.IndexOf(skin.Slots[s]) is int body and >= 0)
            {
                frame = sk.GetModelFrame(body);
                size = sk.GetModelScale(body);
            }
            else
            {
                now[s] = Matrix4x4.Identity;
                continue;
            }
            now[s] = skin.RestInverse[s] * (Matrix4x4.CreateScale(size) * Matrix4x4.CreateFromQuaternion(frame.Rotation) * Matrix4x4.CreateTranslation(frame.Position));
        }
        int n = skin.Fitted.Length;
        var posed = new Vector3[n];
        var dots = new Vector3[Math.Min(n, 400)];
        for(int i = 0; i < n; i++)
        {
            var w = vertices.Weights[i];
            var at = Vector3.Zero;
            float total = 0f;
            for(int k = 0; k < w.Length; k++)
            {
                at += Vector3.Transform(skin.Fitted[i], now[skin.Slot[i][k]]) * w[k];
                total += w[k];
            }
            posed[i] = total > 0f ? at / total : skin.Fitted[i];
        }
        // (for "Show the shaft": a sample of the skinned vertices themselves, to check them against the real mesh)
        for(int i = 0; i < dots.Length; i++)
            dots[i] = sk.ModelToWorldPoint(posed[(int)((long)i * n / dots.Length)]);
        GuideDots = dots;

        // The bones' line as drawn (model space), to measure along.
        var line = new List<Vector3>(ShaftBones.Length);
        foreach(var name in ShaftBones)
            if(sk.TryGetDrawnWorldFrame(name, _cache, out var f))
                line.Add(sk.WorldToModelPoint(f.Position));
        int k2 = line.Count;
        if(k2 < 2)
            return false;
        var along = new float[k2];
        for(int i = 1; i < k2; i++)
            along[i] = along[i - 1] + Vector3.Distance(line[i - 1], line[i]);
        var place = new float[n];
        for(int i = 0; i < n; i++)
        {
            float best = float.MaxValue;
            for(int j = 0; j + 1 < k2; j++)
            {
                var d = line[j + 1] - line[j];
                float len2 = d.LengthSquared();
                if(len2 < 1e-10f)
                    continue;
                float t = Vector3.Dot(posed[i] - line[j], d) / len2;
                t = j + 2 < k2 ? Math.Clamp(t, 0f, 1f) : Math.Clamp(t, 0f, 4f);
                float dist = Vector3.DistanceSquared(posed[i], line[j] + d * t);
                if(dist < best)
                {
                    best = dist;
                    place[i] = along[j] + MathF.Sqrt(len2) * t;
                }
            }
        }
        var sorted = (float[])place.Clone();
        Array.Sort(sorted);
        float start = sorted[(int)(n * 0.01f)], end = sorted[Math.Min(n - 1, (int)(n * 0.985f))];
        if(end - start < 0.01f)
            return false;
        // Slices: each one's middle and thickness (the middle of its points' distances, so a stray one doesn't count).
        const int Slices = 20;
        var centres = new List<Vector3>(Slices + 1);
        var radii = new List<float>(Slices + 1);
        var members = new List<int>[Slices];
        for(int sl = 0; sl < Slices; sl++)
            members[sl] = new List<int>();
        for(int i = 0; i < n; i++)
        {
            int sl = (int)((place[i] - start) / (end - start) * Slices);
            if(sl >= 0 && sl < Slices)
                members[sl].Add(i);
        }
        var distances = new List<float>();
        for(int sl = 0; sl < Slices; sl++)
        {
            var m = members[sl];
            if(m.Count < 4)
                continue;
            var c = Vector3.Zero;
            foreach(int i in m)
                c += posed[i];
            c /= m.Count;
            distances.Clear();
            foreach(int i in m)
                distances.Add(Vector3.Distance(posed[i], c));
            distances.Sort();
            centres.Add(c);
            radii.Add(distances[distances.Count / 2]);
        }
        if(centres.Count < 4)
            return false;
        // The very tip: just past the last slice, rounded off.
        var lastDir = centres[^1] - centres[^2];
        centres.Add(centres[^1] + (lastDir.LengthSquared() > 1e-12f ? Vector3.Normalize(lastDir) : Vector3.UnitY) * radii[^1] * 0.6f);
        radii.Add(radii[^1] * 0.35f);
        // Gently smoothed (no steps between slices).
        var points = new Vector3[centres.Count];
        var thick = new float[centres.Count];
        float scale = MathF.Max(0.01f, sk.RootScale.Y);
        for(int i = 0; i < centres.Count; i++)
        {
            var c = centres[i];
            float r = radii[i];
            if(i > 0 && i + 1 < centres.Count - 1)
            {
                c = centres[i - 1] * 0.25f + centres[i] * 0.5f + centres[i + 1] * 0.25f;
                r = radii[i - 1] * 0.25f + radii[i] * 0.5f + radii[i + 1] * 0.25f;
            }
            points[i] = sk.ModelToWorldPoint(c);
            thick[i] = r * scale;
        }
        shaft = new Shaft(points, thick);
        return shaft.Length > 0.01f;
    }

    // ---- Bone update (posing stage): where the giver's head goes

    /// <summary>The head's target (model space) when it's following the shaft; null otherwise.</summary>
    public Frame? HeadTarget(nint address, string bone, Frame held, ActorSkeleton giver)
    {
        if(!_running || address != _giver || bone != Head)
            return null;
        float blend = Smoother(_blend);
        if(blend <= 0f)
            return null;
        if(!TryShaft(out var shaft))
            return null;
        if(!TryMouth(giver, out var mouthLocal, out var localForward, out var localUp))
        {
            Status = "Can't find this character's lips.";
            return null;
        }
        var s = _settings();
        float fromTip = _depth * shaft.Length;
        var (pointWorld, tangentWorld) = shaft.At(fromTip);
        var point = giver.WorldToModelPoint(pointWorld);
        var tangent = Norm(Vector3.Transform(tangentWorld, Quaternion.Inverse(giver.RootRotation)));

        // Face down the shaft (towards its base), keeping the head's own up as much as possible. Mostly along the
        // shaft as a whole (its base to its tip), only a little along its bend right at the lips: following every
        // bend made the head nod up and down on every stroke.
        var whole = Norm(Vector3.Transform(shaft.Points[^1] - shaft.Points[0], Quaternion.Inverse(giver.RootRotation)));
        float bend = Math.Clamp(s.FollowBend, 0f, 1f);
        var forward = -Norm(Vector3.Lerp(whole, tangent, bend));
        var up = Vector3.Transform(localUp, held.Rotation);
        up -= forward * Vector3.Dot(up, forward);
        if(up.LengthSquared() < 1e-6f)
            up = Vector3.UnitY - forward * forward.Y;
        var nowForward = Vector3.Transform(localForward, held.Rotation);
        var nowUp = Vector3.Transform(localUp, held.Rotation);
        var rotation = Quaternion.Normalize(MathUtil.AlignFrames(nowForward, nowUp, forward, Norm(up)) * held.Rotation);
        // Pitch on top (head angle + the nod), round the head's own side axis.
        // (a big shaft tips the chin up: the head tilts back to take it straighter)
        float pitch = (s.HeadAngle + _nod - Math.Clamp(s.ChinUp, 0f, 40f) * _bigness * blend) * MathF.PI / 180f;
        if(pitch != 0f)
        {
            var side = Norm(Vector3.Cross(Vector3.Transform(localUp, rotation), Vector3.Transform(localForward, rotation)));
            rotation = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(side, pitch) * rotation);
        }
        // The lips' centre on the shaft.
        // Nudged by the Mouth position sliders (in the head's own axes: right as the character sees it, up),
        // for faces whose lips sit a little off where Limber measures them.
        var localRight = Norm(Vector3.Cross(localUp, localForward));
        var nudge = localRight * s.OffsetRight + localUp * s.OffsetUp;
        // With the lips' live mesh, the way a real mouth takes it: the upper lip (with the upper teeth) stays nearly
        // where it is and the shaft sits under it, the jaw dropping to make room. So the mouth's middle goes above
        // the shaft's line by nearly its radius (the lips only lift the upper lip a few mm).
        if(s.LipMesh && Limber.Gpu.LipMesh.HasShapes && Limber.Gpu.LipMesh.Owner == _giver)
        {
            float measuredHere = shaft.RadiusAt(fromTip);
            float r = measuredHere > 0f ? measuredHere / MathF.Max(0.01f, giver.RootScale.Y) : MathF.Max(0.004f, s.Thickness);
            r *= Math.Clamp(s.OpenSize, 0.5f, 2f) * Math.Clamp(s.OpenHeight, 0.5f, 2f);
            nudge += localUp * MathF.Max(0f, r - UpperLipLift) * blend;
        }
        var position = point - Vector3.Transform(mouthLocal - nudge, rotation); // (+ moves the mouth up / right)
        if(!MathUtil.IsFinite(position) || !MathUtil.IsFinite(rotation))
            return null;

        // (what it's doing, in numbers: for checking when something looks off)
        float turnDegrees = 2f * MathF.Acos(Math.Clamp(MathF.Abs(Quaternion.Dot(held.Rotation, rotation)), 0f, 1f)) * 180f / MathF.PI;
        string numbers = $"shaft {shaft.Length * 100f:0.0} cm{(shaft.Measured ? " (measured)" : " (bones)")}, head moves {Vector3.Distance(held.Position, position) * 100f:0} cm and turns {turnDegrees:0}°";
        Status = Vector3.Distance(held.Position, position) > 0.6f
            ? "Following, but the partner is far away: move the two closer together. (" + numbers + ")"
            : "Following: " + numbers + ".";
        return new Frame(Vector3.Lerp(held.Position, position, blend), Quaternion.Slerp(held.Rotation, rotation, blend));
    }

    // Where the centre of the lips is from the head (in the head's own axes, from the face skeleton's copy of the
    // head so it doesn't matter what the body has done yet), and the head's forward / up axes (from the rest pose,
    // which faces +Z with +Y up).
    private bool TryMouth(in ActorSkeleton giver, out Vector3 mouthLocal, out Vector3 localForward, out Vector3 localUp)
    {
        mouthLocal = default;
        localForward = Vector3.UnitZ;
        localUp = Vector3.UnitY;
        if(!FindLips(giver, out int part, out var lips) || lips.Count == 0)
            return false;
        var info = giver.PartialInfo(part, _cache);
        if(info == null)
            return false;
        int root = info.IndexOf(Head);
        var head = root >= 0 ? giver.GetPartialFrame(part, root) : giver.GetPartialParentFrame(part, 0, _cache);
        var centre = Vector3.Zero;
        foreach(var b in lips)
            centre += giver.GetPartialFrame(part, b).Position;
        centre /= lips.Count;
        var toHead = Quaternion.Inverse(head.Rotation);
        mouthLocal = Vector3.Transform(centre - head.Position, toHead);

        // The mouth's own axes, from the face itself: across from corner to corner, up from the lower lip to the
        // upper, and straight out of the mouth (away from the head). The way the mouth really opens, which isn't
        // quite the way the head faces.
        int cornerA = -1, cornerB = -1, upper = info.IndexOf("j_f_ulip_a"), lower = info.IndexOf("j_f_dlip_a");
        foreach(var b in lips)
            if(info.Names[b].StartsWith("j_f_lip", StringComparison.Ordinal))
            {
                if(cornerA < 0) cornerA = b;
                else if(cornerB < 0) cornerB = b;
            }
        if(cornerA >= 0 && cornerB >= 0 && upper >= 0 && lower >= 0)
        {
            var across = giver.GetPartialFrame(part, cornerB).Position - giver.GetPartialFrame(part, cornerA).Position;
            var up = giver.GetPartialFrame(part, upper).Position - giver.GetPartialFrame(part, lower).Position;
            var outward = Vector3.Cross(across, up);
            if(outward.LengthSquared() > 1e-12f && across.LengthSquared() > 1e-8f)
            {
                outward = Vector3.Normalize(outward);
                if(Vector3.Dot(outward, centre - head.Position) < 0f)
                    outward = -outward;
                // Up: the head's own up kept square to that (the lips' opening can tilt it).
                int bodyHead = giver.Info.IndexOf(Head);
                var headUp = bodyHead >= 0 && RestModel(giver, bodyHead, out var restTurn)
                    ? Vector3.Transform(Vector3.Transform(Vector3.UnitY, Quaternion.Inverse(restTurn)), head.Rotation)
                    : up;
                headUp -= outward * Vector3.Dot(headUp, outward);
                if(headUp.LengthSquared() > 1e-8f)
                {
                    localForward = Vector3.Transform(outward, toHead);
                    localUp = Vector3.Transform(Vector3.Normalize(headUp), toHead);
                    return true;
                }
            }
        }

        int body = giver.Info.IndexOf(Head);
        if(body >= 0 && RestModel(giver, body, out var rest))
        {
            var inverse = Quaternion.Inverse(rest);
            localForward = Vector3.Transform(Vector3.UnitZ, inverse);
            localUp = Vector3.Transform(Vector3.UnitY, inverse);
        }
        return true;
    }

    private static bool RestModel(in ActorSkeleton sk, int bone, out Quaternion rotation)
    {
        rotation = Quaternion.Identity;
        var chain = new List<int>();
        for(int b = bone; b >= 0; b = sk.Info.ParentOf(b))
            chain.Add(b);
        for(int i = chain.Count - 1; i >= 0; i--)
        {
            if(!sk.TryGetReferenceLocal(chain[i], out _, out var local))
                return false;
            rotation = Quaternion.Normalize(rotation * local);
        }
        return true;
    }

    private bool FindLips(in ActorSkeleton sk, out int part, out List<int> lips)
    {
        part = -1;
        lips = new List<int>();
        for(int p = 1; p < sk.PartialCount; p++)
        {
            var info = sk.PartialInfo(p, _cache);
            if(info == null)
                continue;
            for(int b = 0; b < info.Count; b++)
                foreach(var prefix in LipPrefixes)
                    if(info.Names[b].StartsWith(prefix, StringComparison.Ordinal))
                    {
                        lips.Add(b);
                        break;
                    }
            if(lips.Count > 0)
            {
                part = p;
                return true;
            }
        }
        return false;
    }

    // ---- Bone update (before posing): the partner's shaft moves with it

    /// <summary>
    /// The partner's shaft moves along: it tilts up a little as the mouth goes deeper (the head pushing on it) and
    /// is tugged toward the mouth on the way out (the lips holding on), stretching slightly toward the tip. Runs
    /// before posing, so the head then follows the shaft where it really is this frame.
    /// </summary>
    public void UpdatePartner()
    {
        if(!_running || _blend <= 0f)
            return;
        var s = _settings();
        float amount = Math.Clamp(s.PartnerMoves, 0f, 1f) * Smoother(_blend);
        if(amount <= 0f)
            return;
        if(!ActorSkeleton.TryGet(_receiver, _receiverIndex, _cache, out var sk) || sk.Info.Count == 0)
            return;
        var bones = new List<(int Part, int Bone)>();
        foreach(var name in ShaftBones)
        {
            if(sk.TryFindExtraBone(name, _cache, out int p, out int b))
                bones.Add((p, b));
            else if(sk.Info.IndexOf(name) is int body and >= 0)
                bones.Add((0, body));
        }
        if(bones.Count < 2)
            return;
        Frame Get((int Part, int Bone) x) => x.Part == 0 ? sk.GetModelFrame(x.Bone) : sk.GetPartialFrame(x.Part, x.Bone);
        void SetRotation((int Part, int Bone) x, Quaternion q)
        {
            if(x.Part == 0) sk.SetModelRotation(x.Bone, q);
            else sk.SetPartialRotation(x.Part, x.Bone, q);
        }
        void SetPosition((int Part, int Bone) x, Vector3 v)
        {
            if(x.Part == 0) sk.SetModelPosition(x.Bone, v);
            else sk.SetPartialPosition(x.Part, x.Bone, v);
        }

        var root = Get(bones[0]);
        var axis = Norm(Get(bones[^1]).Position - root.Position);
        // Tilt: round the side axis (across the body), up as the mouth goes deeper.
        var side = Vector3.Cross(Vector3.UnitY, axis);
        if(side.LengthSquared() < 1e-8f)
            side = Vector3.UnitX;
        side = Vector3.Normalize(side);
        float tilt = amount * (_shape - 0.3f) * 7f * MathF.PI / 180f;
        if(MathF.Abs(tilt) > 1e-5f)
            SetRotation(bones[0], Quaternion.Normalize(Quaternion.CreateFromAxisAngle(side, -tilt) * root.Rotation));

        // Tug: on the way out the lips pull the shaft a little longer, most at the tip.
        float tug = Math.Clamp(-_depthSpeed * 0.02f, 0f, 0.006f) * amount * (0.4f + Math.Clamp(s.LipGrip, 0f, 1f)) * MathF.Max(0.5f, sk.RootScale.Y);
        if(tug > 1e-5f)
        {
            float step = tug / (bones.Count - 1);
            var dir = Norm(Get(bones[^1]).Position - Get(bones[0]).Position);
            for(int i = 1; i < bones.Count; i++)
                SetPosition(bones[i], Get(bones[i]).Position + dir * step);
        }
    }

    // ---- Bone update (after posing): jaw open, lips round the shaft, cheeks

    public void UpdateMouth()
    {
        _lipsHanded = false;
        try
        {
            Mouth();
        }
        finally
        {
            if(!_lipsHanded)
                Limber.Gpu.LipMesh.Hand(null);
        }
    }

    private bool _lipsHanded;

    private void Mouth()
    {
        if(!_running || _blend <= 0f)
            return;
        var s = _settings();
        if(!ActorSkeleton.TryGet(_giver, _giverIndex, _cache, out var giver) || giver.Info.Count == 0)
            return;
        if(!TryShaft(out var shaft) || !FindLips(giver, out int part, out var lips))
            return;
        var info = giver.PartialInfo(part, _cache);
        if(info == null)
            return;
        float blend = Smoother(_blend);
        float scale = MathF.Max(0.5f, giver.RootScale.Y);

        float fromTip = _depth * shaft.Length;
        var (pointWorld, tangentWorld) = shaft.At(fromTip);
        var axisPoint = giver.WorldToModelPoint(pointWorld);
        var axis = Norm(Vector3.Transform(tangentWorld, Quaternion.Inverse(giver.RootRotation)));
        // Thinner right at the tip.
        // The shaft's real thickness at the lips when measured (in this character's units), else the slider's.
        float measured = shaft.RadiusAt(fromTip);
        float radius = measured > 0f
            ? measured / MathF.Max(0.01f, giver.RootScale.Y)
            : MathF.Max(0.004f, s.Thickness) * (0.72f + 0.28f * Math.Clamp(fromTip / MathF.Max(1e-3f, shaft.Length * 0.1f), 0f, 1f));

        // How big it is for this mouth (0: up to ~3 cm across, 1: ~6 cm): bigger opens the jaw further, tips the chin
        // up and fills the cheeks out.
        float bigNow = Math.Clamp((radius * Math.Clamp(s.OpenSize, 0.5f, 2f) * Math.Clamp(s.OpenHeight, 0.5f, 2f) - 0.016f) / 0.014f, 0f, 1f);
        _bigness += (bigNow - _bigness) * 0.1f;

        // Only once the lips have got there (the head may still be on its way, or out of reach).
        var mouth = Vector3.Zero;
        foreach(var b in lips)
            mouth += giver.GetPartialFrame(part, b).Position;
        mouth /= lips.Count;
        float away = Vector3.Distance(mouth, axisPoint);
        // (for "Show the shaft": the shaft as Limber sees it, where the lips should be, and where they are)
        var guideRadii = new float[shaft.Points.Length];
        for(int i = 0; i < guideRadii.Length; i++)
            guideRadii[i] = shaft.Radii?[i] ?? MathF.Max(0.004f, s.Thickness) * giver.RootScale.Y;
        Guide = new GuideShape(shaft.Points, guideRadii, pointWorld, giver.ModelToWorldPoint(mouth));
        if(away > 0.06f * scale)
            return;
        // The ring goes round the mouth where it actually is (across the shaft), not pulled over to the shaft's
        // line: if the head is a little off (out of reach, or nudged by the sliders) the lips keep their place, and
        // fade out of the wrap the further off it is.
        var across = mouth - axisPoint;
        var centre = axisPoint + across - Vector3.Normalize(axis) * Vector3.Dot(across, axis);
        float near = Math.Clamp(1f - (away - 0.02f * scale) / (0.035f * scale), 0f, 1f);

        int Bone(string name) => info.IndexOf(name);
        int Any(params string[] names)
        {
            foreach(var name in names)
                if(Bone(name) is int b and >= 0)
                    return b;
            return -1;
        }
        int upper = Any("j_f_ulip_a", "j_f_umlip_01_l", "j_f_ulip_01_l"), lower = Any("j_f_dlip_a", "j_f_dmlip_01_l", "j_f_dlip_01_l");
        int jaw = Any("j_f_ago", "j_f_dago");

        // Jaw: open just enough for the shaft between the lips (lip bones sit a few mm inside each lip).
        const float lipDepth = 0.004f;
        _jawGap = 0f;
        if(jaw >= 0 && upper >= 0 && lower >= 0 && s.JawOpen > 0f)
        {
            var u = giver.GetPartialFrame(part, upper).Position;
            var l = giver.GetPartialFrame(part, lower).Position;
            var pivot = giver.GetPartialFrame(part, jaw);
            float gap = Vector3.Distance(u, l);
            bool wrapped = s.LipMesh && Limber.Gpu.LipMesh.HasShapes && Limber.Gpu.LipMesh.Owner == _giver;
            // (the live-mesh mouth opens as tall as its Size and Height sliders say)
            float tall = wrapped ? radius * Math.Clamp(s.OpenSize, 0.5f, 2f) * Math.Clamp(s.OpenHeight, 0.5f, 2f) : radius;
            float need = (2f * (tall + lipDepth * scale) - gap) * Math.Clamp(s.JawOpen, 0f, 1.5f);
            // With the lips' live mesh the jaw does the opening (the shaft sits under the upper lip, see HeadTarget):
            // down by the shaft's whole thickness, less the little the upper lip lifts.
            if(wrapped)
                need = MathF.Max(0f, 2f * tall - UpperLipLift - gap) * Math.Clamp(s.JawOpen, 0f, 1.5f);
            float arm = Vector3.Distance(pivot.Position, l);
            if(need > 0f && arm > 1e-3f)
            {
                float angle = MathF.Min(MathF.Atan2(need, arm), 40f * MathF.PI / 180f) * blend;
                // Round the head's sideways axis, whichever way takes the lower lip away from the upper one.
                var side = Norm(Vector3.Cross(l - pivot.Position, u - l));
                var turn = Quaternion.CreateFromAxisAngle(side, angle);
                var moved = pivot.Position + Vector3.Transform(l - pivot.Position, turn);
                if(Vector3.Distance(moved, u) < gap)
                    turn = Quaternion.CreateFromAxisAngle(side, -angle);
                giver.SetPartialRotation(part, jaw, Quaternion.Normalize(turn * pivot.Rotation));
                _jawGap = arm * MathF.Sin(angle) / MathF.Max(0.01f, scale);
            }
        }

        // Lips: a round ring round the shaft. Each lip bone gets its own place on the circle: the corners at the
        // sides, the upper lip's bones spread evenly over the top half (left to right, as they are on the face) and
        // the lower lip's over the bottom, so the opening is a circle, not the stretched oval the bones would make
        // on their own. A little way out from the shaft (the lip's own thickness), pushed slightly forward (lips
        // pursed round it) and pulled along by the stroke (grip).
        // With the face's own open-mouth shapes on (the lips' live mesh), those shape the lips: the lip bones stay out.
        bool shaped = s.LipMesh && Limber.Gpu.LipMesh.HasShapes && Limber.Gpu.LipMesh.Owner == _giver;
        float fit = shaped ? 0f : Math.Clamp(s.LipFit, 0f, 1f) * blend * near;
        float drag = Math.Clamp(_depthSpeed * shaft.Length * 0.06f * Math.Clamp(s.LipGrip, 0f, 1f), -0.007f, 0.007f) * scale;
        if(fit > 0f)
        {
            // The mouth's centre again now the jaw is open (across the shaft only).
            var opened = Vector3.Zero;
            foreach(var b in lips)
                opened += giver.GetPartialFrame(part, b).Position;
            opened = opened / lips.Count - axisPoint;
            centre = axisPoint + opened - axis * Vector3.Dot(opened, axis);
            // The mouth's own up (upper lip over lower) and right, across the shaft.
            var up = upper >= 0 && lower >= 0
                ? giver.GetPartialFrame(part, upper).Position - giver.GetPartialFrame(part, lower).Position
                : Vector3.UnitY;
            up -= axis * Vector3.Dot(up, axis);
            up = up.LengthSquared() > 1e-10f ? Vector3.Normalize(up) : Norm(Vector3.UnitY - axis * axis.Y);
            var right = Norm(Vector3.Cross(up, axis));

            var corners = new List<(int Bone, float X)>();
            var tops = new List<(int Bone, float X)>();
            var bottoms = new List<(int Bone, float X)>();
            foreach(var b in lips)
            {
                var v = giver.GetPartialFrame(part, b).Position - centre;
                float x = Vector3.Dot(v, right), y = Vector3.Dot(v, up);
                string name = info.Names[b];
                if(name.StartsWith("j_f_lip", StringComparison.Ordinal))
                    corners.Add((b, x));
                else if(name.StartsWith("j_f_u", StringComparison.Ordinal) || (!name.StartsWith("j_f_d", StringComparison.Ordinal) && y >= 0f))
                    tops.Add((b, x));
                else
                    bottoms.Add((b, x));
            }
            tops.Sort((m1, m2) => m1.X.CompareTo(m2.X));
            bottoms.Sort((m1, m2) => m1.X.CompareTo(m2.X));
            MouthBones = $"Corners: {Names(corners)}\nUpper lip: {Names(tops)}\nLower lip: {Names(bottoms)}"
                + (jaw >= 0 ? "\nJaw: j_f_ago" : "\nJaw: (not found)");
            string Names(List<(int Bone, float X)> list) => list.Count == 0 ? "(none)" : string.Join(", ", list.ConvertAll(e => info.Names[e.Bone]));
            float thick = Math.Clamp(s.LipThickness, 0.5f, 2.5f);
            // Thicker lips: each lip bone bigger, and the ring out by the extra so the inside still meets the shaft.
            float ring = MathF.Max(0.002f, radius + lipDepth * scale * thick + s.Tightness * scale);
            float pout = Math.Clamp(s.Pout, 0f, 1f);
            float pursed = (0.002f + 0.008f * pout) * scale * fit;
            float roll = pout * 35f * MathF.PI / 180f * fit;
            float maxMove = 0.012f * scale;
            float roundness = Math.Clamp(s.Roundness, 0f, 1f), width = Math.Clamp(s.MouthWidth, 0.5f, 2f);
            var shape = s.LipShape;
            var dots = new List<LipDot>(lips.Count);
            void Place(int b, float angle, float grip)
            {
                var at = giver.GetPartialFrame(part, b).Position;
                float along = Vector3.Dot(at - centre, axis);
                string name = info.Names[b];
                Vector3 around;
                if(shape != null && shape.TryGetValue(name, out var mine) && mine is { Length: >= 2 })
                {
                    // Placed by hand in the mouth shape editor (x across, y up; 1 = the shaft's edge).
                    around = right * (mine[0] * width) + up * mine[1];
                    dots.Add(new LipDot(name, mine[0], mine[1], true));
                }
                else
                {
                    dots.Add(new LipDot(name, MathF.Cos(angle), MathF.Sin(angle), false));
                    // Round: the bone's own place on the circle; less round: nearer the angle it has on the face.
                    var off = at - centre;
                    float natural = MathF.Atan2(Vector3.Dot(off, up), Vector3.Dot(off, right));
                    float turn = MathF.IEEERemainder(angle - natural, MathF.PI * 2f);
                    angle = natural + turn * roundness;
                    around = right * (MathF.Cos(angle) * width) + up * MathF.Sin(angle);
                }
                var target = centre + around * ring + axis * (along * (1f - fit) + drag * grip - pursed);
                // Never dragged far (a lip bone far off would pull the chin or cheek into a spike); this bone only,
                // so nothing hanging from it (chin, cheek) is pulled along.
                var move = (target - at) * fit;
                if(move.Length() > maxMove)
                    move *= maxMove / move.Length();
                giver.SetPartialPosition(part, b, at + move, children: false);
                var frame = giver.GetPartialFrame(part, b);
                // Pout: each lip rolled outward round the ring, so its inside (the lipstick) shows all the way round.
                if(roll > 0f)
                {
                    var rollAxis = Vector3.Cross(Norm(around), axis);
                    if(rollAxis.LengthSquared() > 1e-8f)
                        giver.SetPartialRotation(part, b, Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.Normalize(rollAxis), roll) * frame.Rotation), children: false);
                }
                if(MathF.Abs(thick - 1f) > 1e-3f)
                    giver.SetPartialScale(part, b, giver.GetPartialScale(part, b) * (1f + (thick - 1f) * blend), children: false);
            }
            // The corners tucked in snug against the shaft (no gap or point showing from the side).
            float ringBefore = ring;
            ring = ringBefore * (1f - 0.12f * Math.Clamp(s.CornerTuck, 0f, 1f));
            foreach(var (b, x) in corners)
                Place(b, x >= 0f ? 0f : MathF.PI, 0.8f);
            ring = ringBefore;
            for(int i = 0; i < tops.Count; i++)
            {
                float t = (i + 1f) / (tops.Count + 1f);
                Place(tops[i].Bone, MathF.PI * (1f - t), 1f - 0.5f * MathF.Abs(2f * t - 1f));
            }
            for(int i = 0; i < bottoms.Count; i++)
            {
                float t = (i + 1f) / (bottoms.Count + 1f);
                Place(bottoms[i].Bone, MathF.PI * (1f + t), 1f - 0.5f * MathF.Abs(2f * t - 1f));
            }
            LipDots = dots.ToArray();
        }

        // Teeth out of sight: shrunk away into the mouth (the shaft would go right through them).
        if(s.HideTeeth)
        {
            for(int b = 0; b < info.Count; b++)
            {
                if(!info.Names[b].StartsWith("j_f_haguki", StringComparison.Ordinal))
                    continue;
                var frame = giver.GetPartialFrame(part, b);
                // (this bone only: the lower lip can hang from the lower teeth, and must stay where it is)
                giver.SetPartialPosition(part, b, frame.Position + axis * 0.012f * scale * blend, children: false);
                giver.SetPartialScale(part, b, giver.GetPartialScale(part, b) * (1f - 0.97f * blend), children: false);
            }
        }

        // Cheeks drawn in on the way out.
        float suck = Math.Clamp(s.Suction, 0f, 1f) * blend * Math.Clamp(_depthSpeed * 4f, -1f, 0f) * -1f;
        if(suck > 0f)
        {
            for(int b = 0; b < info.Count; b++)
            {
                if(!info.Names[b].StartsWith("j_f_hoho", StringComparison.Ordinal))
                    continue;
                var at = giver.GetPartialFrame(part, b).Position;
                var v = at - axisPoint;
                var radial = v - axis * Vector3.Dot(v, axis);
                if(radial.LengthSquared() < 1e-10f)
                    continue;
                giver.SetPartialPosition(part, b, at - Vector3.Normalize(radial) * 0.004f * scale * suck);
            }
        }

        HandLips(giver, shaft, s, blend);
    }

    // ---- Bench export: one moment of the scene, for testing the lips offline

    /// <summary>
    /// Saves what the lips work from right now into the plugin's own folder (Bench/&lt;time&gt;): the face model file,
    /// the face bones' rest and posed matrices, the shaft (line and thickness), the partner's model files that hold
    /// the shaft with its bones' rest and posed matrices, and the settings. Game paths only (no folders, no names).
    /// Call on the framework thread (from the window), with Oral running.
    /// </summary>
    public string ExportBench()
    {
        try
        {
            if(!_running)
                return "Start Oral first.";
            var cache = new SkeletonInfoCache();
            if(!ActorSkeleton.TryGet(_giver, _giverIndex, cache, out var giver))
                return "Can't read the character's skeleton.";
            ActorSkeleton.TryGet(_receiver, _receiverIndex, cache, out var receiver);
            var face = Limber.Gpu.LipMesh.FaceModel;
            var frame = Limber.Gpu.LipMesh.LastFrame;
            var bones = Limber.Gpu.LipMesh.Bones;
            if(face == null || frame == null || bones == null)
                return "The lips' live mesh isn't running (turn on \"Lips wrap the shaft\").";
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            var dir = System.IO.Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "Bench", stamp);
            System.IO.Directory.CreateDirectory(dir);

            static float[] M(Matrix4x4 m) => [m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44];
            static float[] V(Vector3 v) => [v.X, v.Y, v.Z];
            static string GamePath(string file)
            {
                var f = file.Replace('\\', '/');
                int at = f.IndexOf("chara/", StringComparison.OrdinalIgnoreCase);
                return at >= 0 ? f[at..] : System.IO.Path.GetFileName(f);
            }
            Matrix4x4 Posed(ActorSkeleton sk, string name)
            {
                Frame f;
                Vector3 size;
                if(sk.TryFindExtraBone(name, cache, out int part, out int pb))
                {
                    f = sk.GetPartialFrame(part, pb);
                    size = sk.GetPartialScale(part, pb);
                }
                else if(sk.Info.IndexOf(name) is int body and >= 0)
                {
                    f = sk.GetModelFrame(body);
                    size = sk.GetModelScale(body);
                }
                else
                    return Matrix4x4.Identity;
                return Matrix4x4.CreateScale(size) * Matrix4x4.CreateFromQuaternion(f.Rotation) * Matrix4x4.CreateTranslation(f.Position);
            }

            System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, "face.mdl"), face.Value.Data);
            var faceBones = new Dictionary<string, object>();
            foreach(var name in bones)
                faceBones[name] = new { rest = M(RestMatrix(giver, name)), posed = M(Posed(giver, name)) };

            // The partner: the model files that hold the shaft, and every bone they use.
            var partner = new List<object>();
            var partnerBones = new Dictionary<string, object>();
            if(receiver.Info != null && receiver.Info.Count > 0)
            {
                var drawObject = (nint)((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)_receiver)->DrawObject;
                int index = 0;
                foreach(var (slot, file) in Limber.Gpu.LiveMesh.ModelFiles(drawObject))
                {
                    var data = ReadFile(file);
                    if(data == null)
                        continue;
                    List<(Vector3 Position, string[] Bones, float[] Weights)> found;
                    try
                    {
                        found = Limber.BodyRig.MdlRigger.WeightedVertices(data, n => n.StartsWith("iv_ochinko_", StringComparison.Ordinal), 0.3f);
                    }
                    catch
                    {
                        continue;
                    }
                    if(found.Count == 0)
                        continue;
                    string name = $"partner{index++}.mdl";
                    System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, name), data);
                    partner.Add(new { file = name, gamePath = GamePath(file), slot, body = Limber.BodyRig.Deformers.BodyOf(file) });
                    foreach(var (_, names, _) in found)
                        foreach(var b in names)
                            if(!partnerBones.ContainsKey(b))
                                partnerBones[b] = new { rest = M(RestMatrix(receiver, b)), posed = M(Posed(receiver, b)) };
                }
            }

            var shaftLine = new List<float[]>();
            foreach(var v in frame.Shaft)
                shaftLine.Add(V(v));
            var s = _settings();
            // (the settings without who's in the scene)
            var settings = Newtonsoft.Json.Linq.JObject.FromObject(s);
            foreach(var key in new[] { "Giver", "GiverIndex", "Receiver", "ReceiverIndex" })
                settings.Remove(key);
            var doc = new
            {
                version = 1,
                face = new { file = "face.mdl", gamePath = GamePath(face.Value.File), bones = faceBones },
                giver = new { modelToWorld = M(giver.ModelToWorld), rootScale = V(giver.RootScale) },
                receiver = receiver.Info != null && receiver.Info.Count > 0
                    ? new { modelToWorld = M(receiver.ModelToWorld), rootScale = V(receiver.RootScale), body = BodyId(_receiver) }
                    : null,
                partner,
                partnerBones,
                shaft = new { line = shaftLine, radius = frame.Radius, measured = s.MeasureShaft },
                frame = new { seal = frame.Seal, forward = frame.Forward, openWidth = frame.OpenWidth, openHeight = frame.OpenHeight, cheekPuff = frame.CheekPuff, jawGap = frame.JawGap },
                settings,
                status = Limber.Gpu.LipMesh.Status,
            };
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "frame.json"),
                Newtonsoft.Json.JsonConvert.SerializeObject(doc, Newtonsoft.Json.Formatting.Indented));
            return $"Saved for the bench: Bench/{stamp} (in Limber's plugin folder).";
        }
        catch(Exception e)
        {
            return "Couldn't save: " + e.Message;
        }
    }

    // ---- The lips' live mesh (Limber.Gpu.LipMesh): this frame's face matrices and the shaft, for the render thread

    private string[]? _lipRestFor;
    private Matrix4x4[] _lipRestInverse = [];
    private float _jawGap; // how far the jaw bone drops the lower lip this frame (model metres)
    private float _bigness; // 0..1: how big the shaft is for the mouth (eased)
    private const float UpperLipLift = 0.004f; // how far the upper lip lifts round the shaft (model metres)
    private long _nextLipTry;
    private string _lipTried = ""; // the face model the lips' live mesh last tried to start on

    private void HandLips(in ActorSkeleton giver, in Shaft shaft, OralSettings s, float blend)
    {
        var bones = Limber.Gpu.LipMesh.Bones;
        if(bones == null || Limber.Gpu.LipMesh.Owner != _giver || !s.LipMesh)
            return;
        if(!ReferenceEquals(_lipRestFor, bones))
        {
            _lipRestInverse = new Matrix4x4[bones.Length];
            for(int i = 0; i < bones.Length; i++)
                _lipRestInverse[i] = Matrix4x4.Invert(RestMatrix(giver, bones[i]), out var inv) ? inv : Matrix4x4.Identity;
            _lipRestFor = bones;
        }
        var skinning = new Matrix4x4[bones.Length];
        for(int i = 0; i < bones.Length; i++)
        {
            Frame frame;
            Vector3 size;
            if(giver.TryFindExtraBone(bones[i], _cache, out int p, out int b))
            {
                frame = giver.GetPartialFrame(p, b);
                size = giver.GetPartialScale(p, b);
            }
            else if(giver.Info.IndexOf(bones[i]) is int body and >= 0)
            {
                frame = giver.GetModelFrame(body);
                size = giver.GetModelScale(body);
            }
            else
            {
                skinning[i] = Matrix4x4.Identity;
                continue;
            }
            var now = Matrix4x4.CreateScale(size) * Matrix4x4.CreateFromQuaternion(frame.Rotation) * Matrix4x4.CreateTranslation(frame.Position);
            skinning[i] = _lipRestInverse[i] * now;
        }
        float unit = 1f / MathF.Max(0.01f, giver.RootScale.Y);
        var line = new Vector3[shaft.Points.Length];
        var radii = new float[shaft.Points.Length];
        float fallback = MathF.Max(0.004f, s.Thickness);
        for(int i = 0; i < line.Length; i++)
        {
            line[i] = giver.WorldToModelPoint(shaft.Points[i]);
            radii[i] = shaft.Radii != null ? shaft.Radii[i] * unit : fallback * (i == line.Length - 1 ? 0.5f : 1f);
            radii[i] = MathF.Max(0.002f, radii[i] + s.Tightness);
        }
        Limber.Gpu.LipMesh.Hand(new Limber.Gpu.LipMesh.Frame
        {
            Skinning = skinning,
            Shaft = line,
            Radius = radii,
            Seal = Math.Clamp(s.LipSeal, 0f, 1f) * blend,
            Firmness = Math.Clamp(s.LipFirmness, 0f, 1f),
            Forward = (0.001f + 0.005f * Math.Clamp(s.Pout, 0f, 1.5f)) * blend,
            OpenWidth = Math.Clamp(s.OpenSize, 0.5f, 2f) * Math.Clamp(s.OpenWidth, 0.5f, 2f),
            OpenHeight = Math.Clamp(s.OpenSize, 0.5f, 2f) * Math.Clamp(s.OpenHeight, 0.5f, 2f),
            JawGap = _jawGap,
            CheekPuff = 0.009f * _bigness * Math.Clamp(s.Cheeks, 0f, 1.5f) * blend,
            // the lips held by the shaft: pulled out with it on the way out, tucked in on the way in
            Drag = -Math.Clamp(_depthSpeed * shaft.Length * 0.06f * Math.Clamp(s.LipGrip, 0f, 1f), -0.007f, 0.007f) * blend,
        });
        _lipsHanded = true;
    }

    // A bone's rest-pose matrix in model space: a body bone from the body's reference pose; a face (or other extra
    // skeleton) bone from that skeleton's reference pose, hung from the body's copy of its root bone.
    public static Matrix4x4 RestMatrix(ActorSkeleton sk, string name)
    {
        static Matrix4x4 Local(FFXIVClientStructs.Havok.Common.Base.Math.QsTransform.hkQsTransformf t)
        {
            var position = *(Vector3*)&t.Translation;
            var rotation = Quaternion.Normalize(*(Quaternion*)&t.Rotation);
            var scale = *(Vector3*)&t.Scale;
            return Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(position);
        }
        Matrix4x4 Body(int bone)
        {
            var reference = sk.Pose->Skeleton->ReferencePose;
            var m = Matrix4x4.Identity;
            var chain = new List<int>();
            for(int b = bone; b >= 0; b = sk.Info.ParentOf(b))
                chain.Add(b);
            for(int i = chain.Count - 1; i >= 0; i--)
                if(chain[i] < reference.Length)
                    m = Local(reference[chain[i]]) * m;
            return m;
        }
        int index = sk.Info.IndexOf(name);
        if(index >= 0 && !ActorSkeleton.IsSkeletonModBone(name))
            return Body(index);
        for(int p = 1; p < sk.PartialCount; p++)
        {
            var pose = sk.PartialPose(p);
            var info = pose == null ? null : sk.PartialInfo(p, new SkeletonInfoCache());
            int bone = info?.IndexOf(name) ?? -1;
            if(bone < 0 || pose->Skeleton == null)
                continue;
            var reference = pose->Skeleton->ReferencePose;
            var chain = new List<int>();
            for(int b = bone; b >= 0; b = info!.ParentOf(b))
                chain.Add(b);
            // The root: where the body's copy of it rests (or the body bone this skeleton hangs from).
            int root = chain[^1];
            int bodyRoot = sk.Info.IndexOf(info!.Names[root]);
            if(bodyRoot < 0)
                bodyRoot = sk.PartialParentBone(p);
            var m = bodyRoot >= 0 ? Body(bodyRoot) : (root < reference.Length ? Local(reference[root]) : Matrix4x4.Identity);
            for(int i = chain.Count - 2; i >= 0; i--)
                if(chain[i] < reference.Length)
                    m = Local(reference[chain[i]]) * m;
            return m;
        }
        return index >= 0 ? Body(index) : Matrix4x4.Identity;
    }

    /// <summary>Render thread: start / stop the lips' live mesh with Oral, and run it.</summary>
    public void UpdateLipMesh()
    {
        var s = _settings();
        bool want = s.LipMesh && _running && _blend > 0f;
        if(!want)
        {
            if(Limber.Gpu.LipMesh.Running)
                Limber.Gpu.LipMesh.Stop();
            _lipTried = "";
            return;
        }
        if(Limber.Gpu.LipMesh.Owner != _giver)
        {
            // Starting means searching the face model's memory (slow): once per face model, not over and over.
            long now = Environment.TickCount64;
            if(now >= _nextLipTry)
            {
                _nextLipTry = now + 1000;
                var drawObject = (nint)((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)_giver)->DrawObject;
                string face = "";
                foreach(var (_, file) in Limber.Gpu.LiveMesh.ModelFiles(drawObject))
                    if(Limber.Gpu.LipMesh.IsFace(file))
                        face = file;
                string key = _giver + "|" + face;
                if(drawObject != 0 && face.Length > 0 && key != _lipTried)
                {
                    _lipTried = key;
                    Limber.Gpu.LipMesh.Start(_giver, drawObject, ReadFile);
                }
            }
        }
        else
            _lipTried = "";
        Limber.Gpu.LipMesh.Update();
    }
}
