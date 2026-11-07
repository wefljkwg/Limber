using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Bindings.ImGuizmo;
using Dalamud.Interface.Utility;
using Dalamud.Plugin.Services;
using Limber.BodyRig;
using Limber.Core;

namespace Limber.Koi.Props;

/// <summary>
/// A placed Koikatsu prop's bones drawn over the game as nodes (while its settings are open on the Props page): click
/// one to choose it, then move, turn or size it with the gizmo, like a character's handles.
/// </summary>
internal static unsafe class KoiPropNodes
{
    private const int GizmoId = 0x4C494D4B;

    public enum Tool { Rotate, Move, Scale }

    public static bool Show = true;
    public static Tool Op = Tool.Rotate;
    public static bool WorldAxes;
    public static int Selected = -1;

    private static int _frame = -10;
    private static SpawnedProp? _prop;
    private static Matrix4x4? _tracking;
    private static Vector3 _startSize;
    private static int _trackingBone = -1;
    private static bool _wantInput;

    /// <summary>The prop's settings are showing this frame (its nodes are drawn while they are).</summary>
    public static void Open(SpawnedProp prop)
    {
        if(_prop?.Id != prop.Id)
        {
            Selected = -1;
            _tracking = null;
        }
        _prop = prop;
        _frame = ImGui.GetFrameCount();
    }

    private static bool Showing => Show && _prop != null && ImGui.GetFrameCount() - _frame <= 1;

    /// <summary>The node gizmo is showing (the bone gizmo stays hidden: one gizmo a frame).</summary>
    public static bool GizmoWanted => Showing && Selected >= 0;

    /// <summary>A node or the gizmo was under the mouse or held (last frame).</summary>
    public static bool OwnsMouse => _wantInput && Showing;

    // The game's mirror of Unity's space (x the other way).
    private static readonly Matrix4x4 Mirror = Matrix4x4.CreateScale(-1f, 1f, 1f);

    private static bool ModelMatrix(SpawnedProp prop, out Matrix4x4 model)
    {
        model = Matrix4x4.Identity;
        if(prop.Objects.Length == 0 || prop.Objects[0] == null || !prop.Ready[0])
            return false;
        var obj = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object*)prop.Objects[0];
        Vector3 scale = obj->Scale, position = obj->Position;
        Quaternion rotation = obj->Rotation;
        model = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(position);
        return true;
    }

    // A bone (prefab space) into the world.
    private static Matrix4x4 ToWorld(Matrix4x4 bone, Matrix4x4 model) => Mirror * bone * Mirror * model;

    public static void Draw(IGameGui gameGui)
    {
        if(!Showing || _prop is not { } prop || KoiPropRuntime.For(prop.Id) is not { } inst || inst.Rig.Bones.Count == 0
           || !ModelMatrix(prop, out var model))
        {
            _wantInput = false;
            _tracking = null;
            return;
        }
        var rig = inst.Rig;
        int n = rig.Bones.Count;
        var posed = inst.Posed.Length == n ? inst.Posed : KoiPropRuntime.HeldPose(inst);
        if(Selected >= n)
            Selected = -1;

        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.Pos);
        ImGui.SetNextWindowSize(viewport.Size);
        var flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoBringToFrontOnFocus
            | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoDocking;
        if(!_wantInput)
            flags |= ImGuiWindowFlags.NoInputs;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        bool want = false;
        try
        {
            if(ImGui.Begin("##limber_koi_nodes", flags))
            {
                var draw = ImGui.GetWindowDrawList();
                float radius = 5f * ImGuiHelpers.GlobalScale;
                uint line = ImGui.GetColorU32(new Vector4(0.6f, 0.85f, 1f, 0.6f));
                uint fill = ImGui.GetColorU32(new Vector4(0.35f, 0.75f, 1f, 0.85f));
                uint hot = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 1f));
                uint chosen = ImGui.GetColorU32(new Vector4(1f, 0.8f, 0.2f, 1f));
                var mouse = ImGui.GetMousePos();
                var screens = new Vector2[n];
                var seen = new bool[n];
                for(int b = 0; b < n; b++)
                    seen[b] = gameGui.WorldToScreen(ToWorld(posed[b], model).Translation, out screens[b], out bool inView) && inView;
                for(int b = 0; b < n; b++)
                {
                    int parent = rig.Bones[b].Parent;
                    if(parent >= 0 && seen[b] && seen[parent])
                        draw.AddLine(screens[parent], screens[b], line, 1.5f * ImGuiHelpers.GlobalScale);
                }
                bool gizmoHere = Selected >= 0 && CameraUtil.TryGetMatrices(out _, out _);
                bool overGizmo = gizmoHere && (ImGuizmo.IsOver() || ImGuizmo.IsUsing());
                int hovered = -1;
                float best = radius * 1.8f;
                for(int b = 0; b < n; b++)
                {
                    if(!seen[b])
                        continue;
                    float d = Vector2.Distance(mouse, screens[b]);
                    if(d < best)
                    {
                        best = d;
                        hovered = b;
                    }
                }
                bool otherWindow = ImGui.IsWindowHovered(ImGuiHoveredFlags.AnyWindow) && !ImGui.IsWindowHovered();
                if(otherWindow)
                    hovered = -1;
                for(int b = 0; b < n; b++)
                {
                    if(!seen[b])
                        continue;
                    draw.AddCircleFilled(screens[b], radius, b == Selected ? chosen : fill);
                    if(b == hovered)
                        draw.AddCircle(screens[b], radius + 2f * ImGuiHelpers.GlobalScale, hot, 0, 2f);
                }
                if(hovered >= 0 && !overGizmo)
                {
                    want = true;
                    ImGui.SetTooltip(rig.Bones[hovered].Name);
                    if(ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                    {
                        Selected = hovered;
                        _tracking = null;
                    }
                    else if(ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                    {
                        Reset(inst, hovered);
                    }
                }
                if(Selected >= 0 && seen[Selected])
                    draw.AddText(screens[Selected] + new Vector2(8, -8) * ImGuiHelpers.GlobalScale, chosen, rig.Bones[Selected].Name);
                if(Selected >= 0)
                    want |= Gizmo(inst, posed, model, viewport.Pos, viewport.Size);
            }
        }
        finally
        {
            ImGui.End();
            ImGui.PopStyleVar(2);
        }
        _wantInput = want;
        if(want)
            ImGui.SetNextFrameWantCaptureMouse(true);
    }

    /// <summary>A bone back to how it rests.</summary>
    public static void Reset(KoiPropRuntime.Instance inst, int b)
    {
        inst.Turn[b] = Vector3.Zero;
        if(b < inst.Move.Length)
            inst.Move[b] = Vector3.Zero;
        if(b < inst.Size.Length)
            inst.Size[b] = Vector3.One;
        inst.Dirty = true;
        inst.Settled = false;
    }

    private static bool Gizmo(KoiPropRuntime.Instance inst, Matrix4x4[] posed, Matrix4x4 model, Vector2 viewportPos, Vector2 viewportSize)
    {
        if(!CameraUtil.TryGetMatrices(out var view, out var projection))
            return false;
        int b = Selected;
        var rig = inst.Rig;
        int parent = rig.Bones[b].Parent;
        // (where it is in the world, without any size: the gizmo then turns and moves it cleanly)
        var world = ToWorld(posed[b], model);
        if(!Matrix4x4.Decompose(world, out _, out var rotation, out var position))
            return false;
        var clean = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(rotation)) * Matrix4x4.CreateTranslation(position);
        if(_trackingBone != b)
        {
            _trackingBone = b;
            _tracking = null;
        }
        var matrix = _tracking ?? clean;
        var operation = Op switch { Tool.Move => ImGuizmoOperation.Translate, Tool.Scale => ImGuizmoOperation.Scale, _ => ImGuizmoOperation.Rotate };
        var mode = WorldAxes && Op != Tool.Scale ? ImGuizmoMode.World : ImGuizmoMode.Local;

        ImGuizmo.BeginFrame();
        ImGuizmo.SetID(GizmoId);
        ImGuizmo.SetOrthographic(false);
        ImGuizmo.SetDrawlist();
        ImGuizmo.SetRect(viewportPos.X, viewportPos.Y, viewportSize.X, viewportSize.Y);
        ImGuizmo.Enable(true);
        if(ImGuizmo.Manipulate(ref view, ref projection, operation, mode, ref matrix))
        {
            if(_tracking == null)
                _startSize = b < inst.Size.Length ? inst.Size[b] : Vector3.One;
            _tracking = matrix;
            Apply(inst, b, parent, posed, model, matrix);
        }
        bool usingGizmo = ImGuizmo.IsUsing(), overGizmo = ImGuizmo.IsOver();
        if(!usingGizmo)
            _tracking = null;
        ImGuizmo.SetID(0);
        return usingGizmo || overGizmo;
    }

    // The gizmo's matrix (world) back into the bone's turn, move or size.
    private static void Apply(KoiPropRuntime.Instance inst, int b, int parent, Matrix4x4[] posed, Matrix4x4 model, Matrix4x4 matrix)
    {
        if(!Matrix4x4.Decompose(matrix, out var scale, out var rotation, out var position)
           || !MathUtil.IsFinite(position) || !MathUtil.IsFinite(rotation) || !Matrix4x4.Invert(model, out var toModel))
            return;
        var bone = inst.Rig.Bones[b];
        var parentWorld = parent >= 0 ? posed[parent] : Matrix4x4.Identity;
        if(!Matrix4x4.Invert(parentWorld, out var toParent))
            return;
        switch(Op)
        {
            case Tool.Move:
            {
                // (the world point into the prefab's space, then into the parent's)
                var prefab = Vector3.Transform(position, toModel);
                prefab.X = -prefab.X;
                var inParent = Vector3.Transform(prefab, toParent);
                inst.Move[b] = inParent - bone.RestLocal.Translation;
                break;
            }
            case Tool.Rotate:
            {
                // world turn → prefab space → relative to the parent → on top of how it rests
                var turnWorld = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(rotation));
                var modelTurn = Matrix4x4.CreateFromQuaternion(Decomposed(model));
                Matrix4x4.Invert(modelTurn, out var unModel);
                var prefabTurn = Mirror * turnWorld * unModel * Mirror;
                var parentTurn = Matrix4x4.CreateFromQuaternion(Decomposed(parentWorld));
                Matrix4x4.Invert(parentTurn, out var unParent);
                var local = prefabTurn * unParent;
                var rest = Matrix4x4.CreateFromQuaternion(Decomposed(bone.RestLocal));
                Matrix4x4.Invert(rest, out var unRest);
                var q = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(local * unRest));
                if(MathUtil.IsFinite(q))
                    inst.Turn[b] = ToYawPitchRoll(q);
                break;
            }
            case Tool.Scale:
                if(MathUtil.IsFinite(scale))
                    inst.Size[b] = Vector3.Clamp(_startSize * scale, new Vector3(0.01f), new Vector3(20f));
                break;
        }
        inst.Dirty = true;
        inst.Settled = false;
    }

    private static Quaternion Decomposed(Matrix4x4 m)
        => Matrix4x4.Decompose(m, out _, out var q, out _) ? Quaternion.Normalize(q) : Quaternion.Identity;

    /// <summary>Degrees (yaw, pitch, roll), as Matrix4x4.CreateFromYawPitchRoll takes them.</summary>
    public static Vector3 ToYawPitchRoll(Quaternion q)
    {
        float pitch = MathF.Asin(Math.Clamp(2f * (q.W * q.X - q.Y * q.Z), -1f, 1f));
        float yaw = MathF.Atan2(2f * (q.W * q.Y + q.X * q.Z), 1f - 2f * (q.X * q.X + q.Y * q.Y));
        float roll = MathF.Atan2(2f * (q.W * q.Z + q.X * q.Y), 1f - 2f * (q.X * q.X + q.Z * q.Z));
        return new Vector3(yaw, pitch, roll) * (180f / MathF.PI);
    }
}
