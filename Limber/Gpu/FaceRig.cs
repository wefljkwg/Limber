using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Limber.BodyRig;

namespace Limber.Gpu;

/// <summary>
/// The live face rig: smooth control points laid over the face model that's drawn (any race, any face mod: it reads
/// the face's own mesh and finds its features from the face bones' weights), moving the skin like blend shapes.
///
/// Controls (in the face's rest space: +X the character's left, +Y up, +Z forward):
///   7 along the upper lip and 7 along the lower lip (the corners at the ends), 3 along each brow (inner to outer),
///   3 along each upper and each lower eyelid, and one on each cheek.
/// Each vertex follows the controls near it, blended smoothly along the feature (so a lip bends as a curve, not in
/// steps between bones) and fading into the skin around it. Teeth, tongue and eyeballs are left alone.
///
/// Per frame (render thread) the offsets handed over are added to the face's rest vertices and written into its
/// vertex buffer; the game then skins them with the face bones as usual. Only on this PC; nothing is sent anywhere.
/// </summary>
public static unsafe class FaceRig
{
    // ---- The controls
    public const int LipCount = 7;          // per lip, right corner (-X) to left corner (+X)
    public const int BrowCount = 3;         // per brow, inner to outer
    public const int LidCount = 3;          // per lid, inner to outer
    public const int UpperLip = 0;
    public const int LowerLip = UpperLip + LipCount;
    public const int BrowL = LowerLip + LipCount;
    public const int BrowR = BrowL + BrowCount;
    public const int UpperLidL = BrowR + BrowCount;
    public const int UpperLidR = UpperLidL + LidCount;
    public const int LowerLidL = UpperLidR + LidCount;
    public const int LowerLidR = LowerLidL + LidCount;
    public const int CheekL = LowerLidR + LidCount;
    public const int CheekR = CheekL + 1;
    public const int Count = CheekR + 1;

    /// <summary>What a driver hands over each frame: one offset per control (rest space, model metres).</summary>
    public sealed class Pose
    {
        public readonly Vector3[] Offsets = new Vector3[Count];
    }

    private sealed class Mesh
    {
        public required MdlRigger.MeshLayout Layout;
        public required int[] Vertices;                     // the vertices it moves
        public required Vector3[] Rest;                     // their rest positions
        public required (int Control, float W)[][] Weights; // per moved vertex: the controls it follows
    }

    private sealed class State
    {
        public required LiveMesh.Found Target;
        public required byte[] Original;
        public required byte[] Work;
        public required List<Mesh> Meshes;
        public nint Address;
        public nint Replacement;
        public Pose? Last;
        public bool Clean = true;
        public float Scale = 1f;
    }

    private static State? _state;
    private static volatile Pose? _pose;
    public static string Status = "";
    public static bool Running => _state != null;
    public static nint Owner => _state?.Address ?? 0;
    /// <summary>How big this face is against an average one (for drivers: offsets scale with it).</summary>
    public static float Scale => _state?.Scale ?? 1f;

    /// <summary>The offsets to show (null: ease back to the face as it is).</summary>
    public static void Hand(Pose? pose) => _pose = pose;

    // ---- Start / stop (UI thread)

    public static string Start(nint address, nint drawObject, Func<string, byte[]?> readFile)
    {
        Stop();
        var log = new List<string>();
        var found = LiveMesh.Probe(drawObject, log, LipMesh.IsFace);
        foreach(var target in found.Where(f => f.IsVertex && LipMesh.IsFace(f.File)))
        {
            try
            {
                var state = Build(target, readFile, out string report);
                if(state == null)
                {
                    Status = "Face rig: " + report;
                    continue;
                }
                state.Address = address;
                if(target.Desc.Usage == 1) // immutable: an identical writable buffer in its place
                {
                    state.Replacement = LiveMesh.Replace(target, state.Original);
                    if(state.Replacement == 0)
                        continue;
                }
                _state = state;
                _pose = null;
                return Status = "Face rig: " + report;
            }
            catch(Exception e)
            {
                Status = "Face rig: " + e.Message;
            }
        }
        if(Status.Length == 0 || !Status.StartsWith("Face rig:", StringComparison.Ordinal))
            Status = "Face rig: couldn't find the face model (the face needs to be drawn).";
        return Status;
    }

    public static void Stop()
    {
        var state = _state;
        _state = null;
        _pose = null;
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

    // ---- Building the rig from the face's own mesh

    private static bool Any(string bone, params string[] prefixes)
    {
        foreach(var p in prefixes)
            if(bone.StartsWith(p, StringComparison.Ordinal))
                return true;
        return false;
    }

    private static float Smooth(float u)
    {
        u = Math.Clamp(u, 0f, 1f);
        return u * u * (3f - 2f * u);
    }

    private static State? Build(LiveMesh.Found target, Func<string, byte[]?> readFile, out string report)
    {
        report = "";
        var data = readFile(target.File);
        if(data == null)
        {
            report = "couldn't read the face model.";
            return null;
        }
        var (offset, size, layouts) = MdlRigger.Layout(data);
        int lod = Array.FindIndex(size, s => s == target.Desc.ByteWidth);
        if(lod < 0)
        {
            report = "the face model drawn isn't the one on file.";
            return null;
        }
        var original = data.AsSpan((int)offset[lod], (int)size[lod]).ToArray();
        var skins = MdlRigger.Skin(data, lod);

        // Every vertex of the face's meshes, with how much of it is each feature (from the face bones' weights).
        var all = new List<(int Mesh, int V, Vector3 P, float Up, float Down, float Corner, float Brow, float ULid, float DLid, float Cheek, float Skip)>();
        foreach(var layout in layouts.Where(l => l.Lod == lod))
        {
            var skin = skins.FirstOrDefault(s => s.Mesh == layout.Mesh);
            if(skin == null)
                continue;
            var bones = skin.Bones;
            for(int v = 0; v < layout.VertexCount; v++)
            {
                int pa = layout.PositionAt + v * layout.PositionStride;
                if(pa + 12 > original.Length)
                    break;
                var p = MdlRigger.ReadVector(original, pa, layout.PositionType);
                float up = 0, down = 0, corner = 0, brow = 0, ulid = 0, dlid = 0, cheek = 0, skip = 0;
                for(int k = 0; k < skin.Slots; k++)
                {
                    int slot = skin.Indices[v * 8 + k];
                    if(slot >= bones.Length)
                        continue;
                    float w = skin.Weights[v * 8 + k];
                    string b = bones[slot];
                    if(LipMesh.IsUpperLip(b)) up += w;
                    else if(LipMesh.IsLowerLip(b)) down += w;
                    else if(b.StartsWith("j_f_lip", StringComparison.Ordinal)) corner += w;
                    else if(Any(b, "j_f_mayu", "j_f_mmayu", "j_f_miken")) brow += w;
                    else if(b.StartsWith("j_f_umab", StringComparison.Ordinal)) ulid += w;
                    else if(b.StartsWith("j_f_dmab", StringComparison.Ordinal)) dlid += w;
                    else if(Any(b, "j_f_hoho", "j_f_shoho")) cheek += w;
                    else if(Any(b, "j_f_haguki", "j_f_bero", "j_f_eye")) skip += w;
                }
                all.Add((layout.Mesh, v, p, up, down, corner, brow, ulid, dlid, cheek, skip));
            }
        }
        if(all.Count == 0)
        {
            report = "the face model has no skinned vertices.";
            return null;
        }

        // ---- The mouth: its middle, width, the line between the lips.
        var lipVerts = all.Where(a => a.Up + a.Down + a.Corner > 0.4f && a.Skip < 0.5f).ToList();
        if(lipVerts.Count < 12)
        {
            report = "couldn't find the lips on this face.";
            return null;
        }
        var cx = lipVerts.Average(a => a.P.X);
        var cy = lipVerts.Average(a => a.P.Y);
        var cz = lipVerts.Average(a => a.P.Z);
        float half = lipVerts.Max(a => MathF.Abs(a.P.X - cx));
        if(half < 0.004f || half > 0.08f)
        {
            report = $"the lips' width looks wrong ({half * 200f:0.0} cm).";
            return null;
        }
        float scale = Math.Clamp(half / 0.024f, 0.4f, 2.5f);
        // where the lips meet: the corners' height (the line curves from the middle to them)
        var cornerVerts = lipVerts.Where(a => MathF.Abs(a.P.X - cx) > half * 0.85f).ToList();
        float cornerY = cornerVerts.Count > 0 ? cornerVerts.Average(a => a.P.Y) : cy;
        var upperOnly = lipVerts.Where(a => a.Up > a.Down + 0.2f && MathF.Abs(a.P.X - cx) < half * 0.3f).ToList();
        var lowerOnly = lipVerts.Where(a => a.Down > a.Up + 0.2f && MathF.Abs(a.P.X - cx) < half * 0.3f).ToList();
        float midY = upperOnly.Count > 0 && lowerOnly.Count > 0 ? (upperOnly.Min(a => a.P.Y) + lowerOnly.Max(a => a.P.Y)) * 0.5f : cy;
        float LineY(float x)
        {
            float u = Math.Clamp((x - cx) / half, -1f, 1f);
            return midY + (cornerY - midY) * u * u;
        }

        // ---- The brows and lids: per side, their span across.
        (float X0, float X1, float Y, float Z, bool Ok) Span(Func<(int Mesh, int V, Vector3 P, float Up, float Down, float Corner, float Brow, float ULid, float DLid, float Cheek, float Skip), float> of, int side)
        {
            var set = all.Where(a => of(a) > 0.35f && a.Skip < 0.5f && Math.Sign(a.P.X - cx) == side).ToList();
            if(set.Count < 4)
                return (0, 0, 0, 0, false);
            float inner = side > 0 ? set.Min(a => a.P.X) : set.Max(a => a.P.X);
            float outer = side > 0 ? set.Max(a => a.P.X) : set.Min(a => a.P.X);
            return (inner, outer, set.Average(a => a.P.Y), set.Average(a => a.P.Z), true);
        }
        var browL = Span(a => a.Brow, 1);
        var browR = Span(a => a.Brow, -1);
        var ulidL = Span(a => a.ULid, 1);
        var ulidR = Span(a => a.ULid, -1);
        var dlidL = Span(a => a.DLid, 1);
        var dlidR = Span(a => a.DLid, -1);
        var cheekL = Span(a => a.Cheek, 1);
        var cheekR = Span(a => a.Cheek, -1);

        // Spread one row of controls along x (partition of unity: the weights along the row add up to 1).
        static void Row(List<(int, float)> into, float x, float x0, float x1, int count, int first)
        {
            if(MathF.Abs(x1 - x0) < 1e-5f)
                return;
            float u = (x - x0) / (x1 - x0) * (count - 1);
            u = Math.Clamp(u, 0f, count - 1);
            int i = Math.Min((int)u, count - 2);
            float f = u - i;
            f = f * f * (3f - 2f * f); // smooth between neighbours
            if(1f - f > 1e-3f) into.Add((first + i, 1f - f));
            if(f > 1e-3f) into.Add((first + i + 1, f));
        }

        var meshes = new Dictionary<int, (List<int> V, List<Vector3> P, List<(int, float)[]> W)>();
        int moved = 0;
        float mouthReachX = half * 1.35f, mouthUp = 0.02f * scale, mouthDown = 0.022f * scale, mouthDepth = 0.03f * scale;
        foreach(var a in all)
        {
            if(a.Skip > 0.5f)
                continue;
            var w = new List<(int, float)>();
            var p = a.P;

            // Lips (and the skin round them): upper row above the line between the lips, lower row below.
            float dx = (p.X - cx) / mouthReachX;
            float lineY = LineY(p.X);
            float dyUp = (p.Y - lineY) / mouthUp, dyDown = (lineY - p.Y) / mouthDown;
            float dz = (cz - p.Z) / mouthDepth;
            if(MathF.Abs(dx) < 1f && dz < 1f && p.Z > cz - mouthDepth)
            {
                float across = 1f - Smooth(MathF.Abs(dx));
                float side = Smooth((p.Y - lineY) / (0.003f * scale) * 0.5f + 0.5f);   // 1 above the line, 0 below
                // on the lip itself all of it; off the lip, fading out over the skin
                float lipness = Math.Clamp(a.Up + a.Down + a.Corner, 0f, 1f);
                float upFade = MathF.Max(lipness, 1f - Smooth(dyUp));
                float downFade = MathF.Max(lipness, 1f - Smooth(dyDown));
                float depthFade = 1f - Smooth(MathF.Max(0f, dz));
                var row = new List<(int, float)>();
                float rx = Math.Clamp(p.X, cx - half, cx + half);
                float upper = across * side * upFade * depthFade;
                float lower = across * (1f - side) * downFade * depthFade;
                if(upper > 1e-3f)
                {
                    row.Clear();
                    Row(row, rx, cx - half, cx + half, LipCount, UpperLip);
                    foreach(var (c, cw) in row) w.Add((c, cw * upper));
                }
                if(lower > 1e-3f)
                {
                    row.Clear();
                    Row(row, rx, cx - half, cx + half, LipCount, LowerLip);
                    foreach(var (c, cw) in row) w.Add((c, cw * lower));
                }
            }

            // Brows (and the forehead / skin just under them).
            foreach(var (span, first, sgn) in new[] { (browL, BrowL, 1), (browR, BrowR, -1) })
            {
                if(!span.Ok || Math.Sign(p.X - cx) != sgn)
                    continue;
                float reach = 0.02f * scale;
                float dy = p.Y - span.Y;
                float fade = MathF.Max(Math.Clamp(a.Brow * 1.5f, 0f, 1f), 1f - Smooth(dy > 0 ? dy / (reach * 1.3f) : -dy / (reach * 0.6f)));
                float lo = MathF.Min(span.X0, span.X1), hi = MathF.Max(span.X0, span.X1);
                float outside = p.X < lo ? (lo - p.X) / (0.012f * scale) : p.X > hi ? (p.X - hi) / (0.012f * scale) : 0f;
                fade *= 1f - Smooth(outside);
                fade *= 1f - Smooth(MathF.Max(0f, span.Z - p.Z - 0.01f * scale) / (0.02f * scale));
                if(fade < 1e-3f)
                    continue;
                var row = new List<(int, float)>();
                Row(row, p.X, span.X0, span.X1, BrowCount, first);
                foreach(var (c, cw) in row) w.Add((c, cw * fade));
            }

            // Eyelids: by the lid bones' own weights (the lids are skinned to them), spread along each lid.
            foreach(var (span, first, sgn, amount) in new[] { (ulidL, UpperLidL, 1, a.ULid), (ulidR, UpperLidR, -1, a.ULid), (dlidL, LowerLidL, 1, a.DLid), (dlidR, LowerLidR, -1, a.DLid) })
            {
                if(!span.Ok || amount < 0.02f || Math.Sign(p.X - cx) != sgn)
                    continue;
                var row = new List<(int, float)>();
                Row(row, p.X, span.X0, span.X1, LidCount, first);
                foreach(var (c, cw) in row) w.Add((c, cw * Math.Clamp(amount * 1.2f, 0f, 1f)));
            }

            // Cheeks: round their middle.
            foreach(var (span, control, sgn) in new[] { (cheekL, CheekL, 1), (cheekR, CheekR, -1) })
            {
                if(!span.Ok || Math.Sign(p.X - cx) != sgn)
                    continue;
                var mid = new Vector3((span.X0 + span.X1) * 0.5f, span.Y, span.Z);
                float d = Vector3.Distance(p, mid) / (0.025f * scale);
                float fade = MathF.Max(a.Cheek, 1f - Smooth(d));
                if(fade > 1e-3f)
                    w.Add((control, fade));
            }

            if(w.Count == 0)
                continue;
            if(!meshes.TryGetValue(a.Mesh, out var m))
                meshes[a.Mesh] = m = (new List<int>(), new List<Vector3>(), new List<(int, float)[]>());
            m.V.Add(a.V);
            m.P.Add(p);
            m.W.Add(w.ToArray());
            moved++;
        }

        var list = new List<Mesh>();
        foreach(var (meshIndex, m) in meshes)
        {
            var layout = layouts.First(l => l.Lod == lod && l.Mesh == meshIndex);
            list.Add(new Mesh { Layout = layout, Vertices = m.V.ToArray(), Rest = m.P.ToArray(), Weights = m.W.ToArray() });
        }
        int found = (browL.Ok ? 1 : 0) + (browR.Ok ? 1 : 0) + (ulidL.Ok ? 1 : 0) + (ulidR.Ok ? 1 : 0) + (dlidL.Ok ? 1 : 0) + (dlidR.Ok ? 1 : 0) + (cheekL.Ok ? 1 : 0) + (cheekR.Ok ? 1 : 0);
        report = $"on, {moved} vertices rigged (lips {half * 200f:0.0} cm across; brows, lids and cheeks: {found} of 8 found).";
        return new State { Target = target, Original = original, Work = (byte[])original.Clone(), Meshes = list, Scale = scale };
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
                _state = null;
                Status = "Face rig: the face was redrawn; starting again...";
                return;
            }
            var pose = _pose;
            if(pose == null)
            {
                if(!state.Clean)
                {
                    LiveMesh.Upload(buffer, state.Target.Desc, state.Original);
                    state.Clean = true;
                    state.Last = null;
                }
                return;
            }
            if(ReferenceEquals(pose, state.Last))
                return;
            state.Last = pose;
            Buffer.BlockCopy(state.Original, 0, state.Work, 0, state.Original.Length);
            var off = pose.Offsets;
            foreach(var mesh in state.Meshes)
            {
                var layout = mesh.Layout;
                for(int i = 0; i < mesh.Vertices.Length; i++)
                {
                    var move = Vector3.Zero;
                    foreach(var (c, w) in mesh.Weights[i])
                        move += off[c] * w;
                    if(move.LengthSquared() < 1e-14f)
                        continue;
                    int pa = layout.PositionAt + mesh.Vertices[i] * layout.PositionStride;
                    if(pa + 12 <= state.Work.Length)
                        MdlRigger.WriteVector(state.Work, pa, layout.PositionType, mesh.Rest[i] + move);
                }
            }
            LiveMesh.Upload(buffer, state.Target.Desc, state.Work);
            state.Clean = false;
        }
        catch(Exception e)
        {
            Status = "Face rig: " + e.Message;
            Stop();
        }
    }
}
