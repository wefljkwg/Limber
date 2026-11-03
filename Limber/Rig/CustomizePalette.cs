using System;
using System.Collections.Generic;

namespace Limber.Rig;

/// <summary>
/// The character creator's own choices, from the game's data: the colour palettes (chara/xls/charamake/human.cmp: a
/// block of RGBA colours, the interface versions of each palette, and per clan and sex the skin and hair ones) and each
/// clan and sex's hairstyles with their preview icons (the HairMakeType and CharaMakeCustomize sheets).
/// </summary>
public static class CustomizePalette
{
    // human.cmp: a colour block (shared palettes), its interface twin, then 32 clan / sex blocks. Bytes.
    private const int Full = 256 * 4, Toned = 128 * 4;
    private const int EyesAt = 0, HighlightsAt = Full, LipsDarkAt = 2 * Full, FacePaintDarkAt = 2 * Full + Toned,
        FeaturesAt = 2 * Full + 2 * Toned, LipsLightAt = 3 * Full + 2 * Toned, FacePaintLightAt = 3 * Full + 3 * Toned;
    private const int BlockSize = 3 * Full + 4 * Toned + 4 * Full; // (with the four unused palettes at its end)
    private const int InterfaceAt = BlockSize, RacesAt = 2 * BlockSize;
    private const int RaceSize = Full + 2 * Full + Full + Full; // skin, hair (main + sheen), skin and hair for the interface
    private const int SkinInterfaceAt = 3 * Full, HairInterfaceAt = 4 * Full;

    private static byte[]? _cmp;
    private static bool _cmpTried;

    private static byte[]? Cmp
    {
        get
        {
            if(!_cmpTried)
            {
                _cmpTried = true;
                try
                {
                    _cmp = Plugin.DataManager.GetFile("chara/xls/charamake/human.cmp")?.Data;
                }
                catch
                {
                    _cmp = null;
                }
            }
            return _cmp;
        }
    }

    /// <summary>Whether this customize byte is a colour from a palette.</summary>
    public static bool IsColour(int index) => index is 8 or 9 or 10 or 11 or 13 or 15 or 20 or 25;

    /// <summary>
    /// The palette for a colour customize byte: each choice's value and its colour (ImGui's packed colour). Empty if the
    /// game's data couldn't be read.
    /// </summary>
    public static (byte Value, uint Colour)[] Colours(int index, byte tribe, byte sex)
    {
        var cmp = Cmp;
        if(cmp == null)
            return [];
        try
        {
            int race = Math.Clamp((tribe - 1) * 2 + (sex == 1 ? 1 : 0), 0, 31);
            return index switch
            {
                8 => Run(cmp, RacesAt + race * RaceSize + SkinInterfaceAt, 192, 0),
                10 => Run(cmp, RacesAt + race * RaceSize + HairInterfaceAt, 192, 0),
                11 => Run(cmp, InterfaceAt + HighlightsAt, 192, 0),
                9 or 15 => Run(cmp, InterfaceAt + EyesAt, 192, 0),
                13 => Run(cmp, InterfaceAt + FeaturesAt, 192, 0),
                // dark shades are 0..95, light ones 128..223
                20 => [.. Run(cmp, LipsDarkAt, 96, 0), .. Run(cmp, LipsLightAt, 96, 128)],
                25 => [.. Run(cmp, FacePaintDarkAt, 96, 0), .. Run(cmp, FacePaintLightAt, 96, 128)],
                _ => [],
            };
        }
        catch
        {
            return [];
        }
    }

    private static (byte, uint)[] Run(byte[] cmp, int at, int count, int first)
    {
        var list = new List<(byte, uint)>(count);
        for(int i = 0; i < count; i++)
        {
            int o = at + i * 4;
            if(o + 4 > cmp.Length)
                break;
            // R, G, B, A bytes: read as one number that's ImGui's packed colour (made opaque)
            uint c = BitConverter.ToUInt32(cmp, o) | 0xFF000000u;
            list.Add(((byte)(first + i), c));
        }
        return list.ToArray();
    }

    private static readonly Dictionary<(byte, byte), (byte Value, uint Icon)[]> Hair = new();

    /// <summary>The hairstyles this clan and sex can pick in the character creator: the value and its preview icon.</summary>
    public static (byte Value, uint Icon)[] Hairstyles(byte tribe, byte sex)
    {
        if(Hair.TryGetValue((tribe, sex), out var known))
            return known;
        var list = new List<(byte, uint)>();
        try
        {
            var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.RawRow>(name: "HairMakeType");
            var customize = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.CharaMakeCustomize>();
            uint rowId = (uint)Math.Max(0, (tribe - 1) * 2 + (sex == 1 ? 1 : 0));
            if(sheet != null && customize != null && sheet.TryGetRow(rowId, out var row))
            {
                int count = row.ReadUInt8Column(30); // how many hairstyles
                for(int i = 0; i < count; i++)
                {
                    uint id = row.ReadUInt32Column(66 + i * 9); // each one's row in CharaMakeCustomize
                    if(id == uint.MaxValue || !customize.TryGetRow(id, out var hair) || hair.Icon == 0)
                        continue;
                    list.Add((hair.FeatureID, hair.Icon));
                }
            }
        }
        catch(Exception e)
        {
            Plugin.Log.Debug(e, "Limber: couldn't read the hairstyles");
        }
        list.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        var made = list.ToArray();
        Hair[(tribe, sex)] = made;
        return made;
    }
}
