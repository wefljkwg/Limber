using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Limber.UI;

/// <summary>
/// Widths that fit the window however wide or narrow it is: a slider keeps room for its label beside it (and when the
/// window is too narrow for both, the two share it), a fixed width never runs past the window's edge; and a row of
/// controls that wraps onto the next line instead of running off the side.
/// </summary>
public static class Fit
{
    private static float Avail => MathF.Max(ImGui.GetContentRegionAvail().X, 1f);

    /// <summary>A control with its label beside it: everything but <paramref name="labelRoom"/>, or (narrow) a share.</summary>
    public static float Label(float labelRoom)
    {
        float avail = Avail;
        float wide = avail - labelRoom;
        float narrow = avail * 0.55f;
        return MathF.Max(wide, MathF.Min(narrow, avail - 30f * ImGuiHelpers.GlobalScale));
    }

    /// <summary>A fixed width, made smaller when the window is too narrow for it (its label still showing).</summary>
    public static float Fixed(float width)
    {
        float avail = Avail;
        return width <= avail * 0.65f ? width : MathF.Max(avail * 0.65f, 40f * ImGuiHelpers.GlobalScale);
    }

    /// <summary>Instead of ImGui.SameLine(): stays on this line if something <paramref name="width"/> wide still fits
    /// after the last item, otherwise starts the next line.</summary>
    public static void Next(float width)
    {
        float right = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X;
        float end = ImGui.GetItemRectMax().X;
        if(end + ImGui.GetStyle().ItemSpacing.X + width <= right)
            ImGui.SameLine();
    }

    private static string Shown(string label)
    {
        int hash = label.IndexOf("##", StringComparison.Ordinal);
        return hash >= 0 ? label[..hash] : label;
    }

    /// <summary>How wide a checkbox or radio button with this label is.</summary>
    public static float Check(string label)
        => ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X + ImGui.CalcTextSize(Shown(label)).X;

    /// <summary>How wide a button with this label is.</summary>
    public static float Button(string label)
        => ImGui.CalcTextSize(Shown(label)).X + ImGui.GetStyle().FramePadding.X * 2f;

    /// <summary>How wide some text is.</summary>
    public static float Text(string text) => ImGui.CalcTextSize(text).X;
}
