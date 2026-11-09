using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Limber.Rig;

namespace Limber.UI;

/// <summary>Experimental page: Limber's motion played by the game's own animation system (see EngineLayerService).</summary>
public sealed class EngineLayerTab
{
    private readonly Selection _selection;
    private readonly RigSettings _config;
    private readonly EngineLayerService _service;
    private List<string> _readout = new();
    private long _readAt;

    public EngineLayerTab(Selection selection, RigSettings config, EngineLayerService service)
    {
        _selection = selection;
        _config = config;
        _service = service;
    }

    private static void Tooltip(string text)
    {
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip(text);
    }

    private static readonly (EngineLayerMotion Motion, string Name, string Tip)[] Motions =
    [
        (EngineLayerMotion.Thrust, "Thrust", "The hips forward and back, smoothly."),
        (EngineLayerMotion.Pump, "Pump", "A quick push, then a slower return."),
        (EngineLayerMotion.Bounce, "Bounce", "The same way every beat (bouncing)."),
        (EngineLayerMotion.Circle, "Circle", "The hips go round."),
    ];

    public void Draw()
    {
        var s = _config.EngineLayer ??= new EngineLayerSettings();
        bool changed = false;
        ImGui.TextWrapped("Experimental and untested: plays a small Limber motion (the hips) through the game's own animation "
            + "system, mixed with whatever emote is playing, instead of Limber moving the bones afterwards. If the game "
            + "misbehaves, untick it and tell the author what the readout below said.");
        ImGui.Spacing();
        var actor = _selection.Actor;
        bool adult = actor.IsSet && Limber.Koi.Safety.AdultAllowed(actor.Address);
        using(ImRaii.Disabled(!adult && !s.Enabled))
        {
            bool on = s.Enabled;
            if(ImGui.Checkbox("Engine animation layer on the chosen character###elayer_on", ref on))
            {
                s.Enabled = on;
                changed = true;
            }
        }
        Tooltip("Adds one more animation to the character's body in the game's own list and moves it every frame.\nUntick: it fades out and is taken off.");
        if(!actor.IsSet)
            ImGui.TextDisabled("Choose a character at the top of the window.");
        else if(!adult)
            ImGui.TextDisabled("Only for adult characters.");
        ImGui.TextDisabled(_service.Status);
        if(_service.Problem.Length > 0)
            ImGui.TextColored(new Vector4(0.95f, 0.4f, 0.4f, 1f), "Problem: " + _service.Problem);

        string motion = Array.Find(Motions, m => m.Motion == s.Motion).Name ?? s.Motion.ToString();
        ImGui.SetNextItemWidth(-160 * ImGuiHelpers.GlobalScale);
        using(var combo = ImRaii.Combo("Motion###elayer_motion", motion))
        {
            if(combo.Success)
                foreach(var (m, mName, tip) in Motions)
                {
                    if(ImGui.Selectable(mName, m == s.Motion))
                    {
                        s.Motion = m;
                        changed = true;
                    }
                    Tooltip(tip);
                }
        }
        changed |= Slider("Speed", "speed", s.Speed, v => s.Speed = v, 10f, 300f, "%.0f a minute", "Strokes a minute.");
        changed |= Slider("Move", "move", s.Move, v => s.Move = v, -15f, 15f, "%.1f cm", "How far the hips move each way.");
        changed |= Slider("Tilt", "tilt", s.Tilt, v => s.Tilt = v, -30f, 30f, "%.1f°", "How far the hips tilt each way.");
        changed |= AxisCombo("Move along", "move_axis", s.MoveAxis, v => s.MoveAxis = v, "Which of the hip bone's own directions it moves along (try another if it moves sideways or up).");
        changed |= AxisCombo("Tilt around", "tilt_axis", s.TiltAxis, v => s.TiltAxis = v, "Which of the hip bone's own directions it tilts around.");
        bool spine = s.Spine;
        if(ImGui.Checkbox("Lower back counters###elayer_spine", ref spine))
        {
            s.Spine = spine;
            changed = true;
        }
        Tooltip("The lower back turns a little the other way, so the chest stays steadier. Takes effect when the layer is next put on.");
        changed |= Slider("Weight", "weight", s.Weight, v => s.Weight = v, 0f, 1f, "%.2f", "How strongly it's mixed in.");
        changed |= Slider("Fade", "fade", s.FadeSeconds, v => s.FadeSeconds = v, 0.05f, 5f, "%.2f s", "Seconds to fade in and out.");
        int hint = s.BlendHint == 1 ? 1 : 0;
        ImGui.SetNextItemWidth(-160 * ImGuiHelpers.GlobalScale);
        if(ImGui.Combo("Mixing###elayer_hint", ref hint, "Additive\0Additive (older kind)\0"))
        {
            s.BlendHint = hint == 1 ? 1 : 2;
            changed = true;
        }
        Tooltip("How the game adds it to the emote. Try the other if the hips jump, grow or twist. Takes effect when the layer is next put on.");

        ImGui.Spacing();
        if(ImGui.CollapsingHeader("What the game has (readout)###elayer_readout"))
        {
            long now = Environment.TickCount64;
            if(now - _readAt > 250)
            {
                _readAt = now;
                _readout = _service.Diagnostics(actor.IsSet ? actor.Address : 0);
            }
            foreach(var line in _readout)
                ImGui.TextUnformatted(line);
            if(ImGui.SmallButton("Copy###elayer_copy"))
                ImGui.SetClipboardText(string.Join("\n", _readout));
            Tooltip("Copies the readout (to paste into a message).");
        }
        if(changed)
            _config.Save();
    }

    private static bool Slider(string label, string id, float value, Action<float> set, float min, float max, string format, string tip)
    {
        ImGui.SetNextItemWidth(-160 * ImGuiHelpers.GlobalScale);
        bool changed = ImGui.SliderFloat($"{label}###elayer_{id}", ref value, min, max, format);
        Tooltip(tip + "\nCtrl+click to type a value.");
        if(changed && float.IsFinite(value))
            set(Math.Clamp(value, min, max));
        return changed;
    }

    private static bool AxisCombo(string label, string id, int value, Action<int> set, string tip)
    {
        int v = Math.Clamp(value, 0, 2);
        ImGui.SetNextItemWidth(-160 * ImGuiHelpers.GlobalScale);
        bool changed = ImGui.Combo($"{label}###elayer_{id}", ref v, "X\0Y\0Z\0");
        Tooltip(tip);
        if(changed)
            set(v);
        return changed;
    }
}
