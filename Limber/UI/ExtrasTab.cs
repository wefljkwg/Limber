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
        if(!actor.IsSet)
            ImGui.TextDisabled("Choose a character at the top first.");
    }
}
