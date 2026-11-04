using Limber.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;

namespace Limber.Rig;

/// <summary>
/// Ragdoll mode (Extras): the character goes limp and falls where they stand. The body's joints become points that
/// fall under gravity, held together by links the length of the bones (the torso held stiff, the neck, arms and legs
/// loose), landing on the floor they stood on and sliding to a stop; each frame the skeleton is set from the points
/// (each bone turned from where it was at the start to where its points are now, so nothing drifts). Bone update,
/// after posing. Only on your screen.
/// </summary>
public sealed unsafe class RagdollService
{
    private sealed class Body
    {
        public ushort Index;
        public Vector3[] P = [], Prev = [];
        public float[] Radius = [];
        public List<(int A, int B, float Length, float Min, float Max)> Links = new();
        public float Ground;
        public long Last;
        // the bones, parents first: (bone, its point, how it's turned)
        public List<Driven> Bones = new();
    }

    private sealed class Driven
    {
        public int Bone, Point;
        public Quaternion StartRotation;
        // limbs: the point it points at; the torso and head: also two points across (their left / right)
        public int Toward, AcrossA = -1, AcrossB = -1;
        public Vector3 StartDir, StartAcross;
    }

    private readonly SkeletonInfoCache _cache = new();
    private readonly Dictionary<nint, Body> _bodies = new();
    private readonly HashSet<nint> _start = new();
    private readonly HashSet<nint> _stop = new();
    private volatile nint[] _active = [];
    private readonly object _sync = new();

    public bool IsOn(nint address) => Array.IndexOf(_active, address) >= 0;

    /// <summary>Starts (or stops) ragdoll for this character (UI thread).</summary>
    public void Toggle(nint address, ushort index)
    {
        lock(_sync)
        {
            if(IsOn(address))
                _stop.Add(address);
            else
            {
                _start.Add(address);
                _indexOf[address] = index;
            }
        }
    }

    public void StopAll()
    {
        lock(_sync)
            foreach(var a in _active)
                _stop.Add(a);
    }

    private readonly Dictionary<nint, ushort> _indexOf = new();

    private static readonly string[] Torso = ["j_kosi", "j_sebo_a", "j_sebo_b", "j_sebo_c"];

    /// <summary>Bone update stage.</summary>
    public void UpdateFrame()
    {
        lock(_sync)
        {
            foreach(var a in _stop)
                _bodies.Remove(a);
            _stop.Clear();
            foreach(var a in _start)
                if(_indexOf.TryGetValue(a, out var idx) && ActorSkeleton.TryGet(a, idx, _cache, out var sk) && Build(sk, idx) is { } body)
                    _bodies[a] = body;
            _start.Clear();
            var active = new nint[_bodies.Count];
            _bodies.Keys.CopyTo(active, 0);
            _active = active;
        }
        List<nint>? gone = null;
        foreach(var (address, body) in _bodies)
        {
            try
            {
                if(!ActorSkeleton.TryGet(address, body.Index, _cache, out var sk))
                {
                    (gone ??= new()).Add(address);
                    continue;
                }
                Step(body, address);
                Pose(sk, body);
            }
            catch
            {
                (gone ??= new()).Add(address);
            }
        }
        if(gone != null)
            lock(_sync)
                foreach(var a in gone)
                    _bodies.Remove(a);
    }

    // ---- Setting it up from the pose the character is in

    private Body? Build(ActorSkeleton sk, ushort index)
    {
        var info = sk.Info;
        var body = new Body { Index = index, Last = Stopwatch.GetTimestamp() };
        var points = new List<Vector3>();
        var radius = new List<float>();
        var pointOf = new Dictionary<string, int>();

        int Point(string bone, float r)
        {
            if(pointOf.TryGetValue(bone, out var p))
                return p;
            int b = info.IndexOf(bone);
            if(b < 0)
                return -1;
            points.Add(sk.GetModelFrame(b).Position);
            radius.Add(r);
            return pointOf[bone] = points.Count - 1;
        }
        int Extra(Vector3 at, float r)
        {
            points.Add(at);
            radius.Add(r);
            return points.Count - 1;
        }
        void Link(int a, int b, float min = 1f, float max = 1f)
        {
            if(a < 0 || b < 0)
                return;
            float rest = Vector3.Distance(points[a], points[b]);
            if(rest > 1e-4f)
                body.Links.Add((a, b, rest, rest * min, rest * max));
        }

        // The joints.
        int hips = Point("j_kosi", 0.10f);
        int spineA = Point("j_sebo_a", 0.10f), spineB = Point("j_sebo_b", 0.10f), chest = Point("j_sebo_c", 0.10f);
        int neck = Point("j_kubi", 0.05f), head = Point("j_kao", 0.09f);
        if(hips < 0 || chest < 0 || head < 0)
            return null;
        var headDir = neck >= 0 ? Vector3.Normalize(points[head] - points[neck]) : Vector3.UnitY;
        int headTop = Extra(points[head] + headDir * 0.14f, 0.09f);

        var side = new Dictionary<string, int>();
        foreach(var s in new[] { "l", "r" })
        {
            int clavicle = Point($"j_sako_{s}", 0.05f);
            int upper = Point($"j_ude_a_{s}", 0.05f), lower = Point($"j_ude_b_{s}", 0.04f), hand = Point($"j_te_{s}", 0.04f);
            int handEnd = hand >= 0 && lower >= 0 ? Extra(points[hand] + Vector3.Normalize(points[hand] - points[lower]) * 0.08f, 0.04f) : -1;
            int thigh = Point($"j_asi_a_{s}", 0.07f), knee = Point($"j_asi_b_{s}", 0.06f), shin = Point($"j_asi_c_{s}", 0.05f);
            int foot = Point($"j_asi_d_{s}", 0.04f), toe = Point($"j_asi_e_{s}", 0.03f);
            side[$"clavicle{s}"] = clavicle; side[$"upper{s}"] = upper; side[$"lower{s}"] = lower; side[$"hand{s}"] = hand;
            side[$"handEnd{s}"] = handEnd; side[$"thigh{s}"] = thigh; side[$"knee{s}"] = knee; side[$"shin{s}"] = shin;
            side[$"foot{s}"] = foot; side[$"toe{s}"] = toe;
        }

        // The torso, stiff: every torso point linked to every other.
        var core = new List<int>();
        foreach(var p in new[] { hips, spineA, spineB, chest, side["claviclel"], side["clavicler"], side["thighl"], side["thighr"] })
            if(p >= 0)
                core.Add(p);
        for(int i = 0; i < core.Count; i++)
            for(int j = i + 1; j < core.Count; j++)
                Link(core[i], core[j]);
        // Neck and head: the head stays on, the neck bends a little.
        Link(chest, neck);
        Link(neck, head);
        Link(head, headTop);
        Link(neck, headTop);
        Link(chest, head, 0.8f, 1f);
        // Arms and legs: each bone its length; elbows and knees can't fold right up.
        foreach(var s in new[] { "l", "r" })
        {
            Link(side[$"clavicle{s}"], side[$"upper{s}"]);
            Link(side[$"upper{s}"], side[$"lower{s}"]);
            Link(side[$"lower{s}"], side[$"hand{s}"]);
            Link(side[$"hand{s}"], side[$"handEnd{s}"]);
            Link(side[$"lower{s}"], side[$"handEnd{s}"]);
            Link(side[$"upper{s}"], side[$"hand{s}"], 0.35f, 1f);
            Link(side[$"thigh{s}"], side[$"knee{s}"]);
            Link(side[$"knee{s}"], side[$"shin{s}"]);
            Link(side[$"shin{s}"], side[$"foot{s}"]);
            Link(side[$"foot{s}"], side[$"toe{s}"]);
            Link(side[$"shin{s}"], side[$"toe{s}"]);
            Link(side[$"thigh{s}"], side[$"shin{s}"], 0.5f, 1f);
            Link(side[$"thigh{s}"], side[$"foot{s}"], 0.45f, 1f);
        }

        body.P = points.ToArray();
        body.Prev = (Vector3[])body.P.Clone();
        body.Radius = radius.ToArray();
        // The floor: where the feet are (or a little under the lowest point).
        float ground = float.MaxValue;
        foreach(var p in body.P)
            ground = MathF.Min(ground, p.Y);
        body.Ground = ground - 0.01f;
        // a small push so it doesn't balance upright: back and a touch to one side
        var push = new Vector3(0.01f, 0.004f, -0.03f); // (a firm shove back, and a touch up and to the side)
        for(int i = 0; i < body.P.Length; i++)
            if(body.P[i].Y > body.Ground + 0.5f)
                body.Prev[i] -= push * ((body.P[i].Y - body.Ground) / 1.5f);

        // How each bone follows its points (parents first).
        void Drive(string bone, int point, int toward, int acrossA = -1, int acrossB = -1)
        {
            int b = info.IndexOf(bone);
            if(b < 0 || point < 0 || toward < 0)
                return;
            var d = new Driven
            {
                Bone = b, Point = point, Toward = toward, AcrossA = acrossA, AcrossB = acrossB,
                StartRotation = sk.GetModelFrame(b).Rotation,
                StartDir = body.P[toward] - body.P[point],
            };
            if(acrossA >= 0 && acrossB >= 0)
                d.StartAcross = body.P[acrossA] - body.P[acrossB];
            body.Bones.Add(d);
        }
        Drive("j_kosi", hips, spineA >= 0 ? spineA : chest, side["thighl"], side["thighr"]);
        if(spineA >= 0)
            Drive("j_sebo_a", spineA, spineB >= 0 ? spineB : chest, side["claviclel"], side["clavicler"]);
        if(spineB >= 0)
            Drive("j_sebo_b", spineB, chest, side["claviclel"], side["clavicler"]);
        Drive("j_sebo_c", chest, neck >= 0 ? neck : head, side["claviclel"], side["clavicler"]);
        if(neck >= 0)
            Drive("j_kubi", neck, head, side["claviclel"], side["clavicler"]);
        Drive("j_kao", head, headTop, side["claviclel"], side["clavicler"]);
        foreach(var s in new[] { "l", "r" })
        {
            Drive($"j_sako_{s}", side[$"clavicle{s}"], side[$"upper{s}"]);
            Drive($"j_ude_a_{s}", side[$"upper{s}"], side[$"lower{s}"]);
            Drive($"j_ude_b_{s}", side[$"lower{s}"], side[$"hand{s}"]);
            Drive($"j_te_{s}", side[$"hand{s}"], side[$"handEnd{s}"]);
            Drive($"j_asi_a_{s}", side[$"thigh{s}"], side[$"knee{s}"]);
            Drive($"j_asi_b_{s}", side[$"knee{s}"], side[$"shin{s}"]);
            Drive($"j_asi_c_{s}", side[$"shin{s}"], side[$"foot{s}"]);
            Drive($"j_asi_d_{s}", side[$"foot{s}"], side[$"toe{s}"]);
        }
        body.Bones.Sort((x, y) => info.Depths[x.Bone].CompareTo(info.Depths[y.Bone]));
        return body;
    }

    // ---- The physics: points falling, links holding them together, the floor

    /// <summary>How much a part bounces back off the floor (0 none, 1 all of its speed).</summary>
    public static float Bounce = 0.5f;
    /// <summary>How far parts slide on the floor (0 they stop dead, 1 like ice).</summary>
    public static float Slide = 0.9f;

    private readonly System.Collections.Concurrent.ConcurrentQueue<(nint, Vector3)> _pushes = new();

    /// <summary>Shoves a ragdolling character (a random way, mostly back and up) (UI thread).</summary>
    public void Push(nint address)
    {
        var r = Random.Shared;
        float a = (float)(r.NextDouble() * Math.PI * 2);
        _pushes.Enqueue((address, new Vector3(MathF.Sin(a) * 2.5f, 2.5f + (float)r.NextDouble() * 1.5f, MathF.Cos(a) * 2.5f)));
    }

    private void Step(Body body, nint address)
    {
        long now = Stopwatch.GetTimestamp();
        float dt = Math.Clamp((float)(now - body.Last) / Stopwatch.Frequency, 1f / 240f, 1f / 30f);
        body.Last = now;
        var gravity = new Vector3(0f, -9.8f, 0f);
        const float Damping = 0.999f; // (hardly any air drag)
        var p = body.P;
        var prev = body.Prev;
        // shoves (a speed in metres a second, given to every point)
        int n = _pushes.Count;
        for(int k = 0; k < n && _pushes.TryDequeue(out var push); k++)
        {
            if(push.Item1 != address)
            {
                if(IsOn(push.Item1))
                    _pushes.Enqueue(push); // (someone else's: theirs next)
                continue;
            }
            for(int i = 0; i < p.Length; i++)
                prev[i] -= push.Item2 * dt * (0.7f + 0.6f * (i % 3) / 2f); // (a little uneven, so it tumbles)
        }
        var before = new Vector3[p.Length];
        for(int i = 0; i < p.Length; i++)
        {
            var velocity = (p[i] - prev[i]) * Damping;
            before[i] = velocity;
            prev[i] = p[i];
            p[i] += velocity + gravity * (dt * dt);
        }
        for(int iteration = 0; iteration < 12; iteration++)
        {
            foreach(var (a, b, _, min, max) in body.Links)
            {
                var d = p[b] - p[a];
                float length = d.Length();
                if(length < 1e-6f)
                    continue;
                float target = Math.Clamp(length, min, max);
                if(MathF.Abs(length - target) < 1e-6f)
                    continue;
                var fix = d * ((length - target) / length * 0.5f);
                p[a] += fix;
                p[b] -= fix;
            }
            // nothing goes through the floor
            for(int i = 0; i < p.Length; i++)
            {
                float floor = body.Ground + body.Radius[i];
                if(p[i].Y < floor)
                    p[i].Y = floor;
            }
        }
        // What touches the floor bounces back up and slides on along it.
        float bounce = Math.Clamp(Bounce, 0f, 0.95f), slide = Math.Clamp(Slide, 0f, 0.995f);
        for(int i = 0; i < p.Length; i++)
        {
            float floor = body.Ground + body.Radius[i];
            if(p[i].Y > floor + 0.002f)
                continue;
            var v = p[i] - prev[i];
            // coming down: back up by the bounce (a slow landing just settles)
            v.Y = before[i].Y < -0.004f ? -before[i].Y * bounce : MathF.Max(v.Y, 0f);
            v.X *= slide;
            v.Z *= slide;
            prev[i] = p[i] - v;
        }
    }

    // ---- The skeleton set from the points

    private static Quaternion FromTo(Vector3 a, Vector3 b)
    {
        a = Vector3.Normalize(a);
        b = Vector3.Normalize(b);
        float dot = Vector3.Dot(a, b);
        if(dot > 0.99999f)
            return Quaternion.Identity;
        if(dot < -0.99999f)
        {
            var axis = Vector3.Cross(Vector3.UnitX, a);
            if(axis.LengthSquared() < 1e-6f)
                axis = Vector3.Cross(Vector3.UnitY, a);
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.PI);
        }
        var c = Vector3.Cross(a, b);
        return Quaternion.Normalize(new Quaternion(c, 1f + dot));
    }

    private static Quaternion Basis(Vector3 up, Vector3 across)
    {
        up = Vector3.Normalize(up);
        var x = across - up * Vector3.Dot(across, up);
        x = x.LengthSquared() < 1e-8f ? Vector3.UnitX : Vector3.Normalize(x);
        var z = Vector3.Cross(x, up);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(new Matrix4x4(
            x.X, x.Y, x.Z, 0f, up.X, up.Y, up.Z, 0f, z.X, z.Y, z.Z, 0f, 0f, 0f, 0f, 1f)));
    }

    private void Pose(in ActorSkeleton sk, Body body)
    {
        var p = body.P;
        foreach(var d in body.Bones)
        {
            var dir = p[d.Toward] - p[d.Point];
            if(dir.LengthSquared() < 1e-10f)
                continue;
            Quaternion turn;
            if(d.AcrossA >= 0 && d.AcrossB >= 0)
            {
                var across = p[d.AcrossA] - p[d.AcrossB];
                turn = Basis(dir, across) * Quaternion.Inverse(Basis(d.StartDir, d.StartAcross));
            }
            else
                turn = FromTo(d.StartDir, dir);
            sk.SetModelRotation(d.Bone, Quaternion.Normalize(turn * d.StartRotation));
            sk.SetModelPosition(d.Bone, p[d.Point]);
        }
        sk.RealignPartials(_cache);
        sk.RefreshAttachedModels();
    }
}
