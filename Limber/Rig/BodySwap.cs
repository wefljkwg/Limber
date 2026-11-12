using System;
using System.Collections.Generic;
using System.Linq;
using Limber.Core;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace Limber.Rig;

/// <summary>
/// Full body swap: the character becomes another race (and clan) for real, so the game loads that race's whole
/// skeleton (body, face, hair, headgear and top skeletons, with their physics) and every model (body, face, hair, ears,
/// tail) - nothing is mixed. Its other looks stay (colours, hair, face, bust, muscle...), and by default its height in
/// metres stays too: the game's size for the new race is worked out and Limber's own character scale makes up the
/// difference. Swapping back puts the original race and size back. Needs a redraw (the mod manager's, a moment).
/// </summary>
public static unsafe class BodySwap
{
    private sealed record Before(ushort Index, AppearanceData Look, float Scale, float Factor);

    private static readonly Dictionary<nint, Before> Swapped = new();

    public static bool IsSwapped(nint address) => Swapped.ContainsKey(address);

    /// <summary>The race and clan the character had before Limber swapped it, or null.</summary>
    public static (byte Race, byte Tribe)? OriginalOf(nint address)
        => Swapped.TryGetValue(address, out var b) ? (b.Look.Customize[0], b.Look.Customize[4]) : null;

    /// <param name="sex">0 male, 1 female; null: its own.</param>
    public static string Swap(nint address, ushort index, byte race, byte tribe, bool keepHeight, ActorTransforms? moves, byte? sex = null)
    {
        var c = Get(address, index);
        if(c == null)
            return "The character isn't there.";
        var now = Appearance.Read(address, index);
        if(now == null)
            return "Couldn't read the character.";
        if(now.Customize[2] == 4)
            return "Changing a child's body isn't allowed.";
        if(IsBodySwapped(address))
            return "Swap the body back first.";
        byte newSex = sex ?? now.Customize[1];
        if(now.Customize[0] == race && now.Customize[4] == tribe && now.Customize[1] == newSex)
            return "It's that race, clan and gender already.";

        float heightBefore = Height(c);
        // The first swap remembers the original; swapping again from a swapped body keeps that original.
        if(!Swapped.TryGetValue(address, out var before))
        {
            float scale = moves?.Get(address)?.Scale ?? 1f;
            before = new Before(index, now.Copy(), scale, 1f);
        }

        var look = now.Copy();
        look.Customize[0] = race;
        look.Customize[1] = newSex;
        look.Customize[4] = tribe;
        // Values every race has (an out-of-range face or tail/ears type can leave a part undrawn).
        if(look.Customize[5] is 0 or > 4)
            look.Customize[5] = 1;
        if(look.Customize[22] is 0 or > 4)
            look.Customize[22] = 1;
        string result = OwnApply(address, index, look);
        if(!result.StartsWith("Applied", StringComparison.Ordinal))
            return result;

        // The new race's size for the same height slider (the game works it out from the customize just written).
        float step = 1f;
        if(keepHeight)
        {
            float heightAfter = Height(c);
            if(heightBefore > 0.01f && heightAfter > 0.01f)
                step = heightBefore / heightAfter;
        }
        // (from a body already swapped: the scale Limber added then is taken off first, and the whole difference to the
        // original race put on)
        float oldFactor = Swapped.TryGetValue(address, out var was) ? was.Factor : 1f;
        float factor = keepHeight ? oldFactor * step : 1f;
        if(moves != null)
            lock(moves.Sync)
            {
                var offset = moves.GetOrAdd(address, index);
                float baseScale = offset.Scale / (oldFactor <= 0f ? 1f : oldFactor);
                offset.Scale = Math.Clamp(baseScale * factor, ActorTransforms.MinScale, ActorTransforms.MaxScale);
            }
        Swapped[address] = before with { Factor = factor };
        if(!keepHeight)
            Checks.Remove(address);
        else
            Checks[address] = new Check { Index = index, HeightBefore = heightBefore, OldFactor = oldFactor, Moves = moves };
        return keepHeight ? $"Swapped (same height: scaled {factor:0.###}x)." : "Swapped.";
    }

    public static string SwapBack(nint address, ushort index, ActorTransforms? moves)
    {
        if(!Swapped.TryGetValue(address, out var before))
            return "Not swapped.";
        var now = Appearance.Read(address, index);
        if(now == null)
            return "The character isn't there.";
        var look = now.Copy();
        look.Customize[0] = before.Look.Customize[0];
        look.Customize[4] = before.Look.Customize[4];
        look.Customize[5] = before.Look.Customize[5];
        look.Customize[22] = before.Look.Customize[22];
        string result = OwnApply(address, index, look);
        if(moves != null)
            lock(moves.Sync)
                if(moves.Get(address) is { } offset)
                    offset.Scale = Math.Clamp(offset.Scale / (before.Factor <= 0f ? 1f : before.Factor), ActorTransforms.MinScale, ActorTransforms.MaxScale);
        Swapped.Remove(address);
        Checks.Remove(address);
        return result.StartsWith("Applied", StringComparison.Ordinal) ? "Back to its own race and size." : result;
    }

    // ---- The size checked again once the new body is drawn (in case the game only knew the new race's size then)

    private sealed class Check
    {
        public ushort Index;
        public float HeightBefore, OldFactor;
        public ActorTransforms? Moves;
        public int Frames;
    }
    private static readonly Dictionary<nint, Check> Checks = new();
    private static int _pruneFrames;

    /// <summary>Framework update.</summary>
    // ---- A new look put on a swapped character (a saved look, an import, a copy...): the swap stays
    //
    // Whole swap: the new look is the character's own now (what Swap back returns to), worn as the swapped race.
    // Body only: the swap is let go for one redraw, so the game makes the new look's colours for its own race (the
    // swapped body would mix up its palette), they're taken, and the body is swapped again on top.

    private static bool _swapping;

    private static string OwnApply(nint address, ushort index, AppearanceData look)
    {
        _swapping = true;
        try
        {
            return Appearance.Apply(address, index, look, customize: true, gear: false, weapons: false);
        }
        finally
        {
            _swapping = false;
        }
    }

    private sealed class Reswap
    {
        public required ushort Index;
        public required byte Race, Tribe, Sex;
        public required System.Numerics.Vector3 Tint;
        public nint Draw;
        public int Frames, Settled;
    }
    private static readonly Dictionary<nint, Reswap> Reswaps = new();

    /// <summary>Whether a body swap is waiting to go back on after a new look (Appearance & gear).</summary>
    public static bool Reswapping(nint address) => Reswaps.ContainsKey(address);

    /// <summary>
    /// Called by Appearance.Apply before a look's customize data is written (not for Limber's own swaps, or a kept
    /// look put back): the look to write instead.
    /// </summary>
    public static AppearanceData LookChanging(nint address, ushort index, AppearanceData look)
    {
        if(_swapping || look.Customize.Length < 26 || (!Swapped.ContainsKey(address) && !BodySwaps.ContainsKey(address) && !Reswaps.ContainsKey(address)))
            return look;
        var now = Appearance.Read(address, index);
        if(now == null || now.Customize.AsSpan().SequenceEqual(look.Customize))
            return look;
        if(Swapped.TryGetValue(address, out var before))
        {
            Swapped[address] = before with { Look = look.Copy() };
            var worn = look.Copy();
            worn.Customize[0] = now.Customize[0];
            worn.Customize[1] = now.Customize[1];
            worn.Customize[4] = now.Customize[4];
            if(worn.Customize[5] is 0 or > 4)
                worn.Customize[5] = 1;
            if(worn.Customize[22] is 0 or > 4)
                worn.Customize[22] = 1;
            Plugin.Log.Information($"Limber body swap: #{index} took a new look; still swapped (race {worn.Customize[0]}).");
            return worn;
        }
        if(BodySwaps.TryGetValue(address, out var swap))
        {
            var (race, tribe, sex) = RaceOf(swap.Body);
            Reswaps[address] = new Reswap
            {
                Index = index, Race = race, Tribe = tribe, Sex = sex, Tint = SkinTintOf(index),
                Draw = (nint)((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)Get(address, index))->DrawObject,
            };
            // (let go without a redraw of its own: the new look's redraw draws it unswapped)
            BodySwaps.Remove(address);
            ModelRaces.TryRemove(index, out _);
            OwnColours.Remove(address);
            GameColours.Remove(address);
            foreach(var key in Tinted.Keys.Where(k => k.Index == index).ToList())
                Tinted.TryRemove(key, out _);
            RefreshDraws();
            Plugin.Log.Information($"Limber body swap: #{index} took a new look; body c{swap.Body:D4} goes back on once it's drawn.");
        }
        else if(Reswaps.TryGetValue(address, out var waiting))
        {
            // (another look while waiting: wait for that one's redraw instead)
            waiting.Draw = (nint)((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)Get(address, index))->DrawObject;
            waiting.Frames = waiting.Settled = 0;
        }
        return look;
    }

    // The race, clan and sex of a body id (BodyCode the other way round).
    private static (byte Race, byte Tribe, byte Sex) RaceOf(ushort body)
    {
        int n = body / 100;
        byte sex = (byte)(n % 2 == 0 ? 1 : 0);
        return (n - sex) switch
        {
            1 => (1, 1, sex), 3 => (1, 2, sex), 5 => (2, 3, sex), 11 => (3, 5, sex), 7 => (4, 7, sex),
            9 => (5, 9, sex), 13 => (6, 11, sex), 15 => (7, 13, sex), 17 => (8, 15, sex),
            _ => ((byte)0, (byte)0, sex),
        };
    }

    // (framework) Bodies to swap again once the new look is drawn (its own colours made).
    private static void PumpReswaps()
    {
        foreach(var (address, r) in new List<KeyValuePair<nint, Reswap>>(Reswaps))
        {
            var c = Get(address, r.Index);
            if(c == null || r.Race == 0)
            {
                Reswaps.Remove(address);
                continue;
            }
            r.Frames++;
            nint draw = (nint)((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)c)->DrawObject;
            bool redrawn = draw != 0 && draw != r.Draw && ((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)c)->IsReadyToDraw();
            // (drawn anew and settled for a moment; or, after five seconds, anyway)
            if(redrawn)
                r.Settled++;
            // (a redraw can make the new model at the same address: then ready again after a moment is enough)
            if(r.Settled < 15 && !(r.Frames >= 90 && ((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)c)->IsReadyToDraw()) && r.Frames < 300)
                continue;
            Reswaps.Remove(address);
            var now = Appearance.Read(address, r.Index);
            if(now == null || BodyCode(now.Customize[0], now.Customize[4], now.Customize[1]) == BodyCode(r.Race, r.Tribe, r.Sex))
            {
                Plugin.Log.Information($"Limber body swap: #{r.Index}'s new look has that body already; not swapped again.");
                continue;
            }
            string result = SwapBodyOnly(address, r.Index, r.Race, r.Tribe, r.Sex);
            Plugin.Log.Information($"Limber body swap: #{r.Index} swapped again after a new look: {result}");
            if(r.Tint != System.Numerics.Vector3.One)
                Tints[r.Index] = r.Tint;
        }
    }

    public static void Tick()
    {
        if(Reswaps.Count > 0)
            PumpReswaps();
        if(BodySwaps.Count > 0)
        {
            FitHeads();
            KeepColours();
            MakeTints();
            // (each slot's model race for the other body, kept up to date as gear changes: twice a second)
            if(++_raceFrames >= 30)
            {
                _raceFrames = 0;
                foreach(var (address, swap) in BodySwaps)
                    WorkOutModelRaces(address, swap);
            }
        }
        // Characters that left (GPose ended, actor removed): forgotten; draw objects looked up again (a redraw makes a
        // new one).
        if(++_pruneFrames >= 60)
        {
            _pruneFrames = 0;
            foreach(var (address, swap) in new List<KeyValuePair<nint, BodyOnly>>(BodySwaps))
                if(Get(address, swap.Index) == null)
                {
                    BodySwaps.Remove(address);
                    ModelRaces.TryRemove(swap.Index, out _);
                }
            RefreshDraws();
            foreach(var (address, b) in new List<KeyValuePair<nint, Before>>(Swapped))
                if(Get(address, b.Index) == null)
                {
                    Swapped.Remove(address);
                    Checks.Remove(address);
                }
        }
        if(Checks.Count == 0)
            return;
        foreach(var (address, check) in new List<KeyValuePair<nint, Check>>(Checks))
        {
            if(++check.Frames < 20)
                continue;
            Checks.Remove(address);
            var c = Get(address, check.Index);
            if(c == null || check.Moves == null || !Swapped.TryGetValue(address, out var b))
                continue;
            float after = Height(c);
            if(check.HeightBefore <= 0.01f || after <= 0.01f)
                continue;
            float factor = check.OldFactor * (check.HeightBefore / after);
            if(MathF.Abs(factor - b.Factor) < 0.001f)
                continue;
            lock(check.Moves.Sync)
            {
                var offset = check.Moves.GetOrAdd(address, check.Index);
                float baseScale = offset.Scale / (b.Factor <= 0f ? 1f : b.Factor);
                offset.Scale = Math.Clamp(baseScale * factor, ActorTransforms.MinScale, ActorTransforms.MaxScale);
            }
            Swapped[address] = b with { Factor = factor };
        }
    }

    // ---- Body only: the head (face, hair, ears, horns) stays this race's; the body becomes the other race's
    //
    // The character keeps its own race, so its face, hair, ears, tail and face animations stay its own. For everything
    // that belongs to the body, the game is asked for the files as if the character were the other race: the body
    // skeleton and its physics, the body / hands / legs / feet models (and neck, wrist and ring accessories), their
    // materials and skin, and the body animations. So it gets exactly the body a character of that race gets - the
    // collection's body mods for that race included - and the models are fitted for that race too. Only this character
    // changes. One redraw.
    //
    // How: the game builds each of those file paths (and fits each model) from Human.RaceSexId (+0xAA0). Its path
    // functions (Human vtable 76-92) and the model fitting (101) are hooked, and for a swapped character's body parts
    // RaceSexId holds the other race's id for the length of that one call, then goes back.

    private sealed record BodyOnly(ushort Index, ushort Own, ushort Body);
    private static readonly Dictionary<nint, BodyOnly> BodySwaps = new();     // by character (GameObject address)
    private static readonly Dictionary<nint, BodyOnly> BodyDraws = new();     // by its draw object (kept up to date)
    [ThreadStatic] private static nint _creatingFor;                           // the character whose model is being made

    public static bool IsBodySwapped(nint address) => BodySwaps.ContainsKey(address);

    /// <summary>The body id (101 Midlander man ... 1801 Viera woman) for a race, clan and sex.</summary>
    public static ushort BodyCode(byte race, byte tribe, byte sex)
    {
        int pair = race switch
        {
            1 => tribe == 2 ? 3 : 1,
            2 => 5, 3 => 11, 4 => 7, 5 => 9, 6 => 13, 7 => 15, 8 => 17,
            _ => 0,
        };
        return pair == 0 ? (ushort)0 : (ushort)((pair + (sex == 1 ? 1 : 0)) * 100 + 1);
    }

    /// <param name="sex">The body's: 0 male, 1 female; null: its own.</param>
    public static string SwapBodyOnly(nint address, ushort index, byte race, byte tribe, byte? sex = null)
    {
        var c = Get(address, index);
        if(c == null)
            return "The character isn't there.";
        var now = Appearance.Read(address, index);
        if(now == null)
            return "Couldn't read the character.";
        if(now.Customize[2] == 4)
            return "Changing a child's body isn't allowed.";
        if(IsSwapped(address))
            return "Swap the whole body back first.";
        ushort own = BodyCode(now.Customize[0], now.Customize[4], now.Customize[1]);
        ushort body = BodyCode(race, tribe, sex ?? now.Customize[1]);
        if(own == 0 || body == 0)
            return "Can't tell which race that is.";
        if(own == body)
            return "That's its own body already.";
        if(!Hook((nint)((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)c)->DrawObject))
            return "Couldn't hook the game's file lookup: " + _hookProblem;
        TakeColours(address);
        BodySwaps[address] = new BodyOnly(index, own, body);
        WorkOutModelRaces(address, BodySwaps[address]);
        Logged.Clear();
        FitLogged.Clear();
        Plugin.Log.Information($"Limber body swap: #{index} c{own:D4} -> body c{body:D4}");
        RefreshDraws();
        Appearance.Redraw(index);
        return "Body swapped: head, hair and face kept.";
    }

    public static string SwapBodyBack(nint address, ushort index)
    {
        if(!BodySwaps.Remove(address))
            return "Not swapped.";
        ModelRaces.TryRemove(index, out _);
        Tints.TryRemove(index, out _);
        foreach(var key in Tinted.Keys.Where(k => k.Index == index).ToList())
            Tinted.TryRemove(key, out _);
        OwnColours.Remove(address);
        GameColours.Remove(address);
        RefreshDraws();
        Appearance.Redraw(index);
        return "Its own body again.";
    }

    /// <summary>Every swap undone (Limber unloading).</summary>
    public static void ClearBodySwaps()
    {
        var was = new List<KeyValuePair<nint, BodyOnly>>(BodySwaps);
        BodySwaps.Clear();
        BodyDraws.Clear();
        FittedTables.Clear();
        ModelRaces.Clear();
        Tints.Clear();
        Tinted.Clear();
        SkinSeen.Clear();
        foreach(var f in TintFiles)
            Limber.Koi.Props.KoiProps.Extra.Remove(f);
        TintFiles.Clear();
        OwnColours.Clear();
        GameColours.Clear();
        foreach(var (address, swap) in was)
            if(Get(address, swap.Index) != null)
                Appearance.Redraw(swap.Index);
        // Whole-body swaps back to their own race too (sizes are Limber's own, gone with it).
        foreach(var (address, b) in new List<KeyValuePair<nint, Before>>(Swapped))
            if(Get(address, b.Index) != null)
                SwapBack(address, b.Index, null);
        Swapped.Clear();
        Checks.Clear();
    }

    // Draw object -> swap (framework thread; the hooks read it on the same thread).
    private static void RefreshDraws()
    {
        BodyDraws.Clear();
        foreach(var (address, swap) in BodySwaps)
        {
            var c = Get(address, swap.Index);
            if(c == null)
                continue;
            nint draw = (nint)((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)c)->DrawObject;
            if(draw != 0)
                BodyDraws[draw] = swap;
        }
    }

    // The swap for a draw object, while it's being made too (the character isn't pointing at it yet then).
    private static BodyOnly? SwapFor(nint human)
    {
        if(BodyDraws.TryGetValue(human, out var swap))
            return swap;
        if(_creatingFor != 0 && BodySwaps.TryGetValue(_creatingFor, out swap))
            return swap;
        return null;
    }

    // ---- The hooks

    private const int RaceSexIdOffset = 0xAA0;
    // body, hands, legs, feet, neck, wrists, right ring, left ring (equipment slots; 0 head and 5 ears stay the head's)
    private static bool BodySlot(uint slot) => slot is >= 1 and <= 4 or >= 6 and <= 9;
    // partial skeletons: 0 the body, 4 the top's (gear skeleton); 1 face, 2 hair, 3 headgear stay the head's
    private static bool BodyPartial(uint partial) => partial is 0 or 4;

    private delegate nint PathByIndex(nint human, nint buffer, nuint size, uint index);
    private delegate nint PathByIndexName(nint human, nint buffer, nuint size, uint index, nint name);
    private delegate nint PathByIndexUnk(nint human, nint buffer, nuint size, uint index, uint unk);
    private delegate nint CreateDeformerDelegate(nint human, uint slot);
    private delegate ushort RaceDelegate(nint human);

    private static Dalamud.Hooking.Hook<PathByIndex>? _sklb, _mdl, _skp, _phyb, _kdb, _bnmb, _imc, _skinMtrl, _decal;
    private static Dalamud.Hooking.Hook<PathByIndexName>? _pap, _mtrl;
    private static Dalamud.Hooking.Hook<PathByIndexUnk>? _matPap;
    private static Dalamud.Hooking.Hook<CreateDeformerDelegate>? _deformer;
    // Human vtable 88: "which race is this" (just RaceSexId). The game's animation loaders (resident motion packs,
    // weapon and emote packs, timelines) ask it, not the path functions: answered with the new race for a swapped
    // character, so its body animations are the new race's.
    private static Dalamud.Hooking.Hook<RaceDelegate>? _race;
    private static bool _hooked;
    private static string _hookProblem = "";
    private static object? _creating, _created;

    private static bool Hook(nint drawObject)
    {
        if(_hooked)
            return true;
        if(drawObject == 0)
        {
            _hookProblem = "the character isn't drawn.";
            return false;
        }
        try
        {
            nint vt = *(nint*)drawObject;
            nint F(int slot) => *(nint*)(vt + slot * 8);
            var gi = Plugin.GameInterop;
            _sklb = gi.HookFromAddress<PathByIndex>(F(76), (h, b, n, i) => Body(h, BodyPartial(i), () => _sklb!.Original(h, b, n, i)));
            _mdl = gi.HookFromAddress<PathByIndex>(F(77), (h, b, n, i) => BodySlotCall(h, i, true, () => _mdl!.Original(h, b, n, i)));
            _skp = gi.HookFromAddress<PathByIndex>(F(78), (h, b, n, i) => Body(h, BodyPartial(i), () => _skp!.Original(h, b, n, i)));
            _phyb = gi.HookFromAddress<PathByIndex>(F(79), (h, b, n, i) => Body(h, BodyPartial(i), () => _phyb!.Original(h, b, n, i)));
            _kdb = gi.HookFromAddress<PathByIndex>(F(80), (h, b, n, i) => Body(h, BodyPartial(i), () => _kdb!.Original(h, b, n, i)));
            _bnmb = gi.HookFromAddress<PathByIndex>(F(82), (h, b, n, i) => Body(h, BodyPartial(i), () => _bnmb!.Original(h, b, n, i)));
            _pap = gi.HookFromAddress<PathByIndexName>(F(84), PapDetour);
            _matPap = gi.HookFromAddress<PathByIndexUnk>(F(87), (h, b, n, i, u) => BodySlotCall(h, i, true, () => _matPap!.Original(h, b, n, i, u)));
            _imc = gi.HookFromAddress<PathByIndex>(F(89), (h, b, n, i) => BodySlotCall(h, i, true, () => _imc!.Original(h, b, n, i)));
            // (skin stays the character's own - its tone, scales and markings - so skin materials, the skin material and
            // body decals aren't asked as the other race; the gear's own materials are)
            // (a body of the other gender takes its own skin material: only its own fits it - another race's or gender's
            // skin comes out glitched. The skin's colour is the character's own either way: see KeepColours.)
            _mtrl = gi.HookFromAddress<PathByIndexName>(F(90), (h, b, n, i, m) =>
                TintSkin(h, b, n, i, IsSkin(m), BodySlotCall(h, i, !IsSkin(m) || OtherSex(h), () => _mtrl!.Original(h, b, n, i, m))));
            _skinMtrl = gi.HookFromAddress<PathByIndex>(F(91), (h, b, n, i) => TintSkin(h, b, n, i, true, _skinMtrl!.Original(h, b, n, i)));
            _decal = gi.HookFromAddress<PathByIndex>(F(92), (h, b, n, i) => _decal!.Original(h, b, n, i));
            _deformer = gi.HookFromAddress<CreateDeformerDelegate>(F(101), (h, s) => BodySlotCall(h, s, true, () => _deformer!.Original(h, s)));
            _race = gi.HookFromAddress<RaceDelegate>(F(88), RaceDetour);
            _sklb.Enable(); _mdl.Enable(); _skp.Enable(); _phyb.Enable(); _kdb.Enable(); _bnmb.Enable(); _pap.Enable();
            _matPap.Enable(); _imc.Enable(); _mtrl.Enable(); _skinMtrl.Enable(); _decal.Enable(); _deformer.Enable(); _race.Enable();
            // Which character a new draw object is for, while it's made (the mod manager says so).
            try
            {
                var creating = Plugin.PluginInterface.GetIpcSubscriber<nint, Guid, nint, nint, nint, object?>("Penumbra.CreatingCharacterBase.V5");
                var created = Plugin.PluginInterface.GetIpcSubscriber<nint, Guid, nint, object?>("Penumbra.CreatedCharacterBase.V5");
                creating.Subscribe(OnCreating);
                created.Subscribe(OnCreated);
                _creating = creating;
                _created = created;
            }
            catch
            {
                // (no mod manager: a swapped character's body is still found once it's drawn, from the next redraw)
            }
            _hooked = true;
            return true;
        }
        catch(Exception e)
        {
            _hookProblem = e.Message;
            DisposeHooks();
            return false;
        }
    }

    private static void OnCreating(nint gameObject, Guid collection, nint modelId, nint customize, nint equip) => _creatingFor = gameObject;

    private static void OnCreated(nint gameObject, Guid collection, nint drawObject)
    {
        if(BodySwaps.TryGetValue(gameObject, out var swap) && drawObject != 0)
            BodyDraws[drawObject] = swap;
        _creatingFor = 0;
    }

    // ---- Each gear slot's model race
    //
    // The game doesn't build a slot's model path from the character's race alone: when the gear goes on it works out
    // which race's model to use (its own if the race has one for that item, else the women's or men's base model - most
    // gear and the smallclothes are the Midlander woman's c0201 or man's c0101, reshaped to the race), and keeps it in
    // the slot (+0x16). So only swapping the race kept a woman's body on a man's skeleton. For a swapped body it's worked
    // out for the other body the same way (Penumbra's ResolveEqdpRaceCode: the race's own model, its one fallback, else
    // Midlander man; from the game's equipment / accessory deformer parameter tables) and put in during the slot's
    // lookups.

    private static int _raceFrames;
    private const int SlotBase = 0xC50, SlotStride = 0x20, SlotModelRace = 0x16;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<ushort, ushort[]> ModelRaces = new(); // by object index
    private static readonly Dictionary<(ushort Race, bool Accessory), byte[]?> Eqdp = new();

    private static bool OtherSex(nint human)
        => SwapFor(human) is { } swap && (swap.Own / 100 % 2) != (swap.Body / 100 % 2);

    // ---- Body skin tint (Body swap page): the swapped body's skin matched to the head by eye
    //
    // The head and body share one skin colour, but the body's skin texture is its own (another gender's or race's,
    // lighter or darker): so its skin material gets a colour multiplier (skin.shpk's g_DiffuseColor, white by
    // default). Only this character's: its skin materials are copied under a private path (with the tint), handed to
    // the mod manager, and its lookups answered with that path; the textures stay the body's own.

    private const uint DiffuseColor = 0x2C2A34DD; // g_DiffuseColor (CRC from 0)
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<ushort, System.Numerics.Vector3> Tints = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(ushort Index, string Path), byte> SkinSeen = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(ushort Index, string Path), string> Tinted = new();
    private static readonly List<string> TintFiles = new();

    /// <summary>The swapped body's skin tint (white: none). Redrawn to take it.</summary>
    public static void SetSkinTint(nint address, ushort index, System.Numerics.Vector3 tint)
    {
        bool none = System.Numerics.Vector3.DistanceSquared(tint, System.Numerics.Vector3.One) < 1e-6f;
        if(none ? !Tints.TryRemove(index, out _) : Tints.TryGetValue(index, out var was) && was == tint)
            return;
        if(!none)
            Tints[index] = tint;
        foreach(var key in Tinted.Keys.Where(k => k.Index == index).ToList())
            Tinted.TryRemove(key, out _);
        Appearance.RedrawNative(index);
    }

    public static System.Numerics.Vector3 SkinTintOf(ushort index)
        => Tints.TryGetValue(index, out var t) ? t : System.Numerics.Vector3.One;

    // A skin material lookup (loading thread): this character's tinted copy's path when there is one; else noted, for
    // a copy to be made (framework thread) and the character redrawn.
    private static nint TintSkin(nint human, nint buffer, nuint size, uint slot, bool skin, nint path)
    {
        if(path == 0 || !skin || !BodySlot(slot) || Tints.IsEmpty || BodySwaps.Count == 0 || SwapFor(human) is not { } swap
           || !Tints.ContainsKey(swap.Index))
            return path;
        try
        {
            string got = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(path) ?? "";
            if(got.Length == 0 || !got.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase))
                return path;
            if(Tinted.TryGetValue((swap.Index, got), out var mine))
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(mine);
                if((nuint)bytes.Length + 1 > size)
                    return path;
                System.Runtime.InteropServices.Marshal.Copy(bytes, 0, buffer, bytes.Length);
                *((byte*)buffer + bytes.Length) = 0;
                return buffer;
            }
            SkinSeen[(swap.Index, got)] = 0;
        }
        catch
        {
        }
        return path;
    }

    // (framework thread) Tinted copies of the skin materials seen, handed to the mod manager; the characters redrawn.
    private static void MakeTints()
    {
        if(SkinSeen.IsEmpty)
            return;
        var redraw = new HashSet<ushort>();
        var dir = System.IO.Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "skintint");
        foreach(var key in SkinSeen.Keys.ToList())
        {
            SkinSeen.TryRemove(key, out _);
            if(!Tints.TryGetValue(key.Index, out var tint) || Tinted.ContainsKey(key))
                continue;
            try
            {
                // the file the character really draws (its mods' or the game's)
                string real = key.Path;
                try
                {
                    real = Plugin.PluginInterface.GetIpcSubscriber<string, int, string>("Penumbra.ResolveGameObjectPath.V5").InvokeFunc(key.Path, key.Index);
                }
                catch
                {
                }
                byte[]? data = System.IO.Path.IsPathRooted(real) && System.IO.File.Exists(real)
                    ? System.IO.File.ReadAllBytes(real)
                    : Plugin.DataManager.GetFile(real)?.Data ?? Plugin.DataManager.GetFile(key.Path)?.Data;
                if(data == null)
                    continue;
                var tinted = Limber.Koi.Props.MtrlBuilder.SetConstant(data, DiffuseColor, [tint.X, tint.Y, tint.Z]);
                string tag = $"i{key.Index}_{(uint)HashCode.Combine(tint.X, tint.Y, tint.Z, key.Path):x8}";
                string name = System.IO.Path.GetFileName(key.Path);
                string game = $"chara/limber/skintint/{tag}/{name}";
                System.IO.Directory.CreateDirectory(dir);
                string disk = System.IO.Path.Combine(dir, $"{tag}_{name}");
                System.IO.File.WriteAllBytes(disk, tinted);
                Limber.Koi.Props.KoiProps.Extra[game] = disk;
                TintFiles.Add(game);
                Tinted[key] = game;
                redraw.Add(key.Index);
            }
            catch(Exception e)
            {
                Plugin.Log.Warning(e, "Limber body swap: couldn't tint the skin");
            }
        }
        if(redraw.Count == 0)
            return;
        Limber.Koi.Props.KoiProps.Register();
        foreach(var index in redraw)
            Appearance.RedrawNative(index);
    }

    // ---- Colours: the character's own
    //
    // The game turns the skin, lip, hair and eye colour picks into colours through the race's palette (the same pick is
    // a different colour on each race), into the character's one CustomizeParameter block that the head and body both
    // read. For a swapped body it used the other race's palette (the body's race is what the character says it is, for
    // its animations), so the skin came out that race's tone at the same pick - a Roegadyn's pale skin under a Miqo'te
    // head. The block is kept as it was before the swap: taken then, and put back whenever the game has made it
    // again (a redraw); left alone when something else changed it (Skin effects' flush, say).

    private static readonly Dictionary<nint, byte[]> OwnColours = new();   // by the character, taken before the swap
    private static readonly Dictionary<nint, byte[]> GameColours = new();  // what the game made of them after it

    private static FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Human* HumanOf(nint address)
    {
        var go = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)address;
        if(go == null)
            return null;
        var draw = go->DrawObject;
        if(draw == null || draw->Object.GetObjectType() != FFXIVClientStructs.FFXIV.Client.Graphics.Scene.ObjectType.CharacterBase)
            return null;
        var cb = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase*)draw;
        return cb->GetModelType() == FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase.ModelType.Human ? (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Human*)cb : null;
    }

    private static byte* ColoursOf(nint address)
    {
        var human = HumanOf(address);
        if(human == null || human->CustomizeParameterCBuffer == null)
            return null;
        return (byte*)human->CustomizeParameterCBuffer->UnsafeSourcePointer;
    }

    private static void TakeColours(nint address)
    {
        var p = ColoursOf(address);
        if(p == null)
            return;
        OwnColours[address] = new ReadOnlySpan<byte>(p, sizeof(FFXIVClientStructs.FFXIV.Shader.CustomizeParameter)).ToArray();
        GameColours.Remove(address);
    }

    private static void KeepColours()
    {
        foreach(var (address, swap) in BodySwaps)
        {
            if(!OwnColours.TryGetValue(address, out var own) || Get(address, swap.Index) == null)
                continue;
            var p = ColoursOf(address);
            if(p == null)
                continue;
            var now = new Span<byte>(p, own.Length);
            if(now.SequenceEqual(own))
                continue;
            // (the game's own make of them: when it's first seen, or the same again after a redraw)
            if(GameColours.TryGetValue(address, out var made) && !now.SequenceEqual(made))
                continue; // (changed on purpose by something else: left)
            GameColours[address] = now.ToArray();
            own.CopyTo(now);
        }
    }

    private static T BodySlotCall<T>(nint human, uint slot, bool swapThis, Func<T> original)
    {
        if(!swapThis || !BodySlot(slot) || BodySwaps.Count == 0 || SwapFor(human) is not { } swap)
            return original();
        if(!ModelRaces.TryGetValue(swap.Index, out var races) || slot >= races.Length || races[slot] == 0)
            return Body(human, true, original);
        // (Human+0xC50 is a pointer to the slots' data - FFXIVClientStructs' ChangedEquipData - not the data itself:
        // written straight into the Human it overwrote other fields and the body vanished)
        byte* slots = *(byte**)(human + SlotBase);
        if(slots == null || !SafeMemory.Plausible((nint)slots))
            return Body(human, true, original);
        ushort* modelRace = (ushort*)(slots + (nint)slot * SlotStride + SlotModelRace);
        ushort was = *modelRace;
        // (only over a value that is a race id: cXXXX, 0101..1804 - anything else means this isn't the model race)
        if(was < 101 || was > 1804 || was % 100 is not (1 or 4))
        {
            NoteRace(slot, was);
            return Body(human, true, original);
        }
        *modelRace = races[slot];
        try
        {
            return Body(human, true, original);
        }
        finally
        {
            *modelRace = was;
        }
    }

    private static readonly HashSet<uint> RaceNoted = new();

    // (in the log once a slot: what was found where the model race should be, when it didn't look like one)
    private static void NoteRace(uint slot, ushort found)
    {
        if(RaceNoted.Add(slot))
            Plugin.Log.Warning($"Limber body swap: slot {slot}'s model race wasn't where expected (found {found}); its race kept.");
    }

    // The other body's model race for each slot, from the gear the character has on (framework thread).
    private static void WorkOutModelRaces(nint address, BodyOnly swap)
    {
        var look = Appearance.Read(address, swap.Index);
        if(look == null || look.Gear.Length < 10)
            return;
        var races = new ushort[10];
        for(int slot = 0; slot < 10; slot++)
            if(BodySlot((uint)slot))
                races[slot] = ModelRace(swap.Body, (ushort)(look.Gear[slot] & 0xFFFF), slot);
        if(!ModelRaces.TryGetValue(swap.Index, out var before) || !before.AsSpan().SequenceEqual(races))
            Plugin.Log.Information($"Limber body swap: #{swap.Index} body c{swap.Body:D4}, each slot's model: "
                + string.Join(" ", Enumerable.Range(0, 10).Where(i => races[i] != 0).Select(i => $"{i}:c{races[i]:D4}")));
        ModelRaces[swap.Index] = races;
    }

    // Penumbra's ResolveEqdpRaceCode: its own model, else its fallback's, else the Midlander man's.
    private static ushort ModelRace(ushort race, ushort set, int slot)
    {
        const ushort MidlanderMale = 101;
        if(race == MidlanderMale)
            return MidlanderMale;
        bool accessory = slot >= 5;
        if(race % 10 != 1 && accessory)
            return MidlanderMale;
        if(HasModel(race, accessory, set, slot))
            return race;
        ushort fallback = race switch
        {
            201 => MidlanderMale,                 // Midlander woman
            1501 => 901,                          // Hrothgar man -> Roegadyn man
            1201 => 1101,                         // Lalafell woman -> Lalafell man
            _ when race / 100 % 2 == 0 => 201,    // other women -> Midlander woman
            _ => MidlanderMale,
        };
        if(fallback == MidlanderMale)
            return MidlanderMale;
        return HasModel(fallback, accessory, set, slot) ? fallback : MidlanderMale;
    }

    // The deformer parameter table's "has its own model" bit for this race, item and slot.
    private static bool HasModel(ushort race, bool accessory, ushort set, int slot)
    {
        if(!Eqdp.TryGetValue((race, accessory), out var data))
        {
            string kind = accessory ? "accessorydeformerparameter" : "equipmentdeformerparameter";
            try
            {
                data = Plugin.DataManager.GetFile($"chara/xls/charadb/{kind}/c{race:D4}.eqdp")?.Data;
            }
            catch
            {
                data = null;
            }
            Eqdp[(race, accessory)] = data;
        }
        if(data == null || data.Length < 6)
            return false;
        int blockSize = BitConverter.ToUInt16(data, 2), blockCount = BitConverter.ToUInt16(data, 4);
        if(blockSize == 0)
            return false;
        int block = set / blockSize;
        if(block >= blockCount)
            return false;
        int control = BitConverter.ToUInt16(data, 6 + block * 2);
        if(control == 0xFFFF)
            return false;
        int at = 6 + blockCount * 2 + (control + set % blockSize) * 2;
        if(at + 2 > data.Length)
            return false;
        int entry = BitConverter.ToUInt16(data, at);
        int bit = 2 * (accessory ? slot - 5 : slot) + 1; // (the second bit of the slot's pair: it has a model)
        return (entry >> bit & 1) != 0;
    }

    // One of the game's lookups, as the other race when it's for a swapped character's body.
    private static T Body<T>(nint human, bool bodyPart, Func<T> original)
    {
        if(!bodyPart || (BodySwaps.Count == 0) || SwapFor(human) is not { } swap)
            return original();
        ushort* race = (ushort*)(human + RaceSexIdOffset);
        ushort was = *race;
        if(was != swap.Own)
            return original();
        *race = swap.Body;
        try
        {
            return original();
        }
        finally
        {
            *race = was;
        }
    }

    private static ushort RaceDetour(nint human)
    {
        ushort race = _race!.Original(human);
        if(BodySwaps.Count == 0 || SwapFor(human) is not { } swap || race != swap.Own)
            return race;
        return swap.Body;
    }

    // (what a swapped character's animations resolve to, in the log: the first few of each)
    private static readonly HashSet<string> Logged = new();

    // Animations: every body animation comes from the other race (whatever race's file the game first picked: some races
    // borrow another's animations, e.g. Au Ra use Midlander ones); the face's (a different folder) stay this race's.
    private static nint PapDetour(nint human, nint buffer, nuint size, uint index, nint name)
    {
        nint path = _pap!.Original(human, buffer, size, index, name);
        if(BodySwaps.Count == 0 || path == 0 || SwapFor(human) is not { } swap)
            return path;
        try
        {
            string got = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(path) ?? "";
            if(!IsBodyAnimation(got))
            {
                Note("kept", got);
                return path;
            }
            nint swapped = Body(human, true, () => _pap!.Original(human, buffer, size, index, name));
            Note("body", swapped == 0 ? "" : System.Runtime.InteropServices.Marshal.PtrToStringUTF8(swapped) ?? "");
            return swapped;
        }
        catch
        {
            return path;
        }
    }

    private static void Note(string kind, string path)
    {
        if(Logged.Count < 40 && Logged.Add(kind + path))
            Plugin.Log.Information($"Limber body swap: animation ({kind}) {path}");
    }

    // chara/human/cXXXX/animation/aNNNN/... is the body's; .../animation/fNNNN/... the face's.
    private static bool IsBodyAnimation(string path)
    {
        int at = path.IndexOf("/animation/", StringComparison.OrdinalIgnoreCase);
        return at >= 0 && path.StartsWith("chara/human/c", StringComparison.OrdinalIgnoreCase)
            && at + 11 < path.Length && (path[at + 11] == 'a' || path[at + 11] == 'A');
    }

    // A skin material ("/mt_c0201b0001_a.mtrl": the body's skin, turned into the character's own race's skin).
    private static bool IsSkin(nint name)
    {
        if(name == 0)
            return false;
        try
        {
            string file = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(name) ?? "";
            int c = file.IndexOf("mt_c", StringComparison.OrdinalIgnoreCase);
            return c >= 0 && c + 9 < file.Length && (file[c + 8] == 'b' || file[c + 8] == 'B');
        }
        catch
        {
            return false;
        }
    }

    private static void DisposeHooks()
    {
        foreach(var hook in new IDisposable?[] { _sklb, _mdl, _skp, _phyb, _kdb, _bnmb, _pap, _matPap, _imc, _mtrl, _skinMtrl, _decal, _deformer, _race })
            hook?.Dispose();
        _sklb = _mdl = _skp = _phyb = _kdb = _bnmb = _imc = _skinMtrl = _decal = null;
        _pap = _mtrl = null;
        _matPap = null;
        _deformer = null;
        _race = null;
        try
        {
            (_creating as Dalamud.Plugin.Ipc.ICallGateSubscriber<nint, Guid, nint, nint, nint, object?>)?.Unsubscribe(OnCreating);
            (_created as Dalamud.Plugin.Ipc.ICallGateSubscriber<nint, Guid, nint, object?>)?.Unsubscribe(OnCreated);
        }
        catch { }
        _creating = _created = null;
        _hooked = false;
    }

    public static void DisposeDeformer() => DisposeHooks();

    // ---- Body only: fitting the head's models to the new body
    //
    // The face, hair, ears and tail (and hats, earrings) are this race's models. Most of each moves with the head's own
    // skeletons, which sit on the new body's head bone, but its lower edge (the neck of the face model, long hair on
    // the back) is weighted to body bones (neck, upper back, collar): those are now the other race's, and the game
    // never refits head models to a body, so those vertices stayed where this race's neck is - stretched to the new
    // head. Each model has, per bone table, the inverse bind matrices its skinning uses (bone-table object +0xB8,
    // one 3x4 per bone, the model's own copy); for the body bones they get this race -> the other race's fitting from
    // the game's own deformer file (human.pbd) folded in, the way the game fits a body model made for another race.
    // Once per table; checked every frame (a redraw or a new hat makes new ones).

    private sealed class Fitted
    {
        public nint Matrices;
        public int[] Entries = [];
        public float[][] Written = [];
        public bool Seen;
    }

    private static readonly Dictionary<nint, Fitted> FittedTables = new();
    private static readonly Dictionary<(ushort, ushort), Dictionary<string, System.Numerics.Matrix4x4>?> Fits = new();
    private static byte[]? _pbd;
    private static bool _pbdRead;
    private static readonly Dictionary<nint, string[]> BoneNameCache = new();
    private static readonly HashSet<string> FitLogged = new();

    private static void FitHeads()
    {
        foreach(var f in FittedTables.Values)
            f.Seen = false;
        foreach(var (address, swap) in BodySwaps)
        {
            var c = Get(address, swap.Index);
            if(c == null)
                continue;
            nint draw = (nint)((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)c)->DrawObject;
            if(draw == 0)
                continue;
            try
            {
                FitHead(draw, swap);
            }
            catch(Exception e)
            {
                if(FitLogged.Add("error"))
                    Plugin.Log.Warning($"Limber body swap: couldn't fit the head ({e.Message})");
            }
        }
        foreach(var key in new List<nint>(FittedTables.Keys))
            if(!FittedTables[key].Seen)
                FittedTables.Remove(key);
    }

    // Head slots: 0 hat, 5 earrings, 10+ face, hair, ears / horns, tail.
    private static bool HeadSlot(int slot) => slot is 0 or 5 || slot >= 10;

    private static void FitHead(nint draw, BodyOnly swap)
    {
        var cb = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase*)draw;
        if(cb->Skeleton == null)
            return;
        int slots = cb->SlotCount;
        if(slots <= 0 || slots > 64 || cb->Models == null)
            return;
        var names = BodyBoneNames(cb);
        if(names == null)
            return;
        var fit = FitFor(swap.Own, swap.Body);
        if(fit == null || fit.Count == 0)
            return;
        for(int slot = 0; slot < slots; slot++)
        {
            if(!HeadSlot(slot))
                continue;
            nint model = SafeMemory.ReadPointer((nint)(cb->Models + slot));
            if(!SafeMemory.Plausible(model))
                continue;
            nint list = SafeMemory.ReadPointer(model + 0x58);
            int count = SafeMemory.Read<int>(model + 0x60, 0);
            if(!SafeMemory.Plausible(list) || count <= 0 || count > 16)
                continue;
            for(int i = 0; i < count; i++)
                FitTable(list + i * 0xE0, names, fit, slot);
        }
    }

    private static void FitTable(nint table, string[] names, Dictionary<string, System.Numerics.Matrix4x4> fit, int slot)
    {
        nint matrices = SafeMemory.ReadPointer(table + 0xB8);
        nint refsAt = SafeMemory.ReadPointer(table + 0xC0);
        int count = SafeMemory.Read<int>(table + 0xC8, 0);
        if(!SafeMemory.Plausible(matrices) || !SafeMemory.Plausible(refsAt) || count <= 0 || count > 1024)
            return;
        var floats = new float[count * 12];
        var refs = new ushort[count];
        fixed(float* to = floats)
            if(!SafeMemory.Copy(matrices, to, count * 48))
                return;
        fixed(ushort* to = refs)
            if(!SafeMemory.Copy(refsAt, to, count * 2))
                return;

        // Already fitted (and still as written)?
        if(FittedTables.TryGetValue(table, out var done) && done.Matrices == matrices)
        {
            bool same = true;
            for(int e = 0; e < done.Entries.Length && same; e++)
                for(int k = 0; k < 12; k++)
                    if(floats[done.Entries[e] * 12 + k] != done.Written[e][k])
                    {
                        same = false;
                        break;
                    }
            if(same)
            {
                done.Seen = true;
                return;
            }
        }

        // The body bones this table uses that the fitting moves.
        var entries = new List<int>();
        for(int e = 0; e < count; e++)
        {
            int partial = refs[e] >> 12, bone = refs[e] & 0xFFF;
            if(partial == 0 && bone < names.Length && fit.ContainsKey(names[bone]))
                entries.Add(e);
        }
        var record = new Fitted { Matrices = matrices, Seen = true };
        if(entries.Count == 0)
        {
            FittedTables[table] = record;
            return;
        }

        // How the 3x4s are laid out: rows of four (a column-vector matrix, the move in the last column) or four rows of
        // three (row-vector): whichever has unit-length rows in its rotation part (the head models aren't fitted by the
        // game, so these are plain rigid inverse binds).
        float errA = 0f, errB = 0f;
        foreach(var e in entries)
        {
            int o = e * 12;
            errA += RowError(floats, o, o + 4, o + 8);
            errB += RowError(floats, o, o + 3, o + 6);
        }
        bool columnLayout = errA <= errB;

        var written = new float[entries.Count][];
        for(int n = 0; n < entries.Count; n++)
        {
            int e = entries[n], o = e * 12;
            var ib = ToRowVector(floats, o, columnLayout);
            var t = fit[names[refs[e] & 0xFFF]];
            var result = t * ib; // a vertex: this race's shape -> the other race's -> the bone's space
            FromRowVector(result, floats, o, columnLayout);
            written[n] = new float[12];
            Array.Copy(floats, o, written[n], 0, 12);
        }
        fixed(float* from = floats)
            if(!WriteProcessMemory(-1, matrices, from, count * 48, out var wrote) || wrote != count * 48)
                return;
        record.Entries = entries.ToArray();
        record.Written = written;
        FittedTables[table] = record;
        if(FitLogged.Count < 30 && FitLogged.Add($"{slot}:{table:X}"))
            Plugin.Log.Information($"Limber body swap: fitted head slot {slot} table {table:X}: {entries.Count} body bones ({(columnLayout ? "rows of 4" : "rows of 3")}, layout error {errA:0.###} / {errB:0.###})");
    }

    private static float RowError(float[] m, int r0, int r1, int r2)
    {
        float Len(int at) => MathF.Sqrt(m[at] * m[at] + m[at + 1] * m[at + 1] + m[at + 2] * m[at + 2]);
        return MathF.Abs(Len(r0) - 1f) + MathF.Abs(Len(r1) - 1f) + MathF.Abs(Len(r2) - 1f);
    }

    private static System.Numerics.Matrix4x4 ToRowVector(float[] m, int o, bool columnLayout) => columnLayout
        ? new System.Numerics.Matrix4x4(
            m[o + 0], m[o + 4], m[o + 8], 0f,
            m[o + 1], m[o + 5], m[o + 9], 0f,
            m[o + 2], m[o + 6], m[o + 10], 0f,
            m[o + 3], m[o + 7], m[o + 11], 1f)
        : new System.Numerics.Matrix4x4(
            m[o + 0], m[o + 1], m[o + 2], 0f,
            m[o + 3], m[o + 4], m[o + 5], 0f,
            m[o + 6], m[o + 7], m[o + 8], 0f,
            m[o + 9], m[o + 10], m[o + 11], 1f);

    private static void FromRowVector(System.Numerics.Matrix4x4 r, float[] m, int o, bool columnLayout)
    {
        if(columnLayout)
        {
            m[o + 0] = r.M11; m[o + 1] = r.M21; m[o + 2] = r.M31; m[o + 3] = r.M41;
            m[o + 4] = r.M12; m[o + 5] = r.M22; m[o + 6] = r.M32; m[o + 7] = r.M42;
            m[o + 8] = r.M13; m[o + 9] = r.M23; m[o + 10] = r.M33; m[o + 11] = r.M43;
        }
        else
        {
            m[o + 0] = r.M11; m[o + 1] = r.M12; m[o + 2] = r.M13;
            m[o + 3] = r.M21; m[o + 4] = r.M22; m[o + 5] = r.M23;
            m[o + 6] = r.M31; m[o + 7] = r.M32; m[o + 8] = r.M33;
            m[o + 9] = r.M41; m[o + 10] = r.M42; m[o + 11] = r.M43;
        }
    }

    // The body skeleton's bone names (partial 0), by its Havok skeleton.
    private static string[]? BodyBoneNames(FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase* cb)
    {
        var skeleton = cb->Skeleton;
        if(skeleton == null || skeleton->PartialSkeletonCount < 1 || skeleton->PartialSkeletons == null)
            return null;
        var partial = &skeleton->PartialSkeletons[0];
        FFXIVClientStructs.Havok.Animation.Rig.hkaPose* pose = null;
        for(int i = 0; i < 4 && pose == null; i++)
            pose = partial->GetHavokPose(i);
        if(pose == null || pose->Skeleton == null)
            return null;
        nint key = (nint)pose->Skeleton;
        if(BoneNameCache.TryGetValue(key, out var cached))
            return cached;
        var info = SkeletonInfo.Read(pose->Skeleton);
        if(info == null)
            return null;
        if(BoneNameCache.Count > 64)
            BoneNameCache.Clear();
        BoneNameCache[key] = info.Names;
        return info.Names;
    }

    // This race's body -> the other race's, per bone (row vectors, model space). The deformer file fits each body from
    // the one above it in its tree (Midlander man at the top), so: this race back up to the top, then down to the other.
    private static Dictionary<string, System.Numerics.Matrix4x4>? FitFor(ushort own, ushort body)
    {
        if(Fits.TryGetValue((own, body), out var known))
            return known;
        if(!_pbdRead)
        {
            _pbdRead = true;
            _pbd = Limber.BodyRig.RigBuilder.ReadActive(Limber.BodyRig.Deformers.GamePath, out _);
        }
        Dictionary<string, System.Numerics.Matrix4x4>? result = null;
        if(_pbd != null)
        {
            const ushort Top = 101;
            var direct = Limber.BodyRig.Deformers.Fitting(_pbd, own, body);
            if(direct != null)
                result = direct;
            else
            {
                var up = own == Top ? new() : Limber.BodyRig.Deformers.Fitting(_pbd, Top, own);
                var down = body == Top ? new() : Limber.BodyRig.Deformers.Fitting(_pbd, Top, body);
                if(up != null && down != null)
                {
                    result = new Dictionary<string, System.Numerics.Matrix4x4>(StringComparer.Ordinal);
                    foreach(var name in new HashSet<string>(up.Keys.Concat(down.Keys)))
                    {
                        var undo = System.Numerics.Matrix4x4.Identity;
                        if(up.TryGetValue(name, out var u) && !System.Numerics.Matrix4x4.Invert(u, out undo))
                            continue;
                        var go = down.TryGetValue(name, out var d) ? d : System.Numerics.Matrix4x4.Identity;
                        result[name] = undo * go;
                    }
                }
            }
        }
        Fits[(own, body)] = result;
        if(result != null && result.TryGetValue("j_kubi", out var neck))
            Plugin.Log.Information($"Limber body swap: head fitting c{own:D4} -> c{body:D4}: {result.Count} bones, neck moves {neck.Translation}");
        else
            Plugin.Log.Warning($"Limber body swap: no head fitting c{own:D4} -> c{body:D4} ({(_pbd == null ? "deformer file not read" : "not in the deformer file")})");
        return result;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool WriteProcessMemory(nint process, nint address, void* buffer, nint size, out nint written);

    /// <summary>Forgets characters that left the scene.</summary>
    public static void Forget(nint address) => Swapped.Remove(address);

    // The character's height as the game works it out (race, clan, sex, body type and height slider).
    private static float Height(Character* c)
    {
        try
        {
            return c->ModelContainer.CalculateHeight();
        }
        catch
        {
            return 0f;
        }
    }

    private static Character* Get(nint address, ushort index)
    {
        var obj = ActorSkeleton.GetObject(index);
        if(obj == null || (nint)obj != address || !obj->IsCharacter())
            return null;
        return (Character*)obj;
    }
}
