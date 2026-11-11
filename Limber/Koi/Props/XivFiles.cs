using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;

namespace Limber.Koi.Props;

/// <summary>
/// A game model to build props like: one of the game's own static (background) models, whose first mesh's vertex
/// layout, model settings and first material are copied, so the new models are drawn the same way.
/// </summary>
public sealed class MdlTemplate
{
    public required byte[] File;
    public required string Path;
    public required byte[] Declaration;          // mesh 0's vertex declaration (17 x 8 bytes)
    public required List<(byte Stream, byte Offset, byte Type, byte Usage)> Elements;
    public required int[] Strides;
    public required byte[] ModelHeader;          // 56 bytes
    public required byte[] Lod0;                 // 60 bytes
    public required ushort BoneTable;
    public required string Material;             // its first material's path
    public required byte[][] Vertex0;            // mesh 0's first vertex, per stream (defaults for what's not set)
    public uint Version;
    public int Winding = 1; // +1: its triangles turn the same way as their normals' right-hand rule; -1 the other way

    internal static Vector3 ReadVec(byte[] b, int at, byte type, bool unit) => type switch
    {
        2 or 3 => new Vector3(BitConverter.ToSingle(b, at), BitConverter.ToSingle(b, at + 4), BitConverter.ToSingle(b, at + 8)),
        14 => new Vector3((float)BitConverter.ToHalf(b, at), (float)BitConverter.ToHalf(b, at + 2), (float)BitConverter.ToHalf(b, at + 4)),
        5 or 8 => unit ? new Vector3(b[at] / 255f * 2f - 1f, b[at + 1] / 255f * 2f - 1f, b[at + 2] / 255f * 2f - 1f) : new Vector3(b[at], b[at + 1], b[at + 2]),
        _ => Vector3.Zero,
    };

    public static int SizeOf(byte type) => type switch
    {
        0 => 4, 1 => 8, 2 => 12, 3 => 16, 5 => 4, 8 => 4, 13 => 4, 14 => 8,
        _ => -1,
    };

    /// <summary>Reads what's needed from a model, or null if it isn't one props can be made like.</summary>
    public static MdlTemplate? Read(string path, byte[] b)
    {
        var r = new BinaryReader(new MemoryStream(b));
        uint version = r.ReadUInt32();
        uint stack = r.ReadUInt32(), runtime = r.ReadUInt32();
        int declarations = r.ReadUInt16();
        r.ReadUInt16();
        if(declarations < 1)
            return null;
        const int header = 0x44;
        int dataStart = (int)(header + stack + runtime);
        r.BaseStream.Position = header;
        var decl = r.ReadBytes(17 * 8);
        var elements = new List<(byte, byte, byte, byte)>();
        for(int e = 0; e < 17; e++)
        {
            byte stream = decl[e * 8], offset = decl[e * 8 + 1], type = decl[e * 8 + 2], usage = decl[e * 8 + 3];
            if(stream == 255)
                break;
            if(SizeOf(type) < 0)
                return null;
            elements.Add((stream, offset, type, usage));
        }
        if(!elements.Any(x => x.Item4 == 0) || !elements.Any(x => x.Item4 == 3) || !elements.Any(x => x.Item4 == 4))
            return null;
        if(elements.Any(x => x.Item4 is 1 or 2))
            return null; // (skinned)
        r.BaseStream.Position = header + declarations * 136;
        int stringCount = r.ReadUInt16();
        r.ReadUInt16();
        int stringSize = (int)r.ReadUInt32();
        var strings = r.ReadBytes(stringSize);
        int modelHeaderAt = (int)r.BaseStream.Position;
        var modelHeader = r.ReadBytes(56);
        int meshCount = BitConverter.ToUInt16(modelHeader, 4), attributeCount = BitConverter.ToUInt16(modelHeader, 6);
        int submeshCount = BitConverter.ToUInt16(modelHeader, 8), materialCount = BitConverter.ToUInt16(modelHeader, 10);
        int elementIds = BitConverter.ToUInt16(modelHeader, 24), terrainShadowMeshes = modelHeader[26];
        byte flags2 = modelHeader[27];
        int terrainShadowSubmeshes = BitConverter.ToUInt16(modelHeader, 38);
        if(meshCount < 1 || materialCount < 1)
            return null;
        r.BaseStream.Position = modelHeaderAt + 56 + elementIds * 32;
        var lod0 = r.ReadBytes(60);
        r.ReadBytes(120);
        if((flags2 & 0x10) != 0)
            r.ReadBytes(3 * 40);
        var mesh0 = r.ReadBytes(36);
        r.ReadBytes((meshCount - 1) * 36);
        r.ReadBytes(attributeCount * 4 + terrainShadowMeshes * 20 + submeshCount * 16 + terrainShadowSubmeshes * 12);
        uint materialOffset = r.ReadUInt32();
        int end = Array.IndexOf(strings, (byte)0, (int)materialOffset);
        string material = Encoding.UTF8.GetString(strings, (int)materialOffset, (end < 0 ? strings.Length : end) - (int)materialOffset);

        var strides = new int[3];
        foreach(var (s, o, t, _) in elements)
            if(s < 3)
                strides[s] = Math.Max(strides[s], o + SizeOf(t));
        uint vertexData = BitConverter.ToUInt32(lod0, 52);
        var vertex0 = new byte[3][];
        for(int s = 0; s < 3; s++)
        {
            // (the mesh's vertex buffer offsets are at 20, 24 and 28)
            uint off = BitConverter.ToUInt32(mesh0, 20 + 4 * s);
            long at = vertexData + off;
            vertex0[s] = strides[s] > 0 && at + strides[s] <= b.Length ? b.AsSpan((int)at, strides[s]).ToArray() : new byte[strides[s]];
        }
        // Which way its triangles turn against its normals (+1 / -1): the props' are turned the same way, so the game
        // draws their outsides (the other way round it draws their insides, lit from behind: a shine round the edges).
        float winding = 0f;
        try
        {
            var pos = elements.First(e => e.Item4 == 0);
            var nrm = elements.First(e => e.Item4 == 3);
            uint indexData = BitConverter.ToUInt32(lod0, 56);
            uint start = BitConverter.ToUInt32(mesh0, 16), count = BitConverter.ToUInt32(mesh0, 4);
            Vector3 P(int v) => ReadVec(b, (int)(vertexData + BitConverter.ToUInt32(mesh0, 20 + 4 * pos.Item1)) + v * strides[pos.Item1] + pos.Item2, pos.Item3, false);
            Vector3 N(int v) => ReadVec(b, (int)(vertexData + BitConverter.ToUInt32(mesh0, 20 + 4 * nrm.Item1)) + v * strides[nrm.Item1] + nrm.Item2, nrm.Item3, true);
            for(uint i = 0; i + 2 < count && i < 900; i += 3)
            {
                int at = (int)(indexData + (start + i) * 2);
                int a = BitConverter.ToUInt16(b, at), c1 = BitConverter.ToUInt16(b, at + 2), c2 = BitConverter.ToUInt16(b, at + 4);
                var face = Vector3.Cross(P(c1) - P(a), P(c2) - P(a));
                winding += MathF.Sign(Vector3.Dot(face, N(a) + N(c1) + N(c2)));
            }
        }
        catch
        {
        }
        return new MdlTemplate
        {
            File = b, Path = path, Declaration = decl, Elements = elements, Strides = strides, ModelHeader = modelHeader, Lod0 = lod0,
            BoneTable = BitConverter.ToUInt16(mesh0, 14), Material = material, Vertex0 = vertex0, Version = version,
            Winding = winding < 0f ? -1 : 1,
        };
    }
}

/// <summary>One mesh for a new prop model: positions, normals, tangents (w: handedness), UVs, triangles, material.</summary>
public sealed class PropMesh
{
    public List<Vector3> P = new(), N = new();
    public List<Vector4> T = new();
    public List<Vector2> UV = new();
    public List<int> I = new();
    public int Material;
    // (for posing it live: each vertex's bones (up to 4, the prop's bone numbers) and their weights, and its blend
    // shapes: name -> (vertex, how far it moves at full))
    public List<(int A, int B, int C, int D)> Bones = new();
    public List<Vector4> Weights = new();
    public Dictionary<string, List<(int Vertex, Vector3 Delta)>> Shapes = new();
}

/// <summary>Where a built model's vertices are: the vertex block (as the game's buffer holds it), and for each of the
/// meshes given (in order) where each stream of its vertices starts in that block, and the meshes' vertex counts.</summary>
public sealed class MdlLayout
{
    public byte[] Block = [];
    public List<(uint[] Offsets, int Count)> Meshes = new();
}

/// <summary>Writes a static (background) model in the game's format, laid out like the template.</summary>
public static class MdlBuilder
{
    public static byte[] Build(MdlTemplate t, IReadOnlyList<PropMesh> meshesIn, IReadOnlyList<string> materials)
        => Build(t, meshesIn, materials, out _);

    /// <summary>Turns every triangle round (on top of matching the template's way), as a fix to try.</summary>
    [ThreadStatic] public static bool FlipFaces;
    /// <summary>Normals pointing the other way, as a fix to try.</summary>
    [ThreadStatic] public static bool FlipNormals;

    /// <summary>The same, and where each vertex ended up (meshes with more than 65535 vertices aren't split here: those
    /// models can't be moved live, and <paramref name="layout"/> is null).</summary>
    public static byte[] Build(MdlTemplate t, IReadOnlyList<PropMesh> meshesIn, IReadOnlyList<string> materials, out MdlLayout? layout)
    {
        layout = meshesIn.All(m => m.P.Count <= 65535 && m.I.Count > 0) ? new MdlLayout() : null;
        // (a mesh may hold at most 65535 vertices: bigger ones are cut into pieces)
        var meshes = new List<PropMesh>();
        foreach(var m in meshesIn)
            meshes.AddRange(Split(m));
        meshes.RemoveAll(m => m.I.Count == 0);
        if(meshes.Count == 0)
            throw new InvalidDataException("nothing to draw");

        // Vertex and index data, mesh by mesh.
        var vertex = new MemoryStream();
        var index = new MemoryStream();
        var meshInfo = new List<(int VertexCount, int IndexCount, int Start, uint[] Offsets)>();
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        int streams = t.Strides.Count(s => s > 0);
        foreach(var m in meshes)
        {
            var offsets = new uint[3];
            for(int s = 0; s < 3; s++)
            {
                if(t.Strides[s] == 0)
                    continue;
                offsets[s] = (uint)vertex.Length;
                var buf = new byte[t.Strides[s] * m.P.Count];
                for(int v = 0; v < m.P.Count; v++)
                {
                    var at = buf.AsSpan(v * t.Strides[s], t.Strides[s]);
                    t.Vertex0[s].CopyTo(at);
                    foreach(var (es, eo, et, eu) in t.Elements)
                        if(es == s)
                            Encode(at.Slice(eo), et, eu, m, v);
                }
                vertex.Write(buf);
                while(vertex.Length % 16 != 0)
                    vertex.WriteByte(0);
            }
            foreach(var p in m.P)
            {
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
            int start = (int)(index.Length / 2);
            bool turn = (Winding(m) * t.Winding < 0) ^ FlipFaces;
            for(int i = 0; i + 2 < m.I.Count; i += 3)
            {
                index.Write(BitConverter.GetBytes((ushort)m.I[i]));
                index.Write(BitConverter.GetBytes((ushort)m.I[turn ? i + 2 : i + 1]));
                index.Write(BitConverter.GetBytes((ushort)m.I[turn ? i + 1 : i + 2]));
            }
            while(index.Length % 16 != 0)
                index.WriteByte(0);
            meshInfo.Add((m.P.Count, m.I.Count, start, offsets));
            layout?.Meshes.Add((offsets, m.P.Count));
        }
        if(layout != null)
            layout.Block = vertex.ToArray();

        // Strings: the materials.
        var strings = new MemoryStream();
        var materialOffsets = new List<uint>();
        foreach(var name in materials)
        {
            materialOffsets.Add((uint)strings.Length);
            strings.Write(Encoding.UTF8.GetBytes(name));
            strings.WriteByte(0);
        }
        while(strings.Length % 4 != 0)
            strings.WriteByte(0);

        var o = new MemoryStream();
        var w = new BinaryWriter(o);
        w.Write(new byte[0x44]); // file header, filled in at the end
        for(int k = 0; k < meshes.Count; k++)
            w.Write(t.Declaration);
        int runtimeStart = (int)o.Position;
        w.Write((ushort)materials.Count);
        w.Write((ushort)0);
        w.Write((uint)strings.Length);
        w.Write(strings.ToArray());

        // Model header (the template's settings, with this model's counts).
        var mh = (byte[])t.ModelHeader.Clone();
        float radius = MathF.Max(min.Length(), max.Length());
        BitConverter.TryWriteBytes(mh.AsSpan(0), radius);
        BitConverter.TryWriteBytes(mh.AsSpan(4), (ushort)meshes.Count);
        BitConverter.TryWriteBytes(mh.AsSpan(6), (ushort)0);
        BitConverter.TryWriteBytes(mh.AsSpan(8), (ushort)meshes.Count);
        BitConverter.TryWriteBytes(mh.AsSpan(10), (ushort)materials.Count);
        BitConverter.TryWriteBytes(mh.AsSpan(12), (ushort)0); // bones
        BitConverter.TryWriteBytes(mh.AsSpan(14), (ushort)0); // bone tables
        BitConverter.TryWriteBytes(mh.AsSpan(16), (ushort)0); // shapes
        BitConverter.TryWriteBytes(mh.AsSpan(18), (ushort)0);
        BitConverter.TryWriteBytes(mh.AsSpan(20), (ushort)0);
        mh[22] = 1; // lods
        BitConverter.TryWriteBytes(mh.AsSpan(24), (ushort)0); // element ids
        mh[26] = 0; // terrain shadow meshes
        mh[27] = (byte)(mh[27] & ~0x10); // no extra lods
        BitConverter.TryWriteBytes(mh.AsSpan(38), (ushort)0); // terrain shadow submeshes
        mh[43] = 0; // neck morphs
        BitConverter.TryWriteBytes(mh.AsSpan(44), (ushort)0); // bone table arrays
        w.Write(mh);

        // LoDs: the first holds everything; the other two are empty.
        int lodAt = (int)o.Position;
        int n = meshes.Count;
        for(int l = 0; l < 3; l++)
        {
            var lod = new byte[60];
            if(l == 0)
                Buffer.BlockCopy(t.Lod0, 4, lod, 4, 8); // its distance ranges
            BitConverter.TryWriteBytes(lod.AsSpan(0), (ushort)(l == 0 ? 0 : n));
            BitConverter.TryWriteBytes(lod.AsSpan(2), (ushort)(l == 0 ? n : 0));
            for(int k = 0; k < 4; k++)
                BitConverter.TryWriteBytes(lod.AsSpan(12 + 4 * k), (ushort)n); // water, shadow, terrain shadow, fog: none
            w.Write(lod);
        }

        // Meshes.
        for(int k = 0; k < n; k++)
        {
            var (vc, ic, start, offsets) = meshInfo[k];
            w.Write((ushort)vc);
            w.Write((ushort)0);
            w.Write((uint)ic);
            w.Write((ushort)meshes[k].Material);
            w.Write((ushort)k); // its submesh
            w.Write((ushort)1);
            w.Write(t.BoneTable);
            w.Write((uint)start);
            for(int s = 0; s < 3; s++)
                w.Write(offsets[s]);
            for(int s = 0; s < 3; s++)
                w.Write((byte)t.Strides[s]);
            w.Write((byte)streams);
        }
        // Submeshes (one per mesh).
        for(int k = 0; k < n; k++)
        {
            w.Write((uint)meshInfo[k].Start);
            w.Write((uint)meshInfo[k].IndexCount);
            w.Write(0u);
            w.Write((ushort)0);
            w.Write((ushort)0);
        }
        foreach(var off in materialOffsets)
            w.Write(off);
        w.Write(0u); // submesh bone map
        long next = o.Position + 1;
        byte padding = (byte)((8 - (next & 7)) & 7);
        w.Write(padding);
        for(int k = 0; k < padding; k++)
            w.Write((byte)0);
        // Bounding boxes: the model, the model again, water, fog.
        void Box(Vector3 a, Vector3 b)
        {
            w.Write(a.X); w.Write(a.Y); w.Write(a.Z); w.Write(1f);
            w.Write(b.X); w.Write(b.Y); w.Write(b.Z); w.Write(1f);
        }
        Box(min, max);
        Box(min, max);
        Box(Vector3.Zero, Vector3.Zero);
        Box(Vector3.Zero, Vector3.Zero);

        int dataStart = (int)o.Position;
        uint vertexSize = (uint)vertex.Length, indexSize = (uint)index.Length;
        w.Write(vertex.ToArray());
        w.Write(index.ToArray());
        var file = o.ToArray();

        // LoD 0's data.
        int polygons = meshes.Sum(m => m.I.Count / 3);
        // (polygons +36, vertex size +44, index size +48, vertex offset +52, index offset +56)
        BitConverter.TryWriteBytes(file.AsSpan(lodAt + 36), (uint)polygons);
        BitConverter.TryWriteBytes(file.AsSpan(lodAt + 40), 0u);
        BitConverter.TryWriteBytes(file.AsSpan(lodAt + 44), vertexSize);
        BitConverter.TryWriteBytes(file.AsSpan(lodAt + 48), indexSize);
        BitConverter.TryWriteBytes(file.AsSpan(lodAt + 52), (uint)dataStart);
        BitConverter.TryWriteBytes(file.AsSpan(lodAt + 56), (uint)(dataStart + vertexSize));
        for(int l = 1; l < 3; l++)
        {
            BitConverter.TryWriteBytes(file.AsSpan(lodAt + 60 * l + 52), (uint)(dataStart + vertexSize + indexSize));
            BitConverter.TryWriteBytes(file.AsSpan(lodAt + 60 * l + 56), (uint)(dataStart + vertexSize + indexSize));
        }

        // File header.
        var h = file.AsSpan();
        BitConverter.TryWriteBytes(h.Slice(0), t.Version);
        BitConverter.TryWriteBytes(h.Slice(4), (uint)(n * 136));
        BitConverter.TryWriteBytes(h.Slice(8), (uint)(dataStart - runtimeStart));
        BitConverter.TryWriteBytes(h.Slice(12), (ushort)n);
        BitConverter.TryWriteBytes(h.Slice(14), (ushort)materials.Count);
        BitConverter.TryWriteBytes(h.Slice(16), (uint)dataStart);                       // vertex offsets
        BitConverter.TryWriteBytes(h.Slice(28), (uint)(dataStart + vertexSize));        // index offsets
        BitConverter.TryWriteBytes(h.Slice(40), vertexSize);                            // vertex sizes
        BitConverter.TryWriteBytes(h.Slice(52), indexSize);                             // index sizes
        h[64] = 1;                                                                      // lods
        h[65] = t.File.Length > 65 ? t.File[65] : (byte)1;                             // index buffer streaming
        h[66] = 0;                                                                      // edge geometry
        return file;
    }

    // Which way a mesh's triangles turn against its normals, as the template's is told (+1 / -1).
    private static int Winding(PropMesh m)
    {
        float sum = 0f;
        for(int i = 0; i + 2 < m.I.Count && i < 3000; i += 3)
        {
            int a = m.I[i], b = m.I[i + 1], c = m.I[i + 2];
            if(a >= m.P.Count || b >= m.P.Count || c >= m.P.Count || a >= m.N.Count || b >= m.N.Count || c >= m.N.Count)
                continue;
            var face = Vector3.Cross(m.P[b] - m.P[a], m.P[c] - m.P[a]);
            sum += MathF.Sign(Vector3.Dot(face, m.N[a] + m.N[b] + m.N[c]));
        }
        return sum < 0f ? -1 : 1;
    }

    private static IEnumerable<PropMesh> Split(PropMesh m)
    {
        if(m.P.Count <= 65535)
        {
            yield return m;
            yield break;
        }
        var part = new PropMesh { Material = m.Material };
        var map = new Dictionary<int, int>();
        for(int i = 0; i + 2 < m.I.Count; i += 3)
        {
            if(part.P.Count > 65532)
            {
                yield return part;
                part = new PropMesh { Material = m.Material };
                map.Clear();
            }
            for(int k = 0; k < 3; k++)
            {
                int v = m.I[i + k];
                if(!map.TryGetValue(v, out var nv))
                {
                    nv = part.P.Count;
                    map[v] = nv;
                    part.P.Add(m.P[v]);
                    part.N.Add(v < m.N.Count ? m.N[v] : Vector3.UnitY);
                    part.T.Add(v < m.T.Count ? m.T[v] : new Vector4(1, 0, 0, 1));
                    part.UV.Add(v < m.UV.Count ? m.UV[v] : Vector2.Zero);
                }
                part.I.Add(nv);
            }
        }
        yield return part;
    }

    private static void Encode(Span<byte> at, byte type, byte usage, PropMesh m, int v)
    {
        Vector4 value;
        switch(usage)
        {
            case 0: // position
                value = new Vector4(m.P[v], 1f);
                break;
            case 3: // normal
                value = new Vector4((v < m.N.Count ? m.N[v] : Vector3.UnitY) * (FlipNormals ? -1f : 1f), 0f);
                if(type == 8)
                    value = value * 0.5f + new Vector4(0.5f);
                break;
            case 4: // uv (both sets the same)
            {
                var uv = v < m.UV.Count ? m.UV[v] : Vector2.Zero;
                value = new Vector4(uv.X, uv.Y, uv.X, uv.Y);
                break;
            }
            case 5: // flow
            case 6: // tangent ("binormal"): the bitangent direction, its handedness in w
            {
                var n = v < m.N.Count ? m.N[v] : Vector3.UnitY;
                var tg = v < m.T.Count ? m.T[v] : new Vector4(1, 0, 0, 1);
                var b = Vector3.Cross(n, new Vector3(tg.X, tg.Y, tg.Z)) * (tg.W < 0 ? -1f : 1f);
                var dir = usage == 6 ? b : new Vector3(tg.X, tg.Y, tg.Z);
                if(type == 8)
                    value = new Vector4(dir * 0.5f + new Vector3(0.5f), tg.W < 0 ? 0f : 1f);
                else
                    value = new Vector4(dir, tg.W < 0 ? -1f : 1f);
                break;
            }
            default:
                return; // (kept as the template's first vertex has it: colours and the like)
        }
        switch(type)
        {
            case 0: BitConverter.TryWriteBytes(at, value.X); break;
            case 1: BitConverter.TryWriteBytes(at, value.X); BitConverter.TryWriteBytes(at.Slice(4), value.Y); break;
            case 2: BitConverter.TryWriteBytes(at, value.X); BitConverter.TryWriteBytes(at.Slice(4), value.Y); BitConverter.TryWriteBytes(at.Slice(8), value.Z); break;
            case 3: BitConverter.TryWriteBytes(at, value.X); BitConverter.TryWriteBytes(at.Slice(4), value.Y); BitConverter.TryWriteBytes(at.Slice(8), value.Z); BitConverter.TryWriteBytes(at.Slice(12), value.W); break;
            case 5:
            case 8:
                at[0] = Unorm(value.X); at[1] = Unorm(value.Y); at[2] = Unorm(value.Z); at[3] = Unorm(value.W);
                break;
            case 13: BitConverter.TryWriteBytes(at, (Half)value.X); BitConverter.TryWriteBytes(at.Slice(2), (Half)value.Y); break;
            case 14:
                BitConverter.TryWriteBytes(at, (Half)value.X); BitConverter.TryWriteBytes(at.Slice(2), (Half)value.Y);
                BitConverter.TryWriteBytes(at.Slice(4), (Half)value.Z); BitConverter.TryWriteBytes(at.Slice(6), (Half)value.W);
                break;
        }
    }

    private static byte Unorm(float x) => (byte)Math.Clamp((int)MathF.Round(x * 255f), 0, 255);
}

/// <summary>A material made from one of the game's: the same shader and settings, with other textures.</summary>
public static class MtrlBuilder
{
    /// <summary>The template's texture paths.</summary>
    public static List<string> Textures(byte[] b)
    {
        var list = new List<string>();
        int texCount = b[12];
        int uv = b[13], cs = b[14];
        int stringsAt = 16 + 4 * (texCount + uv + cs);
        for(int i = 0; i < texCount; i++)
            list.Add(Str(b, stringsAt + BitConverter.ToUInt16(b, 16 + 4 * i)));
        return list;
    }

    /// <summary>Its shader package's name.</summary>
    public static string Shader(byte[] b)
    {
        int texCount = b[12], uv = b[13], cs = b[14];
        int stringsAt = 16 + 4 * (texCount + uv + cs);
        return Str(b, stringsAt + BitConverter.ToUInt16(b, 10));
    }

    private static string Str(byte[] b, int at)
    {
        int end = Array.IndexOf(b, (byte)0, at);
        return Encoding.UTF8.GetString(b, at, (end < 0 ? b.Length : end) - at);
    }

    /// <summary>Its shader constants: id and values (floats).</summary>
    public static List<(uint Id, float[] Values)> Constants(byte[] b)
    {
        var list = new List<(uint, float[])>();
        int texCount = b[12], uv = b[13], cs = b[14];
        int dataSet = BitConverter.ToUInt16(b, 6), stringSize = BitConverter.ToUInt16(b, 8), additional = b[15];
        int at = 16 + 4 * (texCount + uv + cs) + stringSize + additional + dataSet;
        if(at + 12 > b.Length)
            return list;
        int valueSize = BitConverter.ToUInt16(b, at), keys = BitConverter.ToUInt16(b, at + 2), constants = BitConverter.ToUInt16(b, at + 4), samplers = BitConverter.ToUInt16(b, at + 6);
        int constantsAt = at + 12 + keys * 8, valuesAt = constantsAt + constants * 8 + samplers * 12;
        for(int c = 0; c < constants; c++)
        {
            int e = constantsAt + c * 8;
            if(e + 8 > b.Length)
                break;
            uint id = BitConverter.ToUInt32(b, e);
            int offset = BitConverter.ToUInt16(b, e + 4), size = BitConverter.ToUInt16(b, e + 6);
            var values = new float[Math.Min(size / 4, 16)];
            for(int k = 0; k < values.Length; k++)
                if(valuesAt + offset + k * 4 + 4 <= b.Length && offset + k * 4 + 4 <= valueSize)
                    values[k] = BitConverter.ToSingle(b, valuesAt + offset + k * 4);
            list.Add((id, values));
        }
        return list;
    }

    // The game's prop shader (bg.shpk) settings: friendly name, what it does, and whether it's one worth changing.
    // (Their real names, e.g. g_DiffuseColor, are from the community's lists of the game's shader names.)
    private static readonly Dictionary<uint, (string Name, string Tip, bool Useful)> Known = new()
    {
        [0x2C2A34DD] = ("Colour multiplier", "g_DiffuseColor: multiplies the whole texture's colour (1, 1, 1: as it is). Lower all three to darken it.", true),
        [0x141722D5] = ("Shine colour", "g_SpecularColor: the colour and strength of its reflections (1, 1, 1: white). Lower for duller, tint for coloured shine.", true),
        [0xB5545FBB] = ("Bump strength (shader)", "g_NormalScale: how strongly its bump map bends the light (1: as it is, 0: smooth).", true),
        [0xB7FA33E2] = ("Ambient shadow", "g_SSAOMask: how much the game darkens its creases and where it meets other things (1: full, 0: none). Higher helps it sit in the scene.", true),
        [0xEEF5665F] = ("Reflection strength", "g_EnvMapPower: how much it mirrors its surroundings.", true),
        [0x29AC0223] = ("See-through cut-off", "g_AlphaThreshold: parts more see-through than this aren't drawn at all (0: everything drawn).", true),
        [0xD925FF32] = ("Shadow cut-off", "g_ShadowAlphaThreshold: like the see-through cut-off, for the shadow it casts.", true),
        [0x8F8B0070] = ("Height depth", "g_HeightScale: how deep its height map makes the surface look (usually unused on imported props).", true),
        [0xA5D02C52] = ("Colour texture tiling", "g_ColorUVScale: how many times its colour texture repeats (1: once). Leave at 1 for imported props.", false),
        [0x8D03A782] = ("Shine texture tiling", "g_SpecularUVScale: how many times its shine texture repeats.", false),
        [0xBB99CF76] = ("Bump texture tiling", "g_NormalUVScale: how many times its bump texture repeats.", false),
        [0x59BDA0B1] = ("Shader variant (leave alone)", "g_ShaderID: which variant of the shader it uses. Changing it can break how it's drawn.", false),
        [0x8981D4D9] = ("Detail texture number", "g_DetailID: which of the game's shared detail textures (fine grain) it layers on (0: none).", false),
        [0xDD93D839] = ("Detail colour", "g_DetailColor: the detail texture's colour.", false),
        [0x9F42EDA2] = ("Detail bump strength", "g_DetailNormalScale: how strong the detail texture's bumps are.", false),
        [0x025A9BEE] = ("Detail bump tiling", "g_DetailNormalUvScale: how often the detail bumps repeat.", false),
        [0xC63D9716] = ("Detail colour tiling", "g_DetailColorUvScale: how often the detail colour repeats.", false),
        [0xF3F28C58] = ("Detail colour fade distance", "g_DetailColorFadeDistance: how far away the detail colour fades out.", false),
        [0x236EE793] = ("Detail bump fade distance", "g_DetailNormalFadeDistance: how far away the detail bumps fade out.", false),
        [0xB10AF2DA] = ("Detail colour sharpness", "g_DetailColorMipBias: blurrier (higher) or sharper detail colour.", false),
        [0x756DFE22] = ("Detail bump sharpness", "g_DetailNormalMipBias: blurrier (higher) or sharper detail bumps.", false),
        [0x07EDA444] = ("Second layer blend", "g_AlphaMultiParam: how a second layer is blended in (for the game's layered materials; unused here).", false),
        [0x30B91319] = ("Second layer weight", "g_AlphaMultiWeight: for the game's layered materials (unused here).", false),
        [0x3F8AC211] = ("Second layer colour", "g_MultiDiffuseColor: the second layer's colour multiplier (unused here).", false),
        [0x86D60CB8] = ("Second layer shine", "g_MultiSpecularColor (unused here).", false),
        [0xAA676D0F] = ("Second layer glow", "g_MultiEmissiveColor (unused here).", false),
        [0x793AC5A3] = ("Second layer bump strength", "g_MultiNormalScale (unused here).", false),
        [0x926E860D] = ("Second layer ambient shadow", "g_MultiSSAOMask (unused here).", false),
        [0x43E59A68] = ("Second layer height depth", "g_MultiHeightScale (unused here).", false),
        [0x11FD4221] = ("Second layer detail colour", "g_MultiDetailColor (unused here).", false),
        [0xAC156136] = ("Second layer detail number", "g_MultiDetailID (unused here).", false),
        [0xA83DBDF1] = ("Second layer detail bumps", "g_MultiDetailNormalScale (unused here).", false),
        [0x5095E770] = ("Shadows from lamps", "g_EnableLightShadow: whether it casts shadows from placed lights (0: off, 1: on).", false),
        [0xBCEA8C11] = ("Shadows", "g_EnableShadow: whether it casts shadows (0: off, 1: on).", false),
        [0x5106E045] = ("Sphere map number", "g_SphereMapID: which of the game's reflection maps it uses.", false),
        [0x9A696A17] = ("Scroll speed", "g_UVScrollTime: for the game's scrolling textures (unused here).", false),
        [0xBCA22FD4] = ("Inclusion aperture", "g_InclusionAperture: for the game's crystal materials (unused here).", false),
        [0xB500BB24] = ("Scattering", "g_ScatteringLevel: light scattering, for the game's own materials (unused here).", false),
        [0x38A64362] = ("Glow colour", "g_EmissiveColor: light it gives off on its own.", true),
    };

    /// <summary>A shader constant's friendly name (unknown ones by number).</summary>
    public static string NameOf(uint id) => Known.TryGetValue(id, out var k) ? k.Name : $"Unknown (0x{id:X8})";

    /// <summary>What a shader constant does (and its real name), or null if Limber doesn't know.</summary>
    public static string? TipOf(uint id) => Known.TryGetValue(id, out var k) ? k.Tip : null;

    /// <summary>Whether it's one worth changing on an imported prop.</summary>
    public static bool IsUseful(uint id) => Known.TryGetValue(id, out var k) && k.Useful;

    /// <summary>The material's emissive colour (how much it glows on its own), as the game's shaders call it.</summary>
    public const uint EmissiveColor = 0x38A64362;

    /// <summary>
    /// A shader constant set (added if the material hasn't got it): after the strings, extra data and colour table come
    /// the shader settings (sizes and counts, 12 bytes), its keys (8 bytes each), constants (id, offset, size: 8 bytes
    /// each), samplers (12 bytes each), and the constants' values.
    /// </summary>
    public static byte[] SetConstant(byte[] b, uint id, float[] values)
    {
        int texCount = b[12], uv = b[13], cs = b[14];
        int dataSet = BitConverter.ToUInt16(b, 6), stringSize = BitConverter.ToUInt16(b, 8), additional = b[15];
        int at = 16 + 4 * (texCount + uv + cs) + stringSize + additional + dataSet;
        if(at + 12 > b.Length)
            return b;
        int valueSize = BitConverter.ToUInt16(b, at), keys = BitConverter.ToUInt16(b, at + 2), constants = BitConverter.ToUInt16(b, at + 4), samplers = BitConverter.ToUInt16(b, at + 6);
        int keysAt = at + 12, constantsAt = keysAt + keys * 8, samplersAt = constantsAt + constants * 8, valuesAt = samplersAt + samplers * 12;
        if(valuesAt + valueSize > b.Length)
            return b;
        for(int c = 0; c < constants; c++)
        {
            int e = constantsAt + c * 8;
            if(BitConverter.ToUInt32(b, e) != id)
                continue;
            int offset = BitConverter.ToUInt16(b, e + 4), size = BitConverter.ToUInt16(b, e + 6);
            var copy = (byte[])b.Clone();
            for(int k = 0; k < values.Length && k * 4 + 4 <= size; k++)
                BitConverter.TryWriteBytes(copy.AsSpan(valuesAt + offset + k * 4), values[k]);
            return copy;
        }
        // (not there: added, its values at the end of the others)
        var o = new MemoryStream();
        o.Write(b, 0, at);
        var w = new BinaryWriter(o);
        w.Write((ushort)(valueSize + values.Length * 4));
        w.Write((ushort)keys);
        w.Write((ushort)(constants + 1));
        w.Write((ushort)samplers);
        o.Write(b, at + 8, 4);
        o.Write(b, keysAt, keys * 8);
        o.Write(b, constantsAt, constants * 8);
        w.Write(id);
        w.Write((ushort)valueSize);
        w.Write((ushort)(values.Length * 4));
        o.Write(b, samplersAt, samplers * 12);
        o.Write(b, valuesAt, valueSize);
        foreach(var v in values)
            w.Write(v);
        int rest = valuesAt + valueSize;
        if(rest < b.Length)
            o.Write(b, rest, b.Length - rest);
        var file = o.ToArray();
        BitConverter.TryWriteBytes(file.AsSpan(4), (ushort)Math.Min(file.Length, 0xFFFF));
        return file;
    }

    /// <summary>The template with its textures swapped (same number, same order).</summary>
    public static byte[] With(byte[] b, IReadOnlyList<string> textures)
    {
        int texCount = b[12], uv = b[13], cs = b[14];
        int dataSet = BitConverter.ToUInt16(b, 6), stringSize = BitConverter.ToUInt16(b, 8), shaderOff = BitConverter.ToUInt16(b, 10);
        int additional = b[15];
        int offsetsAt = 16;
        int stringsAt = offsetsAt + 4 * (texCount + uv + cs);
        var old = b.AsSpan(stringsAt, stringSize).ToArray();
        string Old(int off) => Str(b, stringsAt + off);

        var table = new MemoryStream();
        ushort Add(string s)
        {
            var at = (ushort)table.Length;
            table.Write(Encoding.UTF8.GetBytes(s));
            table.WriteByte(0);
            return at;
        }
        var texOffsets = new ushort[texCount];
        for(int i = 0; i < texCount; i++)
            texOffsets[i] = Add(i < textures.Count ? textures[i] : Old(BitConverter.ToUInt16(b, offsetsAt + 4 * i)));
        var uvOffsets = new ushort[uv];
        for(int i = 0; i < uv; i++)
            uvOffsets[i] = Add(Old(BitConverter.ToUInt16(b, offsetsAt + 4 * (texCount + i))));
        var csOffsets = new ushort[cs];
        for(int i = 0; i < cs; i++)
            csOffsets[i] = Add(Old(BitConverter.ToUInt16(b, offsetsAt + 4 * (texCount + uv + i))));
        ushort shader = Add(Old(shaderOff));
        while(table.Length % 4 != 0)
            table.WriteByte(0);

        var o = new MemoryStream();
        var w = new BinaryWriter(o);
        w.Write(b, 0, 16);
        for(int i = 0; i < texCount; i++)
        {
            w.Write(texOffsets[i]);
            w.Write(BitConverter.ToUInt16(b, offsetsAt + 4 * i + 2));
        }
        for(int i = 0; i < uv; i++)
        {
            w.Write(uvOffsets[i]);
            w.Write(BitConverter.ToUInt16(b, offsetsAt + 4 * (texCount + i) + 2));
        }
        for(int i = 0; i < cs; i++)
        {
            w.Write(csOffsets[i]);
            w.Write(BitConverter.ToUInt16(b, offsetsAt + 4 * (texCount + uv + i) + 2));
        }
        w.Write(table.ToArray());
        int rest = stringsAt + stringSize;
        w.Write(b, rest, b.Length - rest); // additional data, colour table, shader settings
        var file = o.ToArray();
        BitConverter.TryWriteBytes(file.AsSpan(4), (ushort)Math.Min(file.Length, 0xFFFF));
        BitConverter.TryWriteBytes(file.AsSpan(8), (ushort)table.Length);
        BitConverter.TryWriteBytes(file.AsSpan(10), shader);
        _ = old;
        _ = dataSet;
        _ = additional;
        return file;
    }
}

/// <summary>Textures in the game's format (uncompressed 32-bit, with mipmaps).</summary>
public static class TexWriter
{
    /// <summary>RGBA pixels (row 0 first) as a .tex (B8G8R8A8), with its mipmaps.</summary>
    public static byte[] Bgra(byte[] rgba, int width, int height)
    {
        var levels = new List<byte[]>();
        var cur = rgba;
        int w = width, h = height;
        while(true)
        {
            var bgra = new byte[w * h * 4];
            for(int i = 0; i < w * h; i++)
            {
                bgra[i * 4] = cur[i * 4 + 2];
                bgra[i * 4 + 1] = cur[i * 4 + 1];
                bgra[i * 4 + 2] = cur[i * 4];
                bgra[i * 4 + 3] = cur[i * 4 + 3];
            }
            levels.Add(bgra);
            if((w <= 4 && h <= 4) || levels.Count >= 13)
                break;
            (cur, w, h) = Half(cur, w, h);
        }
        var header = new byte[80];
        BitConverter.TryWriteBytes(header.AsSpan(0), 0x00800000u);
        BitConverter.TryWriteBytes(header.AsSpan(4), 0x1450u);
        BitConverter.TryWriteBytes(header.AsSpan(8), (ushort)width);
        BitConverter.TryWriteBytes(header.AsSpan(10), (ushort)height);
        BitConverter.TryWriteBytes(header.AsSpan(12), (ushort)1);
        header[14] = (byte)levels.Count;
        header[15] = 1;
        BitConverter.TryWriteBytes(header.AsSpan(16), 0u);
        BitConverter.TryWriteBytes(header.AsSpan(20), (uint)Math.Min(1, levels.Count - 1));
        BitConverter.TryWriteBytes(header.AsSpan(24), (uint)Math.Min(2, levels.Count - 1));
        uint off = 80;
        for(int k = 0; k < levels.Count; k++)
        {
            BitConverter.TryWriteBytes(header.AsSpan(28 + 4 * k), off);
            off += (uint)levels[k].Length;
        }
        var o = new MemoryStream();
        o.Write(header);
        foreach(var l in levels)
            o.Write(l);
        return o.ToArray();
    }

    /// <summary>One colour, as a small texture.</summary>
    public static byte[] Solid(byte r, byte g, byte b, byte a)
    {
        var px = new byte[8 * 8 * 4];
        for(int i = 0; i < 64; i++)
        {
            px[i * 4] = r;
            px[i * 4 + 1] = g;
            px[i * 4 + 2] = b;
            px[i * 4 + 3] = a;
        }
        return Bgra(px, 8, 8);
    }

    public static (byte[] Pixels, int W, int H) Half(byte[] src, int w, int h)
    {
        int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
        var dst = new byte[nw * nh * 4];
        for(int y = 0; y < nh; y++)
            for(int x = 0; x < nw; x++)
                for(int c = 0; c < 4; c++)
                {
                    int x0 = Math.Min(x * 2, w - 1), x1 = Math.Min(x * 2 + 1, w - 1), y0 = Math.Min(y * 2, h - 1), y1 = Math.Min(y * 2 + 1, h - 1);
                    int sum = src[(y0 * w + x0) * 4 + c] + src[(y0 * w + x1) * 4 + c] + src[(y1 * w + x0) * 4 + c] + src[(y1 * w + x1) * 4 + c];
                    dst[(y * nw + x) * 4 + c] = (byte)((sum + 2) / 4);
                }
        return (dst, nw, nh);
    }
}
