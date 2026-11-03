using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Limber.Rig;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Limber.UI;

/// <summary>
/// The Face Posing page: every face bone moved, turned and scaled along the face's own axes. A face map (the
/// character's own face bones, seen from the front, drawn by Limber), the bones by region, the chosen bone's sliders,
/// and dots on the face in the scene (click to pick, drag to move).
/// </summary>
public sealed class FacePosingTab
{
    private readonly Selection _selection;
    private readonly FacePosingService _face;
    private readonly RigSettings _config;
    private string? _bone;
    private int _lastDrawn = -10;

    // Regions: the name starts that belong to each (longest first wins when it matters).
    private static readonly (string Name, string[] Starts, Vector4 Colour)[] Regions =
    [
        ("Brows", ["j_f_mmayu", "j_f_mayu", "j_f_miken"], new(0.95f, 0.75f, 0.35f, 1f)),
        ("Eyes", ["j_f_eyepuru", "j_f_eye"], new(0.45f, 0.8f, 1f, 1f)),
        ("Eyelids", ["j_f_umab", "j_f_dmab", "j_f_mab"], new(0.55f, 0.65f, 1f, 1f)),
        ("Nose", ["j_f_hana"], new(0.85f, 0.85f, 0.85f, 1f)),
        ("Cheeks", ["j_f_shoho", "j_f_hoho"], new(1f, 0.6f, 0.65f, 1f)),
        ("Lips", ["j_f_uslip", "j_f_umlip", "j_f_ulip", "j_f_dslip", "j_f_dmlip", "j_f_dlip", "j_f_lip"], new(1f, 0.45f, 0.5f, 1f)),
        ("Jaw and teeth", ["j_f_dago", "j_f_ago", "j_f_haguki"], new(0.7f, 0.9f, 0.6f, 1f)),
        ("Tongue", ["j_f_bero"], new(1f, 0.5f, 0.8f, 1f)),
    ];
    private static readonly Vector4 OtherColour = new(0.75f, 0.75f, 0.75f, 1f);

    // Lines drawn between a region's bones on the map (outlines): brows, upper / lower lids, upper / lower lips.
    private static readonly string[][] Outlines =
    [
        ["j_f_mmayu", "j_f_mayu"],
        ["j_f_umab"],
        ["j_f_dmab"],
        ["j_f_uslip", "j_f_umlip", "j_f_ulip", "j_f_lip"],
        ["j_f_dslip", "j_f_dmlip", "j_f_dlip", "j_f_lip"],
    ];

    public FacePosingTab(Selection selection, FacePosingService face, RigSettings config)
    {
        _selection = selection;
        _face = face;
        _config = config;
    }

    /// <summary>The page was drawn this frame or the last (the scene dots show while it's open).</summary>
    public bool Showing => ImGui.GetFrameCount() - _lastDrawn <= 1;

    private Dictionary<string, FacePoseSettings> All => _config.FacePoses ??= new();

    private static int RegionOf(string bone)
    {
        int best = -1, bestLength = 0;
        for(int r = 0; r < Regions.Length; r++)
            foreach(var start in Regions[r].Starts)
                if(bone.StartsWith(start, StringComparison.Ordinal) && start.Length > bestLength)
                {
                    best = r;
                    bestLength = start.Length;
                }
        return best;
    }

    private static Vector4 ColourOf(string bone)
    {
        int r = RegionOf(bone);
        return r >= 0 ? Regions[r].Colour : OtherColour;
    }

    public void Draw()
    {
        _lastDrawn = ImGui.GetFrameCount();
        var actor = _selection.Actor;
        if(!actor.IsSet)
        {
            _face.Watch(0, 0);
            ImGui.TextDisabled("Choose a character at the top of the window.");
            return;
        }
        string name = actor.Name ?? "";
        if(name.Length == 0)
        {
            ImGui.TextDisabled("This character has no name to keep the face pose under.");
            return;
        }
        _face.Watch(actor.Address, actor.ObjectIndex);
        All.TryGetValue(name, out var pose);

        bool on = pose?.Enabled ?? true;
        if(ImGui.Checkbox("Face posing on###face_on", ref on))
        {
            Pose(name).Enabled = on;
            _config.Save();
        }
        ImGui.SameLine();
        bool mirror = pose?.Mirror ?? true;
        if(ImGui.Checkbox("Mirror edits###face_mirror", ref mirror))
        {
            Pose(name).Mirror = mirror;
            _config.Save();
        }
        Tooltip("Changing a left or right bone changes the one on the other side the mirrored way.");
        ImGui.SameLine();
        bool dots = _config.ShowFaceBones;
        if(ImGui.Checkbox("Face dots in the scene###face_dots", ref dots))
        {
            _config.ShowFaceBones = dots;
            _config.Save();
        }
        Tooltip("Dots on the character's face bones: click one to pick it, drag it to move it.\n"
            + "Tip: Freeze motion first, so the animation's own expression doesn't fight you.");
        ImGui.SameLine();
        using(ImRaii.Disabled(pose == null || pose.Bones.Count == 0))
        {
            if(ImGui.Button("Reset face###face_reset_all") && pose != null)
            {
                pose.Bones.Clear();
                _config.Save();
            }
        }
        Tooltip("Takes every face bone's offset off (Undo at the top brings it back).");
        ImGui.Separator();

        var view = _face.View;
        if(view == null || view.Address != actor.Address)
        {
            ImGui.TextDisabled("This character's face isn't available right now (GPose, and Limber on?).");
            return;
        }

        float left = MathF.Min(340f * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().X * 0.5f);
        using(var map = ImRaii.Child("###face_map", new Vector2(left, -1), true))
        {
            if(map.Success)
                DrawMap(name, view);
        }
        ImGui.SameLine();
        using(var side = ImRaii.Child("###face_side", new Vector2(-1, -1), false))
        {
            if(side.Success)
            {
                DrawEditor(name);
                ImGui.Spacing();
                DrawBoneList(name, view);
            }
        }
    }

    private FacePoseSettings Pose(string name)
    {
        if(!All.TryGetValue(name, out var pose))
            All[name] = pose = new FacePoseSettings();
        return pose;
    }

    private FaceBoneOffset Offset(string name, string bone)
    {
        var pose = Pose(name);
        if(!pose.Bones.TryGetValue(bone, out var o))
            pose.Bones[bone] = o = new FaceBoneOffset();
        return o;
    }

    // A change to one bone, and (Mirror edits) its partner the mirrored way.
    private void Change(string name, string bone, Func<FaceBoneOffset, FaceBoneOffset> change)
    {
        var pose = Pose(name);
        var o = Offset(name, bone);
        var made = change(o);
        pose.Bones[bone] = made;
        if(pose.Mirror && FacePosingService.OtherSide(bone) is { } other)
            pose.Bones[other] = new FaceBoneOffset
            {
                Move = new Vector3(-made.Move.X, made.Move.Y, made.Move.Z),
                Turn = new Vector3(made.Turn.X, -made.Turn.Y, -made.Turn.Z),
                Scale = made.Scale,
            };
    }

    // ---- The face map: the face's bones seen from the front (across, up), drawn here

    private Vector2 _mapDragStart;
    private Vector3 _mapDragMove;
    private string? _mapDragging;

    private void DrawMap(string name, FacePosingService.FaceView view)
    {
        var draw = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var size = ImGui.GetContentRegionAvail();
        size.Y = MathF.Max(size.Y, 200f * ImGuiHelpers.GlobalScale);
        var centre = origin + size * 0.5f;
        // Model units -> pixels: the face's width fills about 70% of the panel.
        float pixels = MathF.Min(size.X, size.Y) * 0.7f / MathF.Max(0.02f, view.Width);

        Vector2 ToMap(Vector3 model)
        {
            var d = model - view.Middle;
            return centre + new Vector2(-Vector3.Dot(d, view.Across), -Vector3.Dot(d, view.Up)) * pixels; // (facing you: its left on your right)
        }

        // A soft face shape behind (an oval round the bones), so the dots read as a face.
        float ovalW = view.Width * 0.62f * pixels, ovalH = view.Width * 0.85f * pixels;
        uint fill = ImGui.GetColorU32(new Vector4(0.22f, 0.2f, 0.24f, 1f)), edge = ImGui.GetColorU32(new Vector4(0.4f, 0.38f, 0.45f, 1f));
        for(float y = -ovalH; y <= ovalH; y += 2f)
        {
            float v = y / ovalH;
            float half = ovalW * MathF.Sqrt(MathF.Max(0f, 1f - v * v)) * (v > 0f ? 1f - 0.08f * v : 1f); // a little narrower toward the chin
            draw.AddLine(centre + new Vector2(-half, y), centre + new Vector2(half, y), fill, 2.5f);
        }
        Vector2 Edge(float a)
        {
            float c = MathF.Cos(a), sn = MathF.Sin(a);
            return centre + new Vector2(c * ovalW * (sn > 0f ? 1f - 0.08f * sn : 1f), sn * ovalH);
        }
        for(int i = 0; i < 48; i++)
            draw.AddLine(Edge(i / 48f * MathF.PI * 2f), Edge((i + 1) / 48f * MathF.PI * 2f), edge, 1.5f);

        var places = view.Bones.Select(b => (b.Name, At: ToMap(b.Model))).ToArray();
        var at = places.ToDictionary(p => p.Name, p => p.At);

        // Outlines: each side's row, from the middle out (sorted across).
        foreach(var starts in Outlines)
            foreach(var side in new[] { "_l", "_r", "" })
            {
                var row = places.Where(p => starts.Any(s => p.Name.StartsWith(s, StringComparison.Ordinal))
                                            && (side.Length == 0 ? !p.Name.EndsWith("_l") && !p.Name.EndsWith("_r") : p.Name.EndsWith(side)))
                                .OrderBy(p => p.At.X).Select(p => p.At).ToArray();
                for(int i = 1; i < row.Length; i++)
                    draw.AddLine(row[i - 1], row[i], ImGui.GetColorU32(ColourOf(starts[0]) * new Vector4(1, 1, 1, 0.45f)), 1.5f);
            }

        // The dots: click to pick, drag to move (across / up; Alt + drag: forward / back).
        var mouse = ImGui.GetMousePos();
        float r = 5f * ImGuiHelpers.GlobalScale;
        string? hovered = null;
        float best = r * 1.8f;
        var pose = All.TryGetValue(name, out var p0) ? p0 : null;
        foreach(var (bone, pos) in places)
        {
            float d = Vector2.Distance(mouse, pos);
            if(d < best && ImGui.IsWindowHovered())
            {
                best = d;
                hovered = bone;
            }
        }
        foreach(var (bone, pos) in places)
        {
            bool chosen = bone == _bone;
            bool moved = pose != null && pose.Bones.TryGetValue(bone, out var o) && !o.IsZero;
            var colour = ColourOf(bone);
            float rr = chosen ? r * 1.4f : bone == hovered ? r * 1.2f : r;
            draw.AddCircleFilled(pos, rr, ImGui.GetColorU32(colour * new Vector4(1, 1, 1, moved ? 1f : 0.7f)));
            if(moved)
                draw.AddCircle(pos, rr + 2f, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.9f)), 0, 1.5f);
            if(chosen)
                draw.AddCircle(pos, rr + 4f, ImGui.GetColorU32(new Vector4(1f, 0.95f, 0.4f, 1f)), 0, 2f);
        }

        // Clicks and drags on the map.
        ImGui.InvisibleButton("###face_map_area", size);
        if(ImGui.IsItemActivated() && hovered != null)
        {
            _bone = hovered;
            _mapDragging = hovered;
            _mapDragStart = mouse;
            _mapDragMove = pose != null && pose.Bones.TryGetValue(hovered, out var o) ? o.Move : Vector3.Zero;
        }
        if(_mapDragging != null && ImGui.IsItemActive())
        {
            var delta = (mouse - _mapDragStart) / pixels * 1000f; // mm
            bool depth = ImGui.GetIO().KeyAlt;
            var move = depth
                ? _mapDragMove + new Vector3(0f, 0f, -delta.Y)
                : _mapDragMove + new Vector3(-delta.X, -delta.Y, 0f);
            string bone = _mapDragging;
            Change(name, bone, o => { var c = o.Copy(); c.Move = move; return c; });
        }
        if(_mapDragging != null && ImGui.IsItemDeactivated())
        {
            _mapDragging = null;
            _config.Save();
        }
        if(hovered != null && _mapDragging == null)
            ImGui.SetTooltip(HandleCatalog.Label(hovered) + "\nDrag: move across / up.  Alt + drag: forward / back.");
        draw.AddText(origin + new Vector2(6, 4), ImGui.GetColorU32(new Vector4(0.7f, 0.7f, 0.75f, 1f)), "Seen from the front");
    }

    // ---- The chosen bone's sliders

    private void DrawEditor(string name)
    {
        if(_bone == null)
        {
            ImGui.TextDisabled("Pick a bone on the face map, in the list, or on the face in the scene.");
            return;
        }
        ImGui.TextColored(ColourOf(_bone), HandleCatalog.Label(_bone));
        ImGui.SameLine();
        ImGui.TextDisabled(_bone);
        All.TryGetValue(name, out var pose);
        var o = pose != null && pose.Bones.TryGetValue(_bone, out var have) ? have.Copy() : new FaceBoneOffset();
        bool changed = false;

        var move = o.Move;
        ImGui.SetNextItemWidth(-90 * ImGuiHelpers.GlobalScale);
        changed |= ImGui.DragFloat3("Move (mm)###face_move", ref move, 0.05f, -30f, 30f, "%.2f");
        Tooltip("Across (+ = the character's left), up, forward (out of the face).");
        var turn = o.Turn;
        ImGui.SetNextItemWidth(-90 * ImGuiHelpers.GlobalScale);
        changed |= ImGui.DragFloat3("Turn (°)###face_turn", ref turn, 0.25f, -90f, 90f, "%.1f");
        Tooltip("About the face's axes: tip (nod), turn (shake), tilt.");
        float scale = o.Scale;
        ImGui.SetNextItemWidth(-90 * ImGuiHelpers.GlobalScale);
        changed |= ImGui.DragFloat("Scale###face_scale", ref scale, 0.005f, 0.05f, 5f, "%.3f");
        if(changed)
        {
            var bone = _bone;
            Change(name, bone, _ => new FaceBoneOffset { Move = move, Turn = turn, Scale = scale });
            if(!ImGui.IsAnyItemActive())
                _config.Save();
        }
        if(ImGui.IsItemDeactivatedAfterEdit())
            _config.Save();
        if(ImGui.Button("Reset this bone###face_reset_bone") && pose != null)
        {
            pose.Bones.Remove(_bone);
            if(pose.Mirror && FacePosingService.OtherSide(_bone) is { } other)
                pose.Bones.Remove(other);
            _config.Save();
        }
    }

    // ---- The bones by region

    private void DrawBoneList(string name, FacePosingService.FaceView view)
    {
        All.TryGetValue(name, out var pose);
        var names = view.Bones.Select(b => b.Name).ToList();
        for(int r = -1; r < Regions.Length; r++)
        {
            int region = r;
            var inRegion = names.Where(b => RegionOf(b) == region).OrderBy(b => b, StringComparer.Ordinal).ToList();
            if(inRegion.Count == 0)
                continue;
            string title = region >= 0 ? Regions[region].Name : "Other";
            int moved = pose == null ? 0 : inRegion.Count(b => pose.Bones.TryGetValue(b, out var o) && !o.IsZero);
            if(!ImGui.CollapsingHeader($"{title} ({inRegion.Count}{(moved > 0 ? $", {moved} moved" : "")})###face_region_{title}"))
                continue;
            foreach(var bone in inRegion)
            {
                bool isMoved = pose != null && pose.Bones.TryGetValue(bone, out var o) && !o.IsZero;
                using(ImRaii.PushColor(ImGuiCol.Text, ColourOf(bone), isMoved))
                    if(ImGui.Selectable($"{(isMoved ? "● " : "")}{HandleCatalog.Label(bone)}  ({bone})###face_pick_{bone}", _bone == bone))
                        _bone = bone;
            }
        }
    }

    // ---- Dots on the face in the scene (click to pick, drag to move)

    private sealed class SceneDrag
    {
        public required string Bone;
        public required string Character;
        public Vector2 StartMouse;
        public Vector3 StartMove;
        public float InvA, InvB, InvC, InvD; // screen pixels -> (across, up) mm
    }

    private SceneDrag? _sceneDrag;
    private bool _sceneWantInput;

    /// <summary>Each frame from the plugin, while the page is open and the face dots are on.</summary>
    public void DrawScene()
    {
        var actor = _selection.Actor;
        var view = _face.View;
        if(!Showing || !_config.ShowFaceBones || !actor.IsSet || view == null || view.Address != actor.Address || (actor.Name ?? "").Length == 0)
        {
            _sceneDrag = null;
            _sceneWantInput = false;
            return;
        }
        string name = actor.Name!;
        var gui = Plugin.GameGui;
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.Pos);
        ImGui.SetNextWindowSize(viewport.Size);
        var flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoBringToFrontOnFocus
            | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoDocking;
        if(!_sceneWantInput)
            flags |= ImGuiWindowFlags.NoInputs;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        try
        {
            if(!ImGui.Begin("##limber_face_overlay", flags))
                return;
            var draw = ImGui.GetWindowDrawList();
            var mouse = ImGui.GetMousePos();
            float r = 4f * ImGuiHelpers.GlobalScale;
            string? hovered = null;
            float best = r * 2.2f;
            var pose = All.TryGetValue(name, out var p0) ? p0 : null;
            var screen = new List<(string Bone, Vector2 At)>();
            foreach(var b in view.Bones)
                if(gui.WorldToScreen(b.World, out var s, out bool inView) && inView)
                {
                    screen.Add((b.Name, s));
                    float d = Vector2.Distance(mouse, s);
                    if(d < best)
                    {
                        best = d;
                        hovered = b.Name;
                    }
                }
            foreach(var (bone, s) in screen)
            {
                bool moved = pose != null && pose.Bones.TryGetValue(bone, out var o) && !o.IsZero;
                float rr = bone == _bone ? r * 1.5f : bone == hovered ? r * 1.25f : r;
                draw.AddCircleFilled(s, rr, ImGui.GetColorU32(ColourOf(bone) * new Vector4(1, 1, 1, 0.85f)));
                draw.AddCircle(s, rr, ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.6f)), 0, 1f);
                if(moved)
                    draw.AddCircle(s, rr + 2f, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.9f)), 0, 1.2f);
                if(bone == _bone)
                    draw.AddCircle(s, rr + 4f, ImGui.GetColorU32(new Vector4(1f, 0.95f, 0.4f, 1f)), 0, 2f);
            }

            // Start a drag: the screen directions of the face's across and up axes give pixels -> millimetres.
            if(_sceneDrag == null && hovered != null && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                var b = Array.Find(view.Bones, x => x.Name == hovered);
                var across = Vector3.Transform(view.Across, view.ToWorld) * view.WorldScale * 0.001f; // 1 mm in the world
                var up = Vector3.Transform(view.Up, view.ToWorld) * view.WorldScale * 0.001f;
                if(gui.WorldToScreen(b.World, out var o0, out _) && gui.WorldToScreen(b.World + across, out var oa, out _) && gui.WorldToScreen(b.World + up, out var ou, out _))
                {
                    var ax = oa - o0;
                    var uy = ou - o0;
                    float det = ax.X * uy.Y - ax.Y * uy.X;
                    _bone = hovered;
                    if(MathF.Abs(det) > 1e-6f)
                        _sceneDrag = new SceneDrag
                        {
                            Bone = hovered,
                            Character = name,
                            StartMouse = mouse,
                            StartMove = pose != null && pose.Bones.TryGetValue(hovered, out var have) ? have.Move : Vector3.Zero,
                            InvA = uy.Y / det, InvB = -uy.X / det, InvC = -ax.Y / det, InvD = ax.X / det,
                        };
                }
            }
            if(_sceneDrag is { } drag)
            {
                if(ImGui.IsMouseDown(ImGuiMouseButton.Left) && drag.Character == name)
                {
                    var d = mouse - drag.StartMouse;
                    float mx = drag.InvA * d.X + drag.InvB * d.Y, my = drag.InvC * d.X + drag.InvD * d.Y;
                    var move = drag.StartMove + new Vector3(mx, my, 0f);
                    Change(name, drag.Bone, o => { var c = o.Copy(); c.Move = move; return c; });
                }
                else
                {
                    _sceneDrag = null;
                    _config.Save();
                }
            }
            if(hovered != null && _sceneDrag == null)
                ImGui.SetTooltip(HandleCatalog.Label(hovered));
            _sceneWantInput = hovered != null || _sceneDrag != null;
        }
        finally
        {
            ImGui.End();
            ImGui.PopStyleVar(2);
        }
        if(_sceneWantInput)
            ImGui.SetNextFrameWantCaptureMouse(true);
    }

    private static void Tooltip(string text)
    {
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip(text);
    }
}
