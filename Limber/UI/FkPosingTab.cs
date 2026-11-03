using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Limber.Core;
using Limber.Rig;

namespace Limber.UI;

/// <summary>
/// The FK Posing page: turn bones directly (Koikatsu FK / Brio / Anamnesis style). A bone list as the skeleton's tree,
/// rotation (and optionally position) of the chosen bone relative to its parent, the gizmo on it in the scene (the
/// rings rotate it, everything below it follows), part switches, mirror, rest pose, copy / paste and undo.
/// Works together with IK posing: IK runs on top of the FK pose.
/// </summary>
public sealed class FkPosingTab
{
    private readonly Selection _selection;
    private readonly RigService _rigs;
    private readonly FkService _fk;
    private readonly RigSettings _config;

    private string _search = "";
    private readonly HashSet<string> _open = new() { "n_root", "j_kosi", "j_sebo_a", "j_sebo_b", "j_sebo_c" };
    private Vector3 _euler;          // what the rotation fields show (kept while dragging, so they don't jump)
    private string? _eulerBone;
    private bool _fromRest = true;   // show the rotation as a turn from the rest pose
    private FkBoneSettings? _clipboard;
    private string _note = "";

    // Undo per character (whole FK pose copies).
    private readonly Dictionary<string, List<FkPoseSettings>> _undo = new(), _redo = new();

    /// <summary>The scene's dots (for Pin / Let go: the same pins as the IK Posing page).</summary>
    public HandleOverlay? Overlay { get; set; }

    public FkPosingTab(Selection selection, RigService rigs, FkService fk, RigSettings config)
    {
        _selection = selection;
        _rigs = rigs;
        _fk = fk;
        _config = config;
    }

    private Dictionary<string, FkPoseSettings> All => _config.FkPoses ??= new();

    // New poses go on top of the animation (Brio style); older saved ones are hold poses.
    private static FkPoseSettings NewPose() => new() { Layered = true };

    private bool IsLayered(string character) => !All.TryGetValue(character, out var p) || p.Layered;

    /// <summary>What the animation gives a bone (relative to its parent) under the FK turn on top of it.</summary>
    private (Quaternion Local, Vector3 Pos) BaseOf(string character, string bone, BoneSnapshot s, int i)
    {
        if(_fk.TryGetBase(bone, out var local, out var pos))
            return (local, pos);
        var e = All.TryGetValue(character, out var p) && p.Bones.TryGetValue(bone, out var b) && b.Enabled ? b : null;
        var now = LocalNow(s, i); var nowPos = LocalPosNow(s, i);
        if(e == null)
            return (now, nowPos);
        return (Quaternion.Normalize(now * Quaternion.Inverse(e.Rotation)), e.Move ? nowPos - e.Position : nowPos);
    }

    /// <summary>Whether the page is showing (set by the window each frame it draws the page).</summary>
    public int LastDrawnFrame { get; private set; } = -100;

    public bool Showing => ImGui.GetFrameCount() - LastDrawnFrame <= 1;

    // ---- Shared with the scene overlay (the gizmo turns bones through here while this page is open)

    public bool HasFk(string character, string bone)
        => All.TryGetValue(character, out var pose) && pose.Enabled && pose.Bones.TryGetValue(bone, out var b) && b.Enabled;

    private bool _gizmoChecked;

    /// <summary>A gizmo drag began: remember the pose for undo (once per drag).</summary>
    public void GizmoStart(string character)
    {
        if(_gizmoChecked)
            return;
        _gizmoChecked = true;
        Checkpoint(character);
    }

    public void GizmoEnd() => _gizmoChecked = false;

    /// <summary>Puts the bone where the gizmo has it (world rotation / position): stored relative to its parent.</summary>
    public void SetFromWorld(ActorRef actor, BoneSnapshot snapshot, string bone, Quaternion? worldRotation, Vector3? worldPosition)
    {
        string name = actor.Name ?? "";
        int i = snapshot.IndexOf(bone);
        if(name.Length == 0 || i < 0)
            return;
        bool layered = IsLayered(name);
        var (baseLocal, basePos) = BaseOf(name, bone, snapshot, i);
        var entry = Entry(name, bone, snapshot, create: true)!;
        int parent = i < snapshot.Parents.Length ? snapshot.Parents[i] : -1;
        var parentWorld = parent >= 0 ? snapshot.WorldRotations[parent] : snapshot.RootRotation;
        if(worldRotation is { } q && MathUtil.IsFinite(q))
        {
            var wanted = Quaternion.Normalize(Quaternion.Inverse(parentWorld) * q);
            // on top of the animation: the turn that takes the animated bone there
            entry.Rotation = layered ? Quaternion.Normalize(Quaternion.Inverse(baseLocal) * wanted) : wanted;
        }
        if(worldPosition is { } p && MathUtil.IsFinite(p))
        {
            var parentPos = parent >= 0 ? ModelPos(snapshot, snapshot.WorldPositions[parent]) : Vector3.Zero;
            var parentModel = Quaternion.Normalize(Quaternion.Inverse(snapshot.RootRotation) * parentWorld);
            var wantedPos = Vector3.Transform(ModelPos(snapshot, p) - parentPos, Quaternion.Inverse(parentModel));
            entry.Position = layered ? wantedPos - basePos : wantedPos;
            entry.Move = true;
        }
        _eulerBone = null;
        _config.Save();
    }

    // ---- Reading the pose on screen

    private static Vector3 ModelPos(BoneSnapshot s, Vector3 world)
        => Vector3.Transform(world - s.RootPosition, Quaternion.Inverse(s.RootRotation)) / MathF.Max(s.Scale, 0.01f);

    /// <summary>The bone's rotation relative to its parent as it is on screen now.</summary>
    private static Quaternion LocalNow(BoneSnapshot s, int i)
    {
        int parent = i < s.Parents.Length ? s.Parents[i] : -1;
        var parentWorld = parent >= 0 ? s.WorldRotations[parent] : s.RootRotation;
        return Quaternion.Normalize(Quaternion.Inverse(parentWorld) * s.WorldRotations[i]);
    }

    private static Vector3 LocalPosNow(BoneSnapshot s, int i)
    {
        int parent = i < s.Parents.Length ? s.Parents[i] : -1;
        if(parent < 0)
            return ModelPos(s, s.WorldPositions[i]);
        var parentModel = Quaternion.Normalize(Quaternion.Inverse(s.RootRotation) * s.WorldRotations[parent]);
        return Vector3.Transform(ModelPos(s, s.WorldPositions[i]) - ModelPos(s, s.WorldPositions[parent]), Quaternion.Inverse(parentModel));
    }

    /// <summary>The bone's FK entry; created from what's on screen if <paramref name="create"/>.</summary>
    private FkBoneSettings? Entry(string character, string bone, BoneSnapshot? snapshot, bool create)
    {
        if(!All.TryGetValue(character, out var pose))
        {
            if(!create)
                return null;
            All[character] = pose = NewPose();
        }
        if(pose.Bones.TryGetValue(bone, out var entry))
            return entry;
        if(!create)
            return null;
        entry = new FkBoneSettings();
        int i = snapshot?.IndexOf(bone) ?? -1;
        if(snapshot != null && i >= 0 && !pose.Layered)
        {
            entry.Rotation = LocalNow(snapshot, i);
            entry.Position = LocalPosNow(snapshot, i);
        }
        pose.Bones[bone] = entry;
        pose.Enabled = true;
        return entry;
    }

    // ---- Undo

    private void Checkpoint(string character)
    {
        var copy = All.TryGetValue(character, out var pose) ? pose.Copy() : new FkPoseSettings();
        if(!_undo.TryGetValue(character, out var stack))
            _undo[character] = stack = new();
        stack.Add(copy);
        if(stack.Count > 60)
            stack.RemoveAt(0);
        if(_redo.TryGetValue(character, out var redo))
            redo.Clear();
    }

    private void Step(string character, bool back)
    {
        var from = back ? _undo : _redo;
        var to = back ? _redo : _undo;
        if(!from.TryGetValue(character, out var stack) || stack.Count == 0)
            return;
        if(!to.TryGetValue(character, out var other))
            to[character] = other = new();
        other.Add(All.TryGetValue(character, out var now) ? now.Copy() : new FkPoseSettings());
        All[character] = stack[^1];
        stack.RemoveAt(stack.Count - 1);
        _eulerBone = null;
        _config.Save();
    }

    // ---- Euler angles (degrees; X, then Y, then Z)

    private static Quaternion FromEuler(Vector3 degrees)
    {
        var r = degrees * (MathF.PI / 180f);
        return Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, r.Z)
            * Quaternion.CreateFromAxisAngle(Vector3.UnitY, r.Y)
            * Quaternion.CreateFromAxisAngle(Vector3.UnitX, r.X));
    }

    private static Vector3 ToEuler(Quaternion q)
    {
        q = Quaternion.Normalize(q);
        float x = MathF.Atan2(2f * (q.W * q.X + q.Y * q.Z), 1f - 2f * (q.X * q.X + q.Y * q.Y));
        float y = MathF.Asin(Math.Clamp(2f * (q.W * q.Y - q.Z * q.X), -1f, 1f));
        float z = MathF.Atan2(2f * (q.W * q.Z + q.X * q.Y), 1f - 2f * (q.Y * q.Y + q.Z * q.Z));
        return new Vector3(x, y, z) * (180f / MathF.PI);
    }

    // ---- The page

    public void Draw()
    {
        LastDrawnFrame = ImGui.GetFrameCount();
        var actor = _selection.Actor;
        if(!actor.IsSet)
        {
            ImGui.TextDisabled("Choose a character at the top of the window.");
            return;
        }
        string name = actor.Name ?? "";
        if(name.Length == 0)
        {
            ImGui.TextDisabled("This character has no name to keep the pose under.");
            return;
        }
        _fk.Watch(actor.Address, actor.ObjectIndex);

        BoneSnapshot? snapshot;
        lock(_rigs.Sync)
            snapshot = _rigs.GetSnapshot(actor.Address);

        ImGui.TextWrapped("Turn bones directly: pick a bone (in the list or by clicking its dot), then use the rings in the scene "
            + "or the fields below. Everything below it follows. Pick below whether your turns go on top of the animation (like Brio: the "
            + "character keeps moving, freeze motion to stop it) or hold the pose. IK posing (the IK Posing page) works on top of this.");
        ImGui.Spacing();

        All.TryGetValue(name, out var pose);
        bool on = pose?.Enabled ?? true;
        if(ImGui.Checkbox("FK on for this character###fk_on", ref on))
        {
            Checkpoint(name);
            if(pose == null)
                All[name] = pose = NewPose();
            pose.Enabled = on;
            _config.Save();
        }
        ImGui.SameLine();
        ImGui.TextDisabled($"({pose?.Bones.Count ?? 0} bone{(pose?.Bones.Count == 1 ? "" : "s")} posed)");

        // How FK goes on: on top of the animation (Brio) or holding the pose (Koikatsu).
        bool layeredNow = pose?.Layered ?? true;
        int mode = layeredNow ? 0 : 1;
        bool switched = false;
        if(ImGui.RadioButton("On top of the animation (Brio style)###fk_mode_layer", mode == 0)) { mode = 0; switched = true; }
        Tooltip("Each bone's FK is a turn added to what the animation does, so the character keeps animating (breathing, "
            + "emotes, idles) with your pose on it. Freeze motion (top of the window) to hold everything still, as in Brio.");
        ImGui.SameLine();
        if(ImGui.RadioButton("Hold the pose (Koikatsu style)###fk_mode_hold", mode == 1)) { mode = 1; switched = true; }
        Tooltip("Each bone you pose holds its own turn and stops animating; the bones you haven't touched keep animating.");
        if(switched && (mode == 0) != layeredNow)
        {
            Checkpoint(name);
            if(pose == null)
                All[name] = pose = NewPose();
            pose.Layered = mode == 0;
            pose.Bones.Clear(); // (the two keep different values; undo brings the old pose back)
            _eulerBone = null;
            _note = "Switched; the FK turns were cleared (Undo brings them back).";
            _config.Save();
        }

        // Part switches (Koikatsu style): FK on or off per body part, the values kept.
        ImGui.Text("Parts:");
        foreach(var part in FkService.Parts)
        {
            ImGui.SameLine();
            bool partOn = pose == null || !pose.PartsOff.Contains(part);
            if(ImGui.Checkbox($"{part}###fk_part_{part}", ref partOn))
            {
                Checkpoint(name);
                if(pose == null)
                    All[name] = pose = NewPose();
                if(partOn)
                    pose.PartsOff.Remove(part);
                else
                    pose.PartsOff.Add(part);
                _config.Save();
            }
        }

        // Whole pose.
        bool hasSnapshot = snapshot != null;
        if(ImGui.Button("Start from what's on screen###fk_bake") && hasSnapshot)
        {
            Checkpoint(name);
            bool wasLayered = IsLayered(name);
            int n = Bake(name, actor, snapshot!);
            _note = $"FK now holds the pose on screen ({n} bones)" + (wasLayered ? ", switched to Hold the pose." : ". Edit from here.");
        }
        Tooltip("Copies the pose on screen (animation, IK and all) into FK, bone by bone, so it holds still and you can edit it.");
        ImGui.SameLine();
        if(ImGui.Button("Mirror pose###fk_mirror_all") && pose != null && pose.Bones.Count > 0)
        {
            Checkpoint(name);
            _fk.Mirror(actor.Address, actor.ObjectIndex, name, pose.Bones.Keys.ToList(), pose.Layered);
            _note = "Mirrored (left and right swapped).";
        }
        Tooltip("Swaps left and right: each side takes the other's pose, reflected.");
        ImGui.SameLine();
        if(ImGui.Button("Rest pose###fk_rest_all") && hasSnapshot)
        {
            Checkpoint(name);
            int n = RestAll(name, actor, snapshot!);
            _note = $"{n} bones put in the skeleton's rest pose.";
        }
        Tooltip("Every bone to the skeleton's own rest pose (the pose the model is built in).");
        ImGui.SameLine();
        if(ImGui.Button("Clear FK###fk_clear") && pose != null)
        {
            Checkpoint(name);
            pose.Bones.Clear();
            _config.Save();
            _note = "FK cleared: back to the animation.";
        }
        Tooltip("Removes every FK turn on this character (undo brings it back).");
        ImGui.SameLine();
        using(ImRaii.Disabled(!_undo.TryGetValue(name, out var u) || u.Count == 0))
            if(ImGui.Button("Undo###fk_undo"))
                Step(name, back: true);
        ImGui.SameLine();
        using(ImRaii.Disabled(!_redo.TryGetValue(name, out var r) || r.Count == 0))
            if(ImGui.Button("Redo###fk_redo"))
                Step(name, back: false);
        if(_note.Length > 0)
            ImGui.TextDisabled(_note);

        ImGui.Separator();
        if(snapshot == null)
        {
            ImGui.TextDisabled("This character's bones aren't available right now (GPose, and Limber on?).");
            return;
        }

        float listWidth = MathF.Min(300f * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().X * 0.45f);
        using(var list = ImRaii.Child("###fk_bones", new Vector2(listWidth, -1), true))
        {
            if(list.Success)
                DrawBoneList(name, actor, snapshot, pose);
        }
        ImGui.SameLine();
        using(var editor = ImRaii.Child("###fk_editor", new Vector2(-1, -1), false))
        {
            if(editor.Success)
                DrawEditor(name, actor, snapshot);
        }
    }

    private int Bake(string name, ActorRef actor, BoneSnapshot snapshot)
    {
        if(!All.TryGetValue(name, out var pose))
            All[name] = pose = NewPose();
        if(pose.Layered)
        {
            // (holding the whole pose is the hold mode: turns on top of the animation would be added twice)
            pose.Layered = false;
            pose.Bones.Clear();
        }
        bool adult = Limber.Koi.Safety.AdultAllowed(actor.Address);
        int n = 0;
        for(int i = 0; i < snapshot.Count; i++)
        {
            string bone = snapshot.Names[i];
            if(!Bakeable(bone, adult))
                continue;
            if(!pose.Bones.TryGetValue(bone, out var entry))
                pose.Bones[bone] = entry = new FkBoneSettings();
            entry.Rotation = LocalNow(snapshot, i);
            entry.Enabled = true;
            n++;
        }
        pose.Enabled = true;
        _eulerBone = null;
        _config.Save();
        return n;
    }

    // The bones a whole-pose action touches: the skeleton's joints (not physics, face expression or helper bones).
    private static bool Bakeable(string bone, bool adult)
        => HandleCatalog.IsPickable(bone, adult) && bone.StartsWith("j_", StringComparison.Ordinal) && !HandleCatalog.IsFaceBone(bone)
           && !bone.StartsWith("j_mune", StringComparison.Ordinal);

    private int RestAll(string name, ActorRef actor, BoneSnapshot snapshot)
    {
        if(!All.TryGetValue(name, out var pose))
            All[name] = pose = NewPose();
        bool adult = Limber.Koi.Safety.AdultAllowed(actor.Address);
        int n = 0;
        foreach(var bone in snapshot.Names)
        {
            if(!Bakeable(bone, adult) || !_fk.TryGetRest(bone, out var rest))
                continue;
            var target = rest;
            if(pose.Layered)
                target = Quaternion.Normalize(Quaternion.Inverse(BaseOf(name, bone, snapshot, snapshot.IndexOf(bone)).Local) * rest);
            if(!pose.Bones.TryGetValue(bone, out var entry))
                pose.Bones[bone] = entry = new FkBoneSettings();
            entry.Rotation = target;
            entry.Move = false;
            entry.Enabled = true;
            n++;
        }
        pose.Enabled = true;
        _eulerBone = null;
        _config.Save();
        return n;
    }

    // ---- Bone list: the skeleton as a tree (or the matches, when searching)

    private void DrawBoneList(string name, ActorRef actor, BoneSnapshot snapshot, FkPoseSettings? pose)
    {
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("###fk_search", "Search bones (arm, finger, tail...)", ref _search, 64);
        bool adult = Limber.Koi.Safety.AdultAllowed(actor.Address);
        bool Shown(string bone) => HandleCatalog.IsPickable(bone, adult);

        if(_search.Length > 0)
        {
            for(int i = 0; i < snapshot.Count; i++)
            {
                string bone = snapshot.Names[i];
                if(!Shown(bone))
                    continue;
                string label = HandleCatalog.Label(bone);
                if(!bone.Contains(_search, StringComparison.OrdinalIgnoreCase) && !label.Contains(_search, StringComparison.OrdinalIgnoreCase))
                    continue;
                DrawBoneRow(name, bone, label, pose, leaf: true);
            }
            return;
        }

        var children = new Dictionary<int, List<int>>();
        var roots = new List<int>();
        for(int i = 0; i < snapshot.Count; i++)
        {
            if(!Shown(snapshot.Names[i]))
                continue;
            int parent = i < snapshot.Parents.Length ? snapshot.Parents[i] : -1;
            while(parent >= 0 && !Shown(snapshot.Names[parent]))
                parent = snapshot.Parents[parent];
            if(parent < 0)
                roots.Add(i);
            else
            {
                if(!children.TryGetValue(parent, out var list))
                    children[parent] = list = new();
                list.Add(i);
            }
        }
        // The selected bone's branch is open.
        if(_selection.Bone is { } selected && snapshot.IndexOf(selected) is int s and >= 0)
            for(int p = s < snapshot.Parents.Length ? snapshot.Parents[s] : -1; p >= 0; p = snapshot.Parents[p])
                _open.Add(snapshot.Names[p]);

        void Node(int i, int depth)
        {
            string bone = snapshot.Names[i];
            bool has = children.TryGetValue(i, out var kids);
            bool open = DrawBoneRow(name, bone, HandleCatalog.Label(bone), pose, leaf: !has);
            if(has && open && depth < 64)
            {
                ImGui.Indent(10f * ImGuiHelpers.GlobalScale);
                foreach(var k in kids!)
                    Node(k, depth + 1);
                ImGui.Unindent(10f * ImGuiHelpers.GlobalScale);
            }
        }
        foreach(var root in roots)
            Node(root, 0);
    }

    /// <summary>One row: an open / close arrow (if it has bones below it), the name (click to pick), a mark if it's posed.</summary>
    private bool DrawBoneRow(string character, string bone, string label, FkPoseSettings? pose, bool leaf)
    {
        bool open = _open.Contains(bone);
        using var id = ImRaii.PushId(bone);
        if(!leaf)
        {
            if(ImGui.ArrowButton("###arrow", open ? ImGuiDir.Down : ImGuiDir.Right))
            {
                if(open)
                    _open.Remove(bone);
                else
                    _open.Add(bone);
                open = !open;
            }
        }
        else
            ImGui.Dummy(new Vector2(ImGui.GetFrameHeight(), ImGui.GetFrameHeight()));
        ImGui.SameLine();
        bool posed = pose != null && pose.Bones.TryGetValue(bone, out var b) && b.Enabled;
        string text = (posed ? "● " : "") + (label == bone ? bone : $"{label}  ({bone})");
        using(ImRaii.PushColor(ImGuiCol.Text, new Vector4(0.8f, 0.6f, 1f, 1f), posed))
            if(ImGui.Selectable($"{text}###sel", _selection.Bone == bone))
                _selection.Bone = bone;
        return open;
    }

    // ---- The chosen bone

    private void DrawEditor(string name, ActorRef actor, BoneSnapshot snapshot)
    {
        var bone = _selection.Bone;
        if(bone == null || snapshot.IndexOf(bone) < 0)
        {
            ImGui.TextDisabled("Pick a bone in the list, or click its dot in the scene.");
            return;
        }
        if(!HandleCatalog.IsPickable(bone, Limber.Koi.Safety.AdultAllowed(actor.Address)))
        {
            ImGui.TextDisabled("Not available on this character.");
            return;
        }
        int i = snapshot.IndexOf(bone);
        ImGui.Text(HandleCatalog.Label(bone));
        ImGui.SameLine();
        ImGui.TextDisabled($"{bone}  ·  {FkService.PartOf(bone)}");

        bool pinned;
        bool held;
        lock(_rigs.Sync)
        {
            var goal = _rigs.GetRig(actor.Address)?.Find(bone);
            held = goal is { Enabled: true };
            pinned = goal is { Enabled: true, Space: GoalSpace.Pinned };
            if(held)
                ImGui.TextColored(new Vector4(1f, 0.75f, 0.3f, 1f), pinned ? "Pinned: it stays where it is while the rest of the body moves." : "IK has a goal on this bone (IK Posing page): IK decides where it ends up.");
        }

        // Pin / let go (the same pins as the IK Posing page), and which axes the gizmo and quick turns use.
        using(ImRaii.Disabled(Overlay == null))
        {
            if(ImGui.Button(pinned ? "Unpin###fk_pin" : "Pin###fk_pin"))
                Overlay?.TogglePin(actor, bone);
            Tooltip(pinned ? "Stop holding it in place (it keeps any other IK goal it has)." : "Hold this bone where it is now: the body can move and turn, and the limb bends to keep it there.");
            ImGui.SameLine();
            using(ImRaii.Disabled(!held))
            {
                if(ImGui.Button("Let go###fk_letgo"))
                    Overlay?.Release(actor, bone);
            }
            Tooltip("Removes the pin (or any IK goal) from this bone: FK and the animation move it again.");
        }
        ImGui.SameLine();
        ImGui.TextDisabled("|");
        ImGui.SameLine();
        bool world = _config.GizmoWorldAxes;
        if(ImGui.RadioButton("Bone axes###fk_axes_bone", !world))
        {
            _config.GizmoWorldAxes = false;
            _config.Save();
        }
        Tooltip("The gizmo and the quick turns go round the bone's own axes.");
        ImGui.SameLine();
        if(ImGui.RadioButton("World axes###fk_axes_world", world))
        {
            _config.GizmoWorldAxes = true;
            _config.Save();
        }
        Tooltip("The gizmo and the quick turns go round the world's axes (X across, Y up, Z forward in the scene), whatever way the bone faces.");

        var entry = Entry(name, bone, snapshot, create: false);
        bool posed = entry is { Enabled: true };
        if(entry != null)
        {
            bool enabled = entry.Enabled;
            if(ImGui.Checkbox("Use FK on this bone###fk_bone_on", ref enabled))
            {
                Checkpoint(name);
                entry.Enabled = enabled;
                _config.Save();
            }
        }
        else
            ImGui.TextDisabled("Not posed with FK yet: it follows the animation. Turning it starts from where it is now.");

        bool layered = IsLayered(name);
        _fk.TryGetRest(bone, out var rest);
        bool fromRest = _fromRest && !layered;
        Quaternion shownQ;
        if(layered)
        {
            // On top of the animation: the turn added to it (0 / 0 / 0 = as the animation has it).
            shownQ = entry?.Rotation ?? Quaternion.Identity;
            ImGui.TextDisabled("Turn added to the animation (0 / 0 / 0 = as it animates):");
        }
        else
        {
            // Rotation: from the rest pose (0, 0, 0 = rest), or the plain turn relative to the parent.
            ImGui.Checkbox("Angles from the rest pose###fk_from_rest", ref _fromRest);
            Tooltip("On: 0 / 0 / 0 is the skeleton's rest pose for this bone. Off: the bone's whole turn relative to its parent.");
            var local = entry?.Rotation ?? LocalNow(snapshot, i);
            shownQ = fromRest ? Quaternion.Normalize(Quaternion.Inverse(rest) * local) : local;
        }
        if(_eulerBone != bone || !ImGui.IsAnyItemActive())
        {
            _euler = ToEuler(shownQ);
            _eulerBone = bone;
        }
        var euler = _euler;
        ImGui.SetNextItemWidth(-1);
        if(ImGui.DragFloat3("###fk_rot", ref euler, 0.25f, -360f, 360f, "%.1f°"))
        {
            if(ImGui.IsItemActivated() || entry == null)
                Checkpoint(name);
            entry ??= Entry(name, bone, snapshot, create: true)!;
            var q = FromEuler(euler);
            entry.Rotation = fromRest ? Quaternion.Normalize(rest * q) : q;
            entry.Enabled = true;
            _euler = euler;
            _config.Save();
        }
        else if(ImGui.IsItemActivated())
            Checkpoint(name);
        ImGui.TextDisabled("X / Y / Z (degrees, the bone's own axes). Drag, or double-click to type.");

        // Quick turns.
        foreach(var (axis, label) in new[] { (Vector3.UnitX, "X"), (Vector3.UnitY, "Y"), (Vector3.UnitZ, "Z") })
        {
            foreach(var step in new[] { -15f, -5f, 5f, 15f })
            {
                if(ImGui.SmallButton($"{label} {(step > 0 ? "+" : "")}{step:0}###fk_q_{label}{step}"))
                {
                    Checkpoint(name);
                    if(_config.GizmoWorldAxes && snapshot.TryGetWorld(bone, out var worldFrame))
                    {
                        // round the world's axis: turn it where it is in the scene, then back into FK
                        var turned = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(axis, step * MathF.PI / 180f) * worldFrame.Rotation);
                        SetFromWorld(actor, snapshot, bone, turned, null);
                    }
                    else
                    {
                        entry ??= Entry(name, bone, snapshot, create: true)!;
                        entry.Rotation = Quaternion.Normalize(entry.Rotation * Quaternion.CreateFromAxisAngle(axis, step * MathF.PI / 180f));
                        entry.Enabled = true;
                    }
                    _eulerBone = null;
                    _config.Save();
                }
                ImGui.SameLine();
            }
            ImGui.NewLine();
        }

        // Position (optional).
        bool move = entry?.Move ?? false;
        if(ImGui.Checkbox("Also move it###fk_move", ref move))
        {
            Checkpoint(name);
            entry ??= Entry(name, bone, snapshot, create: true)!;
            entry.Move = move;
            if(move)
                entry.Position = layered ? Vector3.Zero : LocalPosNow(snapshot, i);
            _config.Save();
        }
        Tooltip("Holds the bone at a place relative to its parent too (stretches the joint). The gizmo's move mode sets it.");
        ImGui.SameLine();
        bool children = !(entry?.KeepChildren ?? false);
        if(ImGui.Checkbox("Move children###fk_move_children", ref children))
        {
            Checkpoint(name);
            entry ??= Entry(name, bone, snapshot, create: true)!;
            entry.KeepChildren = !children;
            _config.Save();
        }
        Tooltip("On: the bones under this one go with it when you turn or move it (usual FK).\n"
            + "Off: they stay where they are; only this bone turns / moves (bones under it that you posed with FK still follow it).");
        if(entry is { Move: true })
        {
            var cm = entry.Position * 100f;
            ImGui.SetNextItemWidth(-1);
            if(ImGui.DragFloat3("###fk_pos", ref cm, 0.05f, -500f, 500f, "%.2f cm"))
            {
                entry.Position = cm / 100f;
                _config.Save();
            }
            if(ImGui.IsItemActivated())
                Checkpoint(name);
        }

        ImGui.Spacing();
        if(ImGui.Button("Rest###fk_bone_rest"))
        {
            Checkpoint(name);
            entry ??= Entry(name, bone, snapshot, create: true)!;
            entry.Rotation = layered ? Quaternion.Normalize(Quaternion.Inverse(BaseOf(name, bone, snapshot, i).Local) * rest) : rest;
            entry.Move = false;
            entry.Enabled = true;
            _eulerBone = null;
            _config.Save();
        }
        Tooltip("This bone to the skeleton's rest pose.");
        ImGui.SameLine();
        using(ImRaii.Disabled(!posed))
        {
            if(ImGui.Button("Back to animation###fk_bone_clear"))
            {
                Checkpoint(name);
                if(All.TryGetValue(name, out var pose))
                    pose.Bones.Remove(bone);
                _eulerBone = null;
                _config.Save();
            }
        }
        Tooltip("Removes FK from this bone: it follows the animation again.");
        ImGui.SameLine();
        if(ImGui.Button("Mirror to other side###fk_bone_mirror"))
        {
            var other = FkService.OtherSide(bone);
            if(other == null)
                _note = "This bone has no other side (it's in the middle): Mirror pose reflects it.";
            else
            {
                Checkpoint(name);
                entry ??= Entry(name, bone, snapshot, create: true)!;
                // The other side takes this one's pose (and this one keeps it): mirror just the other side's entry.
                MirrorOneWay(name, actor, snapshot, i, bone, other);
            }
        }
        Tooltip("The bone on the other side takes this bone's pose, reflected (and so do the bones below it that FK holds).");

        if(ImGui.Button("Copy###fk_copy"))
            _clipboard = (entry ?? new FkBoneSettings { Rotation = layered ? Quaternion.Identity : LocalNow(snapshot, i) }).Copy();
        ImGui.SameLine();
        using(ImRaii.Disabled(_clipboard == null))
        {
            if(ImGui.Button("Paste###fk_paste") && _clipboard != null)
            {
                Checkpoint(name);
                if(!All.TryGetValue(name, out var pose))
                    All[name] = pose = NewPose();
                pose.Bones[bone] = _clipboard.Copy();
                pose.Bones[bone].Enabled = true;
                _eulerBone = null;
                _config.Save();
            }
        }
        ImGui.SameLine();
        if(ImGui.Button("Branch back to animation###fk_branch_clear"))
        {
            Checkpoint(name);
            if(All.TryGetValue(name, out var pose))
                foreach(var b in Branch(snapshot, i))
                    pose.Bones.Remove(snapshot.Names[b]);
            _eulerBone = null;
            _config.Save();
        }
        Tooltip("This bone and every bone below it follow the animation again.");

        ImGui.Spacing();
        ImGui.TextDisabled("In the scene: click a dot to pick its bone; the rings turn it (Posing settings: the gizmo can also move, "
            + "and use world or the bone's axes). Purple dots are bones FK holds.");
    }

    private static IEnumerable<int> Branch(BoneSnapshot s, int root)
    {
        yield return root;
        for(int i = 0; i < s.Count; i++)
        {
            for(int p = i < s.Parents.Length ? s.Parents[i] : -1; p >= 0; p = s.Parents[p])
                if(p == root)
                {
                    yield return i;
                    break;
                }
        }
    }

    // Copies a bone (and its branch) to the other side, reflected: done by the service (it needs the rest pose),
    // which mirrors both sides; the source side keeps its own values (they're put back after).
    private void MirrorOneWay(string name, ActorRef actor, BoneSnapshot snapshot, int index, string bone, string other)
    {
        if(!All.TryGetValue(name, out var pose))
            return;
        var keep = new Dictionary<string, FkBoneSettings>();
        var bones = new List<string>();
        // This bone's branch (the bones below it on this side) that FK holds.
        foreach(int k in Branch(snapshot, index))
        {
            string b = snapshot.Names[k];
            if(FkService.OtherSide(b) == null || !pose.Bones.TryGetValue(b, out var s))
                continue;
            bones.Add(b);
            keep[b] = s.Copy();
        }
        _pendingKeep = (name, keep);
        _fk.Mirror(actor.Address, actor.ObjectIndex, name, bones.Select(FkService.OtherSide).Where(b => b != null).Select(b => b!).ToList(), pose.Layered);
        _note = $"{HandleCatalog.Label(other)} takes {HandleCatalog.Label(bone)}'s pose.";
    }

    private (string Name, Dictionary<string, FkBoneSettings> Keep)? _pendingKeep;
    private int _pendingFrames;

    /// <summary>Each frame (framework thread): after a one-way mirror, the source side gets its own values back.</summary>
    public void AfterMirror(bool mirrored)
    {
        if(_pendingKeep is not { } pending)
            return;
        if(!mirrored && ++_pendingFrames < 30)
            return;
        _pendingFrames = 0;
        if(mirrored && All.TryGetValue(pending.Name, out var pose))
            foreach(var (b, s) in pending.Keep)
                pose.Bones[b] = s;
        _pendingKeep = null;
        _config.Save();
    }

    private static void Tooltip(string text)
    {
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip(text);
    }
}
