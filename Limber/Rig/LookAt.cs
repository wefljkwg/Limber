using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using Limber.Core;

namespace Limber.Rig;

/// <summary>One character's head / eye tracking (kept by name).</summary>
public sealed class LookAtSettings
{
    public bool Enabled { get; set; }
    /// <summary>The character to look at by name ("" = the camera).</summary>
    public string Target { get; set; } = "";
    /// <summary>Look at one of their own bones (a hand, for example) instead of someone else.</summary>
    public bool Self { get; set; }
    /// <summary>Which of their bones (their face by default).</summary>
    public string TargetBone { get; set; } = "j_kao";
    /// <summary>How much the head (and neck) turns toward it (0 = only the eyes).</summary>
    public float Head { get; set; } = 0.8f;
    public bool Eyes { get; set; } = true;
    /// <summary>How fast it turns (higher = quicker).</summary>
    public float Speed { get; set; } = 4f;
    /// <summary>How far the head turns from the chest, side to side / up and down (degrees).</summary>
    public float MaxTurn { get; set; } = 70f;
    public float MaxTilt { get; set; } = 40f;
    /// <summary>How far the eyes turn in the head (degrees).</summary>
    public float EyeRange { get; set; } = 25f;
    /// <summary>Behind the shoulder: past this, the head turns back to the front instead of craning round.</summary>
    public bool LetGoBehind { get; set; } = true;

    // Fine adjustments (the game's eyes and heads can look a little past or off what they aim at).
    /// <summary>The point looked at moved (metres, along the looker's chest: X to its left, Y up, Z forward).</summary>
    public float AimLeft { get; set; }
    public float AimUp { get; set; }
    public float AimForward { get; set; }
    /// <summary>The whole look turned (degrees): + to the left, + up.</summary>
    public float AimYaw { get; set; }
    public float AimPitch { get; set; }
    /// <summary>The head turned on top (degrees): + to the left, + up, + tilted to its left.</summary>
    public float HeadYaw { get; set; }
    public float HeadPitch { get; set; }
    public float HeadRoll { get; set; }
    /// <summary>The eyes turned on top (degrees): + to the left, + up; Spread + = further apart (wall-eyed), - = crossed.</summary>
    public float EyeYaw { get; set; }
    public float EyePitch { get; set; }
    public float EyeSpread { get; set; }

    public bool AdjustedHead => HeadYaw != 0f || HeadPitch != 0f || HeadRoll != 0f;

    public LookAtSettings Copy() => (LookAtSettings)MemberwiseClone();
}

/// <summary>
/// Head and eye tracking: a character's head (with the neck taking part of the turn) and eyes follow another
/// character's bone (their face by default, or any bone: chest, hips, hands...) or the camera. Kept within a natural
/// range from the chest, eased (it turns toward a moving target smoothly), and turning back to the front when the
/// target goes behind. Bone update, after posing, so it works on top of the pose and animation. Only on your screen.
/// </summary>
public sealed unsafe class LookAtService
{
    private static readonly string[] EyeBones = ["j_f_eye_l", "j_f_eye_r"];

    private readonly record struct Target(nint Address, ushort Index, LookAtSettings Settings, nint Other, ushort OtherIndex);

    private sealed class State
    {
        public Vector3 Dir;      // where it looks now (model space, eased)
        public bool Has;
        public long Ticks;
        public ushort Index;
        public int Neck = -1;
        // What was written last frame (and the turn added), per bone (partial << 16 | bone; the body is partial 0):
        // when the game doesn't rebuild the pose (paused), the turn is taken off before adding the new one.
        public readonly Dictionary<int, (Quaternion Written, Quaternion Turn)> Writes = new();
    }

    private static bool Same(Quaternion a, Quaternion b) => MathF.Abs(Quaternion.Dot(a, b)) > 0.999999f;

    private readonly IObjectTable _objects;
    private readonly Func<Dictionary<string, LookAtSettings>> _settings;
    private readonly SkeletonInfoCache _cache = new();
    private readonly Dictionary<nint, State> _states = new();
    private volatile Target[] _targets = [];
    private volatile object _camera = Vector3.Zero; // boxed Vector3 (set from the framework thread)
    public Func<nint, bool> Skip = _ => false;

    public LookAtService(IObjectTable objects, Func<Dictionary<string, LookAtSettings>> settings)
    {
        _objects = objects;
        _settings = settings;
    }

    /// <summary>Framework thread, once a frame.</summary>
    public void Refresh()
    {
        if(CameraUtil.TryGetMatrices(out var view, out _) && Matrix4x4.Invert(view, out var inv) && MathUtil.IsFinite(inv.Translation))
            _camera = inv.Translation;
        var all = _settings();
        if(all.Count == 0)
        {
            _targets = [];
            return;
        }
        // Everyone by name (in GPose a character and their GPose copy share a name).
        var byName = new Dictionary<string, List<(nint, ushort)>>();
        foreach(var obj in _objects)
        {
            if(obj is not ICharacter || obj.Address == 0 || obj.Name.TextValue.Length == 0)
                continue;
            if(!byName.TryGetValue(obj.Name.TextValue, out var same))
                byName[obj.Name.TextValue] = same = new();
            same.Add((obj.Address, obj.ObjectIndex));
        }
        var list = new List<Target>();
        foreach(var obj in _objects)
        {
            if(obj is not ICharacter || obj.Address == 0)
                continue;
            string name = obj.Name.TextValue;
            if(name.Length == 0 || !all.TryGetValue(name, out var s) || !s.Enabled)
                continue;
            nint other = 0; ushort otherIndex = 0;
            if(s.Self)
                (other, otherIndex) = (obj.Address, obj.ObjectIndex);
            else if(s.Target.Length > 0)
            {
                if(!byName.TryGetValue(s.Target, out var found))
                    continue; // they aren't here
                // Someone else of that name, on the same side of GPose (GPose copies look at GPose copies).
                bool gpose = obj.ObjectIndex >= 200;
                (nint, ushort) pick = default;
                foreach(var c in found)
                {
                    if(c.Item1 == obj.Address)
                        continue;
                    if(pick.Item1 == 0 || ((c.Item2 >= 200) == gpose && (pick.Item2 >= 200) != gpose))
                        pick = c;
                }
                if(pick.Item1 == 0)
                    continue;
                (other, otherIndex) = pick;
            }
            list.Add(new Target(obj.Address, obj.ObjectIndex, s.Copy(), other, otherIndex));
        }
        _targets = list.ToArray();
    }

    public void Clear() => _targets = [];

    /// <summary>Whether this character is following someone right now (they're here and it's on).</summary>
    public bool IsFollowing(nint address)
    {
        foreach(var t in _targets)
            if(t.Address == address)
                return true;
        return false;
    }

    /// <summary>Bone update stage (after posing).</summary>
    public void UpdateFrame()
    {
        var targets = _targets;
        long now = Stopwatch.GetTimestamp();
        var seen = new HashSet<nint>();
        foreach(var t in targets)
        {
            seen.Add(t.Address);
            try
            {
                if(!Skip(t.Address))
                    Step(t, now);
            }
            catch
            {
                _states.Remove(t.Address);
            }
        }
        // Stopped (or gone): take our last turn back off if the pose is paused and still holds it.
        if(_states.Count > seen.Count)
            foreach(var a in new List<nint>(_states.Keys))
            {
                if(seen.Contains(a))
                    continue;
                var old = _states[a];
                _states.Remove(a);
                try
                {
                    if(ActorSkeleton.TryGet(a, old.Index, _cache, out var sk))
                        Undo(sk, old, old.Neck);
                }
                catch
                {
                }
            }
    }

    // The bone's rest turn in model space (from the skeleton's reference pose).
    private static Quaternion RestModel(in ActorSkeleton sk, int bone)
    {
        var q = Quaternion.Identity;
        var chain = new List<int>();
        for(int b = bone; b >= 0 && chain.Count < 256; b = sk.Info.ParentOf(b))
            chain.Add(b);
        for(int i = chain.Count - 1; i >= 0; i--)
            if(sk.TryGetReferenceLocal(chain[i], out _, out var local))
                q = Quaternion.Normalize(q * local);
        return q;
    }

    // Which way a bone faces / is up now, from how it's turned from its rest (the model faces +Z at rest).
    private static Vector3 Facing(in ActorSkeleton sk, int bone, Vector3 axis)
        => Vector3.Normalize(Vector3.Transform(axis, Quaternion.Normalize(sk.GetModelFrame(bone).Rotation * Quaternion.Inverse(RestModel(sk, bone)))));

    private static Quaternion Limit(Quaternion q, float maxDegrees)
    {
        q = Quaternion.Normalize(q);
        if(q.W < 0) q = -q;
        float angle = 2f * MathF.Acos(Math.Clamp(q.W, -1f, 1f));
        float max = maxDegrees * MathF.PI / 180f;
        return angle > max && angle > 1e-6f ? Quaternion.Slerp(Quaternion.Identity, q, max / angle) : q;
    }

    private static void Write(in ActorSkeleton sk, State state, int partial, int bone, Quaternion turn, Quaternion rotation)
    {
        sk.SetModelRotation(bone, rotation);
        state.Writes[partial << 16 | bone] = (sk.GetModelFrame(bone).Rotation, turn);
    }

    // The pose wasn't rebuilt since last frame (the bones still hold what was written): take the turns back off,
    // children first (the eyes, the head, then the neck), so they don't add up frame after frame.
    private void Undo(in ActorSkeleton sk, State state, int neck)
    {
        if(state.Writes.Count == 0)
            return;
        var undo = new List<(int Partial, int Bone, Quaternion Turn)>();
        foreach(var (key, w) in state.Writes)
        {
            int p = key >> 16, b = key & 0xFFFF;
            var now = p == 0 ? sk.GetModelFrame(b).Rotation : sk.GetPartialFrame(p, b).Rotation;
            if(Same(now, w.Written))
                undo.Add((p, b, w.Turn));
        }
        state.Writes.Clear();
        if(undo.Count == 0)
            return;
        // the face's bones first, then the head, then the neck
        undo.Sort((a, c) =>
        {
            int k = (c.Partial != 0).CompareTo(a.Partial != 0);
            return k != 0 ? k : (a.Partial == 0 && a.Bone == neck).CompareTo(c.Partial == 0 && c.Bone == neck);
        });
        bool body = false;
        foreach(var (p, b, turn) in undo)
        {
            var inv = Quaternion.Inverse(turn);
            if(p == 0)
            {
                sk.SetModelRotation(b, Quaternion.Normalize(inv * sk.GetModelFrame(b).Rotation));
                body = true;
            }
            else
                sk.SetPartialRotation(p, b, Quaternion.Normalize(inv * sk.GetPartialFrame(p, b).Rotation), children: true);
        }
        if(body)
            sk.RealignPartials(_cache);
    }

    private void Step(in Target t, long now)
    {
        var s = t.Settings;
        if(!ActorSkeleton.TryGet(t.Address, t.Index, _cache, out var sk) || sk.Info.Count == 0)
            return;
        int neck = sk.Info.IndexOf("j_kubi"), head = sk.Info.IndexOf("j_kao"), chest = sk.Info.IndexOf("j_sebo_c");
        if(head < 0)
            return;
        if(chest < 0)
            chest = sk.Info.ParentOf(neck >= 0 ? neck : head);

        if(!_states.TryGetValue(t.Address, out var state))
            _states[t.Address] = state = new State { Ticks = now, Index = t.Index };
        state.Neck = neck;
        Undo(sk, state, neck);

        // Where to look (world), then in this character's model space.
        Vector3 world;
        if(t.Other == 0)
            world = (Vector3)_camera;
        else
        {
            if(!ActorSkeleton.TryGet(t.Other, t.OtherIndex, _cache, out var other) || other.Info.Count == 0)
                return;
            if(!other.TryGetDrawnWorldFrame(s.TargetBone.Length > 0 ? s.TargetBone : "j_kao", _cache, out var f)
               && !other.TryGetDrawnWorldFrame("j_kao", _cache, out f))
                return;
            world = f.Position;
        }
        var target = sk.WorldToModelPoint(world);
        var headPos = sk.GetModelFrame(head).Position;

        // The chest's axes (right = up x forward: the character's left, +X).
        var fwd = Facing(sk, chest, Vector3.UnitZ);
        var up = Facing(sk, chest, Vector3.UnitY);
        var right = Vector3.Normalize(Vector3.Cross(up, fwd));
        // The point moved (metres, in the chest's axes; the model is in the character's own size already).
        target += right * s.AimLeft + up * s.AimUp + fwd * s.AimForward;
        var to = target - headPos;
        if(to.LengthSquared() < 1e-8f || !MathUtil.IsFinite(to))
            return;
        to = Vector3.Normalize(to);

        // Kept within reach of the chest: side to side and up / down limits; behind the shoulder, back to the front.
        float yaw = MathF.Atan2(Vector3.Dot(to, right), Vector3.Dot(to, fwd)) * 180f / MathF.PI + Math.Clamp(s.AimYaw, -90f, 90f);
        float pitch = MathF.Asin(Math.Clamp(Vector3.Dot(to, up), -1f, 1f)) * 180f / MathF.PI + Math.Clamp(s.AimPitch, -60f, 60f);
        float maxYaw = Math.Clamp(s.MaxTurn, 0f, 120f), maxPitch = Math.Clamp(s.MaxTilt, 0f, 80f);
        bool behind = s.LetGoBehind && MathF.Abs(yaw) > maxYaw + 35f;
        if(behind)
        {
            yaw = 0f;
            pitch = 0f;
        }
        yaw = Math.Clamp(yaw, -maxYaw, maxYaw);
        pitch = Math.Clamp(pitch, -maxPitch * 1.2f, maxPitch);
        float yr = yaw * MathF.PI / 180f, pr = pitch * MathF.PI / 180f;
        var wanted = Vector3.Normalize(MathF.Cos(pr) * (MathF.Cos(yr) * fwd + MathF.Sin(yr) * right) + MathF.Sin(pr) * up);

        // Eased toward it.
        float dt = Math.Clamp((float)((now - state.Ticks) / (double)Stopwatch.Frequency), 0f, 0.1f);
        state.Ticks = now;
        var current = Facing(sk, head, Vector3.UnitZ);
        if(!state.Has)
        {
            state.Dir = current;
            state.Has = true;
        }
        float k = 1f - MathF.Exp(-dt * Math.Clamp(s.Speed, 0.2f, 30f));
        var dir = Vector3.Normalize(Vector3.Lerp(state.Dir, wanted, k));
        if(!MathUtil.IsFinite(dir))
            dir = wanted;
        state.Dir = dir;

        // The head (the neck takes part of the turn), as much as Head says.
        float weight = Math.Clamp(s.Head, 0f, 1f);
        if(weight > 0f || s.AdjustedHead)
        {
            if(neck >= 0)
            {
                var turnAll = MathUtil.FromTo(current, dir);
                var neckTurn = Quaternion.Slerp(Quaternion.Identity, turnAll, 0.4f * weight);
                Write(sk, state, 0, neck, neckTurn, Quaternion.Normalize(neckTurn * sk.GetModelFrame(neck).Rotation));
            }
            var nowFacing = Facing(sk, head, Vector3.UnitZ);
            var aimed = Vector3.Normalize(Vector3.Lerp(current, dir, weight));
            var rest = MathUtil.FromTo(nowFacing, aimed);
            // The head's own adjustment on top, about the way it now faces: turned (about up), nodded (about its
            // side), tilted (about its forward).
            if(s.AdjustedHead && MathUtil.IsFinite(aimed))
            {
                var hu = Vector3.Normalize(up - aimed * Vector3.Dot(up, aimed));
                var hs = Vector3.Normalize(Vector3.Cross(hu, aimed));
                const float D = MathF.PI / 180f;
                var extra = Quaternion.CreateFromAxisAngle(hu, Math.Clamp(s.HeadYaw, -60f, 60f) * D)
                    * Quaternion.CreateFromAxisAngle(hs, -Math.Clamp(s.HeadPitch, -45f, 45f) * D)
                    * Quaternion.CreateFromAxisAngle(aimed, -Math.Clamp(s.HeadRoll, -45f, 45f) * D);
                if(MathUtil.IsFinite(extra))
                    rest = Quaternion.Normalize(extra * rest);
            }
            Write(sk, state, 0, head, rest, Quaternion.Normalize(rest * sk.GetModelFrame(head).Rotation));
            sk.RealignPartials(_cache);
            // what the realign carried onto the face (eyes) isn't ours to take back
            foreach(var name in EyeBones)
                if(sk.TryFindExtraBone(name, _cache, out int ep, out int eb))
                    state.Writes.Remove(ep << 16 | eb);
        }

        // The eyes: what's left of the way to the target, within the eyes' range.
        if(!s.Eyes)
            return;
        var faced = Facing(sk, head, Vector3.UnitZ);
        // (the eyes' own adjustment: about the head's up and side)
        var eu = Vector3.Normalize(up - faced * Vector3.Dot(up, faced));
        var es = Vector3.Normalize(Vector3.Cross(eu, faced));
        bool eyeAdjust = (s.EyeYaw != 0f || s.EyePitch != 0f || s.EyeSpread != 0f) && MathUtil.IsFinite(eu) && MathUtil.IsFinite(es);
        foreach(var name in EyeBones)
        {
            if(!sk.TryFindExtraBone(name, _cache, out int p, out int b))
                continue;
            var eye = sk.GetPartialFrame(p, b);
            var look = target - eye.Position;
            if(look.LengthSquared() < 1e-8f)
                continue;
            var aim = behind ? faced : Vector3.Normalize(look);
            var turn = Limit(MathUtil.FromTo(faced, aim), Math.Clamp(s.EyeRange, 0f, 45f));
            if(eyeAdjust)
            {
                // (the left eye, +X: outward is to the left; the right eye's outward the other way)
                float outward = name.EndsWith("_l", StringComparison.Ordinal) ? 1f : -1f;
                const float D = MathF.PI / 180f;
                var extra = Quaternion.CreateFromAxisAngle(eu, (Math.Clamp(s.EyeYaw, -30f, 30f) + outward * Math.Clamp(s.EyeSpread, -20f, 20f)) * D)
                    * Quaternion.CreateFromAxisAngle(es, -Math.Clamp(s.EyePitch, -30f, 30f) * D);
                if(MathUtil.IsFinite(extra))
                    turn = Quaternion.Normalize(extra * turn);
            }
            var rot = Quaternion.Normalize(turn * eye.Rotation);
            sk.SetPartialRotation(p, b, rot, children: true);
            state.Writes[p << 16 | b] = (sk.GetPartialFrame(p, b).Rotation, turn);
        }
    }
}
