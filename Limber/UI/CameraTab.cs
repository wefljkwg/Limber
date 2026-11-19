using System;
using System.Linq;
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

        bool free = CameraControl.Free;
        if(ImGui.Checkbox("Free camera###cam_free", ref free))
            CameraControl.SetFree(free);
        Tooltip("The camera flies on its own: W / S forward and back, A / D sideways, E (or Space) up, Q down,\n"
            + "Shift faster, Ctrl slower. Turn it with the mouse as usual. The keys don't move your character while it's on.\n"
            + "Off: the camera goes back to following the character.");
        if(free)
        {
            ImGui.SameLine();
            float speed = CameraControl.FreeSpeed;
            ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(140f * ImGuiHelpers.GlobalScale));
            if(ImGui.SliderFloat("Speed###cam_free_speed", ref speed, 0.2f, 20f, "%.1f m/s", ImGuiSliderFlags.Logarithmic))
                CameraControl.FreeSpeed = speed;
        }

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
            ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(200f * ImGuiHelpers.GlobalScale));
            edit |= ImGui.SliderFloat("Closest###cam_near", ref near, 0.05f, 5f, "%.2f");
            ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(200f * ImGuiHelpers.GlobalScale));
            edit |= ImGui.SliderFloat("Furthest###cam_far", ref far, 5f, 500f, "%.0f", ImGuiSliderFlags.Logarithmic);
            ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(200f * ImGuiHelpers.GlobalScale));
            edit |= ImGui.SliderFloat("Narrowest view###cam_fmin", ref fMin, 0.01f, 0.69f, "%.2f");
            ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(200f * ImGuiHelpers.GlobalScale));
            edit |= ImGui.SliderFloat("Widest view###cam_fmax", ref fMax, 0.78f, 3f, "%.2f");
            ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(200f * ImGuiHelpers.GlobalScale));
            edit |= ImGui.SliderFloat("Furthest down###cam_pmin", ref pMin, -89.5f, 0f, "%.0f°");
            ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(200f * ImGuiHelpers.GlobalScale));
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
        ImGui.SetNextItemWidth(Limber.UI.Fit.Label(140f * ImGuiHelpers.GlobalScale));
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
        ImGui.SetNextItemWidth(Limber.UI.Fit.Label(140f * ImGuiHelpers.GlobalScale));
        if(ImGui.SliderFloat("Distance###cam_dist", ref distance, minD, maxD, "%.2f"))
        {
            shot.Distance = distance;
            changed = true;
        }
        Tooltip("How far the camera is from what it looks at (zoom). Ctrl+click to type. Right-click: the game's usual distance.");
        if(ImGui.IsItemClicked(ImGuiMouseButton.Right))
            CameraControl.Reset(distance: true, fov: false, tilt: false, pitch: false);

        float fov = shot.FoV;
        ImGui.SetNextItemWidth(Limber.UI.Fit.Label(140f * ImGuiHelpers.GlobalScale));
        if(ImGui.SliderFloat("Field of view###cam_fov", ref fov, minF, maxF, "%.3f"))
        {
            shot.FoV = fov;
            changed = true;
        }
        Tooltip("Narrower (telephoto, flatter) or wider (more perspective). Right-click: the game's usual view.");
        if(ImGui.IsItemClicked(ImGuiMouseButton.Right))
            CameraControl.Reset(distance: false, fov: true, tilt: false, pitch: false);

        float yaw = shot.Yaw * Deg;
        ImGui.SetNextItemWidth(Limber.UI.Fit.Label(140f * ImGuiHelpers.GlobalScale));
        if(ImGui.SliderFloat("Round###cam_yaw", ref yaw, -180f, 180f, "%.1f°"))
        {
            shot.Yaw = yaw / Deg;
            changed = true;
        }
        Tooltip("The camera's angle round what it looks at.");

        float pitch = shot.Pitch * Deg;
        ImGui.SetNextItemWidth(Limber.UI.Fit.Label(140f * ImGuiHelpers.GlobalScale));
        if(ImGui.SliderFloat("Up / down###cam_pitch", ref pitch, minV * Deg, maxV * Deg, "%.1f°"))
        {
            shot.Pitch = pitch / Deg;
            changed = true;
        }
        Tooltip("Looking up (+) or down (-).");

        float tilt = shot.Tilt;
        ImGui.SetNextItemWidth(Limber.UI.Fit.Label(140f * ImGuiHelpers.GlobalScale));
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
        ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(200f * ImGuiHelpers.GlobalScale));
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

        DrawRig();
    }

    // ---- Camera props and monitors

    private static readonly (string Label, float Aspect)[] Aspects = [("16:9", 16f / 9f), ("4:3", 4f / 3f), ("1:1", 1f), ("21:9", 21f / 9f), ("9:16", 9f / 16f)];

    private void DrawRig()
    {
        ImGui.Spacing();
        ImGui.Separator();
        if(!ImGui.CollapsingHeader("Camera props and monitors###cam_rig"))
            return;
        CameraRig.TabOpen();
        var cams = _config.CameraProps ??= new();
        var mons = _config.Monitors ??= new();
        bool save = false;
        ImGui.TextWrapped("Place cameras in the world and screens that show what they see (like Koikatsu's studio cameras). "
                          + "The game can only draw from one camera at a time, so now and then one frame is drawn from a camera prop "
                          + "for its screens and the last normal frame is shown instead: a higher refresh is smoother on the screens "
                          + "but makes the game itself a little less smooth.");
        bool on = _config.MonitorsOn;
        if(ImGui.Checkbox("Monitors on###cam_rig_on", ref on)) { _config.MonitorsOn = on; save = true; }
        Tooltip("Off: no frames are taken for the screens (and they aren't drawn).");
        ImGui.SameLine();
        float refresh = _config.MonitorRefresh;
        ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(160f * ImGuiHelpers.GlobalScale));
        if(ImGui.SliderFloat("Refresh###cam_rig_hz", ref refresh, 0.5f, 30f, "%.1f / s", ImGuiSliderFlags.Logarithmic)) { _config.MonitorRefresh = refresh; save = true; }
        Tooltip("How many new pictures a second each camera's screens get. Each one is a frame the game shows twice, so keep it low (3-8) for smooth play.
"
            + "With DLSS / FSR the frame after a picture can smear a little: plain anti-aliasing avoids that.");
        bool show = _config.ShowCameraProps, always = _config.ShowCameraPropsAlways;
        if(ImGui.Checkbox("Show the cameras###cam_rig_show", ref show)) { _config.ShowCameraProps = show; save = true; }
        Tooltip("Small wireframe cameras where they are (only while this section is open, unless Always).");
        ImGui.SameLine();
        if(ImGui.Checkbox("Always###cam_rig_always", ref always)) { _config.ShowCameraPropsAlways = always; save = true; }
        ImGui.SameLine();
        bool move = CameraRig.GizmoMove;
        if(ImGui.RadioButton("Move###cam_rig_move", move)) CameraRig.GizmoMove = true;
        ImGui.SameLine();
        if(ImGui.RadioButton("Turn###cam_rig_turn", !move)) CameraRig.GizmoMove = false;
        Tooltip("What the handles on the chosen camera or screen do.");

        // Cameras
        ImGui.TextUnformatted("Cameras");
        ImGui.SameLine();
        if(ImGui.SmallButton("+ Camera here###cam_rig_addcam") && CameraRig.Eye(out var eye, out var fwd))
        {
            var cam = new CameraProp { Name = $"Camera {cams.Count + 1}", Position = eye, Rotation = CameraRig.Facing(fwd) };
            cams.Add(cam);
            CameraRig.Selected = cam.Id;
            save = true;
        }
        Tooltip("A camera where the game camera is now, looking the same way.");
        CameraProp? dropCam = null;
        foreach(var cam in cams)
        {
            using var id = ImRaii.PushId(cam.Id.ToString());
            bool sel = CameraRig.Selected == cam.Id;
            if(ImGui.RadioButton($"###sel", sel))
                CameraRig.Selected = sel ? Guid.Empty : cam.Id;
            Tooltip("Choose it (handles to move / turn it).");
            ImGui.SameLine();
            string name = cam.Name;
            ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(120f * ImGuiHelpers.GlobalScale));
            if(ImGui.InputText("###name", ref name, 40)) { cam.Name = name; save = true; }
            ImGui.SameLine();
            float fov = cam.FoV;
            ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(120f * ImGuiHelpers.GlobalScale));
            if(ImGui.SliderFloat("FoV###fov", ref fov, 0f, 120f, fov < 1f ? "game's" : "%.0f°")) { cam.FoV = fov; save = true; }
            Tooltip("Its field of view (0: the same as the game camera's).");
            ImGui.SameLine();
            if(ImGui.SmallButton("To my view###here") && CameraRig.Eye(out var e2, out var f2))
            {
                cam.Position = e2;
                cam.Rotation = CameraRig.Facing(f2);
                save = true;
            }
            Tooltip("Moves it to where the game camera is, looking the same way.");
            ImGui.SameLine();
            if(ImGui.SmallButton("Remove###rm"))
                dropCam = cam;
        }
        if(dropCam != null)
        {
            cams.Remove(dropCam);
            save = true;
        }

        // Monitors
        ImGui.TextUnformatted("Screens");
        ImGui.SameLine();
        if(ImGui.SmallButton("+ Screen in front of me###cam_rig_addmon") && CameraRig.Eye(out var me, out var mf))
        {
            var mon = new MonitorProp
            {
                Name = $"Screen {mons.Count + 1}", Position = me + mf * 2.5f, Rotation = CameraRig.Facing(-mf),
                Camera = cams.Count > 0 ? cams[^1].Id : Guid.Empty,
            };
            mons.Add(mon);
            CameraRig.Selected = mon.Id;
            save = true;
        }
        Tooltip("A screen 2.5 m in front of the game camera, facing it, showing the last camera.");
        MonitorProp? dropMon = null;
        foreach(var mon in mons)
        {
            using var id = ImRaii.PushId(mon.Id.ToString());
            bool sel = CameraRig.Selected == mon.Id;
            if(ImGui.RadioButton($"###sel", sel))
                CameraRig.Selected = sel ? Guid.Empty : mon.Id;
            Tooltip("Choose it (handles to move / turn it).");
            ImGui.SameLine();
            bool mOn = mon.On;
            if(ImGui.Checkbox("###on", ref mOn)) { mon.On = mOn; save = true; }
            Tooltip("Screen on.");
            ImGui.SameLine();
            string name = mon.Name;
            ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(110f * ImGuiHelpers.GlobalScale));
            if(ImGui.InputText("###name", ref name, 40)) { mon.Name = name; save = true; }
            ImGui.SameLine();
            var current = cams.FirstOrDefault(c => c.Id == mon.Camera);
            ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(120f * ImGuiHelpers.GlobalScale));
            using(var combo = ImRaii.Combo("###cam", current?.Name ?? "(no camera)"))
            {
                if(combo.Success)
                    foreach(var c in cams)
                        if(ImGui.Selectable($"{c.Name}###c_{c.Id}", c.Id == mon.Camera)) { mon.Camera = c.Id; save = true; }
            }
            Tooltip("Which camera it shows.");
            ImGui.SameLine();
            if(ImGui.SmallButton("Remove###rm"))
                dropMon = mon;
            if(!sel)
                continue;
            using var indent = ImRaii.PushIndent();
            float w = mon.Width;
            ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(160f * ImGuiHelpers.GlobalScale));
            if(ImGui.SliderFloat("Width###w", ref w, 0.1f, 20f, "%.2f m", ImGuiSliderFlags.Logarithmic)) { mon.Width = w; save = true; }
            ImGui.SameLine();
            string aspect = Aspects.FirstOrDefault(a => MathF.Abs(a.Aspect - mon.Aspect) < 0.01f).Label ?? $"{mon.Aspect:0.00}";
            ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(80f * ImGuiHelpers.GlobalScale));
            using(var combo = ImRaii.Combo("Shape###asp", aspect))
            {
                if(combo.Success)
                    foreach(var a in Aspects)
                        if(ImGui.Selectable(a.Label, MathF.Abs(a.Aspect - mon.Aspect) < 0.01f)) { mon.Aspect = a.Aspect; save = true; }
            }
            float bright = mon.Brightness, bezel = mon.Bezel;
            ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(160f * ImGuiHelpers.GlobalScale));
            if(ImGui.SliderFloat("Brightness###br", ref bright, 0f, 3f, "%.2f")) { mon.Brightness = bright; save = true; }
            ImGui.SameLine();
            ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(120f * ImGuiHelpers.GlobalScale));
            if(ImGui.SliderFloat("Frame###bz", ref bezel, 0f, 0.2f, "%.3f")) { mon.Bezel = bezel; save = true; }
            Tooltip("The frame round the picture (of the width).");
            ImGui.SameLine();
            var bc = new Vector4(mon.BezelColour is { Length: >= 3 } b ? new Vector3(b[0], b[1], b[2]) : new Vector3(0.05f), 1f);
            if(ImGui.ColorEdit4("###bzc", ref bc, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoAlpha)) { mon.BezelColour = [bc.X, bc.Y, bc.Z, 1f]; save = true; }
            Tooltip("The frame's colour.");
            bool mirror = mon.Mirror, backPanel = mon.BackPanel;
            if(ImGui.Checkbox("Mirror###mir", ref mirror)) { mon.Mirror = mirror; save = true; }
            Tooltip("The picture flipped left to right (like a mirror).");
            ImGui.SameLine();
            if(ImGui.Checkbox("Dark back###back", ref backPanel)) { mon.BackPanel = backPanel; save = true; }
            Tooltip("From behind it's a dark panel (off: the picture shows from both sides).");
            ImGui.SameLine();
            if(ImGui.SmallButton("In front of me###mhere") && CameraRig.Eye(out var me2, out var mf2))
            {
                mon.Position = me2 + mf2 * 2.5f;
                mon.Rotation = CameraRig.Facing(-mf2);
                save = true;
            }
        }
        if(dropMon != null)
        {
            mons.Remove(dropMon);
            save = true;
        }
        if(Limber.Gpu.MonitorRenderer.Error.Length > 0)
        {
            ImGui.TextColored(new Vector4(0.95f, 0.4f, 0.4f, 1f), "Can't draw the screens: " + Limber.Gpu.MonitorRenderer.Error);
            ImGui.SameLine();
            if(ImGui.SmallButton("Try again###cam_rig_retry"))
                Limber.Gpu.MonitorRenderer.Retry();
        }
        if(save)
            _config.Save();
    }

    private static void Tooltip(string text)
    {
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip(text);
    }
}
