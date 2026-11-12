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
    private readonly System.Collections.Generic.Dictionary<nint, (System.Numerics.Vector3 Colour, float Brightness)> _tints = new();

    // (for the page's presets)
    public int Race { get; set; } = 0;     // index into Appearance.Races
    public int Clan { get; set; } = 0;     // 0 or 1
    public bool KeepHeight { get; set; } = true;
    public bool BodyOnly { get; set; }
    public int Gender { get; set; } = 0;  // 0 its own, 1 male, 2 female

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
        ImGui.TextWrapped("Whole character: it becomes the other race for real (its whole skeleton with physics, every model and "
            + "animation). Body only: its body (skeleton, physics, gear and body animations) becomes the other race's while the "
            + "head, face and hair stay its own. Either way it's drawn again (a moment).");
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
        string[] genders = ["Keep its own", "Male", "Female"];
        int gender = Math.Clamp(Gender, 0, 2);
        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        if(ImGui.BeginCombo("Gender###swap_gender", genders[gender]))
        {
            for(int i = 0; i < genders.Length; i++)
                if(ImGui.Selectable(genders[i], i == gender))
                    Gender = gender = i;
            ImGui.EndCombo();
        }
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("The new body's gender: its own, or male / female (with Body only, the head, face and hair stay its own).");
        byte? sex = gender == 0 ? null : (byte)(gender - 1);

        if(ImGui.RadioButton("Whole character###swap_whole", !BodyOnly))
            BodyOnly = false;
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("Everything becomes the new race: body, head, face, hair, ears, tail, and all its animations.");
        ImGui.SameLine();
        if(ImGui.RadioButton("Body only###swap_body_only", BodyOnly))
            BodyOnly = true;
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("Only the body becomes the new race's, exactly as a character of that race gets it: its skeleton and\n"
                + "physics, body, the gear worn on it and body animations (your body mods for that race included).\n"
                + "Its skin tone, the head, face, hair, ears, horns, tail and face animations stay its own. Only this character changes.");

        if(!BodyOnly)
        {
            bool keep = KeepHeight;
            if(ImGui.Checkbox("Keep the same height###swap_keep", ref keep))
                KeepHeight = keep;
            if(ImGui.IsItemHovered())
                ImGui.SetTooltip("On: the character stays the same size in metres (Limber scales the new body to match).\n"
                    + "Off: the new race's own size for the same Height slider (a Lalafell stays small, a Roegadyn tall).");
        }

        if(ImGui.Button("Swap body###swap_go", new System.Numerics.Vector2(160 * ImGuiHelpers.GlobalScale, 0)))
            _status = BodyOnly
                ? BodySwap.SwapBodyOnly(actor.Address, actor.ObjectIndex, races[race].Race, clans[clan].Tribe, sex)
                : BodySwap.Swap(actor.Address, actor.ObjectIndex, races[race].Race, clans[clan].Tribe, KeepHeight, _moves, sex);
        ImGui.SameLine();
        bool swapped = BodySwap.IsSwapped(actor.Address) || BodySwap.IsBodySwapped(actor.Address);
        using(ImRaii.Disabled(!swapped))
        {
            if(ImGui.Button("Swap back###swap_back"))
            {
                _tints.Remove(actor.Address);
                _status = BodySwap.IsBodySwapped(actor.Address)
                    ? BodySwap.SwapBodyBack(actor.Address, actor.ObjectIndex)
                    : BodySwap.SwapBack(actor.Address, actor.ObjectIndex, _moves);
            }
        }
        if(ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Its own race, clan, gender and size again.");

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
        if(BodySwap.IsBodySwapped(actor.Address))
            DrawSkinTint(actor.Address, actor.ObjectIndex);
        if(_status.Length > 0)
            ImGui.TextDisabled(_status);
        ImGui.Spacing();
        ImGui.TextDisabled("Hairstyles only some races have may not show on another race (the hair goes back when you swap back).\n"
            + "Body mods follow the mod manager's setup for the new race.\n"
            + "Body only: the neck join may show a gap between very different races; Body shape sliders still work on the new body.");
    }

    // The swapped body's skin tint: its own skin texture (another gender's or race's) matched to the head by eye.
    private void DrawSkinTint(nint address, ushort index)
    {
        ImGui.Spacing();
        if(!_tints.TryGetValue(address, out var t))
        {
            var now = BodySwap.SkinTintOf(index);
            float most = MathF.Max(now.X, MathF.Max(now.Y, now.Z));
            t = most > 1f ? (now / most, most) : (now, 1f);
        }
        var colour = t.Colour;
        float brightness = t.Brightness;
        bool apply = false;
        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        if(ImGui.ColorEdit3("Body skin tint###swap_tint", ref colour, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.PickerHueWheel))
            t.Colour = colour;
        apply |= ImGui.IsItemDeactivatedAfterEdit();
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("Multiplies the swapped body's skin colour, to match the head.\nWhite: no tint. The body's own textures stay.");
        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        if(ImGui.SliderFloat("Brightness###swap_tint_bright", ref brightness, 0.5f, 1.5f, "%.2f"))
            t.Brightness = brightness;
        apply |= ImGui.IsItemDeactivatedAfterEdit();
        ImGui.SameLine();
        if(ImGui.Button("Reset###swap_tint_reset"))
        {
            t = (System.Numerics.Vector3.One, 1f);
            apply = true;
        }
        _tints[address] = t;
        if(apply)
            BodySwap.SetSkinTint(address, index, t.Colour * t.Brightness);
        ImGui.TextDisabled("The tint applies when you let go (the character redraws).");
    }
}
