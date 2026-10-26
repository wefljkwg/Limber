using System;
using System.Collections.Generic;
using System.Numerics;

namespace Limber.BodyShape;

/// <summary>Where a bone is (model space).</summary>
public readonly record struct BoneFrame(Vector3 Position, Quaternion Rotation, Vector3 Scale);

/// <summary>
/// Everything the sliders do to one bone, all together, in the bone's own axes (so it goes with the bone however it's
/// posed): a scale (about its joint), then a turn, then a move; and a stretch toward its next joint (live only).
/// Which children keep their own shape goes with it.
/// </summary>
public sealed class ShapeOp
{
    public required string Bone;
    public Vector3 Scale = Vector3.One;
    public Quaternion Turn = Quaternion.Identity;
    public Vector3 Move;
    public float Stretch = 1f;
    public int StretchAxis;
    public string? Next;
    public OpKeep Keep = OpKeep.None;
    public HashSet<string> KeepNames = new();
    /// <summary>The bones right below it keep their own size (Bone scale: "children scale" off).</summary>
    public bool HoldChildSizes;
    /// <summary>The bones right below it stay where they were (Bone scale: "children move" off).</summary>
    public bool HoldChildPlaces;

    public bool IsIdentity => Vector3.DistanceSquared(Scale, Vector3.One) < 1e-12f && MathF.Abs(Turn.W) > 0.999999f
        && Move.LengthSquared() < 1e-14f && MathF.Abs(Stretch - 1f) < 1e-6f;

    public ShapeOp Copy() => new()
    {
        Bone = Bone, Scale = Scale, Turn = Turn, Move = Move, Stretch = Stretch, StretchAxis = StretchAxis, Next = Next,
        Keep = Keep, KeepNames = new HashSet<string>(KeepNames), HoldChildSizes = HoldChildSizes, HoldChildPlaces = HoldChildPlaces,
    };

    /// <summary>This and <paramref name="other"/> (on the same bone) together: other's done after this.</summary>
    public ShapeOp With(ShapeOp other)
    {
        var r = Copy();
        r.Scale = Scale * other.Scale;
        r.Turn = Quaternion.Normalize(Turn * other.Turn);
        r.Move = Move + other.Move;
        r.Stretch = Stretch * other.Stretch;
        if (other.Next != null)
        {
            r.Next = other.Next;
            r.StretchAxis = other.StretchAxis;
        }
        static int Rank(OpKeep k) => k == OpKeep.Children ? 2 : k == OpKeep.Named ? 1 : 0;
        if (Rank(other.Keep) > Rank(r.Keep))
            r.Keep = other.Keep;
        r.KeepNames.UnionWith(other.KeepNames);
        r.HoldChildSizes |= other.HoldChildSizes;
        r.HoldChildPlaces |= other.HoldChildPlaces;
        return r;
    }

    /// <summary>This one with <paramref name="before"/> taken off (what's still to do when that's already on).</summary>
    public ShapeOp Over(ShapeOp? before)
    {
        var r = Copy();
        if (before == null)
            return r;
        r.Scale = new Vector3(Scale.X / before.Scale.X, Scale.Y / before.Scale.Y, Scale.Z / before.Scale.Z);
        r.Turn = Quaternion.Normalize(Quaternion.Inverse(before.Turn) * Turn);
        r.Move = Move - before.Move;
        r.Stretch = Stretch / before.Stretch;
        return r;
    }

    /// <summary>Whether a child (by its name) keeps its own shape rather than going with this bone.</summary>
    public bool Keeps(string child) => !Limber.BodyRig.RigParts.IsRigBone(child) && (Keep switch
    {
        OpKeep.Children => true,
        OpKeep.Named => Matches(KeepNames, child),
        _ => false,
    } || child == Next);
    // (Limber's rig bones on a bone are its own flesh: they always go with it.)

    private static bool Matches(HashSet<string> names, string child)
    {
        foreach (var n in names)
            if (child.StartsWith(n, StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary>Where a rest point goes (building: the bone's rest frame; no stretch).</summary>
    public Vector3 Apply(Vector3 p, in BoneFrame rest)
    {
        var local = Vector3.Transform(p - rest.Position, Quaternion.Inverse(rest.Rotation)) * Scale;
        return rest.Position + Vector3.Transform(Move + Vector3.Transform(local, Turn), rest.Rotation);
    }
}

public static class ShapeOps
{
    /// <summary>
    /// What these slider values do to each bone (by name), with the bones where they are now (or at rest).
    /// <paramref name="use"/> picks which sliders count (e.g. only those a build can bake).
    /// </summary>
    public static Dictionary<string, ShapeOp> Resolve(IReadOnlyDictionary<string, float>? values, Func<string, BoneFrame?> frameOf,
        Func<BodySlider, bool> use)
    {
        var ops = new Dictionary<string, ShapeOp>();
        if (values == null)
            return ops;
        foreach (var (key, raw) in values)
        {
            if (!BodySliders.ByKey.TryGetValue(key, out var slider) || !use(slider) || !float.IsFinite(raw) || raw == 0f)
                continue;
            float v = Math.Clamp(raw, slider.Min, slider.Max);
            foreach (var def in slider.Ops)
                foreach (bool left in BodySliders.HasSides(def.Bone) ? new[] { true, false } : new[] { true })
                {
                    string name = BodySliders.ForSide(def.Bone, left);
                    if (frameOf(name) is not { } frame)
                        continue;
                    string? next = def.Next != null ? BodySliders.ForSide(def.Next, left) : null;
                    var nextFrame = next != null ? frameOf(next) : null;
                    if (!ops.TryGetValue(name, out var op))
                        ops[name] = op = new ShapeOp { Bone = name };
                    // Stricter keeping wins (Children over Named over None).
                    static int Rank(OpKeep k) => k == OpKeep.Children ? 2 : k == OpKeep.Named ? 1 : 0;
                    if (Rank(def.Keep) > Rank(op.Keep))
                        op.Keep = def.Keep;
                    foreach (var k in def.KeepNames)
                        op.KeepNames.Add(k);

                    float factor = MathF.Max(0.05f, 1f + def.Scale * v);
                    if (def.Stretch)
                    {
                        if (nextFrame is not { } nf)
                            continue;
                        op.Next = next;
                        op.StretchAxis = Largest(Vector3.Abs(Vector3.Transform(nf.Position - frame.Position, Quaternion.Inverse(frame.Rotation))));
                        op.Stretch *= factor;
                        continue;
                    }
                    if (def.Scale != 0f)
                        op.Scale *= AxisScale(def.Axes, factor, frame, nextFrame);
                    float outward = frame.Position.X >= 0f ? 1f : -1f; // a side bone: x is outward
                    var toLocal = Quaternion.Inverse(frame.Rotation);
                    if (def.Move != Vector3.Zero)
                        op.Move += Vector3.Transform(new Vector3(def.Move.X * (BodySliders.HasSides(def.Bone) || def.Mirror ? outward : 1f), def.Move.Y, def.Move.Z) * v, toLocal);
                    // Straight out from a limb's line (or up through a bone): fuller there.
                    if (def.Radial != 0f && def.AxisFrom != null && frameOf(BodySliders.ForSide(def.AxisFrom, left)) is { } fromFrame)
                    {
                        var a = fromFrame.Position;
                        Vector3 closest;
                        if (def.AxisTo != null && frameOf(BodySliders.ForSide(def.AxisTo, left)) is { } toFrame && Vector3.DistanceSquared(toFrame.Position, a) > 1e-8f)
                        {
                            var line = Vector3.Normalize(toFrame.Position - a);
                            closest = a + line * Vector3.Dot(frame.Position - a, line);
                        }
                        else
                            closest = new Vector3(a.X, frame.Position.Y, a.Z);
                        var away = frame.Position - closest;
                        if (away.LengthSquared() > 1e-10f)
                            op.Move += Vector3.Transform(Vector3.Normalize(away) * (def.Radial * v), toLocal);
                    }
                    if (def.Turn != Vector3.Zero)
                    {
                        float sx = def.Turn.X * v, sy = def.Turn.Y * v * outward, sz = def.Turn.Z * v * outward;
                        const float Deg = MathF.PI / 180f;
                        var q = Quaternion.CreateFromAxisAngle(Vector3.UnitY, sy * Deg)
                              * Quaternion.CreateFromAxisAngle(Vector3.UnitX, -sx * Deg)
                              * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -sz * Deg);
                        // (body axes at rest, as a turn in the bone's own axes)
                        var local = Quaternion.Normalize(toLocal * q * frame.Rotation);
                        op.Turn = Quaternion.Normalize(op.Turn * local);
                    }
                }
        }
        foreach (var name in new List<string>(ops.Keys))
            if (ops[name].IsIdentity)
                ops.Remove(name);
        return ops;
    }

    private static Vector3 AxisScale(OpAxes axes, float f, in BoneFrame frame, BoneFrame? next)
    {
        if (axes == OpAxes.All)
            return new Vector3(f);
        int axis;
        switch (axes)
        {
            case OpAxes.Across:
            case OpAxes.Along:
                if (next is not { } n)
                    return Vector3.One;
                axis = Largest(Vector3.Abs(Vector3.Transform(n.Position - frame.Position, Quaternion.Inverse(frame.Rotation))));
                if (axes == OpAxes.Along)
                    return Unit(axis, f);
                var across = new Vector3(f);
                return Set(across, axis, 1f);
            case OpAxes.SideToSide:
                axis = Nearest(frame.Rotation, Vector3.UnitX);
                break;
            case OpAxes.UpDown:
                axis = Nearest(frame.Rotation, Vector3.UnitY);
                break;
            default:
                axis = Nearest(frame.Rotation, Vector3.UnitZ);
                break;
        }
        return Unit(axis, f);
    }

    public static int Largest(Vector3 v) => v.X >= v.Y && v.X >= v.Z ? 0 : v.Y >= v.Z ? 1 : 2;

    // Which of a bone's own axes points most along (or against) a body direction.
    private static int Nearest(Quaternion rotation, Vector3 direction) => Largest(new Vector3(
        MathF.Abs(Vector3.Dot(Vector3.Transform(Vector3.UnitX, rotation), direction)),
        MathF.Abs(Vector3.Dot(Vector3.Transform(Vector3.UnitY, rotation), direction)),
        MathF.Abs(Vector3.Dot(Vector3.Transform(Vector3.UnitZ, rotation), direction))));

    // 1 on every axis but this one.
    public static Vector3 Unit(int axis, float f) => Set(Vector3.One, axis, f);

    public static Vector3 Set(Vector3 v, int axis, float f)
    {
        if (axis == 0)
            v.X = f;
        else if (axis == 1)
            v.Y = f;
        else
            v.Z = f;
        return v;
    }
}
