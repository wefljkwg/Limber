using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Limber.Rig;

namespace Limber.UI;

/// <summary>
/// The Look at page: the chosen character's head and eyes follow another character (their face, or any of their
/// bones), one of their own bones, or the camera.
/// </summary>
public sealed class LookAtTab
{
    private readonly Selection _selection;
    private readonly RigSettings _config;
    private readonly LookAtService _lookAt;
    private string _customBone = "";

    public LookAtTab(Selection selection, RigSettings config, LookAtService lookAt)
    {
        _selection = selection;
        _config = config;
        _lookAt = lookAt;
    }

    private static readonly (string Name, string Bone)[] Bones =
    [
        ("Face", "j_kao"),
        ("Chest", "j_sebo_c"),
        ("Hips", "j_kosi"),
        ("Left hand", "j_te_l"),
        ("Right hand", "j_te_r"),
        ("Left foot", "j_asi_d_l"),
        ("Right foot", "j_asi_d_r"),
    ];

    public void Draw()
    {
        var actor = _selection.Actor;
        if(!actor.IsSet)
        {
            ImGui.TextDisabled("Choose a character at the top of the window.");
            return;
        }
        string name = actor.Name ?? "";
        if(name.Length == 0)
        {
            ImGui.TextDisabled("This character has no name to keep the settings under.");
            return;
        }
        var all = _config.LookAt ??= new();
        if(!all.TryGetValue(name, out var s))
            all[name] = s = new LookAtSettings();
        bool changed = false;

        ImGui.TextWrapped("The character's head and eyes follow someone: another character (their face, or any of their bones), "
            + "one of their own bones, or the camera. It works on top of the pose and animation, turns smoothly, stays within "
            + "a natural range from the chest, and looks back to the front when the target goes behind them.");
        ImGui.Spacing();

        // On / off and whether it's following right now.
        using(ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.25f, 0.5f, 0.3f, 1f), !s.Enabled))
        using(ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.55f, 0.25f, 0.25f, 1f), s.Enabled))
        {
            if(ImGui.Button(s.Enabled ? "Stop looking###look_go" : "Start looking###look_go", new Vector2(140 * ImGuiHelpers.GlobalScale, 0)))
            {
                s.Enabled = !s.Enabled;
                changed = true;
            }
        }
        ImGui.SameLine();
        if(!s.Enabled)
            ImGui.TextDisabled("off");
        else if(_lookAt.IsFollowing(actor.Address))
            ImGui.TextColored(new Vector4(0.5f, 0.9f, 0.5f, 1f), "following");
        else
            ImGui.TextDisabled("waiting (the target isn't here?)");

        // Who.
        ImGui.Spacing();
        string who = s.Self ? "Themselves (one of their own bones)" : s.Target.Length == 0 ? "The camera" : s.Target;
        ImGui.SetNextItemWidth(260 * ImGuiHelpers.GlobalScale);
        using(var combo = ImRaii.Combo("Look at###look_who", who))
        {
            if(combo)
            {
                if(ImGui.Selectable("The camera###look_cam", !s.Self && s.Target.Length == 0))
                {
                    s.Self = false;
                    s.Target = "";
                    changed = true;
                }
                if(ImGui.Selectable("Themselves (one of their own bones)###look_self", s.Self))
                {
                    s.Self = true;
                    if(s.TargetBone == "j_kao")
                        s.TargetBone = "j_te_r";
                    changed = true;
                }
                var seen = new System.Collections.Generic.HashSet<string>();
                foreach(var other in _selection.ListCharacters())
                {
                    if(string.IsNullOrEmpty(other.Name) || other.Name == name || !seen.Add(other.Name))
                        continue;
                    if(ImGui.Selectable($"{other.Name}###look_t_{other.ObjectIndex}", !s.Self && s.Target == other.Name))
                    {
                        s.Self = false;
                        s.Target = other.Name;
                        changed = true;
                    }
                }
            }
        }
        Tooltip("Who to look at. Kept by name, so it carries on when they leave and come back.");

        // Which bone.
        if(s.Self || s.Target.Length > 0)
        {
            ImGui.Text("Their:");
            foreach(var (label, bone) in Bones)
            {
                if(s.Self && bone == "j_kao")
                    continue;
                ImGui.SameLine();
                bool on = s.TargetBone == bone;
                using(ImRaii.PushColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.ButtonActive), on))
                {
                    if(ImGui.SmallButton($"{label}###look_b_{bone}"))
                    {
                        s.TargetBone = bone;
                        changed = true;
                    }
                }
            }
            if(_customBone.Length == 0)
                _customBone = s.TargetBone;
            ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
            if(ImGui.InputText("Bone name###look_bone", ref _customBone, 64, ImGuiInputTextFlags.EnterReturnsTrue) && _customBone.Trim().Length > 0)
            {
                s.TargetBone = _customBone.Trim();
                changed = true;
            }
            Tooltip("Any bone by its name (press Enter), for example j_ude_b_l (left forearm) or a body mod's bone. "
                + "Hover a dot on the IK Posing page to see a bone's name. If they don't have it, their face is used.");
            if(!ImGui.IsItemActive())
                _customBone = s.TargetBone;
        }

        // How.
        ImGui.Spacing();
        ImGui.Separator();
        bool eyes = s.Eyes;
        if(ImGui.Checkbox("Eyes follow###look_eyes", ref eyes))
        {
            s.Eyes = eyes;
            changed = true;
        }
        Tooltip("The eyes turn the rest of the way (needs a face with eye bones; most faces have them).");
        ImGui.SameLine();
        bool behind = s.LetGoBehind;
        if(ImGui.Checkbox("Look away when behind###look_behind", ref behind))
        {
            s.LetGoBehind = behind;
            changed = true;
        }
        Tooltip("When the target goes well behind the shoulder, the head turns back to the front instead of staying craned round.");

        changed |= Slider("Head turn", s.Head * 100f, 0f, 100f, "%.0f%%", v => s.Head = v / 100f,
            "How much the head (and neck) turns toward the target. 0% = only the eyes move.");
        changed |= Slider("Speed", s.Speed, 0.5f, 20f, "%.1f", v => s.Speed = v,
            "How quickly it turns toward the target (low = slow and lazy, high = snappy).");
        changed |= Slider("Side to side", s.MaxTurn, 0f, 120f, "%.0f°", v => s.MaxTurn = v,
            "How far the head can turn left / right from where the chest faces.");
        changed |= Slider("Up and down", s.MaxTilt, 0f, 80f, "%.0f°", v => s.MaxTilt = v,
            "How far the head can tilt up / down from where the chest faces.");
        using(ImRaii.Disabled(!s.Eyes))
            changed |= Slider("Eye range", s.EyeRange, 0f, 45f, "%.0f°", v => s.EyeRange = v,
                "How far the eyes can turn in the head.");

        // Fine adjustments: where it looks, the head, the eyes (the game's look can land a little past or off).
        ImGui.Spacing();
        if(ImGui.CollapsingHeader("Adjustments###look_adjust"))
        {
            ImGui.TextDisabled("For when the look lands a little off. + is to the character's left and up.");
            ImGui.Text("Where it looks");
            changed |= Slider("Point left / right", s.AimLeft * 100f, -50f, 50f, "%.1f cm", v => s.AimLeft = v / 100f,
                "Moves the point looked at to the character's left (+) or right (-), in centimetres.");
            changed |= Slider("Point up / down", s.AimUp * 100f, -50f, 50f, "%.1f cm", v => s.AimUp = v / 100f,
                "Moves the point looked at up (+) or down (-), in centimetres.");
            changed |= Slider("Point nearer / further", s.AimForward * 100f, -50f, 50f, "%.1f cm", v => s.AimForward = v / 100f,
                "Moves the point looked at toward the character's front (+) or back (-), in centimetres.");
            changed |= Slider("Look left / right", s.AimYaw, -30f, 30f, "%.1f°", v => s.AimYaw = v,
                "Turns the whole look (head and eyes) to the left (+) or right (-).");
            changed |= Slider("Look up / down", s.AimPitch, -30f, 30f, "%.1f°", v => s.AimPitch = v,
                "Turns the whole look (head and eyes) up (+) or down (-).");
            ImGui.Text("Head");
            changed |= Slider("Head turn left / right", s.HeadYaw, -45f, 45f, "%.1f°", v => s.HeadYaw = v,
                "The head turned a little more to the left (+) or right (-) on top of the look (the eyes still find the target).");
            changed |= Slider("Head nod up / down", s.HeadPitch, -30f, 30f, "%.1f°", v => s.HeadPitch = v,
                "The head tipped up (+) or down (-) on top of the look (the eyes still find the target).");
            changed |= Slider("Head tilt", s.HeadRoll, -30f, 30f, "%.1f°", v => s.HeadRoll = v,
                "The head tilted to the character's left (+) or right (-).");
            ImGui.Text("Eyes");
            using(ImRaii.Disabled(!s.Eyes))
            {
                changed |= Slider("Eyes left / right", s.EyeYaw, -20f, 20f, "%.1f°", v => s.EyeYaw = v,
                    "The eyes turned to the left (+) or right (-) of where they aim.");
                changed |= Slider("Eyes up / down", s.EyePitch, -20f, 20f, "%.1f°", v => s.EyePitch = v,
                    "The eyes turned up (+) or down (-) of where they aim.");
                changed |= Slider("Eyes apart / together", s.EyeSpread, -10f, 10f, "%.1f°", v => s.EyeSpread = v,
                    "+ turns each eye outward (they look less crossed), - inward (more crossed).");
            }
            if(ImGui.SmallButton("Clear the adjustments###look_adjust_clear"))
            {
                s.AimLeft = s.AimUp = s.AimForward = s.AimYaw = s.AimPitch = 0f;
                s.HeadYaw = s.HeadPitch = s.HeadRoll = 0f;
                s.EyeYaw = s.EyePitch = s.EyeSpread = 0f;
                changed = true;
            }
        }

        if(ImGui.SmallButton("Defaults###look_defaults"))
        {
            var d = new LookAtSettings();
            s.Head = d.Head; s.Speed = d.Speed; s.MaxTurn = d.MaxTurn; s.MaxTilt = d.MaxTilt; s.EyeRange = d.EyeRange;
            s.Eyes = d.Eyes; s.LetGoBehind = d.LetGoBehind;
            changed = true;
        }

        if(changed)
            _config.Save();
    }

    private static bool Slider(string label, float value, float min, float max, string format, Action<float> set, string tip)
    {
        ImGui.SetNextItemWidth(260 * ImGuiHelpers.GlobalScale);
        bool edited = ImGui.SliderFloat($"{label}###look_{label}", ref value, min, max, format);
        Tooltip(tip + "\nCtrl+click to type a value.");
        if(edited)
            set(value);
        return edited;
    }

    private static void Tooltip(string text)
    {
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip(text);
    }
}
