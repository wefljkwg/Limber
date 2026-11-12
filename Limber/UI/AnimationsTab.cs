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

    // (for the page's presets)
    public float Speed { get => _speed; set => _speed = Math.Clamp(value, 0f, 2f); }
    public bool StartFrozen { get => _startFrozen; set => _startFrozen = value; }
    public int Race { get => _race; set => _race = value >= -1 && value < Animations.Races.Length ? value : -1; }
    public string Group { get => _group; set => _group = value ?? ""; }
    public string Kind { get => _kind; set => _kind = value ?? ""; }
    public bool FavouritesOnly { get => _favOnly; set => _favOnly = value; }
    public string Search { get => _search; set => _search = value ?? ""; }
    private string _status = "";

    private readonly AnimationRetarget? _retarget;
    private bool _viaSkeleton;
    private AnimationRetarget.Source? _source;
    private string _sourceSearch = "";

    public AnimationsTab(Selection selection, AnimationRetarget? retarget = null)
    {
        _selection = selection;
        _retarget = retarget;
    }

    // (perf) The kinds, groups and animations found, worked out again only when the list, the search, a filter or the
    // favourites change (see Draw); they were worked out from every animation each frame.
    private IReadOnlyList<Animations.Entry>? _kindsFor;
    private int _kindsCount;
    private List<string> _kinds = new(), _groups = new();
    private Dictionary<string, int> _kindCounts = new();
    private List<Animations.Entry>? _found;
    private (string, bool, string, string, int) _foundKey;
    private object? _foundFavSet;

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
            if(_retarget?.Target == actor.Address)
                _retarget.Stop();
            Animations.ClearRace(actor.Address, actor.ObjectIndex);
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
            {
                _race = -1;
                Animations.ClearRace(actor.Address, actor.ObjectIndex);
            }
            for(int i = 0; i < Animations.Races.Length; i++)
                if(ImGui.Selectable($"{Animations.Races[i].Name}###anim_race_{i}", i == _race))
                    _race = i;
            ImGui.EndCombo();
        }
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("Play another race's version of the animation, as Anamnesis's \"Data path\" does: the character loads its\n"
                + "animations as that race (the game's, or your mods made for that race), keeping its own body. Stays on until\n"
                + "\"Its own race\" or Stop. If the game already has the animation loaded, play another one first, then this one again.");
        DrawOtherSkeleton(actor);

        // Filter: by what kind of animation it is (in plain words), the key's own group, and a search.
        // (perf: the kinds, groups and the list found are worked out again only when the list or a filter changes, not
        // every frame: thousands of animations searched by text each frame)
        if(!ReferenceEquals(_kindsFor, all) || _kindsCount != all.Count)
        {
            _kindsFor = all;
            _kindsCount = all.Count;
            _kinds = all.Select(e => e.Kind).Distinct().OrderBy(g => g == "Other" ? "~" : g).ToList();
            _groups = all.Select(e => e.Group).Distinct().OrderBy(g => g).ToList();
            _kindCounts = all.GroupBy(e => e.Kind).ToDictionary(g => g.Key, g => g.Count());
            _found = null;
        }
        var kinds = _kinds;
        ImGui.SetNextItemWidth(170 * ImGuiHelpers.GlobalScale);
        if(ImGui.BeginCombo("Kind###anim_kind", _kind.Length == 0 ? "(all)" : _kind))
        {
            if(ImGui.Selectable("(all)###anim_kind_all", _kind.Length == 0))
                _kind = "";
            foreach(var g in kinds)
                if(ImGui.Selectable($"{g}  ({_kindCounts.GetValueOrDefault(g)})###anim_kind_{g}", g == _kind))
                    _kind = g;
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        var groups = _groups;
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
        var favSet = Favorites.Identity("animations");
        int favCount = Favorites.Count("animations");
        var foundKey = (_search, _favOnly, _kind, _group, favCount);
        if(_found == null || foundKey != _foundKey || !ReferenceEquals(favSet, _foundFavSet))
        {
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
            _found = all.Where(e => (!_favOnly || Favorites.Has("animations", e.Key))
                                      && (_kind.Length == 0 || e.Kind == _kind)
                                      && (_group.Length == 0 || e.Group == _group)
                                      && Matches(e))
                .OrderByDescending(e => words.Length == 1 && ushort.TryParse(words[0].TrimStart('#'), out var n) && e.Id == n)
                .ThenBy(e => e.Name.Length > 0 ? 0 : e.Described.Length > 0 ? 1 : 2)
                .Take(400).ToList();
            _foundKey = foundKey;
            _foundFavSet = favSet;
        }
        var list = _found;
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
            // (perf: only the rows on screen are drawn, up to 400 icons and their text were drawn every frame)
            int rows = (list.Count + columns - 1) / columns;
            var clipper = ImGui.ImGuiListClipper();
            clipper.Begin(rows);
            while(clipper.Step())
                for(int row = clipper.DisplayStart; row < clipper.DisplayEnd; row++)
                {
                    ImGui.TableNextRow();
                    for(int col = 0; col < columns; col++)
                    {
                        int index = row * columns + col;
                        if(index >= list.Count)
                            break;
                        var e = list[index];
                        ImGui.TableSetColumnIndex(col);
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
                            _status = _viaSkeleton && _retarget != null && _source != null
                                ? _retarget.Start(actor.Address, actor.ObjectIndex, _source, e.Id)
                                : Animations.PlayFrom(actor.Address, actor.ObjectIndex, e.Id, _startFrozen, _speed,
                                    _race < 0 ? (ushort)0 : Animations.Races[_race].Body);
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
            clipper.End();
            clipper.Destroy();
        }
    }

    // ---- From another skeleton: a race's or creature's animation put on this character by bone names

    private void DrawOtherSkeleton(ActorRef actor)
    {
        if(_retarget == null)
            return;
        ImGui.Checkbox("Play through another skeleton###anim_via", ref _viaSkeleton);
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("Experimental: double-click plays the animation on a hidden helper of the race or creature chosen below, and its\n"
                + "bones are put on this character by name, so it works even when the skeletons don't match (a creature's\n"
                + "animation on a player, one race's on another). Bones the source hasn't got follow their parents. GPose only.");
        if(_viaSkeleton)
        {
            ImGui.Indent();
            ImGui.SetNextItemWidth(260 * ImGuiHelpers.GlobalScale);
            using(var combo = ImRaii.Combo("Source###anim_src", _source?.Name ?? "Choose a race or creature..."))
                if(combo.Success)
                {
                    ImGui.SetNextItemWidth(-1);
                    ImGui.InputTextWithHint("###anim_src_search", "Search creatures (name or m0123)", ref _sourceSearch, 64);
                    if(_sourceSearch.Length == 0)
                        foreach(var (body, name) in Animations.Races)
                            if(ImGui.Selectable($"Race: {name}###anim_src_r{body}", _source?.RaceBody == body))
                                _source = new AnimationRetarget.Source(name, body, 0);
                    var all = Entities.All;
                    if(all.Count == 0)
                        ImGui.TextDisabled("Reading the creature list...");
                    else
                    {
                        int shown = 0;
                        foreach(var e in all)
                        {
                            if(e.Type == 1 || e.Id == 0)
                                continue;
                            if(_sourceSearch.Length > 0 && !e.Name.Contains(_sourceSearch, StringComparison.OrdinalIgnoreCase)
                               && !e.Code.Contains(_sourceSearch, StringComparison.OrdinalIgnoreCase))
                                continue;
                            if(++shown > 80)
                            {
                                ImGui.TextDisabled("(more: search to narrow it down)");
                                break;
                            }
                            string label = (e.Name.Length > 0 ? e.Name : e.Code) + $"  ({e.Code})";
                            if(ImGui.Selectable($"{label}###anim_src_c{e.Id}", _source?.Model == (int)e.Id))
                                _source = new AnimationRetarget.Source(e.Name.Length > 0 ? e.Name : e.Code, 0, (int)e.Id);
                        }
                    }
                }
            bool hips = _retarget.MoveHips;
            if(ImGui.Checkbox("Move the hips###anim_via_hips", ref hips))
                _retarget.MoveHips = hips;
            if(ImGui.IsItemHovered())
                ImGui.SetTooltip("The hips move (up, down, along) as the source's do, scaled to this character's size.");
            ImGui.SameLine();
            bool show = _retarget.ShowSource;
            if(ImGui.Checkbox("Show the source###anim_via_show", ref show))
                _retarget.ShowSource = show;
            float weight = _retarget.Weight;
            ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
            if(ImGui.SliderFloat("Amount###anim_via_weight", ref weight, 0f, 1f, "%.2f"))
                _retarget.Weight = weight;
            if(_retarget.Target == actor.Address)
            {
                ImGui.TextDisabled(_retarget.Status + (_retarget.Matched > 0 ? $" ({_retarget.Matched} bones matched)" : ""));
                if(ImGui.SmallButton("Stop###anim_via_stop"))
                    _retarget.Stop();
            }
            else if(_source == null)
                ImGui.TextDisabled("Choose a source, then double-click an animation below (generic ones like idles, emotes,\n"
                    + "battle and \"mon_sp\" animations play as that creature's own version).");
            ImGui.Unindent();
        }
    }
}
