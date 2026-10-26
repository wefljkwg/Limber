using System;
using System.Numerics;

namespace Limber.BodyRig;

/// <summary>
/// A collider's solid shape: a core (a line for a capsule, a box for a box) grown by a radius. A box's core is the
/// box less its rounded corners, so the box itself is exactly its size. Everything that collides measures with
/// <see cref="Gap(Vector3, out Vector3, out Vector3)"/>: the signed distance from the core (below 0 inside a box's
/// core), the core's nearest point, and the way out.
/// </summary>
public readonly struct Shape
{
    public readonly Vector3 A, B;       // capsule: the core line's ends; box: both the centre
    public readonly float R;            // capsule: radius; box: corner rounding
    public readonly bool Box;
    public readonly Vector3 U, V, W;    // box: its own axes (unit)
    public readonly Vector3 Half;       // box: half the core's size along U, V, W

    public Shape(Vector3 a, Vector3 b, float r)
    {
        A = a;
        B = b;
        R = r;
        Box = false;
        U = Vector3.UnitX;
        V = Vector3.UnitY;
        W = Vector3.UnitZ;
        Half = Vector3.Zero;
    }

    public Shape(Vector3 centre, Vector3 u, Vector3 v, Vector3 w, Vector3 half, float r)
    {
        A = B = centre;
        R = r;
        Box = true;
        U = u;
        V = v;
        W = w;
        Half = Vector3.Max(half, Vector3.Zero);
    }

    /// <summary>A box of full size <paramref name="size"/> (along the frame's x, y, z), corners rounded by <paramref name="round"/>.</summary>
    public static Shape BoxOf(Vector3 centre, Quaternion rotation, Vector3 size, float round)
    {
        var half = Vector3.Max(size * 0.5f, new Vector3(0.0005f));
        float r = MathF.Min(round, MathF.Min(half.X, MathF.Min(half.Y, half.Z)) * 0.5f);
        return new Shape(centre, Vector3.Transform(Vector3.UnitX, rotation), Vector3.Transform(Vector3.UnitY, rotation),
            Vector3.Transform(Vector3.UnitZ, rotation), half - new Vector3(r), r);
    }

    public Shape WithRadius(float r) => Box ? new Shape(A, U, V, W, Half, r) : new Shape(A, B, r);

    /// <summary>The same shape moved by <paramref name="point"/> (e.g. world to model space), sizes scaled by <paramref name="scale"/>.</summary>
    public Shape Moved(Func<Vector3, Vector3> point, float scale)
    {
        if (!Box)
            return new Shape(point(A), point(B), R * scale);
        var at = A;
        var c = point(at);
        Vector3 Axis(Vector3 a)
        {
            var d = point(at + a) - c;
            return d.LengthSquared() > 1e-12f ? Vector3.Normalize(d) : a;
        }
        return new Shape(c, Axis(U), Axis(V), Axis(W), Half * scale, R * scale);
    }

    /// <summary>The line it lies along (a box: none, both ends its centre).</summary>
    public bool HasLength => !Box && (B - A).LengthSquared() > 1e-12f;

    /// <summary>
    /// Signed distance from the core: how far <paramref name="p"/> is from it, below 0 inside a box's core (by how
    /// deep). <paramref name="core"/> is its nearest point on the core (on a face, from inside) and
    /// <paramref name="normal"/> the way out (zero when it can't be told: right on a capsule's line).
    /// </summary>
    public float Gap(Vector3 p, out Vector3 core, out Vector3 normal)
    {
        if (!Box)
        {
            core = Closest(A, B, p);
            var d = p - core;
            float len = d.Length();
            normal = len > 1e-9f ? d / len : Vector3.Zero;
            return len;
        }
        var q = p - A;
        float x = Vector3.Dot(q, U), y = Vector3.Dot(q, V), z = Vector3.Dot(q, W);
        float ox = MathF.Abs(x) - Half.X, oy = MathF.Abs(y) - Half.Y, oz = MathF.Abs(z) - Half.Z;
        if (ox > 0f || oy > 0f || oz > 0f)
        {
            float cx = Math.Clamp(x, -Half.X, Half.X), cy = Math.Clamp(y, -Half.Y, Half.Y), cz = Math.Clamp(z, -Half.Z, Half.Z);
            core = A + U * cx + V * cy + W * cz;
            var d = p - core;
            float len = d.Length();
            normal = len > 1e-9f ? d / len : Vector3.Zero;
            return len;
        }
        // Inside: out through the nearest face.
        if (ox >= oy && ox >= oz)
        {
            normal = U * (x >= 0f ? 1f : -1f);
            core = p + normal * -ox;
            return ox;
        }
        if (oy >= oz)
        {
            normal = V * (y >= 0f ? 1f : -1f);
            core = p + normal * -oy;
            return oy;
        }
        normal = W * (z >= 0f ? 1f : -1f);
        core = p + normal * -oz;
        return oz;
    }

    public float Gap(Vector3 p) => Gap(p, out _, out _);

    /// <summary>How far along <paramref name="dir"/> (unit) <paramref name="p"/> has to go to be <paramref name="radius"/>
    /// clear of the core (0 if it already is).</summary>
    public float Exit(Vector3 p, Vector3 dir, float radius)
    {
        if (Gap(p) >= radius)
            return 0f;
        float hi = MathF.Max(radius, 0.005f);
        float limit = 2f * (Half.X + Half.Y + Half.Z) + (B - A).Length() + radius * 4f + 0.05f;
        while (Gap(p + dir * hi) < radius)
        {
            hi *= 2f;
            if (hi > limit)
                return hi;
        }
        float lo = hi * 0.5f;
        if (Gap(p + dir * lo) >= radius)
            lo = 0f;
        for (int i = 0; i < 14; i++)
        {
            float mid = (lo + hi) * 0.5f;
            if (Gap(p + dir * mid) >= radius)
                hi = mid;
            else
                lo = mid;
        }
        return hi;
    }

    /// <summary>The nearest point of the core along a capsule's line, as a fraction (0..1); a box: 0.5 (always "along its side").</summary>
    public float Along(Vector3 p)
    {
        if (Box)
            return 0.5f;
        var ab = B - A;
        float len2 = ab.LengthSquared();
        return len2 > 1e-12f ? Vector3.Dot(p - A, ab) / len2 : 0f;
    }

    /// <summary>Takes the part of <paramref name="v"/> along a capsule's line out (a box: unchanged).</summary>
    public Vector3 Across(Vector3 v)
    {
        if (Box)
            return v;
        var ab = B - A;
        float len2 = ab.LengthSquared();
        return len2 > 1e-12f ? v - ab * (Vector3.Dot(v, ab) / len2) : v;
    }

    /// <summary>The smallest of its sizes (a capsule: its radius; a box: half its thinnest side).</summary>
    public float Thickness => Box ? MathF.Min(Half.X, MathF.Min(Half.Y, Half.Z)) + R : R;

    public static Vector3 Closest(Vector3 a, Vector3 b, Vector3 p)
    {
        var ab = b - a;
        float len2 = ab.LengthSquared();
        if (len2 < 1e-12f)
            return a;
        float t = Math.Clamp(Vector3.Dot(p - a, ab) / len2, 0f, 1f);
        return a + ab * t;
    }
}
