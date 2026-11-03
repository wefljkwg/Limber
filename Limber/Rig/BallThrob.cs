using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Dalamud.Plugin.Services;
using Limber.Core;

namespace Limber.Rig;

/// <summary>Testicle throbbing patterns (Limber Animations page).</summary>
public enum BallThrobPattern
{
    Pulse,       // an even pulse on every beat
    Heartbeat,   // two pulses close together, then a rest
    Clench,      // drawn up tight and held a moment, then let go
    Bounce,      // bobbing up and down
    Pump,        // rhythmic contractions, quick up, slow down
    Alternate,   // left, then right
    Shiver,      // a fast tremble
    Rolling,     // round in small circles, each its own way
    Climax,      // strong contractions, close together, then further apart and weaker; a rest; again
    Twitch,      // sudden jerks at random moments
    Breathing,   // slow swelling and easing
    WithPenis,   // on the penis throbbing's beat (when that's on)
}

/// <summary>One character's testicle throbbing (kept by name).</summary>
public sealed class BallThrobSettings
{
    public bool Enabled { get; set; }
    public BallThrobPattern Pattern { get; set; } = BallThrobPattern.Pulse;
    /// <summary>Changes pattern by itself every <see cref="AutoSeconds"/> (blending from one to the next).</summary>
    public bool Auto { get; set; }
    public float AutoSeconds { get; set; } = 20f;
    /// <summary>Beats per minute.</summary>
    public float Speed { get; set; } = 60f;
    /// <summary>How much bigger on a beat (0.1 = 10%).</summary>
    public float Swell { get; set; } = 0.08f;
    /// <summary>How far they draw up toward the body on a beat (cm).</summary>
    public float DrawUp { get; set; } = 0.6f;
    /// <summary>How much they tighten (narrower, rounder) as they draw up (0..1).</summary>
    public float Squeeze { get; set; } = 0.3f;
    /// <summary>Up / down bob (cm), for the bouncing and rolling patterns and a little on every beat.</summary>
    public float Bounce { get; set; } = 0.3f;
    /// <summary>Side to side (cm).</summary>
    public float Sway { get; set; } = 0.15f;
    /// <summary>How far apart in time the two are (0 = together, 1 = half a beat apart).</summary>
    public float Offset { get; set; } = 0.15f;
    /// <summary>Uneven timing and strength (0 = machine-like, 1 = very uneven).</summary>
    public float Randomness { get; set; } = 0.3f;
    /// <summary>Seconds to ease in and out.</summary>
    public float Ease { get; set; } = 1.5f;

    public BallThrobSettings Copy() => (BallThrobSettings)MemberwiseClone();
}

/// <summary>
/// Testicle throbbing: the testicles pulse by themselves while it's on, separately from the penis throbbing (either,
/// or both at once), in a chosen pattern or changing patterns on their own: swelling, drawing up and tightening,
/// bouncing, swaying, each a little after the other. Bone update, after the penis throbbing (on top of it, and of
/// the dynamic bones). Only on your screen; adult characters only.
/// </summary>
public sealed unsafe class BallThrobService
{
    private static readonly string[] Balls = ["iv_kougan_l", "iv_kougan_r"];
    private static readonly BallThrobPattern[] AutoOrder =
    [
        BallThrobPattern.Pulse, BallThrobPattern.Heartbeat, BallThrobPattern.Pump, BallThrobPattern.Bounce, BallThrobPattern.Clench,
        BallThrobPattern.Rolling, BallThrobPattern.Alternate, BallThrobPattern.Breathing, BallThrobPattern.Twitch, BallThrobPattern.Climax,
    ];

    private readonly record struct Place(int Partial, int Bone);

    private sealed class Write
    {
        public Place At;
        public Vector3 Scale, Position, WrittenScale, WrittenPosition;
    }

    private sealed class State
    {
        public float Level;
        public double Time;
        public long LastTicks;
        public readonly List<Write> Writes = new();
    }

    private readonly IObjectTable _objects;
    private readonly Func<Dictionary<string, BallThrobSettings>> _settings;
    private readonly SkeletonInfoCache _cache = new();
    private readonly Dictionary<nint, State> _states = new();
    private volatile (nint Address, ushort Index, BallThrobSettings Settings)[] _targets = [];
    private volatile Dictionary<nint, float> _levels = new();

    /// <summary>The penis throbbing's beat now (for the "with the penis" pattern), or -1.</summary>
    public Func<nint, float> PenisBeat = _ => -1f;

    public BallThrobService(IObjectTable objects, Func<Dictionary<string, BallThrobSettings>> settings)
    {
        _objects = objects;
        _settings = settings;
    }

    /// <summary>The beat now (for the page's meter), or -1 when it isn't running.</summary>
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
        var list = new List<(nint, ushort, BallThrobSettings)>();
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
                float shown = Step(address, index, s, state, now);
                if(state.Level > 0f)
                    levels[address] = shown;
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

    // ---- The patterns: for one testicle at time t, how much of each movement (0..1, bob and sway -1..1)

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

    // A quick rise and an easier fall, x seconds after it started (length l).
    private static float Pulse(double x, double l)
    {
        if(x < 0 || x > l * 4)
            return 0f;
        double rise = l * 0.25;
        return x < rise ? Smooth((float)(x / rise)) : (float)Math.Exp(-(x - rise) / (l * 0.4));
    }

    private readonly record struct Motion(float Swell, float Up, float Bob, float Sway);

    private Motion PatternAt(BallThrobPattern pattern, BallThrobSettings s, double t, int side, nint address)
    {
        double beat = 60.0 / Math.Clamp(s.Speed, 5f, 300f);
        float rnd = Math.Clamp(s.Randomness, 0f, 1f);
        long n = (long)Math.Floor(t / beat);
        double into = t - n * beat;
        float strength = 1f + rnd * (Hash(n, 3 + side) - 0.5f) * 0.8f;
        switch(pattern)
        {
            default:
            case BallThrobPattern.Pulse:
            {
                float p = Pulse(into, beat * 0.35) * strength;
                return new Motion(p, p * 0.6f, p * 0.2f, 0f);
            }
            case BallThrobPattern.Heartbeat:
            {
                float p = (Pulse(into, beat * 0.16) + 0.65f * Pulse(into - beat * 0.26, beat * 0.16)) * strength;
                return new Motion(p, p * 0.5f, p * 0.15f, 0f);
            }
            case BallThrobPattern.Clench:
            {
                // up over a third of the beat, held, then let go
                float u = (float)(into / beat);
                float p = u < 0.3f ? Smooth(u / 0.3f) : u < 0.6f ? 1f : 1f - Smooth((u - 0.6f) / 0.35f);
                p *= strength;
                return new Motion(p * 0.5f, p, 0f, 0f);
            }
            case BallThrobPattern.Bounce:
            {
                float b = MathF.Sin((float)(t / beat * Math.Tau)) * strength;
                return new Motion(0.1f * MathF.Max(b, 0f), 0f, b, 0f);
            }
            case BallThrobPattern.Pump:
            {
                float u = (float)(into / beat);
                float p = u < 0.15f ? Smooth(u / 0.15f) : 1f - Smooth((u - 0.15f) / 0.85f);
                p *= strength;
                return new Motion(p * 0.7f, p * 0.9f, -p * 0.2f, 0f);
            }
            case BallThrobPattern.Alternate:
            {
                // each its own beat, half a beat apart (the offset slider adds to it)
                double x = t / beat + (side > 0 ? 0.5 : 0.0);
                double k = x - Math.Floor(x);
                float p = Pulse(k * beat, beat * 0.3) * strength;
                return new Motion(p, p * 0.7f, p * 0.3f, 0f);
            }
            case BallThrobPattern.Shiver:
            {
                float f = (float)(t * Math.Tau * Math.Clamp(s.Speed, 5f, 300f) / 60.0 * 6.0);
                float b = (MathF.Sin(f) * 0.6f + MathF.Sin(f * 1.7f + side) * 0.4f) * (0.6f + 0.4f * strength);
                return new Motion(0.15f + 0.1f * b, 0.4f, b * 0.4f, b * 0.3f);
            }
            case BallThrobPattern.Rolling:
            {
                float a = (float)(t / beat * Math.Tau) * side;
                return new Motion(0.1f, 0.2f, MathF.Sin(a) * strength, MathF.Cos(a) * strength);
            }
            case BallThrobPattern.Climax:
            {
                // a set of 8 contractions getting further apart and weaker, then a rest as long as them
                double set = beat * 12;
                double x = t % set;
                float p = 0f;
                double at = 0;
                for(int i = 0; i < 8; i++)
                {
                    double gap = beat * (0.45 + i * 0.1);
                    p = MathF.Max(p, Pulse(x - at, beat * 0.25) * (1f - i * 0.1f));
                    at += gap;
                }
                p *= strength;
                return new Motion(p, p, -p * 0.3f, 0f);
            }
            case BallThrobPattern.Twitch:
            {
                // on some beats (more with randomness), a short jerk at a random moment of it
                float chance = 0.35f + 0.4f * rnd;
                if(Hash(n, 7) > chance)
                    return default;
                double when = beat * 0.7 * Hash(n, 9 + side);
                float p = Pulse(into - when, beat * 0.08) * (0.6f + 0.6f * Hash(n, 13));
                return new Motion(p * 0.3f, p, p * 0.5f, (Hash(n, 17 + side) - 0.5f) * p);
            }
            case BallThrobPattern.Breathing:
            {
                float p = 0.5f - 0.5f * MathF.Cos((float)(t / (beat * 4) * Math.Tau));
                return new Motion(p, p * 0.3f, -p * 0.2f, 0f);
            }
            case BallThrobPattern.WithPenis:
            {
                float p = Math.Clamp(PenisBeat(address), 0f, 1f);
                return new Motion(p, p * 0.8f, p * 0.2f, 0f);
            }
        }
    }

    private Motion Blend(Motion a, Motion b, float k) => new(a.Swell + (b.Swell - a.Swell) * k, a.Up + (b.Up - a.Up) * k, a.Bob + (b.Bob - a.Bob) * k, a.Sway + (b.Sway - a.Sway) * k);

    private Motion MotionAt(BallThrobSettings s, double t, int side, nint address)
    {
        if(!s.Auto)
            return PatternAt(s.Pattern, s, t, side, address);
        // changing pattern by itself, blending over the last 2 seconds of each
        double span = Math.Clamp(s.AutoSeconds, 3f, 300f);
        long k = (long)Math.Floor(t / span);
        double into = t - k * span;
        var a = AutoOrder[(int)((k % AutoOrder.Length + AutoOrder.Length) % AutoOrder.Length)];
        var b = AutoOrder[(int)(((k + 1) % AutoOrder.Length + AutoOrder.Length) % AutoOrder.Length)];
        float blend = Smooth((float)((into - (span - 2.0)) / 2.0));
        var ma = PatternAt(a, s, t, side, address);
        return blend <= 0f ? ma : Blend(ma, PatternAt(b, s, t, side, address), blend);
    }

    // ---- On the bones

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

    private static void SetPosition(in ActorSkeleton sk, Place at, Vector3 v)
    {
        if(at.Partial == 0) sk.SetModelPosition(at.Bone, v);
        else sk.SetPartialPosition(at.Partial, at.Bone, v);
    }

    private static bool Near(Vector3 a, Vector3 b) => Vector3.DistanceSquared(a, b) < 1e-10f;

    private float Step(nint address, ushort index, BallThrobSettings s, State state, long now)
    {
        float dt = Math.Clamp((float)((now - state.LastTicks) / (double)Stopwatch.Frequency), 0f, 0.1f);
        state.LastTicks = now;
        state.Level = Math.Clamp(state.Level + (s.Enabled ? dt : -dt) / MathF.Max(0.05f, s.Ease), 0f, 1f);
        state.Time += dt;
        if(!ActorSkeleton.TryGet(address, index, _cache, out var sk) || sk.Info.Count == 0)
            return 0f;

        // What it wrote last frame off again where the game didn't rebuild the pose since (paused).
        var stale = new List<(Write W, bool Scale, bool Position)>();
        foreach(var w in state.Writes)
            stale.Add((w, Near(GetScale(sk, w.At), w.WrittenScale), Near(Get(sk, w.At).Position, w.WrittenPosition)));
        foreach(var (w, scale, position) in stale)
        {
            if(scale)
                SetScale(sk, w.At, w.Scale);
            if(position)
                SetPosition(sk, w.At, w.Position);
        }
        state.Writes.Clear();
        if(state.Level <= 0f)
            return 0f;

        float level = Smooth(state.Level);
        float shown = 0f;
        // Which way is up and sideways for the body (from the hips: model space turns with the character).
        int hips = sk.Info.IndexOf("j_kosi");
        var hipRot = hips >= 0 ? sk.GetModelFrame(hips).Rotation : Quaternion.Identity;
        var up = Vector3.Transform(Vector3.UnitY, hipRot);
        var across = Vector3.Transform(Vector3.UnitX, hipRot);
        for(int k = 0; k < Balls.Length; k++)
        {
            if(!Find(sk, Balls[k], out var at))
                continue;
            int side = k == 0 ? 1 : -1;
            // the second a little after the first (offset), unless the pattern does it itself
            double t = state.Time - (side < 0 ? Math.Clamp(s.Offset, 0f, 1f) * 0.5 * 60.0 / Math.Clamp(s.Speed, 5f, 300f) : 0.0);
            var m = MotionAt(s, t, side, address);
            shown = MathF.Max(shown, MathF.Max(m.Swell, MathF.Max(m.Up, MathF.Abs(m.Bob))));
            var w = new Write { At = at, Scale = GetScale(sk, at), Position = Get(sk, at).Position };
            float swell = 1f + Math.Clamp(s.Swell, 0f, 1f) * m.Swell * level;
            float squeeze = Math.Clamp(s.Squeeze, 0f, 1f) * Math.Clamp(m.Up, 0f, 1f) * level;
            // tightening: a little narrower across and rounder as they draw up
            var factor = new Vector3(swell * (1f - 0.18f * squeeze), swell * (1f - 0.1f * squeeze), swell * (1f - 0.12f * squeeze));
            SetScale(sk, at, w.Scale * factor);
            var move = up * (Math.Clamp(s.DrawUp, 0f, 5f) * 0.01f * Math.Clamp(m.Up, 0f, 1.5f) + Math.Clamp(s.Bounce, 0f, 5f) * 0.01f * m.Bob)
                + across * (Math.Clamp(s.Sway, 0f, 5f) * 0.01f * m.Sway * side);
            SetPosition(sk, at, w.Position + move * level);
            w.WrittenScale = GetScale(sk, at);
            w.WrittenPosition = Get(sk, at).Position;
            state.Writes.Add(w);
        }
        return shown * level;
    }
}
