using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin.Services;
using Limber.Core;

namespace Limber.Rig;

/// <summary>
/// Steam / musk (Skin effects page): faint wisps rising off a character's skin (the whole body, the crotch, the
/// chest, the armpits) or their breath, drifting with movement and fading as they rise. Worked out here (framework
/// thread) as soft puffs; drawn by <see cref="Limber.Gpu.SteamRenderer"/> into the game's picture, hidden behind
/// what's in front of them. Only on your screen.
/// </summary>
public sealed unsafe class SteamService
{
    [Flags]
    public enum Areas
    {
        None = 0,
        Body = 1,
        Crotch = 2,
        Chest = 4,
        Armpits = 8,
        Breath = 16,
        Head = 32,
        Penis = 64,
    }

    /// <summary>One puff as the renderer reads it (32 bytes): where, how big; how strong, a seed.</summary>
    public struct Puff
    {
        public Vector4 PosSize;
        public Vector4 Data;
    }

    private sealed class Particle
    {
        public Vector3 Pos, Vel;
        public float Age, Life, Size0, Size1, Seed, Strength;
    }

    private sealed class State
    {
        public float Amount;
        public Vector3 LastRoot;
        public bool HaveRoot;
        public double Carry;
        public bool Seen;
    }

    public const int MaxPuffs = 2000;

    private readonly IObjectTable _objects;
    private readonly Func<Dictionary<string, SkinEffectSettings>> _settings;
    private readonly SkeletonInfoCache _cache = new();
    private readonly List<Particle> _live = new();
    private readonly Dictionary<nint, State> _states = new();
    private readonly Random _random = new();
    private readonly object _lock = new();
    private Puff[] _published = new Puff[256];
    private int _publishedCount;
    private Vector3[] _probes = [];
    private float _time;

    /// <summary>The running one (for the renderer and the page).</summary>
    public static SteamService? Instance { get; private set; }

    /// <summary>The colour and opacity the renderer uses (the chosen character's, or the last one's).</summary>
    public Vector3 Color { get; private set; } = new(0.92f, 0.92f, 0.95f);
    public float Opacity { get; private set; } = 0.35f;
    public float Time => _time;

    public SteamService(IObjectTable objects, Func<Dictionary<string, SkinEffectSettings>> settings)
    {
        _objects = objects;
        _settings = settings;
        Instance = this;
    }

    /// <summary>How many puffs are up now (the page's readout).</summary>
    public int Count => _publishedCount;

    /// <summary>Framework thread, once a frame.</summary>
    public void Update(bool here, double seconds)
    {
        float dt = (float)Math.Clamp(seconds, 0.0, 0.1);
        _time += dt;
        var all = _settings();
        foreach(var st in _states.Values)
            st.Seen = false;
        var probes = new List<Vector3>();
        if(here)
        {
            foreach(var obj in _objects)
            {
                if(obj is not Dalamud.Game.ClientState.Objects.Types.ICharacter || obj.Address == 0)
                    continue;
                string name = obj.Name.TextValue;
                SkinEffectSettings? s = null;
                if(name.Length > 0 && all.TryGetValue(name, out var found))
                    s = found;
                bool known = _states.TryGetValue(obj.Address, out var state);
                float want = s != null && s.Enabled ? Math.Clamp(s.Steam, 0f, 1f) : 0f;
                if(want <= 0f && !known)
                    continue;
                if(state == null)
                    _states[obj.Address] = state = new State();
                state.Seen = true;
                // (fades in and out with the page's Fade)
                float ramp = MathF.Max(s?.RampSeconds ?? 2f, 0.05f);
                state.Amount = Approach(state.Amount, want, dt / ramp);
                if(state.Amount <= 0.001f || s == null)
                    continue;
                if(!ActorSkeleton.TryGet(obj.Address, obj.ObjectIndex, _cache, out var sk))
                    continue;
                Color = s.SteamColor;
                Opacity = Math.Clamp(s.SteamOpacity, 0f, 1f);
                Emit(sk, s, state, dt, probes);
            }
        }
        foreach(var key in new List<nint>(_states.Keys))
            if(!_states[key].Seen)
                _states.Remove(key);

        // Each puff: rises (warm air), drifts and curls, slows, grows and fades.
        for(int i = _live.Count - 1; i >= 0; i--)
        {
            var p = _live[i];
            p.Age += dt;
            if(p.Age >= p.Life)
            {
                _live.RemoveAt(i);
                continue;
            }
            float a = p.Age / p.Life;
            float t = _time * 0.7f + p.Seed * 13f;
            var swirl = new Vector3(MathF.Sin(t * 1.3f + p.Pos.Y * 4f), 0f, MathF.Cos(t * 1.1f + p.Pos.X * 4f)) * 0.09f;
            p.Vel += (new Vector3(0f, 0.18f, 0f) + swirl) * dt;
            p.Vel *= MathF.Exp(-dt * 1.4f);
            p.Pos += p.Vel * dt;
            _ = a;
        }

        // Published for the render thread.
        lock(_lock)
        {
            if(_published.Length < _live.Count)
                _published = new Puff[Math.Max(_live.Count, _published.Length * 2)];
            int n = 0;
            foreach(var p in _live)
            {
                float a = p.Age / p.Life;
                float fadeIn = Smooth(0f, 0.18f, a);
                float fadeOut = MathF.Pow(1f - a, 1.6f);
                float size = p.Size0 + (p.Size1 - p.Size0) * MathF.Sqrt(a);
                _published[n++] = new Puff
                {
                    PosSize = new Vector4(p.Pos, size),
                    Data = new Vector4(p.Strength * fadeIn * fadeOut, p.Seed, a, 0f),
                };
            }
            _publishedCount = n;
            if(probes.Count > 0)
                _probes = probes.ToArray();
        }
    }

    // New puffs at each area this character has on, by how much steam there is.
    private void Emit(ActorSkeleton sk, SkinEffectSettings s, State state, float dt, List<Vector3> probes)
    {
        var areas = (Areas)s.SteamAreas;
        if(areas == Areas.None)
            areas = Areas.Body;
        var up = Vector3.Transform(Vector3.UnitY, sk.RootRotation);
        var forward = Vector3.Transform(Vector3.UnitZ, sk.RootRotation);
        float scale = MathF.Max(sk.RootScale.Y, 0.3f);
        // (moving: the steam is left behind a little, as it would be)
        var root = sk.RootPosition;
        var moved = state.HaveRoot && dt > 0f ? (root - state.LastRoot) / dt : Vector3.Zero;
        if(moved.Length() > 20f)
            moved = Vector3.Zero;
        state.LastRoot = root;
        state.HaveRoot = true;

        bool Bone(string name, out Vector3 at)
        {
            at = default;
            if(!sk.TryGetDrawnWorldFrame(name, _cache, out var f) || !MathUtil.IsFinite(f.Position))
                return false;
            at = f.Position;
            return true;
        }
        var sources = new List<(Vector3 At, float Spread, Vector3 Push, float Weight)>();
        if(Bone("j_kosi", out var hips))
            probes.Add(hips);
        if(Bone("j_sebo_c", out var chestBone))
            probes.Add(chestBone);
        if(areas.HasFlag(Areas.Body))
            foreach(var b in new[] { "j_sebo_a", "j_sebo_b", "j_sebo_c", "j_kosi", "j_kubi", "j_asi_a_l", "j_asi_a_r", "j_ude_a_l", "j_ude_a_r", "j_asi_b_l", "j_asi_b_r" })
                if(Bone(b, out var at))
                    sources.Add((at, 0.09f * scale, Vector3.Zero, 0.6f));
        if(areas.HasFlag(Areas.Crotch) && Bone("j_asi_a_l", out var hl) && Bone("j_asi_a_r", out var hr))
        {
            var at = (hl + hr) * 0.5f - up * 0.06f * scale + forward * 0.04f * scale;
            sources.Add((at, 0.04f * scale, forward * 0.05f, 1.6f));
        }
        if(areas.HasFlag(Areas.Chest))
        {
            bool any = false;
            foreach(var b in new[] { "j_mune_l", "j_mune_r" })
                if(Bone(b, out var at))
                {
                    sources.Add((at + forward * 0.05f * scale, 0.05f * scale, forward * 0.04f, 1f));
                    any = true;
                }
            if(!any && Bone("j_sebo_c", out var c))
                sources.Add((c + forward * 0.1f * scale, 0.07f * scale, forward * 0.04f, 1.6f));
        }
        if(areas.HasFlag(Areas.Armpits))
            foreach(var side in new[] { "l", "r" })
                if(Bone($"j_ude_a_{side}", out var arm) && Bone("j_sebo_c", out var c))
                    sources.Add((Vector3.Lerp(c, arm, 0.75f) - up * 0.05f * scale, 0.035f * scale, Vector3.Zero, 1f));
        if(areas.HasFlag(Areas.Penis))
            foreach(var b in DynamicBoneService.PenisBones)
                if(Bone(b, out var at))
                    sources.Add((at, 0.018f * scale, Vector3.Zero, 0.8f));
        if(areas.HasFlag(Areas.Head) && Bone("j_kao", out var head))
            sources.Add((head + up * 0.08f * scale, 0.07f * scale, Vector3.Zero, 1f));
        if(areas.HasFlag(Areas.Breath) && Bone("j_kao", out var face))
        {
            // (out of the mouth, a little in front of the face: in short puffs, like breathing)
            var faceForward = forward;
            if(sk.TryGetDrawnWorldFrame("j_kao", _cache, out var kf))
            {
                var f = Vector3.Transform(Vector3.UnitZ, kf.Rotation);
                if(MathUtil.IsFinite(f) && Vector3.Dot(f, forward) > 0.2f)
                    faceForward = Vector3.Normalize(f);
            }
            float breath = MathF.Max(0f, MathF.Sin(_time * MathF.Tau / 3.2f));
            var mouth = face + faceForward * 0.08f * scale - up * 0.045f * scale;
            sources.Add((mouth, 0.012f * scale, faceForward * 0.35f, 2.2f * breath));
        }
        if(sources.Count == 0)
            return;

        float amount = state.Amount;
        float rise = Math.Clamp(s.SteamRise, 0.2f, 4f);
        float size = Math.Clamp(s.SteamSize, 0.2f, 4f);
        // (puffs a second, all areas together)
        double rate = 70.0 * amount * Math.Clamp(s.SteamDensity, 0.1f, 4f);
        double total = 0;
        foreach(var src in sources)
            total += src.Weight;
        if(total <= 0)
            return;
        state.Carry += rate * dt;
        int make = (int)state.Carry;
        state.Carry -= make;
        for(int k = 0; k < make && _live.Count < MaxPuffs; k++)
        {
            // (an area, by its weight)
            double pick = _random.NextDouble() * total;
            var src = sources[0];
            foreach(var x in sources)
            {
                pick -= x.Weight;
                if(pick <= 0)
                {
                    src = x;
                    break;
                }
            }
            var jitter = new Vector3((float)_random.NextDouble() - 0.5f, (float)_random.NextDouble() - 0.5f, (float)_random.NextDouble() - 0.5f) * 2f * src.Spread;
            _live.Add(new Particle
            {
                Pos = src.At + jitter,
                Vel = up * (0.12f * rise) + src.Push + moved * 0.3f,
                Life = (1.4f + (float)_random.NextDouble() * 1.4f) * rise,
                Size0 = 0.03f * size * scale,
                Size1 = (0.12f + (float)_random.NextDouble() * 0.08f) * size * scale * MathF.Sqrt(rise),
                Seed = (float)_random.NextDouble(),
                Strength = 0.5f + amount * 0.5f,
            });
        }
    }

    /// <summary>Render thread: the puffs up now (copied into <paramref name="into"/>), and points inside the bodies.</summary>
    public int Take(ref Puff[] into, out Vector3[] probes)
    {
        lock(_lock)
        {
            if(into.Length < _publishedCount)
                into = new Puff[Math.Max(_publishedCount, into.Length * 2)];
            Array.Copy(_published, into, _publishedCount);
            probes = _probes;
            return _publishedCount;
        }
    }

    public void Clear()
    {
        _live.Clear();
        _states.Clear();
        lock(_lock)
            _publishedCount = 0;
    }

    private static float Approach(float from, float to, float step)
        => from < to ? MathF.Min(to, from + step) : MathF.Max(to, from - step);

    private static float Smooth(float e0, float e1, float x)
    {
        float t = Math.Clamp((x - e0) / (e1 - e0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
