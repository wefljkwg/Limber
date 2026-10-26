using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Limber.BodyRig;

/// <summary>
/// The game's racial deformer file (chara/xls/boneDeformer/human.pbd): how a model made for one race is fitted to
/// another's body, bone by bone. Bones it doesn't list aren't fitted at all, so a model worn by another race than the
/// one it was made for (most gear: one model shared by several races) would leave everything weighted to Limber's
/// rig bones in the other race's proportions: gaps at the seams. Each rig bone is given its parent's fitting (the
/// nearest bone above it that has one), which is exactly how those vertices were fitted before they were rigged.
/// </summary>
public static class Deformers
{
    public const string GamePath = "chara/xls/boneDeformer/human.pbd";

    private sealed class Deformer
    {
        public ushort Race;
        public short Tree;
        public float Scale;
        public List<(string Bone, byte[] Matrix)> Bones = new(); // 12 floats each, as stored
        public bool Empty;
    }

    /// <summary>
    /// The deformer file with every bone <paramref name="isRig"/> picks (named in <paramref name="parentOf"/>) given its
    /// nearest fitted ancestor's matrix; earlier rig entries are replaced. Null if the file can't be read.
    /// </summary>
    public static byte[]? WithRig(byte[] pbd, IReadOnlyDictionary<string, string> parentOf, Func<string, bool> isRig, out int added)
    {
        added = 0;
        if (pbd.Length < 4)
            return null;
        int count = BitConverter.ToInt32(pbd, 0);
        if (count <= 0 || count > 4096 || 4 + count * 20 > pbd.Length)
            return null;
        var deformers = new Deformer[count];
        var offsets = new int[count];
        int blobsEnd = 4 + count * 20;
        for (int i = 0; i < count; i++)
        {
            int at = 4 + i * 12;
            deformers[i] = new Deformer
            {
                Race = BitConverter.ToUInt16(pbd, at),
                Tree = BitConverter.ToInt16(pbd, at + 2),
                Scale = BitConverter.ToSingle(pbd, at + 8),
            };
            offsets[i] = BitConverter.ToInt32(pbd, at + 4);
        }
        var tree = pbd.AsSpan(4 + count * 12, count * 8).ToArray();
        for (int i = 0; i < count; i++)
        {
            if (offsets[i] == 0)
            {
                deformers[i].Empty = true;
                continue;
            }
            int end = ReadDeformer(pbd, offsets[i], deformers[i].Bones);
            if (end < 0)
                return null;
            blobsEnd = Math.Max(blobsEnd, end);
        }
        // Whatever follows the deformers (newer files carry an extra packed block): kept as it is, after them.
        var trailing = blobsEnd < pbd.Length ? pbd.AsSpan(blobsEnd).ToArray() : Array.Empty<byte>();

        foreach (var d in deformers)
        {
            if (d.Empty)
                continue;
            d.Bones.RemoveAll(b => isRig(b.Bone));
            var known = d.Bones.ToDictionary(b => b.Bone, b => b.Matrix);
            foreach (var (bone, _) in parentOf)
            {
                if (!isRig(bone) || known.ContainsKey(bone))
                    continue;
                // The nearest bone above it (that isn't a rig bone itself) this race has a fitting for.
                string? up = parentOf.TryGetValue(bone, out var p) ? p : null;
                for (int guard = 0; up != null && guard < 64; guard++)
                {
                    if (!isRig(up) && known.TryGetValue(up, out var matrix))
                    {
                        d.Bones.Add((bone, matrix));
                        added++;
                        break;
                    }
                    up = parentOf.TryGetValue(up, out var next) ? next : null;
                }
            }
        }
        return Write(deformers, tree, trailing);
    }

    // One deformer's bones and matrices; returns where its data ends (strings included, 4-aligned), or -1.
    private static int ReadDeformer(byte[] pbd, int at, List<(string, byte[])> bones)
    {
        if (at < 0 || at + 4 > pbd.Length)
            return -1;
        int n = BitConverter.ToInt32(pbd, at);
        if (n < 0 || n > 20000)
            return -1;
        int names = at + 4;
        int matrices = names + n * 2 + ((n & 1) != 0 ? 2 : 0);
        if (matrices + n * 48 > pbd.Length)
            return -1;
        int end = matrices + n * 48;
        for (int i = 0; i < n; i++)
        {
            int s = at + BitConverter.ToUInt16(pbd, names + i * 2);
            int e = s;
            while (e < pbd.Length && pbd[e] != 0)
                e++;
            if (e >= pbd.Length)
                return -1;
            bones.Add((Encoding.UTF8.GetString(pbd, s, e - s), pbd.AsSpan(matrices + i * 48, 48).ToArray()));
            end = Math.Max(end, e + 1);
        }
        return (end + 3) & ~3;
    }

    private static byte[] Write(Deformer[] deformers, byte[] tree, byte[] trailing)
    {
        var blobs = deformers.Select(d => d.Empty || d.Bones.Count == 0 ? Array.Empty<byte>() : Blob(d.Bones)).ToArray();
        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            w.Write(deformers.Length);
            int next = 4 + deformers.Length * 20;
            for (int i = 0; i < deformers.Length; i++)
            {
                w.Write(deformers[i].Race);
                w.Write(deformers[i].Tree);
                w.Write(blobs[i].Length == 0 ? 0 : next);
                w.Write(deformers[i].Scale);
                next += blobs[i].Length;
            }
            w.Write(tree);
            foreach (var blob in blobs)
                w.Write(blob);
            w.Write(trailing);
        }
        return stream.ToArray();
    }

    // Count, name offsets (from the blob's start), padding to 4, matrices, then the names (null-ended), padded to 4.
    private static byte[] Blob(List<(string Bone, byte[] Matrix)> bones)
    {
        int n = bones.Count;
        int header = 4 + n * 2 + ((n & 1) != 0 ? 2 : 0) + n * 48;
        var names = new MemoryStream();
        var offsets = new ushort[n];
        var seen = new Dictionary<string, int>();
        for (int i = 0; i < n; i++)
        {
            if (!seen.TryGetValue(bones[i].Bone, out int at))
            {
                at = (int)names.Length;
                seen[bones[i].Bone] = at;
                var bytes = Encoding.UTF8.GetBytes(bones[i].Bone);
                names.Write(bytes);
                names.WriteByte(0);
            }
            offsets[i] = (ushort)(header + at);
        }
        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            w.Write(n);
            foreach (var o in offsets)
                w.Write(o);
            if ((n & 1) != 0)
                w.Write((ushort)0);
            foreach (var (_, matrix) in bones)
                w.Write(matrix);
            w.Write(names.ToArray());
            while ((stream.Length & 3) != 0)
                w.Write((byte)0);
        }
        return stream.ToArray();
    }
}
