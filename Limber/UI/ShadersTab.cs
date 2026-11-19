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
        ImGui.TextWrapped("Change a character's materials: their colour, brightness and glow (gear and hair). "
                          + "Only that character changes (players, NPCs, actors), and only on your screen. "
                          + "Each change draws them again once to take it (a moment after you stop dragging). "
                          + "The drawn effects (light, rim, outline, see through walls) are Limber's own: they work on any part, skin too, and show at once.");

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


        var tint = new Vector3(look.Tint?[0] ?? 1f, look.Tint?[1] ?? 1f, look.Tint?[2] ?? 1f);
        if(ImGui.ColorEdit3("Colour###sh_tint", ref tint, ImGuiColorEditFlags.NoInputs)) { look.Tint = [tint.X, tint.Y, tint.Z]; changed = true; }
        Tooltip("Multiplies their colour (white: as it is).");
        ImGui.SameLine();
        changed |= Slider("Brightness###sh_bright", look.Brightness, v => look.Brightness = v, 0f, 3f, "Darker (below 1) or brighter (above).");
        var glow = new Vector3(look.Glow?[0] ?? 1f, look.Glow?[1] ?? 1f, look.Glow?[2] ?? 1f);
        if(ImGui.ColorEdit3("Glow###sh_glowc", ref glow, ImGuiColorEditFlags.NoInputs)) { look.Glow = [glow.X, glow.Y, glow.Z]; changed = true; }
        Tooltip("The colour they glow with (gear and hair: the game's skin shader can't glow).");
        ImGui.SameLine();
        changed |= Slider("Glow strength###sh_glow", look.GlowStrength, v => look.GlowStrength = v, 0f, 3f, "How much they glow on their own, even in the dark (0: none).");

        // Limber's own drawn effects (no redraw: they show straight away)
        ImGui.TextDisabled("Drawn effects (Limber's own: any part, skin too; show straight away)");
        changed |= Colour("Light###sh_fx_light", look.Light, new Vector3(1f, 0.55f, 0.85f), v => look.Light = v, "The colour the surface lights up with.");
        ImGui.SameLine();
        changed |= Slider("Light up###sh_fx_lights", look.LightStrength, v => look.LightStrength = v, 0f, 2f, "Lights the whole surface up with that colour, skin included (0: none).");
        changed |= Slider("Pulse###sh_fx_pulse", look.Pulse, v => look.Pulse = v, 0f, 4f, "The light and rim breathe in and out, this many times a second (0: steady).");
        changed |= Colour("Rim###sh_fx_rim", look.Rim, new Vector3(0.55f, 0.8f, 1f), v => look.Rim = v, "The colour of the light round their edges.");
        ImGui.SameLine();
        changed |= Slider("Rim light###sh_fx_rims", look.RimStrength, v => look.RimStrength = v, 0f, 3f, "A light round their edges, as if lit from behind (0: none).");
        changed |= Slider("Rim tightness###sh_fx_rimt", look.RimTightness, v => look.RimTightness = v, 0.5f, 10f, "How close to the edges the rim stays (higher: a thinner line).");
        changed |= Colour("Outline###sh_fx_out", look.Outline, Vector3.Zero, v => look.Outline = v, "The outline's colour.");
        ImGui.SameLine();
        changed |= Slider("Outline width###sh_fx_outw", look.OutlineWidth, v => look.OutlineWidth = v, 0f, 20f, "A cartoon outline round them, this many pixels thick (0: none).");
        bool see = look.SeeThrough;
        if(ImGui.Checkbox("See through walls###sh_fx_see", ref see)) { look.SeeThrough = see; changed = true; }
        Tooltip("Where something's in front of them, a faint silhouette in the rim's colour still shows.");
        if(look.HasEffects() && look.Enabled)
            ImGui.TextDisabled($"Marking {Limber.Gpu.EffectRenderer.Drawn} of the game's draws ({Limber.Gpu.EffectRenderer.Views} bone buffers found)");
        if(Limber.Gpu.EffectRenderer.Error.Length > 0)
        {
            ImGui.TextColored(new Vector4(0.95f, 0.4f, 0.4f, 1f), "Can't draw the effects: " + Limber.Gpu.EffectRenderer.Error);
            ImGui.SameLine();
            if(ImGui.SmallButton("Try again###sh_fx_retry"))
                Limber.Gpu.EffectRenderer.Retry();
        }

        if(ImGui.SmallButton("Reset###sh_reset"))
        {
            var fresh = new ShaderLook { Character = look.Character, Part = look.Part, Enabled = look.Enabled };
            look.Tint = fresh.Tint; look.Brightness = 1f; look.Glow = fresh.Glow; look.GlowStrength = 0f;
            look.Shine = 1f; look.Reflection = 1f; look.Bumps = 1f; look.Shader = "";
            look.Light = fresh.Light; look.LightStrength = 0f; look.Pulse = 0f; look.Rim = fresh.Rim; look.RimStrength = 0f;
            look.RimTightness = fresh.RimTightness; look.Outline = fresh.Outline; look.OutlineWidth = 0f; look.SeeThrough = false;
            changed = true;
        }
        return changed;
    }

    private static bool Colour(string label, float[]? value, Vector3 fallback, Action<float[]> set, string tip)
    {
        var c = value is { Length: >= 3 } ? new Vector3(value[0], value[1], value[2]) : fallback;
        bool moved = ImGui.ColorEdit3(label, ref c, ImGuiColorEditFlags.NoInputs);
        if(moved)
            set([c.X, c.Y, c.Z]);
        Tooltip(tip);
        return moved;
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
                    picked.Light = l.Light; picked.LightStrength = l.LightStrength; picked.Pulse = l.Pulse; picked.Rim = l.Rim;
                    picked.RimStrength = l.RimStrength; picked.RimTightness = l.RimTightness; picked.Outline = l.Outline;
                    picked.OutlineWidth = l.OutlineWidth; picked.SeeThrough = l.SeeThrough;
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
