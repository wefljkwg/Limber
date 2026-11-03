using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Dalamud.Plugin.Services;
using Limber.Core;

namespace Limber.Rig;

/// <summary>The throb patterns (Limber Animations page).</summary>
public enum ThrobPattern
{
    Steady,
    Heartbeat,
    Building,
    Bursts,
    Twitching,
    Wave,
    Random,
    Climax,
    SlowSwell,
}

/// <summary>One character's throbbing (kept by name).</summary>
public sealed class ThrobSettings
{
    public bool Enabled { get; set; }
    public ThrobPattern Pattern { get; set; } = ThrobPattern.Heartbeat;
    /// <summary>Changes pattern by itself every <see cref="AutoSeconds"/> (blending from one to the next).</summary>
    public bool Auto { get; set; }
    public float AutoSeconds { get; set; } = 20f;
    /// <summary>Beats per minute.</summary>
    public float Speed { get; set; } = 60f;
    /// <summary>How much thicker it gets on a beat (0.12 = 12%).</summary>
    public float Swell { get; set; } = 0.1f;
    /// <summary>How much longer on a beat.</summary>
    public float Length { get; set; } = 0.03f;
    /// <summary>How far it rises as the throb happens (degrees).</summary>
    public float Lift { get; set; } = 6f;
    /// <summary>How far it dips below where it was as the throb ends (degrees).</summary>
    public float Fall { get; set; } = 3f;
    /// <summary>How the beat travels from the base to the tip (0 = all at once, 1 = a slow ripple).</summary>
    public float Travel { get; set; } = 0.4f;
    /// <summary>The swell more toward the tip (0 = even, 1 = mostly the tip).</summary>
    public float TipSwell { get; set; } = 0.4f;
    /// <summary>Uneven timing and strength (0 = machine-like, 1 = very uneven).</summary>
    public float Randomness { get; set; } = 0.3f;
    /// <summary>The testicles drawn up a little on the strong beats.</summary>
    public float Testicles { get; set; } = 0.5f;
    /// <summary>Stronger while this character's shaft is in Oral mode (0 = no change, 1 = twice as strong).</summary>
    public float OralBoost { get; set; } = 0.5f;
    /// <summary>Seconds to ease in and out.</summary>
    public float Ease { get; set; } = 1.5f;

    public ThrobSettings Copy() => (ThrobSettings)MemberwiseClone();
}

/// <summary>
/// Throbbing: the penis pulses by itself while it's on, in a chosen pattern (steady, heartbeat, building, bursts,
/// twitching, a travelling wave, random, climax contractions, slow swelling) or changing patterns on its own. Each
/// beat swells the shaft (more toward the tip, if wanted), lengthens it a little and lifts it, travelling from the base
/// to the tip; the testicles can draw up with the strong beats. It eases in and out, and gets stronger in Oral mode.
/// Bone update, after the dynamic bones (it works on top of them). Only on your screen; adult characters only.
/// </summary>
public sealed unsafe class ThrobService
{
    private static readonly string[] Shaft = DynamicBoneService.PenisBones;
    private static readonly string[] Balls = ["iv_kougan_l", "iv_kougan_r"];

    private sealed class Written
    {
        public Vector3 Scale, ScaleFactor = Vector3.One;
        public Quaternion Rotation = Quaternion.Identity, Turn = Quaternion.Identity;
        public Vector3 Position, Move;
        public bool Any;
    }

    private sealed class State
    {
        public float Level;      // eased in / out 0..1
        public double Time;      // pattern clock (seconds)
        public long LastTicks;
        public readonly Dictionary<string, Written> Writes = new();
        public float Shown;      // the last beat level (for the page's meter)
    }

    private readonly IObjectTable _objects;
    private readonly Func<Dictionary<string, ThrobSettings>> _settings;
    private readonly SkeletonInfoCache _cache = new();
    private readonly Dictionary<nint, State> _states = new();
    private volatile (nint Address, ushort Index, ThrobSettings Settings)[] _targets = [];
    public Func<nint, bool> InOral = _ => false;

    private volatile Dictionary<nint, float> _levels = new();

    public ThrobService(IObjectTable objects, Func<Dictionary<string, ThrobSettings>> settings)
    {
        _objects = objects;
        _settings = settings;
    }

    /// <summary>The beat right now on a character (0..1, for the meter); -1 if it isn't throbbing.</summary>
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
        var list = new List<(nint, ushort, ThrobSettings)>();
        foreach(var obj in _objects)
        {
            if(obj == null || obj.Address == 0)
                continue;
            string name = obj.Name.TextValue;
            if(name.Length == 0 || !all.TryGetValue(name, out var s))
                continue;
            if(!Limber.Koi.Safety.AdultAllowed(obj.Address))
                continue;
            list.Add((obj.Address, obj.ObjectIndex, s.Copy())); // (a copy: the bone thread reads it)
        }
        _targets = list.ToArray();
    }

    public void Clear() => _targets = [];

    /// <summary>Bone update stage.</summary>
    private readonly System.Collections.Concurrent.ConcurrentQueue<nint> _sync = new();

    /// <summary>Starts this character's beat from the top on the next frame (to line it up with another one).</summary>
    public void Sync(nint address) => _sync.Enqueue(address);

    public void UpdateFrame()
    {
        while(_sync.TryDequeue(out var again))
            if(_states.TryGetValue(again, out var restart))
                restart.Time = 0;
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

    // ---- Patterns: a beat level (swell) and a lift, from the clock; pure functions of time so the beat can travel.

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

    // One pulse: a quick rise and a slower fall (x = seconds since it started, length in seconds).
    private static float Pulse(double x, double length)
    {
        if(x < 0 || x > length * 3)
            return 0f;
        double rise = length * 0.25;
        if(x < rise)
        {
            double u = x / rise;
            return (float)(u * u * (3 - 2 * u));
        }
        return (float)Math.Exp(-(x - rise) / (length * 0.45));
    }

    /// <summary>(swell 0..1, lift 0..1) at time t for a pattern.</summary>
    private static (float Swell, float Lift) Beat(ThrobPattern pattern, double t, float bpm, float randomness)
    {
        double period = 60.0 / Math.Clamp(bpm, 5f, 400f);
        long n = (long)Math.Floor(t / period);
        double x = t - n * period; // seconds into this beat
        float Strength(long k) => 1f - randomness * 0.6f * Hash(k, 1);
        double Jitter(long k) => randomness * 0.25 * period * (Hash(k, 2) - 0.5);
        // The pulse that began in this beat, and the tail of the one before.
        float Train(double length)
        {
            float v = 0f;
            for(long k = n - 2; k <= n; k++)
            {
                double start = k * period + Jitter(k);
                v = MathF.Max(v, Pulse(t - start, length) * Strength(k));
            }
            return v;
        }
        switch(pattern)
        {
            case ThrobPattern.Steady:
            {
                float v = Train(period * 0.45);
                return (v, v);
            }
            case ThrobPattern.Heartbeat:
            {
                float v = 0f;
                for(long k = n - 1; k <= n; k++)
                {
                    double start = k * period + Jitter(k);
                    v = MathF.Max(v, Pulse(t - start, period * 0.22) * Strength(k));
                    v = MathF.Max(v, 0.7f * Pulse(t - start - period * 0.3, period * 0.22) * Strength(k));
                }
                return (v, v * 0.8f);
            }
            case ThrobPattern.Building:
            {
                // 20 beats: growing stronger and doubling up, then a rest of 4.
                long c = ((n % 24) + 24) % 24;
                if(c >= 20)
                    return (Pulse(x, period) * 0.25f, 0f);
                float grow = 0.3f + 0.7f * (c / 19f);
                double sub = c >= 12 ? period * 0.5 : period;
                double xs = x % sub;
                float v = Pulse(xs, sub * 0.45) * grow * Strength(n);
                return (v, v);
            }
            case ThrobPattern.Bursts:
            {
                // Four quick pulses, then a pause as long.
                long c = ((n % 8) + 8) % 8;
                if(c >= 4)
                    return (0f, 0f);
                double quick = period * 0.5;
                double xs = x % quick;
                float v = Pulse(xs, quick * 0.4) * Strength(n * 2 + (x >= quick ? 1 : 0));
                return (v, v);
            }
            case ThrobPattern.Twitching:
            {
                // Sudden short jerks at random moments (about one per beat), mostly a lift.
                float v = 0f;
                for(long k = n - 1; k <= n; k++)
                {
                    if(Hash(k, 3) > 0.6f + randomness * 0.2f)
                        continue;
                    double start = k * period + Hash(k, 4) * period * 0.8;
                    v = MathF.Max(v, Pulse(t - start, 0.12) * (0.5f + 0.5f * Hash(k, 5)));
                }
                return (v * 0.35f, v);
            }
            case ThrobPattern.Wave:
            {
                // A smooth, continuous wave (it travels base to tip with Travel).
                float v = 0.5f - 0.5f * MathF.Cos((float)(2 * Math.PI * t / period));
                return (v, v * 0.4f);
            }
            case ThrobPattern.Random:
            {
                float v = 0f;
                for(long k = n - 2; k <= n; k++)
                {
                    if(Hash(k, 6) < 0.25f)
                        continue; // a skipped beat
                    double start = k * period + (Hash(k, 7) - 0.5) * period * 0.6;
                    v = MathF.Max(v, Pulse(t - start, period * (0.25 + 0.35 * Hash(k, 8))) * (0.35f + 0.65f * Hash(k, 9)));
                }
                return (v, v * (0.5f + 0.5f * Hash(n, 10)));
            }
            case ThrobPattern.Climax:
            {
                // Strong contractions, close together at first and further apart and weaker after; a rest; again.
                const int count = 12;
                double cycle = 0, first = 0.55 * period / 0.8;
                for(int k = 0; k < count; k++)
                    cycle += first * Math.Pow(1.13, k);
                cycle += 5.0;
                double tc = t % cycle;
                double at = 0;
                float v = 0f;
                for(int k = 0; k < count; k++)
                {
                    double gap = first * Math.Pow(1.13, k);
                    v = MathF.Max(v, Pulse(tc - at, Math.Min(gap * 0.5, 0.45)) * MathF.Pow(0.9f, k) * Strength(k + (long)(t / cycle) * 100));
                    at += gap;
                }
                return (v, v);
            }
            case ThrobPattern.SlowSwell:
            {
                // Slow swelling (a breath every 4 beats) with soft pulses on top.
                float slow = 0.5f - 0.5f * MathF.Cos((float)(2 * Math.PI * t / (period * 4)));
                float soft = Train(period * 0.5) * 0.35f;
                return (MathF.Min(1f, slow * 0.8f + soft), soft * 0.6f);
            }
        }
        return (0f, 0f);
    }

    private static readonly ThrobPattern[] AutoOrder =
        [ThrobPattern.SlowSwell, ThrobPattern.Steady, ThrobPattern.Heartbeat, ThrobPattern.Wave, ThrobPattern.Twitching,
         ThrobPattern.Random, ThrobPattern.Bursts, ThrobPattern.Building, ThrobPattern.Climax];

    /// <summary>The pattern Auto plays at time t (and the next one, blended in over the last second).</summary>
    public static (ThrobPattern A, ThrobPattern B, float Mix) AutoAt(double t, float seconds)
    {
        double span = Math.Clamp(seconds, 3f, 600f);
        long slot = (long)Math.Floor(t / span);
        ThrobPattern Pick(long k) => AutoOrder[(int)(Hash(k, 11) * AutoOrder.Length) % AutoOrder.Length];
        double into = t - slot * span;
        float mix = into > span - 1.0 ? (float)((into - (span - 1.0)) / 1.0) : 0f;
        return (Pick(slot), Pick(slot + 1), mix);
    }

    private static (float Swell, float Lift) Level(ThrobSettings s, double t)
    {
        if(!s.Auto)
            return Beat(s.Pattern, t, s.Speed, s.Randomness);
        var (a, b, mix) = AutoAt(t, s.AutoSeconds);
        var va = Beat(a, t, s.Speed, s.Randomness);
        if(mix <= 0f)
            return va;
        var vb = Beat(b, t, s.Speed, s.Randomness);
        return (va.Swell + (vb.Swell - va.Swell) * mix, va.Lift + (vb.Lift - va.Lift) * mix);
    }

    // ---- Putting it on the bones

    private readonly record struct Place(int Partial, int Bone);

    private bool Find(in ActorSkeleton sk, string name, out Place place)
    {
        if(sk.TryFindExtraBone(name, _cache, out int p, out int b))
        {
            place = new Place(p, b);
            return true;
        }
        int body = sk.Info.IndexOf(name);
        place = new Place(0, body);
        return body >= 0;
    }

    private static Frame Get(in ActorSkeleton sk, Place at) => at.Partial == 0 ? sk.GetModelFrame(at.Bone) : sk.GetPartialFrame(at.Partial, at.Bone);
    private static Vector3 GetScale(in ActorSkeleton sk, Place at) => at.Partial == 0 ? sk.GetModelScale(at.Bone) : sk.GetPartialScale(at.Partial, at.Bone);

    private static void SetScale(in ActorSkeleton sk, Place at, Vector3 v)
    {
        if(at.Partial == 0) sk.SetModelScale(at.Bone, v);
        else sk.SetPartialScale(at.Partial, at.Bone, v);
    }

    private static void SetRotation(in ActorSkeleton sk, Place at, Quaternion q)
    {
        if(at.Partial == 0) sk.SetModelRotation(at.Bone, q);
        else sk.SetPartialRotation(at.Partial, at.Bone, q);
    }

    private static void SetPosition(in ActorSkeleton sk, Place at, Vector3 p)
    {
        if(at.Partial == 0) sk.SetModelPosition(at.Bone, p);
        else sk.SetPartialPosition(at.Partial, at.Bone, p);
    }

    private static bool Near(Vector3 a, Vector3 b) => Vector3.DistanceSquared(a, b) < 1e-10f;
    private static bool Near(Quaternion a, Quaternion b) => MathF.Abs(Quaternion.Dot(a, b)) > 0.9999999f;

    private void Step(nint address, ushort index, ThrobSettings s, State state, long now)
    {
        float dt = Math.Clamp((float)((now - state.LastTicks) / (double)Stopwatch.Frequency), 0f, 0.1f);
        state.LastTicks = now;
        float ease = MathF.Max(0.05f, s.Ease);
        state.Level = Math.Clamp(state.Level + (s.Enabled ? dt : -dt) / ease, 0f, 1f);
        state.Time += dt;
        if(!ActorSkeleton.TryGet(address, index, _cache, out var sk) || sk.Info.Count == 0)
            return;

        var shaft = new List<Place>();
        foreach(var name in Shaft)
            if(Find(sk, name, out var at))
                shaft.Add(at);
        if(shaft.Count < 2)
            return;

        float level = state.Level * state.Level * (3f - 2f * state.Level);
        if(InOral(address))
            level *= 1f + Math.Clamp(s.OralBoost, 0f, 3f);

        // What the pose has (with last frame's throb taken off if the game didn't rebuild it since).
        Frame Original(string key, Place at, out Vector3 scale)
        {
            var f = Get(sk, at);
            scale = GetScale(sk, at);
            if(state.Writes.TryGetValue(key, out var w) && w.Any)
            {
                if(Near(scale, w.Scale))
                    scale /= w.ScaleFactor;
                if(Near(f.Rotation, w.Rotation))
                    f.Rotation = Quaternion.Normalize(Quaternion.Inverse(w.Turn) * f.Rotation);
                if(Near(f.Position, w.Position))
                    f.Position -= w.Move;
            }
            return f;
        }

        int n = shaft.Count;
        var origin = new Frame[n];
        var originScale = new Vector3[n];
        for(int i = 0; i < n; i++)
            origin[i] = Original(Shaft[Math.Min(i, Shaft.Length - 1)] + "#" + i, shaft[i], out originScale[i]);
        // Put back what the pose has first (parents first), so this frame's throb goes on clean.
        for(int i = 0; i < n; i++)
        {
            SetRotation(sk, shaft[i], origin[i].Rotation);
            SetScale(sk, shaft[i], originScale[i]);
        }

        var baseDir = origin[n - 1].Position - origin[0].Position;
        if(baseDir.LengthSquared() < 1e-10f)
            return;
        baseDir = Vector3.Normalize(baseDir);
        var side = Vector3.Cross(baseDir, Vector3.UnitY);
        bool canLift = side.LengthSquared() > 1e-6f;
        if(canLift)
            side = Vector3.Normalize(side);

        float travel = Math.Clamp(s.Travel, 0f, 1f) * 0.09f; // seconds per bone
        float shown = 0f;
        for(int i = 0; i < n; i++)
        {
            var (swell, lift) = Level(s, state.Time - i * travel);
            // As the throb ends: how much the lift is falling away (what it had a moment ago, less what it has now).
            var (_, earlier) = Level(s, state.Time - i * travel - 0.12);
            float fall = Math.Clamp(earlier - lift, 0f, 1f) * 2.5f;
            fall = MathF.Min(fall, 1f) * level;
            swell *= level;
            lift *= level;
            if(i == 0)
                shown = swell;
            float along = n > 1 ? i / (float)(n - 1) : 0f;
            float girth = 1f + Math.Clamp(s.Swell, 0f, 1f) * swell * (1f - Math.Clamp(s.TipSwell, 0f, 1f) * 0.6f + Math.Clamp(s.TipSwell, 0f, 1f) * 0.9f * along);
            float longer = 1f + Math.Clamp(s.Length, 0f, 0.5f) * swell;

            var f = Get(sk, shaft[i]);
            // Which of the bone's own axes runs along the shaft (toward the next bone, or on from the one before).
            var next = i + 1 < n ? Get(sk, shaft[i + 1]).Position - f.Position : f.Position - Get(sk, shaft[i - 1]).Position;
            var local = Vector3.Abs(Vector3.Transform(next, Quaternion.Inverse(f.Rotation)));
            int axis = local.X >= local.Y && local.X >= local.Z ? 0 : local.Y >= local.Z ? 1 : 2;
            var factor = new Vector3(girth);
            if(axis == 0) factor.X = longer;
            else if(axis == 1) factor.Y = longer;
            else factor.Z = longer;
            SetScale(sk, shaft[i], originScale[i] * factor);

            // The lift: each bone turned up a little (the base most), so the whole shaft rises.
            float raise = Math.Clamp(s.Lift, 0f, 60f) * lift - Math.Clamp(s.Fall, 0f, 60f) * fall;
            if(canLift && MathF.Abs(raise) > 1e-5f)
            {
                float degrees = raise * (i == 0 ? 0.6f : 0.4f / (n - 1));
                var turn = Quaternion.CreateFromAxisAngle(side, degrees * MathF.PI / 180f); // (a positive turn about this side axis tips it up)
                var nowF = Get(sk, shaft[i]);
                SetRotation(sk, shaft[i], Quaternion.Normalize(turn * nowF.Rotation));
            }
        }
        state.Shown = shown;

        // What was written (to take it off again next frame if the pose isn't rebuilt).
        for(int i = 0; i < n; i++)
        {
            var key = Shaft[Math.Min(i, Shaft.Length - 1)] + "#" + i;
            if(!state.Writes.TryGetValue(key, out var w))
                state.Writes[key] = w = new Written();
            var f = Get(sk, shaft[i]);
            w.Scale = GetScale(sk, shaft[i]);
            w.ScaleFactor = new Vector3(SafeDiv(w.Scale.X, originScale[i].X), SafeDiv(w.Scale.Y, originScale[i].Y), SafeDiv(w.Scale.Z, originScale[i].Z));
            w.Rotation = f.Rotation;
            w.Turn = Quaternion.Normalize(f.Rotation * Quaternion.Inverse(origin[i].Rotation));
            w.Position = f.Position;
            w.Move = Vector3.Zero; // (the base doesn't move; the rest follow their parents)
            w.Any = true;
        }

        // The testicles drawn up a little on the strong beats.
        float balls = Math.Clamp(s.Testicles, 0f, 1f);
        var (bSwell, _) = Level(s, state.Time);
        float draw = balls * bSwell * level;
        foreach(var name in Balls)
        {
            if(!Find(sk, name, out var at))
                continue;
            var key = name;
            var f = Original(key, at, out var scale);
            var up = new Vector3(0f, 0.006f * draw, 0f);
            SetPosition(sk, at, f.Position + up);
            var shrink = new Vector3(1f - 0.04f * draw);
            SetScale(sk, at, scale * shrink);
            if(!state.Writes.TryGetValue(key, out var w))
                state.Writes[key] = w = new Written();
            w.Position = Get(sk, at).Position;
            w.Move = up;
            w.Scale = GetScale(sk, at);
            w.ScaleFactor = shrink;
            w.Rotation = Get(sk, at).Rotation;
            w.Turn = Quaternion.Identity;
            w.Any = true;
        }
    }

    private static float SafeDiv(float a, float b) => MathF.Abs(b) > 1e-8f ? a / b : 1f;
}
