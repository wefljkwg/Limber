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
/// The Appearance &amp; gear page (Scene), like Brio's and Anamnesis' appearance and equipment editors: race, clan,
/// sex and every customize value; each gear slot and weapon (picked from the game's items, or by model numbers) with
/// its two dyes; hat / weapons / visor / facewear; looks saved, shared as text, copied from another character, and
/// put back to the original. Only on your screen.
/// </summary>
public sealed class AppearanceTab
{
    private readonly Selection _selection;
    private readonly RigSettings _config;
    private readonly Dictionary<nint, AppearanceData> _original = new();
    private AppearanceData? _edit;
    private nint _editFor;
    private bool _dirty;
    private string _status = "";
    private readonly string[] _search = new string[12];
    private string _presetName = "";
    private int _glasses;

    public AppearanceTab(Selection selection, RigSettings config)
    {
        _selection = selection;
        _config = config;
        for(int i = 0; i < _search.Length; i++)
            _search[i] = "";
    }

    public void Draw()
    {
        var actor = _selection.Actor;
        if(!actor.IsSet)
        {
            ImGui.TextDisabled("Choose a character at the top of the window.");
            return;
        }
        ImGui.TextWrapped("Change the chosen character's appearance and gear, like Brio and Anamnesis: race, clan, face and every "
            + "customize value, each gear slot and weapon with its dyes. Only on your screen; the character goes back to its real "
            + "look whenever the game draws it from scratch. Best on GPose actors.");
        ImGui.Spacing();

        var now = Appearance.Read(actor.Address, actor.ObjectIndex);
        if(now == null)
        {
            ImGui.TextDisabled("This character can't be read right now.");
            return;
        }
        if(!_original.ContainsKey(actor.Address))
            _original[actor.Address] = now.Copy();
        if(_edit == null || _editFor != actor.Address || (!_dirty && !ImGui.IsAnyItemActive()))
        {
            _edit = now.Copy();
            _editFor = actor.Address;
            _dirty = false;
        }
        var look = _edit;

        // Apply / back / auto.
        using(ImRaii.Disabled(!_dirty))
        {
            if(ImGui.Button("Apply###app_apply"))
                Apply(actor, look);
        }
        ImGui.SameLine();
        bool live = _config.AppearanceLive;
        if(ImGui.Checkbox("Apply as I change###app_live", ref live))
        {
            _config.AppearanceLive = live;
            _config.Save();
        }
        Tooltip("Each change goes on as soon as you let go of it (race, clan, sex, face and colours redraw the character).");
        ImGui.SameLine();
        if(ImGui.Button("Back to its original look###app_original") && _original.TryGetValue(actor.Address, out var orig))
            Apply(actor, orig.Copy());
        Tooltip("The look it had when you first opened this page for it.");
        if(_status.Length > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(_status);
        }

        if(ImGui.CollapsingHeader("Body###app_body", ImGuiTreeNodeFlags.DefaultOpen))
            DrawBody(actor, look);
        if(ImGui.CollapsingHeader("Gear###app_gear", ImGuiTreeNodeFlags.DefaultOpen))
            DrawGear(actor, look);
        if(ImGui.CollapsingHeader("Looks###app_looks"))
            DrawLooks(actor, look);
    }

    private void Changed(ActorRef actor, AppearanceData look)
    {
        _dirty = true;
        if(_config.AppearanceLive && !ImGui.IsAnyItemActive())
            Apply(actor, look);
    }

    private void Apply(ActorRef actor, AppearanceData look)
    {
        _status = Appearance.Apply(actor.Address, actor.ObjectIndex, look);
        _dirty = false;
        _edit = null;
    }

    // ---- Race, clan, sex and the customize values

    private void DrawBody(ActorRef actor, AppearanceData look)
    {
        var cz = look.Customize;
        if(cz[2] == 4)
        {
            ImGui.TextDisabled("This is a child's body: its appearance can't be changed here.");
            return;
        }
        var race = Array.Find(Appearance.Races, r => r.Race == cz[0]);
        ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
        using(var combo = ImRaii.Combo("Race###app_race", race.Name ?? $"#{cz[0]}"))
        {
            if(combo.Success)
                foreach(var r in Appearance.Races)
                    if(ImGui.Selectable(r.Name, r.Race == cz[0]))
                    {
                        cz[0] = r.Race;
                        cz[4] = r.Clans[0].Tribe;
                        Changed(actor, look);
                    }
        }
        ImGui.SameLine();
        race = Array.Find(Appearance.Races, r => r.Race == cz[0]);
        var clans = race.Clans ?? [];
        string clan = clans.FirstOrDefault(c => c.Tribe == cz[4]).Name ?? $"#{cz[4]}";
        ImGui.SetNextItemWidth(170 * ImGuiHelpers.GlobalScale);
        using(var combo = ImRaii.Combo("Clan###app_clan", clan))
        {
            if(combo.Success)
                foreach(var c in clans)
                    if(ImGui.Selectable(c.Name, c.Tribe == cz[4]))
                    {
                        cz[4] = c.Tribe;
                        Changed(actor, look);
                    }
        }
        ImGui.SameLine();
        if(ImGui.RadioButton("Male###app_m", cz[1] == 0))
        {
            cz[1] = 0;
            Changed(actor, look);
        }
        ImGui.SameLine();
        if(ImGui.RadioButton("Female###app_f", cz[1] == 1))
        {
            cz[1] = 1;
            Changed(actor, look);
        }

        using var table = ImRaii.Table("###app_cust", 2, ImGuiTableFlags.SizingStretchSame);
        if(!table.Success)
            return;
        foreach(var (index, name, max) in Appearance.CustomizeFields)
        {
            ImGui.TableNextColumn();
            int v = cz[index];
            ImGui.SetNextItemWidth(110 * ImGuiHelpers.GlobalScale);
            if(ImGui.InputInt($"{name}###app_c{index}", ref v, 1, 10))
            {
                cz[index] = (byte)Math.Clamp(v, 0, max);
                _dirty = true;
            }
            if(ImGui.IsItemDeactivatedAfterEdit())
                Changed(actor, look);
        }
    }

    // ---- Gear and weapons

    private void DrawGear(ActorRef actor, AppearanceData look)
    {
        var toggles = Appearance.Toggles(actor.Address, actor.ObjectIndex);
        bool hat = toggles.Hat, weapons = toggles.Weapons, visor = toggles.Visor;
        if(ImGui.Checkbox("Hide headgear###app_hat", ref hat))
            Appearance.HideHeadgear(actor.Address, actor.ObjectIndex, hat);
        ImGui.SameLine();
        if(ImGui.Checkbox("Hide weapons###app_weap", ref weapons))
            Appearance.HideWeapons(actor.Address, actor.ObjectIndex, weapons);
        ImGui.SameLine();
        if(ImGui.Checkbox("Visor###app_visor", ref visor))
            Appearance.SetVisor(actor.Address, actor.ObjectIndex, visor);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90 * ImGuiHelpers.GlobalScale);
        if(ImGui.InputInt("Facewear###app_glasses", ref _glasses, 1, 10, flags: ImGuiInputTextFlags.EnterReturnsTrue))
            Appearance.SetGlasses(actor.Address, actor.ObjectIndex, (ushort)Math.Clamp(_glasses, 0, ushort.MaxValue));
        Tooltip("Facewear (glasses) by its number from the game (0 = none); press Enter.");

        using var table = ImRaii.Table("###app_gear_table", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp);
        if(!table.Success)
            return;
        ImGui.TableSetupColumn("Slot", ImGuiTableColumnFlags.WidthFixed, 80 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 2f);
        ImGui.TableSetupColumn("Model", ImGuiTableColumnFlags.WidthFixed, 150 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Dyes", ImGuiTableColumnFlags.WidthStretch, 1.2f);
        for(int s = 0; s < 12; s++)
        {
            bool weapon = s >= 10;
            using var id = ImRaii.PushId(s);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(weapon ? (s == 10 ? "Main hand" : "Off hand") : Appearance.GearSlots[s]);

            ulong value = weapon ? look.Weapons[s - 10] : look.Gear[s];
            // the item it is (by model), and a picker
            ImGui.TableNextColumn();
            string current = ItemName(s, value);
            ImGui.SetNextItemWidth(-1);
            using(var combo = ImRaii.Combo("###item", current, ImGuiComboFlags.HeightLarge))
            {
                if(combo.Success)
                {
                    ImGui.SetNextItemWidth(-1);
                    ImGui.InputTextWithHint("###search", "Search...", ref _search[s], 64);
                    if(!weapon && ImGui.Selectable("(nothing)"))
                    {
                        look.Gear[s] = 0;
                        Changed(actor, look);
                    }
                    int shown = 0;
                    foreach(var item in Appearance.Items)
                    {
                        if(item.Slot != s)
                            continue;
                        if(_search[s].Length > 0 && !item.Name.Contains(_search[s], StringComparison.OrdinalIgnoreCase))
                            continue;
                        if(++shown > 300)
                        {
                            ImGui.TextDisabled("(more: search to narrow it down)");
                            break;
                        }
                        if(ImGui.Selectable($"{item.Name}###i{item.Id}"))
                        {
                            if(weapon)
                                look.Weapons[s - 10] = Appearance.WeaponValue(item.Model, value);
                            else
                                look.Gear[s] = Appearance.GearValue(item, value);
                            Changed(actor, look);
                        }
                    }
                }
            }

            // the model numbers (for mods that add items, or anything the list doesn't have)
            ImGui.TableNextColumn();
            if(weapon)
            {
                var w = new FFXIVClientStructs.FFXIV.Client.Game.Character.WeaponModelId { Value = value };
                int[] parts = [w.Id, w.Type, w.Variant];
                float third = (ImGui.GetContentRegionAvail().X - 8f) / 3f;
                bool edited = false, done = false;
                for(int k = 0; k < 3; k++)
                {
                    if(k > 0)
                        ImGui.SameLine(0, 4f);
                    ImGui.SetNextItemWidth(third);
                    edited |= ImGui.InputInt($"###wmodel{k}", ref parts[k], 0, 0);
                    done |= ImGui.IsItemDeactivatedAfterEdit();
                    Tooltip("Weapon model: set, type, variant.");
                }
                if(edited)
                {
                    w.Id = (ushort)Math.Clamp(parts[0], 0, ushort.MaxValue);
                    w.Type = (ushort)Math.Clamp(parts[1], 0, ushort.MaxValue);
                    w.Variant = (ushort)Math.Clamp(parts[2], 0, ushort.MaxValue);
                    look.Weapons[s - 10] = w.Value;
                    _dirty = true;
                }
                if(done)
                    Changed(actor, look);
            }
            else
            {
                var g = new FFXIVClientStructs.FFXIV.Client.Game.Character.EquipmentModelId { Value = value };
                int[] parts = [g.Id, g.Variant];
                float half = (ImGui.GetContentRegionAvail().X - 4f) / 2f;
                bool edited = false, done = false;
                for(int k = 0; k < 2; k++)
                {
                    if(k > 0)
                        ImGui.SameLine(0, 4f);
                    ImGui.SetNextItemWidth(half);
                    edited |= ImGui.InputInt($"###gmodel{k}", ref parts[k], 0, 0);
                    done |= ImGui.IsItemDeactivatedAfterEdit();
                    Tooltip("Gear model: model number, variant.");
                }
                if(edited)
                {
                    g.Id = (ushort)Math.Clamp(parts[0], 0, ushort.MaxValue);
                    g.Variant = (byte)Math.Clamp(parts[1], 0, 255);
                    look.Gear[s] = g.Value;
                    _dirty = true;
                }
                if(done)
                    Changed(actor, look);
            }

            // the two dyes
            ImGui.TableNextColumn();
            for(int d = 0; d < 2; d++)
            {
                byte stain = weapon
                    ? (d == 0 ? new FFXIVClientStructs.FFXIV.Client.Game.Character.WeaponModelId { Value = value }.Stain0 : new FFXIVClientStructs.FFXIV.Client.Game.Character.WeaponModelId { Value = value }.Stain1)
                    : (d == 0 ? new FFXIVClientStructs.FFXIV.Client.Game.Character.EquipmentModelId { Value = value }.Stain0 : new FFXIVClientStructs.FFXIV.Client.Game.Character.EquipmentModelId { Value = value }.Stain1);
                var dye = Appearance.Dyes.FirstOrDefault(x => x.Id == stain);
                if(d == 1)
                    ImGui.SameLine();
                ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X * (d == 0 ? 0.5f : 1f) - (d == 0 ? 4f : 0f));
                using var combo = ImRaii.Combo($"###dye{d}", dye?.Name ?? $"#{stain}");
                if(!combo.Success)
                    continue;
                foreach(var option in Appearance.Dyes)
                {
                    if(option.Id != 0)
                    {
                        var c = option.Color;
                        var color = new Vector4(((c >> 16) & 0xFF) / 255f, ((c >> 8) & 0xFF) / 255f, (c & 0xFF) / 255f, 1f);
                        ImGui.ColorButton($"###sw{option.Id}", color, ImGuiColorEditFlags.NoTooltip, new Vector2(14, 14) * ImGuiHelpers.GlobalScale);
                        ImGui.SameLine();
                    }
                    if(ImGui.Selectable($"{option.Name}###st{option.Id}", option.Id == stain))
                    {
                        if(weapon)
                        {
                            var w = new FFXIVClientStructs.FFXIV.Client.Game.Character.WeaponModelId { Value = value };
                            if(d == 0) w.Stain0 = option.Id; else w.Stain1 = option.Id;
                            look.Weapons[s - 10] = w.Value;
                        }
                        else
                        {
                            var g = new FFXIVClientStructs.FFXIV.Client.Game.Character.EquipmentModelId { Value = value };
                            if(d == 0) g.Stain0 = option.Id; else g.Stain1 = option.Id;
                            look.Gear[s] = g.Value;
                        }
                        Changed(actor, look);
                    }
                }
            }
        }
    }

    private static string ItemName(int slot, ulong value)
    {
        if(value == 0)
            return "(nothing)";
        if(slot >= 10)
        {
            var w = new FFXIVClientStructs.FFXIV.Client.Game.Character.WeaponModelId { Value = value };
            foreach(var item in Appearance.Items)
                if(item.Slot == slot && (ushort)(item.Model & 0xFFFF) == w.Id && (ushort)((item.Model >> 16) & 0xFFFF) == w.Type
                   && (ushort)((item.Model >> 32) & 0xFFFF) == w.Variant)
                    return item.Name;
            return $"Weapon {w.Id}-{w.Type}-{w.Variant}";
        }
        var g = new FFXIVClientStructs.FFXIV.Client.Game.Character.EquipmentModelId { Value = value };
        foreach(var item in Appearance.Items)
            if(item.Slot == slot && (ushort)(item.Model & 0xFFFF) == g.Id && (byte)((item.Model >> 16) & 0xFF) == g.Variant)
                return item.Name;
        return $"Model {g.Id}, variant {g.Variant}";
    }

    // ---- Saved looks, sharing, copying another character

    private void DrawLooks(ActorRef actor, AppearanceData look)
    {
        var presets = _config.AppearancePresets ??= new();
        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("###app_preset_name", "Name for this look", ref _presetName, 48);
        ImGui.SameLine();
        if(ImGui.Button("Save this look###app_save") && _presetName.Trim().Length > 0)
        {
            var copy = look.Copy();
            copy.Name = _presetName.Trim();
            presets.RemoveAll(p => p.Name == copy.Name);
            presets.Add(copy);
            _config.Save();
            _presetName = "";
        }
        ImGui.SameLine();
        if(ImGui.Button("Copy as text###app_copy"))
            ImGui.SetClipboardText(Newtonsoft.Json.JsonConvert.SerializeObject(look));
        ImGui.SameLine();
        if(ImGui.Button("Paste###app_paste"))
        {
            try
            {
                var pasted = Newtonsoft.Json.JsonConvert.DeserializeObject<AppearanceData>(ImGui.GetClipboardText() ?? "");
                if(pasted is { Customize.Length: 26 })
                    Apply(actor, pasted);
            }
            catch
            {
                _status = "That isn't a look copied from here.";
            }
        }

        // Another character's look.
        var others = _selection.ListCharacters().Where(c => c.Address != actor.Address).ToList();
        if(others.Count > 0)
        {
            ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
            using var combo = ImRaii.Combo("Copy from###app_from", "another character...");
            if(combo.Success)
                foreach(var other in others)
                {
                    if(!ImGui.Selectable($"{other.Label}###app_from_{other.ObjectIndex}"))
                        continue;
                    var theirs = Appearance.Read(other.Address, other.ObjectIndex);
                    if(theirs != null)
                        Apply(actor, theirs);
                }
        }

        ImGui.Spacing();
        int remove = -1;
        for(int i = 0; i < presets.Count; i++)
        {
            using var id = ImRaii.PushId(i);
            ImGui.TextUnformatted(presets[i].Name);
            ImGui.SameLine();
            if(ImGui.SmallButton("Whole look"))
                Apply(actor, presets[i].Copy());
            ImGui.SameLine();
            if(ImGui.SmallButton("Body only"))
                _status = Appearance.Apply(actor.Address, actor.ObjectIndex, presets[i].Copy(), customize: true, gear: false, weapons: false);
            ImGui.SameLine();
            if(ImGui.SmallButton("Gear only"))
                _status = Appearance.Apply(actor.Address, actor.ObjectIndex, presets[i].Copy(), customize: false, gear: true, weapons: true);
            ImGui.SameLine();
            if(ImGui.SmallButton("x") && ImGui.GetIO().KeyCtrl)
                remove = i;
            Tooltip("Hold Ctrl and click to delete this look.");
        }
        if(remove >= 0)
        {
            presets.RemoveAt(remove);
            _config.Save();
        }
    }

    private static void Tooltip(string text)
    {
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip(text);
    }
}
