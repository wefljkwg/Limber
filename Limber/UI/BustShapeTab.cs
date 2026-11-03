using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Limber.Rig;

namespace Limber.UI;

/// <summary>
/// The Breast size page (Body): for tops made with size shapes (Koikatsu-style), the chosen character's size, from
/// flat to huge: their own Bust slider, or a size of its own here.
/// </summary>
public sealed class BustShapeTab
{
    private readonly Selection _selection;
    private readonly RigSettings _config;
    private readonly BustShapeService _service;

    public BustShapeTab(Selection selection, RigSettings config, BustShapeService service)
    {
        _selection = selection;
        _config = config;
        _service = service;
    }

    public void Draw()
    {
        ImGui.TextDisabled(Limber.Gpu.BustShapes.Status.Length > 0 ? Limber.Gpu.BustShapes.Status : "Breast shapes: no shaped top drawn yet.");

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
        var all = _config.BustShapes ??= new();
        if(!all.TryGetValue(name, out var s))
            all[name] = s = new BustShapeSettings();
        bool changed = false;

        var sizes = Limber.Gpu.BustShapes.SizesOf(actor.Address);
        if(Limber.Gpu.BustShapes.PausedFor(actor.Address))
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.4f, 1f), "Paused: the GPU soft body (Body physics) is on for this character, and both would change the same model. "
                + "Turn the GPU soft body off to use the breast size shapes (the bone soft body works with them).");
        else if(sizes == null)
        {
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.4f, 1f), "This character isn't wearing a top with breast size shapes.");
            string top = Limber.Gpu.BustShapes.TopOf(actor.Address);
            if(top.Length > 0)
                ImGui.TextDisabled($"Its top is drawn from: {top}");
            ImGui.TextWrapped("If you turned the shaped top's mod on: another mod that changes the top (a Limber rigged model, a body mod) "
                + "may have a higher priority in your mod manager. Give the shaped top the highest priority, or turn the other off.");
        }
        else
            ImGui.TextDisabled($"Wearing a shaped top: sizes {string.Join(", ", Array.ConvertAll(sizes, v => v.ToString("0")))}.");

        bool on = s.Enabled;
        if(ImGui.Checkbox("Shape the breasts by size###bust_on", ref on))
        {
            s.Enabled = on;
            changed = true;
        }
        Tooltip("Off: the top is drawn as its model is, at its own size.");

        bool fromGame = s.FromGame;
        if(ImGui.Checkbox("Use the character's Bust slider###bust_game", ref fromGame))
        {
            s.FromGame = fromGame;
            changed = true;
        }
        int game = BustShapeService.GameBust(actor.Address);
        ImGui.SameLine();
        ImGui.TextDisabled(game >= 0 ? $"(Bust: {game})" : "");
        if(!s.FromGame)
            changed |= Slider("Breast size", s.Size, v => s.Size = v, 0f, 100f, "%.0f", "0: a flat chest. About 12: small mounds. 30: small. 50: medium. 75: large. 100: huge.");
        changed |= Slider("Beyond", s.Extra, v => s.Extra = v, -50f, 30f, "%+.0f", "Added to the size: below 0 smaller, and past 100 it carries on growing a little.");

        bool cancel = s.CancelGameScale;
        if(ImGui.Checkbox("Take off the game's own bust scaling###bust_cancel", ref cancel))
        {
            s.CancelGameScale = cancel;
            changed = true;
        }
        Tooltip("The game also scales the breast bones by the Bust slider; the shapes already are the size, so this takes that off.");

        float now = _service.SizeOf(actor.Address);
        if(now >= 0f)
            ImGui.ProgressBar(Math.Clamp(now / 100f, 0f, 1f), new Vector2(-1, 0), $"size {now:0}");

        if(changed)
            _config.Save();
    }

    private static bool Slider(string label, float value, Action<float> set, float min, float max, string format, string tip)
    {
        ImGui.SetNextItemWidth(-130 * ImGuiHelpers.GlobalScale);
        bool edited = ImGui.SliderFloat($"{label}###bust_{label}", ref value, min, max, format);
        Tooltip(tip + "\nCtrl+click to type a value.");
        if(edited)
            set(value);
        return edited;
    }

    private static void Tooltip(string text)
    {
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip(text);
    }
}
