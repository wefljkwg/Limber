using System;
using System.Collections.Generic;
using System.Diagnostics;
using Dalamud.Plugin.Services;

namespace Limber.Rig;

/// <summary>How the size changes on the way from the start size to the end size.</summary>
public enum SizeChangePattern
{
    Steady,       // the same factor every second (even growth to the eye, at any size)
    Smooth,       // eased in and out
    Surge,        // fast at first, slowing
    Accelerating, // slow at first, faster and faster
    Pulses,       // in pulses, swelling a little past where it's heading on each
    Steps,        // sudden jumps, holding between
    Spurts,       // random-sized jumps at uneven moments
}

/// <summary>One character's height growth / shrinking (kept by name).</summary>
public sealed class SizeChangeSettings
{
    public bool Enabled { get; set; }
    /// <summary>The size it starts from and the size it ends at (1 = its own size).</summary>
    public float From { get; set; } = 1f;
    public float To { get; set; } = 10f;
    /// <summary>Seconds from the start size to the end size.</summary>
    public float Duration { get; set; } = 20f;
    public SizeChangePattern Pattern { get; set; } = SizeChangePattern.Smooth;
    public ExpansionRepeat Repeat { get; set; } = ExpansionRepeat.Hold;
    /// <summary>Beats a minute (Pulses, Steps, Spurts).</summary>
    public float Speed { get; set; } = 30f;
    /// <summary>How big each pulse is (Pulses), how uneven the spurts are (Spurts), 0..1.</summary>
    public float Strength { get; set; } = 0.5f;
    /// <summary>Seconds to go back to its own size when stopped (0 = at once).</summary>
    public float Return { get; set; } = 2f;

    public SizeChangeSettings Copy() => (SizeChangeSettings)MemberwiseClone();
}

/// <summary>
/// Height growth and shrinking (Limber Animations): the whole character scaled about its feet over time, from ant-sized
/// up to a city-sized giant, on the model's own size (so the game keeps drawing it whole at any size). Only on your
/// screen. Bone update, before the Actor offsets are written.
/// </summary>
public sealed class SizeChangeService
{
    public const float MinSize = 0.0005f, MaxSize = 2000f;

    private sealed class State
    {
        public double Time;
        public float Log;       // ln(size) now
        public bool Was;
        public long LastTicks;
        public ushort Index;
    }

    private readonly IObjectTable _objects;
    private readonly Func<Dictionary<string, SizeChangeSettings>> _settings;
    private readonly ActorTransforms _actors;
    private readonly Dictionary<nint, State> _states = new();
    private volatile (nint Address, ushort Index, SizeChangeSettings Settings)[] _targets = [];
    private volatile Dictionary<nint, float> _shown = new();
    private readonly HashSet<nint> _restart = new();

    public SizeChangeService(IObjectTable objects, Func<Dictionary<string, SizeChangeSettings>> settings, ActorTransforms actors)
    {
        _objects = objects;
        _settings = settings;
        _actors = actors;
    }

    /// <summary>The character's size now (1 = its own), -1 when it isn't changed.</summary>
    public float SizeOf(nint address) => _shown.TryGetValue(address, out var v) ? v : -1f;

    /// <summary>Start over from the start size.</summary>
    public void Restart(nint address)
    {
        lock(_restart)
            _restart.Add(address);
    }

    /// <summary>Framework thread, once a frame.</summary>
    public void Refresh()
    {
        var all = _settings();
        if(all.Count == 0 && _states.Count == 0)
        {
            _targets = [];
            return;
        }
        var list = new List<(nint, ushort, SizeChangeSettings)>();
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

    /// <summary>Bone update stage (before "Limber actors").</summary>
    public void UpdateFrame()
    {
        var targets = _targets;
        long now = Stopwatch.GetTimestamp();
        var seen = new HashSet<nint>();
        var shown = new Dictionary<nint, float>();
        HashSet<nint>? restart = null;
        lock(_restart)
            if(_restart.Count > 0)
            {
                restart = new HashSet<nint>(_restart);
                _restart.Clear();
            }
        foreach(var (address, index, s) in targets)
        {
            seen.Add(address);
            if(!_states.TryGetValue(address, out var state))
            {
                if(!s.Enabled)
                    continue;
                _states[address] = state = new State { LastTicks = now };
            }
            state.Index = index;
            float dt = Math.Clamp((float)((now - state.LastTicks) / (double)Stopwatch.Frequency), 0f, 0.25f);
            state.LastTicks = now;
            if(s.Enabled && (!state.Was || (restart?.Contains(address) ?? false)))
                state.Time = 0;
            state.Was = s.Enabled;

            float goal;
            float follow; // seconds to catch up with where it's heading (smooths sudden setting changes)
            if(s.Enabled)
            {
                state.Time += dt;
                goal = LogAt(s, state.Time);
                follow = s.Pattern is SizeChangePattern.Steps or SizeChangePattern.Spurts ? 0.06f : 0.12f;
            }
            else
            {
                goal = 0f;
                follow = MathF.Max(0.001f, s.Return) / 3f;
            }
            if(dt > 0f)
                state.Log += (goal - state.Log) * (1f - MathF.Exp(-dt / follow));
            if(!float.IsFinite(state.Log))
                state.Log = 0f;

            if(!s.Enabled && MathF.Abs(state.Log) < 1e-3f)
            {
                _states.Remove(address);
                _actors.SetAnimatedScale(address, index, 1f);
                continue;
            }
            float size = Math.Clamp(MathF.Exp(state.Log), MinSize, MaxSize);
            _actors.SetAnimatedScale(address, index, size);
            shown[address] = size;
        }
        // Gone (or no longer allowed): back to its own size.
        if(_states.Count > seen.Count)
            foreach(var (a, st) in new List<KeyValuePair<nint, State>>(_states))
                if(!seen.Contains(a))
                {
                    _actors.SetAnimatedScale(a, st.Index, 1f);
                    _states.Remove(a);
                }
        _shown = shown;
    }

    private static float Ln(float v) => MathF.Log(Math.Clamp(v, MinSize, MaxSize));

    /// <summary>ln(size) at time t (seconds since it started).</summary>
    public static float LogAt(SizeChangeSettings s, double t)
    {
        float from = Ln(s.From), to = Ln(s.To);
        float d = Math.Clamp(s.Duration, 0.1f, 36000f);
        double x;
        switch(s.Repeat)
        {
            case ExpansionRepeat.Loop:
                x = t % d / d;
                break;
            case ExpansionRepeat.Reverse:
            {
                double c = t % (2 * d) / d;
                x = c <= 1 ? c : 2 - c;
                break;
            }
            default:
                x = Math.Min(t / d, 1.0);
                break;
        }
        float f = Fraction(s, (float)x, d, (long)(t / (2 * d)));
        return from + (to - from) * f;
    }

    // How far along (0..1, a pulse can go a little past) at x of the way through.
    private static float Fraction(SizeChangeSettings s, float x, float d, long cycle)
    {
        x = Math.Clamp(x, 0f, 1f);
        float beats = MathF.Max(1f, d * Math.Clamp(s.Speed, 1f, 600f) / 60f);
        float strength = Math.Clamp(s.Strength, 0f, 1f);
        switch(s.Pattern)
        {
            case SizeChangePattern.Steady:
                return x;
            case SizeChangePattern.Surge:
                return 1f - MathF.Pow(1f - x, 3f);
            case SizeChangePattern.Accelerating:
                return x * x * x;
            case SizeChangePattern.Pulses:
            {
                // Eased overall, each beat swelling past it and settling back.
                float phase = x * beats % 1f;
                float bump = MathF.Sin(phase * MathF.PI) * MathF.Exp(-3f * phase);
                return Smooth(x) + bump * strength * 0.6f / beats * (1f - x * x);
            }
            case SizeChangePattern.Steps:
            {
                int n = Math.Max(2, (int)MathF.Round(beats));
                return MathF.Min(1f, MathF.Floor(x * n + 1e-4f) / n);
            }
            case SizeChangePattern.Spurts:
            {
                // Uneven jumps: each beat adds a random share of the way (the shares add up to all of it).
                int n = Math.Max(2, (int)MathF.Round(beats));
                int done = Math.Min(n, (int)MathF.Floor(x * n + 1e-4f));
                float total = 0f, upTo = 0f;
                for(int i = 0; i < n; i++)
                {
                    float w = 1f - strength + strength * 2f * Hash(cycle * 1000 + i);
                    total += w;
                    if(i < done)
                        upTo += w;
                }
                return total > 0f ? upTo / total : x;
            }
            default:
                return Smooth(x);
        }
    }

    private static float Smooth(float x) => x * x * (3f - 2f * x);

    private static float Hash(long n)
    {
        unchecked
        {
            ulong v = (ulong)n * 0x9E3779B97F4A7C15UL;
            v ^= v >> 31;
            v *= 0x94D049BB133111EBUL;
            v ^= v >> 29;
            return (v & 0xFFFFFF) / (float)0x1000000;
        }
    }
}
