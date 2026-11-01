using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Limber.BodyRig;
using Limber.Core;

namespace Limber.Gpu;

/// <summary>
/// The lips as a live mesh, for Oral: the face model's own lip vertices fitted onto the shaft every frame, so the
/// lips close round it exactly (however few lip bones the face has, and whatever shape the shaft is).
///
/// Per frame: the bone hook hands over the face's skinning matrices and the shaft (its measured middle line and
/// thickness, in the character's model space). On the render thread each lip point is skinned to where the face
/// puts it; points inside the shaft go out to its surface, and lip points near it are drawn onto the surface
/// (fading with distance and with how much of the point is lip), so the lips wrap and seal instead of the shaft
/// passing through them; the result is smoothed over the mesh (keeping its depth), eased in time, turned back into
/// the model's rest space and written into the face model's vertex buffer.
/// Only on this PC; nothing is sent anywhere.
/// </summary>
public static unsafe class LipMesh
{
    // Lip bones, both ways faces name them (older faces: j_f_ulip_a, j_f_lip_l...; Dawntrail faces: j_f_ulip_01_l,
    // j_f_umlip_01_l (the lips' middle), j_f_uslip_l (the sides)...).
    private static readonly string[] LipPrefixes = ["j_f_ulip", "j_f_umlip", "j_f_uslip", "j_f_dlip", "j_f_dmlip", "j_f_dslip", "j_f_lip"];
    private static readonly string[] UpperPrefixes = ["j_f_ulip", "j_f_umlip", "j_f_uslip"];
    private static readonly string[] LowerPrefixes = ["j_f_dlip", "j_f_dmlip", "j_f_dslip"];

    public static bool IsUpperLip(string name) => Array.Exists(UpperPrefixes, p => name.StartsWith(p, StringComparison.Ordinal));
    public static bool IsLowerLip(string name) => Array.Exists(LowerPrefixes, p => name.StartsWith(p, StringComparison.Ordinal));
    /// <summary>A face model's file (game path or Penumbra's replacement).</summary>
    public static bool IsFace(string file) => file.Replace('\\', '/').Contains("/face/", StringComparison.OrdinalIgnoreCase);

    public static bool IsLipBone(string name) => Array.Exists(LipPrefixes, p => name.StartsWith(p, StringComparison.Ordinal));

    private sealed class Point // one surface point (vertices at the same spot, split by UV seams, move as one)
    {
        public Vector3 Rest, RestNormal;
        public int[] Members = [];
        public int[] Bone = [];
        public float[] Weight = [];
        public float Mask;          // how much of it is lip (0..1)
        public int Side;            // +1 upper lip, -1 lower lip, 0 neither (corners, skin)
        public int[] Neighbours = [];
        public Vector3 Offset;      // shown offset (posed model space)
        public Vector3 RestOffset;  // shown offset from the open-mouth shapes (rest space, like a blend shape)
    }

    private sealed class Mesh
    {
        public required MdlRigger.MeshLayout Layout;
        public required Point[] Points;
        public required int[] Triangles; // point indices
        public Wrap? Wrap;               // the lips wrapped onto the shaft (null: fitting live instead)
    }

    // The lips wrapped onto the shaft (see BuildWrap / FitWrap).
    private sealed class Wrap
    {
        public Vector3 Centre;      // the lips' middle (rest space)
        public float Half;          // half the width of where the lips touch
        public float RingZ;         // how far forward the lips' front rims reach (rest space): the flat ring's place
        public int[] Inner = [];    // the points on the surfaces where the two lips touch (they go onto the shaft)
        public float[] Theta = [];  // per inner point: its place round the shaft (radians; upper lip over the top)
        public float[] Behind = []; // per inner point: how far behind its own lip's front rim it is
        public int[] Free = [];     // the points that follow (lips and skin round them)
        public int[][] FreeNear = [];  // per free point: its free neighbours (index into Free)
        public int[][] InnerNear = []; // per free point: its inner neighbours (index into Inner)
        public float[] Degree = [];    // per free point: how many neighbours it has in all
        public float[] PoutWeight = [];// per point: how much it pushes forward with the lips
        public float[] CheekWeight = [];// per point: how much it fills out with the cheeks
        public bool[] Interior = [];   // per point: inside the mouth (hidden behind the lips: never pushed out)
        public int[] Band = [];        // the lips themselves: each rides round with its own lip's rim (keeps its fullness)
        public (int A, int B, float T)[] BandRim = []; // per band point: the two rim points (inner index) it sits between
        public int[] BandSide = [];    // per band point: +1 upper lip, -1 lower lip
        public int[][] BandNear = [];  // per free point: its band neighbours (index into Band)
        public Vector3[] Solved = [];  // per free point: last frame's move (the next frame's start)
        public float Open, Radius, Error;
    }

    private static string _baseStatus = "";
    private static long _nextStatus;

    private sealed class State
    {
        public required LiveMesh.Found Target;
        public required byte[] Original;
        public byte[] FileData = [];     // the whole face model file (for the bench export)
        public required byte[] Work;
        public required List<Mesh> Meshes;
        public required string[] Bones;
        public nint Address;
        public nint Replacement;
        public object? LastFrame;
        public long LastTicks;
        public bool Clean = true; // the buffer holds the model's own vertices
    }

    /// <summary>What the bone hook hands over each frame.</summary>
    public sealed class Frame
    {
        public required Matrix4x4[] Skinning;   // per bone slot: rest model space -> posed model space
        public required Vector3[] Shaft;        // the shaft's middle, base -> tip (model space)
        public required float[] Radius;         // its thickness there (model metres)
        public float Seal;                      // 0..1: how far lip points reach to close onto it
        public float Firmness = 0.7f;           // 0..1: how much the lip moves as a whole (holding its shape)
        public float Forward = 0.003f;          // how far the lips push out round the shaft (model metres)
        public float JawGap;                    // how far the jaw bone already drops the lower lip (model metres)
        public float OpenWidth = 1f, OpenHeight = 1f; // the opening against the shaft (1 = just round it)
        public float CheekPuff;                 // how far the cheeks fill out (model metres)
        public float Drag;                      // the lips pulled along the shaft by the stroke (+ = outward, model metres)
    }

    private static State? _state;
    private static volatile Frame? _frame;
    public static string Status = "";
    public static bool Running => _state != null;
    /// <summary>The mouth's opening map was made (the lips are shaped by it, not by the lip bones).</summary>
    public static bool HasShapes => _state is { } st && st.Meshes.Exists(m => m.Wrap != null);
    public static nint Owner => _state?.Address ?? 0;
    /// <summary>The bones the face model's lip points use (for the bone hook to read), or null.</summary>
    public static string[]? Bones => _state?.Bones;

    /// <summary>For the bench export: the face model (its game path and file) and the last frame handed over.</summary>
    public static (string File, byte[] Data)? FaceModel => _state is { } st ? (st.Target.File, st.FileData) : null;
    public static Frame? LastFrame => _frame ?? _state?.LastFrame as Frame;

    /// <summary>Called from the bone hook with this frame's matrices and shaft (null: nothing touching).</summary>
    public static void Hand(Frame? frame) => _frame = frame;

    // ---- Start / stop (UI thread)

    public static string Start(nint address, nint drawObject, Func<string, byte[]?> readFile)
    {
        Stop();
        var log = new List<string>();
        var found = LiveMesh.Probe(drawObject, log, IsFace);
        foreach(var target in found.Where(f => f.IsVertex && IsFace(f.File)))
        {
            try
            {
                var state = Build(target, readFile);
                if(state == null)
                    continue;
                state.Address = address;
                if(target.Desc.Usage == 1) // immutable: an identical writable buffer in its place
                {
                    state.Replacement = LiveMesh.Replace(target, state.Original);
                    if(state.Replacement == 0)
                        continue;
                }
                _state = state;
                _frame = null;
                var wrap = state.Meshes.FirstOrDefault(m => m.Wrap != null)?.Wrap;
                return Status = _baseStatus = $"Lips: live mesh on {state.Meshes.Sum(m => m.Points.Length)} points"
                    + (wrap != null ? $", lips found ({wrap.Inner.Length} touching points, {wrap.Half * 200f:0.0} cm across)." : ", couldn't find where the lips touch (fitting live instead).");
            }
            catch(Exception e)
            {
                Status = "Lips: " + e.Message;
            }
        }
        return Status = "Lips: couldn't find the face model's lips (the live mesh needs the face to be drawn).";
    }

    public static void Stop()
    {
        var state = _state;
        _state = null;
        _frame = null;
        if(state == null)
            return;
        try
        {
            if(state.Replacement != 0)
                LiveMesh.Restore(state.Target, state.Replacement);
            else if(LiveMesh.StillHolds(state.Target, state.Target.Buffer))
                LiveMesh.Upload(state.Target.Buffer, state.Target.Desc, state.Original);
        }
        catch
        {
            // the model is gone already
        }
    }

    private static State? Build(LiveMesh.Found target, Func<string, byte[]?> readFile)
    {
        var data = readFile(target.File);
        if(data == null)
            return null;
        var (offset, size, layouts) = MdlRigger.Layout(data);
        int lod = Array.FindIndex(size, s => s == target.Desc.ByteWidth);
        if(lod < 0)
            return null;
        var original = data.AsSpan((int)offset[lod], (int)size[lod]).ToArray();
        var skins = MdlRigger.Skin(data, lod);
        var bones = new List<string>();
        int Slot(string name)
        {
            int i = bones.IndexOf(name);
            if(i >= 0)
                return i;
            bones.Add(name);
            return bones.Count - 1;
        }
        var meshes = new List<Mesh>();
        foreach(var layout in layouts.Where(l => l.Lod == lod))
        {
            var skin = skins.FirstOrDefault(s => s.Mesh == layout.Mesh);
            if(skin == null)
                continue;
            int count = layout.VertexCount;
            var lip = skin.Bones.Select(IsLipBone).ToArray();
            var upperBone = skin.Bones.Select(IsUpperLip).ToArray();
            var lowerBone = skin.Bones.Select(IsLowerLip).ToArray();
            var mouthInside = skin.Bones.Select(b => b.StartsWith("j_f_haguki", StringComparison.Ordinal) || b.StartsWith("j_f_bero", StringComparison.Ordinal)).ToArray();
            // Every vertex: where it is, and how much of it is lip / upper lip / lower lip / teeth and tongue.
            var pos = new Vector3[count];
            var lipW = new float[count];
            var upW = new float[count];
            var downW = new float[count];
            var insideW = new float[count];
            int valid = 0;
            for(int v = 0; v < count; v++)
            {
                int pa = layout.PositionAt + v * layout.PositionStride;
                if(pa + 12 > original.Length)
                    break;
                pos[v] = MdlRigger.ReadVector(original, pa, layout.PositionType);
                valid = v + 1;
                for(int k = 0; k < skin.Slots; k++)
                {
                    int slot = skin.Indices[v * 8 + k];
                    if(slot >= lip.Length)
                        continue;
                    float wk = skin.Weights[v * 8 + k];
                    if(lip[slot])
                        lipW[v] += wk;
                    if(upperBone[slot])
                        upW[v] += wk;
                    if(lowerBone[slot])
                        downW[v] += wk;
                    if(mouthInside[slot])
                        insideW[v] += wk;
                }
            }
            count = valid;
            // The triangles, and again with each shape key's replacement vertices in them (a face's chosen lips, nose,
            // cheeks are shape keys: the game draws those vertices instead, so they must be part of the mesh too).
            var t = WithShapes(skin.Triangles, data, lod, layout.Mesh);
            var keyOf = new (int, int, int)[count];
            var normalAt = new Dictionary<(int, int, int), Vector3>();
            for(int v = 0; v < count; v++)
                keyOf[v] = ((int)MathF.Round(pos[v].X * 20000f), (int)MathF.Round(pos[v].Y * 20000f), (int)MathF.Round(pos[v].Z * 20000f));
            for(int i = 0; i + 2 < t.Length; i += 3)
            {
                int a = t[i], b = t[i + 1], c = t[i + 2];
                if(a >= count || b >= count || c >= count)
                    continue;
                var face = Vector3.Cross(pos[b] - pos[a], pos[c] - pos[a]);
                foreach(var v in new[] { a, b, c })
                    normalAt[keyOf[v]] = normalAt.GetValueOrDefault(keyOf[v]) + face;
            }
            Vector3 NormalOf(int vi)
            {
                var nv = normalAt.GetValueOrDefault(keyOf[vi]);
                return nv.LengthSquared() > 1e-20f ? Vector3.Normalize(nv) : Vector3.UnitZ;
            }
            int SideOf(int vi) => upW[vi] > downW[vi] + 0.05f ? 1 : downW[vi] > upW[vi] + 0.05f ? -1 : 0;

            // The mouth: the lips' middle, and the surfaces where the two lips touch (each facing the other lip).
            var centre = Vector3.Zero;
            int lips = 0;
            for(int v = 0; v < count; v++)
                if(upW[v] + downW[v] > 0.5f && insideW[v] < 0.5f)
                {
                    centre += pos[v];
                    lips++;
                }
            var inner = new bool[count];
            float half = 0f;
            int inners = 0;
            if(lips >= 10)
            {
                centre /= lips;
                for(int v = 0; v < count; v++)
                {
                    if(upW[v] + downW[v] <= 0.5f || insideW[v] >= 0.5f)
                        continue;
                    var nv = NormalOf(v);
                    int side = SideOf(v);
                    if(nv.Z < 0.45f && ((side > 0 && nv.Y < -0.5f) || (side < 0 && nv.Y > 0.5f)))
                    {
                        inner[v] = true;
                        inners++;
                        half = MathF.Max(half, MathF.Abs(pos[v].X - centre.X));
                    }
                }
            }
            bool wrap = inners >= 8 && half > 0.006f && half < 0.06f;
            // The area that moves with the lips: the lips and the skin round them (not the teeth or tongue); its
            // outer border stays where it is.
            float scale = wrap ? Math.Clamp(half / 0.023f, 0.6f, 1.8f) : 1f;
            var region = new bool[count];
            var collide = new bool[count];
            if(wrap)
                for(int v = 0; v < count; v++)
                {
                    var d = pos[v] - centre;
                    collide[v] = d.Length() < 0.09f * scale && d.Z > -0.1f * scale;
                }
            if(wrap)
                for(int v = 0; v < count; v++)
                {
                    var d = pos[v] - centre;
                    region[v] = insideW[v] < 0.5f && MathF.Abs(d.X) < 0.07f * scale && d.Y > -0.06f * scale && d.Y < 0.035f * scale
                                && d.Z > -0.07f * scale;
                }

            var points = new List<Point>();
            var members = new List<List<int>>();
            var pointOf = new int[count];
            Array.Fill(pointOf, -1);
            var byPosition = new Dictionary<(int, int, int), int>();
            var pointInner = new List<bool>();
            var pointRegion = new List<bool>();
            for(int v = 0; v < count; v++)
            {
                if(lipW[v] < 0.04f && !region[v] && !collide[v])
                    continue;
                int side = SideOf(v);
                // Upper or lower lip: where the lips meet, the two lips' vertices can be at the very same spot; they
                // must stay apart (they open different ways).
                var key = (keyOf[v].Item1 * 4 + side + 1, keyOf[v].Item2, keyOf[v].Item3);
                if(!byPosition.TryGetValue(key, out int g))
                {
                    g = points.Count;
                    byPosition[key] = g;
                    var b = new List<int>();
                    var wt = new List<float>();
                    for(int k = 0; k < skin.Slots; k++)
                    {
                        float wk = skin.Weights[v * 8 + k];
                        int slot = skin.Indices[v * 8 + k];
                        if(wk <= 0f || slot >= skin.Bones.Length)
                            continue;
                        b.Add(Slot(skin.Bones[slot]));
                        wt.Add(wk);
                    }
                    points.Add(new Point { Rest = pos[v], Bone = b.ToArray(), Weight = wt.ToArray(), Mask = Math.Min(1f, lipW[v] * 2.2f), Side = side });
                    members.Add(new List<int>());
                    pointInner.Add(false);
                    pointRegion.Add(false);
                }
                pointOf[v] = g;
                members[g].Add(v);
                pointInner[g] |= inner[v];
                pointRegion[g] |= region[v];
            }
            if(points.Count < 12)
                continue;
            var links = new HashSet<(int, int)>();
            var tris = new List<int>();
            var normals = new Vector3[points.Count];
            var border = new bool[points.Count];
            for(int i = 0; i + 2 < t.Length; i += 3)
            {
                int va = t[i], vb = t[i + 1], vc = t[i + 2];
                if(va >= count || vb >= count || vc >= count)
                    continue;
                int a = pointOf[va], b = pointOf[vb], c = pointOf[vc];
                // A region point next to anything outside the region is on its border (held still).
                bool ra = a >= 0 && pointRegion[a], rb = b >= 0 && pointRegion[b], rc = c >= 0 && pointRegion[c];
                if(ra != rb || rb != rc)
                {
                    if(ra) border[a] = true;
                    if(rb) border[b] = true;
                    if(rc) border[c] = true;
                }
                foreach(var (x, y) in new[] { (a, b), (b, c), (c, a) })
                    if(x >= 0 && y >= 0 && x != y)
                        links.Add(x < y ? (x, y) : (y, x));
                if(a < 0 || b < 0 || c < 0 || a == b || b == c || a == c)
                    continue;
                tris.Add(a); tris.Add(b); tris.Add(c);
                var face = Vector3.Cross(points[b].Rest - points[a].Rest, points[c].Rest - points[a].Rest);
                normals[a] += face; normals[b] += face; normals[c] += face;
            }
            var near = points.Select(_ => new List<int>()).ToArray();
            foreach(var (x, y) in links)
            {
                near[x].Add(y);
                near[y].Add(x);
            }
            for(int g = 0; g < points.Count; g++)
            {
                points[g].Members = members[g].ToArray();
                points[g].Neighbours = near[g].ToArray();
                points[g].RestNormal = normals[g].LengthSquared() > 1e-20f ? Vector3.Normalize(normals[g]) : Vector3.UnitZ;
            }
            var built = new Mesh { Layout = layout, Points = points.ToArray(), Triangles = tris.ToArray() };
            if(wrap)
                built.Wrap = BuildWrap(built, centre, half, scale, pointInner, pointRegion, border);
            meshes.Add(built);
        }
        if(meshes.Count == 0)
            return null;
        return new State { Target = target, Original = original, Work = (byte[])original.Clone(), Meshes = meshes, Bones = bones.ToArray(), FileData = data };
    }

    private static ushort[] WithShapes(ushort[] triangles, byte[] data, int lod, int mesh)
    {
        List<(string Name, (int Index, int Vertex)[] Values)> shapes;
        try
        {
            shapes = MdlRigger.MeshShapes(data, lod, mesh);
        }
        catch
        {
            return triangles;
        }
        if(shapes.Count == 0)
            return triangles;
        var all = new List<ushort>(triangles);
        foreach(var (_, values) in shapes)
        {
            var swap = new Dictionary<int, int>();
            foreach(var (index, vertex) in values)
                swap[index] = vertex;
            var touched = new HashSet<int>();
            foreach(var index in swap.Keys)
                touched.Add(index / 3);
            foreach(int tri in touched)
            {
                if(tri * 3 + 2 >= triangles.Length)
                    continue;
                for(int k = 0; k < 3; k++)
                {
                    int at = tri * 3 + k;
                    all.Add((ushort)(swap.TryGetValue(at, out int v) ? v : triangles[at]));
                }
            }
        }
        return all.ToArray();
    }

    // ---- Every frame (render thread)

    public static void Update()
    {
        var state = _state;
        if(state == null)
            return;
        try
        {
            var buffer = state.Replacement != 0 ? state.Replacement : state.Target.Buffer;
            if(!LiveMesh.StillHolds(state.Target, buffer))
            {
                // Redrawn (new face, gear change...): let it go; Oral starts it again on the new model.
                _state = null;
                Status = "Lips: the face was redrawn; starting again...";
                return;
            }
            var frame = _frame;
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            float dt = state.LastTicks == 0 ? 1f / 60f : Math.Clamp((float)((now - state.LastTicks) / (double)System.Diagnostics.Stopwatch.Frequency), 1f / 240f, 0.1f);
            state.LastTicks = now;
            if(frame == null)
            {
                // Nothing to fit to: ease back, then the model's own vertices once.
                if(!state.Clean)
                {
                    bool any = false;
                    foreach(var mesh in state.Meshes)
                        foreach(var p in mesh.Points)
                        {
                            p.Offset *= MathF.Max(0f, 1f - dt * 10f);
                            p.RestOffset *= MathF.Max(0f, 1f - dt * 10f);
                            any |= p.Offset.LengthSquared() > 1e-10f || p.RestOffset.LengthSquared() > 1e-10f;
                        }
                    if(!any)
                    {
                        foreach(var mesh in state.Meshes)
                            foreach(var p in mesh.Points)
                            {
                                p.Offset = Vector3.Zero;
                                p.RestOffset = Vector3.Zero;
                            }
                        LiveMesh.Upload(buffer, state.Target.Desc, state.Original);
                        state.Clean = true;
                    }
                    else
                        Write(state, buffer, state.LastFrame as Frame);
                }
                return;
            }
            if(ReferenceEquals(frame, state.LastFrame))
                return;
            state.LastFrame = frame;
            foreach(var mesh in state.Meshes)
            {
                if(mesh.Wrap != null)
                    FitWrap(mesh, frame, dt);
                else
                    Fit(mesh, frame, dt);
            }
            Write(state, buffer, frame);
            state.Clean = false;
        }
        catch(Exception e)
        {
            Status = "Lips: " + e.Message;
            Stop();
        }
    }

    // Where each lip point goes this frame: a cage fit (the way a lattice / shrinkwrap-with-offset rig does it by
    // hand). Round the shaft, in slices of angle, the lip's inner edge is found (its point nearest the shaft); the
    // move that puts that edge on the shaft's surface is worked out per slice, and every lip point in the slice
    // moves by it: the lip keeps its whole thickness (inner and outer edge go together) instead of being flattened
    // onto the surface, and the corners (where the mouth is wider) close in on their own. Faded out toward the
    // cheeks, chin and nose, and in front of / behind the lips.
    private const int Slices = 48;

    // ---- The lips wrapped onto the shaft

    /// <summary>
    /// Made once (rest pose: +Z out of the face, +Y up). The surfaces where the two lips touch when the mouth is
    /// closed are what will touch the shaft: each gets its place round it, the way a slit opens into a circle (across
    /// the mouth, corner to corner, becomes round the circle: the upper lip over the top, the lower lip under it,
    /// the corners at the sides). The rest of the lips and the skin round them (out to a border that stays put)
    /// follow by a smooth solve each frame: every point the average of its neighbours, so the lips keep their
    /// fullness and the skin stretches evenly instead of creasing.
    /// </summary>
    private static Wrap? BuildWrap(Mesh mesh, Vector3 centre, float half, float scale, List<bool> inner, List<bool> region, bool[] border)
    {
        var points = mesh.Points;
        int n = points.Length;
        var innerList = new List<int>();
        for(int g = 0; g < n; g++)
            if(inner[g] && region[g] && points[g].Side != 0)
                innerList.Add(g);
        if(innerList.Count < 8)
            return null;
        var w = new Wrap { Centre = centre, Half = half, Inner = innerList.ToArray() };
        w.Theta = new float[w.Inner.Length];
        w.Behind = new float[w.Inner.Length];
        // Each lip's front rim (the most forward touching point), across the mouth in bins: the rims all go on one
        // flat ring (from the side the mouth is a straight line across the shaft, not the face's curve).
        const int Bins = 10;
        var rim = new float[2, Bins];
        for(int s = 0; s < 2; s++)
            for(int b = 0; b < Bins; b++)
                rim[s, b] = float.NaN;
        int BinOf(float x) => Math.Clamp((int)((x - centre.X + half) / (2f * half) * Bins), 0, Bins - 1);
        float ringZ = float.MinValue;
        foreach(int g in w.Inner)
        {
            int s = points[g].Side > 0 ? 0 : 1, b = BinOf(points[g].Rest.X);
            float z = points[g].Rest.Z;
            rim[s, b] = float.IsNaN(rim[s, b]) ? z : MathF.Max(rim[s, b], z);
            ringZ = MathF.Max(ringZ, z);
        }
        w.RingZ = ringZ;
        for(int i = 0; i < w.Inner.Length; i++)
        {
            var p = points[w.Inner[i]];
            float x = Math.Clamp((p.Rest.X - centre.X) / (half * 1.05f), -1f, 1f);
            w.Theta[i] = MathF.Acos(x) * (p.Side > 0 ? 1f : -1f);
            float r = rim[p.Side > 0 ? 0 : 1, BinOf(p.Rest.X)];
            w.Behind[i] = float.IsNaN(r) ? 0f : p.Rest.Z - r;
        }
        // The points that follow: in the area, not on its border, not touching points.
        var innerIndex = new int[n];
        Array.Fill(innerIndex, -1);
        for(int i = 0; i < w.Inner.Length; i++)
            innerIndex[w.Inner[i]] = i;
        var freeIndex = new int[n];
        Array.Fill(freeIndex, -1);
        var free = new List<int>();
        for(int g = 0; g < n; g++)
            if(region[g] && !border[g] && innerIndex[g] < 0)
            {
                freeIndex[g] = free.Count;
                free.Add(g);
            }
        w.Free = free.ToArray();
        w.FreeNear = new int[free.Count][];
        w.InnerNear = new int[free.Count][];
        w.Degree = new float[free.Count];
        for(int i = 0; i < free.Count; i++)
        {
            var nb = points[free[i]].Neighbours;
            w.Degree[i] = nb.Length + 1e-4f;
            w.FreeNear[i] = nb.Where(o => freeIndex[o] >= 0).Select(o => freeIndex[o]).ToArray();
            w.InnerNear[i] = nb.Where(o => innerIndex[o] >= 0).Select(o => innerIndex[o]).ToArray();
        }
        w.Solved = new Vector3[free.Count];
        // The lips and the skin right round them push forward together (evenly, so the front stays flat).
        w.PoutWeight = new float[n];
        w.CheekWeight = new float[n];
        // Inside the mouth: behind the lips' front, facing in toward the mouth's middle. Inside the shaft there it
        // can't be seen (the shaft hides it); pushed out, it would show round the lips' rim as ragged bits.
        w.Interior = new bool[n];
        for(int g = 0; g < n; g++)
        {
            var p = points[g];
            var radial = new Vector3(p.Rest.X - centre.X, p.Rest.Y - centre.Y, 0f);
            if(radial.LengthSquared() < 1e-12f)
                continue;
            w.Interior[g] = Vector3.Dot(p.RestNormal, Vector3.Normalize(radial)) < -0.2f && p.Rest.Z < w.RingZ - 0.003f * scale;
        }

        // The lip band: the lips themselves (mostly lip) carried round with their own lip's rim, interpolated between
        // the two nearest rim points: their offset from the rim turned with it (across the mouth -> along the ring,
        // up / down from the rim -> outward from the shaft, depth -> along the shaft), so each lip keeps its whole
        // thickness and shape round the shaft instead of being squeezed by the solve.
        var rims = new Dictionary<int, List<int>> { [1] = new(), [-1] = new() };
        foreach(int side in new[] { 1, -1 })
        {
            for(int b = 0; b < Bins; b++)
            {
                int bestI = -1;
                for(int i = 0; i < w.Inner.Length; i++)
                {
                    var p = points[w.Inner[i]];
                    if(p.Side != side || BinOf(p.Rest.X) != b)
                        continue;
                    if(bestI < 0 || p.Rest.Z > points[w.Inner[bestI]].Rest.Z)
                        bestI = i;
                }
                if(bestI >= 0)
                    rims[side].Add(bestI);
            }
            rims[side].Sort((a, b) => points[w.Inner[a]].Rest.X.CompareTo(points[w.Inner[b]].Rest.X));
        }
        var band = new List<int>();
        var bandRim = new List<(int, int, float)>();
        var bandSide = new List<int>();
        var isFree = new bool[n];
        foreach(int g in w.Free)
            isFree[g] = true;
        for(int g = 0; g < n; g++)
        {
            var p = points[g];
            if(!isFree[g] || p.Side == 0 || p.Mask < 0.9f)
                continue;
            var rs = rims[p.Side];
            if(rs.Count == 0)
                continue;
            int j = 1;
            while(j < rs.Count - 1 && points[w.Inner[rs[j]]].Rest.X < p.Rest.X)
                j++;
            int a = rs[Math.Max(0, j - 1)], c = rs[Math.Min(j, rs.Count - 1)];
            float xa = points[w.Inner[a]].Rest.X, xc = points[w.Inner[c]].Rest.X;
            float t = xc - xa > 1e-6f ? Math.Clamp((p.Rest.X - xa) / (xc - xa), 0f, 1f) : 0f;
            band.Add(g);
            bandRim.Add((a, c, t));
            bandSide.Add(p.Side);
        }
        w.Band = band.ToArray();
        w.BandRim = bandRim.ToArray();
        w.BandSide = bandSide.ToArray();
        if(w.Band.Length > 0)
        {
            // The band points are placed, not solved: the free points are the rest.
            var bandIndex = new int[n];
            Array.Fill(bandIndex, -1);
            for(int i = 0; i < w.Band.Length; i++)
                bandIndex[w.Band[i]] = i;
            var keep = w.Free.Where(g => bandIndex[g] < 0).ToArray();
            Array.Fill(freeIndex, -1);
            for(int i = 0; i < keep.Length; i++)
                freeIndex[keep[i]] = i;
            w.Free = keep;
            w.FreeNear = new int[keep.Length][];
            w.InnerNear = new int[keep.Length][];
            w.BandNear = new int[keep.Length][];
            w.Degree = new float[keep.Length];
            for(int i = 0; i < keep.Length; i++)
            {
                var nb = points[keep[i]].Neighbours;
                w.Degree[i] = nb.Length + 1e-4f;
                w.FreeNear[i] = nb.Where(o => freeIndex[o] >= 0).Select(o => freeIndex[o]).ToArray();
                w.InnerNear[i] = nb.Where(o => innerIndex[o] >= 0).Select(o => innerIndex[o]).ToArray();
                w.BandNear[i] = nb.Where(o => bandIndex[o] >= 0).Select(o => bandIndex[o]).ToArray();
            }
            w.Solved = new Vector3[keep.Length];
        }
        else
            w.BandNear = new int[w.Free.Length][].Select(_ => Array.Empty<int>()).ToArray();
        static float Bump(float x, float width) => MathF.Pow(Math.Clamp(1f - x * x / (width * width), 0f, 1f), 2f);
        for(int g = 0; g < n; g++)
        {
            if(!region[g] || border[g])
                continue;
            var d = points[g].Rest - centre;
            float rho = MathF.Sqrt(d.X * d.X + d.Y * d.Y * 2.56f) / scale;
            w.PoutWeight[g] = Math.Clamp(1f - (rho - 0.02f) / 0.025f, 0f, 1f) * Math.Clamp(points[g].Mask * 0.68f, 0f, 1f);
            // The cheeks: beside the mouth, a little below it (not the lips themselves).
            w.CheekWeight[g] = Bump(MathF.Abs(d.X) - 0.042f * scale, 0.022f * scale) * Bump(d.Y + 0.008f * scale, 0.028f * scale)
                               * (1f - Math.Clamp(points[g].Mask * 0.68f, 0f, 1f)) * (d.X < 0f ? -1f : 1f);
        }
        return w;
    }

    // Every frame: the touching points onto the shaft (round it, on one flat ring, as far as the shaft is in), the
    // rest by the smooth solve (a few steps from last frame's answer), pushed forward a little; in the rest space
    // (like a blend shape), eased in time.
    private static void FitWrap(Mesh mesh, Frame frame, float dt)
    {
        var points = mesh.Points;
        var w = mesh.Wrap!;
        int n = points.Length, m = w.Inner.Length;
        var posed = new Vector3[m];
        var toRest = new Matrix4x4[m];
        var average = new Matrix4x4();
        var mouth = Vector3.Zero;
        for(int i = 0; i < m; i++)
        {
            posed[i] = Skin(points[w.Inner[i]], frame.Skinning, out var b);
            average += b;
            mouth += posed[i];
            b.M41 = b.M42 = b.M43 = 0f;
            b.M44 = 1f;
            toRest[i] = Matrix4x4.Invert(b, out var inv) ? inv : Matrix4x4.Identity;
        }
        average *= 1f / m;
        mouth /= m;
        float open = 0f, radius = 0f, scale = 1f;
        var axisOut = Vector3.UnitZ;
        var core = mouth;
        if(frame.Shaft.Length >= 2 && Closest(frame.Shaft, frame.Radius, mouth, out core, out radius, out var along))
        {
            axisOut = -along; // out of the mouth, toward the base
            scale = MathF.Max(1e-3f, Vector3.TransformNormal(Vector3.UnitX, average).Length());
            float tipIn = -Vector3.Dot(frame.Shaft[^1] - mouth, axisOut);
            float inside = Math.Clamp(tipIn / 0.008f, 0f, 1f);
            float off = Vector3.Distance(mouth, core);
            float near = Math.Clamp(1f - (off - w.Half * scale) / (w.Half * scale), 0f, 1f);
            open = inside * near * Math.Clamp(radius / 0.006f, 0f, 1f);
        }
        float seal = Math.Clamp(frame.Seal, 0f, 1f);
        // Round the shaft: "side" is the face's own left-right, "up" square to it and the shaft.
        var sideRef = Vector3.TransformNormal(Vector3.UnitX, average);
        var ring = Vector3.Transform(new Vector3(w.Centre.X, w.Centre.Y, w.RingZ), average);
        float ringAlong = Vector3.Dot(ring - core, axisOut);
        var moveInner = new Vector3[m];
        // (each touching point's target and its frame round the shaft: the lip band rides on these)
        var target = new Vector3[m];
        var radialAt = new Vector3[m];
        var tangentAt = new Vector3[m];
        var axisAt = new Vector3[m];
        float err = 0f;
        if(open > 0f)
            for(int i = 0; i < m; i++)
            {
                float at = ringAlong + w.Behind[i] * scale + frame.Forward + frame.Drag;
                if(!Closest(frame.Shaft, frame.Radius, core + axisOut * at, out var c, out float r, out var a))
                    continue;
                var outward = -a;
                var side = sideRef - outward * Vector3.Dot(sideRef, outward);
                side = side.LengthSquared() > 1e-10f ? Vector3.Normalize(side) : Vector3.UnitX;
                var up = Vector3.Cross(outward, side);
                // On the shaft's surface (a hair inside it with Lip seal: sealed, no gap), at its own depth on the ring.
                var radial = MathF.Cos(w.Theta[i]) * frame.OpenWidth * side + MathF.Sin(w.Theta[i]) * frame.OpenHeight * up;
                target[i] = c + radial * r * (1.01f - 0.05f * seal);
                radialAt[i] = Vector3.Normalize(radial);
                // (along the ring the way "across the mouth" goes: to the face's right on both lips)
                tangentAt[i] = (w.Theta[i] >= 0f ? 1f : -1f) * (MathF.Sin(w.Theta[i]) * side - MathF.Cos(w.Theta[i]) * up);
                axisAt[i] = outward;
                var to = posed[i] + (target[i] - posed[i]) * open;
                moveInner[i] = Vector3.TransformNormal(to - posed[i], toRest[i]);
                err += MathF.Abs(Vector3.Distance(posed[i], c) - r) / m;
            }
        // The lip band: each lip point rides round on its rim (between the two nearest rim points), its offset from
        // the rim turned with it.
        var moveBand = new Vector3[w.Band.Length];
        if(open > 0f)
            for(int k = 0; k < w.Band.Length; k++)
            {
                int g = w.Band[k];
                var (ia, ib, t) = w.BandRim[k];
                var restAnchor = Vector3.Lerp(points[w.Inner[ia]].Rest, points[w.Inner[ib]].Rest, t);
                var anchor = Vector3.Lerp(target[ia], target[ib], t);
                var rad = Vector3.Lerp(radialAt[ia], radialAt[ib], t);
                var tan = Vector3.Lerp(tangentAt[ia], tangentAt[ib], t);
                var ax = Vector3.Lerp(axisAt[ia], axisAt[ib], t);
                if(rad.LengthSquared() < 1e-10f || tan.LengthSquared() < 1e-10f || ax.LengthSquared() < 1e-10f)
                    continue;
                rad = Vector3.Normalize(rad);
                tan = Vector3.Normalize(tan);
                ax = Vector3.Normalize(ax);
                var o = points[g].Rest - restAnchor;
                var goal = anchor + (tan * o.X + rad * (o.Y * w.BandSide[k]) + ax * o.Z) * scale;
                var cur = SkinAt(points[g], points[g].Rest, frame.Skinning, out var bm);
                bm.M41 = bm.M42 = bm.M43 = 0f;
                bm.M44 = 1f;
                if(!Matrix4x4.Invert(bm, out var inv))
                    continue;
                moveBand[k] = Vector3.TransformNormal((goal - cur) * open, inv);
            }

        // The smooth solve (conjugate gradients on the mesh's own neighbour average), from last frame's answer.
        int f = w.Free.Length;
        var x = w.Solved;
        var rhs = new Vector3[f];
        for(int i = 0; i < f; i++)
        {
            foreach(int k in w.InnerNear[i])
                rhs[i] += moveInner[k];
            foreach(int k in w.BandNear[i])
                rhs[i] += moveBand[k];
        }
        Vector3 Apply(Vector3[] v, int i)
        {
            var sum = v[i] * w.Degree[i];
            foreach(int o in w.FreeNear[i])
                sum -= v[o];
            return sum;
        }
        var res = new Vector3[f];
        var dir = new Vector3[f];
        var ad = new Vector3[f];
        var rr = Vector3.Zero;
        for(int i = 0; i < f; i++)
        {
            res[i] = rhs[i] - Apply(x, i);
            dir[i] = res[i];
            rr += res[i] * res[i];
        }
        for(int iteration = 0; iteration < 60 && MathF.Max(rr.X, MathF.Max(rr.Y, rr.Z)) > 1e-14f; iteration++)
        {
            var dAd = Vector3.Zero;
            for(int i = 0; i < f; i++)
            {
                ad[i] = Apply(dir, i);
                dAd += dir[i] * ad[i];
            }
            var alpha = new Vector3(dAd.X > 1e-20f ? rr.X / dAd.X : 0f, dAd.Y > 1e-20f ? rr.Y / dAd.Y : 0f, dAd.Z > 1e-20f ? rr.Z / dAd.Z : 0f);
            var next = Vector3.Zero;
            for(int i = 0; i < f; i++)
            {
                x[i] += alpha * dir[i];
                res[i] -= alpha * ad[i];
                next += res[i] * res[i];
            }
            var beta = new Vector3(rr.X > 1e-30f ? next.X / rr.X : 0f, rr.Y > 1e-30f ? next.Y / rr.Y : 0f, rr.Z > 1e-30f ? next.Z / rr.Z : 0f);
            for(int i = 0; i < f; i++)
                dir[i] = res[i] + beta * dir[i];
            rr = next;
        }
        var want = new Vector3[n];
        for(int i = 0; i < m; i++)
            want[w.Inner[i]] = moveInner[i];
        for(int i = 0; i < f; i++)
            want[w.Free[i]] = MathUtil.IsFinite(x[i]) ? x[i] : Vector3.Zero;
        for(int k = 0; k < w.Band.Length; k++)
            want[w.Band[k]] = MathUtil.IsFinite(moveBand[k]) ? moveBand[k] : Vector3.Zero;
        for(int i = 0; i < f; i++)
            if(!MathUtil.IsFinite(x[i]))
                x[i] = Vector3.Zero;
        // Pushed forward with the lips; cheeks filled out.
        float pout = frame.Forward * 1.6f * open;
        for(int g = 0; g < n; g++)
        {
            float cheek = frame.CheekPuff * open * w.CheekWeight[g];
            want[g] += new Vector3(cheek, 0f, pout * w.PoutWeight[g] + 0.4f * MathF.Abs(cheek));
        }
        // No clipping: anything of the face (nose, chin, cheeks, the inside of the mouth) that would end up inside the
        // shaft is pushed out onto its surface, and the push spread a little onto its neighbours (a smooth dent or
        // bulge, no spikes). Twice, as a push can move a neighbour in.
        if(open > 0f && frame.Shaft.Length >= 2)
        {
            var isInner = new bool[n];
            foreach(int g in w.Inner)
                isInner[g] = true;
            var push = new Vector3[n];
            var hit = new bool[n];
            for(int round = 0; round < 2; round++)
            {
                Array.Clear(push);
                Array.Clear(hit);
                bool anyHit = false;
                for(int g = 0; g < n; g++)
                {
                    if(isInner[g] || w.Interior[g])
                        continue;
                    var p = points[g];
                    var at = SkinAt(p, p.Rest + want[g], frame.Skinning, out var b);
                    if(!Closest(frame.Shaft, frame.Radius, at, out var c, out float r, out _))
                        continue;
                    var away = at - c;
                    // (just inside the surface, like the lips' own edge: no step between them)
                    float dist = away.Length(), limit = r * 0.985f;
                    if(dist >= limit)
                        continue;
                    var outward = dist > 1e-6f ? away / dist : axisOut;
                    b.M41 = b.M42 = b.M43 = 0f;
                    b.M44 = 1f;
                    if(!Matrix4x4.Invert(b, out var inv))
                        continue;
                    push[g] = Vector3.TransformNormal(outward * (limit - dist), inv);
                    hit[g] = anyHit = true;
                }
                if(!anyHit)
                    break;
                var spread = new Vector3[n];
                for(int pass = 0; pass < 3; pass++)
                {
                    Array.Copy(push, spread, n);
                    for(int g = 0; g < n; g++)
                    {
                        if(hit[g] || isInner[g])
                            continue;
                        var nb = points[g].Neighbours;
                        if(nb.Length == 0)
                            continue;
                        var mean = Vector3.Zero;
                        foreach(int o in nb)
                            mean += push[o];
                        mean *= 0.6f / nb.Length;
                        if(mean.LengthSquared() > push[g].LengthSquared())
                            spread[g] = mean;
                    }
                    (push, spread) = (spread, push);
                }
                for(int g = 0; g < n; g++)
                    if(MathUtil.IsFinite(push[g]) && push[g].LengthSquared() < 0.04f * 0.04f)
                        want[g] += push[g];
            }
        }
        float ease = MathF.Min(1f, dt * 20f);
        for(int g = 0; g < n; g++)
        {
            var o = points[g].RestOffset + (want[g] - points[g].RestOffset) * ease;
            points[g].RestOffset = MathUtil.IsFinite(o) && o.LengthSquared() < 0.05f * 0.05f ? o : Vector3.Zero;
            points[g].Offset = Vector3.Zero;
        }
        w.Open = open;
        w.Radius = radius / scale;
        w.Error = err;
        long now = Environment.TickCount64;
        if(now >= _nextStatus)
        {
            _nextStatus = now + 400;
            Status = _baseStatus + (open > 0.05f
                ? $" Now: shaft {w.Radius * 200f:0.0} cm thick, opened {open * 100f:0}%."
                : " Now: closed (no shaft in the mouth).");
        }
    }

    private static void Fit(Mesh mesh, Frame frame, float dt)
    {
        var points = mesh.Points;
        int n = points.Length;
        if(n == 0 || frame.Shaft.Length < 2)
            return;
        var posed = new Vector3[n];
        var core = new Vector3[n];
        var dir = new Vector3[n];
        var gap = new float[n];  // how far the point is outside the shaft's surface (negative: inside it)
        var valid = new bool[n];
        const float Margin = 0.0008f;
        // The mouth's middle (by how much of each point is lip), and the shaft there.
        var centre = Vector3.Zero;
        float weight = 0f;
        for(int g = 0; g < n; g++)
        {
            posed[g] = Skin(points[g], frame.Skinning, out _);
            centre += posed[g] * points[g].Mask;
            weight += points[g].Mask;
        }
        if(weight <= 0f)
            return;
        centre /= weight;
        if(!Closest(frame.Shaft, frame.Radius, centre, out var middle, out _, out var axis))
            return;
        // Angles round the shaft: from a fixed "up" across it.
        var u = Vector3.UnitY - axis * Vector3.Dot(Vector3.UnitY, axis);
        if(u.LengthSquared() < 1e-6f)
            u = Vector3.UnitX - axis * Vector3.Dot(Vector3.UnitX, axis);
        u = Vector3.Normalize(u);
        var v = Vector3.Cross(axis, u);

        float near = 0.004f + 0.02f * Math.Clamp(frame.Seal, 0f, 1f); // how far an inner edge may be pulled in
        var inner = new float[Slices];
        Array.Fill(inner, float.NaN);
        var slice = new float[n];
        var along = new float[n];
        for(int g = 0; g < n; g++)
        {
            if(!Closest(frame.Shaft, frame.Radius, posed[g], out var c, out float radius, out _))
                continue;
            var away = posed[g] - c;
            float dist = away.Length();
            if(dist < 1e-6f)
                continue;
            core[g] = c;
            dir[g] = away / dist;
            gap[g] = dist - (radius + Margin);
            valid[g] = true;
            var fromMiddle = posed[g] - middle;
            along[g] = Vector3.Dot(fromMiddle, axis);
            float angle = MathF.Atan2(Vector3.Dot(fromMiddle, v), Vector3.Dot(fromMiddle, u));
            slice[g] = (angle / (MathF.PI * 2f) + 1f) % 1f * Slices;
            // The inner edge: the lip's own points (mostly lip), near the lips' plane.
            if(points[g].Mask > 0.5f && MathF.Abs(along[g]) < 0.015f)
            {
                int s = (int)slice[g] % Slices;
                if(float.IsNaN(inner[s]) || gap[g] < inner[s])
                    inner[s] = gap[g];
            }
        }
        // Slices with no lip point: from their neighbours (round the circle).
        bool any = false;
        foreach(var x in inner)
            any |= !float.IsNaN(x);
        if(!any)
            return;
        for(int pass = 0; pass < Slices; pass++)
        {
            bool filled = true;
            for(int s = 0; s < Slices; s++)
            {
                if(!float.IsNaN(inner[s]))
                    continue;
                float a = inner[(s + Slices - 1) % Slices], b = inner[(s + 1) % Slices];
                if(!float.IsNaN(a) || !float.IsNaN(b))
                    inner[s] = float.IsNaN(a) ? b : float.IsNaN(b) ? a : (a + b) * 0.5f;
                else
                    filled = false;
            }
            if(filled)
                break;
        }
        // Each slice's move: its inner edge onto the surface (in, where there's a gap, as far as the seal reaches;
        // out, where the shaft would go through the lip). Smoothed round the circle so there are no steps.
        var move = new float[Slices];
        for(int s = 0; s < Slices; s++)
        {
            float g0 = inner[s];
            move[s] = g0 < 0f ? -g0 : g0 <= near ? -g0 : -near * (1f - Math.Clamp((g0 - near) / 0.02f, 0f, 1f));
        }
        var smooth = new float[Slices];
        for(int pass = 0; pass < 2; pass++)
        {
            for(int s = 0; s < Slices; s++)
                smooth[s] = move[(s + Slices - 1) % Slices] * 0.25f + move[s] * 0.5f + move[(s + 1) % Slices] * 0.25f;
            (move, smooth) = (smooth, move);
        }
        // Every point by its slice's move (blended between slices), faded by how much of it is lip and how far it is
        // from the lips' plane; never left inside the shaft.
        var want = new Vector3[n];
        for(int g = 0; g < n; g++)
        {
            if(!valid[g])
                continue;
            float at = slice[g] - 0.5f;
            int s0 = ((int)MathF.Floor(at) % Slices + Slices) % Slices, s1 = (s0 + 1) % Slices;
            float t = at - MathF.Floor(at);
            float m = move[s0] + (move[s1] - move[s0]) * t;
            float lip = Math.Clamp(points[g].Mask, 0f, 1f);
            lip = lip * lip * (3f - 2f * lip);
            float plane = Math.Clamp(1f - (MathF.Abs(along[g]) - 0.012f) / 0.015f, 0f, 1f);
            float d = m * lip * plane;
            if(gap[g] + d < 0f)
                d = -gap[g]; // out onto the surface, at least
            want[g] = dir[g] * d;
        }
        float ease = MathF.Min(1f, dt * 25f);
        for(int g = 0; g < n; g++)
        {
            var o = points[g].Offset + (want[g] - points[g].Offset) * ease;
            points[g].Offset = MathUtil.IsFinite(o) && o.LengthSquared() < 0.03f * 0.03f ? o : Vector3.Zero;
        }
    }

    private static Vector3 Skin(Point p, Matrix4x4[] m, out Matrix4x4 blended) => SkinAt(p, p.Rest, m, out blended);

    private static Vector3 SkinAt(Point p, Vector3 rest, Matrix4x4[] m, out Matrix4x4 blended)
    {
        var q = Vector3.Zero;
        float total = 0f;
        blended = default;
        for(int b = 0; b < p.Bone.Length; b++)
        {
            if(p.Bone[b] >= m.Length)
                continue;
            q += Vector3.Transform(rest, m[p.Bone[b]]) * p.Weight[b];
            blended += m[p.Bone[b]] * p.Weight[b];
            total += p.Weight[b];
        }
        if(total <= 0f)
        {
            blended = Matrix4x4.Identity;
            return rest;
        }
        blended *= 1f / total;
        return q / total;
    }

    // The nearest point on the shaft's middle line, its thickness there and the line's direction there.
    private static bool Closest(Vector3[] line, float[] radius, Vector3 x, out Vector3 core, out float r, out Vector3 along)
    {
        core = default;
        r = 0f;
        along = Vector3.UnitZ;
        float best = float.MaxValue;
        for(int i = 1; i < line.Length; i++)
        {
            var a = line[i - 1];
            var d = line[i] - a;
            float len2 = d.LengthSquared();
            float t = len2 > 1e-12f ? Math.Clamp(Vector3.Dot(x - a, d) / len2, 0f, 1f) : 0f;
            var c = a + d * t;
            float dist = Vector3.DistanceSquared(x, c);
            if(dist < best)
            {
                best = dist;
                core = c;
                r = radius[i - 1] + (radius[i] - radius[i - 1]) * t;
                if(len2 > 1e-12f)
                    along = d / MathF.Sqrt(len2);
            }
        }
        return best < float.MaxValue;
    }

    // The offsets back into the model's rest space (what the vertex buffer holds), normals turned to match.
    private static void Write(State state, nint buffer, Frame? frame)
    {
        if(frame == null)
            return;
        Buffer.BlockCopy(state.Original, 0, state.Work, 0, state.Original.Length);
        foreach(var mesh in state.Meshes)
        {
            var layout = mesh.Layout;
            var points = mesh.Points;
            var local = new Vector3[points.Length];
            bool any = false;
            for(int g = 0; g < points.Length; g++)
            {
                var p = points[g];
                if(p.RestOffset.LengthSquared() > 1e-12f)
                    local[g] = p.RestOffset; // an open-mouth shape: already in rest space, like a blend shape
                else
                {
                    if(p.Offset.LengthSquared() < 1e-12f)
                        continue;
                    Skin(p, frame.Skinning, out var blended);
                    blended.M41 = blended.M42 = blended.M43 = 0f;
                    blended.M44 = 1f;
                    if(!Matrix4x4.Invert(blended, out var inverse))
                        inverse = Matrix4x4.Identity;
                    local[g] = Vector3.TransformNormal(p.Offset, inverse);
                }
                any = true;
                var at = p.Rest + local[g];
                foreach(var v in p.Members)
                {
                    int pa = layout.PositionAt + v * layout.PositionStride;
                    if(pa + 12 <= state.Work.Length)
                        MdlRigger.WriteVector(state.Work, pa, layout.PositionType, at);
                }
            }
            if(!any || layout.NormalAt < 0 || layout.NormalType is not (2 or 3 or 14))
                continue;
            var turned = new Vector3[points.Length];
            var t = mesh.Triangles;
            for(int i = 0; i + 2 < t.Length; i += 3)
            {
                int a = t[i], b = t[i + 1], c = t[i + 2];
                var face = Vector3.Cross(points[b].Rest + local[b] - points[a].Rest - local[a], points[c].Rest + local[c] - points[a].Rest - local[a]);
                turned[a] += face; turned[b] += face; turned[c] += face;
            }
            for(int g = 0; g < points.Length; g++)
            {
                if(local[g].LengthSquared() < 1e-12f || turned[g].LengthSquared() < 1e-20f)
                    continue;
                var rot = MathUtil.FromTo(points[g].RestNormal, Vector3.Normalize(turned[g]));
                foreach(var v in points[g].Members)
                {
                    int na = layout.NormalAt + v * layout.NormalStride;
                    if(na + 12 > state.Work.Length)
                        continue;
                    var normal = MdlRigger.ReadVector(state.Original, na, layout.NormalType);
                    if(normal.LengthSquared() < 1e-12f)
                        continue;
                    MdlRigger.WriteVector(state.Work, na, layout.NormalType, Vector3.Transform(normal, rot));
                }
            }
        }
        LiveMesh.Upload(buffer, state.Target.Desc, state.Work);
    }
}
