using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Limber.Rig;

namespace Limber.UI;

/// <summary>The Animations page: play any of the game's animations on the chosen character, optionally another race's version.</summary>
public sealed class AnimationsTab
{
    private readonly Selection _selection;
    private string _search = "";
    private bool _favOnly;
    private string _group = "";
    private string _kind = "";
    private int _race = -1; // index into Animations.Races; -1 = the character's own
    private float _speed = 1f;
    private bool _startFrozen;
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

        // Start frozen; play again from the start, pause, resume: this character or every one Limber played on.
        ImGui.Checkbox("Start frozen###anim_start_frozen", ref _startFrozen);
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("Animations picked below (and Play / Play all) start held on their first frame. Resume to set them going.");
        ImGui.SameLine();
        ushort mine = Animations.PlayedId(actor.Address);
        using(ImRaii.Disabled(mine == 0))
        {
            if(ImGui.Button("Play###anim_play"))
                _status = Animations.Restart(actor.Address, actor.ObjectIndex, mine, _startFrozen, _speed);
            if(ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(mine == 0 ? "Pick an animation below first." : $"{Animations.KeyOf(mine)} from the start: back to standing, then played fresh.");
        }
        ImGui.SameLine();
        if(ImGui.Button("Play all###anim_play_all"))
        {
            int n = 0;
            foreach(var (address, index) in Animations.PlayedOn().ToList())
            {
                Animations.Restart(address, index, Animations.PlayedId(address), _startFrozen, _speed);
                n++;
            }
            _status = n == 0 ? "Nothing played on anyone yet." : $"{n} character(s) playing from the start, together.";
        }
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("Every character Limber played an animation on: all from the start at once (in sync).");
        ImGui.SameLine();
        if(ImGui.Button("Pause###anim_pause"))
            Animations.Pause(actor.Address, actor.ObjectIndex);
        ImGui.SameLine();
        if(ImGui.Button("Pause all###anim_pause_all"))
        {
            Animations.Pause(actor.Address, actor.ObjectIndex);
            foreach(var (address, index) in Animations.PlayedOn().ToList())
                Animations.Pause(address, index);
        }
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("This character and every one Limber played an animation on.");
        ImGui.SameLine();
        if(ImGui.Button("Resume###anim_resume"))
            Animations.Resume(actor.Address, actor.ObjectIndex, _speed);
        ImGui.SameLine();
        if(ImGui.Button("Resume all###anim_resume_all"))
        {
            Animations.Resume(actor.Address, actor.ObjectIndex, _speed);
            foreach(var (address, index) in Animations.PlayedOn().Concat(Animations.PausedOn()).ToList())
                Animations.Resume(address, index, _speed);
        }
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("Every paused or frozen one carries on from where it is.");
        if(Animations.IsPaused(actor.Address))
        {
            ImGui.SameLine();
            ImGui.TextDisabled("(paused)");
        }

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

        // Filter: by what kind of animation it is (in plain words), the key's own group, and a search.
        var kinds = all.Select(e => e.Kind).Distinct().OrderBy(g => g == "Other" ? "~" : g).ToList();
        ImGui.SetNextItemWidth(170 * ImGuiHelpers.GlobalScale);
        if(ImGui.BeginCombo("Kind###anim_kind", _kind.Length == 0 ? "(all)" : _kind))
        {
            if(ImGui.Selectable("(all)###anim_kind_all", _kind.Length == 0))
                _kind = "";
            foreach(var g in kinds)
                if(ImGui.Selectable($"{g}  ({all.Count(e => e.Kind == g)})###anim_kind_{g}", g == _kind))
                    _kind = g;
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        var groups = all.Select(e => e.Group).Distinct().OrderBy(g => g).ToList();
        ImGui.SetNextItemWidth(130 * ImGuiHelpers.GlobalScale);
        if(ImGui.BeginCombo("Key group###anim_group", _group.Length == 0 ? "(all)" : _group))
        {
            if(ImGui.Selectable("(all)###anim_group_all", _group.Length == 0))
                _group = "";
            foreach(var g in groups)
                if(ImGui.Selectable($"{g}###anim_group_{g}", g == _group))
                    _group = g;
            ImGui.EndCombo();
        }
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("The first part of the game's own key for it (emote, normal, battle...).");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("###anim_search", "Search: name, words, key or number", ref _search, 100);
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("Matches the emote's name, the plain-words name (\"ground sitting\", \"walking\"), the game's key\n"
                + "(j_pose01_loop) or its number (13, as posing tools show it). Several words: all must match.");
        ImGui.SameLine();
        ImGui.Checkbox($"Favorites only ({Favorites.Count("animations")})###anim_favs", ref _favOnly);
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("Right-click an animation to add it to (or take it out of) your favourites.");

        var words = _search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool Matches(Animations.Entry e)
        {
            foreach(var w in words)
            {
                if(ushort.TryParse(w.TrimStart('#'), out var id) && e.Id == id)
                    continue;
                if(e.Key.Contains(w, StringComparison.OrdinalIgnoreCase) || e.Name.Contains(w, StringComparison.OrdinalIgnoreCase)
                   || e.Described.Contains(w, StringComparison.OrdinalIgnoreCase) || e.Kind.Contains(w, StringComparison.OrdinalIgnoreCase))
                    continue;
                return false;
            }
            return true;
        }
        // (a number typed: that animation first)
        var list = all.Where(e => (!_favOnly || Favorites.Has("animations", e.Key))
                                  && (_kind.Length == 0 || e.Kind == _kind)
                                  && (_group.Length == 0 || e.Group == _group)
                                  && Matches(e))
            .OrderByDescending(e => words.Length == 1 && ushort.TryParse(words[0].TrimStart('#'), out var n) && e.Id == n)
            .ThenBy(e => e.Name.Length > 0 ? 0 : e.Described.Length > 0 ? 1 : 2)
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
                    ImGui.SetTooltip((e.Name.Length > 0 ? e.Name + "\n" : "") + (e.Described.Length > 0 ? e.Described + "\n" : "")
                        + $"{e.Key}  (#{e.Id}, {e.Kind})\nDouble-click to play.  Right-click: favourite.");
                if(ImGui.IsItemClicked(ImGuiMouseButton.Right))
                    Favorites.Toggle("animations", e.Key);
                if(picked && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                {
                    _status = _race < 0
                        ? Animations.Play(actor.Address, actor.ObjectIndex, e.Id)
                        : Animations.PlayAsRace(actor.Address, actor.ObjectIndex, e.Id, Animations.Races[_race].Body);
                    if(_startFrozen)
                    {
                        Animations.Seek(actor.Address, actor.ObjectIndex, 0f);
                        Animations.Pause(actor.Address, actor.ObjectIndex);
                    }
                    else
                    {
                        Animations.Resume(actor.Address, actor.ObjectIndex, _speed);
                    }
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
                draw.AddText(textAt, ImGui.GetColorU32(ImGuiCol.Text), e.Shown);
                draw.AddText(textAt + new System.Numerics.Vector2(0, ImGui.GetTextLineHeight()), ImGui.GetColorU32(ImGuiCol.TextDisabled), $"#{e.Id}  {e.Key}");
                draw.PopClipRect();
                if(Favorites.Has("animations", e.Key))
                    Favorites.DrawStar(draw, new System.Numerics.Vector2(start.X + width - 16f * scale, start.Y + 3f * scale), 12f * scale);
            }
        }
    }
}
