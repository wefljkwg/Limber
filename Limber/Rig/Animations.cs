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
    public static List<(nint Address, ushort Index)> PlayedOn()
    {
        // (a list, not an iterator: iterators can't look at the game's objects)
        var list = new List<(nint, ushort)>();
        foreach(var (address, (index, _)) in Played.ToArray())
            if(Get(address, index) != null)
                list.Add((address, index));
            else
                Played.Remove(address);
        return list;
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
        Holds.RemoveAll(h => h.Address == address);
        Restarts.Add(new Pending { Address = address, Index = index, Id = id, Frames = 2, Frozen = frozen, Speed = speed });
        Played[address] = (index, id);
        return frozen ? $"Starting {KeyOf(id)} (held on its first frame)." : $"Playing {KeyOf(id)} from the start.";
    }

    public static void Pause(nint address, ushort index)
    {
        Holds.RemoveAll(h => h.Address == address);
        SetSpeed(address, index, 0f);
        Paused[address] = index;
    }

    public static void Resume(nint address, ushort index, float speed)
    {
        Holds.RemoveAll(h => h.Address == address);
        SetSpeed(address, index, speed <= 0f ? 1f : speed);
        Paused.Remove(address);
    }

    /// <summary>Framework update: the restarts waiting their frames.</summary>
    public static void Tick()
    {
        if(Paths.Count > 0)
            KeepPaths();
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
            Hold(p.Address, p.Index, p.Id, p.Frozen, p.Speed);
        }
        TickHolds();
    }

    // ---- Starting held on the first frame
    //
    // PlayTimeline only queues the animation: the game starts it a frame or more later, and starting it sets the
    // slot's speed back to normal. Pausing straight away paused the old animation (and the new one then played on),
    // so "Start frozen" did nothing. Now the hold waits until the new animation is the one running, puts it at its
    // start, sets the speed, and keeps that for a few more frames in case the game sets it again.

    private sealed class Held
    {
        public nint Address;
        public ushort Index, Id;
        public bool Frozen;
        public float Speed;
        public int Wait = 60;   // frames to wait for the animation to start
        public int Keep = 6;    // frames to keep it once it has
    }
    private static readonly List<Held> Holds = new();

    /// <summary>Plays <paramref name="id"/> and starts it held on its first frame (frozen) or at the given speed.</summary>
    public static string PlayFrom(nint address, ushort index, ushort id, bool frozen, float speed, ushort raceBody = 0)
    {
        string status = raceBody == 0 ? Play(address, index, id) : PlayAsRace(address, index, id, raceBody);
        Hold(address, index, id, frozen, speed);
        return frozen ? status.TrimEnd('.') + " (held on its first frame)." : status;
    }

    private static void Hold(nint address, ushort index, ushort id, bool frozen, float speed)
    {
        Holds.RemoveAll(h => h.Address == address);
        Holds.Add(new Held { Address = address, Index = index, Id = id, Frozen = frozen, Speed = speed <= 0f ? 1f : speed });
        if(frozen)
            Paused[address] = index;
        else
            Paused.Remove(address);
    }

    private static void TickHolds()
    {
        for(int i = Holds.Count - 1; i >= 0; i--)
        {
            var h = Holds[i];
            var c = Get(h.Address, h.Index);
            if(c == null)
            {
                Holds.RemoveAt(i);
                continue;
            }
            bool started = c->Timeline.TimelineSequencer.TimelineIds[0] == h.Id;
            if(!started && --h.Wait > 0)
                continue;
            if(!started && c->Timeline.BaseOverride != h.Id)
            {
                // (it never started: the play didn't take, so whatever is playing is left alone)
                Holds.RemoveAt(i);
                if(h.Frozen)
                    Paused.Remove(h.Address);
                continue;
            }
            if(h.Keep == 6)
                Seek(h.Address, h.Index, 0f);
            SetSpeed(h.Address, h.Index, h.Frozen ? 0f : h.Speed);
            if(--h.Keep <= 0)
                Holds.RemoveAt(i);
        }
    }

    /// <summary>Back to what the character was doing before Limber played anything.</summary>
    public static string Stop(nint address, ushort index)
    {
        var c = Get(address, index);
        if(c == null)
            return "The character isn't there.";
        ClearRace(address, index);
        c->Timeline.BaseOverride = 0;
        ushort back = Before.GetValueOrDefault(address, (ushort)3);
        Before.Remove(address);
        Played.Remove(address);
        Paused.Remove(address);
        Restarts.RemoveAll(r => r.Address == address);
        Holds.RemoveAll(h => h.Address == address);
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
    /// A race and the races whose animations it falls back on, nearest first (the game's own way: Highlanders use the
    /// Midlanders', Lalafell women the Lalafell men's, other women the Midlander woman's, everyone the Midlander man's).
    /// </summary>
    public static List<ushort> Chain(ushort body)
    {
        var chain = new List<ushort> { body };
        void Add(ushort b)
        {
            if(b != 0 && !chain.Contains(b))
                chain.Add(b);
        }
        Add(body switch
        {
            301 => 101, 401 => 201, 1201 => 1101, 1501 => 901, 1601 => 1001, 1701 => 1801,
            _ => 0,
        });
        if(body / 100 % 2 == 0)
            Add(201);
        Add(101);
        return chain;
    }

    /// <summary>The file a race really has for an animation: its own, or the nearest race's it falls back on ("" if none).</summary>
    public static string NearestFile(ushort body, string key, out ushort from)
    {
        foreach(var b in Chain(body))
            if(Plugin.DataManager.FileExists(FileOf(b, key)))
            {
                from = b;
                return FileOf(b, key);
            }
        from = 0;
        return "";
    }

    /// <summary>
    /// Plays another race's version of an animation, the way Anamnesis does it ("Data path"): the character's model
    /// keeps its own race, but the race number the game picks its files by (Human +0xAA0, and the head's +0xAA4) is
    /// set to the other race's, so every animation it loads from then on is that race's - the game's own, or a mod's
    /// made for that race (the mod manager gives it as it would to that race). Kept on until "Its own race" or Stop.
    /// If the game already has the animation loaded, play another one first, then this one again.
    /// </summary>
    public static string PlayAsRace(nint address, ushort index, ushort id, ushort raceBody)
    {
        var key = KeyOf(id);
        ushort own = BodyOf(address, index);
        if(key.Length == 0 || own == 0)
            return "Can't tell which animation file that is.";
        if(raceBody == own)
        {
            ClearRace(address, index);
            return Play(address, index, id);
        }
        string problem = SetDataPath(address, index, raceBody);
        if(problem.Length > 0)
            return problem;
        string name = Races.FirstOrDefault(r => r.Body == raceBody).Name;
        // The base animations (standing idle, walking, running...) are in the race's "resident" files, which the game
        // loads once when the character is drawn and keeps: the data path alone doesn't change them. Those files are
        // swapped for the other race's (this character's collection only) and the character is drawn again, then
        // the animation played once it's back.
        if(!Plugin.DataManager.FileExists(FileOf(own, key)) && NearestFile(own, key, out _).Length == 0)
        {
            string swapped = SwapResident(address, index, own, raceBody);
            if(swapped.Length > 0)
                return swapped;
            Appearance.RedrawNative(index);
            Restarts.RemoveAll(r => r.Address == address);
            Restarts.Add(new Pending { Address = address, Index = index, Id = id, Frames = 45, Frozen = false, Speed = 1f });
            return $"Playing {key} as {name} (a base animation: redrawn to load that race's).";
        }
        Play(address, index, id);
        return $"Playing {key} as {name}.";
    }

    // The base animations' files (bt_common/resident), every one the game may ask for this race (its own or the ones
    // it falls back on) given as the other race's (its own, or the nearest it falls back on).
    private static readonly string[] ResidentFiles = ["idle", "move_a", "move_b"];

    private static nint _swapAddress;

    private static string SwapResident(nint address, ushort index, ushort own, ushort raceBody)
    {
        var swaps = new Dictionary<string, string>();
        foreach(var file in ResidentFiles)
        {
            string key = "resident/" + file;
            string to = NearestFile(raceBody, key, out ushort used);
            if(to.Length == 0)
                continue;
            foreach(var b in Chain(own))
                if(b != used)
                    swaps[FileOf(b, key)] = to;
        }
        if(swaps.Count == 0)
            return "";
        try
        {
            var collection = Plugin.PluginInterface.GetIpcSubscriber<int, (bool, bool, (Guid, string))>("Penumbra.GetCollectionForObject.V5").InvokeFunc(index);
            if(!collection.Item1)
                return "The mod manager doesn't know this character right now.";
            ClearSwap();
            _swapCollection = collection.Item3.Item1;
            _swapAddress = address;
            int result = Plugin.PluginInterface.GetIpcSubscriber<string, Guid, Dictionary<string, string>, string, int, int>("Penumbra.AddTemporaryMod.V5")
                .InvokeFunc(SwapTag, _swapCollection, swaps, "", SwapPriority);
            return result == 0 ? "" : $"The mod manager didn't take the base animations' swap (it said {result}).";
        }
        catch(Exception e)
        {
            return "The mod manager isn't reachable: " + e.Message;
        }
    }

    // ---- The data path (Anamnesis's name): the race the game loads this character's files for

    private const int DataPathOffset = 0xAA0; // Human.RaceSexId
    private const int DataHeadOffset = 0xAA4;

    private sealed class DataPath
    {
        public required ushort Index;
        public nint Draw;
        public short OwnPath;
        public byte OwnHead;
        public required short Path;
        public byte Head;
    }
    private static readonly Dictionary<nint, DataPath> Paths = new();

    private static FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase* HumanOf(Character* c)
    {
        var draw = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase*)c->GameObject.DrawObject;
        if(draw == null || draw->GetModelType() != FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase.ModelType.Human)
            return null;
        return draw;
    }

    // The head number for a race's path (Anamnesis's table: which face set the head is read from).
    private static byte HeadFor(byte tribe, short path)
    {
        if(tribe % 2 != 0)
            return (byte)(path is 301 or 401 ? 0x65 : 0x01);
        if(tribe <= 10)
            return (byte)(path is 101 or 201 or 104 ? 0x01 : 0x65);
        return (byte)(path is 301 or 401 ? 0xC9 : 0x65);
    }

    private static string SetDataPath(nint address, ushort index, ushort raceBody)
    {
        var c = Get(address, index);
        if(c == null)
            return "The character isn't there.";
        var human = HumanOf(c);
        if(human == null)
            return "Only a normal (human) body can play another race's animations.";
        byte tribe = ((byte*)&c->DrawData.CustomizeData)[4];
        if(!Paths.TryGetValue(address, out var d) || d.Draw != (nint)human)
        {
            d = new DataPath
            {
                Index = index, Draw = (nint)human, Path = (short)raceBody,
                OwnPath = *(short*)((byte*)human + DataPathOffset), OwnHead = *((byte*)human + DataHeadOffset),
            };
            Paths[address] = d;
        }
        d.Path = (short)raceBody;
        d.Head = HeadFor(tribe, d.Path);
        *(short*)((byte*)human + DataPathOffset) = d.Path;
        *((byte*)human + DataHeadOffset) = d.Head;
        return "";
    }

    /// <summary>The character's own race's animations again.</summary>
    public static void ClearRace(nint address, ushort index)
    {
        // (its base animations swapped: back to its own, drawn again to load them)
        if(_swapAddress == address && _swapCollection != Guid.Empty)
        {
            ClearSwap();
            _swapAddress = 0;
            Appearance.RedrawNative(index);
        }
        if(!Paths.Remove(address, out var d))
            return;
        var c = Get(address, index);
        if(c == null)
            return;
        var human = HumanOf(c);
        if(human != null && (nint)human == d.Draw)
        {
            *(short*)((byte*)human + DataPathOffset) = d.OwnPath;
            *((byte*)human + DataHeadOffset) = d.OwnHead;
        }
    }

    /// <summary>Which race's animations the character plays now (0: its own).</summary>
    public static ushort RaceOf(nint address) => Paths.TryGetValue(address, out var d) ? (ushort)d.Path : (ushort)0;

    // (framework) Kept on: written again if the game set it back, and on the new model after a redraw.
    private static void KeepPaths()
    {
        foreach(var (address, d) in Paths.ToArray())
        {
            var c = Get(address, d.Index);
            if(c == null)
            {
                Paths.Remove(address);
                continue;
            }
            var human = HumanOf(c);
            if(human == null)
                continue;
            if((nint)human != d.Draw)
            {
                d.Draw = (nint)human;
                d.OwnPath = *(short*)((byte*)human + DataPathOffset);
                d.OwnHead = *((byte*)human + DataHeadOffset);
            }
            if(*(short*)((byte*)human + DataPathOffset) != d.Path)
                *(short*)((byte*)human + DataPathOffset) = d.Path;
            if(*((byte*)human + DataHeadOffset) != d.Head)
                *((byte*)human + DataHeadOffset) = d.Head;
        }
    }

    /// <summary>Takes the race swap away again.</summary>
    public static void ClearRace()
    {
        // (every character's own race's animations back: the data paths)
        foreach(var (address, d) in Paths.ToArray())
            ClearRace(address, d.Index);
        ClearSwap();
    }

    private static void ClearSwap()
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
