using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Limber.Rig;

namespace Limber.UI;

/// <summary>The Animations page: play any of the game's animations on the chosen character, optionally another race's version.</summary>
public sealed class AnimationsTab
{
    private readonly Selection _selection;
    private string _search = "";
    private string _group = "emote";
    private int _race = -1; // index into Animations.Races; -1 = the character's own
    private float _speed = 1f;
    private string _status = "";

    public AnimationsTab(Selection selection) => _selection = selection;

    public void Draw()
    {
        var actor = _selection.Actor;
        if(!actor.IsSet)
        {
            ImGui.TextDisabled("Choose a character at the top of the window.");
            return;
        }
        var all = Animations.All;
        if(all.Count == 0)
        {
            ImGui.TextDisabled("Couldn't read the game's animation list.");
            return;
        }

        // What's playing now.
        if(Animations.Current(actor.Address, actor.ObjectIndex) is { } now)
        {
            ImGui.TextDisabled($"Playing: {(now.Base.Key.Length > 0 ? now.Base.Key : "#" + now.Base.Id)}"
                + (now.Override != 0 ? $"  (held by Limber: {Animations.KeyOf(now.Override)})" : ""));
        }
        if(ImGui.Button("Stop (back to its own)###anim_stop"))
        {
            Animations.ClearRace();
            _status = Animations.Stop(actor.Address, actor.ObjectIndex);
            _speed = 1f;
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
        if(ImGui.SliderFloat("Speed###anim_speed", ref _speed, 0f, 2f, "%.2f"))
            Animations.SetSpeed(actor.Address, actor.ObjectIndex, _speed);
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("How fast it plays (0 = paused on this frame).");

        // Which race's version.
        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        string raceLabel = _race < 0 ? "Its own race" : Animations.Races[_race].Name;
        if(ImGui.BeginCombo("Race's version###anim_race", raceLabel))
        {
            if(ImGui.Selectable("Its own race###anim_race_own", _race < 0))
                _race = -1;
            for(int i = 0; i < Animations.Races.Length; i++)
                if(ImGui.Selectable($"{Animations.Races[i].Name}###anim_race_{i}", i == _race))
                    _race = i;
            ImGui.EndCombo();
        }
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("Play another race's version of the animation (experimental: Penumbra swaps the file for this character's "
                + "collection). If the game already has the animation loaded, play another one first, then this one again.");

        // Filter.
        var groups = all.Select(e => e.Group).Distinct().OrderBy(g => g).ToList();
        ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
        if(ImGui.BeginCombo("Kind###anim_group", _group.Length == 0 ? "(all)" : _group))
        {
            if(ImGui.Selectable("(all)###anim_group_all", _group.Length == 0))
                _group = "";
            foreach(var g in groups)
                if(ImGui.Selectable($"{g}###anim_group_{g}", g == _group))
                    _group = g;
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("###anim_search", "Search (name or key)", ref _search, 100);

        var list = all.Where(e => (_group.Length == 0 || e.Group == _group)
                                  && (_search.Length == 0 || e.Key.Contains(_search, StringComparison.OrdinalIgnoreCase)
                                      || e.Name.Contains(_search, StringComparison.OrdinalIgnoreCase)))
            .Take(400).ToList();
        if(_status.Length > 0)
            ImGui.TextDisabled(_status);
        ImGui.TextDisabled($"{list.Count}{(list.Count == 400 ? "+" : "")} found. Double-click to play.");
        using(var child = Dalamud.Interface.Utility.Raii.ImRaii.Child("###anim_list", new System.Numerics.Vector2(-1, -1), true))
        {
            if(!child.Success)
                return;
            foreach(var e in list)
            {
                string label = e.Name.Length > 0 ? $"{e.Name}   ({e.Key})" : e.Key;
                if(ImGui.Selectable($"{label}###anim_{e.Id}", false, ImGuiSelectableFlags.AllowDoubleClick) && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                {
                    _status = _race < 0
                        ? Animations.Play(actor.Address, actor.ObjectIndex, e.Id)
                        : Animations.PlayAsRace(actor.Address, actor.ObjectIndex, e.Id, Animations.Races[_race].Body);
                    if(_speed != 1f)
                        Animations.SetSpeed(actor.Address, actor.ObjectIndex, _speed);
                }
            }
        }
    }
}
