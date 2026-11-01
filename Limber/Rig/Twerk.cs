using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Dalamud.Plugin.Services;
using Limber.Core;

namespace Limber.Rig;

/// <summary>The twerk styles (Limber Animations page).</summary>
public enum TwerkStyle
{
    Classic,
    Bounce,
    Shake,
    Wind,
    LeftRight,
    Drop,
}

/// <summary>The position twerked in.</summary>
public enum TwerkPose
{
    Squat,
    Standing,
    Kneeling,
    AllFours,
}

/// <summary>Where the hands go while twerking.</summary>
public enum TwerkHands
{
    Knees,
    Thighs,
    Hips,
    Free,
}

/// <summary>One character's twerk (kept by name).</summary>
public sealed class TwerkSettings
{
    public bool Enabled { get; set; }
    public TwerkStyle Style { get; set; } = TwerkStyle.Classic;
    public TwerkPose Pose { get; set; } = TwerkPose.Squat;
    /// <summary>Move from one style to another by itself.</summary>
    public bool Auto { get; set; }
    public float AutoSeconds { get; set; } = 12f;
    /// <summary>Hip pops per minute.</summary>
    public float Speed { get; set; } = 170f;
    /// <summary>How far the hips swing (degrees).</summary>
    public float Swing { get; set; } = 18f;
    /// <summary>Up / down bounce with each pop (centimetres).</summary>
    public float Bounce { get; set; } = 1.5f;
    /// <summary>How low the squat is (0 = standing, 1 = deep).</summary>
    public float Depth { get; set; } = 0.5f;
    /// <summary>How far the upper body leans forward (degrees).</summary>
    public float Lean { get; set; } = 35f;
    /// <summary>How far apart the feet are (0 = as the animation has them).</summary>
    public float Width { get; set; } = 0.4f;
    /// <summary>How much the upper body keeps still while the hips move (isolation).</summary>
    public float Isolation { get; set; } = 0.7f;
    public TwerkHands Hands { get; set; } = TwerkHands.Knees;
    /// <summary>Uneven pops (some stronger, some weaker).</summary>
    public float Randomness { get; set; } = 0.2f;
    /// <summary>Seconds to ease in / out.</summary>
    public float Ease { get; set; } = 0.8f;

    public TwerkSettings Copy() => (TwerkSettings)MemberwiseClone();
}

/// <summary>
/// Twerking, made by Limber: a squat with the upper body leaning forward and the hands on the knees (or thighs, hips,
/// or left to the animation), the hips popping back and forth (or bouncing, shaking, winding, popping side to side,
/// dropping low) while the upper body stays steady and the feet stay planted where the animation has them.
/// Bone update, before Dynamic Bones (so jiggle reacts to it); eased in and out. Only on your screen.
/// </summary>
public sealed unsafe class TwerkService
{
    private static readonly TwerkStyle[] AutoOrder = [TwerkStyle.Classic, TwerkStyle.Bounce, TwerkStyle.Shake, TwerkStyle.Wind, TwerkStyle.LeftRight, TwerkStyle.Drop];

    // The bones it turns, parents first (whichever the skeleton has).
    private static readonly string[] Touched =
    [
        "n_hara", "j_kosi", "j_sebo_a", "j_sebo_b", "j_sebo_c", "j_kubi", "j_kao",
        "j_asi_a_l", "j_asi_b_l", "j_asi_c_l", "j_asi_d_l", "j_asi_a_r", "j_asi_b_r", "j_asi_c_r", "j_asi_d_r",
        "j_sako_l", "j_ude_a_l", "j_ude_b_l", "j_te_l", "j_sako_r", "j_ude_a_r", "j_ude_b_r", "j_te_r",
    ];

    private sealed class State
    {
        public float Level;
        public double Time;
        public long LastTicks;
        public float Shown;
        // What the pose had before it was changed, and the root / hips as written (to put it back if the game
        // doesn't rebuild the pose, e.g. paused).
        public int[]? Bones;
        public Frame[]? Before;
        public Frame WrittenRoot, WrittenHips;
        public bool Wrote;
    }

    private readonly IObjectTable _objects;
    private readonly Func<Dictionary<string, TwerkSettings>> _settings;
    private readonly SkeletonInfoCache _cache = new();
    private readonly Dictionary<nint, State> _states = new();
    private volatile (nint Address, ushort Index, TwerkSettings Settings)[] _targets = [];
    private volatile Dictionary<nint, float> _levels = new();
    public Func<nint, bool> Skip = _ => false;

    public TwerkService(IObjectTable objects, Func<Dictionary<string, TwerkSettings>> settings)
    {
        _objects = objects;
        _settings = settings;
    }

    /// <summary>The pop right now (0..1, for the meter); -1 if not twerking.</summary>
    public float LevelOf(nint address) => _levels.TryGetValue(address, out var v) ? v : -1f;

    /// <summary>Framework thread, once a frame.</summary>
    public void Refresh()
    {
        var all = _settings();
        if(all.Count == 0)
        {
            _targets = [];
            return;
        }
        var list = new List<(nint, ushort, TwerkSettings)>();
        foreach(var obj in _objects)
        {
            if(obj == null || obj.Address == 0)
                continue;
            string name = obj.Name.TextValue;
            if(name.Length == 0 || !all.TryGetValue(name, out var s))
                continue;
            if(!Limber.Koi.Safety.AdultAllowed(obj.Address))
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
        var levels = new Dictionary<nint, float>();
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
                if(state.Level > 0f)
                    levels[address] = state.Shown;
                else if(!s.Enabled)
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
        _levels = levels;
    }

    // ---- The motion: pure functions of time

    private static float Hash(long n, int salt)
    {
        unchecked
        {
            ulong x = ((ulong)n * 0x9E3779B97F4A7C15UL) ^ ((ulong)(uint)salt * 0xBF58476D1CE4E5B9UL);
            x ^= x >> 31;
            x *= 0x94D049BB133111EBUL;
            x ^= x >> 29;
            return (x & 0xFFFFFF) / (float)0x1000000;
        }
    }

    private static float Smooth(float u)
    {
        u = Math.Clamp(u, 0f, 1f);
        return u * u * (3f - 2f * u);
    }

    // One pop: a quick snap out (the first third) and an easier return (0..1).
    private static float Pop(double phase)
    {
        // a quick (but rounded) snap out and an eased return, so it reads as a pop without looking mechanical
        float u = (float)(phase - Math.Floor(phase));
        if(u < 0.38f)
        {
            float a = u / 0.38f;
            return 0.5f - 0.5f * MathF.Cos(a * MathF.PI);
        }
        float b = (u - 0.38f) / 0.62f;
        return 0.5f + 0.5f * MathF.Cos(b * MathF.PI);
    }

    /// <summary>The hips' turn (pitch: + = arched, butt out; roll; yaw, degrees, per unit of swing), the up / down
    /// bounce (per unit of bounce, + = down), an extra squat (0..1) and the pop shown on the meter.</summary>
    private readonly record struct Motion(float Pitch, float Roll, float Yaw, float Drop, float Squat, float Shown)
    {
        public static Motion Lerp(Motion a, Motion b, float t) => new(
            a.Pitch + (b.Pitch - a.Pitch) * t, a.Roll + (b.Roll - a.Roll) * t, a.Yaw + (b.Yaw - a.Yaw) * t,
            a.Drop + (b.Drop - a.Drop) * t, a.Squat + (b.Squat - a.Squat) * t, a.Shown + (b.Shown - a.Shown) * t);
    }

    private static Motion MotionOf(TwerkStyle style, double t, float popsPerMinute, float randomness)
    {
        double rate = Math.Clamp(popsPerMinute, 20f, 400f) / 60.0;
        double phase = t * rate;
        long n = (long)Math.Floor(phase);
        float strength = 1f + Math.Clamp(randomness, 0f, 1f) * (Hash(n, 3) - 0.5f) * 0.8f;
        float pop = Pop(phase) * strength;
        switch(style)
        {
            default:
            case TwerkStyle.Classic:
                // Arch out on the pop, tuck back under between; a small dip with each.
                return new Motion((2f * pop - 1f), 0f, 0f, 0.35f * pop, 0f, pop);
            case TwerkStyle.Bounce:
                // Up and down from the knees, the hips following a little.
                return new Motion(0.35f * (2f * pop - 1f), 0f, 0f, 1.6f * pop, 0f, pop);
            case TwerkStyle.Shake:
            {
                // A fast side-to-side shake (twice the pops), the hips rocking under.
                double fast = phase * 2;
                float side = (float)Math.Sin(fast * 2 * Math.PI) * strength;
                float under = (float)Math.Sin(fast * 2 * Math.PI + Math.PI / 2);
                return new Motion(0.25f + 0.15f * under, 0.7f * side, 0.35f * side, 0.15f * MathF.Abs(side), 0f, MathF.Abs(side));
            }
            case TwerkStyle.Wind:
            {
                // Slow circles of the hips (one per two pops).
                double slow = phase * 0.5 * 2 * Math.PI;
                return new Motion(0.9f * (float)Math.Cos(slow), 0.75f * (float)Math.Sin(slow), 0f, 0.3f * (0.5f + 0.5f * (float)Math.Cos(slow)), 0f, 0.5f + 0.5f * (float)Math.Cos(slow));
            }
            case TwerkStyle.LeftRight:
            {
                // Each pop to one side, then the other.
                float side = (n & 1) == 0 ? 1f : -1f;
                return new Motion((2f * pop - 1f) * 0.8f, 0.6f * side * pop, 0.25f * side * pop, 0.3f * pop, 0f, pop);
            }
            case TwerkStyle.Drop:
            {
                // Pops while sinking low over eight pops, then rising again.
                float sink = 0.5f - 0.5f * (float)Math.Cos(phase / 8.0 * 2 * Math.PI);
                return new Motion((2f * pop - 1f) * (0.7f + 0.3f * sink), 0f, 0f, 0.35f * pop, sink, pop);
            }
        }
    }

    private static Motion MotionAt(TwerkSettings s, double t)
    {
        if(!s.Auto)
            return MotionOf(s.Style, t, s.Speed, s.Randomness);
        double span = Math.Clamp(s.AutoSeconds, 3f, 600f);
        long slot = (long)Math.Floor(t / span);
        TwerkStyle Pick(long k) => AutoOrder[(int)(Hash(k, 17) * AutoOrder.Length) % AutoOrder.Length];
        double into = t - slot * span;
        float mix = into > span - 1.2 ? Smooth((float)((into - (span - 1.2)) / 1.2)) : 0f;
        var a = MotionOf(Pick(slot), t, s.Speed, s.Randomness);
        return mix <= 0f ? a : Motion.Lerp(a, MotionOf(Pick(slot + 1), t, s.Speed, s.Randomness), mix);
    }

    /// <summary>The style playing now (the page shows it when it changes by itself).</summary>
    public static TwerkStyle StyleAt(TwerkSettings s, double t)
    {
        if(!s.Auto)
            return s.Style;
        double span = Math.Clamp(s.AutoSeconds, 3f, 600f);
        return AutoOrder[(int)(Hash((long)Math.Floor(t / span), 17) * AutoOrder.Length) % AutoOrder.Length];
    }

    public double TimeOf(nint address) => _states.TryGetValue(address, out var st) ? st.Time : 0;

    // ---- Applying it

    private static Quaternion Turn(Vector3 axis, float degrees) => Quaternion.CreateFromAxisAngle(axis, degrees * MathF.PI / 180f);

    private static bool Near(Vector3 a, Vector3 b) => Vector3.DistanceSquared(a, b) < 1e-10f;
    private static bool Near(Quaternion a, Quaternion b) => MathF.Abs(Quaternion.Dot(a, b)) > 0.9999999f;

    private void Step(nint address, ushort index, TwerkSettings s, State state, long now)
    {
        float dt = Math.Clamp((float)((now - state.LastTicks) / (double)Stopwatch.Frequency), 0f, 0.1f);
        state.LastTicks = now;
        state.Level = Math.Clamp(state.Level + (s.Enabled ? dt : -dt) / MathF.Max(0.05f, s.Ease), 0f, 1f);
        state.Time += dt;
        if(!ActorSkeleton.TryGet(address, index, _cache, out var sk) || sk.Info.Count == 0)
            return;
        int I(string name) => sk.Info.IndexOf(name);
        int root = I("n_hara"), hips = I("j_kosi");
        if(hips < 0)
            return;
        if(root < 0)
            root = sk.Info.ParentOf(hips);
        if(root < 0)
            return;

        // The pose wasn't rebuilt since last frame (paused): put back what it had before.
        if(state.Wrote && state.Bones != null && state.Before != null)
        {
            var r = sk.GetModelFrame(root);
            var h = sk.GetModelFrame(hips);
            if(Near(r.Position, state.WrittenRoot.Position) && Near(h.Rotation, state.WrittenHips.Rotation))
                for(int i = 0; i < state.Bones.Length; i++)
                {
                    int b = state.Bones[i];
                    if(b == root)
                        sk.SetModelPosition(b, state.Before[i].Position);
                    sk.SetModelRotation(b, state.Before[i].Rotation);
                }
        }
        state.Wrote = false;

        // What the animation has (absolute, in the character's space), parents first.
        var bones = new List<int>();
        foreach(var name in Touched)
        {
            int b = I(name);
            if(b >= 0 && !bones.Contains(b))
                bones.Add(b);
        }
        if(!bones.Contains(root))
            bones.Insert(0, root);
        var before = new Frame[bones.Count];
        for(int i = 0; i < bones.Count; i++)
            before[i] = sk.GetModelFrame(bones[i]);
        state.Bones = bones.ToArray();
        state.Before = before;
        Frame Orig(int b)
        {
            int k = bones.IndexOf(b);
            return k >= 0 ? before[k] : sk.GetModelFrame(b);
        }

        float level = Smooth(state.Level);
        var m = MotionAt(s, state.Time);
        state.Shown = Math.Clamp(m.Shown, 0f, 1f) * level;
        if(level <= 0f)
            return;
        int thighL = I("j_asi_a_l"), shinL = I("j_asi_b_l"), kneeL = I("j_asi_c_l"), footL = I("j_asi_d_l");
        int thighR = I("j_asi_a_r"), shinR = I("j_asi_b_r"), kneeR = I("j_asi_c_r"), footR = I("j_asi_d_r");
        if(thighL < 0 || kneeL < 0 || footL < 0 || thighR < 0 || kneeR < 0 || footR < 0)
            return;
        // The character's own measurements (the same in any pose).
        float upperLeg = (Vector3.Distance(Orig(thighL).Position, Orig(kneeL).Position) + Vector3.Distance(Orig(thighR).Position, Orig(kneeR).Position)) * 0.5f;
        float lowerLeg = (Vector3.Distance(Orig(kneeL).Position, Orig(footL).Position) + Vector3.Distance(Orig(kneeR).Position, Orig(footR).Position)) * 0.5f;
        float legLength = upperLeg + lowerLeg;
        if(legLength < 0.05f)
            return;
        float sc = legLength / 0.85f;

        var pose = s.Pose;
        bool onFloor = pose is TwerkPose.Kneeling or TwerkPose.AllFours;
        double t = state.Time;
        float swing = Math.Clamp(s.Swing, 0f, 45f);
        float width = Math.Clamp(s.Width, 0f, 1f);
        float iso = Math.Clamp(s.Isolation, 0f, 1f);
        float bounce = Math.Clamp(s.Bounce, 0f, 10f) * 0.01f;
        float depth = Math.Clamp(s.Depth * (1f + 0.5f * m.Squat), 0f, 1.3f);
        float hipPitch = swing * m.Pitch, hipRoll = swing * m.Roll, hipYaw = swing * m.Yaw;
        // a slow weight shift from side to side and breathing, so it's never perfectly still
        float shift = MathF.Sin((float)(t * 2 * Math.PI * 0.23)) * legLength * 0.012f;
        float breath = MathF.Sin((float)(t * 2 * Math.PI * 0.3));

        // The ground (model space) under the feet, the same for both, and the ankles' height off it.
        float ankleY = MathF.Min(Orig(footL).Position.Y, Orig(footR).Position.Y);
        float floor = ankleY - 0.075f * sc;
        var hipMidNow = (Orig(thighL).Position + Orig(thighR).Position) * 0.5f;
        float baseX = Orig(root).Position.X, baseZ = Orig(root).Position.Z; // centred where the character stands

        // ---- 1. The layout: where the hips go, the knees and feet, how far the body leans.
        Vector3 hipGoal;
        var kneeGoal = new Vector3[2];
        var footGoal = new Vector3[2];
        bool fixedKnees = onFloor;
        float toeOut, lean, pelvisTilt, kneeOut;
        float leanAdd = s.Lean - 35f;
        switch(pose)
        {
            default:
            case TwerkPose.Squat:
            {
                // A wide, low squat: feet well apart and turned out, knees out over the toes, sitting back.
                float half = legLength * (0.2f + 0.14f * width);
                for(int i = 0; i < 2; i++)
                    footGoal[i] = new Vector3(baseX + (i == 0 ? half : -half), ankleY, baseZ + 0.03f * legLength);
                hipGoal = new Vector3(baseX + shift, ankleY + legLength * (0.8f - 0.44f * depth) - bounce * m.Drop, baseZ - legLength * (0.03f + 0.1f * depth));
                toeOut = 30f; kneeOut = 0.9f;
                lean = 30f + 12f * depth + leanAdd;
                pelvisTilt = 14f + 0.45f * lean;
                break;
            }
            case TwerkPose.Standing:
            {
                float half = legLength * (0.09f + 0.06f * width);
                for(int i = 0; i < 2; i++)
                    footGoal[i] = new Vector3(baseX + (i == 0 ? half : -half), ankleY, baseZ + 0.02f * legLength);
                hipGoal = new Vector3(baseX + shift, ankleY + legLength * (0.97f - 0.12f * depth) - bounce * 0.6f * m.Drop, baseZ - legLength * 0.05f);
                toeOut = 12f; kneeOut = 0.3f;
                lean = 65f + leanAdd;
                pelvisTilt = 10f + 0.65f * lean;
                break;
            }
            case TwerkPose.Kneeling:
            case TwerkPose.AllFours:
            {
                // Both knees on the ground, the shins lying straight back, the feet together behind.
                bool fours = pose == TwerkPose.AllFours;
                float kneeY = floor + 0.06f * sc;
                float half = legLength * ((fours ? 0.08f : 0.09f) + 0.07f * width);
                float kneeZ = baseZ + legLength * (fours ? 0.02f : 0.1f);
                float sit = (fours ? -2f + 30f * depth : 12f + 48f * depth) * MathF.PI / 180f;
                var kneeMid = new Vector3(baseX, kneeY, kneeZ);
                hipGoal = kneeMid + upperLeg * new Vector3(0f, MathF.Cos(sit), -MathF.Sin(sit)) + new Vector3(shift, -bounce * m.Drop, 0f);
                var shin = lowerLeg * Vector3.Normalize(new Vector3(0f, (floor + 0.05f * sc) - kneeY, -lowerLeg));
                for(int i = 0; i < 2; i++)
                {
                    float x = i == 0 ? half : -half;
                    kneeGoal[i] = new Vector3(baseX + x, kneeY, kneeZ);
                    footGoal[i] = kneeGoal[i] + shin + new Vector3(-x * 0.15f, 0f, 0f);
                }
                toeOut = 0f; kneeOut = 0f;
                lean = (fours ? 82f : 18f + 10f * depth) + leanAdd * (fours ? 0.5f : 1f);
                pelvisTilt = fours ? 6f + 0.8f * lean : 8f + 0.5f * lean;
                break;
            }
        }
        lean = Math.Clamp(lean, 0f, 110f);

        // ---- 2. The body: placed so the hips land on their spot, the pelvis tilted and popping.
        sk.SetModelPosition(root, Orig(root).Position + (hipGoal - hipMidNow));
        var hipTurn = Turn(Vector3.UnitY, hipYaw) * Turn(Vector3.UnitZ, hipRoll) * Turn(Vector3.UnitX, pelvisTilt + 10f + hipPitch);
        sk.SetModelRotation(hips, Quaternion.Normalize(hipTurn * RestModel(sk, hips)));
        var hipMid = (sk.GetModelFrame(thighL).Position + sk.GetModelFrame(thighR).Position) * 0.5f;
        var rootAt = sk.GetModelFrame(root).Position + (hipGoal - hipMid);
        sk.SetModelPosition(root, rootAt);

        // ---- 3. The spine: the lean spread over it, and the hips' motion rippling up it a moment later
        //         (follow-through), less with more isolation; the chest turning against the hips; breathing.
        (string Name, float Lean, float Follow, float Lag)[] spine =
        [
            ("j_sebo_a", 0.5f, 0.5f, 0.04f),
            ("j_sebo_b", 0.78f, 0.3f, 0.08f),
            ("j_sebo_c", 1.0f, 0.18f, 0.12f),
            ("j_kubi", pose == TwerkPose.AllFours ? 0.62f : 0.7f, 0.1f, 0.17f),
            ("j_kao", pose == TwerkPose.AllFours ? 0.42f : 0.32f, 0.08f, 0.22f),
        ];
        Quaternion chestTurn = Quaternion.Identity;
        void Spine(float leanNow)
        {
            foreach(var (name, part, follow, lag) in spine)
            {
                int b = I(name);
                if(b < 0)
                    continue;
                var late = MotionAt(s, t - lag);
                float f = follow * (1f - 0.75f * iso);
                float pitchHere = leanNow * part + swing * late.Pitch * f + (name == "j_sebo_c" ? 1.5f * breath : 0f);
                float rollHere = swing * late.Roll * f * 0.8f;
                float yawHere = swing * late.Yaw * f - hipYaw * 0.25f * part; // the chest turning a little against the hips
                var turn = Turn(Vector3.UnitY, yawHere) * Turn(Vector3.UnitZ, rollHere) * Turn(Vector3.UnitX, pitchHere);
                sk.SetModelRotation(b, Quaternion.Normalize(turn * RestModel(sk, b)));
                if(name == "j_sebo_c")
                    chestTurn = turn;
            }
            // the collarbones with the chest (the same on both sides)
            foreach(var name in new[] { "j_sako_l", "j_sako_r" })
            {
                int b = I(name);
                if(b >= 0)
                    sk.SetModelRotation(b, Quaternion.Normalize(chestTurn * RestModel(sk, b)));
            }
        }
        Spine(lean);

        int upperL = I("j_ude_a_l"), lowerL = I("j_ude_b_l"), handL = I("j_te_l");
        int upperR = I("j_ude_a_r"), lowerR = I("j_ude_b_r"), handR = I("j_te_r");
        bool arms = upperL >= 0 && lowerL >= 0 && handL >= 0 && upperR >= 0 && lowerR >= 0 && handR >= 0;
        float upperArm = arms ? Vector3.Distance(Orig(upperL).Position, Orig(lowerL).Position) : 0f;
        float lowerArm = arms ? Vector3.Distance(Orig(lowerL).Position, Orig(handL).Position) : 0f;

        // On all fours, lean until the shoulders sit an arm's length (nearly straight) over the floor.
        if(pose == TwerkPose.AllFours && arms && upperArm > 1e-3f)
        {
            float want = floor + 0.035f * sc + (upperArm + lowerArm) * 0.95f;
            for(int pass = 0; pass < 3; pass++)
            {
                var shoulders = (sk.GetModelFrame(upperL).Position + sk.GetModelFrame(upperR).Position) * 0.5f;
                float torso = MathF.Max(0.1f * sc, Vector3.Distance(shoulders, sk.GetModelFrame(hips).Position));
                float off = shoulders.Y - want;
                if(MathF.Abs(off) < 0.002f * sc)
                    break;
                lean = Math.Clamp(lean + off / torso * 180f / MathF.PI, 40f, 120f);
                Spine(lean);
            }
        }

        // ---- 4. The legs: from their rest, so both sides are the same; knees always bending forward / out.
        for(int i = 0; i < 2; i++)
        {
            int thigh = i == 0 ? thighL : thighR, shinB = i == 0 ? shinL : shinR, knee = i == 0 ? kneeL : kneeR, foot = i == 0 ? footL : footR;
            float side = i == 0 ? 1f : -1f;
            sk.SetModelRotation(thigh, RestModel(sk, thigh));
            if(shinB >= 0)
                sk.SetModelRotation(shinB, RestModel(sk, shinB));
            sk.SetModelRotation(knee, RestModel(sk, knee));
            var a = sk.GetModelFrame(thigh).Position;
            var kneeAt = fixedKnees ? kneeGoal[i] : KneeOf(a, footGoal[i], upperLeg, lowerLeg, Vector3.Normalize(new Vector3(side * kneeOut, 0f, 1f)));
            Limb(sk, thigh, knee, foot, kneeAt, footGoal[i]);
            // the foot: flat and turned out (standing), or lying on its top with the toes back (on the knees)
            var footTurn = onFloor ? Turn(Vector3.UnitX, 160f) : Turn(Vector3.UnitY, side * toeOut);
            sk.SetModelRotation(foot, Quaternion.Normalize(footTurn * RestModel(sk, foot)));
        }

        // ---- 5. The arms: hands on the knees / thighs / hips, or flat on the floor on all fours; elbows always
        //         bending back and out.
        if(arms && upperArm > 1e-3f && lowerArm > 1e-3f && (pose == TwerkPose.AllFours || s.Hands != TwerkHands.Free))
        {
            for(int i = 0; i < 2; i++)
            {
                float side = i == 0 ? 1f : -1f;
                int upper = i == 0 ? upperL : upperR, lower = i == 0 ? lowerL : lowerR, hand = i == 0 ? handL : handR;
                sk.SetModelRotation(upper, Quaternion.Normalize(chestTurn * RestModel(sk, upper)));
                sk.SetModelRotation(lower, Quaternion.Normalize(chestTurn * RestModel(sk, lower)));
                var shoulder = sk.GetModelFrame(upper).Position;
                Vector3 goal, elbowWay;
                Quaternion palm;
                if(pose == TwerkPose.AllFours)
                {
                    var other = sk.GetModelFrame(i == 0 ? upperR : upperL).Position;
                    float halfShoulders = MathF.Abs(shoulder.X - other.X) * 0.5f;
                    goal = new Vector3(baseX + side * halfShoulders * 1.05f, floor + 0.03f * sc, shoulder.Z + 0.03f * legLength);
                    elbowWay = Vector3.Normalize(new Vector3(side * 0.3f, 0f, -1f));
                    palm = Turn(Vector3.UnitY, side * -85f);                 // flat, fingers forward
                }
                else if(s.Hands == TwerkHands.Hips)
                {
                    var hp = sk.GetModelFrame(hips).Position;
                    goal = hp + new Vector3(side * legLength * 0.2f, legLength * 0.06f, legLength * 0.02f);
                    elbowWay = Vector3.Normalize(new Vector3(side, 0f, -1f));
                    palm = Turn(Vector3.UnitZ, side * 70f) * Turn(Vector3.UnitY, side * 60f);
                }
                else
                {
                    int thigh = i == 0 ? thighL : thighR, knee = i == 0 ? kneeL : kneeR;
                    var kp = sk.GetModelFrame(knee).Position;
                    var tp = sk.GetModelFrame(thigh).Position;
                    float along = s.Hands == TwerkHands.Knees ? (onFloor ? 0.3f : 0.1f) : 0.5f;
                    goal = Vector3.Lerp(kp, tp, along) + new Vector3(side * 0.02f * sc, 0.045f * sc, 0.025f * sc);
                    elbowWay = Vector3.Normalize(new Vector3(side, 0f, -0.35f));
                    palm = Turn(Vector3.UnitX, 25f) * Turn(Vector3.UnitY, side * -75f); // resting on top, fingers over the front
                }
                var elbowAt = KneeOf(shoulder, goal, upperArm, lowerArm, elbowWay);
                Limb(sk, upper, lower, hand, elbowAt, goal);
                sk.SetModelRotation(hand, Quaternion.Normalize(palm * RestModel(sk, hand)));
            }
        }

        // ---- 6. Blend from the animation by how far in it is (eased), parents first.
        var made = new Frame[bones.Count];
        for(int i = 0; i < bones.Count; i++)
            made[i] = sk.GetModelFrame(bones[i]);
        if(level < 0.999f)
            for(int i = 0; i < bones.Count; i++)
            {
                int b = bones[i];
                if(b == root)
                    sk.SetModelPosition(b, Vector3.Lerp(before[i].Position, made[i].Position, level));
                sk.SetModelRotation(b, Quaternion.Normalize(Quaternion.Slerp(before[i].Rotation, made[i].Rotation, level)));
            }

        sk.RealignPartials(_cache);
        state.WrittenRoot = sk.GetModelFrame(root);
        state.WrittenHips = sk.GetModelFrame(hips);
        state.Wrote = true;
    }

    // The bone's rest turn in the character's space (the skeleton's reference pose: standing straight, facing
    // forward), cached per skeleton.
    private readonly Dictionary<(nint, int), Quaternion> _rest = new();
    private Quaternion RestModel(in ActorSkeleton sk, int bone)
    {
        var key = ((nint)sk.Pose->Skeleton, bone);
        if(_rest.TryGetValue(key, out var q))
            return q;
        q = Quaternion.Identity;
        var chain = new List<int>();
        for(int b = bone; b >= 0 && chain.Count < 256; b = sk.Info.ParentOf(b))
            chain.Add(b);
        for(int i = chain.Count - 1; i >= 0; i--)
            if(sk.TryGetReferenceLocal(chain[i], out _, out var local))
                q = Quaternion.Normalize(q * local);
        if(_rest.Count > 4096)
            _rest.Clear();
        _rest[key] = q;
        return q;
    }

    /// <summary>Where the middle joint (knee / elbow) goes for the end to reach `end` from `start`, always bent
    /// toward `bendWay` (so it can't flip the wrong way), and never quite straight.</summary>
    private static Vector3 KneeOf(Vector3 start, Vector3 end, float upper, float lower, Vector3 bendWay)
    {
        var d = end - start;
        float dist = d.Length();
        if(dist < 1e-5f)
            return start + bendWay * upper;
        var dir = d / dist;
        float reach = Math.Clamp(dist, MathF.Abs(upper - lower) + 0.002f, (upper + lower) * 0.995f);
        var bend = bendWay - dir * Vector3.Dot(bendWay, dir);
        if(bend.LengthSquared() < 1e-8f)
            bend = MathUtil.AnyPerpendicular(dir);
        bend = Vector3.Normalize(bend);
        float cos = Math.Clamp((upper * upper + reach * reach - lower * lower) / (2f * upper * reach), -1f, 1f);
        float sin = MathF.Sqrt(MathF.Max(0f, 1f - cos * cos));
        return start + upper * (dir * cos + bend * sin);
    }

    /// <summary>Turns a two-bone limb so its middle joint lands on `middleAt` and its end points at `endAt` (the
    /// shortest turn from where each bone is now, so they don't twist).</summary>
    private static void Limb(in ActorSkeleton sk, int upper, int middle, int end, Vector3 middleAt, Vector3 endAt)
    {
        var a = sk.GetModelFrame(upper);
        var b = sk.GetModelPosition(middle);
        var swing = MathUtil.FromTo(b - a.Position, middleAt - a.Position);
        sk.SetModelRotation(upper, Quaternion.Normalize(swing * a.Rotation));
        var m = sk.GetModelFrame(middle);
        var c = sk.GetModelPosition(end);
        var bendTurn = MathUtil.FromTo(c - m.Position, endAt - m.Position);
        sk.SetModelRotation(middle, Quaternion.Normalize(bendTurn * m.Rotation));
    }
}
