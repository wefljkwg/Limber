using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;

namespace Limber.Rig;

/// <summary>One character's stomach bulge (Body shape page; kept by name).</summary>
[Serializable]
public sealed class BulgeSettings
{
    public bool Enabled { get; set; }
    /// <summary>How far it pushes out (times a bit under a quarter of the hips' width).</summary>
    public float Size { get; set; } = 1f;
    /// <summary>How long (1 = from the pubis to the navel).</summary>
    public float Length { get; set; } = 0.8f;
    /// <summary>How wide (times about a shaft's width).</summary>
    public float Width { get; set; } = 1f;
    /// <summary>Where it starts, up (+) or down (-) from the pubis (times the pubis-to-navel distance).</summary>
    public float Start { get; set; }
    /// <summary>Off to one side (-1 left .. 1 right, times the width).</summary>
    public float Side { get; set; }
    /// <summary>0 a ridge (a capsule along the belly) .. 1 a round dome.</summary>
    public float Shape { get; set; }
    /// <summary>How much more the far end pushes out (the tip).</summary>
    public float Peak { get; set; } = 0.6f;
    /// <summary>How low it is near the pubis, rising toward the end (0 even all along).</summary>
    public float Taper { get; set; } = 0.4f;
    /// <summary>How round (long) the far end is (low: blunt, high: drawn out).</summary>
    public float Roundness { get; set; } = 1f;
    /// <summary>0 crisp edges .. 1 very soft.</summary>
    public float Softness { get; set; } = 0.5f;
    /// <summary>How well the penis's own shape shows through (a rounder shaft, the head's rim).</summary>
    public float PenisShape { get; set; } = 0.3f;
    /// <summary>Creases along both sides where the skin is pulled up over it.</summary>
    public float Creases { get; set; } = 0.4f;
    /// <summary>The belly round it pulled in a little.</summary>
    public float Tightening { get; set; } = 0.3f;
    /// <summary>The navel stretched flat where the bulge reaches it.</summary>
    public float Navel { get; set; } = 0.5f;
    /// <summary>Follow the partner's penis: grows up the belly as the tip goes deeper and sinks as it pulls out.</summary>
    public bool Follow { get; set; }
    /// <summary>How quickly it follows (rises; it sinks a little slower).</summary>
    public float FollowSpeed { get; set; } = 1f;

    public BulgeSettings Copy() => (BulgeSettings)MemberwiseClone();
}

/// <summary>
/// The stomach bulge: a shape pushed out of the front of the belly (a ridge up the middle, rising to a rounded peak
/// at its far end, or a round dome), drawn by the live rig on the belly's skin. With Follow on it grows and sinks with
/// how deep the partner's penis tip is. The shape is worked out in the body's rest pose (model space: X to the side,
/// Y up, Z forward), so it moves with the body.
/// </summary>
public static class Bulge
{
    /// <summary>The settings for each character (by address), set every frame by the plugin (framework thread).</summary>
    public static readonly ConcurrentDictionary<nint, BulgeSettings> Wanted = new();
    /// <summary>The partner's penis tip and the bone before it (world), for each character following one.</summary>
    public static readonly ConcurrentDictionary<nint, (Vector3 Tip, Vector3 Back)> Tips = new();

    public static bool AnyOn => !Wanted.IsEmpty;

    /// <summary>What's happening with each character's bulge (for the page).</summary>
    public static readonly ConcurrentDictionary<nint, string> Status = new();
    /// <summary>Skin vertices the bulge moved last frame (all characters), and this frame's count so far.</summary>
    public static volatile int TouchedLast;
    public static int TouchedNow;

    /// <summary>Where the bulge is this frame, in the body's rest pose (see <see cref="Height"/>).</summary>
    public sealed class Now
    {
        public float X0, Y0, Length, Width, Amp, Shape, Peak, Taper, Round, Soft, Penis, Creases, Tight, Navel;
        public Vector3 NavelAt;
        public float NavelRadius, FrontZ, HipWidth;
    }

    /// <summary>The body's landmarks at rest (model space).</summary>
    public sealed class Marks
    {
        public float MidX, PubisY, NavelY, PelvisZ, HipWidth;
        public Vector3 NavelAt;
        public Matrix4x4 PelvisRest;
        public int PelvisBone = -1;
    }

    public static Marks? MarksFrom(Func<string, (Vector3 Position, Quaternion Rotation, Vector3 Scale)?> rest, Func<string, int> index)
    {
        if(rest("j_asi_a_l") is not { } hl || rest("j_asi_a_r") is not { } hr || rest("j_kosi") is not { } pelvis)
            return null;
        var spineA = rest("j_sebo_a");
        var spineB = rest("j_sebo_b");
        float hw = Vector3.Distance(hl.Position, hr.Position);
        if(hw < 0.02f || !float.IsFinite(hw))
            return null;
        float hips = (hl.Position.Y + hr.Position.Y) * 0.5f;
        float pubis = hips - hw * 0.12f;
        float navel = spineA is { } a && spineB is { } b ? a.Position.Y + (b.Position.Y - a.Position.Y) * 0.35f
            : spineA is { } a2 ? a2.Position.Y + hw * 0.35f : pubis + hw * 1.1f;
        if(navel <= pubis + 0.02f)
            navel = pubis + hw * 1.1f;
        float midX = (hl.Position.X + hr.Position.X) * 0.5f;
        float z = pelvis.Position.Z;
        return new Marks
        {
            MidX = midX, PubisY = pubis, NavelY = navel, PelvisZ = z, HipWidth = hw,
            NavelAt = new Vector3(midX, navel, z + hw * 0.55f),
            PelvisRest = Matrix4x4.CreateScale(pelvis.Scale) * Matrix4x4.CreateFromQuaternion(pelvis.Rotation) * Matrix4x4.CreateTranslation(pelvis.Position),
            PelvisBone = index("j_kosi"),
        };
    }

    /// <summary>The shape at these settings (and, following, this length and side), for the render thread.</summary>
    public static Now Make(Marks m, BulgeSettings s, float lengthNow, float sideNow, float strength)
    {
        float span = m.NavelY - m.PubisY;
        return new Now
        {
            X0 = m.MidX + sideNow,
            Y0 = m.PubisY + Math.Clamp(s.Start, -0.5f, 1f) * span,
            Length = MathF.Max(lengthNow, 0.005f),
            Width = Math.Clamp(s.Width, 0.2f, 3f) * m.HipWidth * 0.17f,
            Amp = Math.Clamp(s.Size, 0f, 3f) * m.HipWidth * 0.2f * Math.Clamp(strength, 0f, 1f),
            Shape = Math.Clamp(s.Shape, 0f, 1f),
            Peak = Math.Clamp(s.Peak, 0f, 2f),
            Taper = Math.Clamp(s.Taper, 0f, 1f),
            Round = Math.Clamp(s.Roundness, 0.2f, 3f),
            Soft = Math.Clamp(s.Softness, 0f, 1f),
            Penis = Math.Clamp(s.PenisShape, 0f, 1f),
            Creases = Math.Clamp(s.Creases, 0f, 1f),
            Tight = Math.Clamp(s.Tightening, 0f, 1f),
            Navel = Math.Clamp(s.Navel, 0f, 1f),
            NavelAt = m.NavelAt,
            NavelRadius = m.HipWidth * 0.12f,
            FrontZ = m.PelvisZ,
            HipWidth = m.HipWidth,
        };
    }

    private static float Smooth(float e0, float e1, float x)
    {
        float t = Math.Clamp((x - e0) / (e1 - e0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    // The bulge alone (no creases, tightening or navel): how far out at (x, y).
    private static float Raised(Now b, float x, float y)
    {
        float t = (y - b.Y0) / b.Length;
        float w = b.Width;
        // (the head's rim: a little wider near the end, with a slight neck before it)
        float rim = b.Penis * Smooth(0.72f, 0.9f, t) * (1f - Smooth(1.0f, 1.1f, t));
        float wHere = w * (1f + 0.22f * rim);
        float xr = (x - b.X0) / wHere;
        // Across: soft (a bell) or rounder like a shaft under the skin.
        float bell = MathF.Exp(-xr * xr * 2.2f);
        float shaft = xr * xr < 1f ? MathF.Sqrt(1f - xr * xr) : 0f;
        float crisp = MathF.Pow(MathF.Max(shaft, 0f), 0.6f);
        float soft = 0.35f + 0.65f * b.Soft;
        float across = bell * soft + (1f - soft) * crisp;
        across = across * (1f - b.Penis * 0.6f) + shaft * b.Penis * 0.6f;
        // Along: rising out of the pubis, lower near it (taper), a round end, and the peak at the end.
        // (nothing at or below the pubis: the pubic hair and what's below stay as they are)
        float rise = Smooth(0.02f, 0.2f + 0.15f * b.Soft, t);
        float endLen = MathF.Max(w * b.Round / b.Length, 0.02f);
        float end = t <= 1f ? 1f : MathF.Exp(-MathF.Pow((t - 1f) / endLen, 2f));
        float level = 1f - b.Taper * (1f - Math.Clamp(t, 0f, 1f));
        float neck = 1f - 0.18f * b.Penis * MathF.Exp(-MathF.Pow((t - 0.82f) / 0.05f, 2f));
        float ridge = rise * end * level * neck * across;
        // the peak: a rounder bump where the tip is
        float pr = MathF.Sqrt(xr * xr / 1.3f + MathF.Pow((t - 1f) * b.Length / (w * 1.25f), 2f));
        float peak = b.Peak * MathF.Exp(-pr * pr * 1.6f) * Smooth(0.02f, 0.25f, t);
        // A dome: round, centred on the far end.
        float dx = (x - b.X0) / (w * 2.2f), dy = (y - (b.Y0 + b.Length * 0.75f)) / MathF.Max(b.Length * 0.6f, w * 2f);
        float r2 = dx * dx + dy * dy;
        float dome = r2 < 1f ? MathF.Pow(1f - r2, 1.2f + (1f - b.Soft)) : 0f;
        dome *= (1f + b.Peak * 0.4f) * Smooth(0.02f, 0.25f, t);
        return (ridge + peak) * (1f - b.Shape) + dome * b.Shape;
    }

    /// <summary>How far the skin at rest point (x, y) is pushed out along its normal (metres; negative: pulled in).</summary>
    public static float Height(Now b, float x, float y)
    {
        if(b.Amp <= 0f)
            return 0f;
        float h = Raised(b, x, y);
        float t = (y - b.Y0) / b.Length;
        float along = Smooth(0.02f, 0.25f, t) * (t <= 1.15f ? 1f : MathF.Exp(-MathF.Pow((t - 1.15f) * 4f, 2f)));
        float xr = MathF.Abs(x - b.X0) / (b.Width * (1f + b.Shape * 1.2f));
        float crease = b.Creases * 0.22f * MathF.Exp(-MathF.Pow((xr - 1.45f) / 0.28f, 2f)) * along;
        float tight = b.Tight * 0.12f * MathF.Exp(-MathF.Pow(xr / 3.2f, 2f)) * Smooth(0.02f, 0.3f, t) * (t < 1.3f ? 1f : MathF.Exp(-MathF.Pow(t - 1.3f, 2f) * 6f));
        float total = h - crease - tight * (1f - MathF.Min(h, 1f));
        // the navel filled out (stretched flat) where the bulge reaches it
        float nd = MathF.Sqrt(MathF.Pow(x - b.NavelAt.X, 2f) + MathF.Pow(y - b.NavelAt.Y, 2f)) / MathF.Max(b.NavelRadius, 0.005f);
        if(nd < 1.5f && b.Navel > 0f)
            total += b.Navel * MathF.Min(h, 1f) * 0.25f * MathF.Exp(-nd * nd * 2f);
        return total * b.Amp;
    }
}
