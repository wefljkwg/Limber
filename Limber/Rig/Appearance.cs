using System;
using System.Collections.Generic;
using System.Linq;
using Limber.Core;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using EquipSlot = FFXIVClientStructs.FFXIV.Client.Game.Character.DrawDataContainer.EquipmentSlot;
using WeaponSlot = FFXIVClientStructs.FFXIV.Client.Game.Character.DrawDataContainer.WeaponSlot;

namespace Limber.Rig;

/// <summary>A character's look as Limber can set it: its customize bytes, gear (model, variant, dyes) and weapons.</summary>
public sealed class AppearanceData
{
    public byte[] Customize { get; set; } = new byte[26];
    /// <summary>Head, body, hands, legs, feet, ears, neck, wrists, right ring, left ring (EquipmentModelId values).</summary>
    public ulong[] Gear { get; set; } = new ulong[10];
    /// <summary>Main hand, off hand (WeaponModelId values).</summary>
    public ulong[] Weapons { get; set; } = new ulong[2];
    public string Name { get; set; } = "";
    /// <summary>The name an actor added with this look gets ("" = Limber 1, Limber 2...).</summary>
    public string ActorName { get; set; } = "";
    /// <summary>A global preset put on an actor added with this look ("" = none).</summary>
    public string GlobalPreset { get; set; } = "";

    public AppearanceData Copy() => new()
    {
        Customize = (byte[])Customize.Clone(), Gear = (ulong[])Gear.Clone(), Weapons = (ulong[])Weapons.Clone(), Name = Name,
        ActorName = ActorName, GlobalPreset = GlobalPreset,
    };
}

/// <summary>
/// Changing a character's appearance and gear, the way Brio and Anamnesis do: the character's own draw data (its
/// customize bytes, the gear and weapon model ids with their dyes) is changed, and gear goes on in place; a change of
/// race, clan, sex or face needs the character drawn again (Penumbra redraws it). Only on your screen: nothing is sent
/// to the server, and the character goes back to its real look whenever the game draws it from scratch (zoning,
/// leaving GPose for a GPose actor). Changing a character into a child body is not allowed.
/// </summary>
public static unsafe class Appearance
{
    public static readonly string[] GearSlots = ["Head", "Body", "Hands", "Legs", "Feet", "Earrings", "Necklace", "Bracelets", "Right ring", "Left ring"];

    private static Character* Get(nint address, ushort index)
    {
        var obj = ActorSkeleton.GetObject(index);
        if(obj == null || (nint)obj != address || !obj->IsCharacter())
            return null;
        return (Character*)obj;
    }

    /// <summary>The character's look as it is now.</summary>
    public static AppearanceData? Read(nint address, ushort index)
    {
        var c = Get(address, index);
        if(c == null)
            return null;
        var data = new AppearanceData();
        var cz = (byte*)&c->DrawData.CustomizeData;
        for(int i = 0; i < 26; i++)
            data.Customize[i] = cz[i];
        for(int s = 0; s < 10; s++)
            data.Gear[s] = c->DrawData.Equipment((EquipSlot)s).Value;
        for(int w = 0; w < 2; w++)
            data.Weapons[w] = c->DrawData.Weapon((WeaponSlot)w).ModelId.Value;
        return data;
    }

    // Customize bytes: 0 race, 1 sex, 2 body type, 4 clan, 5 face. Changing these needs a full redraw.
    private static readonly int[] RedrawBytes = [0, 1, 2, 4, 5];

    /// <summary>Puts a look on the character: customize, gear and weapons. Returns what happened.</summary>
    public static string Apply(nint address, ushort index, AppearanceData look, bool customize = true, bool gear = true, bool weapons = true)
    {
        var c = Get(address, index);
        if(c == null)
            return "The character isn't there.";
        bool redraw = false;
        if(customize && look.Customize.Length >= 26)
        {
            var cz = (byte*)&c->DrawData.CustomizeData;
            // Never a child body (body type 4), and never from an adult into a child race/body.
            byte bodyType = look.Customize[2];
            if(bodyType == 4 || cz[2] == 4)
                return "Changing a child's body, or into one, isn't allowed.";
            bool changed = false;
            for(int i = 0; i < 26; i++)
            {
                if(cz[i] == look.Customize[i])
                    continue;
                if(RedrawBytes.Contains(i))
                    redraw = true;
                cz[i] = look.Customize[i];
                changed = true;
            }
            // (colours, hair, features... also need the model updated: the redraw does it reliably)
            redraw |= changed;
        }
        if(gear && look.Gear.Length >= 10)
            for(int s = 0; s < 10; s++)
            {
                if(c->DrawData.Equipment((EquipSlot)s).Value == look.Gear[s])
                    continue;
                var id = new EquipmentModelId { Value = look.Gear[s] };
                if(redraw)
                    c->DrawData.Equipment((EquipSlot)s) = id;  // (the redraw puts it on)
                else
                {
                    c->DrawData.LoadEquipment((EquipSlot)s, &id, true);
                    // Didn't take (the game skips it on some characters, e.g. while it's still loading): written in
                    // and redrawn instead, so a change always shows.
                    if(c->DrawData.Equipment((EquipSlot)s).Value != look.Gear[s])
                    {
                        c->DrawData.Equipment((EquipSlot)s) = id;
                        redraw = true;
                    }
                }
            }
        if(weapons && look.Weapons.Length >= 2)
            for(int w = 0; w < 2; w++)
            {
                ref var slot = ref c->DrawData.Weapon((WeaponSlot)w);
                if(slot.ModelId.Value == look.Weapons[w])
                    continue;
                slot.ModelId = new WeaponModelId { Value = look.Weapons[w] };
                redraw = true; // (weapons go on reliably with a redraw)
            }
        if(redraw)
            Redraw(index);
        return redraw ? "Applied (redrawn)." : "Applied.";
    }

    /// <summary>
    /// A look written straight into a character that hasn't been drawn yet (a new actor): it then appears already
    /// wearing it, no redraw needed. A child body isn't allowed.
    /// </summary>
    public static bool WriteUndrawn(Character* c, AppearanceData look)
    {
        if(c == null || look.Customize.Length < 26 || look.Customize[2] == 4)
            return false;
        var cz = (byte*)&c->DrawData.CustomizeData;
        if(cz[2] == 4)
            return false;
        for(int i = 0; i < 26; i++)
            cz[i] = look.Customize[i];
        if(look.Gear.Length >= 10)
            for(int s = 0; s < 10; s++)
                c->DrawData.Equipment((EquipSlot)s) = new EquipmentModelId { Value = look.Gear[s] };
        if(look.Weapons.Length >= 2)
            for(int w = 0; w < 2; w++)
                c->DrawData.Weapon((WeaponSlot)w).ModelId = new WeaponModelId { Value = look.Weapons[w] };
        return true;
    }

    // ---- NPC looks (from the game's data: the event NPCs, with their own names)

    public sealed record NpcLook(uint Id, string Name, AppearanceData Look);

    private static volatile IReadOnlyList<NpcLook>? _npcs;
    private static int _npcsStarted;

    /// <summary>Every named event NPC with a human body (adults only), read once in the background; empty until ready.</summary>
    public static IReadOnlyList<NpcLook> Npcs
    {
        get
        {
            if(_npcs != null)
                return _npcs;
            if(System.Threading.Interlocked.Exchange(ref _npcsStarted, 1) == 0)
                System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        _npcs = ReadNpcs();
                    }
                    catch(Exception e)
                    {
                        Plugin.Log.Warning(e, "Limber: couldn't read the NPC list");
                        _npcs = Array.Empty<NpcLook>();
                    }
                });
            return Array.Empty<NpcLook>();
        }
    }

    public static bool NpcsReady => _npcs != null;

    // A sheet field as a byte / number, whatever it is (a plain number or a link to another sheet's row).
    private static ulong Num(object? v)
    {
        switch(v)
        {
            case null: return 0;
            case byte b: return b;
            case sbyte sb: return (byte)sb;
            case ushort us: return us;
            case short sh: return (ushort)sh;
            case uint ui: return ui;
            case int i: return (uint)i;
            case ulong ul: return ul;
            case long l: return (ulong)l;
            case bool bo: return bo ? 1u : 0u;
        }
        var rowId = v.GetType().GetProperty("RowId");
        return rowId != null && rowId.GetValue(v) is { } r ? Num(r) : 0;
    }

    private static List<NpcLook> ReadNpcs()
    {
        var list = new List<NpcLook>();
        var bases = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.ENpcBase>();
        var names = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.ENpcResident>();
        if(bases == null || names == null)
            return list;
        foreach(var row in bases)
        {
            if(!names.TryGetRow(row.RowId, out var resident))
                continue;
            string name = resident.Singular.ExtractText();
            if(name.Length == 0)
                continue;
            byte race = (byte)Num(row.Race), body = (byte)Num(row.BodyType);
            if(race == 0 || race > 8 || body == 4 || body == 0 || Num(row.ModelChara) != 0)
                continue; // (not a human, or a child body)
            var look = new AppearanceData { Name = name };
            var c = look.Customize;
            c[0] = race; c[1] = (byte)Num(row.Gender); c[2] = body; c[3] = (byte)Num(row.Height); c[4] = (byte)Num(row.Tribe);
            c[5] = (byte)Num(row.Face); c[6] = (byte)Num(row.HairStyle); c[7] = (byte)(Num(row.HairHighlight) != 0 ? 0x80 : 0);
            c[8] = (byte)Num(row.SkinColor); c[9] = (byte)Num(row.EyeHeterochromia); c[10] = (byte)Num(row.HairColor);
            c[11] = (byte)Num(row.HairHighlightColor); c[12] = (byte)Num(row.FacialFeature); c[13] = (byte)Num(row.FacialFeatureColor);
            c[14] = (byte)Num(row.Eyebrows); c[15] = (byte)Num(row.EyeColor); c[16] = (byte)Num(row.EyeShape); c[17] = (byte)Num(row.Nose);
            c[18] = (byte)Num(row.Jaw); c[19] = (byte)Num(row.Mouth); c[20] = (byte)Num(row.LipColor); c[21] = (byte)Num(row.BustOrTone1);
            c[22] = (byte)Num(row.ExtraFeature1); c[23] = (byte)Num(row.ExtraFeature2OrBust); c[24] = (byte)Num(row.FacePaint);
            c[25] = (byte)Num(row.FacePaintColor);
            if(c[9] == 0)
                c[9] = c[15]; // (no second eye colour: the same)

            // Gear: the shared NPC outfit when it has one, else its own.
            static ulong Gear(object model, object dye, object dye2) => (Num(model) & 0xFFFFFF) | (Num(dye) & 0xFF) << 24 | (Num(dye2) & 0xFF) << 32;
            static ulong Weapon(object model, object dye, object dye2) => (Num(model) & 0xFFFFFFFFFFFF) | (Num(dye) & 0xFF) << 48 | (Num(dye2) & 0xFF) << 56;
            if(Num(row.NpcEquip) != 0 && row.NpcEquip.ValueNullable is { } e)
            {
                look.Gear = [Gear(e.ModelHead, e.DyeHead, e.Dye2Head), Gear(e.ModelBody, e.DyeBody, e.Dye2Body), Gear(e.ModelHands, e.DyeHands, e.Dye2Hands),
                    Gear(e.ModelLegs, e.DyeLegs, e.Dye2Legs), Gear(e.ModelFeet, e.DyeFeet, e.Dye2Feet), Gear(e.ModelEars, e.DyeEars, e.Dye2Ears),
                    Gear(e.ModelNeck, e.DyeNeck, e.Dye2Neck), Gear(e.ModelWrists, e.DyeWrists, e.Dye2Wrists),
                    Gear(e.ModelRightRing, e.DyeRightRing, e.Dye2RightRing), Gear(e.ModelLeftRing, e.DyeLeftRing, e.Dye2LeftRing)];
                look.Weapons = [Weapon(e.ModelMainHand, e.DyeMainHand, e.Dye2MainHand), Weapon(e.ModelOffHand, e.DyeOffHand, e.Dye2OffHand)];
            }
            else
            {
                look.Gear = [Gear(row.ModelHead, row.DyeHead, row.Dye2Head), Gear(row.ModelBody, row.DyeBody, row.Dye2Body), Gear(row.ModelHands, row.DyeHands, row.Dye2Hands),
                    Gear(row.ModelLegs, row.DyeLegs, row.Dye2Legs), Gear(row.ModelFeet, row.DyeFeet, row.Dye2Feet), Gear(row.ModelEars, row.DyeEars, row.Dye2Ears),
                    Gear(row.ModelNeck, row.DyeNeck, row.Dye2Neck), Gear(row.ModelWrists, row.DyeWrists, row.Dye2Wrists),
                    Gear(row.ModelRightRing, row.DyeRightRing, row.Dye2RightRing), Gear(row.ModelLeftRing, row.DyeLeftRing, row.Dye2LeftRing)];
                look.Weapons = [Weapon(row.ModelMainHand, row.DyeMainHand, row.Dye2MainHand), Weapon(row.ModelOffHand, row.DyeOffHand, row.Dye2OffHand)];
            }
            list.Add(new NpcLook(row.RowId, name, look));
        }
        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    /// <summary>One gear slot: model id, variant and the two dyes.</summary>
    public static void SetGear(nint address, ushort index, int slot, ushort model, byte variant, byte stain0, byte stain1)
    {
        var c = Get(address, index);
        if(c == null || slot < 0 || slot >= 10)
            return;
        var id = new EquipmentModelId { Id = model, Variant = variant, Stain0 = stain0, Stain1 = stain1 };
        c->DrawData.LoadEquipment((EquipSlot)slot, &id, true);
    }

    public static void SetWeapon(nint address, ushort index, int slot, ushort set, ushort type, ushort variant, byte stain0, byte stain1)
    {
        var c = Get(address, index);
        if(c == null || slot < 0 || slot >= 2)
            return;
        ref var data = ref c->DrawData.Weapon((WeaponSlot)slot);
        data.ModelId = new WeaponModelId { Id = set, Type = type, Variant = variant, Stain0 = stain0, Stain1 = stain1 };
        Redraw(index);
    }

    public static void HideHeadgear(nint address, ushort index, bool hide)
    {
        var c = Get(address, index);
        if(c != null)
            c->DrawData.HideHeadgear(0, hide);
    }

    public static void HideWeapons(nint address, ushort index, bool hide)
    {
        var c = Get(address, index);
        if(c != null)
            c->DrawData.HideWeapons(hide);
    }

    public static void SetVisor(nint address, ushort index, bool on)
    {
        var c = Get(address, index);
        if(c != null)
            c->DrawData.SetVisor(on);
    }

    public static void SetGlasses(nint address, ushort index, ushort id)
    {
        var c = Get(address, index);
        if(c != null)
            c->DrawData.SetGlasses(0, id);
    }

    public static (bool Hat, bool Weapons, bool Visor) Toggles(nint address, ushort index)
    {
        var c = Get(address, index);
        if(c == null)
            return (false, false, false);
        return (c->DrawData.IsHatHidden, c->DrawData.IsWeaponHidden, c->DrawData.IsVisorToggled);
    }

    /// <summary>Draw the character again from its draw data (through Penumbra, so its mods stay on).</summary>
    public static void Redraw(ushort index)
    {
        try
        {
            Plugin.PluginInterface.GetIpcSubscriber<int, int, object?>("Penumbra.RedrawObject.V5").InvokeAction(index, 0);
        }
        catch
        {
            // no Penumbra: the change shows when the game next draws the character
        }
    }

    // ---- The game's lists: gear by slot, weapons, dyes

    public sealed record Item(uint Id, string Name, ulong Model, ulong SubModel, int Slot); // Slot: 0..9 gear, 10 main hand, 11 off hand

    /// <summary>Whether the item goes in that slot (rings are listed once, as right rings, and fit either hand).</summary>
    public static bool Fits(Item item, int slot) => item.Slot == slot || (slot == 9 && item.Slot == 8);

    /// <summary>Whole outfits for the gear buttons (weapons kept): each slot's model id (EquipmentModelId value).</summary>
    public enum Outfit { Smallclothes, NpcSmallclothes, Emperors }

    public static ulong[] OutfitGear(Outfit outfit)
    {
        var gear = new ulong[10];  // 0: nothing on the head and accessories, smallclothes elsewhere
        switch(outfit)
        {
            case Outfit.NpcSmallclothes:
                // the NPCs' smallclothes (e9903, variant 1) on body, hands, legs and feet
                foreach(int s in new[] { 1, 2, 3, 4 })
                    gear[s] = 9903 | 1UL << 16;
                break;
            case Outfit.Emperors:
                // the Emperor's New set, from the item list (the hat, robe, gloves, breeches, boots and accessories)
                foreach(var item in Items)
                    if(item.Slot is >= 0 and < 10 && item.Name.StartsWith("Emperor's New ", StringComparison.OrdinalIgnoreCase) && gear[item.Slot] == 0)
                        gear[item.Slot] = item.Model & 0xFFFFFF;
                // (the right ring slot takes the ring too)
                if(gear[8] == 0)
                    gear[8] = gear[9];
                if(gear[9] == 0)
                    gear[9] = gear[8];
                break;
        }
        return gear;
    }

    /// <summary>Puts a whole outfit on (weapons kept); the dyes come off with it.</summary>
    public static string PutOn(nint address, ushort index, Outfit outfit)
    {
        var look = Read(address, index);
        if(look == null)
            return "The character isn't there.";
        look.Gear = OutfitGear(outfit);
        // The Emperor's New Fists in the hands (the item from the game's list), with no off hand of their own.
        bool fists = false;
        foreach(var item in Items)
            if(item.Slot == 10 && item.Name.Equals("The Emperor's New Fists", StringComparison.OrdinalIgnoreCase))
            {
                look.Weapons[0] = WeaponValue(item.Model, 0);
                look.Weapons[1] = item.SubModel != 0 ? WeaponValue(item.SubModel, 0) : 0;
                fists = true;
                break;
            }
        return Apply(address, index, look, customize: false, gear: true, weapons: fists);
    }
    public sealed record Dye(byte Id, string Name, uint Color);

    private static List<Item>? _items;
    private static List<Dye>? _dyes;

    public static IReadOnlyList<Item> Items
    {
        get
        {
            if(_items != null)
                return _items;
            var list = new List<Item>();
            try
            {
                var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();
                if(sheet != null)
                    foreach(var row in sheet)
                    {
                        string name = row.Name.ExtractText();
                        if(name.Length == 0 || row.ModelMain == 0)
                            continue;
                        var cat = row.EquipSlotCategory.Value;
                        int slot = cat.Head != 0 ? 0 : cat.Body != 0 ? 1 : cat.Gloves != 0 ? 2 : cat.Legs != 0 ? 3 : cat.Feet != 0 ? 4
                            : cat.Ears != 0 ? 5 : cat.Neck != 0 ? 6 : cat.Wrists != 0 ? 7 : cat.FingerR != 0 ? 8 : cat.FingerL != 0 ? 9
                            : cat.MainHand != 0 ? 10 : cat.OffHand != 0 ? 11 : -1;
                        if(slot < 0)
                            continue;
                        list.Add(new Item(row.RowId, name, row.ModelMain, row.ModelSub, slot));
                    }
            }
            catch(Exception e)
            {
                Plugin.Log.Warning(e, "Limber: couldn't read the item list");
            }
            _items = list;
            return list;
        }
    }

    public static IReadOnlyList<Dye> Dyes
    {
        get
        {
            if(_dyes != null)
                return _dyes;
            var list = new List<Dye> { new(0, "No dye", 0) };
            try
            {
                var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Stain>();
                if(sheet != null)
                    foreach(var row in sheet)
                    {
                        string name = row.Name.ExtractText();
                        if(row.RowId == 0 || row.RowId > 255 || name.Length == 0)
                            continue;
                        list.Add(new Dye((byte)row.RowId, name, row.Color));
                    }
            }
            catch(Exception e)
            {
                Plugin.Log.Warning(e, "Limber: couldn't read the dye list");
            }
            _dyes = list;
            return list;
        }
    }

    /// <summary>An item's gear model as the slot stores it (model id, variant; the dyes kept from what's there).</summary>
    public static ulong GearValue(Item item, ulong current)
    {
        ushort id = (ushort)(item.Model & 0xFFFF);
        byte variant = (byte)((item.Model >> 16) & 0xFF);
        var now = new EquipmentModelId { Value = current };
        return new EquipmentModelId { Id = id, Variant = variant, Stain0 = now.Stain0, Stain1 = now.Stain1 }.Value;
    }

    /// <summary>A weapon's model as the slot stores it (set, type, variant; the dyes kept).</summary>
    public static ulong WeaponValue(ulong model, ulong current)
    {
        var now = new WeaponModelId { Value = current };
        return new WeaponModelId
        {
            Id = (ushort)(model & 0xFFFF), Type = (ushort)((model >> 16) & 0xFFFF), Variant = (ushort)((model >> 32) & 0xFFFF),
            Stain0 = now.Stain0, Stain1 = now.Stain1,
        }.Value;
    }

    // ---- The customize bytes, named (index, name, max), for the editor

    public static readonly (int Index, string Name, int Max)[] CustomizeFields =
    [
        (3, "Height", 100), (5, "Face", 255), (6, "Hairstyle", 255), (8, "Skin colour", 255), (10, "Hair colour", 255),
        (11, "Highlights colour", 255), (9, "Eye colour (right)", 255), (15, "Eye colour (left)", 255), (14, "Eyebrows", 255),
        (16, "Eye shape", 255), (17, "Nose", 255), (18, "Jaw", 255), (19, "Mouth", 255), (20, "Lip / fur colour", 255),
        (21, "Muscle tone / ear size", 100), (22, "Tail shape / ears", 255), (23, "Bust size", 100), (24, "Face paint", 255),
        (25, "Face paint colour", 255), (13, "Tattoo / limbal ring colour", 255), (12, "Facial features (bits)", 255), (7, "Highlights (128 = on)", 255),
    ];

    public static readonly (byte Race, string Name, (byte Tribe, string Name)[] Clans)[] Races =
    [
        (1, "Hyur", [(1, "Midlander"), (2, "Highlander")]),
        (2, "Elezen", [(3, "Wildwood"), (4, "Duskwight")]),
        (3, "Lalafell", [(5, "Plainsfolk"), (6, "Dunesfolk")]),
        (4, "Miqo'te", [(7, "Seeker of the Sun"), (8, "Keeper of the Moon")]),
        (5, "Roegadyn", [(9, "Sea Wolf"), (10, "Hellsguard")]),
        (6, "Au Ra", [(11, "Raen"), (12, "Xaela")]),
        (7, "Hrothgar", [(13, "Helion"), (14, "The Lost")]),
        (8, "Viera", [(15, "Rava"), (16, "Veena")]),
    ];
}
