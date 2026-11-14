using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Limber.Core;

namespace Limber.Rig;

/// <summary>A saved paired animation (Posing &gt; Paired Animations): everything but who the two characters are.</summary>
[Serializable]
public sealed class PairedAnimation
{
    public string Name { get; set; } = "";
    public float Forward { get; set; } = 0.8f;
    public float Side { get; set; }
    public float Up { get; set; }
    public float Facing { get; set; } = 180f;
    public bool StartFrozen { get; set; }
    public float Speed { get; set; } = 1f;
    public PairedSide First { get; set; } = new();
    public PairedSide Second { get; set; } = new();
}

/// <summary>One of the two: its animation, which race's version, and its posing (FK pose, IK goals, attached bones).</summary>
[Serializable]
public sealed class PairedSide
{
    public ushort Animation { get; set; }
    /// <summary>The race whose version of the animation it plays (Animations.Races' body id), or 0 for its own.</summary>
    public ushort RaceBody { get; set; }
    public FkPoseSettings? Fk { get; set; }
    public List<SavedGoal>? Goals { get; set; }
}

/// <summary>One IK goal (held, pinned or attached bone), as saved.</summary>
[Serializable]
public sealed class SavedGoal
{
    public string Bone { get; set; } = "";
    public GoalSolve Solve { get; set; }
    public string? UpperBone { get; set; }
    public string? MiddleBone { get; set; }
    public int ChainLength { get; set; } = 2;
    public float[]? BendHint { get; set; }
    public GoalSpace Space { get; set; }
    public GoalSpace HomeSpace { get; set; }
    public bool Enabled { get; set; } = true;
    public bool LockRotation { get; set; }
    public MirrorMode Mirror { get; set; }
    public bool HoldPosition { get; set; } = true;
    public float[] Position { get; set; } = new float[3];
    public float[] Rotation { get; set; } = [0f, 0f, 0f, 1f];
    /// <summary>Attached: to which of the pair (0 = first, 1 = second), or -1 to someone else (by name).</summary>
    public int ParentWho { get; set; } = -1;
    public string ParentName { get; set; } = "";
    public string? ParentBone { get; set; }

    public static SavedGoal? From(Goal g, nint first, nint second)
    {
        if(!g.HasTarget)
            return null;
        return new SavedGoal
        {
            Bone = g.Bone,
            Solve = g.Solve,
            UpperBone = g.UpperBone,
            MiddleBone = g.MiddleBone,
            ChainLength = g.ChainLength,
            BendHint = g.BendHint is { } b ? [b.X, b.Y, b.Z] : null,
            Space = g.Space,
            HomeSpace = g.HomeSpace,
            Enabled = g.Enabled,
            LockRotation = g.LockRotation,
            Mirror = g.Mirror,
            HoldPosition = g.HoldPosition,
            Position = [g.Local.Position.X, g.Local.Position.Y, g.Local.Position.Z],
            Rotation = [g.Local.Rotation.X, g.Local.Rotation.Y, g.Local.Rotation.Z, g.Local.Rotation.W],
            ParentWho = g.Space != GoalSpace.Attached ? -1 : g.ParentActor == first ? 0 : g.ParentActor == second ? 1 : -1,
            ParentName = g.ParentActorName ?? "",
            ParentBone = g.ParentBone,
        };
    }

    /// <summary>The goal again. <paramref name="parent"/> finds who an attached bone holds on to (null: let go).</summary>
    public Goal ToGoal(Func<SavedGoal, (nint Address, ushort Index, string Name)?> parent)
    {
        var g = new Goal
        {
            Bone = Bone,
            Solve = Solve,
            UpperBone = UpperBone,
            MiddleBone = MiddleBone,
            ChainLength = ChainLength,
            BendHint = BendHint is { Length: 3 } b ? new Vector3(b[0], b[1], b[2]) : null,
            Space = Space,
            HomeSpace = HomeSpace,
            Enabled = Enabled,
            LockRotation = LockRotation,
            Mirror = Mirror,
            HoldPosition = HoldPosition,
        };
        g.Local = new Frame(
            Position is { Length: 3 } p ? new Vector3(p[0], p[1], p[2]) : Vector3.Zero,
            Rotation is { Length: 4 } r ? Quaternion.Normalize(new Quaternion(r[0], r[1], r[2], r[3])) : Quaternion.Identity);
        g.HasTarget = true;
        g.CapturePosition = false;
        g.CaptureRotation = false;
        if(Space == GoalSpace.Attached)
        {
            if(parent(this) is { } who && who.Address != 0 && ParentBone != null)
            {
                g.ParentActor = who.Address;
                g.ParentObjectIndex = who.Index;
                g.ParentActorName = who.Name ?? ParentName;
                g.ParentBone = ParentBone;
            }
            else
            {
                // (whoever it held on to isn't here: it stays where the bone is now instead)
                g.Space = HomeSpace == GoalSpace.Attached ? GoalSpace.FollowBody : HomeSpace;
                g.HasTarget = false;
                g.RequestCapture();
            }
        }
        return g;
    }
}
