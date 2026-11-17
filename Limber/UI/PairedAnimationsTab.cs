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
/// Paired Animations (Posing): two characters lined up with each other (the second placed and turned relative to the
/// first), an animation picked for each, and both started on the same frame.
/// </summary>
public sealed unsafe class PairedAnimationsTab
{
    private readonly Selection _selection;
    private readonly ActorTransforms _actors;
    private readonly RigService? _rigs;
    private readonly RigSettings? _config;
    private string _status = "";
    private ushort _firstRace, _secondRace; // the race whose version each plays (body id), 0 = its own
    private string _saveName = "";
    private bool _saveIk = true, _saveFk = true, _saveMove = true, _loadPoses = true;
    // Loaded and played: the first's Move character goes on first, the line-up and the animations a few frames later
    // (once the first is drawn where it's moved to).
    private int _pendingPlay;
    private PairedSide? _pendingSecondMove;

    private nint _first, _second;
    private ushort _firstAnim, _secondAnim;
    private string _firstSearch = "", _secondSearch = "";
    // Where the second goes, from the first: in front (+) / behind, to its right (+) / left, up, metres; and which way
    // it faces, degrees from the first's facing (180 = facing the first).
    private float _forward = 0.8f, _side, _up, _facing = 180f;
    private bool _startFrozen;
    private float _speed = 1f;

    private static readonly (string Name, float Forward, float Side, float Up, float Facing)[] Layouts =
    [
        ("Face to face", 0.8f, 0f, 0f, 180f),
        ("Close, face to face", 0.35f, 0f, 0f, 180f),
        ("Behind, same way", -0.5f, 0f, 0f, 0f),
        ("Side by side", 0f, 0.7f, 0f, 0f),
        ("In front, back to them", 0.5f, 0f, 0f, 0f),
        ("Same spot", 0f, 0f, 0f, 0f),
    ];

    public PairedAnimationsTab(Selection selection, ActorTransforms actors, RigService? rigs = null, RigSettings? config = null)
    {
        _selection = selection;
        _actors = actors;
        _rigs = rigs;
        _config = config;
    }

    private static void Tip(string text)
    {
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip(text);
    }

    public void Draw()
    {
        var all = _selection.ListCharacters();
        var names = ActorNames.Of(all);
        string LabelOf(ActorRef a) => (names.TryGetValue(a.Address, out var n) ? n : string.IsNullOrEmpty(a.Name) ? "(no name)" : a.Name) + ActorNames.Distance(a);
        if(_first == 0 && _selection.Actor.IsSet)
            _first = _selection.Actor.Address;
        var first = all.FirstOrDefault(a => a.Address == _first);
        var second = all.FirstOrDefault(a => a.Address == _second);

        ImGui.TextWrapped("Two characters lined up with each other, each with its own animation, started together on the same frame.");
        ImGui.Spacing();

        // ---- Who
        Picker("First###pair_first", all, ref _first, LabelOf);
        Tip("Stays where it is: the second is placed round it.");
        Picker("Second###pair_second", all, ref _second, LabelOf);
        Tip("Moved and turned to line up with the first (Limber's Move character offset: only on your screen).");
        if(first.IsSet && second.IsSet && first.Address == second.Address)
            ImGui.TextColored(new Vector4(1f, 0.6f, 0.4f, 1f), "Pick two different characters.");

        // ---- Lining up
        ImGui.Spacing();
        ImGui.TextUnformatted("Line up");
        ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(200f * ImGuiHelpers.GlobalScale));
        using(var combo = ImRaii.Combo("Layout###pair_layout", "Choose a layout..."))
            if(combo.Success)
                foreach(var l in Layouts)
                    if(ImGui.Selectable(l.Name))
                        (_forward, _side, _up, _facing) = (l.Forward, l.Side, l.Up, l.Facing);
        ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(200f * ImGuiHelpers.GlobalScale));
        ImGui.DragFloat("In front (+) / behind###pair_fwd", ref _forward, 0.005f, -5f, 5f, "%.2f m");
        ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(200f * ImGuiHelpers.GlobalScale));
        ImGui.DragFloat("Right (+) / left###pair_side", ref _side, 0.005f, -5f, 5f, "%.2f m");
        ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(200f * ImGuiHelpers.GlobalScale));
        ImGui.DragFloat("Up (+) / down###pair_up", ref _up, 0.005f, -3f, 3f, "%.2f m");
        ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(200f * ImGuiHelpers.GlobalScale));
        ImGui.DragFloat("Facing###pair_facing", ref _facing, 0.5f, -180f, 360f, "%.0f°");
        Tip("Degrees from the way the first faces: 180 faces the first, 0 faces the same way.");
        bool ready = first.IsSet && second.IsSet && first.Address != second.Address;
        using(ImRaii.Disabled(!ready))
        {
            if(ImGui.Button("Line them up###pair_lineup"))
                _status = LineUp(first, second);
            ImGui.SameLine();
            if(ImGui.Button("Undo line-up###pair_unline"))
            {
                if(_actors.Get(second.Address) is { } o)
                    lock(_actors.Sync)
                    {
                        o.Move = Vector3.Zero;
                        o.Turn = Vector3.Zero;
                    }
                _status = "The second is back where the game has it.";
            }
        }

        // ---- Animations
        ImGui.Spacing();
        ImGui.TextUnformatted("Animations");
        AnimationPicker("First's animation", "pair_a1", ref _firstAnim, ref _firstSearch);
        ImGui.SameLine();
        RacePicker("pair_r1", ref _firstRace);
        AnimationPicker("Second's animation", "pair_a2", ref _secondAnim, ref _secondSearch);
        ImGui.SameLine();
        RacePicker("pair_r2", ref _secondRace);
        ImGui.Checkbox("Start frozen###pair_frozen", ref _startFrozen);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(140f * ImGuiHelpers.GlobalScale));
        ImGui.SliderFloat("Speed###pair_speed", ref _speed, 0f, 2f, "%.2f");

        using(ImRaii.Disabled(!ready || (_firstAnim == 0 && _secondAnim == 0)))
        {
            if(ImGui.Button("Line up and play both###pair_go", new Vector2(200 * ImGuiHelpers.GlobalScale, 0)))
            {
                string line = LineUp(first, second);
                _status = line + " " + PlayBoth(first, second);
            }
            ImGui.SameLine();
            if(ImGui.Button("Play both###pair_play"))
                _status = PlayBoth(first, second);
            Tip("Both started on the same frame (from their beginning), without moving anyone.");
            ImGui.SameLine();
            if(ImGui.Button("Stop both###pair_stop"))
            {
                Animations.Stop(first.Address, first.ObjectIndex);
                Animations.Stop(second.Address, second.ObjectIndex);
                if(Animations.RaceOf(first.Address) != 0)
                    Animations.ClearRace(first.Address, first.ObjectIndex);
                if(Animations.RaceOf(second.Address) != 0)
                    Animations.ClearRace(second.Address, second.ObjectIndex);
                _status = "Both back to their own animations.";
            }
        }
        if(_status.Length > 0)
            ImGui.TextDisabled(_status);

        if(_pendingPlay > 0 && --_pendingPlay == 0)
        {
            if(ready)
            {
                _status += " " + LineUp(first, second);
                if(_pendingSecondMove != null)
                    PutSecondMove(second, _pendingSecondMove);
                _status += " " + PlayBoth(first, second);
            }
            _pendingSecondMove = null;
        }

        DrawSaved(first, second, ready);
    }

    private static void RacePicker(string id, ref ushort body)
    {
        ushort now = body;
        string label = now == 0 ? "Its own race" : Animations.Races.FirstOrDefault(r => r.Body == now).Name ?? "Its own race";
        ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(150f * ImGuiHelpers.GlobalScale));
        using(var combo = ImRaii.Combo($"###{id}", label))
        {
            if(combo.Success)
            {
                if(ImGui.Selectable($"Its own race###{id}_own", body == 0))
                    body = 0;
                foreach(var (b, name) in Animations.Races)
                    if(ImGui.Selectable($"{name}###{id}_{b}", b == body))
                        body = b;
            }
        }
        Tip("Which race's version of the animation it plays (the game's, or your mods made for that race),\n"
            + "keeping its own body - like Anamnesis's \"Data path\". \"Stop both\" puts it back to its own race.");
    }

    // ---- Saved paired animations: everything but who the two are (line-up, animations, races, speed, and each
    // one's FK pose, IK goals and attached bones).
    private void DrawSaved(ActorRef first, ActorRef second, bool ready)
    {
        if(_config == null)
            return;
        var saved = _config.PairedAnimations ??= new();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted("Saved paired animations");
        ImGui.TextDisabled("Everything above but who the two are, with each one's posing if you like.");
        ImGui.Checkbox("With IK and attached bones###pair_save_ik", ref _saveIk);
        Tip("Each one's IK Posing goals (held and pinned bones) and bones attached to the other (or to themselves).");
        ImGui.SameLine();
        ImGui.Checkbox("With FK poses###pair_save_fk", ref _saveFk);
        Tip("Each one's FK Posing pose.");
        ImGui.SameLine();
        ImGui.Checkbox("With Move character###pair_save_move", ref _saveMove);
        Tip("Each one's Move character (move, turn, size), always from where they started, never as places in the world:\n"
            + "the first's own; the second's turn and size on top of the line-up, and where it actually stands next to\n"
            + "the first (if it was moved off the line-up, that's what's saved as the line-up).");
        ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(200f * ImGuiHelpers.GlobalScale));
        ImGui.InputTextWithHint("###pair_save_name", "Name", ref _saveName, 64);
        ImGui.SameLine();
        using(ImRaii.Disabled(_saveName.Trim().Length == 0))
        {
            if(ImGui.Button("Save###pair_save"))
            {
                var pair = Capture(_saveName.Trim(), first, second);
                saved.RemoveAll(p => p.Name == pair.Name);
                saved.Add(pair);
                _config.Save();
                _status = $"Saved \"{pair.Name}\".";
                _saveName = "";
            }
        }
        Tip("Saves over one with the same name.");
        if(saved.Count == 0)
            return;
        ImGui.Checkbox("Put their posing on when loading###pair_load_poses", ref _loadPoses);
        Tip("Loading also puts the saved FK poses, IK goals, attached bones and Move character on the two chosen\n"
            + "characters (replacing theirs). Off: only the line-up, animations and races are loaded.");
        PairedAnimation? remove = null;
        foreach(var pair in saved)
        {
            using var pid = ImRaii.PushId(pair.Name);
            ImGui.Bullet();
            ImGui.SameLine();
            ImGui.TextUnformatted(pair.Name);
            ImGui.SameLine();
            if(ImGui.SmallButton("Load###pair_load"))
                _status = Load(pair, first, second, false);
            Tip("The settings come back; the two chosen characters get the saved posing (if that's on).");
            ImGui.SameLine();
            using(ImRaii.Disabled(!ready))
            {
                if(ImGui.SmallButton("Load and play###pair_load_play"))
                    _status = Load(pair, first, second, true);
            }
            Tip("Loads it, lines the two up and starts both animations.");
            ImGui.SameLine();
            if(ImGui.SmallButton("x###pair_del") && ImGui.GetIO().KeyCtrl)
                remove = pair;
            Tip("Hold Ctrl and click to delete.");
        }
        if(remove != null)
        {
            saved.Remove(remove);
            _config.Save();
        }
    }

    private PairedAnimation Capture(string name, ActorRef first, ActorRef second)
    {
        var pair = new PairedAnimation
        {
            Name = name, Forward = _forward, Side = _side, Up = _up, Facing = _facing, StartFrozen = _startFrozen, Speed = _speed,
            First = new PairedSide { Animation = _firstAnim, RaceBody = _firstRace },
            Second = new PairedSide { Animation = _secondAnim, RaceBody = _secondRace },
        };
        void Posing(ActorRef who, PairedSide side)
        {
            if(!who.IsSet)
                return;
            if(_saveFk && _config?.FkPoses != null && !string.IsNullOrEmpty(who.Name)
               && _config.FkPoses.TryGetValue(who.Name, out var fk) && fk != null)
                side.Fk = fk.Copy();
            if(_saveIk && _rigs != null)
                lock(_rigs.Sync)
                    if(_rigs.GetRig(who.Address) is { } rig)
                        side.Goals = rig.Goals.Select(g => SavedGoal.From(g, first.Address, second.Address)).OfType<SavedGoal>().ToList();
        }
        Posing(first, pair.First);
        Posing(second, pair.Second);
        if(_saveMove)
        {
            // The first's Move character as it is (from its starting place).
            if(first.IsSet && _actors.Get(first.Address) is { } a)
                lock(_actors.Sync)
                    pair.First.Move = new PairedMove { Move = a.Move, Turn = a.Turn, Scale = a.Scale, Facing = a.Facing, KeepWhileMoving = a.KeepWhileMoving, Priority = a.Priority };
            // The second: where it actually stands from the first (kept as the line-up), and its own tilt and size.
            if(first.IsSet && second.IsSet && first.Address != second.Address && _actors.Get(second.Address) is { } b)
            {
                if(_actors.TryGetPlaced(first.Address, first.ObjectIndex, out var p1, out var r1)
                   && _actors.TryGetPlaced(second.Address, second.ObjectIndex, out var p2, out var r2))
                {
                    float yaw1 = ActorTransforms.DegreesOf(r1).X * MathF.PI / 180f;
                    float yaw2 = ActorTransforms.DegreesOf(r2).X;
                    var forward = new Vector3(MathF.Sin(yaw1), 0f, MathF.Cos(yaw1));
                    var right = new Vector3(-forward.Z, 0f, forward.X);
                    var d = p2 - p1;
                    pair.Forward = Vector3.Dot(d, forward);
                    pair.Side = Vector3.Dot(d, right);
                    pair.Up = d.Y;
                    float facing = yaw2 - yaw1 * 180f / MathF.PI;
                    pair.Facing = ((facing + 180f) % 360f + 360f) % 360f - 180f;
                }
                lock(_actors.Sync)
                    pair.Second.Move = new PairedMove { Turn = new Vector3(0f, b.Turn.Y, b.Turn.Z), Scale = b.Scale, KeepWhileMoving = b.KeepWhileMoving, Priority = b.Priority };
            }
        }
        return pair;
    }

    private string Load(PairedAnimation pair, ActorRef first, ActorRef second, bool play)
    {
        (_forward, _side, _up, _facing, _startFrozen, _speed) = (pair.Forward, pair.Side, pair.Up, pair.Facing, pair.StartFrozen, pair.Speed);
        (_firstAnim, _firstRace) = (pair.First.Animation, pair.First.RaceBody);
        (_secondAnim, _secondRace) = (pair.Second.Animation, pair.Second.RaceBody);
        string said = $"Loaded \"{pair.Name}\".";
        if(_loadPoses)
        {
            _rigs?.Checkpoint(); // (undo puts their posing back)
            int put = PutPosing(first, pair.First, first, second) + PutPosing(second, pair.Second, first, second);
            if(put > 0)
                said += " Posing put on.";
        }
        if(_loadPoses && first.IsSet && pair.First.Move is { } m1)
        {
            var o = _actors.GetOrAdd(first.Address, first.ObjectIndex);
            lock(_actors.Sync)
            {
                o.Facing = m1.Facing;
                o.Move = m1.Move;
                o.Turn = m1.Turn;
                o.Scale = m1.Scale;
                o.KeepWhileMoving = m1.KeepWhileMoving;
                o.Priority = m1.Priority;
            }
        }
        if(play && first.IsSet && second.IsSet && first.Address != second.Address)
        {
            // (a few frames on: once the first stands where it's moved to)
            _pendingPlay = 3;
            _pendingSecondMove = _loadPoses ? pair.Second : null;
        }
        else if(_loadPoses && second.IsSet && pair.Second.Move != null)
            PutSecondMove(second, pair.Second);
        return said;
    }

    // The second's own tilt and size (on top of the line-up's place and facing).
    private void PutSecondMove(ActorRef second, PairedSide side)
    {
        if(side.Move is not { } m || !second.IsSet)
            return;
        var o = _actors.GetOrAdd(second.Address, second.ObjectIndex);
        lock(_actors.Sync)
        {
            o.Turn = new Vector3(o.Turn.X, m.Turn.Y, m.Turn.Z);
            o.Scale = m.Scale;
            o.KeepWhileMoving = m.KeepWhileMoving;
            o.Priority = m.Priority;
        }
    }

    private int PutPosing(ActorRef who, PairedSide side, ActorRef first, ActorRef second)
    {
        if(!who.IsSet)
            return 0;
        int put = 0;
        if(side.Fk != null && _config != null && !string.IsNullOrEmpty(who.Name))
        {
            (_config.FkPoses ??= new())[who.Name] = side.Fk.Copy();
            _config.Save();
            put++;
        }
        if(side.Goals != null && _rigs != null)
        {
            var others = _selection.ListCharacters();
            (nint, ushort, string)? Parent(SavedGoal g)
            {
                if(g.ParentWho == 0 && first.IsSet)
                    return (first.Address, first.ObjectIndex, first.Name);
                if(g.ParentWho == 1 && second.IsSet)
                    return (second.Address, second.ObjectIndex, second.Name);
                var named = others.FirstOrDefault(c => !string.IsNullOrEmpty(c.Name) && c.Name == g.ParentName);
                return named.IsSet ? (named.Address, named.ObjectIndex, named.Name) : null;
            }
            lock(_rigs.Sync)
            {
                var rig = _rigs.GetOrCreateRig(who.Address, who.ObjectIndex, who.Name ?? "");
                rig.Goals.Clear();
                foreach(var g in side.Goals)
                    rig.Goals.Add(g.ToGoal(Parent));
            }
            put++;
        }
        return put;
    }

    private static void Picker(string label, List<ActorRef> all, ref nint chosen, Func<ActorRef, string> labelOf)
    {
        nint pick = chosen; // (a ref parameter can't be used inside the lambda)
        var now = all.FirstOrDefault(a => a.Address == pick);
        ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(260f * ImGuiHelpers.GlobalScale));
        using var combo = ImRaii.Combo(label, now.IsSet ? labelOf(now) : "Choose a character");
        if(!combo.Success)
            return;
        foreach(var a in all)
            if(ImGui.Selectable($"{labelOf(a)}###{label}_{a.Address:X}", a.Address == chosen))
                chosen = a.Address;
    }

    private static void AnimationPicker(string label, string id, ref ushort chosen, ref string search)
    {
        string now = chosen == 0 ? "None (keep what it plays)" : Name(chosen);
        ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(260f * ImGuiHelpers.GlobalScale));
        using var combo = ImRaii.Combo($"{label}###{id}", now);
        if(!combo.Success)
            return;
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint($"###{id}_search", "Search (name or key: dance, pose01, sit...)", ref search, 64);
        if(ImGui.Selectable($"None (keep what it plays)###{id}_none", chosen == 0))
            chosen = 0;
        int shown = 0;
        string s = search;
        foreach(var e in Animations.All)
        {
            if(s.Length > 0 && !e.Shown.Contains(s, StringComparison.OrdinalIgnoreCase) && !e.Key.Contains(s, StringComparison.OrdinalIgnoreCase))
                continue;
            if(++shown > 150)
            {
                ImGui.TextDisabled("(more: search to narrow it down)");
                break;
            }
            if(ImGui.Selectable($"{e.Shown}  ({e.Key})###{id}_{e.Id}", e.Id == chosen))
                chosen = e.Id;
        }
    }

    private static string Name(ushort id)
    {
        foreach(var e in Animations.All)
            if(e.Id == id)
                return e.Shown;
        return "#" + id;
    }

    // The first as it's drawn now (its place and facing, with any Limber move on it).
    private static bool Drawn(nint address, out Vector3 position, out float yaw)
    {
        position = default;
        yaw = 0f;
        var obj = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)address;
        if(obj == null)
            return false;
        if(obj->DrawObject != null)
        {
            position = obj->DrawObject->Object.Position;
            var q = (Quaternion)obj->DrawObject->Object.Rotation;
            yaw = MathF.Atan2(2f * (q.W * q.Y + q.X * q.Z), 1f - 2f * (q.Y * q.Y + q.X * q.X));
        }
        else
        {
            position = obj->Position;
            yaw = obj->Rotation;
        }
        return MathUtil.IsFinite(position);
    }

    private string LineUp(ActorRef first, ActorRef second)
    {
        // The first where it's drawn: its starting place with its own Move character on.
        Vector3 at;
        float yaw;
        if(_actors.TryGetPlaced(first.Address, first.ObjectIndex, out var placed, out var placedRotation))
        {
            at = placed;
            yaw = ActorTransforms.DegreesOf(placedRotation).X * MathF.PI / 180f;
        }
        else if(!Drawn(first.Address, out at, out yaw))
            return "Can't find the first character.";
        if(second.Address == 0)
            return "Can't find the second character.";
        // The first's own directions: forward is the way it faces; right is to its right.
        var forward = new Vector3(MathF.Sin(yaw), 0f, MathF.Cos(yaw));
        var right = new Vector3(-forward.Z, 0f, forward.X);
        var want = at + forward * _forward + right * _side + Vector3.UnitY * _up;
        float wantYaw = yaw + _facing * MathF.PI / 180f;
        // (an offset from where the game has the second, in the world's axes)
        var o = _actors.GetOrAdd(second.Address, second.ObjectIndex);
        lock(_actors.Sync)
            o.Facing = false;
        if(!_actors.SetPlaced(second.Address, second.ObjectIndex, want, Quaternion.CreateFromYawPitchRoll(wantYaw, 0f, 0f)))
            return "Can't find the second character.";
        return "Lined up.";
    }

    private string PlayBoth(ActorRef first, ActorRef second)
    {
        // (the same frame: both queued now, so the game starts them together)
        string said = "";
        if(_firstAnim != 0)
            said += Animations.PlayFrom(first.Address, first.ObjectIndex, _firstAnim, _startFrozen, _speed, _firstRace);
        if(_secondAnim != 0)
        {
            string two = Animations.PlayFrom(second.Address, second.ObjectIndex, _secondAnim, _startFrozen, _speed, _secondRace);
            if(two.Length > 0 && two != said)
                said += (said.Length > 0 ? " " : "") + two;
        }
        return said.Length > 0 ? said : "Both started.";
    }
}
