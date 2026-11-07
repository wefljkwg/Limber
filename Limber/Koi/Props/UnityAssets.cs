using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Limber.Koi.Props;

/// <summary>
/// Just enough of Unity's asset bundle format to read Koikatsu's studio items (Unity 5.6): the bundle container
/// (UnityFS, with LZ4 or LZMA blocks), the serialized files in it, and their objects through the type trees stored
/// with them, as plain values (dictionaries by field name, lists, byte arrays, numbers, strings).
/// </summary>
public static class UnityFs
{
    /// <summary>The files in a bundle (name -> bytes): the serialized file(s) and any resource files next to them.</summary>
    public static Dictionary<string, byte[]> Open(byte[] b)
    {
        var r = new Reader(b, true);
        string sig = r.CString();
        if(sig != "UnityFS")
            throw new InvalidDataException($"not a Unity bundle ({sig})");
        uint version = r.U32();
        r.CString();
        r.CString();
        r.I64();
        uint compressedInfo = r.U32(), uncompressedInfo = r.U32(), flags = r.U32();
        if(version >= 7)
            r.Align(16);
        byte[] info;
        if((flags & 0x80) != 0)
            info = b.AsSpan(b.Length - (int)compressedInfo, (int)compressedInfo).ToArray();
        else
            info = r.Bytes((int)compressedInfo);
        info = Decompress(info, flags, (int)uncompressedInfo);
        var ri = new Reader(info, true);
        ri.Bytes(16);
        int blockCount = ri.I32();
        var blocks = new (uint U, uint C, ushort F)[blockCount];
        for(int i = 0; i < blockCount; i++)
            blocks[i] = (ri.U32(), ri.U32(), ri.U16());
        int nodeCount = ri.I32();
        var nodes = new (long Offset, long Size, string Name)[nodeCount];
        for(int i = 0; i < nodeCount; i++)
        {
            long off = ri.I64(), size = ri.I64();
            ri.U32();
            nodes[i] = (off, size, ri.CString());
        }
        if((flags & 0x200) != 0)
            r.Align(16);
        long total = 0;
        foreach(var bl in blocks)
            total += bl.U;
        var data = new byte[total];
        int at = 0;
        foreach(var bl in blocks)
        {
            var chunk = Decompress(r.Bytes((int)bl.C), bl.F, (int)bl.U);
            Buffer.BlockCopy(chunk, 0, data, at, chunk.Length);
            at += (int)bl.U;
        }
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach(var (off, size, name) in nodes)
            files[name] = data.AsSpan((int)off, (int)size).ToArray();
        return files;
    }

    private static byte[] Decompress(byte[] src, uint flags, int size)
    {
        switch(flags & 0x3f)
        {
            case 0:
                return src;
            case 1:
                return Lzma.Decode(src, size);
            case 2:
            case 3:
                return Lz4.Decode(src, size);
            default:
                throw new InvalidDataException($"unknown compression {flags & 0x3f}");
        }
    }
}

/// <summary>LZ4 block decompression.</summary>
public static class Lz4
{
    public static byte[] Decode(byte[] src, int size)
    {
        var dst = new byte[size];
        int i = 0, o = 0, n = src.Length;
        while(i < n)
        {
            int token = src[i++];
            int lit = token >> 4;
            if(lit == 15)
            {
                int b;
                do
                {
                    b = src[i++];
                    lit += b;
                }
                while(b == 255);
            }
            Buffer.BlockCopy(src, i, dst, o, lit);
            i += lit;
            o += lit;
            if(i >= n || o >= size)
                break;
            int offset = src[i] | (src[i + 1] << 8);
            i += 2;
            int match = token & 15;
            if(match == 15)
            {
                int b;
                do
                {
                    b = src[i++];
                    match += b;
                }
                while(b == 255);
            }
            match += 4;
            int from = o - offset;
            for(int k = 0; k < match; k++)
                dst[o + k] = dst[from + k];
            o += match;
        }
        return dst;
    }
}

/// <summary>LZMA decoding (Unity's: the 5 property bytes, then the stream; the size is known).</summary>
public sealed class Lzma
{
    public static byte[] Decode(byte[] src, int size)
    {
        var d = new Lzma(src, size);
        d.Run();
        return d._out;
    }

    private readonly byte[] _in, _out;
    private int _ip, _op;
    private uint _range, _code;
    private readonly int _lc, _lp, _pb;
    private readonly ushort[] _probs;

    private const int kNumStates = 12, kNumPosBitsMax = 4, kEndPosModelIndex = 14, kNumFullDistances = 128, kNumAlignBits = 4, kMatchMinLen = 2;

    // Probability tables, laid out as in the reference decoder.
    private const int IsMatch = 0, IsRep = IsMatch + (kNumStates << kNumPosBitsMax), IsRepG0 = IsRep + kNumStates,
        IsRepG1 = IsRepG0 + kNumStates, IsRepG2 = IsRepG1 + kNumStates, IsRep0Long = IsRepG2 + kNumStates,
        PosSlot = IsRep0Long + (kNumStates << kNumPosBitsMax), SpecPos = PosSlot + (4 << 6),
        Align = SpecPos + kNumFullDistances - kEndPosModelIndex, LenCoder = Align + (1 << kNumAlignBits),
        RepLenCoder = LenCoder + 0x404, Literal = RepLenCoder + 0x404;

    private Lzma(byte[] src, int size)
    {
        _in = src;
        _out = new byte[size];
        int p = src[0];
        _lc = p % 9;
        p /= 9;
        _lp = p % 5;
        _pb = p / 5;
        _probs = new ushort[Literal + (0x300 << (_lc + _lp))];
        Array.Fill(_probs, (ushort)1024);
        _ip = 5;
        _range = 0xFFFFFFFF;
        _code = 0;
        for(int i = 0; i < 5; i++)
            _code = (_code << 8) | NextByte();
    }

    private byte NextByte() => _ip < _in.Length ? _in[_ip++] : (byte)0;

    private void Normalize()
    {
        if(_range < (1u << 24))
        {
            _range <<= 8;
            _code = (_code << 8) | NextByte();
        }
    }

    private int Bit(int index)
    {
        uint bound = (_range >> 11) * _probs[index];
        int bit;
        if(_code < bound)
        {
            _range = bound;
            _probs[index] += (ushort)((2048 - _probs[index]) >> 5);
            bit = 0;
        }
        else
        {
            _range -= bound;
            _code -= bound;
            _probs[index] -= (ushort)(_probs[index] >> 5);
            bit = 1;
        }
        Normalize();
        return bit;
    }

    private uint Direct(int count)
    {
        uint res = 0;
        for(int i = 0; i < count; i++)
        {
            _range >>= 1;
            uint t = (_code - _range) >> 31;
            _code -= _range & (t - 1);
            res = (res << 1) | (1 - t);
            Normalize();
        }
        return res;
    }

    private int Tree(int base_, int bits)
    {
        int m = 1;
        for(int i = 0; i < bits; i++)
            m = (m << 1) | Bit(base_ + m);
        return m - (1 << bits);
    }

    private int ReverseTree(int base_, int bits)
    {
        int m = 1, sym = 0;
        for(int i = 0; i < bits; i++)
        {
            int bit = Bit(base_ + m);
            m = (m << 1) | bit;
            sym |= bit << i;
        }
        return sym;
    }

    // Length decoder: choice, choice2, low[16][8], mid[16][8], high[256].
    private int Len(int base_, int posState)
    {
        if(Bit(base_) == 0)
            return Tree(base_ + 2 + (posState << 3), 3);
        if(Bit(base_ + 1) == 0)
            return 8 + Tree(base_ + 2 + 128 + (posState << 3), 3);
        return 16 + Tree(base_ + 2 + 256, 8);
    }

    private void Run()
    {
        int state = 0;
        uint rep0 = 0, rep1 = 0, rep2 = 0, rep3 = 0;
        int pbMask = (1 << _pb) - 1, lpMask = (1 << _lp) - 1;
        while(_op < _out.Length)
        {
            int posState = _op & pbMask;
            if(Bit(IsMatch + (state << kNumPosBitsMax) + posState) == 0)
            {
                byte prev = _op > 0 ? _out[_op - 1] : (byte)0;
                int lit = Literal + 0x300 * (((_op & lpMask) << _lc) + (prev >> (8 - _lc)));
                int sym = 1;
                if(state >= 7)
                {
                    int match = _out[_op - (int)rep0 - 1];
                    while(sym < 0x100)
                    {
                        int matchBit = (match >> 7) & 1;
                        match <<= 1;
                        int bit = Bit(lit + ((1 + matchBit) << 8) + sym);
                        sym = (sym << 1) | bit;
                        if(matchBit != bit)
                            break;
                    }
                }
                while(sym < 0x100)
                    sym = (sym << 1) | Bit(lit + sym);
                _out[_op++] = (byte)sym;
                state = state < 4 ? 0 : state < 10 ? state - 3 : state - 6;
                continue;
            }
            int len;
            if(Bit(IsRep + state) != 0)
            {
                if(Bit(IsRepG0 + state) == 0)
                {
                    if(Bit(IsRep0Long + (state << kNumPosBitsMax) + posState) == 0)
                    {
                        state = state < 7 ? 9 : 11;
                        _out[_op] = _out[_op - (int)rep0 - 1];
                        _op++;
                        continue;
                    }
                }
                else
                {
                    uint dist;
                    if(Bit(IsRepG1 + state) == 0)
                        dist = rep1;
                    else
                    {
                        if(Bit(IsRepG2 + state) == 0)
                            dist = rep2;
                        else
                        {
                            dist = rep3;
                            rep3 = rep2;
                        }
                        rep2 = rep1;
                    }
                    rep1 = rep0;
                    rep0 = dist;
                }
                len = Len(RepLenCoder, posState);
                state = state < 7 ? 8 : 11;
            }
            else
            {
                rep3 = rep2;
                rep2 = rep1;
                rep1 = rep0;
                len = Len(LenCoder, posState);
                state = state < 7 ? 7 : 10;
                int lenState = Math.Min(len, 3);
                int slot = Tree(PosSlot + (lenState << 6), 6);
                if(slot < 4)
                    rep0 = (uint)slot;
                else
                {
                    int direct = (slot >> 1) - 1;
                    rep0 = (uint)((2 | (slot & 1)) << direct);
                    if(slot < kEndPosModelIndex)
                        rep0 += (uint)ReverseTree(SpecPos + (int)rep0 - slot - 1, direct);
                    else
                    {
                        rep0 += Direct(direct - kNumAlignBits) << kNumAlignBits;
                        rep0 += (uint)ReverseTree(Align, kNumAlignBits);
                        if(rep0 == 0xFFFFFFFF)
                            break; // end marker
                    }
                }
            }
            len += kMatchMinLen;
            int from = _op - (int)rep0 - 1;
            if(from < 0)
                throw new InvalidDataException("bad LZMA data");
            for(int k = 0; k < len && _op < _out.Length; k++)
                _out[_op++] = _out[from + k];
        }
    }
}

/// <summary>A byte reader, big or little endian.</summary>
public sealed class Reader
{
    public readonly byte[] B;
    public int P;
    public bool Big;

    public Reader(byte[] b, bool big)
    {
        B = b;
        Big = big;
    }

    private ReadOnlySpan<byte> Take(int n)
    {
        var s = B.AsSpan(P, n);
        P += n;
        return s;
    }

    public byte U8() => B[P++];
    public sbyte I8() => (sbyte)B[P++];
    public ushort U16() => Big ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(Take(2)) : System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public short I16() => (short)U16();
    public uint U32() => Big ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(Take(4)) : System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public int I32() => (int)U32();
    public ulong U64() => Big ? System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(Take(8)) : System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
    public long I64() => (long)U64();
    public float F32() => BitConverter.Int32BitsToSingle(I32());
    public double F64() => BitConverter.Int64BitsToDouble(I64());
    public byte[] Bytes(int n) => Take(n).ToArray();
    public void Align(int n) => P = (P + n - 1) / n * n;

    public string CString()
    {
        int end = Array.IndexOf(B, (byte)0, P);
        if(end < 0)
            end = B.Length;
        var s = Encoding.UTF8.GetString(B, P, end - P);
        P = end + 1;
        return s;
    }
}

/// <summary>One field of a type tree.</summary>
public sealed class TypeNode
{
    public string Type = "", Name = "";
    public int Size, Level;
    public uint Flags;
    public readonly List<TypeNode> Children = new();
    public bool Aligned => (Flags & 0x4000) != 0;
}

/// <summary>A serialized file (format 9 to 17 or so): its types and objects.</summary>
public sealed class SerializedFile
{
    public readonly record struct Entry(long PathId, int Start, int Size, int ClassId, TypeNode? Tree);

    private readonly byte[] _b;
    private readonly bool _big;
    public uint Version { get; }
    public string UnityVersion { get; } = "";
    public Dictionary<long, Entry> Objects { get; } = new();
    public List<string> Externals { get; } = new();

    public SerializedFile(byte[] b)
    {
        _b = b;
        var r = new Reader(b, true);
        r.U32();
        r.U32();
        Version = r.U32();
        uint dataOffset = r.U32();
        if(Version < 9)
            throw new InvalidDataException($"serialized file version {Version} isn't supported");
        byte endian = r.U8();
        r.Bytes(3);
        r.Big = endian != 0;
        _big = r.Big;
        UnityVersion = r.CString();
        r.U32();
        bool hasTree = Version < 13 || r.U8() != 0;
        var types = new List<(int ClassId, TypeNode? Tree)>();
        int typeCount = r.I32();
        for(int i = 0; i < typeCount; i++)
        {
            int classId = r.I32();
            if(Version >= 16)
                r.U8();
            if(Version >= 17)
                r.I16();
            if(Version >= 13)
            {
                if((Version < 16 && classId < 0) || (Version >= 16 && classId == 114))
                    r.Bytes(16);
                r.Bytes(16);
            }
            types.Add((classId, hasTree ? ReadTree(r) : null));
        }
        int objectCount = r.I32();
        for(int i = 0; i < objectCount; i++)
        {
            long pathId;
            if(Version >= 14)
            {
                r.Align(4);
                pathId = r.I64();
            }
            else
                pathId = r.I32();
            uint start = r.U32(), size = r.U32();
            int typeIndex = r.I32();
            if(Version < 16)
                r.U16();
            if(Version < 11)
                r.U16();
            if(Version >= 11 && Version < 17)
                r.I16();
            if(Version == 15 || Version == 16)
                r.U8();
            var (cid, tree) = typeIndex >= 0 && typeIndex < types.Count ? types[typeIndex] : (typeIndex, null);
            // (before format 16 the type index is the class id itself)
            if(Version < 16)
                foreach(var t in types)
                    if(t.ClassId == typeIndex)
                    {
                        (cid, tree) = t;
                        break;
                    }
            Objects[pathId] = new Entry(pathId, (int)(start + dataOffset), (int)size, cid, tree);
        }
        if(Version >= 11)
        {
            int scripts = r.I32();
            for(int i = 0; i < scripts; i++)
            {
                r.I32();
                if(Version >= 14)
                {
                    r.Align(4);
                    r.I64();
                }
                else
                    r.I32();
            }
        }
        int externals = r.I32();
        for(int i = 0; i < externals; i++)
        {
            if(Version >= 6)
                r.CString();
            if(Version >= 5)
            {
                r.Bytes(16);
                r.I32();
            }
            Externals.Add(r.CString());
        }
    }

    private static TypeNode ReadTree(Reader r)
    {
        int count = r.I32(), stringSize = r.I32();
        var raw = new (int Level, uint Type, uint Name, int Size, uint Flags)[count];
        for(int i = 0; i < count; i++)
        {
            r.U16();
            int level = r.U8();
            r.U8();
            uint type = r.U32(), name = r.U32();
            int size = r.I32();
            r.I32();
            uint flags = r.U32();
            raw[i] = (level, type, name, size, flags);
        }
        var strings = r.Bytes(stringSize);
        string Name(uint o)
        {
            if((o & 0x80000000) != 0)
                return CommonStrings.Get((int)(o & 0x7fffffff));
            int end = Array.IndexOf(strings, (byte)0, (int)o);
            return Encoding.UTF8.GetString(strings, (int)o, (end < 0 ? strings.Length : end) - (int)o);
        }
        var nodes = new TypeNode[count];
        for(int i = 0; i < count; i++)
            nodes[i] = new TypeNode { Type = Name(raw[i].Type), Name = Name(raw[i].Name), Size = raw[i].Size, Level = raw[i].Level, Flags = raw[i].Flags };
        var stack = new Stack<TypeNode>();
        stack.Push(nodes[0]);
        for(int i = 1; i < count; i++)
        {
            while(stack.Peek().Level >= nodes[i].Level)
                stack.Pop();
            stack.Peek().Children.Add(nodes[i]);
            stack.Push(nodes[i]);
        }
        return nodes[0];
    }

    /// <summary>An object's fields, read through its type tree.</summary>
    public Dictionary<string, object?>? Read(long pathId)
    {
        if(!Objects.TryGetValue(pathId, out var e) || e.Tree == null)
            return null;
        var r = new Reader(_b, _big) { P = e.Start };
        return ReadNode(r, e.Tree) as Dictionary<string, object?>;
    }

    private static object? ReadNode(Reader r, TypeNode n)
    {
        bool align = n.Aligned;
        object? v;
        switch(n.Type)
        {
            case "SInt8": v = (int)r.I8(); break;
            case "UInt8": case "char": v = (int)r.U8(); break;
            case "bool": v = r.U8() != 0; break;
            case "SInt16": case "short": v = (int)r.I16(); break;
            case "UInt16": case "unsigned short": v = (int)r.U16(); break;
            case "SInt32": case "int": v = r.I32(); break;
            case "UInt32": case "unsigned int": case "Type*": v = (long)r.U32(); break;
            case "SInt64": case "long long": v = r.I64(); break;
            case "UInt64": case "unsigned long long": case "FileSize": v = (long)r.U64(); break;
            case "float": v = r.F32(); break;
            case "double": v = r.F64(); break;
            case "string":
            {
                int len = r.I32();
                v = Encoding.UTF8.GetString(r.B, r.P, len);
                r.P += len;
                r.Align(4);
                break;
            }
            case "TypelessData":
            {
                int len = r.I32();
                v = r.Bytes(len);
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
                    if(el.Type is "UInt8" or "char" or "SInt8")
                        v = r.Bytes(len);
                    else
                    {
                        var list = new List<object?>(Math.Min(len, 1 << 20));
                        for(int i = 0; i < len; i++)
                            list.Add(ReadNode(r, el));
                        v = list;
                    }
                }
                else if(n.Type == "pair" && n.Children.Count == 2)
                    v = new KeyValuePair<object?, object?>(ReadNode(r, n.Children[0]), ReadNode(r, n.Children[1]));
                else
                {
                    var d = new Dictionary<string, object?>(n.Children.Count);
                    foreach(var c in n.Children)
                        d[c.Name] = ReadNode(r, c);
                    v = d;
                }
                break;
        }
        if(align)
            r.Align(4);
        return v;
    }
}

/// <summary>Unity's shared type-tree strings (by offset).</summary>
internal static class CommonStrings
{
    private const string All = "AABB\0AnimationClip\0AnimationCurve\0AnimationState\0Array\0Base\0BitField\0bitset\0bool\0char\0ColorRGBA\0Component\0data\0deque\0double\0dynamic_array\0FastPropertyName\0first\0float\0Font\0GameObject\0Generic Mono\0GradientNEW\0GUID\0GUIStyle\0int\0list\0long long\0map\0Matrix4x4f\0MdFour\0MonoBehaviour\0MonoScript\0m_ByteSize\0m_Curve\0m_EditorClassIdentifier\0m_EditorHideFlags\0m_Enabled\0m_ExtensionPtr\0m_GameObject\0m_Index\0m_IsArray\0m_IsStatic\0m_MetaFlag\0m_Name\0m_ObjectHideFlags\0m_PrefabInternal\0m_PrefabParentObject\0m_Script\0m_StaticEditorFlags\0m_Type\0m_Version\0Object\0pair\0PPtr<Component>\0PPtr<GameObject>\0PPtr<Material>\0PPtr<MonoBehaviour>\0PPtr<MonoScript>\0PPtr<Object>\0PPtr<Prefab>\0PPtr<Sprite>\0PPtr<TextAsset>\0PPtr<Texture>\0PPtr<Texture2D>\0PPtr<Transform>\0Prefab\0Quaternionf\0Rectf\0RectInt\0RectOffset\0second\0set\0short\0size\0SInt16\0SInt32\0SInt64\0SInt8\0staticvector\0string\0TextAsset\0TextMesh\0Texture\0Texture2D\0Transform\0TypelessData\0UInt16\0UInt32\0UInt64\0UInt8\0unsigned int\0unsigned long long\0unsigned short\0vector\0Vector2f\0Vector3f\0Vector4f\0m_ScriptingClassIdentifier\0Gradient\0Type*\0int2_storage\0int3_storage\0BoundsInt\0m_CorrespondingSourceObject\0m_PrefabInstance\0m_PrefabAsset\0FileSize\0Hash128\0";
    private static Dictionary<int, string>? _map;

    public static string Get(int offset)
    {
        if(_map == null)
        {
            var m = new Dictionary<int, string>();
            int o = 0;
            foreach(var part in All.Split('\0'))
            {
                if(part.Length == 0)
                    break;
                m[o] = part;
                o += part.Length + 1;
            }
            _map = m;
        }
        return _map.TryGetValue(offset, out var s) ? s : $"?{offset}";
    }
}

/// <summary>Helpers for reading the plain values the reader gives.</summary>
public static class Val
{
    public static Dictionary<string, object?>? D(this object? o, string key) => (o as Dictionary<string, object?>)?.GetValueOrDefault(key) as Dictionary<string, object?>;
    public static List<object?> L(this object? o, string key) => (o as Dictionary<string, object?>)?.GetValueOrDefault(key) as List<object?> ?? new();
    public static object? Get(this object? o, string key) => (o as Dictionary<string, object?>)?.GetValueOrDefault(key);
    public static byte[] Bytes(this object? o, string key) => (o as Dictionary<string, object?>)?.GetValueOrDefault(key) as byte[] ?? [];
    public static string S(this object? o, string key) => (o as Dictionary<string, object?>)?.GetValueOrDefault(key) as string ?? "";

    public static float F(this object? o, string key, float fallback = 0f) => Num((o as Dictionary<string, object?>)?.GetValueOrDefault(key), fallback);
    public static long I(this object? o, string key, long fallback = 0) => (o as Dictionary<string, object?>)?.GetValueOrDefault(key) switch
    {
        int i => i,
        long l => l,
        float f => (long)f,
        bool b => b ? 1 : 0,
        _ => fallback,
    };

    public static float Num(object? v, float fallback = 0f) => v switch
    {
        float f => f,
        double d => (float)d,
        int i => i,
        long l => l,
        _ => fallback,
    };

    /// <summary>A PPtr's (file id, path id).</summary>
    public static (int File, long Path) Ptr(this object? o, string key)
    {
        var p = o.D(key);
        return p == null ? (0, 0) : ((int)p.I("m_FileID"), p.I("m_PathID"));
    }

    public static System.Numerics.Vector3 V3(this object? o, string key)
    {
        var v = o.D(key);
        return v == null ? default : new System.Numerics.Vector3(v.F("x"), v.F("y"), v.F("z"));
    }

    public static System.Numerics.Quaternion Q(this object? o, string key)
    {
        var v = o.D(key);
        return v == null ? System.Numerics.Quaternion.Identity : new System.Numerics.Quaternion(v.F("x"), v.F("y"), v.F("z"), v.F("w"));
    }
}
