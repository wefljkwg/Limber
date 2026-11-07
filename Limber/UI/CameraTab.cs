using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Limber.Rig;

namespace Limber.UI;

/// <summary>
/// The Camera page (Scene): the game camera's distance, field of view, angles and tilt, set directly; its limits
/// unlocked; moved off its place; through floors and walls; and shots saved to come back to (the Timeline can key the
/// camera too). In GPose and out of it.
/// </summary>
public sealed class CameraTab
{
    private readonly RigSettings _config;
    private string _shotName = "";

    /// <summary>The chosen character's facing (radians), for "Look from" (null: none).</summary>
    public Func<float?>? CharacterYaw { get; set; }

    public CameraTab(RigSettings config)
    {
        _config = config;
    }

    private const float Deg = 180f / MathF.PI;

    public void Draw()
    {
        var shot = CameraControl.Read();
        if(shot == null)
        {
            ImGui.TextDisabled("No camera to work with right now.");
            return;
        }

        // Everything back as the game has it.
        if(ImGui.Button("Reset camera###cam_reset_all"))
        {
            _config.CameraOffset = Vector3.Zero;
            _config.Save();
            CameraControl.Reset();
        }
        Tooltip("Back to the game's usual distance, field of view, height and angle, and Limber's move off its place undone.\n"
            + "(The limits stay as they are: turn Unlock off for the game's own.)");

        bool unlock = _config.CameraUnlock;
        if(ImGui.Checkbox("Unlock the camera's limits###cam_unlock", ref unlock))
        {
            _config.CameraUnlock = unlock;
            _config.Save();
        }
        Tooltip("Closer and further than the game allows, looking straight up or down, and a wider / narrower field of view.\n"
            + "Off: the camera's own limits come back exactly as the game had them (and the zoom is brought back inside them).");
        if(_config.CameraUnlock && ImGui.TreeNode("Unlocked limits###cam_limits"))
        {
            bool edit = false;
            float near = _config.CameraMinDistance, far = _config.CameraMaxDistance;
            float fMin = _config.CameraMinFoV, fMax = _config.CameraMaxFoV, pMin = _config.CameraMinPitch, pMax = _config.CameraMaxPitch;
            ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
            edit |= ImGui.SliderFloat("Closest###cam_near", ref near, 0.05f, 5f, "%.2f");
            ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
            edit |= ImGui.SliderFloat("Furthest###cam_far", ref far, 5f, 500f, "%.0f", ImGuiSliderFlags.Logarithmic);
            ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
            edit |= ImGui.SliderFloat("Narrowest view###cam_fmin", ref fMin, 0.01f, 0.69f, "%.2f");
            ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
            edit |= ImGui.SliderFloat("Widest view###cam_fmax", ref fMax, 0.78f, 3f, "%.2f");
            ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
            edit |= ImGui.SliderFloat("Furthest down###cam_pmin", ref pMin, -89.5f, 0f, "%.0f°");
            ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
            edit |= ImGui.SliderFloat("Furthest up###cam_pmax", ref pMax, 0f, 89.5f, "%.0f°");
            if(edit)
            {
                (_config.CameraMinDistance, _config.CameraMaxDistance) = (near, far);
                (_config.CameraMinFoV, _config.CameraMaxFoV, _config.CameraMinPitch, _config.CameraMaxPitch) = (fMin, fMax, pMin, pMax);
                _config.Save();
            }
            if(ImGui.SmallButton("Default unlocked limits###cam_limits_def"))
            {
                (_config.CameraMinDistance, _config.CameraMaxDistance) = (0.1f, 60f);
                (_config.CameraMinFoV, _config.CameraMaxFoV, _config.CameraMinPitch, _config.CameraMaxPitch) = (0.05f, 1.6f, -89f, 89f);
                _config.Save();
            }
            var own = CameraControl.Own();
            ImGui.TextDisabled($"The game's own: {own.MinDistance:0.0} to {own.MaxDistance:0} away, view {own.MinFoV:0.00} to {own.MaxFoV:0.00}, "
                + $"{own.MinPitch * Deg:0}° to {own.MaxPitch * Deg:0}°.");
            ImGui.TreePop();
        }

        bool through = _config.CameraNoCollide;
        if(ImGui.Checkbox("Through floors and walls###cam_nocollide", ref through))
        {
            _config.CameraNoCollide = through;
            _config.Save();
        }
        Tooltip("The camera passes through floors, walls and ceilings instead of bumping into them and pulling in.");

        // Where the camera is: moved off the game's place, along the way it looks.
        var offset = _config.CameraOffset;
        ImGui.SetNextItemWidth(-140 * ImGuiHelpers.GlobalScale);
        if(ImGui.DragFloat3("Position###cam_offset", ref offset, 0.01f, -20f, 20f, "%.2f"))
        {
            _config.CameraOffset = offset;
            _config.Save();
        }
        Tooltip("Moves the camera (and what it looks at) sideways (+ right), up and forward, in metres, the way the camera looks.");
        ImGui.SameLine();
        if(ImGui.SmallButton("Reset###cam_offset_reset"))
        {
            _config.CameraOffset = System.Numerics.Vector3.Zero;
            _config.Save();
        }

        var limits = CameraControl.Limits();
        float minD = limits?.MinD ?? 1.5f, maxD = limits?.MaxD ?? 20f, minF = limits?.MinF ?? -0.5f, maxF = limits?.MaxF ?? 0.8f;
        float minV = limits?.MinV ?? -1.48f, maxV = limits?.MaxV ?? 0.78f;
        bool changed = false;

        float distance = shot.Distance;
        ImGui.SetNextItemWidth(-140 * ImGuiHelpers.GlobalScale);
        if(ImGui.SliderFloat("Distance###cam_dist", ref distance, minD, maxD, "%.2f"))
        {
            shot.Distance = distance;
            changed = true;
        }
        Tooltip("How far the camera is from what it looks at (zoom). Ctrl+click to type. Right-click: the game's usual distance.");
        if(ImGui.IsItemClicked(ImGuiMouseButton.Right))
            CameraControl.Reset(distance: true, fov: false, tilt: false, pitch: false);

        float fov = shot.FoV;
        ImGui.SetNextItemWidth(-140 * ImGuiHelpers.GlobalScale);
        if(ImGui.SliderFloat("Field of view###cam_fov", ref fov, minF, maxF, "%.3f"))
        {
            shot.FoV = fov;
            changed = true;
        }
        Tooltip("Narrower (telephoto, flatter) or wider (more perspective). Right-click: the game's usual view.");
        if(ImGui.IsItemClicked(ImGuiMouseButton.Right))
            CameraControl.Reset(distance: false, fov: true, tilt: false, pitch: false);

        float yaw = shot.Yaw * Deg;
        ImGui.SetNextItemWidth(-140 * ImGuiHelpers.GlobalScale);
        if(ImGui.SliderFloat("Round###cam_yaw", ref yaw, -180f, 180f, "%.1f°"))
        {
            shot.Yaw = yaw / Deg;
            changed = true;
        }
        Tooltip("The camera's angle round what it looks at.");

        float pitch = shot.Pitch * Deg;
        ImGui.SetNextItemWidth(-140 * ImGuiHelpers.GlobalScale);
        if(ImGui.SliderFloat("Up / down###cam_pitch", ref pitch, minV * Deg, maxV * Deg, "%.1f°"))
        {
            shot.Pitch = pitch / Deg;
            changed = true;
        }
        Tooltip("Looking up (+) or down (-).");

        float tilt = shot.Tilt;
        ImGui.SetNextItemWidth(-140 * ImGuiHelpers.GlobalScale);
        if(ImGui.SliderFloat("Tilt###cam_tilt", ref tilt, -1f, 1f, "%.3f"))
        {
            shot.Tilt = tilt;
            changed = true;
        }
        Tooltip("The camera's tilt offset (what the game's camera height setting moves). Right-click: none.");
        if(ImGui.IsItemClicked(ImGuiMouseButton.Right))
            CameraControl.Reset(distance: false, fov: false, tilt: true, pitch: false);

        if(changed)
            CameraControl.Write(shot);

        // Quick angles.
        ImGui.Text("Look from:");
        float facing = CharacterYaw?.Invoke() ?? 0f;
        foreach(var (label, yawDeg, pitchDeg) in new[] { ("Front", 180f, 0f), ("Back", 0f, 0f), ("Left", -90f, 0f), ("Right", 90f, 0f), ("Above", float.NaN, -80f), ("Below", float.NaN, 60f) })
        {
            ImGui.SameLine();
            if(ImGui.SmallButton($"{label}###cam_q_{label}"))
            {
                // (round the chosen character, from the way it faces)
                if(!float.IsNaN(yawDeg))
                    shot.Yaw = facing + yawDeg / Deg;
                shot.Pitch = pitchDeg / Deg;
                CameraControl.Write(shot);
            }
        }
        Tooltip("Round the chosen character, from the way it faces.");

        // Saved shots.
        ImGui.Spacing();
        ImGui.Separator();
        var shots = _config.CameraShots ??= new();
        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("###cam_shot_name", "Name for this shot", ref _shotName, 48);
        ImGui.SameLine();
        if(ImGui.Button("Save this shot###cam_save"))
        {
            var saved = CameraControl.Read();
            if(saved != null)
            {
                saved.Name = _shotName.Trim().Length > 0 ? _shotName.Trim() : $"Shot {shots.Count + 1}";
                shots.Add(saved);
                _shotName = "";
                _config.Save();
            }
        }
        int remove = -1;
        for(int i = 0; i < shots.Count; i++)
        {
            using var id = ImRaii.PushId(i);
            if(ImGui.SmallButton("Go"))
                CameraControl.Write(shots[i]);
            ImGui.SameLine();
            ImGui.TextUnformatted($"{shots[i].Name}  ({shots[i].Distance:0.0} away, {shots[i].Yaw * Deg:0}° round, {shots[i].Pitch * Deg:0}° up)");
            ImGui.SameLine();
            if(ImGui.SmallButton("x") && ImGui.GetIO().KeyCtrl)
                remove = i;
            Tooltip("Hold Ctrl and click to delete this shot.");
        }
        if(remove >= 0)
        {
            shots.RemoveAt(remove);
            _config.Save();
        }
    }

    private static void Tooltip(string text)
    {
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip(text);
    }
}
