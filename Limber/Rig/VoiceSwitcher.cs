using System;
using System.Collections.Generic;
using System.Linq;
using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace Limber.Rig;

/// <summary>
/// Voice Switcher (Scene): any character speaks with another voice. A character's voice is one number the game keeps
/// with it (the voice its emotes and battle sounds play with); each actor gets its own swap, kept on (written back
/// every frame) until it's taken off. The player voices come from the character creator's own list per clan and sex.
/// Only on your screen.
/// </summary>
public static unsafe class VoiceSwitcher
{
    public readonly record struct Voice(ushort Id, string Group, int Number);

    // address -> (index, the voice it's swapped to, its own voice)
    private static readonly Dictionary<nint, (ushort Index, ushort Voice, ushort Own)> Swaps = new();

    private static NativeCharacter* Get(nint address, ushort index)
    {
        var obj = Limber.Core.ActorSkeleton.GetObject(index);
        return obj == null || (nint)obj != address ? null : (NativeCharacter*)obj;
    }

    /// <summary>The voice the character has right now (0 if it isn't there).</summary>
    public static ushort Current(nint address, ushort index)
    {
        var c = Get(address, index);
        return c == null ? (ushort)0 : c->Vfx.VoiceId;
    }

    /// <summary>The character's own voice (from before it was swapped), or its voice now.</summary>
    public static ushort Own(nint address, ushort index)
        => Swaps.TryGetValue(address, out var s) ? s.Own : Current(address, index);

    public static bool IsSwapped(nint address) => Swaps.ContainsKey(address);

    public static void Set(nint address, ushort index, ushort voice)
    {
        var c = Get(address, index);
        if(c == null)
            return;
        ushort own = Swaps.TryGetValue(address, out var s) ? s.Own : c->Vfx.VoiceId;
        Swaps[address] = (index, voice, own);
        c->Vfx.VoiceId = voice;
    }

    public static void Reset(nint address, ushort index)
    {
        if(!Swaps.Remove(address, out var s))
            return;
        var c = Get(address, index);
        if(c != null)
            c->Vfx.VoiceId = s.Own;
    }

    public static void ResetAll()
    {
        foreach(var (address, s) in Swaps.ToArray())
            Reset(address, s.Index);
    }

    /// <summary>Framework thread, once a frame: keeps each swap on (a redraw or the game may set the voice back).</summary>
    public static void Update()
    {
        if(Swaps.Count == 0)
            return;
        List<nint>? gone = null;
        foreach(var (address, s) in Swaps)
        {
            var c = Get(address, s.Index);
            if(c == null)
            {
                (gone ??= new()).Add(address);
                continue;
            }
            if(c->Vfx.VoiceId != s.Voice)
                c->Vfx.VoiceId = s.Voice;
        }
        if(gone != null)
            foreach(var a in gone)
                Swaps.Remove(a);
    }

    // ---- The player voices (the character creator's list for each clan and sex)

    private static List<Voice>? _voices;

    public static IReadOnlyList<Voice> PlayerVoices => _voices ??= Load();

    private static ulong Num(object? v)
    {
        switch(v)
        {
            case null: return 0;
            case byte b: return b;
            case sbyte sb: return (ulong)Math.Max((int)sb, 0);
            case ushort us: return us;
            case short sh: return (ulong)Math.Max((int)sh, 0);
            case uint ui: return ui;
            case int i: return (ulong)Math.Max(i, 0);
            case ulong ul: return ul;
        }
        var rowId = v.GetType().GetProperty("RowId");
        return rowId != null && rowId.GetValue(v) is { } r ? Num(r) : 0;
    }

    // (read by name, so it keeps working however the sheet's columns are laid out)
    private static List<Voice> Load()
    {
        var list = new List<Voice>();
        try
        {
            var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.CharaMakeType>();
            if(sheet == null)
                return list;
            var type = typeof(Lumina.Excel.Sheets.CharaMakeType);
            var race = type.GetProperty("Race");
            var tribe = type.GetProperty("Tribe");
            var gender = type.GetProperty("Gender");
            var voices = type.GetProperties().FirstOrDefault(p => p.Name.Contains("Voice", StringComparison.OrdinalIgnoreCase)
                                                                  && typeof(System.Collections.IEnumerable).IsAssignableFrom(p.PropertyType));
            if(race == null || tribe == null || gender == null || voices == null)
                return list;
            foreach(var row in sheet)
            {
                byte r = (byte)Num(race.GetValue(row)), t = (byte)Num(tribe.GetValue(row));
                int g = (int)Num(gender.GetValue(row));
                if(r == 0 || t == 0)
                    continue;
                var raceInfo = Array.Find(Appearance.Races, x => x.Race == r);
                string clan = raceInfo.Clans?.FirstOrDefault(c => c.Tribe == t).Name ?? $"clan {t}";
                string group = $"{raceInfo.Name ?? $"Race {r}"} {clan} ({(g == 1 ? "female" : "male")})";
                int n = 0;
                if(voices.GetValue(row) is System.Collections.IEnumerable items)
                    foreach(var item in items)
                    {
                        ushort id = (ushort)Num(item);
                        if(id == 0)
                            continue;
                        list.Add(new Voice(id, group, ++n));
                    }
            }
        }
        catch(Exception e)
        {
            Plugin.Log.Debug(e, "Limber: couldn't read the player voices");
        }
        return list;
    }
}
