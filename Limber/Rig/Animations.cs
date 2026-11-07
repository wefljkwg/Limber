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

    /// <summary>One animation: its number, key, emote name (if an emote uses it), the key's first part, its icon, a
    /// plain-words description made from the key (for those with no emote name), and the kind it is.</summary>
    public sealed record Entry(ushort Id, string Key, string Name, string Group, uint Icon = 0, string Described = "", string Kind = "Other")
    {
        /// <summary>The name to show: the emote's, else the description, else the key.</summary>
        public string Shown => Name.Length > 0 ? Name : Described.Length > 0 ? Described : Key;
    }

    // ---- Plain words for the keys (so "emote/j_pose01_loop" reads "Ground sitting pose 1 (loop)")

    private static readonly (string Token, string Words)[] Words =
    [
        ("s_pose", "Chair sitting pose"), ("j_pose", "Ground sitting pose"), ("l_pose", "Lying pose"), ("b_pose", "Bed pose"), ("pose", "Standing pose"),
        ("jmn", "Ground sitting"), ("sit", "Sitting"), ("doze", "Dozing"), ("sleep", "Sleeping"), ("idle", "Idle"), ("wait", "Waiting"),
        ("walk", "Walking"), ("run", "Running"), ("dash", "Sprinting"), ("move", "Moving"), ("turn", "Turning"), ("jump", "Jumping"),
        ("fall", "Falling"), ("land", "Landing"), ("swim", "Swimming"), ("dive", "Diving"), ("fly", "Flying"), ("ride", "Riding"),
        ("mount", "Mount"), ("battle", "Battle"), ("btl", "Battle"), ("attack", "Attack"), ("damage", "Hit"), ("dmg", "Hit"),
        ("death", "Death"), ("dead", "Dead"), ("revive", "Revive"), ("cast", "Casting"), ("magic", "Magic"), ("limitbreak", "Limit break"),
        ("dance", "Dance"), ("emote", "Emote"), ("talk", "Talking"), ("event", "Event"), ("craft", "Crafting"), ("gather", "Gathering"),
        ("fishing", "Fishing"), ("cbnm", "Cutscene (normal)"), ("cbbm", "Cutscene"), ("facial", "Face"), ("face", "Face"), ("eye", "Eyes"),
        ("mouth", "Mouth"), ("blink", "Blinking"), ("lip", "Lips"), ("hand", "Hand"), ("loop", "(loop)"), ("start", "(start)"),
        ("end", "(end)"), ("st", "(start)"), ("ed", "(end)"), ("lp", "(loop)"), ("left", "left"), ("right", "right"), ("lft", "left"),
        ("rgt", "right"), ("front", "front"), ("back", "back"), ("upper", "upper body"), ("lower", "lower body"),
    ];

    /// <summary>A plain description of a key, from the words in it ("" if none are known).</summary>
    public static string Describe(string key)
    {
        var name = key[(key.LastIndexOf('/') + 1)..].ToLowerInvariant();
        var parts = new List<string>();
        int known = 0;
        int i = 0;
        while(i < name.Length)
        {
            if(name[i] == '_' || name[i] == '-')
            {
                i++;
                continue;
            }
            bool matched = false;
            foreach(var (token, words) in Words)
                if(string.CompareOrdinal(name, i, token, 0, token.Length) == 0
                   && (i + token.Length == name.Length || !char.IsLetter(name[i + token.Length]) || token.Contains('_')))
                {
                    parts.Add(words);
                    i += token.Length;
                    known++;
                    matched = true;
                    break;
                }
            if(matched)
                continue;
            // a number (pose 01 -> 1), or an unknown word kept as it is
            int j = i;
            while(j < name.Length && name[j] != '_' && name[j] != '-')
                j++;
            var word = name[i..j];
            parts.Add(word.All(char.IsDigit) ? (int.TryParse(word, out var n) ? n.ToString() : word) : word);
            i = j;
        }
        if(known == 0)
            return "";
        var text = string.Join(" ", parts);
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    /// <summary>The kind of animation, from its key.</summary>
    public static string KindOf(string key, bool emote)
    {
        var k = key.ToLowerInvariant();
        if(k.Contains("s_pose") || k.Contains("j_pose") || k.Contains("l_pose") || k.Contains("/sit") || k.Contains("jmn") || k.Contains("doze"))
            return "Sitting and lying";
        if(k.Contains("/pose") || k.Contains("cpose"))
            return "Standing poses";
        if(emote)
            return "Emotes";
        if(k.Contains("facial") || k.StartsWith("face", StringComparison.Ordinal) || k.Contains("/eye") || k.Contains("mouth") || k.Contains("blink"))
            return "Face";
        if(k.Contains("walk") || k.Contains("run") || k.Contains("move") || k.Contains("turn") || k.Contains("jump") || k.Contains("swim") || k.Contains("fly") || k.Contains("ride"))
            return "Moving";
        if(k.Contains("battle") || k.Contains("btl") || k.Contains("attack") || k.Contains("ws/") || k.Contains("magic") || k.Contains("cast") || k.Contains("death") || k.Contains("dmg"))
            return "Battle";
        if(k.Contains("dance"))
            return "Dances";
        if(k.StartsWith("event", StringComparison.Ordinal) || k.Contains("cbbm") || k.Contains("cbnm") || k.Contains("cut"))
            return "Events and cutscenes";
        if(k.Contains("craft") || k.Contains("gather") || k.Contains("fish"))
            return "Crafting and gathering";
        if(k.Contains("idle") || k.Contains("wait"))
            return "Idle";
        return "Other";
    }

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
                var icons = new Dictionary<uint, uint>();
                var emotes = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Emote>();
                if(emotes != null)
                    foreach(var emote in emotes)
                    {
                        string name = emote.Name.ExtractText();
                        if(name.Length == 0)
                            continue;
                        foreach(var t in emote.ActionTimeline)
                            if(t.RowId != 0 && !names.ContainsKey(t.RowId))
                            {
                                names[t.RowId] = name;
                                icons[t.RowId] = emote.Icon;
                            }
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
                        string emoteName = names.GetValueOrDefault(row.RowId, "");
                        list.Add(new Entry((ushort)row.RowId, key, emoteName, group, icons.GetValueOrDefault(row.RowId), Describe(key), KindOf(key, emoteName.Length > 0)));
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
        Played[address] = (index, id);
        return $"Playing {KeyOf(id)}.";
    }

    // ---- Playing again, pausing, resuming (Animations page)

    /// <summary>What Limber last played on each character (to play again from the start).</summary>
    private static readonly Dictionary<nint, (ushort Index, ushort Id)> Played = new();
    private static readonly Dictionary<nint, ushort> Paused = new();

    /// <summary>Characters paused or frozen here.</summary>
    public static IEnumerable<(nint Address, ushort Index)> PausedOn() => Paused.Select(p => (p.Key, p.Value)).ToList();
    private sealed class Pending
    {
        public nint Address;
        public ushort Index, Id;
        public int Frames;
        public bool Frozen;
        public float Speed;
    }
    private static readonly List<Pending> Restarts = new();

    /// <summary>The characters Limber has played something on (still there).</summary>
    public static IEnumerable<(nint Address, ushort Index)> PlayedOn()
    {
        foreach(var (address, (index, _)) in Played.ToArray())
            if(Get(address, index) != null)
                yield return (address, index);
            else
                Played.Remove(address);
    }

    public static ushort PlayedId(nint address) => Played.TryGetValue(address, out var p) ? p.Id : (ushort)0;

    public static bool IsPaused(nint address) => Paused.ContainsKey(address);

    /// <summary>
    /// From the beginning: back to the plain standing idle for a couple of frames (so it starts fresh, not blended
    /// from where it was), then the animation played from its first frame (and held there if <paramref name="frozen"/>).
    /// </summary>
    public static string Restart(nint address, ushort index, ushort id, bool frozen, float speed)
    {
        var c = Get(address, index);
        if(c == null)
            return "The character isn't there.";
        if(!Before.ContainsKey(address))
            Before[address] = c->Timeline.TimelineSequencer.TimelineIds[0];
        c->Timeline.BaseOverride = 0;
        c->Timeline.TimelineSequencer.SetSlotSpeed(0, 1f);
        c->Timeline.TimelineSequencer.SetSlotSpeed(1, 1f);
        c->Timeline.TimelineSequencer.PlayTimeline(3);
        Restarts.RemoveAll(r => r.Address == address);
        Restarts.Add(new Pending { Address = address, Index = index, Id = id, Frames = 2, Frozen = frozen, Speed = speed });
        Played[address] = (index, id);
        return frozen ? $"Starting {KeyOf(id)} (held on its first frame)." : $"Playing {KeyOf(id)} from the start.";
    }

    public static void Pause(nint address, ushort index)
    {
        SetSpeed(address, index, 0f);
        Paused[address] = index;
    }

    public static void Resume(nint address, ushort index, float speed)
    {
        SetSpeed(address, index, speed <= 0f ? 1f : speed);
        Paused.Remove(address);
    }

    /// <summary>Framework update: the restarts waiting their frames.</summary>
    public static void Tick()
    {
        for(int i = Restarts.Count - 1; i >= 0; i--)
        {
            var p = Restarts[i];
            if(--p.Frames > 0)
                continue;
            Restarts.RemoveAt(i);
            var c = Get(p.Address, p.Index);
            if(c == null)
                continue;
            c->Timeline.BaseOverride = p.Id;
            c->Timeline.TimelineSequencer.PlayTimeline(p.Id);
            Seek(p.Address, p.Index, 0f);
            if(p.Frozen)
                Pause(p.Address, p.Index);
            else
            {
                SetSpeed(p.Address, p.Index, p.Speed <= 0f ? 1f : p.Speed);
                Paused.Remove(p.Address);
            }
        }
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
        Played.Remove(address);
        Paused.Remove(address);
        Restarts.RemoveAll(r => r.Address == address);
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

    /// <summary>
    /// Puts the base animation at <paramref name="seconds"/> into it (for the Timeline's playhead). Some animations
    /// may not take it (the game moves them on its own way): then they just carry on from where they are.
    /// </summary>
    public static void Seek(nint address, ushort index, float seconds)
    {
        var c = Get(address, index);
        if(c == null || !float.IsFinite(seconds))
            return;
        var timeline = c->Timeline.TimelineSequencer.GetSchedulerTimeline(0);
        if(timeline == null)
            return;
        ((TimelineController*)timeline)->CurrentTimestamp = seconds;
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
                return "The mod manager doesn't know this character right now.";
            ClearRace();
            _swapCollection = collection.Item3.Item1;
            int result = Plugin.PluginInterface.GetIpcSubscriber<string, Guid, Dictionary<string, string>, string, int, int>("Penumbra.AddTemporaryMod.V5")
                .InvokeFunc(SwapTag, _swapCollection, new Dictionary<string, string> { [from] = to }, "", SwapPriority);
            if(result != 0)
                return $"The mod manager didn't take the swap (it said {result}).";
        }
        catch(Exception e)
        {
            return "The mod manager isn't reachable: " + e.Message;
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
