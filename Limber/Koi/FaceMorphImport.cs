using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Limber.Gpu;
using Limber.Koi.Props;
using Newtonsoft.Json.Linq;

namespace Limber.Koi;

/// <summary>
/// Face morphs from Virt-a-Mate and Koikatsu (experimental), transferred onto a FFXIV face and saved next to Limber's
/// settings (never shipped with Limber: they're made on this PC, from the games installed on it).
///
/// The source face's features and the FFXIV face's are found the same way (mouth corners, where the lips meet, eyes, nose
/// tip, nostrils, chin, cheeks); a smooth warp (thin-plate spline) takes each FFXIV face vertex to the same place on the
/// source face; the nearest point of the source surface gives the morph's move there, sized from the source face to
/// this one (by the mouth's width and the eyes' height over it), faded out where the faces don't meet, then smoothed.
/// Virt-a-Mate: its female base head (Genesis 2) and morphs (its own and those in its packages and Custom folder).
/// Koikatsu: its face's blend shapes (mouth and eye patterns).
/// </summary>
public static class FaceMorphImport
{
    public sealed class Candidate
    {
        public string Source = "";     // "VaM" or "Koikatsu"
        public string Name = "";       // as listed
        public string Group = "";
        public string Origin = "";     // where it's from (package, file, built in)
        public bool Suggested;         // ticked to start with: made for this kind of face
        public int[] Vertex = [];      // the source mesh's vertices it moves...
        public Vector3[] Delta = [];   // ...and how far (FFXIV's axes)
        public string Key => Source + ": " + Name;
    }

    private sealed class SourceMesh
    {
        public required Vector3[] P;     // FFXIV's axes (+X the character's left), the source's own units
        public required int[] Tri;
        public required FaceMorphs.Landmarks Marks;
        public required bool[] Usable;   // per triangle: part of the face's skin (not eyes, teeth, tongue)
    }

    public static volatile string Status = "";
    public static volatile bool Busy;
    public static List<Candidate> Vam = new(), Koikatsu = new();
    private static SourceMesh? _vam, _koi;

    // ---- Names worth starting with

    private static readonly string[] VamSuggested =
    [
        "Blowjob Lips", "deepthroat", "BJ Cheeks - Suck Left", "BJ Cheeks - Suck Right", "BJ Cheeks - Balloon Left", "BJ Cheeks - Balloon Right",
        "BJ Lips - Center In-Out", "BJ Lips - Left In-Out", "BJ Lips - Move In-Out", "BJ Lips - Right In-Out", "PHMLipsPucker", "PHMLipsPuckerWide",
        "PHMCheeksBalloon", "PHMCheeksBalloonPucker", "PHMCheeksSinkL", "PHMCheeksSinkR", "Cheeks Sink Lower", "Nostrils Flare", "PHMNoseWrinkle",
        "PHMMouthOpenWide", "Mouth Open Wide 2", "PHMEyelidsTopDownL", "PHMEyelidsTopDownR", "PHMBrowSqueeze", "PHMBrowInnerUpL", "PHMBrowInnerUpR",
        "PHMPain", "PHMDesire", "AshAuryn_kiss1", "AshAuryn_kiss2", "AshAuryn_Pain1", "Breathing_NoseIn", "Breathing_NoseOut", "Breathing_Lips",
        "Lips Pucker (REN)", "LipsPuffOut", "PHMLipBottomOutL", "PHMLipBottomOutR", "PHMLipTopUpL", "PHMLipTopUpR",
    ];

    private static readonly string[] KoiSuggested = ["kuwae", "o_l", "kisu", "name", "a_l", "u_l", "eyes kurusi", "eyes naki", "eyes gag", "eyes sinken"];

    // ---- Virt-a-Mate

    public static void ScanVam(string folder) => Run(() => ScanVamNow(folder));

    private static void Run(Action work)
    {
        if(Busy)
            return;
        Busy = true;
        Task.Run(() =>
        {
            try
            {
                work();
            }
            catch(Exception e)
            {
                Status = "Couldn't: " + e.Message;
                Plugin.Log.Warning(e, "Limber: face morph import");
            }
            finally
            {
                Busy = false;
            }
        });
    }

    private static string? FindVam(string folder)
    {
        folder = folder.Trim().Trim('"');
        foreach(var f in new[] { folder, Path.Combine(folder, "VaM_1.22.0.3") })
            if(File.Exists(Path.Combine(f, "VaM_Data", "StreamingAssets", "a_per")))
                return f;
        return null;
    }

    private static void ScanVamNow(string folder)
    {
        var root = FindVam(folder);
        if(root == null)
        {
            Status = "That isn't Virt-a-Mate's folder (the one with VaM.exe and VaM_Data).";
            return;
        }
        Status = "Reading Virt-a-Mate's female head...";
        var mesh = ReadVamBase(Path.Combine(root, "VaM_Data", "StreamingAssets", "a_per"));
        if(mesh == null)
        {
            Status = "Couldn't find Virt-a-Mate's female base mesh (Genesis 2 Female) in VaM_Data.";
            return;
        }
        _vam = mesh;
        var list = new List<Candidate>();
        float headY = mesh.Marks.Chin.Y - 2.5f * mesh.Marks.Half; // below the chin a little: the throat
        bool Head(Candidate c)
        {
            if(c.Vertex.Length < 8)
                return false;
            int head = 0;
            foreach(var v in c.Vertex)
                if(v < mesh.P.Length && mesh.P[v].Y > headY)
                    head++;
            return head >= 8 && head >= c.Vertex.Length * 0.3f;
        }
        Status = "Reading Virt-a-Mate's own morphs...";
        var bank = Path.Combine(root, "VaM_Data", "StreamingAssets", "f_mb");
        if(File.Exists(bank))
            foreach(var c in ReadVamBank(bank))
                if(Head(c))
                    list.Add(c);
        Status = "Reading morphs in packages...";
        var packages = Path.Combine(root, "AddonPackages");
        if(Directory.Exists(packages))
            foreach(var pkg in Directory.EnumerateFiles(packages, "*.var", SearchOption.AllDirectories))
            {
                try
                {
                    using var zip = ZipFile.OpenRead(pkg);
                    foreach(var entry in zip.Entries)
                    {
                        var n = entry.FullName.Replace('\\', '/');
                        if(!n.EndsWith(".vmi", StringComparison.OrdinalIgnoreCase) || !n.Contains("/Morphs/female/", StringComparison.OrdinalIgnoreCase))
                            continue;
                        var vmb = zip.GetEntry(n[..^4] + ".vmb");
                        if(vmb == null)
                            continue;
                        var c = FromVmi(ReadAll(entry), ReadAll(vmb), Path.GetFileNameWithoutExtension(pkg));
                        if(c != null && Head(c))
                            list.Add(c);
                    }
                }
                catch
                {
                    // (a broken package: skipped)
                }
            }
        var custom = Path.Combine(root, "Custom", "Atom", "Person", "Morphs", "female");
        if(Directory.Exists(custom))
            foreach(var vmi in Directory.EnumerateFiles(custom, "*.vmi", SearchOption.AllDirectories))
            {
                var vmb = vmi[..^4] + ".vmb";
                if(!File.Exists(vmb))
                    continue;
                var c = FromVmi(File.ReadAllBytes(vmi), File.ReadAllBytes(vmb), "Custom");
                if(c != null && Head(c))
                    list.Add(c);
            }
        // (the same name twice: the first kept)
        var seen = new HashSet<string>();
        list = list.Where(c => seen.Add(c.Name)).OrderBy(c => c.Suggested ? 0 : 1).ThenBy(c => c.Group).ThenBy(c => c.Name).ToList();
        Vam = list;
        Status = $"Virt-a-Mate: {list.Count} face morphs found ({list.Count(c => c.Suggested)} suggested).";
    }

    private static byte[] ReadAll(ZipArchiveEntry e)
    {
        using var s = e.Open();
        using var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    }

    private static Candidate? FromVmi(byte[] vmi, byte[] vmb, string origin)
    {
        string name, group;
        try
        {
            var j = JObject.Parse(System.Text.Encoding.UTF8.GetString(vmi).TrimStart('﻿'));
            name = (string?)j["displayName"] ?? (string?)j["id"] ?? "";
            group = (string?)j["group"] ?? (string?)j["region"] ?? "";
        }
        catch
        {
            return null;
        }
        if(name.Length == 0 || vmb.Length < 4)
            return null;
        int n = BitConverter.ToInt32(vmb, 0);
        if(n <= 0 || 4 + n * 16 > vmb.Length)
            return null;
        var v = new int[n];
        var d = new Vector3[n];
        for(int i = 0; i < n; i++)
        {
            v[i] = BitConverter.ToInt32(vmb, 4 + i * 16);
            d[i] = new Vector3(-BitConverter.ToSingle(vmb, 8 + i * 16), BitConverter.ToSingle(vmb, 12 + i * 16), BitConverter.ToSingle(vmb, 16 + i * 16));
        }
        return new Candidate { Source = "VaM", Name = name, Group = group, Origin = origin, Vertex = v, Delta = d, Suggested = VamSuggested.Contains(name) };
    }

    private static SerializedFile? Serialized(string bundle, out byte[] data)
    {
        data = [];
        var files = UnityFs.Open(File.ReadAllBytes(bundle));
        foreach(var (name, bytes) in files)
            if(!name.EndsWith(".resS", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".resource", StringComparison.OrdinalIgnoreCase))
            {
                data = bytes;
                return new SerializedFile(bytes);
            }
        return null;
    }

    private static bool Has(TypeNode? tree, string field) => tree != null && tree.Children.Any(c => c.Name == field);

    // VaM's female base mesh: the DAZMesh "GenesisFemale" (21556 vertices), with its polygons and their materials.
    private static SourceMesh? ReadVamBase(string bundle)
    {
        var sf = Serialized(bundle, out _);
        if(sf == null)
            return null;
        foreach(var (id, e) in sf.Objects)
        {
            if(e.ClassId != 114 || e.Size < 500_000 || !Has(e.Tree, "_baseVertices") || !Has(e.Tree, "_basePolyList"))
                continue;
            var o = sf.Read(id);
            if(o == null || o.I("_numBaseVertices") != 21556 || o.S("nodeId") != "GenesisFemale")
                continue;
            var verts = o.L("_baseVertices");
            var p = new Vector3[verts.Count];
            for(int i = 0; i < p.Length; i++)
                p[i] = new Vector3(-verts[i].F("x"), verts[i].F("y"), verts[i].F("z"));
            var mats = o.L("_materialNames").Select(x => x as string ?? "").ToList();
            var tri = new List<int>();
            var usable = new List<bool>();
            var lips = new HashSet<int>();
            var irisL = new List<Vector3>();
            var irisR = new List<Vector3>();
            var nostrils = new List<Vector3>();
            var skinMats = new HashSet<string>(["Face", "Lips", "Nostrils", "Head", "Ears", "Neck", "InnerMouth"]);
            foreach(var poly in o.L("_basePolyList"))
            {
                int m = (int)poly.I("materialNum");
                string mat = m >= 0 && m < mats.Count ? mats[m] : "";
                var vs = poly.L("vertices").Select(x => x is int i ? i : Convert.ToInt32(x)).Where(x => x >= 0 && x < p.Length).ToList();
                if(vs.Count < 3)
                    continue;
                for(int k = 1; k + 1 < vs.Count; k++)
                {
                    tri.Add(vs[0]); tri.Add(vs[k]); tri.Add(vs[k + 1]);
                    usable.Add(skinMats.Contains(mat));
                }
                if(mat == "Lips")
                    foreach(var v in vs)
                        lips.Add(v);
                else if(mat == "Irises")
                    foreach(var v in vs)
                        (p[v].X > 0 ? irisL : irisR).Add(p[v]);
                else if(mat == "Nostrils")
                    foreach(var v in vs)
                        nostrils.Add(p[v]);
            }
            if(lips.Count < 20 || irisL.Count == 0 || irisR.Count == 0)
                return null;
            var marks = new FaceMorphs.Landmarks();
            var lp = lips.Select(v => p[v]).ToList();
            float cx = lp.Average(a => a.X), cy = lp.Average(a => a.Y), cz = lp.Average(a => a.Z);
            float half = lp.Max(a => MathF.Abs(a.X - cx));
            marks.Mouth = new Vector3(cx, cy, cz);
            marks.Half = half;
            marks.CornerY = lp.Where(a => MathF.Abs(a.X - cx) > half * 0.85f).Select(a => a.Y).DefaultIfEmpty(cy).Average();
            // where the lips meet: the lips' points deepest in, near the middle
            var mid = lp.Where(a => MathF.Abs(a.X - cx) < half * 0.3f).ToList();
            marks.MidY = mid.Count > 0 ? mid.OrderBy(a => a.Z).First().Y : cy;
            marks.EyeL = Mean(irisL);
            marks.EyeR = Mean(irisR);
            var skin = new List<Vector3>();
            for(int t = 0; t < usable.Count; t++)
                if(usable[t])
                    for(int k = 0; k < 3; k++)
                        skin.Add(p[tri[t * 3 + k]]);
            FinishMarks(marks, skin);
            var nl = nostrils.Where(a => a.X > cx).ToList();
            var nr = nostrils.Where(a => a.X < cx).ToList();
            if(nl.Count > 0) marks.NostrilL = Mean(nl);
            if(nr.Count > 0) marks.NostrilR = Mean(nr);
            return new SourceMesh { P = p, Tri = tri.ToArray(), Usable = usable.ToArray(), Marks = marks };
        }
        return null;
    }

    private static Vector3 Mean(List<Vector3> list)
    {
        var s = Vector3.Zero;
        foreach(var v in list)
            s += v;
        return list.Count > 0 ? s / list.Count : Vector3.Zero;
    }

    // The nose, chin and cheeks, the same way as on the FFXIV face (FaceMorphs.Read).
    private static void FinishMarks(FaceMorphs.Landmarks m, List<Vector3> all)
    {
        float cx = m.Mouth.X, cz = m.Mouth.Z, h = m.Half;
        float eyeY = (m.EyeL.Y + m.EyeR.Y) * 0.5f;
        var nose = all.Where(a => MathF.Abs(a.X - cx) < 0.35f * h && a.Y > m.MidY + 0.4f * h && a.Y < eyeY - 0.3f * h).ToList();
        m.NoseTip = nose.Count > 0 ? nose.OrderByDescending(a => a.Z).First() : new Vector3(cx, m.MidY + 1.2f * h, cz + 0.5f * h);
        m.NoseBaseY = m.NoseTip.Y - 0.35f * h;
        m.NostrilL = m.NoseTip + new Vector3(0.3f * h, -0.1f * h, -0.55f * h);
        m.NostrilR = m.NoseTip + new Vector3(-0.3f * h, -0.1f * h, -0.55f * h);
        var chin = all.Where(a => MathF.Abs(a.X - cx) < 0.3f * h && a.Y < m.MidY - 0.5f * h && a.Z > cz - 1.2f * h).ToList();
        m.Chin = chin.Count > 0 ? chin.OrderBy(a => a.Y).First() : new Vector3(cx, m.MidY - 1.6f * h, cz - 0.3f * h);
        Vector3 Surface(float x, float y)
        {
            var near = all.Where(a => MathF.Abs(a.X - x) < 0.3f * h && MathF.Abs(a.Y - y) < 0.3f * h).ToList();
            return new Vector3(x, y, near.Count > 0 ? near.Max(a => a.Z) : cz - 0.6f * h);
        }
        float cheekY = m.MidY + 0.45f * (eyeY - m.MidY);
        m.CheekL = Surface(cx + 1.55f * h, cheekY);
        m.CheekR = Surface(cx - 1.55f * h, cheekY);
    }

    // VaM's own morphs (its morph bank: DAZMorph lists in MonoBehaviours), read without making an object per move.
    private static List<Candidate> ReadVamBank(string bundle)
    {
        var list = new List<Candidate>();
        var sf = Serialized(bundle, out var data);
        if(sf == null)
            return list;
        foreach(var (_, e) in sf.Objects)
        {
            if(e.ClassId != 114 || e.Tree == null || !Has(e.Tree, "_morphs"))
                continue;
            var r = new Reader(data, false) { P = e.Start };
            try
            {
                foreach(var c in e.Tree.Children)
                {
                    if(c.Name == "_morphs")
                        ReadMorphList(r, c, list);
                    else
                        Skip(r, c);
                }
            }
            catch
            {
                // (an object that didn't read: skipped)
            }
        }
        return list;
    }

    private static void ReadMorphList(Reader r, TypeNode n, List<Candidate> into)
    {
        var arr = n.Children[0];
        int count = r.I32();
        var el = arr.Children[1];
        for(int i = 0; i < count; i++)
        {
            string name = "", display = "", region = "", group = "";
            int[] v = [];
            Vector3[] d = [];
            foreach(var f in el.Children)
            {
                switch(f.Name)
                {
                    case "morphName": name = ReadString(r, f); break;
                    case "displayName": display = ReadString(r, f); break;
                    case "region": region = ReadString(r, f); break;
                    case "group": group = ReadString(r, f); break;
                    case "deltas":
                    {
                        var a = f.Children[0];
                        int k = r.I32();
                        var vert = a.Children[1];
                        bool simple = vert.Children.Count == 2 && vert.Children[0].Type == "int" && vert.Children[1].Children.Count == 3;
                        v = new int[k];
                        d = new Vector3[k];
                        for(int j = 0; j < k; j++)
                        {
                            if(simple)
                            {
                                v[j] = r.I32();
                                float x = r.F32(), y = r.F32(), z = r.F32();
                                d[j] = new Vector3(-x, y, z);
                            }
                            else
                                Skip(r, vert);
                        }
                        if(a.Aligned || f.Aligned)
                            r.Align(4);
                        break;
                    }
                    default:
                        Skip(r, f);
                        break;
                }
            }
            if(el.Aligned)
                r.Align(4);
            if(v.Length > 0 && name.Length > 0)
                into.Add(new Candidate
                {
                    Source = "VaM", Name = name, Group = group.Length > 0 ? group : region, Origin = "Virt-a-Mate (built in)",
                    Vertex = v, Delta = d, Suggested = VamSuggested.Contains(name) || VamSuggested.Contains(display),
                });
        }
        if(arr.Aligned || n.Aligned)
            r.Align(4);
    }

    private static string ReadString(Reader r, TypeNode n)
    {
        int len = r.I32();
        var s = System.Text.Encoding.UTF8.GetString(r.B, r.P, len);
        r.P += len;
        r.Align(4);
        return s;
    }

    // Past one value without keeping it (the same rules as SerializedFile reads them).
    private static void Skip(Reader r, TypeNode n)
    {
        bool align = n.Aligned;
        switch(n.Type)
        {
            case "SInt8": case "UInt8": case "char": case "bool": r.P += 1; break;
            case "SInt16": case "short": case "UInt16": case "unsigned short": r.P += 2; break;
            case "SInt32": case "int": case "UInt32": case "unsigned int": case "Type*": case "float": r.P += 4; break;
            case "SInt64": case "long long": case "UInt64": case "unsigned long long": case "FileSize": case "double": r.P += 8; break;
            case "string":
            {
                int len = r.I32();
                r.P += len;
                r.Align(4);
                break;
            }
            case "TypelessData":
            {
                int len = r.I32();
                r.P += len;
                break;
            }
            default:
                if(n.Children.Count > 0 && n.Children[0].Type == "Array")
                {
                    var arr = n.Children[0];
                    if(arr.Aligned)
                        align = true;
                    int len = r.I32();
                    var el = arr.Children[1];
                    int fixedSize = FixedSize(el);
                    if(fixedSize > 0)
                        r.P += fixedSize * len;
                    else
                        for(int i = 0; i < len; i++)
                            Skip(r, el);
                }
                else
                    foreach(var c in n.Children)
                        Skip(r, c);
                break;
        }
        if(align)
            r.Align(4);
    }

    // A value's size when it's always the same and needs no lining up inside (else 0).
    private static int FixedSize(TypeNode n)
    {
        int Leaf(string t) => t switch
        {
            "SInt8" or "UInt8" or "char" or "bool" => 1,
            "SInt16" or "short" or "UInt16" or "unsigned short" => 2,
            "SInt32" or "int" or "UInt32" or "unsigned int" or "float" or "Type*" => 4,
            "SInt64" or "long long" or "UInt64" or "unsigned long long" or "double" or "FileSize" => 8,
            _ => 0,
        };
        if(n.Children.Count == 0)
            return n.Aligned && Leaf(n.Type) % 4 != 0 ? 0 : Leaf(n.Type);
        if(n.Aligned || n.Type == "string" || n.Children[0].Type == "Array")
            return 0;
        int size = 0;
        foreach(var c in n.Children)
        {
            int s = FixedSize(c);
            if(s == 0)
                return 0;
            size += s;
        }
        return size;
    }

    // ---- Koikatsu

    public static void ScanKoikatsu(string folder) => Run(() => ScanKoikatsuNow(folder));

    private static void ScanKoikatsuNow(string folder)
    {
        var path = Path.Combine(folder.Trim().Trim('"'), "abdata", "chara", "bo_head_00.unity3d");
        if(!File.Exists(path))
        {
            Status = "Koikatsu's face wasn't found (abdata/chara/bo_head_00.unity3d in Koikatsu's folder; set it on Koikatsu > Import props).";
            return;
        }
        Status = "Reading Koikatsu's face...";
        var sf = Serialized(path, out _);
        if(sf == null)
            return;
        Dictionary<string, object?>? face = null;
        foreach(var (id, e) in sf.Objects)
        {
            if(e.ClassId != 43)
                continue;
            var o = sf.Read(id);
            if(o != null && o.S("m_Name") == "cf_O_face")
            {
                face = o;
                break;
            }
        }
        var mesh = face == null ? null : MeshData.Read(face);
        if(face == null || mesh == null)
        {
            Status = "Koikatsu's face mesh (cf_O_face) wasn't in its face file.";
            return;
        }
        var p = mesh.P.Select(v => new Vector3(-v.X, v.Y, v.Z)).ToArray();
        var shapes = MeshData.Shapes(face).ToList();
        (int[] V, Vector3[] D) Shape(string name)
        {
            var s = shapes.FirstOrDefault(x => x.Name == name);
            return s.Deltas == null ? ([], []) : (s.Deltas.Select(x => x.Vertex).ToArray(), s.Deltas.Select(x => new Vector3(-x.Delta.X, x.Delta.Y, x.Delta.Z)).ToArray());
        }
        float[] Size((int[] V, Vector3[] D) s)
        {
            var a = new float[p.Length];
            for(int i = 0; i < s.V.Length; i++)
                if(s.V[i] < a.Length)
                    a[s.V[i]] = s.D[i].Length();
            return a;
        }
        // Where its features are: the lips from the kiss shape, the corners from the wide "i", the eyes from closing them.
        var kisu = Size(Shape("kuti_face.f00_kisu_op"));
        var wide = Size(Shape("kuti_face.f00_i_l_op"));
        var shut = Size(Shape("eye_face.f00_def_cl"));
        float kmax = kisu.Max(), wmax = wide.Max(), emax = shut.Max();
        if(kmax <= 0f || emax <= 0f)
        {
            Status = "Koikatsu's face shapes weren't as expected (no kiss / eyes closed shapes).";
            return;
        }
        var marks = new FaceMorphs.Landmarks();
        Vector3 sum = Vector3.Zero;
        float wsum = 0f;
        for(int i = 0; i < p.Length; i++)
            if(kisu[i] > 0.5f * kmax)
            {
                sum += p[i] * kisu[i];
                wsum += kisu[i];
            }
        marks.Mouth = sum / MathF.Max(wsum, 1e-9f);
        float half = 0f;
        for(int i = 0; i < p.Length; i++)
            if(wmax > 0f && wide[i] > 0.4f * wmax && MathF.Abs(p[i].Y - marks.Mouth.Y) < 0.02f)
                half = MathF.Max(half, MathF.Abs(p[i].X - marks.Mouth.X));
        marks.Half = half > 0.004f ? half : 0.021f;
        marks.MidY = marks.CornerY = marks.Mouth.Y;
        var eyes = Enumerable.Range(0, p.Length).Where(i => shut[i] > 0.4f * emax).Select(i => p[i]).ToList();
        marks.EyeL = Mean(eyes.Where(v => v.X > marks.Mouth.X).ToList());
        marks.EyeR = Mean(eyes.Where(v => v.X < marks.Mouth.X).ToList());
        FinishMarks(marks, p.ToList());
        var tri = mesh.Indices.Length / 3 * 3 == mesh.Indices.Length ? mesh.Indices : mesh.Indices[..(mesh.Indices.Length / 3 * 3)];
        _koi = new SourceMesh { P = p, Tri = tri, Usable = Enumerable.Repeat(true, tri.Length / 3).ToArray(), Marks = marks };
        var list = new List<Candidate>();
        foreach(var (name, deltas) in shapes)
        {
            string shortName;
            if(name.StartsWith("kuti_face.f00_", StringComparison.Ordinal))
                shortName = name["kuti_face.f00_".Length..];
            else if(name.StartsWith("eye_face.f00_", StringComparison.Ordinal))
                shortName = "eyes " + name["eye_face.f00_".Length..];
            else
                continue;
            if(shortName.EndsWith("_op", StringComparison.Ordinal))
                shortName = shortName[..^3];
            else if(shortName.EndsWith("_cl", StringComparison.Ordinal))
                shortName = shortName[..^3] + " (closed)";
            var s = Shape(name);
            list.Add(new Candidate
            {
                Source = "Koikatsu", Name = shortName, Group = name.StartsWith("kuti", StringComparison.Ordinal) ? "Mouth" : "Eyes",
                Origin = "Koikatsu's face", Vertex = s.V, Delta = s.D, Suggested = KoiSuggested.Contains(shortName),
            });
        }
        Koikatsu = list.OrderBy(c => c.Suggested ? 0 : 1).ThenBy(c => c.Group).ThenBy(c => c.Name).ToList();
        Status = $"Koikatsu: {list.Count} face shapes found ({list.Count(c => c.Suggested)} suggested).";
    }

    // ---- Onto a FFXIV face

    public static void Import(FaceMorphs.Face face, List<Candidate> picked) => Run(() => ImportNow(face, picked));

    private static void ImportNow(FaceMorphs.Face face, List<Candidate> picked)
    {
        var made = new List<FaceMorphs.Morph>();
        foreach(var group in picked.GroupBy(c => c.Source))
        {
            var src = group.Key == "VaM" ? _vam : _koi;
            if(src == null)
                continue;
            Status = $"Fitting {group.Key}'s face onto this one...";
            var map = Map(face, src);
            int n = 0;
            foreach(var c in group)
            {
                Status = $"{group.Key}: {c.Name} ({++n} of {group.Count()})...";
                var m = Transfer(face, src, map, c);
                if(m != null)
                    made.Add(m);
            }
        }
        if(made.Count == 0)
        {
            Status = "Nothing was imported (scan first, then tick some).";
            return;
        }
        FaceMorphs.SaveImported(face, made);
        Status = $"Imported {made.Count} morphs onto this face. They're on the Face morphs page now.";
    }

    // Per FFXIV face vertex: the source triangle it lands on, where in it, and how far off the surface.
    private sealed class Hit
    {
        public int Tri = -1;
        public Vector3 Bary;
        public float Distance;
    }

    private static Dictionary<int, Hit[]> Map(FaceMorphs.Face face, SourceMesh src)
    {
        var warp = Warp(face.Marks.All, src.Marks.All);
        var grid = new TriGrid(src, src.Marks.Half * 0.35f);
        var result = new Dictionary<int, Hit[]>();
        foreach(var (mesh, verts) in face.Vertices)
        {
            var hits = new Hit[verts.Length];
            for(int v = 0; v < verts.Length; v++)
            {
                hits[v] = new Hit();
                if(verts[v].Skip > 0.5f)
                    continue;
                var q = warp(verts[v].P);
                grid.Closest(q, src.Marks.Half * 1.5f, hits[v]);
            }
            result[mesh] = hits;
        }
        return result;
    }

    private static FaceMorphs.Morph? Transfer(FaceMorphs.Face face, SourceMesh src, Dictionary<int, Hit[]> map, Candidate c)
    {
        var dense = new Vector3[src.P.Length];
        for(int i = 0; i < c.Vertex.Length; i++)
            if(c.Vertex[i] >= 0 && c.Vertex[i] < dense.Length)
                dense[c.Vertex[i]] = c.Delta[i];
        var t = face.Marks;
        var s = src.Marks;
        float sx = t.Half / MathF.Max(1e-6f, s.Half);
        float teye = (t.EyeL.Y + t.EyeR.Y) * 0.5f - t.MidY, seye = (s.EyeL.Y + s.EyeR.Y) * 0.5f - s.MidY;
        float sy = MathF.Abs(seye) > 1e-6f ? teye / seye : sx;
        float sz = (sx + sy) * 0.5f;
        var moves = new Dictionary<int, Vector3[]>();
        foreach(var (mesh, hits) in map)
        {
            var verts = face.Vertices[mesh];
            var d = new Vector3[hits.Length];
            for(int v = 0; v < hits.Length; v++)
            {
                var h = hits[v];
                if(h.Tri < 0)
                    continue;
                float fade = 1f - Smooth((h.Distance / s.Half - 0.25f) / 0.35f);
                float keep = (1f - Math.Clamp(verts[v].Skip * 2f, 0f, 1f)) * (1f - Math.Clamp(verts[v].Neck * 1.5f, 0f, 1f));
                if(fade * keep <= 0f)
                    continue;
                int a = src.Tri[h.Tri * 3], b = src.Tri[h.Tri * 3 + 1], cc = src.Tri[h.Tri * 3 + 2];
                var m = dense[a] * h.Bary.X + dense[b] * h.Bary.Y + dense[cc] * h.Bary.Z;
                d[v] = new Vector3(m.X * sx, m.Y * sy, m.Z * sz) * fade * keep;
            }
            moves[mesh] = d;
        }
        SmoothMoves(face, moves, 2, 0.35f);
        var meshes = new List<int>();
        var vs = new List<int>();
        var ds = new List<Vector3>();
        foreach(var (mesh, d) in moves)
            for(int v = 0; v < d.Length; v++)
                if(d[v].LengthSquared() > 1e-14f)
                {
                    meshes.Add(mesh);
                    vs.Add(v);
                    ds.Add(d[v]);
                }
        if(vs.Count == 0)
            return null;
        return new FaceMorphs.Morph
        {
            Name = c.Key, Group = c.Source + ": " + (c.Group.Length > 0 ? c.Group : "Other"), Source = c.Source, Tip = $"From {c.Origin}.",
            Mesh = meshes.ToArray(), Vertex = vs.ToArray(), Delta = ds.ToArray(),
        };
    }

    private static float Smooth(float u)
    {
        u = Math.Clamp(u, 0f, 1f);
        return u * u * (3f - 2f * u);
    }

    // A few rounds of each vertex's move eased toward its neighbours' (vertices at one place, split at seams, as one).
    private static void SmoothMoves(FaceMorphs.Face face, Dictionary<int, Vector3[]> moves, int rounds, float k)
    {
        var ids = new Dictionary<int, int[]>();
        var cells = new Dictionary<(int, int, int), int>();
        foreach(var (mesh, verts) in face.Vertices)
        {
            var a = new int[verts.Length];
            for(int v = 0; v < verts.Length; v++)
            {
                var p = verts[v].P;
                var key = ((int)MathF.Round(p.X * 1e5f), (int)MathF.Round(p.Y * 1e5f), (int)MathF.Round(p.Z * 1e5f));
                if(!cells.TryGetValue(key, out int id))
                    cells[key] = id = cells.Count;
                a[v] = id;
            }
            ids[mesh] = a;
        }
        int n = cells.Count;
        var near = new HashSet<int>[n];
        for(int i = 0; i < n; i++)
            near[i] = new HashSet<int>();
        foreach(var (mesh, t) in face.Triangles)
        {
            if(!ids.TryGetValue(mesh, out var a))
                continue;
            for(int i = 0; i + 2 < t.Length; i += 3)
            {
                if(t[i] >= a.Length || t[i + 1] >= a.Length || t[i + 2] >= a.Length)
                    continue;
                int x = a[t[i]], y = a[t[i + 1]], z = a[t[i + 2]];
                near[x].Add(y); near[x].Add(z); near[y].Add(x); near[y].Add(z); near[z].Add(x); near[z].Add(y);
            }
        }
        var w = new Vector3[n];
        var count = new int[n];
        foreach(var (mesh, d) in moves)
            for(int v = 0; v < d.Length; v++)
            {
                w[ids[mesh][v]] += d[v];
                count[ids[mesh][v]]++;
            }
        for(int i = 0; i < n; i++)
            if(count[i] > 1)
                w[i] /= count[i];
        for(int round = 0; round < rounds; round++)
        {
            var next = new Vector3[n];
            for(int i = 0; i < n; i++)
            {
                if(near[i].Count == 0)
                {
                    next[i] = w[i];
                    continue;
                }
                var avg = Vector3.Zero;
                foreach(var j in near[i])
                    avg += w[j];
                avg /= near[i].Count;
                next[i] = w[i] * (1f - k) + avg * k;
            }
            w = next;
        }
        foreach(var (mesh, d) in moves)
        {
            var verts = face.Vertices[mesh];
            for(int v = 0; v < d.Length; v++)
                d[v] = verts[v].Skip > 0.5f ? Vector3.Zero : w[ids[mesh][v]];
        }
    }

    // ---- The warp: a thin-plate spline taking the FFXIV face's features to the source's

    private static Func<Vector3, Vector3> Warp(Vector3[] from, Vector3[] to)
    {
        int n = from.Length, size = n + 4;
        var a = new double[size, size];
        var b = new double[size, 3];
        for(int i = 0; i < n; i++)
        {
            for(int j = 0; j < n; j++)
                a[i, j] = Vector3.Distance(from[i], from[j]) + (i == j ? 1e-9 : 0);
            a[i, n] = 1; a[i, n + 1] = from[i].X; a[i, n + 2] = from[i].Y; a[i, n + 3] = from[i].Z;
            a[n, i] = 1; a[n + 1, i] = from[i].X; a[n + 2, i] = from[i].Y; a[n + 3, i] = from[i].Z;
            b[i, 0] = to[i].X; b[i, 1] = to[i].Y; b[i, 2] = to[i].Z;
        }
        var x = Solve(a, b, size);
        return p =>
        {
            double rx = x[n, 0] + x[n + 1, 0] * p.X + x[n + 2, 0] * p.Y + x[n + 3, 0] * p.Z;
            double ry = x[n, 1] + x[n + 1, 1] * p.X + x[n + 2, 1] * p.Y + x[n + 3, 1] * p.Z;
            double rz = x[n, 2] + x[n + 1, 2] * p.X + x[n + 2, 2] * p.Y + x[n + 3, 2] * p.Z;
            for(int i = 0; i < n; i++)
            {
                double u = Vector3.Distance(p, from[i]);
                rx += x[i, 0] * u; ry += x[i, 1] * u; rz += x[i, 2] * u;
            }
            return new Vector3((float)rx, (float)ry, (float)rz);
        };
    }

    // Gaussian elimination with partial pivoting (a: size x size, b: size x 3).
    private static double[,] Solve(double[,] a, double[,] b, int size)
    {
        for(int col = 0; col < size; col++)
        {
            int pivot = col;
            for(int r = col + 1; r < size; r++)
                if(Math.Abs(a[r, col]) > Math.Abs(a[pivot, col]))
                    pivot = r;
            if(pivot != col)
            {
                for(int c = 0; c < size; c++)
                    (a[col, c], a[pivot, c]) = (a[pivot, c], a[col, c]);
                for(int c = 0; c < 3; c++)
                    (b[col, c], b[pivot, c]) = (b[pivot, c], b[col, c]);
            }
            double d = a[col, col];
            if(Math.Abs(d) < 1e-15)
                continue;
            for(int r = 0; r < size; r++)
            {
                if(r == col)
                    continue;
                double f = a[r, col] / d;
                if(f == 0)
                    continue;
                for(int c = col; c < size; c++)
                    a[r, c] -= f * a[col, c];
                for(int c = 0; c < 3; c++)
                    b[r, c] -= f * b[col, c];
            }
        }
        var x = new double[size, 3];
        for(int r = 0; r < size; r++)
            for(int c = 0; c < 3; c++)
                x[r, c] = Math.Abs(a[r, r]) > 1e-15 ? b[r, c] / a[r, r] : 0;
        return x;
    }

    // ---- The source's triangles in a grid, for the nearest point

    private sealed class TriGrid
    {
        private readonly SourceMesh _m;
        private readonly float _cell;
        private readonly Dictionary<(int, int, int), List<int>> _cells = new();

        public TriGrid(SourceMesh m, float cell)
        {
            _m = m;
            _cell = MathF.Max(cell, 1e-4f);
            for(int t = 0; t < m.Tri.Length / 3; t++)
            {
                if(!m.Usable[t])
                    continue;
                var a = m.P[m.Tri[t * 3]];
                var b = m.P[m.Tri[t * 3 + 1]];
                var c = m.P[m.Tri[t * 3 + 2]];
                var lo = Vector3.Min(a, Vector3.Min(b, c));
                var hi = Vector3.Max(a, Vector3.Max(b, c));
                var (x0, y0, z0) = Cell(lo);
                var (x1, y1, z1) = Cell(hi);
                for(int x = x0; x <= x1; x++)
                    for(int y = y0; y <= y1; y++)
                        for(int z = z0; z <= z1; z++)
                        {
                            if(!_cells.TryGetValue((x, y, z), out var list))
                                _cells[(x, y, z)] = list = new List<int>();
                            list.Add(t);
                        }
            }
        }

        private (int, int, int) Cell(Vector3 p) => ((int)MathF.Floor(p.X / _cell), (int)MathF.Floor(p.Y / _cell), (int)MathF.Floor(p.Z / _cell));

        public void Closest(Vector3 q, float reach, Hit hit)
        {
            var (cx, cy, cz) = Cell(q);
            int rings = (int)MathF.Ceiling(reach / _cell);
            float best = float.MaxValue;
            for(int ring = 0; ring <= rings; ring++)
            {
                for(int x = cx - ring; x <= cx + ring; x++)
                    for(int y = cy - ring; y <= cy + ring; y++)
                        for(int z = cz - ring; z <= cz + ring; z++)
                        {
                            if(Math.Max(Math.Abs(x - cx), Math.Max(Math.Abs(y - cy), Math.Abs(z - cz))) != ring)
                                continue;
                            if(!_cells.TryGetValue((x, y, z), out var list))
                                continue;
                            foreach(int t in list)
                            {
                                var a = _m.P[_m.Tri[t * 3]];
                                var b = _m.P[_m.Tri[t * 3 + 1]];
                                var c = _m.P[_m.Tri[t * 3 + 2]];
                                var bary = ClosestBary(q, a, b, c);
                                var at = a * bary.X + b * bary.Y + c * bary.Z;
                                float d = Vector3.Distance(at, q);
                                if(d < best)
                                {
                                    best = d;
                                    hit.Tri = t;
                                    hit.Bary = bary;
                                    hit.Distance = d;
                                }
                            }
                        }
                // (found one nearer than the next ring could hold)
                if(best <= ring * _cell)
                    break;
            }
        }

        // Real-Time Collision Detection's closest point on a triangle, as barycentric weights.
        private static Vector3 ClosestBary(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            var ab = b - a;
            var ac = c - a;
            var ap = p - a;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            if(d1 <= 0 && d2 <= 0)
                return new Vector3(1, 0, 0);
            var bp = p - b;
            float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if(d3 >= 0 && d4 <= d3)
                return new Vector3(0, 1, 0);
            float vc = d1 * d4 - d3 * d2;
            if(vc <= 0 && d1 >= 0 && d3 <= 0)
            {
                float v = d1 / (d1 - d3);
                return new Vector3(1 - v, v, 0);
            }
            var cp = p - c;
            float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
            if(d6 >= 0 && d5 <= d6)
                return new Vector3(0, 0, 1);
            float vb = d5 * d2 - d1 * d6;
            if(vb <= 0 && d2 >= 0 && d6 <= 0)
            {
                float w = d2 / (d2 - d6);
                return new Vector3(1 - w, 0, w);
            }
            float va = d3 * d6 - d5 * d4;
            if(va <= 0 && (d4 - d3) >= 0 && (d5 - d6) >= 0)
            {
                float w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
                return new Vector3(0, 1 - w, w);
            }
            float den = 1f / (va + vb + vc);
            float v2 = vb * den, w2 = vc * den;
            return new Vector3(1 - v2 - w2, v2, w2);
        }
    }
}
