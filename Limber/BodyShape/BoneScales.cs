using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Limber.BodyShape;

/// <summary>
/// One bone's scale (Bone scale page): in the bone's own axes, with whether the bones below it get bigger with it,
/// whether they move out with it (or stay where they were), and whether it takes on its parent's scale itself.
/// </summary>
public sealed class BoneScaleEntry
{
    public Vector3 Scale { get; set; } = Vector3.One;
    /// <summary>The bones below it are scaled with it (off: they keep their own size).</summary>
    public bool ChildrenScale { get; set; } = true;
    /// <summary>The bones below it move out (or in) with it (off: they stay where they were).</summary>
    public bool ChildrenMove { get; set; } = true;
    /// <summary>It's scaled along with the bones above it (off: it keeps its own size whatever they do).</summary>
    public bool FollowParent { get; set; } = true;

    public bool IsDefault => Vector3.DistanceSquared(Scale, Vector3.One) < 1e-12f && ChildrenScale && ChildrenMove && FollowParent;

    public BoneScaleEntry Copy() => new() { Scale = Scale, ChildrenScale = ChildrenScale, ChildrenMove = ChildrenMove, FollowParent = FollowParent };
}

public static class BoneScales
{
    public const float Min = 0.01f, Max = 10f;

    /// <summary>Bones a character that isn't an adult body never has scaled (body mod, rig and chest bones).</summary>
    public static bool Allowed(string bone, bool adult)
        => adult || (!Limber.Rig.HandleCatalog.IsBodyModBone(bone) && !bone.StartsWith("j_mune", StringComparison.Ordinal));

    /// <summary>The changes these entries make, one per bone (in its own axes, about its joint).</summary>
    public static Dictionary<string, ShapeOp> Ops(IReadOnlyDictionary<string, BoneScaleEntry>? entries, bool adult)
    {
        var ops = new Dictionary<string, ShapeOp>();
        if (entries == null)
            return ops;
        foreach (var (bone, e) in entries)
        {
            if (e == null || !Allowed(bone, adult))
                continue;
            var s = Vector3.Clamp(e.Scale, new Vector3(Min), new Vector3(Max));
            if (!float.IsFinite(s.X) || !float.IsFinite(s.Y) || !float.IsFinite(s.Z))
                continue;
            ops[bone] = new ShapeOp { Bone = bone, Scale = s, HoldChildSizes = !e.ChildrenScale, HoldChildPlaces = !e.ChildrenMove };
        }
        return ops;
    }

    /// <summary>The bones that keep their own size whatever the bones above them do.</summary>
    public static HashSet<string> OwnSize(IReadOnlyDictionary<string, BoneScaleEntry>? entries, bool adult)
    {
        var set = new HashSet<string>();
        if (entries != null)
            foreach (var (bone, e) in entries)
                if (e != null && !e.FollowParent && Allowed(bone, adult))
                    set.Add(bone);
        return set;
    }

    /// <summary>The on / off choices, as text: when they change, everything is put back and done again.</summary>
    public static string Signature(IReadOnlyDictionary<string, BoneScaleEntry>? entries, bool adult)
    {
        if (entries == null || entries.Count == 0)
            return "";
        return string.Join(";", entries.Where(kv => kv.Value != null && Allowed(kv.Key, adult)).OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Where(kv => !kv.Value.ChildrenScale || !kv.Value.ChildrenMove || !kv.Value.FollowParent)
            .Select(kv => $"{kv.Key}:{(kv.Value.ChildrenScale ? 1 : 0)}{(kv.Value.ChildrenMove ? 1 : 0)}{(kv.Value.FollowParent ? 1 : 0)}"));
    }
}
