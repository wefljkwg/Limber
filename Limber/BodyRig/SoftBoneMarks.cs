using System;
using System.Collections.Generic;
using System.Linq;

namespace Limber.BodyRig;

/// <summary>
/// Bones the user has marked (Renderer page) as breast, butt or testicle bones, for skeletons whose bones Limber
/// doesn't know by name. The live mesh's soft body and one-click rigging use them on top of the bones they know.
/// Saved by bone name with the side swapped for "{side}", so marking one side marks both.
/// </summary>
public static class SoftBoneMarks
{
    public const string Breasts = "breasts", Butt = "butt", Testicles = "testicles", None = "none";

    /// <summary>The saved marks: bone pattern -> part key (or <see cref="None"/>).</summary>
    public static Func<Dictionary<string, string>> Source = () => new();

    public static string Pattern(string bone)
    {
        foreach (var (l, r) in new[] { ("_l", "_r"), ("_L", "_R") })
        {
            if (bone.EndsWith(l, StringComparison.Ordinal))
                return bone[..^2] + "_{side}";
            if (bone.EndsWith(r, StringComparison.Ordinal))
                return bone[..^2] + "_{side}";
        }
        return bone;
    }

    /// <summary>What the user marked this bone as, or null (not marked: Limber decides by its name).</summary>
    public static string? MarkOf(string bone)
    {
        try
        {
            var marks = Source();
            return marks.TryGetValue(bone, out var exact) ? exact : marks.TryGetValue(Pattern(bone), out var k) ? k : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The marked bones (patterns) for a rig part.</summary>
    public static string[] ParentsFor(string partKey)
    {
        try
        {
            return Source().Where(kv => kv.Value == partKey).Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        }
        catch
        {
            return [];
        }
    }

    public static void Set(string bone, string? part)
    {
        var marks = Source();
        marks.Remove(bone);
        var pattern = Pattern(bone);
        if (part == null)
            marks.Remove(pattern);
        else
            marks[pattern] = part;
    }
}
