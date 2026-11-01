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

    public LimberAnimationsTab(Selection selection, RigSettings config, ThrobService throb, TwerkService twerk, AhegaoService ahegao)
    {
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
        ImGui.TextWrapped("Animations Limber plays by itself on the chosen character, on top of their pose and animation.");
        ImGui.Spacing();
        if(ImGui.CollapsingHeader("Throbbing###lanim_throb", ImGuiTreeNodeFlags.DefaultOpen))
            DrawThrob(name, actor);
        if(ImGui.CollapsingHeader("Twerking###lanim_twerk", ImGuiTreeNodeFlags.DefaultOpen))
            DrawTwerk(name, actor);
        if(ImGui.CollapsingHeader("Ahegao###lanim_ahegao", ImGuiTreeNodeFlags.DefaultOpen))
            DrawAhegao(name, actor);
    }

    private void DrawAhegao(string name, ActorRef actor)
    {
        var all = _config.Ahegao ??= new();
        if(!all.TryGetValue(name, out var s))
            all[name] = s = new AhegaoSettings();
        bool changed = false;

        ImGui.TextWrapped("An ahegao face made from the face's own bones: eyes rolled up, brows raised, the mouth open with the "
            + "tongue out, the head tipped back. It goes over the character's expression and eases in and out. "
            + "Needs a face with eye and mouth bones (the game's faces have them).");
        ImGui.Spacing();

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
        changed |= SliderAh("Mouth corners", s.Smile * 100f, v => s.Smile = v / 100f, -200f, 200f, "%.0f%%", "The mouth corners up (+, a smile, lifting the cheeks) or down (-), the lips near them following.");
        changed |= SliderAh("Upper lip", s.UpperLip * 100f, v => s.UpperLip = v / 100f, -200f, 200f, "%.0f%%", "The middle of the upper lip up (+) or down (-); the corners stay.");
        changed |= SliderAh("Lower lip", s.LowerLip * 100f, v => s.LowerLip = v / 100f, -200f, 200f, "%.0f%%", "The middle of the lower lip up (+) or down (-); the corners stay.");
        changed |= SliderAh("Tongue out", s.TongueOut * 100f, v => s.TongueOut = v / 100f, 0f, 400f, "%.0f%%", "How far the tongue comes out (past 100% it reaches further than it naturally would).");
        changed |= SliderAh("Tongue length", s.TongueLength * 100f, v => s.TongueLength = v / 100f, 0f, 200f, "%.0f%%", "The tongue longer.");
        changed |= SliderAh("Tongue droop", s.TongueDroop, v => s.TongueDroop = v, 0f, 120f, "%.0f°", "How far the tongue hangs down over the lip.");
        changed |= SliderAh("Tongue up / down", s.TongueUp, v => s.TongueUp = v, -90f, 90f, "%.0f°", "The tongue pointing up (+, the tip curling up toward the upper lip) or down (-). It still stays over the lower lip.");
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

        ImGui.TextWrapped("The character twerks: a squat with the upper body leaning forward and the hands on the knees, the hips "
            + "popping to the beat while the upper body keeps steady and the feet stay where the animation has them. "
            + "Works best over a standing idle; Dynamic Bones jiggle reacts to it.");
        ImGui.Spacing();

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

        ImGui.TextWrapped("The penis pulses by itself: it swells, lengthens a little and lifts on each beat, the beat rolling from the "
            + "base to the tip; the testicles can draw up with it. Pick a pattern (or let it change patterns on its own) and press Start. "
            + "It eases in and out, works with Dynamic Bones and the pose, and gets stronger while this character is in Oral mode. "
            + "Needs a body with penis bones (IVCS / YAS).");
        ImGui.Spacing();

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
