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
    private bool _favOnly;
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
            ImGui.SetTooltip("Play another race's version of the animation (experimental: the mod manager swaps the file for this character's "
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
        ImGui.SameLine();
        ImGui.Checkbox($"Favorites only ({Favorites.Count("animations")})###anim_favs", ref _favOnly);
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("Right-click an animation to add it to (or take it out of) your favourites.");

        var list = all.Where(e => (!_favOnly || Favorites.Has("animations", e.Key))
                                  && (_group.Length == 0 || e.Group == _group)
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
            // A grid: each animation's icon (the emote's own, where it has one) with its name next to it.
            float scale = ImGuiHelpers.GlobalScale;
            float iconSize = 36f * scale, cellWidth = 230f * scale, cellHeight = iconSize + 8f * scale;
            int columns = Math.Max(1, (int)(ImGui.GetContentRegionAvail().X / cellWidth));
            using var table = Dalamud.Interface.Utility.Raii.ImRaii.Table("###anim_grid", columns, ImGuiTableFlags.SizingStretchSame);
            if(!table.Success)
                return;
            var draw = ImGui.GetWindowDrawList();
            foreach(var e in list)
            {
                ImGui.TableNextColumn();
                var start = ImGui.GetCursorScreenPos();
                float width = ImGui.GetContentRegionAvail().X;
                bool picked = ImGui.Selectable($"###anim_{e.Id}", false, ImGuiSelectableFlags.AllowDoubleClick, new System.Numerics.Vector2(width, cellHeight));
                if(ImGui.IsItemHovered())
                    ImGui.SetTooltip((e.Name.Length > 0 ? e.Name + "\n" : "") + e.Key + "\nDouble-click to play.  Right-click: favourite.");
                if(ImGui.IsItemClicked(ImGuiMouseButton.Right))
                    Favorites.Toggle("animations", e.Key);
                if(picked && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                {
                    _status = _race < 0
                        ? Animations.Play(actor.Address, actor.ObjectIndex, e.Id)
                        : Animations.PlayAsRace(actor.Address, actor.ObjectIndex, e.Id, Animations.Races[_race].Body);
                    if(_speed != 1f)
                        Animations.SetSpeed(actor.Address, actor.ObjectIndex, _speed);
                }
                // The icon, or a plain tile for animations that aren't an emote.
                var iconMin = start + new System.Numerics.Vector2(4f * scale, 4f * scale);
                var iconMax = iconMin + new System.Numerics.Vector2(iconSize, iconSize);
                var wrap = e.Icon != 0 ? GameIcons.Get(e.Icon) : null;
                if(wrap != null)
                    draw.AddImage(wrap.Handle, iconMin, iconMax);
                else
                {
                    draw.AddRectFilled(iconMin, iconMax, ImGui.GetColorU32(new System.Numerics.Vector4(0.25f, 0.25f, 0.3f, 1f)), 4f * scale);
                    string letter = e.Group.Length > 0 ? e.Group[..1].ToUpperInvariant() : "?";
                    var ts = ImGui.CalcTextSize(letter);
                    draw.AddText(iconMin + (new System.Numerics.Vector2(iconSize, iconSize) - ts) * 0.5f, ImGui.GetColorU32(new System.Numerics.Vector4(0.7f, 0.7f, 0.75f, 1f)), letter);
                }
                // Its name and key beside it (cut off at the cell's edge).
                var textAt = new System.Numerics.Vector2(iconMax.X + 6f * scale, start.Y + 4f * scale);
                draw.PushClipRect(start, start + new System.Numerics.Vector2(width, cellHeight), true);
                draw.AddText(textAt, ImGui.GetColorU32(ImGuiCol.Text), e.Name.Length > 0 ? e.Name : e.Key);
                if(e.Name.Length > 0)
                    draw.AddText(textAt + new System.Numerics.Vector2(0, ImGui.GetTextLineHeight()), ImGui.GetColorU32(ImGuiCol.TextDisabled), e.Key);
                draw.PopClipRect();
                if(Favorites.Has("animations", e.Key))
                    Favorites.DrawStar(draw, new System.Numerics.Vector2(start.X + width - 16f * scale, start.Y + 3f * scale), 12f * scale);
            }
        }
    }
}
