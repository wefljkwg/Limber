using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Limber.Rig;

namespace Limber.UI;

/// <summary>
/// How characters are named in Limber's lists (the actor list and the character drop-down): their own name, or for
/// the ones without one "NPC One", "NPC Two"... (by kind, in object order), and how far each is from you.
/// </summary>
public static class ActorNames
{
    /// <summary>Each character's shown name, by address.</summary>
    public static Dictionary<nint, string> Of(IEnumerable<ActorRef> characters)
    {
        var names = new Dictionary<nint, string>();
        var counts = new Dictionary<string, int>();
        foreach(var a in characters.OrderBy(a => a.ObjectIndex))
        {
            if(!string.IsNullOrEmpty(a.Name))
            {
                names[a.Address] = a.Name;
                continue;
            }
            string prefix = Plugin.ObjectTable[a.ObjectIndex]?.ObjectKind switch
            {
                ObjectKind.Pc => "Player",
                ObjectKind.BattleNpc or ObjectKind.EventNpc or ObjectKind.Retainer or ObjectKind.Companion => "NPC",
                _ => "Actor",
            };
            int n = counts.TryGetValue(prefix, out var c) ? c + 1 : 1;
            counts[prefix] = n;
            names[a.Address] = $"{prefix} {ActorSpawner.NumberWord(n)}";
        }
        return names;
    }

    /// <summary>"  (12 yalms)" from your character, or "" for you yourself or one that's gone.</summary>
    public static string Distance(ActorRef a)
    {
        var player = Plugin.ObjectTable[0];
        var obj = Plugin.ObjectTable[a.ObjectIndex];
        if(player == null || obj == null || obj.Address != a.Address || obj.Address == player.Address)
            return "";
        return $"  ({Vector3.Distance(player.Position, obj.Position):0} yalms)";
    }
}
