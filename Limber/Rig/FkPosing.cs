using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin.Services;
using Limber.Core;

namespace Limber.Rig;

/// <summary>One bone's FK pose: its rotation relative to its parent (and, if moved, its place relative to it).</summary>
public sealed class FkBoneSettings
{
    public bool Enabled { get; set; } = true;
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float W { get; set; } = 1f;
    /// <summary>Also put it at <see cref="Position"/> (relative to its parent), not only turn it.</summary>
    public bool Move { get; set; }
    public Vector3 Position { get; set; }
    /// <summary>Off ("Move children" unticked): turning or moving this bone leaves the bones under it where they were.</summary>
    public bool KeepChildren { get; set; }

    [Newtonsoft.Json.JsonIgnore]
    public Quaternion Rotation
    {
        get
        {
            var q = new Quaternion(X, Y, Z, W);
            return q.LengthSquared() > 1e-8f ? Quaternion.Normalize(q) : Quaternion.Identity;
        }
        set
        {
            var q = MathUtil.IsFinite(value) && value.LengthSquared() > 1e-8f ? Quaternion.Normalize(value) : Quaternion.Identity;
            X = q.X;
            Y = q.Y;
            Z = q.Z;
            W = q.W;
        }
    }

    public FkBoneSettings Copy() => new() { Enabled = Enabled, X = X, Y = Y, Z = Z, W = W, Move = Move, Position = Position, KeepChildren = KeepChildren };
}

/// <summary>One character's FK pose (kept by name).</summary>
public sealed class FkPoseSettings
{
    public bool Enabled { get; set; } = true;
    public Dictionary<string, FkBoneSettings> Bones { get; set; } = new();
    /// <summary>Body parts whose FK is switched off for now (kept, just not applied), like Koikatsu's FK part toggles.</summary>
    public HashSet<string> PartsOff { get; set; } = new();
    /// <summary>
    /// On top of the animation (Brio style): each bone's FK is a turn (and move) added to whatever the animation
    /// gives it, so the character keeps animating with the pose on it. Off (hold, Koikatsu style): each posed bone
    /// holds its own turn and stops animating. (Poses saved before this existed are hold poses.)
    /// </summary>
    public bool Layered { get; set; }

    public FkPoseSettings Copy()
    {
        var copy = new FkPoseSettings { Enabled = Enabled, PartsOff = new HashSet<string>(PartsOff), Layered = Layered };
        foreach(var (bone, s) in Bones)
            copy.Bones[bone] = s.Copy();
        return copy;
    }
}

/// <summary>
/// FK posing (forward kinematics), the way Koikatsu's FK, Brio and Anamnesis pose: each bone is turned directly,
/// relative to its parent, and everything below it follows. A bone with an FK rotation holds it instead of what the
/// animation gives it; bones without one keep animating. It runs before IK posing every frame, so the two work
/// together: IK (the IK Posing page) starts from the FK pose, and bones IK holds or reaches with end up where IK
/// puts them. Only on your screen.
/// </summary>
public sealed unsafe class FkService
{
    /// <summary>The parts a bone belongs to (for the part switches).</summary>
    public static readonly string[] Parts = ["Body", "Head", "Arms", "Hands", "Legs", "Hair", "Tail", "Other"];

    public static string PartOf(string bone)
    {
        static bool S(string b, string p) => b.StartsWith(p, StringComparison.Ordinal);
        if(S(bone, "j_te_") || S(bone, "j_oya_") || S(bone, "j_hito_") || S(bone, "j_naka_") || S(bone, "j_kusu_") || S(bone, "j_ko_")
           || S(bone, "n_hte_") || S(bone, "n_buki"))
            return "Hands";
        if(S(bone, "j_sako_") || S(bone, "j_ude_") || S(bone, "n_hkata_") || S(bone, "n_hhiji_") || S(bone, "n_hude_") || S(bone, "j_kata_"))
            return "Arms";
        if(S(bone, "j_asi_") || S(bone, "n_hizasoubi_") || S(bone, "j_asi"))
            return "Legs";
        if(S(bone, "j_kami_") || S(bone, "j_ex_h"))
            return "Hair";
        if(S(bone, "n_sippo") || S(bone, "j_sippo") || S(bone, "j_ex_t"))
            return "Tail";
        if(S(bone, "j_kubi") || S(bone, "j_kao") || S(bone, "j_f_") || S(bone, "j_ago") || S(bone, "j_mimi") || S(bone, "n_ear") || S(bone, "j_zera") || S(bone, "n_kao"))
            return "Head";
        if(S(bone, "j_kosi") || S(bone, "j_sebo_") || S(bone, "j_mune_") || S(bone, "n_root") || S(bone, "n_hara") || S(bone, "j_buki") || S(bone, "n_throw"))
            return "Body";
        return "Other";
    }

    /// <summary>The bone on the other side (…_l / …_r), or null.</summary>
    public static string? OtherSide(string bone)
    {
        if(bone.EndsWith("_l", StringComparison.Ordinal))
            return bone[..^2] + "_r";
        if(bone.EndsWith("_r", StringComparison.Ordinal))
            return bone[..^2] + "_l";
        return null;
    }

    private readonly record struct Item(string Bone, Quaternion Rotation, bool Move, Vector3 Position, bool KeepChildren = false);

    private readonly record struct Target(nint Address, ushort Index, Item[] Bones, bool Layered);

    private readonly record struct MirrorRequest(nint Address, ushort Index, string Character, string[] Bones, bool Layered);

    private readonly IObjectTable _objects;
    private readonly Func<Dictionary<string, FkPoseSettings>> _settings;
    private readonly SkeletonInfoCache _cache = new(); // bone update thread only
    private volatile Target[] _targets = [];
    private readonly ConcurrentQueue<MirrorRequest> _mirrors = new();
    private readonly ConcurrentQueue<(string Character, string Bone, Quaternion Local)> _mirrored = new();

    // The watched character's rest rotations (relative to the parent), for showing rotations from the rest pose.
    private sealed record WatchTarget(nint Address, ushort Index);
    private volatile WatchTarget? _watch;
    private volatile Dictionary<string, Quaternion> _rest = new();
    private nint _restFor;

    public FkService(IObjectTable objects, Func<Dictionary<string, FkPoseSettings>> settings)
    {
        _objects = objects;
        _settings = settings;
    }

    /// <summary>The character whose rest pose the FK page wants (framework thread).</summary>
    public void Watch(nint address, ushort index)
    {
        if(_watch is not { } w || w.Address != address || w.Index != index)
            _watch = new WatchTarget(address, index);
    }

    /// <summary>A bone's rest rotation relative to its parent, for the watched character (identity if unknown).</summary>
    public bool TryGetRest(string bone, out Quaternion rest)
    {
        if(_rest.TryGetValue(bone, out rest))
            return true;
        rest = Quaternion.Identity;
        return false;
    }

    /// <summary>Mirror these bones' poses onto the other side (and those onto these), next frame.</summary>
    public void Mirror(nint address, ushort index, string character, IEnumerable<string> bones, bool layered = false)
        => _mirrors.Enqueue(new MirrorRequest(address, index, character, new List<string>(bones).ToArray(), layered));

    /// <summary>Limber off (or out of GPose): no FK on anyone until the next refresh.</summary>
    public void Clear() => _targets = [];

    /// <summary>Framework thread, once a frame: takes in finished mirrors, and which characters have FK on.</summary>
    public bool Refresh()
    {
        bool changed = false;
        var all = _settings();
        while(_mirrored.TryDequeue(out var m))
        {
            if(!all.TryGetValue(m.Character, out var pose))
                all[m.Character] = pose = new FkPoseSettings();
            if(!pose.Bones.TryGetValue(m.Bone, out var bone))
                pose.Bones[m.Bone] = bone = new FkBoneSettings();
            bone.Rotation = m.Local;
            bone.Enabled = true;
            changed = true;
        }
        if(all.Count == 0)
        {
            _targets = [];
            return changed;
        }
        var list = new List<Target>();
        foreach(var obj in _objects)
        {
            if(obj == null || obj.Address == 0)
                continue;
            string name = obj.Name.TextValue;
            if(name.Length == 0 || !all.TryGetValue(name, out var pose) || !pose.Enabled || pose.Bones.Count == 0)
                continue;
            bool adult = Limber.Koi.Safety.AdultAllowed(obj.Address);
            var items = new List<Item>();
            foreach(var (bone, s) in pose.Bones)
            {
                if(!s.Enabled || pose.PartsOff.Contains(PartOf(bone)) || !HandleCatalog.IsPickable(bone, adult))
                    continue;
                items.Add(new Item(bone, s.Rotation, s.Move, s.Position, s.KeepChildren));
            }
            if(items.Count > 0)
                list.Add(new Target(obj.Address, obj.ObjectIndex, items.ToArray(), pose.Layered));
        }
        _targets = list.ToArray();
        return changed;
    }

    /// <summary>Bone update stage, before IK posing.</summary>
    public void UpdateFrame()
    {
        foreach(var t in _targets)
        {
            try
            {
                Apply(t);
            }
            catch(Exception e)
            {
                Plugin.Log.Debug(e, "Limber: FK posing skipped a character");
            }
        }
        if(_watch is { } w && _layers.TryGetValue(w.Address, out var wl))
        {
            var b = new Dictionary<string, (Quaternion Base, Vector3 BasePos)>(wl.Count);
            foreach(var (bone, l) in wl)
                b[bone] = (l.Base, l.BasePos);
            _watchedBase = b;
        }
        else if(_watchedBase.Count > 0)
            _watchedBase = new();
        if(_layers.Count > _targets.Length + 4)
            _layers.Clear();
        while(_mirrors.TryDequeue(out var request))
        {
            try
            {
                DoMirror(request);
            }
            catch(Exception e)
            {
                Plugin.Log.Debug(e, "Limber: FK mirror failed");
            }
        }
        try
        {
            ReadRest();
        }
        catch
        {
            // the watched character went away
        }
    }

    private readonly record struct Place(int Partial, int Bone, int Depth);

    private bool Find(in ActorSkeleton sk, string name, out Place place)
    {
        if(sk.TryFindExtraBone(name, _cache, out int p, out int b))
        {
            var info = sk.PartialInfo(p, _cache);
            place = new Place(p, b, 1000 + (info != null && b < info.Depths.Length ? info.Depths[b] : 0));
            return true;
        }
        int body = sk.Info.IndexOf(name);
        if(body >= 0)
        {
            place = new Place(0, body, body < sk.Info.Depths.Length ? sk.Info.Depths[body] : 0);
            return true;
        }
        place = default;
        return false;
    }

    // Each skeleton's children lists (from its parents), made once.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SkeletonInfo, int[][]> Children = new();

    private static int[][] ChildrenOf(SkeletonInfo info) => Children.GetValue(info, i =>
    {
        var lists = new List<int>[i.Parents.Length];
        for(int b = 0; b < lists.Length; b++)
            lists[b] = new List<int>();
        for(int b = 0; b < i.Parents.Length; b++)
        {
            int p = i.Parents[b];
            if(p >= 0 && p < lists.Length)
                lists[p].Add(b);
        }
        var result = new int[lists.Length][];
        for(int b = 0; b < lists.Length; b++)
            result[b] = lists[b].ToArray();
        return result;
    });

    // The frames of a bone's direct children (those not posed with FK themselves: theirs is relative to it anyway).
    private static List<(int Bone, Frame Frame)>? Keep(in ActorSkeleton sk, SkeletonInfo info, int partial, int bone, Item[] posed)
    {
        var kids = ChildrenOf(info);
        if(bone < 0 || bone >= kids.Length || kids[bone].Length == 0)
            return null;
        var names = new HashSet<string>();
        foreach(var p in posed)
            names.Add(p.Bone);
        var list = new List<(int, Frame)>(kids[bone].Length);
        foreach(int c in kids[bone])
        {
            if(c < info.Names.Length && names.Contains(info.Names[c]))
                continue;
            list.Add((c, partial == 0 ? sk.GetModelFrame(c) : sk.GetPartialFrame(partial, c)));
        }
        return list;
    }

    // Those children back where they were (each takes the bones under it along).
    private static void Restore(in ActorSkeleton sk, int partial, List<(int Bone, Frame Frame)>? kept)
    {
        if(kept == null)
            return;
        foreach(var (c, f) in kept)
        {
            if(partial == 0)
            {
                sk.SetModelRotation(c, f.Rotation);
                sk.SetModelPosition(c, f.Position);
            }
            else
            {
                sk.SetPartialRotation(partial, c, f.Rotation);
                sk.SetPartialPosition(partial, c, f.Position);
            }
        }
    }

    private void Apply(in Target t)
    {
        if(!ActorSkeleton.TryGet(t.Address, t.Index, _cache, out var sk) || sk.Info.Count == 0)
            return;
        var placed = new List<(Place At, Item Bone)>(t.Bones.Length);
        foreach(var item in t.Bones)
            if(Find(sk, item.Bone, out var at))
                placed.Add((at, item));
        // Parents first: each bone is turned from where its parent ended up.
        placed.Sort((a, b) => a.At.Depth.CompareTo(b.At.Depth));
        bool body = false, realigned = false;
        if(!_layers.TryGetValue(t.Address, out var layer))
            _layers[t.Address] = layer = new Dictionary<string, Layer>();
        var seen = new HashSet<string>();
        foreach(var (at, item) in placed)
        {
            seen.Add(item.Bone);
            if(at.Partial == 0)
            {
                // "Move children" off: where the bones under it are now, to put them back after.
                var kept = item.KeepChildren ? Keep(sk, sk.Info, 0, at.Bone, t.Bones) : null;
                int parent = sk.Info.ParentOf(at.Bone);
                var pf = parent >= 0 ? sk.GetModelFrame(parent) : Frame.Identity;
                if(t.Layered)
                {
                    var (local, pos) = Layered(layer, item, pf, sk.GetModelFrame(at.Bone));
                    sk.SetModelRotation(at.Bone, Quaternion.Normalize(pf.Rotation * local));
                    if(item.Move)
                        sk.SetModelPosition(at.Bone, pf.Position + Vector3.Transform(pos, pf.Rotation));
                }
                else
                {
                    sk.SetModelRotation(at.Bone, Quaternion.Normalize(pf.Rotation * item.Rotation));
                    if(item.Move && MathUtil.IsFinite(item.Position))
                        sk.SetModelPosition(at.Bone, pf.Position + Vector3.Transform(item.Position, pf.Rotation));
                }
                Restore(sk, 0, kept);
                body = true;
                continue;
            }
            if(body && !realigned)
            {
                // The face, hair and tail follow the body bones turned above before their own bones are turned.
                sk.RealignPartials(_cache);
                realigned = true;
            }
            var keptP = item.KeepChildren && sk.PartialInfo(at.Partial, _cache) is { } pinfo ? Keep(sk, pinfo, at.Partial, at.Bone, t.Bones) : null;
            var ppf = sk.GetPartialParentFrame(at.Partial, at.Bone, _cache);
            if(t.Layered)
            {
                var (local, pos) = Layered(layer, item, ppf, sk.GetPartialFrame(at.Partial, at.Bone));
                sk.SetPartialRotation(at.Partial, at.Bone, Quaternion.Normalize(ppf.Rotation * local));
                if(item.Move)
                    sk.SetPartialPosition(at.Partial, at.Bone, ppf.Position + Vector3.Transform(pos, ppf.Rotation));
                Restore(sk, at.Partial, keptP);
                continue;
            }
            sk.SetPartialRotation(at.Partial, at.Bone, Quaternion.Normalize(ppf.Rotation * item.Rotation));
            if(item.Move && MathUtil.IsFinite(item.Position))
                sk.SetPartialPosition(at.Partial, at.Bone, ppf.Position + Vector3.Transform(item.Position, ppf.Rotation));
            Restore(sk, at.Partial, keptP);
        }
        if(body && !realigned)
            sk.RealignPartials(_cache);
        // bones no longer posed: forget what was written on them
        if(layer.Count > seen.Count)
            foreach(var k in new List<string>(layer.Keys))
                if(!seen.Contains(k))
                    layer.Remove(k);
    }

    // ---- On top of the animation

    private sealed class Layer
    {
        public Quaternion WrittenLocal = Quaternion.Identity, Turn = Quaternion.Identity, Base = Quaternion.Identity;
        public Vector3 WrittenPos, Move, BasePos;
        public bool Any;
    }

    private readonly Dictionary<nint, Dictionary<string, Layer>> _layers = new(); // bone update thread only
    private volatile Dictionary<string, (Quaternion Base, Vector3 BasePos)> _watchedBase = new();

    /// <summary>
    /// What the animation gives the bone this frame (relative to its parent), with the turn added on top. If the
    /// pose wasn't rebuilt since last frame (paused, frozen with Freeze motion), what's there is last frame's result,
    /// so last frame's turn is taken off first: it's never added twice.
    /// </summary>
    private static (Quaternion Local, Vector3 Pos) Layered(Dictionary<string, Layer> layer, in Item item, in Frame parent, in Frame now)
    {
        var toLocal = Quaternion.Inverse(parent.Rotation);
        var local = Quaternion.Normalize(toLocal * now.Rotation);
        var pos = Vector3.Transform(now.Position - parent.Position, toLocal);
        if(!layer.TryGetValue(item.Bone, out var l))
            layer[item.Bone] = l = new Layer();
        if(l.Any && MathF.Abs(Quaternion.Dot(local, l.WrittenLocal)) > 0.9999995f)
            local = Quaternion.Normalize(local * Quaternion.Inverse(l.Turn));
        if(l.Any && Vector3.DistanceSquared(pos, l.WrittenPos) < 1e-12f)
            pos -= l.Move;
        var turned = Quaternion.Normalize(local * item.Rotation);
        var move = item.Move && MathUtil.IsFinite(item.Position) ? item.Position : Vector3.Zero;
        l.WrittenLocal = turned;
        l.Turn = item.Rotation;
        l.WrittenPos = pos + move;
        l.Move = move;
        l.Any = true;
        l.Base = local;
        l.BasePos = pos;
        return (turned, pos + move);
    }

    /// <summary>The animation's own turn (relative to the parent) of a bone of the watched character, last frame,
    /// before the FK turn on top (only for bones posed on top of the animation).</summary>
    public bool TryGetBase(string bone, out Quaternion local, out Vector3 position)
    {
        if(_watchedBase.TryGetValue(bone, out var b))
        {
            local = b.Base;
            position = b.BasePos;
            return true;
        }
        local = Quaternion.Identity;
        position = Vector3.Zero;
        return false;
    }

    // ---- Rest pose (model space and relative to the parent) of body bones

    private Quaternion RestModel(in ActorSkeleton sk, int bone, Dictionary<int, Quaternion> memo)
    {
        if(memo.TryGetValue(bone, out var q))
            return q;
        int parent = sk.Info.ParentOf(bone);
        var parentRest = parent >= 0 ? RestModel(sk, parent, memo) : Quaternion.Identity;
        q = sk.TryGetReferenceLocal(bone, out _, out var local) ? Quaternion.Normalize(parentRest * local) : parentRest;
        memo[bone] = q;
        return q;
    }

    private void ReadRest()
    {
        if(_watch is not { } watch)
            return;
        var (address, index) = (watch.Address, watch.Index);
        if(address == 0)
            return;
        if(address == _restFor && _rest.Count > 0)
            return;
        if(!ActorSkeleton.TryGet(address, index, _cache, out var sk) || sk.Info.Count == 0)
            return;
        var rest = new Dictionary<string, Quaternion>();
        for(int b = 0; b < sk.Info.Count; b++)
            if(sk.TryGetReferenceLocal(b, out _, out var local))
                rest[sk.Info.Names[b]] = local;
        for(int p = 1; p < sk.PartialCount; p++)
        {
            var info = sk.PartialInfo(p, _cache);
            if(info == null)
                continue;
            for(int b = 0; b < info.Count; b++)
                if(!rest.ContainsKey(info.Names[b]) && sk.TryGetPartialReferenceLocal(p, b, out var local))
                    rest[info.Names[b]] = local;
        }
        _rest = rest;
        _restFor = address;
    }

    // ---- Mirroring (body bones): the pose reflected across the character's middle

    private static Quaternion Reflect(Quaternion q) => new(q.X, -q.Y, -q.Z, q.W);

    private void DoMirror(in MirrorRequest r)
    {
        if(!ActorSkeleton.TryGet(r.Address, r.Index, _cache, out var sk) || sk.Info.Count == 0)
            return;
        var set = new HashSet<int>();
        foreach(var name in r.Bones)
        {
            int b = sk.Info.IndexOf(name);
            if(b >= 0)
                set.Add(b);
            var other = OtherSide(name);
            int o = other != null ? sk.Info.IndexOf(other) : -1;
            if(o >= 0)
                set.Add(o);
        }
        if(set.Count == 0)
            return;
        var memo = new Dictionary<int, Quaternion>();
        // Where each one should face (model space): its counterpart's turn away from rest, reflected.
        var wanted = new Dictionary<int, Quaternion>();
        foreach(int b in set)
        {
            string name = sk.Info.Names[b];
            var other = OtherSide(name);
            int source = other != null ? sk.Info.IndexOf(other) : b;
            if(source < 0)
                source = b;
            var now = sk.GetModelFrame(source).Rotation;
            var turn = Quaternion.Normalize(now * Quaternion.Inverse(RestModel(sk, source, memo)));
            wanted[b] = Quaternion.Normalize(Reflect(turn) * RestModel(sk, b, memo));
        }
        foreach(var (b, model) in wanted)
        {
            int parent = sk.Info.ParentOf(b);
            var parentModel = parent < 0 ? Quaternion.Identity
                : wanted.TryGetValue(parent, out var pw) ? pw : sk.GetModelFrame(parent).Rotation;
            var local = Quaternion.Normalize(Quaternion.Inverse(parentModel) * model);
            if(r.Layered)
            {
                // on top of the animation: the turn that takes what the animation gives it to the mirrored pose
                var name = sk.Info.Names[b];
                var baseLocal = _layers.TryGetValue(r.Address, out var ly) && ly.TryGetValue(name, out var l) && l.Any ? l.Base
                    : Quaternion.Normalize(Quaternion.Inverse(parent >= 0 ? sk.GetModelFrame(parent).Rotation : Quaternion.Identity) * sk.GetModelFrame(b).Rotation);
                local = Quaternion.Normalize(Quaternion.Inverse(baseLocal) * local);
            }
            if(MathUtil.IsFinite(local))
                _mirrored.Enqueue((r.Character, sk.Info.Names[b], local));
        }
    }
}
