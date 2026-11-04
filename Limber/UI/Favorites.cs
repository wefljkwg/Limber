using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace Limber.UI;

/// <summary>Your favourite housing props, world props and animations (kept with your settings).</summary>
public static class Favorites
{
    /// <summary>Set by the plugin.</summary>
    public static RigSettings? Settings;

    private static readonly Vector4 Gold = new(1f, 0.82f, 0.25f, 1f), Dim = new(0.5f, 0.5f, 0.55f, 1f);

    private static HashSet<string> List(string list)
    {
        var all = Settings?.Favorites;
        if(all == null)
            return new();
        if(!all.TryGetValue(list, out var set))
            all[list] = set = new();
        return set;
    }

    public static bool Has(string list, string key) => List(list).Contains(key);

    public static int Count(string list) => List(list).Count;

    public static void Toggle(string list, string key)
    {
        var set = List(list);
        if(!set.Remove(key))
            set.Add(key);
        Settings?.Save();
    }

    /// <summary>A small star button: gold for a favourite. Click to add or take it out.</summary>
    public static void Star(string list, string key, string id)
    {
        bool on = Has(list, key);
        using(ImRaii.PushFont(UiBuilder.IconFont))
        using(ImRaii.PushColor(ImGuiCol.Text, on ? Gold : Dim))
        using(ImRaii.PushColor(ImGuiCol.Button, Vector4.Zero))
        {
            if(ImGui.SmallButton($"{FontAwesomeIcon.Star.ToIconString()}###fav_{id}"))
                Toggle(list, key);
        }
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip(on ? "A favourite: click to take it out." : "Click to add it to your favourites.");
    }

    /// <summary>A gold star drawn at a spot (for grids where the whole cell is the button).</summary>
    public static void DrawStar(ImDrawListPtr draw, Vector2 at, float size)
        => draw.AddText(UiBuilder.IconFont, size, at, ImGui.GetColorU32(Gold), FontAwesomeIcon.Star.ToIconString());
}
