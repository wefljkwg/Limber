using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Limber.Rig;

namespace Limber.UI;

/// <summary>
/// The Global presets page (right after Getting started): everything a character has saved as one preset, put on any
/// character, edited, and put on characters by themselves when they come into the scene.
/// </summary>
public sealed class GlobalPresetsTab
{
    private readonly Selection _selection;
    private readonly RigSettings _config;
    private readonly GlobalPresetService _presets;
    private string _name = "";
    private string _ruleName = "";
    private string _status = "";
    private string? _renaming;
    private string _rename = "";
    private readonly System.Collections.Generic.HashSet<string> _skip = new();   // parts left out of the next save

    public GlobalPresetsTab(Selection selection, RigSettings config, GlobalPresetService presets)
    {
        _selection = selection;
        _config = config;
        _presets = presets;
    }

    public void Draw()
    {
        var actor = _selection.Actor;
        string chosen = actor.IsSet ? actor.Name ?? "" : "";
        var all = _config.GlobalPresets ??= new();

        // ---- Save
        SeparatorText("Save a global preset");
        if(chosen.Length == 0)
            ImGui.TextDisabled("Choose a character at the top: everything Limber has for them is saved as one preset.");
        else
        {
            var parts = _presets.PartsOf(chosen);
            if(parts.Count == 0)
                ImGui.TextUnformatted($"{chosen} has nothing set yet.");
            else
            {
                // What goes in: each page the character has settings for, ticked (untick to leave it out).
                ImGui.TextUnformatted($"Save from {chosen}:");
                foreach(var part in parts)
                {
                    ImGui.SameLine();
                    bool take = !_skip.Contains(part);
                    if(ImGui.Checkbox($"{part}###gp_take_{part}", ref take))
                    {
                        if(take)
                            _skip.Remove(part);
                        else
                            _skip.Add(part);
                    }
                    if(ImGui.GetContentRegionAvail().X < 140 * ImGuiHelpers.GlobalScale)
                        ImGui.NewLine();
                }
            }
            var taking = parts.Where(p => !_skip.Contains(p)).ToList();
            ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(220f * ImGuiHelpers.GlobalScale));
            ImGui.InputTextWithHint("###gp_name", "Name for the preset", ref _name, 48);
            ImGui.SameLine();
            using(ImRaii.Disabled(_name.Trim().Length == 0 || taking.Count == 0))
            {
                if(ImGui.Button("Save###gp_save"))
                {
                    _presets.Save(chosen, _name.Trim(), taking);
                    _status = $"Saved \"{_name.Trim()}\" from {chosen}.";
                    _name = "";
                }
            }
        }
        if(_status.Length > 0)
            ImGui.TextDisabled(_status);

        // ---- The presets
        SeparatorText($"Global presets ({all.Count})");
        if(all.Count == 0)
            ImGui.TextDisabled("None yet.");
        GlobalPreset? remove = null;
        foreach(var preset in all.ToList())
        {
            using var id = ImRaii.PushId(preset.Name);
            if(_renaming == preset.Name)
            {
                ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(200f * ImGuiHelpers.GlobalScale));
                bool done = ImGui.InputText("###gp_rename", ref _rename, 48, ImGuiInputTextFlags.EnterReturnsTrue);
                ImGui.SameLine();
                done |= ImGui.SmallButton("OK###gp_rename_ok");
                if(done && _rename.Trim().Length > 0 && all.All(p => p == preset || p.Name != _rename.Trim()))
                {
                    string old = preset.Name;
                    preset.Name = _rename.Trim();
                    // (the rules and looks that use it follow the new name)
                    foreach(var rule in _config.GlobalPresetRules ?? new())
                        if(rule.Preset == old)
                            rule.Preset = preset.Name;
                    foreach(var look in _config.AppearancePresets ?? new())
                        if(look.GlobalPreset == old)
                            look.GlobalPreset = preset.Name;
                    _renaming = null;
                    _config.Save();
                }
            }
            else
            {
                int on = preset.Parts.Keys.Count(k => !(preset.Off?.Contains(k) ?? false));
                bool open = ImGui.TreeNode($"{preset.Name}  ({on} of {preset.Parts.Count} part{(preset.Parts.Count == 1 ? "" : "s")} on)###gp_node");
                ImGui.SameLine();
                using(ImRaii.Disabled(chosen.Length == 0))
                {
                    if(ImGui.SmallButton("Put on the chosen character###gp_apply") && _presets.Apply(preset.Name, chosen))
                        _status = $"Put \"{preset.Name}\" on {chosen}.";
                    Tooltip("Every page that's on in this preset replaces the chosen character's settings for it.");
                    ImGui.SameLine();
                    if(ImGui.SmallButton("Overwrite with the chosen character###gp_overwrite"))
                    {
                        _presets.Save(chosen, preset.Name);
                        _status = $"\"{preset.Name}\" now holds what {chosen} has.";
                    }
                    Tooltip("Edit this preset: set the chosen character up the way you want, then press this to save it over the preset.");
                }
                ImGui.SameLine();
                using(ImRaii.Disabled(chosen.Length == 0))
                {
                    if(ImGui.SmallButton((chosen.Length > 0 ? $"Always on {chosen}" : "Always on the chosen character") + "###gp_always"))
                    {
                        _presets.RuleFor(chosen).Preset = preset.Name;
                        _config.Save();
                        _status = $"{chosen} now gets \"{preset.Name}\" whenever they turn up.";
                    }
                    Tooltip("Adds a rule below: whenever a player, NPC (a squadron member, say) or actor with this name is around, they get this preset.");
                }
                ImGui.SameLine();
                if(ImGui.SmallButton("Rename###gp_rename_btn"))
                {
                    _renaming = preset.Name;
                    _rename = preset.Name;
                }
                ImGui.SameLine();
                if(ImGui.SmallButton("x###gp_delete") && ImGui.GetIO().KeyCtrl)
                    remove = preset;
                Tooltip("Hold Ctrl and click to delete this preset.");
                if(open)
                {
                    // Every part it can hold: on / off, taken again from the chosen character, or taken out.
                    preset.Off ??= new();
                    string? drop = null;
                    foreach(var part in _presets.AllParts())
                    {
                        using var pid = ImRaii.PushId(part);
                        bool has = preset.Parts.ContainsKey(part);
                        if(has)
                        {
                            bool partOn = !preset.Off.Contains(part);
                            if(ImGui.Checkbox($"{part}###gp_part_on", ref partOn))
                            {
                                if(partOn)
                                    preset.Off.Remove(part);
                                else
                                    preset.Off.Add(part);
                                _config.Save();
                            }
                            Tooltip("On: put on with the preset. Off: kept in it, but left alone when it's put on.");
                        }
                        else
                            ImGui.TextDisabled($"   {part} (not in it)");
                        bool can = chosen.Length > 0 && _presets.PartsOf(chosen).Contains(part);
                        ImGui.SameLine(240 * ImGuiHelpers.GlobalScale);
                        using(ImRaii.Disabled(!can))
                        {
                            if(ImGui.SmallButton((has ? "take again from " : "add from ") + (chosen.Length > 0 ? chosen : "the chosen character") + "###gp_take")
                               && _presets.TakePart(preset, part, chosen))
                                _status = $"\"{preset.Name}\": {part} taken from {chosen}.";
                        }
                        if(has)
                        {
                            ImGui.SameLine();
                            if(ImGui.SmallButton("take out###gp_drop"))
                                drop = part;
                        }
                    }
                    if(drop != null)
                    {
                        preset.Parts.Remove(drop);
                        preset.Off.Remove(drop);
                        _config.Save();
                    }
                    ImGui.TreePop();
                }
            }
        }
        if(remove != null)
        {
            all.Remove(remove);
            _config.Save();
        }

        // ---- Rules: put a preset (and a look) on characters by themselves
        SeparatorText("Always on these characters and NPCs");
        ImGui.TextWrapped("Whenever a player, NPC (a squadron member, a companion, a retainer) or actor with this name is around, "
                          + "they get the global preset and the saved look. It goes on about a second after they turn up, every time.");
        var rules = _config.GlobalPresetRules ??= new();

        // Pick someone around you (players and NPCs alike).
        var around = _selection.ListCharacters().Where(c => !string.IsNullOrEmpty(c.Name)).ToList();
        var labels = ActorNames.Of(around);
        ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(260f * ImGuiHelpers.GlobalScale));
        using(var combo = ImRaii.Combo("###gp_rule_near", "Add someone around you..."))
        {
            if(combo.Success)
            {
                var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach(var c in around)
                {
                    if(!done.Add(c.Name!))
                        continue;
                    bool has = rules.Any(r => r.Character.Equals(c.Name, StringComparison.OrdinalIgnoreCase));
                    string label = (labels.TryGetValue(c.Address, out var l) ? l : c.Name!) + ActorNames.Distance(c) + (has ? "  (has a rule)" : "");
                    if(ImGui.Selectable($"{label}###gp_near_{c.Address}"))
                    {
                        if(!has)
                            rules.Add(new GlobalPresetRule { Character = c.Name!, Preset = all.FirstOrDefault()?.Name ?? "" });
                        _config.Save();
                    }
                }
            }
        }
        ImGui.SameLine();
        if(ImGui.Button("Put them on again now###gp_reapply"))
        {
            _presets.Reapply();
            _status = "Putting every rule on everyone around again.";
        }
        Tooltip("Puts each rule's preset and look on everyone here again, as if they'd just turned up.");
        ImGui.TextDisabled("Or type a name:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(200f * ImGuiHelpers.GlobalScale));
        ImGui.InputTextWithHint("###gp_rule_name", chosen.Length > 0 ? chosen : "Character or actor name", ref _ruleName, 64);
        ImGui.SameLine();
        if(ImGui.Button("Add###gp_rule_add"))
        {
            string who = _ruleName.Trim().Length > 0 ? _ruleName.Trim() : chosen;
            if(who.Length > 0)
            {
                rules.Add(new GlobalPresetRule { Character = who, Preset = all.FirstOrDefault()?.Name ?? "" });
                _ruleName = "";
                _config.Save();
            }
        }
        Tooltip("Adds a rule for this name (empty: the character chosen at the top).");

        int removeRule = -1;
        var looks = _config.AppearancePresets ?? new();
        using(var table = ImRaii.Table("###gp_rules", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            if(table.Success && rules.Count > 0)
            {
                ImGui.TableSetupColumn("On", ImGuiTableColumnFlags.WidthFixed, 30 * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("Character", ImGuiTableColumnFlags.WidthStretch, 1.4f);
                ImGui.TableSetupColumn("Global preset", ImGuiTableColumnFlags.WidthStretch, 1.4f);
                ImGui.TableSetupColumn("Look", ImGuiTableColumnFlags.WidthStretch, 1.4f);
                ImGui.TableSetupColumn("###del", ImGuiTableColumnFlags.WidthFixed, 30 * ImGuiHelpers.GlobalScale);
                ImGui.TableHeadersRow();
                for(int i = 0; i < rules.Count; i++)
                {
                    var rule = rules[i];
                    using var id = ImRaii.PushId(i);
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    bool on = rule.Enabled;
                    if(ImGui.Checkbox("###gp_rule_on", ref on))
                    {
                        rule.Enabled = on;
                        _config.Save();
                    }
                    ImGui.TableNextColumn();
                    string who = rule.Character;
                    ImGui.SetNextItemWidth(-1);
                    if(ImGui.InputText("###gp_rule_who", ref who, 64))
                        rule.Character = who;
                    if(ImGui.IsItemDeactivatedAfterEdit())
                        _config.Save();
                    ImGui.TableNextColumn();
                    ImGui.SetNextItemWidth(-1);
                    using(var combo = ImRaii.Combo("###gp_rule_preset", rule.Preset.Length > 0 ? rule.Preset : "(none)"))
                    {
                        if(combo.Success)
                        {
                            if(ImGui.Selectable("(none)", rule.Preset.Length == 0))
                            {
                                rule.Preset = "";
                                _config.Save();
                            }
                            foreach(var p in all)
                                if(ImGui.Selectable(p.Name, p.Name == rule.Preset))
                                {
                                    rule.Preset = p.Name;
                                    _config.Save();
                                }
                        }
                    }
                    ImGui.TableNextColumn();
                    ImGui.SetNextItemWidth(-1);
                    using(var combo = ImRaii.Combo("###gp_rule_look", rule.Look.Length > 0 ? rule.Look : "(keep their own)"))
                    {
                        if(combo.Success)
                        {
                            if(ImGui.Selectable("(keep their own)", rule.Look.Length == 0))
                            {
                                rule.Look = "";
                                _config.Save();
                            }
                            foreach(var l in looks)
                                if(ImGui.Selectable(l.Name, l.Name == rule.Look))
                                {
                                    rule.Look = l.Name;
                                    _config.Save();
                                }
                        }
                    }
                    ImGui.TableNextColumn();
                    if(ImGui.SmallButton("x###gp_rule_del") && ImGui.GetIO().KeyCtrl)
                        removeRule = i;
                    Tooltip("Hold Ctrl and click to delete this rule.");
                }
            }
        }
        if(removeRule >= 0)
        {
            rules.RemoveAt(removeRule);
            _config.Save();
        }
    }

    private static void SeparatorText(string text)
    {
        ImGui.Spacing();
        ImGui.TextColored(new System.Numerics.Vector4(0.95f, 0.85f, 0.55f, 1f), text);
        ImGui.Separator();
    }

    private static void Tooltip(string text)
    {
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip(text);
    }
}
