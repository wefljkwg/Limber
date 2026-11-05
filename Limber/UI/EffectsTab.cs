using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Limber.BodyRig;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Limber.UI;

/// <summary>
/// The "Effects" page: every visual effect the game data names, in groups by type, to place in the scene or put on
/// one of a character's bones (only on your screen).
/// </summary>
public sealed unsafe class EffectsTab
{
    private readonly Effects _effects;
    private readonly Selection _selection;

    private string _search = string.Empty;
    private bool _favOnly, _filteredFav;
    private int _filteredFavCount = -1;
    private EffectItem? _chosen;
    private EffectItem[] _filtered = [];
    private string _filteredFor = "\0";
    private int _filteredCount = -1;
    // type -> its groups -> (start, end) in _filtered
    private readonly List<(string Type, int Start, int End, List<(string Group, int Start, int End)> Groups)> _types = new();

    private string _placeBone = "j_te_r";
    private int _targetIndex = -1; // the character effect's target (object index; -1: the caster itself)
    private readonly Dictionary<Guid, string> _attachBone = new();
    private bool _attachKeep = true;
    // New effects: looped from the start, played over every this many seconds.
    private bool _loopNew = true;
    private float _loopEvery = 3f;

    private void Defaults(SpawnedEffect e)
    {
        e.Repeat = _loopNew;
        e.Every = _loopEvery;
    }
    private const string FavList = "effects";

    // Finding one: by kind (smoke, fire, sparkles...), and the list in sections by kind or by where it's from.
    private string _kind = "";
    private bool _byKind = true;
    private string _filteredKind = "\0";
    private bool _filteredByKind;
    private string[] _rowType = [], _rowGroup = [];

    public EffectsTab(Effects effects, Selection selection)
    {
        _effects = effects;
        _selection = selection;
    }

    public void Draw()
    {
        if(!_effects.Ready)
        {
            ImGui.TextDisabled(_effects.Status.Length > 0 ? _effects.Status : "Effects aren't available.");
            return;
        }
        var catalog = _effects.Catalog;
        if(catalog == null)
        {
            ImGui.TextDisabled(_effects.CatalogFailed ? "Couldn't read the effects from the game data." : "Reading the effects from the game data...");
            return;
        }
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X * 0.6f);
        ImGui.InputTextWithHint("###fx_search", $"Search {catalog.Length} effects: words like smoke, fire, heart, a spell, a job, a place", ref _search, 64);
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("Every word has to match somewhere: its name, what uses it (actions, statuses, emotes), its kind, the place it's from or its file.\n"
                + "For example \"steam limsa\", \"fire black mage\", \"heart\".");
        ImGui.SameLine();
        ImGui.Checkbox($"Favorites only ({Favorites.Count(FavList)})###fx_favs", ref _favOnly);
        ImGui.Checkbox("Loop new effects###fx_loop_new", ref _loopNew);
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("Effects you place from now on play over and over (most effects play once and end).");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(110 * ImGuiHelpers.GlobalScale);
        if(ImGui.DragFloat("###fx_loop_every", ref _loopEvery, 0.05f, 0.2f, 600f, "every %.1f s"))
            _loopEvery = Math.Clamp(_loopEvery, 0.2f, 600f);
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("How often a looped effect starts over (its length: set it to how long the effect plays).");

        if(_effects.CatalogGrowing)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("(still adding the effects placed in every zone...)");
        }
        DrawKinds();

        int favCount = Favorites.Count(FavList);
        if(_filteredFor != _search || _filteredCount != catalog.Length || _filteredFav != _favOnly || (_favOnly && _filteredFavCount != favCount)
           || _filteredKind != _kind || _filteredByKind != _byKind)
        {
            _filteredFor = _search;
            _filteredCount = catalog.Length;
            _filteredFav = _favOnly;
            _filteredFavCount = favCount;
            _filteredKind = _kind;
            _filteredByKind = _byKind;
            var words = _search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            bool Matches(EffectItem i)
            {
                foreach(var w in words)
                    if(!(i.Name.Contains(w, StringComparison.OrdinalIgnoreCase) || i.Also.Contains(w, StringComparison.OrdinalIgnoreCase)
                         || i.Path.Contains(w, StringComparison.OrdinalIgnoreCase) || i.Group.Contains(w, StringComparison.OrdinalIgnoreCase)
                         || i.Type.Contains(w, StringComparison.OrdinalIgnoreCase) || i.Kinds.Any(k => k.Contains(w, StringComparison.OrdinalIgnoreCase))))
                        return false;
                return true;
            }
            var picked = catalog.Where(i => (!_favOnly || Favorites.Has(FavList, i.Path)) && (_kind.Length == 0 || i.Kinds.Contains(_kind)) && Matches(i));
            // The rows: by kind (an effect of several kinds in each), or by where it's from.
            var rows = new List<(string Type, string Group, EffectItem Item)>();
            if(_byKind)
            {
                foreach(var i in picked)
                    foreach(var kind in i.Kinds)
                        if(_kind.Length == 0 || kind == _kind)
                            rows.Add((kind, i.Type, i));
                var order = Effects.AllKinds.Select((k, n) => (k, n)).ToDictionary(x => x.k, x => x.n);
                rows = rows.OrderBy(r => order.GetValueOrDefault(r.Type, 999)).ThenBy(r => r.Group, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(r => r.Item.Name, StringComparer.OrdinalIgnoreCase).ToList();
            }
            else
                foreach(var i in picked)
                    rows.Add((i.Type, i.Group, i)); // (the catalogue is in this order already)
            _filtered = rows.Select(r => r.Item).ToArray();
            _rowType = rows.Select(r => r.Type).ToArray();
            _rowGroup = rows.Select(r => r.Group).ToArray();
            _types.Clear();
            int k = 0;
            while(k < _filtered.Length)
            {
                int start = k;
                string type = _rowType[k];
                var groups = new List<(string, int, int)>();
                while(k < _filtered.Length && _rowType[k] == type)
                {
                    int g = k;
                    string group = _rowGroup[k];
                    while(k < _filtered.Length && _rowType[k] == type && _rowGroup[k] == group)
                        k++;
                    groups.Add((group, g, k));
                }
                _types.Add((type, start, k, groups));
            }
        }

        float height = Math.Max(220f * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().Y * 0.5f);
        float listWidth = ImGui.GetContentRegionAvail().X * 0.55f;
        using(var list = ImRaii.Child("###fx_list", new Vector2(listWidth, height), true))
        {
            if(list.Success)
                DrawList();
        }
        ImGui.SameLine();
        using(var side = ImRaii.Child("###fx_side", new Vector2(-1, height), true))
        {
            if(side.Success)
                DrawChosen();
        }
        DrawPlaced();
    }

    // The kinds as buttons (one at a time; again to show all), and how the list is put in sections.
    private void DrawKinds()
    {
        ImGui.TextUnformatted("Sections:");
        ImGui.SameLine();
        if(ImGui.RadioButton("By kind###fx_by_kind", _byKind))
            _byKind = true;
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("Smoke, fire, sparkles, hearts...: what it looks like (from its names; one can be in several).");
        ImGui.SameLine();
        if(ImGui.RadioButton("By where it's from###fx_by_source", !_byKind))
            _byKind = false;
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("Actions by job, statuses, emotes, places (by zone), area markers, tethers and the game's own folders.");
        ImGui.SameLine();
        ImGui.TextDisabled("Only:");
        float right = ImGui.GetWindowPos().X + ImGui.GetWindowWidth() - ImGui.GetStyle().WindowPadding.X;
        bool first = true;
        foreach(var kind in new[] { "" }.Concat(Effects.AllKinds))
        {
            string label = kind.Length == 0 ? "All kinds" : kind;
            float w = ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2f + ImGui.GetStyle().ItemSpacing.X;
            if(!first && ImGui.GetItemRectMax().X + w < right)
                ImGui.SameLine();
            else if(first)
                ImGui.SameLine();
            first = false;
            bool on = _kind == kind;
            using(ImRaii.PushColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.ButtonActive), on))
            {
                if(ImGui.SmallButton($"{label}###fx_kind_{kind}"))
                    _kind = on ? "" : kind;
            }
        }
    }

    // In sections by type, each in groups (the name's leading letters, or whose effects they are).
    private void DrawList()
    {
        if(_filtered.Length == 0)
        {
            ImGui.TextDisabled("Nothing matches.");
            return;
        }
        bool searching = _search.Length > 0;
        const int Limit = 400;
        foreach(var (type, start, end, groups) in _types)
        {
            if(searching)
                ImGui.SetNextItemOpen(true, ImGuiCond.Always);
            if(!ImGui.TreeNodeEx($"{type} ({end - start})###fx_type_{type}", ImGuiTreeNodeFlags.SpanAvailWidth))
                continue;
            foreach(var (group, gs, ge) in groups)
            {
                if(searching && groups.Count <= 12)
                    ImGui.SetNextItemOpen(true, ImGuiCond.Always);
                if(!ImGui.TreeNodeEx($"{group} ({ge - gs})###fx_group_{type}_{group}", ImGuiTreeNodeFlags.SpanAvailWidth))
                    continue;
                int shown = 0;
                for(int k = gs; k < ge; k++)
                {
                    if(++shown > Limit)
                    {
                        ImGui.TextDisabled($"... and {ge - gs - Limit} more: search to narrow it down.");
                        break;
                    }
                    var item = _filtered[k];
                    Favorites.Star(FavList, item.Path, "fx_" + item.Path);
                    ImGui.SameLine();
                    if(ImGui.Selectable($"{item.Name}###fx_{item.Path}", _chosen == item))
                        _chosen = item;
                    if(ImGui.IsItemHovered())
                        ImGui.SetTooltip((item.Also.Length > 0 ? $"Also used by: {item.Also}\n" : "") + item.Path
                            + "\nDouble-click to place it in front of the chosen character.");
                    if(ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && _selection.Actor.IsSet)
                        SpawnInFront(item, _selection.Actor);
                }
                ImGui.TreePop();
            }
            ImGui.TreePop();
        }
    }

    private void DrawChosen()
    {
        var item = _chosen;
        if(item == null)
        {
            ImGui.TextDisabled("Pick an effect to place it.");
            ImGui.TextWrapped("Most effects play once and end: \"Play again\" (or \"Repeat\") on a placed one plays it over. Tethers need two ends and may show nothing on their own.");
            return;
        }
        ImGui.TextUnformatted(item.Name);
        ImGui.TextDisabled($"{item.Type}: {item.Group}");
        if(item.Kinds.Length > 0 && !(item.Kinds.Length == 1 && item.Kinds[0] == "Other"))
            ImGui.TextDisabled("Looks like: " + string.Join(", ", item.Kinds.Where(k => k != "Other")));
        if(item.Also.Length > 0)
            ImGui.TextWrapped("Also used by: " + item.Also);
        ImGui.TextDisabled(item.Path);
        if(ImGui.SmallButton("Copy path###fx_copy"))
            ImGui.SetClipboardText(item.Path);
        ImGui.Spacing();
        var actor = _selection.Actor;
        if(!actor.IsSet)
        {
            ImGui.TextDisabled("Choose a character at the top of the window to place it.");
            return;
        }
        // On a character (spells, statuses, emotes: the effects that bind to a caster's and a target's bones).
        bool characterKind = item.Type is "Actions (by job)" or "Status effects" or "Emotes" || item.Path.StartsWith("vfx/ws/", StringComparison.Ordinal)
            || item.Path.StartsWith("vfx/common/eff/", StringComparison.Ordinal) || item.Path.StartsWith("vfx/channeling/", StringComparison.Ordinal);
        if(_effects.ActorReady)
        {
            using(ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.25f, 0.45f, 0.6f, 1f), characterKind))
            {
                if(ImGui.Button($"Play on {actor.Name}###fx_spawn_on"))
                {
                    nint target = actor.Address;
                    ushort targetIndex = actor.ObjectIndex;
                    string targetName = actor.Name;
                    if(_targetIndex >= 0 && Plugin.ObjectTable[_targetIndex] is { } t && t.Address != 0)
                    {
                        target = t.Address;
                        targetIndex = (ushort)_targetIndex;
                        targetName = t.Name.TextValue;
                    }
                    _effects.SpawnOn(item, actor.Address, actor.ObjectIndex, actor.Name, target, targetIndex, targetName, Defaults);
                }
            }
            if(ImGui.IsItemHovered())
                ImGui.SetTooltip("The game's own way of playing it: from this character, at the target below. Its parts find their bones "
                    + "(a hand, the head, the target), and it moves with them. Use this for spells, statuses and emotes.");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(-1);
            string targetLabel = _targetIndex >= 0 && Plugin.ObjectTable[_targetIndex] is { } cur ? $"at {cur.Name.TextValue}" : "at itself";
            using(var combo = ImRaii.Combo("###fx_target", targetLabel))
            {
                if(combo.Success)
                {
                    if(ImGui.Selectable("at itself###fxt_self", _targetIndex < 0))
                        _targetIndex = -1;
                    foreach(var o in Plugin.ObjectTable)
                    {
                        if(o is not Dalamud.Game.ClientState.Objects.Types.ICharacter || o.Address == 0 || o.Address == actor.Address || o.Name.TextValue.Length == 0)
                            continue;
                        if(ImGui.Selectable($"at {o.Name.TextValue}###fxt_{o.ObjectIndex}", _targetIndex == o.ObjectIndex))
                            _targetIndex = o.ObjectIndex;
                    }
                }
            }
            if(characterKind)
                ImGui.TextDisabled("This one most likely needs a character to play on.");
        }
        if(ImGui.Button($"Place in front of {actor.Name}###fx_spawn"))
            SpawnInFront(item, actor);
        ImGui.Spacing();
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X * 0.6f);
        using(var combo = ImRaii.Combo("###fx_place_bone", HousingPropsTab.BoneLabel(_placeBone)))
        {
            if(combo.Success)
                foreach(var (b, name) in HousingPropsTab.AttachBones)
                    if(ImGui.Selectable($"{name}###fxb_{b}", b == _placeBone))
                        _placeBone = b;
        }
        ImGui.SameLine();
        if(ImGui.Button("Put on this bone###fx_spawn_bone"))
        {
            string bone = _placeBone;
            _effects.Spawn(item, Vector3.Zero, 0f, e =>
            {
                Defaults(e);
                Effects.Attach(e, actor.Address, actor.ObjectIndex, actor.Name, bone, keep: false);
            });
        }
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip($"Places it right on that bone of {actor.Name}: it moves with it from then on.");
    }

    private void SpawnInFront(EffectItem item, ActorRef actor)
    {
        var obj = Limber.Core.ActorSkeleton.GetObject(actor.ObjectIndex);
        if(obj == null || (nint)obj != actor.Address)
            return;
        var position = (Vector3)obj->Position;
        float yaw = obj->Rotation;
        var forward = new Vector3(MathF.Sin(yaw), 0f, MathF.Cos(yaw));
        _effects.Spawn(item, position + forward * 1.0f, yaw * 180f / MathF.PI, Defaults);
    }

    private void DrawPlaced()
    {
        var placed = _effects.Spawned;
        ImGui.Spacing();
        if(!ImGui.CollapsingHeader($"Placed ({placed.Count})###fx_placed", ImGuiTreeNodeFlags.DefaultOpen))
            return;
        if(placed.Count == 0)
        {
            ImGui.TextDisabled("Nothing placed yet.");
            return;
        }
        if(ImGui.SmallButton("Play all again###fx_replay_all"))
            foreach(var e in placed)
                Effects.Replay(e);
        ImGui.SameLine();
        bool allLooped = placed.All(e => e.Repeat);
        if(ImGui.SmallButton(allLooped ? "Stop looping all###fx_loop_all" : "Loop all###fx_loop_all"))
            foreach(var e in placed)
                e.Repeat = !allLooped;
        ImGui.SameLine();
        if(ImGui.SmallButton("Remove all###fx_remove_all"))
            _effects.RemoveAll();

        foreach(var e in placed)
        {
            using var id = ImRaii.PushId(e.Id.ToString());
            string where = e.OnCharacter ? $"  (on {e.CasterName}{(e.Target != e.Caster ? $", at {e.TargetName}" : "")})"
                : e.AttachTo != 0 ? $"  (on {HousingPropsTab.BoneLabel(e.AttachBone)})" : "";
            bool open = ImGui.TreeNode($"{e.Item.Name}{where}###node");
            ImGui.SameLine();
            bool loop = e.Repeat;
            if(ImGui.Checkbox("Loop###loop", ref loop))
                e.Repeat = loop;
            if(ImGui.IsItemHovered())
                ImGui.SetTooltip("Plays it over and over (every Loop length seconds).");
            ImGui.SameLine();
            if(ImGui.SmallButton("Play again###replay"))
                Effects.Replay(e);
            ImGui.SameLine();
            if(ImGui.SmallButton("Delete###delete"))
                _effects.Remove(e.Id);
            if(!open)
                continue;

            float every = e.Every;
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X * 0.7f);
            if(ImGui.DragFloat("Loop length (s)###every", ref every, 0.05f, 0.2f, 600f, "%.1f"))
                e.Every = Math.Clamp(every, 0.2f, 600f);
            if(ImGui.IsItemHovered())
                ImGui.SetTooltip("How often it starts over while Loop is on: about how long the effect plays."
                    + (e.OnCharacter ? " (On a character it also starts over as soon as it ends.)" : ""));
            if(e.OnCharacter)
            {
                DrawMoveIt(e);
                ImGui.TreePop();
                continue;
            }

            DrawAttach(e);

            bool onBone = e.AttachTo != 0;
            if(onBone)
            {
                // Which way the offset goes: along the bone, the character's facing, or the world.
                ImGui.TextUnformatted("Move along:");
                foreach(var (axes, label, tip) in new[]
                {
                    (EffectAxes.Bone, "Bone", "The bone's own axes: it turns with the bone."),
                    (EffectAxes.Character, "Character", "The character's: X sideways, Y up, Z forward from the way it faces."),
                    (EffectAxes.World, "World", "The world's axes: only its place follows the bone, not its turn."),
                })
                {
                    ImGui.SameLine();
                    if(ImGui.RadioButton($"{label}###axes_{axes}", e.Axes == axes))
                        _effects.SetAxes(e, axes);
                    if(ImGui.IsItemHovered())
                        ImGui.SetTooltip(tip);
                }
            }
            else
                DrawNudge(e);
            var position = e.Position;
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X * 0.7f);
            if(ImGui.DragFloat3(onBone ? "Offset from the bone###position" : "Position (world)###position", ref position, onBone ? 0.002f : 0.01f, -10000f, 10000f, onBone ? "%.3f" : "%.2f"))
            {
                e.Position = position;
                Effects.MarkChanged(e);
            }
            var turn = e.Turn;
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X * 0.7f);
            if(ImGui.DragFloat3(onBone ? "Turn on the bone (°)###turn" : "Turn (°)###turn", ref turn, 0.5f, -360f, 360f, "%.1f"))
            {
                e.Turn = turn;
                Effects.MarkChanged(e);
            }
            float scale = e.Scale;
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X * 0.7f);
            if(ImGui.DragFloat("Size###scale", ref scale, 0.005f, 0.001f, 1000f, "%.3f", ImGuiSliderFlags.Logarithmic))
            {
                e.Scale = scale;
                Effects.MarkChanged(e);
            }
            ImGui.TreePop();
        }
    }

    // A character effect moved: played on a hidden helper (a copy of the character holding its pose), put where it's moved to.
    private void DrawMoveIt(SpawnedEffect e)
    {
        bool move = e.MoveIt;
        if(ImGui.Checkbox("Move it###move_it", ref move))
            e.MoveIt = move;
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("Plays it on a hidden copy of the character instead (holding the same pose every frame), put where you move it to:\n"
                + "the effect still finds its bones, just somewhere else. Off: back on the character itself.");
        if(!e.MoveIt)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("(on the character: the game places it with its bones)");
            return;
        }
        ImGui.SameLine();
        bool show = e.ShowHelper;
        if(ImGui.Checkbox("Show the helper###show_helper", ref show))
            e.ShowHelper = show;
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("Shows the hidden copy it plays on (to see where it is, or if the effect doesn't show).");
        if(e.MoveStatus.Length > 0)
            ImGui.TextDisabled(e.MoveStatus);

        ImGui.TextUnformatted("Move along:");
        ImGui.SameLine();
        if(ImGui.RadioButton("Character###move_char", !e.MoveWorldAxes))
            e.MoveWorldAxes = false;
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("The character's axes: X sideways, Y up, Z forward from the way it faces (it turns with the character).");
        ImGui.SameLine();
        if(ImGui.RadioButton("World###move_world", e.MoveWorldAxes))
            e.MoveWorldAxes = true;
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("The world's axes (the offset doesn't turn with the character).");
        var offset = e.MoveOffset;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X * 0.7f);
        if(ImGui.DragFloat3("Offset###move_offset", ref offset, 0.01f, -1000f, 1000f, "%.2f"))
            e.MoveOffset = offset;
        var turn = e.MoveTurn;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X * 0.7f);
        if(ImGui.DragFloat3("Turn (°)###move_turn", ref turn, 0.5f, -360f, 360f, "%.1f"))
            e.MoveTurn = turn;
        if(ImGui.SmallButton("Back on the character###move_reset"))
        {
            e.MoveOffset = Vector3.Zero;
            e.MoveTurn = Vector3.Zero;
        }
    }

    // A free effect moved along a character's axes (forward, sideways, up from the way it faces); Position is the world's.
    private void DrawNudge(SpawnedEffect e)
    {
        var actor = _selection.Actor;
        if(!actor.IsSet)
            return;
        var obj = Limber.Core.ActorSkeleton.GetObject(actor.ObjectIndex);
        if(obj == null || (nint)obj != actor.Address)
            return;
        float yaw = obj->Rotation;
        var forward = new Vector3(MathF.Sin(yaw), 0f, MathF.Cos(yaw));
        var side = new Vector3(forward.Z, 0f, -forward.X);
        var delta = Vector3.Zero;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X * 0.7f);
        if(ImGui.DragFloat3($"Move along {actor.Name}###nudge", ref delta, 0.01f, -100f, 100f, "drag"))
        {
            e.Position += side * delta.X + Vector3.UnitY * delta.Y + forward * delta.Z;
            Effects.MarkChanged(e);
        }
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip($"Drag to move it along {actor.Name}'s axes: X sideways, Y up, Z forward (Position below is the world's).");
    }

    private void DrawAttach(SpawnedEffect e)
    {
        if(e.AttachTo != 0)
        {
            ImGui.TextUnformatted($"On {(e.AttachName.Length > 0 ? e.AttachName : "a character")}'s {HousingPropsTab.BoneLabel(e.AttachBone)}");
            if(!_effects.BoneFound(e))
            {
                ImGui.SameLine();
                ImGui.TextDisabled("(not found now: left where it was)");
            }
            ImGui.SameLine();
            if(ImGui.SmallButton("Detach###detach"))
                _effects.Detach(e);
            if(ImGui.IsItemHovered())
                ImGui.SetTooltip("Off the bone, left where it is now.");
            bool size = e.AttachSize;
            if(ImGui.Checkbox("Size with the character###attach_size", ref size))
                e.AttachSize = size;
            return;
        }
        var actor = _selection.Actor;
        if(!actor.IsSet)
            return;
        if(!_attachBone.TryGetValue(e.Id, out var bone))
            _attachBone[e.Id] = bone = "j_te_r";
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X * 0.45f);
        using(var combo = ImRaii.Combo("###attach_bone", HousingPropsTab.BoneLabel(bone)))
        {
            if(combo.Success)
                foreach(var (b, name) in HousingPropsTab.AttachBones)
                    if(ImGui.Selectable($"{name}###ab_{b}", b == bone))
                        _attachBone[e.Id] = bone = b;
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(110 * ImGuiHelpers.GlobalScale);
        string typed = bone;
        if(ImGui.InputText("###attach_bone_name", ref typed, 64))
            _attachBone[e.Id] = bone = typed.Trim();
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("Or type any bone's name (as the skeleton has it, e.g. j_te_r, j_kao, j_ago).");
        ImGui.SameLine();
        using(ImRaii.Disabled(bone.Length == 0))
        {
            if(ImGui.SmallButton($"Attach to {actor.Name}###attach"))
                Effects.Attach(e, actor.Address, actor.ObjectIndex, actor.Name, bone, _attachKeep);
        }
        ImGui.SameLine();
        ImGui.Checkbox("Stay where it is###attach_keep", ref _attachKeep);
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("On: it stays where it is now and moves with the bone from there. Off: it goes right onto the bone.");
    }
}
