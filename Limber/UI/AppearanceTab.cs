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

    private readonly Dalamud.Interface.ImGuiFileDialog.FileDialogManager _files = new();

    public void Draw()
    {
        _files.Draw();
        var actor = _selection.Actor;
        if(!actor.IsSet)
        {
            ImGui.TextDisabled("Choose a character at the top of the window.");
            return;
        }

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
        if(_applySoon && _dirty && !ImGui.IsAnyItemActive() && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            _applySoon = false;
            Apply(actor, look);
        }

        // Which Penumbra collection draws this character (like Brio).
        DrawCollection(actor);

        // Whole outfits.
        if(ImGui.Button("All smallclothes###app_out_small"))
            _status = Appearance.PutOn(actor.Address, actor.ObjectIndex, Appearance.Outfit.Smallclothes);
        Tooltip("Takes off all gear: smallclothes, nothing on the head, no accessories (weapons kept).");
        ImGui.SameLine();
        if(ImGui.Button("All smallclothes NPC###app_out_npc"))
            _status = Appearance.PutOn(actor.Address, actor.ObjectIndex, Appearance.Outfit.NpcSmallclothes);
        Tooltip("The NPCs' smallclothes on body, hands, legs and feet; nothing on the head, no accessories.");
        ImGui.SameLine();
        if(ImGui.Button("All emperor's###app_out_emp"))
            _status = Appearance.PutOn(actor.Address, actor.ObjectIndex, Appearance.Outfit.Emperors);
        Tooltip("The whole Emperor's New set (hat, robe, gloves, breeches, boots and accessories).");

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
        if(ImGui.CollapsingHeader("NPC looks###app_npcs"))
            DrawNpcs(actor);
    }

    private void Changed(ActorRef actor, AppearanceData look)
    {
        _dirty = true;
        // (put on once nothing's being dragged or typed in: a pick made while a box was still active used to be
        // left waiting for Apply, so it looked like it did nothing)
        _applySoon = _config.AppearanceLive;
    }

    private bool _applySoon;

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

        // Switches: hair highlights, and eyes of two colours.
        bool highlights = (cz[7] & 0x80) != 0;
        if(ImGui.Checkbox("Highlights###app_highlights", ref highlights))
        {
            cz[7] = (byte)(highlights ? cz[7] | 0x80 : cz[7] & 0x7F);
            Changed(actor, look);
        }
        ImGui.SameLine();
        if(_heteroFor != actor.Address)
        {
            _heteroFor = actor.Address;
            _hetero = cz[15] != cz[9];
        }
        bool hetero = _hetero;
        if(ImGui.Checkbox("Heterochromia###app_hetero", ref hetero))
        {
            _hetero = hetero;
            if(!hetero && cz[15] != cz[9])
            {
                cz[15] = cz[9];
                Changed(actor, look);
            }
        }
        Tooltip("Each eye its own colour. Off: both eyes take the right eye's colour.");

        using var table = ImRaii.Table("###app_cust", 2, ImGuiTableFlags.SizingStretchSame);
        if(!table.Success)
            return;
        foreach(var (index, name, max) in Appearance.CustomizeFields)
        {
            if(index == 7 || (index == 15 && !_hetero))
                continue; // (the switches above)
            ImGui.TableNextColumn();
            string label = index == 9 && !_hetero ? "Eye colour" : name;
            int v = cz[index];
            bool edited = false, done = false;
            if(index == 6)
                (edited, done) = HairPicker(cz, ref v, label);
            else if(Limber.Rig.CustomizePalette.IsColour(index))
                (edited, done) = ColourPicker(cz, index, ref v, label);
            else if(max == 100)
            {
                // (values on a scale: a slider)
                ImGui.SetNextItemWidth(150 * ImGuiHelpers.GlobalScale);
                edited = ImGui.SliderInt($"{label}###app_c{index}", ref v, 0, 100);
                done = ImGui.IsItemDeactivatedAfterEdit();
            }
            else
            {
                ImGui.SetNextItemWidth(110 * ImGuiHelpers.GlobalScale);
                edited = ImGui.InputInt($"{label}###app_c{index}", ref v, 1, 10);
                done = ImGui.IsItemDeactivatedAfterEdit();
            }
            if(edited)
            {
                cz[index] = (byte)Math.Clamp(v, 0, max);
                if(index == 9 && !_hetero)
                    cz[15] = cz[9];
                _dirty = true;
            }
            if(done)
                Changed(actor, look);
        }
    }

    private bool _hetero;
    private nint _heteroFor;

    // A colour: a swatch of the current one, a palette of the game's own choices on click, and the number.
    private (bool Edited, bool Done) ColourPicker(byte[] cz, int index, ref int v, string label)
    {
        var palette = Limber.Rig.CustomizePalette.Colours(index, cz[4], cz[1]);
        bool edited = false, done = false;
        float size = ImGui.GetFrameHeight();
        uint now = 0xFF808080;
        foreach(var (value, colour) in palette)
            if(value == v)
                now = colour;
        if(ImGui.ColorButton($"###app_sw{index}", ImGui.ColorConvertU32ToFloat4(now), ImGuiColorEditFlags.NoTooltip, new System.Numerics.Vector2(size * 1.6f, size)) && palette.Length > 0)
            ImGui.OpenPopup($"###app_pal{index}");
        Tooltip(palette.Length > 0 ? "Click for the colours." : "The game's colours couldn't be read: use the number.");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(80 * ImGuiHelpers.GlobalScale);
        if(ImGui.InputInt($"{label}###app_c{index}", ref v, 1, 10))
            edited = true;
        done |= ImGui.IsItemDeactivatedAfterEdit();
        if(ImGui.BeginPopup($"###app_pal{index}"))
        {
            // The creator's grid: 8 across, like the game's own palette
            int columns = 8, i = 0;
            float cell = 22f * ImGuiHelpers.GlobalScale;
            foreach(var (value, colour) in palette)
            {
                if(i++ % columns != 0)
                    ImGui.SameLine(0, 2f);
                bool selected = value == v;
                if(selected)
                    ImGui.PushStyleColor(ImGuiCol.Border, 0xFFFFFFFF);
                if(ImGui.ColorButton($"###app_pc{index}_{value}", ImGui.ColorConvertU32ToFloat4(colour), ImGuiColorEditFlags.NoTooltip | (selected ? ImGuiColorEditFlags.None : ImGuiColorEditFlags.NoBorder), new System.Numerics.Vector2(cell, cell)))
                {
                    v = value;
                    edited = done = true;
                    ImGui.CloseCurrentPopup();
                }
                if(selected)
                    ImGui.PopStyleColor();
                if(ImGui.IsItemHovered())
                    ImGui.SetTooltip($"#{value}");
            }
            ImGui.EndPopup();
        }
        return (edited, done);
    }

    // The hairstyle: its preview icon, and on click every hairstyle this clan and sex has, as pictures.
    private (bool Edited, bool Done) HairPicker(byte[] cz, ref int v, string label)
    {
        var styles = Limber.Rig.CustomizePalette.Hairstyles(cz[4], cz[1]);
        bool edited = false, done = false;
        float size = ImGui.GetFrameHeight();
        uint icon = 0;
        foreach(var (value, i) in styles)
            if(value == v)
                icon = i;
        var wrap = icon != 0 ? GameIcons.Get(icon) : null;
        bool open;
        if(wrap != null)
        {
            ImGui.Image(wrap.Handle, new System.Numerics.Vector2(size, size));
            open = ImGui.IsItemClicked();
        }
        else
            open = ImGui.Button($"...###app_hair_btn", new System.Numerics.Vector2(size * 1.6f, size));
        if(open && styles.Length > 0)
            ImGui.OpenPopup("###app_hair_pick");
        Tooltip(styles.Length > 0 ? "Click for the hairstyles." : "The hairstyles couldn't be read: use the number.");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(80 * ImGuiHelpers.GlobalScale);
        if(ImGui.InputInt($"{label}###app_c6", ref v, 1, 10))
            edited = true;
        done |= ImGui.IsItemDeactivatedAfterEdit();
        if(ImGui.BeginPopup("###app_hair_pick"))
        {
            int columns = 6, n = 0;
            float cell = 56f * ImGuiHelpers.GlobalScale;
            foreach(var (value, i) in styles)
            {
                if(n++ % columns != 0)
                    ImGui.SameLine();
                var w = GameIcons.Get(i);
                using var id = ImRaii.PushId(value);
                bool selected = value == v;
                bool picked;
                var at = ImGui.GetCursorScreenPos();
                if(w != null)
                {
                    ImGui.Image(w.Handle, new System.Numerics.Vector2(cell, cell));
                    picked = ImGui.IsItemClicked();
                }
                else
                    picked = ImGui.Button($"{value}", new System.Numerics.Vector2(cell, cell));
                if(selected)
                {
                    var dl = ImGui.GetWindowDrawList();
                    var b = at + new System.Numerics.Vector2(cell, cell);
                    dl.AddLine(at, new System.Numerics.Vector2(b.X, at.Y), 0xFF40D0FF, 2f);
                    dl.AddLine(new System.Numerics.Vector2(b.X, at.Y), b, 0xFF40D0FF, 2f);
                    dl.AddLine(b, new System.Numerics.Vector2(at.X, b.Y), 0xFF40D0FF, 2f);
                    dl.AddLine(new System.Numerics.Vector2(at.X, b.Y), at, 0xFF40D0FF, 2f);
                }
                if(ImGui.IsItemHovered())
                    ImGui.SetTooltip($"Hairstyle {value}");
                if(picked)
                {
                    v = value;
                    edited = done = true;
                    ImGui.CloseCurrentPopup();
                }
            }
            ImGui.EndPopup();
        }
        return (edited, done);
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
                        if(!Appearance.Fits(item, s))
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
            if(Appearance.Fits(item, slot) && (ushort)(item.Model & 0xFFFF) == g.Id && (byte)((item.Model >> 16) & 0xFF) == g.Variant)
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

        ImGui.SameLine();
        if(ImGui.Button("Import a .chara file...###app_chara"))
        {
            var who = actor;
            _files.OpenFileDialog("Import a look", "Look files{.chara},.*", (ok, paths) =>
            {
                if(!ok || paths.Count == 0)
                    return;
                try
                {
                    var name = System.IO.Path.GetFileNameWithoutExtension(paths[0]);
                    var read = Limber.Rig.CharaFile.Read(System.IO.File.ReadAllText(paths[0]), name, out var error);
                    if(read == null)
                    {
                        _status = error;
                        return;
                    }
                    Apply(who, read);
                    _status = $"Imported {name}.";
                }
                catch(Exception e)
                {
                    _status = "Couldn't read that file: " + e.Message;
                }
            }, 1, null, true);
        }
        Tooltip("Puts a look saved as a .chara file (the look files other posing tools save) on this character:\n"
            + "body, face, hair, colours, gear and weapons. Save it here afterwards to keep it with your looks.");

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

            // As an actor: the name it gets, and a global preset put on it.
            ImGui.Indent();
            string actorName = presets[i].ActorName ?? "";
            ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
            if(ImGui.InputTextWithHint("###app_actor_name", "Actor name", ref actorName, 48))
                presets[i].ActorName = actorName;
            if(ImGui.IsItemDeactivatedAfterEdit())
                _config.Save();
            Tooltip("An actor added with this look (Actors page) gets this name, and keeps this look. Empty: Limber 1, Limber 2...");
            ImGui.SameLine();
            var globals = _config.GlobalPresets ?? new();
            string attached = presets[i].GlobalPreset ?? "";
            ImGui.SetNextItemWidth(180 * ImGuiHelpers.GlobalScale);
            using(var gp = ImRaii.Combo("###app_actor_preset", attached.Length > 0 ? attached : "No global preset"))
            {
                if(gp.Success)
                {
                    if(ImGui.Selectable("No global preset", attached.Length == 0))
                    {
                        presets[i].GlobalPreset = "";
                        _config.Save();
                    }
                    foreach(var g in globals)
                        if(ImGui.Selectable(g.Name, g.Name == attached))
                        {
                            presets[i].GlobalPreset = g.Name;
                            _config.Save();
                        }
                }
            }
            Tooltip("A global preset (Global presets page) put on an actor added with this look.");
            ImGui.Unindent();
        }
        if(remove >= 0)
        {
            presets.RemoveAt(remove);
            _config.Save();
        }
    }

    private string _npcSearch = "";
    private Dictionary<Guid, string>? _collections;
    private long _collectionsAt;

    // The Penumbra collection the chosen character is drawn with; another one picked for it (Brio style), or back to
    // Penumbra's own rules. Redrawn either way.
    private void DrawCollection(ActorRef actor)
    {
        try
        {
            long now = Environment.TickCount64;
            if(_collections == null || now - _collectionsAt > 5000)
            {
                _collections = Plugin.PluginInterface.GetIpcSubscriber<Dictionary<Guid, string>>("Penumbra.GetCollections.V5").InvokeFunc();
                _collectionsAt = now;
            }
            var current = Plugin.PluginInterface.GetIpcSubscriber<int, (bool, bool, (Guid, string))>("Penumbra.GetCollectionForObject.V5")
                .InvokeFunc(actor.ObjectIndex);
            var (valid, own, (currentId, currentName)) = current;
            ImGui.SetNextItemWidth(260 * ImGuiHelpers.GlobalScale);
            using(var combo = ImRaii.Combo("Mod collection###app_collection", valid ? currentName + (own ? "" : " (the default)") : "(none)"))
            {
                if(combo.Success)
                {
                    if(ImGui.Selectable("(the default)###app_coll_none", !own))
                        SetCollection(actor, null);
                    foreach(var (id, name) in _collections.OrderBy(c => c.Value, StringComparer.OrdinalIgnoreCase))
                        if(ImGui.Selectable($"{name}###app_coll_{id}", own && id == currentId))
                            SetCollection(actor, id);
                }
            }
            Tooltip("Which mod collection this character is drawn with. Picking one gives this character its own;\n"
                + "\"(the default)\" takes that away again. It's redrawn either way.");
        }
        catch
        {
            ImGui.TextDisabled("Mod collection: the mod manager isn't available.");
        }
    }

    private void SetCollection(ActorRef actor, Guid? id)
    {
        try
        {
            var (ec, _) = Plugin.PluginInterface.GetIpcSubscriber<int, Guid?, bool, bool, (int, (Guid, string)?)>("Penumbra.SetCollectionForObject.V5")
                .InvokeFunc(actor.ObjectIndex, id, true, true);
            Plugin.PluginInterface.GetIpcSubscriber<int, int, object?>("Penumbra.RedrawObject.V5").InvokeAction(actor.ObjectIndex, 0);
            _status = ec is 0 or 1 ? (id == null ? "Back to the default (redrawn)." : "Collection set (redrawn).") : $"The mod manager couldn't set it (error {ec}).";
        }
        catch(Exception e)
        {
            _status = "The mod manager couldn't set it: " + e.Message;
        }
    }

    private void DrawNpcs(ActorRef actor)
    {
        DrawNpcPicker(ref _npcSearch, "app_npc", npc =>
        {
            ImGui.SameLine();
            if(ImGui.SmallButton("Put on"))
                Apply(actor, npc.Look.Copy());
            Tooltip("The NPC's whole look on the chosen character.");
            ImGui.SameLine();
            if(ImGui.SmallButton("Save"))
            {
                var presets = _config.AppearancePresets ??= new();
                var copy = npc.Look.Copy();
                copy.Name = npc.Name;
                presets.RemoveAll(p => p.Name == copy.Name);
                presets.Add(copy);
                _config.Save();
                _status = $"Saved \"{npc.Name}\" under Looks.";
            }
            Tooltip("Keeps it under Looks (and for new actors on the Actors page).");
        });
    }

    /// <summary>A searchable list of the game's NPC looks; <paramref name="row"/> draws each one's buttons.</summary>
    public static void DrawNpcPicker(ref string search, string id, Action<Appearance.NpcLook> row)
    {
        var npcs = Appearance.Npcs;
        if(!Appearance.NpcsReady)
        {
            ImGui.TextDisabled("Reading the NPCs from the game's data...");
            return;
        }
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint($"###{id}_search", $"Search {npcs.Count} NPCs by name", ref search, 64);
        string q = search.Trim();
        if(q.Length < 2)
        {
            ImGui.TextDisabled("Type at least two letters of a name.");
            return;
        }
        int shown = 0, total = 0;
        using var child = ImRaii.Child($"###{id}_list", new Vector2(-1, 220 * ImGuiHelpers.GlobalScale), true);
        if(!child.Success)
            return;
        foreach(var npc in npcs)
        {
            if(npc.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            total++;
            if(shown >= 150)
                continue;
            shown++;
            using var pid = ImRaii.PushId((int)npc.Id);
            ImGui.TextUnformatted(npc.Name);
            ImGui.SameLine();
            ImGui.TextDisabled($"#{npc.Id}");
            row(npc);
        }
        if(total > shown)
            ImGui.TextDisabled($"... and {total - shown} more: type more of the name.");
        if(total == 0)
            ImGui.TextDisabled("No NPC by that name.");
    }

    private static void Tooltip(string text)
    {
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip(text);
    }
}
