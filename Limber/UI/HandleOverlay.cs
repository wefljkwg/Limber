using Dalamud.Bindings.ImGui;
using Dalamud.Bindings.ImGuizmo;
using Dalamud.Interface.Utility;
using Dalamud.Plugin.Services;
using Limber.Core;
using Limber.Rig;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Limber.UI;

// The dots drawn on the selected character: drag to pose, right-click to pin, middle-click to let go,
// plus a rotation gizmo on the selected dot.
public sealed class HandleOverlay
{
    private const int GizmoId = 0x4C494D42;

    private readonly RigService _rigs;
    private readonly Selection _selection;
    private readonly RigSettings _config;
    private readonly IGameGui _gameGui;

    private struct HandleView
    {
        public string Bone;
        public string Label;
        public Vector2 Screen;
        public Vector3 World;
        public float Radius;
        public bool IsMain;
        public GoalSpace? Space;
        public bool GoalEnabled;
        public bool TurnOnly;   // only turned, not held in place (yellow)
    }

    private sealed class DragState
    {
        public required string Bone;
        public required nint Actor;
        public ushort ObjectIndex;
        public string ActorName = string.Empty;
        public bool Moved;
        public Vector2 StartMouse;
        public Vector3 StartWorld;
        public Vector3 Right;
        public Vector3 Up;
        public float InvA, InvB, InvC, InvD; // inverse of the screen-per-world 2x2 matrix
    }

    private readonly List<HandleView> _handles = [];
    private int _hovered = -1;
    private DragState? _drag;
    private bool _wantInput;
    private Matrix4x4? _gizmoTracking;
    private string? _gizmoBone;

    public bool IsDragging => _drag != null;

    /// <summary>Another gizmo (a prop's) is in use this frame: don't draw the bone gizmo (one gizmo per frame).</summary>
    public Func<bool> SuppressGizmo = () => false;
    /// <summary>Another overlay (the prop gizmo) has the mouse: don't grab dots under it.</summary>
    public Func<bool> MouseTakenElsewhere = () => false;

    public HandleOverlay(RigService rigs, Selection selection, RigSettings config, IGameGui gameGui)
    {
        _rigs = rigs;
        _selection = selection;
        _config = config;
        _gameGui = gameGui;
    }

    // Breast rig preview, requested by the Body rig tab while it's open (see RequestRigPreview).
    private Limber.BodyRig.BreastRigShape? _rigPreview;
    private Limber.BodyRig.RigPart _rigPreviewPart = Limber.BodyRig.RigParts.Breasts;
    private int _rigPreviewFrame = -10;

    /// <summary>Show the breast rig grid on the selected character this frame.</summary>
    public void RequestRigPreview(Limber.BodyRig.RigPart part, Limber.BodyRig.BreastRigShape shape)
    {
        _rigPreviewPart = part;
        _rigPreview = shape;
        _rigPreviewFrame = ImGui.GetFrameCount();
    }

    private bool RigPreviewWanted => _rigPreview != null && ImGui.GetFrameCount() - _rigPreviewFrame <= 1;

    public void Draw()
    {
        var actor = _selection.Actor;
        BoneSnapshot? snapshot = null;

        bool preview = RigPreviewWanted && actor.IsSet;
        bool show = actor.IsSet && (preview || (_config.Enabled && _config.ShowHandles && _rigs.IsActive
            && !(_config.HideHandlesWithGameUi && _gameGui.GameUiHidden)));

        if(show)
        {
            lock(_rigs.Sync)
                snapshot = _rigs.GetSnapshot(actor.Address);
        }

        if(snapshot == null)
        {
            Reset();
            return;
        }

        if(_drag != null && _drag.Actor != actor.Address)
            _drag = null;

        if(_config.Enabled && _config.ShowHandles && _rigs.IsActive)
            BuildHandles(snapshot, actor);
        else
            _handles.Clear();

        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.Pos);
        ImGui.SetNextWindowSize(viewport.Size);

        var flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoBringToFrontOnFocus
            | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoDocking;

        // Only take the mouse away from the game while a dot or the gizmo is under it or being dragged.
        if(!_wantInput)
            flags |= ImGuiWindowFlags.NoInputs;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);

        try
        {
            if(ImGui.Begin("##limber_handle_overlay", flags))
                DrawContents(snapshot, actor, viewport.Pos, viewport.Size);
        }
        finally
        {
            ImGui.End();
            ImGui.PopStyleVar(2);
        }

        if(_wantInput)
            ImGui.SetNextFrameWantCaptureMouse(true);
    }

    // Pink dots: where the breast rig bones go. Green rings: rig bones already in the skeleton.
    private void DrawRigPreview(ImDrawListPtr drawList, BoneSnapshot snapshot, Limber.BodyRig.RigPart part, Limber.BodyRig.BreastRigShape shape)
    {
        bool Has(string n) => snapshot.IndexOf(n) >= 0;
        var leftName = part.ParentBone(Has, true);
        var rightName = part.ParentBone(Has, false);
        if(leftName == null || rightName == null || !snapshot.TryGetWorld(leftName, out var left) || !snapshot.TryGetWorld(rightName, out var right))
            return;

        float scale = snapshot.Scale > 0.01f ? snapshot.Scale : 1f;
        float separation = Vector3.Distance(left.Position, right.Position) / scale;
        if(!(separation > 0.005f && separation < 1f))
            separation = part.FallbackSeparation;

        var inverseRoot = Quaternion.Inverse(snapshot.RootRotation);
        var middle = (left.Position + right.Position) * 0.5f;
        uint pink = ImGui.GetColorU32(new Vector4(1f, 0.45f, 0.85f, 1f));
        uint green = ImGui.GetColorU32(new Vector4(0.35f, 1f, 0.45f, 1f));
        float dot = 4f * ImGuiHelpers.GlobalScale;
        foreach(var (frame, isLeft) in new[] { (left, true), (right, false) })
        {
            float outward = Vector3.Transform(frame.Position - middle, inverseRoot).X >= 0 ? 1f : -1f;
            for(int i = 0; i < part.PerSide; i++)
            {
                var offset = part.ModelOffset(i, shape, separation, outward);
                var world = frame.Position + Vector3.Transform(offset * scale, snapshot.RootRotation);
                if(_gameGui.WorldToScreen(world, out var screen, out bool inView) && inView)
                    drawList.AddCircleFilled(screen, dot, pink);

                if(snapshot.TryGetWorld(part.BoneName(isLeft, i), out var existing)
                   && _gameGui.WorldToScreen(existing.Position, out var s2, out bool v2) && v2)
                    drawList.AddCircle(s2, dot * 1.6f, green, 12, 2f);
            }
        }
    }

    private void Reset()
    {
        _handles.Clear();
        _hovered = -1;
        _drag = null;
        _wantInput = false;
        _gizmoTracking = null;
        _gizmoBone = null;
    }

    private void BuildHandles(BoneSnapshot snapshot, ActorRef actor)
    {
        _handles.Clear();

        Dictionary<string, (GoalSpace Space, bool Enabled, bool TurnOnly)>? goals = null;
        lock(_rigs.Sync)
        {
            var rig = _rigs.GetRig(actor.Address);
            if(rig != null)
            {
                goals = [];
                foreach(var goal in rig.Goals)
                    goals[goal.Bone] = (goal.Space, goal.Enabled, goal.TurnOnly);
            }
        }

        float scale = ImGuiHelpers.GlobalScale;
        float mainRadius = Math.Clamp(_config.HandleSize, 3f, 40f) * scale;
        float smallRadius = MathF.Max(3f * scale, mainRadius * 0.55f);

        var added = new HashSet<string>();

        void Add(string bone, bool isMain)
        {
            if(!added.Add(bone))
                return;

            int index = snapshot.IndexOf(bone);
            if(index < 0)
                return;

            var world = snapshot.WorldPositions[index];
            if(!_gameGui.WorldToScreen(world, out var screen, out bool inView) || !inView)
                return;

            var view = new HandleView
            {
                Bone = bone,
                Label = HandleCatalog.Label(bone),
                Screen = screen,
                World = world,
                Radius = isMain ? mainRadius : smallRadius,
                IsMain = isMain,
            };

            if(goals != null && goals.TryGetValue(bone, out var state))
            {
                view.Space = state.Space;
                view.GoalEnabled = state.Enabled;
                view.TurnOnly = state.TurnOnly;
            }

            _handles.Add(view);
        }

        foreach(var handle in HandleCatalog.Handles)
            Add(handle.Bone, true);

        // Bones with goals that aren't one of the main handles.
        if(goals != null)
            foreach(var bone in goals.Keys)
                Add(bone, false);

        if(_selection.Bone != null)
            Add(_selection.Bone, false);

        if(_config.ShowAllBones)
        {
            var names = snapshot.Info.Names;
            bool adult = Limber.Koi.Safety.AdultAllowed(actor.Address);
            for(int i = 0; i < names.Length; i++)
                if(HandleCatalog.IsGrabbable(names[i], adult))
                    Add(names[i], false);
        }
    }

    private void DrawContents(BoneSnapshot snapshot, ActorRef actor, Vector2 viewportPos, Vector2 viewportSize)
    {
        var drawList = ImGui.GetWindowDrawList();
        if(RigPreviewWanted && _rigPreview != null)
            DrawRigPreview(drawList, snapshot, _rigPreviewPart, _rigPreview);
        if(!(_config.Enabled && _config.ShowHandles && _rigs.IsActive))
        {
            // Only here for the rig preview: no handles or gizmo.
            _hovered = -1;
            _drag = null;
            _wantInput = false;
            return;
        }
        var mouse = ImGui.GetMousePos();

        // Another window (or a popup) under the mouse takes priority over the dots.
        bool otherWindowHovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.AnyWindow) && !ImGui.IsWindowHovered();

        bool gizmoOwnsMouse = DrawGizmo(snapshot, actor, viewportPos, viewportSize);

        _hovered = -1;
        bool busy = otherWindowHovered || gizmoOwnsMouse || MouseTakenElsewhere() || (!SuppressGizmo() && ImGuizmo.IsUsing());

        if(_drag == null && !busy)
        {
            float best = float.MaxValue;
            for(int i = 0; i < _handles.Count; i++)
            {
                var handle = _handles[i];
                float distance = Vector2.Distance(mouse, handle.Screen);
                float reach = handle.Radius * 1.35f;

                // Prefer the main handles when dots overlap.
                float score = handle.IsMain ? distance * 0.8f : distance;
                if(distance <= reach && score < best)
                {
                    best = score;
                    _hovered = i;
                }
            }

            if(_hovered >= 0)
            {
                var handle = _handles[_hovered];

                if(ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    _selection.Bone = handle.Bone;
                    StartDrag(actor, handle, mouse);
                }
                else if(ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                {
                    _selection.Bone = handle.Bone;
                    TogglePin(actor, handle.Bone);
                }
                else if(ImGui.IsMouseClicked(ImGuiMouseButton.Middle))
                {
                    Release(actor, handle.Bone);
                }
            }
        }

        if(_drag != null)
        {
            if(!ImGui.IsMouseDown(ImGuiMouseButton.Left))
                _drag = null;
            else
                UpdateDrag(mouse);
        }

        DrawHandles(drawList);

        _wantInput = _hovered >= 0 || _drag != null || gizmoOwnsMouse;
    }

    private void DrawHandles(ImDrawListPtr drawList)
    {
        uint outline = ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.75f));
        uint selectedRing = ImGui.GetColorU32(new Vector4(1f, 0.92f, 0.2f, 1f));
        float thickness = MathF.Max(1f, 1.5f * ImGuiHelpers.GlobalScale);

        // Small dots first so the main handles sit on top.
        for(int pass = 0; pass < 2; pass++)
        {
            for(int i = 0; i < _handles.Count; i++)
            {
                var handle = _handles[i];
                if(handle.IsMain != (pass == 1))
                    continue;

                bool hovered = i == _hovered || (_drag != null && _drag.Bone == handle.Bone);
                bool selected = handle.Bone == _selection.Bone;

                float radius = hovered ? handle.Radius * 1.25f : handle.Radius;
                var fill = ImGui.GetColorU32(HandleColor(handle, hovered));

                drawList.AddCircleFilled(handle.Screen, radius, fill);
                drawList.AddCircle(handle.Screen, radius, outline, 0, thickness);

                if(selected)
                    drawList.AddCircle(handle.Screen, radius + 3f * ImGuiHelpers.GlobalScale, selectedRing, 0, thickness * 1.5f);
            }
        }

        if(_hovered >= 0 && _drag == null)
        {
            var handle = _handles[_hovered];
            string state = handle.Space switch
            {
                GoalSpace.Pinned => " (pinned)",
                GoalSpace.Attached => " (attached)",
                GoalSpace.FollowBody => handle.TurnOnly ? " (turned)" : " (held)",
                _ => string.Empty,
            };

            var textPos = handle.Screen + new Vector2(handle.Radius + 6f, -ImGui.GetTextLineHeight() * 0.5f);
            string text = handle.Label + state;
            drawList.AddText(textPos + Vector2.One, ImGui.GetColorU32(new Vector4(0, 0, 0, 0.9f)), text);
            drawList.AddText(textPos, ImGui.GetColorU32(new Vector4(1, 1, 1, 1)), text);
        }
    }

    private static Vector4 HandleColor(in HandleView handle, bool hovered)
    {
        Vector4 color;
        if(handle.Space == null)
            color = handle.IsMain ? new Vector4(1f, 1f, 1f, 0.85f) : new Vector4(0.85f, 0.85f, 0.9f, 0.65f);
        else if(!handle.GoalEnabled)
            color = new Vector4(0.55f, 0.55f, 0.55f, 0.85f);
        else if(handle.TurnOnly)
            color = new Vector4(1f, 0.9f, 0.15f, 0.95f);
        else
            color = handle.Space switch
            {
                GoalSpace.Pinned => new Vector4(0.95f, 0.25f, 0.25f, 0.95f),
                GoalSpace.Attached => new Vector4(0.25f, 0.85f, 1f, 0.95f),
                _ => new Vector4(1f, 0.55f, 0.1f, 0.95f),
            };

        if(hovered)
            color = Vector4.Lerp(color, new Vector4(1f, 1f, 0.6f, 1f), 0.45f);

        return color;
    }

    // ---- Dragging

    private void StartDrag(ActorRef actor, HandleView handle, Vector2 mouse)
    {
        if(!CameraUtil.TryGetAxes(out var right, out var up))
            return;

        // How far the dot moves on screen for a small step right and up in the world, inverted,
        // so mouse movement maps back to movement in the plane facing the camera at the bone's depth.
        const float step = 0.05f;
        if(!_gameGui.WorldToScreen(handle.World, out var origin, out _)
            || !_gameGui.WorldToScreen(handle.World + right * step, out var rightScreen, out _)
            || !_gameGui.WorldToScreen(handle.World + up * step, out var upScreen, out _))
            return;

        var dRight = (rightScreen - origin) / step;
        var dUp = (upScreen - origin) / step;

        float determinant = dRight.X * dUp.Y - dUp.X * dRight.Y;
        if(MathF.Abs(determinant) < 1e-4f)
            return;

        _drag = new DragState
        {
            Bone = handle.Bone,
            Actor = actor.Address,
            ObjectIndex = actor.ObjectIndex,
            ActorName = actor.Name,
            StartMouse = mouse,
            StartWorld = handle.World,
            Right = right,
            Up = up,
            InvA = dUp.Y / determinant,
            InvB = -dUp.X / determinant,
            InvC = -dRight.Y / determinant,
            InvD = dRight.X / determinant,
        };
    }

    private void UpdateDrag(Vector2 mouse)
    {
        var drag = _drag!;
        var delta = mouse - drag.StartMouse;

        // A click without moving only selects the dot; the bone is held once it is actually dragged.
        if(!drag.Moved)
        {
            if(delta.LengthSquared() < 9f)
                return;
            drag.Moved = true;
            _rigs.Checkpoint();
        }

        float alongRight = drag.InvA * delta.X + drag.InvB * delta.Y;
        float alongUp = drag.InvC * delta.X + drag.InvD * delta.Y;
        var world = drag.StartWorld + drag.Right * alongRight + drag.Up * alongUp;
        if(!MathUtil.IsFinite(world))
            return;

        lock(_rigs.Sync)
        {
            var rig = _rigs.GetOrCreateRig(drag.Actor, drag.ObjectIndex, drag.ActorName);
            var goal = rig.GetOrAdd(drag.Bone);
            goal.Enabled = true;
            goal.KeepPosition();
            goal.PendingWorldPosition = world;
        }
    }

    // ---- Pins and releasing

    public void TogglePin(ActorRef actor, string bone, bool checkpoint = true)
    {
        lock(_rigs.Sync)
        {
            if(checkpoint)
                _rigs.Checkpoint();
            var rig = _rigs.GetOrCreateRig(actor.Address, actor.ObjectIndex, actor.Name);
            var existing = rig.Find(bone);
            var goal = existing ?? rig.GetOrAdd(bone);

            if(existing != null && goal.Space == GoalSpace.Pinned)
            {
                goal.Space = goal.HomeSpace == GoalSpace.Pinned ? GoalSpace.FollowBody : goal.HomeSpace;
            }
            else
            {
                if(goal.Space == GoalSpace.Attached)
                {
                    goal.ParentActor = 0;
                    goal.ParentBone = null;
                    goal.ParentActorName = string.Empty;
                }
                goal.Space = GoalSpace.Pinned;
                goal.LockRotation = true;
                goal.HoldPosition = true;
            }

            goal.Enabled = true;
            goal.RequestCapture();
        }
    }

    public void Release(ActorRef actor, string bone)
    {
        lock(_rigs.Sync)
        {
            var rig = _rigs.GetRig(actor.Address);
            if(rig?.Find(bone) == null)
                return;

            _rigs.Checkpoint();
            rig.Remove(bone);
            if(rig.Goals.Count == 0)
                _rigs.RemoveRig(actor.Address);
        }

        if(_drag != null && _drag.Bone == bone)
            _drag = null;
    }

    // ---- Gizmo on the selected handle: rotation rings or move arrows, on the bone's own axes or the world's

    private bool DrawGizmo(BoneSnapshot snapshot, ActorRef actor, Vector2 viewportPos, Vector2 viewportSize)
    {
        var bone = _selection.Bone;
        if(!_config.ShowRotationGizmo || bone == null || _drag != null || SuppressGizmo())
        {
            _gizmoTracking = null;
            return false;
        }

        if(!snapshot.TryGetWorld(bone, out var world) || !CameraUtil.TryGetMatrices(out var view, out var projection))
        {
            _gizmoTracking = null;
            return false;
        }

        if(_gizmoBone != bone)
        {
            _gizmoBone = bone;
            _gizmoTracking = null;
        }

        bool move = _config.GizmoMove;
        var operation = move ? ImGuizmoOperation.Translate : ImGuizmoOperation.Rotate;
        var mode = _config.GizmoWorldAxes ? ImGuizmoMode.World : ImGuizmoMode.Local;

        var matrix = _gizmoTracking ?? MathUtil.FrameMatrix(world);

        ImGuizmo.BeginFrame();
        ImGuizmo.SetID(GizmoId);
        ImGuizmo.SetOrthographic(false);
        ImGuizmo.SetDrawlist();
        ImGuizmo.SetRect(viewportPos.X, viewportPos.Y, viewportSize.X, viewportSize.Y);
        ImGuizmo.Enable(true);

        if(ImGuizmo.Manipulate(ref view, ref projection, operation, mode, ref matrix))
        {
            // First change of this drag: remember the state before it for undo.
            if(_gizmoTracking == null)
                _rigs.Checkpoint();
            _gizmoTracking = matrix;

            if(Matrix4x4.Decompose(matrix, out _, out var rotation, out var translation))
            {
                lock(_rigs.Sync)
                {
                    var rig = _rigs.GetOrCreateRig(actor.Address, actor.ObjectIndex, actor.Name);
                    bool isNew = rig.Find(bone) == null;
                    var goal = rig.GetOrAdd(bone);
                    goal.Enabled = true;

                    if(move)
                    {
                        goal.KeepPosition();
                        if(MathUtil.IsFinite(translation))
                            goal.PendingWorldPosition = translation;
                    }
                    else if(isNew)
                    {
                        // Only turned: it turns on its own, the body above doesn't bend to hold its place.
                        goal.HoldPosition = false;
                    }

                    if(!move && MathUtil.IsFinite(rotation))
                    {
                        goal.PendingWorldRotation = Quaternion.Normalize(rotation);
                        goal.LockRotation = true;
                    }
                }
            }
        }

        bool usingGizmo = ImGuizmo.IsUsing();
        bool overGizmo = ImGuizmo.IsOver();

        if(!usingGizmo)
            _gizmoTracking = null;

        ImGuizmo.SetID(0);

        return usingGizmo || overGizmo;
    }
}
