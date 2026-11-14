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
    private string _status = "";

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

    public PairedAnimationsTab(Selection selection, ActorTransforms actors)
    {
        _selection = selection;
        _actors = actors;
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
        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        using(var combo = ImRaii.Combo("Layout###pair_layout", "Choose a layout..."))
            if(combo.Success)
                foreach(var l in Layouts)
                    if(ImGui.Selectable(l.Name))
                        (_forward, _side, _up, _facing) = (l.Forward, l.Side, l.Up, l.Facing);
        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        ImGui.DragFloat("In front (+) / behind###pair_fwd", ref _forward, 0.005f, -5f, 5f, "%.2f m");
        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        ImGui.DragFloat("Right (+) / left###pair_side", ref _side, 0.005f, -5f, 5f, "%.2f m");
        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        ImGui.DragFloat("Up (+) / down###pair_up", ref _up, 0.005f, -3f, 3f, "%.2f m");
        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
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
        AnimationPicker("Second's animation", "pair_a2", ref _secondAnim, ref _secondSearch);
        ImGui.Checkbox("Start frozen###pair_frozen", ref _startFrozen);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(140 * ImGuiHelpers.GlobalScale);
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
                _status = "Both back to their own animations.";
            }
        }
        if(_status.Length > 0)
            ImGui.TextDisabled(_status);
    }

    private static void Picker(string label, List<ActorRef> all, ref nint chosen, Func<ActorRef, string> labelOf)
    {
        var now = all.FirstOrDefault(a => a.Address == chosen);
        ImGui.SetNextItemWidth(260 * ImGuiHelpers.GlobalScale);
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
        ImGui.SetNextItemWidth(260 * ImGuiHelpers.GlobalScale);
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
        if(!Drawn(first.Address, out var at, out var yaw))
            return "Can't find the first character.";
        var obj = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)second.Address;
        if(obj == null)
            return "Can't find the second character.";
        // The first's own directions: forward is the way it faces; right is to its right.
        var forward = new Vector3(MathF.Sin(yaw), 0f, MathF.Cos(yaw));
        var right = new Vector3(-forward.Z, 0f, forward.X);
        var want = at + forward * _forward + right * _side + Vector3.UnitY * _up;
        float wantYaw = yaw + _facing * MathF.PI / 180f;
        // (the offset goes on top of where the game has the second)
        Vector3 basePos = obj->Position;
        float baseYaw = obj->Rotation;
        var o = _actors.GetOrAdd(second.Address, second.ObjectIndex);
        lock(_actors.Sync)
        {
            o.Facing = false;
            o.Move = want - basePos;
            float turn = (wantYaw - baseYaw) * 180f / MathF.PI;
            turn = ((turn + 180f) % 360f + 360f) % 360f - 180f;
            o.Turn = new Vector3(turn, 0f, 0f);
        }
        return "Lined up.";
    }

    private string PlayBoth(ActorRef first, ActorRef second)
    {
        // (the same frame: both queued now, so the game starts them together)
        if(_firstAnim != 0)
            Animations.PlayFrom(first.Address, first.ObjectIndex, _firstAnim, _startFrozen, _speed);
        if(_secondAnim != 0)
            Animations.PlayFrom(second.Address, second.ObjectIndex, _secondAnim, _startFrozen, _speed);
        return "Both started.";
    }
}
