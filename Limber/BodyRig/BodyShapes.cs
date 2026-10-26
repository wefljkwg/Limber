using System;
using System.Collections.Generic;
using System.Numerics;

namespace Limber.BodyRig;

/// <summary>
/// A blend shape for a rigged part, made of bones instead of vertices (so it works on any model weighted to the
/// rig, and blends smoothly from 0 to 1 and beyond): an offset for every lattice node, an exact size change, and
/// (breasts) a nipple size change.
/// </summary>
public sealed record BodyShape(string Name, string Tip, float Scale, float NippleScale, float[]? Left, float[]? Right)
{
    /// <summary>Node <paramref name="i"/>'s offset on one side, in box units (outward, up, forward).</summary>
    public Vector3 Node(bool left, int i)
    {
        var d = left ? Left : Right;
        if (d == null || i * 3 + 2 >= d.Length)
            return Vector3.Zero;
        return new Vector3(d[i * 3], d[i * 3 + 1], d[i * 3 + 2]);
    }
}

public static partial class BodyShapes
{
    public static IReadOnlyList<BodyShape> Testicles => TesticleData;
    public static IReadOnlyList<BodyShape> Breasts => BreastData;

    public static IReadOnlyList<BodyShape> For(RigPart part)
        => part == RigParts.Testicles ? TesticleData : part == RigParts.Breasts ? BreastData : Array.Empty<BodyShape>();

    /// <summary>How much of a shape is set (0 if it isn't).</summary>
    public static float Amount(IReadOnlyDictionary<string, float>? amounts, string name)
        => amounts != null && amounts.TryGetValue(name, out var v) && float.IsFinite(v) ? Math.Clamp(v, -1f, 3f) : 0f;

    /// <summary>The size change of all the set shapes together (1 = none).</summary>
    public static float ScaleOf(RigPart part, IReadOnlyDictionary<string, float>? amounts, bool nipple = false)
    {
        float scale = 1f;
        foreach (var shape in For(part))
        {
            float a = Amount(amounts, shape.Name);
            if (a == 0f)
                continue;
            float k = nipple ? shape.NippleScale : shape.Scale;
            if (k != 1f)
                scale *= MathF.Pow(k, a); // smooth either way, and never zero or negative
        }
        return Math.Clamp(scale, 0.1f, 5f);
    }
}
