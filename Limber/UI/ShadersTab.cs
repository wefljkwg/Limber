using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Limber.Rig;

namespace Limber.UI;

/// <summary>The Shaders page (Scene): a character's materials drawn with another of the game's shaders and other
/// settings, on that character only (players, NPCs and actors), and saved as presets to put on anyone.</summary>
public sealed class ShadersTab
{
    private readonly Selection _selection;
    private readonly RigSettings _config;
    private readonly Shaders _shaders;
    private string _presetName = "";
    private string _status = "";

    public ShadersTab(Selection selection, RigSettings config, Shaders shaders)
    {
        _selection = selection;
        _config = config;
        _shaders = shaders;
    }

    private static void Tooltip(string text)
    {
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip(text);
    }

    public void Draw()
    {
        var looks = _config.ShaderLooks ??= new();
        var presets = _config.ShaderPresets ??= new();
        ImGui.TextWrapped("Draw a character's materials with another of the game's shaders, or change their colour, brightness, glow, "
                          + "shine, reflections and bumps. Only that character changes (players, NPCs, actors), and only on your screen. "
                          + "Each change draws them again once to take it (a moment after you stop dragging).");

        // Add someone.
        var around = _selection.ListCharacters().Where(c => !string.IsNullOrEmpty(c.Name)).ToList();
        var labels = ActorNames.Of(around);
        ImGui.SetNextItemWidth(Fit.Fixed(260f * ImGuiHelpers.GlobalScale));
        using(var combo = ImRaii.Combo("###sh_add", "Add someone around you..."))
        {
            if(combo.Success)
            {
                var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach(var c in around)
                {
                    if(!done.Add(c.Name!))
                        continue;
                    string label = (labels.TryGetValue(c.Address, out var l) ? l : c.Name!) + ActorNames.Distance(c);
                    if(ImGui.Selectable($"{label}###sh_near_{c.Address}"))
                    {
                        looks.Add(new ShaderLook { Character = c.Name! });
                        _config.Save();
                    }
                }
            }
        }
        if(_selection.Actor.IsSet && !string.IsNullOrEmpty(_selection.Actor.Name))
        {
            ImGui.SameLine();
            if(ImGui.Button($"Add {_selection.Actor.Name}###sh_add_sel"))
            {
                looks.Add(new ShaderLook { Character = _selection.Actor.Name });
                _config.Save();
            }
        }
        if(_shaders.Status.Length > 0 || _status.Length > 0)
            ImGui.TextDisabled(_status.Length > 0 ? _status : _shaders.Status);

        if(looks.Count == 0)
        {
            ImGui.TextDisabled("Nobody has a shader look yet.");
            DrawPresets(null, presets);
            return;
        }

        ShaderLook? remove = null;
        ShaderLook? chosen = null;
        foreach(var group in looks.GroupBy(l => l.Character, StringComparer.OrdinalIgnoreCase).ToList())
        {
            ImGui.Separator();
            ImGui.TextUnformatted(group.Key);
            ImGui.SameLine();
            if(ImGui.SmallButton($"+ Part###sh_addpart_{group.Key}"))
            {
                var used = group.Select(l => l.Part).ToHashSet(StringComparer.OrdinalIgnoreCase);
                looks.Add(new ShaderLook { Character = group.Key, Part = Shaders.Parts.FirstOrDefault(p => !used.Contains(p)) ?? "All" });
                _config.Save();
            }
            Tooltip("Another look on the same character for one part (skin, hair, face or gear): a part's own look wins over All.");
            ImGui.SameLine();
            if(ImGui.SmallButton($"Draw again###sh_redraw_{group.Key}"))
                _shaders.Redraw(group.Key);
            Tooltip("Draws them again (if a change didn't show).");
            foreach(var look in group)
            {
                using var id = ImRaii.PushId($"sh_{looks.IndexOf(look)}");
                if(DrawLook(look, ref remove))
                    _config.Save();
                if(ImGui.SmallButton("Use for presets###sh_pick"))
                    chosen = look;
                Tooltip("Save this look as a preset below, or load a preset onto it.");
                ImGui.Spacing();
            }
        }
        if(remove != null)
        {
            looks.Remove(remove);
            _config.Save();
        }
        if(chosen != null)
            _picked = chosen;
        if(_picked != null && !looks.Contains(_picked))
            _picked = null;
        DrawPresets(_picked, presets);
    }

    private ShaderLook? _picked;

    // One look's settings. True when something changed.
    private bool DrawLook(ShaderLook look, ref ShaderLook? remove)
    {
        bool changed = false;
        bool on = look.Enabled;
        if(ImGui.Checkbox("On###sh_on", ref on)) { look.Enabled = on; changed = true; }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(Fit.Fixed(110f * ImGuiHelpers.GlobalScale));
        using(var combo = ImRaii.Combo("Part###sh_part", look.Part))
        {
            if(combo.Success)
                foreach(var p in Shaders.Parts)
                    if(ImGui.Selectable(p, p == look.Part)) { look.Part = p; changed = true; }
        }
        Tooltip("Which of their materials: All, Skin (the body's and face's skin), Hair (with ears and tail), Face (eyes, the rest of the face) or Gear (clothes, accessories, weapons).");
        ImGui.SameLine();
        if(ImGui.SmallButton("Remove###sh_rm"))
            remove = look;

        var choice = Shaders.ShaderChoices.FirstOrDefault(c => c.File.Equals(look.Shader ?? "", StringComparison.OrdinalIgnoreCase));
        ImGui.SetNextItemWidth(Fit.Label(160f * ImGuiHelpers.GlobalScale));
        using(var combo = ImRaii.Combo("Shader###sh_shader", choice.Label ?? "Its own"))
        {
            if(combo.Success)
                foreach(var c in Shaders.ShaderChoices)
                {
                    if(ImGui.Selectable(c.Label, c.File == (look.Shader ?? ""))) { look.Shader = c.File; changed = true; }
                    Tooltip(c.Tip);
                }
        }
        Tooltip("Another of the game's shaders for these materials. Not every material suits every shader: if one comes out wrong, go back to Its own.");

        var tint = new Vector3(look.Tint?[0] ?? 1f, look.Tint?[1] ?? 1f, look.Tint?[2] ?? 1f);
        if(ImGui.ColorEdit3("Colour###sh_tint", ref tint, ImGuiColorEditFlags.NoInputs)) { look.Tint = [tint.X, tint.Y, tint.Z]; changed = true; }
        Tooltip("Multiplies their colour (white: as it is).");
        ImGui.SameLine();
        changed |= Slider("Brightness###sh_bright", look.Brightness, v => look.Brightness = v, 0f, 3f, "Darker (below 1) or brighter (above).");
        var glow = new Vector3(look.Glow?[0] ?? 1f, look.Glow?[1] ?? 1f, look.Glow?[2] ?? 1f);
        if(ImGui.ColorEdit3("Glow###sh_glowc", ref glow, ImGuiColorEditFlags.NoInputs)) { look.Glow = [glow.X, glow.Y, glow.Z]; changed = true; }
        Tooltip("The colour they glow with.");
        ImGui.SameLine();
        changed |= Slider("Glow strength###sh_glow", look.GlowStrength, v => look.GlowStrength = v, 0f, 3f, "How much they glow on their own, even in the dark (0: none).");
        changed |= Slider("Shine###sh_shine", look.Shine, v => look.Shine = v, 0f, 5f, "How strong their highlights are (1: as they are).");
        changed |= Slider("Reflections###sh_refl", look.Reflection, v => look.Reflection = v, 0f, 5f, "How much they mirror their surroundings (1: as they are; not every shader reflects).");
        changed |= Slider("Bumps###sh_bump", look.Bumps, v => look.Bumps = v, 0f, 3f, "How strongly their bump maps shade the surface (0: smooth, 1: as they are).");
        if(ImGui.SmallButton("Reset###sh_reset"))
        {
            var fresh = new ShaderLook { Character = look.Character, Part = look.Part, Enabled = look.Enabled };
            look.Tint = fresh.Tint; look.Brightness = 1f; look.Glow = fresh.Glow; look.GlowStrength = 0f;
            look.Shine = 1f; look.Reflection = 1f; look.Bumps = 1f; look.Shader = "";
            changed = true;
        }
        return changed;
    }

    private static bool Slider(string label, float value, Action<float> set, float min, float max, string tip)
    {
        ImGui.SetNextItemWidth(Fit.Label(160f * ImGuiHelpers.GlobalScale));
        bool moved = ImGui.SliderFloat(label, ref value, min, max, "%.2f");
        if(moved)
            set(value);
        Tooltip(tip + " Ctrl+click to type a value.");
        return moved;
    }

    private void DrawPresets(ShaderLook? picked, List<ShaderPreset> presets)
    {
        ImGui.Separator();
        ImGui.TextUnformatted("Presets");
        ImGui.TextDisabled(picked == null ? "Press \"Use for presets\" on a look to save it or load one onto it."
            : $"Look: {picked.Character}, {picked.Part}");
        ImGui.SetNextItemWidth(Fit.Fixed(200f * ImGuiHelpers.GlobalScale));
        ImGui.InputTextWithHint("###sh_preset_name", "Preset name", ref _presetName, 64);
        ImGui.SameLine();
        using(ImRaii.Disabled(picked == null || _presetName.Trim().Length == 0))
        {
            if(ImGui.Button("Save###sh_preset_save") && picked != null)
            {
                var name = _presetName.Trim();
                var copy = picked.Clone();
                copy.Character = "";
                presets.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                presets.Add(new ShaderPreset { Name = name, Look = copy });
                _config.Save();
                _status = $"Saved \"{name}\".";
            }
        }
        ShaderPreset? drop = null;
        foreach(var p in presets)
        {
            using var id = ImRaii.PushId($"shp_{p.Name}");
            using(ImRaii.Disabled(picked == null))
            {
                if(ImGui.SmallButton("Load") && picked != null)
                {
                    var l = p.Look.Clone();
                    picked.Tint = l.Tint; picked.Brightness = l.Brightness; picked.Glow = l.Glow; picked.GlowStrength = l.GlowStrength;
                    picked.Shine = l.Shine; picked.Reflection = l.Reflection; picked.Bumps = l.Bumps; picked.Shader = l.Shader;
                    _config.Save();
                    _status = $"Loaded \"{p.Name}\" onto {picked.Character}.";
                }
            }
            ImGui.SameLine();
            if(ImGui.SmallButton("Delete"))
                drop = p;
            ImGui.SameLine();
            ImGui.TextUnformatted(p.Name);
        }
        if(drop != null)
        {
            presets.Remove(drop);
            _config.Save();
        }
    }
}
