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
    // While walking or running (by how fast the character goes): how much the movement swings it. 0 = held still to
    // the body, 1 = as simulated, up to 2 = more. Standing still is never changed.
    public float Moving { get; set; } = 1f;
    // How much the swing keeps the part where it is (0 = swings as simulated, 1 = stays put; it still ripples), and
    // how much it keeps its angle (0 = turns with the swing, 1 = slides with it without turning).
    public float KeepPosition { get; set; }
    // The body mod's own jiggle (its physics bones the part hangs from) ignored: the part is held where that bone sits
    // at rest on the body, so only Limber's physics moves it (both together flailed far too much).
    public bool IgnoreModJiggle { get; set; } = true;
    public float KeepRotation { get; set; }
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
    // Breasts: the size of each part from the base to the tip (1 = unchanged): Breast 1 the base on the chest,
    // 2 a bit out from it, 3 the middle (the bulk), 4 the areolas, 5 the tip.
    // Breasts: the whole breast turned about its base: out (+) / in (-), up (+) / down (-), degrees.
    public float TiltOut { get; set; }
    public float TiltUp { get; set; }
    public float Part1 { get; set; } = 1f;
    public float Part2 { get; set; } = 1f;
    public float Part3 { get; set; } = 1f;
    public float Part4 { get; set; } = 1f;
    public float Part5 { get; set; } = 1f;
    // ...and each part moved: 5 x (out, up, forward), in breast-bone separations (0 = unchanged). Replaced as a whole
    // when changed (never edited in place).
    public float[] PartMove { get; set; } = new float[15];

    // Blend shapes (BodyShapes), by name: 0 = off, 1 = full, in between blends, beyond exaggerates. Replaced as a
    // whole when changed (never edited in place), so the physics always reads a complete set.
    public Dictionary<string, float> Shapes { get; set; } = new();

    public bool ShapeIsDefault =>
        Size == 1f && Width == 1f && Height == 1f && Projection == 1f && Lift == 0f && Spacing == 0f
        && TiltOut == 0f && TiltUp == 0f
        && Part1 == 1f && Part2 == 1f && Part3 == 1f && Part4 == 1f && Part5 == 1f
        && (PartMove == null || PartMove.All(v => v == 0f))
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

    /// <summary>Physics steps per frame (at 60 fps; as many per second at any frame rate). Collisions are worked out
    /// in every step, so fast arms and hands don't pass through between frames.</summary>
    public int Substeps { get; set; } = 4;
    /// <summary>The fastest (m/s) a point may wobble relative to the body (stops wild flicks after hard hits).</summary>
    public float MaxSpeed { get; set; } = 10f;

    public CollisionSettings Clone() => (CollisionSettings)MemberwiseClone();

    public static CollisionSettings TesticleDefaults() => new() { OwnArms = false, Thighs = true, ThighSize = 1f, Friction = 0.15f, Softness = 0.3f };
    public static CollisionSettings ButtDefaults() => new() { OwnArms = false, Thighs = false, Friction = 0.4f, Softness = 0.2f };
    public static CollisionSettings BellyDefaults() => new() { OwnArms = false, GrownBelly = false, Friction = 0.4f, Softness = 0.3f };
}
