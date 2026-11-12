using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Limber.BodyRig;
using Limber.Core;

namespace Limber.Gpu;

/// <summary>
/// Mesh sculpt (experimental): the face model's own vertices pinched and dragged with the mouse, right on the face.
///
/// The face is read once (its vertices merged where they share a place, so a texture seam never tears; the face's
/// shape-key copies of a vertex follow it). Each frame the page skins every point the way the game will (face bones,
/// their weights) to find it on the screen, for picking and for the dots. Edits are kept in the face's rest space,
/// like a blend shape, so they ride along with every bone and animation.
///
/// Like the face morphs, this doesn't write the vertex buffer itself: the face rig (FaceRig) and Oral's lips mesh
/// (LipMesh) add the edits when they write it (the face rig is started for them when nothing else has the face), so
/// Oral's lips wrap first and the edits go on top. One face at a time. Only on this PC; nothing is sent anywhere.
/// </summary>
public static class MeshSculpt
{
    // ---- The face, as read --------------------------------------------------------------------------------------------

    public sealed class Point
    {
        public Vector3 Rest, Normal;
        public (int Mesh, int Vertex)[] Members = []; // the vertices at this place, and their shape-key copies
        public int[] Bone = [];                        // index into Model.Bones
        public float[] Weight = [];
        public int[] Neighbours = [];
        public bool Shown;                             // drawn by the face as it is (not only a shape key's copy)
        public bool Inner;                             // eyes, teeth, tongue
        public int Mirror = -1;                        // the point across the face (-1: none, or on the middle)
    }

    public sealed class Model
    {
        public required string File;
        public required string Key;
        public required int ByteWidth;
        public required string[] Bones;
        public required Point[] Points;
        public required Dictionary<int, MdlRigger.MeshLayout> Layouts;
        public required Dictionary<int, Vector3[]> Rest;     // per mesh: each vertex's rest place
        public required Dictionary<int, int[]> PointOf;      // per mesh: each vertex's point (-1: none)
        public required Dictionary<int, ushort[]> Triangles; // per mesh (with the shape keys' triangles)
        public float Size;                                   // how big the face is (model metres across)
        public int[] PointTriangles = [];                    // the face as drawn, as point triangles (for the map)
        public Vector3 Centre;                               // the middle of the face (rest space)
    }

    /// <summary>A character's edits on one face model: rest-space moves, per point.</summary>
    public sealed class Sculpt
    {
        public string Name = "";
        public string Key = "";
        public bool On = true;
        public Dictionary<int, Vector3> Edits = new();
    }

    private static Model? _model;
    private static Sculpt? _sculpt;
    private static nint _owner;
    private static int _version;
    public static volatile string Applied = "";

    public static Model? Current => _model;
    public static Sculpt? Edits => _sculpt;
    /// <summary>The character the edits are for (0: none).</summary>
    public static nint Owner => _owner;
    /// <summary>Bumped whenever the edits change: writers write again when it moves.</summary>
    public static int Version => _version;
    public static void Changed() => _version++;

    /// <summary>The edits go on this character's face now.</summary>
    public static bool Applies(nint address)
        => address != 0 && address == _owner && _model != null && _sculpt is { On: true } s && s.Edits.Count > 0;

    private static bool _oralOk;

    /// <summary>The edits go on this character's face during Oral too (not for Lalafell: expressions only).</summary>
    public static bool AppliesInOral(nint address) => _oralOk && Applies(address);

    /// <summary>The page's character and face (its saved edits loaded when they change).</summary>
    public static void Use(nint owner, string name, Model? model, bool oralOk)
    {
        _owner = owner;
        _oralOk = oralOk;
        if(model == null)
        {
            if(_model != null)
                _version++;
            _model = null;
            _sculpt = null;
            return;
        }
        if(ReferenceEquals(model, _model) && _sculpt != null && _sculpt.Name == name)
            return;
        _model = model;
        _sculpt = Load(name, model);
        _version++;
    }

    public static void Stop()
    {
        _owner = 0;
        _model = null;
        _sculpt = null;
        _version++;
    }

    private static readonly Dictionary<string, Model> Models = new();

    /// <summary>The face read from this model file (one LoD), made once and kept. Null if it can't be read.</summary>
    public static Model? Prepare(string file, byte[] data, int lod)
    {
        var (offset, size, layouts) = MdlRigger.Layout(data);
        if(lod < 0 || lod >= size.Length || size[lod] == 0)
            return null;
        string key = FaceMorphs.KeyOf(file, (int)size[lod]);
        if(Models.TryGetValue(key, out var known))
            return known;
        var block = data.AsSpan((int)offset[lod], (int)size[lod]).ToArray();
        var skins = MdlRigger.Skin(data, lod);
        var bones = new List<string>();
        int BoneSlot(string name)
        {
            int i = bones.IndexOf(name);
            if(i >= 0)
                return i;
            bones.Add(name);
            return bones.Count - 1;
        }
        static (int, int, int) Cell(Vector3 p) => ((int)MathF.Round(p.X * 1e5f), (int)MathF.Round(p.Y * 1e5f), (int)MathF.Round(p.Z * 1e5f));

        var points = new List<Point>();
        var members = new List<List<(int, int)>>();
        var cells = new Dictionary<(int, int, int), int>();
        var lays = new Dictionary<int, MdlRigger.MeshLayout>();
        var rests = new Dictionary<int, Vector3[]>();
        var pointOf = new Dictionary<int, int[]>();
        var tris = new Dictionary<int, ushort[]>();
        var near = new List<HashSet<int>>();
        var pointTris = new List<int>();
        foreach(var layout in layouts.Where(l => l.Lod == lod))
        {
            var skin = skins.FirstOrDefault(s => s.Mesh == layout.Mesh);
            if(skin == null)
                continue;
            int count = layout.VertexCount;
            var rest = new Vector3[count];
            var normal = new Vector3[count];
            int valid = 0;
            for(int v = 0; v < count; v++)
            {
                int pa = layout.PositionAt + v * layout.PositionStride;
                if(pa + 12 > block.Length)
                    break;
                rest[v] = MdlRigger.ReadVector(block, pa, layout.PositionType);
                if(layout.NormalAt >= 0 && layout.NormalType is 2 or 3 or 14)
                {
                    int na = layout.NormalAt + v * layout.NormalStride;
                    if(na + 6 <= block.Length)
                        normal[v] = MdlRigger.ReadVector(block, na, layout.NormalType);
                }
                valid = v + 1;
            }
            count = valid;
            var map = new int[layout.VertexCount];
            Array.Fill(map, -1);

            // The face as drawn: vertices its triangles use (one point per place).
            var baseTris = skin.Triangles;
            var used = new bool[count];
            foreach(var t in baseTris)
                if(t < count)
                    used[t] = true;
            int NewPoint(int v, bool shown)
            {
                var p = new Point { Rest = rest[v], Normal = normal[v], Shown = shown };
                var bl = new List<int>();
                var wl = new List<float>();
                float inner = 0f;
                for(int k = 0; k < skin.Slots; k++)
                {
                    int slot = skin.Indices[v * 8 + k];
                    float w = skin.Weights[v * 8 + k];
                    if(slot >= skin.Bones.Length || w <= 0f)
                        continue;
                    string b = skin.Bones[slot];
                    bl.Add(BoneSlot(b));
                    wl.Add(w);
                    if(b.StartsWith("j_f_eye", StringComparison.Ordinal) || b.StartsWith("j_f_haguki", StringComparison.Ordinal)
                       || b.StartsWith("j_f_bero", StringComparison.Ordinal))
                        inner += w;
                }
                p.Bone = bl.ToArray();
                p.Weight = wl.ToArray();
                p.Inner = inner > 0.5f;
                points.Add(p);
                members.Add(new());
                near.Add(new());
                return points.Count - 1;
            }
            for(int v = 0; v < count; v++)
            {
                if(!used[v])
                    continue;
                var c = Cell(rest[v]);
                if(!cells.TryGetValue(c, out int pi))
                    cells[c] = pi = NewPoint(v, true);
                map[v] = pi;
                members[pi].Add((layout.Mesh, v));
            }

            // The shape keys' copies of vertices (a face's chosen lips, nose...: drawn in place of the originals) follow
            // the vertex they stand in for.
            var all = new List<ushort>(baseTris);
            try
            {
                foreach(var (_, values) in MdlRigger.MeshShapes(data, lod, layout.Mesh))
                {
                    var swap = new Dictionary<int, int>();
                    foreach(var (index, vertex) in values)
                    {
                        swap[index] = vertex;
                        if(index < baseTris.Length && vertex < count && map[vertex] < 0 && baseTris[index] < count && map[baseTris[index]] >= 0)
                        {
                            map[vertex] = map[baseTris[index]];
                            members[map[vertex]].Add((layout.Mesh, vertex));
                        }
                    }
                    foreach(int tri in swap.Keys.Select(i => i / 3).Distinct())
                    {
                        if(tri * 3 + 2 >= baseTris.Length)
                            continue;
                        for(int k = 0; k < 3; k++)
                        {
                            int at = tri * 3 + k;
                            all.Add((ushort)(swap.TryGetValue(at, out int sv) ? sv : baseTris[at]));
                        }
                    }
                }
            }
            catch
            {
                // (no shape keys read: the face as drawn is enough)
            }
            for(int i = 0; i + 2 < baseTris.Length; i += 3)
            {
                int a = baseTris[i], b = baseTris[i + 1], c = baseTris[i + 2];
                if(a >= count || b >= count || c >= count)
                    continue;
                int pa = map[a], pb = map[b], pc = map[c];
                if(pa < 0 || pb < 0 || pc < 0)
                    continue;
                if(pa != pb && pb != pc && pa != pc)
                {
                    pointTris.Add(pa);
                    pointTris.Add(pb);
                    pointTris.Add(pc);
                }
                near[pa].Add(pb); near[pa].Add(pc);
                near[pb].Add(pa); near[pb].Add(pc);
                near[pc].Add(pa); near[pc].Add(pb);
            }
            lays[layout.Mesh] = layout;
            rests[layout.Mesh] = rest;
            pointOf[layout.Mesh] = map;
            tris[layout.Mesh] = all.ToArray();
        }
        if(points.Count < 16)
            return null;
        for(int i = 0; i < points.Count; i++)
        {
            points[i].Members = members[i].ToArray();
            near[i].Remove(i);
            points[i].Neighbours = near[i].ToArray();
            var n = points[i].Normal;
            points[i].Normal = n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : Vector3.UnitZ;
        }
        // Across the face: each point's twin at -X (the face's rest space is centred on X = 0).
        var grid = new Dictionary<(int, int, int), List<int>>();
        const float GridCell = 0.001f;
        static (int, int, int) G(Vector3 p) => ((int)MathF.Floor(p.X / GridCell), (int)MathF.Floor(p.Y / GridCell), (int)MathF.Floor(p.Z / GridCell));
        for(int i = 0; i < points.Count; i++)
        {
            var g = G(points[i].Rest);
            if(!grid.TryGetValue(g, out var l))
                grid[g] = l = new();
            l.Add(i);
        }
        for(int i = 0; i < points.Count; i++)
        {
            var p = points[i].Rest;
            if(MathF.Abs(p.X) < 0.0003f)
                continue;
            var want = new Vector3(-p.X, p.Y, p.Z);
            var g = G(want);
            int best = -1;
            float bestD = 0.0008f * 0.0008f;
            for(int dx = -1; dx <= 1; dx++)
                for(int dy = -1; dy <= 1; dy++)
                    for(int dz = -1; dz <= 1; dz++)
                        if(grid.TryGetValue((g.Item1 + dx, g.Item2 + dy, g.Item3 + dz), out var l))
                            foreach(int j in l)
                            {
                                float d = Vector3.DistanceSquared(points[j].Rest, want);
                                if(d < bestD && j != i)
                                {
                                    bestD = d;
                                    best = j;
                                }
                            }
            points[i].Mirror = best;
        }
        var shown = points.Where(p => p.Shown && !p.Inner).ToList();
        float across = shown.Count > 0 ? shown.Max(p => p.Rest.X) - shown.Min(p => p.Rest.X) : 0.15f;
        var model = new Model
        {
            File = file, Key = key, ByteWidth = (int)size[lod], Bones = bones.ToArray(), Points = points.ToArray(),
            Layouts = lays, Rest = rests, PointOf = pointOf, Triangles = tris, Size = MathF.Max(0.02f, across),
            PointTriangles = pointTris.ToArray(),
            Centre = shown.Count > 0 ? shown.Aggregate(Vector3.Zero, (a, p) => a + p.Rest) / shown.Count : Vector3.Zero,
        };
        if(Models.Count > 6)
            Models.Clear();
        Models[key] = model;
        return model;
    }

    // ---- Writing (for the face rig and the lips mesh, on a fresh copy of the face's vertex block) ---------------------

    /// <summary>
    /// Adds the edits to a copy of the face model's vertex block that another writer has just filled (the model's own
    /// vertices, morphs, lips...): positions moved by the edits, normals turned with the surface. Returns false if
    /// nothing was written.
    /// </summary>
    public static bool ApplyTo(byte[] work, string file, int byteWidth, nint address, bool oral = false)
    {
        var model = _model;
        var sculpt = _sculpt;
        if(!(oral ? AppliesInOral(address) : Applies(address)) || model == null || sculpt == null)
            return false;
        if(FaceMorphs.KeyOf(file, byteWidth) != model.Key)
        {
            Applied = "the face drawn isn't the one sculpted (another level of detail or face?)";
            return false;
        }
        int moved = 0;
        foreach(var (mesh, layout) in model.Layouts)
        {
            var rest = model.Rest[mesh];
            var map = model.PointOf[mesh];
            var d = new Vector3[rest.Length];
            bool any = false;
            for(int v = 0; v < rest.Length; v++)
            {
                int p = map[v];
                if(p < 0 || !sculpt.Edits.TryGetValue(p, out var e))
                    continue;
                int pa = layout.PositionAt + v * layout.PositionStride;
                if(pa + 12 > work.Length)
                    continue;
                d[v] = e;
                any = true;
                moved++;
                MdlRigger.WriteVector(work, pa, layout.PositionType, MdlRigger.ReadVector(work, pa, layout.PositionType) + e);
            }
            if(!any || layout.NormalAt < 0 || layout.NormalType is not (2 or 3 or 14) || !model.Triangles.TryGetValue(mesh, out var t))
                continue;
            var before = new Vector3[rest.Length];
            var after = new Vector3[rest.Length];
            for(int i = 0; i + 2 < t.Length; i += 3)
            {
                int a = t[i], b = t[i + 1], c = t[i + 2];
                if(a >= d.Length || b >= d.Length || c >= d.Length)
                    continue;
                if(d[a] == Vector3.Zero && d[b] == Vector3.Zero && d[c] == Vector3.Zero)
                    continue;
                var f0 = Vector3.Cross(rest[b] - rest[a], rest[c] - rest[a]);
                var f1 = Vector3.Cross(rest[b] + d[b] - rest[a] - d[a], rest[c] + d[c] - rest[a] - d[a]);
                before[a] += f0; before[b] += f0; before[c] += f0;
                after[a] += f1; after[b] += f1; after[c] += f1;
            }
            for(int v = 0; v < rest.Length; v++)
            {
                if(d[v] == Vector3.Zero || before[v].LengthSquared() < 1e-20f || after[v].LengthSquared() < 1e-20f)
                    continue;
                int na = layout.NormalAt + v * layout.NormalStride;
                if(na + 6 > work.Length)
                    continue;
                var normal = MdlRigger.ReadVector(work, na, layout.NormalType);
                if(normal.LengthSquared() < 1e-12f)
                    continue;
                var rot = MathUtil.FromTo(Vector3.Normalize(before[v]), Vector3.Normalize(after[v]));
                MdlRigger.WriteVector(work, na, layout.NormalType, Vector3.Transform(normal, rot));
            }
        }
        Applied = $"{sculpt.Edits.Count} points ({moved} vertices) sculpted";
        return moved > 0;
    }

    // ---- Saved edits (one file per character and face model, next to Limber's settings) -------------------------------

    public static string Folder => System.IO.Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "mesh_sculpt");

    private static string FileFor(string name, string key)
    {
        var safe = new string(name.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        return System.IO.Path.Combine(Folder, $"{safe}__{key}.json");
    }

    private sealed class Saved
    {
        public bool On { get; set; } = true;
        public List<float[]> Vertices { get; set; } = new(); // [mesh, vertex, x, y, z] (rest space, model metres)
    }

    private static Sculpt Load(string name, Model model)
    {
        var s = new Sculpt { Name = name, Key = model.Key };
        try
        {
            var path = FileFor(name, model.Key);
            if(!System.IO.File.Exists(path))
                return s;
            var saved = System.Text.Json.JsonSerializer.Deserialize<Saved>(System.IO.File.ReadAllText(path));
            if(saved == null)
                return s;
            s.On = saved.On;
            foreach(var row in saved.Vertices)
            {
                if(row.Length < 5 || !model.PointOf.TryGetValue((int)row[0], out var map) || (int)row[1] < 0 || (int)row[1] >= map.Length)
                    continue;
                int p = map[(int)row[1]];
                var e = new Vector3(row[2], row[3], row[4]);
                if(p >= 0 && MathUtil.IsFinite(e))
                    s.Edits[p] = e;
            }
        }
        catch(Exception e)
        {
            Plugin.Log.Warning(e, "Limber mesh sculpt: couldn't read the saved edits");
        }
        return s;
    }

    public static void Save()
    {
        var model = _model;
        var s = _sculpt;
        if(model == null || s == null || s.Name.Length == 0)
            return;
        try
        {
            var saved = new Saved { On = s.On };
            foreach(var (p, e) in s.Edits)
            {
                var m = model.Points[p].Members.FirstOrDefault(x => x.Mesh >= 0);
                if(model.Points[p].Members.Length > 0)
                    saved.Vertices.Add([m.Mesh, m.Vertex, e.X, e.Y, e.Z]);
            }
            System.IO.Directory.CreateDirectory(Folder);
            var path = FileFor(s.Name, model.Key);
            if(saved.Vertices.Count == 0 && saved.On)
            {
                if(System.IO.File.Exists(path))
                    System.IO.File.WriteAllText(path, "{}");
                return;
            }
            System.IO.File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(saved));
        }
        catch(Exception e)
        {
            Plugin.Log.Warning(e, "Limber mesh sculpt: couldn't save the edits");
        }
    }

    // ---- Where the points are now (the page, each frame) ---------------------------------------------------------------

    /// <summary>One point as drawn this frame.</summary>
    public struct Posed
    {
        public Vector3 World, Normal;   // world place and normal
        public Matrix4x4 ToRest;        // model-space move -> rest-space move (this point's skinning, undone)
    }

    private static string[]? _restFor;
    private static nint _restAddress;
    private static Matrix4x4[] _restInverse = [];
    private static readonly SkeletonInfoCache Cache = new();

    /// <summary>
    /// Every point skinned the way the game will draw it (with the edits), in the world. False if the character's
    /// skeleton can't be read.
    /// </summary>
    public static unsafe bool Pose(nint address, ushort index, Model model, Sculpt? sculpt, ref Posed[] posed, out ActorSkeleton sk)
    {
        if(!ActorSkeleton.TryGet(address, index, Cache, out sk))
            return false;
        var bones = model.Bones;
        if(!ReferenceEquals(_restFor, bones) || _restAddress != address)
        {
            _restInverse = new Matrix4x4[bones.Length];
            for(int i = 0; i < bones.Length; i++)
                _restInverse[i] = Matrix4x4.Invert(Limber.Rig.OralService.RestMatrix(sk, bones[i]), out var inv) ? inv : Matrix4x4.Identity;
            _restFor = bones;
            _restAddress = address;
        }
        var skinning = new Matrix4x4[bones.Length];
        for(int i = 0; i < bones.Length; i++)
        {
            Limber.Core.Frame frame;
            Vector3 size;
            if(sk.TryFindExtraBone(bones[i], Cache, out int p, out int b))
            {
                frame = sk.GetPartialFrame(p, b);
                size = sk.GetPartialScale(p, b);
            }
            else if(sk.Info.IndexOf(bones[i]) is int body and >= 0)
            {
                frame = sk.GetModelFrame(body);
                size = sk.GetModelScale(body);
            }
            else
            {
                skinning[i] = Matrix4x4.Identity;
                continue;
            }
            var now = Matrix4x4.CreateScale(size) * Matrix4x4.CreateFromQuaternion(frame.Rotation) * Matrix4x4.CreateTranslation(frame.Position);
            skinning[i] = _restInverse[i] * now;
        }
        var points = model.Points;
        if(posed.Length != points.Length)
            posed = new Posed[points.Length];
        var toWorld = sk.ModelToWorld;
        for(int i = 0; i < points.Length; i++)
        {
            var pt = points[i];
            var m = new Matrix4x4();
            float total = 0f;
            for(int k = 0; k < pt.Bone.Length; k++)
            {
                float w = pt.Weight[k];
                m += skinning[pt.Bone[k]] * w;
                total += w;
            }
            m = total > 1e-4f ? m * (1f / total) : Matrix4x4.Identity;
            var rest = pt.Rest;
            if(sculpt != null && sculpt.On && sculpt.Edits.TryGetValue(i, out var e))
                rest += e;
            var model3 = Vector3.Transform(rest, m);
            var linear = m;
            linear.M41 = linear.M42 = linear.M43 = 0f;
            linear.M44 = 1f;
            if(!Matrix4x4.Invert(linear, out var toRest))
                toRest = Matrix4x4.Identity;
            var n = Vector3.TransformNormal(Vector3.TransformNormal(pt.Normal, linear), toWorld);
            posed[i] = new Posed
            {
                World = Vector3.Transform(model3, toWorld),
                Normal = n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : Vector3.UnitY,
                ToRest = toRest,
            };
        }
        return true;
    }

    /// <summary>A world-space move of one point, as a rest-space edit.</summary>
    public static Vector3 ToRest(in ActorSkeleton sk, in Posed p, Vector3 worldMove)
        => Vector3.TransformNormal(Vector3.TransformNormal(worldMove, sk.WorldToModel), p.ToRest);

    /// <summary>
    /// How much each point moves with the selection: 1 on it, easing to 0 at `radius` (model metres) away along the
    /// surface (so the upper lip doesn't drag the lower one with it).
    /// </summary>
    public static Dictionary<int, float> Falloff(Model model, IReadOnlyCollection<int> selected, float radius)
    {
        var result = new Dictionary<int, float>();
        var dist = new Dictionary<int, float>();
        var queue = new PriorityQueue<int, float>();
        foreach(int s in selected)
        {
            dist[s] = 0f;
            queue.Enqueue(s, 0f);
        }
        if(radius > 1e-5f)
            while(queue.TryDequeue(out int p, out float d))
            {
                if(d > dist.GetValueOrDefault(p, float.MaxValue))
                    continue;
                foreach(int q in model.Points[p].Neighbours)
                {
                    float nd = d + Vector3.Distance(model.Points[p].Rest, model.Points[q].Rest);
                    if(nd >= radius || nd >= dist.GetValueOrDefault(q, float.MaxValue))
                        continue;
                    dist[q] = nd;
                    queue.Enqueue(q, nd);
                }
            }
        foreach(var (p, d) in dist)
        {
            float u = Math.Clamp(radius > 1e-5f ? d / radius : 0f, 0f, 1f);
            float w = 1f - u * u * (3f - 2f * u);
            if(w > 1e-3f)
                result[p] = w;
        }
        foreach(int s in selected)
            result[s] = 1f;
        return result;
    }
}
