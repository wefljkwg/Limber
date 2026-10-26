using System;
using System.Numerics;

namespace Limber.Core;

// A position and rotation (no scale).
public struct Frame
{
    public Vector3 Position;
    public Quaternion Rotation;

    public Frame(Vector3 position, Quaternion rotation)
    {
        Position = position;
        Rotation = rotation;
    }

    public static Frame Identity => new(Vector3.Zero, Quaternion.Identity);

    // Express a frame given in the same space as this one relative to this one.
    public readonly Frame ToLocal(Frame other)
    {
        var inverse = Quaternion.Inverse(Rotation);
        return new Frame(
            Vector3.Transform(other.Position - Position, inverse),
            Quaternion.Normalize(inverse * other.Rotation));
    }

    // Inverse of ToLocal.
    public readonly Frame FromLocal(Frame local)
    {
        return new Frame(
            Position + Vector3.Transform(local.Position, Rotation),
            Quaternion.Normalize(Rotation * local.Rotation));
    }

    public readonly Vector3 PointToLocal(Vector3 point) => Vector3.Transform(point - Position, Quaternion.Inverse(Rotation));

    public readonly Vector3 PointFromLocal(Vector3 point) => Position + Vector3.Transform(point, Rotation);
}

public static class MathUtil
{
    public const float Epsilon = 1e-6f;

    // Shortest rotation that turns direction `from` onto direction `to`.
    public static Quaternion FromTo(Vector3 from, Vector3 to)
    {
        float fromLength = from.Length();
        float toLength = to.Length();
        if(fromLength < Epsilon || toLength < Epsilon)
            return Quaternion.Identity;

        from /= fromLength;
        to /= toLength;

        float dot = Vector3.Dot(from, to);
        if(dot > 0.999999f)
            return Quaternion.Identity;

        if(dot < -0.999999f)
        {
            var axis = AnyPerpendicular(from);
            return Quaternion.CreateFromAxisAngle(axis, MathF.PI);
        }

        var cross = Vector3.Cross(from, to);
        return Quaternion.Normalize(new Quaternion(cross, 1f + dot));
    }

    public static Vector3 AnyPerpendicular(Vector3 v)
    {
        var other = MathF.Abs(v.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
        return Vector3.Normalize(Vector3.Cross(v, other));
    }

    // Rotation that carries the frame (u0, plane normal n0) onto (u1, n1).
    // u and n are expected to be (close to) perpendicular unit vectors.
    public static Quaternion AlignFrames(Vector3 u0, Vector3 n0, Vector3 u1, Vector3 n1)
    {
        u0 = Vector3.Normalize(u0);
        n0 = Vector3.Normalize(n0 - u0 * Vector3.Dot(n0, u0));
        var w0 = Vector3.Cross(u0, n0);

        u1 = Vector3.Normalize(u1);
        n1 = Vector3.Normalize(n1 - u1 * Vector3.Dot(n1, u1));
        var w1 = Vector3.Cross(u1, n1);

        // Row-vector convention: v * R. Rows of `from` are the old basis, rows of `to` the new one,
        // so R = transpose(from) * to maps u0 -> u1, n0 -> n1, w0 -> w1.
        var from = new Matrix4x4(
            u0.X, u0.Y, u0.Z, 0,
            n0.X, n0.Y, n0.Z, 0,
            w0.X, w0.Y, w0.Z, 0,
            0, 0, 0, 1);

        var to = new Matrix4x4(
            u1.X, u1.Y, u1.Z, 0,
            n1.X, n1.Y, n1.Z, 0,
            w1.X, w1.Y, w1.Z, 0,
            0, 0, 0, 1);

        var rotation = Matrix4x4.Transpose(from) * to;
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(rotation));
    }

    public static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    public static bool IsFinite(Quaternion q) => float.IsFinite(q.X) && float.IsFinite(q.Y) && float.IsFinite(q.Z) && float.IsFinite(q.W);

    public static Matrix4x4 FrameMatrix(Frame frame)
        => Matrix4x4.CreateFromQuaternion(frame.Rotation) * Matrix4x4.CreateTranslation(frame.Position);
}
