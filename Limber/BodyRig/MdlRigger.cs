using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;

namespace Limber.BodyRig;

/// <summary>
/// Weights an FFXIV model (.mdl, v5 / v6) onto a rig part's lattice bones, like limber_breast_weights.py does in
/// Blender, without leaving the game: every vertex of the part is weighted to the corners of the lattice cell it's in.
/// Only the bone list, the bone tables and the vertices' bone weights / indices change; geometry, materials, shapes and
/// everything else are copied byte for byte.
/// </summary>
public static class MdlRigger
{
    private const uint V5 = 0x01000005;
    private const int FileHeaderSize = 0x44;
    private const float Margin = 0.75f;

    /// <summary>One side of one part: its lattice bones (18, in Limber's order) where they are in the model's space.</summary>
    public sealed record Side(string[] Bones, Vector3[] Positions, string Parent, string[] Sources, bool Merge, string OldPrefix)
    {
        /// <summary>Which part (both sides share it) and which side.</summary>
        public string PartKey { get; init; } = "";
        public bool Left { get; init; } = true;
        /// <summary>How far the part's surface is pulled onto a smooth ellipsoid around it (0..1), baked into the model.</summary>
        public float Roundness { get; init; }
        /// <summary>How much the crease between the two sides is filled out (0..1), baked into the model.</summary>
        public float Fill { get; init; }
        /// <summary>A nipple bone (null = none): what's within <see cref="NippleRadius"/> of it goes on it, fading out.</summary>
        public string? Nipple { get; init; }
        public Vector3 NipplePosition { get; init; }
        public float NippleRadius { get; init; }
        /// <summary>The box's size in bones: rows (top to bottom), columns (inner to outer), and layer steps (back
        /// to front: Steps + 1 layers when the back face is bones too).</summary>
        public int Rows { get; init; } = 3;
        public int Columns { get; init; } = 3;
        public int Steps { get; init; } = 2;
        /// <summary>Lattice bones its mesh needs room for; when there isn't, this side is left out (not the model).</summary>
        public bool Optional { get; init; }
    }

    /// <summary>Passes of evening out how the lattice's share fades into the body (see Smooth).</summary>
    private const int SmoothPasses = 10;

    private sealed class Element
    {
        public byte Stream, Offset, Type, Usage;
    }

    private sealed class Mesh
    {
        public int VertexCount, BoneTable;
        public uint IndexCount, StartIndex;
        public uint[] BufferOffset = new uint[3];
        public byte[] Stride = new byte[3];
    }

    private sealed class Parsed
    {
        public uint Version, StackSize, RuntimeSize;
        public int LodCount;
        public uint[] VertexOffset = new uint[3];
        public uint[] IndexOffset = new uint[3];
        public List<List<Element>> Declarations = new();
        public int StringsPos, StringCount, StringsUsed;
        public ushort StringPad;
        public Dictionary<uint, string> Strings = new();
        public int ModelHeaderPos, LodPos, BoneNamesPos, BoneTablesEnd, PaddingPos, BoxesPos, BoneBoxesPos, DataStart;
        public int BoneCount, NeckMorphs;
        public (int MeshIndex, int MeshCount)[] Lods = new (int, int)[3];
        public List<Mesh> Meshes = new();
        public List<string> Bones = new();
        public List<List<ushort>> Tables = new();
    }

    /// <summary>The model with the parts weighted onto their lattices, or null if nothing in it belongs to them.</summary>
    public static byte[]? Rig(byte[] file, IReadOnlyList<Side> sides, List<string> log)
    {
        var m = Parse(file);
        if (m.NeckMorphs > 0)
        {
            log.Add("has neck morphs (a head model?), skipped");
            return null;
        }

        var bones = new List<string>(m.Bones);
        var added = new List<string>();
        var tables = m.Tables.Select(t => new List<ushort>(t)).ToList();
        var data = file.AsSpan(m.DataStart).ToArray();
        int cap = m.Version == V5 ? 64 : 128;
        int total = 0;

        int BoneId(string name)
        {
            int i = bones.IndexOf(name);
            if (i >= 0)
                return i;
            bones.Add(name);
            added.Add(name);
            return bones.Count - 1;
        }

        // Rounder, smoother parts (and a filled crease), baked into the geometry before weighting.
        foreach (var pair in sides.Where(s => s.Roundness > 0f || s.Fill > 0f).GroupBy(s => s.PartKey))
        {
            try
            {
                total += Round(m, data, pair.ToList(), log);
            }
            catch (Exception e)
            {
                log.Add($"couldn't round {pair.Key} ({e.Message})");
            }
        }

        for (int lod = 0; lod < m.LodCount; lod++)
        {
            var (first, count) = m.Lods[lod];
            for (int mi = first; mi < first + count && mi < m.Meshes.Count; mi++)
            {
                var mesh = m.Meshes[mi];
                if (mi >= m.Declarations.Count)
                    continue;
                var decl = m.Declarations[mi];
                var pe = decl.FirstOrDefault(e => e.Usage == 0);
                var we = decl.FirstOrDefault(e => e.Usage == 1);
                var ie = decl.FirstOrDefault(e => e.Usage == 2);
                if (pe == null || we == null || ie == null || mesh.BoneTable >= tables.Count)
                    continue;
                int slots = we.Type == 17 ? 8 : 4;
                if ((ie.Type == 17 ? 8 : 4) != slots || pe.Type is not (2 or 3 or 14))
                {
                    log.Add($"mesh {mi}: unknown vertex format, skipped");
                    continue;
                }
                var table = tables[mesh.BoneTable];

                int Address(Element e, int v)
                    => (int)(m.VertexOffset[lod] - m.DataStart + mesh.BufferOffset[e.Stream] + v * mesh.Stride[e.Stream] + e.Offset);

                // Vertices already weighted in this mesh, per part (so the second side doesn't redo the first's share).
                var done = new Dictionary<string, HashSet<int>>();

                foreach (var side in sides)
                {
                    var names = table.Select(t => t < bones.Count ? bones[t] : "").ToList();
                    var usedSources = side.Sources.Where(names.Contains).ToList();
                    bool old = names.Any(n => n.StartsWith(side.OldPrefix, StringComparison.Ordinal));
                    if (usedSources.Count == 0 && !old)
                        continue;
                    if (!Frame(side.Positions, side.Rows, side.Columns, side.Steps, out var origin, out var inverse))
                    {
                        log.Add($"{side.Bones[0]}: the lattice is flat, skipped");
                        continue;
                    }

                    // A shape grid: only the grid points this mesh's vertices land near go in its bone table (a mesh
                    // that only reaches the edge of a grid needs a few, so more fit next to the rest of the rig).
                    IEnumerable<string> needed = side.Bones;
                    if (side.Optional)
                    {
                        var sourceNames = new HashSet<string>((side.Merge ? usedSources : usedSources.Take(1)).Concat(side.Bones).Append(side.Parent));
                        var sourceSlots = new HashSet<int>();
                        for (int k = 0; k < names.Count; k++)
                            if (sourceNames.Contains(names[k]) || names[k].StartsWith(side.OldPrefix, StringComparison.Ordinal))
                                sourceSlots.Add(k);
                        bool gridBack = side.Bones.Length == side.Rows * side.Columns * (side.Steps + 1);
                        var used = new SortedSet<int>();
                        for (int v = 0; v < mesh.VertexCount; v++)
                        {
                            int wa = Address(we, v), ia = Address(ie, v), pa = Address(pe, v);
                            if (wa + slots > data.Length || ia + slots > data.Length || pa + 12 > data.Length)
                                break;
                            bool on = false;
                            for (int k = 0; k < slots && !on; k++)
                                on = data[wa + k] != 0 && sourceSlots.Contains(data[ia + k]);
                            if (!on)
                                continue;
                            var c = Vector3.Transform(ReadPosition(data, pa, pe.Type) - origin, inverse);
                            float cu = Math.Clamp(c.X, 0f, side.Columns - 1), ct = Math.Clamp(c.Y, 0f, side.Rows - 1), cd = Math.Clamp(c.Z, 0f, side.Steps);
                            foreach (var (col, row, layer, x) in Corners(cu, ct, cd, side.Columns, side.Rows, side.Steps))
                                if (layer > 0 || gridBack)
                                    used.Add((gridBack ? layer : layer - 1) * side.Rows * side.Columns + row * side.Columns + col);
                        }
                        if (used.Count == 0)
                            continue;
                        needed = used.Where(i => i < side.Bones.Length).Select(i => side.Bones[i]).ToList();
                    }

                    // The lattice bones (and the bone the back face stays on) in this mesh's bone table.
                    bool full = false;
                    int before = table.Count;
                    foreach (var name in needed.Prepend(side.Parent).Concat(side.Nipple != null ? new[] { side.Nipple } : Array.Empty<string>()))
                    {
                        var id = (ushort)BoneId(name);
                        if (table.Contains(id))
                            continue;
                        if (table.Count >= cap)
                        {
                            full = true;
                            break;
                        }
                        table.Add(id);
                    }
                    if (full && side.Optional)
                    {
                        // No room for this one: left out of this mesh (the rest of the rig still goes on).
                        table.RemoveRange(before, table.Count - before);
                        log.Add($"mesh {mi}: no room for {side.PartKey} ({cap} bones a mesh), left out");
                        continue;
                    }
                    if (full)
                    {
                        log.Add($"mesh {mi}: too many bones for one mesh ({cap}), skipped");
                        return null;
                    }

                    var local = new Dictionary<string, int>();
                    for (int k = 0; k < table.Count; k++)
                        local.TryAdd(bones[table[k]], k);
                    var source = new HashSet<int>();
                    foreach (var n in (side.Merge ? usedSources : usedSources.Take(1)).Concat(side.Bones).Append(side.Parent))
                        if (local.TryGetValue(n, out var k))
                            source.Add(k);
                    foreach (var (n, k) in local)
                        if (n.StartsWith(side.OldPrefix, StringComparison.Ordinal))
                            source.Add(k);
                    int parent = local[side.Parent];
                    var lattice = side.Bones.Select(b => local.TryGetValue(b, out var slot) ? slot : -1).ToArray();
                    // 27 bones: the back face is bones too; 18: the back face is the part's own bone.
                    bool backBones = side.Bones.Length == side.Rows * side.Columns * (side.Steps + 1);
                    int nipple = side.Nipple != null ? local[side.Nipple] : -1;
                    if (nipple >= 0)
                        source.Add(nipple);
                    var ownLattice = new HashSet<int>(lattice.Where(k => k >= 0));
                    if (nipple >= 0)
                        ownLattice.Add(nipple);
                    if (!done.TryGetValue(side.PartKey, out var doneHere))
                        done[side.PartKey] = doneHere = new HashSet<int>();

                    // The middle between the two sides: vertices there are shared between both boxes (more the
                    // closer they are to the middle), so the two halves move and grow as one, with no cleft opening.
                    var partner = side.Fill > 0f && side.PartKey.Length > 0
                        ? sides.FirstOrDefault(o => !ReferenceEquals(o, side) && o.PartKey == side.PartKey) : null;
                    int[]? partnerLattice = null;
                    Vector3 partnerOrigin = default;
                    Matrix4x4 partnerInverse = default;
                    bool partnerBack = false;
                    float midX = 0f, band = 0f;
                    if (partner != null && Frame(partner.Positions, partner.Rows, partner.Columns, partner.Steps, out partnerOrigin, out partnerInverse))
                    {
                        bool room = true;
                        foreach (var name in partner.Bones)
                        {
                            var id = (ushort)BoneId(name);
                            if (table.Contains(id))
                                continue;
                            if (table.Count >= cap)
                            {
                                room = false;
                                break;
                            }
                            table.Add(id);
                        }
                        if (room)
                        {
                            for (int k = 0; k < table.Count; k++)
                                local.TryAdd(bones[table[k]], k);
                            partnerLattice = partner.Bones.Select(b => local[b]).ToArray();
                            partnerBack = partner.Bones.Length == partner.Rows * partner.Columns * (partner.Steps + 1);
                            var ca = Average(side.Positions);
                            var cb = Average(partner.Positions);
                            midX = (ca.X + cb.X) * 0.5f;
                            band = MathF.Abs(ca.X - cb.X) * 0.5f * (0.5f + side.Fill);
                        }
                    }

                    int changed = 0;
                    Span<byte> w = stackalloc byte[8];
                    Span<byte> ix = stackalloc byte[8];
                    for (int v = 0; v < mesh.VertexCount; v++)
                    {
                        int wa = Address(we, v), ia = Address(ie, v), pa = Address(pe, v);
                        if (wa + slots > data.Length || ia + slots > data.Length || pa + 12 > data.Length)
                            break;
                        data.AsSpan(wa, slots).CopyTo(w);
                        data.AsSpan(ia, slots).CopyTo(ix);
                        float weight = 0f;
                        var others = new List<(int Bone, float W)>();
                        bool already = doneHere.Contains(v);
                        for (int k = 0; k < slots; k++)
                        {
                            if (w[k] == 0)
                                continue;
                            // Already given its share of this side's box by the other side's pass: keep that as it is.
                            if (source.Contains(ix[k]) && !(already && ownLattice.Contains(ix[k])))
                                weight += w[k] / 255f;
                            else
                                others.Add((ix[k], w[k] / 255f));
                        }
                        if (weight <= 0f)
                            continue;

                        var position = ReadPosition(data, pa, pe.Type);
                        var c = Vector3.Transform(position - origin, inverse);
                        float cu = Math.Clamp(c.X, 0f, side.Columns - 1), ct = Math.Clamp(c.Y, 0f, side.Rows - 1), cd = Math.Clamp(c.Z, 0f, side.Steps);
                        // All of the part's own weight goes on the box (clamped onto its nearest face from outside):
                        // the model's own weights already blend the part into the body around it, so nothing is left
                        // behind on the part's bone to crease when the box grows.
                        float share = 1f;

                        var wanted = new Dictionary<int, float>();
                        float onParent = weight * (1f - share);
                        foreach (var (col, row, layer, x) in Corners(cu, ct, cd, side.Columns, side.Rows, side.Steps))
                        {
                            if (layer == 0 && !backBones)
                                onParent += weight * share * x;
                            else
                            {
                                int bone = lattice[(backBones ? layer : layer - 1) * side.Rows * side.Columns + row * side.Columns + col];
                                if (bone < 0)
                                    onParent += weight * share * x; // (not in this mesh's table: can't happen, but never lost)
                                else
                                    wanted[bone] = wanted.GetValueOrDefault(bone) + weight * share * x;
                            }
                        }
                        if (onParent > 1e-6f)
                            wanted[parent] = wanted.GetValueOrDefault(parent) + onParent;

                        if (partnerLattice != null && band > 1e-5f)
                        {
                            float shared = 0.5f * side.Fill * (1f - SmoothStep(MathF.Abs(position.X - midX) / band));
                            if (shared > 1e-3f)
                            {
                                foreach (var key in wanted.Keys.ToList())
                                    wanted[key] *= 1f - shared;
                                var c2 = Vector3.Transform(position - partnerOrigin, partnerInverse);
                                float pu = Math.Clamp(c2.X, 0f, partner!.Columns - 1), pt = Math.Clamp(c2.Y, 0f, partner.Rows - 1), pd = Math.Clamp(c2.Z, 0f, partner.Steps);
                                foreach (var (col, row, layer, x) in Corners(pu, pt, pd, partner.Columns, partner.Rows, partner.Steps))
                                {
                                    if (layer == 0 && !partnerBack)
                                        continue;
                                    int bone = partnerLattice[(partnerBack ? layer : layer - 1) * partner.Rows * partner.Columns + row * partner.Columns + col];
                                    wanted[bone] = wanted.GetValueOrDefault(bone) + weight * shared * x;
                                }
                            }
                        }

                        // The nipple and areola on the nipple bone (fading out at the rim), the rest on the box.
                        if (nipple >= 0 && side.NippleRadius > 1e-5f)
                        {
                            float f = Math.Clamp(SmoothStep(1f - Vector3.Distance(position, side.NipplePosition) / side.NippleRadius) * 1.6f, 0f, 1f);
                            if (f > 1e-3f)
                            {
                                foreach (var key in wanted.Keys.ToList())
                                    wanted[key] *= 1f - f;
                                wanted[nipple] = wanted.GetValueOrDefault(nipple) + weight * f;
                            }
                        }

                        // No room for all of it next to the body's weights: the body's smallest weights go onto its
                        // largest (a tiny change, only where it fades into the body), never onto any rig bone.
                        int need = wanted.Count(kv => kv.Value > 1e-5f);
                        // (a shape grid never changes the body's own weights to make room: it takes what's free)
                        if (!side.Optional && others.Count > slots - need)
                        {
                            bool IsRig(int k) => k < table.Count && RigParts.IsRigBone(bones[table[k]]);
                            var body = others.Where(o => !IsRig(o.Bone)).OrderByDescending(o => o.W).ToList();
                            var rig = others.Where(o => IsRig(o.Bone)).ToList();
                            int keep = Math.Max(1, slots - need - rig.Count);
                            if (body.Count > keep)
                            {
                                float spare = body.Skip(keep).Sum(o => o.W);
                                body = body.Take(keep).ToList();
                                body[0] = (body[0].Bone, body[0].W + spare);
                            }
                            others = rig.Concat(body).ToList();
                        }

                        int room = slots - others.Count;
                        if (room < 1)
                            continue;
                        var ranked = wanted.OrderByDescending(kv => kv.Value).Select(kv => (Bone: kv.Key, W: kv.Value)).ToList();
                        if (ranked.Count > room)
                        {
                            // No room for every corner: keep the strongest, scaled up to the same total (nothing is
                            // left on the part's own bone to hold the surface back).
                            var kept = ranked.Take(room).ToList();
                            float sum = kept.Sum(e => e.W);
                            ranked = sum > 1e-6f ? kept.Select(e => (e.Bone, e.W * weight / sum)).ToList() : kept;
                        }
                        var entries = others.Concat(ranked).Where(e => e.W > 1e-5f).OrderByDescending(e => e.W).Take(slots).ToList();
                        if (!Quantize(entries, w, ix, slots))
                            continue;
                        w[..slots].CopyTo(data.AsSpan(wa, slots));
                        ix[..slots].CopyTo(data.AsSpan(ia, slots));
                        doneHere.Add(v);
                        changed++;
                    }
                    if (changed > 0)
                        log.Add($"mesh {mi}: {changed} vertices on {side.Bones[0][..^3]}");
                    total += changed;
                }

                // Even out where each part fades into the body, so it grows and moves without puckering there.
                foreach (var (key, verts) in done)
                {
                    if (verts.Count == 0)
                        continue;
                    var partSides = sides.Where(x => x.PartKey == key).ToList();
                    // A shape grid: each side on its own, handing back to its own side's bone (never the other leg's).
                    var groups = partSides[0].Optional ? partSides.Select(x => new List<Side> { x }).ToList() : new List<List<Side>> { partSides };
                    foreach (var group in groups)
                    {
                        var rigSlots = new HashSet<int>();
                        for (int k = 0; k < table.Count; k++)
                            if (group.Any(x => x.Bones.Contains(bones[table[k]]) || x.Nipple == bones[table[k]]))
                                rigSlots.Add(k);
                        int fallback = table.IndexOf((ushort)bones.IndexOf(group[0].Parent));
                        if (rigSlots.Count == 0)
                            continue;
                        try
                        {
                            Smooth(m, data, lod, mesh, pe, we, ie, slots, rigSlots, fallback, group[0].Optional);
                        }
                        catch (Exception e)
                        {
                            log.Add($"mesh {mi}: couldn't smooth {key} ({e.Message})");
                        }
                    }
                }
            }
        }

        return total == 0 ? null : Rebuild(file, m, bones, added, tables, data);
    }

    // ---- Rounding

    private readonly record struct Ellipsoid(Vector3 Centre, Vector3 Axes);

    /// <summary>
    /// Pull each side's part toward a smooth ellipsoid fitted around it (its top, where it attaches, is left alone),
    /// and push the crease between the two sides out toward their fronts / backs. Normals and tangents of moved
    /// vertices are recomputed. Returns how many vertices moved (0 = nothing).
    /// </summary>
    private static int Round(Parsed m, byte[] data, List<Side> pair, List<string> log)
    {
        // Per mesh: which bone-table slots belong to each side.
        HashSet<int> Slots(List<ushort> table, Side side)
        {
            var set = new HashSet<int>();
            for (int k = 0; k < table.Count; k++)
            {
                var n = table[k] < m.Bones.Count ? m.Bones[table[k]] : "";
                if (side.Sources.Contains(n) || side.Bones.Contains(n) || n.StartsWith(side.OldPrefix, StringComparison.Ordinal))
                    set.Add(k);
            }
            return set;
        }

        // 1. An ellipsoid per side, from LoD 0's vertices that are mostly that side's.
        var ellipsoids = new Ellipsoid?[pair.Count];
        var everything = new List<Vector3>();
        for (int si = 0; si < pair.Count; si++)
        {
            var points = new List<Vector3>();
            var side = pair[si];
            var cache = new Dictionary<Mesh, HashSet<int>>();
            ForEachVertex(m, data, 0, (mesh, decl, v, slots, pe, we, ie) =>
            {
                if (!cache.TryGetValue(mesh, out var mine))
                    cache[mesh] = mine = Slots(m.Tables[mesh.BoneTable], side);
                if (mine.Count > 0 && SideWeight(data, we, ie, slots, mesh, m, 0, v, mine) >= 0.5f)
                    points.Add(ReadPosition(data, Address(m, 0, mesh, pe, v), pe.Type));
            });
            if (points.Count < 30)
                continue;
            ellipsoids[si] = Around(points);
            everything.AddRange(points);
        }
        if (ellipsoids.All(e => e == null))
            return 0;
        // Between the two, the surface is left as modelled (rounding each side onto its own ball would carve a cleft
        // there); the middle is handled by sharing its weights between both sides instead.
        float midX = 0f, lobe = 0f;
        bool pairUp = pair.Count == 2 && ellipsoids[0] != null && ellipsoids[1] != null;
        if (pairUp)
        {
            midX = (ellipsoids[0]!.Value.Centre.X + ellipsoids[1]!.Value.Centre.X) * 0.5f;
            lobe = (ellipsoids[0]!.Value.Axes.X + ellipsoids[1]!.Value.Axes.X) * 0.5f;
        }

        int moved = 0;
        for (int lod = 0; lod < m.LodCount; lod++)
        {
            var (first, count) = m.Lods[lod];
            for (int mi = first; mi < first + count && mi < m.Meshes.Count && mi < m.Declarations.Count; mi++)
            {
                var mesh = m.Meshes[mi];
                var decl = m.Declarations[mi];
                var pe = decl.FirstOrDefault(e => e.Usage == 0);
                var we = decl.FirstOrDefault(e => e.Usage == 1);
                var ie = decl.FirstOrDefault(e => e.Usage == 2);
                if (pe == null || we == null || ie == null || mesh.BoneTable >= m.Tables.Count || pe.Type is not (2 or 3 or 14))
                    continue;
                int slots = we.Type == 17 ? 8 : 4;
                var mine = pair.Select(s => Slots(m.Tables[mesh.BoneTable], s)).ToArray();
                if (mine.All(x => x.Count == 0))
                    continue;

                var shift = new Vector3[mesh.VertexCount];
                bool any = false;
                for (int v = 0; v < mesh.VertexCount; v++)
                {
                    var p = ReadPosition(data, Address(m, lod, mesh, pe, v), pe.Type);
                    var w = new float[pair.Count];
                    for (int si = 0; si < pair.Count; si++)
                        w[si] = mine[si].Count > 0 ? SideWeight(data, we, ie, slots, mesh, m, lod, v, mine[si]) : 0f;
                    if (w.Sum() <= 0.01f)
                        continue;

                    var d = Vector3.Zero;
                    float topFade = 1f;
                    for (int si = 0; si < pair.Count; si++)
                    {
                        if (w[si] <= 0.01f || ellipsoids[si] is not { } e)
                            continue;
                        var local = (p - e.Centre) / e.Axes;
                        float r = local.Length();
                        if (r < 1e-4f)
                            continue;
                        // Leave the top (where it hangs from, the cord) alone; full effect below the middle.
                        float up = (p.Y - e.Centre.Y) / e.Axes.Y;
                        float fade = 1f - SmoothStep((up - 0.15f) / 0.85f);
                        topFade = MathF.Min(topFade, fade);
                        var onSurface = e.Centre + local / r * e.Axes;
                        float keepMiddle = pairUp ? SmoothStep(MathF.Abs(p.X - midX) / MathF.Max(lobe * 0.8f, 1e-4f)) : 1f;
                        d += (onSurface - p) * (pair[si].Roundness * w[si] * fade * keepMiddle);
                    }

                    if (d.LengthSquared() > 1e-12f)
                    {
                        shift[v] = d;
                        any = true;
                        WritePosition(data, Address(m, lod, mesh, pe, v), pe.Type, p + d);
                        moved++;
                    }
                }
                if (any)
                    Renormal(m, data, lod, mesh, decl, shift);
            }
        }
        if (moved > 0)
            log.Add($"rounded {pair[0].PartKey} ({moved} vertices)");
        return moved > 0 ? 1 : 0;
    }

    private static Vector3 Average(Vector3[] points)
    {
        var sum = Vector3.Zero;
        foreach (var p in points)
            sum += p;
        return points.Length > 0 ? sum / points.Length : sum;
    }

    // An ellipsoid around points (1% / 99% bounds per axis).
    private static Ellipsoid Around(List<Vector3> points)
    {
        float Q(Func<Vector3, float> axis, float q)
        {
            var sorted = points.Select(axis).OrderBy(x => x).ToArray();
            return sorted[Math.Clamp((int)(q * (sorted.Length - 1)), 0, sorted.Length - 1)];
        }
        var lo = new Vector3(Q(p => p.X, 0.01f), Q(p => p.Y, 0.01f), Q(p => p.Z, 0.01f));
        var hi = new Vector3(Q(p => p.X, 0.99f), Q(p => p.Y, 0.99f), Q(p => p.Z, 0.99f));
        return new Ellipsoid((lo + hi) * 0.5f, Vector3.Max((hi - lo) * 0.5f, new Vector3(1e-3f)));
    }

    private static int Address(Parsed m, int lod, Mesh mesh, Element e, int v)
        => (int)(m.VertexOffset[lod] - m.DataStart + mesh.BufferOffset[e.Stream] + v * mesh.Stride[e.Stream] + e.Offset);

    private static float SideWeight(byte[] data, Element we, Element ie, int slots, Mesh mesh, Parsed m, int lod, int v, HashSet<int> mine)
    {
        int wa = Address(m, lod, mesh, we, v), ia = Address(m, lod, mesh, ie, v);
        if (wa + slots > data.Length || ia + slots > data.Length)
            return 0f;
        float w = 0f;
        for (int k = 0; k < slots; k++)
            if (data[wa + k] != 0 && mine.Contains(data[ia + k]))
                w += data[wa + k] / 255f;
        return w;
    }

    private static void ForEachVertex(Parsed m, byte[] data, int lod, Action<Mesh, List<Element>, int, int, Element, Element, Element> each)
    {
        if (lod >= m.LodCount)
            return;
        var (first, count) = m.Lods[lod];
        for (int mi = first; mi < first + count && mi < m.Meshes.Count && mi < m.Declarations.Count; mi++)
        {
            var mesh = m.Meshes[mi];
            var decl = m.Declarations[mi];
            var pe = decl.FirstOrDefault(e => e.Usage == 0);
            var we = decl.FirstOrDefault(e => e.Usage == 1);
            var ie = decl.FirstOrDefault(e => e.Usage == 2);
            if (pe == null || we == null || ie == null || mesh.BoneTable >= m.Tables.Count || pe.Type is not (2 or 3 or 14))
                continue;
            int slots = we.Type == 17 ? 8 : 4;
            for (int v = 0; v < mesh.VertexCount; v++)
                if (Address(m, lod, mesh, pe, v) + 12 <= data.Length)
                    each(mesh, decl, v, slots, pe, we, ie);
        }
    }

    private static void WritePosition(byte[] data, int at, byte type, Vector3 p)
    {
        if (type == 14)
        {
            BitConverter.TryWriteBytes(data.AsSpan(at), (Half)p.X);
            BitConverter.TryWriteBytes(data.AsSpan(at + 2), (Half)p.Y);
            BitConverter.TryWriteBytes(data.AsSpan(at + 4), (Half)p.Z);
        }
        else
        {
            BitConverter.TryWriteBytes(data.AsSpan(at), p.X);
            BitConverter.TryWriteBytes(data.AsSpan(at + 4), p.Y);
            BitConverter.TryWriteBytes(data.AsSpan(at + 8), p.Z);
        }
    }

    // New normals (and tangents kept at right angles to them) for vertices that moved, from the triangles around them;
    // vertices at the same spot (UV seams) share one, so no seam lines appear.
    private static void Renormal(Parsed m, byte[] data, int lod, Mesh mesh, List<Element> decl, Vector3[] shift)
    {
        var pe = decl.First(e => e.Usage == 0);
        var ne = decl.FirstOrDefault(e => e.Usage == 3);
        if (ne == null || ne.Type is not (2 or 3 or 14))
            return;
        int n = mesh.VertexCount;
        var pos = new Vector3[n];
        for (int v = 0; v < n; v++)
            pos[v] = ReadPosition(data, Address(m, lod, mesh, pe, v), pe.Type);
        var normal = new Vector3[n];
        int indexStart = (int)(m.IndexOffset[lod] - m.DataStart + mesh.StartIndex * 2);
        for (int t = 0; t + 2 < mesh.IndexCount; t += 3)
        {
            int at = indexStart + t * 2;
            if (at + 6 > data.Length)
                break;
            int a = BitConverter.ToUInt16(data, at), b = BitConverter.ToUInt16(data, at + 2), c = BitConverter.ToUInt16(data, at + 4);
            if (a >= n || b >= n || c >= n)
                continue;
            var face = Vector3.Cross(pos[b] - pos[a], pos[c] - pos[a]); // length = twice the area: area weighting
            normal[a] += face;
            normal[b] += face;
            normal[c] += face;
        }
        // Weld by position.
        var welded = new Dictionary<(int, int, int), Vector3>();
        (int, int, int) Key(Vector3 p) => ((int)MathF.Round(p.X * 20000f), (int)MathF.Round(p.Y * 20000f), (int)MathF.Round(p.Z * 20000f));
        for (int v = 0; v < n; v++)
            welded[Key(pos[v])] = welded.GetValueOrDefault(Key(pos[v])) + normal[v];

        var te = decl.Where(e => e.Usage is 5 or 6 && e.Type == 8).ToList();
        for (int v = 0; v < n; v++)
        {
            float amount = Math.Clamp(shift[v].Length() / 0.003f, 0f, 1f);
            if (amount <= 0f)
                continue;
            var fresh = welded[Key(pos[v])];
            if (fresh.LengthSquared() < 1e-20f)
                continue;
            fresh = Vector3.Normalize(fresh);
            int na = Address(m, lod, mesh, ne, v);
            var old = ReadPosition(data, na, ne.Type);
            if (old.LengthSquared() > 1e-12f && Vector3.Dot(old, fresh) < 0f && Vector3.Dot(old, normal[v]) < 0f)
                fresh = -fresh; // keep the authored side
            var result = Vector3.Normalize(Vector3.Lerp(old.LengthSquared() > 1e-12f ? Vector3.Normalize(old) : fresh, fresh, amount));
            WritePosition(data, na, ne.Type, result);
            foreach (var t in te)
            {
                int ta = Address(m, lod, mesh, t, v);
                var tangent = new Vector3(data[ta] / 255f * 2f - 1f, data[ta + 1] / 255f * 2f - 1f, data[ta + 2] / 255f * 2f - 1f);
                tangent -= result * Vector3.Dot(result, tangent);
                if (tangent.LengthSquared() < 1e-8f)
                    continue;
                tangent = Vector3.Normalize(tangent);
                data[ta] = (byte)Math.Clamp(MathF.Round((tangent.X + 1f) * 0.5f * 255f), 0f, 255f);
                data[ta + 1] = (byte)Math.Clamp(MathF.Round((tangent.Y + 1f) * 0.5f * 255f), 0f, 255f);
                data[ta + 2] = (byte)Math.Clamp(MathF.Round((tangent.Z + 1f) * 0.5f * 255f), 0f, 255f);
            }
        }
    }

    // Where a big reshape folded the skin over itself (triangles turned over: dark, broken shading), the moves around
    // there evened out with their neighbours' until nothing is folded (or it's given up); everywhere else as it was.
    private static void Untangle(Parsed m, byte[] data, int lod, Mesh mesh, Element pe, Vector3[] shift)
    {
        int n = mesh.VertexCount;
        var after = new Vector3[n];
        for (int v = 0; v < n; v++)
            after[v] = ReadPosition(data, Address(m, lod, mesh, pe, v), pe.Type);
        var keys = new Dictionary<(int, int, int), int>();
        var kid = new int[n];
        var kb = new List<Vector3>();
        for (int v = 0; v < n; v++)
        {
            var b = after[v] - shift[v];
            var k = ((int)MathF.Round(b.X * 20000f), (int)MathF.Round(b.Y * 20000f), (int)MathF.Round(b.Z * 20000f));
            if (!keys.TryGetValue(k, out int id))
            {
                keys[k] = id = kb.Count;
                kb.Add(b);
            }
            kid[v] = id;
        }
        int count = kb.Count;
        var ks = new Vector3[count];
        var c = new int[count];
        for (int v = 0; v < n; v++)
        {
            ks[kid[v]] += shift[v];
            c[kid[v]]++;
        }
        for (int i = 0; i < count; i++)
            ks[i] /= Math.Max(1, c[i]);
        var tris = new List<(int A, int B, int C, Vector3 Face)>();
        var near = new HashSet<int>[count];
        for (int i = 0; i < count; i++)
            near[i] = new HashSet<int>();
        int indexStart = (int)(m.IndexOffset[lod] - m.DataStart + mesh.StartIndex * 2);
        for (int t = 0; t + 2 < mesh.IndexCount; t += 3)
        {
            int at = indexStart + t * 2;
            if (at + 6 > data.Length)
                break;
            int a = BitConverter.ToUInt16(data, at), b = BitConverter.ToUInt16(data, at + 2), cc = BitConverter.ToUInt16(data, at + 4);
            if (a >= n || b >= n || cc >= n)
                continue;
            int ka = kid[a], kb2 = kid[b], kc = kid[cc];
            if (ka == kb2 || kb2 == kc || ka == kc)
                continue;
            near[ka].Add(kb2); near[ka].Add(kc);
            near[kb2].Add(ka); near[kb2].Add(kc);
            near[kc].Add(ka); near[kc].Add(kb2);
            var face = Vector3.Cross(kb[kb2] - kb[ka], kb[kc] - kb[ka]);
            if (face.LengthSquared() > 1e-24f)
                tris.Add((ka, kb2, kc, face));
        }
        var start = (Vector3[])ks.Clone();
        bool changed = false;
        for (int iteration = 0; iteration < 80; iteration++)
        {
            var region = new HashSet<int>();
            foreach (var (a, b, cc, face) in tris)
            {
                Vector3 pa = kb[a] + ks[a], pb = kb[b] + ks[b], pc = kb[cc] + ks[cc];
                if (Vector3.Dot(Vector3.Cross(pb - pa, pc - pa), face) <= 0f)
                {
                    region.Add(a);
                    region.Add(b);
                    region.Add(cc);
                }
            }
            if (region.Count == 0)
                break;
            for (int ring = 0; ring < 1 + iteration / 30; ring++)
                foreach (var i in region.ToList())
                    region.UnionWith(near[i]);
            var next = (Vector3[])ks.Clone();
            foreach (var i in region)
            {
                if (near[i].Count == 0)
                    continue;
                var mean = Vector3.Zero;
                foreach (var j in near[i])
                    mean += ks[j];
                next[i] = ks[i] * 0.5f + mean / near[i].Count * 0.5f;
            }
            ks = next;
            changed = true;
        }
        if (!changed)
            return;
        for (int v = 0; v < n; v++)
        {
            int i = kid[v];
            if (Vector3.DistanceSquared(ks[i], start[i]) < 1e-14f)
                continue;
            shift[v] = ks[i];
            WritePosition(data, Address(m, lod, mesh, pe, v), pe.Type, kb[i] + ks[i]);
        }
    }

    // For a reshape: each vertex's authored normal (and tangents) turned the way the triangles around it turned, so the
    // artist's smoothing stays as it was. Each triangle's own turn is used on the normal and the results averaged (by
    // area), which can't flip where opposite-facing triangles meet (a sum of their face normals nearly cancels there);
    // a triangle that turned over is left out. Vertices at the same spot (UV seams) use the same triangles.
    private static void TurnNormals(Parsed m, byte[] data, int lod, Mesh mesh, List<Element> decl, Vector3[] shift)
    {
        var pe = decl.First(e => e.Usage == 0);
        var ne = decl.FirstOrDefault(e => e.Usage == 3);
        if (ne == null || ne.Type is not (2 or 3 or 14))
            return;
        int n = mesh.VertexCount;
        var after = new Vector3[n];
        var before = new Vector3[n];
        for (int v = 0; v < n; v++)
        {
            after[v] = ReadPosition(data, Address(m, lod, mesh, pe, v), pe.Type);
            before[v] = after[v] - shift[v];
        }
        (int, int, int) Key(Vector3 p) => ((int)MathF.Round(p.X * 20000f), (int)MathF.Round(p.Y * 20000f), (int)MathF.Round(p.Z * 20000f));
        var turns = new Dictionary<(int, int, int), List<(Quaternion Turn, float Weight)>>();
        int indexStart = (int)(m.IndexOffset[lod] - m.DataStart + mesh.StartIndex * 2);
        for (int t = 0; t + 2 < mesh.IndexCount; t += 3)
        {
            int at = indexStart + t * 2;
            if (at + 6 > data.Length)
                break;
            int a = BitConverter.ToUInt16(data, at), b = BitConverter.ToUInt16(data, at + 2), c = BitConverter.ToUInt16(data, at + 4);
            if (a >= n || b >= n || c >= n)
                continue;
            if (shift[a].LengthSquared() < 1e-14f && shift[b].LengthSquared() < 1e-14f && shift[c].LengthSquared() < 1e-14f)
                continue;
            var f0 = Vector3.Cross(before[b] - before[a], before[c] - before[a]);
            var f1 = Vector3.Cross(after[b] - after[a], after[c] - after[a]);
            float l0 = f0.Length(), l1 = f1.Length();
            if (l0 < 1e-12f || l1 < 1e-12f || Vector3.Dot(f0, f1) <= 0f)
                continue;
            var turn = Between(f0 / l0, f1 / l1);
            foreach (int v in new[] { a, b, c })
            {
                var k = Key(before[v]);
                if (!turns.TryGetValue(k, out var list))
                    turns[k] = list = new List<(Quaternion, float)>();
                list.Add((turn, l0));
            }
        }
        var te = decl.Where(e => e.Usage is 5 or 6 && e.Type == 8).ToList();
        for (int v = 0; v < n; v++)
        {
            if (shift[v].LengthSquared() < 1e-14f && !turns.ContainsKey(Key(before[v])))
                continue;
            if (!turns.TryGetValue(Key(before[v]), out var list) || list.Count == 0)
                continue;
            int na = Address(m, lod, mesh, ne, v);
            var old = ReadPosition(data, na, ne.Type);
            float len = old.Length();
            if (len < 1e-6f)
                continue;
            var unit = old / len;
            var sum = Vector3.Zero;
            foreach (var (q, w) in list)
                sum += Vector3.Transform(unit, q) * w;
            if (sum.LengthSquared() < 1e-20f)
                continue;
            var turned = Vector3.Normalize(sum);
            WritePosition(data, na, ne.Type, turned * len);
            foreach (var t in te)
            {
                int ta = Address(m, lod, mesh, t, v);
                var tangent = new Vector3(data[ta] / 255f * 2f - 1f, data[ta + 1] / 255f * 2f - 1f, data[ta + 2] / 255f * 2f - 1f);
                if (tangent.LengthSquared() < 1e-8f)
                    continue;
                var tsum = Vector3.Zero;
                foreach (var (q, w) in list)
                    tsum += Vector3.Transform(tangent, q) * w;
                tangent = tsum - turned * Vector3.Dot(turned, tsum);
                if (tangent.LengthSquared() < 1e-8f)
                    continue;
                tangent = Vector3.Normalize(tangent);
                data[ta] = (byte)Math.Clamp(MathF.Round((tangent.X + 1f) * 0.5f * 255f), 0f, 255f);
                data[ta + 1] = (byte)Math.Clamp(MathF.Round((tangent.Y + 1f) * 0.5f * 255f), 0f, 255f);
                data[ta + 2] = (byte)Math.Clamp(MathF.Round((tangent.Z + 1f) * 0.5f * 255f), 0f, 255f);
            }
        }
    }

    // The shortest turn taking unit vector a to unit vector b.
    private static Quaternion Between(Vector3 a, Vector3 b)
    {
        float d = Vector3.Dot(a, b);
        if (d < -0.999999f)
        {
            var axis = Vector3.Cross(Vector3.UnitX, a);
            if (axis.LengthSquared() < 1e-6f)
                axis = Vector3.Cross(Vector3.UnitY, a);
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.PI);
        }
        var c = Vector3.Cross(a, b);
        return Quaternion.Normalize(new Quaternion(c.X, c.Y, c.Z, 1f + d));
    }

    /// <summary>Where one mesh's positions and normals sit inside its LoD's vertex block (for the live mesh).</summary>
    public sealed record MeshLayout(int Lod, int Mesh, int VertexCount, int PositionAt, int PositionStride, byte PositionType,
        int NormalAt, int NormalStride, byte NormalType);

    /// <summary>
    /// The model's vertex blocks (per LoD: where in the file, how big) and each mesh's position / normal layout in them.
    /// </summary>
    public static (uint[] Offset, uint[] Size, List<MeshLayout> Meshes) Layout(byte[] file)
    {
        var m = Parse(file);
        var offset = new uint[3];
        var size = new uint[3];
        for (int i = 0; i < 3; i++)
        {
            offset[i] = BitConverter.ToUInt32(file, 0x10 + i * 4);
            size[i] = BitConverter.ToUInt32(file, 0x28 + i * 4);
        }
        var meshes = new List<MeshLayout>();
        for (int lod = 0; lod < m.LodCount; lod++)
        {
            var (first, count) = m.Lods[lod];
            for (int mi = first; mi < first + count && mi < m.Meshes.Count && mi < m.Declarations.Count; mi++)
            {
                var mesh = m.Meshes[mi];
                var decl = m.Declarations[mi];
                var pe = decl.FirstOrDefault(e => e.Usage == 0);
                var ne = decl.FirstOrDefault(e => e.Usage == 3);
                if (pe == null)
                    continue;
                meshes.Add(new MeshLayout(lod, mi, mesh.VertexCount,
                    (int)mesh.BufferOffset[pe.Stream] + pe.Offset, mesh.Stride[pe.Stream], pe.Type,
                    ne != null ? (int)mesh.BufferOffset[ne.Stream] + ne.Offset : -1, ne != null ? mesh.Stride[ne.Stream] : 0, ne?.Type ?? 0));
            }
        }
        return (offset, size, meshes);
    }

    /// <summary>How much of each vertex (per mesh of one LoD) is on the bones <paramref name="isPart"/> picks.</summary>
    public static Dictionary<int, float[]> PartWeights(byte[] file, int lod, Func<string, bool> isPart)
    {
        var result = new Dictionary<int, float[]>();
        var m = Parse(file);
        if (lod >= m.LodCount)
            return result;
        var (first, count) = m.Lods[lod];
        for (int mi = first; mi < first + count && mi < m.Meshes.Count && mi < m.Declarations.Count; mi++)
        {
            var mesh = m.Meshes[mi];
            var decl = m.Declarations[mi];
            var we = decl.FirstOrDefault(e => e.Usage == 1);
            var ie = decl.FirstOrDefault(e => e.Usage == 2);
            if (we == null || ie == null || mesh.BoneTable >= m.Tables.Count)
                continue;
            int slots = we.Type == 17 ? 8 : 4;
            var table = m.Tables[mesh.BoneTable];
            var mine = table.Select(t => t < m.Bones.Count && isPart(m.Bones[t])).ToArray();
            var weights = new float[mesh.VertexCount];
            if (mine.Any(x => x))
            {
                for (int v = 0; v < mesh.VertexCount; v++)
                {
                    int wa = (int)(m.VertexOffset[lod] + mesh.BufferOffset[we.Stream] + v * mesh.Stride[we.Stream] + we.Offset);
                    int ia = (int)(m.VertexOffset[lod] + mesh.BufferOffset[ie.Stream] + v * mesh.Stride[ie.Stream] + ie.Offset);
                    if (wa + slots > file.Length || ia + slots > file.Length)
                        break;
                    float w = 0f;
                    for (int k = 0; k < slots; k++)
                        if (file[wa + k] != 0 && file[ia + k] < mine.Length && mine[file[ia + k]])
                            w += file[wa + k] / 255f;
                    weights[v] = Math.Min(w, 1f);
                }
            }
            result[mi] = weights;
        }
        return result;
    }

    /// <summary>One mesh's skinning (bone names, each vertex's 8 bone slots and weights) and triangles, for one LoD.</summary>
    public sealed record MeshSkin(int Mesh, string[] Bones, byte[] Indices, float[] Weights, int Slots, ushort[] Triangles);

    public static List<MeshSkin> Skin(byte[] file, int lod)
    {
        var result = new List<MeshSkin>();
        var m = Parse(file);
        if (lod >= m.LodCount)
            return result;
        var (first, count) = m.Lods[lod];
        for (int mi = first; mi < first + count && mi < m.Meshes.Count && mi < m.Declarations.Count; mi++)
        {
            var mesh = m.Meshes[mi];
            var decl = m.Declarations[mi];
            var we = decl.FirstOrDefault(e => e.Usage == 1);
            var ie = decl.FirstOrDefault(e => e.Usage == 2);
            if (we == null || ie == null || mesh.BoneTable >= m.Tables.Count)
                continue;
            int slots = we.Type == 17 ? 8 : 4;
            var names = m.Tables[mesh.BoneTable].Select(t => t < m.Bones.Count ? m.Bones[t] : "").ToArray();
            var indices = new byte[mesh.VertexCount * 8];
            var weights = new float[mesh.VertexCount * 8];
            for (int v = 0; v < mesh.VertexCount; v++)
            {
                int wa = (int)(m.VertexOffset[lod] + mesh.BufferOffset[we.Stream] + v * mesh.Stride[we.Stream] + we.Offset);
                int ia = (int)(m.VertexOffset[lod] + mesh.BufferOffset[ie.Stream] + v * mesh.Stride[ie.Stream] + ie.Offset);
                if (wa + slots > file.Length || ia + slots > file.Length)
                    break;
                for (int k = 0; k < slots; k++)
                {
                    indices[v * 8 + k] = file[ia + k];
                    weights[v * 8 + k] = file[wa + k] / 255f;
                }
            }
            int start = (int)(m.IndexOffset[lod] + mesh.StartIndex * 2);
            int n = (int)Math.Min(mesh.IndexCount, (uint)Math.Max(0, (file.Length - start) / 2));
            var tris = new ushort[n];
            for (int i = 0; i < n; i++)
                tris[i] = BitConverter.ToUInt16(file, start + i * 2);
            result.Add(new MeshSkin(mi, names, indices, weights, slots, tris));
        }
        return result;
    }

    /// <summary>
    /// Every vertex (every LoD) moved by <paramref name="move"/> (its position, the mesh's bone names, its 8 bone slots
    /// and weights, how many slots are used), and the normals of what moved made again. Null if nothing moved.
    /// </summary>
    public static byte[]? Reshape(byte[] file, Func<Vector3, string[], byte[], float[], int, Vector3> move, out int moved)
    {
        moved = 0;
        var m = Parse(file);
        var data = file.AsSpan(m.DataStart).ToArray();
        var idx = new byte[8];
        var w = new float[8];
        for (int lod = 0; lod < m.LodCount; lod++)
        {
            var (first, count) = m.Lods[lod];
            for (int mi = first; mi < first + count && mi < m.Meshes.Count && mi < m.Declarations.Count; mi++)
            {
                var mesh = m.Meshes[mi];
                var decl = m.Declarations[mi];
                var pe = decl.FirstOrDefault(e => e.Usage == 0);
                var we = decl.FirstOrDefault(e => e.Usage == 1);
                var ie = decl.FirstOrDefault(e => e.Usage == 2);
                if (pe == null || pe.Type is not (2 or 3 or 14))
                    continue;
                bool skinned = we != null && ie != null && mesh.BoneTable < m.Tables.Count;
                var names = skinned ? m.Tables[mesh.BoneTable].Select(t => t < m.Bones.Count ? m.Bones[t] : "").ToArray() : Array.Empty<string>();
                int slots = we?.Type == 17 ? 8 : 4;
                var shift = new Vector3[mesh.VertexCount];
                bool any = false;
                for (int v = 0; v < mesh.VertexCount; v++)
                {
                    int pa = Address(m, lod, mesh, pe, v);
                    if (pa < 0 || pa + (pe.Type == 14 ? 6 : 12) > data.Length)
                        break;
                    var p = ReadPosition(data, pa, pe.Type);
                    Array.Clear(idx);
                    Array.Clear(w);
                    if (skinned)
                    {
                        int wa = Address(m, lod, mesh, we!, v), ia = Address(m, lod, mesh, ie!, v);
                        if (wa < 0 || ia < 0 || wa + slots > data.Length || ia + slots > data.Length)
                            break;
                        for (int k = 0; k < slots; k++)
                        {
                            idx[k] = data[ia + k];
                            w[k] = data[wa + k] / 255f;
                        }
                    }
                    var q = move(p, names, idx, w, slots);
                    if (!float.IsFinite(q.X) || !float.IsFinite(q.Y) || !float.IsFinite(q.Z) || Vector3.DistanceSquared(q, p) < 1e-14f)
                        continue;
                    WritePosition(data, pa, pe.Type, q);
                    shift[v] = q - p;
                    any = true;
                    moved++;
                }
                if (any)
                {
                    Untangle(m, data, lod, mesh, pe, shift);
                    TurnNormals(m, data, lod, mesh, decl, shift);
                }
            }
        }
        if (moved == 0)
            return null;
        var result = (byte[])file.Clone();
        data.CopyTo(result, m.DataStart);
        return result;
    }

    public static Vector3 ReadVector(byte[] data, int at, byte type) => ReadPosition(data, at, type);

    public static void WriteVector(byte[] data, int at, byte type, Vector3 p) => WritePosition(data, at, type, p);

    /// <summary>Where the vertices are (LoD 0) that mostly belong to the bones <paramref name="isPart"/> picks.</summary>
    public static List<Vector3> Vertices(byte[] file, Func<string, bool> isPart, float minWeight = 0.3f)
    {
        var list = new List<Vector3>();
        var m = Parse(file);
        if (m.LodCount < 1)
            return list;
        var (first, count) = m.Lods[0];
        for (int mi = first; mi < first + count && mi < m.Meshes.Count && mi < m.Declarations.Count; mi++)
        {
            var mesh = m.Meshes[mi];
            var decl = m.Declarations[mi];
            var pe = decl.FirstOrDefault(e => e.Usage == 0);
            var we = decl.FirstOrDefault(e => e.Usage == 1);
            var ie = decl.FirstOrDefault(e => e.Usage == 2);
            if (pe == null || we == null || ie == null || mesh.BoneTable >= m.Tables.Count || pe.Type is not (2 or 3 or 14))
                continue;
            int slots = we.Type == 17 ? 8 : 4;
            var table = m.Tables[mesh.BoneTable];
            var mine = table.Select(t => t < m.Bones.Count && isPart(m.Bones[t])).ToArray();
            if (!mine.Any(x => x))
                continue;
            for (int v = 0; v < mesh.VertexCount; v++)
            {
                int Address(Element e) => (int)(m.VertexOffset[0] + mesh.BufferOffset[e.Stream] + v * mesh.Stride[e.Stream] + e.Offset);
                int wa = Address(we), ia = Address(ie), pa = Address(pe);
                if (wa + slots > file.Length || ia + slots > file.Length || pa + 12 > file.Length)
                    break;
                float w = 0f;
                for (int k = 0; k < slots; k++)
                {
                    int bone = file[ia + k];
                    if (file[wa + k] != 0 && bone < mine.Length && mine[bone])
                        w += file[wa + k] / 255f;
                }
                if (w >= minWeight)
                    list.Add(ReadPosition(file, pa, pe.Type));
            }
        }
        return list;
    }

    // Weights as bytes summing to 255 (largest remainder), indices beside them; unused slots zero.
    private static bool Quantize(List<(int Bone, float W)> entries, Span<byte> w, Span<byte> ix, int slots)
    {
        float sum = entries.Sum(e => e.W);
        if (sum <= 1e-6f || entries.Any(e => e.Bone > 255))
            return false;
        var scaled = entries.Select(e => e.W / sum * 255f).ToArray();
        var whole = scaled.Select(x => (int)MathF.Floor(x)).ToArray();
        int left = 255 - whole.Sum();
        foreach (var k in Enumerable.Range(0, scaled.Length).OrderByDescending(k => scaled[k] - whole[k]).Take(left))
            whole[k]++;
        w[..slots].Clear();
        ix[..slots].Clear();
        int slot = 0;
        for (int k = 0; k < entries.Count && slot < slots; k++)
        {
            if (whole[k] <= 0)
                continue;
            w[slot] = (byte)whole[k];
            ix[slot] = (byte)entries[k].Bone;
            slot++;
        }
        return slot > 0;
    }

    private static float SmoothStep(float x)
    {
        x = Math.Clamp(x, 0f, 1f);
        return x * x * (3f - 2f * x);
    }

    /// <summary>
    /// The corners of the lattice cell holding (u, v, d), with their weights: the cell's Freudenthal tetrahedron around
    /// the point (4 corners, not 8, leaving room for the body's own weights). Continuous from cell to cell and exactly
    /// linear, so the box moved or scaled as a whole moves every vertex exactly, and shapes come out smooth.
    /// </summary>
    private static IEnumerable<(int Col, int Row, int Layer, float W)> Corners(float u, float v, float d, int columns, int rows, int steps)
    {
        int c0 = Math.Max(0, Math.Min((int)MathF.Floor(u), columns - 2));
        int r0 = Math.Max(0, Math.Min((int)MathF.Floor(v), rows - 2));
        int l0 = Math.Max(0, Math.Min((int)MathF.Floor(d), steps - 1));
        Span<float> f = [u - c0, v - r0, d - l0];
        Span<int> axis = [0, 1, 2];
        // Axes by fraction, largest first.
        for (int i = 0; i < 2; i++)
            for (int j = i + 1; j < 3; j++)
                if (f[axis[j]] > f[axis[i]])
                    (axis[i], axis[j]) = (axis[j], axis[i]);
        var weights = new[] { 1f - f[axis[0]], f[axis[0]] - f[axis[1]], f[axis[1]] - f[axis[2]], f[axis[2]] };
        var order = new[] { axis[0], axis[1], axis[2] };
        int col = c0, row = r0, layer = l0;
        var result = new List<(int, int, int, float)>(4);
        for (int k = 0; k < 4; k++)
        {
            if (weights[k] > 1e-6f)
                result.Add((col, row, layer, weights[k]));
            if (k < 3)
            {
                if (order[k] == 0) col++;
                else if (order[k] == 1) row++;
                else layer++;
            }
        }
        return result;
    }

    /// <summary>
    /// Even out how fast the rig's share of each vertex fades into the body's own bones, over the mesh's surface: only
    /// between vertices that are partly on the rig (fully on it stays fully on it, untouched stays untouched), and open
    /// edges (seams with the body) go fully back to the body. Stops the skin puckering where it fades out when the part
    /// grows or moves.
    /// </summary>
    private static void Smooth(Parsed m, byte[] data, int lod, Mesh mesh, Element pe, Element we, Element ie, int slots,
        HashSet<int> rig, int fallback, bool toOwnBone = false)
    {
        int n = mesh.VertexCount;
        if (n == 0 || rig.Count == 0)
            return;
        // Welded by position (UV seams split vertices).
        var group = new int[n];
        var groups = new Dictionary<(int, int, int), int>();
        for (int v = 0; v < n; v++)
        {
            int pa = Address(m, lod, mesh, pe, v);
            if (pa + 12 > data.Length)
                return;
            var p = ReadPosition(data, pa, pe.Type);
            var key = ((int)MathF.Round(p.X * 20000f), (int)MathF.Round(p.Y * 20000f), (int)MathF.Round(p.Z * 20000f));
            if (!groups.TryGetValue(key, out var g))
                groups[key] = g = groups.Count;
            group[v] = g;
        }
        int gc = groups.Count;
        var share = new float[gc];
        var count = new int[gc];
        var vertexShare = new float[n];
        for (int v = 0; v < n; v++)
        {
            int wa = Address(m, lod, mesh, we, v), ia = Address(m, lod, mesh, ie, v);
            float x = 0f;
            for (int k = 0; k < slots; k++)
                if (data[wa + k] != 0 && rig.Contains(data[ia + k]))
                    x += data[wa + k] / 255f;
            vertexShare[v] = x;
            share[group[v]] += x;
            count[group[v]]++;
        }
        for (int g = 0; g < gc; g++)
            share[g] /= Math.Max(1, count[g]);

        // Neighbours, and open edges.
        var edges = new Dictionary<(int, int), int>();
        int indexStart = (int)(m.IndexOffset[lod] - m.DataStart + mesh.StartIndex * 2);
        for (int t = 0; t + 2 < mesh.IndexCount; t += 3)
        {
            int at = indexStart + t * 2;
            if (at + 6 > data.Length)
                break;
            int a = BitConverter.ToUInt16(data, at), b = BitConverter.ToUInt16(data, at + 2), c = BitConverter.ToUInt16(data, at + 4);
            if (a >= n || b >= n || c >= n)
                continue;
            foreach (var (x, y) in new[] { (group[a], group[b]), (group[b], group[c]), (group[c], group[a]) })
            {
                if (x == y)
                    continue;
                var e = x < y ? (x, y) : (y, x);
                edges[e] = edges.GetValueOrDefault(e) + 1;
            }
        }
        var neighbours = new List<int>[gc];
        for (int g = 0; g < gc; g++)
            neighbours[g] = new List<int>();
        var open = new bool[gc];
        foreach (var ((x, y), uses) in edges)
        {
            neighbours[x].Add(y);
            neighbours[y].Add(x);
            if (uses == 1)
                open[x] = open[y] = true;
        }

        bool Full(int g) => share[g] >= 0.999f;
        bool None(int g) => share[g] <= 1e-6f || open[g];
        var now = new float[gc];
        for (int g = 0; g < gc; g++)
            now[g] = None(g) ? 0f : share[g];
        var next = new float[gc];
        for (int pass = 0; pass < SmoothPasses; pass++)
        {
            for (int g = 0; g < gc; g++)
            {
                if (Full(g)) { next[g] = 1f; continue; }
                if (None(g)) { next[g] = 0f; continue; }
                float sum = 0f;
                foreach (var o in neighbours[g])
                    sum += now[o];
                next[g] = neighbours[g].Count > 0 ? 0.5f * now[g] + 0.5f * sum / neighbours[g].Count : now[g];
            }
            (now, next) = (next, now);
        }

        Span<byte> w = stackalloc byte[8];
        Span<byte> ix = stackalloc byte[8];
        for (int v = 0; v < n; v++)
        {
            float current = vertexShare[v], wanted = now[group[v]];
            if (current <= 0f || MathF.Abs(wanted - current) < 1f / 255f)
                continue;
            if (toOwnBone && wanted > current)
                continue; // (only ever handed back to its own bone; never taken from the others)
            int wa = Address(m, lod, mesh, we, v), ia = Address(m, lod, mesh, ie, v);
            var entries = new List<(int Bone, float W)>();
            float body = 0f;
            for (int k = 0; k < slots; k++)
            {
                if (data[wa + k] == 0)
                    continue;
                float x = data[wa + k] / 255f;
                if (!rig.Contains(data[ia + k]))
                    body += x;
                entries.Add((data[ia + k], x));
            }
            // (a shape grid hands what it lets go of to its own bone, which it moves exactly with: never to the
            // bones around it, so nothing at a seam with another model changes)
            if ((body <= 1e-6f || toOwnBone) && wanted < current)
            {
                // Nothing of the body's to hand the rest to: the part's own bone takes it.
                if (fallback < 0)
                    continue;
                // (only the rig's own share is scaled: the other bones keep exactly what they had)
                entries = entries.Select(e => (e.Bone, rig.Contains(e.Bone) ? e.W * wanted / current : e.W)).Append((fallback, current - wanted)).ToList();
            }
            else if (body > 1e-6f)
                entries = entries.Select(e => (e.Bone, rig.Contains(e.Bone) ? e.W * wanted / current : e.W * (1f - wanted) / body)).ToList();
            else
                continue;
            // Merge duplicates (the fallback may already be there) and keep what fits.
            entries = entries.GroupBy(e => e.Bone).Select(g => (Bone: g.Key, W: g.Sum(e => e.W))).Where(e => e.W > 1e-5f)
                .OrderByDescending(e => e.W).Take(slots).ToList();
            if (Quantize(entries, w, ix, slots))
            {
                w[..slots].CopyTo(data.AsSpan(wa, slots));
                ix[..slots].CopyTo(data.AsSpan(ia, slots));
            }
        }
    }

    /// <summary>
    /// The lattice's box as an affine map (box units column / row / layer, layer 0 = the back face) fitted to the bones'
    /// spots; <paramref name="inverse"/> takes a model-space offset from <paramref name="origin"/> back to box units.
    /// </summary>
    private static bool Frame(Vector3[] positions, int rows, int columns, int steps, out Vector3 origin, out Matrix4x4 inverse)
    {
        // Least squares over the bones: p = origin + col * A + row * B + layer * C.
        int n = positions.Length;
        int first = n == rows * columns * (steps + 1) ? 0 : 1; // box layer of the first bone layer
        var ata = new double[4, 4];
        var atb = new double[4, 3];
        for (int i = 0; i < n; i++)
        {
            int layer = i / (rows * columns) + first, rest = i % (rows * columns);
            double[] a = [rest % columns, rest / columns, layer, 1.0];
            double[] b = [positions[i].X, positions[i].Y, positions[i].Z];
            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 4; c++)
                    ata[r, c] += a[r] * a[c];
                for (int c = 0; c < 3; c++)
                    atb[r, c] += a[r] * b[c];
            }
        }
        var x = Solve(ata, atb);
        origin = default;
        inverse = default;
        if (x == null)
            return false;
        origin = new Vector3((float)x[3, 0], (float)x[3, 1], (float)x[3, 2]);
        // Row-vector convention: offset = (col, row, layer) * F, with F's rows the three axes.
        var forward = new Matrix4x4(
            (float)x[0, 0], (float)x[0, 1], (float)x[0, 2], 0,
            (float)x[1, 0], (float)x[1, 1], (float)x[1, 2], 0,
            (float)x[2, 0], (float)x[2, 1], (float)x[2, 2], 0,
            0, 0, 0, 1);
        return Matrix4x4.Invert(forward, out inverse) && MathF.Abs(forward.GetDeterminant()) > 1e-12f;
    }

    // Gaussian elimination with partial pivoting: A (4x4) X = B (4x3).
    private static double[,]? Solve(double[,] a, double[,] b)
    {
        var m = new double[4, 7];
        for (int r = 0; r < 4; r++)
        {
            for (int c = 0; c < 4; c++)
                m[r, c] = a[r, c];
            for (int c = 0; c < 3; c++)
                m[r, 4 + c] = b[r, c];
        }
        for (int col = 0; col < 4; col++)
        {
            int pivot = col;
            for (int r = col + 1; r < 4; r++)
                if (Math.Abs(m[r, col]) > Math.Abs(m[pivot, col]))
                    pivot = r;
            if (Math.Abs(m[pivot, col]) < 1e-12)
                return null;
            for (int c = 0; c < 7; c++)
                (m[col, c], m[pivot, c]) = (m[pivot, c], m[col, c]);
            for (int r = 0; r < 4; r++)
            {
                if (r == col)
                    continue;
                double f = m[r, col] / m[col, col];
                for (int c = 0; c < 7; c++)
                    m[r, c] -= f * m[col, c];
            }
        }
        var x = new double[4, 3];
        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 3; c++)
                x[r, c] = m[r, 4 + c] / m[r, r];
        return x;
    }

    private static Vector3 ReadPosition(byte[] data, int at, byte type) => type switch
    {
        14 => new Vector3((float)BitConverter.ToHalf(data, at), (float)BitConverter.ToHalf(data, at + 2), (float)BitConverter.ToHalf(data, at + 4)),
        _ => new Vector3(BitConverter.ToSingle(data, at), BitConverter.ToSingle(data, at + 4), BitConverter.ToSingle(data, at + 8)),
    };

    // ---- Reading the file's layout

    private static Parsed Parse(byte[] b)
    {
        var m = new Parsed();
        var r = new BinaryReader(new MemoryStream(b));
        m.Version = r.ReadUInt32();
        m.StackSize = r.ReadUInt32();
        m.RuntimeSize = r.ReadUInt32();
        int declarations = r.ReadUInt16();
        r.ReadUInt16(); // material count
        for (int i = 0; i < 3; i++)
            m.VertexOffset[i] = r.ReadUInt32();
        for (int i = 0; i < 3; i++)
            m.IndexOffset[i] = r.ReadUInt32();
        r.BaseStream.Position = FileHeaderSize - 4;
        m.LodCount = r.ReadByte();
        r.BaseStream.Position = FileHeaderSize;
        m.DataStart = (int)(FileHeaderSize + m.StackSize + m.RuntimeSize);

        for (int d = 0; d < declarations; d++)
        {
            var list = new List<Element>();
            long start = r.BaseStream.Position;
            for (int e = 0; e < 17; e++)
            {
                var el = new Element { Stream = r.ReadByte(), Offset = r.ReadByte(), Type = r.ReadByte(), Usage = r.ReadByte() };
                r.ReadBytes(4);
                if (el.Stream == 255)
                    break;
                list.Add(el);
            }
            r.BaseStream.Position = start + 17 * 8;
            m.Declarations.Add(list);
        }

        m.StringsPos = (int)r.BaseStream.Position;
        m.StringCount = r.ReadUInt16();
        m.StringPad = r.ReadUInt16();
        int size = (int)r.ReadUInt32();
        var strings = r.ReadBytes(size);
        int o = 0;
        for (int i = 0; i < m.StringCount; i++)
        {
            int end = Array.IndexOf(strings, (byte)0, o);
            if (end < 0)
                throw new InvalidDataException("bad string table");
            m.Strings[(uint)o] = Encoding.UTF8.GetString(strings, o, end - o);
            o = end + 1;
        }
        m.StringsUsed = o;

        m.ModelHeaderPos = (int)r.BaseStream.Position;
        r.ReadSingle();
        int meshCount = r.ReadUInt16(), attributeCount = r.ReadUInt16(), submeshCount = r.ReadUInt16(), materialCount = r.ReadUInt16();
        m.BoneCount = r.ReadUInt16();
        int boneTableCount = r.ReadUInt16(), shapeCount = r.ReadUInt16(), shapeMeshCount = r.ReadUInt16(), shapeValueCount = r.ReadUInt16();
        r.ReadByte(); // lod count
        r.ReadByte(); // flags 1
        int elementIdCount = r.ReadUInt16();
        int terrainShadowMeshCount = r.ReadByte();
        byte flags2 = r.ReadByte();
        r.ReadSingle();
        r.ReadSingle();
        r.ReadUInt16();
        int terrainShadowSubmeshCount = r.ReadUInt16();
        r.ReadByte(); // flags 3
        r.ReadByte();
        r.ReadByte();
        m.NeckMorphs = r.ReadByte();
        int boneTableArrays = r.ReadUInt16();
        r.BaseStream.Position = m.ModelHeaderPos + 56;

        r.BaseStream.Position += elementIdCount * 32;
        m.LodPos = (int)r.BaseStream.Position;
        for (int i = 0; i < 3; i++)
        {
            long at = m.LodPos + 60 * i;
            r.BaseStream.Position = at;
            m.Lods[i] = (r.ReadUInt16(), r.ReadUInt16());
        }
        r.BaseStream.Position = m.LodPos + 180;
        if ((flags2 & 0x10) != 0)
            r.BaseStream.Position += 3 * 40; // extra LoDs

        for (int i = 0; i < meshCount; i++)
        {
            long at = r.BaseStream.Position;
            var mesh = new Mesh { VertexCount = r.ReadUInt16() };
            r.ReadUInt16();
            mesh.IndexCount = r.ReadUInt32();
            r.ReadUInt16(); // material
            r.ReadUInt16(); // submesh index
            r.ReadUInt16(); // submesh count
            mesh.BoneTable = r.ReadUInt16();
            mesh.StartIndex = r.ReadUInt32();
            for (int k = 0; k < 3; k++)
                mesh.BufferOffset[k] = r.ReadUInt32();
            for (int k = 0; k < 3; k++)
                mesh.Stride[k] = r.ReadByte();
            r.ReadByte(); // stream count
            r.BaseStream.Position = at + 36;
            m.Meshes.Add(mesh);
        }

        r.BaseStream.Position += attributeCount * 4 + terrainShadowMeshCount * 20 + submeshCount * 16
                                 + terrainShadowSubmeshCount * 12 + materialCount * 4;
        m.BoneNamesPos = (int)r.BaseStream.Position;
        for (int i = 0; i < m.BoneCount; i++)
            m.Bones.Add(m.Strings.TryGetValue(r.ReadUInt32(), out var name) ? name : "");

        if (m.Version == V5)
        {
            for (int i = 0; i < boneTableCount; i++)
            {
                var all = new ushort[64];
                for (int k = 0; k < 64; k++)
                    all[k] = r.ReadUInt16();
                int count = (int)r.ReadUInt32();
                m.Tables.Add(all.Take(Math.Min(count, 64)).ToList());
            }
        }
        else
        {
            for (int i = 0; i < boneTableCount; i++)
            {
                long at = r.BaseStream.Position;
                int offset = r.ReadUInt16(), count = r.ReadUInt16();
                long back = r.BaseStream.Position;
                r.BaseStream.Position = at + offset * 4;
                var list = new List<ushort>();
                for (int k = 0; k < count; k++)
                    list.Add(r.ReadUInt16());
                m.Tables.Add(list);
                r.BaseStream.Position = back;
            }
            r.BaseStream.Position += boneTableArrays * 2;
        }
        m.BoneTablesEnd = (int)r.BaseStream.Position;

        r.BaseStream.Position += shapeCount * 16 + shapeMeshCount * 12 + shapeValueCount * 4;
        int boneMap = (int)r.ReadUInt32();
        r.BaseStream.Position += boneMap;
        m.PaddingPos = (int)r.BaseStream.Position;
        int padding = r.ReadByte();
        r.BaseStream.Position += padding;
        m.BoxesPos = (int)r.BaseStream.Position;
        m.BoneBoxesPos = m.BoxesPos + 4 * 32;
        if (m.BoneBoxesPos + m.BoneCount * 32 > m.DataStart)
            throw new InvalidDataException("the model's layout isn't what Limber expects");
        return m;
    }

    // ---- Writing it back

    private static byte[] Rebuild(byte[] b, Parsed m, List<string> bones, List<string> added, List<List<ushort>> tables, byte[] data)
    {
        var o = new MemoryStream();
        var w = new BinaryWriter(o);
        w.Write(b, 0, m.StringsPos); // file header (patched below) and vertex declarations

        // Strings: the old ones exactly where they were, the new bone names after them, padded to 4.
        var strings = new MemoryStream();
        strings.Write(b, m.StringsPos + 8, m.StringsUsed);
        var newOffsets = new List<uint>();
        foreach (var name in added)
        {
            newOffsets.Add((uint)strings.Length);
            strings.Write(Encoding.UTF8.GetBytes(name));
            strings.WriteByte(0);
        }
        while (strings.Length % 4 != 0)
            strings.WriteByte(0);
        w.Write((ushort)(m.StringCount + added.Count));
        w.Write(m.StringPad);
        w.Write((uint)strings.Length);
        w.Write(strings.ToArray());

        int modelHeader = (int)o.Position;
        w.Write(b, m.ModelHeaderPos, m.BoneNamesPos - m.ModelHeaderPos);
        int lodPos = modelHeader + (m.LodPos - m.ModelHeaderPos);

        w.Write(b, m.BoneNamesPos, m.BoneCount * 4);
        foreach (var offset in newOffsets)
            w.Write(offset);

        bool v5 = m.Version == V5;
        if (v5)
        {
            foreach (var t in tables)
            {
                for (int k = 0; k < 64; k++)
                    w.Write(k < t.Count ? t[k] : (ushort)0);
                w.Write((uint)t.Count);
            }
        }
        else
        {
            // Headers (offset in 4-byte units from the header itself, count), then the index arrays, each padded to even.
            int arrays = tables.Count;
            for (int k = 0; k < tables.Count; k++)
            {
                w.Write((ushort)(arrays - k));
                w.Write((ushort)tables[k].Count);
                arrays += (tables[k].Count + 1) / 2;
            }
            foreach (var t in tables)
            {
                foreach (var index in t)
                    w.Write(index);
                if ((t.Count & 1) == 1)
                    w.Write((ushort)0);
            }
        }

        w.Write(b, m.BoneTablesEnd, m.PaddingPos - m.BoneTablesEnd); // shapes, shape meshes / values, submesh bone map
        long next = o.Position + 1;
        byte padding = (byte)(next & 7);
        if (padding > 0)
            padding = (byte)(8 - padding);
        w.Write(padding);
        for (int k = 0; k < padding; k++)
            w.Write((byte)0);
        w.Write(b, m.BoxesPos, 4 * 32);
        w.Write(b, m.BoneBoxesPos, m.BoneCount * 32);
        foreach (var _ in added)
            w.Write(b, m.BoxesPos + 32, 32); // a new bone's box: the whole model's

        int header = (int)o.Position;
        int delta = header - m.DataStart;
        w.Write(data);
        var result = o.ToArray();

        // Model header: bone count and (v6) the bone table arrays' total length.
        BitConverter.TryWriteBytes(result.AsSpan(modelHeader + 12), (ushort)bones.Count);
        if (!v5)
            BitConverter.TryWriteBytes(result.AsSpan(modelHeader + 44), (ushort)tables.Sum(t => t.Count + (t.Count & 1)));

        // File header: runtime size, and every absolute offset into the data moves by delta.
        BitConverter.TryWriteBytes(result.AsSpan(8), (uint)(header - FileHeaderSize - m.StackSize));
        for (int i = 0; i < 3; i++)
        {
            Shift(result, 16 + 4 * i, delta);
            Shift(result, 28 + 4 * i, delta);
            if (i < m.LodCount)
            {
                Shift(result, lodPos + 60 * i + 32, delta); // edge geometry
                Shift(result, lodPos + 60 * i + 52, delta); // vertex data
                Shift(result, lodPos + 60 * i + 56, delta); // index data
            }
        }
        return result;
    }

    private static void Shift(byte[] file, int at, int delta)
    {
        uint v = BitConverter.ToUInt32(file, at);
        if (v != 0)
            BitConverter.TryWriteBytes(file.AsSpan(at), (uint)(v + delta));
    }
}
