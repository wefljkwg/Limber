using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Limber.Rig;

namespace Limber.UI;

/// <summary>The Body swap page: the chosen character becomes another race, whole (skeleton, body, face, ears, tail).</summary>
public sealed class BodySwapTab
{
    private readonly Selection _selection;
    private readonly ActorTransforms? _moves;
    private string _status = "";

    // (for the page's presets)
    public int Race { get; set; } = 0;     // index into Appearance.Races
    public int Clan { get; set; } = 0;     // 0 or 1
    public bool KeepHeight { get; set; } = true;

    public BodySwapTab(Selection selection, ActorTransforms? moves)
    {
        _selection = selection;
        _moves = moves;
    }

    public void Draw()
    {
        var actor = _selection.Actor;
        if(!actor.IsSet)
        {
            ImGui.TextDisabled("Choose a character at the top of the window.");
            return;
        }
        ImGui.TextWrapped("The chosen character becomes another race for real: the game loads that race's whole skeleton "
            + "(body, face, hair, headgear) with its physics, and every model (body, face, ears, tail). Everything else about "
            + "its look stays. It's drawn again to do it (a moment).");
        ImGui.Spacing();

        var races = Appearance.Races;
        int race = Math.Clamp(Race, 0, races.Length - 1);
        ImGui.SetNextItemWidth(180 * ImGuiHelpers.GlobalScale);
        if(ImGui.BeginCombo("Race###swap_race", races[race].Name))
        {
            for(int i = 0; i < races.Length; i++)
                if(ImGui.Selectable($"{races[i].Name}###swap_race_{i}", i == race))
                    Race = race = i;
            ImGui.EndCombo();
        }
        var clans = races[race].Clans;
        int clan = Math.Clamp(Clan, 0, clans.Length - 1);
        ImGui.SetNextItemWidth(180 * ImGuiHelpers.GlobalScale);
        if(ImGui.BeginCombo("Clan###swap_clan", clans[clan].Name))
        {
            for(int i = 0; i < clans.Length; i++)
                if(ImGui.Selectable($"{clans[i].Name}###swap_clan_{i}", i == clan))
                    Clan = clan = i;
            ImGui.EndCombo();
        }
        bool keep = KeepHeight;
        if(ImGui.Checkbox("Keep the same height###swap_keep", ref keep))
            KeepHeight = keep;
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("On: the character stays the same size in metres (Limber scales the new body to match).\n"
                + "Off: the new race's own size for the same Height slider (a Lalafell stays small, a Roegadyn tall).");

        if(ImGui.Button("Swap body###swap_go", new System.Numerics.Vector2(160 * ImGuiHelpers.GlobalScale, 0)))
            _status = BodySwap.Swap(actor.Address, actor.ObjectIndex, races[race].Race, clans[clan].Tribe, KeepHeight, _moves);
        ImGui.SameLine();
        using(ImRaii.Disabled(!BodySwap.IsSwapped(actor.Address)))
        {
            if(ImGui.Button("Swap back###swap_back"))
                _status = BodySwap.SwapBack(actor.Address, actor.ObjectIndex, _moves);
        }
        if(ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Its own race, clan and size again.");

        if(BodySwap.OriginalOf(actor.Address) is { } original)
        {
            string from = "?";
            foreach(var r in races)
                if(r.Race == original.Race)
                    foreach(var c in r.Clans)
                        if(c.Tribe == original.Tribe)
                            from = $"{c.Name} {r.Name}";
            ImGui.TextDisabled($"Swapped (its own body: {from}).");
        }
        if(_status.Length > 0)
            ImGui.TextDisabled(_status);
        ImGui.Spacing();
        ImGui.TextDisabled("Hairstyles only some races have may not show on another race (the hair goes back when you swap back).\n"
            + "Body mods follow the mod manager's setup for the new race.");
    }
}
