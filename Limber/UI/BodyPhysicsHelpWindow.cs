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
            "Body physics makes the chest, butt and testicles soft: they jiggle as the character moves, bounce, and dent when " +
            "something presses on them (hands, thighs, the floor, props).",
            "To do that, Limber adds a few extra bones inside those parts and makes a copy of your body models that follows " +
            "them. Your own mods are never changed: Limber only reads them and puts its copies in two mods of its own.",
            "It's only on your screen. Other players see nothing different.",
        ]),
        ("Before you start", "What you need",
        [
            "1. Penumbra, with a body mod on your character (for example Bibo+, RUEXB+, YAB, TBSE...).",
            "2. A skeleton mod with IVCS or YAS bones, which almost every body mod asks for anyway. Without one there are no " +
            "butt or genital bones to move (the chest still works).",
            "3. An adult character. Body physics never works on Lalafell or child characters.",
            "4. GPose (or \"Also run outside GPose\" turned on in Settings > Advanced).",
        ]),
        ("Step by step", "Setting it up (once)",
        [
            "1. Wear the outfit or body you want physics on (naked works too).",
            "2. Pick your character at the top of the Limber window (or tick \"Follow my target\").",
            "3. On this page, click \"Rig everything (one click)\". Leave \"Fit the boxes to the models automatically\" on.",
            "4. Wait for the log to say it's done. Limber builds two Penumbra mods for your character's collection, " +
            "\"Limber Breast Rig\" (the extra bones) and \"Limber Rigged Models\" (copies of your models that follow them), " +
            "turns them on above your other mods, and redraws your character.",
            "5. Open the Breasts / Butt / Testicles headers and make sure \"... on\" is ticked. The status line should say " +
            "\"Physics: running on 1 character(s)\".",
            "That's it: move the character (walk, emote, pose) and it jiggles.",
        ]),
        ("Making it feel right", "The sliders (each part has its own)",
        [
            "Firmness: how quickly it springs back. Low = soft and floppy, high = firm.",
            "Bounce: how long it keeps bouncing after a movement.",
            "Weight: heavier swings further and slower.",
            "Jiggle: exaggerates the movement.",
            "Keep shape: 1 keeps the model's own shape when it's still.",
            "Cohesion: high moves as one piece; low is more liquid.",
            "Max stretch: a limit on how far it can swing and stretch.",
            "Collisions: lets arms, hands, thighs, the floor and props push on it.",
            "Shape (under each part): size, width, height, projection, lift and spacing, on top of the model's own shape.",
            "Tip: change one slider at a time and move the character to see it.",
        ]),
        ("Touching", "Hands, grabbing and props",
        [
            "With Collisions on, a hand pressed into the chest or butt dents it and pushes it aside.",
            "On the Hands page, \"Grab\" curls the fingers onto the skin under them and keeps them on it as it moves. " +
            "\"Lock\" holds that spot to the hand; \"Whole breast\" carries the whole breast with the hand.",
            "On the Devices page you can add colliders (balls, boxes) and attach them to bones, or place them in the scene, " +
            "for the body to press against.",
        ]),
        ("If something's off", "When it doesn't work",
        [
            "Nothing moves: open the part's header and click \"Check the worn models\". Every model that should move must say " +
            "\"rigged\". If one says NOT rigged, click \"Rig everything\" again while wearing it.",
            "\"Physics: not running on anyone\": the character has no rig bones yet (rig it), the part is turned off, " +
            "or Limber isn't running here (GPose).",
            "You changed outfit, body mod or skeleton mod: click \"Rig everything\" again. Limber rigs what's worn at the time.",
            "Seams or gaps on the body: make sure Limber's two mods are above your body and skeleton mods in Penumbra " +
            "(Limber sets this itself; another mod with a higher priority can push in front).",
            "It looks wrong after a Penumbra change: redraw your character (or rig again).",
            "To take it all off: turn off \"Limber Breast Rig\" and \"Limber Rigged Models\" in Penumbra.",
        ]),
        ("Advanced", "The other headers",
        [
            "Bone placement (in each part): where the extra bones go. With automatic fitting on you never need it; " +
            "\"Show the box\" shows them on the character.",
            "Manual rig build: builds only the bone mod, for people who weight their own models in Blender.",
            "Live mesh (experimental): per-vertex soft body on the graphics card, smoother denting. It may crash the game; " +
            "save your work first.",
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
