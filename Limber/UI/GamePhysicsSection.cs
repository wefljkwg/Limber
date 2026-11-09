using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Limber.Rig;

namespace Limber.UI;

/// <summary>Body physics page: "The game's own physics" section (hair, clothes, body-mod jiggle bones).</summary>
public static class GamePhysicsSection
{
    private static readonly Vector4 Warning = new(0.95f, 0.75f, 0.30f, 1f);
    private static readonly Vector4 Dim = new(0.65f, 0.65f, 0.65f, 1f);

    public static void Draw(RigSettings config, ActorRef actor)
    {
        if(!ImGui.CollapsingHeader("The game's own physics (hair, clothes, body mods)###lmb_game_physics"))
            return;
        var s = config.GamePhysics ??= new GamePhysicsSettings();
        var gp = GamePhysics.Current;
        bool changed = false;
        using var id = ImRaii.PushId("lmb_game_physics");
        ImGui.TextWrapped("Changes to the game's OWN physics: hair, clothes, and the jiggle bones body mods add. "
            + "Separate from Limber's soft bodies above. Each part is off until you tick it; untick it and the game's own values come back.");
        if(gp == null)
        {
            ImGui.TextColored(Warning, "Not available (Limber didn't start this part).");
            return;
        }
        if(gp.Problem.Length > 0)
            ImGui.TextColored(Warning, "Problem: " + gp.Problem);

        void Check(string label, Func<bool> get, Action<bool> set, string tip)
        {
            bool v = get();
            if(ImGui.Checkbox(label, ref v))
            {
                set(v);
                changed = true;
            }
            Tooltip(tip);
        }
        void Slider(string label, Func<float> get, Action<float> set, float min, float max, string format, string tip)
        {
            float v = get();
            ImGui.SetNextItemWidth(220);
            if(ImGui.SliderFloat(label, ref v, min, max, format))
            {
                set(v);
                changed = true;
            }
            Tooltip(tip);
        }
        void SliderInt(string label, Func<int> get, Action<int> set, int min, int max, string tip)
        {
            int v = get();
            ImGui.SetNextItemWidth(220);
            if(ImGui.SliderInt(label, ref v, min, max))
            {
                set(v);
                changed = true;
            }
            Tooltip(tip);
        }

        // Who
        string[] who = ["GPose actors and Limber's actors", "Everyone"];
        int w = Math.Clamp(s.Who, 0, 1);
        ImGui.SetNextItemWidth(260);
        if(ImGui.Combo("Who###who", ref w, who, who.Length))
        {
            s.Who = w;
            changed = true;
        }
        Tooltip("Which characters the options below change (the speed fix is for everyone).\n"
            + "GPose actors: every character in GPose (your own one too) and the actors Limber adds.");

        ImGui.Spacing();
        Check("Right speed at any frame rate###fps", () => s.FrameRateFix, v => s.FrameRateFix = v,
            "The game's physics takes fixed steps. Below 60 fps it runs slow, and parts with their own rate run too fast at high "
            + "frame rates. This counts the real time for each part and takes as many steps as that time needs (a few at most).\n"
            + "Above 60 fps the game already handles its usual parts by itself; then this does nothing for them.\n"
            + "Turns itself off when Rebound or a similar plugin is fixing the speed.");
        if(s.FrameRateFix)
        {
            ImGui.Indent();
            SliderInt("Most steps in one frame###fps_max", () => s.MaxSteps, v => s.MaxSteps = v, 1, 8,
                "After a slow frame, at most this many steps catch up (more: right speed after hitches; fewer: lighter).");
            if(gp.FrameRateStatus.Length > 0)
                ImGui.TextColored(Dim, gp.FrameRateStatus);
            ImGui.Unindent();
        }

        Check("Full physics (ignore the game's \"Simple\" / \"Off\")###full", () => s.ForceFull, v => s.ForceFull = v,
            "Hair, clothes, earrings, tails and the chest move fully on the chosen characters even when the game's character "
            + "settings have their physics on Simple or Off (for example for other players).");
        if(s.ForceFull && !gp.CanForce)
            ImGui.TextColored(Warning, "Physics that the game has switched off completely can't be started (see Problem).");

        Check("Limber's posing moves the jiggle###posefirst", () => s.PoseBeforePhysics, v => s.PoseBeforePhysics = v,
            "The game works out its physics from the animation, before Limber poses the character, so turning or moving the "
            + "hips, spine or chest with Limber (posing, twerk, animations) didn't make anything jiggle.\n"
            + "On: what Limber did to the hips, spine and chest last frame is put on before the game's physics, so the jiggle "
            + "follows Limber's motion (one frame behind). Taken off again straight after, so nothing is turned twice.");

        Check("Less buzzing against the body###buzz", () => s.AntiBuzz, v => s.AntiBuzz = v,
            "Where hair, clothes or breasts touch the body they can shiver in and out. This makes them lose some speed when "
            + "they touch, slide less, get pushed out more gently, and checks the touch more often.");
        if(s.AntiBuzz)
        {
            ImGui.Indent();
            Slider("Speed kept after a touch###hitkeep", () => s.HitKeep, v => s.HitKeep = v, 0f, 1f, "%.2f",
                "How much of its speed a part keeps when it touches the body (the game: all of it).");
            Slider("Grip on the body###hitfriction", () => s.HitFriction, v => s.HitFriction = v, 0f, 1f, "%.2f",
                "How much sliding along the body is held back (the game: none).");
            Slider("Push-out strength###hitpush", () => s.HitPush, v => s.HitPush = v, 0.1f, 1f, "%.2f",
                "How much of the way out of the body a part is pushed in one go (the game: all the way, which bounces).");
            SliderInt("Touch checks a step###hitloops", () => s.CollisionLoops, v => s.CollisionLoops = v, 1, 6,
                "How many times each step the game checks touching (only where it checks at all).");
            Slider("Top speed###hitspeed", () => s.BuzzMaxSpeed, v => s.BuzzMaxSpeed = v, 0.5f, 20f, "%.1f",
                "The fastest a part may move (lower: calmer; only where the part has a top speed of its own).");
            ImGui.Unindent();
        }

        Check("Jiggle settings for body-mod bones###jiggle", () => s.Jiggle, v => s.Jiggle = v,
            "How the breast, butt and belly bones body mods add (and, if ticked below, the game's own chest) jiggle. "
            + "The game gives these no settings of its own.");
        if(s.Jiggle)
        {
            ImGui.Indent();
            foreach(var p in GamePhysicsSettings.Presets)
            {
                bool active = s.JigglePreset == p.Name;
                using(ImRaii.PushColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.ButtonActive), active))
                    if(ImGui.Button($"{p.Name}###preset_{p.Name}"))
                    {
                        s.UsePreset(p.Name);
                        changed = true;
                    }
                Tooltip(p.Name switch
                {
                    "Subtle" => "A little movement, settles quickly.",
                    "Soft" => "Lags behind and wobbles softly.",
                    _ => "Lags a lot and keeps bouncing.",
                });
                ImGui.SameLine();
            }
            ImGui.TextColored(Dim, s.JigglePreset.Length > 0 ? s.JigglePreset : "Custom");
            void Custom() => s.JigglePreset = "";
            Slider("Follows the body###follow", () => s.Follow, v => { s.Follow = v; Custom(); }, 0f, 1f, "%.2f",
                "How much of the body's movement the bones take at once (the game: 0.30). Lower: they lag and swing more.");
            SliderInt("Shape passes###loops", () => s.ConstraintLoops, v => { s.ConstraintLoops = v; Custom(); }, 1, 6,
                "How firmly the bones keep their lengths each step (more: firmer, less stretchy).");
            Slider("Settling###damping", () => s.JiggleDamping, v => { s.JiggleDamping = v; Custom(); }, 0.1f, 3f, "%.2f",
                "How quickly the wobble dies down (1 = as the body mod made it).");
            Slider("Pull back to shape###attract", () => s.JiggleAttract, v => { s.JiggleAttract = v; Custom(); }, 0.1f, 3f, "%.2f",
                "How strongly the bones are pulled back to where the animation puts them (1 = as the body mod made it).");
            Slider("Top speed###jigglespeed", () => s.JiggleMaxSpeed, v => { s.JiggleMaxSpeed = v; Custom(); }, 0.5f, 20f, "%.1f",
                "The fastest the bones may move (stops wild flicks).");
            Check("The game's own chest physics too###chest", () => s.JiggleChest, v => s.JiggleChest = v,
                "Also use these on the game's own chest physics (bodies without a body mod).");
            ImGui.Unindent();
        }

        Check("Turning swings hair and jiggle###turn", () => s.TurnInertia, v => s.TurnInertia = v,
            "The game's physics doesn't notice the character turning. On: turning makes hair, clothes and jiggle bones lag "
            + "and swing out, as they would.");
        if(s.TurnInertia)
        {
            ImGui.Indent();
            Slider("Strength###turnstrength", () => s.TurnStrength, v => s.TurnStrength = v, 0f, 2f, "%.2f",
                "How much of the turn they lag behind (1 = all of it).");
            Slider("Most a frame (degrees)###turnmax", () => s.TurnMaxDegrees, v => s.TurnMaxDegrees = v, 1f, 45f, "%.0f",
                "Fast spins are counted as at most this many degrees a frame, so nothing flies off.");
            ImGui.Unindent();
        }

        Check("Reset after a jump###autoreset", () => s.AutoReset, v => s.AutoReset = v,
            "When a character jumps a long way in one frame (moved by Limber, a pose loaded, a teleport), its game physics "
            + "starts again from the pose instead of whipping across.");
        using(ImRaii.Disabled(!gp.CanReset))
        {
            if(ImGui.Button("Reset physics (chosen character)###reset_one") && actor.IsSet)
                gp.RequestReset(actor.Address);
            Tooltip("The chosen character's hair, clothes and jiggle start again from where the pose has them (after a "
                + "teleport, a loaded pose or unfreezing GPose).");
            ImGui.SameLine();
            if(ImGui.Button("Reset everyone's###reset_all"))
                gp.RequestReset(0);
            Tooltip("The same for every character the options above apply to (\"Who\").");
        }

        if(changed)
            config.Save();
    }

    private static void Tooltip(string text)
    {
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip(text);
    }
}
