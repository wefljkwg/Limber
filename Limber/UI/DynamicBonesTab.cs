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
        changed |= Slider("Weight", () => s.Weight, v => s.Weight = v, "How much it hangs down when it isn't hard.", 2f);
        changed |= Slider("Inertia", () => s.Inertia, v => s.Inertia = v, "How much the body's speeding up and turning swings it (0%: moves with the body).");
        changed |= Slider("Stiffness", () => s.Stiffness, v => s.Stiffness = v, "How much it resists bending at each joint: higher keeps its shape and swings as one piece from the base (no warping when it's heavy or soft); lower lets it curl and fold.");
        ImGui.TextDisabled("While this character is Oral's partner, the shaft stays as posed (the mouth follows it).");

        // Each bone on its own.
        ImGui.Spacing();
        bool perBone = s.PerBone;
        if(ImGui.Checkbox("Each bone separately###dyn_perbone", ref perBone))
        {
            s.PerBone = perBone;
            if(perBone)
                FillBones(s, onlyMissing: true);
            changed = true;
        }
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("Give every bone along the penis its own hardness, bounce, weight and inertia (base to tip), "
                + "instead of the settings above for all of them. E.g. a firm base with a softer, heavier tip.");
        if(s.PerBone)
        {
            if(ImGui.SmallButton("Copy the settings above to all###dyn_copyall"))
            {
                FillBones(s, onlyMissing: false);
                changed = true;
            }
            ImGui.SameLine();
            if(ImGui.SmallButton("Firm base, soft tip###dyn_taper"))
            {
                FillBones(s, onlyMissing: false);
                int n = DynamicBoneService.PenisBones.Length;
                for(int i = 0; i < n; i++)
                {
                    float t = n > 1 ? i / (float)(n - 1) : 0f;
                    var b = s.Bones[DynamicBoneService.PenisBones[i]];
                    b.Hardness = Math.Clamp(s.Hardness + (1f - s.Hardness) * (1f - t) * 0.6f - 0.25f * t, 0f, 1f);
                    b.Weight = Math.Clamp(s.Weight * (0.7f + 0.8f * t), 0f, 2f);
                    b.Bounce = Math.Clamp(s.Bounce * (0.8f + 0.5f * t), 0f, 1f);
                }
                changed = true;
            }
            for(int i = 0; i < DynamicBoneService.PenisBones.Length; i++)
            {
                string bone = DynamicBoneService.PenisBones[i];
                if(!s.Bones.TryGetValue(bone, out var b))
                    s.Bones[bone] = b = new DynamicBoneSpring { Hardness = s.Hardness, Bounce = s.Bounce, Weight = s.Weight, Inertia = s.Inertia, Stiffness = s.Stiffness };
                string label = i == 0 ? "Base" : i == DynamicBoneService.PenisBones.Length - 1 ? "Tip" : $"Shaft {i}";
                if(!ImGui.TreeNode($"{label} ({bone})###dyn_b_{bone}"))
                    continue;
                changed |= Slider($"Hardness##{bone}", () => b.Hardness, v => b.Hardness = v, "100%: this bone holds the pose exactly. 0%: it hangs and swings.");
                changed |= Slider($"Bounce##{bone}", () => b.Bounce, v => b.Bounce = v, "How long this bone keeps swinging.");
                changed |= Slider($"Weight##{bone}", () => b.Weight, v => b.Weight = v, "How heavy this bone is: how much it hangs down when it isn't hard.", 2f);
                changed |= Slider($"Inertia##{bone}", () => b.Inertia, v => b.Inertia = v, "How much this bone trails behind when the body moves.");
                changed |= Slider($"Stiffness##{bone}", () => b.Stiffness, v => b.Stiffness = v, "How much this joint resists bending against the bone before it.");
                ImGui.TreePop();
            }
        }

        if(changed)
            _config.Save();
    }

    private static void FillBones(DynamicBoneSettings s, bool onlyMissing)
    {
        foreach(var bone in DynamicBoneService.PenisBones)
            if(!onlyMissing || !s.Bones.ContainsKey(bone))
                s.Bones[bone] = new DynamicBoneSpring { Hardness = s.Hardness, Bounce = s.Bounce, Weight = s.Weight, Inertia = s.Inertia, Stiffness = s.Stiffness };
    }

    private static bool Slider(string label, Func<float> get, Action<float> set, string tip, float max = 1f)
    {
        float v = get() * 100f;
        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        // (a label like "Weight##bone" shows "Weight" and keeps each bone's slider apart)
        string shown = label.Split("##")[0];
        bool changed = ImGui.SliderFloat($"{shown}###dyn_{label}", ref v, 0f, max * 100f, "%.0f%%");
        if(changed)
            set(Math.Clamp(v / 100f, 0f, max));
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip(tip);
        return changed;
    }
}
