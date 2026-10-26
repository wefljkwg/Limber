using System;
using System.Numerics;
using FFXIVClientStructs.Havok.Common.Base.Math.QsTransform;
using FFXIVClientStructs.Havok.Common.Base.Math.Quaternion;
using FFXIVClientStructs.Havok.Common.Base.Math.Vector;

namespace Limber.Koi.Mathx;

/// <summary>Small vector / quaternion helpers. Quaternions follow System.Numerics (Hamilton, q*v*q^-1).</summary>
internal static unsafe class QMath
{
    public const float Epsilon = 1e-6f;

    public static Vector3 ToVec(this hkVector4f v) => new(v.X, v.Y, v.Z);
    public static Quaternion ToQuat(this hkQuaternionf q) => new(q.X, q.Y, q.Z, q.W);

    public static hkVector4f ToHk(this Vector3 v, float w = 0f) => new() { X = v.X, Y = v.Y, Z = v.Z, W = w };
    public static hkQuaternionf ToHk(this Quaternion q) => new() { X = q.X, Y = q.Y, Z = q.Z, W = q.W };

    public static Vector3 Rotate(this Quaternion q, Vector3 v) => Vector3.Transform(v, q);

    public static bool TryNormalize(Vector3 v, out Vector3 n, float minLength = 1e-5f)
    {
        var len = v.Length();
        if (len < minLength || float.IsNaN(len))
        {
            n = default;
            return false;
        }
        n = v / len;
        return true;
    }

    /// <summary>Shortest-arc rotation taking direction a onto direction b (both normalized).</summary>
    public static Quaternion FromTo(Vector3 a, Vector3 b)
    {
        var d = Vector3.Dot(a, b);
        if (d >= 1f - Epsilon)
            return Quaternion.Identity;

        if (d <= -1f + Epsilon)
        {
            // 180 degrees: any axis perpendicular to a
            var axis = Vector3.Cross(Vector3.UnitX, a);
            if (axis.LengthSquared() < 1e-6f)
                axis = Vector3.Cross(Vector3.UnitY, a);
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.PI);
        }

        var c = Vector3.Cross(a, b);
        var q = new Quaternion(c.X, c.Y, c.Z, 1f + d);
        return Quaternion.Normalize(q);
    }

    /// <summary>Removes the component of v along unit axis n.</summary>
    public static Vector3 Reject(Vector3 v, Vector3 n) => v - n * Vector3.Dot(v, n);

    /// <summary>
    /// Rotation R such that R*aimLocal == aimTarget and R*poleLocal lies in the half-plane of poleTarget
    /// (both measured perpendicular to the aim axis). All inputs normalized.
    /// </summary>
    public static Quaternion AimPole(Vector3 aimLocal, Vector3 poleLocal, Vector3 aimTarget, Vector3 poleTarget)
    {
        var swing = FromTo(aimLocal, aimTarget);
        var p1 = Reject(swing.Rotate(poleLocal), aimTarget);
        var p2 = Reject(poleTarget, aimTarget);
        if (!TryNormalize(p1, out p1) || !TryNormalize(p2, out p2))
            return swing;

        var angle = MathF.Atan2(Vector3.Dot(aimTarget, Vector3.Cross(p1, p2)), Vector3.Dot(p1, p2));
        var twist = Quaternion.CreateFromAxisAngle(aimTarget, angle);
        return Quaternion.Normalize(twist * swing);
    }

    /// <summary>Clamp the rotation angle of q to maxRadians.</summary>
    public static Quaternion ClampAngle(Quaternion q, float maxRadians)
    {
        q = Quaternion.Normalize(q);
        if (q.W < 0) q = -q;
        var angle = 2f * MathF.Acos(System.Math.Clamp(q.W, -1f, 1f));
        if (angle <= maxRadians || angle < Epsilon)
            return q;
        return Quaternion.Slerp(Quaternion.Identity, q, maxRadians / angle);
    }

    public static Quaternion EnsureSameHemisphere(Quaternion q, Quaternion reference)
        => Quaternion.Dot(q, reference) < 0 ? -q : q;

    public static bool IsFinite(Quaternion q)
        => float.IsFinite(q.X) && float.IsFinite(q.Y) && float.IsFinite(q.Z) && float.IsFinite(q.W);

    public static bool IsFinite(Vector3 v)
        => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    /// <summary>parent ∘ local for Havok QS transforms (scale applied component-wise).</summary>
    public static void Compose(Vector3 parentPos, Quaternion parentRot, Vector3 parentScale,
        Vector3 localPos, Quaternion localRot, Vector3 localScale,
        out Vector3 pos, out Quaternion rot, out Vector3 scale)
    {
        pos = parentPos + parentRot.Rotate(parentScale * localPos);
        rot = Quaternion.Normalize(parentRot * localRot);
        scale = parentScale * localScale;
    }

    /// <summary>Inverse of <see cref="Compose"/>: local transform of child given both model transforms.</summary>
    public static void Decompose(Vector3 parentPos, Quaternion parentRot, Vector3 parentScale,
        Vector3 childPos, Quaternion childRot,
        out Vector3 localPos, out Quaternion localRot)
    {
        var inv = Quaternion.Inverse(parentRot);
        var p = inv.Rotate(childPos - parentPos);
        localPos = new Vector3(
            SafeDiv(p.X, parentScale.X),
            SafeDiv(p.Y, parentScale.Y),
            SafeDiv(p.Z, parentScale.Z));
        localRot = Quaternion.Normalize(inv * childRot);
    }

    private static float SafeDiv(float a, float b) => MathF.Abs(b) < 1e-6f ? a : a / b;

    public static void Read(hkQsTransformf* t, out Vector3 pos, out Quaternion rot, out Vector3 scale)
    {
        pos = t->Translation.ToVec();
        rot = t->Rotation.ToQuat();
        scale = t->Scale.ToVec();
    }
}
