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
/// The "Renderer" tab: a live, turnable view of the selected character's whole skeleton (body, face, hair, tail and
/// any skeleton a mod adds, known to Limber or not), moving with its animation. Click a bone to pose it, attach to
/// it, or put a collider on it.
/// </summary>
public sealed class RendererTab
{
    private static readonly Vector4 Accent = new(0.6f, 0.9f, 1f, 1f);
    private static readonly uint[] PartColors =
    [
        0xFFE6E6E6, // body (ABGR)
        0xFFB48CFF, // face: pink
        0xFF50D2FF, // hair: yellow
        0xFFFFC850, // other: blue
        0xFF78FF78, // more: green
        0xFFFF78E6,
    ];
    private const uint HeldColor = 0xFF3C9CFF;     // orange
    private const uint SelectedColor = 0xFF00FFFF; // yellow

    private readonly RigService _rigs;
    private readonly Selection _selection;
    private readonly RigSettings _config;
    private readonly LimberTab _limber;
    public Limber.BodyRig.PropService? PropService { get; set; }

    // Camera
    private float _yaw = 0.5f, _pitch = 0.15f, _distance = 3.2f;
    private Vector2 _pan;
    private bool _followCharacter = true, _showNames, _showDots = true;
    private Vector3 _anchor;
    private bool _haveAnchor;
    private nint _anchorActor;
    private readonly HashSet<int> _hiddenParts = [];
    private string _search = string.Empty;
    private bool _dragged;

    public RendererTab(RigService rigs, Selection selection, RigSettings config, LimberTab limber)
    {
        _rigs = rigs;
        _selection = selection;
        _config = config;
        _limber = limber;
    }

    public void Draw()
    {
        var actor = _selection.Actor;
        if(!actor.IsSet)
        {
            ImGui.TextDisabled("Choose a character first.");
            return;
        }

        BoneSnapshot? snapshot;
        HashSet<string> held;
        lock(_rigs.Sync)
        {
            snapshot = _rigs.GetSnapshot(actor.Address);
            held = _rigs.GetRig(actor.Address)?.Goals.Where(g => g.Enabled).Select(g => g.Bone).ToHashSet() ?? [];
        }
        if(snapshot == null)
        {
            ImGui.TextDisabled("Waiting for the character's skeleton...");
            return;
        }

        bool adult = Limber.Koi.Safety.AdultAllowed(actor.Address);
        bool Shown(int i) => !_hiddenParts.Contains(snapshot.Parts[i]) && HandleCatalog.IsPickable(snapshot.Names[i], adult);

        if(_anchorActor != actor.Address)
        {
            _anchorActor = actor.Address;
            _haveAnchor = false;
        }

        DrawOptions(snapshot);

        // ---- Points in view space
        int count = snapshot.Count;
        var points = new Vector3[count];
        var inverse = Quaternion.Inverse(snapshot.RootRotation);
        float scale = snapshot.Scale > 1e-4f ? snapshot.Scale : 1f;
        if(!_haveAnchor)
        {
            _anchor = snapshot.RootPosition;
            _haveAnchor = true;
        }
        for(int i = 0; i < count; i++)
        {
            var world = snapshot.WorldPositions[i];
            points[i] = _followCharacter
                ? Vector3.Transform(world - snapshot.RootPosition, inverse) / scale
                : world - _anchor;
        }

        // Centre on the body's middle (between the lowest and highest body bones).
        var centre = Vector3.Zero;
        {
            float lo = float.MaxValue, hi = float.MinValue;
            var sum = Vector3.Zero;
            int n = 0;
            for(int i = 0; i < snapshot.Info.Count; i++)
            {
                lo = MathF.Min(lo, points[i].Y);
                hi = MathF.Max(hi, points[i].Y);
                sum += points[i];
                n++;
            }
            if(n > 0)
                centre = new Vector3(sum.X / n, (lo + hi) * 0.5f, sum.Z / n);
        }

        // ---- Canvas
        var avail = ImGui.GetContentRegionAvail();
        float height = MathF.Max(260 * ImGuiHelpers.GlobalScale, avail.Y - 120 * ImGuiHelpers.GlobalScale);
        var size = new Vector2(MathF.Max(avail.X, 100), height);
        var origin = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("###limber_renderer_canvas", size, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
        bool hovered = ImGui.IsItemHovered();
        bool active = ImGui.IsItemActive();
        var io = ImGui.GetIO();

        if(ImGui.IsItemActivated())
            _dragged = false;
        if(active && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 2f))
        {
            _yaw -= io.MouseDelta.X * 0.01f;
            _pitch = Math.Clamp(_pitch + io.MouseDelta.Y * 0.01f, -1.5f, 1.5f);
            _dragged = true;
        }
        if(active && (ImGui.IsMouseDragging(ImGuiMouseButton.Right, 2f) || ImGui.IsMouseDragging(ImGuiMouseButton.Middle, 2f)))
        {
            _pan += io.MouseDelta * (_distance / (size.Y * 0.9f));
            _dragged = true;
        }
        if(hovered && io.MouseWheel != 0)
            _distance = Math.Clamp(_distance * MathF.Pow(0.9f, io.MouseWheel), 0.15f, 30f);

        var drawList = ImGui.GetWindowDrawList();
        drawList.PushClipRect(origin, origin + size, true);
        drawList.AddRectFilled(origin, origin + size, 0xFF1E1A18);

        var view = Quaternion.CreateFromYawPitchRoll(_yaw, _pitch, 0f);
        var viewInverse = Quaternion.Inverse(view);
        float focal = size.Y * 1.2f;
        var screenCentre = origin + size * 0.5f;
        bool Project(Vector3 p, out Vector2 screen, out float depth)
        {
            var v = Vector3.Transform(p - centre, viewInverse);
            v.X += _pan.X;
            v.Y -= _pan.Y;
            depth = _distance - v.Z;
            screen = default;
            if(depth < 0.05f)
                return false;
            screen = screenCentre + new Vector2(v.X, -v.Y) * (focal / depth);
            return true;
        }

        DrawGrid(drawList, Project, centre);

        var screenPoints = new Vector2[count];
        var visible = new bool[count];
        for(int i = 0; i < count; i++)
            visible[i] = Shown(i) && Project(points[i], out screenPoints[i], out _);

        float thickness = 1.5f * ImGuiHelpers.GlobalScale;
        for(int i = 0; i < count; i++)
        {
            int parent = snapshot.Parents[i];
            if(!visible[i] || parent < 0 || parent >= count || !visible[parent])
                continue;
            drawList.AddLine(screenPoints[parent], screenPoints[i], PartColor(snapshot.Parts[i]) & 0xB0FFFFFF, thickness);
        }

        // Hover: the nearest dot under the mouse.
        int hover = -1;
        if(hovered)
        {
            float best = 8f * ImGuiHelpers.GlobalScale;
            best *= best;
            for(int i = 0; i < count; i++)
            {
                if(!visible[i])
                    continue;
                float d = Vector2.DistanceSquared(screenPoints[i], io.MousePos);
                if(d < best)
                {
                    best = d;
                    hover = i;
                }
            }
        }

        float dot = 2.5f * ImGuiHelpers.GlobalScale;
        var selected = _selection.Bone;
        for(int i = 0; i < count; i++)
        {
            if(!visible[i])
                continue;
            var name = snapshot.Names[i];
            bool isSelected = name == selected;
            bool isHeld = held.Contains(name);
            bool match = _search.Length > 0 && (name.Contains(_search, StringComparison.OrdinalIgnoreCase) || HandleCatalog.Label(name).Contains(_search, StringComparison.OrdinalIgnoreCase));
            if(!_showDots && !isSelected && !isHeld && !match && i != hover)
                continue;
            uint color = isSelected ? SelectedColor : isHeld ? HeldColor : match ? 0xFF00FF00 : PartColor(snapshot.Parts[i]);
            float r = isSelected || i == hover ? dot * 2f : isHeld || match ? dot * 1.6f : dot;
            drawList.AddCircleFilled(screenPoints[i], r, color);
            if(_showNames || isSelected || match)
                drawList.AddText(screenPoints[i] + new Vector2(5, -7) * ImGuiHelpers.GlobalScale, color, HandleCatalog.Label(name));
        }

        drawList.AddText(origin + new Vector2(6, 4), 0xFFA0A0A0, "Drag: turn   Right-drag: pan   Wheel: zoom   Click a bone: select");
        drawList.PopClipRect();

        if(hover >= 0)
        {
            var name = snapshot.Names[hover];
            var label = HandleCatalog.Label(name);
            ImGui.SetTooltip(label == name ? $"{name}\n{PartName(snapshot, snapshot.Parts[hover])}" : $"{label}\n{name}\n{PartName(snapshot, snapshot.Parts[hover])}");
            if(ImGui.IsItemDeactivated() && !_dragged && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
                _selection.Bone = name;
        }

        DrawSelectedActions(actor, snapshot, held);
    }

    private void DrawOptions(BoneSnapshot snapshot)
    {
        ImGui.Checkbox("Follow character", ref _followCharacter);
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("Off: the view stays put and the character walks and turns in it.");
        ImGui.SameLine();
        ImGui.Checkbox("Names", ref _showNames);
        ImGui.SameLine();
        ImGui.Checkbox("Dots", ref _showDots);
        ImGui.SameLine();
        if(ImGui.SmallButton("Reset view"))
        {
            _yaw = 0.5f;
            _pitch = 0.15f;
            _distance = 3.2f;
            _pan = Vector2.Zero;
            _haveAnchor = false;
        }

        // Parts of the skeleton, each can be hidden.
        var parts = snapshot.Parts.Distinct().OrderBy(p => p).ToList();
        foreach(var part in parts)
        {
            bool shown = !_hiddenParts.Contains(part);
            int bones = snapshot.Parts.Count(p => p == part);
            using(ImRaii.PushColor(ImGuiCol.Text, ImGui.ColorConvertU32ToFloat4(PartColor(part))))
            {
                if(ImGui.Checkbox($"{PartName(snapshot, part)} ({bones})###renderer_part_{part}", ref shown))
                {
                    if(shown)
                        _hiddenParts.Remove(part);
                    else
                        _hiddenParts.Add(part);
                }
            }
            ImGui.SameLine();
        }
        ImGui.NewLine();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("###renderer_search", "Find bones (highlighted in green)", ref _search, 64);
    }

    private void DrawSelectedActions(ActorRef actor, BoneSnapshot snapshot, HashSet<string> held)
    {
        var bone = _selection.Bone;
        if(bone == null || snapshot.IndexOf(bone) < 0)
        {
            ImGui.TextDisabled("Click a bone to select it.");
            return;
        }

        if(!HandleCatalog.IsPickable(bone, Limber.Koi.Safety.AdultAllowed(actor.Address)))
        {
            ImGui.TextDisabled("Click a bone to select it.");
            return;
        }
        int index = snapshot.IndexOf(bone);
        ImGui.TextColored(Accent, HandleCatalog.Label(bone));
        ImGui.SameLine();
        ImGui.TextDisabled($"({bone}, {PartName(snapshot, snapshot.Parts[index])})");

        float third = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X * 2) / 3f;
        using(ImRaii.Disabled(held.Contains(bone)))
        {
            if(ImGui.Button("Hold it###renderer_hold", new Vector2(third, 0)))
            {
                lock(_rigs.Sync)
                {
                    _rigs.Checkpoint();
                    _rigs.GetOrCreateRig(actor.Address, actor.ObjectIndex, actor.Name).GetOrAdd(bone);
                }
            }
        }
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("Holds it where it is; turn or move it with the gizmo (Limber > Pose).");
        ImGui.SameLine();
        if(ImGui.Button("Attach to it###renderer_attach", new Vector2(third, 0)))
            _limber.SetAttachTarget(actor, bone);
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("Makes this the bone to hold on to in Limber > Attach.");
        ImGui.SameLine();
        if(ImGui.Button("Collider here###renderer_prop", new Vector2(third, 0)))
        {
            (_config.Props ??= new()).Add(new Limber.BodyRig.PropCollider
            {
                Name = $"Collider on {HandleCatalog.Label(bone)}",
                ParentCharacter = actor.Name,
                ParentIndexHint = actor.ObjectIndex,
                ParentBone = bone,
            });
            PropService?.Refresh();
            _config.Save();
        }
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("Puts a collider on this bone (Devices page).");
    }

    private static uint PartColor(int part) => PartColors[Math.Min(part, PartColors.Length - 1)];

    // A readable name for a part of the skeleton, from the bones in it.
    private readonly Dictionary<(nint, int), string> _partNames = [];

    private string PartName(BoneSnapshot snapshot, int part)
    {
        if(part == 0)
            return "Body";
        if(_partNames.TryGetValue((snapshot.Actor, part), out var cached))
            return cached;
        string name = $"Extra {part}";
        for(int i = 0; i < snapshot.Count; i++)
        {
            if(snapshot.Parts[i] != part)
                continue;
            var bone = snapshot.Names[i];
            if(bone.StartsWith("j_f_", StringComparison.Ordinal) || bone.StartsWith("j_ago", StringComparison.Ordinal))
            {
                name = "Face";
                break;
            }
            if(bone.StartsWith("j_kami", StringComparison.Ordinal))
            {
                name = "Hair";
                break;
            }
            if(bone.Contains("sippo", StringComparison.Ordinal) || bone.Contains("tail", StringComparison.OrdinalIgnoreCase))
            {
                name = "Tail";
                break;
            }
            if(bone.StartsWith("j_zera", StringComparison.Ordinal) || bone.StartsWith("j_mimi", StringComparison.Ordinal))
            {
                name = "Ears";
                break;
            }
        }
        _partNames[(snapshot.Actor, part)] = name;
        return name;
    }

    private delegate bool Projector(Vector3 p, out Vector2 screen, out float depth);

    private static void DrawGrid(ImDrawListPtr drawList, Projector project, Vector3 centre)
    {
        // A 4 m floor grid at the character's feet, every 25 cm.
        const float half = 2f, step = 0.25f;
        float y = 0f;
        for(float t = -half; t <= half + 1e-3f; t += step)
        {
            uint color = MathF.Abs(t) < 1e-3f ? 0x70FFFFFFu : 0x30FFFFFFu;
            if(project(new Vector3(t, y, -half), out var a, out _) && project(new Vector3(t, y, half), out var b, out _))
                drawList.AddLine(a, b, color, 1f);
            if(project(new Vector3(-half, y, t), out a, out _) && project(new Vector3(half, y, t), out b, out _))
                drawList.AddLine(a, b, color, 1f);
        }
        // Facing: +Z (front) marked in blue.
        if(project(new Vector3(0, y, 0), out var o, out _) && project(new Vector3(0, y, 0.5f), out var f, out _))
            drawList.AddLine(o, f, 0xFFFF9050, 2f);
    }
}
