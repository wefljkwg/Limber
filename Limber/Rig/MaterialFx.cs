using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Limber.Rig;

/// <summary>
/// One material effect on a shader look (the Koikatsu Material Editor's swapped shaders, done by Limber's own drawing:
/// see Gpu/EffectRenderer): which one, how much of it, its colours and its settings. A look can stack several; they're
/// applied in order on the character's visible pixels.
/// </summary>
[Serializable]
public sealed class MaterialFx
{
    public string Kind { get; set; } = "";
    public bool On { get; set; } = true;
    public float Strength { get; set; } = 1f;   // 0: none of it, 1: all of it
    public float Speed { get; set; } = 1f;      // for the moving ones
    public float[] A { get; set; } = [1f, 1f, 1f];
    public float[] B { get; set; } = [0f, 0f, 0f];
    public float[] P { get; set; } = [0f, 0f, 0f, 0f];

    public MaterialFx Clone()
    {
        var c = (MaterialFx)MemberwiseClone();
        c.A = (float[])(A ?? [1f, 1f, 1f]).Clone();
        c.B = (float[])(B ?? [0f, 0f, 0f]).Clone();
        c.P = (float[])(P ?? [0f, 0f, 0f, 0f]).Clone();
        return c;
    }

    public static Vector3 Colour(float[]? c) => c is { Length: >= 3 } ? new Vector3(c[0], c[1], c[2]) : Vector3.One;

    /// <summary>A setting: its label, range, starting value and what it does.</summary>
    public sealed record Param(string Label, float Min, float Max, float Default, string Tip);

    /// <summary>One kind of effect (the id is what the shader knows it by).</summary>
    public sealed record Def(int Id, string Name, string Group, string Tip, string? ALabel, Vector3 ADefault, string? BLabel, Vector3 BDefault,
        Param[] Params, bool Moves);

    private static Param Pr(string label, float min, float max, float def, string tip) => new(label, min, max, def, tip);

    private static readonly Vector3 White = Vector3.One, Black = Vector3.Zero;

    public static readonly Def[] Defs =
    [
        // Colour
        new(1, "Colour grade", "Colour", "Hue, saturation, contrast and brightness, and a tint.", "Tint", White, null, Black,
            [Pr("Hue shift", -180, 180, 0, "Turns every colour round the colour wheel (degrees)."), Pr("Saturation", 0, 3, 1, "0: grey, 1: as it is, above: more vivid."),
             Pr("Contrast", 0, 3, 1, "1: as it is."), Pr("Brightness", 0, 3, 1, "1: as it is.")], false),
        new(2, "Invert", "Colour", "Every colour turned to its opposite (a negative).", null, White, null, Black, [], false),
        new(3, "Greyscale / sepia", "Colour", "Black and white, or old-photo brown.", null, White, null, Black,
            [Pr("Sepia", 0, 1, 0, "0: grey, 1: brown.")], false),
        new(4, "Posterize", "Colour", "Fewer colour steps, like a poster print.", null, White, null, Black,
            [Pr("Levels", 2, 16, 4, "How many steps each colour has.")], false),
        new(42, "Gradient map", "Colour", "Their shading recoloured from one colour (dark) to another (light).", "Dark", new(0.1f, 0f, 0.25f), "Light", new(1f, 0.75f, 0.4f), [], false),
        new(44, "Shadow", "Colour", "Darkened and tinted, like standing in shadow.", "Tint", new(0.6f, 0.6f, 0.8f), null, Black,
            [Pr("Darkness", 0, 1, 0.5f, "How much darker.")], false),
        new(24, "Hue cycle", "Colour", "Every colour slowly turning round the colour wheel.", null, White, null, Black, [], true),
        new(48, "Rainbow wash", "Colour", "Rainbow bands sweeping up the body.", null, White, null, Black,
            [Pr("Band size", 0.1f, 5, 1, "How many bands over the body."), Pr("Amount", 0, 1, 0.6f, "How much of the rainbow shows.")], true),
        new(47, "Pulse", "Colour", "Brightness throbbing, with a colour flash.", "Flash colour", new(1f, 0.3f, 0.5f), null, Black,
            [Pr("Throb", 0, 1, 0.4f, "How much the brightness swells."), Pr("Flash", 0, 1, 0.3f, "How much of the flash colour.")], true),

        // Looks
        new(5, "Toon (flat bands)", "Looks", "Cartoon shading: the light in a few flat steps.", "Shadow tint", new(0.75f, 0.7f, 0.9f), "Highlight", White,
            [Pr("Bands", 2, 8, 3, "How many steps."), Pr("Softness", 0, 1, 0, "Blends the steps back toward smooth."), Pr("Highlight", 0, 1, 0.3f, "A sharp shine.")], false),
        new(45, "Cel shading (light)", "Looks", "Anime cel shading from the surface's direction to the light.", "Shadow tint", new(0.6f, 0.55f, 0.75f), "Edge light", White,
            [Pr("Bands", 2, 6, 2, "How many steps of light."), Pr("Edge light", 0, 1, 0.2f, "A thin bright line on the edges.")], false),
        new(6, "Silhouette", "Looks", "A flat solid colour, like a shadow person.", "Colour", Black, "Edge glow", new(0.4f, 0.6f, 1f),
            [Pr("Edge glow", 0, 2, 0.5f, "A glow round the edges.")], false),
        new(7, "Hologram", "Looks", "Sci-fi hologram: tinted, scanlines moving up, rim glow, flicker.", "Tint", new(0.3f, 0.9f, 1f), "Rim", new(0.6f, 1f, 1f),
            [Pr("Lines per metre", 5, 200, 60, "How close the scanlines are."), Pr("Flicker", 0, 1, 0.3f, "Sudden dimming now and then."), Pr("Noise", 0, 1, 0.2f, "Static.")], true),
        new(8, "X-ray", "Looks", "Dark inside, bright edges, like an X-ray.", "Inside", new(0.05f, 0.1f, 0.3f), "Edges", new(0.6f, 0.9f, 1f),
            [Pr("Edge size", 0.05f, 1, 0.4f, "How far in from the edges the glow reaches."), Pr("Inside brightness", 0, 2, 0.5f, "How much the inside shows.")], false),
        new(9, "Thermal vision", "Looks", "A heat camera: dark blue cold to white hot.", null, White, null, Black,
            [Pr("Heat", -0.5f, 0.5f, 0.1f, "Makes them hotter or colder."), Pr("Contrast", 0.5f, 3, 1.2f, "")], false),
        new(10, "Night vision", "Looks", "Green night-vision goggles, with noise and lines.", "Colour", new(0.3f, 1f, 0.35f), null, Black,
            [Pr("Noise", 0, 0.5f, 0.12f, ""), Pr("Lines", 0, 1, 0.4f, "")], true),
        new(11, "Metal", "Looks", "Polished metal (chrome, silver, gold, copper by colour).", "Metal colour", new(0.9f, 0.9f, 0.95f), null, Black,
            [Pr("Shininess", 1, 200, 40, "How sharp the shine is."), Pr("Reflection contrast", 0.2f, 4, 1, ""), Pr("Keep detail", 0, 1, 0.3f, "How much of their own shading stays.")], false),
        new(46, "Gold statue", "Looks", "A metal statue of them, keeping their shading.", "Metal colour", new(1f, 0.78f, 0.3f), null, Black, [], false),
        new(15, "Stone", "Looks", "Turned to stone (petrified), with cracks.", "Stone colour", new(0.62f, 0.6f, 0.56f), null, Black,
            [Pr("Grain size", 0.5f, 5, 1.5f, ""), Pr("Darkness", 0, 1, 0.2f, ""), Pr("Cracks", 0, 1, 0.5f, "")], false),
        new(12, "Glass", "Looks", "Clear glass: what's behind bent through them, edges catching the light.", "Tint", new(0.85f, 0.95f, 1f), "Edge colour", White,
            [Pr("Bend", 0, 40, 12, "How far the picture bends (pixels)."), Pr("Edge shine", 0, 2, 0.6f, ""), Pr("Tint", 0, 1, 0.3f, "")], false),
        new(13, "Slime / goo", "Looks", "Wobbling see-through slime with a wet shine.", "Slime colour", new(0.4f, 1f, 0.5f), null, Black,
            [Pr("Wobble", 0, 30, 8, "How much it bends what's behind (pixels)."), Pr("Shine", 0, 2, 1, ""), Pr("Colour", 0, 1, 0.5f, "How much of the slime colour.")], true),
        new(14, "Ice / frost", "Looks", "Frozen: icy tint, cracks and glints.", "Ice colour", new(0.7f, 0.9f, 1f), null, Black,
            [Pr("Frost", 0, 1, 0.6f, ""), Pr("Cracks", 0, 1, 0.5f, ""), Pr("Glints", 0, 1, 0.4f, ""), Pr("Crack size", 0.5f, 5, 1.5f, "")], true),
        new(39, "Wet / oily", "Looks", "Glistening wet or oiled skin.", null, White, null, Black,
            [Pr("Darken", 0, 1, 0.3f, "Wet things look a little darker."), Pr("Gloss", 0, 2, 0.8f, ""), Pr("Gloss sharpness", 2, 200, 60, "")], false),
        new(38, "Snow", "Looks", "Snow settled on everything facing up, with sparkles.", "Snow colour", new(0.95f, 0.97f, 1f), null, Black,
            [Pr("Coverage", 0, 1, 0.5f, ""), Pr("Sparkle", 0, 1, 0.5f, "")], false),
        new(23, "Iridescent", "Looks", "Oil-slick / pearl colours shifting with the angle.", null, White, null, Black,
            [Pr("Colour spread", 0, 4, 1.5f, ""), Pr("Drift", 0, 2, 0.3f, "How fast the colours move."), Pr("Amount", 0, 1, 0.6f, "")], true),
        new(50, "Inner glow", "Looks", "A coloured glow fading in from the edges.", "Colour", new(1f, 0.4f, 0.8f), null, Black,
            [Pr("Tightness", 0.5f, 8, 2, ""), Pr("Strength", 0, 2, 1, "")], false),
        new(49, "Dissolve", "Looks", "Eaten away in noisy patches to a colour, with a burning edge.", "Gone colour", Black, "Edge", new(1f, 0.5f, 0.1f),
            [Pr("Amount", 0, 1, 0.4f, "How much is gone."), Pr("Patch size", 0.1f, 5, 1, ""), Pr("Breathe", 0, 1, 0, "1: the amount comes and goes.")], true),

        // Patterns (on the body, following them)
        new(16, "Space / galaxy", "Patterns", "A starfield and nebula through them.", "Nebula 1", new(0.35f, 0.1f, 0.6f), "Nebula 2", new(0.1f, 0.5f, 0.9f),
            [Pr("Stars", 0, 1, 0.4f, ""), Pr("Scale", 0.1f, 5, 1, "")], true),
        new(17, "Lava", "Patterns", "Molten rock: glowing cracks flowing.", "Hot", new(1f, 0.45f, 0.05f), "Crust", new(0.15f, 0.03f, 0.02f),
            [Pr("Scale", 0.1f, 5, 1, ""), Pr("Glow", 0, 3, 1.2f, "")], true),
        new(18, "Water caustics", "Patterns", "Rippling light as if under water.", "Water colour", new(0.3f, 0.6f, 0.9f), null, Black,
            [Pr("Scale", 0.1f, 5, 1, ""), Pr("Brightness", 0, 2, 0.8f, "")], true),
        new(19, "Stripes", "Patterns", "Stripes across them (can slide).", "Colour 1", White, "Colour 2", new(1f, 0.3f, 0.5f),
            [Pr("Width (cm)", 0.2f, 30, 4, ""), Pr("Angle", -90, 90, 0, "")], true),
        new(20, "Checker", "Patterns", "A chequerboard.", "Colour 1", White, "Colour 2", Black,
            [Pr("Square (cm)", 0.2f, 30, 5, "")], false),
        new(21, "Polka dots", "Patterns", "Dots all over (can drift).", "Dots", new(1f, 0.85f, 0.2f), "Base", new(0.9f, 0.2f, 0.4f),
            [Pr("Spacing (cm)", 0.5f, 30, 6, ""), Pr("Dot size", 0.05f, 1, 0.5f, "")], true),
        new(22, "Camouflage", "Patterns", "Camo blotches, keeping their shading.", "Colour 1", new(0.25f, 0.32f, 0.18f), "Colour 2", new(0.55f, 0.5f, 0.35f),
            [Pr("Scale", 0.1f, 5, 1, "")], false),
        new(37, "Sparkle / glitter", "Patterns", "Twinkling glitter over them.", "Sparkle colour", White, null, Black,
            [Pr("Density", 0, 1, 0.4f, ""), Pr("Fineness", 0.2f, 5, 1, "")], true),
        new(41, "Energy bands", "Patterns", "Bright bands rising through them.", "Colour", new(0.4f, 0.8f, 1f), null, Black,
            [Pr("Spacing (cm)", 1, 100, 25, ""), Pr("Width", 0.01f, 1, 0.2f, "")], true),

        // Picture filters (like the Koikatsu grab-pass shaders)
        new(25, "CRT / old TV", "Filters", "Scanlines, colour fringes, a rolling bar and static.", null, White, null, Black,
            [Pr("Scanlines", 0, 1, 0.5f, ""), Pr("Colour split (px)", 0, 10, 2, ""), Pr("Roll bar", 0, 1, 0.5f, ""), Pr("Static", 0, 0.5f, 0.05f, "")], true),
        new(26, "Glitch", "Filters", "Rows jumping sideways with colour splits.", null, White, null, Black,
            [Pr("Block size (px)", 2, 60, 12, ""), Pr("Amount", 0, 1, 0.3f, "How many rows jump."), Pr("Colour split (px)", 0, 15, 3, "")], true),
        new(27, "Blur", "Filters", "Soft focus / frosted.", null, White, null, Black,
            [Pr("Radius (px)", 0.5f, 20, 4, "")], false),
        new(43, "Heat haze", "Filters", "Shimmering wobble like hot air.", null, White, null, Black,
            [Pr("Amount (px)", 0, 20, 4, ""), Pr("Scale", 0.1f, 5, 1, "")], true),
        new(35, "Chromatic aberration", "Filters", "Red and blue fringes, like a cheap lens.", null, White, null, Black,
            [Pr("Spread (px)", 0, 15, 3, "")], false),
        new(36, "Film grain", "Filters", "Grainy old film.", null, White, null, Black,
            [Pr("Amount", 0, 0.5f, 0.12f, "")], true),
        new(40, "Hex tiles", "Filters", "Broken into hexagon tiles (frosted glass block).", null, White, null, Black,
            [Pr("Tile size (px)", 3, 60, 14, ""), Pr("Grout", 0, 1, 0.3f, "Dark lines between tiles.")], false),

        // Art styles
        new(28, "Pencil sketch", "Art", "A pencil drawing: outlines and hatching on paper.", "Paper", new(0.97f, 0.95f, 0.9f), "Lead", new(0.15f, 0.15f, 0.18f),
            [Pr("Lines", 0, 5, 2, ""), Pr("Hatching", 0, 1, 0.6f, "")], false),
        new(29, "Oil painting", "Art", "Painterly brush strokes.", null, White, null, Black,
            [Pr("Brush (px)", 1, 6, 3, "")], false),
        new(30, "Ink lines", "Art", "Comic ink outlines on their detail.", "Ink", Black, null, Black,
            [Pr("Line width (px)", 1, 4, 1, ""), Pr("Threshold", 0, 1, 0.25f, "Higher: fewer lines."), Pr("Keep colour", 0, 1, 1, "0: white paper, 1: their colours.")], false),
        new(31, "Halftone", "Art", "Comic-print dots.", "Ink", new(0.1f, 0.1f, 0.15f), "Paper", new(1f, 0.97f, 0.9f),
            [Pr("Dot size (px)", 2, 30, 6, ""), Pr("Angle", 0, 90, 45, "")], false),
        new(32, "Dither", "Art", "Retro dithered pixels in two colours.", "Dark", new(0.1f, 0.05f, 0.2f), "Light", new(1f, 0.9f, 0.7f),
            [Pr("Levels", 2, 8, 2, ""), Pr("Pixel size", 1, 8, 2, "")], false),
        new(33, "Cross-hatch", "Art", "Pen cross-hatching by darkness.", "Ink", new(0.1f, 0.1f, 0.15f), "Paper", new(0.98f, 0.96f, 0.9f),
            [Pr("Spacing (px)", 3, 20, 6, ""), Pr("Line width", 0.5f, 4, 1, "")], false),
        new(34, "Emboss", "Art", "Raised relief, like stamped metal.", null, White, null, Black,
            [Pr("Depth", 0, 5, 2, ""), Pr("Keep colour", 0, 1, 0.5f, "")], false),
    ];

    private static readonly Dictionary<string, Def> ByName = Defs.ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);

    public static Def? DefOf(string name) => ByName.TryGetValue(name ?? "", out var d) ? d : null;

    public static int IdOf(string name) => DefOf(name)?.Id ?? 0;

    /// <summary>A new effect of this kind with its starting colours and settings.</summary>
    public static MaterialFx New(Def d) => new()
    {
        Kind = d.Name,
        A = [d.ADefault.X, d.ADefault.Y, d.ADefault.Z],
        B = [d.BDefault.X, d.BDefault.Y, d.BDefault.Z],
        P = Enumerable.Range(0, 4).Select(i => i < d.Params.Length ? d.Params[i].Default : 0f).ToArray(),
    };
}
