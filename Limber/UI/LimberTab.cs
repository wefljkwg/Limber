using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Limber.Rig;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Limber.UI;

// The "Limber" tab of the main window: IK posing with on-body handles, pins and attachments.
public sealed partial class LimberTab
{
    private static readonly Vector4 Accent = new(0.6f, 0.9f, 1f, 1f);
    private static readonly Vector4 Warning = new(1f, 0.75f, 0.3f, 1f);
    private static readonly string[] MirrorNames = ["Independent", "Mirror", "Opposite"];

    // Bones on the other character people usually want to hold on to.
    private static readonly string[] QuickAttachBones =
    [
        "j_te_l", "j_te_r",
        "j_ude_b_l", "j_ude_b_r",
        "j_ude_a_l", "j_ude_a_r",
        "j_sako_l", "j_sako_r",
        "j_kao", "j_kubi",
        "j_sebo_c", "j_sebo_b",
        "j_kosi",
        "j_asi_a_l", "j_asi_a_r",
        "j_asi_c_l", "j_asi_c_r",
    ];

    private readonly RigService _rigs;
    private readonly Selection _selection;
    private readonly HandleOverlay _overlay;
    private readonly RigSettings _config;

    // The two soft bodies (for the Rigging tab's status line).
    public Limber.BodyRig.SoftBody? SoftBreasts { get; set; }
    public Limber.BodyRig.SoftBody? SoftTesticles { get; set; }
    public Limber.BodyRig.SoftBody? SoftButt { get; set; }
    public Limber.BodyRig.SoftBody? SoftBelly { get; set; }
    // Expansion (Limber Animations): the belly's growth, shown on the belly's line.
    public Limber.Rig.ExpansionService? Expansion { get; set; }
    // Props (for "Attach a collider" in the Attach tab).
    public Limber.BodyRig.PropService? PropService { get; set; }
    // Whole-character move / turn / scale (Actor tab).
    public ActorTransforms? Actors { get; set; }
    private (string Line, string? RigGame)[]? _modelCheck;
    private bool _modelChecking;
    private List<Limber.Gpu.LiveMesh.Found>? _gpuFound;
    private List<string>? _gpuLog;
    private string? _gpuStatus;

    private string _boneSearch = string.Empty;
    private string _attachBoneSearch = string.Empty;
    private string? _attachBone;
    private bool _attachKeepOffset = true;

    /// <summary>Hold on to this bone in the Attach tab (from the Renderer).</summary>
    public void SetAttachTarget(ActorRef target, string bone)
    {
        _selection.AttachTarget = target;
        _attachBone = bone;
    }

    public LimberTab(RigService rigs, Selection selection, HandleOverlay overlay, RigSettings config)
    {
        _rigs = rigs;
        _selection = selection;
        _overlay = overlay;
        _config = config;
    }

    public void Draw()
    {
        DrawGeneral();
        ImGui.Spacing();

        using(var tabs = ImRaii.TabBar("###limber_tabs"))
        {
            if(!tabs.Success)
                return;

            using(var tab = ImRaii.TabItem("Actor"))
                if(tab.Success)
                    DrawActorTab();

            using(var tab = ImRaii.TabItem("Pose"))
                if(tab.Success)
                    DrawPoseTab();

            using(var tab = ImRaii.TabItem("Attach"))
                if(tab.Success)
                    DrawAttachTab();

            using(var tab = ImRaii.TabItem("Settings"))
                if(tab.Success)
                    DrawSettingsTab();
        }
    }

    // ---- Top: on/off and which character

    private void DrawGeneral()
    {
        bool enabled = _config.Enabled;
        if(ImGui.Checkbox("Posing on", ref enabled))
        {
            _config.Enabled = enabled;
            _config.Save();
        }
        Tooltip("Off: nothing is posed; your setup is kept.");

        if(!_rigs.HookReady)
            ImGui.TextColored(Warning, "Couldn't hook the game's bone update (needs an update for this game version).");
        else if(_config.Enabled && !_rigs.IsActive)
            ImGui.TextColored(Warning, "Only works in GPose (see Advanced to change that).");

    }

    /// <summary>"Also run outside GPose" (the main Advanced tab sets it for the whole plugin).</summary>
    public bool WorkOutsideGPose
    {
        get => _config.WorkOutsideGPose;
        set
        {
            _config.WorkOutsideGPose = value;
            _config.Save();
        }
    }

    /// <summary>Which character every tab works on (shown above the main tabs).</summary>
    // ---- Reset to defaults (the button above every page)

    private long _resetArmedUntil;
    private string _resetNote = "";
    private long _resetNoteUntil;

    /// <summary>The "Reset to defaults" button: everything Limber does to the chosen character back to nothing (two clicks).</summary>
    public void DrawResetCharacter()
    {
        var actor = _selection.Actor;
        long now = Environment.TickCount64;
        bool armed = now < _resetArmedUntil;
        using(ImRaii.Disabled(!actor.IsSet))
        {
            if(ImGui.Button(armed ? "Click again to reset###limber_reset_char" : "Reset to defaults###limber_reset_char"))
            {
                if(armed)
                {
                    _resetArmedUntil = 0;
                    _resetNote = ResetCharacter(actor);
                    _resetNoteUntil = now + 4000;
                }
                else
                    _resetArmedUntil = now + 3000;
            }
        }
        Tooltip(actor.IsSet
            ? $"Puts {actor.Name} back as the game has them: lets go of every posed bone and motion, undoes Limber's move / turn / "
              + "size, clears their bone scales and body shape sliders, removes the devices placed on them, and stops Oral on them. "
              + "Click twice (it asks once). Undo brings the pose back."
            : "Choose a character first.");
        if(now < _resetNoteUntil && _resetNote.Length > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(_resetNote);
        }
    }

    /// <summary>Everything Limber does to one character back to nothing. Returns what was reset.</summary>
    public string ResetCharacter(ActorRef actor)
    {
        if(!actor.IsSet)
            return "";
        var done = new List<string>();
        // Pose and motions (undo can bring the pose back).
        Motions?.StopFor(actor.Address, null);
        lock(_rigs.Sync)
        {
            if(_rigs.GetRig(actor.Address)?.Goals.Count > 0)
                done.Add("pose");
            _rigs.Checkpoint();
            _rigs.RemoveRig(actor.Address);
        }
        _selection.Bone = null;
        // Move / turn / size.
        Actors?.Reset(actor.Address);
        string name = actor.Name ?? "";
        if(name.Length > 0)
        {
            // Bone scales and body shape (kept by name).
            if(_config.BoneScales != null && _config.BoneScales.Remove(name))
                done.Add("bone scales");
            if(_config.FkPoses != null && _config.FkPoses.Remove(name))
                done.Add("FK pose");
            if(_config.Throb != null && _config.Throb.TryGetValue(name, out var throb) && throb.Enabled)
            {
                throb.Enabled = false;
                done.Add("throbbing");
            }
            if(_config.LookAt != null && _config.LookAt.TryGetValue(name, out var look) && look.Enabled)
            {
                look.Enabled = false;
                done.Add("look at");
            }
            if(_config.Twerk != null && _config.Twerk.TryGetValue(name, out var twerk) && twerk.Enabled)
            {
                twerk.Enabled = false;
                done.Add("twerking");
            }
            if(_config.Ahegao != null && _config.Ahegao.TryGetValue(name, out var ahegao) && ahegao.Enabled)
            {
                ahegao.Enabled = false;
                done.Add("ahegao");
            }
            if(_config.Expansion != null && _config.Expansion.TryGetValue(name, out var expansion) && (expansion.Breasts.Enabled || expansion.Butt.Enabled || expansion.Penis.Enabled))
            {
                expansion.Breasts.Enabled = expansion.Butt.Enabled = expansion.Penis.Enabled = false;
                done.Add("expansion");
            }
            if(_config.BallThrob != null && _config.BallThrob.TryGetValue(name, out var balls) && balls.Enabled)
            {
                balls.Enabled = false;
                done.Add("testicle throbbing");
            }
            if(_config.BustShapes != null && _config.BustShapes.Remove(name))
                done.Add("breast size");
            if(_config.BodyShapes != null && _config.BodyShapes.TryGetValue(name, out var shape) && shape.Count > 0)
            {
                shape.Clear();
                done.Add("body shape");
            }
            // Devices placed on this character.
            int removed = _config.Props?.RemoveAll(p => string.Equals(p.ParentCharacter, name, StringComparison.Ordinal)) ?? 0;
            if(removed > 0)
                done.Add($"{removed} device{(removed == 1 ? "" : "s")}");
            // Oral on them.
            if(_config.Oral is { Enabled: true } oral && (oral.Giver == name || oral.Receiver == name))
            {
                oral.Enabled = false;
                done.Add("Oral");
            }
        }
        _config.Save();
        return done.Count > 0 ? "Reset: " + string.Join(", ", done) + "." : "Reset (nothing was changed on them).";
    }

    public void DrawActorPicker()
    {
        var characters = _selection.ListCharacters();
        string preview = _selection.Actor.IsSet ? _selection.Actor.Label : "Choose a character";
        ImGui.Text("Character:");
        ImGui.SameLine();

        ImGui.SetNextItemWidth(MathF.Max(150f * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().X - 225f * ImGuiHelpers.GlobalScale));
        using(var combo = ImRaii.Combo("###limber_actor", preview))
        {
            if(combo.Success)
            {
                foreach(var character in characters)
                {
                    bool selected = character.Address == _selection.Actor.Address;
                    if(ImGui.Selectable($"{character.Label}###actor_{character.Address:X}", selected))
                    {
                        _selection.Select(character);
                        _config.FollowTarget = false;
                        _config.Save();
                    }
                }
            }
        }

        ImGui.SameLine();
        bool follow = _config.FollowTarget;
        if(ImGui.Checkbox("Follow my target", ref follow))
        {
            _config.FollowTarget = follow;
            _config.Save();
        }
        Tooltip("Work on whichever character is targeted (the GPose target while in GPose).\nPicking someone from the list turns this off.");

        ImGui.SameLine();
        using(ImRaii.Disabled(!_selection.Actor.IsSet))
        {
            if(ImGui.Button("Redraw###limber_redraw") && _selection.Actor.IsSet)
                Limber.Rig.Appearance.Redraw(_selection.Actor.ObjectIndex);
        }
        Tooltip("Draws the chosen character again (through the mod manager, so its mods stay on): picks up mod and gear changes.");
    }

    /// <summary>"Show Limber handles on the character" (shown under the character picker, above the main tabs).</summary>
    public void DrawShowHandles()
    {
        bool showHandles = _config.ShowHandles;
        if(ImGui.Checkbox("Show Limber handles on the character###limber_show_handles", ref showHandles))
        {
            _config.ShowHandles = showHandles;
            _config.Save();
        }
    }

    // ---- Hotkeys tab (main tab bar)

    /// <summary>Waiting for a key press to set a hotkey (the hotkeys themselves are ignored meanwhile).</summary>
    public static bool CapturingHotkey => _capturing != null;
    private static Hotkey? _capturing;

    public void DrawHotkeysTab()
    {
        bool on = _config.UndoHotkeys;
        if(ImGui.Checkbox("Undo / redo hotkeys on###limber_undo_keys", ref on))
        {
            _config.UndoHotkeys = on;
            _config.Save();
        }
        Tooltip("Undo and redo posing changes. Work while Limber is posing (in GPose, or outside it if that's on).");
        ImGui.Spacing();
        HotkeyRow("Undo", _config.UndoKey ??= new());
        HotkeyRow("Redo", _config.RedoKey ??= new());
        ImGui.Spacing();
        HotkeyRow("Open / close Limber", _config.OpenKey ??= new());
        Tooltip("Opens or closes this window from anywhere in the game (also when /limber1 doesn't get through).");
        ImGui.Spacing();
        ImGui.TextDisabled("Click Change, then press the key with any Ctrl / Shift / Alt you want. Esc cancels.");
    }

    private static int _captureIgnore; // a key already down when Change was clicked (not taken as the new key)

    private void HotkeyRow(string name, Hotkey key)
    {
        using var id = ImRaii.PushId(name);
        ImGui.Text($"{name}:");
        ImGui.SameLine(90 * ImGuiHelpers.GlobalScale);
        if(ReferenceEquals(_capturing, key))
        {
            ImGui.TextColored(Accent, "Press a key...");
            var keys = Plugin.KeyState;
            if(keys[Dalamud.Game.ClientState.Keys.VirtualKey.ESCAPE])
                _capturing = null;
            else
            {
                int down = Hotkey.AnyKeyDown(keys);
                if(down == 0)
                    _captureIgnore = 0;
                else if(down != _captureIgnore)
                {
                    key.Key = down;
                    key.Ctrl = keys[Dalamud.Game.ClientState.Keys.VirtualKey.CONTROL];
                    key.Shift = keys[Dalamud.Game.ClientState.Keys.VirtualKey.SHIFT];
                    key.Alt = keys[Dalamud.Game.ClientState.Keys.VirtualKey.MENU];
                    _capturing = null;
                    _config.Save();
                }
            }
        }
        else
            ImGui.TextColored(Accent, key.Label);
        ImGui.SameLine(260 * ImGuiHelpers.GlobalScale);
        if(ImGui.SmallButton(ReferenceEquals(_capturing, key) ? "Cancel" : "Change"))
        {
            _capturing = ReferenceEquals(_capturing, key) ? null : key;
            _captureIgnore = Hotkey.AnyKeyDown(Plugin.KeyState);
        }
        ImGui.SameLine();
        if(ImGui.SmallButton("Clear"))
        {
            key.Key = 0;
            if(ReferenceEquals(_capturing, key))
                _capturing = null;
            _config.Save();
        }
    }

    // ---- Actor tab: move, turn and scale the whole character

    private void DrawActorTab()
    {
        var actor = _selection.Actor;
        if(!actor.IsSet || Actors == null)
        {
            ImGui.TextDisabled("Choose a character first.");
            return;
        }
        var current = Actors.Get(actor.Address);
        var move = current?.Move ?? Vector3.Zero;
        var turn = current?.Turn ?? Vector3.Zero;
        float scale = current?.Scale ?? 1f;
        bool keep = current?.KeepWhileMoving ?? true;
        bool changed = false;
        bool facing = current?.Facing ?? false;
        ImGui.TextUnformatted("Axes:");
        ImGui.SameLine();
        if(ImGui.RadioButton("World###limber_actor_world", !facing))
            facing = false;
        Tooltip("Move and turn along the world's axes, whichever way the character faces.");
        ImGui.SameLine();
        if(ImGui.RadioButton("Character facing###limber_actor_facing", facing))
            facing = true;
        Tooltip("Move and turn along the way the character faces: forward / back, left / right, up; turns about its own axes.");
        if(facing != (current?.Facing ?? false))
        {
            var o = Actors.GetOrAdd(actor.Address, actor.ObjectIndex);
            lock(Actors.Sync)
                o.Facing = facing;
        }
        ImGui.SetNextItemWidth(-90 * ImGuiHelpers.GlobalScale);
        changed |= ImGui.DragFloat3("Move (m)###limber_actor_move", ref move, 0.005f, -50f, 50f, "%.3f");
        Tooltip(facing ? "Sideways, up, forward (the way the character faces)." : "World X, up, world Z.");
        ImGui.SetNextItemWidth(-90 * ImGuiHelpers.GlobalScale);
        changed |= ImGui.DragFloat3("Turn (°)###limber_actor_turn", ref turn, 0.5f, -180f, 180f, "%.1f");
        Tooltip("Yaw (around), pitch (forward / back), roll (sideways).");
        ImGui.SetNextItemWidth(-90 * ImGuiHelpers.GlobalScale);
        changed |= ImGui.DragFloat("Scale###limber_actor_scale", ref scale, 0.005f, Limber.Rig.ActorTransforms.MinScale, Limber.Rig.ActorTransforms.MaxScale, "%.4f", ImGuiSliderFlags.AlwaysClamp | ImGuiSliderFlags.Logarithmic);
        if(changed)
        {
            var o = Actors.GetOrAdd(actor.Address, actor.ObjectIndex);
            lock(Actors.Sync)
            {
                o.Move = move;
                o.Turn = turn;
                o.Scale = scale;
            }
        }
        if(ImGui.Checkbox("Keep while moving###limber_actor_keep", ref keep))
        {
            var o = Actors.GetOrAdd(actor.Address, actor.ObjectIndex);
            lock(Actors.Sync)
                o.KeepWhileMoving = keep;
        }
        Tooltip("On (default): applied to the skeleton, so it stays on while the character walks or turns, and when another\nplugin keeps setting the character's place. Outside GPose: needs \"Also run outside GPose\" (Advanced).");
        bool priority = current?.Priority ?? true;
        if(ImGui.Checkbox("Take priority over other plugins###limber_actor_priority", ref priority))
        {
            var o = Actors.GetOrAdd(actor.Address, actor.ObjectIndex);
            lock(Actors.Sync)
                o.Priority = priority;
        }
        Tooltip("On (default): Limber's move / turn / scale goes on top of whatever another plugin sets.\n" +
                "Their position, rotation and scale still count: Limber's are added to them, never wiped by them.\n" +
                "With \"Keep while moving\" on, Limber already works on the skeleton after their posing; this also covers it off.");

        if(ImGui.Button("Reset###limber_actor_reset"))
            Actors.Reset(actor.Address);
        ImGui.TextDisabled("Only on your screen.");
    }

    // ---- Pose tab

    private void DrawPoseTab()
    {
        var actor = _selection.Actor;
        if(!actor.IsSet)
        {
            ImGui.TextDisabled("Choose a character first.");
            return;
        }

        // Laid out like the FK Posing page: the whole-pose buttons on top, the skeleton on the left, the chosen
        // bone (and the held bones) on the right.
        DrawHistoryRow(actor);

        int count;
        lock(_rigs.Sync)
            count = _rigs.GetRig(actor.Address)?.Goals.Count ?? 0;
        if(ImGui.Button("Pin feet###limber_pin_feet"))
        {
            _rigs.Checkpoint();
            PinIfFree(actor, "j_asi_d_l");
            PinIfFree(actor, "j_asi_d_r");
        }
        ImGui.SameLine();
        if(ImGui.Button("Pin hands###limber_pin_hands"))
        {
            _rigs.Checkpoint();
            PinIfFree(actor, "j_te_l");
            PinIfFree(actor, "j_te_r");
        }
        ImGui.SameLine();
        using(ImRaii.Disabled(count == 0))
        {
            if(ImGui.Button("Let go of all###limber_let_go_all"))
            {
                Motions?.StopFor(actor.Address, null);
                lock(_rigs.Sync)
                {
                    _rigs.Checkpoint();
                    _rigs.RemoveRig(actor.Address);
                }
                _selection.Bone = null;
            }
        }
        ImGui.SameLine();
        ImGui.TextDisabled($"({count} bone{(count == 1 ? "" : "s")} held)");
        ImGui.TextDisabled("Drag a dot to pose.  Right-click: pin.  Middle-click: let go.  Finger presets: the Hands page.");

        ImGui.Separator();
        BoneSnapshot? snapshot;
        lock(_rigs.Sync)
            snapshot = _rigs.GetSnapshot(actor.Address);
        if(snapshot == null)
        {
            ImGui.TextDisabled("This character's bones aren't available right now (GPose, and Limber on?).");
            return;
        }

        float listWidth = MathF.Min(300f * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().X * 0.45f);
        using(var list = ImRaii.Child("###ik_bones", new Vector2(listWidth, -1), true))
        {
            if(list.Success)
                DrawIkBoneList(actor, snapshot);
        }
        ImGui.SameLine();
        using(var editor = ImRaii.Child("###ik_editor", new Vector2(-1, -1), false))
        {
            if(editor.Success)
            {
                if(ImGui.CollapsingHeader("Selected bone###limber_pose_selected", ImGuiTreeNodeFlags.DefaultOpen))
                    DrawSelectedBone(actor);
                if(ImGui.CollapsingHeader($"Held bones ({count})###limber_pose_held", ImGuiTreeNodeFlags.DefaultOpen))
                {
                    lock(_rigs.Sync)
                    {
                        var rig = _rigs.GetRig(actor.Address);
                        if(rig == null || rig.Goals.Count == 0)
                            ImGui.TextDisabled("None yet.");
                        else
                            DrawGoalTable(actor, rig);
                    }
                }
            }
        }
    }

    private readonly HashSet<string> _ikOpen = new(StringComparer.Ordinal);

    // The skeleton as a tree (or the matches, when searching), like the FK page's: held bones marked.
    private void DrawIkBoneList(ActorRef actor, BoneSnapshot snapshot)
    {
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("###limber_bone_search", "Search bones (arm, finger, tail...)", ref _boneSearch, 64);
        bool adult = Limber.Koi.Safety.AdultAllowed(actor.Address);
        bool Shown(string bone) => HandleCatalog.IsPickable(bone, adult);
        Dictionary<string, (bool Enabled, GoalSpace Space)> held = new(StringComparer.Ordinal);
        lock(_rigs.Sync)
            if(_rigs.GetRig(actor.Address) is { } rig)
                foreach(var g in rig.Goals)
                    held[g.Bone] = (g.Enabled, g.Space);

        if(_boneSearch.Length > 0)
        {
            for(int i = 0; i < snapshot.Count; i++)
            {
                string bone = snapshot.Names[i];
                if(!Shown(bone))
                    continue;
                string label = HandleCatalog.Label(bone);
                if(!bone.Contains(_boneSearch, StringComparison.OrdinalIgnoreCase) && !label.Contains(_boneSearch, StringComparison.OrdinalIgnoreCase))
                    continue;
                DrawIkBoneRow(bone, label, held, leaf: true);
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
                if(!children.TryGetValue(parent, out var kids))
                    children[parent] = kids = new();
                kids.Add(i);
            }
        }
        // The selected bone's branch is open.
        if(_selection.Bone is { } selected && snapshot.IndexOf(selected) is int s and >= 0)
            for(int p = s < snapshot.Parents.Length ? snapshot.Parents[s] : -1; p >= 0; p = snapshot.Parents[p])
                _ikOpen.Add(snapshot.Names[p]);

        void Node(int i, int depth)
        {
            string bone = snapshot.Names[i];
            bool has = children.TryGetValue(i, out var kids);
            bool open = DrawIkBoneRow(bone, HandleCatalog.Label(bone), held, leaf: !has);
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

    /// <summary>One row: an open / close arrow, the name (click to pick), marked if it's held (pinned in its own colour).</summary>
    private bool DrawIkBoneRow(string bone, string label, Dictionary<string, (bool Enabled, GoalSpace Space)> held, bool leaf)
    {
        bool open = _ikOpen.Contains(bone);
        using var id = ImRaii.PushId(bone);
        if(!leaf)
        {
            if(ImGui.ArrowButton("###arrow", open ? ImGuiDir.Down : ImGuiDir.Right))
            {
                if(open)
                    _ikOpen.Remove(bone);
                else
                    _ikOpen.Add(bone);
                open = !open;
            }
        }
        else
            ImGui.Dummy(new Vector2(ImGui.GetFrameHeight(), ImGui.GetFrameHeight()));
        ImGui.SameLine();
        bool isHeld = held.TryGetValue(bone, out var h) && h.Enabled;
        bool pinned = isHeld && h.Space == GoalSpace.Pinned;
        string text = (pinned ? "◆ " : isHeld ? "● " : "") + (label == bone ? bone : $"{label}  ({bone})");
        var colour = pinned ? new Vector4(1f, 0.55f, 0.35f, 1f) : new Vector4(1f, 0.85f, 0.35f, 1f);
        using(ImRaii.PushColor(ImGuiCol.Text, colour, isHeld))
            if(ImGui.Selectable($"{text}###sel", _selection.Bone == bone))
                _selection.Bone = bone;
        return open;
    }

    private int _handSide = 2; // 0 left, 1 right, 2 both
    private float _handAmount = 1f;
    private Limber.Rig.HandPose? _handLast;
    private static readonly string[] HandSides = ["Left", "Right", "Both"];

    // Preset finger poses: only the fingers turn, the hand stays free (yellow dots).
    private void DrawHandPoses(ActorRef actor)
    {
        for(int i = 0; i < HandSides.Length; i++)
        {
            if(i > 0)
                ImGui.SameLine();
            if(ImGui.RadioButton($"{HandSides[i]}###limber_hand_side{i}", _handSide == i))
                _handSide = i;
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1);
        bool amountChanged = ImGui.SliderFloat("###limber_hand_amount", ref _handAmount, 0f, 1f, "Amount %.2f");
        if(ImGui.IsItemActivated() && _handLast != null)
            _rigs.Checkpoint();
        if(amountChanged && _handLast != null)
            ApplyHandPose(actor, _handLast, checkpoint: false);

        var style = ImGui.GetStyle();
        foreach(var group in Limber.Rig.HandPoses.Groups)
        {
            ImGui.Spacing();
            ImGui.TextDisabled(group);
            float avail = ImGui.GetContentRegionAvail().X;
            float x = 0f;
            foreach(var pose in Limber.Rig.HandPoses.All.Where(p => p.Group == group))
            {
                float w = ImGui.CalcTextSize(pose.Name).X + style.FramePadding.X * 2f;
                if(x > 0f && x + style.ItemSpacing.X + w <= avail)
                {
                    ImGui.SameLine();
                    x += style.ItemSpacing.X + w;
                }
                else
                    x = w;

                bool current = _handLast == pose;
                using(ImRaii.PushColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.ButtonActive), current))
                {
                    if(ImGui.Button($"{pose.Name}###limber_hand_{pose.Name}"))
                    {
                        _handLast = pose;
                        ApplyHandPose(actor, pose, checkpoint: true);
                    }
                }
            }
        }

        ImGui.Spacing();
        // Grab: the fingers wrap onto the skin they're over and stay on it as it moves.
        lock(_rigs.Sync)
        {
            _rigs.Grab.Hands.TryGetValue(actor.Address, out var grab);
            bool on = grab != null && (_handSide == 0 ? grab.Left : _handSide == 1 ? grab.Right : grab.Left && grab.Right);
            if(ImGui.Checkbox("Grab###limber_hand_grab", ref on))
            {
                grab ??= _rigs.Grab.Hands[actor.Address] = new FingerGrab.Hand { Index = actor.ObjectIndex };
                if(_handSide != 1)
                    grab.Left = on;
                if(_handSide != 0)
                    grab.Right = on;
            }
            Tooltip("The fingers curl onto the skin under them (the breasts) and stay on it as it moves, on top of the hand pose.\nWorks best with a hand preset close to the shape, and hand colliders on for the dent.");
            if(grab != null)
            {
                ImGui.SameLine();
                float grip = grab.Grip;
                ImGui.SetNextItemWidth(140 * ImGuiHelpers.GlobalScale);
                if(ImGui.SliderFloat("Grip (mm)###limber_hand_grip", ref grip, 0f, 20f, "%.0f"))
                    grab.Grip = grip;
                Tooltip("How far the fingers sink into the skin.");
                float rigidity = grab.Rigidity * 100f;
                ImGui.SetNextItemWidth(140 * ImGuiHelpers.GlobalScale);
                if(ImGui.SliderFloat("Rigidity###limber_hand_rigidity", ref rigidity, 0f, 100f, "%.0f%%"))
                    grab.Rigidity = rigidity / 100f;
                Tooltip("How firmly each finger keeps the spot of skin it holds and resists turning: less sliding and creeping, but slower to follow the hand.");
                ImGui.SameLine();
                float spread = grab.Spread * 100f;
                ImGui.SetNextItemWidth(140 * ImGuiHelpers.GlobalScale);
                if(ImGui.SliderFloat("Spread###limber_hand_spread", ref spread, 0f, 100f, "%.0f%%"))
                    grab.Spread = spread / 100f;
                Tooltip("Fans the four fingers out evenly across the hand (every gap the same), wider as it goes up. The thumb isn't moved.");
            }
            {
                bool locked = grab != null && (_handSide == 0 ? grab.LockLeft : _handSide == 1 ? grab.LockRight : grab.LockLeft && grab.LockRight);
                if(ImGui.Checkbox("Lock###limber_hand_lock", ref locked))
                {
                    grab ??= _rigs.Grab.Hands[actor.Address] = new FingerGrab.Hand { Index = actor.ObjectIndex };
                    grab.Index = actor.ObjectIndex;
                    if(_handSide != 1)
                        grab.LockLeft = locked;
                    if(_handSide != 0)
                        grab.LockRight = locked;
                }
                Tooltip("The breast under the hand is carried with it: move the hand and the breast goes along (all of it with Whole breast on, else just the part under the hand). The live mesh keeps working on top, and Grab keeps the fingers as they are while locked.\nLock with the hand where you want to hold it.");
                if(grab != null && (grab.LockLeft || grab.LockRight))
                {
                    ImGui.SameLine();
                    float lockStrength = grab.LockStrength * 100f;
                    ImGui.SetNextItemWidth(140 * ImGuiHelpers.GlobalScale);
                    if(ImGui.SliderFloat("Lock strength###limber_hand_lock_strength", ref lockStrength, 0f, 100f, "%.0f%%"))
                        grab.LockStrength = lockStrength / 100f;
                    Tooltip("How fully the breast follows the hand (lower: it lags and pulls back toward the body).");
                    ImGui.SameLine();
                    bool whole = grab.LockWhole;
                    if(ImGui.Checkbox("Whole breast###limber_hand_lock_whole", ref whole))
                        grab.LockWhole = whole;
                    Tooltip("On: the whole breast the hand is on goes with it (still attached at the chest, so it stretches there), good for pressing them together.\nOff: only the part under the hand is carried.");
                }
            }
        }

        if(ImGui.Button("Back to the animation###limber_hand_clear"))
        {
            lock(_rigs.Sync)
            {
                var rig = _rigs.GetRig(actor.Address);
                if(rig != null)
                {
                    _rigs.Checkpoint();
                    if(_handSide != 1)
                        Limber.Rig.HandPoses.Clear(rig, right: false);
                    if(_handSide != 0)
                        Limber.Rig.HandPoses.Clear(rig, right: true);
                    if(rig.Goals.Count == 0)
                        _rigs.RemoveRig(actor.Address);
                }
            }
            _handLast = null;
        }
    }

    private void ApplyHandPose(ActorRef actor, Limber.Rig.HandPose pose, bool checkpoint)
    {
        lock(_rigs.Sync)
        {
            if(checkpoint)
                _rigs.Checkpoint();
            var snapshot = _rigs.GetSnapshot(actor.Address);
            Func<string, bool>? has = snapshot == null ? null : name => snapshot.IndexOf(name) >= 0;
            var rig = _rigs.GetOrCreateRig(actor.Address, actor.ObjectIndex, actor.Name);
            if(_handSide != 1)
                Limber.Rig.HandPoses.Apply(rig, pose, right: false, _handAmount, has);
            if(_handSide != 0)
                Limber.Rig.HandPoses.Apply(rig, pose, right: true, _handAmount, has);
            if(rig.Goals.Count == 0)
                _rigs.RemoveRig(actor.Address);
        }
    }

    private void PinIfFree(ActorRef actor, string bone)
    {
        bool pinned;
        lock(_rigs.Sync)
            pinned = _rigs.GetRig(actor.Address)?.Find(bone)?.Space == GoalSpace.Pinned;

        if(!pinned)
            _overlay.TogglePin(actor, bone, checkpoint: false);
    }

    private void DrawGoalTable(ActorRef actor, ActorRig rig)
    {
        using var table = ImRaii.Table("###limber_goals", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp);
        if(!table.Success)
            return;

        ImGui.TableSetupColumn("Bone", ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn("State", ImGuiTableColumnFlags.WidthStretch, 1.4f);
        ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, 150 * ImGuiHelpers.GlobalScale);

        string? remove = null;

        foreach(var goal in rig.Goals.OrderBy(g => g.SortDepth).ThenBy(g => g.Bone))
        {
            using var id = ImRaii.PushId(goal.Bone);
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            bool selected = _selection.Bone == goal.Bone;
            if(ImGui.Selectable(HandleCatalog.Label(goal.Bone), selected, ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowItemOverlap))
                _selection.Bone = goal.Bone;

            ImGui.TableNextColumn();
            string state = goal.Space switch
            {
                GoalSpace.Pinned => "Pinned in place",
                GoalSpace.Attached => $"On {goal.ParentActorName}: {HandleCatalog.Label(goal.ParentBone ?? "?")}",
                _ => goal.TurnOnly ? "Turned" : "Held, follows body",
            };
            if(!goal.Enabled)
                state = "Off - " + state;
            if(goal.LockRotation)
                state += ", rotation held";
            if(goal.LastSolveFailed && goal.Enabled)
                state += " (can't reach)";
            if(Motions?.For(actor.Address, goal.Bone) is { } motion)
                state += $", moved by the motion device \"{motion.Name}\" (Let go turns it off)";
            ImGui.TextWrapped(state);

            ImGui.TableNextColumn();
            bool on = goal.Enabled;
            if(ImGui.Checkbox("###on", ref on))
            {
                _rigs.Checkpoint();
                goal.Enabled = on;
            }
            Tooltip("On / off");

            ImGui.SameLine();
            if(goal.Space != GoalSpace.Attached)
            {
                bool pinned = goal.Space == GoalSpace.Pinned;
                if(ImGui.SmallButton(pinned ? "Unpin" : "Pin"))
                    _overlay.TogglePin(actor, goal.Bone);
            }
            else if(ImGui.SmallButton("Detach"))
            {
                _rigs.Checkpoint();
                RigService.Detach(goal);
            }

            ImGui.SameLine();
            if(ImGui.SmallButton("Let go"))
                remove = goal.Bone;
        }

        if(remove != null)
        {
            Motions?.StopFor(actor.Address, remove);
            _rigs.Checkpoint();
            rig.Remove(remove);
            if(rig.Goals.Count == 0)
                _rigs.RemoveRig(actor.Address);
        }
    }

    private void DrawSelectedBone(ActorRef actor)
    {
        var bone = _selection.Bone;
        if(bone == null)
        {
            ImGui.TextDisabled("Click a dot, or pick one under All bones.");
            return;
        }

        ImGui.TextColored(Accent, HandleCatalog.Label(bone));
        if(HandleCatalog.Label(bone) != bone)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"({bone})");
        }
        ImGui.SameLine();
        if(ImGui.SmallButton("Let go###limber_selected_let_go"))
            _overlay.Release(actor, bone);

        DrawGizmoMode();

        lock(_rigs.Sync)
        {
            var goal = _rigs.GetRig(actor.Address)?.Find(bone);
            if(goal == null)
            {
                if(ImGui.Button("Hold it where it is"))
                {
                    _rigs.Checkpoint();
                    var rig = _rigs.GetOrCreateRig(actor.Address, actor.ObjectIndex, actor.Name);
                    rig.GetOrAdd(bone);
                }
                return;
            }

            bool lockRotation = goal.LockRotation;
            if(ImGui.Checkbox("Hold rotation", ref lockRotation))
            {
                _rigs.Checkpoint();
                goal.LockRotation = lockRotation;
                if(lockRotation)
                    goal.CaptureRotation = true;
            }
            Tooltip("Off: it turns along with the bones above it.");

            if(goal.Space == GoalSpace.FollowBody)
            {
                ImGui.SameLine();
                bool hold = goal.HoldPosition;
                if(ImGui.Checkbox("Hold place", ref hold))
                {
                    _rigs.Checkpoint();
                    if(hold)
                        goal.KeepPosition();
                    else
                    {
                        goal.HoldPosition = false;
                        goal.LockRotation = true;
                        goal.CaptureRotation = true;
                    }
                }
                Tooltip("Off: it only turns; nothing above it bends.");
            }

            if(Limber.Rig.RigService.OtherSide(bone) != null)
            {
                int mirror = (int)goal.Mirror;
                ImGui.SetNextItemWidth(130 * ImGuiHelpers.GlobalScale);
                if(ImGui.Combo("Other side###limber_mirror", ref mirror, MirrorNames, MirrorNames.Length))
                {
                    _rigs.Checkpoint();
                    goal.Mirror = (MirrorMode)mirror;
                }
            }

            if(goal.Solve == GoalSolve.Chain && goal.HoldPosition)
            {
                int length = goal.ChainLength;
                ImGui.SetNextItemWidth(130 * ImGuiHelpers.GlobalScale);
                bool lengthChanged = ImGui.SliderInt("Bones that bend", ref length, 1, 8);
                if(ImGui.IsItemActivated())
                    _rigs.Checkpoint();
                if(lengthChanged)
                    goal.ChainLength = Math.Clamp(length, 1, 8);
            }
        }
    }

    private void DrawBonePicker(ActorRef actor)
    {

        BoneSnapshot? snapshot;
        lock(_rigs.Sync)
            snapshot = _rigs.GetSnapshot(actor.Address);

        if(snapshot == null)
        {
            ImGui.TextDisabled("This character's bones aren't available right now.");
            return;
        }

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("###limber_bone_search", "Search (tail, finger, ear...)", ref _boneSearch, 64);

        using var child = ImRaii.Child("###limber_bone_list", new Vector2(-1, 140 * ImGuiHelpers.GlobalScale), true);
        if(!child.Success)
            return;

        bool adult = Limber.Koi.Safety.AdultAllowed(actor.Address);
        foreach(var name in snapshot.Names)
        {
            if(!HandleCatalog.IsPickable(name, adult))
                continue;

            string label = HandleCatalog.Label(name);
            if(_boneSearch.Length > 0
                && !name.Contains(_boneSearch, StringComparison.OrdinalIgnoreCase)
                && !label.Contains(_boneSearch, StringComparison.OrdinalIgnoreCase))
                continue;

            string text = label == name ? name : $"{label}  ({name})";
            if(ImGui.Selectable($"{text}###pick_{name}", _selection.Bone == name))
                _selection.Bone = name;
        }
    }

    // ---- Attach tab

    private void DrawAttachTab()
    {
        var actor = _selection.Actor;
        if(!actor.IsSet)
        {
            ImGui.TextDisabled("Choose a character first.");
            return;
        }

        float labelWidth = 90 * ImGuiHelpers.GlobalScale;
        ImGui.TextUnformatted("This bone");
        ImGui.SameLine(labelWidth);
        if(_selection.Bone != null)
            ImGui.TextColored(Accent, HandleCatalog.Label(_selection.Bone));
        else
            ImGui.TextDisabled("click a dot");

        ImGui.TextUnformatted("Hold on to");
        ImGui.SameLine(labelWidth);
        var characters = _selection.ListCharacters();
        string preview = _selection.AttachTarget.IsSet ? _selection.AttachTarget.Label : "Choose a character";
        ImGui.SetNextItemWidth(-1);
        using(var combo = ImRaii.Combo("###limber_attach_actor", preview))
        {
            if(combo.Success)
            {
                foreach(var character in characters)
                {
                    string label = character.Address == actor.Address ? $"{character.Label} (this character)" : character.Label;
                    if(ImGui.Selectable($"{label}###attach_{character.Address:X}", character.Address == _selection.AttachTarget.Address))
                    {
                        _selection.AttachTarget = character;
                        _attachBone = null;
                    }
                }
            }
        }

        var target = _selection.AttachTarget;
        if(target.IsSet)
        {
            BoneSnapshot? snapshot;
            lock(_rigs.Sync)
                snapshot = _rigs.GetSnapshot(target.Address);

            ImGui.TextUnformatted("Their bone");
            ImGui.SameLine(labelWidth);
            if(_attachBone != null)
                ImGui.TextColored(Accent, HandleCatalog.Label(_attachBone));
            else
                ImGui.TextDisabled("pick one below");

            if(snapshot == null)
            {
                ImGui.TextDisabled("Reading their skeleton...");
            }
            else
            {
                float quarter = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X * 3) * 0.25f;
                int column = 0;
                foreach(var name in QuickAttachBones)
                {
                    if(snapshot.IndexOf(name) < 0)
                        continue;
                    if(column % 4 != 0)
                        ImGui.SameLine();
                    bool isChosen = _attachBone == name;
                    using(ImRaii.PushColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.ButtonActive), isChosen))
                    {
                        if(ImGui.Button($"{HandleCatalog.Label(name)}###quick_{name}", new Vector2(quarter, 0)))
                            _attachBone = name;
                    }
                    column++;
                }

                ImGui.SetNextItemWidth(-1);
                ImGui.InputTextWithHint("###limber_attach_search", "Search their bones", ref _attachBoneSearch, 64);
                if(_attachBoneSearch.Length > 0)
                {
                    using var list = ImRaii.Child("###limber_attach_list", new Vector2(-1, 110 * ImGuiHelpers.GlobalScale), true);
                    if(list.Success)
                    {
                        bool adult = Limber.Koi.Safety.AdultAllowed(target.Address);
                        foreach(var name in snapshot.Names)
                        {
                            if(!HandleCatalog.IsPickable(name, adult))
                                continue; // body mod bones: adult bodies only
                            string label = HandleCatalog.Label(name);
                            if(!name.Contains(_attachBoneSearch, StringComparison.OrdinalIgnoreCase)
                                && !label.Contains(_attachBoneSearch, StringComparison.OrdinalIgnoreCase))
                                continue;

                            if(ImGui.Selectable($"{(label == name ? name : $"{label}  ({name})")}###attach_pick_{name}", _attachBone == name))
                                _attachBone = name;
                        }
                    }
                }
            }
        }

        ImGui.Spacing();
        bool canAttach = _selection.Bone != null && target.IsSet && _attachBone != null
            && !(target.Address == actor.Address && _attachBone == _selection.Bone);
        ImGui.Checkbox("Keep offset", ref _attachKeepOffset);
        Tooltip("On: it stays where it is now. Off: it snaps onto their bone.");
        ImGui.SameLine();
        using(ImRaii.Disabled(!canAttach))
        {
            if(ImGui.Button("Attach###limber_attach_button", new Vector2(-1, 0)) && canAttach)
                Attach(actor, _selection.Bone!, target, _attachBone!);
        }

        using(ImRaii.Disabled(!target.IsSet || _attachBone == null))
        {
            if(ImGui.Button("Put a collider on their bone###limber_attach_prop", new Vector2(-1, 0)) && target.IsSet && _attachBone != null)
            {
                var prop = new Limber.BodyRig.PropCollider
                {
                    Name = $"Collider on {HandleCatalog.Label(_attachBone)}",
                    ParentCharacter = target.Name,
                    ParentIndexHint = target.ObjectIndex,
                    ParentBone = _attachBone,
                };
                (_config.Props ??= new()).Add(prop);
                PropService?.Refresh();
                _config.Save();
            }
        }
        Tooltip("Follows the bone and pushes soft bodies. Size it on the Devices page.");

        int attached;
        lock(_rigs.Sync)
            attached = _rigs.Rigs.Sum(r => r.Goals.Count(g => g.Space == GoalSpace.Attached));
        ImGui.Spacing();
        if(ImGui.CollapsingHeader($"Attached ({attached})###limber_attached_list", ImGuiTreeNodeFlags.DefaultOpen))
        {
            lock(_rigs.Sync)
            {
                foreach(var rig in _rigs.Rigs.ToList())
                {
                    foreach(var goal in rig.Goals.Where(g => g.Space == GoalSpace.Attached).ToList())
                    {
                        using var id = ImRaii.PushId($"{rig.Address:X}_{goal.Bone}");
                        if(ImGui.SmallButton("Detach"))
                        {
                            _rigs.Checkpoint();
                            RigService.Detach(goal);
                        }
                        ImGui.SameLine();
                        ImGui.TextWrapped($"{rig.Name}: {HandleCatalog.Label(goal.Bone)}  ->  {goal.ParentActorName}: {HandleCatalog.Label(goal.ParentBone ?? "?")}");
                    }
                }
                if(attached == 0)
                    ImGui.TextDisabled("Nothing attached.");
            }
        }
    }

    private void Attach(ActorRef actor, string bone, ActorRef target, string targetBone)
    {
        lock(_rigs.Sync)
        {
            _rigs.Checkpoint();
            var rig = _rigs.GetOrCreateRig(actor.Address, actor.ObjectIndex, actor.Name);
            var goal = rig.GetOrAdd(bone);

            goal.Space = GoalSpace.Attached;
            goal.HoldPosition = true;
            goal.ParentActor = target.Address;
            goal.ParentObjectIndex = target.ObjectIndex;
            goal.ParentActorName = target.Name;
            goal.ParentBone = targetBone;
            goal.LockRotation = true;
            goal.Enabled = true;

            if(_attachKeepOffset)
            {
                goal.RequestCapture();
            }
            else
            {
                goal.Local.Position = Vector3.Zero;
                goal.HasTarget = true;
                goal.CapturePosition = false;
                goal.CaptureRotation = true;
            }
        }
    }

    // ---- Body rig tab: build the breast grid skeleton mod

    private List<string>? _rigReport;
    private System.Threading.Tasks.Task<Limber.BodyRig.RigBuilder.Result>? _rigBuild;

    private Limber.BodyRig.RigPart CurrentPart
        => _config.RigPartKey == Limber.BodyRig.RigParts.Testicles.Key ? Limber.BodyRig.RigParts.Testicles
            : _config.RigPartKey == Limber.BodyRig.RigParts.Butt.Key ? Limber.BodyRig.RigParts.Butt
            : _config.RigPartKey == Limber.BodyRig.RigParts.Belly.Key ? Limber.BodyRig.RigParts.Belly : Limber.BodyRig.RigParts.Breasts;

    private Limber.BodyRig.BreastRigShape ShapeFor(Limber.BodyRig.RigPart part)
    {
        if(part == Limber.BodyRig.RigParts.Testicles)
            return _config.TesticleLattice ??= part.DefaultShape();
        if(part == Limber.BodyRig.RigParts.Butt)
            return _config.ButtLattice ??= part.DefaultShape();
        if(part == Limber.BodyRig.RigParts.Belly)
            return _config.BellyLattice ??= part.DefaultShape();
        if(part.Shape)
        {
            _config.ShapeLattices ??= new();
            if(!_config.ShapeLattices.TryGetValue(part.Key, out var placed))
                _config.ShapeLattices[part.Key] = placed = part.DefaultShape();
            return placed;
        }
        if(part == Limber.BodyRig.RigParts.LegacyTesticles)
            return _config.TesticleRig ??= part.DefaultShape();
        if(part == Limber.BodyRig.RigParts.LegacyBreasts)
            return _config.BreastRig ??= part.DefaultShape();
        return _config.BreastLattice ??= part.DefaultShape();
    }

    private void SetShape(Limber.BodyRig.RigPart part, Limber.BodyRig.BreastRigShape shape)
    {
        if(part == Limber.BodyRig.RigParts.Testicles)
            _config.TesticleLattice = shape;
        else if(part == Limber.BodyRig.RigParts.Breasts)
            _config.BreastLattice = shape;
        else if(part == Limber.BodyRig.RigParts.Butt)
            _config.ButtLattice = shape;
        else if(part == Limber.BodyRig.RigParts.Belly)
            _config.BellyLattice = shape;
        else if(part.Shape)
            (_config.ShapeLattices ??= new())[part.Key] = shape;
    }

    private Limber.BodyRig.SoftBodySettings SoftFor(Limber.BodyRig.RigPart part)
        => part == Limber.BodyRig.RigParts.Testicles
            ? _config.SoftBodyTesticles ??= Limber.BodyRig.SoftBodySettings.TesticleDefaults()
            : part == Limber.BodyRig.RigParts.Butt
                ? _config.SoftBodyButt ??= Limber.BodyRig.SoftBodySettings.ButtDefaults()
                : part == Limber.BodyRig.RigParts.Belly
                    ? _config.SoftBodyBelly ??= Limber.BodyRig.SoftBodySettings.BellyDefaults()
                    : _config.SoftBody ??= new Limber.BodyRig.SoftBodySettings();

    /// <summary>The Rigging tab: one collapsible section per rigged part, then one build for all of them.</summary>
    /// <summary>Opens the Body physics guide window.</summary>
    public Action? ShowPhysicsHelp { get; set; }

    public void DrawRiggingTab()
    {
        bool physicsOn = _config.BodyPhysicsOn;
        if(ImGui.Checkbox("Body physics on###lmb_physics_on", ref physicsOn))
        {
            _config.BodyPhysicsOn = physicsOn;
            _config.Save();
        }
        Tooltip("Turns all body physics on or off: the soft breasts, butt, testicles and belly, and the live mesh (off whenever Limber starts).");
        {
            bool only = _config.PhysicsOnlyTicked;
            if(CharacterFilter.Draw("lmb_physics", "Body physics", ref only, _config.PhysicsCharacters ??= new(), _selection.ListCharacters(), _selection.Actor))
            {
                _config.PhysicsOnlyTicked = only;
                _config.Save();
            }
        }
        bool liveRig = _config.LiveRig;
        if(ImGui.Checkbox("Live rig (no rigging, no redraw)###lmb_live_rig", ref liveRig))
        {
            _config.LiveRig = liveRig;
            _config.Save();
        }
        Tooltip("Physics works on the chosen character straight away: Limber keeps the extra bones itself and bends the worn "
            + "models on the graphics card, so nothing is rigged, built or redrawn. The Body shape page's detail sliders show "
            + "live too.\nOne character at a time per outfit (the game shares a model between everyone wearing it). "
            + "\"Rig everything\" below still works, and is lighter on the PC for several characters at once.");
        if(liveRig)
        {
            bool separate = _config.LiveRigSeparate;
            ImGui.Indent();
            if(ImGui.Checkbox("Separate wobbles (experimental)###lmb_live_rig_separate", ref separate))
            {
                _config.LiveRigSeparate = separate;
                _config.Save();
            }
            Tooltip("Characters wearing the same outfit or body each wobble on their own: the chosen character and the three "
                + "nearest others with physics. Each gets its own copy of what it wears, and the game's draws of it are sent to "
                + "that copy. Off: only the chosen character moves on a shared outfit.");
            if(separate && Limber.Gpu.LiveRig.SeparateProblem.Length > 0)
                ImGui.TextColored(Warning, "Separate wobbles: " + Limber.Gpu.LiveRig.SeparateProblem);
            ImGui.Unindent();
            var actor = _selection.Actor;
            var report = actor.IsSet ? Limber.Gpu.LiveRig.ReportFor(actor.Address) : "";
            if(report.Length > 0)
                ImGui.TextWrapped(report);
            if(Limber.Gpu.LiveRig.Status.Length > 0)
                ImGui.TextDisabled(Limber.Gpu.LiveRig.Status);
        }
        ImGui.Separator();
        if(ShowPhysicsHelp != null)
        {
            if(ImGui.Button("How to use this (step by step)###lmb_physics_help"))
                ShowPhysicsHelp();
            Tooltip("Opens a guide: what Body physics does, what you need, setting it up, the sliders, and what to do if it doesn't work.");
            ImGui.Spacing();
        }
        // 1. Rig: one button for everything.
        SeparatorText("Rig");
        DrawOneClick();
        DrawModelCheck();

        // 2. Each part: on or off by itself, its physics settings tucked under it.
        SeparatorText("Parts");
        foreach(var part in Limber.BodyRig.RigParts.All)
        {
            using(ImRaii.PushId(part.Key))
                DrawPartRow(part);
        }

        ImGui.Spacing();
        if(ImGui.CollapsingHeader("Experimental###lmb_live_mesh"))
            DrawLiveMesh();
    }

    // Step 1 of a per-vertex soft body: finding and rewriting the worn models' vertices on the GPU.
    private unsafe void DrawLiveMesh()
    {
        var actor = _selection.Actor;
        {
            var liveSettings = Limber.Gpu.LiveSoftBody.Settings();
            bool auto = liveSettings.AutoStart;
            if(ImGui.Checkbox("Start by itself###lmb_live_auto", ref auto))
            {
                liveSettings.AutoStart = auto;
                _config.Save();
            }
            Tooltip("Starts the live soft body on the chosen character by itself (finding the right model on its own), and again "
                + "when what it wears changes. Off: use Start now, or the buffer list below.");
            ImGui.SameLine();
            using(ImRaii.Disabled(!actor.IsSet))
                if(ImGui.Button("Start now###lmb_live_start"))
                {
                    _gpuStatus = Limber.Gpu.LiveSoftBody.AutoStart(actor.Address, actor.ObjectIndex, ReadModelFile, LivePartOf);
                }
            Tooltip("Finds the character's body model on the GPU and starts the live soft body on it.");
            if(Limber.Gpu.LiveSoftBody.AutoStatus.Length > 0 && _gpuStatus == null)
                ImGui.TextDisabled(Limber.Gpu.LiveSoftBody.AutoStatus);
        }
        bool manual = ImGui.TreeNode("Buffer list (manual, advanced)###lmb_gpu_manual");
        if(manual)
        {
        using(ImRaii.Disabled(!actor.IsSet))
        {
            if(ImGui.Button("Find the GPU buffers###lmb_gpu_probe"))
            {
                Limber.Gpu.LiveMesh.StopWobble();
                _gpuLog = new List<string>();
                _gpuFound = null;
                var obj = Limber.Core.ActorSkeleton.GetObject(actor.ObjectIndex);
                if(obj == null || (nint)obj != actor.Address)
                    _gpuLog.Add("The character isn't there any more.");
                else if(!Limber.Koi.Safety.AdultAllowed(actor.Address))
                    _gpuLog.Add("Not available for this character.");
                else
                {
                    try
                    {
                        _gpuFound = Limber.Gpu.LiveMesh.Probe((nint)obj->DrawObject, _gpuLog);
                    }
                    catch(Exception e)
                    {
                        _gpuLog.Add("Probe failed: " + e.Message);
                    }
                }
                _gpuStatus = null;
            }
        }
            Tooltip("Then click Soft body on a \"vertices\" line of the body / top model.");
            ImGui.TreePop();
        }
        if(Limber.Gpu.LiveMesh.Wobbling)
        {
            ImGui.SameLine();
            if(ImGui.Button("Stop the wobble###lmb_gpu_stop"))
                Limber.Gpu.LiveMesh.StopWobble();
        }
        if(Limber.Gpu.LiveSoftBody.Running || Limber.Gpu.LiveSoftBody.Resuming)
        {
            ImGui.SameLine();
            if(ImGui.Button("Stop the live soft body###lmb_gpu_soft_stop"))
                Limber.Gpu.LiveSoftBody.Stop();
            ImGui.TextDisabled($"Points touched right now: {Limber.Gpu.LiveSoftBody.LastTouched}");
            if(Limber.Gpu.LiveSoftBody.UsePhysics && Limber.Gpu.LiveSoftBody.PhysicsParticles > 0)
                ImGui.TextDisabled($"Soft body: {Limber.Gpu.LiveSoftBody.PhysicsParticles} particles, {Limber.Gpu.LiveSoftBody.PhysicsMilliseconds:0.0} ms a step");
        }
        bool physics = Limber.Gpu.LiveSoftBody.UsePhysics;
        if(ImGui.Checkbox("Soft body physics###lmb_live_physics", ref physics))
        {
            Limber.Gpu.LiveSoftBody.UsePhysics = physics;
            _config.Save();
        }
        Tooltip("On: the whole breast is a soft body (hands dent the skin and move the breast, it bulges, both breasts push on each other, it wobbles).\nOff: the older skin-only dent.");
        void LiveSlider(string label, float value, Action<float> set, float min, float max, string tip, string format = "%.2f")
        {
            ImGui.SetNextItemWidth(180 * ImGuiHelpers.GlobalScale);
            if(ImGui.SliderFloat(label, ref value, min, max, format))
                set(value);
            if(ImGui.IsItemDeactivatedAfterEdit())
                _config.Save();
            Tooltip(tip);
        }
        // Size works with the live mesh on too (it scales the breast bones; the live mesh follows them).
        var breasts = SoftFor(Limber.BodyRig.RigParts.Breasts);
        float size = breasts.Size;
        ImGui.SetNextItemWidth(180 * ImGuiHelpers.GlobalScale);
        if(ImGui.SliderFloat("Breast size###lmb_live_size", ref size, 0.1f, 3f, "%.2f"))
            breasts.Size = size;
        if(ImGui.IsItemDeactivatedAfterEdit())
            _config.Save();
        Tooltip("1 = unchanged. Same as Size under Breasts > Shape; the blend shapes work too.");
        LiveSlider("Mesh collision strength###lmb_live_strength", Limber.Gpu.LiveSoftBody.Strength, v => Limber.Gpu.LiveSoftBody.Strength = v, 0f, 1f,
            "How hard colliders dent the skin. 1 = stays outside colliders; lower = it sinks in.");
        float bone = Math.Clamp(_config.PropBoneStrength, 0f, 1f);
        ImGui.SetNextItemWidth(180 * ImGuiHelpers.GlobalScale);
        if(ImGui.SliderFloat("Bone collision strength###lmb_live_bone_strength", ref bone, 0f, 1f, "%.2f"))
            _config.PropBoneStrength = bone;
        if(ImGui.IsItemDeactivatedAfterEdit())
            _config.Save();
        Tooltip("How hard colliders push the soft body bones (moves the breast as a whole). 0 = not at all.");
        if(!physics)
        {
            LiveSlider("Spread (cm)###lmb_live_spread", Limber.Gpu.LiveSoftBody.Spread, v => Limber.Gpu.LiveSoftBody.Spread = v, 0.5f, 10f,
                "How far around a dent the skin gives way.");
            LiveSlider("Bulge###lmb_live_bulge", Limber.Gpu.LiveSoftBody.Bulge, v => Limber.Gpu.LiveSoftBody.Bulge = v, 0f, 2f,
                "Swelling around a dent.");
        }
        LiveSlider("Firmness###lmb_live_firm", Limber.Gpu.LiveSoftBody.Firmness, v => Limber.Gpu.LiveSoftBody.Firmness = v, 0f, 1f,
            physics ? "How firmly it holds its shape (and springs back)." : "How fast it springs back.");
        LiveSlider("Bounce###lmb_live_bounce", Limber.Gpu.LiveSoftBody.Bounce, v => Limber.Gpu.LiveSoftBody.Bounce = v, 0f, 1f,
            physics ? "How long it keeps wobbling." : "Wobble on the way back.");
        {
            var live = Limber.Gpu.LiveSoftBody.Settings();
            LiveSlider("Smoothing###lmb_live_smooth", live.Smoothing, v => live.Smoothing = v, 0f, 1f,
                "How smooth dents look. Higher: rounder edges; the dent keeps its depth (nothing shrinks).");
        }
        if(physics && ImGui.TreeNode("More soft body settings###lmb_live_more"))
        {
            var live = Limber.Gpu.LiveSoftBody.Settings();
            LiveSlider("Side firmness###lmb_live_side", live.SideFirmness, v => live.SideFirmness = v, 0f, 1f,
                "How much firmer the skin is sideways than in and out. Higher: dents go in, but less sloshing about.");
            LiveSlider("Max bulge (cm)###lmb_live_bulge_max", live.MaxBulge, v => live.MaxBulge = v, 0.2f, 6f,
                "How far skin may swell out past where the body puts it (stops ballooning around a deep press).", "%.1f");
            LiveSlider("Carry###lmb_live_carry", live.Carry, v => live.Carry = v, 0f, 1f,
                "How much skin is dragged along by what touches it as that moves (a sliding hand pulls the skin with it; "
                + "fast moves don't slip through). Works with Friction.");
            LiveSlider("Pull back###lmb_live_pull", live.PullBack, v => live.PullBack = v, 0f, 1f,
                "How strongly it's drawn back to where the body puts it (higher: stiffer, less sag and swing).");
            LiveSlider("Friction###lmb_live_friction", live.Friction, v => live.Friction = v, 0f, 1f,
                "How much skin that's touched is held against what touches it (higher: calmer under pressure, less sliding).");
            LiveSlider("Steadiness###lmb_live_steady", live.Steadiness, v => live.Steadiness = v, 0f, 0.9f,
                "Smooths frame-to-frame jitter of the skin (higher: calmer, but it trails fast moves a little).");
            int accuracy = live.Accuracy;
            ImGui.SetNextItemWidth(180 * ImGuiHelpers.GlobalScale);
            if(ImGui.SliderInt("Accuracy###lmb_live_accuracy", ref accuracy, 2, 16))
                live.Accuracy = accuracy;
            if(ImGui.IsItemDeactivatedAfterEdit())
                _config.Save();
            Tooltip("Solver passes a step. Higher: firmer contact and less jitter; costs more time (see the ms above).");
            LiveSlider("Breasts push each other###lmb_live_breastpush", live.BreastPush, v => live.BreastPush = v, 0f, 1f,
                "How hard the two breasts push on each other where they meet (0: they pass through each other).");
            LiveSlider("Skin margin (mm)###lmb_live_margin", live.SkinMargin, v => live.SkinMargin = v, 0f, 10f,
                "Gap kept between the skin and colliders.", "%.1f");
            LiveSlider("Imprint depth (cm)###lmb_live_imprint", live.ImprintDepth, v => live.ImprintDepth = v, 0f, 6f,
                "How deep the exact shape of what touches is pressed into the skin (fingers' outline).", "%.1f");
            LiveSlider("Cling range (cm)###lmb_live_cling_range", live.ClingRange, v => live.ClingRange = v, 0.5f, 10f,
                "How far skin reaches to cling onto a collider that has Skin cling (Devices > the collider).", "%.1f");
            if(ImGui.SmallButton("Defaults###lmb_live_defaults"))
            {
                var d = new Limber.Gpu.LiveMeshSettings();
                live.PullBack = d.PullBack; live.Friction = d.Friction; live.Steadiness = d.Steadiness; live.Accuracy = d.Accuracy;
                live.BreastPush = d.BreastPush; live.SkinMargin = d.SkinMargin; live.ImprintDepth = d.ImprintDepth; live.ClingRange = d.ClingRange;
                live.Firmness = d.Firmness; live.Bounce = d.Bounce; live.Strength = d.Strength;
                live.SideFirmness = d.SideFirmness; live.MaxBulge = d.MaxBulge; live.Carry = d.Carry; live.Smoothing = d.Smoothing;
                _config.Save();
            }
            ImGui.TreePop();
        }
        if(_gpuStatus == Limber.Gpu.LiveSoftBody.ReadyingText && !Limber.Gpu.LiveSoftBody.Preparing)
            _gpuStatus = Limber.Gpu.LiveSoftBody.AutoStatus;
        if(_gpuStatus != null)
            ImGui.TextWrapped(_gpuStatus);
        if(Limber.Gpu.LiveMesh.LastError != null)
            ImGui.TextColored(Warning, Limber.Gpu.LiveMesh.LastError);
        if(Limber.Gpu.LiveSoftBody.LastError != null)
            ImGui.TextColored(Warning, Limber.Gpu.LiveSoftBody.LastError);
        if(_gpuLog != null)
            foreach(var line in _gpuLog)
                ImGui.TextWrapped(line);
        if(_gpuFound == null)
            return;
        var lines = new List<string>();
        int i = 0;
        foreach(var f in _gpuFound)
        {
            string line = $"slot {f.Slot} {f.Kind} {f.Desc.ByteWidth} bytes, {f.Usage}, cpu {f.Desc.CPUAccessFlags}, misc {f.Desc.MiscFlags}, {f.Holders.Count} pointer(s) at {f.Path}";
            lines.Add(line);
            if(f.IsVertex)
            {
                if(ImGui.SmallButton($"Wobble###lmb_gpu_wobble_{i}"))
                {
                    Limber.Gpu.LiveSoftBody.Stop();
                    _gpuStatus = Limber.Gpu.LiveMesh.StartWobble(f, ReadModelFile, IsBreastBone);
                }
                ImGui.SameLine();
                if(ImGui.SmallButton($"Soft body###lmb_gpu_soft_{i}"))
                    _gpuStatus = Limber.Gpu.LiveSoftBody.Start(f, actor.Address, actor.ObjectIndex, ReadModelFile, LivePartOf);
                Tooltip("Start the per-vertex soft body on this model.");
                ImGui.SameLine();
            }
            ImGui.TextUnformatted(line);
            if(ImGui.IsItemClicked() && ImGui.GetIO().KeyCtrl)
                ImGui.SetClipboardText(line);
            i++;
        }
        if(lines.Count > 0 && ImGui.SmallButton("Copy the whole list###lmb_gpu_copy"))
            ImGui.SetClipboardText(string.Join("\n", (_gpuLog ?? new()).Concat(lines)));
    }

    private static bool IsBreastBone(string name) => LivePartOf(name) == 0;

    // Which soft part a bone moves for the live mesh: 0 breasts, 1 butt, 2 testicles, -1 none. Bones marked on the
    // Renderer page first (other skeletons), then the ones Limber knows by name.
    public static int LivePartOf(string name)
    {
        switch(Limber.BodyRig.SoftBoneMarks.MarkOf(name))
        {
            case Limber.BodyRig.SoftBoneMarks.Breasts: return 0;
            case Limber.BodyRig.SoftBoneMarks.Butt: return 1;
            case Limber.BodyRig.SoftBoneMarks.Testicles: return 2;
            case Limber.BodyRig.SoftBoneMarks.None: return -1;
        }
        return KnownLivePartOf(name);
    }

    public static int KnownLivePartOf(string name)
        => name.StartsWith("iv_c_mune_", StringComparison.Ordinal) || name.StartsWith("j_mune_", StringComparison.Ordinal)
           || name.StartsWith("lmb_lat_mune_", StringComparison.Ordinal) || name.StartsWith("lmb_mune_", StringComparison.Ordinal) ? 0
           : name.StartsWith("iv_shiri_", StringComparison.Ordinal) || name.StartsWith("lmb_lat_shiri_", StringComparison.Ordinal) ? 1
           : name.StartsWith("iv_kougan_", StringComparison.Ordinal) || name.StartsWith("iv_kintama", StringComparison.Ordinal)
             || name.StartsWith("lmb_lat_kougan_", StringComparison.Ordinal) || name.StartsWith("lmb_kougan_", StringComparison.Ordinal) ? 2
           : -1;

    // A model file as the game loaded it (Penumbra's replacement on disk, or the game's own).
    /// <summary>A model file as the game loaded it (Penumbra's replacement on disk, or the game's own).</summary>
    public static byte[]? ReadModelFile(string name)
    {
        var path = name;
        if(path.StartsWith('|'))
        {
            int end = path.IndexOf('|', 1);
            if(end > 0)
                path = path[(end + 1)..];
        }
        try
        {
            if(System.IO.Path.IsPathRooted(path))
                return System.IO.File.Exists(path) ? System.IO.File.ReadAllBytes(path) : null;
            return Plugin.DataManager.GetFile(path)?.Data;
        }
        catch
        {
            return null;
        }
    }

    // One part: its switch, collisions and whether it's running, on one line; the rest under "Settings".
    private void DrawPartRow(Limber.BodyRig.RigPart part)
    {
        var soft = SoftFor(part);
        bool enabled = soft.Enabled;
        ImGui.AlignTextToFramePadding();
        if(ImGui.Checkbox($"{part.Label}###lmb_part_on", ref enabled))
        {
            soft.Enabled = enabled;
            _config.Save();
        }
        Tooltip($"Soft {part.Label.ToLowerInvariant()}: physics on every nearby character whose models are rigged.");
        ImGui.SameLine(160 * ImGuiHelpers.GlobalScale);
        bool collide = soft.Collisions;
        if(ImGui.Checkbox("Collisions###lmb_part_collide", ref collide))
        {
            soft.Collisions = collide;
            _config.Save();
        }
        Tooltip("Arms, hands, thighs, the floor and props push it.");
        ImGui.SameLine();
        DrawSoftStatus(part);
        if(ImGui.TreeNode($"Settings###lmb_part_settings"))
        {
            DrawRigPart(part);
            ImGui.TreePop();
        }
    }

    // The belly: how pregnant the chosen character is (kept with its expansion, Limber Animations).
    private void DrawPregnancy()
    {
        var actor = _selection.Actor;
        string name = actor.IsSet ? actor.Name ?? "" : "";
        if(name.Length == 0 || !Limber.Koi.Safety.AdultAllowed(actor.Address))
        {
            ImGui.TextDisabled("Pregnancy: choose an adult character at the top.");
            return;
        }
        var all = _config.Expansion ??= new();
        if(!all.TryGetValue(name, out var ex))
            all[name] = ex = new Limber.Rig.ExpansionSettings();
        float pregnant = ex.Pregnant * 100f;
        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        if(ImGui.SliderFloat("Pregnancy###lmb_pregnant", ref pregnant, -50f, 300f, "%.0f%%"))
        {
            ex.Pregnant = pregnant / 100f;
            _config.Save();
        }
        Tooltip($"How pregnant {name}'s belly is, all the time (100% = full term; below 0 flatter). It grows round and smooth "
            + "on the belly rig, with or without its physics. To grow it over time (pumping, swelling, breathing...), use "
            + "Expansion > Belly in Limber Animations, on top of this.\nCtrl+click to type a value.");
        float shape = ex.Belly.Shape * 100f;
        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        if(ImGui.SliderFloat("Belly shape###lmb_pregnant_shape", ref shape, -100f, 100f, "%.0f%%"))
        {
            ex.Belly.Shape = shape / 100f;
            _config.Save();
        }
        Tooltip("-100%: rounder and wider. 0: in between. +100%: more forward, narrower (for the held pregnancy and the growth).");
        bool grown = MathF.Abs(Expansion?.BellyOf(actor.Address).Amount ?? 0f) > 1e-4f;
        if(grown && !_config.LiveRig && SoftBelly is { } sim && (System.Environment.TickCount64 - sim.LastTicks > 1000 || sim.LastCount == 0))
            ImGui.TextColored(Warning, "No belly rig on this character yet: turn on Live rig above, or Rig everything.");
        ImGui.Spacing();
    }

    private void DrawRigPart(Limber.BodyRig.RigPart part)
    {
        if(part == Limber.BodyRig.RigParts.Belly)
            DrawPregnancy();
        DrawSoftBody(part);

        if(!ImGui.TreeNode("Bone placement (advanced)###lmb_rig_placement"))
            return;
        var shape = ShapeFor(part);
        bool changed = false;
        float size = shape.Size, forward = shape.Forward, up = shape.Up, outward = shape.Out, depth = shape.Depth;
        ImGui.SetNextItemWidth(180 * ImGuiHelpers.GlobalScale);
        if(ImGui.SliderFloat("Size##lmb_rig", ref size, 0.3f, 8f, "%.2f"))
        {
            shape.Size = size;
            if(part.Region)
                shape.Out = size * 0.5f; // (each belly half from the middle of the body out)
            changed = true;
        }
        ImGui.SetNextItemWidth(180 * ImGuiHelpers.GlobalScale);
        if(ImGui.SliderFloat("Forward##lmb_rig", ref forward, -2f, 4f, "%.2f")) { shape.Forward = forward; changed = true; }
        ImGui.SetNextItemWidth(180 * ImGuiHelpers.GlobalScale);
        if(ImGui.SliderFloat("Up / down##lmb_rig", ref up, -3f, 3f, "%.2f")) { shape.Up = up; changed = true; }
        if(!part.Region)
        {
            ImGui.SetNextItemWidth(180 * ImGuiHelpers.GlobalScale);
            if(ImGui.SliderFloat("Outward##lmb_rig", ref outward, -3f, 3f, "%.2f")) { shape.Out = outward; changed = true; }
        }
        ImGui.SetNextItemWidth(180 * ImGuiHelpers.GlobalScale);
        if(ImGui.SliderFloat("Depth##lmb_rig", ref depth, 0.1f, 5f, "%.2f")) { shape.Depth = depth; changed = true; }
        if(part.OwnHeight)
        {
            float height = shape.Height > 0f ? shape.Height : shape.Size;
            ImGui.SetNextItemWidth(180 * ImGuiHelpers.GlobalScale);
            if(ImGui.SliderFloat("Height##lmb_rig", ref height, 0.3f, 8f, "%.2f")) { shape.Height = height; changed = true; }
        }
        Tooltip("The box the rig bones fill. Set automatically when the box is fitted; applies on the next rig.");
        if(ImGui.SmallButton("Default placement##lmb_rig"))
        {
            SetShape(part, part.DefaultShape());
            changed = true;
        }

        // One part's grid is shown at a time.
        bool preview = _config.PreviewBreastRig && _config.RigPartKey == part.Key;
        ImGui.SameLine();
        if(ImGui.Checkbox("Show the box##lmb_rig_preview", ref preview))
        {
            _config.PreviewBreastRig = preview;
            if(preview)
                _config.RigPartKey = part.Key;
            changed = true;
        }
        Tooltip("Pink dots: where the bones will go. Green rings: rig bones already in the skeleton.");
        if(changed)
            _config.Save();

        if(_config.PreviewBreastRig && _config.RigPartKey == part.Key)
            DrawRigPreview(part, ShapeFor(part));
        ImGui.TreePop();
    }

    private Limber.BodyRig.OneClickRig.Progress? _oneClick;

    private Dictionary<string, Limber.BodyRig.BreastRigShape> RigShapes()
    {
        var shapes = new Dictionary<string, Limber.BodyRig.BreastRigShape>();
        foreach(var p in Limber.BodyRig.RigParts.Built)
            shapes[p.Key] = ShapeFor(p).Clone();
        return shapes;
    }

    // One click: rig mod, box fitted to the worn models, weighting them, saving, turning on, redrawing.
    private void StartOneClick(Limber.BodyRig.RigPart[] parts)
    {
        var actor = _selection.Actor;
        if(!actor.IsSet)
            return;
        var dir = System.IO.Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "BodyRig", "RiggedModels");
        // (The old baked roundness / middle joining are replaced by the live blend shapes.)
        _oneClick = Limber.BodyRig.OneClickRig.Start(actor.ObjectIndex, actor.Address, RigShapes(), dir, parts, _config.RigAutoFit);
    }

    /// <summary>One-click rig of everything (the Body Shape tab's button); false if it can't start now.</summary>
    public bool RigEverything()
    {
        if(OneClickBusy || !_selection.Actor.IsSet)
            return false;
        StartOneClick(Limber.BodyRig.RigParts.Rigged);
        return true;
    }

    private bool OneClickBusy => _oneClick is { Done: false } || _rigBuild is { IsCompleted: false };

    private void DrawOneClick()
    {
        var actor = _selection.Actor;
        using(ImRaii.Disabled(OneClickBusy || !actor.IsSet))
        {
            string label = _oneClick is { Done: false } ? "Rigging...###lmb_one_click" : "Rig everything (one click)###lmb_one_click";
            if(ImGui.Button(label, new Vector2(-1, 0)))
                StartOneClick(Limber.BodyRig.RigParts.Rigged);
        }
        Tooltip("Rigs the breasts, testicles, butt and belly of the chosen character's worn models.");
        bool fit = _config.RigAutoFit;
        if(ImGui.Checkbox("Fit the boxes to the models automatically###lmb_auto_fit", ref fit))
        {
            _config.RigAutoFit = fit;
            _config.Save();
        }
        Tooltip("Recommended. Off: uses Bone placement as set.");
        if(!actor.IsSet)
            ImGui.TextDisabled("Choose or target a character first.");
        if(_oneClick != null)
        {
            if(_oneClick.Done && !_oneClick.FittedApplied)
            {
                _oneClick.FittedApplied = true;
                foreach(var part in Limber.BodyRig.RigParts.Rigged)
                    if(_oneClick.Fitted.TryGetValue(part.Key, out var shape))
                        SetShape(part, shape);
                _config.Save();
            }
            using var child = ImRaii.Child("###lmb_one_click_log", new Vector2(-1, 140 * ImGuiHelpers.GlobalScale), true);
            if(child.Success)
            {
                foreach(var line in _oneClick.Lines)
                    ImGui.TextWrapped(line);
                if(_oneClick.Done && ImGui.SmallButton("Clear###lmb_one_click_clear"))
                    _oneClick = null;
            }
        }
        ImGui.Spacing();
    }

    private void DrawRigBuild()
    {
        bool building = OneClickBusy;
        if(_rigBuild is { IsCompleted: true })
        {
            var task = _rigBuild;
            _rigBuild = null;
            if(task.IsCompletedSuccessfully)
            {
                var result = task.Result;
                _rigReport = result.Lines;
                if(result.PackagePath != null)
                {
                    _rigReport.Add(result.Installed
                        ? "Sent to your mod manager to install. In your mod manager: enable \"" + Limber.BodyRig.RigBuilder.ModName + "\" in your character's collection, give it a higher priority than your skeleton mod, and redraw your character."
                        : "Import the .pmp into your mod manager yourself (Import Mod), enable it above your skeleton mod and redraw.");
                }
            }
            else
            {
                var error = task.Exception?.GetBaseException();
                _rigReport = ["Failed: " + (error?.Message ?? "unknown error")];
                Plugin.Log.Warning(error, "Limber: body rig build failed");
            }
        }
        using(ImRaii.Disabled(building))
        {
            if(ImGui.Button(building ? "Building...###lmb_rig_build" : "Build the rig mod (all parts)###lmb_rig_build", new Vector2(-1, 0)) && !building)
            {
                var dir = System.IO.Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "BodyRig");
                var shapes = RigShapes();
                _rigReport = null;
                // On the game's own thread, where its Havok code expects to run.
                _rigBuild = Plugin.Framework.RunOnFrameworkThread(() => Limber.BodyRig.RigBuilder.Build(shapes, dir));
            }
        }
        Tooltip("Only the skeleton part of the rig (the one-click rig does this too).");

        if(_rigReport != null)
            foreach(var line in _rigReport)
                ImGui.TextWrapped(line);
    }

    // What a part's physics is doing right now (on its line).
    private void DrawSoftStatus(Limber.BodyRig.RigPart part)
    {
        var sim = part == Limber.BodyRig.RigParts.Testicles ? SoftTesticles : part == Limber.BodyRig.RigParts.Butt ? SoftButt
            : part == Limber.BodyRig.RigParts.Belly ? SoftBelly : SoftBreasts;
        if(sim == null || !SoftFor(part).Enabled)
        {
            ImGui.TextDisabled("off");
            return;
        }
        bool recent = System.Environment.TickCount64 - sim.LastTicks < 1000;
        if(!recent || sim.LastCount == 0)
        {
            ImGui.TextColored(Warning, "not running");
            Tooltip(_config.LiveRig
                ? "No nearby character has this part's bones yet: the live rig is getting ready, or the worn models have no bones for this part."
                : "No nearby character has this part's rig bones: turn on the live rig, or rig it above (and check the worn models).");
        }
        else
            ImGui.TextDisabled($"running on {sim.LastCount} (moving up to {sim.LastMove * 100f:0.0} cm)");
    }

    // Whether the chosen character's worn models use the rig (once for all parts): to see why something doesn't move.
    private void DrawModelCheck()
    {
        using(ImRaii.Disabled(_modelChecking || !_selection.Actor.IsSet))
        {
            if(ImGui.SmallButton("Check the worn models###lmb_check_models"))
                CheckModels();
        }
        Tooltip("Lists the models the chosen character wears and which parts each is rigged for\n" +
                "(a model that isn't can't move, whatever the settings say: rig everything again).");
        if(_modelCheck == null)
            return;
        foreach(var (line, _) in _modelCheck)
            ImGui.TextWrapped(line);
        if(ImGui.SmallButton("Hide###lmb_check_hide"))
            _modelCheck = null;
    }

    private void CheckModels()
    {
        var actor = _selection.Actor;
        bool liveRig = _config.LiveRig;
        _modelChecking = true;
        _modelCheck = [("Checking...", null)];
        System.Threading.Tasks.Task.Run(async () =>
        {
            var lines = new List<(string Line, string? RigGame)>();
            try
            {
                var paths = await Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    var all = Plugin.PluginInterface
                        .GetIpcSubscriber<ushort[], Dictionary<string, HashSet<string>>?[]>("Penumbra.GetGameObjectResourcePaths.V5")
                        .InvokeFunc(new[] { actor.ObjectIndex });
                    return all.Length > 0 ? all[0] : null;
                });
                if(paths == null)
                    lines.Add(("The mod manager couldn't list this character's files.", null));
                else
                {
                    foreach(var (actual, games) in paths)
                    {
                        foreach(var game in games.Where(g => g.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)))
                        {
                            var file = actual.StartsWith('|') && actual.IndexOf('|', 1) > 0 ? actual[(actual.IndexOf('|', 1) + 1)..] : actual;
                            byte[]? data = System.IO.Path.IsPathRooted(file) && System.IO.File.Exists(file)
                                ? await System.IO.File.ReadAllBytesAsync(file)
                                : await Plugin.Framework.RunOnFrameworkThread(() => Plugin.DataManager.GetFile(file)?.Data);
                            if(data == null)
                                continue;
                            var text = System.Text.Encoding.ASCII.GetString(data, 0, Math.Min(data.Length, 65536));
                            bool breasts = text.Contains("lmb_lat_mune_"), testicles = text.Contains("lmb_lat_kougan_"), butt = text.Contains("lmb_lat_shiri_"),
                                belly = text.Contains("lmb_lat_hara_");
                            bool hasParts = text.Contains("mune") || text.Contains("kougan") || text.Contains("kintama") || text.Contains("shiri") || text.Contains("j_sebo_a");
                            if(!hasParts)
                                continue;
                            string origin = System.IO.Path.IsPathRooted(file) ? System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(file) ?? "") : "game";
                            bool rigged = breasts || testicles || butt || belly;
                            lines.Add(($"{System.IO.Path.GetFileName(game)}: " + (rigged
                                ? "rigged (" + string.Join(", ", new[] { breasts ? "breasts" : null, testicles ? "testicles" : null, butt ? "butt" : null, belly ? "belly" : null }.Where(x => x != null)) + ")"
                                : liveRig ? "not rigged (the live rig moves it)" : "NOT rigged") + $" - from {origin}", rigged ? null : game));
                        }
                    }
                    if(lines.Count == 0)
                        lines.Add(("No worn model has breast, testicle, butt or belly bones.", null));
                }
            }
            catch(Exception e)
            {
                lines.Add(("Couldn't check: " + e.Message, null));
            }
            _modelCheck = lines.ToArray();
            _modelChecking = false;
        });
    }

    private void DrawSoftBody(Limber.BodyRig.RigPart part)
    {
        string softName = "Soft " + part.Label.ToLowerInvariant();
        var soft = SoftFor(part);
        bool changed = false;

        void Slider(string label, float value, Action<float> set, string tip, float min = -3f, float max = 3f)
        {
            ImGui.SetNextItemWidth(180 * ImGuiHelpers.GlobalScale);
            if(ImGui.SliderFloat(label, ref value, min, max, "%.2f")) { set(value); changed = true; }
            Tooltip(tip);
        }
        Slider("Firmness##lmb_soft", soft.Firmness, v => soft.Firmness = v, "How quickly it springs back.");
        Slider("Bounce##lmb_soft", soft.Bounce, v => soft.Bounce = v, "How long it keeps bouncing.");
        Slider("Weight##lmb_soft", soft.Weight, v => soft.Weight = v, "Heavier swings further and slower.");
        Slider("Keep shape##lmb_soft", soft.KeepShape, v => soft.KeepShape = v, "1 = keeps the model's shape at rest.", 0f, 1f);
        Slider("Jiggle##lmb_soft", soft.Jiggle, v => soft.Jiggle = v, "Exaggerates the movement.", 0f, 6f);
        Slider("Cohesion##lmb_soft", soft.Cohesion, v => soft.Cohesion = v, "High = one piece; low = more liquid.");
        Slider("Max stretch##lmb_soft", soft.MaxStretch, v => soft.MaxStretch = v, "Limit on swing and stretch.", 0f, 3f);


        if(ImGui.TreeNode("Shape###lmb_shape_node"))
        {

        Slider("Size##lmb_shape", soft.Size, v => soft.Size = v, "Overall size (1 = unchanged).", 0.1f, 3f);
        Slider("Width##lmb_shape", soft.Width, v => soft.Width = v, "Side to side (1 = unchanged).", 0.1f, 3f);
        Slider("Height##lmb_shape", soft.Height, v => soft.Height = v, "Up and down (1 = unchanged).", 0.1f, 3f);
        Slider("Projection##lmb_shape", soft.Projection, v => soft.Projection = v, "How far it sticks out forward (1 = unchanged).", 0.1f, 3f);
        Slider("Lift##lmb_shape", soft.Lift, v => soft.Lift = v, "Moves it up (+) or down (-) (0 = unchanged).", -1f, 1f);
        Slider("Spacing##lmb_shape", soft.Spacing, v => soft.Spacing = v, "Moves the two apart (+) or together (-) (0 = unchanged).", -1f, 1f);
        ImGui.TreePop();
        }

        if(ImGui.TreeNode("Blend shapes###lmb_blend_node"))
        {
        Tooltip("0 = off, 1 = full. Ctrl+click to type up to 3.");
        var shapeList = Limber.BodyRig.BodyShapes.For(part);
        soft.Shapes ??= new();
        foreach(var shape in shapeList)
        {
            float amount = soft.Shapes.TryGetValue(shape.Name, out var a) ? a : 0f;
            ImGui.SetNextItemWidth(180 * ImGuiHelpers.GlobalScale);
            if(ImGui.SliderFloat($"{shape.Name}##lmb_bshape_{part.Key}_{shape.Name}", ref amount, 0f, 1f, "%.2f"))
            {
                // A new set each time (never changed in place), as the physics reads it on another thread.
                var next = new Dictionary<string, float>(soft.Shapes) { [shape.Name] = Math.Clamp(amount, -1f, 3f) };
                if(MathF.Abs(amount) < 1e-4f)
                    next.Remove(shape.Name);
                soft.Shapes = next;
                changed = true;
            }
            Tooltip(shape.Tip);
        }
        ImGui.TreePop();
        }

        if(ImGui.SmallButton("Reset shape##lmb_shape"))
        {
            soft.Size = soft.Width = soft.Height = soft.Projection = 1f;
            soft.Lift = soft.Spacing = 0f;
            soft.Shapes = new();
            changed = true;
        }
        if(ImGui.SmallButton($"Default {softName.ToLowerInvariant()}##lmb_soft"))
        {
            // Physics back to this part's defaults; the shape is kept.
            var fresh = part == Limber.BodyRig.RigParts.Testicles
                ? Limber.BodyRig.SoftBodySettings.TesticleDefaults()
                : part == Limber.BodyRig.RigParts.Butt
                    ? Limber.BodyRig.SoftBodySettings.ButtDefaults()
                    : part == Limber.BodyRig.RigParts.Belly
                        ? Limber.BodyRig.SoftBodySettings.BellyDefaults()
                        : new Limber.BodyRig.SoftBodySettings();
            fresh.Enabled = soft.Enabled;
            fresh.Size = soft.Size; fresh.Width = soft.Width; fresh.Height = soft.Height; fresh.Projection = soft.Projection;
            fresh.Lift = soft.Lift; fresh.Spacing = soft.Spacing;
            fresh.Shapes = new Dictionary<string, float>(soft.Shapes ?? new());
            if(part == Limber.BodyRig.RigParts.Testicles)
                _config.SoftBodyTesticles = fresh;
            else if(part == Limber.BodyRig.RigParts.Butt)
                _config.SoftBodyButt = fresh;
            else if(part == Limber.BodyRig.RigParts.Belly)
                _config.SoftBodyBelly = fresh;
            else
                _config.SoftBody = fresh;
            changed = true;
        }
        if(changed)
            _config.Save();
    }

    private void DrawRigPreview(Limber.BodyRig.RigPart part, Limber.BodyRig.BreastRigShape shape)
    {
        var actor = _selection.Actor;
        if(!actor.IsSet)
        {
            ImGui.TextColored(Warning, "Pick a character at the top to see the grid.");
            return;
        }
        if(!Limber.Koi.Safety.AdultAllowed(actor.Address))
            return;
        BoneSnapshot? snapshot;
        lock(_rigs.Sync)
            snapshot = _rigs.GetSnapshot(actor.Address);
        if(snapshot == null)
        {
            ImGui.TextColored(Warning, "No bone data for this character yet (it has to be drawn on screen).");
            return;
        }

        bool Has(string n) => snapshot.IndexOf(n) >= 0;
        var leftName = part.ParentBone(Has, true);
        var rightName = part.ParentBone(Has, false);
        if(leftName == null || rightName == null)
        {
            ImGui.TextColored(Warning, $"This character's skeleton has no {part.Label.ToLowerInvariant()} bones.");
            return;
        }

        _overlay.RequestRigPreview(part, shape);
        int present = 0;
        for(int i = 0; i < part.PerSide; i++)
        {
            if(Has(part.BoneName(true, i))) present++;
            if(Has(part.BoneName(false, i))) present++;
        }
        int total = part.PerSide * 2;
        ImGui.TextDisabled($"Grid hangs from {leftName} / {rightName}. Pink dots: where the bones will go.");
        if(present == total)
            ImGui.TextColored(new Vector4(0.45f, 0.9f, 0.45f, 1f), $"All {total} rig bones are in this character's skeleton (green rings).");
        else if(present > 0)
            ImGui.TextColored(Warning, $"Only {present} of {total} rig bones are in this character's skeleton.");
        else
            ImGui.TextDisabled("The rig bones aren't in this character's skeleton yet (build the mod, enable it, redraw).");
    }

    // ---- Settings tab

    private void DrawSettingsTab()
    {
        bool changed = false;

        if(ImGui.CollapsingHeader("Posing###limber_settings_posing", ImGuiTreeNodeFlags.DefaultOpen))
        {
            bool hipsAlone = _config.HipsMoveAlone;
            if(ImGui.Checkbox("Hips move on their own", ref hipsAlone))
            {
                _config.HipsMoveAlone = hipsAlone;
                changed = true;
            }
            Tooltip("On: the hips and legs move without the upper body. Off: the hips carry the whole body.");
        }

        if(ImGui.CollapsingHeader("Handles###limber_settings_handles", ImGuiTreeNodeFlags.DefaultOpen))
        {
            // (always editable: these used to grey out whenever the dots were hidden, with no way to tell why)
            bool shown = _config.ShowHandles;
            if(ImGui.Checkbox("Show dots###limber_settings_show_dots", ref shown))
            {
                _config.ShowHandles = shown;
                changed = true;
            }
            Tooltip("The bone dots in the scene (the settings below apply whenever they're shown).");

            bool gizmo = _config.ShowRotationGizmo;
            if(ImGui.Checkbox("Gizmo on the selected dot", ref gizmo))
            {
                _config.ShowRotationGizmo = gizmo;
                changed = true;
            }
            if(gizmo)
                DrawGizmoMode();

            bool allBones = _config.ShowAllBones;
            if(ImGui.Checkbox("Dots for every bone", ref allBones))
            {
                _config.ShowAllBones = allBones;
                changed = true;
            }

            bool rigBones = _config.ShowRigBones;
            if(ImGui.Checkbox("Show rigged bones###limber_rig_dots_settings", ref rigBones))
            {
                _config.ShowRigBones = rigBones;
                HandleCatalog.ShowRigBones = rigBones;
                changed = true;
            }
            Tooltip("The extra bones Limber's physics rig adds (the breast, butt and testicle lattices): shown as dots and in the bone lists. "
                + "Off (default): hidden, since they aren't for posing.");

            bool hideWithUi = _config.HideHandlesWithGameUi;
            if(ImGui.Checkbox("Hide with the game UI", ref hideWithUi))
            {
                _config.HideHandlesWithGameUi = hideWithUi;
                changed = true;
            }

            float size = _config.HandleSize;
            ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
            if(ImGui.SliderFloat("Dot size", ref size, 4f, 24f, "%.0f"))
            {
                _config.HandleSize = size;
                changed = true;
            }
        }

        if(ImGui.CollapsingHeader("Reset###limber_settings_reset"))
        {
            lock(_rigs.Sync)
            {
                int count = _rigs.Rigs.Sum(r => r.Goals.Count);
                ImGui.TextDisabled($"{count} held bone{(count == 1 ? "" : "s")} on {_rigs.Rigs.Count} character{(_rigs.Rigs.Count == 1 ? "" : "s")}.");
                using(ImRaii.Disabled(count == 0))
                {
                    if(ImGui.Button("Let go of everything on every character", new Vector2(-1, 0)))
                    {
                        Motions?.StopAll();
                        _rigs.Checkpoint();
                        _rigs.ClearAll();
                        _selection.Bone = null;
                    }
                }
            }
        }

        if(changed)
            _config.Save();
    }

    // ---- Reset / undo / redo row, and gizmo mode

    private void DrawHistoryRow(ActorRef actor)
    {
        float third = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X * 2) / 3f;

        bool hasGoals;
        lock(_rigs.Sync)
            hasGoals = _rigs.GetRig(actor.Address)?.Goals.Count > 0;

        using(ImRaii.Disabled(!hasGoals))
        {
            if(ImGui.Button("Reset pose###limber_reset_pose", new Vector2(third, 0)))
            {
                Motions?.StopFor(actor.Address, null);
                lock(_rigs.Sync)
                {
                    _rigs.Checkpoint();
                    _rigs.RemoveRig(actor.Address);
                }
                _selection.Bone = null;
            }
        }
        Tooltip($"Let go of every bone on {actor.Name}.");

        // Back to the skeleton's rest pose (the pose the model is built in): the whole body, or the chosen bone.
        ImGui.SameLine();
        using(ImRaii.Disabled(Fk == null))
        {
            if(ImGui.Button("Rest pose###limber_rest_all", new Vector2(third, 0)) && Fk != null)
            {
                Motions?.StopFor(actor.Address, null);
                lock(_rigs.Sync)
                {
                    _rigs.Checkpoint();
                    _rigs.RemoveRig(actor.Address);
                }
                _selection.Bone = null;
                _restNote = Fk.RestPose(actor, null);
            }
        }
        Tooltip("The whole body to the skeleton's rest pose: lets go of every bone, and holds the rest pose with FK (Clear FK on the FK posing page undoes it).");
        ImGui.SameLine();
        var chosen = _selection.Bone;
        using(ImRaii.Disabled(Fk == null || chosen == null))
        {
            if(ImGui.Button("Rest pose (this bone)###limber_rest_bone", new Vector2(third, 0)) && Fk != null && chosen != null)
            {
                lock(_rigs.Sync)
                {
                    _rigs.Checkpoint();
                    _rigs.GetRig(actor.Address)?.Remove(chosen);
                }
                _restNote = Fk.RestPose(actor, chosen);
            }
        }
        Tooltip("The chosen bone to its rest pose (let go, and held there by FK).");
        if(_restNote.Length > 0)
            ImGui.TextDisabled(_restNote);

        SymmetryControls.Draw(_config, actor.Name ?? "", "ik");
    }

    private string _restNote = "";

    /// <summary>The character chosen at the top of the window.</summary>
    public ActorRef ChosenActor => _selection.Actor;

    /// <summary>The FK posing page (its rest pose), set by the plugin.</summary>
    public FkPosingTab? Fk { get; set; }

    /// <summary>Undo / redo at the top of the window: posing, sliders, switches, every setting.</summary>
    public void DrawUndoRedo()
    {
        using(ImRaii.Disabled(!_rigs.CanUndo))
        {
            if(ImGui.Button("Undo###limber_undo"))
                _rigs.Undo();
        }
        Tooltip("Undo the last change: a bone moved, a slider, a switch, anything in Limber. " + (_config.UndoKey ??= new()).Label);
        ImGui.SameLine();
        using(ImRaii.Disabled(!_rigs.CanRedo))
        {
            if(ImGui.Button("Redo###limber_redo"))
                _rigs.Redo();
        }
        Tooltip("Redo what was undone. " + (_config.RedoKey ??= new()).Label);
    }

    private void DrawGizmoMode()
    {
        bool changed = false;

        ImGui.TextUnformatted("Gizmo:");
        ImGui.SameLine();
        if(ImGui.RadioButton("Rotate###limber_gizmo_rotate", !_config.GizmoMove))
        {
            _config.GizmoMove = false;
            changed = true;
        }
        ImGui.SameLine();
        if(ImGui.RadioButton("Move###limber_gizmo_move", _config.GizmoMove))
        {
            _config.GizmoMove = true;
            changed = true;
        }

        ImGui.SameLine();
        ImGui.TextDisabled("|");
        ImGui.SameLine();
        if(ImGui.RadioButton("Bone axes###limber_gizmo_local", !_config.GizmoWorldAxes))
        {
            _config.GizmoWorldAxes = false;
            changed = true;
        }
        ImGui.SameLine();
        if(ImGui.RadioButton("World axes###limber_gizmo_world", _config.GizmoWorldAxes))
        {
            _config.GizmoWorldAxes = true;
            changed = true;
        }

        if(changed)
            _config.Save();
    }

    // ---- Helpers

    private static void SeparatorText(string text)
    {
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.75f, 0.75f, 0.8f, 1f), text);
        ImGui.Separator();
    }

    private static void Tooltip(string text)
    {
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip(text);
    }
}
