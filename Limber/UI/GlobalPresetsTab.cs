using System;
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
            ImGui.TextUnformatted($"{chosen} has: " + (parts.Count == 0 ? "nothing set yet" : string.Join(", ", parts)));
            ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
            ImGui.InputTextWithHint("###gp_name", "Name for the preset", ref _name, 48);
            ImGui.SameLine();
            using(ImRaii.Disabled(_name.Trim().Length == 0 || parts.Count == 0))
            {
                if(ImGui.Button("Save###gp_save"))
                {
                    _presets.Save(chosen, _name.Trim());
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
                ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
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
                bool open = ImGui.TreeNode($"{preset.Name}  ({preset.Parts.Count} part{(preset.Parts.Count == 1 ? "" : "s")})###gp_node");
                ImGui.SameLine();
                using(ImRaii.Disabled(chosen.Length == 0))
                {
                    if(ImGui.SmallButton("Put on the chosen character###gp_apply") && _presets.Apply(preset.Name, chosen))
                        _status = $"Put \"{preset.Name}\" on {chosen}.";
                    Tooltip("Every page's settings in this preset replace the chosen character's.");
                    ImGui.SameLine();
                    if(ImGui.SmallButton("Overwrite with the chosen character###gp_overwrite"))
                    {
                        _presets.Save(chosen, preset.Name);
                        _status = $"\"{preset.Name}\" now holds what {chosen} has.";
                    }
                    Tooltip("Edit this preset: set the chosen character up the way you want, then press this to save it over the preset.");
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
                    // What's in it; any part can be taken out.
                    string? drop = null;
                    foreach(var part in preset.Parts.Keys.OrderBy(k => k))
                    {
                        ImGui.BulletText(part);
                        ImGui.SameLine();
                        if(ImGui.SmallButton($"take out###gp_drop_{part}"))
                            drop = part;
                    }
                    if(drop != null)
                    {
                        preset.Parts.Remove(drop);
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
        SeparatorText("Put on by themselves");
        ImGui.TextDisabled("When a player or actor with this name comes into the scene (or is added), they get the preset and look.");
        var rules = _config.GlobalPresetRules ??= new();
        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
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
