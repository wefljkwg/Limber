using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Limber.Rig;

namespace Limber.UI;

/// <summary>
/// The Timeline page (Scene): make animations from any of Limber's functions. Tracks per character (FK bones, IK
/// goals, moving the character, bone scales, body shape, every setting of throbbing / twerking / ahegao / look at /
/// dynamic bones, and the scene's Oral and soft body settings), keyframes on a lane per track (click to pick, drag
/// to move, double-click to add, right-click for its curve), played back looping or once at any speed.
/// </summary>
public sealed class TimelineTab
{
    private readonly Selection _selection;
    private readonly RigSettings _config;
    private readonly TimelineService _timeline;

    private readonly HashSet<TimelineKey> _picked = new();
    private TimelineKey? _dragKey;
    private float _dragFrom, _dragStartMouse;
    private Dictionary<TimelineKey, float>? _dragStartTimes;
    private float _viewStart, _viewLength = -1f;
    private readonly HashSet<string> _closed = new();
    private string _rename = "";
    private string _boneName = "";
    private string _animSearch = "";
    private readonly List<TimelineClip> _undo = new(), _redo = new();
    private TimelineKey? _menuKey;
    private TimelineTrack? _menuTrack;

    private static readonly (float Step, string Name)[] Snaps =
    [
        (0f, "Off"), (1f / 30f, "1/30 s"), (0.1f, "0.1 s"), (0.25f, "0.25 s"), (0.5f, "0.5 s"), (1f, "1 s"),
    ];

    private static readonly (TimelineEase Ease, string Name, string Tip)[] Eases =
    [
        (TimelineEase.Smooth, "Smooth", "A smooth curve through the keys (the keys around it shape it)."),
        (TimelineEase.Linear, "Linear", "Straight on to the next key, at an even pace."),
        (TimelineEase.EaseInOut, "Ease in and out", "Slow out of this key, slow into the next."),
        (TimelineEase.EaseIn, "Ease in", "Starts slowly, arrives fast."),
        (TimelineEase.EaseOut, "Ease out", "Starts fast, arrives slowly."),
        (TimelineEase.Bounce, "Bounce", "Overshoots the next key and settles into it."),
        (TimelineEase.Constant, "Constant (cut)", "Holds this key until the next one, then jumps."),
    ];

    private static readonly Vector4 Accent = new(0.55f, 0.75f, 1f, 1f);

    public TimelineTab(Selection selection, RigSettings config, TimelineService timeline)
    {
        _selection = selection;
        _config = config;
        _timeline = timeline;
    }

    private void Checkpoint()
    {
        if(_timeline.Clip is { } clip)
        {
            _undo.Add(clip.Copy());
            if(_undo.Count > 60)
                _undo.RemoveAt(0);
            _redo.Clear();
        }
    }

    private void Restore(List<TimelineClip> from, List<TimelineClip> to)
    {
        var all = _config.Timelines;
        if(from.Count == 0 || all == null || all.Count == 0)
            return;
        int i = Math.Clamp(_config.TimelineIndex, 0, all.Count - 1);
        to.Add(all[i].Copy());
        all[i] = from[^1];
        from.RemoveAt(from.Count - 1);
        _picked.Clear();
        _timeline.Refresh();
        _config.Save();
    }

    /// <summary>The page in the main window: the timeline, or (while it's in its own window) a way to bring it back.</summary>
    public void DrawPage()
    {
        if(_config.TimelineOwnWindow)
        {
            ImGui.TextDisabled("The Timeline is in its own window.");
            if(ImGui.Button("Put it back here###tl_back"))
            {
                _config.TimelineOwnWindow = false;
                _config.Save();
            }
            return;
        }
        Draw();
    }

    public void Draw()
    {
        var all = _config.Timelines ??= new();
        bool own = _config.TimelineOwnWindow;
        if(ImGui.Checkbox("Move to a new window###tl_own_window", ref own))
        {
            _config.TimelineOwnWindow = own;
            _config.Save();
        }
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("The Timeline in a window of its own (move and size it anywhere). Untick to put it back on its page.");
        ImGui.Separator();

        DrawClips(all);
        var clip = _timeline.Clip;
        if(clip == null)
            return;
        ImGui.Separator();
        DrawTransport(clip);
        DrawToolbar(clip);
        DrawPickedKey(clip);
        ImGui.Spacing();
        DrawLanes(clip);

        if(_config.TimelineAutoKey && !_timeline.Playing && _timeline.AutoKey() > 0)
            _config.Save();
    }

    // ---- Animations (clips)

    private void DrawClips(List<TimelineClip> all)
    {
        if(all.Count == 0)
        {
            if(ImGui.Button("New animation###tl_first"))
            {
                all.Add(new TimelineClip { Name = "Animation 1" });
                _config.TimelineIndex = 0;
                _config.Save();
            }
            return;
        }
        int current = Math.Clamp(_config.TimelineIndex, 0, all.Count - 1);
        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        using(var combo = ImRaii.Combo("###tl_clip", all[current].Name))
        {
            if(combo.Success)
                for(int i = 0; i < all.Count; i++)
                    if(ImGui.Selectable($"{all[i].Name}###tl_clip_{i}", i == current))
                    {
                        _timeline.Pause();
                        _config.TimelineIndex = i;
                        _picked.Clear();
                        _undo.Clear();
                        _redo.Clear();
                        _timeline.Seek(0f);
                        _config.Save();
                    }
        }
        ImGui.SameLine();
        if(ImGui.Button("New###tl_new"))
        {
            all.Add(new TimelineClip { Name = $"Animation {all.Count + 1}" });
            _config.TimelineIndex = all.Count - 1;
            _picked.Clear();
            _timeline.Seek(0f);
            _config.Save();
        }
        ImGui.SameLine();
        if(ImGui.Button("Duplicate###tl_dup"))
        {
            var copy = all[current].Copy();
            copy.Name += " (copy)";
            all.Add(copy);
            _config.TimelineIndex = all.Count - 1;
            _config.Save();
        }
        ImGui.SameLine();
        if(ImGui.Button("Rename###tl_rename"))
        {
            _rename = all[current].Name;
            ImGui.OpenPopup("###tl_rename_popup");
        }
        ImGui.SameLine();
        if(ImGui.Button("Delete###tl_delete") && ImGui.GetIO().KeyCtrl)
        {
            _timeline.Pause();
            all.RemoveAt(current);
            _config.TimelineIndex = Math.Max(0, current - 1);
            _picked.Clear();
            _config.Save();
            return;
        }
        Tooltip("Hold Ctrl and click to delete this animation.");
        ImGui.SameLine();
        if(ImGui.Button("Copy###tl_export"))
            ImGui.SetClipboardText(Newtonsoft.Json.JsonConvert.SerializeObject(all[current]));
        Tooltip("Copies this animation as text, to keep or share.");
        ImGui.SameLine();
        if(ImGui.Button("Paste###tl_import"))
        {
            try
            {
                var pasted = Newtonsoft.Json.JsonConvert.DeserializeObject<TimelineClip>(ImGui.GetClipboardText() ?? "");
                if(pasted is { Tracks: not null })
                {
                    all.Add(pasted);
                    _config.TimelineIndex = all.Count - 1;
                    _config.Save();
                }
            }
            catch
            {
                // not an animation
            }
        }
        Tooltip("Adds an animation copied with Copy (as a new one).");

        if(ImGui.BeginPopup("###tl_rename_popup"))
        {
            ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
            if(ImGui.InputText("Name###tl_rename_text", ref _rename, 64, ImGuiInputTextFlags.EnterReturnsTrue) && _rename.Trim().Length > 0)
            {
                all[current].Name = _rename.Trim();
                _config.Save();
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
    }

    // ---- Play / pause, the playhead, length, loop, speed

    private void DrawTransport(TimelineClip clip)
    {
        if(ImGui.Button("|<###tl_start"))
            _timeline.Seek(0f);
        Tooltip("To the start.");
        ImGui.SameLine();
        if(ImGui.Button("< key###tl_prevkey"))
            _timeline.Seek(NextKeyTime(clip, _timeline.Time, back: true));
        Tooltip("To the previous key.");
        ImGui.SameLine();
        using(ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.25f, 0.5f, 0.3f, 1f), !_timeline.Playing))
        using(ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.55f, 0.4f, 0.2f, 1f), _timeline.Playing))
        {
            if(ImGui.Button(_timeline.Playing ? "Pause###tl_play" : "Play###tl_play", new Vector2(70 * ImGuiHelpers.GlobalScale, 0)))
            {
                if(_timeline.Playing)
                    _timeline.Pause();
                else
                    _timeline.Play();
            }
        }
        ImGui.SameLine();
        if(ImGui.Button("Stop###tl_stop"))
            _timeline.Stop();
        Tooltip("Stop and go back to the start.");
        ImGui.SameLine();
        if(ImGui.Button("key >###tl_nextkey"))
            _timeline.Seek(NextKeyTime(clip, _timeline.Time, back: false));
        Tooltip("To the next key.");
        ImGui.SameLine();
        if(ImGui.Button(">|###tl_end"))
            _timeline.Seek(clip.Length);
        Tooltip("To the end.");

        ImGui.SameLine();
        float t = _timeline.Time;
        ImGui.SetNextItemWidth(90 * ImGuiHelpers.GlobalScale);
        if(ImGui.DragFloat("###tl_time", ref t, 0.01f, 0f, clip.Length, "%.2f s"))
            _timeline.Seek(t);
        Tooltip("The playhead (drag, or double-click to type).");
        ImGui.SameLine();
        ImGui.TextUnformatted("/");
        ImGui.SameLine();
        float length = clip.Length;
        ImGui.SetNextItemWidth(90 * ImGuiHelpers.GlobalScale);
        if(ImGui.DragFloat("###tl_length", ref length, 0.05f, 0.1f, 600f, "%.2f s"))
        {
            clip.Length = Math.Clamp(length, 0.1f, 600f);
            _config.Save();
        }
        Tooltip("How long the animation is.");
        ImGui.SameLine();
        bool loop = clip.Loop;
        if(ImGui.Checkbox("Loop###tl_loop", ref loop))
        {
            clip.Loop = loop;
            _config.Save();
        }
        ImGui.SameLine();
        float speed = clip.Speed;
        ImGui.SetNextItemWidth(110 * ImGuiHelpers.GlobalScale);
        if(ImGui.SliderFloat("Speed###tl_speed", ref speed, -3f, 3f, "%.2fx"))
        {
            clip.Speed = speed;
            _config.Save();
        }
        Tooltip("How fast it plays (negative: backwards). Ctrl+click to type.");
    }

    private static float NextKeyTime(TimelineClip clip, float t, bool back)
    {
        float best = back ? 0f : clip.Length;
        foreach(var track in clip.Tracks)
            foreach(var k in track.Keys)
            {
                if(back && k.Time < t - 1e-3f && k.Time > best)
                    best = k.Time;
                if(!back && k.Time > t + 1e-3f && k.Time < best)
                    best = k.Time;
            }
        return best;
    }

    // ---- Adding tracks, keying, snapping, auto key, undo

    private void DrawToolbar(TimelineClip clip)
    {
        if(ImGui.Button("Add track###tl_add"))
            ImGui.OpenPopup("###tl_add_menu");
        Tooltip("Animate something of the character chosen at the top of the window (or the scene's).");
        DrawAddMenu(clip);
        ImGui.SameLine();
        if(ImGui.Button("Add the whole pose###tl_addpose"))
        {
            Checkpoint();
            AddWholePose(clip);
            _config.Save();
        }
        Tooltip("Tracks for the chosen character's FK bones, IK goals and move / turn / size, keyed at the playhead.");
        ImGui.SameLine();
        if(ImGui.Button("Key all###tl_keyall"))
        {
            Checkpoint();
            foreach(var track in clip.Tracks.Where(tr => tr.Enabled))
                _timeline.KeyNow(track);
            _config.Save();
        }
        Tooltip("A key on every track at the playhead, with each one's value as it is now.");
        ImGui.SameLine();
        bool auto = _config.TimelineAutoKey;
        if(ImGui.Checkbox("Auto key###tl_autokey", ref auto))
        {
            _config.TimelineAutoKey = auto;
            _config.Save();
        }
        Tooltip("While paused: whatever you change on the other pages (on a track that's here) is keyed at the playhead by itself.");
        ImGui.SameLine();
        int snap = Math.Max(0, Array.FindIndex(Snaps, s => MathF.Abs(s.Step - _config.TimelineSnap) < 1e-5f));
        ImGui.SetNextItemWidth(80 * ImGuiHelpers.GlobalScale);
        using(var combo = ImRaii.Combo("Snap###tl_snap", Snaps[snap].Name))
        {
            if(combo.Success)
                for(int i = 0; i < Snaps.Length; i++)
                    if(ImGui.Selectable(Snaps[i].Name, i == snap))
                    {
                        _config.TimelineSnap = Snaps[i].Step;
                        _config.Save();
                    }
        }
        Tooltip("Keys and the playhead snap to this step when dragged.");
        ImGui.SameLine();
        using(ImRaii.Disabled(_undo.Count == 0))
            if(ImGui.Button("Undo###tl_undo"))
                Restore(_undo, _redo);
        ImGui.SameLine();
        using(ImRaii.Disabled(_redo.Count == 0))
            if(ImGui.Button("Redo###tl_redo"))
                Restore(_redo, _undo);
    }

    private void AddWholePose(TimelineClip clip)
    {
        var actor = _selection.Actor;
        string name = actor.Name ?? "";
        if(!actor.IsSet || name.Length == 0)
            return;
        foreach(var bone in _timeline.FkBones(name))
            _timeline.AddTrack(clip, name, "fk.rot", bone);
        foreach(var (bone, space) in _timeline.IkGoals(name))
            _timeline.AddTrack(clip, name, "ik", bone, space.ToString());
        _timeline.AddTrack(clip, name, "move", "");
    }

    private void DrawAddMenu(TimelineClip clip)
    {
        if(!ImGui.BeginPopup("###tl_add_menu"))
            return;
        var actor = _selection.Actor;
        string name = actor.IsSet ? actor.Name ?? "" : "";
        if(name.Length == 0)
            ImGui.TextDisabled("Choose a character at the top of the window for its tracks.");
        else
        {
            ImGui.TextColored(Accent, name);

            // The bone picked in the scene (or a posing page's list), for any bone at all.
            if(_selection.Bone is { Length: > 0 } picked)
            {
                if(ImGui.BeginMenu($"Picked bone: {HandleCatalog.Label(picked)}###tl_m_picked"))
                {
                    if(ImGui.MenuItem("FK turn###tl_mp_fkr"))
                        Add(clip, name, "fk.rot", picked);
                    if(ImGui.MenuItem("FK move###tl_mp_fkp"))
                        Add(clip, name, "fk.pos", picked);
                    var goal = _timeline.IkGoals(name).FirstOrDefault(g => g.Bone == picked);
                    if(goal.Bone != null && ImGui.MenuItem($"IK goal{(goal.Space == GoalSpace.Pinned ? " (pinned)" : "")}###tl_mp_ik"))
                        Add(clip, name, "ik", picked, goal.Space.ToString());
                    if(ImGui.MenuItem("Scale###tl_mp_sc"))
                        Add(clip, name, "scale", picked);
                    ImGui.EndMenu();
                }
            }
            else
                ImGui.TextDisabled("(click a bone's dot to pick it: its FK / IK / scale go here)");

            if(ImGui.BeginMenu("FK bone (turn)###tl_m_fk"))
            {
                var bones = _timeline.FkBones(name);
                if(bones.Count == 0)
                    ImGui.TextDisabled("No bones posed with FK yet (FK Posing page), or type one below.");
                if(bones.Count > 1 && ImGui.MenuItem("All FK bones###tl_m_fk_all"))
                {
                    Checkpoint();
                    foreach(var b in bones)
                        _timeline.AddTrack(clip, name, "fk.rot", b);
                    _config.Save();
                }
                foreach(var b in bones)
                    if(ImGui.MenuItem($"{HandleCatalog.Label(b)}  ({b})###tl_m_fk_{b}"))
                        Add(clip, name, "fk.rot", b);
                BoneByName(clip, name, "fk.rot");
                ImGui.EndMenu();
            }
            if(ImGui.BeginMenu("FK bone (move)###tl_m_fkpos"))
            {
                foreach(var b in _timeline.FkBones(name))
                    if(ImGui.MenuItem($"{HandleCatalog.Label(b)}  ({b})###tl_m_fkp_{b}"))
                        Add(clip, name, "fk.pos", b);
                BoneByName(clip, name, "fk.pos");
                ImGui.EndMenu();
            }
            if(ImGui.BeginMenu("IK goal###tl_m_ik"))
            {
                var goals = _timeline.IkGoals(name);
                if(goals.Count == 0)
                    ImGui.TextDisabled("No IK goals yet: drag or pin a dot on the IK Posing page first.");
                if(goals.Count > 1 && ImGui.MenuItem("All IK goals###tl_m_ik_all"))
                {
                    Checkpoint();
                    foreach(var (b, space) in goals)
                        _timeline.AddTrack(clip, name, "ik", b, space.ToString());
                    _config.Save();
                }
                foreach(var (b, space) in goals)
                    if(ImGui.MenuItem($"{HandleCatalog.Label(b)}{(space == GoalSpace.Pinned ? " (pinned)" : "")}###tl_m_ik_{b}"))
                        Add(clip, name, "ik", b, space.ToString());
                ImGui.EndMenu();
            }
            if(ImGui.MenuItem("Game animation###tl_m_anim"))
                Add(clip, name, "anim", "");
            if(ImGui.IsItemHovered())
                ImGui.SetTooltip("The game's animations (emotes, poses...): each key plays one, at its speed, until the next key. Pick a key to choose its animation.");
            if(ImGui.MenuItem("Move / turn / size###tl_m_move"))
                Add(clip, name, "move", "");
            if(ImGui.BeginMenu("Bone scale###tl_m_scale"))
            {
                var bones = _timeline.ScaledBones(name);
                if(bones.Count == 0)
                    ImGui.TextDisabled("No bones scaled yet (Bone scale page), or type one below.");
                foreach(var b in bones)
                    if(ImGui.MenuItem($"{HandleCatalog.Label(b)}  ({b})###tl_m_sc_{b}"))
                        Add(clip, name, "scale", b);
                BoneByName(clip, name, "scale");
                ImGui.EndMenu();
            }
            if(ImGui.BeginMenu("Body shape###tl_m_shape"))
            {
                foreach(var group in Limber.BodyShape.BodySliders.All.Where(sl => !sl.BuildOnly).GroupBy(sl => sl.Group))
                {
                    if(!ImGui.BeginMenu($"{group.Key}###tl_m_sg_{group.Key}"))
                        continue;
                    foreach(var slider in group)
                        if(ImGui.MenuItem($"{slider.Name}###tl_m_s_{slider.Key}"))
                            Add(clip, name, "shape", slider.Key);
                    ImGui.EndMenu();
                }
                ImGui.EndMenu();
            }
            foreach(var (property, feature, depth) in TimelineService.Features)
                SettingsMenu(clip, name, "path", property, feature, depth, name);
        }
        ImGui.Separator();
        ImGui.TextDisabled("The scene");
        if(ImGui.MenuItem("Camera###tl_m_camera"))
            Add(clip, "", "camera", "");
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("The game camera: distance, field of view, angles and tilt (set it on the Camera page, then key it).");
        foreach(var (property, feature, depth) in TimelineService.Globals)
            SettingsMenu(clip, "", "path", property, feature, depth, "");
        ImGui.EndPopup();
    }

    // A feature's menu: its settings, and a submenu for each part of it (a bone, a prop, a section...).
    private void SettingsMenu(TimelineClip clip, string character, string kind, string property, string feature, int depth, string owner)
    {
        if(!ImGui.BeginMenu($"{feature}###tl_m_{property}_{owner}"))
            return;
        var settings = _timeline.SettingsOf(property, character, depth);
        if(settings.Count == 0)
            ImGui.TextDisabled("Nothing here yet.");
        void Item((string Path, string Group, string Name, Type Type) setting)
        {
            string tag = setting.Type == typeof(float) || setting.Type == typeof(double) ? ""
                : setting.Type == typeof(Vector3) ? " (x / y / z)" : setting.Type == typeof(Quaternion) ? " (rotation)"
                : setting.Type == typeof(string) ? " (text)" : " (switch / choice)";
            if(ImGui.MenuItem($"{setting.Name}{tag}###tl_mi_{setting.Path}"))
            {
                string label = $"{feature}: {(setting.Group.Length > 0 ? setting.Group + " / " : "")}{setting.Name}";
                Checkpoint();
                _timeline.AddTrack(clip, character, kind, setting.Path, TimelineService.ExtraFor(setting.Type), name: label);
                _closed.Remove(character);
                _config.Save();
            }
        }
        foreach(var setting in settings.Where(x => x.Group.Length == 0))
            Item(setting);
        foreach(var group in settings.Where(x => x.Group.Length > 0).GroupBy(x => x.Group))
        {
            if(!ImGui.BeginMenu($"{group.Key}###tl_mg_{property}_{group.Key}"))
                continue;
            foreach(var setting in group)
                Item(setting);
            ImGui.EndMenu();
        }
        ImGui.EndMenu();
    }

    private void BoneByName(TimelineClip clip, string name, string kind)
    {
        ImGui.Separator();
        ImGui.SetNextItemWidth(180 * ImGuiHelpers.GlobalScale);
        if(ImGui.InputText($"Bone name###tl_bone_{kind}", ref _boneName, 64, ImGuiInputTextFlags.EnterReturnsTrue) && _boneName.Trim().Length > 0)
        {
            Add(clip, name, kind, _boneName.Trim());
            ImGui.CloseCurrentPopup();
        }
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("Any bone by its name, then Enter (hover a dot on the posing pages to see a bone's name).");
    }

    private void Add(TimelineClip clip, string character, string kind, string target, string extra = "")
    {
        Checkpoint();
        _timeline.AddTrack(clip, character, kind, target, extra);
        _closed.Remove(character);
        _config.Save();
    }

    // ---- The picked key

    private void DrawPickedKey(TimelineClip clip)
    {
        if(_picked.Count == 0)
        {
            ImGui.TextDisabled("Click a key to pick it (Ctrl: more), drag to move, double-click a lane to add one, right-click for its curve. Delete removes picked keys.");
            return;
        }
        var key = _picked.First();
        var track = clip.Tracks.FirstOrDefault(tr => tr.Keys.Contains(key));
        if(track == null)
        {
            _picked.Clear();
            return;
        }
        ImGui.TextColored(Accent, _picked.Count == 1 ? $"Key: {TimelineService.Label(track)}" : $"{_picked.Count} keys");
        ImGui.SameLine();
        float time = key.Time;
        ImGui.SetNextItemWidth(90 * ImGuiHelpers.GlobalScale);
        if(_picked.Count == 1 && ImGui.DragFloat("###tl_key_time", ref time, 0.01f, 0f, clip.Length, "%.2f s"))
        {
            key.Time = Math.Clamp(time, 0f, clip.Length);
            track.Keys.Sort((a, b) => a.Time.CompareTo(b.Time));
            _timeline.Refresh();
            _config.Save();
        }
        if(ImGui.IsItemActivated())
            Checkpoint();
        ImGui.SameLine();
        string ease = Array.Find(Eases, x => x.Ease == key.Ease).Name ?? key.Ease.ToString();
        ImGui.SetNextItemWidth(140 * ImGuiHelpers.GlobalScale);
        using(var combo = ImRaii.Combo("Curve###tl_key_ease", ease))
        {
            if(combo.Success)
                foreach(var (e, label, tip) in Eases)
                {
                    if(ImGui.Selectable(label, key.Ease == e))
                    {
                        Checkpoint();
                        foreach(var k in _picked)
                            k.Ease = e;
                        _timeline.Refresh();
                        _config.Save();
                    }
                    Tooltip(tip);
                }
        }
        Tooltip("How it goes from this key to the next.");
        ImGui.SameLine();
        if(ImGui.Button("Set to now###tl_key_set"))
        {
            Checkpoint();
            foreach(var k in _picked)
            {
                var tr = clip.Tracks.FirstOrDefault(x => x.Keys.Contains(k));
                if(tr != null && _timeline.TryRead(tr, out var v))
                    k.Value = v;
            }
            _timeline.Refresh();
            _config.Save();
        }
        Tooltip("The picked keys take their track's value as it is now.");
        ImGui.SameLine();
        if(ImGui.Button("Delete###tl_key_del"))
            DeletePicked(clip);

        // A game animation key: which animation, and how fast.
        if(_picked.Count == 1 && track.Kind == "anim")
            DrawAnimationKey(key);
    }

    private static string AnimationName(ushort id)
    {
        if(id == 0)
            return "(its own animation)";
        foreach(var e in Animations.All)
            if(e.Id == id)
                return e.Name.Length > 0 ? $"{e.Name} ({e.Key})" : e.Key;
        return $"#{id}";
    }

    private void DrawAnimationKey(TimelineKey key)
    {
        if(key.Value.Length < 2)
            key.Value = [key.Value.Length > 0 ? key.Value[0] : 0f, 1f];
        ushort id = (ushort)Math.Clamp((int)MathF.Round(key.Value[0]), 0, ushort.MaxValue);
        ImGui.TextUnformatted("Plays: " + AnimationName(id));
        float speed = key.Value[1];
        ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
        if(ImGui.SliderFloat("Speed###tl_anim_speed", ref speed, 0f, 3f, "%.2fx"))
        {
            key.Value[1] = speed;
            _timeline.Refresh();
            _config.Save();
        }
        if(ImGui.IsItemActivated())
            Checkpoint();
        ImGui.SameLine();
        if(ImGui.Button("Its own animation###tl_anim_own"))
        {
            Checkpoint();
            key.Value[0] = 0f;
            _timeline.Refresh();
            _config.Save();
        }
        Tooltip("From this key on, the character goes back to what it was doing before (its own idle / emote).");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("###tl_anim_search", "Search animations (name or key: dance, sit, pose01...)", ref _animSearch, 64);
        using var list = ImRaii.Child("###tl_anim_list", new Vector2(-1, 140 * ImGuiHelpers.GlobalScale), true);
        if(!list.Success)
            return;
        string q = _animSearch.Trim();
        int shown = 0;
        foreach(var e in Animations.All)
        {
            if(q.Length > 0 && !e.Key.Contains(q, StringComparison.OrdinalIgnoreCase) && !e.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                continue;
            if(++shown > 300)
            {
                ImGui.TextDisabled("(more: search to narrow it down)");
                break;
            }
            string label = e.Name.Length > 0 ? $"{e.Name}  ·  {e.Key}" : e.Key;
            if(ImGui.Selectable($"{label}###tl_anim_{e.Id}", e.Id == id))
            {
                Checkpoint();
                key.Value[0] = e.Id;
                _timeline.Refresh();
                _config.Save();
            }
        }
    }

    private void DeletePicked(TimelineClip clip)
    {
        if(_picked.Count == 0)
            return;
        Checkpoint();
        foreach(var track in clip.Tracks)
            track.Keys.RemoveAll(k => _picked.Contains(k));
        _picked.Clear();
        _timeline.Refresh();
        _config.Save();
    }

    // ---- The lanes

    private void DrawLanes(TimelineClip clip)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float labelWidth = 250f * scale, row = 22f * scale, ruler = 24f * scale;
        if(_viewLength <= 0f || _viewLength > clip.Length * 4f)
        {
            _viewStart = 0f;
            _viewLength = clip.Length;
        }
        using var child = ImRaii.Child("###tl_lanes", new Vector2(-1, -1), true, ImGuiWindowFlags.NoScrollWithMouse);
        if(!child.Success)
            return;
        var draw = ImGui.GetWindowDrawList();
        var io = ImGui.GetIO();
        float width = ImGui.GetContentRegionAvail().X;
        float laneWidth = MathF.Max(60f, width - labelWidth);
        var origin = ImGui.GetCursorScreenPos();
        float laneX = origin.X + labelWidth;
        float X(float t) => laneX + (t - _viewStart) / _viewLength * laneWidth;
        float T(float x) => _viewStart + (x - laneX) / laneWidth * _viewLength;
        float Snap(float t)
        {
            float step = _config.TimelineSnap;
            t = step > 0f ? MathF.Round(t / step) * step : t;
            return Math.Clamp(t, 0f, clip.Length);
        }
        uint grid = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.08f));
        uint text = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.6f));
        uint playhead = ImGui.GetColorU32(new Vector4(1f, 0.45f, 0.3f, 1f));

        // Ruler: click / drag to move the playhead; wheel to zoom, Shift+wheel to scroll.
        ImGui.SetCursorScreenPos(new Vector2(laneX, origin.Y));
        ImGui.InvisibleButton("###tl_ruler", new Vector2(laneWidth, ruler));
        bool rulerHovered = ImGui.IsItemHovered();
        if(ImGui.IsItemActive())
        {
            _timeline.Pause();
            _timeline.Seek(Snap(T(io.MousePos.X)));
        }
        draw.AddRectFilled(new Vector2(laneX, origin.Y), new Vector2(laneX + laneWidth, origin.Y + ruler), ImGui.GetColorU32(new Vector4(0.15f, 0.15f, 0.17f, 1f)));
        float tick = NiceStep(_viewLength / MathF.Max(1f, laneWidth / (70f * scale)));
        for(float t = MathF.Ceiling(_viewStart / tick) * tick; t <= _viewStart + _viewLength + 1e-4f; t += tick)
        {
            float x = X(t);
            draw.AddLine(new Vector2(x, origin.Y + ruler * 0.55f), new Vector2(x, origin.Y + ruler), text);
            draw.AddText(new Vector2(x + 2f, origin.Y + 2f), text, t.ToString(tick < 1f ? "0.00" : "0.#") + "s");
        }
        float endX = X(clip.Length);
        draw.AddLine(new Vector2(endX, origin.Y), new Vector2(endX, origin.Y + ruler), ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.5f)), 2f);

        // Rows: a header per character (fold it), then its tracks.
        float y = origin.Y + ruler + 2f * scale;
        TimelineTrack? removeTrack = null;
        bool lanesHovered = rulerHovered;
        bool openMenu = false;
        var groups = clip.Tracks.GroupBy(tr => tr.Character).ToList();
        foreach(var group in groups)
        {
            string who = group.Key.Length == 0 ? "The scene" : group.Key;
            ImGui.SetCursorScreenPos(new Vector2(origin.X, y));
            bool open = !_closed.Contains(group.Key);
            if(ImGui.ArrowButton($"###tl_fold_{group.Key}", open ? ImGuiDir.Down : ImGuiDir.Right))
            {
                if(open) _closed.Add(group.Key);
                else _closed.Remove(group.Key);
            }
            ImGui.SameLine();
            ImGui.TextColored(Accent, who);
            draw.AddLine(new Vector2(laneX, y + row), new Vector2(laneX + laneWidth, y + row), grid);
            // the group's keys, summed up in its header lane
            foreach(var track in group)
                foreach(var k in track.Keys)
                    Diamond(draw, new Vector2(X(k.Time), y + row * 0.5f), row * 0.22f, ImGui.GetColorU32(new Vector4(0.7f, 0.7f, 0.75f, 0.5f)));
            y += row;
            if(!open)
                continue;
            foreach(var track in group)
            {
                using var id = ImRaii.PushId(clip.Tracks.IndexOf(track));
                ImGui.SetCursorScreenPos(new Vector2(origin.X + 14f * scale, y + 1f));
                bool enabled = track.Enabled;
                if(ImGui.Checkbox("###tl_on", ref enabled))
                {
                    track.Enabled = enabled;
                    _config.Save();
                }
                Tooltip("Off: this track is kept but doesn't play.");
                ImGui.SameLine();
                if(ImGui.SmallButton("+###tl_key"))
                {
                    Checkpoint();
                    _timeline.KeyNow(track);
                    _config.Save();
                }
                Tooltip("Key its value as it is now, at the playhead.");
                ImGui.SameLine();
                if(ImGui.SmallButton("x###tl_remove") && ImGui.GetIO().KeyCtrl)
                    removeTrack = track;
                Tooltip("Hold Ctrl and click to remove this track.");
                ImGui.SameLine();
                ImGui.TextUnformatted(TimelineService.Label(track));
                if(ImGui.IsItemHovered())
                    ImGui.SetTooltip($"{TimelineService.Label(track)}\n{track.Kind}: {track.Target}  ({track.Keys.Count} keys)");

                // The lane: its keys.
                var laneMin = new Vector2(laneX, y);
                var laneMax = new Vector2(laneX + laneWidth, y + row);
                ImGui.SetCursorScreenPos(laneMin);
                ImGui.InvisibleButton("###tl_lane", laneMax - laneMin, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
                bool hovered = ImGui.IsItemHovered();
                lanesHovered |= hovered;
                draw.AddRectFilled(laneMin, laneMax, ImGui.GetColorU32(track.Enabled ? new Vector4(1f, 1f, 1f, 0.03f) : new Vector4(0f, 0f, 0f, 0.2f)));
                draw.AddLine(new Vector2(laneX, y + row), new Vector2(laneX + laneWidth, y + row), grid);
                TimelineKey? under = null;
                foreach(var k in track.Keys)
                {
                    var c = new Vector2(X(k.Time), y + row * 0.5f);
                    bool picked = _picked.Contains(k);
                    float r = row * 0.3f;
                    if(hovered && MathF.Abs(io.MousePos.X - c.X) <= r + 2f)
                        under = k;
                    uint color = ImGui.GetColorU32(picked ? new Vector4(1f, 0.8f, 0.3f, 1f)
                        : k.Ease == TimelineEase.Constant ? new Vector4(0.8f, 0.5f, 0.5f, 1f) : new Vector4(0.6f, 0.8f, 1f, 1f));
                    Diamond(draw, c, r, color);
                    if(track.Kind == "anim" && k.Value.Length > 0)
                    {
                        // which animation, written after its key (the lane shows the sequence)
                        string what = AnimationName((ushort)Math.Clamp((int)MathF.Round(k.Value[0]), 0, ushort.MaxValue));
                        int cut = what.IndexOf(" (", StringComparison.Ordinal);
                        if(cut > 0)
                            what = what[..cut];
                        draw.AddText(new Vector2(c.X + r + 3f, y + 3f), text, what);
                    }
                }
                if(hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    if(under != null)
                    {
                        if(io.KeyCtrl)
                        {
                            if(!_picked.Remove(under))
                                _picked.Add(under);
                        }
                        else if(!_picked.Contains(under))
                        {
                            _picked.Clear();
                            _picked.Add(under);
                        }
                        // start dragging the picked keys
                        Checkpoint();
                        _dragKey = under;
                        _dragFrom = under.Time;
                        _dragStartMouse = io.MousePos.X;
                        _dragStartTimes = _picked.ToDictionary(k => k, k => k.Time);
                    }
                    else
                    {
                        if(!io.KeyCtrl)
                            _picked.Clear();
                        _timeline.Pause();
                        _timeline.Seek(Snap(T(io.MousePos.X)));
                    }
                }
                if(hovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && under == null)
                {
                    // a key here, with the track's value as it is now
                    Checkpoint();
                    if(_timeline.TryRead(track, out var value))
                    {
                        var k = TimelineService.SetKey(track, Snap(T(io.MousePos.X)), value);
                        _picked.Clear();
                        _picked.Add(k);
                        _timeline.Refresh();
                        _config.Save();
                    }
                }
                if(hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                {
                    _menuKey = under;
                    _menuTrack = track;
                    if(under != null && !_picked.Contains(under))
                    {
                        _picked.Clear();
                        _picked.Add(under);
                    }
                    openMenu = true; // (opened below, outside this track's ID scope, where the menu is drawn)
                }
                y += row;
            }
        }
        if(groups.Count == 0)
        {
            ImGui.SetCursorScreenPos(new Vector2(origin.X, y));
            ImGui.TextDisabled("No tracks yet: Add track (or Add the whole pose) above.");
            y += row;
        }

        // Dragging the picked keys.
        if(_dragKey != null && _dragStartTimes != null)
        {
            if(ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                float delta = (io.MousePos.X - _dragStartMouse) / laneWidth * _viewLength;
                float moved = Snap(_dragFrom + delta) - _dragFrom;
                foreach(var (k, start) in _dragStartTimes)
                    k.Time = Math.Clamp(start + moved, 0f, clip.Length);
                foreach(var track in clip.Tracks)
                    track.Keys.Sort((a, b) => a.Time.CompareTo(b.Time));
                _timeline.Refresh();
            }
            else
            {
                if(MathF.Abs(_dragKey.Time - _dragFrom) < 1e-5f && _undo.Count > 0)
                    _undo.RemoveAt(_undo.Count - 1); // just a click: nothing to undo
                _dragKey = null;
                _dragStartTimes = null;
                _config.Save();
            }
        }

        // The playhead across everything.
        float px = X(_timeline.Time);
        if(px >= laneX && px <= laneX + laneWidth)
            draw.AddLine(new Vector2(px, origin.Y), new Vector2(px, y), playhead, 2f);

        // Zoom (wheel) and scroll (Shift+wheel) over the lanes; Delete removes the picked keys.
        if(lanesHovered && io.MouseWheel != 0f)
        {
            if(io.KeyShift)
                _viewStart = Math.Clamp(_viewStart - io.MouseWheel * _viewLength * 0.1f, 0f, MathF.Max(0f, clip.Length - _viewLength * 0.2f));
            else
            {
                float at = T(io.MousePos.X);
                float newLength = Math.Clamp(_viewLength * MathF.Pow(0.85f, io.MouseWheel), 0.2f, clip.Length * 2f);
                _viewStart = MathF.Max(0f, at - (at - _viewStart) * newLength / _viewLength);
                _viewLength = newLength;
            }
        }
        if(lanesHovered && ImGui.IsKeyPressed(ImGuiKey.Delete))
            DeletePicked(clip);

        // Right-click menu for a key / lane.
        if(openMenu)
            ImGui.OpenPopup("###tl_key_menu");
        if(ImGui.BeginPopup("###tl_key_menu"))
        {
            if(_menuKey != null)
            {
                ImGui.TextDisabled("Curve to the next key:");
                foreach(var (e, label, _) in Eases)
                    if(ImGui.MenuItem(label, "", _menuKey.Ease == e))
                    {
                        Checkpoint();
                        foreach(var k in _picked)
                            k.Ease = e;
                        _timeline.Refresh();
                        _config.Save();
                    }
                ImGui.Separator();
                if(ImGui.MenuItem("Set to now") && _menuTrack != null && _timeline.TryRead(_menuTrack, out var v))
                {
                    Checkpoint();
                    _menuKey.Value = v;
                    _timeline.Refresh();
                    _config.Save();
                }
                if(ImGui.MenuItem("Go to this key"))
                    _timeline.Seek(_menuKey.Time);
                if(ImGui.MenuItem("Delete"))
                    DeletePicked(clip);
            }
            else if(_menuTrack != null)
            {
                if(ImGui.MenuItem("Key here (value as it is now)"))
                {
                    Checkpoint();
                    _timeline.KeyNow(_menuTrack);
                    _config.Save();
                }
                if(ImGui.MenuItem("Pick all its keys"))
                {
                    _picked.Clear();
                    foreach(var k in _menuTrack.Keys)
                        _picked.Add(k);
                }
                if(ImGui.MenuItem("Remove this track"))
                    removeTrack = _menuTrack;
            }
            ImGui.EndPopup();
        }

        if(removeTrack != null)
        {
            Checkpoint();
            foreach(var k in removeTrack.Keys)
                _picked.Remove(k);
            clip.Tracks.Remove(removeTrack);
            _config.Save();
        }

        // (room for everything drawn, so the child scrolls)
        ImGui.SetCursorScreenPos(new Vector2(origin.X, y + 4f));
        ImGui.Dummy(new Vector2(1f, 1f));
    }

    private static void Diamond(ImDrawListPtr draw, Vector2 c, float r, uint color)
        => draw.AddQuadFilled(c + new Vector2(0, -r), c + new Vector2(r, 0), c + new Vector2(0, r), c + new Vector2(-r, 0), color);

    private static float NiceStep(float raw)
    {
        foreach(var s in new[] { 0.05f, 0.1f, 0.25f, 0.5f, 1f, 2f, 5f, 10f, 30f, 60f })
            if(s >= raw)
                return s;
        return 120f;
    }

    private static void Tooltip(string text)
    {
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip(text);
    }
}
