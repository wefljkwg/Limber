using Dalamud.Bindings.ImGui;
using Dalamud.Bindings.ImGuizmo;
using Dalamud.Interface.Utility;
using Dalamud.Plugin.Services;
using Limber.BodyRig;
using Limber.Core;
using System;
using System.Numerics;

namespace Limber.UI;

// Props drawn over the game as wireframe pills (like Koikatsu's collider props), and a move / rotate gizmo on the
// prop chosen in the Props tab.
public sealed class PropOverlay
{
    private const int GizmoId = 0x4C494D43;
    private const int RingSegments = 24;

    private readonly PropService _props;
    private readonly RigSettings _config;
    private readonly IGameGui _gameGui;

    private int _tabFrame = -10;
    private bool _wantInput;
    private Matrix4x4? _tracking;
    private Guid _trackingId;
    private bool _ownsMouse;

    /// <summary>Soft bodies whose collision surface and body capsules "Show body colliders" draws.</summary>
    public SoftBody[] BodyColliders { get; init; } = [];

    /// <summary>The prop being edited in the Props tab (the gizmo goes on it).</summary>
    public PropCollider? Selected { get; set; }

    public PropOverlay(PropService props, RigSettings config, IGameGui gameGui)
    {
        _props = props;
        _config = config;
        _gameGui = gameGui;
    }

    /// <summary>The Props tab is open this frame (wireframes and the gizmo show while it is).</summary>
    public void TabOpen() => _tabFrame = ImGui.GetFrameCount();

    private bool IsTabOpen => ImGui.GetFrameCount() - _tabFrame <= 1;

    /// <summary>The prop gizmo is showing (so the bone gizmo stays hidden).</summary>
    public bool GizmoWanted => IsTabOpen && _config.PropGizmo && Selected is { Enabled: true } && Find(Selected.Id, out _);

    /// <summary>The prop gizmo is under the mouse or being dragged (last frame).</summary>
    public bool OwnsMouse => _ownsMouse;

    public void Draw()
    {
        var world = _props.World;
        bool wires = _config.PropWiresShown && (_config.PropWiresAlways || IsTabOpen);
        bool gizmo = GizmoWanted;
        bool body = _config.ShowBodyColliders;
        if ((world.Length == 0 && !body) || (!wires && !gizmo && !body))
        {
            Reset();
            return;
        }

        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.Pos);
        ImGui.SetNextWindowSize(viewport.Size);
        var flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoBringToFrontOnFocus
            | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoDocking;
        if (!_wantInput)
            flags |= ImGuiWindowFlags.NoInputs;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        try
        {
            if (ImGui.Begin("##limber_prop_overlay", flags))
            {
                var drawList = ImGui.GetWindowDrawList();
                if (wires)
                    DrawWires(drawList, world);
                if (body)
                    DrawBodyColliders(drawList);
                _wantInput = gizmo && DrawGizmo(viewport.Pos, viewport.Size);
                if (!gizmo)
                    StopTracking();
            }
        }
        finally
        {
            ImGui.End();
            ImGui.PopStyleVar(2);
        }

        _ownsMouse = _wantInput;
        if (_wantInput)
            ImGui.SetNextFrameWantCaptureMouse(true);
    }

    private void Reset()
    {
        _wantInput = false;
        _ownsMouse = false;
        StopTracking();
    }

    // A gizmo drag ended (let go, or the gizmo went away mid-drag): keep the change.
    private void StopTracking()
    {
        if (_tracking == null)
            return;
        _tracking = null;
        _config.Save();
    }

    private bool Find(Guid id, out PropWorld prop)
    {
        foreach (var p in _props.World)
        {
            if (p.Id == id)
            {
                prop = p;
                return true;
            }
        }
        prop = default;
        return false;
    }

    // ---- Wireframes

    private void DrawWires(ImDrawListPtr drawList, PropWorld[] world)
    {
        uint normal = ImGui.GetColorU32(new Vector4(0.35f, 1f, 0.55f, 0.85f));
        uint chosen = ImGui.GetColorU32(new Vector4(1f, 0.85f, 0.25f, 1f));
        float thickness = 1.5f * ImGuiHelpers.GlobalScale;
        var selected = Selected?.Id;
        foreach (var prop in world)
        {
            if (!prop.Visible)
                continue;
            bool isSelected = prop.Id == selected;
            DrawCapsule(drawList, prop, isSelected ? chosen : normal, isSelected ? thickness * 1.6f : thickness);
            if (isSelected && Selected != null && Project(prop.Frame.Position, out var label))
                drawList.AddText(label + new Vector2(8, -8) * ImGuiHelpers.GlobalScale, chosen, Selected.Name);
        }
    }

    // What the soft bodies' bone collision uses: the dots on each breast's (testicle's) surface that props push,
    // and the body's own capsules (torso, arms, hands) the breasts rest against.
    private void DrawBodyColliders(ImDrawListPtr drawList)
    {
        uint dot = ImGui.GetColorU32(new Vector4(1f, 0.45f, 0.85f, 0.9f));
        uint capsule = ImGui.GetColorU32(new Vector4(0.45f, 0.7f, 1f, 0.7f));
        float size = 2.5f * ImGuiHelpers.GlobalScale;
        float thickness = 1.2f * ImGuiHelpers.GlobalScale;
        foreach (var soft in BodyColliders)
        {
            foreach (var p in soft.DebugSurface)
                if (Project(p, out var screen))
                    drawList.AddCircleFilled(screen, size, dot);
            foreach (var (a, b, r) in soft.DebugCapsules)
            {
                var axis = b - a;
                var up = axis.LengthSquared() > 1e-10f ? Vector3.Normalize(axis) : Vector3.UnitY;
                var x = Vector3.Normalize(Vector3.Cross(up, MathF.Abs(up.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
                var z = Vector3.Cross(up, x);
                foreach (var centre in new[] { a, b })
                    DrawArc(drawList, centre, x * r, z * r, 0f, MathF.Tau, RingSegments, capsule, thickness);
                foreach (var side in new[] { x, -x, z, -z })
                    Line(drawList, a + side * r, b + side * r, capsule, thickness);
                foreach (var side in new[] { x, z })
                {
                    DrawArc(drawList, b, side * r, up * r, 0f, MathF.PI, RingSegments / 2, capsule, thickness);
                    DrawArc(drawList, a, side * r, -up * r, 0f, MathF.PI, RingSegments / 2, capsule, thickness);
                }
            }
        }
    }

    // Two rings round the ends of the straight part, four lines along it, and two half-circles over each end.
    private void DrawCapsule(ImDrawListPtr drawList, in PropWorld prop, uint color, float thickness)
    {
        if (prop.IsBox)
        {
            // A box: its twelve edges.
            var sh = prop.Shape;
            var u = sh.U * (sh.Half.X + sh.R);
            var v = sh.V * (sh.Half.Y + sh.R);
            var w = sh.W * (sh.Half.Z + sh.R);
            Vector3 Corner(int i) => sh.A + u * ((i & 1) != 0 ? 1f : -1f) + v * ((i & 2) != 0 ? 1f : -1f) + w * ((i & 4) != 0 ? 1f : -1f);
            for (int i = 0; i < 8; i++)
                for (int bit = 1; bit < 8; bit <<= 1)
                    if ((i & bit) == 0)
                        Line(drawList, Corner(i), Corner(i | bit), color, thickness);
            return;
        }
        var x = Vector3.Transform(Vector3.UnitX, prop.Frame.Rotation);
        var up = Vector3.Transform(Vector3.UnitY, prop.Frame.Rotation);
        var z = Vector3.Transform(Vector3.UnitZ, prop.Frame.Rotation);
        float r = prop.Radius;

        // Rings at both ends of the straight part.
        foreach (var centre in new[] { prop.A, prop.B })
            DrawArc(drawList, centre, x * r, z * r, 0f, MathF.Tau, RingSegments, color, thickness);

        // Lines along the sides.
        foreach (var side in new[] { x, -x, z, -z })
            Line(drawList, prop.A + side * r, prop.B + side * r, color, thickness);

        // Round ends: half-circles in the two planes through the axis.
        foreach (var side in new[] { x, z })
        {
            DrawArc(drawList, prop.B, side * r, up * r, 0f, MathF.PI, RingSegments / 2, color, thickness);
            DrawArc(drawList, prop.A, side * r, -up * r, 0f, MathF.PI, RingSegments / 2, color, thickness);
        }
    }

    // Points centre + cos(t) * u + sin(t) * v for t in [from, to].
    private void DrawArc(ImDrawListPtr drawList, Vector3 centre, Vector3 u, Vector3 v, float from, float to, int segments,
        uint color, float thickness)
    {
        var previous = centre + u * MathF.Cos(from) + v * MathF.Sin(from);
        for (int i = 1; i <= segments; i++)
        {
            float t = from + (to - from) * i / segments;
            var next = centre + u * MathF.Cos(t) + v * MathF.Sin(t);
            Line(drawList, previous, next, color, thickness);
            previous = next;
        }
    }

    private void Line(ImDrawListPtr drawList, Vector3 a, Vector3 b, uint color, float thickness)
    {
        if (Project(a, out var sa) && Project(b, out var sb))
            drawList.AddLine(sa, sb, color, thickness);
    }

    // In front of the camera (it may be off the edge of the screen; the line is clipped there).
    private bool Project(Vector3 world, out Vector2 screen)
        => _gameGui.WorldToScreen(world, out screen, out _);

    // ---- Gizmo on the chosen prop

    private bool DrawGizmo(Vector2 viewportPos, Vector2 viewportSize)
    {
        var prop = Selected;
        if (prop == null || !Find(prop.Id, out var placed) || !CameraUtil.TryGetMatrices(out var view, out var projection))
        {
            StopTracking();
            return false;
        }
        if (_trackingId != prop.Id)
        {
            _trackingId = prop.Id;
            StopTracking();
        }

        bool move = _config.PropGizmoMove;
        var operation = move ? ImGuizmoOperation.Translate : ImGuizmoOperation.Rotate;
        var mode = _config.PropGizmoWorldAxes ? ImGuizmoMode.World : ImGuizmoMode.Local;
        var matrix = _tracking ?? MathUtil.FrameMatrix(placed.Frame);

        ImGuizmo.BeginFrame();
        ImGuizmo.SetID(GizmoId);
        ImGuizmo.SetOrthographic(false);
        ImGuizmo.SetDrawlist();
        ImGuizmo.SetRect(viewportPos.X, viewportPos.Y, viewportSize.X, viewportSize.Y);
        ImGuizmo.Enable(true);

        if (ImGuizmo.Manipulate(ref view, ref projection, operation, mode, ref matrix))
        {
            _tracking = matrix;
            if (Matrix4x4.Decompose(matrix, out _, out var rotation, out var translation)
                && MathUtil.IsFinite(translation) && MathUtil.IsFinite(rotation))
            {
                // Back into the bone's frame (or the world's), without the character's size.
                var local = placed.Parent.ToLocal(new Frame(translation, Quaternion.Normalize(rotation)));
                float scale = placed.Scale > 0.01f ? placed.Scale : 1f;
                if (move)
                    prop.Offset = local.Position / scale;
                else
                    prop.Rotation = local.Rotation;
                _props.Refresh();
            }
        }

        bool usingGizmo = ImGuizmo.IsUsing();
        bool overGizmo = ImGuizmo.IsOver();
        if (!usingGizmo)
            StopTracking();
        ImGuizmo.SetID(0);
        return usingGizmo || overGizmo;
    }
}
