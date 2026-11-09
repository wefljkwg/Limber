using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace Limber.Rig;

/// <summary>
/// Hiding characters (the actor list's eye): the model isn't drawn while hidden. The game can set the flag back (a
/// redraw, a new gear piece), so it's put on again every frame for as long as the character stays hidden.
/// </summary>
public static unsafe class ActorVisibility
{
    // Model bit of GameObject.RenderFlags (the same flag the game uses to keep a character undrawn).
    private const int ModelHidden = 1 << 1;

    private static readonly Dictionary<nint, ushort> Hidden = new();

    public static bool IsHidden(nint address) => Hidden.ContainsKey(address);

    public static int Count => Hidden.Count;

    public static void Hide(nint address, ushort index)
    {
        var go = Get(address, index);
        if(go == null)
            return;
        Hidden[address] = index;
        *(int*)&go->RenderFlags |= ModelHidden;
        // (GPose actors don't act on the flag alone: taken out of drawing too, the way a redraw does it)
        go->DisableDraw();
    }

    public static void Show(nint address, ushort index)
    {
        Hidden.Remove(address);
        var go = Get(address, index);
        if(go != null)
        {
            *(int*)&go->RenderFlags &= ~ModelHidden;
            go->EnableDraw();
        }
    }

    public static void Toggle(nint address, ushort index)
    {
        if(IsHidden(address))
            Show(address, index);
        else
            Hide(address, index);
    }

    public static void ShowAll()
    {
        foreach(var (address, index) in Hidden.ToList())
            Show(address, index);
        Hidden.Clear();
    }

    /// <summary>Framework update: keeps hidden characters hidden; forgets ones that left.</summary>
    public static void Update()
    {
        foreach(var (address, index) in Hidden.ToList())
        {
            var go = Get(address, index);
            if(go == null)
            {
                Hidden.Remove(address);
                continue;
            }
            *(int*)&go->RenderFlags |= ModelHidden;
        }
    }

    // The character at that object-table slot, only while it is still the same one.
    private static GameObject* Get(nint address, ushort index)
    {
        if(address == 0)
            return null;
        var go = Limber.Core.ActorSkeleton.GetObject(index);
        return go != null && (nint)go == address ? go : null;
    }
}
