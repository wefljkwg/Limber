using Limber.Core;
using System.Numerics;

namespace Limber.Rig;

public enum GoalSolve
{
    // Hand or foot: bend the arm or leg (upper bone + middle joint) so the bone reaches the target.
    TwoBone,
    // Turn a few joints above the bone so it reaches the target (spine, neck, elbow or knee swing, any bone).
    Chain,
    // Move the bone itself, with everything below it (hips).
    Rigid,
}

/// <summary>What the bone on the other side (left / right) does when this one is moved.</summary>
public enum MirrorMode
{
    Off,
    Mirror,     // the same move, mirrored (both arms up)
    Opposite,   // the opposite move (one arm up, the other down)
}

public enum GoalSpace
{
    // Held relative to a bone higher up the body, so it moves along when the body moves.
    FollowBody,
    // Held still relative to the character's root: stays put while the rest of the body moves.
    Pinned,
    // Held relative to a bone on another (or the same) character.
    Attached,
}

// One thing Limber holds on a character: "this bone should be here".
// The target is re-applied every frame on top of whatever pose the character has.
public sealed class Goal
{
    public required string Bone { get; init; }
    public GoalSolve Solve { get; set; }
    public string? UpperBone { get; set; }      // TwoBone only
    public string? MiddleBone { get; set; }     // TwoBone only
    public int ChainLength { get; set; } = 2;   // Chain only
    public Vector3? BendHint { get; set; }      // TwoBone: which way the joint bends when the limb is straight (model space)

    public GoalSpace Space { get; set; } = GoalSpace.FollowBody;
    public GoalSpace HomeSpace { get; set; } = GoalSpace.FollowBody; // where it goes back to when detached

    public bool Enabled { get; set; } = true;
    public bool LockRotation { get; set; }
    public MirrorMode Mirror { get; set; }
    // Off: the bone is only turned (on top of the bone above it, like a plain rotation), nothing above it bends.
    // A bone you only turn with the rings starts like that; moving it (drag, arrows, pin, attach) holds its place.
    public bool HoldPosition { get; set; } = true;

    // Only turned so far: from now on also hold its place (rotation kept as it is).
    public void KeepPosition()
    {
        if(HoldPosition)
            return;
        HoldPosition = true;
        CaptureRotation = true;
    }

    // Rotation only, on top of the parent bone (no IK).
    public bool TurnOnly => Space == GoalSpace.FollowBody && !HoldPosition;

    // Target in the space chosen above.
    public Frame Local = Frame.Identity;
    public bool HasTarget;

    // Requests from the UI, handled at the right moment in the next frame update.
    public bool CapturePosition = true;
    public bool CaptureRotation = true;
    public Vector3? PendingWorldPosition;
    public Quaternion? PendingWorldRotation;
    // A turn on top of the bone's rest pose (hand presets), set once the skeleton is at hand.
    public Quaternion? PendingRestTurn;

    // Attached only
    public nint ParentActor;
    public ushort ParentObjectIndex;
    public string ParentActorName = string.Empty;
    public string? ParentBone;

    // Filled in during the frame update.
    public int SortDepth;
    public bool LastSolveFailed;

    // Where the bone actually ended up last frame (model space), with this goal applied. Re-anchoring the goal
    // (attach, pin, detach, hold rotation) starts from here, so the bone stays where the user put it instead of
    // jumping back to the animation's pose.
    public Frame SolvedModel = Frame.Identity;
    public bool HasSolved;

    // A full copy, for undo / redo.
    public Goal Clone() => new()
    {
        Bone = Bone,
        Solve = Solve,
        UpperBone = UpperBone,
        MiddleBone = MiddleBone,
        ChainLength = ChainLength,
        BendHint = BendHint,
        Space = Space,
        HomeSpace = HomeSpace,
        Enabled = Enabled,
        LockRotation = LockRotation,
        Mirror = Mirror,
        HoldPosition = HoldPosition,
        Local = Local,
        HasTarget = HasTarget,
        CapturePosition = CapturePosition,
        CaptureRotation = CaptureRotation,
        PendingWorldPosition = PendingWorldPosition,
        PendingWorldRotation = PendingWorldRotation,
        PendingRestTurn = PendingRestTurn,
        ParentActor = ParentActor,
        ParentObjectIndex = ParentObjectIndex,
        ParentActorName = ParentActorName,
        ParentBone = ParentBone,
        SortDepth = SortDepth,
    };

    public void RequestCapture()
    {
        CapturePosition = true;
        CaptureRotation = true;
    }
}
