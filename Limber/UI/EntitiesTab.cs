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
/// Entity spawning (Scene), Costume (Scene) and the extra minions on the Extras page: any of the game's creatures
/// (minions, mounts, monsters, beast tribes...) picked from one searchable list.
/// </summary>
public sealed class EntitiesTab
{
    private readonly Selection _selection;
    private readonly RigSettings _config;
    private readonly ActorSpawner _spawner;
    private readonly Followers _followers;

    public EntitiesTab(Selection selection, RigSettings config, ActorSpawner spawner, Followers followers)
    {
        _selection = selection;
        _config = config;
        _spawner = spawner;
        _followers = followers;
    }

    // One picker per page (each remembers its own search and choice).
    private sealed class Picker
    {
        public string Search = "";
        public string Kind = "";
        public uint Chosen;
        public string Typed = "";
        public bool ByArea;
        // The groups, built once for the kind and sorting chosen.
        public Node? Tree;
        public (string Kind, bool Area, int Count) TreeFor;
    }

    // A group: its own groups and the creatures directly in it.
    private sealed class Node
    {
        public readonly string Name;
        public readonly SortedDictionary<string, Node> Children = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<Entities.Entry> Items = new();
        public int Count;

        public Node(string name) => Name = name;
    }

    private static Node BuildTree(IEnumerable<Entities.Entry> entries, bool byArea)
    {
        var root = new Node("");
        foreach(var e in entries)
        {
            var node = root;
            node.Count++;
            var path = byArea && e.AreaGroups != null ? e.AreaGroups : e.Groups;
            foreach(var g in path.Prepend(e.Kind))
            {
                if(!node.Children.TryGetValue(g, out var next))
                    node.Children[g] = next = new Node(g);
                node = next;
                node.Count++;
            }
            node.Items.Add(e);
        }
        return root;
    }

    private static bool DrawRow(Picker p, string id, Entities.Entry e)
    {
        bool picked = false;
        if(ImGui.Selectable($"{e.Name}###{id}_e_{e.Kind}_{e.Id}_{e.Name}", e.Id == p.Chosen, ImGuiSelectableFlags.AllowDoubleClick))
        {
            p.Chosen = e.Id;
            if(ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                picked = true;
        }
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip($"{e.Name}\n{e.Kind}, model {e.Code}, #{e.Id}\nDouble-click to use it.");
        ImGui.SameLine();
        ImGui.SetCursorPosX(MathF.Max(ImGui.GetCursorPosX() + 8, ImGui.GetWindowWidth() - 170 * ImGuiHelpers.GlobalScale));
        ImGui.TextDisabled($"{e.Code}  #{e.Id}");
        return picked;
    }

    private static bool DrawNode(Picker p, string id, Node node, string path)
    {
        bool picked = false;
        foreach(var child in node.Children.Values)
        {
            string key = path + "/" + child.Name;
            if(ImGui.TreeNodeEx($"{child.Name}  ({child.Count})###{id}_g_{key}"))
            {
                picked |= DrawNode(p, id, child, key);
                ImGui.TreePop();
            }
        }
        foreach(var e in node.Items)
            picked |= DrawRow(p, id, e);
        return picked;
    }

    private readonly Picker _spawnPick = new(), _costumePick = new(), _minionPick = new() { Kind = "Minions" };
    private string _status = "";

    // ---- The list

    private static bool Pick(Picker p, string id, float height)
    {
        var all = Entities.All;
        if(!Entities.Ready)
        {
            ImGui.TextDisabled("Reading the game's list of creatures...");
            return false;
        }
        bool picked = false;
        var kinds = all.Select(e => e.Kind).Distinct().ToList();
        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        using(var combo = ImRaii.Combo($"Kind###{id}_kind", p.Kind.Length == 0 ? "(all)" : p.Kind))
        {
            if(combo.Success)
            {
                if(ImGui.Selectable($"(all)###{id}_kind_all", p.Kind.Length == 0))
                    p.Kind = "";
                foreach(var k in kinds)
                    if(ImGui.Selectable($"{k}  ({all.Count(e => e.Kind == k)})###{id}_kind_{k}", k == p.Kind))
                        p.Kind = k;
            }
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(110 * ImGuiHelpers.GlobalScale);
        using(var sort = ImRaii.Combo($"###{id}_sort", p.ByArea ? "By area" : "By type"))
        {
            if(sort.Success)
            {
                if(ImGui.Selectable($"By type###{id}_sort_type", !p.ByArea))
                    p.ByArea = false;
                if(ImGui.Selectable($"By area###{id}_sort_area", p.ByArea))
                    p.ByArea = true;
            }
        }
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("By type: creatures grouped by their model (a family of creatures shares one), in hundreds.\n"
                + "By area: NPCs grouped by region and area, where the game places them. (Minions and mounts stay by name; "
                + "enemies' places aren't in the game's own data, so they stay by type.)");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint($"###{id}_search", "Search: name, model (m0123) or number", ref p.Search, 64);
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("A name (minion, mount, NPC), an area, a model's numbers (m0123, b01), or its number in the game's list.\nSeveral words: all must match.");
        var words = p.Search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool Match(Entities.Entry e)
        {
            if(p.Kind.Length > 0 && e.Kind != p.Kind)
                return false;
            foreach(var w in words)
            {
                if(uint.TryParse(w.TrimStart('#'), out var n) && (e.Id == n || e.Model == n))
                    continue;
                if(e.Name.Contains(w, StringComparison.OrdinalIgnoreCase) || e.Code.Contains(w, StringComparison.OrdinalIgnoreCase) || e.Kind.Contains(w, StringComparison.OrdinalIgnoreCase)
                   || (e.AreaGroups?.Any(g => g.Contains(w, StringComparison.OrdinalIgnoreCase)) ?? false))
                    continue;
                return false;
            }
            return true;
        }
        if(words.Length == 0)
        {
            // Not searching: groups to open.
            if(p.Tree == null || p.TreeFor != (p.Kind, p.ByArea, all.Count))
            {
                p.Tree = BuildTree(all.Where(e => p.Kind.Length == 0 || e.Kind == p.Kind), p.ByArea);
                p.TreeFor = (p.Kind, p.ByArea, all.Count);
            }
            using(var tree = ImRaii.Child($"###{id}_tree", new Vector2(-1, height * ImGuiHelpers.GlobalScale), true))
            {
                if(tree.Success)
                    picked |= DrawNode(p, id, p.Tree, "");
            }
            DrawTyped(p, id, all);
            return picked;
        }
        var list = all.Where(Match).ToList();
        ImGui.TextDisabled($"{list.Count} of {all.Count}");
        using(var child = ImRaii.Child($"###{id}_list", new Vector2(-1, height * ImGuiHelpers.GlobalScale), true))
        {
            if(child.Success)
            {
                // Only the rows in view are drawn (the whole list is thousands long); the rest is empty space.
                float row = ImGui.GetTextLineHeightWithSpacing();
                int first = Math.Clamp((int)(ImGui.GetScrollY() / row), 0, Math.Max(0, list.Count - 1));
                int shown = (int)(ImGui.GetWindowHeight() / row) + 2;
                int last = Math.Min(list.Count, first + shown);
                if(first > 0)
                    ImGui.Dummy(new Vector2(1, first * row));
                for(int i = first; i < last; i++)
                    picked |= DrawRow(p, id, list[i]);
                if(last < list.Count)
                    ImGui.Dummy(new Vector2(1, (list.Count - last) * row));
            }
        }
        DrawTyped(p, id, all);
        return picked;
    }

    private static void DrawTyped(Picker p, string id, IReadOnlyList<Entities.Entry> all)
    {
        // (or straight by its number)
        ImGui.SetNextItemWidth(120 * ImGuiHelpers.GlobalScale);
        if(ImGui.InputTextWithHint($"###{id}_typed", "Number", ref p.Typed, 8) && uint.TryParse(p.Typed, out var typed))
            p.Chosen = typed;
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("A model number from the game's list (what posing tools show as \"Model\").");
        ImGui.SameLine();
        var chosen = all.FirstOrDefault(e => e.Id == p.Chosen);
        ImGui.TextDisabled(p.Chosen == 0 ? "Nothing chosen." : chosen != null ? $"Chosen: {chosen.Name}" : $"Chosen: #{p.Chosen}");
    }

    private Dalamud.Game.ClientState.Objects.Types.ICharacter? Source()
    {
        var chosen = _selection.Actor;
        return Plugin.ObjectTable.LocalPlayer
            ?? (chosen.IsSet ? Plugin.ObjectTable.CreateObjectReference(chosen.Address) as Dalamud.Game.ClientState.Objects.Types.ICharacter : null);
    }

    // ---- Entity spawning

    public void DrawSpawn()
    {
        ImGui.TextWrapped("Any creature from the game as an actor of its own: minions, mounts, monsters, beast tribes and more. "
            + "Pose it with FK and IK posing (it has its own skeleton), move it with Move character, play animations on it, and key it on the Timeline, like any actor.");
        if(!Plugin.ClientState.IsGPosing && !_config.WorkOutsideGPose)
            ImGui.TextColored(new Vector4(1f, 0.75f, 0.3f, 1f), "In GPose (or with \"Also run outside GPose\", Advanced).");
        bool go = Pick(_spawnPick, "ent_spawn", 260);
        using(ImRaii.Disabled(_spawnPick.Chosen == 0 || !_spawner.CanAdd))
        {
            go |= ImGui.Button("Add it###ent_spawn_add");
            if(ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("A new actor, a step in front of you, and chosen at the top so you can pose it straight away.");
        }
        if(go && _spawnPick.Chosen != 0 && Source() is { } source)
        {
            var chosenEntry = Entities.All.FirstOrDefault(e => e.Id == _spawnPick.Chosen);
            string entityName = chosenEntry != null ? chosenEntry.Name.Split("  (")[0].Split(" (")[0] : $"Creature {_spawnPick.Chosen}";
            int index = _spawner.SpawnNamed(source, false, entityName);
            if(index < 0)
                _status = _spawner.LastError ?? "The game wouldn't add another actor.";
            else
            {
                Entities.WriteUndrawn((ushort)index, (int)_spawnPick.Chosen);
                if(Plugin.ObjectTable[index] is { } added)
                {
                    _config.FollowTarget = false;
                    _config.Save();
                    _selection.Select(ActorRef.From(added));
                }
                _status = "Added.";
            }
        }
        ImGui.SameLine();
        var actor = _selection.Actor;
        bool ours = actor.IsSet && _spawner.StillOurs(actor.ObjectIndex);
        using(ImRaii.Disabled(_spawnPick.Chosen == 0 || !ours))
        {
            if(ImGui.Button("Change the chosen actor into it###ent_spawn_change") && ours)
                _status = Entities.Become(actor.Address, actor.ObjectIndex, (int)_spawnPick.Chosen);
            if(ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("The actor chosen at the top (one Limber added) becomes this creature instead (redrawn). For anyone else, use Costume.");
        }
        ImGui.SameLine();
        using(ImRaii.Disabled(!ours))
        {
            if(ImGui.Button("Remove the chosen actor###ent_spawn_remove") && ours)
                _status = _spawner.Despawn(actor.ObjectIndex) ? "Removed." : "Couldn't remove it.";
        }
        if(_status.Length > 0)
            ImGui.TextDisabled(_status);
    }

    // ---- Costume

    public void DrawCostume()
    {
        var actor = _selection.Actor;
        if(!actor.IsSet)
        {
            ImGui.TextDisabled("Choose a character at the top of the window.");
            return;
        }
        ImGui.TextWrapped("The chosen character is drawn as any creature instead of itself (its own look comes back when you take the costume off). "
            + "Only on your screen.");
        int now = Entities.ModelOf(actor.Address, actor.ObjectIndex);
        var wearing = Entities.All.FirstOrDefault(e => e.Id == (uint)Math.Max(now, 0));
        ImGui.TextDisabled(now <= 0 ? "Wearing: its own body." : $"Wearing: {(wearing != null ? wearing.Name : "#" + now)}");
        bool go = Pick(_costumePick, "ent_costume", 260);
        using(ImRaii.Disabled(_costumePick.Chosen == 0))
        {
            go |= ImGui.Button("Put the costume on###ent_costume_on");
        }
        if(go && _costumePick.Chosen != 0)
            _status = Entities.Become(actor.Address, actor.ObjectIndex, (int)_costumePick.Chosen);
        ImGui.SameLine();
        using(ImRaii.Disabled(now <= 0 && !Entities.Changed(actor.Address)))
        {
            if(ImGui.Button("Take it off###ent_costume_off"))
                _status = Entities.Restore(actor.Address, actor.ObjectIndex);
        }
        if(_status.Length > 0)
            ImGui.TextDisabled(_status);
    }

    // ---- Extras: more minions

    public void DrawMinions()
    {
        bool on = _config.FollowersOn;
        if(ImGui.Checkbox("Extra minions follow me###ent_min_on", ref on))
        {
            _config.FollowersOn = on;
            _config.Save();
            if(!on)
                _followers.RemoveAll();
        }
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("As many minions (or any creatures) as you like, besides your own, following you around. Only on your screen.\n"
                + "Outside GPose they need \"Also run outside GPose\" (Advanced).");
        var list = _config.Followers ??= new();
        float spacing = _config.FollowerSpacing;
        ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
        if(ImGui.SliderFloat("Spacing###ent_min_space", ref spacing, 0.4f, 4f, "%.1f m"))
            _config.FollowerSpacing = spacing;
        if(ImGui.IsItemDeactivatedAfterEdit())
            _config.Save();
        if(_followers.Status.Length > 0)
            ImGui.TextColored(new Vector4(1f, 0.75f, 0.3f, 1f), _followers.Status);
        int remove = -1;
        for(int i = 0; i < list.Count; i++)
        {
            using var id = ImRaii.PushId(i);
            ImGui.TextUnformatted($"{i + 1}. {(list[i].Name.Length > 0 ? list[i].Name : "#" + list[i].Model)}");
            ImGui.SameLine();
            if(ImGui.SmallButton("x"))
                remove = i;
        }
        if(remove >= 0)
        {
            list.RemoveAt(remove);
            _followers.RemoveAll(); // (all made again in their new places)
            _config.Save();
        }
        if(ImGui.TreeNode("Add a minion###ent_min_add"))
        {
            bool go = Pick(_minionPick, "ent_min", 200);
            using(ImRaii.Disabled(_minionPick.Chosen == 0 || list.Count >= Followers.MaxFollowers))
            {
                go |= ImGui.Button("Add###ent_min_add_btn");
            }
            if(go && _minionPick.Chosen != 0 && list.Count < Followers.MaxFollowers)
            {
                var e = Entities.All.FirstOrDefault(x => x.Id == _minionPick.Chosen);
                list.Add(new FollowerSettings { Model = _minionPick.Chosen, Name = e?.Name ?? "" });
                _config.Save();
            }
            ImGui.TextDisabled($"Up to {Followers.MaxFollowers}.");
            ImGui.TreePop();
        }
    }
}
