using Dalamud.Plugin.Services;
using Limber.Core;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Limber.Rig;

/// <summary>Which side drives the other (left = the skeleton's "_l" bones).</summary>
public enum SymmetryMode
{
    Off,
    LeftToRight,
    RightToLeft,
}

[Serializable]
public sealed class SymmetrySettings
{
    public SymmetryMode Mode { get; set; }
    /// <summary>The other side takes the bones' scale too.</summary>
    public bool Scale { get; set; } = true;

    public SymmetrySettings Copy() => (SymmetrySettings)MemberwiseClone();
}

/// <summary>
/// Real-time symmetry (IK and FK posing pages): every frame, each bone on one side puts its partner on the other side
/// exactly where it is itself, reflected across the character's own middle (not the world's): the same position,
/// the mirrored rotation and (optionally) the same scale. Whatever posed the source side (the animation, IK, FK, bone
/// scale) shows on both. The left and right bones' own axes differ, so each pair's rest pose gives the correction.
/// Bone update, right after IK posing. Only the body skeleton; Limber's rig bones (soft bodies) are left alone.
/// </summary>
public sealed unsafe class SymmetryService
{
    private readonly IObjectTable _objects;
    private readonly Func<Dictionary<string, SymmetrySettings>> _settings;
    private readonly SkeletonInfoCache _cache = new();
    private volatile (nint Address, ushort Index, SymmetrySettings Settings)[] _targets = [];

    // Per skeleton: (source, target, correction) for each direction, parents first.
    private sealed class Pairs
    {
        public (int From, int To, Quaternion Fix)[] LeftToRight = [];
        public (int From, int To, Quaternion Fix)[] RightToLeft = [];
    }

    private readonly Dictionary<SkeletonInfo, Pairs> _pairs = new();

    public SymmetryService(IObjectTable objects, Func<Dictionary<string, SymmetrySettings>> settings)
    {
        _objects = objects;
        _settings = settings;
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
        var list = new List<(nint, ushort, SymmetrySettings)>();
        foreach(var obj in _objects)
        {
            if(obj == null || obj.Address == 0)
                continue;
            string name = obj.Name.TextValue;
            if(name.Length == 0 || !all.TryGetValue(name, out var s) || s.Mode == SymmetryMode.Off)
                continue;
            list.Add((obj.Address, obj.ObjectIndex, s.Copy()));
        }
        _targets = list.ToArray();
    }

    public void Clear() => _targets = [];

    private static Quaternion Reflect(Quaternion q) => new(q.X, -q.Y, -q.Z, q.W);

    /// <summary>Bone update stage.</summary>
    public void UpdateFrame()
    {
        foreach(var (address, index, s) in _targets)
        {
            try
            {
                if(!ActorSkeleton.TryGet(address, index, _cache, out var sk))
                    continue;
                var pairs = PairsFor(sk);
                var list = s.Mode == SymmetryMode.LeftToRight ? pairs.LeftToRight : pairs.RightToLeft;
                if(list.Length == 0)
                    continue;
                // The character's own frame: its root bone (the Actor tab may have turned or moved it).
                var root = sk.Info.Count > 0 ? sk.GetModelFrame(0) : Frame.Identity;
                var inverse = Quaternion.Inverse(root.Rotation);
                foreach(var (from, to, fix) in list)
                {
                    var f = sk.GetModelFrame(from);
                    var local = Vector3.Transform(f.Position - root.Position, inverse);
                    var position = root.Position + Vector3.Transform(new Vector3(-local.X, local.Y, local.Z), root.Rotation);
                    var rotation = root.Rotation * Reflect(inverse * f.Rotation) * fix;
                    sk.SetModelRotation(to, rotation);
                    sk.SetModelPosition(to, position);
                    if(s.Scale)
                        sk.SetModelScale(to, sk.GetModelScale(from));
                }
                sk.RealignPartials(_cache);
                sk.RefreshAttachedModels();
            }
            catch
            {
                // (the character went away mid-frame)
            }
        }
    }

    private Pairs PairsFor(in ActorSkeleton sk)
    {
        var info = sk.Info;
        if(_pairs.TryGetValue(info, out var known))
            return known;
        if(_pairs.Count > 32)
            _pairs.Clear();

        // The rest pose in model space (rotations), from the reference pose.
        var rest = new Quaternion[info.Count];
        for(int i = 0; i < info.Count; i++)
        {
            sk.TryGetReferenceLocal(i, out _, out var r);
            int parent = info.Parents[i];
            rest[i] = parent >= 0 && parent < i ? Quaternion.Normalize(rest[parent] * r) : r;
        }
        var restRootInverse = info.Count > 0 ? Quaternion.Inverse(rest[0]) : Quaternion.Identity;

        var lr = new List<(int, int, Quaternion, int)>();
        var rl = new List<(int, int, Quaternion, int)>();
        for(int i = 0; i < info.Count; i++)
        {
            string name = info.Names[i];
            if(name.StartsWith("lmb_", StringComparison.Ordinal) || name.Length < 3 || name[^2] != '_')
                continue;
            char side = name[^1];
            string? other = side switch
            {
                'l' => name[..^1] + "r",
                'L' => name[..^1] + "R",
                _ => null,
            };
            if(other == null)
                continue;
            int j = info.IndexOf(other);
            if(j < 0)
                continue;
            // the correction that takes a left bone's mirrored axes to its right partner's own (and back)
            var li = restRootInverse * rest[i];
            var ri = restRootInverse * rest[j];
            var fixLr = Quaternion.Normalize(Quaternion.Inverse(Reflect(li)) * ri);
            var fixRl = Quaternion.Normalize(Quaternion.Inverse(Reflect(ri)) * li);
            lr.Add((i, j, fixLr, info.Depths[j]));
            rl.Add((j, i, fixRl, info.Depths[i]));
        }
        lr.Sort((a, b) => a.Item4.CompareTo(b.Item4));
        rl.Sort((a, b) => a.Item4.CompareTo(b.Item4));
        var made = new Pairs
        {
            LeftToRight = lr.ConvertAll(p => (p.Item1, p.Item2, p.Item3)).ToArray(),
            RightToLeft = rl.ConvertAll(p => (p.Item1, p.Item2, p.Item3)).ToArray(),
        };
        _pairs[info] = made;
        return made;
    }
}
