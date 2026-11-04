using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Limber.Rig;

namespace Limber.UI;

/// <summary>The Extras page (right after Global presets): fun one-press things, like Ragdoll mode.</summary>
public sealed class ExtrasTab
{
    private readonly Selection _selection;
    private readonly RagdollService _ragdoll;

    public ExtrasTab(Selection selection, RagdollService ragdoll)
    {
        _selection = selection;
        _ragdoll = ragdoll;
    }

    public void Draw()
    {
        var actor = _selection.Actor;
        bool on = actor.IsSet && _ragdoll.IsOn(actor.Address);
        using(ImRaii.Disabled(!actor.IsSet))
        {
            if(ImGui.Button(on ? "Stop ragdoll###extras_ragdoll" : "Ragdoll mode###extras_ragdoll", new System.Numerics.Vector2(200 * ImGuiHelpers.GlobalScale, 0)) && actor.IsSet)
                _ragdoll.Toggle(actor.Address, actor.ObjectIndex);
        }
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip(on ? "They get back up (back to their pose)." : "The chosen character goes limp and falls where they stand.");
        ImGui.SameLine();
        using(ImRaii.Disabled(!on))
        {
            if(ImGui.Button("Push###extras_ragdoll_push") && on)
                _ragdoll.Push(actor.Address);
        }
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("Shoves them again while they're down (a random way, up and over).");
        if(!actor.IsSet)
            ImGui.TextDisabled("Choose a character at the top first.");

        // How lively it is.
        float bounce = RagdollService.Bounce, slide = RagdollService.Slide;
        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        if(ImGui.SliderFloat("Bounce###extras_ragdoll_bounce", ref bounce, 0f, 0.95f, "%.2f"))
            RagdollService.Bounce = bounce;
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("How much a part bounces back off the floor.");
        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        if(ImGui.SliderFloat("Slide###extras_ragdoll_slide", ref slide, 0f, 0.995f, "%.3f"))
            RagdollService.Slide = slide;
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("How far they slide along the floor (high: like ice).");
    }
}
