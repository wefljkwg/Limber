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
    private sealed record Before(AppearanceData Look, float Scale, float Factor);

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
        if(now.Customize[0] == race && now.Customize[4] == tribe)
            return "It's that race and clan already.";

        float heightBefore = Height(c);
        // The first swap remembers the original; swapping again from a swapped body keeps that original.
        if(!Swapped.TryGetValue(address, out var before))
        {
            float scale = moves?.Get(address)?.Scale ?? 1f;
            before = new Before(now.Copy(), scale, 1f);
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
        if(keepHeight)
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

    /// <summary>Framework update.</summary>
    public static void Tick()
    {
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
    private sealed record BodyOnly(Guid Collection, string Tag, ushort Own, ushort Body);
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
        "resident/action", "resident/emote", "resident/face", "resident/mount", "resident/other",
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
        // The gear worn: the new race's version has priority. (Where this race has no model of its own the game asks for
        // the shared one instead, so that one is given the new race's too.)
        for(int i = 0; i < GearSlots.Length; i++)
        {
            ushort set = (ushort)(now.Gear[GearSlots[i]] & 0xFFFF);
            string to = $"chara/equipment/e{set:D4}/model/{t}e{set:D4}_{GearParts[i]}.mdl";
            Map($"chara/equipment/e{set:D4}/model/{o}e{set:D4}_{GearParts[i]}.mdl", to);
            string shared = sex == 1 ? "c0201" : "c0101";
            if(!Plugin.DataManager.FileExists($"chara/equipment/e{set:D4}/model/{o}e{set:D4}_{GearParts[i]}.mdl") && shared != t)
                Map($"chara/equipment/e{set:D4}/model/{shared}e{set:D4}_{GearParts[i]}.mdl", to);
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
            if(BodySwaps.TryGetValue(address, out var old))
                RemoveMod(old.Collection, old.Tag);
            int result = Plugin.PluginInterface.GetIpcSubscriber<string, Guid, Dictionary<string, string>, string, int, int>("Penumbra.AddTemporaryMod.V5")
                .InvokeFunc($"{BodyTag} {index}", collection, paths, "", BodyPriority);
            if(result != 0)
                return $"The mod manager didn't take the swap (it said {result}).";
        }
        catch(Exception e)
        {
            return "The mod manager isn't reachable: " + e.Message;
        }
        BodySwaps[address] = new BodyOnly(collection, $"{BodyTag} {index}", own, body);
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
        foreach(var swap in BodySwaps.Values)
            RemoveMod(swap.Collection, swap.Tag);
        BodySwaps.Clear();
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
