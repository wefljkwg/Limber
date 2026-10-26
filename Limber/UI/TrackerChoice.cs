using Dalamud.Bindings.ImGui;

namespace Limber.UI;

/// <summary>The body-model picker shared by the Video and VLC Live tabs (one setting for both).</summary>
internal static class TrackerChoice
{
    private static readonly string[] Models = ["Accurate (RTMW3D)", "Fast (MediaPipe)"];

    /// <summary>True when the setting changed (the caller saves).</summary>
    public static bool Draw(Configuration cfg, string id)
    {
        bool changed = false;
        int model = cfg.TrackerAccurate ? 0 : 1;
        ImGui.SetNextItemWidth(170);
        if (ImGui.Combo($"Body model##{id}_model", ref model, Models, Models.Length))
        {
            cfg.TrackerAccurate = model == 0;
            changed = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Accurate: a much bigger whole-body model that also sees depth, hands and feet. Far better with\n" +
                             "bodies lying down, side-on, crouching or upside down (push-ups, sit-ups, floor work).\n" +
                             "Best on the graphics card; on the CPU it's slow.\n" +
                             "Fast: the lighter model from earlier versions. Fine for someone standing and facing the camera.");
        if (cfg.TrackerAccurate)
        {
            ImGui.SameLine();
            var gpu = cfg.TrackerGpu;
            if (ImGui.Checkbox($"Use the graphics card##{id}_gpu", ref gpu))
            {
                cfg.TrackerGpu = gpu;
                changed = true;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Runs the model on the graphics card (any DirectX 12 card). Much faster than the CPU,\n" +
                                 "at the cost of a little game frame rate while tracking. Off, or if it doesn't work: CPU.");
        }
        return changed;
    }
}
