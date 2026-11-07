using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using Limber.BodyRig;
using Limber.Core;

namespace Limber.Gpu;

/// <summary>
/// Face morphs (experimental): blend shapes for the face model that's drawn, made on this PC from the face's own mesh.
///
/// Two kinds: Limber's own shapes, built from where the face's features are (found from the face bones' weights:
/// lips, nose, lids, brows, cheeks, jaw), so they fit any race and face mod; and shapes imported from Koikatsu and
/// Virt-a-Mate (see Limber.Koi.FaceMorphImport), transferred onto this face and saved next to Limber's settings.
///
/// They don't write the face's vertex buffer themselves: the face rig (FaceRig) and the lips' live mesh (LipMesh), which
/// already do, add these shapes when they write it. Whoever drives the morphs asks for the face rig on the character
/// (Wanted), so the shapes show even with nothing else on.
/// Only on this PC; nothing is sent anywhere.
/// </summary>
public static class FaceMorphs
{
    // ---- What's wanted (any thread) -------------------------------------------------------------------------------

    private static volatile Dictionary<string, float> _weights = new();
    private static volatile int _version;
    /// <summary>The character the morphs are for (0: none).</summary>
    public static nint Wanted;
    /// <summary>Bumped whenever the weights change: writers write again when it moves.</summary>
    public static int Version => _version;
    /// <summary>What the last write did (for the page).</summary>
    public static volatile string Applied = "";
    /// <summary>Any morph on at all.</summary>
    public static bool Active => _weights.Count > 0;

    /// <summary>The morphs and how much of each (by name); names not found on the face are skipped.</summary>
    public static void Set(nint owner, Dictionary<string, float> weights)
    {
        var clean = new Dictionary<string, float>();
        foreach(var (k, v) in weights)
            if(MathF.Abs(v) > 1e-4f && float.IsFinite(v))
                clean[k] = Math.Clamp(v, -2f, 2f);
        var old = _weights;
        bool same = owner == Wanted && old.Count == clean.Count;
        if(same)
            foreach(var (k, v) in clean)
                if(!old.TryGetValue(k, out var o) || MathF.Abs(o - v) > 1e-4f)
                {
                    same = false;
                    break;
                }
        if(same)
            return;
        Wanted = clean.Count > 0 ? owner : 0;
        _weights = clean;
        _version++;
    }

    public static void Clear() => Set(0, new());

    // ---- The face, as read --------------------------------------------------------------------------------------------

    /// <summary>One vertex's features (from the face bones' weights) and place at rest.</summary>
    public struct Vertex
    {
        public Vector3 P, N, N0;   // place, normal (averaged where vertices meet), its own normal
        public float Upper, Lower, Corner, Nose, LidUp, LidDown, Brow, InnerBrow, UnderEye, Cheek, Jaw, Neck, Skip;
        public float Lip => Math.Clamp(Upper + Lower + Corner, 0f, 1f);
    }

    /// <summary>Where the features are (rest space: +X the character's left, +Y up, +Z forward; model metres).</summary>
    public sealed class Landmarks
    {
        public Vector3 Mouth;               // the lips' middle
        public float Half;                  // half the mouth's width
        public float MidY, CornerY;         // where the lips meet: in the middle and at the corners
        public Vector3 EyeL, EyeR, NoseTip, NostrilL, NostrilR, Chin, CheekL, CheekR;
        public float NoseBaseY;
        public float LineY(float x)
        {
            float u = Math.Clamp((x - Mouth.X) / Half, -1f, 1f);
            return MidY + (CornerY - MidY) * u * u;
        }
        public Vector3[] All => [Mouth + new Vector3(-Half, CornerY - Mouth.Y, 0f), Mouth + new Vector3(Half, CornerY - Mouth.Y, 0f),
            new(Mouth.X, MidY, Mouth.Z), EyeL, EyeR, NoseTip, NostrilL, NostrilR, Chin, CheekL, CheekR];
    }

    /// <summary>A face model read for morphs: its vertices per mesh (one LoD), triangles, and features.</summary>
    public sealed class Face
    {
        public required string File;
        public required int Lod;
        public required int ByteWidth;
        public required Dictionary<int, MdlRigger.MeshLayout> Layouts;  // by mesh index
        public required Dictionary<int, Vertex[]> Vertices;             // by mesh index
        public required Dictionary<int, ushort[]> Triangles;            // by mesh index
        public required Landmarks Marks;
        public string Key => KeyOf(File, ByteWidth);
        public readonly Dictionary<string, Morph> Morphs = new();
        public readonly List<string> Report = new();
    }

    /// <summary>One morph: its moves, per mesh vertex (rest space).</summary>
    public sealed class Morph
    {
        public string Name = "", Group = "", Source = "Limber", Tip = "";
        public int[] Mesh = [];
        public int[] Vertex = [];
        public Vector3[] Delta = [];
    }

    public static string KeyOf(string file, int byteWidth)
    {
        var f = file.Replace('\\', '/');
        int bar = f.LastIndexOf('|');
        if(bar >= 0)
            f = f[(bar + 1)..];
        return (System.IO.Path.GetFileName(f) + "_" + byteWidth).ToLowerInvariant();
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Face> Faces = new();

    /// <summary>The face read from this model file (one LoD), made once and kept. Null if it can't be read.</summary>
    public static Face? Prepare(string file, byte[] data, int lod)
    {
        var (offset, size, layouts) = MdlRigger.Layout(data);
        if(lod < 0 || lod >= size.Length || size[lod] == 0)
            return null;
        string key = KeyOf(file, (int)size[lod]);
        lock(Gate)
            if(Faces.TryGetValue(key, out var known))
                return known;
        var face = Read(file, data, lod, offset, size, layouts);
        if(face == null)
            return null;
        try
        {
            BuildOwn(face);
        }
        catch(Exception e)
        {
            face.Report.Add("Limber's shapes: " + e.Message);
        }
        try
        {
            LoadImported(face);
        }
        catch(Exception e)
        {
            face.Report.Add("Imported shapes: " + e.Message);
        }
        lock(Gate)
        {
            if(Faces.Count > 8)
                Faces.Clear();
            Faces[key] = face;
        }
        return face;
    }

    /// <summary>The face last prepared for this model (if any).</summary>
    public static Face? Find(string file, int byteWidth)
    {
        lock(Gate)
            return Faces.TryGetValue(KeyOf(file, byteWidth), out var f) ? f : null;
    }

    /// <summary>The faces read so far (for the page: which morphs there are).</summary>
    public static Face[] Known()
    {
        lock(Gate)
            return Faces.Values.ToArray();
    }

    /// <summary>Forget a face (its imported shapes changed): read again next time.</summary>
    public static void Forget(string key)
    {
        lock(Gate)
            Faces.Remove(key);
        _version++;
    }

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

    /// <summary>1 at the middle, easing to 0 at distance 1.</summary>
    private static float Fall(float d) => 1f - Smooth(d);

    public static Face? Read(string file, byte[] data, int lod, uint[] offset, uint[] size, List<MdlRigger.MeshLayout> layouts)
    {
        var block = data.AsSpan((int)offset[lod], (int)size[lod]).ToArray();
        var skins = MdlRigger.Skin(data, lod);
        var verts = new Dictionary<int, Vertex[]>();
        var tris = new Dictionary<int, ushort[]>();
        var lays = new Dictionary<int, MdlRigger.MeshLayout>();
        foreach(var layout in layouts.Where(l => l.Lod == lod))
        {
            var skin = skins.FirstOrDefault(s => s.Mesh == layout.Mesh);
            if(skin == null)
                continue;
            var list = new Vertex[layout.VertexCount];
            int eyeball = 0;
            for(int v = 0; v < layout.VertexCount; v++)
            {
                int pa = layout.PositionAt + v * layout.PositionStride;
                if(pa + 12 > block.Length)
                    break;
                var x = new Vertex { P = MdlRigger.ReadVector(block, pa, layout.PositionType) };
                if(layout.NormalAt >= 0 && layout.NormalType is 2 or 3 or 14)
                {
                    int na = layout.NormalAt + v * layout.NormalStride;
                    if(na + 6 <= block.Length)
                        x.N = x.N0 = MdlRigger.ReadVector(block, na, layout.NormalType);
                }
                for(int k = 0; k < skin.Slots; k++)
                {
                    int slot = skin.Indices[v * 8 + k];
                    if(slot >= skin.Bones.Length)
                        continue;
                    float w = skin.Weights[v * 8 + k];
                    if(w <= 0f)
                        continue;
                    string b = skin.Bones[slot];
                    if(LipMesh.IsUpperLip(b)) x.Upper += w;
                    else if(LipMesh.IsLowerLip(b)) x.Lower += w;
                    else if(b.StartsWith("j_f_lip", StringComparison.Ordinal)) x.Corner += w;
                    else if(Any(b, "j_f_hana", "j_f_uhana")) x.Nose += w;
                    else if(Any(b, "j_f_umab", "j_f_mabup")) x.LidUp += w;
                    else if(Any(b, "j_f_dmab", "j_f_mabdn")) x.LidDown += w;
                    else if(Any(b, "j_f_miken", "j_f_dmiken")) x.InnerBrow += w;
                    else if(Any(b, "j_f_mayu", "j_f_mmayu")) x.Brow += w;
                    else if(Any(b, "j_f_memoto", "j_f_dmemoto")) x.UnderEye += w;
                    else if(Any(b, "j_f_hoho", "j_f_shoho")) x.Cheek += w;
                    else if(Any(b, "j_f_dago", "j_f_ago", "j_ago")) x.Jaw += w;
                    else if(Any(b, "j_kubi", "j_sebo", "j_kosi", "j_mune", "j_sako")) x.Neck += w;
                    else if(Any(b, "j_f_haguki", "j_f_bero", "j_f_eye", "j_f_eyepuru", "j_f_hagukiup", "j_f_hagukidn"))
                    {
                        x.Skip += w;
                        if(b.StartsWith("j_f_eye", StringComparison.Ordinal))
                            eyeball++;
                    }
                }
                list[v] = x;
            }
            // (an eyeball, lash or tooth mesh: left alone whole)
            if(eyeball > layout.VertexCount / 2)
                continue;
            verts[layout.Mesh] = list;
            tris[layout.Mesh] = skin.Triangles;
            lays[layout.Mesh] = layout;
        }
        // Normals averaged where vertices share a place (split at texture seams, or between meshes), so the shapes
        // that move along them don't tear the surface apart there.
        var sum = new Dictionary<(int, int, int), Vector3>();
        static (int, int, int) Cell(Vector3 p) => ((int)MathF.Round(p.X * 1e5f), (int)MathF.Round(p.Y * 1e5f), (int)MathF.Round(p.Z * 1e5f));
        foreach(var list in verts.Values)
            foreach(var x in list)
                if(x.N.LengthSquared() > 1e-12f)
                    sum[Cell(x.P)] = sum.GetValueOrDefault(Cell(x.P)) + Vector3.Normalize(x.N);
        foreach(var list in verts.Values)
            for(int v = 0; v < list.Length; v++)
                if(sum.TryGetValue(Cell(list[v].P), out var n) && n.LengthSquared() > 1e-12f)
                    list[v].N = Vector3.Normalize(n);
        var all = verts.Values.SelectMany(a => a).Where(a => a.Skip < 0.5f).ToList();
        var lip = all.Where(a => a.Lip > 0.4f).ToList();
        if(lip.Count < 12)
            return null;
        var marks = new Landmarks();
        float cx = lip.Average(a => a.P.X), cy = lip.Average(a => a.P.Y), cz = lip.Average(a => a.P.Z);
        float half = lip.Max(a => MathF.Abs(a.P.X - cx));
        if(half < 0.004f || half > 0.08f)
            return null;
        marks.Mouth = new Vector3(cx, cy, cz);
        marks.Half = half;
        var corners = lip.Where(a => MathF.Abs(a.P.X - cx) > half * 0.85f).ToList();
        marks.CornerY = corners.Count > 0 ? corners.Average(a => a.P.Y) : cy;
        var up = lip.Where(a => a.Upper > a.Lower + 0.2f && MathF.Abs(a.P.X - cx) < half * 0.3f).ToList();
        var dn = lip.Where(a => a.Lower > a.Upper + 0.2f && MathF.Abs(a.P.X - cx) < half * 0.3f).ToList();
        marks.MidY = up.Count > 0 && dn.Count > 0 ? (up.Min(a => a.P.Y) + dn.Max(a => a.P.Y)) * 0.5f : cy;
        float h = half;

        Vector3 SideMean(Func<Vertex, float> of, int side, Vector3 fallback)
        {
            var set = all.Where(a => of(a) > 0.3f && Math.Sign(a.P.X - cx) == side).ToList();
            return set.Count >= 3 ? new Vector3(set.Average(a => a.P.X), set.Average(a => a.P.Y), set.Average(a => a.P.Z)) : fallback;
        }
        marks.EyeL = SideMean(a => a.LidUp + a.LidDown, 1, new Vector3(cx + 1.6f * h, cy + 2.6f * h, cz - 0.4f * h));
        marks.EyeR = SideMean(a => a.LidUp + a.LidDown, -1, new Vector3(cx - 1.6f * h, cy + 2.6f * h, cz - 0.4f * h));
        float eyeY = (marks.EyeL.Y + marks.EyeR.Y) * 0.5f;
        var noseZone = all.Where(a => MathF.Abs(a.P.X - cx) < 0.35f * h && a.P.Y > marks.MidY + 0.4f * h && a.P.Y < eyeY - 0.3f * h).ToList();
        marks.NoseTip = noseZone.Count > 0 ? noseZone.OrderByDescending(a => a.P.Z).First().P : new Vector3(cx, cy + 1.2f * h, cz + 0.5f * h);
        marks.NoseBaseY = marks.NoseTip.Y - 0.35f * h;
        marks.NostrilL = SideMean(a => a.Nose, 1, marks.NoseTip + new Vector3(0.55f * h, -0.2f * h, -0.35f * h));
        marks.NostrilR = SideMean(a => a.Nose, -1, marks.NoseTip + new Vector3(-0.55f * h, -0.2f * h, -0.35f * h));
        var chinZone = all.Where(a => MathF.Abs(a.P.X - cx) < 0.3f * h && a.P.Y < marks.MidY - 0.5f * h && a.P.Z > cz - 1.2f * h).ToList();
        marks.Chin = chinZone.Count > 0 ? chinZone.OrderBy(a => a.P.Y).First().P : new Vector3(cx, cy - 1.6f * h, cz - 0.3f * h);
        Vector3 Surface(float x, float y)
        {
            var near = all.Where(a => MathF.Abs(a.P.X - x) < 0.3f * h && MathF.Abs(a.P.Y - y) < 0.3f * h).ToList();
            return new Vector3(x, y, near.Count > 0 ? near.Max(a => a.P.Z) : cz - 0.6f * h);
        }
        float cheekY = marks.MidY + 0.45f * (eyeY - marks.MidY);
        marks.CheekL = Surface(cx + 1.55f * h, cheekY);
        marks.CheekR = Surface(cx - 1.55f * h, cheekY);

        return new Face
        {
            File = file, Lod = lod, ByteWidth = (int)size[lod], Layouts = lays, Vertices = verts, Triangles = tris, Marks = marks,
        };
    }

    // ---- Limber's own shapes ------------------------------------------------------------------------------------------

    /// <summary>Limber's shapes: name, group, what it does.</summary>
    public static readonly (string Name, string Group, string Tip)[] Own =
    [
        ("Lips round (O)", "Lips", "The mouth rounded into an O: corners in, the middle of each lip out."),
        ("Lips stretched", "Lips", "The lips stretched wide round something big: the opening bigger, the lips thinned."),
        ("Lips forward (pucker)", "Lips", "The lips and the skin round them pushed forward and drawn in."),
        ("Lips tube (ohogao)", "Lips", "The lips pushed far forward into a long tube, as in an ohogao face."),
        ("Vacuum lips", "Lips", "The lips held out and rolled outward, clinging (vacuum fellatio)."),
        ("Upper lip up", "Lips", "The upper lip curled up off the teeth."),
        ("Upper lip long", "Lips", "The skin between the nose and the upper lip stretched down."),
        ("Lower lip out", "Lips", "The lower lip pushed out and down."),
        ("Mouth corners in", "Lips", "The corners drawn toward the middle."),
        ("Mouth corners out", "Lips", "The corners pulled out wide."),
        ("Mouth corners down", "Lips", "The corners pulled down."),
        ("Cheeks hollow", "Cheeks", "The cheeks sucked in (vacuum)."),
        ("Cheeks puffed", "Cheeks", "The cheeks blown out."),
        ("Cheek bulge left", "Cheeks", "A bulge in the left cheek from inside (something pressing it)."),
        ("Cheek bulge right", "Cheeks", "A bulge in the right cheek from inside."),
        ("Cheeks raised", "Cheeks", "The cheeks lifted (pushing the lower lids up)."),
        ("Nostrils flared", "Nose", "The nostrils spread wide."),
        ("Nose squashed", "Nose", "The nose pressed flat against something."),
        ("Nose scrunched", "Nose", "The nose wrinkled up."),
        ("Nose pulled down", "Nose", "The nose's tip and base drawn down (a long, strained face)."),
        ("Long face", "Jaw", "The lower face stretched down past what the jaw does (a slack, gaping mouth)."),
        ("Chin down", "Jaw", "The chin drawn down and back."),
        ("Chin pressed", "Jaw", "The chin squashed up and forward."),
        ("Lower lids up", "Eyes", "The lower lids pushed up (squint)."),
        ("Upper lids heavy", "Eyes", "The upper lids drooping."),
        ("Eye bags", "Eyes", "Puffy skin under the eyes."),
        ("Brows knit", "Eyes", "The inner brows pulled down and together (a strained furrow)."),
        ("Brows up inside", "Eyes", "The inner brows raised (worried, helpless)."),
    ];

    private static void BuildOwn(Face face)
    {
        var m = face.Marks;
        float h = m.Half;
        float cx = m.Mouth.X;
        float eyeY = (m.EyeL.Y + m.EyeR.Y) * 0.5f;
        float eyeGap = MathF.Max(0.5f * h, eyeY - m.MidY);
        var acc = new Dictionary<string, (List<int> Mesh, List<int> V, List<Vector3> D)>();
        foreach(var (name, _, _) in Own)
            acc[name] = (new(), new(), new());
        void Put(string name, int mesh, int v, Vector3 d)
        {
            if(d.LengthSquared() < 1e-14f)
                return;
            var a = acc[name];
            a.Mesh.Add(mesh);
            a.V.Add(v);
            a.D.Add(d);
        }

        foreach(var (mesh, list) in face.Vertices)
            for(int v = 0; v < list.Length; v++)
            {
                var x = list[v];
                if(x.Skip > 0.5f)
                    continue;
                // (the edge where the face meets the body's neck stays put: no seam)
                float keep = (1f - Math.Clamp(x.Skip * 2f, 0f, 1f)) * (1f - Math.Clamp(x.Neck * 1.5f, 0f, 1f));
                var p = x.P;
                float u = (p.X - cx) / h;                     // across the mouth: -1 right corner, +1 left
                float lineY = m.LineY(p.X);
                float vy = (p.Y - lineY) / h;                 // above (+) / below (-) where the lips meet
                float behind = (m.Mouth.Z - p.Z) / h;         // behind the lips' front
                float lip = x.Lip;
                float front = Fall(MathF.Max(0f, behind - 0.3f) / 1.2f);      // the face's front, not the back of the head
                float inside = Fall(MathF.Max(0f, behind - 1.2f) / 1.5f);     // ...and the inside of the mouth, which goes with the lips
                // (round the mouth: reaching less far up, toward the nose, than down)
                float ru = MathF.Sqrt(u * u + (vy * (vy < 0f ? 1.25f : 1.7f)) * (vy * (vy < 0f ? 1.25f : 1.7f)));
                float mouthZone = Fall(MathF.Max(0f, ru - 0.9f) / 0.9f) * inside;  // the lips and the skin right round them
                float side = MathF.Sign(u);
                float upper = Smooth(vy / 0.12f * 0.5f + 0.5f);              // 1 above the line, 0 below
                float cornerness = Smooth((MathF.Abs(u) - 0.45f) / 0.55f);
                var radial = new Vector2(u, vy);
                if(radial.LengthSquared() > 1e-6f)
                    radial = Vector2.Normalize(radial);
                var n = x.N.LengthSquared() > 1e-8f ? Vector3.Normalize(x.N) : Vector3.UnitZ;

                // Lips
                if(mouthZone > 1e-3f)
                {
                    float mid = 1f - cornerness;
                    Put("Lips round (O)", mesh, v, new Vector3(-side * 0.35f * cornerness * h, (upper * 2f - 1f) * 0.22f * mid * h, 0.05f * mid * h) * mouthZone * keep);
                    float stretch = mouthZone * Fall(MathF.Max(0f, ru - 0.3f) / 1.4f);
                    Put("Lips stretched", mesh, v, new Vector3(radial.X * 0.38f * h, radial.Y * 0.3f * h, -0.12f * lip * h) * stretch * keep);
                    Put("Lips forward (pucker)", mesh, v, new Vector3(-(p.X - cx) * 0.28f, -(p.Y - lineY) * 0.15f, 0.45f * h * (0.4f + 0.6f * lip)) * mouthZone * keep);
                    float tube = Fall(MathF.Max(0f, ru - 0.35f) / 1.05f) * inside;
                    Put("Lips tube (ohogao)", mesh, v, new Vector3(-(p.X - cx) * 0.45f, -(p.Y - lineY) * 0.3f - 0.08f * h, 0.8f * h * (0.55f + 0.45f * lip)) * tube * keep);
                    Put("Vacuum lips", mesh, v, new Vector3(-(p.X - cx) * 0.2f, (upper * 2f - 1f) * 0.12f * h * lip, 0.4f * h * (0.3f + 0.7f * lip)) * mouthZone * keep);
                    Put("Upper lip up", mesh, v, new Vector3(0f, 0.2f * h, 0.08f * h) * upper * mouthZone * (1f - cornerness * 0.6f) * keep);
                    Put("Lower lip out", mesh, v, new Vector3(0f, -0.14f * h, 0.2f * h) * (1f - upper) * mouthZone * (1f - cornerness * 0.6f) * keep);
                    Put("Mouth corners in", mesh, v, new Vector3(-side * 0.3f * h, 0f, 0.06f * h) * cornerness * mouthZone * keep);
                    Put("Mouth corners out", mesh, v, new Vector3(side * 0.32f * h, 0.02f * h, -0.08f * h) * cornerness * mouthZone * keep);
                    Put("Mouth corners down", mesh, v, new Vector3(0f, -0.22f * h, 0f) * cornerness * mouthZone * keep);
                }
                // The skin between the nose and the upper lip.
                {
                    float above = p.Y - lineY, span = MathF.Max(0.2f * h, m.NoseBaseY - lineY);
                    float t = Math.Clamp(above / span, 0f, 1.2f);
                    float across = Fall(MathF.Max(0f, MathF.Abs(u) - 0.5f) / 0.9f);
                    float band = (above > -0.05f * h && t < 1.2f ? 1f : 0f) * across * front;
                    // the lip and up to the nose's base move down, most at the lip, easing out at the nose
                    float k = band * (1f - Smooth((t - 0.15f) / 1.05f));
                    Put("Upper lip long", mesh, v, new Vector3(0f, -0.3f * h, 0.06f * h) * k * keep);
                }
                // Cheeks
                foreach(var (centre, s) in new[] { (m.CheekL, 1f), (m.CheekR, -1f) })
                {
                    if(MathF.Sign(p.X - cx) != MathF.Sign(s))
                        continue;
                    // the hollow sits under the cheekbone, toward the mouth's corner
                    var hollow = new Vector3(centre.X - s * 0.15f * h, (centre.Y + lineY) * 0.5f, centre.Z);
                    float d = Vector3.Distance(p, hollow) / (1.1f * h);
                    float k = Fall(d) * front * (1f - lip);
                    Put("Cheeks hollow", mesh, v, -n * 0.32f * h * k * keep);
                    float d2 = Vector3.Distance(p, new Vector3(centre.X, (centre.Y + lineY) * 0.5f, centre.Z)) / (1.4f * h);
                    Put("Cheeks puffed", mesh, v, n * 0.34f * h * Fall(d2) * front * (1f - 0.5f * lip) * keep);
                    var bulge = new Vector3(cx + s * 1.25f * h, lineY + 0.1f * h, centre.Z - 0.05f * h);
                    float d3 = Vector3.Distance(p, bulge) / (0.75f * h);
                    Put(s > 0 ? "Cheek bulge left" : "Cheek bulge right", mesh, v, n * 0.42f * h * Fall(d3) * front * (1f - lip) * keep);
                    float d4 = Vector3.Distance(p, centre) / (1.2f * h);
                    Put("Cheeks raised", mesh, v, new Vector3(0f, 0.2f * h, 0.07f * h) * Fall(d4) * front * keep);
                }
                // Nose
                {
                    float dn = Vector3.Distance(p, m.NoseTip) / (1.05f * h);
                    float nose = MathF.Max(Fall(dn), Math.Clamp(x.Nose * 1.3f, 0f, 1f)) * front;
                    foreach(var (wing, s) in new[] { (m.NostrilL, 1f), (m.NostrilR, -1f) })
                    {
                        float dw = Vector3.Distance(p, wing) / (0.5f * h);
                        if(MathF.Sign(p.X - cx) == MathF.Sign(s) || MathF.Abs(p.X - cx) < 0.05f * h)
                            Put("Nostrils flared", mesh, v, new Vector3(s * 0.16f * h, 0.05f * h, -0.02f * h) * Fall(dw) * front * keep);
                    }
                    Put("Nose squashed", mesh, v, new Vector3((p.X - cx) * 0.22f, -0.04f * h, -0.3f * h * Fall(dn * 0.8f)) * nose * keep);
                    Put("Nose scrunched", mesh, v, new Vector3(0f, 0.13f * h, -0.02f * h) * nose * (p.Y > m.NoseBaseY ? 1f : 0.4f) * keep);
                    Put("Nose pulled down", mesh, v, new Vector3(0f, -0.22f * h, 0.04f * h) * nose * keep);
                }
                // Jaw
                if(p.Y < lineY)
                {
                    float below = (lineY - p.Y) / h;
                    float across = Fall(MathF.Max(0f, MathF.Abs(u) - 1.4f) / 1.1f);
                    float ramp = Smooth(below / 0.35f) * (1f - lip * (1f - upper) * 0f);
                    Put("Long face", mesh, v, new Vector3(0f, -0.6f * h * ramp, -0.05f * h * ramp) * across * front * keep);
                    float dc = Vector3.Distance(p, m.Chin) / (1.1f * h);
                    Put("Chin down", mesh, v, new Vector3(0f, -0.3f * h, -0.08f * h) * Fall(dc) * front * keep);
                    Put("Chin pressed", mesh, v, new Vector3(0f, 0.14f * h, 0.12f * h) * Fall(dc) * front * keep);
                }
                // Eyes
                {
                    float lidDown = Math.Clamp(x.LidDown * 1.3f, 0f, 1f), lidUp = Math.Clamp(x.LidUp * 1.3f, 0f, 1f);
                    Put("Lower lids up", mesh, v, new Vector3(0f, 0.09f * h, 0.015f * h) * lidDown * keep);
                    Put("Upper lids heavy", mesh, v, new Vector3(0f, -0.1f * h, 0.01f * h) * lidUp * keep);
                    foreach(var (eye, s) in new[] { (m.EyeL, 1f), (m.EyeR, -1f) })
                    {
                        if(MathF.Sign(p.X - cx) != MathF.Sign(s))
                            continue;
                        var bag = eye + new Vector3(0f, -0.38f * eyeGap * 0.5f, 0.05f * h);
                        float db = Vector3.Distance(p, bag) / (0.55f * h);
                        Put("Eye bags", mesh, v, n * 0.07f * h * Fall(db) * (1f - lidUp) * keep);
                    }
                    float inner = Math.Clamp(x.InnerBrow * 1.5f + x.Brow * Fall(MathF.Max(0f, MathF.Abs(p.X - cx) - 0.6f * h) / (0.8f * h)), 0f, 1f);
                    Put("Brows knit", mesh, v, new Vector3(-MathF.Sign(p.X - cx) * 0.08f * h, -0.12f * h, 0.02f * h) * inner * keep);
                    Put("Brows up inside", mesh, v, new Vector3(0f, 0.14f * h, 0f) * inner * keep);
                }
            }
        foreach(var (name, group, tip) in Own)
        {
            var a = acc[name];
            if(a.V.Count == 0)
                continue;
            face.Morphs[name] = new Morph { Name = name, Group = group, Tip = tip, Mesh = a.Mesh.ToArray(), Vertex = a.V.ToArray(), Delta = a.D.ToArray() };
        }
        face.Report.Add($"{face.Morphs.Count} of Limber's shapes (mouth {h * 200f:0.0} cm across)");
    }

    // ---- Imported shapes (saved per face) --------------------------------------------------------------------------

    public static string Folder => System.IO.Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "face_morphs");

    public static string FileFor(string key)
    {
        var safe = new string(key.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_').ToArray());
        return System.IO.Path.Combine(Folder, safe + ".morphs");
    }

    private const int FileVersion = 1;

    /// <summary>Saves imported morphs for a face (replacing those of the same name).</summary>
    public static void SaveImported(Face face, IEnumerable<Morph> morphs)
    {
        var path = FileFor(face.Key);
        var keep = new Dictionary<string, Morph>();
        foreach(var m in ReadFile(path))
            keep[m.Name] = m;
        foreach(var m in morphs)
            keep[m.Name] = m;
        WriteFile(path, keep.Values);
        Forget(face.Key);
    }

    public static void DeleteImported(string key, string name)
    {
        var path = FileFor(key);
        var list = ReadFile(path).Where(m => m.Name != name).ToList();
        WriteFile(path, list);
        Forget(key);
    }

    private static void WriteFile(string path, IEnumerable<Morph> morphs)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        using var file = System.IO.File.Create(path);
        using var z = new DeflateStream(file, CompressionLevel.Fastest);
        using var w = new BinaryWriter(z);
        var list = morphs.ToList();
        w.Write(FileVersion);
        w.Write(list.Count);
        foreach(var m in list)
        {
            w.Write(m.Name);
            w.Write(m.Group);
            w.Write(m.Source);
            w.Write(m.Tip);
            w.Write(m.Vertex.Length);
            for(int i = 0; i < m.Vertex.Length; i++)
            {
                w.Write(m.Mesh[i]);
                w.Write(m.Vertex[i]);
                w.Write(m.Delta[i].X);
                w.Write(m.Delta[i].Y);
                w.Write(m.Delta[i].Z);
            }
        }
    }

    private static List<Morph> ReadFile(string path)
    {
        var list = new List<Morph>();
        if(!System.IO.File.Exists(path))
            return list;
        using var file = System.IO.File.OpenRead(path);
        using var z = new DeflateStream(file, CompressionMode.Decompress);
        using var r = new BinaryReader(z);
        if(r.ReadInt32() != FileVersion)
            return list;
        int count = r.ReadInt32();
        for(int k = 0; k < count; k++)
        {
            var m = new Morph { Name = r.ReadString(), Group = r.ReadString(), Source = r.ReadString(), Tip = r.ReadString() };
            int n = r.ReadInt32();
            m.Mesh = new int[n];
            m.Vertex = new int[n];
            m.Delta = new Vector3[n];
            for(int i = 0; i < n; i++)
            {
                m.Mesh[i] = r.ReadInt32();
                m.Vertex[i] = r.ReadInt32();
                m.Delta[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            }
            list.Add(m);
        }
        return list;
    }

    private static void LoadImported(Face face)
    {
        int n = 0;
        foreach(var m in ReadFile(FileFor(face.Key)))
        {
            // (made for this model: every vertex must be there)
            bool ok = true;
            for(int i = 0; i < m.Vertex.Length && ok; i++)
                ok = face.Vertices.TryGetValue(m.Mesh[i], out var vs) && m.Vertex[i] < vs.Length;
            if(!ok)
                continue;
            face.Morphs[m.Name] = m;
            n++;
        }
        if(n > 0)
            face.Report.Add($"{n} imported shapes");
    }

    // ---- Writing (called by the face rig and the lips' live mesh, render thread) ----------------------------------

    private sealed class Mixed
    {
        public int Version = -1;
        public Dictionary<int, Vector3[]> Delta = new(); // by mesh: per vertex
        public Dictionary<int, List<int>> Moved = new(); // by mesh: the vertices that move
    }

    private static readonly Dictionary<string, Mixed> MixedFor = new();

    // The weighted sum of the morphs on this face (made again when the weights change).
    private static Mixed? Mix(Face face)
    {
        var weights = _weights;
        if(weights.Count == 0)
            return null;
        if(!MixedFor.TryGetValue(face.Key, out var mix))
            MixedFor[face.Key] = mix = new Mixed();
        if(mix.Version == _version)
            return mix;
        mix.Version = _version;
        mix.Delta.Clear();
        mix.Moved.Clear();
        foreach(var (name, w) in weights)
        {
            if(!face.Morphs.TryGetValue(name, out var morph))
                continue;
            for(int i = 0; i < morph.Vertex.Length; i++)
            {
                int mesh = morph.Mesh[i];
                if(!mix.Delta.TryGetValue(mesh, out var d))
                {
                    if(!face.Vertices.TryGetValue(mesh, out var vs))
                        continue;
                    mix.Delta[mesh] = d = new Vector3[vs.Length];
                    mix.Moved[mesh] = new List<int>();
                }
                int v = morph.Vertex[i];
                if(v >= d.Length)
                    continue;
                if(d[v] == Vector3.Zero)
                    mix.Moved[mesh].Add(v);
                d[v] += morph.Delta[i] * w;
                if(d[v] == Vector3.Zero)
                    d[v] = new Vector3(1e-9f, 0f, 0f); // (still listed)
            }
        }
        return mix;
    }

    /// <summary>
    /// Writes the morphs into a copy of the face model's vertex block (positions, and normals turned with the surface).
    /// For a writer that has just copied the model's own vertices in. Returns false if nothing was written.
    /// </summary>
    public static bool ApplyTo(byte[] work, string file, int byteWidth)
    {
        if(_weights.Count == 0)
            return false;
        var face = Find(file, byteWidth);
        if(face == null)
        {
            Applied = "the face model drawn wasn't read for morphs (its lips weren't found?)";
            return false;
        }
        var mix = Mix(face);
        if(mix == null || mix.Moved.Count == 0)
        {
            Applied = "none of the morphs set are on this face";
            return false;
        }
        Applied = $"{mix.Moved.Values.Sum(m => m.Count)} vertices moved by {_weights.Count} morph(s)";
        foreach(var (mesh, moved) in mix.Moved)
        {
            if(!face.Layouts.TryGetValue(mesh, out var layout) || !face.Vertices.TryGetValue(mesh, out var vs))
                continue;
            var d = mix.Delta[mesh];
            foreach(int v in moved)
            {
                int pa = layout.PositionAt + v * layout.PositionStride;
                if(pa + 12 <= work.Length)
                    MdlRigger.WriteVector(work, pa, layout.PositionType, vs[v].P + d[v]);
            }
            // Normals: each moved vertex's normal turned by how the triangles round it turned.
            if(layout.NormalAt < 0 || layout.NormalType is not (2 or 3 or 14) || !face.Triangles.TryGetValue(mesh, out var t))
                continue;
            var before = new Dictionary<int, Vector3>();
            var after = new Dictionary<int, Vector3>();
            for(int i = 0; i + 2 < t.Length; i += 3)
            {
                int a = t[i], b = t[i + 1], c = t[i + 2];
                if(a >= d.Length || b >= d.Length || c >= d.Length)
                    continue;
                if(d[a] == Vector3.Zero && d[b] == Vector3.Zero && d[c] == Vector3.Zero)
                    continue;
                var f0 = Vector3.Cross(vs[b].P - vs[a].P, vs[c].P - vs[a].P);
                var f1 = Vector3.Cross(vs[b].P + d[b] - vs[a].P - d[a], vs[c].P + d[c] - vs[a].P - d[a]);
                foreach(int k in new[] { a, b, c })
                {
                    before[k] = before.GetValueOrDefault(k) + f0;
                    after[k] = after.GetValueOrDefault(k) + f1;
                }
            }
            foreach(int v in moved)
            {
                if(!before.TryGetValue(v, out var b0) || !after.TryGetValue(v, out var a0) || b0.LengthSquared() < 1e-20f || a0.LengthSquared() < 1e-20f)
                    continue;
                var rot = MathUtil.FromTo(Vector3.Normalize(b0), Vector3.Normalize(a0));
                int na = layout.NormalAt + v * layout.NormalStride;
                if(na + 6 > work.Length || vs[v].N0.LengthSquared() < 1e-12f)
                    continue;
                MdlRigger.WriteVector(work, na, layout.NormalType, Vector3.Transform(vs[v].N0, rot));
            }
        }
        return true;
    }

    /// <summary>The morphs' move of one vertex (for writers that place some vertices themselves).</summary>
    public static Vector3 Delta(string file, int byteWidth, int mesh, int vertex)
    {
        if(_weights.Count == 0)
            return Vector3.Zero;
        var face = Find(file, byteWidth);
        if(face == null)
            return Vector3.Zero;
        var mix = Mix(face);
        if(mix == null || !mix.Delta.TryGetValue(mesh, out var d) || vertex >= d.Length)
            return Vector3.Zero;
        return d[vertex];
    }
}
