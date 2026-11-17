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
/// The Climax page (Scene): invisible emitters that shoot a thick, sticky liquid in spurts, on a character's bone or
/// placed in the world; it sticks to the bodies it hits, runs, drips and pools. Adult characters only.
/// </summary>
public sealed class ClimaxTab
{
    private readonly ClimaxService _climax;
    private readonly Selection _selection;
    private readonly RigSettings _config;
    private bool _dirty;

    private static readonly (string Bone, string Name)[] SourceBones =
    [
        ("iv_ochinko_f", "Penis tip"), ("iv_ochinko_e", "Penis (near the tip)"), ("iv_ochinko_d", "Penis (middle)"),
        ("j_kosi", "Hips"), ("j_kao", "Head"), ("j_te_r", "Right hand"), ("j_te_l", "Left hand"), ("j_sebo_c", "Chest"),
    ];

    private static readonly (string Bone, string Name)[] TargetBones =
    [
        ("j_kao", "Face"), ("j_kubi", "Neck"), ("j_sebo_c", "Chest"), ("j_mune_l", "Left breast"), ("j_mune_r", "Right breast"),
        ("j_sebo_b", "Belly (upper)"), ("j_sebo_a", "Belly"), ("j_kosi", "Hips"), ("j_asi_a_l", "Left thigh"), ("j_asi_a_r", "Right thigh"),
        ("j_te_l", "Left hand"), ("j_te_r", "Right hand"),
    ];

    /// <summary>The aim wireframes of the emitters that show it (drawn every frame, the page open or not).</summary>
    public void DrawOverlay(Dalamud.Plugin.Services.IGameGui gui)
    {
        var emitters = _config.ClimaxEmitters;
        if(emitters == null || emitters.Count == 0)
            return;
        var draw = ImGui.GetBackgroundDrawList();
        bool Screen(Vector3 w, out Vector2 at) => gui.WorldToScreen(w, out at, out bool shown) && shown;
        void Line(Vector3 a, Vector3 b, uint color, float width)
        {
            if(Screen(a, out var sa) && Screen(b, out var sb))
                draw.AddLine(sa, sb, color, width);
        }
        foreach(var e in emitters)
        {
            if(!e.ShowAim || !_climax.TryAim(e.Id, out var aim))
                continue;
            var dir = aim.Dir;
            var right = Vector3.Cross(aim.Up, dir);
            if(right.LengthSquared() < 1e-6f)
                right = Vector3.Cross(Vector3.UnitX, dir);
            right = Vector3.Normalize(right);
            var up = Vector3.Cross(dir, right);
            const uint cyan = 0xFFFFE040, yellow = 0xFF40E0FF, orange = 0xC02090FF, pink = 0xFFC060FF, faint = 0x80FFE040;

            // the emitter: a small cross, and an arrow along the aim
            float s = 0.02f;
            Line(aim.At - right * s, aim.At + right * s, cyan, 2f);
            Line(aim.At - up * s, aim.At + up * s, cyan, 2f);
            Line(aim.At - dir * s, aim.At + dir * s, cyan, 2f);
            Line(aim.At, aim.At + dir * 0.15f, cyan, 2f);

            // the spread (and sway) as a cone 25 cm out
            float spread = (e.Spread + e.Sway + e.AimRandom) * MathF.PI / 180f;
            float reach = 0.25f, radius = MathF.Tan(MathF.Min(spread, 1.3f)) * reach;
            var ringAt = aim.At + dir * reach;
            Vector3? lastRing = null;
            for(int k = 0; k <= 24; k++)
            {
                float t = k / 24f * MathF.Tau;
                var p = ringAt + (right * MathF.Cos(t) + up * MathF.Sin(t)) * radius;
                if(lastRing is { } q)
                    Line(q, p, faint, 1f);
                if(k % 6 == 0)
                    Line(aim.At, p, faint, 1f);
                lastRing = p;
            }

            // the paths: the first spurt at full strength, the last at its weakest
            float g = 9.81f * e.Gravity;
            void Arc(float speed, uint color, float width)
            {
                var p = aim.At;
                var v = dir * speed;
                float h = 1f / 60f;
                for(int i = 0; i < 240; i++)
                {
                    var next = p + v * h;
                    v = v * MathF.Max(1f - e.AirDrag * h, 0f) + new Vector3(0, -g * h, 0);
                    if(e.Floor && aim.HasGround && next.Y < aim.Ground)
                    {
                        float f = (p.Y - aim.Ground) / MathF.Max(p.Y - next.Y, 1e-6f);
                        next = Vector3.Lerp(p, next, Math.Clamp(f, 0f, 1f));
                        Line(p, next, color, width);
                        // where it lands: a small ring on the floor
                        Vector3? prev = null;
                        for(int k = 0; k <= 16; k++)
                        {
                            float t = k / 16f * MathF.Tau;
                            var r = next + new Vector3(MathF.Cos(t), 0, MathF.Sin(t)) * 0.05f;
                            if(prev is { } q)
                                Line(q, r, color, 1f);
                            prev = r;
                        }
                        return;
                    }
                    Line(p, next, color, width);
                    p = next;
                    if(aim.HasGround && p.Y < aim.Ground - 3f)
                        return;
                }
            }
            float first = e.Speed * e.Strength * 0.85f;
            float last = first * MathF.Pow(Math.Clamp(e.Fade, 0f, 1.5f), Math.Max(e.Spurts - 1, 0)) * 0.4f;
            if(e.Spurts > 1 || e.TailOff > 0f)
                Arc(last, orange, 1.5f);
            Arc(first, yellow, 2.5f);

            // the point aimed at
            if(aim.Target is { } target)
            {
                float r = 0.03f;
                Line(target - Vector3.UnitX * r, target + Vector3.UnitX * r, pink, 2f);
                Line(target - Vector3.UnitY * r, target + Vector3.UnitY * r, pink, 2f);
                Line(target - Vector3.UnitZ * r, target + Vector3.UnitZ * r, pink, 2f);
                if(Screen(target, out var ts))
                    draw.AddCircle(ts, 8f * ImGuiHelpers.GlobalScale, pink, 16, 1.5f);
            }
        }
    }

    public Limber.BodyRig.HousingProps? Housing { get; set; }

    public ClimaxTab(ClimaxService climax, Selection selection, RigSettings config)
    {
        _climax = climax;
        _selection = selection;
        _config = config;
    }

    // ---- Ready-made behaviours

    private static readonly (string Name, string Tip, Func<ClimaxEmitter> Make)[] Presets =
    [
        ("Thick ropes", "Heavy, sticky ropes in strong spurts that weaken, coating what they hit and dripping off.", () => new ClimaxEmitter()),
        ("Like the clip", "Strong, wavy ropes one after another, a big load that coats the face and hangs off in thick strings.",
            () => new ClimaxEmitter { Speed = 4.8f, Thickness = 0.013f, Spurts = 8, Interval = 0.85f, SpurtLength = 0.5f, Fade = 0.86f, Wiggle = 3f, WiggleRate = 6f, Viscosity = 0.75f, Cohesion = 1.3f, Settle = 0.3f, DripChance = 0.4f, Coat = 1.1f }),
        ("Huge load", "Many long, thick spurts that hardly weaken, and a long dribble after.",
            () => new ClimaxEmitter { Spurts = 12, Thickness = 0.016f, SpurtLength = 0.6f, Fade = 0.92f, Speed = 5f, Coat = 1.15f, MaxDrops = 12000, Dribble = 5f, DribbleRate = 18f }),
        ("A few weak spurts", "Only a little: three short spurts that fade fast.",
            () => new ClimaxEmitter { Spurts = 3, Strength = 0.6f, Fade = 0.7f, Speed = 3f, Thickness = 0.008f, Dribble = 1.5f }),
        ("Long distance", "Fast and far: flat, long arcs that keep together in the air.",
            () => new ClimaxEmitter { Speed = 8f, Strength = 1.2f, AirDrag = 0.05f, Spurts = 6, Thickness = 0.009f, Cohesion = 1.4f, Viscosity = 0.7f }),
        ("Thin and fast", "Thinner, faster streams that travel further and break up more.",
            () => new ClimaxEmitter { Speed = 6.5f, Thickness = 0.006f, Viscosity = 0.4f, Cohesion = 0.6f, Spurts = 9, Interval = 0.7f, SpurtLength = 0.3f, Fade = 0.88f, Drop = 0.004f }),
        ("Gushing", "Lots, all at once: long, thick spurts close together.",
            () => new ClimaxEmitter { Speed = 3.6f, Thickness = 0.016f, Spurts = 5, Interval = 0.8f, SpurtLength = 0.7f, Fade = 0.9f, Throb = 0.2f, Spread = 3f, Coat = 1.1f, Dribble = 4f }),
        ("Pulsing", "Many quick, throbbing spurts close together.",
            () => new ClimaxEmitter { Throb = 0.45f, ThrobRate = 5f, Spurts = 10, Interval = 0.6f, SpurtLength = 0.35f, Fade = 0.9f }),
        ("Sticky strings", "Very stringy and gooey: it pulls into strings and hangs off the skin.",
            () => new ClimaxEmitter { Viscosity = 0.9f, Cohesion = 2.2f, DripChance = 0.6f, RunSpeed = 0.004f, Settle = 0.15f }),
        ("Oozing", "Very thick and slow: pushed out more than shot, and barely runs.",
            () => new ClimaxEmitter { Speed = 1.2f, Thickness = 0.016f, Spurts = 2, SpurtLength = 2f, Attack = 0.25f, TailOff = 0.8f, Viscosity = 1f, Cohesion = 1.6f, RunSpeed = 0.003f, Coat = 1.15f }),
        ("Slow dribble", "No spurts to speak of: oozing out and running down.",
            () => new ClimaxEmitter { Speed = 0.5f, Thickness = 0.007f, Spurts = 1, SpurtLength = 4f, Attack = 0.2f, TailOff = 0.6f, Dribble = 4f, Viscosity = 0.95f, Cohesion = 1.4f, Spread = 4f }),
        ("Watery", "Thin and runny: splashes, runs down fast and drips a lot.",
            () => new ClimaxEmitter { Viscosity = 0.15f, Cohesion = 0.3f, Settle = 0.08f, Friction = 0.4f, RunSpeed = 0.05f, RunTime = 4f, DripChance = 0.8f, Thickness = 0.008f, Coat = 0.9f }),
        ("Spray", "A wide spray of fine drops.",
            () => new ClimaxEmitter { Spread = 12f, Viscosity = 0.2f, Cohesion = 0.2f, Thickness = 0.005f, Speed = 5f, Drop = 0.0035f, Coat = 1f }),
        ("Splashing", "Splashes more than it sticks: it spreads and slides off where it lands.",
            () => new ClimaxEmitter { Stickiness = 0.2f, Friction = 0.3f, Settle = 0.06f, Viscosity = 0.3f, Cohesion = 0.5f }),
        ("Endless", "Shoots over and over (Repeat on), with a short rest between.",
            () => new ClimaxEmitter { Repeat = true, RestBetweenSets = 2.5f, Spurts = 6 }),
    ];

    // (looks: only what the game-drawn liquid uses; where it lies and how it moves stay as they are)
    // (looks: only what the game-drawn liquid uses; where it lies and how it moves stay as they are)
    private static readonly (string Name, string Tip, Action<ClimaxLook> Apply)[] LookPresets =
    [
        ("Thick goo", "Thick, gooey and joined-up: ropes, webs and globs.", l => { l.Color = new Vector3(0.96f, 0.95f, 0.9f); l.Yellow = 0.15f; l.GameDropSize = 0.6f; l.GameAirSize = 1f; l.GameStringSize = 1.2f; l.GameCoatSize = 0.7f; l.CoatSpread = 1.6f; l.GameMelt = 2.6f; l.GameFullness = 0.9f; l.GameSmoothing = 3; l.GameSmoothStrength = 0.5f; l.GameRipples = 0.3f; }),
        ("Milky white", "White, a fairly thin coat, smooth.", l => { l.Color = new Vector3(0.95f, 0.94f, 0.9f); l.Yellow = 0f; l.GameDropSize = 0.5f; l.GameCoatSize = 0.5f; l.CoatSpread = 1.4f; l.GameMelt = 2f; l.GameFullness = 1f; l.GameSmoothing = 2; }),
        ("Creamy", "A little yellow, thicker.", l => { l.Color = new Vector3(0.95f, 0.93f, 0.86f); l.Yellow = 0.4f; l.GameDropSize = 0.6f; l.GameCoatSize = 0.8f; l.CoatSpread = 1.4f; l.GameSmoothing = 3; }),
        ("Yellowish", "A clearly yellow tint.", l => { l.Color = new Vector3(0.92f, 0.9f, 0.82f); l.Yellow = 0.8f; }),
        ("Stringy", "Thin ropes and strings, less blobby.", l => { l.GameStringSize = 0.8f; l.GameAirSize = 0.8f; l.GameMelt = 1.8f; l.GameFullness = 1.2f; l.GameSmoothing = 2; }),
        ("Thin film", "Landed liquid spread thin and smooth over the skin.", l => { l.GameCoatSize = 0.3f; l.CoatSpread = 2.2f; l.GameCoatOut = 0.8f; l.GameSmoothing = 3; }),
        ("Thick globs", "Landed liquid in thick blobs.", l => { l.GameCoatSize = 1.1f; l.CoatSpread = 1.1f; l.GameDropSize = 0.7f; l.GameSmoothing = 2; }),
        ("Very smooth", "Smoothed a lot: rounder, softer shapes.", l => { l.GameSmoothing = 5; l.GameSmoothStrength = 0.6f; }),
        ("Wet and rippled", "Shinier with ripples breaking up the light.", l => { l.GameGloss = 1f; l.GameShineMap = new Vector4(1f, 1f, 1f, 1f); l.GameRipples = 0.6f; l.GameRippleSize = 0.02f; }),
    ];

    // Buttons in rows that wrap at the window's edge.
    private static bool WrapButton(string label, ref bool first)
    {
        float w = ImGui.CalcTextSize(label.Split("###")[0]).X + ImGui.GetStyle().FramePadding.X * 2;
        if(!first && ImGui.GetItemRectMax().X + ImGui.GetStyle().ItemSpacing.X + w < ImGui.GetWindowPos().X + ImGui.GetWindowWidth() - ImGui.GetStyle().WindowPadding.X)
            ImGui.SameLine();
        first = false;
        return ImGui.SmallButton(label);
    }

    public void Draw()
    {
        var emitters = _config.ClimaxEmitters ??= new();
        var look = _config.ClimaxLook ??= new();

        ImGui.TextWrapped("Invisible emitters that shoot a thick, sticky liquid in spurts: put one on a character's bone (the penis tip, "
            + "say) or anywhere in the world, aim it, and shoot. It sticks to the bodies it lands on and moves with them, runs down the skin, "
            + "drips off in strings and pools on the floor. Adult characters only; only on your screen.");
        ImGui.TextDisabled($"Drops: {_climax.Flying} in the air, {_climax.Landed} landed.");
        if(_climax.Status.Length > 0)
            ImGui.TextColored(new Vector4(1f, 0.5f, 0.4f, 1f), _climax.Status);

        if(ImGui.Button("Add an emitter###clx_add"))
        {
            var e = new ClimaxEmitter { Name = $"Emitter {emitters.Count + 1}" };
            var actor = _selection.Actor;
            if(actor.IsSet && Limber.Koi.Safety.AdultAllowed(actor.Address))
                e.Character = actor.Name;
            emitters.Add(e);
            _dirty = true;
        }
        ImGui.SameLine();
        if(ImGui.Button("Shoot all###clx_all"))
            foreach(var e in emitters)
                _climax.Shoot(e, true);
        ImGui.SameLine();
        if(ImGui.Button("Stop all###clx_stopall"))
            foreach(var e in emitters)
                _climax.Shoot(e, false);
        ImGui.SameLine();
        if(ImGui.Button("Clear all the liquid###clx_clear"))
            _climax.Clear();

        ImGui.Spacing();
        DrawLook(look);
        ImGui.Spacing();

        ClimaxEmitter? remove = null, copy = null;
        foreach(var e in emitters)
        {
            using var id = ImRaii.PushId(e.Id.ToString());
            string where = e.Character.Length > 0 ? $"on {e.Character}" : "in the world";
            if(!ImGui.CollapsingHeader($"{e.Name} ({where}){(e.Shooting ? "  - shooting" : "")}###clx_e", ImGuiTreeNodeFlags.DefaultOpen))
                continue;
            ImGui.Indent();
            DrawEmitter(e, ref remove, ref copy);
            ImGui.Unindent();
        }
        if(remove != null)
        {
            _climax.Clear(remove.Id);
            emitters.Remove(remove);
            _dirty = true;
        }
        if(copy != null)
        {
            var c = copy.Copy();
            c.Id = Guid.NewGuid();
            c.Name = copy.Name + " (copy)";
            c.Shooting = false;
            emitters.Add(c);
            _dirty = true;
        }

        if(_dirty && !ImGui.IsAnyItemActive())
        {
            _dirty = false;
            _config.Save();
        }
    }

    // ---- One emitter

    private void DrawEmitter(ClimaxEmitter e, ref ClimaxEmitter? remove, ref ClimaxEmitter? copy)
    {
        float w = 110 * ImGuiHelpers.GlobalScale;
        if(e.Shooting)
        {
            if(ImGui.Button("Stop###stop", new Vector2(w, 0)))
                _climax.Shoot(e, false);
        }
        else if(ImGui.Button("Shoot###shoot", new Vector2(w, 0)))
            _climax.Shoot(e, true);
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("One set of spurts (and the dribble after it); over and over with Repeat.");
        ImGui.SameLine();
        bool repeat = e.Repeat;
        if(ImGui.Checkbox("Repeat###repeat", ref repeat))
        {
            e.Repeat = repeat;
            _dirty = true;
        }
        ImGui.SameLine();
        if(ImGui.Button("Clear its liquid###clear"))
            _climax.Clear(e.Id);
        ImGui.SameLine();
        if(ImGui.Button("Copy###copy"))
            copy = e;
        ImGui.SameLine();
        if(ImGui.Button("Remove###remove"))
            remove = e;

        string name = e.Name;
        ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(220f * ImGuiHelpers.GlobalScale));
        if(ImGui.InputText("Name###name", ref name, 48))
        {
            e.Name = name;
            _dirty = true;
        }

        ImGui.TextUnformatted("Behaves like:");
        bool firstPreset = false; // (the first goes next to the label)
        foreach(var (pname, tip, make) in Presets)
        {
            if(WrapButton($"{pname}###preset_{pname}", ref firstPreset))
            {
                e.TakeBehaviour(make());
                _dirty = true;
            }
            if(ImGui.IsItemHovered())
                ImGui.SetTooltip(tip + "\n(Changes how it shoots and behaves; keeps where it is and what it aims at.)");
        }

        if(ImGui.TreeNodeEx("Where it is###place", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawPlace(e);
            ImGui.TreePop();
        }
        if(ImGui.TreeNodeEx("Aim###aim", ImGuiTreeNodeFlags.DefaultOpen))
        {
            bool show = e.ShowAim;
            if(ImGui.Checkbox("Show the aim (wireframe)###showaim", ref show))
            {
                e.ShowAim = show;
                _dirty = true;
            }
            if(ImGui.IsItemHovered())
                ImGui.SetTooltip("Lines on the screen: where the emitter is and points, how wide it sprays, and the paths of the first spurt "
                    + "(yellow) and the last, weakest one (orange); the target's point in pink. (Turn it off before taking screenshots.)");
            DrawAim(e);
            ImGui.TreePop();
        }
        if(ImGui.TreeNode("Spurts###spurts"))
        {
            Int("Spurts in a set", () => e.Spurts, v => e.Spurts = v, 1, 30, "How many spurts one Shoot makes.");
            F("Time between spurts", () => e.Interval, v => e.Interval = v, 0.1f, 4f, "%.2f s", "From one spurt's start to the next.");
            F("Time varies by", () => e.IntervalRandom, v => e.IntervalRandom = v, 0f, 1f, "%.2f", "How much that time changes at random, spurt to spurt.");
            F("Spurt length", () => e.SpurtLength, v => e.SpurtLength = v, 0.05f, 6f, "%.2f s", "How long each spurt lasts.");
            F("Strength", () => e.Strength, v => e.Strength = v, 0.05f, 3f, "%.2f", "The first spurt's strength (how fast and far, how much).");
            F("Each spurt weaker by", () => e.Fade, v => e.Fade = v, 0.3f, 1.2f, "x%.2f", "Each spurt's strength as a share of the one before (1 = all the same; above 1 they build up).");
            F("Build-up", () => e.Attack, v => e.Attack = v, 0.001f, 0.9f, "%.3f", "The share of each spurt spent building up to full strength.");
            F("Tail-off", () => e.TailOff, v => e.TailOff = v, 0.1f, 5f, "%.2f", "How sharply each spurt dies away after its peak (higher = a quick burst).");
            F("Throb", () => e.Throb, v => e.Throb = v, 0f, 0.8f, "%.2f", "Pulsing strength within a spurt.");
            F("Throb speed", () => e.ThrobRate, v => e.ThrobRate = v, 0.5f, 25f, "%.1f a second");
            F("Dribble after", () => e.Dribble, v => e.Dribble = v, 0f, 15f, "%.1f s", "The slow ooze after the last spurt (0 = none).");
            F("Dribble amount", () => e.DribbleRate, v => e.DribbleRate = v, 1f, 60f, "%.0f", "How much oozes out after (10: a thin ooze).");
            F("Rest between sets", () => e.RestBetweenSets, v => e.RestBetweenSets = v, 0f, 60f, "%.1f s", "With Repeat: the wait before the next set.");
            ImGui.TreePop();
        }
        if(ImGui.TreeNode("The stream###stream"))
        {
            F("Speed", () => e.Speed, v => e.Speed = v, 0.1f, 20f, "%.2f m/s", "How fast it leaves at full strength: further and flatter when faster.");
            F("Speed varies by", () => e.SpeedRandom, v => e.SpeedRandom = v, 0f, 0.6f, "%.2f");
            F("Thickness", () => e.Thickness, v => e.Thickness = v, 0.002f, 0.05f, "%.4f m", "The stream's radius (how much comes out).");
            F("Thickness at the start", () => e.StartThickness, v => e.StartThickness = v, 0.1f, 3f, "x%.2f", "Each spurt starts this thick (times Thickness)...");
            F("Thickness at the end", () => e.EndThickness, v => e.EndThickness = v, 0.1f, 3f, "x%.2f", "...and thins (or thickens) to this.");
            F("Spread", () => e.Spread, v => e.Spread = v, 0f, 25f, "%.1f°", "How far drops stray from the aim (a spray when high).");
            F("Takes the emitter's movement", () => e.InheritMotion, v => e.InheritMotion = v, 0f, 1.5f, "%.2f", "How much of the emitter's own movement the liquid carries along.");
            ImGui.TreePop();
        }
        if(ImGui.TreeNode("The liquid###liquid"))
        {
            ImGui.TextDisabled("A real liquid: thousands of drops that push apart, pull together and flow.");
            F("Thickness (viscosity)", () => e.Viscosity, v => e.Viscosity = v, 0f, 1f, "%.2f", "How thick and slow-flowing it is: high is thick and gooey, low is runny.");
            F("Holds together", () => e.Cohesion, v => e.Cohesion = v, 0f, 4f, "%.2f", "How strongly the drops pull together: blobs, ropes and strings when high, splashes and spray when low.");
            F("Drop size", () => e.Drop, v => e.Drop = v, 0.002f, 0.012f, "%.4f m", "The size of each drop the liquid is made of: smaller is smoother and finer (and much more work for your PC).");
            Int("Firmness (passes)", () => e.Iterations, v => e.Iterations = v, 1, 6, "Solver passes a step: more keeps the liquid from squashing (and is more work).");
            F("Gravity", () => e.Gravity, v => e.Gravity = v, 0f, 3f, "x%.2f");
            F("Air drag", () => e.AirDrag, v => e.AirDrag = v, 0f, 3f, "%.2f", "Slows it in the air.");
            ImGui.TreePop();
        }
        if(ImGui.TreeNode("On the body###body"))
        {
            F("Clings", () => e.Stickiness, v => e.Stickiness = v, 0f, 1f, "%.2f", "How strongly it's pulled onto the skin.");
            F("Holds on (friction)", () => e.Friction, v => e.Friction = v, 0f, 1f, "%.2f", "How much the skin stops it sliding when it lands.");
            F("Sets when slower than", () => e.Settle, v => e.Settle = v, 0f, 1.5f, "%.2f m/s", "Liquid on the skin slower than this sets there (and then moves with the body). Higher: it stays where it lands; lower: it flows on further first.");
            F("Runs down at", () => e.RunSpeed, v => e.RunSpeed = v, 0f, 0.3f, "%.3f m/s", "How fast stuck liquid runs down the skin.");
            F("Stops running after", () => e.RunTime, v => e.RunTime = v, 0.1f, 60f, "%.1f s", "It runs slower and slower and stops after this long.");
            F("Drips", () => e.DripChance, v => e.DripChance = v, 0f, 3f, "%.2f", "How often liquid hanging from undersides (chin, breasts...) drips off.");
            F("Keeps creeping", () => e.Creep, v => e.Creep = v, 0f, 1f, "%.2f", "After it slows, liquid on the skin keeps creeping down this fast (a share of how fast it first ran): 0 = it stops.");
            bool strings = e.DripStrings;
            if(ImGui.Checkbox("Drips hang on strings###clx_dripstr", ref strings))
            {
                e.DripStrings = strings;
                _dirty = true;
            }
            if(ImGui.IsItemHovered())
                ImGui.SetTooltip("Goopy drips: each drip hangs from where it was on a string that stretches under its weight, then snaps.");
            F("Drip strings stretch to", () => e.StringLength, v => e.StringLength = v, 0.01f, 0.6f, "%.2f m", "How long a drip's string stretches before it snaps.");
            F("Sits out from the skin", () => e.SitOut, v => e.SitOut = v, -1f, 1.5f, "%.2f", "Lower sinks it into the skin; higher floats it on top.");
            F("Coat", () => e.Coat, v => e.Coat = v, 0.5f, 3f, "x%.2f", "Liquid on the skin drawn this much bigger: a smooth coat when higher, beads when lower.");
            F("Body size for hitting", () => e.BodySize, v => e.BodySize = v, 0.5f, 2f, "x%.2f", "The bodies' shape it lands on, made bigger or smaller (if it sinks in or floats off).");
            bool self = e.HitSelf;
            if(ImGui.Checkbox("Lands on its own character too###self", ref self))
            {
                e.HitSelf = self;
                _dirty = true;
            }
            bool only = e.HitOnlyTicked;
            var names = e.HitNames;
            if(CharacterFilter.Draw("clx_hit", "Lands", ref only, names, AdultCharacters(), _selection.Actor))
            {
                e.HitOnlyTicked = only;
                _dirty = true;
            }
            ImGui.TreePop();
        }
        if(ImGui.TreeNode("Floor###floor"))
        {
            bool floor = e.Floor;
            if(ImGui.Checkbox("Pools on the floor###floor_on", ref floor))
            {
                e.Floor = floor;
                _dirty = true;
            }
            F("Floor height", () => e.FloorOffset, v => e.FloorOffset = v, -2f, 2f, "%.3f m", "Up or down from the character's feet (for a bed, a chair...).");
            F("Puddles spread", () => e.PuddleSpread, v => e.PuddleSpread = v, 0f, 5f, "%.2f");
            ImGui.TreePop();
        }
        if(ImGui.TreeNode("How long it stays###stay"))
        {
            F("Stays for", () => e.Stay, v => e.Stay = v, 0f, 600f, "%.0f s", "Landed liquid fades after this long (0 = stays until cleared).");
            F("Fades over", () => e.FadeTime, v => e.FadeTime = v, 0.1f, 30f, "%.1f s");
            Int("Most drops", () => e.MaxDrops, v => e.MaxDrops = v, 500, 20000, "No more comes out once there are this many drops (Clear its liquid, or a Stays for time, makes room). Lower it if it slows your PC.");
            ImGui.TreePop();
        }
    }

    private List<ActorRef> AdultCharacters()
    {
        var list = new List<ActorRef>();
        foreach(var a in _selection.ListCharacters())
            if(Limber.Koi.Safety.AdultAllowed(a.Address))
                list.Add(a);
        return list;
    }

    private void DrawPlace(ClimaxEmitter e)
    {
        bool world = e.Character.Length == 0;
        if(ImGui.RadioButton("On a character###on_char", !world) && world)
        {
            var actor = _selection.Actor;
            if(actor.IsSet && Limber.Koi.Safety.AdultAllowed(actor.Address))
            {
                e.Character = actor.Name;
                _dirty = true;
            }
        }
        ImGui.SameLine();
        if(ImGui.RadioButton("In the world###in_world", world) && !world)
        {
            e.Character = "";
            if(e.Aim is ClimaxAim.AlongBone or ClimaxAim.CharacterForward)
                e.Aim = ClimaxAim.World;
            _dirty = true;
        }
        if(e.Character.Length > 0)
        {
            ChooseCharacter("Character###char", e.Character, n => e.Character = n);
            ChooseBone("Bone###bone", e.Bone, SourceBones, b => e.Bone = b);
            V3("Moved from it###offset", () => e.Offset, v => e.Offset = v, 0.002f, -0.5f, 0.5f, "%.3f",
                "X right, Y up, Z forward (along where it points), metres.");
        }
        else
        {
            V3("Position###wpos", () => e.WorldPosition, v => e.WorldPosition = v, 0.01f, -100000f, 100000f, "%.2f", "In the world, metres.");
            var actor = _selection.Actor;
            using(ImRaii.Disabled(!actor.IsSet))
            {
                if(ImGui.Button("Put it in front of the chosen character###here") && actor.IsSet)
                {
                    unsafe
                    {
                        var go = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)actor.Address;
                        float rot = go->Rotation;
                        var forward = new Vector3(MathF.Sin(rot), 0, MathF.Cos(rot));
                        e.WorldPosition = (Vector3)go->Position + forward * 0.6f + new Vector3(0, 0.9f, 0);
                        e.Yaw = rot * 180f / MathF.PI + 180f;
                        e.Pitch = 15f;
                        e.Aim = ClimaxAim.World;
                    }
                    _dirty = true;
                }
            }
        }
    }

    private void DrawAim(ClimaxEmitter e)
    {
        bool world = e.Character.Length == 0;
        var modes = world
            ? new[] { (ClimaxAim.World, "A direction"), (ClimaxAim.AtTarget, "At a character") }
            : new[] { (ClimaxAim.AlongBone, "Along the bone"), (ClimaxAim.CharacterForward, "Where the character faces"), (ClimaxAim.AtTarget, "At a character"), (ClimaxAim.World, "A direction") };
        foreach(var (mode, label) in modes)
        {
            if(mode != modes[0].Item1)
                ImGui.SameLine();
            if(ImGui.RadioButton($"{label}###aim_{mode}", e.Aim == mode))
            {
                e.Aim = mode;
                _dirty = true;
            }
        }
        if(e.Aim == ClimaxAim.AtTarget)
        {
            ImGui.TextDisabled("It arcs to land on the target, at the first spurt's speed (Speed x Strength).");
            ChooseCharacter("Target###target", e.Target, n => e.Target = n);
            ChooseBone("Target bone###tbone", e.TargetBone, TargetBones, b => e.TargetBone = b);
            V3("Moved from the bone###toffset", () => e.TargetOffset, v => e.TargetOffset = v, 0.002f, -0.5f, 0.5f, "%.3f",
                "The point aimed at: X the target's left, Y up, Z forward (the face's front is about Z 0.09), metres.");
            bool high = e.HighArc;
            if(ImGui.Checkbox("High arc###high", ref high))
            {
                e.HighArc = high;
                _dirty = true;
            }
            if(ImGui.IsItemHovered())
                ImGui.SetTooltip("Lobbed up and dropped on the target, instead of the flatter, direct shot.");
        }
        string yawLabel = e.Aim == ClimaxAim.World ? "Direction (turn)" : "Turn right";
        string pitchLabel = e.Aim == ClimaxAim.World ? "Direction (up)" : "Turn up";
        F(yawLabel, () => e.Yaw, v => e.Yaw = v, -180f, 180f, "%.1f°");
        F(pitchLabel, () => e.Pitch, v => e.Pitch = v, -90f, 90f, "%.1f°");
        F("Wiggle", () => e.Wiggle, v => e.Wiggle = v, 0f, 15f, "%.1f°", "A quick wobble while it shoots: wavy ropes.");
        F("Wiggle speed", () => e.WiggleRate, v => e.WiggleRate = v, 0.5f, 25f, "%.1f a second");
        F("Sway", () => e.Sway, v => e.Sway = v, 0f, 30f, "%.1f°", "How far the aim sways back and forth while shooting.");
        F("Sway speed", () => e.SwayRate, v => e.SwayRate = v, 0f, 5f, "%.2f a second");
        F("Aim varies by", () => e.AimRandom, v => e.AimRandom = v, 0f, 20f, "%.1f°", "Each spurt's aim is off by up to this much, at random.");
    }

    private void ChooseCharacter(string label, string current, Action<string> set)
    {
        ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(260f * ImGuiHelpers.GlobalScale));
        using var combo = ImRaii.Combo(label, current.Length > 0 ? current : "(choose)");
        if(!combo)
            return;
        foreach(var a in AdultCharacters())
        {
            if(ImGui.Selectable($"{a.Label}###c{a.ObjectIndex}", a.Name == current))
            {
                set(a.Name);
                _dirty = true;
            }
        }
    }

    private void ChooseBone(string label, string current, (string Bone, string Name)[] bones, Action<string> set)
    {
        ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(260f * ImGuiHelpers.GlobalScale));
        var found = Array.Find(bones, b => b.Bone == current);
        string shown = found.Name != null ? $"{found.Name} ({current})" : current;
        using(var combo = ImRaii.Combo(label, shown))
        {
            if(combo)
            {
                foreach(var (bone, name) in bones)
                {
                    if(ImGui.Selectable($"{name} ({bone})###b_{bone}", bone == current))
                    {
                        set(bone);
                        _dirty = true;
                    }
                }
            }
        }
        ImGui.SameLine();
        string typed = current;
        ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(130f * ImGuiHelpers.GlobalScale));
        if(ImGui.InputText($"###{label}_typed", ref typed, 64, ImGuiInputTextFlags.EnterReturnsTrue) && typed.Trim().Length > 0)
        {
            set(typed.Trim());
            _dirty = true;
        }
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("Or type any bone's name (Enter).");
    }

    // ---- The look (every emitter's)

    private void DrawLook(ClimaxLook l)
    {
        if(!ImGui.CollapsingHeader("How the liquid looks###clx_look"))
            return;
        ImGui.Indent();
        DrawLookControls(l);
        ImGui.Unindent();
    }

    private void DrawLookControls(ClimaxLook l)
    {
        ImGui.TextDisabled("Drawn by the game itself, so it's lit, shadowed and blurred like everything else.");
        ImGui.TextUnformatted("Looks like:");
        bool firstLook = false;
        foreach(var (pname, tip, apply) in LookPresets)
        {
            if(WrapButton($"{pname}###lookpreset_{pname}", ref firstLook))
            {
                apply(l);
                _dirty = true;
            }
            if(ImGui.IsItemHovered())
                ImGui.SetTooltip(tip);
        }
        ImGui.Spacing();
        ImGui.TextDisabled("Shape (shows straight away)");
        F("Drop size", () => l.GameDropSize, v => l.GameDropSize = v, 0.15f, 1.5f, "x%.2f",
            "How big all of it is in the surface. Drops close together melt into each other and swell.");
        F("Flying drops", () => l.GameAirSize, v => l.GameAirSize = v, 0.1f, 3f, "x%.2f", "The drops in the air, bigger or smaller.");
        F("Strings", () => l.GameStringSize, v => l.GameStringSize = v, 0.1f, 3f, "x%.2f", "The strings and ropes between drops, thicker or thinner.");
        F("Coat thickness", () => l.GameCoatSize, v => l.GameCoatSize = v, 0.05f, 1.5f, "x%.2f",
            "How thick the liquid lying on skin is.");
        F("Coat spread", () => l.CoatSpread, v => l.CoatSpread = v, 0.5f, 4f, "x%.2f",
            "How far liquid lying on skin spreads: higher = one smooth film, lower = separate drops.");
        F("Coat sits out", () => l.GameCoatOut, v => l.GameCoatOut = v, 0f, 1f, "%.2f",
            "How much of the coat is outside the skin: 0 = half sunk in, 1 = all of it on top.");
        F("Melts together", () => l.GameMelt, v => l.GameMelt = v, 1.2f, 4f, "%.2f",
            "How far apart drops can be and still melt into one: higher = gooier, joined-up blobs and webs; lower = separate drops.");
        F("Fullness", () => l.GameFullness, v => l.GameFullness = v, 0.3f, 1.6f, "%.2f",
            "Lower swells the surface out (fuller, rounder), higher shrinks it in (thinner, stringier).");
        Int("Smoothness", () => l.GameSmoothing, v => l.GameSmoothing = v, 0, 8,
            "How many times the surface is smoothed: higher = rounder, softer shapes (and a little more work).");
        F("Smoothing strength", () => l.GameSmoothStrength, v => l.GameSmoothStrength = v, 0.05f, 0.9f, "%.2f", "How much each smoothing pass rounds it.");
        F("Finest detail", () => l.GameDetail * 1000f, v => l.GameDetail = v / 1000f, 0.4f, 10f, "%.1f mm",
            "The smallest the surface's cells may get (it's made as fine as there's room for, down to this). Bigger = faster.");
        ImGui.Separator();
        ImGui.TextDisabled("Material (shows after Apply)");
        var color = l.Color;
        if(ImGui.ColorEdit3("Colour###look_color", ref color))
        {
            l.Color = color;
            _dirty = true;
        }
        F("Yellow hue", () => l.Yellow, v => l.Yellow = v, 0f, 1f, "%.2f", "From white to a creamy, yellowish colour.");
        F("Gloss", () => l.GameGloss, v => l.GameGloss = v, 0f, 1f, "%.2f", "How shiny it is (all of the shine map below, together).");
        var map = l.GameShineMap;
        F("Shine map: red", () => map.X, v => l.GameShineMap = l.GameShineMap with { X = v }, 0f, 1f, "%.2f",
            "The four channels of its shine (specular) map. What each does is up to the game's shader: try each to find the wettest look.");
        F("Shine map: green", () => map.Y, v => l.GameShineMap = l.GameShineMap with { Y = v }, 0f, 1f, "%.2f");
        F("Shine map: blue", () => map.Z, v => l.GameShineMap = l.GameShineMap with { Z = v }, 0f, 1f, "%.2f");
        F("Shine map: alpha", () => map.W, v => l.GameShineMap = l.GameShineMap with { W = v }, 0f, 1f, "%.2f");
        F("Ripples", () => l.GameRipples, v => l.GameRipples = v, 0f, 1f, "%.2f", "Soft ripples in its surface, to break up the light like a wet surface.");
        F("Ripple size", () => l.GameRippleSize * 100f, v => l.GameRippleSize = v / 100f, 0.2f, 20f, "%.1f cm", "How big the ripples are (shows straight away).");
        using(ImRaii.Disabled(!Limber.Gpu.GameLiquid.On))
        {
            if(ImGui.Button("Apply colour and gloss###clx_g_apply"))
                Limber.Gpu.GameLiquid.Reapply();
        }
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("Colour, yellow hue and gloss show from the next time the liquid is placed: this places it again now (a second or two).");
        if(Limber.Gpu.GameLiquid.LookStatus.Length > 0)
            ImGui.TextDisabled(Limber.Gpu.GameLiquid.LookStatus);

        // ---- the carriers (props the game draws the liquid in)
        if(ImGui.TreeNode("Carriers###clx_carriers"))
        {
            ImGui.TextWrapped("The liquid is drawn inside big prop models the game already has (only on your screen). More carriers give "
                + "more room, so a finer surface. They're picked for you; any copy of the same props elsewhere in the room changes colour while liquid is out.");
            int want = l.GameCarrierCount;
            ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(160f * ImGuiHelpers.GlobalScale));
            if(ImGui.SliderInt("How many###clx_carrier_count", ref want, 1, 12))
                l.GameCarrierCount = want;
            if(ImGui.IsItemDeactivatedAfterEdit())
            {
                l.GameCarriers = Limber.Gpu.GameLiquid.Candidates.Take(want).Select(c => c.Path).ToList();
                _dirty = true;
            }
            using(ImRaii.Disabled(Limber.Gpu.GameLiquid.Searching || Housing == null))
            {
                if(ImGui.Button("Look for carriers again###clx_game_find") && Housing != null)
                {
                    l.GameCarriers = new();
                    Limber.Gpu.GameLiquid.Search(Housing);
                    _dirty = true;
                }
            }
            if(Limber.Gpu.GameLiquid.SearchStatus.Length > 0)
                ImGui.TextDisabled(Limber.Gpu.GameLiquid.SearchStatus);
            foreach(var c in Limber.Gpu.GameLiquid.Candidates)
            {
                bool used = l.GameCarriers.Contains(c.Path);
                if(ImGui.Checkbox($"{c.Name}  ({c.Capacity} triangles)###clx_c_{c.Path}", ref used))
                {
                    l.GameCarriers = used ? l.GameCarriers.Append(c.Path).ToList() : l.GameCarriers.Where(p => p != c.Path).ToList();
                    _dirty = true;
                }
                if(ImGui.IsItemHovered())
                    ImGui.SetTooltip(c.Path);
            }
            if(Limber.Gpu.GameLiquid.Candidates.Count == 0 && l.GameCarriers.Count > 0)
                ImGui.TextDisabled($"Using {l.GameCarriers.Count} carriers picked before.");
            if(l.GameCarriers.Count == 0)
                ImGui.TextDisabled("None ticked: the biggest are picked for you.");
            ImGui.TreePop();
        }
        if(ImGui.TreeNode("Troubleshooting###clx_trouble"))
        {
            bool own = Limber.Gpu.GameLiquid.OwnMaterial, col = Limber.Gpu.GameLiquid.WriteColours, dir = Limber.Gpu.GameLiquid.WriteDirections;
            if(ImGui.Checkbox("The liquid's own material###clx_t_mat", ref own))
            {
                Limber.Gpu.GameLiquid.OwnMaterial = own;
                Limber.Gpu.GameLiquid.Reapply();
            }
            if(ImGui.IsItemHovered())
                ImGui.SetTooltip("Off: drawn with the props' own materials instead (to see if the material is what's wrong).");
            if(ImGui.Checkbox("Corner colours###clx_t_col", ref col))
                { Limber.Gpu.GameLiquid.WriteColours = col; Limber.Gpu.GameLiquid.Reapply(); }
            if(ImGui.IsItemHovered())
                ImGui.SetTooltip("Off: the props' own corner colours (copied from their first corner) instead of plain white.");
            if(ImGui.Checkbox("Surface directions###clx_t_dir", ref dir))
                { Limber.Gpu.GameLiquid.WriteDirections = dir; Limber.Gpu.GameLiquid.Reapply(); }
            if(ImGui.IsItemHovered())
                ImGui.SetTooltip("Off: the props' own directions along the surface (copied from their first corner).");
            ImGui.TreePop();
        }
        if(Limber.Gpu.GameLiquid.Status.Length > 0)
        {
            using(ImRaii.PushColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.TextDisabled)))
                ImGui.TextWrapped(Limber.Gpu.GameLiquid.Status);
            var report = Limber.Gpu.GameLiquid.CarrierReport;
            if(report.Length > 0)
                using(ImRaii.PushColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.TextDisabled)))
                    ImGui.TextWrapped(report);
            if(ImGui.SmallButton("Copy status###clx_game_copy"))
                ImGui.SetClipboardText(Limber.Gpu.GameLiquid.Status + "\n" + report);
        }
        if(ImGui.Button("Look back to the start###look_reset"))
        {
            var keep = l.GameCarriers;
            _config.ClimaxLook = new ClimaxLook { GameCarriers = keep };
            _dirty = true;
        }
    }

    // ---- Controls

    private void F(string label, Func<float> get, Action<float> set, float min, float max, string format, string? tip = null)
    {
        float v = get();
        ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(260f * ImGuiHelpers.GlobalScale));
        if(ImGui.SliderFloat($"{label}###{label}", ref v, min, max, format))
        {
            set(v);
            _dirty = true;
        }
        if(tip != null && ImGui.IsItemHovered())
            ImGui.SetTooltip(tip + "\n(Ctrl+click to type a value.)");
    }

    private void Int(string label, Func<int> get, Action<int> set, int min, int max, string? tip = null)
    {
        int v = get();
        ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(260f * ImGuiHelpers.GlobalScale));
        if(ImGui.SliderInt($"{label}###{label}", ref v, min, max))
        {
            set(v);
            _dirty = true;
        }
        if(tip != null && ImGui.IsItemHovered())
            ImGui.SetTooltip(tip);
    }

    private void V3(string label, Func<Vector3> get, Action<Vector3> set, float speed, float min, float max, string format, string? tip = null)
    {
        var v = get();
        ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(260f * ImGuiHelpers.GlobalScale));
        if(ImGui.DragFloat3(label, ref v, speed, min, max, format))
        {
            set(v);
            _dirty = true;
        }
        if(tip != null && ImGui.IsItemHovered())
            ImGui.SetTooltip(tip);
    }
}
