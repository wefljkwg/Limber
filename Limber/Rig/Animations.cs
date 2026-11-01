using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.System.Scheduler.Base;
using Limber.Core;

namespace Limber.Rig;

/// <summary>
/// The game's own animations (action timelines) on a character in GPose: what's playing (for pose files), playing any
/// of them (the Animations page), how fast, and back to what it was. Optionally another race's version of an
/// animation, through a Penumbra temporary file swap (the game picks the file by the character's race).
/// Only on this PC.
/// </summary>
public static unsafe class Animations
{
    /// <summary>One animation slot's state: its timeline row, key (e.g. "emote/b_pose01_loop") and time (seconds).</summary>
    public readonly record struct Slot(ushort Id, string Key, float Time);

    /// <summary>What's playing on a character: base, upper body and face, and the base override (0: none).</summary>
    public readonly record struct State(Slot Base, Slot Upper, Slot Face, ushort Override);

    private static Character* Get(nint address, ushort index)
    {
        var obj = ActorSkeleton.GetObject(index);
        if(obj == null || (nint)obj != address || !obj->IsCharacter())
            return null;
        return (Character*)obj;
    }

    private static Slot Read(Character* c, uint slot)
    {
        ushort id = c->Timeline.TimelineSequencer.TimelineIds[(int)slot];
        var timeline = c->Timeline.TimelineSequencer.GetSchedulerTimeline(slot);
        string key = KeyOf(id);
        float time = 0f;
        if(timeline != null)
            time = ((TimelineController*)timeline)->CurrentTimestamp;
        return new Slot(id, key, time);
    }

    /// <summary>What's playing on the character now; null if it isn't there.</summary>
    public static State? Current(nint address, ushort index)
    {
        var c = Get(address, index);
        if(c == null)
            return null;
        return new State(Read(c, 0), Read(c, 1), Read(c, 2), c->Timeline.BaseOverride);
    }

    // ---- The list (every action timeline with a key)

    public sealed record Entry(ushort Id, string Key, string Name, string Group);

    private static List<Entry>? _all;

    public static IReadOnlyList<Entry> All
    {
        get
        {
            if(_all != null)
                return _all;
            var list = new List<Entry>();
            try
            {
                // Emote names for the timelines emotes use (a friendlier name than the key).
                var names = new Dictionary<uint, string>();
                var emotes = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Emote>();
                if(emotes != null)
                    foreach(var emote in emotes)
                    {
                        string name = emote.Name.ExtractText();
                        if(name.Length == 0)
                            continue;
                        foreach(var t in emote.ActionTimeline)
                            if(t.RowId != 0 && !names.ContainsKey(t.RowId))
                                names[t.RowId] = name;
                    }
                var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.ActionTimeline>();
                if(sheet != null)
                    foreach(var row in sheet)
                    {
                        string key = row.Key.ExtractText();
                        if(key.Length == 0 || row.RowId > ushort.MaxValue)
                            continue;
                        int slash = key.IndexOf('/');
                        string group = slash > 0 ? key[..slash] : "other";
                        list.Add(new Entry((ushort)row.RowId, key, names.GetValueOrDefault(row.RowId, ""), group));
                    }
            }
            catch(Exception e)
            {
                Plugin.Log.Warning(e, "Limber: couldn't read the animation list");
            }
            _all = list;
            return list;
        }
    }

    public static string KeyOf(ushort id)
    {
        if(id == 0)
            return "";
        foreach(var e in All)
            if(e.Id == id)
                return e.Key;
        return "";
    }

    // ---- Playing

    private static readonly Dictionary<nint, ushort> Before = new(); // per character: its base timeline before Limber played one

    /// <summary>Plays an animation on the character (and keeps it on as the base while it stands).</summary>
    public static string Play(nint address, ushort index, ushort id)
    {
        var c = Get(address, index);
        if(c == null)
            return "The character isn't there.";
        if(!Before.ContainsKey(address))
            Before[address] = c->Timeline.TimelineSequencer.TimelineIds[0];
        c->Timeline.BaseOverride = id;
        c->Timeline.TimelineSequencer.PlayTimeline(id);
        return $"Playing {KeyOf(id)}.";
    }

    /// <summary>Back to what the character was doing before Limber played anything.</summary>
    public static string Stop(nint address, ushort index)
    {
        var c = Get(address, index);
        if(c == null)
            return "The character isn't there.";
        c->Timeline.BaseOverride = 0;
        ushort back = Before.GetValueOrDefault(address, (ushort)3);
        Before.Remove(address);
        c->Timeline.TimelineSequencer.PlayTimeline(back == 0 ? (ushort)3 : back);
        SetSpeed(address, index, 1f);
        return "Back to its own animation.";
    }

    /// <summary>How fast the base and upper body animations play (1 = normal, 0 = paused).</summary>
    public static void SetSpeed(nint address, ushort index, float speed)
    {
        var c = Get(address, index);
        if(c == null)
            return;
        c->Timeline.TimelineSequencer.SetSlotSpeed(0, speed);
        c->Timeline.TimelineSequencer.SetSlotSpeed(1, speed);
    }

    // ---- Another race's version (experimental): Penumbra swaps this race's file for that race's

    /// <summary>The playable races' body ids (101 = Midlander man ... 1801 = Viera woman) with names.</summary>
    public static readonly (ushort Body, string Name)[] Races =
    [
        (101, "Midlander (m)"), (201, "Midlander (f)"), (301, "Highlander (m)"), (401, "Highlander (f)"),
        (501, "Elezen (m)"), (601, "Elezen (f)"), (701, "Miqo'te (m)"), (801, "Miqo'te (f)"),
        (901, "Roegadyn (m)"), (1001, "Roegadyn (f)"), (1101, "Lalafell (m)"), (1201, "Lalafell (f)"),
        (1301, "Au Ra (m)"), (1401, "Au Ra (f)"), (1501, "Hrothgar (m)"), (1601, "Hrothgar (f)"),
        (1701, "Viera (m)"), (1801, "Viera (f)"),
    ];

    private const string SwapTag = "Limber race animation";
    private const int SwapPriority = 99;
    private static Guid _swapCollection;

    /// <summary>The body id a character is drawn with (101 ... 1801); 0 if unknown.</summary>
    public static ushort BodyOf(nint address, ushort index)
    {
        var c = Get(address, index);
        if(c == null)
            return 0;
        var cz = (byte*)&c->DrawData.CustomizeData;
        byte race = cz[0], sex = cz[1], tribe = cz[4];
        int pair = race switch
        {
            1 => tribe == 2 ? 3 : 1,
            2 => 5, 3 => 11, 4 => 7, 5 => 9, 6 => 13, 7 => 15, 8 => 17,
            _ => 0,
        };
        return pair == 0 ? (ushort)0 : (ushort)((pair + (sex == 1 ? 1 : 0)) * 100 + 1);
    }

    private static string FileOf(ushort body, string key) => $"chara/human/c{body:D4}/animation/a0001/bt_common/{key}.pap";

    /// <summary>
    /// Plays another race's version of an animation: asks Penumbra to give this character's race that race's file
    /// (a temporary swap, in this character's collection only), then plays it. If the game already has this race's
    /// file loaded, the swap may only show from the next time it loads it.
    /// </summary>
    public static string PlayAsRace(nint address, ushort index, ushort id, ushort raceBody)
    {
        var key = KeyOf(id);
        ushort own = BodyOf(address, index);
        if(key.Length == 0 || own == 0)
            return "Can't tell which animation file that is.";
        if(raceBody == own)
            return Play(address, index, id);
        string from = FileOf(own, key), to = FileOf(raceBody, key);
        if(!Plugin.DataManager.FileExists(to))
            return "That race has no version of this animation.";
        try
        {
            var collection = Plugin.PluginInterface.GetIpcSubscriber<int, (bool, bool, (Guid, string))>("Penumbra.GetCollectionForObject.V5").InvokeFunc(index);
            if(!collection.Item1)
                return "Penumbra doesn't know this character right now.";
            ClearRace();
            _swapCollection = collection.Item3.Item1;
            int result = Plugin.PluginInterface.GetIpcSubscriber<string, Guid, Dictionary<string, string>, string, int, int>("Penumbra.AddTemporaryMod.V5")
                .InvokeFunc(SwapTag, _swapCollection, new Dictionary<string, string> { [from] = to }, "", SwapPriority);
            if(result != 0)
                return $"Penumbra didn't take the swap (it said {result}).";
        }
        catch(Exception e)
        {
            return "Penumbra isn't reachable: " + e.Message;
        }
        Play(address, index, id);
        return $"Playing {key} as {Races.FirstOrDefault(r => r.Body == raceBody).Name}.";
    }

    /// <summary>Takes the race swap away again.</summary>
    public static void ClearRace()
    {
        if(_swapCollection == Guid.Empty)
            return;
        try
        {
            Plugin.PluginInterface.GetIpcSubscriber<string, Guid, int, int>("Penumbra.RemoveTemporaryMod.V5")
                .InvokeFunc(SwapTag, _swapCollection, SwapPriority);
        }
        catch
        {
            // Penumbra gone: nothing to take back
        }
        _swapCollection = Guid.Empty;
    }
}
