using System;
using System.Collections.Generic;
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

    public static string Swap(nint address, ushort index, byte race, byte tribe, bool keepHeight, ActorTransforms? moves)
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
        if(now.Customize[0] == race && now.Customize[4] == tribe)
            return "It's that race and clan already.";

        float heightBefore = Height(c);
        // The first swap remembers the original; swapping again from a swapped body keeps that original.
        if(!Swapped.TryGetValue(address, out var before))
        {
            float scale = moves?.Get(address)?.Scale ?? 1f;
            before = new Before(index, now.Copy(), scale, 1f);
        }

        var look = now.Copy();
        look.Customize[0] = race;
        look.Customize[4] = tribe;
        // Values every race has (an out-of-range face or tail/ears type can leave a part undrawn).
        if(look.Customize[5] is 0 or > 4)
            look.Customize[5] = 1;
        if(look.Customize[22] is 0 or > 4)
            look.Customize[22] = 1;
        string result = Appearance.Apply(address, index, look, customize: true, gear: false, weapons: false);
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
        string result = Appearance.Apply(address, index, look, customize: true, gear: false, weapons: false);
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
    public static void Tick()
    {
        // Characters that left (GPose ended, actor removed): forgotten; draw objects looked up again (a redraw makes a
        // new one).
        if(++_pruneFrames >= 60)
        {
            _pruneFrames = 0;
            foreach(var (address, swap) in new List<KeyValuePair<nint, BodyOnly>>(BodySwaps))
                if(Get(address, swap.Index) == null)
                    BodySwaps.Remove(address);
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

    public static string SwapBodyOnly(nint address, ushort index, byte race, byte tribe)
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
        byte sex = now.Customize[1];
        ushort own = BodyCode(now.Customize[0], now.Customize[4], sex);
        ushort body = BodyCode(race, tribe, sex);
        if(own == 0 || body == 0)
            return "Can't tell which race that is.";
        if(own == body)
            return "That's its own body already.";
        if(!Hook((nint)((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)c)->DrawObject))
            return "Couldn't hook the game's file lookup: " + _hookProblem;
        BodySwaps[address] = new BodyOnly(index, own, body);
        RefreshDraws();
        Appearance.Redraw(index);
        return "Body swapped: head, hair and face kept.";
    }

    public static string SwapBodyBack(nint address, ushort index)
    {
        if(!BodySwaps.Remove(address))
            return "Not swapped.";
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

    private static Dalamud.Hooking.Hook<PathByIndex>? _sklb, _mdl, _skp, _phyb, _kdb, _bnmb, _imc, _skinMtrl, _decal;
    private static Dalamud.Hooking.Hook<PathByIndexName>? _pap, _mtrl;
    private static Dalamud.Hooking.Hook<PathByIndexUnk>? _matPap;
    private static Dalamud.Hooking.Hook<CreateDeformerDelegate>? _deformer;
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
            _mdl = gi.HookFromAddress<PathByIndex>(F(77), (h, b, n, i) => Body(h, BodySlot(i), () => _mdl!.Original(h, b, n, i)));
            _skp = gi.HookFromAddress<PathByIndex>(F(78), (h, b, n, i) => Body(h, BodyPartial(i), () => _skp!.Original(h, b, n, i)));
            _phyb = gi.HookFromAddress<PathByIndex>(F(79), (h, b, n, i) => Body(h, BodyPartial(i), () => _phyb!.Original(h, b, n, i)));
            _kdb = gi.HookFromAddress<PathByIndex>(F(80), (h, b, n, i) => Body(h, BodyPartial(i), () => _kdb!.Original(h, b, n, i)));
            _bnmb = gi.HookFromAddress<PathByIndex>(F(82), (h, b, n, i) => Body(h, BodyPartial(i), () => _bnmb!.Original(h, b, n, i)));
            _pap = gi.HookFromAddress<PathByIndexName>(F(84), PapDetour);
            _matPap = gi.HookFromAddress<PathByIndexUnk>(F(87), (h, b, n, i, u) => Body(h, BodySlot(i), () => _matPap!.Original(h, b, n, i, u)));
            _imc = gi.HookFromAddress<PathByIndex>(F(89), (h, b, n, i) => Body(h, BodySlot(i), () => _imc!.Original(h, b, n, i)));
            // (skin stays the character's own - its tone, scales and markings - so skin materials, the skin material and
            // body decals aren't asked as the other race; the gear's own materials are)
            _mtrl = gi.HookFromAddress<PathByIndexName>(F(90), (h, b, n, i, m) => Body(h, BodySlot(i) && !IsSkin(m), () => _mtrl!.Original(h, b, n, i, m)));
            _skinMtrl = gi.HookFromAddress<PathByIndex>(F(91), (h, b, n, i) => _skinMtrl!.Original(h, b, n, i));
            _decal = gi.HookFromAddress<PathByIndex>(F(92), (h, b, n, i) => _decal!.Original(h, b, n, i));
            _deformer = gi.HookFromAddress<CreateDeformerDelegate>(F(101), (h, s) => Body(h, BodySlot(s), () => _deformer!.Original(h, s)));
            _sklb.Enable(); _mdl.Enable(); _skp.Enable(); _phyb.Enable(); _kdb.Enable(); _bnmb.Enable(); _pap.Enable();
            _matPap.Enable(); _imc.Enable(); _mtrl.Enable(); _skinMtrl.Enable(); _decal.Enable(); _deformer.Enable();
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
                return path;
            return Body(human, true, () => _pap!.Original(human, buffer, size, index, name));
        }
        catch
        {
            return path;
        }
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
        foreach(var hook in new IDisposable?[] { _sklb, _mdl, _skp, _phyb, _kdb, _bnmb, _pap, _matPap, _imc, _mtrl, _skinMtrl, _decal, _deformer })
            hook?.Dispose();
        _sklb = _mdl = _skp = _phyb = _kdb = _bnmb = _imc = _skinMtrl = _decal = null;
        _pap = _mtrl = null;
        _matPap = null;
        _deformer = null;
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
