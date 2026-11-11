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
        // Characters that left (GPose ended, actor removed): forgotten, and a body-only swap taken out of the mod
        // manager (it would go on changing every character of that race in the collection).
        if(++_pruneFrames >= 60)
        {
            _pruneFrames = 0;
            foreach(var (address, swap) in new List<KeyValuePair<nint, BodyOnly>>(BodySwaps))
                if(Get(address, swap.Index) == null)
                {
                    RemoveMod(swap.Collection, swap.Tag);
                    BodySwaps.Remove(address);
                }
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
    // The character keeps its own race, so its face, hair, ears and face animations stay its own. Through the mod
    // manager, every file the game asks for the body is given the other race's instead (what the collection would give
    // that race, body mods included): the body skeleton and its physics, the nude body, the body / hands / legs / feet
    // of the gear worn, and the body animations. It's per collection, so every character of this race sharing the
    // collection gets the body too. One redraw.

    private const string BodyTag = "Limber body swap";
    private const int BodyPriority = 98;
    private sealed record BodyOnly(Guid Collection, string Tag, ushort Index, ushort Own, ushort Body, bool[] Redirected);
    private static readonly Dictionary<nint, BodyOnly> BodySwaps = new();

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

    private static readonly string[] GearParts = ["top", "glv", "dwn", "sho"]; // body, hands, legs, feet
    private static readonly int[] GearSlots = [1, 2, 3, 4];
    private static readonly string[] ResidentAnimations =
    [
        "resident/idle", "resident/move_a", "resident/move_b", "resident/move_c", "resident/move_d", "resident/jump",
        "resident/action", "resident/emote", "resident/mount", "resident/other",
    ];

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

        // (a body swapped before: taken out first, so the files below are what the collection itself gives)
        if(BodySwaps.Remove(address, out var previous))
            RemoveMod(previous.Collection, previous.Tag);

        // Every body file this race asks for -> the other race's (as the collection would give that race).
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string o = $"c{own:D4}", t = $"c{body:D4}";
        void Map(string from, string to)
        {
            if(paths.ContainsKey(from) || !Plugin.DataManager.FileExists(to))
                return;
            paths[from] = Resolve(to, index);
        }
        Map($"chara/human/{o}/skeleton/base/b0001/skl_{o}b0001.sklb", $"chara/human/{t}/skeleton/base/b0001/skl_{t}b0001.sklb");
        Map($"chara/human/{o}/skeleton/base/b0001/phy_{o}b0001.phyb", $"chara/human/{t}/skeleton/base/b0001/phy_{t}b0001.phyb");
        foreach(var part in GearParts)
        {
            Map($"chara/human/{o}/obj/body/b0001/model/{o}b0001_{part}.mdl", $"chara/human/{t}/obj/body/b0001/model/{t}b0001_{part}.mdl");
            Map($"chara/equipment/e0000/model/{o}e0000_{part}.mdl", $"chara/equipment/e0000/model/{t}e0000_{part}.mdl");
        }
        // The gear worn: the new race's version has priority. (Where this race has no model of its own the game uses
        // another race's, which isn't touched: that would change it for every race wearing it.)
        for(int i = 0; i < GearSlots.Length; i++)
        {
            ushort set = (ushort)(now.Gear[GearSlots[i]] & 0xFFFF);
            Map($"chara/equipment/e{set:D4}/model/{o}e{set:D4}_{GearParts[i]}.mdl", $"chara/equipment/e{set:D4}/model/{t}e{set:D4}_{GearParts[i]}.mdl");
        }
        // Body animations (the face's stay this race's).
        string Anim(string code, string key) => $"chara/human/{code}/animation/a0001/bt_common/{key}.pap";
        foreach(var key in ResidentAnimations)
            Map(Anim(o, key), Anim(t, key));
        foreach(var e in Animations.All)
            if(e.Key.Length > 0)
                Map(Anim(o, e.Key), Anim(t, e.Key));

        Guid collection;
        try
        {
            var got = Plugin.PluginInterface.GetIpcSubscriber<int, (bool, bool, (Guid, string))>("Penumbra.GetCollectionForObject.V5").InvokeFunc(index);
            if(!got.Item1)
                return "The mod manager doesn't know this character right now.";
            collection = got.Item3.Item1;
            int result = Plugin.PluginInterface.GetIpcSubscriber<string, Guid, Dictionary<string, string>, string, int, int>("Penumbra.AddTemporaryMod.V5")
                .InvokeFunc($"{BodyTag} {index}", collection, paths, "", BodyPriority);
            if(result != 0)
                return $"The mod manager didn't take the swap (it said {result}).";
        }
        catch(Exception e)
        {
            return "The mod manager isn't reachable: " + e.Message;
        }
        // Which of body / hands / legs / feet now hold the new race's own model (the rest are reshaped to fit it).
        var redirected = new bool[10];
        for(int i = 0; i < GearSlots.Length; i++)
        {
            ushort set = (ushort)(now.Gear[GearSlots[i]] & 0xFFFF);
            redirected[GearSlots[i]] = paths.ContainsKey($"chara/equipment/e{set:D4}/model/{o}e{set:D4}_{GearParts[i]}.mdl");
        }
        BodySwaps[address] = new BodyOnly(collection, $"{BodyTag} {index}", index, own, body, redirected);
        HookDeformer((nint)((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)c)->DrawObject);
        Appearance.Redraw(index);
        return $"Body swapped ({paths.Count} files): head, hair and face kept.";
    }

    public static string SwapBodyBack(nint address, ushort index)
    {
        if(!BodySwaps.Remove(address, out var swap))
            return "Not swapped.";
        RemoveMod(swap.Collection, swap.Tag);
        Appearance.Redraw(index);
        return "Its own body again.";
    }

    /// <summary>Every body-only swap taken away (Limber unloading).</summary>
    public static void ClearBodySwaps()
    {
        foreach(var (address, swap) in new List<KeyValuePair<nint, BodyOnly>>(BodySwaps))
        {
            RemoveMod(swap.Collection, swap.Tag);
            if(Get(address, swap.Index) != null)
                Appearance.Redraw(swap.Index);
        }
        BodySwaps.Clear();
        // Whole-body swaps back to their own race too (sizes are Limber's own, gone with it).
        foreach(var (address, b) in new List<KeyValuePair<nint, Before>>(Swapped))
            if(Get(address, b.Index) != null)
                SwapBack(address, b.Index, null);
        Swapped.Clear();
        Checks.Clear();
    }

    private static void RemoveMod(Guid collection, string tag)
    {
        try
        {
            Plugin.PluginInterface.GetIpcSubscriber<string, Guid, int, int>("Penumbra.RemoveTemporaryMod.V5").InvokeFunc(tag, collection, BodyPriority);
        }
        catch
        {
            // (the mod manager is gone: its temporary mods went with it)
        }
    }

    // What the character's collection gives for a game path (a mod's file, or the game's own path if none).
    private static string Resolve(string gamePath, ushort index)
    {
        try
        {
            var got = Plugin.PluginInterface.GetIpcSubscriber<string, int, string>("Penumbra.ResolveGameObjectPath.V5").InvokeFunc(gamePath, index);
            return string.IsNullOrEmpty(got) ? gamePath : got;
        }
        catch
        {
            return gamePath;
        }
    }

    // ---- Reshaping models for the swapped body
    //
    // When a model loads, the game reshapes it from the race it was made for to the character's race (Human.RaceSexId),
    // e.g. a body mod made only for Midlanders is reshaped to fit an Au Ra. With the body skeleton swapped to another
    // race, that's the wrong target: the mesh comes out shaped for the old race and is moved by the new race's bones
    // (shoulders folded into the chest, a long neck). So for the body, hands, legs, feet and the accessories worn on them,
    // the reshaping is pointed at the new race while that model is set up. Models already swapped for the new race's own
    // aren't reshaped at all. (Human vtable slot 101, CreateDeformer; the head, face, hair, ears and tail keep their own.)

    private delegate nint CreateDeformerDelegate(nint human, uint slot);
    private static Dalamud.Hooking.Hook<CreateDeformerDelegate>? _deformerHook;
    private const int RaceSexIdOffset = 0xAA0;   // Human.RaceSexId
    private const int SlotModelsOffset = 0xC50;  // per slot: 0x20 bytes, the race the slot's model was made for at +0x16
    private static readonly uint[] BodySlots = [1, 2, 3, 4, 6, 7, 8, 9]; // body, hands, legs, feet, neck, wrists, rings

    private static void HookDeformer(nint drawObject)
    {
        if(_deformerHook != null || drawObject == 0)
            return;
        try
        {
            nint vtable = *(nint*)drawObject;
            nint create = vtable == 0 ? 0 : *(nint*)(vtable + 101 * 8);
            if(create == 0)
                return;
            _deformerHook = Plugin.GameInterop.HookFromAddress<CreateDeformerDelegate>(create, CreateDeformerDetour);
            _deformerHook.Enable();
        }
        catch(Exception e)
        {
            Plugin.Log.Warning(e, "Limber: body swap couldn't set up the model reshaping (the body may look squashed)");
            _deformerHook = null;
        }
    }

    public static void DisposeDeformer()
    {
        _deformerHook?.Dispose();
        _deformerHook = null;
    }

    private static nint CreateDeformerDetour(nint human, uint slot)
    {
        var original = _deformerHook!.Original;
        try
        {
            if(BodySwaps.Count == 0 || Array.IndexOf(BodySlots, slot) < 0)
                return original(human, slot);
            ushort race = *(ushort*)(human + RaceSexIdOffset);
            BodyOnly? swap = null;
            foreach(var s in BodySwaps.Values)
                if(s.Own == race && WearsBody(human, s.Body))
                {
                    swap = s;
                    break;
                }
            if(swap == null)
                return original(human, slot);
            nint slots = *(nint*)(human + SlotModelsOffset);
            ushort madeFor = slots == 0 ? (ushort)0 : *(ushort*)(slots + (nint)slot * 0x20 + 0x16);
            // the new race's own model (put in its place by the swap): fits the new skeleton as it is
            if(madeFor == swap.Own && slot < swap.Redirected.Length && swap.Redirected[slot])
                return 0;
            // anything else: reshaped for the new race (the game compares against the character's race: the new one, for
            // the length of this call only)
            *(ushort*)(human + RaceSexIdOffset) = swap.Body;
            try
            {
                return original(human, slot);
            }
            finally
            {
                *(ushort*)(human + RaceSexIdOffset) = race;
            }
        }
        catch(Exception e)
        {
            Plugin.Log.Warning(e, "Limber: body swap model reshaping failed");
            return original(human, slot);
        }
    }

    // The character's body skeleton is the swapped one (its file is the new race's).
    private static bool WearsBody(nint human, ushort body)
    {
        var skeleton = ((FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase*)human)->Skeleton;
        if(skeleton == null || skeleton->PartialSkeletons == null || skeleton->PartialSkeletonCount < 1)
            return false;
        var handle = (byte*)skeleton->PartialSkeletons[0].SkeletonResourceHandle;
        if(handle == null)
            return false;
        string path = Limber.Core.SafeMemory.ReadStdString((nint)(handle + 0x48), 511);
        return path.Contains($"c{body:D4}", StringComparison.OrdinalIgnoreCase);
    }

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
