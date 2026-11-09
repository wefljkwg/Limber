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
