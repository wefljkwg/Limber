using System;
using System.Collections.Generic;
using System.Linq;

namespace Limber.BodyRig;

/// <summary>Soft body for Limber's breast rig. All values 0..1; see SoftBody for what they mean physically.</summary>
[Serializable]
public sealed class SoftBodySettings
{
    public bool Enabled { get; set; } = true;
    public float Firmness { get; set; } = 0.35f;   // how strongly it's pulled back to its shape on the body
    public float Bounce { get; set; } = 0.75f;     // how long it keeps wobbling (less damping)
    public float Jiggle { get; set; } = 2f;        // how much the wobble is exaggerated (1 = as simulated)
    public float Weight { get; set; } = 0.45f;     // gravity and how much it lags behind movement
    public float Cohesion { get; set; } = 0.6f;    // neighbouring bones keep their spacing (less = more liquid)
    // How much of the original shape is kept at rest (1 = exactly the modelled shape; it only moves while in
    // motion, and springs back to it; 0 = it settles with the simulated sag).
    public float KeepShape { get; set; } = 1f;
    public float MaxStretch { get; set; } = 0.55f; // furthest any point may move from rest, in breast-bone separations
    public bool Collisions { get; set; } = true;   // arms, hands, thighs, the floor, and other characters' hands

    // Shape: scales and moves all rig bones together (works with or without the physics). 1 / 0 = unchanged.
    public float Size { get; set; } = 1f;          // overall
    public float Width { get; set; } = 1f;         // side to side
    public float Height { get; set; } = 1f;        // up and down
    public float Projection { get; set; } = 1f;    // how far it sticks out forward
    public float Lift { get; set; } = 0f;          // up (+) / down (-), in breast-bone separations
    public float Spacing { get; set; } = 0f;       // apart (+) / together (-), in breast-bone separations

    // Blend shapes (BodyShapes), by name: 0 = off, 1 = full, in between blends, beyond exaggerates. Replaced as a
    // whole when changed (never edited in place), so the physics always reads a complete set.
    public Dictionary<string, float> Shapes { get; set; } = new();

    public bool ShapeIsDefault =>
        Size == 1f && Width == 1f && Height == 1f && Projection == 1f && Lift == 0f && Spacing == 0f
        && (Shapes == null || Shapes.Values.All(v => v == 0f));

    public SoftBodySettings Clone()
    {
        var copy = (SoftBodySettings)MemberwiseClone();
        copy.Shapes = new Dictionary<string, float>(Shapes ?? new());
        return copy;
    }

    /// <summary>Starting values for the butt rig: firmer, a little less swing.</summary>
    public static SoftBodySettings ButtDefaults() => new()
    {
        Firmness = 0.45f, Bounce = 0.7f, Weight = 0.4f, Jiggle = 1.6f, Cohesion = 0.75f, MaxStretch = 0.45f,
    };

    /// <summary>Starting values for the testicle rig: heavier and swingier.</summary>
    public static SoftBodySettings TesticleDefaults() => new()
    {
        Firmness = 0.3f, Bounce = 0.7f, Weight = 0.75f, Jiggle = 1.8f, Cohesion = 0.9f, MaxStretch = 0.9f,
    };
}
