using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Dalamud.Plugin.Services;
using Limber.Core;

namespace Limber.Rig;

/// <summary>How a part grows over time.</summary>
public enum ExpansionPattern
{
    Steady,        // grows evenly to full size
    Smooth,        // eases in and out of growing
    Surge,         // fast at first, slowing down as it fills
    Accelerating,  // slow at first, faster and faster
    Pulses,        // grows in pulses, a little more with each
    Heartbeat,     // grows on a heartbeat (two beats, a rest)
    Steps,         // sudden jumps, holding between
    Stutter,       // grows, stalls, grows again
    Spurts,        // random bursts of growth
    Pump,          // pumped up: each pump swells, and it settles back a little between
    Overinflate,   // overshoots its full size, then settles back to it
    Wobble,        // grows with a wobble that dies down
    Breathing,     // swells and shrinks back, over and over
    Throbbing,     // stays full, swelling more on each beat
    Burst,         // grows to full, then deflates all at once
    PingPong,      // grows, then shrinks back the same way
    Shrink,        // starts full and shrinks
}

/// <summary>What happens once a growth has played through.</summary>
public enum ExpansionRepeat
{
    Hold,     // stays at the end
    Loop,     // starts over
    Reverse,  // plays back down to the start, then up again
}

/// <summary>One part's expansion (breasts, butt, or penis).</summary>
public sealed class ExpansionPart
{
    public bool Enabled { get; set; }
    public ExpansionPattern Pattern { get; set; } = ExpansionPattern.Smooth;
    /// <summary>How much bigger at full (1 = twice the size).</summary>
    public float Amount { get; set; } = 1f;
    /// <summary>Seconds for one growth.</summary>
    public float Duration { get; set; } = 10f;
    public ExpansionRepeat Repeat { get; set; } = ExpansionRepeat.Hold;
    /// <summary>Beats / pumps / wobbles per minute (for the patterns that have them).</summary>
    public float Speed { get; set; } = 60f;
    /// <summary>How strong the pulses / beats / wobble are (0..1).</summary>
    public float Strength { get; set; } = 0.5f;
    /// <summary>Uneven growth (0..1).</summary>
    public float Randomness { get; set; } = 0.2f;
    /// <summary>Breasts: forward (+) or round (0) / wide (-). Butt: outward (+) or round. Penis: length (+) or girth (-).</summary>
    public float Shape { get; set; }
    /// <summary>Penis: the testicles grow too (0..1 of it).</summary>
    public float Testicles { get; set; } = 0.5f;
    /// <summary>Seconds to ease in / out when switched on / off.</summary>
    public float Ease { get; set; } = 1f;
}

/// <summary>One character's expansion (kept by name).</summary>
public sealed class ExpansionSettings
{
    public ExpansionPart Breasts { get; set; } = new();
    public ExpansionPart Butt { get; set; } = new();
    public ExpansionPart Penis { get; set; } = new() { Amount = 0.5f, Shape = 0.3f };

    public ExpansionSettings Copy() => Newtonsoft.Json.JsonConvert.DeserializeObject<ExpansionSettings>(Newtonsoft.Json.JsonConvert.SerializeObject(this))!;
}

/// <summary>
/// Expansion (Limber Animations): breasts, butt and penis growing over time with a choice of patterns: steady, eased,
/// in surges, pulses, heartbeats, steps, spurts, pumps, overinflating and settling, wobbling, breathing, throbbing,
/// bursting, back and forth, or shrinking; held, looped or played back down. Breasts and butt grow by scaling their
/// bones (the soft bodies grow with them); the penis lengthens (each bone placed further along the shaft) and
/// thickens, and the testicles can grow with it. Bone update, before the physics. Adult characters only. Only on
/// your screen.
/// </summary>
public sealed unsafe class ExpansionService
{
    private static readonly string[] BreastBones = ["j_mune_l", "j_mune_r"];
    private static readonly string[] ButtBones = ["iv_shiri_l", "iv_shiri_r"];
    private static readonly string[] BallBones = ["iv_kougan_l", "iv_kougan_r"];

    private readonly record struct Place(int Partial, int Bone);

    private sealed class Write
    {
        public Place At;
        public Vector3 Scale, Position, WrittenScale, WrittenPosition;
    }

    private sealed class PartState
    {
        public float Level;       // eased in / out 0..1
        public double Time;       // the pattern's clock (seconds)
        public float Shown;       // how grown now (for the page)
        public bool Was;
    }

    private sealed class State
    {
        public readonly PartState Breasts = new(), Butt = new(), Penis = new();
        public long LastTicks;
        // what the pose had before (to put back when the game hasn't rebuilt it), per bone (parents first), and what was written
        public readonly List<Write> Writes = new();
    }

    private readonly IObjectTable _objects;
    private readonly Func<Dictionary<string, ExpansionSettings>> _settings;
    private readonly SkeletonInfoCache _cache = new();
    private readonly Dictionary<nint, State> _states = new();
    private volatile (nint Address, ushort Index, ExpansionSettings Settings)[] _targets = [];
    private volatile Dictionary<(nint, int), float> _shown = new();

    public ExpansionService(IObjectTable objects, Func<Dictionary<string, ExpansionSettings>> settings)
    {
        _objects = objects;
        _settings = settings;
    }

    /// <summary>How grown a part is now (0 = its own size, 1 = full), -1 if it isn't expanding. Part: 0 breasts, 1 butt, 2 penis.</summary>
    public float ShownOf(nint address, int part) => _shown.TryGetValue((address, part), out var v) ? v : -1f;

    private readonly HashSet<(nint, int)> _restart = new();
    /// <summary>Start a part's growth over from the beginning.</summary>
    public void Restart(nint address, int part)
    {
        lock(_restart)
            _restart.Add((address, part));
    }

    /// <summary>Framework thread, once a frame.</summary>
    public void Refresh()
    {
        var all = _settings();
        if(all.Count == 0)
        {
            _targets = [];
            return;
        }
        var list = new List<(nint, ushort, ExpansionSettings)>();
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
        var shown = new Dictionary<(nint, int), float>();
        var seen = new HashSet<nint>();
        foreach(var (address, index, s) in targets)
        {
            seen.Add(address);
            try
            {
                if(!_states.TryGetValue(address, out var state))
                {
                    if(!s.Breasts.Enabled && !s.Butt.Enabled && !s.Penis.Enabled)
                        continue;
                    _states[address] = state = new State { LastTicks = now };
                }
                Step(address, index, s, state, now);
                bool any = false;
                foreach(var (part, ps) in new[] { (0, state.Breasts), (1, state.Butt), (2, state.Penis) })
                    if(ps.Level > 0f)
                    {
                        shown[(address, part)] = ps.Shown;
                        any = true;
                    }
                if(!any && !s.Breasts.Enabled && !s.Butt.Enabled && !s.Penis.Enabled)
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
        _shown = shown;
    }

    // ---- The patterns: how grown (0..1, can overshoot a little) at time t of a growth lasting d seconds

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

    // a quick swell and an easier fall (x seconds since it started, length l)
    private static float Pulse(double x, double l)
    {
        if(x < 0 || x > l * 3)
            return 0f;
        double rise = l * 0.25;
        return x < rise ? Smooth((float)(x / rise)) : (float)Math.Exp(-(x - rise) / (l * 0.45));
    }

    public static float GrowthAt(ExpansionPart p, double t)
    {
        float d = Math.Clamp(p.Duration, 0.2f, 3600f);
        double beat = 60.0 / Math.Clamp(p.Speed, 5f, 400f);
        float strength = Math.Clamp(p.Strength, 0f, 1f);
        float rnd = Math.Clamp(p.Randomness, 0f, 1f);
        // where in the growth (with the repeat), 0..1
        double cycle = t / d;
        float u;
        switch(p.Repeat)
        {
            case ExpansionRepeat.Loop:
                u = (float)(cycle - Math.Floor(cycle));
                break;
            case ExpansionRepeat.Reverse:
            {
                double c = cycle % 2.0;
                u = (float)(c <= 1.0 ? c : 2.0 - c);
                break;
            }
            default:
                u = (float)Math.Min(cycle, 1.0);
                break;
        }
        long n = (long)Math.Floor(t / beat);
        float wobbleRnd = 1f + rnd * (Hash(n, 5) - 0.5f) * 0.6f;
        switch(p.Pattern)
        {
            default:
            case ExpansionPattern.Steady:
                return u;
            case ExpansionPattern.Smooth:
                return Smooth(u);
            case ExpansionPattern.Surge:
                return 1f - (1f - u) * (1f - u) * (1f - u);
            case ExpansionPattern.Accelerating:
                return u * u * u;
            case ExpansionPattern.Pulses:
            {
                // the growth on its own, plus a pulse on each beat that leaves a little behind
                float pulse = Pulse(t - n * beat, beat * 0.5) * strength * 0.15f * wobbleRnd;
                return Smooth(u) * (1f + pulse);
            }
            case ExpansionPattern.Heartbeat:
            {
                double into = t - n * beat;
                float beatUp = Pulse(into, beat * 0.18) + 0.6f * Pulse(into - beat * 0.28, beat * 0.18);
                return Smooth(u) * (1f + beatUp * strength * 0.12f * wobbleRnd);
            }
            case ExpansionPattern.Steps:
            {
                // sudden jumps (one a beat), holding between
                int steps = Math.Max(2, (int)MathF.Round(d / (float)beat));
                float k = MathF.Floor(u * steps);
                float into = u * steps - k;
                return Math.Min(1f, (k + Smooth(into / 0.15f)) / steps);
            }
            case ExpansionPattern.Stutter:
            {
                // grow for a beat, stall for a beat
                int k = (int)Math.Floor(u * 8);
                float into = u * 8 - k;
                float done = (k / 2) / 4f;
                float part = k % 2 == 0 ? Smooth(into) / 4f : 1f / 4f;
                return Math.Min(1f, done + part);
            }
            case ExpansionPattern.Spurts:
            {
                // random-sized spurts at random moments, adding up to full size
                int count = Math.Max(3, (int)MathF.Round(d / (float)beat));
                float total = 0f, upTo = 0f;
                for(int i = 0; i < count; i++)
                    total += 0.3f + Hash(i, 11);
                for(int i = 0; i < count; i++)
                {
                    float at = (i + 0.2f + 0.6f * Hash(i, 13) * rnd) / count;
                    float size = (0.3f + Hash(i, 11)) / total;
                    upTo += size * Smooth((u - at) / 0.04f);
                }
                return upTo;
            }
            case ExpansionPattern.Pump:
            {
                // each pump swells it up, then it settles back a little before the next
                double into = t - n * beat;
                return Smooth(u) * (1f + Pulse(into, beat * 0.35) * strength * 0.18f);
            }
            case ExpansionPattern.Overinflate:
            {
                // past full, then settling back
                float over = 1f + 0.25f * strength;
                if(u < 0.7f)
                    return Smooth(u / 0.7f) * over;
                float settle = Smooth((u - 0.7f) / 0.3f);
                return over + (1f - over) * settle;
            }
            case ExpansionPattern.Wobble:
            {
                float wob = MathF.Sin((float)(t / beat * Math.Tau)) * strength * 0.2f * (1f - u * 0.7f);
                return Smooth(u) * (1f + wob);
            }
            case ExpansionPattern.Breathing:
                return 0.5f - 0.5f * MathF.Cos((float)(t / d * Math.Tau));
            case ExpansionPattern.Throbbing:
            {
                double into = t - n * beat;
                return Smooth(u) * (1f + Pulse(into, beat * 0.4) * strength * 0.25f * wobbleRnd);
            }
            case ExpansionPattern.Burst:
            {
                // up to full over most of it, then out all at once
                if(u < 0.88f)
                    return Smooth(u / 0.88f) * (1f + 0.1f * strength);
                return (1f + 0.1f * strength) * (1f - Smooth((u - 0.88f) / 0.04f));
            }
            case ExpansionPattern.PingPong:
            {
                double c = t / d % 2.0;
                return Smooth((float)(c <= 1.0 ? c : 2.0 - c));
            }
            case ExpansionPattern.Shrink:
                return 1f - Smooth(u);
        }
    }

    // ---- Putting it on the bones

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

    private void Step(nint address, ushort index, ExpansionSettings s, State state, long now)
    {
        float dt = Math.Clamp((float)((now - state.LastTicks) / (double)Stopwatch.Frequency), 0f, 0.1f);
        state.LastTicks = now;
        if(!ActorSkeleton.TryGet(address, index, _cache, out var sk) || sk.Info.Count == 0)
            return;

        // Put back what the pose had where the game didn't rebuild it since last frame (paused). Which ones are
        // looked at first (putting a parent back moves its children), then put back parents first.
        var stale = new List<(Write W, bool Scale, bool Position)>();
        foreach(var w in state.Writes)
            stale.Add((w, Near(GetScale(sk, w.At), w.WrittenScale), Near(Get(sk, w.At).Position, w.WrittenPosition)));
        foreach(var (w, scale, _) in stale)
            if(scale)
                SetScale(sk, w.At, w.Scale);
        foreach(var (w, _, position) in stale)
            if(position)
                SetPosition(sk, w.At, w.Position);
        state.Writes.Clear();

        int partIndex = 0;
        foreach(var (part, ps) in new[] { (s.Breasts, state.Breasts), (s.Butt, state.Butt), (s.Penis, state.Penis) })
        {
            int which = partIndex++;
            lock(_restart)
                if(_restart.Remove((address, which)))
                    ps.Time = 0;
            if(part.Enabled && !ps.Was)
                ps.Time = 0; // switched on: from the start
            ps.Was = part.Enabled;
            ps.Level = Math.Clamp(ps.Level + (part.Enabled ? dt : -dt) / MathF.Max(0.05f, part.Ease), 0f, 1f);
            if(ps.Level <= 0f)
            {
                ps.Shown = 0f;
                continue;
            }
            ps.Time += dt;
            float grown = GrowthAt(part, ps.Time);
            ps.Shown = grown;
            float level = Smooth(ps.Level);
            float extra = MathF.Max(-0.9f, Math.Clamp(part.Amount, -0.9f, 10f) * grown * level);
            float shape = Math.Clamp(part.Shape, -1f, 1f);
            switch(which)
            {
                case 0: Grow(sk, state, BreastBones, extra, shape); break;
                case 1: Grow(sk, state, ButtBones, extra, shape); break;
                case 2: GrowPenis(sk, state, extra, shape, Math.Clamp(part.Testicles, 0f, 1f)); break;
            }
        }
        Written(sk, state);
    }

    private static Write Remember(in ActorSkeleton sk, State state, Place at)
    {
        foreach(var w in state.Writes)
            if(w.At == at)
                return w;
        var made = new Write { At = at, Scale = GetScale(sk, at), Position = Get(sk, at).Position };
        state.Writes.Add(made);
        return made;
    }

    private static void Written(in ActorSkeleton sk, State state)
    {
        foreach(var w in state.Writes)
        {
            w.WrittenScale = GetScale(sk, w.At);
            w.WrittenPosition = Get(sk, w.At).Position;
        }
    }

    // A pair of bones (breasts, butt cheeks) scaled up: evenly, or more toward the front (shape +) or more across
    // (shape -). The front is the character's forward, found on the bone's own axes.
    private void Grow(in ActorSkeleton sk, State state, string[] names, float extra, float shape)
    {
        foreach(var name in names)
        {
            if(!Find(sk, name, out var at))
                continue;
            var w = Remember(sk, state, at);
            float k = MathF.Max(0.1f, 1f + extra);
            var factor = new Vector3(k);
            if(MathF.Abs(shape) > 1e-3f)
            {
                var local = Vector3.Abs(Vector3.Transform(Vector3.UnitZ, Quaternion.Inverse(Get(sk, at).Rotation)));
                int axis = local.X >= local.Y && local.X >= local.Z ? 0 : local.Y >= local.Z ? 1 : 2;
                // more (or less) of the growth forward; the rest across
                float forward = MathF.Max(0.1f, 1f + extra * (1f + 0.6f * shape));
                float across = MathF.Max(0.1f, 1f + extra * (1f - 0.4f * shape));
                factor = new Vector3(across);
                if(axis == 0) factor.X = forward;
                else if(axis == 1) factor.Y = forward;
                else factor.Z = forward;
            }
            SetScale(sk, at, w.Scale * factor);
        }
    }

    // The penis: thicker (each bone scaled), and longer (each bone placed further along the shaft than it was:
    // scaling a bone doesn't move its children, so they're carried out). The testicles grow too.
    private void GrowPenis(in ActorSkeleton sk, State state, float extra, float shape, float balls)
    {
        var chain = new List<Place>();
        foreach(var name in DynamicBoneService.PenisBones)
            if(Find(sk, name, out var at))
                chain.Add(at);
        if(chain.Count >= 2)
        {
            // shape + = more of it in length, - = more in girth
            float lengthK = MathF.Max(0.1f, 1f + extra * (1f + 0.6f * shape));
            float girthK = MathF.Max(0.1f, 1f + extra * (1f - 0.6f * shape));
            var writes = new Write[chain.Count];
            for(int i = 0; i < chain.Count; i++)
                writes[i] = Remember(sk, state, chain[i]);
            // scales first (set outright, so nothing compounds)
            for(int i = 0; i < chain.Count; i++)
                SetScale(sk, chain[i], writes[i].Scale * girthK);
            // then each bone further out from the one before (root out)
            for(int i = 1; i < chain.Count; i++)
            {
                var before = Get(sk, chain[i - 1]).Position;
                var step = writes[i].Position - writes[i - 1].Position;
                SetPosition(sk, chain[i], before + step * lengthK);
            }
        }
        if(balls > 0f)
            foreach(var name in BallBones)
            {
                if(!Find(sk, name, out var at))
                    continue;
                var w = Remember(sk, state, at);
                SetScale(sk, at, w.Scale * MathF.Max(0.1f, 1f + extra * balls));
            }
    }
}
