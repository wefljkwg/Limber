using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Dalamud.Plugin.Services;
using Limber.Core;
using Limber.UI;

namespace Limber.Rig;

/// <summary>The partner reactions on the Size &amp; reactions page.</summary>
public enum ReactionKind
{
    Smirk,
    Giggle,
    EyeRoll,
    ThatsIt,
    ThisSmall,
    PinkyWiggle,
    PointAndLaugh,
}

/// <summary>Small looping motions on top of the pose.</summary>
public enum ReactionMotionKind
{
    Giggle,
    Laugh,
    HeadShake,
    PinkyWiggle,
    FingerFlick,
}

/// <summary>One character's looping reaction motion (kept by name).</summary>
public sealed class ReactionMotionSettings
{
    public bool Enabled { get; set; }
    public ReactionMotionKind Kind { get; set; } = ReactionMotionKind.Giggle;
    /// <summary>The hand for finger motions (right by default).</summary>
    public bool RightHand { get; set; } = true;
    public float Speed { get; set; } = 1f;
    public float Strength { get; set; } = 1f;
    public float Ease { get; set; } = 0.4f;

    public ReactionMotionSettings Copy() => (ReactionMotionSettings)MemberwiseClone();
}

/// <summary>
/// The adult check for the Size &amp; reactions page: an adult body (body type 1) of a race other than Lalafell,
/// read from the character itself; refused when it can't be read. (Limber's original rule, kept here on its own so
/// this page never works on a child body whatever the rest of Limber is set to.)
/// </summary>
public static unsafe class ReactionSafety
{
    public static bool Adult(nint character)
    {
        if(character == 0)
            return false;
        try
        {
            var c = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)character;
            if(!((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)c)->IsCharacter())
                return false;
            var customize = c->DrawData.CustomizeData;
            return customize.Race != 3 && customize.BodyType == 1;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Looping reaction motions: a giggle (quick shoulder and chest shakes in bursts), a laugh (bigger, the head going
/// back), a slow "no" head shake, a wiggling little finger and an index finger flicking. Small turns on top of the pose
/// (and on top of IK / FK and hand presets), eased in and out. Bone update, after posing. Adult characters only.
/// Only on your screen.
/// </summary>
public sealed unsafe class ReactionMotionService
{
    private sealed class State
    {
        public float Level;
        public double Time;
        public long LastTicks;
        public int[]? Bones;
        public Quaternion[]? Before;
        public Quaternion Written;
        public bool Wrote;
    }

    private readonly IObjectTable _objects;
    private readonly Func<Dictionary<string, ReactionMotionSettings>> _settings;
    private readonly SkeletonInfoCache _cache = new();
    private readonly Dictionary<nint, State> _states = new();
    private volatile (nint Address, ushort Index, ReactionMotionSettings Settings)[] _targets = [];
    public Func<nint, bool> Skip = _ => false;

    public ReactionMotionService(IObjectTable objects, Func<Dictionary<string, ReactionMotionSettings>> settings)
    {
        _objects = objects;
        _settings = settings;
    }

    public bool IsRunning(nint address) => _states.TryGetValue(address, out var s) && s.Level > 0f;

    /// <summary>Framework thread, once a frame.</summary>
    public void Refresh()
    {
        var all = _settings();
        if(all.Count == 0)
        {
            _targets = [];
            return;
        }
        var list = new List<(nint, ushort, ReactionMotionSettings)>();
        foreach(var (obj, objName) in FrameObjects.Of(_objects))
        {
            if(obj == null || obj.Address == 0 || objName.Length == 0 || !all.TryGetValue(objName, out var s))
                continue;
            if(!ReactionSafety.Adult(obj.Address))
                continue;
            list.Add((obj.Address, obj.ObjectIndex, s.Copy()));
        }
        _targets = list.ToArray();
    }

    public void Clear() => _targets = [];

    /// <summary>Bone update stage.</summary>
    public void UpdateFrame()
    {
        var targets = _targets;
        long now = Stopwatch.GetTimestamp();
        var seen = new HashSet<nint>();
        foreach(var (address, index, s) in targets)
        {
            seen.Add(address);
            try
            {
                if(!_states.TryGetValue(address, out var state))
                {
                    if(!s.Enabled)
                        continue;
                    _states[address] = state = new State { LastTicks = now };
                }
                if(Skip(address))
                    continue;
                Step(address, index, s, state, now);
                if(state.Level <= 0f && !s.Enabled)
                    _states.Remove(address);
            }
            catch
            {
                _states.Remove(address);
            }
        }
        if(_states.Count > seen.Count)
            foreach(var a in new List<nint>(_states.Keys))
                if(!seen.Contains(a))
                    _states.Remove(a);
    }

    // ---- The motions: pure functions of time

    private static float Smooth(float u)
    {
        u = Math.Clamp(u, 0f, 1f);
        return u * u * (3f - 2f * u);
    }

    // Bursts: on for `on` seconds out of every `every`, eased at both ends (0..1).
    private static float Burst(double t, double on, double every)
    {
        double u = t % every;
        if(u > on)
            return 0f;
        float edge = 0.25f;
        float a = Smooth((float)(u / edge)), b = Smooth((float)((on - u) / edge));
        return MathF.Min(a, b);
    }

    private static Quaternion Turn(Vector3 axis, float degrees) => Quaternion.CreateFromAxisAngle(axis, degrees * MathF.PI / 180f);

    private static string[] BonesOf(ReactionMotionKind kind, bool right)
    {
        string s = right ? "_r" : "_l";
        return kind switch
        {
            ReactionMotionKind.Giggle or ReactionMotionKind.Laugh =>
                ["j_sebo_b", "j_sebo_c", "j_sako_l", "j_sako_r", "j_kubi", "j_kao"],
            ReactionMotionKind.HeadShake => ["j_kubi", "j_kao"],
            ReactionMotionKind.PinkyWiggle => ["j_ko_a" + s, "j_ko_b" + s],
            ReactionMotionKind.FingerFlick => ["j_hito_a" + s, "j_hito_b" + s],
            _ => [],
        };
    }

    private void Step(nint address, ushort index, ReactionMotionSettings s, State state, long now)
    {
        float dt = Math.Clamp((float)((now - state.LastTicks) / (double)Stopwatch.Frequency), 0f, 0.1f);
        state.LastTicks = now;
        state.Level = Math.Clamp(state.Level + (s.Enabled ? dt : -dt) / MathF.Max(0.05f, s.Ease), 0f, 1f);
        state.Time += dt * Math.Clamp(s.Speed, 0.2f, 3f);
        if(!ActorSkeleton.TryGet(address, index, _cache, out var sk) || sk.Info.Count == 0)
            return;

        var names = BonesOf(s.Kind, s.RightHand);
        var bones = new List<int>();
        foreach(var n in names)
        {
            int b = sk.Info.IndexOf(n);
            if(b >= 0)
                bones.Add(b);
        }
        if(bones.Count == 0)
            return;

        // The pose wasn't rebuilt since last frame (paused): put back what it had, so turns don't pile up.
        if(state.Wrote && state.Bones != null && state.Before != null && state.Bones.Length > 0
           && MathF.Abs(Quaternion.Dot(sk.GetModelFrame(state.Bones[0]).Rotation, state.Written)) > 0.9999999f)
            for(int i = 0; i < state.Bones.Length; i++)
                sk.SetModelRotation(state.Bones[i], state.Before[i]);
        state.Wrote = false;

        var before = new Quaternion[bones.Count];
        for(int i = 0; i < bones.Count; i++)
            before[i] = sk.GetModelFrame(bones[i]).Rotation;
        state.Bones = bones.ToArray();
        state.Before = before;

        float level = Smooth(state.Level) * Math.Clamp(s.Strength, 0f, 3f);
        if(level <= 0f)
            return;
        double t = state.Time;
        const float Tau = MathF.PI * 2f;

        // Model space: Y up, Z the way the character faces, X its left. (+ about X = forward / chin down.)
        void World(string name, Quaternion turn)
        {
            int b = sk.Info.IndexOf(name);
            if(b < 0)
                return;
            var cur = sk.GetModelFrame(b).Rotation;
            sk.SetModelRotation(b, Quaternion.Normalize(turn * cur));
        }
        // Finger curl: about the bone's own Z (the axis the hand presets curl about).
        void Curl(string name, float degrees)
        {
            int b = sk.Info.IndexOf(name);
            if(b < 0)
                return;
            var cur = sk.GetModelFrame(b).Rotation;
            sk.SetModelRotation(b, Quaternion.Normalize(cur * Turn(Vector3.UnitZ, -degrees)));
        }

        switch(s.Kind)
        {
            case ReactionMotionKind.Giggle:
            {
                // Quick little shakes in bursts: shoulders hop, the chest bobs, the head nods along.
                float burst = Burst(t, 1.6, 2.6) * level;
                float shake = MathF.Sin((float)(t * 7.0) * Tau);
                float hop = MathF.Max(0f, shake);
                World("j_sebo_b", Turn(Vector3.UnitX, 1.2f * shake * burst));
                World("j_sebo_c", Turn(Vector3.UnitX, 1.2f * shake * burst));
                World("j_sako_l", Turn(Vector3.UnitZ, 3.5f * hop * burst));
                World("j_sako_r", Turn(Vector3.UnitZ, -3.5f * hop * burst));
                World("j_kao", Turn(Vector3.UnitX, 2.5f * shake * burst));
                break;
            }
            case ReactionMotionKind.Laugh:
            {
                // Bigger: leaning back with the head up, the chest and shoulders shaking hard.
                float burst = Burst(t, 2.4, 3.4) * level;
                float shake = MathF.Sin((float)(t * 5.0) * Tau);
                float hop = MathF.Max(0f, shake);
                World("j_sebo_b", Turn(Vector3.UnitX, (-3f + 2f * shake) * burst));
                World("j_sebo_c", Turn(Vector3.UnitX, (-3f + 2f * shake) * burst));
                World("j_sako_l", Turn(Vector3.UnitZ, 5f * hop * burst));
                World("j_sako_r", Turn(Vector3.UnitZ, -5f * hop * burst));
                World("j_kubi", Turn(Vector3.UnitX, -4f * burst));
                World("j_kao", Turn(Vector3.UnitX, (-7f + 3f * shake) * burst));
                break;
            }
            case ReactionMotionKind.HeadShake:
            {
                // A slow, disappointed "no", now and then.
                float burst = Burst(t, 1.5, 4.0) * level;
                float side = MathF.Sin((float)(t * 1.7) * Tau);
                World("j_kubi", Turn(Vector3.UnitY, 3f * side * burst));
                World("j_kao", Turn(Vector3.UnitY, 8f * side * burst));
                break;
            }
            case ReactionMotionKind.PinkyWiggle:
            {
                // The raised little finger bending and straightening.
                string h = s.RightHand ? "_r" : "_l";
                float wig = 0.5f + 0.5f * MathF.Sin((float)(t * 3.0) * Tau);
                Curl("j_ko_a" + h, 28f * wig * level);
                Curl("j_ko_b" + h, 22f * wig * level);
                break;
            }
            case ReactionMotionKind.FingerFlick:
            {
                // The index finger curls in slowly, then flicks out fast (once every 1.2 s).
                string h = s.RightHand ? "_r" : "_l";
                double u = t % 1.2;
                float curl = u < 0.5 ? Smooth((float)(u / 0.5)) : u < 0.56 ? 1f - Smooth((float)((u - 0.5) / 0.06)) * 1.12f : -0.12f * (1f - Smooth((float)((u - 0.56) / 0.3)));
                Curl("j_hito_a" + h, 45f * curl * level);
                Curl("j_hito_b" + h, 40f * curl * level);
                break;
            }
        }
        state.Written = sk.GetModelFrame(bones[0]).Rotation;
        state.Wrote = true;
    }
}

/// <summary>
/// One-click partner reactions (Size &amp; reactions page): a face (made with the ahegao face's controls, no tongue or
/// rolled-up eyes unless the reaction has them), a hand gesture, where the hand goes (over the mouth, held up by the
/// face, pointing), who they look at, and a looping motion. Everything it changes is kept first and put back by
/// Clear. UI thread. Adult characters only.
/// </summary>
public static unsafe class Reactions
{
    public static readonly (ReactionKind Kind, string Label, string Help)[] All =
    [
        (ReactionKind.Smirk, "Smirk", "A smug half-smile, eyes half-lidded, looking down at it."),
        (ReactionKind.Giggle, "Giggle", "A hand over the mouth, eyes squeezed in a smile, shoulders shaking with little giggles."),
        (ReactionKind.EyeRoll, "Eye roll", "Eyes rolled up, head tipped, a flat mouth and a slow head shake."),
        (ReactionKind.ThatsIt, "That's it?", "Brows up, head cocked, staring down at it unimpressed."),
        (ReactionKind.ThisSmall, "This small", "Thumb and finger held up with a tiny gap, looking them in the eye with a smirk."),
        (ReactionKind.PinkyWiggle, "Pinky wiggle", "A little finger held up by the face and wiggled, grinning."),
        (ReactionKind.PointAndLaugh, "Point and laugh", "Pointing right at it, laughing with the head back."),
    ];

    // What was there before (put back by Clear), per character name.
    private sealed class Saved
    {
        public AhegaoSettings? Face;
        public LookAtSettings? Look;
        public ReactionMotionSettings? Motion;
        public bool Right;
        public bool Arm;
        public bool Fingers;
    }
    private static readonly Dictionary<string, Saved> Before = new();

    public static bool IsOn(string name) => Before.ContainsKey(name);

    private static AhegaoSettings Neutral() => new()
    {
        Enabled = true,
        EyesUp = 0f, CrossEyes = 0f, Lids = 0f, LowerLids = 0f, Brows = 0f, BrowTilt = 0f, Worried = 0f, EyesWide = 0f,
        PupilSize = 0f, JawDown = 0f, JawForward = 0f, Furrow = 0f, MouthOpen = 0f, TongueOut = 0f, TongueLength = 0f,
        TongueDroop = 0f, UpperLip = 0f, LowerLip = 0f, TonguePhysics = false, HoldFace = true, FaceRig = false,
        TongueUp = 0f, Wiggle = 0f, Smile = 0f, CornerWide = 0f, SmileLower = 0f, CornerWideLower = 0f,
        CornerDepthLower = 0f, UpperLipWide = 0f, LowerLipWide = 0f, CornerDepth = 0f, HeadTilt = 0f, HeadSide = 0f,
        Pant = 0f, Tremble = 0f, Ease = 0.5f,
    };

    private static AhegaoSettings FaceFor(ReactionKind kind)
    {
        var f = Neutral();
        switch(kind)
        {
            case ReactionKind.Smirk:
                f.Lids = 0.35f; f.LowerLids = 0.3f; f.Brows = 0.1f; f.BrowTilt = -0.25f;
                f.Smile = 0.55f; f.SmileLower = 0.35f; f.CornerWide = 0.25f; f.CornerDepth = -0.2f;
                f.HeadTilt = -6f; f.HeadSide = 7f;
                break;
            case ReactionKind.Giggle:
                f.Lids = 0.35f; f.LowerLids = 0.75f; f.Brows = 0.35f; f.BrowTilt = 0.35f;
                f.MouthOpen = 6f; f.Smile = 0.95f; f.SmileLower = 0.8f; f.CornerWide = 0.3f;
                f.HeadTilt = -4f; f.HeadSide = 6f;
                break;
            case ReactionKind.EyeRoll:
                f.EyesUp = 30f; f.Lids = 0.3f; f.Brows = 0.45f; f.BrowTilt = -0.2f;
                f.Smile = -0.3f; f.SmileLower = -0.3f; f.CornerDepth = -0.3f;
                f.HeadTilt = 5f; f.HeadSide = -8f;
                break;
            case ReactionKind.ThatsIt:
                f.EyesWide = 0.25f; f.Brows = 0.75f; f.BrowTilt = 0.15f; f.Lids = 0.05f;
                f.MouthOpen = 4f; f.Smile = -0.2f; f.SmileLower = -0.25f;
                f.HeadTilt = -8f; f.HeadSide = 11f;
                break;
            case ReactionKind.ThisSmall:
                f.Lids = 0.25f; f.LowerLids = 0.35f; f.Brows = 0.45f; f.BrowTilt = -0.15f;
                f.Smile = 0.65f; f.SmileLower = 0.45f; f.CornerWide = 0.25f;
                f.HeadTilt = -3f; f.HeadSide = 8f;
                break;
            case ReactionKind.PinkyWiggle:
                f.Lids = 0.2f; f.LowerLids = 0.55f; f.Brows = 0.3f; f.BrowTilt = 0.2f;
                f.MouthOpen = 5f; f.Smile = 0.9f; f.SmileLower = 0.7f; f.CornerWide = 0.3f;
                f.HeadTilt = -2f; f.HeadSide = 9f;
                break;
            case ReactionKind.PointAndLaugh:
                f.Lids = 0.4f; f.LowerLids = 0.8f; f.Brows = 0.4f; f.BrowTilt = 0.4f;
                f.MouthOpen = 20f; f.JawDown = 0.2f; f.Smile = 1f; f.SmileLower = 0.6f; f.CornerWide = 0.4f;
                f.HeadTilt = 10f;
                break;
        }
        return f;
    }

    /// <summary>The partner's bone looked at for their size: the shaft's middle, or the hips without one.</summary>
    public static string ShaftOrHips(nint partner, ushort index, SkeletonInfoCache cache)
        => ActorSkeleton.TryGet(partner, index, cache, out var sk) && sk.TryGetDrawnWorldFrame("iv_ochinko_c", cache, out _) ? "iv_ochinko_c" : "j_kosi";

    /// <summary>
    /// Puts the reaction on <paramref name="actor"/> (looking at <paramref name="partner"/>'s size). Returns what
    /// happened. <paramref name="gap"/> is the "This small" finger gap (0 touching .. 1 wide).
    /// </summary>
    public static string Apply(RigSettings config, RigService rigs, ActorRef actor, ActorRef partner, ReactionKind kind, bool right, float gap, bool withMotion)
    {
        string name = actor.Name ?? "";
        if(name.Length == 0)
            return "This character has no name to keep it under.";
        if(!ReactionSafety.Adult(actor.Address) || (partner.IsSet && !ReactionSafety.Adult(partner.Address)))
            return "Reactions are for adult characters only.";
        var cache = new SkeletonInfoCache();
        if(!ActorSkeleton.TryGet(actor.Address, actor.ObjectIndex, cache, out var self))
            return "This character's bones aren't available right now.";

        // Undo anything an earlier reaction did first, so the saved state is the real original.
        if(Before.ContainsKey(name))
            Clear(config, rigs, actor);

        var faces = config.Ahegao ??= new();
        var looks = config.LookAt ??= new();
        var motions = config.ReactionMotions ??= new();
        var saved = new Saved
        {
            Face = faces.TryGetValue(name, out var oldFace) ? oldFace.Copy() : null,
            Look = looks.TryGetValue(name, out var oldLook) ? oldLook.Copy() : null,
            Motion = motions.TryGetValue(name, out var oldMotion) ? oldMotion.Copy() : null,
            Right = right,
        };

        // Face.
        faces[name] = FaceFor(kind);

        // Look at: their size, or their eyes for "This small"; the eye roll only turns the head a little (the eyes
        // are rolled up).
        if(partner.IsSet && !string.IsNullOrEmpty(partner.Name) && partner.Address != actor.Address)
        {
            string bone = kind == ReactionKind.ThisSmall ? "j_kao" : ShaftOrHips(partner.Address, partner.ObjectIndex, cache);
            looks[name] = new LookAtSettings
            {
                Enabled = true,
                Target = partner.Name,
                TargetBone = bone,
                Head = kind == ReactionKind.EyeRoll ? 0.3f : 0.6f,
                Eyes = kind != ReactionKind.EyeRoll,
                Speed = 4f,
            };
        }

        // Motion.
        ReactionMotionKind? motion = kind switch
        {
            ReactionKind.Giggle => ReactionMotionKind.Giggle,
            ReactionKind.EyeRoll => ReactionMotionKind.HeadShake,
            ReactionKind.PinkyWiggle => ReactionMotionKind.PinkyWiggle,
            ReactionKind.PointAndLaugh => ReactionMotionKind.Laugh,
            _ => null,
        };
        if(withMotion && motion is { } m)
            motions[name] = new ReactionMotionSettings { Enabled = true, Kind = m, RightHand = right };
        else if(motions.TryGetValue(name, out var running))
            running.Enabled = false;

        // Hand: fingers and where it goes.
        HandPose? fingers = kind switch
        {
            ReactionKind.Giggle => Find("Relaxed"),
            ReactionKind.ThisSmall => HandPoses.ThisSmall(gap),
            ReactionKind.PinkyWiggle => Find("Pinky up"),
            ReactionKind.PointAndLaugh => Find("Point"),
            _ => null,
        };
        if(fingers != null)
        {
            lock(rigs.Sync)
            {
                rigs.Checkpoint();
                var rig = rigs.GetOrCreateRig(actor.Address, actor.ObjectIndex, name);
                HandPoses.Apply(rig, fingers, right, 1f, b => self.Info.IndexOf(b) >= 0 || self.TryGetDrawnWorldFrame(b, cache, out _));
                saved.Fingers = true;
                if(PlaceHand(rig, self, cache, actor, partner, kind, right))
                    saved.Arm = true;
            }
        }
        Before[name] = saved;
        config.Save();
        return $"{All[(int)kind].Label} on {name}.";
    }

    private static HandPose? Find(string name)
    {
        foreach(var p in HandPoses.All)
            if(p.Name == name)
                return p;
        return null;
    }

    // The hand (wrist) held over the mouth, up by the face, or pointing at the partner; it follows the body (or head).
    private static bool PlaceHand(ActorRig rig, ActorSkeleton self, SkeletonInfoCache cache, ActorRef actor, ActorRef partner, ReactionKind kind, bool right)
    {
        string side = right ? "_r" : "_l";
        if(!self.TryGetDrawnWorldFrame("j_kao", cache, out var head) || !self.TryGetDrawnWorldFrame("j_kubi", cache, out var neck)
           || !self.TryGetDrawnWorldFrame("j_kosi", cache, out var hips) || !self.TryGetDrawnWorldFrame("j_ude_a" + side, cache, out var shoulder)
           || !self.TryGetDrawnWorldFrame("j_ude_b" + side, cache, out var elbow) || !self.TryGetDrawnWorldFrame("j_te" + side, cache, out var wrist))
            return false;
        var fwd = Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, self.RootRotation));
        var outward = Vector3.Normalize(Vector3.Transform(right ? -Vector3.UnitX : Vector3.UnitX, self.RootRotation));
        var up = Vector3.UnitY;
        float sc = Math.Clamp(Vector3.Distance(hips.Position, neck.Position) / 0.55f, 0.4f, 2.5f);
        float arm = Vector3.Distance(shoulder.Position, elbow.Position) + Vector3.Distance(elbow.Position, wrist.Position);

        Vector3 target;
        string anchor;
        switch(kind)
        {
            case ReactionKind.Giggle:
            {
                // Fingers over the mouth: the wrist under and a little to the side of it.
                var mouth = self.TryGetDrawnWorldFrame("j_f_ulip_a", cache, out var lip) ? lip.Position : head.Position + fwd * 0.07f * sc + up * 0.02f * sc;
                target = mouth + fwd * 0.05f * sc - up * 0.09f * sc + outward * 0.03f * sc;
                anchor = "j_kao";
                break;
            }
            case ReactionKind.ThisSmall:
            case ReactionKind.PinkyWiggle:
                // Held up beside the face, out in front.
                target = neck.Position + fwd * 0.24f * sc + outward * 0.14f * sc + up * (kind == ReactionKind.PinkyWiggle ? 0.02f : -0.02f) * sc;
                anchor = "j_sebo_c";
                break;
            case ReactionKind.PointAndLaugh:
            {
                // Arm out toward their size (or straight ahead and down without a partner).
                Vector3 at = shoulder.Position + (fwd - up * 0.5f) * arm;
                if(partner.IsSet && ActorSkeleton.TryGet(partner.Address, partner.ObjectIndex, cache, out var other))
                {
                    string bone = ShaftOrHips(partner.Address, partner.ObjectIndex, cache);
                    if(other.TryGetDrawnWorldFrame(bone, cache, out var it))
                        at = it.Position;
                }
                var dir = at - shoulder.Position;
                if(dir.LengthSquared() < 1e-6f)
                    dir = fwd;
                target = shoulder.Position + Vector3.Normalize(dir) * arm * 0.88f;
                anchor = "j_sebo_c";
                break;
            }
            default:
                return false;
        }
        if(!MathUtil.IsFinite(target))
            return false;
        var goal = rig.GetOrAdd("j_te" + side);
        goal.Enabled = true;
        goal.Space = GoalSpace.Attached;
        goal.HoldPosition = true;
        goal.ParentActor = actor.Address;
        goal.ParentObjectIndex = actor.ObjectIndex;
        goal.ParentActorName = actor.Name ?? "";
        goal.ParentBone = anchor;
        goal.Mirror = MirrorMode.Off;
        goal.RequestCapture();
        goal.PendingWorldPosition = target;
        return true;
    }

    /// <summary>Takes the reaction off and puts back what the character had before it.</summary>
    public static string Clear(RigSettings config, RigService rigs, ActorRef actor)
    {
        string name = actor.Name ?? "";
        if(!Before.Remove(name, out var saved))
            return "No reaction on this character.";
        var faces = config.Ahegao ??= new();
        if(saved.Face != null)
            faces[name] = saved.Face;
        else
            faces.Remove(name);
        var looks = config.LookAt ??= new();
        if(saved.Look != null)
            looks[name] = saved.Look;
        else
            looks.Remove(name);
        var motions = config.ReactionMotions ??= new();
        if(saved.Motion != null)
            motions[name] = saved.Motion;
        else if(motions.TryGetValue(name, out var m))
            m.Enabled = false;
        if(saved.Fingers || saved.Arm)
            lock(rigs.Sync)
            {
                rigs.Checkpoint();
                if(rigs.GetRig(actor.Address) is { } rig)
                {
                    if(saved.Fingers)
                        HandPoses.Clear(rig, saved.Right);
                    if(saved.Arm)
                        rig.Remove("j_te" + (saved.Right ? "_r" : "_l"));
                }
            }
        config.Save();
        return "Reaction taken off.";
    }

    /// <summary>
    /// The fingertip hold: thumb and index pinching, the other fingers held up and away, the hand held on the partner's
    /// shaft (it follows it). Returns what happened.
    /// </summary>
    public static string FingertipHold(RigService rigs, ActorRef actor, ActorRef partner, bool right)
    {
        string name = actor.Name ?? "";
        if(name.Length == 0 || !partner.IsSet || partner.Address == actor.Address)
            return "Choose whose it is first.";
        if(!ReactionSafety.Adult(actor.Address) || !ReactionSafety.Adult(partner.Address))
            return "This is for adult characters only.";
        var cache = new SkeletonInfoCache();
        if(!ActorSkeleton.TryGet(actor.Address, actor.ObjectIndex, cache, out var self) || !ActorSkeleton.TryGet(partner.Address, partner.ObjectIndex, cache, out var other))
            return "Their bones aren't available right now.";
        string side = right ? "_r" : "_l";
        if(!other.TryGetDrawnWorldFrame("iv_ochinko_c", cache, out var shaft))
            return $"{partner.Name} has no shaft bones (a body mod with iv_ochinko bones is needed).";
        if(!self.TryGetDrawnWorldFrame("j_ude_a" + side, cache, out var shoulder) || !self.TryGetDrawnWorldFrame("j_kosi", cache, out var hips)
           || !self.TryGetDrawnWorldFrame("j_kubi", cache, out var neck))
            return "This character's arm wasn't found.";
        float sc = Math.Clamp(Vector3.Distance(hips.Position, neck.Position) / 0.55f, 0.4f, 2.5f);
        var dir = shaft.Position - shoulder.Position;
        if(dir.LengthSquared() < 1e-6f)
            return "They're too close.";
        // The wrist a hand's length back from the pinch, toward the shoulder.
        var target = shaft.Position - Vector3.Normalize(dir) * 0.075f * sc;
        var pose = HandPoses.FingertipHold;
        lock(rigs.Sync)
        {
            rigs.Checkpoint();
            var rig = rigs.GetOrCreateRig(actor.Address, actor.ObjectIndex, name);
            HandPoses.Apply(rig, pose, right, 1f, b => self.Info.IndexOf(b) >= 0 || self.TryGetDrawnWorldFrame(b, cache, out _));
            var goal = rig.GetOrAdd("j_te" + side);
            goal.Enabled = true;
            goal.Space = GoalSpace.Attached;
            goal.HoldPosition = true;
            goal.ParentActor = partner.Address;
            goal.ParentObjectIndex = partner.ObjectIndex;
            goal.ParentActorName = partner.Name ?? "";
            goal.ParentBone = "iv_ochinko_c";
            goal.Mirror = MirrorMode.Off;
            goal.RequestCapture();
            goal.PendingWorldPosition = target;
        }
        return $"{name} holds it with two fingers (drag the hand's dot to fine-tune; Let go to stop).";
    }
}

/// <summary>The shaft measured along its bones (Size &amp; reactions page). Adult characters only.</summary>
public static class ShaftMeasure
{
    public static readonly string[] Chain = ["iv_ochinko_a", "iv_ochinko_b", "iv_ochinko_c", "iv_ochinko_d", "iv_ochinko_e", "iv_ochinko_f"];

    /// <summary>
    /// The shaft's world points (base to tip, the tip a little past the last bone) and its length (metres, the game's
    /// units). False when the character has no shaft bones.
    /// </summary>
    public static bool TryMeasure(nint address, ushort index, SkeletonInfoCache cache, out Vector3[] points, out float length)
    {
        points = [];
        length = 0f;
        if(!ReactionSafety.Adult(address) || !ActorSkeleton.TryGet(address, index, cache, out var sk))
            return false;
        var list = new List<Vector3>();
        foreach(var bone in Chain)
            if(sk.TryGetDrawnWorldFrame(bone, cache, out var f))
                list.Add(f.Position);
        if(list.Count < 2)
            return false;
        // (the last bone sits a little short of the tip: half its segment again)
        var last = list[^1] + (list[^1] - list[^2]) * 0.5f;
        list.Add(last);
        for(int i = 1; i < list.Count; i++)
            length += Vector3.Distance(list[i - 1], list[i]);
        points = list.ToArray();
        return length > 0.001f && float.IsFinite(length);
    }

    public static string Format(float metres, int units)
    {
        float cm = metres * 100f, inches = cm / 2.54f;
        return units switch
        {
            1 => $"{inches:0.0} in",
            2 => $"{cm:0.0} cm / {inches:0.0} in",
            _ => $"{cm:0.0} cm",
        };
    }
}
