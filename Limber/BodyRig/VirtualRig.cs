using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using Limber.Core;

namespace Limber.BodyRig;

/// <summary>
/// The soft parts' lattice bones kept by Limber itself instead of in the character's skeleton file: no skeleton mod,
/// nothing for the game to load, so nothing to redraw. Each bone hangs from a real bone of the character's skeleton
/// (where the rig mod would have put it) and is moved by the soft bodies exactly like a real one; the live rig
/// (<see cref="Limber.Gpu.LiveRig"/>) then bends the worn models' vertices with them on the GPU.
/// Made for one skeleton (a redraw or race change makes a new one). Bone update thread only, except the fixed parts.
/// </summary>
public sealed class VirtualRig
{
    public readonly SkeletonInfo Info;       // the skeleton it's for
    public readonly string[] Names;
    public readonly int[] Parent;            // a bone of the real skeleton (partial 0)
    public readonly Vector3[] Local;         // under the parent, in its axes and scale (no turn of its own)
    public readonly Matrix4x4[] RestInverse; // its rest (bind) pose in model space, inverted
    private readonly Dictionary<string, int> _byName = new(StringComparer.Ordinal);

    // Where each is now (model space), and when the soft body last put it there.
    public readonly Vector3[] Position;
    public readonly Quaternion[] Rotation;
    public readonly Vector3[] Scale;
    public readonly long[] Written;

    /// <summary>The parts it has bones for (part keys).</summary>
    public readonly HashSet<string> Parts = new(StringComparer.Ordinal);

    public VirtualRig(SkeletonInfo info, IReadOnlyList<RigBuilder.PlacedBone> bones, Func<int, Matrix4x4> restModel, IEnumerable<string> parts)
    {
        Info = info;
        int n = bones.Count;
        Names = new string[n];
        Parent = new int[n];
        Local = new Vector3[n];
        RestInverse = new Matrix4x4[n];
        Position = new Vector3[n];
        Rotation = new Quaternion[n];
        Scale = new Vector3[n];
        Written = new long[n];
        for (int i = 0; i < n; i++)
        {
            var b = bones[i];
            Names[i] = b.Name;
            Parent[i] = b.Parent;
            Local[i] = b.Local;
            _byName.TryAdd(b.Name, i);
            // (no turn of its own: its rest frame is its parent's, moved to where it sits)
            var rest = Matrix4x4.CreateTranslation(b.Local) * restModel(b.Parent);
            if (!Matrix4x4.Invert(rest, out RestInverse[i]))
                RestInverse[i] = Matrix4x4.Identity;
            Rotation[i] = Quaternion.Identity;
            Scale[i] = Vector3.One;
        }
        foreach (var p in parts)
            Parts.Add(p);
    }

    public int Count => Names.Length;

    private SkeletonInfo? _matched;

    /// <summary>It was made for this skeleton (each reader keeps its own copy of a skeleton's names: compared by them).</summary>
    public bool Fits(SkeletonInfo info)
    {
        if (ReferenceEquals(info, Info) || ReferenceEquals(info, _matched))
            return true;
        if (info.Count != Info.Count)
            return false;
        for (int i = 0; i < info.Count; i++)
            if (info.Parents[i] != Info.Parents[i] || !string.Equals(info.Names[i], Info.Names[i], StringComparison.Ordinal))
                return false;
        _matched = info;
        return true;
    }

    public int IndexOf(string name) => _byName.TryGetValue(name, out var i) ? i : -1;

    /// <summary>Puts a bone where its parent carries it now (as the game would for a real bone nobody moved).</summary>
    public void Follow(int i, in ActorSkeleton sk)
    {
        var parent = sk.GetModelFrame(Parent[i]);
        var scale = sk.GetModelScale(Parent[i]);
        Position[i] = parent.Position + Vector3.Transform(scale * Local[i], parent.Rotation);
        Rotation[i] = parent.Rotation;
        Scale[i] = scale;
    }

    /// <summary>Its skinning matrix now (rest pose to where it is), for the vertices weighted to it.</summary>
    public Matrix4x4 Skinning(int i, in ActorSkeleton sk, long now)
    {
        // (not moved by a soft body lately: the part is off, so it goes where its parent takes it)
        if (now - Written[i] > 250)
            Follow(i, sk);
        var m = Matrix4x4.CreateScale(Scale[i]) * Matrix4x4.CreateFromQuaternion(Rotation[i]) * Matrix4x4.CreateTranslation(Position[i]);
        return RestInverse[i] * m;
    }
}

/// <summary>The virtual rigs in use, by character (published by the live rig, used by the soft bodies).</summary>
public static class VirtualRigs
{
    private static readonly ConcurrentDictionary<nint, VirtualRig> Rigs = new();

    public static void Set(nint address, VirtualRig rig) => Rigs[address] = rig;

    public static void Remove(nint address) => Rigs.TryRemove(address, out _);

    public static void Clear() => Rigs.Clear();

    /// <summary>A character's virtual rig, if it was made for the skeleton it has now.</summary>
    public static VirtualRig? For(nint address, SkeletonInfo info)
        => Rigs.TryGetValue(address, out var rig) && rig.Fits(info) ? rig : null;

    public static bool Has(nint address) => Rigs.ContainsKey(address);

    public static int Count => Rigs.Count;
}

/// <summary>
/// A character's skeleton as the soft bodies see it: the real bones, and after them the virtual rig's (indices from
/// <see cref="VirtualBase"/> up). Reading and moving a bone works the same either way.
/// </summary>
public readonly unsafe struct RigSkeleton
{
    public const int VirtualBase = 1 << 20;

    public readonly ActorSkeleton Real;
    public readonly VirtualRig? Virtual;

    public RigSkeleton(in ActorSkeleton real, VirtualRig? rig)
    {
        Real = real;
        Virtual = rig;
    }

    public SkeletonInfo Info => Real.Info;
    public Vector3 RootPosition => Real.RootPosition;
    public Quaternion RootRotation => Real.RootRotation;
    public Vector3 RootScale => Real.RootScale;
    public Matrix4x4 ModelToWorld => Real.ModelToWorld;
    public Matrix4x4 WorldToModel => Real.WorldToModel;

    public static bool IsVirtual(int bone) => bone >= VirtualBase;

    public int IndexOf(string name)
    {
        int i = Real.Info.IndexOf(name);
        if (i >= 0)
            return i;
        int v = Virtual?.IndexOf(name) ?? -1;
        return v >= 0 ? VirtualBase + v : -1;
    }

    public int ParentOf(int bone) => bone >= VirtualBase ? Virtual!.Parent[bone - VirtualBase] : Real.Info.ParentOf(bone);

    /// <summary>The bone's name (real or virtual), or "".</summary>
    public string NameOf(int bone)
        => bone >= VirtualBase ? (Virtual != null && bone - VirtualBase < Virtual.Count ? Virtual.Names[bone - VirtualBase] : "")
            : bone >= 0 && bone < Real.Info.Count ? Real.Info.Names[bone] : "";

    public bool TryGetReferenceLocal(int bone, out Vector3 position, out Quaternion rotation)
    {
        if (bone >= VirtualBase)
        {
            position = Virtual!.Local[bone - VirtualBase];
            rotation = Quaternion.Identity;
            return true;
        }
        return Real.TryGetReferenceLocal(bone, out position, out rotation);
    }

    public Frame GetModelFrame(int bone)
        => bone >= VirtualBase ? new Frame(Virtual!.Position[bone - VirtualBase], Virtual.Rotation[bone - VirtualBase]) : Real.GetModelFrame(bone);

    public Vector3 GetModelPosition(int bone) => bone >= VirtualBase ? Virtual!.Position[bone - VirtualBase] : Real.GetModelPosition(bone);

    public Vector3 GetModelScale(int bone) => bone >= VirtualBase ? Virtual!.Scale[bone - VirtualBase] : Real.GetModelScale(bone);

    public void SetModelPosition(int bone, Vector3 position)
    {
        if (bone < VirtualBase)
        {
            Real.SetModelPosition(bone, position);
            return;
        }
        if (!MathUtil.IsFinite(position))
            return;
        int i = bone - VirtualBase;
        Virtual!.Position[i] = position;
        Virtual.Written[i] = Environment.TickCount64;
    }

    public void SetModelRotation(int bone, Quaternion rotation)
    {
        if (bone < VirtualBase)
        {
            Real.SetModelRotation(bone, rotation);
            return;
        }
        if (!MathUtil.IsFinite(rotation))
            return;
        Virtual!.Rotation[bone - VirtualBase] = Quaternion.Normalize(rotation);
    }

    public void SetModelScale(int bone, Vector3 scale)
    {
        if (bone < VirtualBase)
        {
            Real.SetModelScale(bone, scale);
            return;
        }
        if (!MathUtil.IsFinite(scale))
            return;
        Virtual!.Scale[bone - VirtualBase] = scale;
    }

    /// <summary>A virtual bone back where its parent carries it (the start of each frame; real bones: nothing).</summary>
    public void Follow(int bone)
    {
        if (bone >= VirtualBase)
            Virtual!.Follow(bone - VirtualBase, Real);
    }

    public Vector3 ModelToWorldPoint(Vector3 model) => Real.ModelToWorldPoint(model);

    public Vector3 WorldToModelPoint(Vector3 world) => Real.WorldToModelPoint(world);
}
