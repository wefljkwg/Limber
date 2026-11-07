using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Numerics;

namespace Limber.Koi.Props;

/// <summary>
/// What's needed to pose, reshape and recolour a converted Koikatsu prop live, saved next to Limber's settings when
/// it's converted: its meshes (as built into the model), its bones (as they rest, in the prefab's own Unity space),
/// its blend shapes, its dynamic bone chains, and each material's colour slots with the textures to colour.
/// </summary>
public sealed class KoiRig
{
    public sealed class Bone
    {
        public string Name = "";
        public int Parent = -1;
        public Matrix4x4 Rest;       // its place in the prefab (Unity space, row vectors)
        public Matrix4x4 RestLocal;  // relative to its parent
    }

    public sealed class Chain
    {
        public int Root;
        public float Damping = 0.1f, Elasticity = 0.1f, Stiffness = 0.1f, Inert, Radius;
        public Vector3 Gravity, Force;
    }

    public sealed class Material
    {
        public string Name = "";
        public int W, H;
        public byte[] Main = [];      // RGBA
        public byte[]? Mask;          // RGBA (same size): red, green, blue say where each colour goes
        public Vector4[] Colours = [Vector4.One, Vector4.One, Vector4.One];
        public int Slots;             // how many colours it has (1 to 3)
    }

    public List<PropMesh> Meshes = new();
    public List<Bone> Bones = new();
    public List<Chain> Chains = new();
    public List<Material> Materials = new();

    public bool Posable => Bones.Count > 1 || Chains.Count > 0;
    public IEnumerable<string> ShapeNames
    {
        get
        {
            var seen = new HashSet<string>();
            foreach(var m in Meshes)
                foreach(var k in m.Shapes.Keys)
                    if(seen.Add(k))
                        yield return k;
        }
    }

    // ---- Saving (deflated binary)

    private const int Version = 1;

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = File.Create(path);
        using var z = new DeflateStream(file, CompressionLevel.Fastest);
        using var w = new BinaryWriter(z);
        w.Write(Version);
        w.Write(Meshes.Count);
        foreach(var m in Meshes)
        {
            w.Write(m.Material);
            w.Write(m.P.Count);
            for(int i = 0; i < m.P.Count; i++)
            {
                W3(w, m.P[i]);
                W3(w, i < m.N.Count ? m.N[i] : Vector3.UnitY);
                W4(w, i < m.T.Count ? m.T[i] : new Vector4(1, 0, 0, 1));
                var uv = i < m.UV.Count ? m.UV[i] : Vector2.Zero;
                w.Write(uv.X);
                w.Write(uv.Y);
                var b = i < m.Bones.Count ? m.Bones[i] : (0, 0, 0, 0);
                w.Write(b.Item1); w.Write(b.Item2); w.Write(b.Item3); w.Write(b.Item4);
                W4(w, i < m.Weights.Count ? m.Weights[i] : new Vector4(1, 0, 0, 0));
            }
            w.Write(m.I.Count);
            foreach(var i in m.I)
                w.Write(i);
            w.Write(m.Shapes.Count);
            foreach(var (name, list) in m.Shapes)
            {
                w.Write(name);
                w.Write(list.Count);
                foreach(var (v, d) in list)
                {
                    w.Write(v);
                    W3(w, d);
                }
            }
        }
        w.Write(Bones.Count);
        foreach(var b in Bones)
        {
            w.Write(b.Name);
            w.Write(b.Parent);
            WM(w, b.Rest);
            WM(w, b.RestLocal);
        }
        w.Write(Chains.Count);
        foreach(var c in Chains)
        {
            w.Write(c.Root);
            w.Write(c.Damping); w.Write(c.Elasticity); w.Write(c.Stiffness); w.Write(c.Inert); w.Write(c.Radius);
            W3(w, c.Gravity);
            W3(w, c.Force);
        }
        w.Write(Materials.Count);
        foreach(var m in Materials)
        {
            w.Write(m.Name);
            w.Write(m.W);
            w.Write(m.H);
            w.Write(m.Main.Length);
            w.Write(m.Main);
            w.Write(m.Mask?.Length ?? 0);
            if(m.Mask != null)
                w.Write(m.Mask);
            w.Write(m.Slots);
            foreach(var c in m.Colours)
                W4(w, c);
        }
    }

    public static KoiRig? Load(string path)
    {
        if(!File.Exists(path))
            return null;
        using var file = File.OpenRead(path);
        using var z = new DeflateStream(file, CompressionMode.Decompress);
        using var r = new BinaryReader(z);
        if(r.ReadInt32() != Version)
            return null;
        var rig = new KoiRig();
        int meshes = r.ReadInt32();
        for(int k = 0; k < meshes; k++)
        {
            var m = new PropMesh { Material = r.ReadInt32() };
            int n = r.ReadInt32();
            for(int i = 0; i < n; i++)
            {
                m.P.Add(R3(r));
                m.N.Add(R3(r));
                m.T.Add(R4(r));
                m.UV.Add(new Vector2(r.ReadSingle(), r.ReadSingle()));
                m.Bones.Add((r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32()));
                m.Weights.Add(R4(r));
            }
            int ni = r.ReadInt32();
            for(int i = 0; i < ni; i++)
                m.I.Add(r.ReadInt32());
            int shapes = r.ReadInt32();
            for(int s = 0; s < shapes; s++)
            {
                string name = r.ReadString();
                int count = r.ReadInt32();
                var list = new List<(int, Vector3)>(count);
                for(int i = 0; i < count; i++)
                    list.Add((r.ReadInt32(), R3(r)));
                m.Shapes[name] = list;
            }
            rig.Meshes.Add(m);
        }
        int bones = r.ReadInt32();
        for(int b = 0; b < bones; b++)
            rig.Bones.Add(new Bone { Name = r.ReadString(), Parent = r.ReadInt32(), Rest = RM(r), RestLocal = RM(r) });
        int chains = r.ReadInt32();
        for(int c = 0; c < chains; c++)
            rig.Chains.Add(new Chain
            {
                Root = r.ReadInt32(), Damping = r.ReadSingle(), Elasticity = r.ReadSingle(), Stiffness = r.ReadSingle(),
                Inert = r.ReadSingle(), Radius = r.ReadSingle(), Gravity = R3(r), Force = R3(r),
            });
        int materials = r.ReadInt32();
        for(int k = 0; k < materials; k++)
        {
            var m = new Material { Name = r.ReadString(), W = r.ReadInt32(), H = r.ReadInt32() };
            m.Main = r.ReadBytes(r.ReadInt32());
            int mask = r.ReadInt32();
            m.Mask = mask > 0 ? r.ReadBytes(mask) : null;
            m.Slots = r.ReadInt32();
            m.Colours = [R4(r), R4(r), R4(r)];
            rig.Materials.Add(m);
        }
        return rig;
    }

    private static void W3(BinaryWriter w, Vector3 v)
    {
        w.Write(v.X); w.Write(v.Y); w.Write(v.Z);
    }

    private static void W4(BinaryWriter w, Vector4 v)
    {
        w.Write(v.X); w.Write(v.Y); w.Write(v.Z); w.Write(v.W);
    }

    private static void WM(BinaryWriter w, Matrix4x4 m)
    {
        w.Write(m.M11); w.Write(m.M12); w.Write(m.M13); w.Write(m.M14);
        w.Write(m.M21); w.Write(m.M22); w.Write(m.M23); w.Write(m.M24);
        w.Write(m.M31); w.Write(m.M32); w.Write(m.M33); w.Write(m.M34);
        w.Write(m.M41); w.Write(m.M42); w.Write(m.M43); w.Write(m.M44);
    }

    private static Vector3 R3(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
    private static Vector4 R4(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

    private static Matrix4x4 RM(BinaryReader r) => new(
        r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(),
        r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(),
        r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(),
        r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

    // ---- Colours

    /// <summary>The material's texture with these colours: each colour where its mask channel says (else the first
    /// colour over all of it), as Koikatsu's item shader does.</summary>
    public static byte[] Tint(Material m, Vector4[] colours)
    {
        var px = (byte[])m.Main.Clone();
        int n = m.W * m.H;
        Vector4 c1 = colours.Length > 0 ? colours[0] : Vector4.One, c2 = colours.Length > 1 ? colours[1] : Vector4.One, c3 = colours.Length > 2 ? colours[2] : Vector4.One;
        for(int i = 0; i < n; i++)
        {
            var col = new Vector4(px[i * 4], px[i * 4 + 1], px[i * 4 + 2], px[i * 4 + 3]) / 255f;
            Vector4 tinted;
            if(m.Mask != null)
            {
                float r = m.Mask[i * 4] / 255f, g = m.Mask[i * 4 + 1] / 255f, b = m.Mask[i * 4 + 2] / 255f;
                tinted = Vector4.Lerp(col, col * c1, r);
                tinted = Vector4.Lerp(tinted, col * c2, g);
                tinted = Vector4.Lerp(tinted, col * c3, b);
            }
            else
                tinted = col * c1;
            px[i * 4] = (byte)Math.Clamp(tinted.X * 255f, 0, 255);
            px[i * 4 + 1] = (byte)Math.Clamp(tinted.Y * 255f, 0, 255);
            px[i * 4 + 2] = (byte)Math.Clamp(tinted.Z * 255f, 0, 255);
            px[i * 4 + 3] = (byte)Math.Clamp(col.W * 255f, 0, 255);
        }
        return px;
    }
}
