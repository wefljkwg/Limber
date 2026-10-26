using System;
using Limber.BodyRig;
using System.Collections.Generic;
using System.Numerics;
using Limber.Core;

namespace Limber.Gpu;

public static unsafe partial class LiveSoftBody
{
    /// <summary>
    /// The breasts as one real soft body each (position based, shape matching): particles on the skin and through
    /// the inside, held to their shape in overlapping clusters (firmness, and the volume stays), the rim and the chest
    /// wall held to the body, colliders as hard limits (each particle out on the side it was on, so nothing slips
    /// through a finger), and the two breasts pushing on each other. A hand dents the skin and moves the whole breast
    /// in the same step; the flesh bulges around it; it springs back and wobbles on its own.
    /// Every breast vertex follows the particles around it; a last pass presses in the exact shape of what touches.
    /// </summary>
    private sealed class Physics
    {
        // Model-space metres. Worked out offline on a real body (see the notes in the plugin's history).
        private const float ShellSpacing = 0.016f, InnerSpacing = 0.020f, Spacing = 0.018f, ClusterReach = 1.7f;
        private const int EmbedCount = 8;

        /// <summary>The settings one step uses (a copy, so the UI can change them while a step runs).</summary>
        public readonly record struct Tuning(float Strength, float Firm, float Home, float Damp, float ContactKeep, float BreastPush,
            int Iterations, float Steadiness, float ImprintDepth, float ClingRange)
        {
            public static Tuning From(LiveMeshSettings s) => new(
                Math.Clamp(s.Strength, 0f, 1f),
                Lerp(0.25f, 0.85f, Math.Clamp(s.Firmness, 0f, 1f)),
                Lerp(0.002f, 0.1f, Math.Clamp(s.PullBack, 0f, 1f)),
                Lerp(0.95f, 0.995f, Math.Clamp(s.Bounce, 0f, 1f)),
                1f - Math.Clamp(s.Friction, 0f, 1f),
                Math.Clamp(s.BreastPush, 0f, 1f),
                Math.Clamp(s.Accuracy, 2, 16),
                Math.Clamp(s.Steadiness, 0f, 0.9f),
                Math.Clamp(s.ImprintDepth, 0f, 10f) * 0.01f,
                Math.Clamp(s.ClingRange, 0.2f, 15f) * 0.01f);
        }
        // A particle touching nothing moves at most this far a frame (one coming free of a finger eases back).
        private const float MaxMove = 0.006f;
        // The fine press-in on top of the particles (the exact shape of what touches): at most this deep, deeper by
        // at most PressRate a frame, easing back out over PressEase seconds.
        private const float PressRate = 0.008f, PressEase = 0.06f;

        private readonly int _n;
        private readonly Vector3[] _rest;
        private readonly int[][] _bone;
        private readonly float[][] _weight;
        private readonly float[] _pin;
        private readonly byte[] _side;
        private readonly float[] _radius;
        private readonly int[] _clusterStart, _cluster;
        private readonly Quaternion[] _clusterTurn;
        private readonly Vector3[] _x, _v, _target, _before, _acc;
        private readonly int[] _count;
        private readonly bool[] _touch;
        // Per mesh, per surface point: how far the fine press-in has it pushed in (eased).
        public readonly float[][] Depth;
        private readonly Vector3[][] _shown; // per mesh, per surface point: the steadied offset
        private readonly Dictionary<Guid, Vector3[]> _side0 = new(); // per collider: the side each particle was on
        private readonly List<(int A, int B)> _pairs = new();
        private bool _started;

        // Per mesh, per surface point: the particles it follows and how much.
        public readonly (int[] P, float[] W)[][] Embed;
        public int Count => _n;
        public float LastMilliseconds;

        private Physics(List<Particle> particles, List<int[]> clusters, (int[] P, float[] W)[][] embed)
        {
            _n = particles.Count;
            _rest = new Vector3[_n];
            _bone = new int[_n][];
            _weight = new float[_n][];
            _pin = new float[_n];
            _side = new byte[_n];
            _radius = new float[_n];
            for (int i = 0; i < _n; i++)
            {
                var p = particles[i];
                _rest[i] = p.Rest;
                _bone[i] = p.Bone;
                _weight[i] = p.Weight;
                _pin[i] = p.Pin;
                _side[i] = (byte)p.Side;
                _radius[i] = Spacing * 0.5f;
            }
            _clusterStart = new int[_n + 1];
            var flat = new List<int>();
            for (int i = 0; i < _n; i++)
            {
                _clusterStart[i] = flat.Count;
                flat.AddRange(clusters[i]);
            }
            _clusterStart[_n] = flat.Count;
            _cluster = flat.ToArray();
            _clusterTurn = new Quaternion[_n];
            Array.Fill(_clusterTurn, Quaternion.Identity);
            _x = new Vector3[_n];
            _v = new Vector3[_n];
            _target = new Vector3[_n];
            _before = new Vector3[_n];
            _acc = new Vector3[_n];
            _count = new int[_n];
            _touch = new bool[_n];
            Embed = embed;
            Depth = new float[embed.Length][];
            _shown = new Vector3[embed.Length][];
            for (int m = 0; m < embed.Length; m++)
            {
                Depth[m] = new float[embed[m].Length];
                _shown[m] = new Vector3[embed[m].Length];
            }
        }

        private sealed class Particle
        {
            public Vector3 Rest;
            public int[] Bone = [];
            public float[] Weight = [];
            public float Pin;
            public int Side;
        }

        // ---- Building (once, when the live mesh starts)

        public static Physics? Build(List<MeshState> meshes)
        {
            var particles = new List<Particle>();
            for (int side = 0; side < Sides; side++)
                BuildSide(meshes, side, particles);
            if (particles.Count < 40)
                return null;

            // Clusters: each particle and its neighbours on the same breast (at rest).
            var clusters = new List<int[]>(particles.Count);
            float reach2 = Spacing * ClusterReach * Spacing * ClusterReach;
            for (int i = 0; i < particles.Count; i++)
            {
                var list = new List<int>();
                for (int j = 0; j < particles.Count; j++)
                    if (particles[j].Side == particles[i].Side && Vector3.DistanceSquared(particles[i].Rest, particles[j].Rest) <= reach2)
                        list.Add(j);
                clusters.Add(list.ToArray());
            }

            // Every surface point follows the particles nearest it (same breast).
            var embed = new (int[] P, float[] W)[meshes.Count][];
            for (int m = 0; m < meshes.Count; m++)
            {
                var groups = meshes[m].Groups;
                embed[m] = new (int[], float[])[groups.Length];
                var best = new (float D, int P)[EmbedCount];
                for (int g = 0; g < groups.Length; g++)
                {
                    int found = 0;
                    for (int p = 0; p < particles.Count; p++)
                    {
                        if (particles[p].Side != groups[g].Side)
                            continue;
                        float d = Vector3.DistanceSquared(groups[g].Rest, particles[p].Rest);
                        if (found < EmbedCount)
                        {
                            best[found++] = (d, p);
                            continue;
                        }
                        int worst = 0;
                        for (int k = 1; k < EmbedCount; k++)
                            if (best[k].D > best[worst].D)
                                worst = k;
                        if (d < best[worst].D)
                            best[worst] = (d, p);
                    }
                    var ids = new int[found];
                    var ws = new float[found];
                    float total = 0f;
                    for (int k = 0; k < found; k++)
                    {
                        ids[k] = best[k].P;
                        float d = MathF.Sqrt(best[k].D) / Spacing;
                        ws[k] = MathF.Exp(-d * d);
                        total += ws[k];
                    }
                    if (total <= 1e-12f)
                    {
                        ids = [];
                        ws = [];
                    }
                    else
                        for (int k = 0; k < found; k++)
                            ws[k] /= total;
                    embed[m][g] = (ids, ws);
                }
            }
            return new Physics(particles, clusters, embed);
        }

        private static void BuildSide(List<MeshState> meshes, int side, List<Particle> particles)
        {
            // The breast's skin points (most of their weight on the breast).
            var skin = new List<Group>();
            foreach (var mesh in meshes)
                foreach (var g in mesh.Groups)
                    if (g.Side == side && g.Mask > 0.15f)
                        skin.Add(g);
            if (skin.Count < 20)
                return;

            // The skin: one particle per ShellSpacing box. Held to the body at the rim, free over the breast.
            var taken = new HashSet<(int, int, int)>();
            foreach (var g in skin)
            {
                var key = ((int)MathF.Floor(g.Rest.X / ShellSpacing), (int)MathF.Floor(g.Rest.Y / ShellSpacing), (int)MathF.Floor(g.Rest.Z / ShellSpacing));
                if (!taken.Add(key))
                    continue;
                particles.Add(new Particle { Rest = g.Rest, Bone = g.Bone, Weight = g.Weight, Pin = Math.Clamp(1f - (g.Mask - 0.15f) / 0.45f, 0f, 1f), Side = side });
            }

            // The base (where the breast meets the chest): a plane through the rim, facing out along the breast.
            var rim = skin.FindAll(g => g.Mask < 0.35f);
            if (rim.Count < 3)
                return;
            var centre = Vector3.Zero;
            foreach (var g in rim)
                centre += g.Rest;
            centre /= rim.Count;
            var normal = SmallestAxis(rim, centre);
            var middle = Vector3.Zero;
            foreach (var g in skin)
                middle += g.Rest;
            middle /= skin.Count;
            if (Vector3.Dot(middle - centre, normal) < 0f)
                normal = -normal;
            var u = Vector3.Normalize(Vector3.Cross(normal, MathF.Abs(normal.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
            var v = Vector3.Cross(normal, u);
            float minU = float.MaxValue, maxU = float.MinValue, minV = float.MaxValue, maxV = float.MinValue, maxH = 0f;
            foreach (var g in skin)
            {
                var rel = g.Rest - centre;
                float a = Vector3.Dot(rel, u), b = Vector3.Dot(rel, v), h = Vector3.Dot(rel, normal);
                minU = MathF.Min(minU, a); maxU = MathF.Max(maxU, a);
                minV = MathF.Min(minV, b); maxV = MathF.Max(maxV, b);
                maxH = MathF.Max(maxH, h);
            }

            // The breast's own surface, for the inside test.
            var triangles = new List<(Vector3, Vector3, Vector3)>();
            foreach (var mesh in meshes)
            {
                var t = mesh.Triangles;
                for (int i = 0; i + 2 < t.Length; i += 3)
                {
                    var ga = mesh.Groups[t[i]];
                    var gb = mesh.Groups[t[i + 1]];
                    var gc = mesh.Groups[t[i + 2]];
                    if (ga.Side == side && gb.Side == side && gc.Side == side)
                        triangles.Add((ga.Rest, gb.Rest, gc.Rest));
                }
            }

            // The inside: a grid on the base, kept where a ray out along the breast crosses its skin an odd number of
            // times; the layer on the chest wall is held to the body.
            for (float a = minU + InnerSpacing * 0.5f; a < maxU; a += InnerSpacing)
            {
                for (float b = minV + InnerSpacing * 0.5f; b < maxV; b += InnerSpacing)
                {
                    for (float h = InnerSpacing * 0.5f; h < maxH; h += InnerSpacing)
                    {
                        var p = centre + u * a + v * b + normal * h;
                        Group? nearest = null;
                        float best = float.MaxValue;
                        foreach (var g in skin)
                        {
                            float d = Vector3.DistanceSquared(p, g.Rest);
                            if (d < best)
                            {
                                best = d;
                                nearest = g;
                            }
                        }
                        if (nearest == null || MathF.Sqrt(best) < ShellSpacing * 0.6f || nearest.Mask < 0.3f)
                            continue;
                        if ((Crossings(p, normal, triangles) & 1) == 0)
                            continue;
                        particles.Add(new Particle { Rest = p, Bone = nearest.Bone, Weight = nearest.Weight, Pin = h < InnerSpacing * 1.2f ? 1f : 0f, Side = side });
                    }
                }
            }
        }

        private static int Crossings(Vector3 origin, Vector3 dir, List<(Vector3 A, Vector3 B, Vector3 C)> triangles)
        {
            int hits = 0;
            foreach (var (a, b, c) in triangles)
            {
                var e1 = b - a;
                var e2 = c - a;
                var p = Vector3.Cross(dir, e2);
                float det = Vector3.Dot(e1, p);
                if (MathF.Abs(det) < 1e-12f)
                    continue;
                float inv = 1f / det;
                var s = origin - a;
                float uu = Vector3.Dot(s, p) * inv;
                if (uu < 0f || uu > 1f)
                    continue;
                var q = Vector3.Cross(s, e1);
                float vv = Vector3.Dot(dir, q) * inv;
                if (vv < 0f || uu + vv > 1f)
                    continue;
                if (Vector3.Dot(e2, q) * inv > 1e-6f)
                    hits++;
            }
            return hits;
        }

        // The direction the points spread least along (the normal of the plane through them).
        private static Vector3 SmallestAxis(List<Group> points, Vector3 centre)
        {
            float xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
            foreach (var g in points)
            {
                var d = g.Rest - centre;
                xx += d.X * d.X; xy += d.X * d.Y; xz += d.X * d.Z;
                yy += d.Y * d.Y; yz += d.Y * d.Z; zz += d.Z * d.Z;
            }
            // Inverse iteration on (C + small) finds the smallest axis: solve C x = previous, a few times.
            var c = new Matrix4x4(xx, xy, xz, 0, xy, yy, yz, 0, xz, yz, zz, 0, 0, 0, 0, 1);
            float trace = xx + yy + zz;
            c.M11 += trace * 1e-4f; c.M22 += trace * 1e-4f; c.M33 += trace * 1e-4f;
            if (!Matrix4x4.Invert(c, out var inv))
                return Vector3.UnitZ;
            var x = Vector3.Normalize(new Vector3(0.3f, 0.2f, 0.9f));
            for (int i = 0; i < 30; i++)
            {
                var y = Vector3.TransformNormal(x, inv);
                if (y.LengthSquared() < 1e-30f)
                    break;
                x = Vector3.Normalize(y);
            }
            return x;
        }

        // ---- Every frame (worker thread)

        public void Reset()
        {
            _started = false;
            foreach (var d in Depth)
                Array.Clear(d);
            foreach (var d in _shown)
                Array.Clear(d);
        }

        /// <summary>Frame-to-frame jitter smoothed out of a surface point's offset (0 = none).</summary>
        public Vector3 Steady(int mesh, int group, Vector3 offset, float steadiness)
        {
            ref var shown = ref _shown[mesh][group];
            shown = steadiness > 0f ? Vector3.Lerp(offset, shown, steadiness) : offset;
            if (!MathUtil.IsFinite(shown))
                shown = offset;
            return shown;
        }

        /// <summary>The fine press-in at one surface point: <paramref name="need"/> is how far in (along the skin's
        /// facing) it would take to clear what touches it; the result is how far in it's shown.</summary>
        public float Press(int mesh, int group, float need, float dt, float max)
        {
            ref float depth = ref Depth[mesh][group];
            need = Math.Clamp(need, 0f, max);
            float grow = MathF.Min(need, depth + PressRate);
            depth = MathF.Max(grow, depth * MathF.Exp(-dt / PressEase));
            if (!float.IsFinite(depth))
                depth = 0f;
            return depth;
        }

        /// <summary>One step: from the skinning matrices (rest model space to posed) and the colliders (model space).</summary>
        public void Step(Matrix4x4[] m, Shape[] colliders, Guid[] ids, float[] own, float[] cling, float[] share, float dt, Tuning tuning)
        {
            float strength = tuning.Strength;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < _n; i++)
            {
                var q = Vector3.Zero;
                float total = 0f;
                var bones = _bone[i];
                var weights = _weight[i];
                for (int b = 0; b < bones.Length; b++)
                {
                    q += Vector3.Transform(_rest[i], m[bones[b]]) * weights[b];
                    total += weights[b];
                }
                _target[i] = total > 0f ? q / total : _rest[i];
            }
            bool reset = !_started;
            if (!reset)
                for (int i = 0; i < _n; i++)
                    if (!MathUtil.IsFinite(_x[i]) || Vector3.DistanceSquared(_x[i], _target[i]) > 0.15f * 0.15f)
                    {
                        reset = true;
                        break;
                    }
            if (reset)
            {
                Array.Copy(_target, _x, _n);
                Array.Clear(_v);
                _side0.Clear();
                _started = true;
            }

            float firm = tuning.Firm, home = tuning.Home, damp = tuning.Damp;
            int iterations = tuning.Iterations;

            Array.Copy(_x, _before, _n);
            Array.Clear(_touch);
            for (int i = 0; i < _n; i++)
                _x[i] += _v[i] * dt;
            FindPairs();

            for (int iteration = 0; iteration < iterations; iteration++)
            {
                ShapeMatch(firm);
                for (int i = 0; i < _n; i++)
                {
                    float hold = _pin[i] + (1f - _pin[i]) * home;
                    _x[i] += (_target[i] - _x[i]) * hold;
                }
                if (strength > 0f)
                    Collide(colliders, ids, own, cling, share, strength, tuning.ClingRange, remember: iteration == iterations - 1);
                Breasts(tuning.BreastPush);
            }

            float inv = 1f / MathF.Max(dt, 1e-4f);
            for (int i = 0; i < _n; i++)
            {
                if (!_touch[i])
                {
                    var move = _x[i] - _before[i];
                    float len = move.Length();
                    if (len > MaxMove)
                        _x[i] = _before[i] + move * (MaxMove / len);
                }
                // Friction: what's touching something doesn't keep sliding or bouncing off it.
                _v[i] = (_x[i] - _before[i]) * (inv * damp * (_touch[i] ? tuning.ContactKeep : 1f));
                if (!MathUtil.IsFinite(_v[i]))
                {
                    _v[i] = Vector3.Zero;
                    _x[i] = _target[i];
                }
            }
            LastMilliseconds = (float)clock.Elapsed.TotalMilliseconds;
        }

        /// <summary>How far a surface point is moved off where the body puts it (posed model space).</summary>
        public Vector3 Offset(int mesh, int group)
        {
            var (p, w) = Embed[mesh][group];
            var d = Vector3.Zero;
            for (int k = 0; k < p.Length; k++)
                d += (_x[p[k]] - _target[p[k]]) * w[k];
            return d;
        }

        // Each cluster: the best rotation of its rest shape (as the body puts it) onto where its particles are, and
        // each particle pulled toward where that puts it (averaged over the clusters it's in).
        private void ShapeMatch(float firm)
        {
            Array.Clear(_acc);
            Array.Clear(_count);
            for (int c = 0; c < _n; c++)
            {
                int start = _clusterStart[c], end = _clusterStart[c + 1];
                int k = end - start;
                if (k < 2)
                    continue;
                var cx = Vector3.Zero;
                var ct = Vector3.Zero;
                for (int j = start; j < end; j++)
                {
                    cx += _x[_cluster[j]];
                    ct += _target[_cluster[j]];
                }
                cx /= k;
                ct /= k;
                // A = sum (x - cx)(t - ct)^T, as its three columns.
                Vector3 a0 = Vector3.Zero, a1 = Vector3.Zero, a2 = Vector3.Zero;
                for (int j = start; j < end; j++)
                {
                    int p = _cluster[j];
                    var dx = _x[p] - cx;
                    var dt = _target[p] - ct;
                    a0 += dx * dt.X;
                    a1 += dx * dt.Y;
                    a2 += dx * dt.Z;
                }
                var q = ExtractRotation(a0, a1, a2, _clusterTurn[c]);
                _clusterTurn[c] = q;
                for (int j = start; j < end; j++)
                {
                    int p = _cluster[j];
                    _acc[p] += cx + Vector3.Transform(_target[p] - ct, q);
                    _count[p]++;
                }
            }
            for (int i = 0; i < _n; i++)
                if (_count[i] > 0)
                    _x[i] += (_acc[i] / _count[i] - _x[i]) * firm;
        }

        // Rotational part of A (columns a0, a1, a2), warm-started from last frame's (Müller et al. 2016).
        private static Quaternion ExtractRotation(Vector3 a0, Vector3 a1, Vector3 a2, Quaternion q)
        {
            for (int i = 0; i < 6; i++)
            {
                var r0 = Vector3.Transform(Vector3.UnitX, q);
                var r1 = Vector3.Transform(Vector3.UnitY, q);
                var r2 = Vector3.Transform(Vector3.UnitZ, q);
                var omega = Vector3.Cross(r0, a0) + Vector3.Cross(r1, a1) + Vector3.Cross(r2, a2);
                float denom = MathF.Abs(Vector3.Dot(r0, a0) + Vector3.Dot(r1, a1) + Vector3.Dot(r2, a2)) + 1e-9f;
                omega /= denom;
                float w = omega.Length();
                if (w < 1e-9f || !float.IsFinite(w))
                    break;
                q = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(omega / w, w) * q);
            }
            return MathUtil.IsFinite(q) ? q : Quaternion.Identity;
        }

        // Colliders are solid: a particle inside goes out on the side it was on before (or came from, if it went
        // right across a thin one in a frame). Strength below 1 lets the collider sink in that much.
        private void Collide(Shape[] colliders, Guid[] ids, float[] own, float[] cling, float[] share, float strength, float clingRange, bool remember)
        {
            float margin = Skin * 0.5f;
            for (int k = 0; k < colliders.Length; k++)
            {
                var shape = colliders[k];
                float r = shape.R;
                float s = strength * (k < own.Length ? own[k] : 1f);
                if (s <= 0f)
                    continue;
                float sticky = k < cling.Length ? Math.Clamp(cling[k], 0f, 1f) : 0f;
                float size = k < share.Length ? Math.Clamp(share[k], 0f, 1f) : 1f;
                if (!_side0.TryGetValue(ids[k], out var memory))
                    _side0[ids[k]] = memory = new Vector3[_n];
                for (int i = 0; i < _n; i++)
                {
                    var x = _x[i];
                    float t = shape.Along(x);
                    float d = shape.Gap(x, out var c, out var normal);
                    float reach = (shape.Box ? r : r * s) + _radius[i] * size + margin;
                    var was = memory[i];
                    bool have = was.LengthSquared() > 0.25f;
                    // Right across it since last frame: along its side only (round its ends it's just sliding).
                    bool crossed = have && d >= reach && t > 0f && t < 1f && Vector3.Dot(normal, was) < -0.2f && d < reach * 2f + 0.02f;
                    if (d < reach || crossed)
                    {
                        // No side yet (it arrived inside, e.g. placed between the breasts): out toward its own side,
                        // where the body puts it, never through to the other breast.
                        var way = have ? was : OwnWay(_target[i], shape, normal);
                        _x[i] = shape.Box ? x + way * shape.Exit(x, way, reach) : c + way * reach;
                        _touch[i] = true;
                        if (remember)
                            memory[i] = way;
                    }
                    else
                    {
                        // Skin cling: skin near the collider (on its own side) is drawn onto its surface, so it wraps
                        // round it (something held between the breasts), fading out over the cling range.
                        if (sticky > 0f && d < reach + clingRange && (!have || Vector3.Dot(normal, was) > 0f))
                        {
                            float fade = 1f - (d - reach) / clingRange;
                            _x[i] += (c + normal * reach - x) * (sticky * 0.35f * fade);
                            _touch[i] = true;
                        }
                        if (remember)
                            memory[i] = d < reach * 3f + 0.03f ? normal : Vector3.Zero;
                    }
                }
            }
        }

        private static Vector3 OwnWay(Vector3 home, in Shape shape, Vector3 normal)
        {
            shape.Gap(home, out _, out var fromAxis);
            if (fromAxis.LengthSquared() > 0.5f)
                return fromAxis;
            return normal.LengthSquared() > 0.5f ? normal : Vector3.UnitY;
        }

        // Particle pairs (one on each breast) close enough to touch this frame (a coarse grid, once per frame).
        private void FindPairs()
        {
            _pairs.Clear();
            var grid = new Dictionary<(int, int, int), List<int>>();
            // (each pair's right side: 1 for the breasts, 3 for the butt)
            for (int i = 0; i < _n; i++)
            {
                if ((_side[i] & 1) != 1)
                    continue;
                var key = Cell(_x[i]);
                if (!grid.TryGetValue(key, out var list))
                    grid[key] = list = new List<int>();
                list.Add(i);
            }
            if (grid.Count == 0)
                return;
            float reach2 = Spacing * 1.5f * Spacing * 1.5f;
            for (int i = 0; i < _n; i++)
            {
                if ((_side[i] & 1) != 0)
                    continue;
                var (cx, cy, cz) = Cell(_x[i]);
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dz = -1; dz <= 1; dz++)
                            if (grid.TryGetValue((cx + dx, cy + dy, cz + dz), out var list))
                                foreach (int j in list)
                                    if (_side[j] == _side[i] + 1 && Vector3.DistanceSquared(_x[i], _x[j]) < reach2)
                                        _pairs.Add((i, j));
            }
        }

        private static (int, int, int) Cell(Vector3 p)
            => ((int)MathF.Floor(p.X / Spacing), (int)MathF.Floor(p.Y / Spacing), (int)MathF.Floor(p.Z / Spacing));

        // The two breasts push each other apart where they meet (never further apart than the body has them).
        private void Breasts(float push)
        {
            if (push <= 0f)
                return;
            foreach (var (i, j) in _pairs)
            {
                var d = _x[i] - _x[j];
                float len = d.Length();
                float gap = MathF.Min(Spacing, Vector3.Distance(_target[i], _target[j]));
                if (len < 1e-9f || len >= gap)
                    continue;
                var corr = d * ((gap - len) * 0.5f * push / len);
                _x[i] += corr * (1f - _pin[i]);
                _x[j] -= corr * (1f - _pin[j]);
            }
        }
    }

    // The exact shape of what touches, pressed into the skin (finer than the particles): each point still inside a
    // collider goes straight in against the skin's facing until it's clear of all of them (at most 10 cm).
    private static Vector3 PressIn(Vector3 at, Vector3 facing, Shape[] colliders, float[] own, float strength)
    {
        float margin = Skin * 0.25f;
        bool Inside(Vector3 y)
        {
            for (int k = 0; k < colliders.Length; k++)
            {
                var shape = colliders[k];
                float s = strength * (k < own.Length ? own[k] : 1f);
                if (s > 0f && shape.Gap(y) < (shape.Box ? shape.R : shape.R * s) + margin)
                    return true;
            }
            return false;
        }
        if (!Inside(at))
            return at;
        var way = -facing;
        const float Step = 0.004f;
        float hi = -1f;
        for (int k = 1; k <= 25; k++)
        {
            if (!Inside(at + way * (Step * k)))
            {
                hi = Step * k;
                break;
            }
        }
        if (hi < 0f)
            return at;
        float lo = hi - Step;
        for (int i = 0; i < 10; i++)
        {
            float mid = (lo + hi) * 0.5f;
            if (Inside(at + way * mid))
                lo = mid;
            else
                hi = mid;
        }
        return at + way * hi;
    }
}
