using Dalamud.Plugin.Services;
using Limber.Core;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Limber.Rig;

/// <summary>One face bone's offset: along the face's own axes, so it means the same on every face.</summary>
[Serializable]
public sealed class FaceBoneOffset
{
    /// <summary>Millimetres: across (+ = the character's left), up, forward (out of the face).</summary>
    public Vector3 Move { get; set; }
    /// <summary>Degrees about the face's axes: X tips (nod), Y turns (shake), Z tilts.</summary>
    public Vector3 Turn { get; set; }
    public float Scale { get; set; } = 1f;

    public bool IsZero => Move == Vector3.Zero && Turn == Vector3.Zero && MathF.Abs(Scale - 1f) < 1e-5f;
    public FaceBoneOffset Copy() => (FaceBoneOffset)MemberwiseClone();
}

[Serializable]
public sealed class FacePoseSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>Editing a left / right bone changes its partner the mirrored way.</summary>
    public bool Mirror { get; set; } = true;
    public Dictionary<string, FaceBoneOffset> Bones { get; set; } = new();

    public FacePoseSettings Copy()
    {
        var c = new FacePoseSettings { Enabled = Enabled, Mirror = Mirror };
        foreach(var (k, v) in Bones)
            c.Bones[k] = v.Copy();
        return c;
    }
}

/// <summary>
/// Face Posing: each face bone moved, turned and scaled by its own offset, along the face's axes (from the eyes and the
/// mouth, not the head bone's), on top of the expression the animation gives it. Bone update, before Ahegao (which goes
/// on top). Also publishes where the watched character's face bones are, for the page's face map and the scene dots.
/// </summary>
public sealed unsafe class FacePosingService
{
    public readonly record struct BoneView(string Name, Vector3 Model, Vector3 World);

    /// <summary>The watched character's face right now: its bones, and the face's axes and middle (model space).</summary>
    public sealed class FaceView
    {
        public nint Address;
        public BoneView[] Bones = [];
        public Vector3 Across, Up, Forward, Middle;
        public Quaternion ToWorld;   // model -> world rotation
        public float WorldScale = 1f; // world units per model unit
        public float Width = 0.06f;   // between the eyes' outer corners, roughly (model units)
    }

    private readonly IObjectTable _objects;
    private readonly Func<Dictionary<string, FacePoseSettings>> _settings;
    private readonly SkeletonInfoCache _cache = new();
    private volatile (nint Address, ushort Index, FacePoseSettings Settings)[] _targets = [];
    private volatile nint _watchAddress;
    private volatile ushort _watchIndex;
    public volatile FaceView? View;

    private sealed class State
    {
        public int Partial = -1;
        public readonly List<(int Bone, Frame Before, Vector3 Scale)> Before = new();
        public int Sentinel = -1;
        public Frame SentinelWritten;
    }

    private readonly Dictionary<nint, State> _states = new();

    public FacePosingService(IObjectTable objects, Func<Dictionary<string, FacePoseSettings>> settings)
    {
        _objects = objects;
        _settings = settings;
    }

    /// <summary>The character whose face the page shows (UI thread, each frame it's open).</summary>
    public void Watch(nint address, ushort index)
    {
        _watchIndex = index;
        _watchAddress = address;
    }

    /// <summary>Framework thread, once a frame.</summary>
    public void Refresh()
    {
        var all = _settings();
        var list = new List<(nint, ushort, FacePoseSettings)>();
        if(all.Count > 0)
            foreach(var obj in _objects)
            {
                if(obj == null || obj.Address == 0)
                    continue;
                string name = obj.Name.TextValue;
                if(name.Length == 0 || !all.TryGetValue(name, out var s) || !s.Enabled || s.Bones.Count == 0)
                    continue;
                list.Add((obj.Address, obj.ObjectIndex, s.Copy()));
            }
        _targets = list.ToArray();
    }

    public void Clear()
    {
        _targets = [];
        View = null;
    }

    /// <summary>The face's skeleton among the extra ones (-1 if it isn't loaded).</summary>
    private int FacePartial(in ActorSkeleton sk)
    {
        for(int p = 1; p < sk.PartialCount; p++)
        {
            var info = sk.PartialInfo(p, _cache);
            if(info != null && (info.IndexOf("j_f_face") >= 0 || info.IndexOf("j_f_ago") >= 0) && info.IndexOf("j_f_eye_l") >= 0)
                return p;
        }
        return -1;
    }

    /// <summary>The face's axes from its own bones: across from the right eye to the left, up from the mouth to the eyes.</summary>
    private static bool Axes(in ActorSkeleton sk, int part, SkeletonInfo info, out Vector3 across, out Vector3 up, out Vector3 forward, out Vector3 middle, out float width)
    {
        across = Vector3.UnitX;
        up = Vector3.UnitY;
        forward = Vector3.UnitZ;
        middle = Vector3.Zero;
        width = 0.06f;
        int el = info.IndexOf("j_f_eye_l"), er = info.IndexOf("j_f_eye_r");
        if(el < 0 || er < 0)
            return false;
        var l = sk.GetPartialFrame(part, el).Position;
        var r = sk.GetPartialFrame(part, er).Position;
        var eyes = (l + r) * 0.5f;
        var mouth = Vector3.Zero;
        int n = 0;
        for(int i = 0; i < info.Count; i++)
        {
            var name = info.Names[i];
            if(name.StartsWith("j_f_ulip", StringComparison.Ordinal) || name.StartsWith("j_f_dlip", StringComparison.Ordinal)
               || name.StartsWith("j_f_umlip", StringComparison.Ordinal) || name.StartsWith("j_f_dmlip", StringComparison.Ordinal)
               || name.StartsWith("j_f_lip_", StringComparison.Ordinal))
            {
                mouth += sk.GetPartialFrame(part, i).Position;
                n++;
            }
        }
        if(n == 0)
        {
            int jaw = info.IndexOf("j_f_ago");
            if(jaw < 0)
                return false;
            mouth = sk.GetPartialFrame(part, jaw).Position;
        }
        else
            mouth /= n;
        var a = l - r;
        if(a.LengthSquared() < 1e-10f)
            return false;
        across = Vector3.Normalize(a);
        var u = eyes - mouth;
        u -= across * Vector3.Dot(u, across);
        if(u.LengthSquared() < 1e-10f)
            return false;
        up = Vector3.Normalize(u);
        forward = Vector3.Normalize(Vector3.Cross(across, up));
        up = Vector3.Normalize(Vector3.Cross(forward, across));
        middle = (eyes + mouth) * 0.5f;
        width = MathF.Max(0.02f, a.Length() * 1.9f);
        return true;
    }

    private static Quaternion Basis(Vector3 across, Vector3 up, Vector3 forward)
        => Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(new Matrix4x4(
            across.X, across.Y, across.Z, 0f,
            up.X, up.Y, up.Z, 0f,
            forward.X, forward.Y, forward.Z, 0f,
            0f, 0f, 0f, 1f)));

    private static bool Same(Frame a, Frame b) => a.Position == b.Position && a.Rotation == b.Rotation;

    /// <summary>Bone update stage.</summary>
    public void UpdateFrame()
    {
        var seen = new HashSet<nint>();
        foreach(var (address, index, s) in _targets)
        {
            seen.Add(address);
            try
            {
                Apply(address, index, s);
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

        nint wa = _watchAddress;
        ushort wi = _watchIndex;
        if(wa == 0)
        {
            View = null;
            return;
        }
        try
        {
            Publish(wa, wi);
        }
        catch
        {
            View = null;
        }
    }

    private void Apply(nint address, ushort index, FacePoseSettings s)
    {
        if(!ActorSkeleton.TryGet(address, index, _cache, out var sk) || sk.Info.Count == 0)
            return;
        int part = FacePartial(sk);
        if(part < 0)
            return;
        var info = sk.PartialInfo(part, _cache);
        if(info == null)
            return;
        if(!_states.TryGetValue(address, out var state))
            _states[address] = state = new State();

        // The face wasn't rebuilt since last frame (the animation is paused): put back what it was first, so the
        // offsets don't pile up.
        if(state.Partial == part && state.Sentinel >= 0 && state.Sentinel < info.Count
           && Same(sk.GetPartialFrame(part, state.Sentinel), state.SentinelWritten))
            foreach(var (bone, before, scale) in state.Before)
            {
                sk.SetPartialRotation(part, bone, before.Rotation, children: true);
                sk.SetPartialPosition(part, bone, before.Position, children: true);
                sk.SetPartialScale(part, bone, scale, children: false);
            }
        state.Before.Clear();
        state.Partial = part;
        state.Sentinel = -1;

        if(!Axes(sk, part, info, out var across, out var up, out var forward, out _, out _))
            return;
        var basis = Basis(across, up, forward);
        var basisInverse = Quaternion.Inverse(basis);

        // Parents first (the jaw before the lips and teeth on it), so each lands where it should.
        var order = new List<(int Bone, FaceBoneOffset Offset)>();
        foreach(var (name, offset) in s.Bones)
        {
            int b = info.IndexOf(name);
            if(b >= 0 && !offset.IsZero)
                order.Add((b, offset));
        }
        order.Sort((x, y) => info.Depths[x.Bone].CompareTo(info.Depths[y.Bone]));
        foreach(var (b, o) in order)
        {
            var f = sk.GetPartialFrame(part, b);
            var scaleNow = sk.GetPartialScale(part, b);
            state.Before.Add((b, f, scaleNow));
            var t = o.Turn * (MathF.PI / 180f);
            var turn = basis * Quaternion.CreateFromYawPitchRoll(t.Y, t.X, t.Z) * basisInverse;
            var position = f.Position + Vector3.Transform(o.Move * 0.001f, basis);
            sk.SetPartialRotation(part, b, Quaternion.Normalize(turn * f.Rotation), children: true);
            sk.SetPartialPosition(part, b, position, children: true);
            if(MathF.Abs(o.Scale - 1f) > 1e-5f)
                sk.SetPartialScale(part, b, scaleNow * Math.Clamp(o.Scale, 0.05f, 5f), children: false);
        }
        if(order.Count > 0)
        {
            // (the last one written: still exactly this next frame means nothing rebuilt the face)
            state.Sentinel = order[^1].Bone;
            state.SentinelWritten = sk.GetPartialFrame(part, state.Sentinel);
        }
    }

    private void Publish(nint address, ushort index)
    {
        if(!ActorSkeleton.TryGet(address, index, _cache, out var sk) || sk.Info.Count == 0)
        {
            View = null;
            return;
        }
        int part = FacePartial(sk);
        var info = part >= 0 ? sk.PartialInfo(part, _cache) : null;
        if(info == null || !Axes(sk, part, info, out var across, out var up, out var forward, out var middle, out float width))
        {
            View = null;
            return;
        }
        var bones = new List<BoneView>();
        for(int i = 0; i < info.Count; i++)
        {
            string name = info.Names[i];
            if(!name.StartsWith("j_f_", StringComparison.Ordinal) || name == "j_f_face")
                continue;
            var p = sk.GetPartialFrame(part, i).Position;
            bones.Add(new BoneView(name, p, sk.ModelToWorldPoint(p)));
        }
        View = new FaceView
        {
            Address = address,
            Bones = bones.ToArray(),
            Across = across,
            Up = up,
            Forward = forward,
            Middle = middle,
            ToWorld = sk.RootRotation,
            WorldScale = MathF.Max(1e-3f, sk.RootScale.Y),
            Width = width,
        };
    }

    /// <summary>The bone on the other side ("_l" &lt;-&gt; "_r"), or null for a middle bone.</summary>
    public static string? OtherSide(string bone)
        => bone.EndsWith("_l", StringComparison.Ordinal) ? bone[..^2] + "_r"
         : bone.EndsWith("_r", StringComparison.Ordinal) ? bone[..^2] + "_l" : null;
}
