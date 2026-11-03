using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;

namespace Limber.Rig;

/// <summary>
/// Reads a .chara look file (the JSON other posing tools save a character's look in: its race, clan, body and face
/// values by name, and each gear slot's model and dyes) into a look. A child body isn't taken.
/// </summary>
public static class CharaFile
{
    private static readonly Dictionary<string, byte> Races = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Hyur"] = 1, ["Elezen"] = 2, ["Lalafel"] = 3, ["Lalafell"] = 3, ["Miqote"] = 4, ["Miqo'te"] = 4, ["Roegadyn"] = 5,
        ["AuRa"] = 6, ["Au Ra"] = 6, ["Hrothgar"] = 7, ["Viera"] = 8,
    };

    private static readonly Dictionary<string, byte> Tribes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Midlander"] = 1, ["Highlander"] = 2, ["Wildwood"] = 3, ["Duskwight"] = 4, ["Plainsfolk"] = 5, ["Dunesfolk"] = 6,
        ["SeekerOfTheSun"] = 7, ["KeeperOfTheMoon"] = 8, ["SeaWolf"] = 9, ["Hellsguard"] = 10, ["Raen"] = 11, ["Xaela"] = 12,
        ["Hellion"] = 13, ["Lost"] = 14, ["Rava"] = 15, ["Veena"] = 16,
    };

    private static readonly Dictionary<string, byte> Genders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Masculine"] = 0, ["Male"] = 0, ["Feminine"] = 1, ["Female"] = 1,
    };

    private static readonly Dictionary<string, byte> Ages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Normal"] = 1, ["Old"] = 3, ["Young"] = 4,
    };

    private static readonly Dictionary<string, byte> Features = new(StringComparer.OrdinalIgnoreCase)
    {
        ["None"] = 0, ["First"] = 1, ["Second"] = 2, ["Third"] = 4, ["Fourth"] = 8, ["Fifth"] = 16, ["Sixth"] = 32,
        ["Seventh"] = 64, ["LegacyTattoo"] = 128,
    };

    // The customize bytes in order, by the names .chara files use.
    private static readonly (int Index, string Key)[] Numbers =
    [
        (3, "Height"), (5, "Head"), (6, "Hair"), (8, "Skintone"), (9, "REyeColor"), (10, "HairTone"), (11, "Highlights"),
        (13, "LimbalEyes"), (14, "Eyebrows"), (15, "LEyeColor"), (16, "Eyes"), (17, "Nose"), (18, "Jaw"), (19, "Mouth"),
        (20, "LipsToneFurPattern"), (21, "EarMuscleTailSize"), (22, "TailEarsType"), (23, "Bust"), (24, "FacePaint"),
        (25, "FacePaintColor"),
    ];

    // Limber's gear order: head, body, hands, legs, feet, ears, neck, wrists, right ring, left ring.
    private static readonly string[] GearKeys = ["HeadGear", "Body", "Hands", "Legs", "Feet", "Ears", "Neck", "Wrists", "RightRing", "LeftRing"];

    /// <summary>The look in a .chara file, or null (with why).</summary>
    public static AppearanceData? Read(string json, string fallbackName, out string error)
    {
        error = "";
        JObject o;
        try
        {
            o = JObject.Parse(json);
        }
        catch
        {
            error = "That file isn't a look file (it isn't JSON).";
            return null;
        }
        if(o["Race"] == null || o["Tribe"] == null)
        {
            error = "That file has no race or clan in it (not a .chara look file?).";
            return null;
        }
        var look = new AppearanceData { Name = fallbackName };
        var cz = look.Customize;
        cz[0] = Named(o["Race"], Races);
        cz[1] = Named(o["Gender"], Genders);
        cz[2] = o["Age"] == null ? (byte)1 : Named(o["Age"], Ages);
        cz[4] = Named(o["Tribe"], Tribes);
        if(cz[2] == 4)
        {
            error = "That look is a child's body: it isn't used.";
            return null;
        }
        if(cz[2] == 0)
            cz[2] = 1;
        foreach(var (index, key) in Numbers)
            cz[index] = Byte(o[key]);
        cz[7] = Bool(o["EnableHighlights"]) ? (byte)0x80 : (byte)0;
        cz[12] = Flags(o["FacialFeatures"]);
        if(cz[0] is < 1 or > 8 || cz[4] is < 1 or > 16)
        {
            error = "That file's race or clan isn't one Limber knows.";
            return null;
        }

        for(int s = 0; s < GearKeys.Length; s++)
            if(o[GearKeys[s]] is JObject g)
                look.Gear[s] = new EquipmentModelId
                {
                    Id = (ushort)Num(g["ModelBase"]), Variant = (byte)Num(g["ModelVariant"]),
                    Stain0 = (byte)Num(g["DyeId"]), Stain1 = (byte)Num(g["DyeId2"]),
                }.Value;
        foreach(var (w, key) in new[] { (0, "MainHand"), (1, "OffHand") })
            if(o[key] is JObject g)
                look.Weapons[w] = new WeaponModelId
                {
                    Id = (ushort)Num(g["ModelSet"]), Type = (ushort)Num(g["ModelBase"]), Variant = (ushort)Num(g["ModelVariant"]),
                    Stain0 = (byte)Num(g["DyeId"]), Stain1 = (byte)Num(g["DyeId2"]),
                }.Value;
        return look;
    }

    private static long Num(JToken? t)
    {
        if(t == null)
            return 0;
        if(t.Type is JTokenType.Integer or JTokenType.Float)
            return (long)t;
        return long.TryParse(t.ToString(), out var v) ? v : 0;
    }

    private static byte Byte(JToken? t) => (byte)Math.Clamp(Num(t), 0, 255);

    private static bool Bool(JToken? t)
        => t != null && (t.Type == JTokenType.Boolean ? (bool)t : Num(t) != 0 || string.Equals(t.ToString(), "true", StringComparison.OrdinalIgnoreCase));

    // A name from the list, or the number itself.
    private static byte Named(JToken? t, Dictionary<string, byte> names)
    {
        if(t == null)
            return 0;
        if(t.Type == JTokenType.Integer)
            return Byte(t);
        var text = t.ToString().Trim();
        if(names.TryGetValue(text, out var v) || names.TryGetValue(text.Replace(" ", ""), out v))
            return v;
        return byte.TryParse(text, out var n) ? n : (byte)0;
    }

    // Facial feature flags: a number, or names joined with commas ("First, Third").
    private static byte Flags(JToken? t)
    {
        if(t == null)
            return 0;
        if(t.Type == JTokenType.Integer)
            return Byte(t);
        int bits = 0;
        foreach(var part in t.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            bits |= Features.TryGetValue(part, out var b) ? b : byte.TryParse(part, out var n) ? n : 0;
        return (byte)bits;
    }
}
