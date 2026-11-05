using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Limber.Rig;

namespace Limber.UI;

/// <summary>
/// The Limber Animations page: Limber's own automatic animations on the chosen character. For now, throbbing
/// (adult characters): a pattern (or patterns changing by themselves) and how it beats, eased in and out.
/// </summary>
public sealed class LimberAnimationsTab
{
    private readonly Selection _selection;
    private readonly RigSettings _config;
    private readonly ThrobService _throb;
    private readonly TwerkService _twerk;
    private readonly AhegaoService _ahegao;
    private readonly ExpansionService _expansion;
    /// <summary>Testicle throbbing (set by the plugin).</summary>
    public BallThrobService? BallThrob { get; set; }
    /// <summary>Height growth / shrinking (set by the plugin).</summary>
    public SizeChangeService? SizeChange { get; set; }

    public LimberAnimationsTab(Selection selection, RigSettings config, ThrobService throb, TwerkService twerk, AhegaoService ahegao, ExpansionService expansion)
    {
        _expansion = expansion;
        _selection = selection;
        _config = config;
        _throb = throb;
        _twerk = twerk;
        _ahegao = ahegao;
    }

    private static readonly (AhegaoType Type, string Name, string Tip)[] AhegaoTypes =
    [
        (AhegaoType.Classic, "Classic", "Eyes rolled up, mouth open, tongue out over the lip, brows raised."),
        (AhegaoType.CrossEyed, "Cross-eyed", "Eyes rolled up and crossed in."),
        (AhegaoType.Dazed, "Dazed", "Heavy-lidded and spaced out, the tongue just showing."),
        (AhegaoType.Panting, "Panting", "Quick breaths, mouth and tongue moving with them."),
        (AhegaoType.Overwhelmed, "Overwhelmed", "Head thrown back, eyes rolled all the way up, mouth wide, trembling."),
        (AhegaoType.Giddy, "Giddy", "Eyes up with a wide smile and the tongue out."),
        (AhegaoType.Pained, "Pained", "Brows knitted together and up in the middle, a squint, mouth corners down, trembling."),
        (AhegaoType.Worried, "Worried", "Inner brows raised high, a small open mouth turned down, unsteady breaths."),
        (AhegaoType.Exhausted, "Exhausted", "Heavy lids, slack jaw, the tongue hanging, the head drooping to one side, slow breaths."),
        (AhegaoType.Happy, "Happy", "Smiling eyes (lower lids up), corners well up, cheeks lifted."),
        (AhegaoType.Excited, "Excited", "Eyes wide and rolled up, brows high, mouth wide with a grin, fast breaths."),
    ];

    private static readonly (TwerkStyle Style, string Name, string Tip)[] TwerkStyles =
    [
        (TwerkStyle.Classic, "Classic", "Hips popping back (arched) and tucking under, to the beat."),
        (TwerkStyle.Bounce, "Bounce", "Bouncing up and down from the knees, the hips following."),
        (TwerkStyle.Shake, "Shake", "A fast side-to-side shake of the hips."),
        (TwerkStyle.Wind, "Wind", "Slow circles of the hips."),
        (TwerkStyle.LeftRight, "Left / right", "Each pop to one side, then the other."),
        (TwerkStyle.Drop, "Drop", "Popping while sinking low over eight pops, then coming back up."),
    ];

    private static readonly (TwerkPose Pose, string Name, string Tip)[] TwerkPoses =
    [
        (TwerkPose.Squat, "Squat", "Feet planted, knees bent, leaning forward with the hands on the knees."),
        (TwerkPose.Standing, "Standing (bent over)", "Legs nearly straight, bent over at the hips."),
        (TwerkPose.Kneeling, "Kneeling", "On the knees, sitting back toward the heels (Squat sets how far)."),
        (TwerkPose.AllFours, "All fours", "On the hands and knees, the back flat (Squat sets how far the hips sit back)."),
    ];

    private static readonly (TwerkHands Hands, string Name)[] TwerkHandPlaces =
    [
        (TwerkHands.Knees, "On the knees"),
        (TwerkHands.Thighs, "On the thighs"),
        (TwerkHands.Hips, "On the hips"),
        (TwerkHands.Free, "As the animation has them"),
    ];

    private static readonly (string Name, Action<TwerkSettings> Apply)[] TwerkPresets =
    [
        ("Easy", t => { t.Auto = false; t.Style = TwerkStyle.Classic; t.Speed = 120; t.Swing = 12; t.Bounce = 1; t.Depth = 0.35f; t.Lean = 25; t.Isolation = 0.5f; t.Randomness = 0.15f; }),
        ("Classic", t => { t.Auto = false; t.Style = TwerkStyle.Classic; t.Speed = 170; t.Swing = 18; t.Bounce = 1.5f; t.Depth = 0.5f; t.Lean = 35; t.Isolation = 0.7f; t.Randomness = 0.2f; }),
        ("Fast", t => { t.Auto = false; t.Style = TwerkStyle.Classic; t.Speed = 240; t.Swing = 14; t.Bounce = 1; t.Depth = 0.55f; t.Lean = 40; t.Isolation = 0.85f; t.Randomness = 0.25f; }),
        ("Low", t => { t.Auto = false; t.Style = TwerkStyle.Drop; t.Speed = 160; t.Swing = 20; t.Bounce = 2; t.Depth = 0.8f; t.Lean = 45; t.Isolation = 0.7f; t.Randomness = 0.2f; }),
        ("Mixed (auto)", t => { t.Auto = true; t.AutoSeconds = 12; t.Speed = 170; t.Swing = 18; t.Bounce = 1.5f; t.Depth = 0.5f; t.Lean = 35; t.Isolation = 0.7f; t.Randomness = 0.25f; }),
    ];

    private static readonly (ThrobPattern Pattern, string Name, string Tip)[] Patterns =
    [
        (ThrobPattern.Steady, "Steady", "An even pulse on every beat."),
        (ThrobPattern.Heartbeat, "Heartbeat", "Two pulses close together (strong, then softer), like a heartbeat."),
        (ThrobPattern.Building, "Building", "Starts soft and slow, grows stronger and doubles up, then rests and starts again."),
        (ThrobPattern.Bursts, "Bursts", "Four quick pulses, then a pause."),
        (ThrobPattern.Twitching, "Twitching", "Sudden short jerks at random moments, mostly a lift."),
        (ThrobPattern.Wave, "Wave", "A smooth, continuous wave rolling from the base to the tip."),
        (ThrobPattern.Random, "Random", "Uneven pulses: skipped beats, strong and weak ones."),
        (ThrobPattern.Climax, "Climax", "Strong contractions, close together at first, then further apart and weaker; a rest; again."),
        (ThrobPattern.SlowSwell, "Slow swell", "Slow swelling and easing (a breath every four beats) with soft pulses on top."),
    ];

    private static readonly (string Name, Action<ThrobSettings> Apply)[] Presets =
    [
        ("Gentle", s => { s.Pattern = ThrobPattern.SlowSwell; s.Auto = false; s.Speed = 45; s.Swell = 0.06f; s.Length = 0.015f; s.Lift = 3; s.Fall = 1.5f; s.Travel = 0.5f; s.Randomness = 0.2f; s.Testicles = 0.2f; }),
        ("Steady", s => { s.Pattern = ThrobPattern.Heartbeat; s.Auto = false; s.Speed = 60; s.Swell = 0.1f; s.Length = 0.03f; s.Lift = 6; s.Fall = 3; s.Travel = 0.4f; s.Randomness = 0.3f; s.Testicles = 0.5f; }),
        ("Eager", s => { s.Pattern = ThrobPattern.Twitching; s.Auto = false; s.Speed = 80; s.Swell = 0.08f; s.Length = 0.02f; s.Lift = 12; s.Fall = 4; s.Travel = 0.2f; s.Randomness = 0.6f; s.Testicles = 0.4f; }),
        ("Intense", s => { s.Pattern = ThrobPattern.Building; s.Auto = false; s.Speed = 95; s.Swell = 0.16f; s.Length = 0.05f; s.Lift = 10; s.Fall = 5; s.Travel = 0.35f; s.Randomness = 0.35f; s.Testicles = 0.7f; }),
        ("Climax", s => { s.Pattern = ThrobPattern.Climax; s.Auto = false; s.Speed = 70; s.Swell = 0.2f; s.Length = 0.06f; s.Lift = 14; s.Fall = 7; s.Travel = 0.5f; s.Randomness = 0.2f; s.Testicles = 1f; }),
        ("Mixed (auto)", s => { s.Auto = true; s.AutoSeconds = 20; s.Speed = 65; s.Swell = 0.12f; s.Length = 0.035f; s.Lift = 8; s.Fall = 4; s.Travel = 0.4f; s.Randomness = 0.35f; s.Testicles = 0.5f; }),
    ];

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
        DrawSelected(name, actor);
        ImGui.Separator();
        ImGui.Spacing();
        // Both throbbings at once, in time with each other.
        if(BallThrob != null)
        {
            var penis = (_config.Throb ??= new()).TryGetValue(name, out var p) ? p : null;
            var balls = (_config.BallThrob ??= new()).TryGetValue(name, out var b) ? b : null;
            bool bothOn = penis is { Enabled: true } && balls is { Enabled: true };
            if(ImGui.Button(bothOn ? "Stop both throbbings###lanim_both" : "Start both throbbings (in sync)###lanim_both"))
            {
                penis ??= _config.Throb[name] = new ThrobSettings();
                balls ??= _config.BallThrob[name] = new BallThrobSettings();
                penis.Enabled = balls.Enabled = !bothOn;
                if(!bothOn)
                {
                    _throb.Sync(actor.Address);
                    BallThrob.Sync(actor.Address);
                }
                _config.Save();
            }
            if(ImGui.IsItemHovered())
                ImGui.SetTooltip("Starts (or stops) penis and testicle throbbing together, their beats starting at the same moment.");
        }
        if(ImGui.CollapsingHeader("Throbbing###lanim_throb"))
            DrawThrob(name, actor);
        if(BallThrob != null && ImGui.CollapsingHeader("Testicle throbbing###lanim_balls"))
            DrawBallThrob(name, actor);
        if(ImGui.CollapsingHeader("Twerking###lanim_twerk"))
            DrawTwerk(name, actor);
        if(ImGui.CollapsingHeader("Ahegao###lanim_ahegao"))
            DrawAhegao(name, actor);
        if(ImGui.CollapsingHeader("Expansion###lanim_expansion"))
            DrawExpansion(name, actor);
        if(SizeChange != null && ImGui.CollapsingHeader("Height growth / shrinking###lanim_height"))
            DrawHeight(name, actor);
    }

    // ---- Height growth / shrinking

    private static readonly (SizeChangePattern Pattern, string Name, string Tip)[] SizePatterns =
    [
        (SizeChangePattern.Smooth, "Smooth", "Eases into it and eases out at the end size."),
        (SizeChangePattern.Steady, "Steady", "The same factor every second (looks even at any size: 1x to 10x takes as long as 10x to 100x)."),
        (SizeChangePattern.Surge, "Surge", "Fast at first, slowing toward the end size."),
        (SizeChangePattern.Accelerating, "Accelerating", "Slow at first, then faster and faster."),
        (SizeChangePattern.Pulses, "Pulses", "In pulses, swelling a little past where it's heading on each (Speed, Strength)."),
        (SizeChangePattern.Steps, "Steps", "Sudden jumps in size, holding between (Speed)."),
        (SizeChangePattern.Spurts, "Spurts", "Random-sized jumps (Speed, Strength = how uneven)."),
    ];

    private static readonly (string Name, Action<SizeChangeSettings> Apply)[] SizePresets =
    [
        ("Taller", h => { h.From = 1f; h.To = 1.5f; h.Duration = 8f; h.Pattern = SizeChangePattern.Smooth; h.Repeat = ExpansionRepeat.Hold; }),
        ("Giantess", h => { h.From = 1f; h.To = 10f; h.Duration = 20f; h.Pattern = SizeChangePattern.Smooth; h.Repeat = ExpansionRepeat.Hold; }),
        ("Macro", h => { h.From = 1f; h.To = 100f; h.Duration = 40f; h.Pattern = SizeChangePattern.Accelerating; h.Repeat = ExpansionRepeat.Hold; }),
        ("Mega macro", h => { h.From = 1f; h.To = 1000f; h.Duration = 60f; h.Pattern = SizeChangePattern.Steady; h.Repeat = ExpansionRepeat.Hold; }),
        ("Growth spurts", h => { h.From = 1f; h.To = 30f; h.Duration = 30f; h.Pattern = SizeChangePattern.Spurts; h.Speed = 15f; h.Strength = 0.7f; h.Repeat = ExpansionRepeat.Hold; }),
        ("Pulsing growth", h => { h.From = 1f; h.To = 20f; h.Duration = 30f; h.Pattern = SizeChangePattern.Pulses; h.Speed = 40f; h.Strength = 0.7f; h.Repeat = ExpansionRepeat.Hold; }),
        ("Shrink", h => { h.From = 1f; h.To = 0.3f; h.Duration = 10f; h.Pattern = SizeChangePattern.Smooth; h.Repeat = ExpansionRepeat.Hold; }),
        ("Tiny", h => { h.From = 1f; h.To = 0.05f; h.Duration = 20f; h.Pattern = SizeChangePattern.Smooth; h.Repeat = ExpansionRepeat.Hold; }),
        ("Ant-sized", h => { h.From = 1f; h.To = 0.002f; h.Duration = 30f; h.Pattern = SizeChangePattern.Steady; h.Repeat = ExpansionRepeat.Hold; }),
        ("Grow and shrink", h => { h.From = 0.5f; h.To = 5f; h.Duration = 10f; h.Pattern = SizeChangePattern.Smooth; h.Repeat = ExpansionRepeat.Reverse; }),
    ];

    // A rough word for a size (1 = its own).
    private static string SizeWord(float x) => x switch
    {
        < 0.005f => "ant-sized",
        < 0.03f => "bug-sized",
        < 0.12f => "palm-sized",
        < 0.4f => "doll-sized",
        < 0.85f => "small",
        <= 1.18f => "own size",
        < 2f => "tall",
        < 6f => "giant",
        < 30f => "giantess (building-sized)",
        < 200f => "macro (skyscraper-sized)",
        _ => "mega macro (mountain-sized)",
    };

    private static bool SizeSlider(string label, float value, Action<float> set, string tip)
    {
        ImGui.SetNextItemWidth(-130 * ImGuiHelpers.GlobalScale);
        bool edited = ImGui.SliderFloat($"{label}###lanim_h_{label}", ref value, SizeChangeService.MinSize, SizeChangeService.MaxSize,
            value < 0.1f ? "%.4fx" : "%.2fx", ImGuiSliderFlags.Logarithmic);
        Tooltip(tip + "\nCtrl+click to type a value.");
        if(edited && float.IsFinite(value))
            set(Math.Clamp(value, SizeChangeService.MinSize, SizeChangeService.MaxSize));
        ImGui.SameLine();
        ImGui.TextDisabled(SizeWord(value));
        return edited;
    }

    private void DrawHeight(string name, ActorRef actor)
    {
        var all = _config.SizeChange ??= new();
        if(!all.TryGetValue(name, out var h))
            all[name] = h = new SizeChangeSettings();
        bool changed = false;

        float now = SizeChange!.SizeOf(actor.Address);
        using(ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.25f, 0.5f, 0.3f, 1f), !h.Enabled))
        using(ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.55f, 0.25f, 0.25f, 1f), h.Enabled))
        {
            if(ImGui.Button(h.Enabled ? "Stop###lanim_h_go" : "Start###lanim_h_go", new Vector2(120 * ImGuiHelpers.GlobalScale, 0)))
            {
                if(!h.Enabled)
                    SizeChange.Restart(actor.Address);
                h.Enabled = !h.Enabled;
                changed = true;
            }
        }
        Tooltip("Stopped, it goes back to its own size (over Return).");
        ImGui.SameLine();
        if(ImGui.Button("Restart###lanim_h_restart"))
            SizeChange.Restart(actor.Address);
        Tooltip("Start over from the start size.");
        ImGui.SameLine();
        if(now > 0f)
            ImGui.TextUnformatted($"now {(now < 0.1f ? now.ToString("0.0000") : now.ToString("0.00"))}x ({SizeWord(now)})");
        else
            ImGui.TextDisabled("own size");

        ImGui.Text("Presets:");
        int k = 0;
        foreach(var (presetName, apply) in SizePresets)
        {
            if(k++ % 5 != 0)
                ImGui.SameLine();
            if(ImGui.SmallButton($"{presetName}###hpreset_{presetName}"))
            {
                apply(h);
                SizeChange.Restart(actor.Address);
                changed = true;
            }
        }

        changed |= SizeSlider("Start size", h.From, v => h.From = v, "The size it starts from (1x = its own size).");
        changed |= SizeSlider("End size", h.To, v => h.To = v,
            "The size it grows (or shrinks) to: up to 2000x (a mountain-sized giant), down to 0.0005x (smaller than an ant). 1x = its own size.");
        changed |= SliderEx("Duration", h.Duration, v => h.Duration = MathF.Max(0.1f, v), 0.5f, 300f, "%.1f s",
            "Seconds from the start size to the end size.");

        string pattern = Array.Find(SizePatterns, x => x.Pattern == h.Pattern).Name ?? h.Pattern.ToString();
        ImGui.SetNextItemWidth(-130 * ImGuiHelpers.GlobalScale);
        using(var combo = ImRaii.Combo("Pattern###lanim_h_pattern", pattern))
        {
            if(combo.Success)
                foreach(var (pt, ptName, tip) in SizePatterns)
                {
                    if(ImGui.Selectable($"{ptName}###hp_{pt}", h.Pattern == pt))
                    {
                        h.Pattern = pt;
                        changed = true;
                    }
                    Tooltip(tip);
                }
        }
        ImGui.TextDisabled(Array.Find(SizePatterns, x => x.Pattern == h.Pattern).Tip ?? "");
        if(h.Pattern is SizeChangePattern.Pulses or SizeChangePattern.Steps or SizeChangePattern.Spurts)
            changed |= SliderEx("Speed", h.Speed, v => h.Speed = v, 2f, 200f, "%.0f a minute", "How many pulses, steps or spurts a minute.");
        if(h.Pattern is SizeChangePattern.Pulses or SizeChangePattern.Spurts)
            changed |= SliderEx("Strength", h.Strength * 100f, v => h.Strength = v / 100f, 0f, 100f, "%.0f%%",
                "Pulses: how far each one swells past. Spurts: how uneven they are.");

        string repeat = Array.Find(ExpansionRepeats, x => x.Repeat == h.Repeat).Name ?? h.Repeat.ToString();
        ImGui.SetNextItemWidth(-130 * ImGuiHelpers.GlobalScale);
        using(var combo = ImRaii.Combo("At the end###lanim_h_repeat", repeat))
        {
            if(combo.Success)
                foreach(var (r, rName, tip) in ExpansionRepeats)
                {
                    if(ImGui.Selectable($"{rName}###hr_{r}", h.Repeat == r))
                    {
                        h.Repeat = r;
                        changed = true;
                    }
                    Tooltip(tip);
                }
        }
        changed |= SliderEx("Return", h.Return, v => h.Return = MathF.Max(0f, v), 0f, 20f, "%.1f s",
            "Seconds to go back to its own size when stopped.");
        ImGui.TextDisabled("Very big or very small: zoom the camera out (or in) a long way; the gpose camera only goes so far.");

        if(changed)
            _config.Save();
    }

    // The animations "Start all selected" can start: key, name, tooltip.
    private static readonly (string Key, string Name, string Tip)[] Startable =
    [
        ("throb", "Throbbing", "Penis throbbing."),
        ("balls", "Testicle throbbing", "Testicle throbbing (in time with the penis when both are picked)."),
        ("twerk", "Twerking", "Twerking."),
        ("ahegao", "Ahegao", "The ahegao face."),
        ("ex_breasts", "Breast expansion", "Expansion: breasts (started over from their own size)."),
        ("ex_butt", "Butt expansion", "Expansion: butt (started over from its own size)."),
        ("ex_penis", "Penis expansion", "Expansion: penis (started over from its own size)."),
        ("ex_belly", "Belly expansion", "Expansion: belly (started over from its own size)."),
        ("height", "Height growth / shrinking", "The whole character growing or shrinking (started over from its start size)."),
    ];

    private bool IsOn(string name, string key) => key switch
    {
        "throb" => (_config.Throb ??= new()).TryGetValue(name, out var t) && t.Enabled,
        "balls" => (_config.BallThrob ??= new()).TryGetValue(name, out var b) && b.Enabled,
        "twerk" => (_config.Twerk ??= new()).TryGetValue(name, out var w) && w.Enabled,
        "ahegao" => (_config.Ahegao ??= new()).TryGetValue(name, out var a) && a.Enabled,
        "height" => (_config.SizeChange ??= new()).TryGetValue(name, out var h) && h.Enabled,
        _ => (_config.Expansion ??= new()).TryGetValue(name, out var e) && ExpansionPartOf(e, key) is { Enabled: true },
    };

    private static ExpansionPart? ExpansionPartOf(ExpansionSettings e, string key) => key switch
    {
        "ex_breasts" => e.Breasts,
        "ex_butt" => e.Butt,
        "ex_penis" => e.Penis,
        "ex_belly" => e.Belly,
        _ => null,
    };

    private static int ExpansionIndexOf(string key) => key switch
    {
        "ex_breasts" => 0,
        "ex_butt" => 1,
        "ex_penis" => 2,
        "ex_belly" => 3,
        _ => -1,
    };

    private void SetOn(string name, string key, bool on, ActorRef actor)
    {
        switch(key)
        {
            case "throb":
                ((_config.Throb ??= new()).TryGetValue(name, out var t) ? t : _config.Throb[name] = new ThrobSettings()).Enabled = on;
                break;
            case "balls":
                ((_config.BallThrob ??= new()).TryGetValue(name, out var b) ? b : _config.BallThrob[name] = new BallThrobSettings()).Enabled = on;
                break;
            case "twerk":
                ((_config.Twerk ??= new()).TryGetValue(name, out var w) ? w : _config.Twerk[name] = new TwerkSettings()).Enabled = on;
                break;
            case "ahegao":
                ((_config.Ahegao ??= new()).TryGetValue(name, out var a) ? a : _config.Ahegao[name] = new AhegaoSettings()).Enabled = on;
                break;
            case "height":
            {
                var h = (_config.SizeChange ??= new()).TryGetValue(name, out var hs) ? hs : _config.SizeChange[name] = new SizeChangeSettings();
                if(on && !h.Enabled)
                    SizeChange?.Restart(actor.Address);
                h.Enabled = on;
                break;
            }
            default:
            {
                var all = _config.Expansion ??= new();
                if(!all.TryGetValue(name, out var e))
                    all[name] = e = new ExpansionSettings();
                if(ExpansionPartOf(e, key) is not { } part)
                    break;
                bool was = part.Enabled;
                part.Enabled = on;
                if(on && !was)
                    _expansion.Restart(actor.Address, ExpansionIndexOf(key));
                break;
            }
        }
    }

    // At the top: the animations picked here started (or stopped) together with one button.
    private void DrawSelected(string name, ActorRef actor)
    {
        var picked = _config.AnimationsSelected ??= new();
        var available = Array.FindAll(Startable, x => (x.Key != "balls" || BallThrob != null) && (x.Key != "height" || SizeChange != null));
        int count = Array.FindAll(available, x => picked.Contains(x.Key)).Length;
        bool anyOn = Array.Exists(available, x => picked.Contains(x.Key) && IsOn(name, x.Key));

        using(ImRaii.Disabled(count == 0))
        using(ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.25f, 0.5f, 0.3f, 1f)))
        {
            if(ImGui.Button($"Start all selected ({count})###lanim_sel_go", new Vector2(200 * ImGuiHelpers.GlobalScale, 0)))
            {
                foreach(var (key, _, _) in available)
                    if(picked.Contains(key))
                        SetOn(name, key, true, actor);
                // (both throbbings picked: their beats start together)
                if(picked.Contains("throb"))
                    _throb.Sync(actor.Address);
                if(picked.Contains("balls") && BallThrob != null)
                    BallThrob.Sync(actor.Address);
                _config.Save();
            }
        }
        if(ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(count == 0 ? "Tick the animations below first." : "Starts every animation ticked below at once (throbbings in time with each other, expansions from their own size).");
        ImGui.SameLine();
        using(ImRaii.Disabled(!anyOn))
        using(ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.55f, 0.25f, 0.25f, 1f)))
        {
            if(ImGui.Button("Stop all selected###lanim_sel_stop"))
            {
                foreach(var (key, _, _) in available)
                    if(picked.Contains(key))
                        SetOn(name, key, false, actor);
                _config.Save();
            }
        }
        ImGui.SameLine();
        if(ImGui.SmallButton("All###lanim_sel_all"))
        {
            foreach(var x in available)
                picked.Add(x.Key);
            _config.Save();
        }
        ImGui.SameLine();
        if(ImGui.SmallButton("None###lanim_sel_none"))
        {
            picked.Clear();
            _config.Save();
        }

        // The picks, in two columns; each shows whether it's running now.
        using var table = ImRaii.Table("###lanim_sel_table", 2, ImGuiTableFlags.SizingStretchSame);
        if(!table.Success)
            return;
        foreach(var (key, label, tip) in available)
        {
            ImGui.TableNextColumn();
            bool on = picked.Contains(key);
            bool running = IsOn(name, key);
            using(ImRaii.PushColor(ImGuiCol.Text, new Vector4(0.55f, 0.9f, 0.55f, 1f), running))
            {
                if(ImGui.Checkbox($"{label}{(running ? " (on)" : "")}###lanim_sel_{key}", ref on))
                {
                    if(on)
                        picked.Add(key);
                    else
                        picked.Remove(key);
                    _config.Save();
                }
            }
            if(ImGui.IsItemHovered())
                ImGui.SetTooltip(tip + " Its settings are in its section below.");
        }
    }

    private static readonly (ExpansionPattern Pattern, string Name, string Tip)[] ExpansionPatterns =
    [
        (ExpansionPattern.Steady, "Steady", "Grows evenly, the same amount every second."),
        (ExpansionPattern.Smooth, "Smooth", "Eases into growing and eases out as it reaches full size."),
        (ExpansionPattern.Surge, "Surge", "Fast at first, slowing as it fills."),
        (ExpansionPattern.Accelerating, "Accelerating", "Slow at first, then faster and faster."),
        (ExpansionPattern.Pulses, "Pulses", "Grows in pulses, swelling a little past where it's heading on each (Speed, Strength)."),
        (ExpansionPattern.Heartbeat, "Heartbeat", "Grows to a heartbeat: two beats and a rest (Speed, Strength)."),
        (ExpansionPattern.Steps, "Steps", "Sudden jumps in size (one a beat), holding between (Speed)."),
        (ExpansionPattern.Stutter, "Stutter", "Grows, stalls, grows again, in four goes."),
        (ExpansionPattern.Spurts, "Spurts", "Random-sized spurts of growth at uneven moments (Speed, Randomness)."),
        (ExpansionPattern.Pump, "Pump", "Pumped up: each pump swells it, and it settles back a little between (Speed, Strength)."),
        (ExpansionPattern.Overinflate, "Overinflate", "Overshoots its full size, then settles back to it (Strength)."),
        (ExpansionPattern.Wobble, "Wobble", "Grows with a wobble that dies down as it fills (Speed, Strength)."),
        (ExpansionPattern.Breathing, "Breathing", "Swells and shrinks back, over and over, like breathing (Duration is one breath)."),
        (ExpansionPattern.Throbbing, "Throbbing", "Grows, then keeps throbbing bigger on each beat (Speed, Strength)."),
        (ExpansionPattern.Burst, "Burst", "Grows to full and past it, then deflates all at once."),
        (ExpansionPattern.PingPong, "Grow and shrink", "Grows to full, then shrinks back the same way, over and over."),
        (ExpansionPattern.Shrink, "Shrink", "Starts at full size and shrinks back to its own."),
    ];

    private static readonly (ExpansionRepeat Repeat, string Name, string Tip)[] ExpansionRepeats =
    [
        (ExpansionRepeat.Hold, "Hold", "Stays where it ends."),
        (ExpansionRepeat.Loop, "Loop", "Starts over from its own size."),
        (ExpansionRepeat.Reverse, "Back and forth", "Plays back down to its own size, then up again."),
    ];

    private static readonly (string Name, Action<ExpansionPart> Apply)[] ExpansionPresets =
    [
        ("Slow growth", p => { p.Pattern = ExpansionPattern.Smooth; p.Amount = 1f; p.Duration = 30f; p.Repeat = ExpansionRepeat.Hold; }),
        ("Quick swell", p => { p.Pattern = ExpansionPattern.Surge; p.Amount = 0.8f; p.Duration = 4f; p.Repeat = ExpansionRepeat.Hold; }),
        ("Pumping", p => { p.Pattern = ExpansionPattern.Pump; p.Amount = 1.5f; p.Duration = 20f; p.Speed = 50f; p.Strength = 0.7f; p.Repeat = ExpansionRepeat.Hold; }),
        ("Heartbeat", p => { p.Pattern = ExpansionPattern.Heartbeat; p.Amount = 1f; p.Duration = 25f; p.Speed = 70f; p.Strength = 0.6f; p.Repeat = ExpansionRepeat.Hold; }),
        ("Growth spurts", p => { p.Pattern = ExpansionPattern.Spurts; p.Amount = 2f; p.Duration = 20f; p.Speed = 25f; p.Randomness = 0.7f; p.Repeat = ExpansionRepeat.Hold; }),
        ("Overinflate", p => { p.Pattern = ExpansionPattern.Overinflate; p.Amount = 1.5f; p.Duration = 8f; p.Strength = 0.7f; p.Repeat = ExpansionRepeat.Hold; }),
        ("Breathing", p => { p.Pattern = ExpansionPattern.Breathing; p.Amount = 0.4f; p.Duration = 5f; p.Repeat = ExpansionRepeat.Loop; }),
        ("Pop", p => { p.Pattern = ExpansionPattern.Burst; p.Amount = 2f; p.Duration = 12f; p.Strength = 0.6f; p.Repeat = ExpansionRepeat.Loop; }),
        ("Huge", p => { p.Pattern = ExpansionPattern.Accelerating; p.Amount = 4f; p.Duration = 40f; p.Repeat = ExpansionRepeat.Hold; }),
    ];

    // Belly growths that fit it (they set the pattern and timing; Amount 100% = full term).
    private static readonly (string Name, Action<ExpansionPart> Apply)[] BellyPresets =
    [
        ("Pregnancy (slow)", p => { p.Pattern = ExpansionPattern.Smooth; p.Amount = 1f; p.Duration = 90f; p.Repeat = ExpansionRepeat.Hold; }),
        ("Pregnancy (quick)", p => { p.Pattern = ExpansionPattern.Surge; p.Amount = 1f; p.Duration = 12f; p.Repeat = ExpansionRepeat.Hold; }),
        ("Kicks", p => { p.Pattern = ExpansionPattern.Spurts; p.Amount = 1.1f; p.Duration = 40f; p.Speed = 20f; p.Randomness = 0.8f; p.Repeat = ExpansionRepeat.Hold; }),
        ("Inflate", p => { p.Pattern = ExpansionPattern.Pump; p.Amount = 1.5f; p.Duration = 20f; p.Speed = 40f; p.Strength = 0.7f; p.Repeat = ExpansionRepeat.Hold; }),
        ("Stuffed", p => { p.Pattern = ExpansionPattern.Steps; p.Amount = 0.6f; p.Duration = 15f; p.Speed = 12f; p.Repeat = ExpansionRepeat.Hold; }),
        ("Breathing", p => { p.Pattern = ExpansionPattern.Breathing; p.Amount = 0.12f; p.Duration = 4.5f; p.Repeat = ExpansionRepeat.Loop; }),
        ("Overfull", p => { p.Pattern = ExpansionPattern.Overinflate; p.Amount = 2f; p.Duration = 15f; p.Strength = 0.6f; p.Repeat = ExpansionRepeat.Hold; }),
        ("Grow and shrink", p => { p.Pattern = ExpansionPattern.PingPong; p.Amount = 1f; p.Duration = 20f; p.Repeat = ExpansionRepeat.Loop; }),
    ];

    private void DrawExpansion(string name, ActorRef actor)
    {
        var all = _config.Expansion ??= new();
        if(!all.TryGetValue(name, out var s))
            all[name] = s = new ExpansionSettings();
        bool changed = false;

        if(ImGui.SmallButton("Start all###lanim_ex_all_on"))
        {
            s.Breasts.Enabled = s.Butt.Enabled = s.Penis.Enabled = s.Belly.Enabled = true;
            changed = true;
        }
        ImGui.SameLine();
        if(ImGui.SmallButton("Stop all###lanim_ex_all_off"))
        {
            s.Breasts.Enabled = s.Butt.Enabled = s.Penis.Enabled = s.Belly.Enabled = false;
            changed = true;
        }
        ImGui.SameLine();
        if(ImGui.SmallButton("Restart all###lanim_ex_all_re"))
            for(int i = 0; i < 4; i++)
                _expansion.Restart(actor.Address, i);

        foreach(var (part, index, partName, shapeTip) in new[]
        {
            (s.Breasts, 0, "Breasts", "-100%: grows more across (wider). 0: evenly. +100%: grows more forward (projecting)."),
            (s.Butt, 1, "Butt", "-100%: grows more across (wider). 0: evenly. +100%: grows more outward (rounder at the back)."),
            (s.Penis, 2, "Penis", "-100%: mostly thicker. 0: evenly. +100%: mostly longer."),
            (s.Belly, 3, "Belly", "-100%: rounder and wider. 0: in between. +100%: more forward and narrower."),
        })
        {
            ImGui.Spacing();
            using var id = ImRaii.PushId($"lanim_ex_{index}");
            if(!ImGui.TreeNodeEx($"{partName}###lanim_ex_node_{index}"))
                continue;
            if(index == 3)
            {
                // The held pregnancy (always on, under the growth), and what it needs.
                changed |= SliderEx("Pregnant (held)", s.Pregnant * 100f, v => s.Pregnant = v / 100f, -50f, 300f, "%.0f%%",
                    "How pregnant the belly is all the time, under the growth below (100% = full term; below 0 flatter). Stays on until set back to 0.");
                ImGui.TextDisabled("Grows round on the belly rig: Live rig on (Body physics page), or rigged with Rig everything.");
                if(BellyPresets.Length > 0)
                {
                    ImGui.Text("Belly:");
                    int k = 0;
                    foreach(var (presetName, apply) in BellyPresets)
                    {
                        if(k++ % 4 != 0)
                            ImGui.SameLine();
                        if(ImGui.SmallButton($"{presetName}###bpreset_{presetName}"))
                        {
                            apply(s.Belly);
                            changed = true;
                        }
                    }
                }
            }
            changed |= DrawExpansionPart(part, index, partName, shapeTip, actor);
            ImGui.TreePop();
        }

        if(changed)
            _config.Save();
    }

    private bool DrawExpansionPart(ExpansionPart p, int index, string partName, string shapeTip, ActorRef actor)
    {
        bool changed = false;
        string label = p.Enabled ? "Stop###go" : "Start###go";
        using(ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.25f, 0.5f, 0.3f, 1f), !p.Enabled))
        using(ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.55f, 0.25f, 0.25f, 1f), p.Enabled))
        {
            if(ImGui.Button(label, new Vector2(90 * ImGuiHelpers.GlobalScale, 0)))
            {
                p.Enabled = !p.Enabled;
                changed = true;
            }
        }
        ImGui.SameLine();
        if(ImGui.Button("Restart###restart"))
            _expansion.Restart(actor.Address, index);
        Tooltip("Start the growth over from its own size.");
        ImGui.SameLine();
        float grown = _expansion.ShownOf(actor.Address, index);
        if(grown >= 0f)
            ImGui.ProgressBar(Math.Clamp(grown, 0f, 1f), new Vector2(-1, 0), $"+{grown * p.Amount * 100f:0}%");
        else
            ImGui.TextDisabled(p.Enabled ? (index == 2 ? "starting... (no penis bones found?)" : index == 3 ? "starting... (no belly rig?)" : "starting...") : "stopped");

        ImGui.Text("Presets:");
        int n = 0;
        foreach(var (presetName, apply) in ExpansionPresets)
        {
            if(n++ % 5 != 0)
                ImGui.SameLine();
            if(ImGui.SmallButton($"{presetName}###preset_{presetName}"))
            {
                apply(p);
                if(index == 2)
                    p.Amount = MathF.Min(p.Amount, 2f); // (a penis twice as long is already a lot)
                changed = true;
            }
        }

        string current = Array.Find(ExpansionPatterns, x => x.Pattern == p.Pattern).Name ?? p.Pattern.ToString();
        ImGui.SetNextItemWidth(-130 * ImGuiHelpers.GlobalScale);
        using(var combo = ImRaii.Combo("Pattern###pattern", current))
        {
            if(combo.Success)
                foreach(var (pattern, patternName, tip) in ExpansionPatterns)
                {
                    if(ImGui.Selectable($"{patternName}###p_{pattern}", p.Pattern == pattern))
                    {
                        p.Pattern = pattern;
                        changed = true;
                    }
                    Tooltip(tip);
                }
        }
        ImGui.TextDisabled(Array.Find(ExpansionPatterns, x => x.Pattern == p.Pattern).Tip ?? "");

        string repeat = Array.Find(ExpansionRepeats, x => x.Repeat == p.Repeat).Name ?? p.Repeat.ToString();
        ImGui.SetNextItemWidth(-130 * ImGuiHelpers.GlobalScale);
        using(var combo = ImRaii.Combo("At the end###repeat", repeat))
        {
            if(combo.Success)
                foreach(var (r, rName, tip) in ExpansionRepeats)
                {
                    if(ImGui.Selectable($"{rName}###r_{r}", p.Repeat == r))
                    {
                        p.Repeat = r;
                        changed = true;
                    }
                    Tooltip(tip);
                }
        }
        if(p.Repeat == ExpansionRepeat.Hold && p.Pattern is ExpansionPattern.Pulses or ExpansionPattern.Heartbeat or ExpansionPattern.Pump
               or ExpansionPattern.Wobble or ExpansionPattern.Throbbing)
        {
            bool still = p.StillWhenHeld;
            if(ImGui.Checkbox("Still once held###still", ref still))
            {
                p.StillWhenHeld = still;
                changed = true;
            }
            Tooltip("On: once it's grown, the pumping (beats, pulses, wobble) dies away and it stays still at full size.\nOff: it keeps pumping while it's held.");
        }

        changed |= SliderEx("Amount", p.Amount * 100f, v => p.Amount = v / 100f, -90f, 1000f, "+%.0f%%", index == 3
            ? "How much the belly grows at full size (100% = a full-term pregnant belly; 200% and up: more than any real one). Below 0 flattens it."
            : $"How much bigger the {partName.ToLowerInvariant()} get at full size (100% = twice the size). Below 0 shrinks them.");
        changed |= SliderEx("Duration", p.Duration, v => p.Duration = v, 0.5f, 300f, "%.1f s", "Seconds for one growth (or one breath, for Breathing).");
        changed |= SliderEx("Speed", p.Speed, v => p.Speed = v, 5f, 240f, "%.0f /min", "Beats, pumps, steps or wobbles a minute (for the patterns that have them).");
        changed |= SliderEx("Strength", p.Strength * 100f, v => p.Strength = v / 100f, 0f, 100f, "%.0f%%", "How strong the pulses, beats, pumps, wobble or overshoot are.");
        changed |= SliderEx("Randomness", p.Randomness * 100f, v => p.Randomness = v / 100f, 0f, 100f, "%.0f%%", "Uneven beats and spurts.");
        changed |= SliderEx("Shape", p.Shape * 100f, v => p.Shape = v / 100f, -100f, 100f, "%.0f%%", shapeTip);
        if(index == 2)
            changed |= SliderEx("Testicles", p.Testicles * 100f, v => p.Testicles = v / 100f, 0f, 100f, "%.0f%%", "How much the testicles grow with it (100%: as much as the penis).");
        changed |= SliderEx("Ease in / out", p.Ease, v => p.Ease = v, 0.1f, 10f, "%.1f s", "How long it takes to grow back in when started, and to go back to its own size when stopped.");
        return changed;
    }

    // (inside the part's PushId, so the same labels can be used for each part)
    private static bool SliderEx(string label, float value, Action<float> set, float min, float max, string format, string tip)
    {
        ImGui.SetNextItemWidth(-130 * ImGuiHelpers.GlobalScale);
        bool edited = ImGui.SliderFloat($"{label}###lanim_ex_{label}", ref value, min, max, format);
        Tooltip(tip + "\nCtrl+click to type a value.");
        if(edited)
            set(value);
        return edited;
    }

    private void DrawAhegao(string name, ActorRef actor)
    {
        var all = _config.Ahegao ??= new();
        if(!all.TryGetValue(name, out var s))
            all[name] = s = new AhegaoSettings();
        bool changed = false;


        float level = _ahegao.LevelOf(actor.Address);
        using(ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.25f, 0.5f, 0.3f, 1f), !s.Enabled))
        using(ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.55f, 0.25f, 0.25f, 1f), s.Enabled))
        {
            if(ImGui.Button(s.Enabled ? "Stop###lanim_ah_go" : "Start###lanim_ah_go", new Vector2(120 * ImGuiHelpers.GlobalScale, 0)))
            {
                s.Enabled = !s.Enabled;
                changed = true;
            }
        }
        ImGui.SameLine();
        if(level >= 0f)
            ImGui.ProgressBar(Math.Clamp(level, 0f, 1f), new Vector2(-1, 0), "on");
        else
            ImGui.TextDisabled(s.Enabled ? "starting... (no face bones found?)" : "off");

        // Types: each sets the sliders.
        string current = Array.Find(AhegaoTypes, x => x.Type == s.Type).Name ?? s.Type.ToString();
        ImGui.SetNextItemWidth(-130 * ImGuiHelpers.GlobalScale);
        using(var combo = ImRaii.Combo("Type###lanim_ah_type", current))
        {
            if(combo.Success)
                foreach(var (type, typeName, tip) in AhegaoTypes)
                {
                    if(ImGui.Selectable($"{typeName}###lanim_ah_t_{type}", s.Type == type))
                    {
                        s.ApplyType(type);
                        changed = true;
                    }
                    Tooltip(tip + "\n(Sets the sliders below; change them as you like.)");
                }
        }
        ImGui.TextDisabled(Array.Find(AhegaoTypes, x => x.Type == s.Type).Tip ?? "");

        bool hold = s.HoldFace;
        if(ImGui.Checkbox("Hold the face###lanim_ah_hold", ref hold))
        {
            s.HoldFace = hold;
            changed = true;
        }
        Tooltip("While the ahegao is on, emotes and animations can't change the face (/happy, /cheer and so on): it's built on the face at rest instead. "
            + "Turn off to mix it with the face the animation gives (the game's blinking stops while this is on).");

        bool rig = s.FaceRig;
        if(ImGui.Checkbox("Face rig (live mesh)###lanim_ah_rig", ref rig))
        {
            s.FaceRig = rig;
            changed = true;
        }
        Tooltip("Shapes the lips, mouth corners, brows, eyelids and cheeks as smooth curves on the face's own mesh (control points laid "
            + "over the face drawn: any race, any face mod), instead of only moving the face bones. Off: the face bones only.");
        if(s.FaceRig && Limber.Gpu.FaceRig.Status.Length > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(Limber.Gpu.FaceRig.Owner == actor.Address || !Limber.Gpu.FaceRig.Running ? Limber.Gpu.FaceRig.Status : "Face rig: on another character.");
        }

        ImGui.Spacing();
        ImGui.TextDisabled("Eyes");
        changed |= SliderAh("Eyes up", s.EyesUp, v => s.EyesUp = v, 0f, 90f, "%.0f°", "How far the eyes roll up.");
        changed |= SliderAh("Cross-eyed", s.CrossEyes, v => s.CrossEyes = v, 0f, 50f, "%.0f°", "How far the eyes turn in toward each other.");
        changed |= SliderAh("Upper lids", s.Lids * 100f, v => s.Lids = v / 100f, -200f, 200f, "%.0f%%", "How far the upper eyelids droop (+) or open wide (-).");
        changed |= SliderAh("Lower lids", s.LowerLids * 100f, v => s.LowerLids = v / 100f, 0f, 200f, "%.0f%%", "The lower eyelids pushed up: smiling eyes, or a wince.");
        changed |= SliderAh("Brows", s.Brows * 100f, v => s.Brows = v / 100f, -200f, 200f, "%.0f%%", "Eyebrows raised (+) or lowered (-).");
        changed |= SliderAh("Brow tilt", s.BrowTilt * 100f, v => s.BrowTilt = v / 100f, -200f, 200f, "%.0f%%", "The inner ends of the brows up (+, worried / helpless) or down (-, cross).");
        changed |= SliderAh("Furrow", s.Furrow * 100f, v => s.Furrow = v / 100f, 0f, 200f, "%.0f%%", "The brows pulled together and down in the middle (pain, strain).");
        changed |= SliderAh("Tremble", s.Tremble * 100f, v => s.Tremble = v / 100f, 0f, 200f, "%.0f%%", "The eyes trembling.");
        ImGui.TextDisabled("Mouth");
        changed |= SliderAh("Mouth open", s.MouthOpen, v => s.MouthOpen = v, 0f, 70f, "%.0f°", "How far the jaw opens.");
        changed |= SliderAh("Upper corners up / down", s.Smile * 100f, v => s.Smile = v / 100f, -500f, 500f, "%.0f%%", "The upper lip's corners up (+, a smile, lifting the cheeks) or down (-), the lip near them following.");
        changed |= SliderAh("Lower corners up / down", s.SmileLower * 100f, v => s.SmileLower = v / 100f, -500f, 500f, "%.0f%%", "The lower lip's corners up (+) or down (-), the lip near them following.");
        changed |= SliderAh("Upper corners wider", s.CornerWide * 100f, v => s.CornerWide = v / 100f, -500f, 500f, "%.0f%%", "The upper lip's corners further apart (+) or closer together (-).");
        changed |= SliderAh("Lower corners wider", s.CornerWideLower * 100f, v => s.CornerWideLower = v / 100f, -500f, 500f, "%.0f%%", "The lower lip's corners further apart (+) or closer together (-).");
        changed |= SliderAh("Upper corners in / out", s.CornerDepth * 100f, v => s.CornerDepth = v / 100f, -500f, 500f, "%.0f%%", "The upper lip's corners forward (+, pursed) or back into the cheeks (-).");
        changed |= SliderAh("Lower corners in / out", s.CornerDepthLower * 100f, v => s.CornerDepthLower = v / 100f, -500f, 500f, "%.0f%%", "The lower lip's corners forward (+, pursed) or back into the cheeks (-).");
        changed |= SliderAh("Upper lip", s.UpperLip * 100f, v => s.UpperLip = v / 100f, -200f, 200f, "%.0f%%", "The middle of the upper lip up (+) or down (-); the corners stay.");
        changed |= SliderAh("Lower lip", s.LowerLip * 100f, v => s.LowerLip = v / 100f, -200f, 200f, "%.0f%%", "The middle of the lower lip up (+) or down (-); the corners stay.");
        changed |= SliderAh("Upper lip wider", s.UpperLipWide * 100f, v => s.UpperLipWide = v / 100f, -500f, 500f, "%.0f%%", "The upper lip spread out left and right (+) or drawn in narrower (-); its middle stays.");
        changed |= SliderAh("Lower lip wider", s.LowerLipWide * 100f, v => s.LowerLipWide = v / 100f, -500f, 500f, "%.0f%%", "The lower lip spread out left and right (+) or drawn in narrower (-); its middle stays.");
        changed |= SliderAh("Tongue out", s.TongueOut * 100f, v => s.TongueOut = v / 100f, 0f, 400f, "%.0f%%", "How far the tongue comes out (past 100% it reaches further than it naturally would).");
        changed |= SliderAh("Tongue length", s.TongueLength * 100f, v => s.TongueLength = v / 100f, 0f, 200f, "%.0f%%", "The tongue longer.");
        changed |= SliderAh("Tongue droop", s.TongueDroop, v => s.TongueDroop = v, 0f, 120f, "%.0f°", "How far the tongue hangs down over the lip.");
        changed |= SliderAh("Tongue up / down", s.TongueUp, v => s.TongueUp = v, -180f, 180f, "%.0f°", "The tongue pointing up (+, the tip curling up toward the upper lip) or down (-). It still stays over the lower lip.");
        changed |= SliderAh("Tongue follows jaw", s.TongueFollowsJaw * 100f, v => s.TongueFollowsJaw = v / 100f, 0f, 100f, "%.0f%%", "The tongue is attached to the jaw, so opening the mouth swings it down. 100%: it goes all the way with the jaw. Lower: it stays more with the head, so you can pose it on its own.");
        bool collide = s.TongueCollision;
        if(ImGui.Checkbox("Tongue collision###lanim_ah_tcol", ref collide))
        {
            s.TongueCollision = collide;
            changed = true;
        }
        Tooltip("Keeps the tongue off the lips and teeth (with physics on, it bumps off them). Off: it goes exactly where it's posed, even through them.");
        ImGui.SameLine();
        bool physics = s.TonguePhysics;
        if(ImGui.Checkbox("Tongue physics###lanim_ah_tphys", ref physics))
        {
            s.TonguePhysics = physics;
            changed = true;
        }
        Tooltip("Real physics for the tongue: it has a body (a chain of spheres, thinner to the tip) that springs after its pose, sags with gravity and jiggles, and collides with the lips (capsules along their actual bones) and the teeth, so it bumps off them instead of going through. The lips give a little where it presses.");
        if(s.TonguePhysics)
            changed |= SliderAh("Tongue looseness", s.TongueJiggle * 100f, v => s.TongueJiggle = v / 100f, 0f, 200f, "%.0f%%", "0%: firm, follows closely. Higher: looser and floppier, more jiggle.");
        changed |= SliderAh("Tongue wiggle", s.Wiggle * 100f, v => s.Wiggle = v / 100f, 0f, 200f, "%.0f%%", "The tongue moving side to side and up and down.");
        ImGui.TextDisabled("Head and breathing");
        changed |= SliderAh("Head back", s.HeadTilt, v => s.HeadTilt = v, -45f, 70f, "%.0f°", "The head tipped back (chin up); negative tips it forward.");
        changed |= SliderAh("Head to the side", s.HeadSide, v => s.HeadSide = v, -60f, 60f, "%.0f°", "The head leaning to one side.");
        changed |= SliderAh("Panting", s.Pant * 100f, v => s.Pant = v / 100f, 0f, 200f, "%.0f%%", "Quick breaths moving the mouth, tongue and head.");
        changed |= SliderAh("Breaths", s.PantSpeed, v => s.PantSpeed = v, 10f, 400f, "%.0f /min", "How fast the panting is.");
        changed |= SliderAh("Ease in / out", s.Ease, v => s.Ease = v, 0.05f, 10f, "%.2f s", "How long it takes to come on and fade.");

        if(changed)
            _config.Save();
    }

    private static bool SliderAh(string label, float value, Action<float> set, float min, float max, string format, string tip)
    {
        ImGui.SetNextItemWidth(-130 * ImGuiHelpers.GlobalScale);
        bool edited = ImGui.SliderFloat($"{label}###lanim_ah_{label}", ref value, min, max, format);
        Tooltip(tip + "\nCtrl+click to type a value.");
        if(edited)
            set(value);
        return edited;
    }

    private void DrawTwerk(string name, ActorRef actor)
    {
        var all = _config.Twerk ??= new();
        if(!all.TryGetValue(name, out var s))
            all[name] = s = new TwerkSettings();
        bool changed = false;


        float level = _twerk.LevelOf(actor.Address);
        using(ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.25f, 0.5f, 0.3f, 1f), !s.Enabled))
        using(ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.55f, 0.25f, 0.25f, 1f), s.Enabled))
        {
            if(ImGui.Button(s.Enabled ? "Stop###lanim_tw_go" : "Start###lanim_tw_go", new Vector2(120 * ImGuiHelpers.GlobalScale, 0)))
            {
                s.Enabled = !s.Enabled;
                changed = true;
            }
        }
        ImGui.SameLine();
        if(level >= 0f)
        {
            string playing = s.Auto ? Array.Find(TwerkStyles, x => x.Style == TwerkService.StyleAt(s, _twerk.TimeOf(actor.Address))).Name ?? "" : "pop";
            ImGui.ProgressBar(Math.Clamp(level, 0f, 1f), new Vector2(-1, 0), playing);
        }
        else
            ImGui.TextDisabled("stopped");

        ImGui.Text("Presets:");
        foreach(var (presetName, apply) in TwerkPresets)
        {
            ImGui.SameLine();
            if(ImGui.SmallButton($"{presetName}###lanim_tw_preset_{presetName}"))
            {
                apply(s);
                changed = true;
            }
        }

        bool auto = s.Auto;
        if(ImGui.Checkbox("Change styles by itself###lanim_tw_auto", ref auto))
        {
            s.Auto = auto;
            changed = true;
        }
        Tooltip("Moves from one style to another on its own, blending between them.");
        if(s.Auto)
        {
            ImGui.SameLine();
            float secs = s.AutoSeconds;
            ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
            if(ImGui.SliderFloat("every###lanim_tw_auto_secs", ref secs, 3f, 120f, "%.0f s"))
            {
                s.AutoSeconds = secs;
                changed = true;
            }
        }
        using(ImRaii.Disabled(s.Auto))
        {
            string current = Array.Find(TwerkStyles, x => x.Style == s.Style).Name ?? s.Style.ToString();
            ImGui.SetNextItemWidth(-130 * ImGuiHelpers.GlobalScale);
            using(var combo = ImRaii.Combo("Style###lanim_tw_style", current))
            {
                if(combo.Success)
                    foreach(var (style, styleName, tip) in TwerkStyles)
                    {
                        if(ImGui.Selectable($"{styleName}###lanim_tw_s_{style}", s.Style == style))
                        {
                            s.Style = style;
                            changed = true;
                        }
                        Tooltip(tip);
                    }
            }
        }
        if(!s.Auto)
            ImGui.TextDisabled(Array.Find(TwerkStyles, x => x.Style == s.Style).Tip ?? "");

        string poseName = Array.Find(TwerkPoses, x => x.Pose == s.Pose).Name ?? s.Pose.ToString();
        ImGui.SetNextItemWidth(-130 * ImGuiHelpers.GlobalScale);
        using(var poseCombo = ImRaii.Combo("Position###lanim_tw_pose", poseName))
        {
            if(poseCombo.Success)
                foreach(var (pose, name2, tip) in TwerkPoses)
                {
                    if(ImGui.Selectable($"{name2}###lanim_tw_p_{pose}", s.Pose == pose))
                    {
                        s.Pose = pose;
                        changed = true;
                    }
                    Tooltip(tip);
                }
        }
        if(s.Pose == TwerkPose.AllFours)
            ImGui.TextDisabled("On all fours the hands go flat on the floor.");

        string hands = Array.Find(TwerkHandPlaces, x => x.Hands == s.Hands).Name ?? s.Hands.ToString();
        ImGui.SetNextItemWidth(-130 * ImGuiHelpers.GlobalScale);
        using(var handsCombo = ImRaii.Combo("Hands###lanim_tw_hands", hands))
        {
            if(handsCombo.Success)
                foreach(var (place, placeName) in TwerkHandPlaces)
                    if(ImGui.Selectable($"{placeName}###lanim_tw_h_{place}", s.Hands == place))
                    {
                        s.Hands = place;
                        changed = true;
                    }
        }

        ImGui.Spacing();
        ImGui.TextDisabled("How it moves");
        changed |= SliderTw("Speed", s.Speed, v => s.Speed = v, 40f, 320f, "%.0f pops/min", "How fast the hips pop (about 170 matches most twerk music, two pops a beat).");
        changed |= SliderTw("Hip swing", s.Swing, v => s.Swing = v, 0f, 45f, "%.0f°", "How far the hips swing on each pop.");
        changed |= SliderTw("Bounce", s.Bounce, v => s.Bounce = v, 0f, 8f, "%.1f cm", "How much the body dips with each pop.");
        changed |= SliderTw("Squat", s.Depth * 100f, v => s.Depth = v / 100f, 0f, 100f, "%.0f%%", "How low the squat is.");
        changed |= SliderTw("Lean", s.Lean, v => s.Lean = v, 0f, 80f, "%.0f°", "How far the upper body leans forward (more or less than the position's own lean).");
        changed |= SliderTw("Stance", s.Width * 100f, v => s.Width = v / 100f, 0f, 100f, "%.0f%%", "How much wider the feet are than in the animation.");
        changed |= SliderTw("Isolation", s.Isolation * 100f, v => s.Isolation = v / 100f, 0f, 100f, "%.0f%%", "How still the upper body keeps while the hips move (higher = only the hips move).");
        changed |= SliderTw("Randomness", s.Randomness * 100f, v => s.Randomness = v / 100f, 0f, 100f, "%.0f%%", "0%: every pop the same. Higher: some stronger, some softer.");
        changed |= SliderTw("Ease in / out", s.Ease, v => s.Ease = v, 0.1f, 4f, "%.1f s", "How long it takes to get into and out of the squat.");

        if(changed)
            _config.Save();
    }

    private void DrawThrob(string name, ActorRef actor)
    {
        var all = _config.Throb ??= new();
        if(!all.TryGetValue(name, out var s))
            all[name] = s = new ThrobSettings();
        bool changed = false;


        // Start / stop and the beat right now.
        float level = _throb.LevelOf(actor.Address);
        string label = s.Enabled ? "Stop###lanim_throb_go" : "Start###lanim_throb_go";
        using(ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.25f, 0.5f, 0.3f, 1f), !s.Enabled))
        using(ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.55f, 0.25f, 0.25f, 1f), s.Enabled))
        {
            if(ImGui.Button(label, new Vector2(120 * ImGuiHelpers.GlobalScale, 0)))
            {
                s.Enabled = !s.Enabled;
                changed = true;
            }
        }
        ImGui.SameLine();
        if(level >= 0f)
            ImGui.ProgressBar(Math.Clamp(level, 0f, 1f), new Vector2(-1, 0), "beat");
        else
            ImGui.TextDisabled(s.Enabled ? "starting... (no penis bones found on this character?)" : "stopped");

        // Presets.
        ImGui.Text("Presets:");
        foreach(var (presetName, apply) in Presets)
        {
            ImGui.SameLine();
            if(ImGui.SmallButton($"{presetName}###lanim_preset_{presetName}"))
            {
                apply(s);
                changed = true;
            }
        }

        // Pattern.
        bool auto = s.Auto;
        if(ImGui.Checkbox("Change patterns by itself###lanim_auto", ref auto))
        {
            s.Auto = auto;
            changed = true;
        }
        Tooltip("Moves from one pattern to another on its own (blending between them), like a real rhythm that keeps changing.");
        if(s.Auto)
        {
            ImGui.SameLine();
            float secs = s.AutoSeconds;
            ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
            if(ImGui.SliderFloat("every###lanim_auto_secs", ref secs, 3f, 120f, "%.0f s"))
            {
                s.AutoSeconds = secs;
                changed = true;
            }
        }
        using(ImRaii.Disabled(s.Auto))
        {
            string current = Array.Find(Patterns, p => p.Pattern == s.Pattern).Name ?? s.Pattern.ToString();
            ImGui.SetNextItemWidth(-130 * ImGuiHelpers.GlobalScale);
            using(var combo = ImRaii.Combo("Pattern###lanim_pattern", current))
            {
                if(combo.Success)
                    foreach(var (pattern, patternName, tip) in Patterns)
                    {
                        if(ImGui.Selectable($"{patternName}###lanim_p_{pattern}", s.Pattern == pattern))
                        {
                            s.Pattern = pattern;
                            changed = true;
                        }
                        Tooltip(tip);
                    }
            }
        }
        if(!s.Auto)
            ImGui.TextDisabled(Array.Find(Patterns, p => p.Pattern == s.Pattern).Tip ?? "");

        // How it beats.
        ImGui.Spacing();
        ImGui.TextDisabled("How it beats");
        changed |= Slider("Speed", s.Speed, v => s.Speed = v, 10f, 200f, "%.0f beats/min", "How fast it beats.");
        changed |= Slider("Swell", s.Swell * 100f, v => s.Swell = v / 100f, 0f, 50f, "%.0f%%", "How much thicker it gets on a beat.");
        changed |= Slider("Tip swell", s.TipSwell * 100f, v => s.TipSwell = v / 100f, 0f, 100f, "%.0f%%", "0%: the whole shaft swells evenly. 100%: mostly the tip.");
        changed |= Slider("Lengthen", s.Length * 100f, v => s.Length = v / 100f, 0f, 20f, "%.1f%%", "How much longer it gets on a beat.");
        changed |= Slider("Rise", s.Lift, v => s.Lift = v, 0f, 40f, "%.0f°", "How far it rises as the throb happens.");
        changed |= Slider("Fall", s.Fall, v => s.Fall = v, 0f, 40f, "%.0f°", "How far it drops below where it was as the throb ends, before settling back.");
        changed |= Slider("Travel", s.Travel * 100f, v => s.Travel = v / 100f, 0f, 100f, "%.0f%%", "0%: the whole shaft at once. Higher: the beat rolls from the base to the tip.");
        changed |= Slider("Randomness", s.Randomness * 100f, v => s.Randomness = v / 100f, 0f, 100f, "%.0f%%", "0%: machine-like. Higher: uneven timing and strength, more natural.");
        changed |= Slider("Testicles", s.Testicles * 100f, v => s.Testicles = v / 100f, 0f, 100f, "%.0f%%", "How much the testicles draw up on the strong beats.");
        changed |= Slider("Oral boost", s.OralBoost * 100f, v => s.OralBoost = v / 100f, 0f, 200f, "+%.0f%%", "Stronger while this character's shaft is in Oral mode.");
        changed |= Slider("Ease in / out", s.Ease, v => s.Ease = v, 0.1f, 6f, "%.1f s", "How long it takes to start and stop.");

        if(changed)
            _config.Save();
    }

    private static readonly (BallThrobPattern Pattern, string Name, string Tip)[] BallPatterns =
    [
        (BallThrobPattern.Pulse, "Pulse", "An even pulse on every beat: a little bigger and drawn up."),
        (BallThrobPattern.Heartbeat, "Heartbeat", "Two pulses close together, then a rest."),
        (BallThrobPattern.Clench, "Clench", "Drawn up tight and held a moment, then let go."),
        (BallThrobPattern.Bounce, "Bounce", "Bobbing up and down."),
        (BallThrobPattern.Pump, "Pump", "Rhythmic contractions: quickly up, slowly down."),
        (BallThrobPattern.Alternate, "Alternate", "Left, then right."),
        (BallThrobPattern.Shiver, "Shiver", "A fast tremble."),
        (BallThrobPattern.Rolling, "Rolling", "Round in small circles, each its own way."),
        (BallThrobPattern.Climax, "Climax", "Strong contractions, close together, then further apart and weaker; a rest; again."),
        (BallThrobPattern.Twitch, "Twitch", "Sudden jerks at random moments."),
        (BallThrobPattern.Breathing, "Breathing", "Slow swelling and easing."),
        (BallThrobPattern.WithPenis, "With the penis", "On the penis throbbing's beat (turn that on too)."),
    ];

    private void DrawBallThrob(string name, ActorRef actor)
    {
        var all = _config.BallThrob ??= new();
        if(!all.TryGetValue(name, out var s))
            all[name] = s = new BallThrobSettings();
        bool changed = false;

        float level = BallThrob!.LevelOf(actor.Address);
        string label = s.Enabled ? "Stop###lanim_balls_go" : "Start###lanim_balls_go";
        using(ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.25f, 0.5f, 0.3f, 1f), !s.Enabled))
        using(ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.55f, 0.25f, 0.25f, 1f), s.Enabled))
        {
            if(ImGui.Button(label, new Vector2(120 * ImGuiHelpers.GlobalScale, 0)))
            {
                s.Enabled = !s.Enabled;
                changed = true;
            }
        }
        ImGui.SameLine();
        if(level >= 0f)
            ImGui.ProgressBar(Math.Clamp(level, 0f, 1f), new Vector2(-1, 0), "beat");
        else
            ImGui.TextDisabled(s.Enabled ? "starting... (no testicle bones found on this character?)" : "stopped");

        bool auto = s.Auto;
        if(ImGui.Checkbox("Change patterns by itself###lanim_balls_auto", ref auto))
        {
            s.Auto = auto;
            changed = true;
        }
        Tooltip("Moves from one pattern to another on its own (blending between them).");
        if(s.Auto)
        {
            ImGui.SameLine();
            float secs = s.AutoSeconds;
            ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
            if(ImGui.SliderFloat("every###lanim_balls_auto_secs", ref secs, 3f, 120f, "%.0f s"))
            {
                s.AutoSeconds = secs;
                changed = true;
            }
        }
        using(ImRaii.Disabled(s.Auto))
        {
            string current = Array.Find(BallPatterns, p => p.Pattern == s.Pattern).Name ?? s.Pattern.ToString();
            ImGui.SetNextItemWidth(-130 * ImGuiHelpers.GlobalScale);
            using(var combo = ImRaii.Combo("Pattern###lanim_balls_pattern", current))
            {
                if(combo.Success)
                    foreach(var (pattern, patternName, tip) in BallPatterns)
                    {
                        if(ImGui.Selectable($"{patternName}###lanim_balls_p_{pattern}", s.Pattern == pattern))
                        {
                            s.Pattern = pattern;
                            changed = true;
                        }
                        Tooltip(tip);
                    }
            }
        }
        if(!s.Auto)
            ImGui.TextDisabled(Array.Find(BallPatterns, p => p.Pattern == s.Pattern).Tip ?? "");

        changed |= SliderBt("Speed", s.Speed, v => s.Speed = v, 10f, 240f, "%.0f beats/min", "How fast it beats.");
        changed |= SliderBt("Swell", s.Swell * 100f, v => s.Swell = v / 100f, 0f, 50f, "%.0f%%", "How much bigger on a beat.");
        changed |= SliderBt("Draw up", s.DrawUp, v => s.DrawUp = v, 0f, 4f, "%.1f cm", "How far they draw up toward the body on a beat.");
        changed |= SliderBt("Squeeze", s.Squeeze * 100f, v => s.Squeeze = v / 100f, 0f, 100f, "%.0f%%", "How much they tighten as they draw up.");
        changed |= SliderBt("Bounce", s.Bounce, v => s.Bounce = v, 0f, 3f, "%.1f cm", "Up and down (most in Bounce, Rolling and Shiver).");
        changed |= SliderBt("Sway", s.Sway, v => s.Sway = v, 0f, 3f, "%.1f cm", "Side to side (most in Rolling and Shiver).");
        changed |= SliderBt("Left / right offset", s.Offset * 100f, v => s.Offset = v / 100f, 0f, 100f, "%.0f%%", "0%: both together. Higher: the second a little after the first.");
        changed |= SliderBt("Randomness", s.Randomness * 100f, v => s.Randomness = v / 100f, 0f, 100f, "%.0f%%", "0%: machine-like. Higher: uneven timing and strength.");
        changed |= SliderBt("Ease in / out", s.Ease, v => s.Ease = v, 0.1f, 6f, "%.1f s", "How long it takes to start and stop.");
        if(changed)
            _config.Save();
    }

    private static bool SliderBt(string label, float value, Action<float> set, float min, float max, string format, string tip)
    {
        ImGui.SetNextItemWidth(-130 * ImGuiHelpers.GlobalScale);
        bool edited = ImGui.SliderFloat($"{label}###lanim_balls_{label}", ref value, min, max, format);
        Tooltip(tip + "\nCtrl+click to type a value.");
        if(edited)
            set(value);
        return edited;
    }

    private static bool SliderTw(string label, float value, Action<float> set, float min, float max, string format, string tip)
    {
        ImGui.SetNextItemWidth(-130 * ImGuiHelpers.GlobalScale);
        bool edited = ImGui.SliderFloat($"{label}###lanim_tw_{label}", ref value, min, max, format);
        Tooltip(tip + "\nCtrl+click to type a value.");
        if(edited)
            set(value);
        return edited;
    }

    private static bool Slider(string label, float value, Action<float> set, float min, float max, string format, string tip)
    {
        ImGui.SetNextItemWidth(-130 * ImGuiHelpers.GlobalScale);
        bool edited = ImGui.SliderFloat($"{label}###lanim_{label}", ref value, min, max, format);
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
