using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Limber.Core;
using Limber.Rig;

namespace Limber.UI;

/// <summary>
/// The Size &amp; reactions page: the chosen character reacts to a partner's size (face, hand gesture, where the hand
/// goes, who they look at, a looping motion), small looping motions on their own, a fingertip hold, and a ruler along
/// the shaft with a comparison of everyone's. Adult characters only.
/// </summary>
public sealed class ReactionsTab
{
    private readonly Selection _selection;
    private readonly RigSettings _config;
    private readonly RigService _rigs;
    private readonly ReactionMotionService _motions;
    private readonly SkeletonInfoCache _cache = new();
    private string _partner = "";
    private bool _right = true;
    private bool _withMotion = true;
    private float _gap = 0.35f;
    private ReactionKind? _last;
    private string _note = "";

    public ReactionsTab(Selection selection, RigSettings config, RigService rigs, ReactionMotionService motions)
    {
        _selection = selection;
        _config = config;
        _rigs = rigs;
        _motions = motions;
    }

    private static void Tooltip(string text)
    {
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip(text);
    }

    private ActorRef PartnerRef()
    {
        if(_partner.Length == 0)
            return default;
        foreach(var c in _selection.ListCharacters())
            if(c.Name == _partner)
                return c;
        return default;
    }

    public void Draw()
    {
        var actor = _selection.Actor;
        if(!actor.IsSet)
        {
            ImGui.TextDisabled("Choose a character at the top of the window.");
            return;
        }
        string name = actor.Name ?? "";
        if(name.Length == 0)
        {
            ImGui.TextDisabled("This character has no name to keep the settings under.");
            return;
        }
        if(!ReactionSafety.Adult(actor.Address))
        {
            ImGui.TextDisabled("This page is for adult characters only.");
            DrawRulerSection(actor);
            return;
        }

        if(ImGui.CollapsingHeader("Reactions###rx_head", ImGuiTreeNodeFlags.DefaultOpen))
            DrawReactions(actor, name);
        if(ImGui.CollapsingHeader("Looping motions###rx_motion_head", ImGuiTreeNodeFlags.DefaultOpen))
            DrawMotions(actor, name);
        if(ImGui.CollapsingHeader("Fingertip hold###rx_hold_head"))
            DrawHold(actor);
        DrawRulerSection(actor);
    }

    // ---- Reactions

    private void DrawReactions(ActorRef actor, string name)
    {
        ImGui.TextWrapped($"{name} reacts to someone's size: a face, a hand gesture, where the hand goes and who they look at, "
            + "all in one click. \"Take it off\" puts back what they had before.");
        ImGui.Spacing();

        // Whose size.
        ImGui.SetNextItemWidth(240 * ImGuiHelpers.GlobalScale);
        using(var combo = ImRaii.Combo("Reacting to###rx_partner", _partner.Length == 0 ? "(choose)" : _partner))
        {
            if(combo)
            {
                var seen = new HashSet<string>();
                foreach(var other in _selection.ListCharacters())
                {
                    if(string.IsNullOrEmpty(other.Name) || other.Name == name || !seen.Add(other.Name) || !ReactionSafety.Adult(other.Address))
                        continue;
                    if(ImGui.Selectable($"{other.Name}###rx_p_{other.ObjectIndex}", _partner == other.Name))
                        _partner = other.Name;
                }
            }
        }
        Tooltip("Whose size they react to: they look at it, and pointing aims at it.");
        ImGui.SameLine();
        ImGui.Text("Hand:");
        ImGui.SameLine();
        if(ImGui.RadioButton("Right###rx_r", _right))
            _right = true;
        ImGui.SameLine();
        if(ImGui.RadioButton("Left###rx_l", !_right))
            _right = false;
        ImGui.Checkbox("With its motion###rx_withmotion", ref _withMotion);
        Tooltip("Giggle, eye roll, pinky wiggle and point-and-laugh come with a looping motion (shaking with giggles, a head shake...).");

        var partner = PartnerRef();
        float w = 130 * ImGuiHelpers.GlobalScale;
        int n = 0;
        foreach(var (kind, label, help) in Reactions.All)
        {
            if(n++ % 4 != 0)
                ImGui.SameLine();
            if(ImGui.Button($"{label}###rx_k_{kind}", new Vector2(w, 0)))
            {
                _note = Reactions.Apply(_config, _rigs, actor, partner, kind, _right, _gap, _withMotion);
                _last = kind;
            }
            Tooltip(help + (partner.IsSet ? "" : "\n(Choose who they're reacting to above, so they look at it.)"));
        }
        ImGui.SliderFloat("\"This small\" gap###rx_gap", ref _gap, 0f, 1f, "%.2f");
        // (re-applied when the slider is let go)
        if(ImGui.IsItemDeactivatedAfterEdit() && _last == ReactionKind.ThisSmall && Reactions.IsOn(name))
            _note = Reactions.Apply(_config, _rigs, actor, partner, ReactionKind.ThisSmall, _right, _gap, _withMotion);
        Tooltip("How far apart the thumb and finger are for \"This small\" (0 = touching).");

        using(ImRaii.Disabled(!Reactions.IsOn(name)))
            if(ImGui.Button("Take it off###rx_clear"))
            {
                _note = Reactions.Clear(_config, _rigs, actor);
                _last = null;
            }
        Tooltip("Takes the reaction off: the face, look, motion and hand go back to what they were.");
        if(_note.Length > 0)
            ImGui.TextDisabled(_note);
        ImGui.TextDisabled("The hand is placed by IK: drag its dot to fine-tune. The face uses the ahegao face's controls (Limber Animations page).");
        ImGui.Spacing();
    }

    // ---- Looping motions

    private void DrawMotions(ActorRef actor, string name)
    {
        var all = _config.ReactionMotions ??= new();
        if(!all.TryGetValue(name, out var s))
            all[name] = s = new ReactionMotionSettings();
        bool changed = false;

        using(ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.25f, 0.5f, 0.3f, 1f), !s.Enabled))
        using(ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.55f, 0.25f, 0.25f, 1f), s.Enabled))
        {
            if(ImGui.Button(s.Enabled ? "Stop###rx_m_go" : "Start###rx_m_go", new Vector2(100 * ImGuiHelpers.GlobalScale, 0)))
            {
                s.Enabled = !s.Enabled;
                changed = true;
            }
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(180 * ImGuiHelpers.GlobalScale);
        using(var combo = ImRaii.Combo("Motion###rx_m_kind", Label(s.Kind)))
        {
            if(combo)
                foreach(ReactionMotionKind k in Enum.GetValues<ReactionMotionKind>())
                    if(ImGui.Selectable($"{Label(k)}###rx_m_{k}", s.Kind == k))
                    {
                        s.Kind = k;
                        changed = true;
                    }
        }
        if(s.Kind is ReactionMotionKind.PinkyWiggle or ReactionMotionKind.FingerFlick)
        {
            ImGui.SameLine();
            bool right = s.RightHand;
            if(ImGui.Checkbox("Right hand###rx_m_right", ref right))
            {
                s.RightHand = right;
                changed = true;
            }
        }
        float speed = s.Speed, strength = s.Strength;
        if(ImGui.SliderFloat("Speed###rx_m_speed", ref speed, 0.3f, 2.5f, "%.2fx"))
        {
            s.Speed = speed;
            changed = true;
        }
        if(ImGui.SliderFloat("Strength###rx_m_strength", ref strength, 0f, 2.5f, "%.2fx"))
        {
            s.Strength = strength;
            changed = true;
        }
        ImGui.TextDisabled(s.Enabled ? (_motions.IsRunning(actor.Address) ? "playing" : "starting...") : "off (Limber Animations never start by themselves)");
        if(changed)
            _config.Save();
        ImGui.Spacing();
    }

    private static string Label(ReactionMotionKind k) => k switch
    {
        ReactionMotionKind.Giggle => "Giggling",
        ReactionMotionKind.Laugh => "Laughing",
        ReactionMotionKind.HeadShake => "Head shake (\"no\")",
        ReactionMotionKind.PinkyWiggle => "Little finger wiggle",
        ReactionMotionKind.FingerFlick => "Finger flick",
        _ => k.ToString(),
    };

    // ---- Fingertip hold

    private void DrawHold(ActorRef actor)
    {
        ImGui.TextWrapped("Holds the partner's shaft with just the thumb and index finger, the other fingers held up and away. "
            + "The hand stays on it as it moves. Uses the partner and hand chosen under Reactions.");
        var partner = PartnerRef();
        using(ImRaii.Disabled(!partner.IsSet))
            if(ImGui.Button("Hold it with two fingers###rx_hold"))
                _note = Reactions.FingertipHold(_rigs, actor, partner, _right);
        if(!partner.IsSet)
            ImGui.TextDisabled("Choose who they're reacting to first.");
        ImGui.Spacing();
    }

    // ---- Ruler and comparison

    private void DrawRulerSection(ActorRef actor)
    {
        if(!ImGui.CollapsingHeader("Measure###rx_measure_head", ImGuiTreeNodeFlags.DefaultOpen))
            return;
        bool changed = false;
        bool show = _config.ShowRuler, everyone = _config.RulerEveryone;
        if(ImGui.Checkbox("Show the ruler###rx_ruler", ref show))
        {
            _config.ShowRuler = show;
            changed = true;
        }
        Tooltip("A ruler along the shaft in the scene: a tick every centimetre (or half inch), the length at the tip.");
        ImGui.SameLine();
        if(ImGui.Checkbox("On everyone###rx_ruler_all", ref everyone))
        {
            _config.RulerEveryone = everyone;
            changed = true;
        }
        Tooltip("On: everyone with a shaft. Off: only the chosen character.");
        ImGui.SameLine();
        int units = _config.RulerUnits;
        ImGui.SetNextItemWidth(120 * ImGuiHelpers.GlobalScale);
        if(ImGui.Combo("Units###rx_units", ref units, "cm\0inches\0both\0"))
        {
            _config.RulerUnits = units;
            changed = true;
        }
        if(changed)
            _config.Save();

        // Everyone's, longest first, as bars.
        var rows = new List<(string Name, float Length)>();
        var seen = new HashSet<nint>();
        foreach(var c in _selection.ListCharacters())
        {
            if(!seen.Add(c.Address))
                continue;
            if(ShaftMeasure.TryMeasure(c.Address, c.ObjectIndex, _cache, out _, out var len))
                rows.Add((string.IsNullOrEmpty(c.Name) ? $"#{c.ObjectIndex}" : c.Name, len));
        }
        if(rows.Count == 0)
        {
            ImGui.TextDisabled("Nobody here has shaft bones to measure (a body mod with iv_ochinko bones is needed).");
            return;
        }
        rows.Sort((a, b) => b.Length.CompareTo(a.Length));
        float longest = rows[0].Length;
        float barW = MathF.Max(80f, ImGui.GetContentRegionAvail().X - 330 * ImGuiHelpers.GlobalScale);
        foreach(var (who, len) in rows)
        {
            ImGui.Text(who);
            ImGui.SameLine(170 * ImGuiHelpers.GlobalScale);
            var at = ImGui.GetCursorScreenPos();
            float h = ImGui.GetTextLineHeight();
            var draw = ImGui.GetWindowDrawList();
            draw.AddRectFilled(at, at + new Vector2(barW * (len / longest), h), 0xC0C080E0, 3f);
            ImGui.Dummy(new Vector2(barW, h));
            ImGui.SameLine();
            string ratio = len < longest - 1e-4f ? $"  (1/{longest / len:0.0} of the longest)" : "";
            ImGui.Text(ShaftMeasure.Format(len, _config.RulerUnits) + ratio);
        }
        ImGui.TextDisabled("Measured along the shaft's bones, base to tip, in the game's metres.");
    }

    /// <summary>The ruler in the scene (drawn every frame while on, the page open or not).</summary>
    public void DrawOverlay(Dalamud.Plugin.Services.IGameGui gui)
    {
        if(!_config.ShowRuler)
            return;
        var targets = new List<ActorRef>();
        if(_config.RulerEveryone)
        {
            var seen = new HashSet<nint>();
            foreach(var c in _selection.ListCharacters())
                if(seen.Add(c.Address))
                    targets.Add(c);
        }
        else if(_selection.Actor.IsSet)
            targets.Add(_selection.Actor);
        var draw = ImGui.GetBackgroundDrawList();
        foreach(var c in targets)
        {
            if(!ShaftMeasure.TryMeasure(c.Address, c.ObjectIndex, _cache, out var points, out var length))
                continue;
            DrawRuler(gui, draw, points, length);
        }
    }

    private void DrawRuler(Dalamud.Plugin.Services.IGameGui gui, ImDrawListPtr draw, Vector3[] points, float length)
    {
        const uint line = 0xE0FFFFFF, tick = 0xE0FFFFFF, shadow = 0xA0000000, label = 0xFF80E0FF;
        bool inches = _config.RulerUnits == 1;
        float step = inches ? 0.0127f : 0.01f;        // a tick every half inch / centimetre
        int bigEvery = inches ? 2 : 5;                // a long tick (and number) every inch / 5 cm
        var screen = new Vector2[points.Length];
        for(int i = 0; i < points.Length; i++)
            if(!gui.WorldToScreen(points[i], out screen[i], out bool inView) || !inView)
                return;
        for(int i = 1; i < screen.Length; i++)
        {
            draw.AddLine(screen[i - 1], screen[i], shadow, 4f);
            draw.AddLine(screen[i - 1], screen[i], line, 2f);
        }
        // Ticks: walk along the bent line in the world, a tick at each step.
        float along = 0f;
        int count = 0;
        float next = 0f;
        for(int i = 1; i < points.Length; i++)
        {
            float seg = Vector3.Distance(points[i - 1], points[i]);
            if(seg < 1e-6f)
                continue;
            while(next <= along + seg + 1e-6f)
            {
                float u = (next - along) / seg;
                var p = Vector2.Lerp(screen[i - 1], screen[i], u);
                var dir = screen[i] - screen[i - 1];
                var perp = dir.LengthSquared() > 1e-6f ? Vector2.Normalize(new Vector2(-dir.Y, dir.X)) : Vector2.UnitY;
                bool big = count % bigEvery == 0;
                float len = (big ? 9f : 5f) * ImGuiHelpers.GlobalScale;
                draw.AddLine(p - perp * len, p + perp * len, tick, big ? 2f : 1f);
                if(big && count > 0)
                {
                    string text = inches ? $"{count / 2}" : $"{count}";
                    draw.AddText(p + perp * (len + 2f), label, text);
                }
                count++;
                next += step;
                if(count > 400)
                    break;
            }
            along += seg;
        }
        // The length at the tip.
        var tip = screen[^1];
        string total = ShaftMeasure.Format(length, _config.RulerUnits);
        var size = ImGui.CalcTextSize(total);
        var at = tip + new Vector2(8f, -size.Y * 0.5f);
        draw.AddRectFilled(at - new Vector2(4f, 2f), at + size + new Vector2(4f, 2f), 0xC0000000, 4f);
        draw.AddText(at, label, total);
    }
}
