using System;
using Dalamud.Hooking;
using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;
using CharacterBase = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase;

namespace Limber.Rig;

/// <summary>
/// A look put on your own character outside GPose (an important NPC's, with their own face, hair and eyes): the game
/// builds a player's model with ordinary choices in place of NPC-only ones, so as your model is built Limber hands it
/// the look's own values. Only while your character still has the look Limber put on (changed any other way, it's
/// left alone); never anyone else. Only on your screen.
/// </summary>
public static unsafe class PlayerLook
{
    private delegate nint CreateDelegate(uint modelCharaId, nint customize, nint equipment, byte unk);

    private static Hook<CreateDelegate>? _create;
    private static byte[]? _wanted;     // the look's 26 customize values, while it's on your character
    private static bool _logged;

    /// <summary>Hooks the game's "make a character model" (once).</summary>
    public static void Start(Dalamud.Plugin.Services.IGameInteropProvider interop)
    {
        if(_create != null)
            return;
        try
        {
            nint address = (nint)CharacterBase.Addresses.Create.Value;
            if(address == 0)
                return;
            _create = interop.HookFromAddress<CreateDelegate>(address, Detour);
            _create.Enable();
        }
        catch(Exception e)
        {
            Plugin.Log.Warning(e, "Limber: couldn't hook model creation (NPC looks on your own character outside GPose may lose their face and hair)");
        }
    }

    /// <summary>A look was just put on your own character (its customize as it now is): kept for its next builds.</summary>
    public static void Wanted(byte[] customize)
    {
        _wanted = customize.Length >= 26 ? (byte[])customize.Clone() : null;
        _logged = false;
    }

    private static nint Detour(uint modelCharaId, nint customize, nint equipment, byte unk)
    {
        try
        {
            var wanted = _wanted;
            if(wanted != null && modelCharaId == 0 && customize != 0 && ForMe(customize, equipment, wanted))
            {
                var data = (byte*)customize;
                bool differs = false;
                for(int i = 0; i < 26; i++)
                    differs |= data[i] != wanted[i];
                if(differs)
                {
                    if(!_logged)
                    {
                        _logged = true;
                        Plugin.Log.Information($"Limber: building your model with the look's own values (the game had face {data[5]} -> {wanted[5]}, "
                            + $"hair {data[6]} -> {wanted[6]}, eyes {data[16]} -> {wanted[16]}, face paint {data[24]} -> {wanted[24]})");
                    }
                    for(int i = 0; i < 26; i++)
                        data[i] = wanted[i];
                }
                else if(!_logged)
                {
                    _logged = true;
                    Plugin.Log.Information("Limber: your model is being built with the look's own values already");
                }
            }
        }
        catch
        {
        }
        return _create!.Original(modelCharaId, customize, equipment, unk);
    }

    // The model being made is your own character's, and your character still has the look Limber put on.
    private static bool ForMe(nint customize, nint equipment, byte[] wanted)
    {
        var me = (NativeCharacter*)Limber.Core.ActorSkeleton.GetObject(0);
        if(me == null)
            return false;
        var own = (byte*)&me->DrawData.CustomizeData;
        // (still the look: changed some other way since, e.g. at the aesthetician, it's forgotten)
        for(int i = 0; i < 26; i++)
            if(own[i] != wanted[i])
            {
                _wanted = null;
                return false;
            }
        // Your character's model: what's passed comes from your character (its own data, or a copy of it next to it).
        nint start = (nint)me, end = start + sizeof(NativeCharacter);
        if((customize >= start && customize < end) || (equipment >= start && equipment < end))
            return true;
        // (a copy elsewhere: the same race, clan and body as the look, while it's being put on)
        var data = (byte*)customize;
        return data[0] == wanted[0] && data[1] == wanted[1] && data[2] == wanted[2] && data[4] == wanted[4]
            && SameGear(equipment, me);
    }

    // The equipment passed is your character's (all ten slots the same).
    private static bool SameGear(nint equipment, NativeCharacter* me)
    {
        if(equipment == 0)
            return false;
        var passed = (ulong*)equipment;
        for(int s = 0; s < 10; s++)
            if(passed[s] != me->DrawData.Equipment((FFXIVClientStructs.FFXIV.Client.Game.Character.DrawDataContainer.EquipmentSlot)s).Value)
                return false;
        return true;
    }

    public static void Dispose()
    {
        _create?.Disable();
        _create?.Dispose();
        _create = null;
        _wanted = null;
    }
}
