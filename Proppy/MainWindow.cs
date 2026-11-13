using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Bindings.ImGuizmo;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using SceneCameraManager = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CameraManager;

namespace Proppy;

/// <summary>Proppy's window: the housing items to place, the ones placed here, and the chosen one's place, turn and size.</summary>
public sealed unsafe class MainWindow : Window
{
    private readonly Plugin _plugin;
    private string _search = "";
    private int _where; // 0 all, 1 indoor, 2 outdoor, 3 favourites
    private List<Furniture.Item> _found = new();
    private (string, int, int) _foundFor = ("\u0001", -1, -1);
    private PropService.Prop? _chosen;
    private bool _gizmoBusy;
    private Matrix4x4? _tracking;
    private int _changedFrames;

    public MainWindow(Plugin plugin) : base("Proppy###proppy_main")
    {
        _plugin = plugin;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(420, 360), MaximumSize = new Vector2(2000, 2000) };
        Size = new Vector2(560, 640);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    private static void Tip(string text)
    {
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip(text);
    }

    public override void Draw()
    {
        var cfg = _plugin.Config;
        var props = _plugin.Props;
        if(props.Problem.Length > 0)
        {
            ImGui.TextColored(new Vector4(1f, 0.5f, 0.4f, 1f), props.Problem);
            return;
        }
        ImGui.TextWrapped("Housing items anywhere, only on your screen (nothing is sent to the server). They have their real collision: "
            + "walk, stand and jump on them. Others don't see them: on one, you look like you're standing in the air to them.");
        if(_plugin.Away)
        {
            ImGui.TextDisabled("Waiting for the zone to load...");
            return;
        }

        if(ImGui.BeginTabBar("###proppy_tabs"))
        {
            if(ImGui.BeginTabItem("Place###proppy_tab_place"))
            {
                DrawCatalog();
                ImGui.EndTabItem();
            }
            if(ImGui.BeginTabItem($"Placed here ({props.Props.Count})###proppy_tab_placed"))
            {
                DrawPlaced();
                ImGui.EndTabItem();
            }
            if(ImGui.BeginTabItem("Settings###proppy_tab_settings"))
            {
                DrawSettings();
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }

        // (a change kept a moment after it settles: not saved every frame of a drag)
        if(_changedFrames > 0 && --_changedFrames == 0)
            _plugin.SaveLayout();
    }

    private void Changed() => _changedFrames = 30;

    // ---- The items to place

    private void DrawCatalog()
    {
        var cfg = _plugin.Config;
        ImGui.SetNextItemWidth(-200 * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("###proppy_search", "Search housing items", ref _search, 64);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1);
        ImGui.Combo("###proppy_where", ref _where, "All\0Indoor\0Outdoor\0Favourites\0");
        if(_foundFor != (_search, _where, cfg.Favourites.Count))
        {
            _foundFor = (_search, _where, cfg.Favourites.Count);
            _found = _plugin.Furniture.All.Where(i =>
                    (_where != 1 || i.Indoors) && (_where != 2 || !i.Indoors) && (_where != 3 || cfg.Favourites.Contains(i.Path))
                    && (_search.Length == 0 || i.Name.Contains(_search, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }
        ImGui.TextDisabled($"{_found.Count} items. Click one to place it in front of you; right-click: favourite.");
        float row = 36f * ImGuiHelpers.GlobalScale;
        using var child = Dalamud.Interface.Utility.Raii.ImRaii.Child("###proppy_list", new Vector2(-1, -1), true);
        var clipper = ImGui.ImGuiListClipper();
        clipper.Begin(_found.Count, row);
        while(clipper.Step())
            for(int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                var item = _found[i];
                ImGui.PushID(i);
                var start = ImGui.GetCursorPos();
                if(ImGui.Selectable("###pick", false, ImGuiSelectableFlags.None, new Vector2(0, row - 4f)))
                {
                    var (at, yaw) = _plugin.InFront();
                    var placed = _plugin.Props.Place(item.Path, item.Name, at, new Vector3(yaw, 0f, 0f), Vector3.One);
                    if(placed != null)
                    {
                        _chosen = placed;
                        Changed();
                    }
                }
                if(ImGui.IsItemClicked(ImGuiMouseButton.Right))
                {
                    if(!cfg.Favourites.Remove(item.Path))
                        cfg.Favourites.Add(item.Path);
                    _plugin.SaveConfig();
                }
                Tip($"{item.Name}\n{(item.Indoors ? "Indoor" : "Outdoor")} item #{item.ModelKey}\nClick: place it in front of you.");
                ImGui.SetCursorPos(start);
                var icon = item.Icon != 0 && Plugin.Textures.TryGetFromGameIcon(new GameIconLookup(item.Icon), out var tex) && tex != null
                    ? tex.GetWrapOrDefault() : null;
                if(icon != null)
                    ImGui.Image(icon.Handle, new Vector2(row - 6f, row - 6f));
                else
                    ImGui.Dummy(new Vector2(row - 6f, row - 6f));
                ImGui.SameLine();
                ImGui.TextUnformatted((cfg.Favourites.Contains(item.Path) ? "* " : "") + item.Name);
                ImGui.SameLine();
                ImGui.TextDisabled(item.Indoors ? "(indoor)" : "(outdoor)");
                ImGui.PopID();
            }
        clipper.End();
        clipper.Destroy();
    }

    // ---- The items placed here

    private void DrawPlaced()
    {
        var props = _plugin.Props;
        if(props.Props.Count == 0)
        {
            ImGui.TextDisabled("Nothing placed in this zone yet (the Place tab).");
            return;
        }
        if(_chosen != null && !props.Props.Contains(_chosen))
            _chosen = null;
        using(var list = Dalamud.Interface.Utility.Raii.ImRaii.Child("###proppy_placed", new Vector2(-1, 150 * ImGuiHelpers.GlobalScale), true))
            for(int i = 0; i < props.Props.Count; i++)
            {
                var p = props.Props[i];
                if(ImGui.Selectable($"{p.Name}###placed_{i}", ReferenceEquals(p, _chosen)))
                    _chosen = p;
            }
        if(ImGui.Button("Remove all###proppy_clear") && ImGui.GetIO().KeyCtrl)
        {
            props.RemoveAll();
            _chosen = null;
            _plugin.SaveLayout();
        }
        Tip("Hold Ctrl and click: every item in this zone goes (and its saved layout).");
        ImGui.SameLine();
        if(ImGui.Button("Load the saved layout###proppy_restore"))
        {
            _chosen = null;
            _plugin.Restore();
        }
        Tip("This zone's items as last kept (anything placed since and not kept is lost).");

        var c = _chosen;
        if(c == null)
        {
            ImGui.TextDisabled("Pick an item above to move, turn or resize it.");
            return;
        }
        ImGui.Separator();
        ImGui.TextUnformatted(c.Name);
        bool changed = false;
        var pos = c.Position;
        ImGui.SetNextItemWidth(-90 * ImGuiHelpers.GlobalScale);
        if(ImGui.DragFloat3("Position###proppy_pos", ref pos, 0.01f))
        {
            c.Position = pos;
            changed = true;
        }
        var rot = c.Rotation;
        ImGui.SetNextItemWidth(-90 * ImGuiHelpers.GlobalScale);
        if(ImGui.DragFloat3("Turn###proppy_rot", ref rot, 0.5f, -360f, 360f, "%.1f°"))
        {
            c.Rotation = rot;
            changed = true;
        }
        Tip("Degrees: round (yaw), tip (pitch), roll.");
        var scale = c.Scale;
        ImGui.SetNextItemWidth(-90 * ImGuiHelpers.GlobalScale);
        if(ImGui.DragFloat3("Size###proppy_scale", ref scale, 0.01f, 0.05f, 20f))
        {
            c.Scale = Vector3.Clamp(scale, new Vector3(0.05f), new Vector3(20f));
            changed = true;
        }
        float uniform = (c.Scale.X + c.Scale.Y + c.Scale.Z) / 3f;
        ImGui.SetNextItemWidth(-90 * ImGuiHelpers.GlobalScale);
        if(ImGui.DragFloat("Size (all)###proppy_scale_all", ref uniform, 0.01f, 0.05f, 20f))
        {
            c.Scale = new Vector3(Math.Clamp(uniform, 0.05f, 20f));
            changed = true;
        }
        if(ImGui.Button("To me###proppy_tome"))
        {
            var (at, yaw) = _plugin.InFront();
            c.Position = at;
            c.Rotation = new Vector3(yaw, c.Rotation.Y, c.Rotation.Z);
            changed = true;
        }
        Tip("In front of you, facing the way you face.");
        ImGui.SameLine();
        if(ImGui.Button("Under me###proppy_under"))
        {
            var me = Plugin.Objects.LocalPlayer;
            if(me != null)
            {
                c.Position = me.Position;
                changed = true;
            }
        }
        Tip("Right where you stand (then nudge it down with Position's middle number so you stand on top).");
        ImGui.SameLine();
        if(ImGui.Button("Upright###proppy_upright"))
        {
            c.Rotation = new Vector3(c.Rotation.X, 0f, 0f);
            c.Scale = Vector3.One;
            changed = true;
        }
        Tip("No tip or roll, normal size.");
        ImGui.SameLine();
        if(ImGui.Button("Copy###proppy_copy"))
        {
            var copy = _plugin.Props.Place(c.Path, c.Name, c.Position + new Vector3(0.5f, 0f, 0.5f), c.Rotation, c.Scale, c.Collision);
            if(copy != null)
            {
                _chosen = copy;
                Changed();
            }
        }
        ImGui.SameLine();
        if(ImGui.Button("Remove###proppy_remove"))
        {
            _plugin.Props.Remove(c);
            _chosen = null;
            Changed();
            return;
        }
        DrawSeat(c);
        bool collide = c.Collision;
        if(ImGui.Checkbox("Solid (collision)###proppy_collide", ref collide))
        {
            c.Collision = collide;
            c.Settle = 120;
            changed = true;
        }
        Tip("On: you walk, stand and jump on it. Off: you pass through it.");
        // Nudges.
        float step = ImGui.GetIO().KeyShift ? 0.5f : 0.05f;
        ImGui.TextDisabled("Nudge (Shift: bigger steps):");
        ImGui.SameLine();
        foreach(var (label, d) in new[] { ("Up", Vector3.UnitY), ("Down", -Vector3.UnitY) })
        {
            if(ImGui.SmallButton($"{label}###proppy_n_{label}"))
            {
                c.Position += d * step;
                changed = true;
            }
            ImGui.SameLine();
        }
        if(ImGui.SmallButton("Turn 15°###proppy_n_turn"))
        {
            c.Rotation = c.Rotation with { X = c.Rotation.X + 15f };
            changed = true;
        }
        if(changed)
        {
            c.Dirty = true;
            Changed();
        }
    }

    // ---- Sitting / lying on the chosen item (GPose only)

    private void DrawSeat(PropService.Prop c)
    {
        var seat = _plugin.Seat;
        bool gpose = Plugin.ClientState.IsGPosing;
        using(Dalamud.Interface.Utility.Raii.ImRaii.Disabled(!gpose))
        {
            if(ImGui.Button("Sit here###proppy_sit"))
                seat.SitOn(c, lie: false);
            ImGui.SameLine();
            if(ImGui.Button("Lie here###proppy_lie"))
                seat.SitOn(c, lie: true);
        }
        if(ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(gpose ? "The GPose target (or your own character) sits on this chair / lies on this bed, with the game's own animation."
                : "In GPose only: outside it, moving your character onto an item only on your screen would be a teleport the server sees.");
        if(seat.On == c)
        {
            ImGui.SameLine();
            if(ImGui.Button("Stand up###proppy_stand"))
                seat.StandUp();
            bool lie = seat.Lying;
            var offset = lie ? c.LieOffset : c.SitOffset;
            float turn = lie ? c.LieTurn : c.SitTurn;
            bool changed = false;
            ImGui.SetNextItemWidth(-140 * ImGuiHelpers.GlobalScale);
            if(ImGui.DragFloat3(lie ? "Lying spot###proppy_lieoff" : "Seat spot###proppy_sitoff", ref offset, 0.005f, -3f, 3f, "%.3f"))
                changed = true;
            Tip("Across, up, forward on the item (yalms, before its size): where the character sits / lies.");
            ImGui.SetNextItemWidth(-140 * ImGuiHelpers.GlobalScale);
            if(ImGui.DragFloat("Facing###proppy_turn", ref turn, 0.5f, -180f, 180f, "%.0f°"))
                changed = true;
            Tip("Which way the character faces, from the way the item faces.");
            if(changed)
            {
                if(lie)
                {
                    c.LieOffset = offset;
                    c.LieTurn = turn;
                }
                else
                {
                    c.SitOffset = offset;
                    c.SitTurn = turn;
                }
                Changed();
            }
        }
        if(seat.Status.Length > 0)
            ImGui.TextDisabled(seat.Status);
    }

    // ---- Settings

    private void DrawSettings()
    {
        var cfg = _plugin.Config;
        bool restore = cfg.AutoRestore;
        if(ImGui.Checkbox("A zone's items come back when you enter it###proppy_auto", ref restore))
        {
            cfg.AutoRestore = restore;
            _plugin.SaveConfig();
        }
        bool gizmo = cfg.ShowGizmo;
        if(ImGui.Checkbox("Gizmo on the chosen item###proppy_gizmo", ref gizmo))
        {
            cfg.ShowGizmo = gizmo;
            _plugin.SaveConfig();
        }
        if(cfg.ShowGizmo)
        {
            ImGui.SameLine();
            int mode = cfg.GizmoMode;
            if(ImGui.RadioButton("Move###proppy_gm0", mode == 0)) mode = 0;
            ImGui.SameLine();
            if(ImGui.RadioButton("Turn###proppy_gm1", mode == 1)) mode = 1;
            ImGui.SameLine();
            if(ImGui.RadioButton("Size###proppy_gm2", mode == 2)) mode = 2;
            if(mode != cfg.GizmoMode)
            {
                cfg.GizmoMode = mode;
                _plugin.SaveConfig();
            }
            bool world = cfg.GizmoWorldAxes;
            if(ImGui.Checkbox("World axes###proppy_gworld", ref world))
            {
                cfg.GizmoWorldAxes = world;
                _plugin.SaveConfig();
            }
        }
        float distance = cfg.PlaceDistance;
        ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
        if(ImGui.SliderFloat("Place this far in front###proppy_dist", ref distance, 0f, 15f, "%.1f yalms"))
        {
            cfg.PlaceDistance = distance;
            _plugin.SaveConfig();
        }
        ImGui.Spacing();
        int zones = cfg.Layouts.Count, items = cfg.Layouts.Values.Sum(l => l.Count);
        ImGui.TextDisabled($"Kept: {items} items in {zones} zones. This zone: #{_plugin.Zone}.");
    }

    // ---- The gizmo on the chosen item (drawn every frame, the window open or not)

    public void DrawGizmo()
    {
        var c = _chosen;
        var cfg = _plugin.Config;
        if(c == null || !cfg.ShowGizmo || !IsOpen || _plugin.Away || !_plugin.Props.Props.Contains(c) || !TryCamera(out var view, out var projection))
        {
            _gizmoBusy = false;
            _tracking = null;
            return;
        }
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.Pos);
        ImGui.SetNextWindowSize(viewport.Size);
        var flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoBringToFrontOnFocus
            | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoDocking;
        if(!_gizmoBusy)
            flags |= ImGuiWindowFlags.NoInputs;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        try
        {
            if(!ImGui.Begin("##proppy_gizmo", flags))
                return;
            var matrix = _tracking ?? (Matrix4x4.CreateScale(c.Scale) * Matrix4x4.CreateFromQuaternion(c.Quaternion) * Matrix4x4.CreateTranslation(c.Position));
            var operation = cfg.GizmoMode switch { 1 => ImGuizmoOperation.Rotate, 2 => ImGuizmoOperation.Scale, _ => ImGuizmoOperation.Translate };
            var mode = cfg.GizmoWorldAxes && cfg.GizmoMode != 2 ? ImGuizmoMode.World : ImGuizmoMode.Local;
            ImGuizmo.BeginFrame();
            ImGuizmo.SetID(0x50524F50);
            ImGuizmo.SetOrthographic(false);
            ImGuizmo.SetDrawlist();
            ImGuizmo.SetRect(viewport.Pos.X, viewport.Pos.Y, viewport.Size.X, viewport.Size.Y);
            ImGuizmo.Enable(true);
            if(ImGuizmo.Manipulate(ref view, ref projection, operation, mode, ref matrix))
            {
                _tracking = matrix;
                if(Matrix4x4.Decompose(matrix, out var scale, out var rotation, out var position) && float.IsFinite(position.X))
                {
                    c.Position = position;
                    c.Scale = Vector3.Clamp(scale, new Vector3(0.05f), new Vector3(20f));
                    c.Rotation = Degrees(Quaternion.Normalize(rotation));
                    c.Dirty = true;
                    Changed();
                }
            }
            bool usingIt = ImGuizmo.IsUsing(), over = ImGuizmo.IsOver();
            if(!usingIt)
                _tracking = null;
            _gizmoBusy = usingIt || over;
            ImGuizmo.SetID(0);
        }
        finally
        {
            ImGui.End();
            ImGui.PopStyleVar(2);
        }
        if(_gizmoBusy)
            ImGui.SetNextFrameWantCaptureMouse(true);
    }

    // Yaw, pitch, roll (degrees) of a rotation made as CreateFromYawPitchRoll.
    private static Vector3 Degrees(Quaternion q)
    {
        float sinp = 2f * (q.W * q.X - q.Y * q.Z);
        float pitch = MathF.Abs(sinp) >= 1f ? MathF.CopySign(MathF.PI / 2f, sinp) : MathF.Asin(sinp);
        float yaw = MathF.Atan2(2f * (q.W * q.Y + q.X * q.Z), 1f - 2f * (q.X * q.X + q.Y * q.Y));
        float roll = MathF.Atan2(2f * (q.W * q.Z + q.X * q.Y), 1f - 2f * (q.X * q.X + q.Z * q.Z));
        const float d = 180f / MathF.PI;
        return new Vector3(yaw * d, pitch * d, roll * d);
    }

    // The game camera's view and projection (as ImGuizmo wants them).
    private static bool TryCamera(out Matrix4x4 view, out Matrix4x4 projection)
    {
        view = Matrix4x4.Identity;
        projection = Matrix4x4.Identity;
        var manager = SceneCameraManager.Instance();
        if(manager == null)
            return false;
        var camera = manager->CurrentCamera;
        if(camera == null || camera->RenderCamera == null)
            return false;
        view = camera->ViewMatrix;
        view.M44 = 1f;
        var render = camera->RenderCamera;
        projection = render->ProjectionMatrix;
        float near = render->NearPlane, far = render->FarPlane;
        if(far - near > 1e-3f)
        {
            projection.M33 = -(far + near) / (far - near);
            projection.M43 = -(2f * far * near) / (far - near);
        }
        return true;
    }
}
