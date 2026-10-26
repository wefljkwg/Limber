using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Limber.BodyRig;
using Limber.Core;
using Limber.Rig;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Limber.UI;

// The "Props" tab: pill-shaped colliders (like Koikatsu's collider props) the soft bodies are always pushed out of.
// Each sits on a bone of a character (a hand, say, so the hand presses into breasts) or stays in the world.
public sealed unsafe class PropsTab
{
    private static readonly Vector4 Accent = new(0.6f, 0.9f, 1f, 1f);
    private static readonly Vector4 Warning = new(1f, 0.75f, 0.3f, 1f);

    private readonly PropService _props;
    private readonly PropOverlay _overlay;
    private readonly Selection _selection;
    private readonly RigSettings _config;
    // Bone names of the character the chosen prop is on (read in the bone update).
    private nint _namesAddress;
    private string[]? _names;
    private bool _namesPending;

    private ActorRef _addOn;
    private bool _withFingers = true;
    private string[]? _boneCheck;
    private readonly SkeletonInfoCache _checkCache = new(); // bone update thread (hook jobs) only
    // The bone to add on (picked in the Add section), and that character's bone names.
    private string? _addBone;
    private nint _addBoneFor;
    private string _addBoneSearch = string.Empty;
    private nint _addNamesAddress;
    private string[]? _addNames;
    private bool _addNamesPending;
    private string _boneSearch = string.Empty;

    // Motion props (piston, wheel, cycle).
    public MotionService? Motions { get; set; }
    private Guid _motionSelected;
    private static readonly string[] PatternNames = Enum.GetNames<MotionPattern>();

    public PropsTab(PropService props, PropOverlay overlay, Selection selection, RigSettings config)
    {
        _props = props;
        _overlay = overlay;
        _selection = selection;
        _config = config;
    }

    private List<PropCollider> Props => _config.Props ??= new();

    public void Draw()
    {
        _overlay.TabOpen();

        ImGui.TextDisabled("Colliders that push soft breasts / testicles and the live mesh.");
        bool body = _config.ShowBodyColliders;
        if (ImGui.Checkbox("Show body colliders###prop_show_body", ref body))
        {
            _config.ShowBodyColliders = body;
            _config.Save();
        }
        Tooltip("Pink dots: the breast (and testicle) surface that bone collision pushes. Blue: the body's own capsules.");

        if (ImGui.CollapsingHeader("Add###prop_section_add", ImGuiTreeNodeFlags.DefaultOpen))
            DrawAdd();
        if (ImGui.CollapsingHeader("Collision strength###prop_section_strength", ImGuiTreeNodeFlags.DefaultOpen))
            DrawStrength();
        var motions = _config.Motions ??= new();
        if (ImGui.CollapsingHeader($"Motion ({motions.Count})###prop_section_motion"))
            DrawMotions();
        if (ImGui.CollapsingHeader($"Colliders ({Props.Count})###prop_section_list", ImGuiTreeNodeFlags.DefaultOpen))
            DrawList();

        var selected = _overlay.Selected;
        if (selected != null && !Props.Contains(selected))
            _overlay.Selected = selected = null;
        if (selected != null && ImGui.CollapsingHeader($"Edit: {selected.Name}###prop_section_edit", ImGuiTreeNodeFlags.DefaultOpen))
        {
            using var id = ImRaii.PushId(selected.Id.ToString());
            DrawEditor(selected);
        }
        if (ImGui.CollapsingHeader("Display###prop_section_display"))
            DrawOptions();
    }

    // ---- Display and gizmo options

    private void DrawOptions()
    {
        bool shown = _config.PropWiresShown;
        if (ImGui.Checkbox("Show props###prop_wires_shown", ref shown))
        {
            _config.PropWiresShown = shown;
            _config.Save();
        }
        Tooltip("Wireframes only; they collide either way.");
        ImGui.SameLine();
        bool always = _config.PropWiresAlways;
        if (ImGui.Checkbox("Always show wireframes", ref always))
        {
            _config.PropWiresAlways = always;
            _config.Save();
        }
        Tooltip("Off: only while this page is open.");
        if (ImGui.SmallButton("Show all###prop_show_all"))
            SetAllVisible(true);
        ImGui.SameLine();
        if (ImGui.SmallButton("Hide all###prop_hide_all"))
            SetAllVisible(false);
        Tooltip("Each prop's own wireframe (the eye box in the list).");

        bool gizmo = _config.PropGizmo;
        if (ImGui.Checkbox("Gizmo on the chosen prop", ref gizmo))
        {
            _config.PropGizmo = gizmo;
            _config.Save();
        }
        if (gizmo)
        {
            ImGui.SameLine();
            if (ImGui.RadioButton("Move###prop_gizmo_move", _config.PropGizmoMove))
            {
                _config.PropGizmoMove = true;
                _config.Save();
            }
            ImGui.SameLine();
            if (ImGui.RadioButton("Rotate###prop_gizmo_rotate", !_config.PropGizmoMove))
            {
                _config.PropGizmoMove = false;
                _config.Save();
            }
            ImGui.SameLine();
            bool worldAxes = _config.PropGizmoWorldAxes;
            if (ImGui.Checkbox("World axes###prop_gizmo_world", ref worldAxes))
            {
                _config.PropGizmoWorldAxes = worldAxes;
                _config.Save();
            }
        }
    }

    // ---- Adding props

    private void DrawAdd()
    {
        // On the character chosen at the top of the window.
        _addOn = _selection.Actor;
        if (!_addOn.IsSet)
        {
            ImGui.TextDisabled("Choose a character at the top first.");
            return;
        }

        DrawAddBone();

        float half = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) * 0.5f;
        using (ImRaii.Disabled(!_addOn.IsSet))
        {
            if (ImGui.Button("Add hand colliders###prop_add_hands", new Vector2(half, 0)) && _addOn.IsSet)
            {
                var who = _addOn;
                bool fingers = _withFingers;
                _props.WithSkeleton(who.Address, who.ObjectIndex,
                    (in ActorSkeleton skeleton) => PropService.HandProps(skeleton, who.ObjectIndex, who.Name, fingers),
                    hands =>
                    {
                        if (hands == null || hands.Count == 0)
                        {
                            Plugin.Log.Warning("Limber: couldn't read that character's hand bones for the hand colliders.");
                            return;
                        }
                        Props.AddRange(hands);
                        _overlay.Selected = hands[0];
                        Changed(true);
                    });
            }
            Tooltip("A pill on each palm (and fingers).");
            ImGui.SameLine();
            ImGui.Checkbox("With fingers###prop_add_fingers", ref _withFingers);

            float handSize = _config.HandColliderSize;
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X * 0.6f);
            if (ImGui.SliderFloat("Hand collider size###prop_hand_size", ref handSize, 0.25f, 4f, "%.2fx"))
                _config.HandColliderSize = handSize;
            if (ImGui.IsItemDeactivatedAfterEdit())
                _config.Save();
            Tooltip("Makes every hand collider thicker or thinner at once.");

            string bone = BoneToAddOn();
            if (ImGui.Button($"Add on {HandleCatalog.Label(bone)}###prop_add_bone", new Vector2(half, 0)) && _addOn.IsSet)
            {
                var prop = new PropCollider
                {
                    Name = $"Collider {Props.Count + 1}",
                    ParentCharacter = _addOn.Name,
                    ParentIndexHint = _addOn.ObjectIndex,
                    ParentBone = bone,
                };
                Props.Add(prop);
                _overlay.Selected = prop;
                Changed(true);
            }
            Tooltip("Follows that bone.");
            ImGui.SameLine();
            if (ImGui.Button("Add in the world###prop_add_world", new Vector2(half, 0)) && _addOn.IsSet)
            {
                _props.WithSkeleton(_addOn.Address, _addOn.ObjectIndex,
                    (in ActorSkeleton skeleton) => (Vector3?)(skeleton.RootPosition + new Vector3(0f, 1f * skeleton.RootScale.Y, 0f)),
                    spot =>
                    {
                        if (spot == null)
                            return;
                        var prop = new PropCollider { Name = $"Collider {Props.Count + 1}", Offset = spot.Value, ScaleWithCharacter = false };
                        Props.Add(prop);
                        _overlay.Selected = prop;
                        Changed(true);
                    });
            }
            Tooltip("Stays where you put it.");
        }
    }

    private string BoneToAddOn()
    {
        if (_addBone != null && _addBoneFor == _addOn.Address)
            return _addBone;
        return _selection.Actor.Address == _addOn.Address && !string.IsNullOrEmpty(_selection.Bone) ? _selection.Bone! : "j_kosi";
    }

    // Pick any bone of the character to add a collider on (e.g. the penis, a breast, a finger).
    private void DrawAddBone()
    {
        if (!_addOn.IsSet)
            return;
        ImGui.Text("Bone:");
        ImGui.SameLine();
        ImGui.TextColored(Accent, HandleCatalog.Label(BoneToAddOn()));
        if (_addNamesAddress != _addOn.Address)
        {
            _addNamesAddress = _addOn.Address;
            _addNames = null;
            _addNamesPending = false;
        }
        if (_addNames == null && !_addNamesPending)
        {
            _addNamesPending = true;
            var asked = _addOn.Address;
            _props.WithSkeleton(_addOn.Address, _addOn.ObjectIndex, (in ActorSkeleton skeleton) => skeleton.AllBoneNames(_checkCache), names =>
            {
                if (_addNamesAddress == asked)
                    _addNames = names;
                _addNamesPending = false;
            });
        }
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("###prop_add_bone_search", "Search their bones to add on (e.g. penis, breast, hand, finger)", ref _addBoneSearch, 64);
        if (_addBoneSearch.Length == 0 || _addNames == null)
            return;
        bool adult = Limber.Koi.Safety.AdultAllowed(_addOn.Address);
        using var list = ImRaii.Child("###prop_add_bone_list", new Vector2(-1, 110 * ImGuiHelpers.GlobalScale), true);
        if (!list.Success)
            return;
        foreach (var name in _addNames)
        {
            if (!HandleCatalog.IsPickable(name, adult))
                continue; // body mod bones: adult bodies only
            string label = HandleCatalog.Label(name);
            if (!name.Contains(_addBoneSearch, StringComparison.OrdinalIgnoreCase) && !label.Contains(_addBoneSearch, StringComparison.OrdinalIgnoreCase))
                continue;
            if (ImGui.Selectable($"{(label == name ? name : $"{label}  ({name})")}###prop_add_pick_{name}", _addBone == name && _addBoneFor == _addOn.Address))
            {
                _addBone = name;
                _addBoneFor = _addOn.Address;
                _addBoneSearch = string.Empty;
            }
        }
    }

    // ---- The list

    private void DrawList()
    {
        if (Props.Count == 0)
        {
            ImGui.TextDisabled("None yet.");
            return;
        }

        var placed = _props.World;
        float height = Math.Min(Props.Count, 8) * ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().WindowPadding.Y * 2;
        using (var child = ImRaii.Child("###prop_list", new Vector2(-1, height), true))
        {
            if (child.Success)
            {
                foreach (var prop in Props.ToList())
                {
                    using var id = ImRaii.PushId(prop.Id.ToString());
                    bool enabled = prop.Enabled;
                    if (ImGui.Checkbox("###on", ref enabled))
                    {
                        prop.Enabled = enabled;
                        Changed(true);
                    }
                    Tooltip("On / off (collisions and wireframe)");
                    ImGui.SameLine();
                    bool visible = prop.Visible;
                    if (ImGui.Checkbox("###seen", ref visible))
                    {
                        prop.Visible = visible;
                        Changed(true);
                    }
                    Tooltip("Show its wireframe (it still collides when hidden)");
                    ImGui.SameLine();
                    string place = prop.IsWorld ? "in the world" : $"{prop.ParentCharacter}: {HandleCatalog.Label(prop.ParentBone)}";
                    bool here = !prop.Enabled || placed.Any(p => p.Id == prop.Id);
                    using (ImRaii.PushColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.TextDisabled), !here))
                    {
                        if (ImGui.Selectable($"{prop.Name}  -  {place}###pick", _overlay.Selected == prop))
                            _overlay.Selected = prop;
                    }
                }
            }
        }

        if (ImGui.SmallButton("Remove all###prop_remove_all"))
        {
            Props.Clear();
            _overlay.Selected = null;
            Changed(true);
        }
        Tooltip("Removes every collider (undo brings them back).");
    }

    // ---- Editing one prop

    // ---- Motion props: piston, wheel, cycle

    private List<MotionProp> MotionList => _config.Motions ??= new();

    private void DrawMotions()
    {
        if (Motions != null)
        {
            bool playing = Motions.Playing;
            if (ImGui.Checkbox("Playing###motion_playing", ref playing))
                Motions.Playing = playing;
            Tooltip("Pause or play every motion.");

            // Restart from the beginning, all at once (so they line up), or one kind at a time.
            ImGui.SameLine();
            ImGui.TextUnformatted("Restart:");
            ImGui.SameLine();
            if (ImGui.SmallButton("All###motion_restart_all"))
                Restart(null);
            ImGui.SameLine();
            if (ImGui.SmallButton("Pistons###motion_restart_piston"))
                Restart(MotionKind.Piston);
            ImGui.SameLine();
            if (ImGui.SmallButton("Wheels###motion_restart_wheel"))
                Restart(MotionKind.Wheel);
            ImGui.SameLine();
            if (ImGui.SmallButton("Cycles###motion_restart_cycle"))
                Restart(MotionKind.Cycle);
        }

        _addOn = _selection.Actor;
        if (_addOn.IsSet)
        {
            string bone = BoneToAddOn();
            ImGui.TextDisabled($"Adds on {HandleCatalog.Label(bone)}:");
            float third = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X * 2) / 3f;
            foreach (var kind in new[] { MotionKind.Piston, MotionKind.Wheel, MotionKind.Cycle })
            {
                if (kind != MotionKind.Piston)
                    ImGui.SameLine();
                if (ImGui.Button($"{kind}###motion_add_{kind}", new Vector2(third, 0)))
                    AddMotion(kind, bone);
            }
        }
        else
        {
            ImGui.TextDisabled("Choose a character at the top to add one.");
        }

        var list = MotionList;
        MotionProp? remove = null;
        foreach (var m in list)
        {
            using var id = ImRaii.PushId("motion_" + m.Id);
            bool on = m.Enabled;
            if (ImGui.Checkbox("###on", ref on))
            {
                m.Enabled = on;
                _config.Save();
            }
            ImGui.SameLine();
            bool shown = m.Visible;
            if (ImGui.Checkbox("###shown", ref shown))
            {
                m.Visible = shown;
                _config.Save();
            }
            Tooltip("Show its path.");
            ImGui.SameLine();
            if (ImGui.SmallButton("Delete###delete"))
                remove = m;
            ImGui.SameLine();
            if (ImGui.Selectable($"{m.Name}  ({m.Character}: {HandleCatalog.Label(m.Bone)})###select", _motionSelected == m.Id))
                _motionSelected = m.Id;
        }
        if (remove != null)
        {
            list.Remove(remove);
            _config.Save();
        }

        var selected = list.Find(m => m.Id == _motionSelected);
        if (selected != null)
        {
            ImGui.Separator();
            using var id = ImRaii.PushId("motion_edit_" + selected.Id);
            DrawMotionEditor(selected);
        }
    }

    private void Restart(MotionKind? kind)
    {
        foreach (var m in MotionList)
            if (kind == null || m.Kind == kind)
            {
                m.Phase = 0;
                m.Waiting = 0;
            }
    }

    private void AddMotion(MotionKind kind, string bone)
    {
        var m = new MotionProp
        {
            Kind = kind,
            Name = $"{kind} {MotionList.Count + 1}",
            Character = _addOn.Name,
            IndexHint = _addOn.ObjectIndex,
            Bone = bone,
        };
        if (kind == MotionKind.Cycle)
            m.Points = new() { new MotionPoint(new Vector3(0f, 0.05f, 0.05f)), new MotionPoint(new Vector3(0f, 0f, 0.1f)) };
        MotionList.Add(m);
        _motionSelected = m.Id;
        _config.Save();
    }

    private void DrawMotionEditor(MotionProp m)
    {
        if (ImGui.Button("Delete this motion###motion_delete"))
        {
            MotionList.Remove(m);
            _config.Save();
            return;
        }
        string name = m.Name;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X * 0.6f);
        if (ImGui.InputText("Name###motion_name", ref name, 64))
            m.Name = name;
        SaveWhenDone();

        ImGui.TextUnformatted($"Bone: {HandleCatalog.Label(m.Bone)}");
        if (_addOn.IsSet)
        {
            ImGui.SameLine();
            string bone = BoneToAddOn();
            if (bone != m.Bone && ImGui.SmallButton($"Move to {HandleCatalog.Label(bone)}###motion_rebone"))
            {
                m.Bone = bone;
                m.Character = _addOn.Name;
                m.IndexHint = _addOn.ObjectIndex;
                _config.Save();
            }
        }

        float speed = m.Speed;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X * 0.6f);
        if (ImGui.SliderFloat("Speed###motion_speed", ref speed, 0f, 5f, "%.2f / s"))
            m.Speed = speed;
        SaveWhenDone();
        Tooltip(m.Kind switch
        {
            MotionKind.Piston => "Strokes per second.",
            MotionKind.Wheel => "Turns (or swings) per second.",
            _ => "Laps per second.",
        });

        float delay = m.Delay;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X * 0.6f);
        if (ImGui.DragFloat("Delay (s)###motion_delay", ref delay, 0.05f, 0f, 60f, "%.2f"))
            m.Delay = Math.Clamp(delay, 0f, 60f);
        SaveWhenDone();
        Tooltip("Waits this long at the start after each stroke / turn / lap before going again.");

        int pattern = (int)m.Pattern;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X * 0.6f);
        if (ImGui.Combo("Pattern###motion_pattern", ref pattern, PatternNames, PatternNames.Length))
        {
            m.Pattern = (MotionPattern)pattern;
            _config.Save();
        }
        Tooltip("Smooth: eases in and out.  Steady: same speed.  Thrust: quick out, slow back.\nPull: slow out, quick back.  Bounce: sharp turn at the start.  Pause: holds at each end.\nRandom: every stroke a little different.");

        if (m.Kind != MotionKind.Cycle)
        {
            var direction = new Vector2(m.Yaw, m.Pitch);
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X * 0.6f);
            if (ImGui.DragFloat2(m.Kind == MotionKind.Piston ? "Direction (°)###motion_dir" : "Axle (°)###motion_dir", ref direction, 0.5f, -180f, 180f, "%.0f"))
            {
                m.Yaw = direction.X;
                m.Pitch = direction.Y;
            }
            SaveWhenDone();
            Tooltip("Turn (around) and tilt (up / down), from straight forward of the character.");
        }

        switch (m.Kind)
        {
            case MotionKind.Piston:
            {
                float distance = m.Distance * 100f;
                ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X * 0.6f);
                if (ImGui.DragFloat("Distance (cm)###motion_distance", ref distance, 0.1f, -100f, 100f, "%.1f"))
                    m.Distance = distance / 100f;
                SaveWhenDone();
                break;
            }

            case MotionKind.Wheel:
            {
                var center = m.Center * 100f;
                ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X * 0.6f);
                if (ImGui.DragFloat3("Centre (cm)###motion_center", ref center, 0.1f, -100f, 100f, "%.1f"))
                    m.Center = center / 100f;
                SaveWhenDone();
                Tooltip("Where the wheel's middle is, from the bone (left, up, forward). The distance is its radius.");

                bool full = m.FullSpin;
                if (ImGui.Checkbox("Full spin###motion_full", ref full))
                {
                    m.FullSpin = full;
                    _config.Save();
                }
                Tooltip("On: keeps turning all the way round. Off: swings back and forth over the angle.");
                if (!m.FullSpin)
                {
                    ImGui.SameLine();
                    float angle = m.Angle;
                    ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X * 0.5f);
                    if (ImGui.SliderFloat("Angle###motion_angle", ref angle, 1f, 360f, "%.0f°"))
                        m.Angle = angle;
                    SaveWhenDone();
                }
                bool reverse = m.Reverse;
                if (ImGui.Checkbox("Reverse###motion_reverse", ref reverse))
                {
                    m.Reverse = reverse;
                    _config.Save();
                }
                ImGui.SameLine();
                bool turn = m.TurnBone;
                if (ImGui.Checkbox("Turn the bone too###motion_turn", ref turn))
                {
                    m.TurnBone = turn;
                    _config.Save();
                }
                Tooltip("The bone also turns with the wheel, not just goes round.");
                break;
            }

            case MotionKind.Cycle:
            {
                bool curved = m.Curved;
                if (ImGui.Checkbox("Curved###motion_curved", ref curved))
                {
                    m.Curved = curved;
                    _config.Save();
                }
                Tooltip("On: a smooth curve through the points. Off: straight lines between them.");
                ImGui.SameLine();
                bool back = m.BackAndForth;
                if (ImGui.Checkbox("Back and forth###motion_back", ref back))
                {
                    m.BackAndForth = back;
                    _config.Save();
                }
                Tooltip("On: goes to the last point and back the same way. Off: loops back to the start.");

                ImGui.TextDisabled("Point 1 is where the bone is held. Points (cm, left / up / forward from it):");
                var points = m.Points;
                int removeAt = -1;
                for (int i = 0; i < points.Count; i++)
                {
                    using var pid = ImRaii.PushId(i);
                    var v = points[i].Value * 100f;
                    ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X * 0.6f);
                    if (ImGui.DragFloat3($"Point {i + 2}###motion_point", ref v, 0.1f, -200f, 200f, "%.1f"))
                    {
                        var copy = new List<MotionPoint>(points);
                        copy[i] = new MotionPoint(v / 100f);
                        m.Points = points = copy;
                    }
                    SaveWhenDone();
                    ImGui.SameLine();
                    if (ImGui.SmallButton("x###motion_point_remove"))
                        removeAt = i;
                }
                if (removeAt >= 0)
                {
                    var copy = new List<MotionPoint>(points);
                    copy.RemoveAt(removeAt);
                    m.Points = copy;
                    _config.Save();
                }
                if (ImGui.Button("Add point###motion_point_add"))
                {
                    var last = points.Count > 0 ? points[^1].Value : Vector3.Zero;
                    m.Points = new List<MotionPoint>(points) { new MotionPoint(last + new Vector3(0f, 0f, 0.05f)) };
                    _config.Save();
                }
                break;
            }
        }
    }

    // Both collision strengths in one place: bones (here) and the live mesh's (same as in Rigging > Live mesh).
    private void DrawStrength()
    {
        float bone = Math.Clamp(_config.PropBoneStrength, 0f, 1f) * 100f;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X * 0.6f);
        if (ImGui.SliderFloat("Bone collision strength###prop_bone_strength", ref bone, 0f, 100f, "%.0f%%"))
            _config.PropBoneStrength = bone / 100f;
        if (ImGui.IsItemDeactivatedAfterEdit())
            _config.Save();
        Tooltip("How hard colliders push the soft body bones. Lower lets the breast / testicle give in.");

        float mesh = Math.Clamp(Limber.Gpu.LiveSoftBody.Strength, 0f, 1f) * 100f;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X * 0.6f);
        if (ImGui.SliderFloat("Mesh collision strength###prop_mesh_strength", ref mesh, 0f, 100f, "%.0f%%"))
            Limber.Gpu.LiveSoftBody.Strength = mesh / 100f;
        Tooltip("How hard colliders dent the live mesh. Lower lets the skin seep in.");
    }

    private void DrawEditor(PropCollider prop)
    {
        bool found = _props.World.Any(p => p.Id == prop.Id);
        if (prop.Enabled && !found)
        {
            ImGui.TextColored(Warning, prop.IsWorld ? "Not placed." :
                $"Not placed: {prop.ParentCharacter} isn't here, or has no bone \"{prop.ParentBone}\".");
        }

        string name = prop.Name;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("###prop_name", ref name, 64))
        {
            prop.Name = name;
            Changed(false);
        }
        SaveWhenDone();

        bool enabled = prop.Enabled;
        if (ImGui.Checkbox("On###prop_on", ref enabled))
        {
            prop.Enabled = enabled;
            Changed(true);
        }
        ImGui.SameLine();
        bool visible = prop.Visible;
        if (ImGui.Checkbox("Show wireframe###prop_visible", ref visible))
        {
            prop.Visible = visible;
            Changed(true);
        }
        bool bones = prop.BoneCollision;
        if (ImGui.Checkbox("Bone collision###prop_bones", ref bones))
        {
            prop.BoneCollision = bones;
            Changed(true);
        }
        Tooltip("Pushes the soft body bones: moves the breast / testicle as a whole.");
        ImGui.SameLine();
        bool mesh = prop.MeshCollision;
        if (ImGui.Checkbox("Mesh collision###prop_mesh", ref mesh))
        {
            prop.MeshCollision = mesh;
            Changed(true);
        }
        Tooltip("Dents the skin of the live mesh (Rigging > Live mesh).");
        float boneStrength = Math.Clamp(prop.BoneStrength, 0f, 1f) * 100f;
        ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
        using (ImRaii.Disabled(!prop.BoneCollision))
        {
            if (ImGui.SliderFloat("Bone strength###prop_bone_strength", ref boneStrength, 0f, 100f, "%.0f%%"))
            {
                prop.BoneStrength = boneStrength / 100f;
                Changed(false);
            }
        }
        if (ImGui.IsItemDeactivatedAfterEdit())
            Changed(true);
        Tooltip("This collider's own bone collision strength (on top of the overall one).");
        ImGui.SameLine();
        float meshStrength = Math.Clamp(prop.MeshStrength, 0f, 1f) * 100f;
        ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
        using (ImRaii.Disabled(!prop.MeshCollision))
        {
            if (ImGui.SliderFloat("Mesh strength###prop_mesh_strength", ref meshStrength, 0f, 100f, "%.0f%%"))
            {
                prop.MeshStrength = meshStrength / 100f;
                Changed(false);
            }
        }
        if (ImGui.IsItemDeactivatedAfterEdit())
            Changed(true);
        Tooltip("This collider's own mesh collision strength (on top of the overall one).");
        float cling = Math.Clamp(prop.Cling, 0f, 1f) * 100f;
        ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
        using (ImRaii.Disabled(!prop.MeshCollision))
        {
            if (ImGui.SliderFloat("Skin cling###prop_cling", ref cling, 0f, 100f, "%.0f%%"))
            {
                prop.Cling = cling / 100f;
                Changed(false);
            }
        }
        if (ImGui.IsItemDeactivatedAfterEdit())
            Changed(true);
        Tooltip("Skin near it is drawn onto it and wraps round it (something held between the breasts). Live mesh soft body only.");
        ImGui.SameLine();
        bool scaled = prop.ScaleWithCharacter;
        if (ImGui.Checkbox("Scale with character###prop_scaled", ref scaled))
        {
            prop.ScaleWithCharacter = scaled;
            Changed(true);
        }
        Tooltip("Scales with the character.");

        // Shape: a capsule (round) or a box (flat sides).
        if (ImGui.RadioButton("Capsule###prop_shape_capsule", !prop.Box))
        {
            prop.Box = false;
            Changed(true);
        }
        Tooltip("Pill-shaped: a line with a radius, round at both ends.");
        ImGui.SameLine();
        if (ImGui.RadioButton("Box###prop_shape_box", prop.Box))
        {
            prop.Box = true;
            Changed(true);
        }
        Tooltip("A box with flat sides (slightly rounded corners). Big flat ones flatten a breast pressed onto them instead of pushing it into the body.");

        // Size, in centimetres.
        if (prop.Box)
        {
            var box = new Vector3(prop.Width, prop.Height, prop.Depth) * 100f;
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X * 0.6f);
            if (ImGui.DragFloat3("Size (cm)###prop_box_size", ref box, 0.2f, 0.2f, 2000f, "%.1f"))
            {
                prop.Width = Math.Clamp(box.X, 0.2f, 2000f) / 100f;
                prop.Height = Math.Clamp(box.Y, 0.2f, 2000f) / 100f;
                prop.Depth = Math.Clamp(box.Z, 0.2f, 2000f) / 100f;
                Changed(false);
            }
            SaveWhenDone();
            Tooltip("Width, height and depth (its own x, up and z).");
        }
        else
        {
        float radius = prop.Radius * 100f;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X * 0.6f);
        if (ImGui.DragFloat("Radius (cm)###prop_radius", ref radius, 0.05f, 0.1f, 200f, "%.1f"))
        {
            prop.Radius = Math.Clamp(radius, 0.1f, 200f) / 100f;
            Changed(false);
        }
        SaveWhenDone();
        float length = prop.Length * 100f;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X * 0.6f);
        if (ImGui.DragFloat("Length (cm)###prop_length", ref length, 0.1f, 0f, 500f, "%.1f"))
        {
            prop.Length = Math.Clamp(length, 0f, 500f) / 100f;
            Changed(false);
        }
        SaveWhenDone();
        Tooltip("Between the centres of the two round ends (0 = a ball).");
        }

        // Placement.
        var offset = prop.Offset * 100f;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X * 0.6f);
        if (ImGui.DragFloat3(prop.IsWorld ? "Position (cm)###prop_offset" : "Offset (cm)###prop_offset", ref offset, 0.1f))
        {
            if (MathUtil.IsFinite(offset))
                prop.Offset = offset / 100f;
            Changed(false);
        }
        SaveWhenDone();
        Tooltip(prop.IsWorld ? "Where it is in the world." : "From the bone, along the bone's own axes.");
        var turn = new Vector3(prop.Pitch, prop.Yaw, prop.Roll);
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X * 0.6f);
        if (ImGui.DragFloat3("Rotation (°)###prop_rotation", ref turn, 0.5f))
        {
            if (MathUtil.IsFinite(turn))
            {
                prop.Pitch = turn.X;
                prop.Yaw = turn.Y;
                prop.Roll = turn.Z;
            }
            Changed(false);
        }
        SaveWhenDone();

        if (ImGui.SmallButton("Centre on bone###prop_zero_offset"))
        {
            prop.Offset = prop.IsWorld ? prop.Offset : Vector3.Zero;
            Changed(true);
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Reset rotation###prop_zero_rotation"))
        {
            prop.Pitch = prop.Yaw = prop.Roll = 0f;
            Changed(true);
        }

        DrawParent(prop);

        ImGui.Spacing();
        float third = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X * 2) / 3f;
        if (ImGui.Button("Duplicate###prop_duplicate", new Vector2(third, 0)))
        {
            var copy = prop.Clone();
            copy.Id = Guid.NewGuid();
            copy.Name = prop.Name + " copy";
            Props.Insert(Props.IndexOf(prop) + 1, copy);
            _overlay.Selected = copy;
            Changed(true);
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(prop.IsWorld || OtherSide(prop.ParentBone) == null))
        {
            if (ImGui.Button("Mirror copy###prop_mirror", new Vector2(third, 0)))
            {
                Mirror(prop);
            }
        }
        Tooltip("A copy on the other side.");
        ImGui.SameLine();
        if (ImGui.Button("Delete###prop_delete", new Vector2(third, 0)))
        {
            Props.Remove(prop);
            _overlay.Selected = null;
            Changed(true);
        }
    }

    // Which character and bone it's on.
    private void DrawParent(PropCollider prop)
    {
        ImGui.Spacing();
        ImGui.Text("On:");
        ImGui.SameLine();
        var characters = _selection.ListCharacters();
        string preview = prop.IsWorld ? "Nothing (stays in the world)" : prop.ParentCharacter;
        ImGui.SetNextItemWidth(-1);
        using (var combo = ImRaii.Combo("###prop_parent", preview))
        {
            if (combo.Success)
            {
                if (ImGui.Selectable("Nothing (stays in the world)###prop_parent_world", prop.IsWorld))
                    Reparent(prop, default, null);
                foreach (var character in characters)
                {
                    bool current = !prop.IsWorld && character.Name == prop.ParentCharacter && character.ObjectIndex == prop.ParentIndexHint;
                    if (ImGui.Selectable($"{character.Label}###prop_parent_{character.Address:X}", current))
                        Reparent(prop, character, string.IsNullOrEmpty(prop.ParentBone) ? "j_kosi" : prop.ParentBone);
                }
            }
        }
        if (prop.IsWorld)
            return;

        ImGui.Text("Bone:");
        ImGui.SameLine();
        ImGui.TextColored(Accent, $"{HandleCatalog.Label(prop.ParentBone)}  ({prop.ParentBone})");
        ImGui.SameLine();
        if (ImGui.SmallButton("Check this bone###prop_check_bone") && PropService.Find(Plugin.ObjectTable, prop, Plugin.ClientState.IsGPosing, out var ca, out var ci))
        {
            var name = prop.ParentBone;
            _boneCheck = ["Checking..."];
            _props.WithSkeleton(ca, ci, (in ActorSkeleton skeleton) => skeleton.DescribeBone(name, _checkCache), lines =>
                _boneCheck = lines?.ToArray() ?? ["Couldn't read the skeleton."]);
        }
        Tooltip("Lists every skeleton of this character that has this bone (the body and any extra ones) and where each\n" +
                "copy is. Copy it to me if a collider or a pose doesn't follow what's drawn (Ctrl+click a line copies it).");
        if (_boneCheck != null)
            foreach (var line in _boneCheck)
            {
                ImGui.TextWrapped(line);
                if (ImGui.IsItemClicked() && ImGui.GetIO().KeyCtrl)
                    ImGui.SetClipboardText(string.Join("\n", _boneCheck));
            }

        if (!PropService.Find(Plugin.ObjectTable, prop, Plugin.ClientState.IsGPosing, out var address, out var index))
        {
            ImGui.TextDisabled("They aren't here right now.");
            return;
        }
        if (_namesAddress != address || _names == null)
        {
            if (_namesAddress != address)
            {
                _namesAddress = address;
                _names = null;
                _namesPending = false;
            }
            if (!_namesPending)
            {
                _namesPending = true;
                var asked = address;
                _props.WithSkeleton(address, index, (in ActorSkeleton skeleton) => skeleton.AllBoneNames(_checkCache), names =>
                {
                    if (_namesAddress != asked)
                        return;
                    _names = names;
                    _namesPending = false;
                });
            }
        }
        var boneNames = _names;
        if (boneNames == null)
        {
            ImGui.TextDisabled("Reading their skeleton...");
            return;
        }

        if (_selection.Actor.Address == address && !string.IsNullOrEmpty(_selection.Bone) && _selection.Bone != prop.ParentBone)
        {
            if (ImGui.SmallButton($"Move onto {HandleCatalog.Label(_selection.Bone!)} (chosen in Limber)###prop_to_selected"))
                Reparent(prop, new ActorRef(address, index, prop.ParentCharacter), _selection.Bone);
        }

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("###prop_bone_search", "Search their bones (e.g. hand, finger, chest)", ref _boneSearch, 64);
        if (_boneSearch.Length == 0)
            return;
        using var list = ImRaii.Child("###prop_bone_list", new Vector2(-1, 120 * ImGuiHelpers.GlobalScale), true);
        if (!list.Success)
            return;
        bool adult = Limber.Koi.Safety.AdultAllowed(address);
        foreach (var name in boneNames)
        {
            if (!HandleCatalog.IsPickable(name, adult))
                continue; // body mod bones: adult bodies only
            string label = HandleCatalog.Label(name);
            if (!name.Contains(_boneSearch, StringComparison.OrdinalIgnoreCase) && !label.Contains(_boneSearch, StringComparison.OrdinalIgnoreCase))
                continue;
            if (ImGui.Selectable($"{(label == name ? name : $"{label}  ({name})")}###prop_bone_{name}", name == prop.ParentBone))
                Reparent(prop, new ActorRef(address, index, prop.ParentCharacter), name);
        }
    }

    // Move a prop onto another bone (or into the world) where it is now, so it doesn't jump.
    private void Reparent(PropCollider prop, ActorRef character, string? bone)
    {
        bool hadWorld = TryPlaced(prop, out var world);
        if (!character.IsSet || bone == null)
        {
            prop.ParentCharacter = "";
            prop.ParentBone = "";
            prop.ParentIndexHint = -1;
            prop.ScaleWithCharacter = false;
            if (hadWorld)
            {
                prop.Offset = world.Frame.Position;
                prop.Rotation = world.Frame.Rotation;
            }
            Changed(true);
            return;
        }

        prop.ParentCharacter = character.Name;
        prop.ParentIndexHint = character.ObjectIndex;
        prop.ParentBone = bone;
        Changed(true);
        if (!hadWorld)
            return;

        // Keep it where it was: its place relative to the new bone, worked out from the pose in the bone update.
        var target = world.Frame;
        bool scaled = prop.ScaleWithCharacter;
        _props.WithSkeleton(character.Address, character.ObjectIndex, (in ActorSkeleton skeleton) =>
        {
            int i = skeleton.Info.IndexOf(bone);
            if (i < 0)
                return (Frame?)null;
            float scale = scaled && skeleton.RootScale.Y > 0.01f ? skeleton.RootScale.Y : 1f;
            var local = skeleton.GetWorldFrame(i).ToLocal(target);
            return new Frame(local.Position / scale, local.Rotation);
        }, local =>
        {
            if (local == null || prop.ParentBone != bone || prop.ParentCharacter != character.Name)
                return;
            prop.Offset = local.Value.Position;
            prop.Rotation = local.Value.Rotation;
            Changed(true);
        });
    }

    private bool TryPlaced(PropCollider prop, out PropWorld world)
    {
        foreach (var p in _props.World)
        {
            if (p.Id == prop.Id)
            {
                world = p;
                return true;
            }
        }
        world = default;
        return false;
    }

    // ---- Mirroring onto the other side

    private static string? OtherSide(string bone)
    {
        if (bone.EndsWith("_l", StringComparison.Ordinal))
            return bone[..^2] + "_r";
        if (bone.EndsWith("_r", StringComparison.Ordinal))
            return bone[..^2] + "_l";
        return null;
    }

    private static string MirrorName(string name)
    {
        if (name.Contains("Left", StringComparison.Ordinal))
            return name.Replace("Left", "Right", StringComparison.Ordinal);
        if (name.Contains("Right", StringComparison.Ordinal))
            return name.Replace("Right", "Left", StringComparison.Ordinal);
        return name + " (mirrored)";
    }

    // The same prop on the other side's bone, as the mirror image across the character's middle.
    private void Mirror(PropCollider prop)
    {
        var other = OtherSide(prop.ParentBone);
        if (other == null || !PropService.Find(Plugin.ObjectTable, prop, Plugin.ClientState.IsGPosing, out var address, out var index))
            return;
        var source = prop.Clone();
        _props.WithSkeleton(address, index, (in ActorSkeleton skeleton) => MirrorOf(skeleton, source, other), copy =>
        {
            if (copy == null)
                return;
            int at = Props.IndexOf(prop);
            Props.Insert(at < 0 ? Props.Count : at + 1, copy);
            _overlay.Selected = copy;
            Changed(true);
        });
    }

    // Bone update only.
    private static PropCollider? MirrorOf(in ActorSkeleton skeleton, PropCollider prop, string other)
    {
        int from = skeleton.Info.IndexOf(prop.ParentBone), to = skeleton.Info.IndexOf(other);
        if (from < 0 || to < 0)
            return null;

        // In model space (no character size): left/right is X.
        float scale = skeleton.RootScale.Y > 0.01f ? skeleton.RootScale.Y : 1f;
        float toModel = prop.ScaleWithCharacter ? 1f : 1f / scale;
        var model = skeleton.GetModelFrame(from).FromLocal(new Frame(prop.Offset * toModel, prop.Rotation));
        var mirrored = new Frame(new Vector3(-model.Position.X, model.Position.Y, model.Position.Z),
            Quaternion.Normalize(new Quaternion(model.Rotation.X, -model.Rotation.Y, -model.Rotation.Z, model.Rotation.W)));
        var local = skeleton.GetModelFrame(to).ToLocal(mirrored);

        var copy = prop.Clone();
        copy.Id = Guid.NewGuid();
        copy.Name = MirrorName(prop.Name);
        copy.ParentBone = other;
        copy.Offset = local.Position / toModel;
        copy.Rotation = local.Rotation;
        return copy;
    }

    private void SetAllVisible(bool visible)
    {
        foreach (var prop in Props)
            prop.Visible = visible;
        Changed(true);
    }

    // ---- Helpers

    // Edited: place it again now; save now (`save`), or once the slider / box is let go (SaveWhenDone).
    private void Changed(bool save)
    {
        _props.Refresh();
        if (save)
            _config.Save();
    }

    private void SaveWhenDone()
    {
        if (ImGui.IsItemDeactivatedAfterEdit())
            _config.Save();
    }

    private static void SeparatorText(string text)
    {
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.75f, 0.75f, 0.8f, 1f), text);
        ImGui.Separator();
    }

    private static void Tooltip(string text)
    {
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(text);
    }
}
