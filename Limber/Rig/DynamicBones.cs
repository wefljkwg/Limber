using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Dalamud.Plugin.Services;
using Limber.Core;

namespace Limber.Rig;

/// <summary>One character's dynamic bones (penis only for now), kept by name.</summary>
public sealed class DynamicBoneSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>1 = holds the pose exactly (hard); 0 = flaccid (hangs, swings with the body's moves); anything between.</summary>
    public float Hardness { get; set; } = 1f;
    /// <summary>How long it keeps swinging (0 = settles at once, 1 = bouncy).</summary>
    public float Bounce { get; set; } = 0.5f;
    /// <summary>How much it hangs down when soft.</summary>
    public float Weight { get; set; } = 0.6f;
    /// <summary>How much it lags behind when the body moves (0 = moves with the body, 1 = trails a lot).</summary>
    public float Inertia { get; set; } = 0.5f;
}

/// <summary>
/// Dynamic bones, the way Koikatsu's penis props work (its DynamicBone chain): the shaft is a chain of points that
/// follows the pose through springs. Hardness decides how much the pose wins: at 1 the bones are exactly where the
/// pose / animation puts them; lower, each point is pulled back to where the pose wants it less firmly (elasticity),
/// is allowed further from it (stiffness), and gravity hangs it down; at 0 it's flaccid: it hangs, and swings and
/// trails when the body moves. Each point keeps its distance to the one before it (no stretching), and the bones are
/// turned to point along the chain. Bone update (after posing), in world space so moving the character carries it.
/// Only on your screen; adult characters only.
/// </summary>
public sealed unsafe class DynamicBoneService
{
    public static readonly string[] PenisBones = ["iv_ochinko_a", "iv_ochinko_b", "iv_ochinko_c", "iv_ochinko_d", "iv_ochinko_e", "iv_ochinko_f"];

    private sealed class Chain
    {
        public Vector3[] P = [], Prev = [];
        public Vector3 LastRoot;
        public long LastTicks;
    }

    private readonly IObjectTable _objects;
    private readonly Func<Dictionary<string, DynamicBoneSettings>> _settings;
    private readonly SkeletonInfoCache _cache = new(); // bone update thread only
    private readonly Dictionary<nint, Chain> _chains = new();
    private volatile (nint Address, ushort Index, DynamicBoneSettings Settings)[] _targets = [];

    /// <summary>Characters to leave alone this frame (e.g. Oral's partner: the mouth reads the shaft as posed).</summary>
    public Func<nint, bool> Skip = _ => false;

    public DynamicBoneService(IObjectTable objects, Func<Dictionary<string, DynamicBoneSettings>> settings)
    {
        _objects = objects;
        _settings = settings;
    }

    /// <summary>Framework thread, once a frame: which characters on screen have dynamic bones on.</summary>
    public void Refresh()
    {
        var settings = _settings();
        if(settings.Count == 0)
        {
            _targets = [];
            return;
        }
        var list = new List<(nint, ushort, DynamicBoneSettings)>();
        foreach(var obj in _objects)
        {
            if(obj == null || obj.Address == 0)
                continue;
            string name = obj.Name.TextValue;
            if(name.Length == 0 || !settings.TryGetValue(name, out var s) || !s.Enabled)
                continue;
            if(!Limber.Koi.Safety.AdultAllowed(obj.Address))
                continue;
            list.Add((obj.Address, obj.ObjectIndex, s));
        }
        _targets = list.ToArray();
    }

    /// <summary>Bone update stage (after posing).</summary>
    public void UpdateFrame()
    {
        var targets = _targets;
        if(targets.Length == 0)
        {
            if(_chains.Count > 0)
                _chains.Clear();
            return;
        }
        long now = Stopwatch.GetTimestamp();
        foreach(var (address, index, s) in targets)
        {
            try
            {
                if(Skip(address))
                {
                    _chains.Remove(address);
                    continue;
                }
                Step(address, index, s, now);
            }
            catch
            {
                _chains.Remove(address);
            }
        }
    }

    private void Step(nint address, ushort index, DynamicBoneSettings s, long now)
    {
        if(!ActorSkeleton.TryGet(address, index, _cache, out var sk) || sk.Info.Count == 0)
            return;
        var bones = new List<(int Part, int Bone)>();
        foreach(var name in PenisBones)
        {
            if(sk.TryFindExtraBone(name, _cache, out int p, out int b))
                bones.Add((p, b));
            else if(sk.Info.IndexOf(name) is int body and >= 0)
                bones.Add((0, body));
        }
        int n = bones.Count;
        if(n < 2)
            return;
        Frame Get((int Part, int Bone) x) => x.Part == 0 ? sk.GetModelFrame(x.Bone) : sk.GetPartialFrame(x.Part, x.Bone);
        void SetRotation((int Part, int Bone) x, Quaternion q)
        {
            if(x.Part == 0) sk.SetModelRotation(x.Bone, q);
            else sk.SetPartialRotation(x.Part, x.Bone, q);
        }

        // Where the pose / animation puts each point (world), plus one past the last bone (its tip).
        var target = new Vector3[n + 1];
        for(int i = 0; i < n; i++)
            target[i] = sk.ModelToWorldPoint(Get(bones[i]).Position);
        target[n] = target[n - 1] + (target[n - 1] - target[n - 2]);

        float hard = Math.Clamp(s.Hardness, 0f, 1f);
        if(!_chains.TryGetValue(address, out var chain) || chain.P.Length != n + 1)
            _chains[address] = chain = new Chain { P = (Vector3[])target.Clone(), Prev = (Vector3[])target.Clone(), LastRoot = target[0], LastTicks = now };
        float dt = Math.Clamp((float)((now - chain.LastTicks) / (double)Stopwatch.Frequency), 1f / 240f, 0.1f);
        chain.LastTicks = now;
        // A jump (teleport, new pose loaded, a long pause) or fully hard: start again from the pose.
        if(hard >= 0.999f || Vector3.Distance(chain.LastRoot, target[0]) > 0.5f)
        {
            Array.Copy(target, chain.P, n + 1);
            Array.Copy(target, chain.Prev, n + 1);
            chain.LastRoot = target[0];
            return;
        }

        float frames = dt * 60f; // (tuned per 60 fps frame)
        float damping = Math.Clamp(0.35f - 0.3f * Math.Clamp(s.Bounce, 0f, 1f), 0.02f, 0.5f);
        float elasticity = Math.Clamp((0.015f + 0.4f * hard * hard) * frames, 0f, 1f);
        float stiffness = hard;
        var gravity = new Vector3(0f, -9.8f * Math.Clamp(s.Weight, 0f, 1.5f) * (1f - hard), 0f);
        // How much of the body's own move the chain takes along at once (the rest it catches up on).
        float inert = 1f - Math.Clamp(s.Inertia, 0f, 1f);
        var rootMove = target[0] - chain.LastRoot;
        chain.LastRoot = target[0];

        var P = chain.P;
        var Prev = chain.Prev;
        P[0] = target[0];
        Prev[0] = target[0];
        for(int i = 1; i <= n; i++)
        {
            P[i] += rootMove * inert;
            Prev[i] += rootMove * inert;
            var velocity = (P[i] - Prev[i]) * (1f - damping);
            Prev[i] = P[i];
            P[i] += velocity + gravity * dt * dt;
        }
        for(int i = 1; i <= n; i++)
        {
            var restDir = target[i] - target[i - 1];
            float restLen = restDir.Length();
            if(restLen < 1e-6f)
            {
                P[i] = P[i - 1];
                continue;
            }
            var rest = P[i - 1] + restDir;
            // Pulled back toward where the pose wants it...
            P[i] += (rest - P[i]) * elasticity;
            // ...and never further from it than the stiffness allows...
            float maxLen = restLen * (1f - stiffness) * 2f;
            var d = rest - P[i];
            float len = d.Length();
            if(len > maxLen && len > 1e-7f)
                P[i] += d * ((len - maxLen) / len);
            // ...at its own length from the one before (no stretching).
            var seg = P[i] - P[i - 1];
            float sl = seg.Length();
            P[i] = sl > 1e-7f ? P[i - 1] + seg * (restLen / sl) : rest;
        }

        // The bones turned to point along the chain (root first; turning a bone carries the ones below it).
        var toModel = Quaternion.Inverse(sk.RootRotation);
        for(int i = 0; i < n; i++)
        {
            var here = sk.ModelToWorldPoint(Get(bones[i]).Position);
            Vector3 next = i + 1 < n ? sk.ModelToWorldPoint(Get(bones[i + 1]).Position)
                : here + (here - sk.ModelToWorldPoint(Get(bones[i - 1]).Position));
            var current = next - here;
            var wanted = P[i + 1] - P[i];
            if(current.LengthSquared() < 1e-12f || wanted.LengthSquared() < 1e-12f)
                continue;
            var turnWorld = MathUtil.FromTo(Vector3.Normalize(current), Vector3.Normalize(wanted));
            var turnModel = Quaternion.Normalize(toModel * turnWorld * sk.RootRotation);
            var frame = Get(bones[i]);
            if(MathUtil.IsFinite(turnModel))
                SetRotation(bones[i], Quaternion.Normalize(turnModel * frame.Rotation));
        }
    }
}
