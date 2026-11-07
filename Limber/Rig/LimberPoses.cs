using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using Limber.Core;

namespace Limber.Rig;

/// <summary>
/// Limber Poses (Posing): ready-made whole-body poses (A-pose, T-pose, splits, the Jack-O pose), made for each
/// character's own skeleton and saved as its FK pose, so they can be fine-tuned on the FK Posing page afterwards.
/// Each pose says which way each part of the body points (in the character's own directions: X to its left, Y up,
/// Z forward), worked out from the skeleton at rest; poses with the body low (splits, Jack-O) are lowered until
/// their legs / feet reach the floor.
/// </summary>
public sealed class LimberPoses
{
    /// <summary>One bone pointed so that the way to <paramref name="To"/> goes along <paramref name="Dir"/>.</summary>
    public readonly record struct Aim(string Bone, string To, Vector3 Dir);

    /// <summary>
    /// Legs spread just wide enough for the hips to come down until <paramref name="Touch"/> (a chest bone) is as
    /// low as the feet, less <paramref name="Below"/> (in leg lengths): the Jack-O, where the chest lies on the floor
    /// between straight legs. <paramref name="Legs"/> gives the legs' aims for a spread angle from straight down.
    /// </summary>
    public sealed record Fit(string Touch, float Below, Func<float, Aim[]> Legs);

    /// <param name="Variant">Poses with the same name are one button with a choice (Splits: forward, to the sides).</param>
    public sealed record Pose(string Name, string Tip, Aim[] Aims, string[] Ground, float Clearance, string Variant = "", Fit? Fit = null);

    private static Vector3 V(float x, float y, float z) => Vector3.Normalize(new Vector3(x, y, z));

    // (x is the character's left: the right side's directions are the left's with x turned around)
    private static IEnumerable<Aim> Both(string bone, string to, Vector3 left)
    {
        yield return new Aim(bone + "_l", to + "_l", left);
        yield return new Aim(bone + "_r", to + "_r", new Vector3(-left.X, left.Y, left.Z));
    }

    private static Aim[] Join(params IEnumerable<Aim>[] parts)
    {
        var list = new List<Aim>();
        foreach(var p in parts)
            list.AddRange(p);
        return list.ToArray();
    }

    private static readonly Aim[] Upright =
    [
        new("j_sebo_a", "j_sebo_b", V(0, 1, 0)),
        new("j_sebo_b", "j_sebo_c", V(0, 1, 0)),
        new("j_sebo_c", "j_kubi", V(0, 1, 0)),
        new("j_kubi", "j_kao", V(0, 1, 0.05f)),
    ];

    private static IEnumerable<Aim> Arms(Vector3 upper, Vector3 fore, Vector3 hand)
        => Join(Both("j_ude_a", "j_ude_b", upper), Both("j_ude_b", "j_te", fore), Both("j_te", "j_naka_a", hand));

    private static IEnumerable<Aim> Legs(Vector3 thigh, Vector3 shin)
        => Join(Both("j_asi_a", "j_asi_c", thigh), Both("j_asi_c", "j_asi_d", shin));

    public static readonly Pose[] All =
    [
        new("A-Pose", "Standing straight, arms down and out at 45°, legs straight.",
            Join(Upright, Arms(V(0.72f, -0.7f, 0), V(0.72f, -0.7f, 0), V(0.72f, -0.7f, 0)), Legs(V(0.06f, -1, 0), V(0.02f, -1, 0))), [], 0f),
        new("T-Pose", "Standing straight, arms straight out to the sides, legs straight.",
            Join(Upright, Arms(V(1, 0, 0), V(1, 0, 0), V(1, 0, 0)), Legs(V(0.04f, -1, 0), V(0, -1, 0))), [], 0f),
        new("Splits", "Left leg forward, right leg back, flat on the floor; upright, arms out to the sides.",
            Join(Upright, Arms(V(1, 0.15f, 0), V(1, 0.15f, 0), V(1, 0.1f, 0)),
                [new("j_asi_a_l", "j_asi_c_l", V(0.02f, -0.06f, 1)), new("j_asi_c_l", "j_asi_d_l", V(0, -0.04f, 1)),
                 new("j_asi_d_l", "j_asi_e_l", V(0, -0.3f, 1)),
                 new("j_asi_a_r", "j_asi_c_r", V(-0.02f, -0.06f, -1)), new("j_asi_c_r", "j_asi_d_r", V(0, -0.04f, -1)),
                 new("j_asi_d_r", "j_asi_e_r", V(0, -0.4f, -1))]),
            ["j_asi_c_l", "j_asi_d_l", "j_asi_c_r", "j_asi_d_r"], 0.07f, "Forward and back"),
        new("Splits", "Both legs straight out to the sides, flat on the floor; upright, arms out to the sides.",
            Join(Upright, Arms(V(1, 0.15f, 0), V(1, 0.15f, 0), V(1, 0.1f, 0)), Legs(V(1, -0.06f, 0), V(1, -0.04f, 0)),
                Both("j_asi_d", "j_asi_e", V(0.2f, 0.6f, 0.3f))),
            ["j_asi_c_l", "j_asi_d_l", "j_asi_c_r", "j_asi_d_r"], 0.07f, "To the sides"),
        new("Jack-O Pose", "Lying on the chest, back arched and hips up high; legs straight and spread wide out behind, toes on the floor; elbows on the floor, chin resting on the hands, looking ahead.",
            Join(
                // (from the hips, high up, the back slopes forward and down to the chest lying on the floor in front;
                // the neck curls back up so the head looks ahead over the hands)
                [new("j_sebo_a", "j_sebo_b", V(0, -0.72f, 0.69f)), new("j_sebo_b", "j_sebo_c", V(0, -0.5f, 0.87f)),
                 new("j_sebo_c", "j_kubi", V(0, -0.12f, 1f)), new("j_kubi", "j_kao", V(0, 0.62f, 0.78f))],
                // (upper arms forward and down to the elbows on the floor in front, forearms up and in to meet under
                // the chin, hands cupping it)
                Arms(V(0.28f, -0.55f, 0.79f), V(-0.72f, 0.5f, 0.48f), V(-0.35f, 0.85f, 0.4f)),
                JackOLegs(1.1f),
                // (feet stretched back, toes on the floor)
                Both("j_asi_d", "j_asi_e", V(0.15f, -0.55f, -0.82f))),
            ["j_asi_d_l", "j_asi_d_r"], 0.06f, "", new Fit("j_sebo_c", -0.1f, JackOLegs)),
    ];

    // Both legs straight, spread this far from straight down: out to the sides and back behind the hips (wider
    // brings the hips lower, so the chest in front reaches the floor).
    private static Aim[] JackOLegs(float angle)
    {
        var d = V(MathF.Sin(angle) * 0.82f, -MathF.Cos(angle), -MathF.Sin(angle) * 0.55f);
        return Join(Legs(d, d));
    }

    /// <summary>The same pose with left and right swapped (the other leg forward in the splits).</summary>
    public static Pose Mirrored(Pose p)
    {
        static string Swap(string b) => b.EndsWith("_l", StringComparison.Ordinal) ? b[..^2] + "_r" : b.EndsWith("_r", StringComparison.Ordinal) ? b[..^2] + "_l" : b;
        Aim[] Flip(Aim[] from)
        {
            var aims = new Aim[from.Length];
            for(int i = 0; i < aims.Length; i++)
            {
                var a = from[i];
                aims[i] = new Aim(Swap(a.Bone), Swap(a.To), new Vector3(-a.Dir.X, a.Dir.Y, a.Dir.Z));
            }
            return aims;
        }
        var ground = new string[p.Ground.Length];
        for(int i = 0; i < ground.Length; i++)
            ground[i] = Swap(p.Ground[i]);
        var fit = p.Fit is { } f ? f with { Touch = Swap(f.Touch), Legs = angle => Flip(f.Legs(angle)) } : null;
        return p with { Aims = Flip(p.Aims), Ground = ground, Fit = fit };
    }

    // The bones a pose is saved on (held as the pose makes them; the rest keep animating).
    private static readonly string[] Held =
    [
        "j_kosi", "j_sebo_a", "j_sebo_b", "j_sebo_c", "j_kubi", "j_kao",
        "j_sako_l", "j_sako_r", "j_ude_a_l", "j_ude_a_r", "j_ude_b_l", "j_ude_b_r", "j_te_l", "j_te_r",
        "j_asi_a_l", "j_asi_a_r", "j_asi_b_l", "j_asi_b_r", "j_asi_c_l", "j_asi_c_r", "j_asi_d_l", "j_asi_d_r", "j_asi_e_l", "j_asi_e_r",
    ];

    private readonly record struct Request(nint Address, ushort Index, string Character, Pose Pose, bool Freeze);
    /// <summary>A pose worked out: its bones, and how far the whole character goes down (world metres; 0 = stays).
    /// <paramref name="Layered"/>: the bones are turns on top of the animation (it keeps breathing and moving a little
    /// about the pose), else each holds the pose's turn exactly (still).</summary>
    public readonly record struct Result(string Character, nint Address, ushort Index, Dictionary<string, FkBoneSettings> Bones, string Pose, float Drop, bool Layered);

    private readonly ConcurrentQueue<Request> _requests = new();
    private readonly ConcurrentQueue<Result> _results = new();
    private readonly SkeletonInfoCache _cache = new();

    /// <summary>Make this pose for this character (worked out in the next bone update).</summary>
    public void Put(nint address, ushort index, string character, Pose pose, bool freeze = false)
        => _requests.Enqueue(new Request(address, index, character, pose, freeze));

    /// <summary>Framework thread: the poses worked out since.</summary>
    public bool TryTake(out Result result) => _results.TryDequeue(out result);

    /// <summary>Bone update stage.</summary>
    public void UpdateFrame()
    {
        while(_requests.TryDequeue(out var r))
        {
            try
            {
                if(ActorSkeleton.TryGet(r.Address, r.Index, _cache, out var sk) && sk.Info.Count > 0 && Solve(sk, r.Pose, out float drop) is { } bones)
                {
                    if(!r.Freeze)
                    {
                        // Not frozen: each bone's turn is what takes the animation's pose of it (as it is now) to the
                        // pose's, put on top of the animation, so the idle's breathing and sway carry on about the pose.
                        foreach(var (name, b) in bones)
                        {
                            int i = sk.Info.IndexOf(name);
                            if(i < 0)
                                continue;
                            int parent = sk.Info.ParentOf(i);
                            var pr = parent >= 0 ? sk.GetModelFrame(parent).Rotation : Quaternion.Identity;
                            var anim = Quaternion.Normalize(Quaternion.Inverse(pr) * sk.GetModelFrame(i).Rotation);
                            b.Rotation = Quaternion.Normalize(Quaternion.Inverse(anim) * b.Rotation);
                        }
                    }
                    _results.Enqueue(new Result(r.Character, r.Address, r.Index, bones, r.Pose.Name, drop * sk.RootScale.Y, !r.Freeze));
                }
            }
            catch(Exception e)
            {
                Plugin.Log.Debug(e, "Limber: couldn't make a pose");
            }
        }
    }

    // The pose on the skeleton at rest: each bone turned (with everything below it) to point its way, parents first;
    // then lowered to the floor if it has to be; then each held bone's turn relative to its parent.
    private static Dictionary<string, FkBoneSettings>? Solve(in ActorSkeleton sk, Pose pose, out float drop)
    {
        drop = 0f;
        var info = sk.Info;
        var aims = pose.Aims;
        Vector3[] pos;
        Quaternion[] rot;
        if(pose.Fit is { } fit && info.IndexOf(fit.Touch) is var touch and >= 0
           && info.IndexOf("j_asi_d_l") is var foot and >= 0)
        {
            // The spread where the feet come level with the chest (less a little): wider legs bring the hips (and
            // the chest hanging from them) down closer to the feet. Halved until close enough.
            // (between about 30° and 70° from straight down: past that the hips sink too low to look right)
            float lo = 0.45f, hi = 1.45f;
            for(int step = 0; step < 18; step++)
            {
                float mid = (lo + hi) * 0.5f;
                if(!Place(sk, Swapped(aims, fit, mid), out pos, out rot))
                    return null;
                float leg = MathF.Max(LegLength(info, pos), 0.3f);
                // feet still below the chest's mark: spread wider
                if(pos[foot].Y < pos[touch].Y - fit.Below * leg)
                    lo = mid;
                else
                    hi = mid;
            }
            aims = Swapped(aims, fit, (lo + hi) * 0.5f);
        }
        if(!Place(sk, aims, out pos, out rot))
            return null;

        // Down (or up) to the floor (the model's origin is at the feet): the lowest of the bones that should touch it,
        // a little above it (the leg's own thickness). The whole character is moved for it (Move character), so
        // everything on it comes along.
        int hips = info.IndexOf("j_kosi");
        if(pose.Ground.Length > 0 && hips >= 0)
        {
            float lowest = float.MaxValue;
            foreach(var g in pose.Ground)
            {
                int b = info.IndexOf(g);
                if(b >= 0)
                    lowest = MathF.Min(lowest, pos[b].Y);
            }
            float floor = pose.Clearance * MathF.Max(LegLength(info, pos), 0.3f);
            if(lowest < float.MaxValue && MathF.Abs(lowest - floor) > 1e-4f)
                drop = floor - lowest;
        }

        var bones = new Dictionary<string, FkBoneSettings>();
        foreach(var name in Held)
        {
            int b = info.IndexOf(name);
            if(b < 0)
                continue;
            int parent = info.ParentOf(b);
            var parentRot = parent >= 0 ? rot[parent] : Quaternion.Identity;
            bones[name] = new FkBoneSettings { Rotation = Quaternion.Normalize(Quaternion.Inverse(parentRot) * rot[b]) };
        }
        return bones;
    }

    // The pose's aims with the legs' replaced by the fit's at this spread.
    private static Aim[] Swapped(Aim[] aims, Fit fit, float angle)
    {
        var legs = fit.Legs(angle);
        var list = new List<Aim>(aims.Length);
        foreach(var a in aims)
        {
            int i = Array.FindIndex(legs, l => l.Bone == a.Bone);
            list.Add(i >= 0 ? legs[i] : a);
        }
        return list.ToArray();
    }

    private static float LegLength(SkeletonInfo info, Vector3[] pos)
    {
        int a = info.IndexOf("j_asi_a_l"), c = info.IndexOf("j_asi_c_l"), d = info.IndexOf("j_asi_d_l");
        return a >= 0 && c >= 0 && d >= 0 ? Vector3.Distance(pos[a], pos[c]) + Vector3.Distance(pos[c], pos[d]) : 0f;
    }

    // The skeleton at rest, then each aim's bone turned (with everything below it) to point its way, parents first.
    private static bool Place(in ActorSkeleton sk, Aim[] aims, out Vector3[] pos, out Quaternion[] rot)
    {
        var info = sk.Info;
        int n = info.Count;
        pos = new Vector3[n];
        rot = new Quaternion[n];
        var p = pos;
        var r = rot;
        for(int i = 0; i < n; i++)
        {
            int parent = info.ParentOf(i);
            sk.TryGetReferenceLocal(i, out var lp, out var lq);
            if(parent >= 0 && parent < i)
            {
                pos[i] = pos[parent] + Vector3.Transform(lp, rot[parent]);
                rot[i] = Quaternion.Normalize(rot[parent] * lq);
            }
            else
            {
                pos[i] = lp;
                rot[i] = lq;
            }
        }
        var children = new List<int>[n];
        for(int i = 0; i < n; i++)
            children[i] = new List<int>();
        for(int i = 0; i < n; i++)
        {
            int parent = info.ParentOf(i);
            if(parent >= 0 && parent < n)
                children[parent].Add(i);
        }
        void Turn(int bone, Quaternion q)
        {
            var stack = new Stack<int>();
            stack.Push(bone);
            var pivot = p[bone];
            while(stack.Count > 0)
            {
                int b = stack.Pop();
                if(b != bone)
                    p[b] = pivot + Vector3.Transform(p[b] - pivot, q);
                r[b] = Quaternion.Normalize(q * r[b]);
                foreach(int c in children[b])
                    stack.Push(c);
            }
        }
        int found = 0;
        foreach(var aim in aims)
        {
            int b = info.IndexOf(aim.Bone), to = info.IndexOf(aim.To);
            if(b < 0 || to < 0)
                continue;
            var now = pos[to] - pos[b];
            if(now.LengthSquared() < 1e-10f)
                continue;
            var q = MathUtil.FromTo(Vector3.Normalize(now), aim.Dir);
            if(!MathUtil.IsFinite(q))
                continue;
            Turn(b, q);
            found++;
        }
        return found > 0;
    }
}
