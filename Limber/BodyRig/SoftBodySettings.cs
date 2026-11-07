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
    // What it collides with and how (the Physics collisions page).
    public CollisionSettings Collide { get; set; } = new();
    // Testicles: how much they hang with real gravity (0 = held to the modelled shape, 1 = hang fully the way down
    // is, in any pose), and how much both sides move as one sac (0 = each on its own).
    public float Hang { get; set; } = 0.6f;
    public float Together { get; set; } = 0.6f;

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
        copy.Collide = (Collide ?? new()).Clone();
        return copy;
    }

    /// <summary>Starting values for the butt rig: firmer, a little less swing.</summary>
    public static SoftBodySettings ButtDefaults() => new()
    {
        Firmness = 0.45f, Bounce = 0.7f, Weight = 0.4f, Jiggle = 1.6f, Cohesion = 0.75f, MaxStretch = 0.45f,
        Collide = CollisionSettings.ButtDefaults(),
    };

    /// <summary>Starting values for the belly rig: soft and heavy, slow to settle, held to the body at its rim; off
    /// until turned on (a grown belly shows without it).</summary>
    public static SoftBodySettings BellyDefaults() => new()
    {
        Enabled = false, Firmness = 0.35f, Bounce = 0.65f, Weight = 0.35f, Jiggle = 1.4f, Cohesion = 0.85f, MaxStretch = 0.35f,
        KeepShape = 1f, // (it wobbles, but doesn't sag at rest: it hangs from the spine, so a sag would tip the whole belly)
        Collide = CollisionSettings.BellyDefaults(),
    };

    /// <summary>Starting values for the testicle rig: heavier and swingier.</summary>
    public static SoftBodySettings TesticleDefaults() => new()
    {
        Firmness = 0.3f, Bounce = 0.7f, Weight = 0.75f, Jiggle = 1.3f, Cohesion = 0.9f, MaxStretch = 0.9f, Hang = 0.6f, Together = 0.6f,
        Collide = CollisionSettings.TesticleDefaults(),
    };
}

/// <summary>
/// What one soft part collides with, and how (Body > Physics collisions). Sizes are scales of Limber's own
/// estimate for the body (1 = as is); the margin is in centimetres.
/// </summary>
[Serializable]
public sealed class CollisionSettings
{
    // What pushes it.
    public bool OwnArms { get; set; } = true;       // the character's own upper arms
    public bool OwnHands { get; set; } = true;      // its own forearms and hands
    public bool OthersHands { get; set; } = true;   // other characters' forearms and hands
    public bool Thighs { get; set; }                // its own thighs
    public bool Floor { get; set; } = true;         // the ground under the character
    public bool GrownBelly { get; set; } = true;    // a grown (pregnant) belly pushes it out of the way
    public bool Props { get; set; } = true;         // collider props with Bone collision on

    // Sizes of the body's colliders.
    public float ArmSize { get; set; } = 1f;
    public float HandSize { get; set; } = 1f;
    public float ThighSize { get; set; } = 1f;
    public float FloorHeight { get; set; }          // cm, up (+) / down (-) from the character's feet

    // How it reacts.
    public float Strength { get; set; } = 1f;       // 1 = pushed fully out; less: it sinks into what touches it
    public float Margin { get; set; }               // cm kept between it and the colliders
    public float Friction { get; set; } = 0.3f;     // how much sliding along a collider is held back
    public float Rebound { get; set; }              // how much it bounces back off (0: stops dead against it)
    public float Softness { get; set; }             // 0 = hard edge; higher eases into contact over a little depth

    // Testicles: how far squeezing (thighs, hands, props from both sides) slides the whole sac out of the way.
    public float SacSlide { get; set; } = 1f;

    public CollisionSettings Clone() => (CollisionSettings)MemberwiseClone();

    public static CollisionSettings TesticleDefaults() => new() { OwnArms = false, Thighs = true, ThighSize = 1f, Friction = 0.15f, Softness = 0.3f };
    public static CollisionSettings ButtDefaults() => new() { OwnArms = false, Thighs = false, Friction = 0.4f, Softness = 0.2f };
    public static CollisionSettings BellyDefaults() => new() { OwnArms = false, GrownBelly = false, Friction = 0.4f, Softness = 0.3f };
}
