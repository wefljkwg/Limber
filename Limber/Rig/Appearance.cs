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

    public AppearanceData Copy() => new()
    {
        Customize = (byte[])Customize.Clone(), Gear = (ulong[])Gear.Clone(), Weapons = (ulong[])Weapons.Clone(), Name = Name,
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
                    c->DrawData.LoadEquipment((EquipSlot)s, &id, true);
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
