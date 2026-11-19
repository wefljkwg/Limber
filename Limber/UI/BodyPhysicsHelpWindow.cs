using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace Limber.UI;

/// <summary>The Body physics page's "How to use this" pop-up: a step-by-step guide for someone new to it.</summary>
public sealed class BodyPhysicsHelpWindow : Window
{
    private static readonly Vector4 Heading = new(0.95f, 0.85f, 0.55f, 1f);
    private static readonly Vector4 Dim = new(0.65f, 0.65f, 0.65f, 1f);

    private static readonly (string Tab, string Title, string[] Lines)[] Sections =
    [
        ("Start here", "What Body physics does",
        [
            "Body physics makes the breasts, butt, testicles and belly soft: they jiggle as the character moves, bounce, and " +
            "dent when something presses on them (hands, thighs, the floor, props).",
            "It works through the live rig (on by default): Limber keeps a few extra bones inside those parts itself and bends " +
            "the worn models on the graphics card. Nothing is built, your mods are never changed, and the character is never " +
            "redrawn. Change outfit and it follows by itself.",
            "Each part can also be reshaped (size, tilt, the breasts' and butt's five sections, blend shapes), and the belly can " +
            "be made pregnant: Belly > Settings > Pregnancy holds it at a size, and Limber Animations > Expansion grows it over time.",
            "It's only on your screen. Other players see nothing different.",
        ]),
        ("Before you start", "What you need",
        [
            "1. A mod manager, with a body mod on your character (for example Bibo+, RUEXB+, YAB, TBSE...).",
            "2. A skeleton mod with IVCS or YAS bones, which almost every body mod asks for anyway. Without one there are no " +
            "butt or genital bones to move (the breasts still work).",
            "3. GPose, or \"Also run outside GPose\" turned on in Settings > Advanced.",
        ]),
        ("Step by step", "Setting it up",
        [
            "1. Pick the character at the top of the Limber window (or tick \"Follow my target\").",
            "2. On this page, tick \"Body physics on\". Leave \"Live rig (no rigging, no redraw)\" ticked.",
            "3. Under Parts, tick the parts you want: Breasts, Butt, Testicles, Belly. The line next to each says whether it's running.",
            "4. Give it a moment the first time: the line under Live rig says it's getting the character ready, then how many " +
            "vertices are on the bones.",
            "5. Move the character (walk, emote, pose) and it jiggles.",
            "Who it works on: \"Characters affected\" (under Body physics on) can limit it to the characters you tick; otherwise " +
            "it's on everyone nearby with a body it knows.",
            "Several characters in the same outfit: keep \"Separate wobbles\" on, and the chosen character and the three nearest " +
            "others each wobble on their own.",
            "Lots of characters at once, or a slower PC: \"Rig everything (one click)\" under Rig builds the extra bones into two " +
            "mods instead (\"Limber Breast Rig\" and \"Limber Rigged Models\") and redraws the character once. It's lighter to run, " +
            "but has to be done again after an outfit or body mod change.",
        ]),
        ("Each part", "A part's Settings",
        [
            "Open a part's \"Settings\" to tune it.",
            "Physics: Firmness (how quickly it springs back), Bounce (how long it keeps bouncing), Weight (heavier swings " +
            "further and slower), Keep shape (1 keeps the model's own shape at rest), Jiggle (exaggerates the movement), " +
            "Cohesion (one piece, or more liquid), Max stretch (how far it can swing) and While moving (how much walking and " +
            "running swing it: lower stops the flailing). Testicles also have Hang (fall with " +
            "real gravity in any pose) and One sac (both sides moving together).",
            "Shape: Size, Width, Height, Projection, Lift and Spacing on top of the model's own shape; Tilt out / in and " +
            "up / down; and five sections from the base to the tip, each with its own size and X / Y / Z position (breasts, " +
            "butt and testicles).",
            "Blend shapes: ready-made shapes (spherical, melons, saggy, apart, bigger nipples, nipples up or down...) mixed in by amount.",
            "\"Reset shape\" takes the shape back to the model's own; \"Default soft ...\" puts the physics back and keeps the shape.",
            "Tip: change one slider at a time and move the character to see it.",
        ]),
        ("Touching", "Collisions, hands and props",
        [
            "Body > Physics collisions: per part, what pushes it (its own arms, hands and thighs, other characters' hands, " +
            "the floor, a grown belly, props) and how it gives way. Presets there for quick setups.",
            "Hands page: \"Grab\" curls the fingers onto the skin under them and keeps them there as it moves; \"Lock\" holds " +
            "that spot to the hand; \"Whole breast\" carries the whole breast with the hand.",
            "Scene > Devices: colliders (balls, boxes, toys) placed in the scene or attached to bones. With Mesh collision on, " +
            "they press into the skin itself.",
        ]),
        ("If something's off", "When it doesn't work",
        [
            "Nothing moves: check \"Body physics on\", that the part is ticked, that the character is ticked under \"Characters " +
            "affected\" (if that's limited), and that you're in GPose (or running outside it).",
            "The line under Live rig says it's getting ready for a long time: the character's models are still being found. " +
            "Redraw the character, or change outfit and back.",
            "Two characters in the same outfit move together: turn on \"Separate wobbles\", or use \"Rig everything\".",
            "Using \"Rig everything\": after a change of outfit, body mod or skeleton mod, rig again. \"Check the worn models\" " +
            "(in a part's Settings) says which models are rigged. Limber's two mods must stay above your body and skeleton mods; " +
            "to take it all off, turn those two mods off in your mod manager.",
            "Seams, gaps or spikes on the body: tell the author which part and what you were doing.",
        ]),
        ("More", "Further down the page",
        [
            "Bone placement (advanced), in each part's Settings: where the extra bones go. With automatic fitting on you never need it.",
            "The game's own physics: changes to the game's own hair, clothes and body-mod jiggle bones (frame rate, buzzing against " +
            "the body, more jiggle), separate from Limber's.",
            "Experimental: Limber's per-vertex soft body on the graphics card, for smoother denting. It may crash the game; save first.",
            "Also on the live rig: the Body shape page's sliders and the stomach bulge, and Skin effects.",
        ]),
    ];

    public BodyPhysicsHelpWindow() : base("Body physics - how to use it###LimberBodyPhysicsHelp")
    {
        Size = new Vector2(600, 520);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        if (!ImGui.BeginTabBar("##lmb_physics_help"))
            return;
        foreach (var (tab, title, lines) in Sections)
        {
            if (!ImGui.BeginTabItem(tab))
                continue;
            ImGui.Spacing();
            ImGui.TextColored(Heading, title);
            ImGui.Separator();
            ImGui.Spacing();
            foreach (var line in lines)
            {
                ImGui.TextWrapped(line);
                ImGui.Spacing();
            }
            ImGui.EndTabItem();
        }
        ImGui.EndTabBar();
        ImGui.Spacing();
        ImGui.TextColored(Dim, "Hover any slider or button on the Body physics page for a short explanation of it.");
    }
}
