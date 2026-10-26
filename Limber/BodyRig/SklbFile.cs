using System;
using System.IO;

namespace Limber.BodyRig;

/// <summary>FFXIV's .sklb: a small game header followed by Havok skeleton data. Only the Havok part is changed.</summary>
public sealed class SklbFile
{
    private const uint Magic = 0x736B6C62; // "blks"

    public byte[] Header = Array.Empty<byte>();
    public byte[] Havok = Array.Empty<byte>();

    public static SklbFile Read(byte[] data)
    {
        if (data.Length < 16 || BitConverter.ToUInt32(data, 0) != Magic)
            throw new InvalidDataException("Not an FFXIV skeleton (.sklb) file.");
        // Two header layouts: the old one keeps the offsets as 16-bit numbers, the newer one as 32-bit.
        ushort version = BitConverter.ToUInt16(data, 6);
        int havokOffset = version == 0x3132 ? BitConverter.ToInt16(data, 10) : BitConverter.ToInt32(data, 12);
        if (havokOffset <= 0 || havokOffset >= data.Length)
            throw new InvalidDataException("Damaged skeleton file (bad Havok offset).");
        return new SklbFile
        {
            Header = data.AsSpan(0, havokOffset).ToArray(),
            Havok = data.AsSpan(havokOffset).ToArray(),
        };
    }

    public byte[] ToBytes()
    {
        var result = new byte[Header.Length + Havok.Length];
        Header.CopyTo(result, 0);
        Havok.CopyTo(result, Header.Length);
        return result;
    }
}
