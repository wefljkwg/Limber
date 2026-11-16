using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Limber.Rig;

namespace Limber.UI;

/// <summary>The Skin effects page (Scene): wet / sweaty sheen, flush and wet eyes for the chosen character (adults only).</summary>
public sealed class SkinEffectsTab
{
    private readonly Selection _selection;
    private readonly RigSettings _config;
    private readonly SkinEffectsService _service;

    public SkinEffectsTab(Selection selection, RigSettings config, SkinEffectsService service)
    {
        _selection = selection;
        _config = config;
        _service = service;
    }

    private static readonly (string Name, string Tip, Action<SkinEffectSettings> Apply)[] Presets =
    [
        ("Light sweat", "A faint sheen and a little colour.", s => { s.Wetness = 0.3f; s.WaterLine = false; s.Flush = 0.2f; s.WetEyes = 0f; s.RampSeconds = 4f; }),
        ("Glistening", "Shiny with sweat, flushed.", s => { s.Wetness = 0.65f; s.WaterLine = false; s.Flush = 0.4f; s.WetEyes = 0.3f; s.RampSeconds = 3f; }),
        ("Drenched", "Soaked all over, red-faced, eyes wet.", s => { s.Wetness = 1f; s.WaterLine = false; s.Flush = 0.6f; s.WetEyes = 0.7f; s.RampSeconds = 2f; }),
        ("Flushed", "Only the flush (and a little in the eyes).", s => { s.Wetness = 0f; s.WaterLine = false; s.Flush = 0.5f; s.WetEyes = 0.2f; s.RampSeconds = 2f; }),
        ("Bath", "Wet up to the waist, the water rising over a few seconds.", s => { s.Wetness = 1f; s.WaterLine = true; s.WaterLineHeight = 1f; s.RiseSeconds = 4f; s.Flush = 0.25f; s.WetEyes = 0f; s.RampSeconds = 1f; }),
    ];

    private static void Tooltip(string text)
    {
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip(text);
    }

    private static bool Slider(string label, string id, ref float value, float min, float max, string format, string tip)
    {
        ImGui.SetNextItemWidth(-160 * ImGuiHelpers.GlobalScale);
        bool changed = ImGui.SliderFloat($"{label}###skin_{id}", ref value, min, max, format);
        Tooltip(tip + "\nCtrl+click to type a value.");
        if(changed)
            value = Math.Clamp(value, min, max);
        return changed;
    }

    public void Draw()
    {
        var actor = _selection.Actor;
        if(!actor.IsSet)
        {
            ImGui.TextDisabled("Choose a character at the top of the window.");
            return;
        }
        if(!Limber.Koi.Safety.AdultAllowed(actor.Address))
        {
            ImGui.TextDisabled("Only for adult characters.");
            return;
        }
        string name = actor.Name ?? "";
        if(name.Length == 0)
        {
            ImGui.TextDisabled("This character has no name to keep the settings under.");
            return;
        }
        var all = _config.SkinEffects ??= new();
        if(!all.TryGetValue(name, out var s))
            all[name] = s = new SkinEffectSettings();
        bool changed = false;

        using(ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.25f, 0.5f, 0.3f, 1f), !s.Enabled))
        using(ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.55f, 0.25f, 0.25f, 1f), s.Enabled))
        {
            if(ImGui.Button(s.Enabled ? "Stop###skin_go" : "Start###skin_go", new Vector2(120 * ImGuiHelpers.GlobalScale, 0)))
            {
                s.Enabled = !s.Enabled;
                changed = true;
            }
        }
        Tooltip(s.Enabled ? "Fades the effects out (the skin colour and eyes go back to what they were)." : "Fades the effects in on this character (only on your screen).");
        ImGui.SameLine();
        if(_service.Current(actor.Address) is { } now)
            ImGui.TextUnformatted($"wet {now.Wet * 100f:0}%  flush {now.Flush * 100f:0}%  eyes {now.Eyes * 100f:0}%" + (now.Line > 0f ? $"  water line {now.Line:0.00} m" : ""));
        else
            ImGui.TextDisabled("off");

        ImGui.Text("Presets:");
        foreach(var (presetName, tip, apply) in Presets)
        {
            ImGui.SameLine();
            if(ImGui.SmallButton($"{presetName}###skin_preset_{presetName}"))
            {
                apply(s);
                changed = true;
            }
            Tooltip(tip);
        }
        ImGui.Spacing();

        // Wet / sweaty sheen.
        float wet = s.Wetness;
        if(Slider("Wet sheen", "wet", ref wet, 0f, 1f, "%.2f",
            "Sweat or water: the skin darker and glossier (clothes and hair too), the way rain or swimming makes them.\n"
            + "Needs the game's character wetness graphics option."))
        {
            s.Wetness = wet;
            changed = true;
        }
        bool line = s.WaterLine;
        if(ImGui.Checkbox("Water line###skin_line", ref line))
        {
            s.WaterLine = line;
            changed = true;
        }
        Tooltip("Wet only below a line, like standing in water (Wet sheen above 0 turns it on).\n"
            + (_config.SkinEffectsHook ? "Below the line it's as wet as Wet sheen." : "Without the stronger wetness below, it's fully wet below the line; the line lowers slowly by itself."));
        if(s.WaterLine)
        {
            using var indent = ImRaii.PushIndent();
            float height = s.WaterLineHeight;
            if(Slider("Height", "line_h", ref height, 0f, 2.5f, "%.2f m", "How high the water line is (metres above the feet, roughly)."))
            {
                s.WaterLineHeight = height;
                changed = true;
            }
            float rise = s.RiseSeconds;
            if(Slider("Rises over", "line_rise", ref rise, 0f, 60f, "%.1f s", "Seconds for the line to rise from the feet to its height (0: at once). Starts again each time Water line is ticked."))
            {
                s.RiseSeconds = rise;
                changed = true;
            }
        }

        // Flush.
        float flush = s.Flush;
        if(Slider("Flush", "flush", ref flush, 0f, 1f, "%.2f",
            "The skin goes redder (the whole body). Its own colour comes back when this ends; a colour another plugin\n"
            + "(e.g. Glamourer) sets meanwhile is kept and flushed in turn."))
        {
            s.Flush = flush;
            changed = true;
        }
        var color = s.FlushColor;
        ImGui.SetNextItemWidth(-160 * ImGuiHelpers.GlobalScale);
        if(ImGui.ColorEdit3("Flush colour###skin_flush_color", ref color, ImGuiColorEditFlags.NoInputs))
        {
            s.FlushColor = color;
            changed = true;
        }
        Tooltip("The colour the skin is drawn towards (a soft red by default).");

        // Wet eyes.
        float eyes = s.WetEyes;
        if(Slider("Wet eyes", "eyes", ref eyes, 0f, 1f, "%.2f", "Glistening, teary-looking eyes (a brighter catchlight)."))
        {
            s.WetEyes = eyes;
            changed = true;
        }
        float ramp = s.RampSeconds;
        if(Slider("Fade", "ramp", ref ramp, 0.05f, 30f, "%.1f s", "Seconds to fade the effects in and out."))
        {
            s.RampSeconds = ramp;
            changed = true;
        }

        // Steam / musk.
        ImGui.Spacing();
        ImGui.TextUnformatted("Steam / musk");
        float steam = s.Steam;
        if(Slider("Steam", "steam", ref steam, 0f, 1f, "%.2f",
            "Faint wisps rising off the skin (or their breath), drifting as they move and fading as they rise.\n"
            + "Drawn by Limber into the picture, hidden behind what's in front of it. 0: none."))
        {
            s.Steam = steam;
            changed = true;
        }
        if(s.Steam > 0f)
        {
            using var indent = ImRaii.PushIndent();
            var areas = (SteamService.Areas)s.SteamAreas;
            (SteamService.Areas Flag, string Label, string Tip)[] options =
            [
                (SteamService.Areas.Body, "Whole body", "From all over: back, chest, hips, thighs, arms, neck."),
                (SteamService.Areas.Crotch, "Crotch", "From between the legs."),
                (SteamService.Areas.Chest, "Chest", "From the breasts / chest."),
                (SteamService.Areas.Armpits, "Armpits", "From under the arms."),
                (SteamService.Areas.Head, "Head", "Off the top of the head."),
                (SteamService.Areas.Breath, "Breath", "Out of the mouth, in slow breaths."),
            ];
            for(int k = 0; k < options.Length; k++)
            {
                if(k > 0 && k % 3 != 0)
                    ImGui.SameLine();
                bool on = areas.HasFlag(options[k].Flag);
                if(ImGui.Checkbox($"{options[k].Label}###skin_steam_area_{k}", ref on))
                {
                    areas = on ? areas | options[k].Flag : areas & ~options[k].Flag;
                    s.SteamAreas = (int)areas;
                    changed = true;
                }
                Tooltip(options[k].Tip);
            }
            float opacity = s.SteamOpacity;
            if(Slider("Opacity", "steam_op", ref opacity, 0.02f, 1f, "%.2f", "How thick the wisps look."))
            {
                s.SteamOpacity = opacity;
                changed = true;
            }
            float density = s.SteamDensity;
            if(Slider("How many", "steam_n", ref density, 0.1f, 4f, "%.2f", "How many wisps (times)."))
            {
                s.SteamDensity = density;
                changed = true;
            }
            float rise = s.SteamRise;
            if(Slider("Rises", "steam_rise", ref rise, 0.2f, 4f, "%.2f", "How high and how long they rise (times)."))
            {
                s.SteamRise = rise;
                changed = true;
            }
            float size = s.SteamSize;
            if(Slider("Size", "steam_size", ref size, 0.2f, 4f, "%.2f", "How big the wisps are (times)."))
            {
                s.SteamSize = size;
                changed = true;
            }
            var steamColor = s.SteamColor;
            ImGui.SetNextItemWidth(-160 * ImGuiHelpers.GlobalScale);
            if(ImGui.ColorEdit3("Colour###skin_steam_color", ref steamColor, ImGuiColorEditFlags.NoInputs))
            {
                s.SteamColor = steamColor;
                changed = true;
            }
            Tooltip("The steam's colour (a soft white by default; tint it for a coloured musk).");
            ImGui.SameLine();
            bool behind = _config.SteamHideBehind;
            if(ImGui.Checkbox("Hidden behind things###skin_steam_behind", ref behind))
            {
                _config.SteamHideBehind = behind;
                changed = true;
            }
            Tooltip("Hidden behind what's in front of it (the game's depth). Off: always drawn on top.");
            if(SteamService.Instance is { } svc)
                ImGui.TextDisabled($"{svc.Count} wisps up" + (Limber.Gpu.SteamRenderer.Error.Length > 0 ? "" : "") + (s.Enabled ? "" : " (press Start)"));
            if(Limber.Gpu.SteamRenderer.Error.Length > 0)
            {
                ImGui.TextColored(new Vector4(0.95f, 0.4f, 0.4f, 1f), "Can't draw the steam: " + Limber.Gpu.SteamRenderer.Error);
                ImGui.SameLine();
                if(ImGui.SmallButton("Try again###skin_steam_retry"))
                    Limber.Gpu.SteamRenderer.Retry();
            }
            ImGui.TextDisabled("Drawn over the picture: the game's own UI (if it's showing) can end up under it.");
        }

        ImGui.Spacing();
        ImGui.Separator();
        bool hook = _config.SkinEffectsHook;
        if(ImGui.Checkbox("Stronger wetness###skin_hook", ref hook))
        {
            _config.SkinEffectsHook = hook;
            changed = true;
        }
        Tooltip("Lets the wet sheen go all the way (the game's own stops at 80%) and the water line follow Wet sheen,\n"
            + "by changing the game's wetness as it's worked out each frame (one more hook on the game; off: no hook).");
        if(_service.HookActive)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("(hooked)");
        }
        switch(_service.WetnessOptionOn())
        {
            case false:
                ImGui.TextColored(new Vector4(0.95f, 0.75f, 0.3f, 1f), "The game's character wetness graphics option looks off: no wet sheen will show.");
                break;
            case null:
                ImGui.TextDisabled("(Couldn't check the game's character wetness option.)");
                break;
        }
        if(_service.Problem.Length > 0)
            ImGui.TextColored(new Vector4(0.95f, 0.4f, 0.4f, 1f), "Problem: " + _service.Problem);
        ImGui.TextDisabled("Not on the Timeline yet. Other features (like Climax) can drive these later.");

        if(changed)
            _config.Save();
    }
}
