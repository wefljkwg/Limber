using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Limber.Rig;

namespace Limber.UI;

/// <summary>The Dynamic Bones page: Koikatsu-style dynamic bones (penis only for now), per character.</summary>
public sealed class DynamicBonesTab
{
    private readonly Selection _selection;
    private readonly RigSettings _config;

    public DynamicBonesTab(Selection selection, RigSettings config)
    {
        _selection = selection;
        _config = config;
    }

    public void Draw()
    {
        var actor = _selection.Actor;
        if(!actor.IsSet)
        {
            ImGui.TextDisabled("Choose a character at the top of the window.");
            return;
        }
        if(!Limber.Koi.Safety.AdultAllowed(actor.Address))
        {
            ImGui.TextDisabled("Only for adult characters.");
            return;
        }
        string name = actor.Name ?? "";
        if(name.Length == 0)
        {
            ImGui.TextDisabled("This character has no name to keep the settings under.");
            return;
        }
        ImGui.TextWrapped("The penis as a chain of springs, like Koikatsu's: Hardness decides how much the pose wins. 100%: exactly as "
            + "posed / animated. Lower: it gives, hangs and swings with the body's moves. 0%: flaccid. "
            + "Needs an IVCS / YAS skeleton with a penis (iv_ochinko bones). Kept for this character by name; only on your screen.");
        ImGui.Spacing();

        var all = _config.DynamicBones ??= new();
        all.TryGetValue(name, out var s);
        bool on = s is { Enabled: true };
        bool changed = false;
        if(ImGui.Checkbox($"Dynamic bones on {name}###dyn_on", ref on))
        {
            if(on)
            {
                s ??= new DynamicBoneSettings();
                s.Enabled = true;
                all[name] = s;
            }
            else if(s != null)
                s.Enabled = false;
            changed = true;
        }
        if(s == null || !s.Enabled)
        {
            if(changed)
                _config.Save();
            return;
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Penis");
        changed |= Slider("Hardness", () => s.Hardness, v => s.Hardness = v,
            "100%: holds the pose exactly. 0%: flaccid (hangs, swings). Anything between: firm but gives a little, half hard...");
        if(ImGui.SmallButton("Hard###dyn_hard")) { s.Hardness = 1f; changed = true; }
        ImGui.SameLine();
        if(ImGui.SmallButton("Half###dyn_half")) { s.Hardness = 0.5f; changed = true; }
        ImGui.SameLine();
        if(ImGui.SmallButton("Soft###dyn_soft")) { s.Hardness = 0.15f; changed = true; }
        ImGui.SameLine();
        if(ImGui.SmallButton("Flaccid###dyn_flaccid")) { s.Hardness = 0f; changed = true; }
        changed |= Slider("Bounce", () => s.Bounce, v => s.Bounce = v, "How long it keeps swinging after a move (0%: settles at once).");
        changed |= Slider("Weight", () => s.Weight, v => s.Weight = v, "How much it hangs down when it isn't hard.");
        changed |= Slider("Inertia", () => s.Inertia, v => s.Inertia = v, "How much it trails behind when the body moves (0%: moves with the body).");
        ImGui.TextDisabled("While this character is Oral's partner, the shaft stays as posed (the mouth follows it).");

        if(changed)
            _config.Save();
    }

    private static bool Slider(string label, Func<float> get, Action<float> set, string tip)
    {
        float v = get() * 100f;
        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        bool changed = ImGui.SliderFloat($"{label}###dyn_{label}", ref v, 0f, 100f, "%.0f%%");
        if(changed)
            set(Math.Clamp(v / 100f, 0f, 1f));
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip(tip);
        return changed;
    }
}
