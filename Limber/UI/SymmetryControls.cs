using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Limber.Rig;

namespace Limber.UI;

/// <summary>The symmetry switch shown on the IK and FK posing pages (one setting per character, shared by both).</summary>
public static class SymmetryControls
{
    private static readonly string[] Modes = ["Off", "Left side to right", "Right side to left"];

    public static void Draw(RigSettings config, string character, string id)
    {
        if(character.Length == 0)
            return;
        var all = config.Symmetry ??= new();
        all.TryGetValue(character, out var s);
        int mode = (int)(s?.Mode ?? SymmetryMode.Off);
        ImGui.TextUnformatted("Symmetry:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
        if(ImGui.Combo($"###{id}_sym_mode", ref mode, Modes, Modes.Length))
        {
            s ??= all[character] = new SymmetrySettings();
            s.Mode = (SymmetryMode)mode;
            config.Save();
        }
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("Live: the other side takes this side's exact position and rotation, reflected across the "
                + "character's own middle (not the world's), every frame. Pose one side, the other matches.");
        if(s != null && s.Mode != SymmetryMode.Off)
        {
            ImGui.SameLine();
            bool scale = s.Scale;
            if(ImGui.Checkbox($"Scale too###{id}_sym_scale", ref scale))
            {
                s.Scale = scale;
                config.Save();
            }
            if(ImGui.IsItemHovered())
                ImGui.SetTooltip("The other side's bones take this side's scale too (Bone scale and body shape).");
        }
    }
}
