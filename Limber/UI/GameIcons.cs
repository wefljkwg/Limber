using System.Collections.Generic;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;

namespace Limber.UI;

/// <summary>The game's icons, safely: an icon the game doesn't have gives nothing (once), instead of an error.</summary>
public static class GameIcons
{
    private static readonly HashSet<uint> Missing = new();

    /// <summary>The icon ready to draw, or null (not loaded yet, or the game has no such icon).</summary>
    public static IDalamudTextureWrap? Get(uint icon)
    {
        if(icon == 0 || Missing.Contains(icon))
            return null;
        try
        {
            if(!Plugin.TextureProvider.TryGetFromGameIcon(new GameIconLookup(icon), out var texture) || texture == null)
            {
                Missing.Add(icon);
                return null;
            }
            return texture.GetWrapOrDefault();
        }
        catch
        {
            Missing.Add(icon);
            return null;
        }
    }
}
