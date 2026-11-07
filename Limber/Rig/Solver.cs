using Limber.Core;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Limber.Rig;

// Inverse kinematics on a character's body pose, in model space.
// All writes turn (or move) a bone and carry everything below it along.
public static class Solver
{
    private const float ReachTolerance = 0.0005f;

    // Arm or leg: `upper` swings from its joint, `middle` bends, `end` is the hand or foot.
    // The limb keeps bending the way it already bends; `bendHint` is used when it is perfectly straight.
    public static bool SolveTwoBone(in ActorSkeleton skeleton, int upper, int middle, int end, Vector3 target, Vector3? bendHint)
    {
        Vector3? last = null;
        return SolveTwoBone(skeleton, upper, middle, end, target, bendHint, ref last);
    }

    // `lastBend` (model space): the way the joint bent last frame, kept by the caller. When the animated limb is
    // (nearly) straight its own bend direction is noise, so the joint keeps bending the way it did instead of
    // spinning round; the animation's bend takes over again smoothly as the limb bends.
    public static bool SolveTwoBone(in ActorSkeleton skeleton, int upper, int middle, int end, Vector3 target, Vector3? bendHint, ref Vector3? lastBend)
    {
        var a = skeleton.GetModelPosition(upper);
        var b = skeleton.GetModelPosition(middle);
        var c = skeleton.GetModelPosition(end);

        float upperLength = Vector3.Distance(a, b);
        float lowerLength = Vector3.Distance(b, c);
        if(upperLength < MathUtil.Epsilon || lowerLength < MathUtil.Epsilon)
            return false;

        var toTarget = target - a;
        float distance = toTarget.Length();
        if(distance < MathUtil.Epsilon)
            return false;

        var direction = toTarget / distance;

        // Out of reach: stretch towards it. Too close: fold as far as the limb can.
        float minReach = MathF.Abs(upperLength - lowerLength) + 0.001f;
        float maxReach = upperLength + lowerLength - 0.0005f;
        float reach = Math.Clamp(distance, minReach, maxReach);

        // Which way the middle joint should point, perpendicular to the line from the joint to the target: the
        // animated limb's own bend, blended toward last frame's (or the hint) as the animated limb straightens.
        Vector3 Perp(Vector3 v)
        {
            var p = v - direction * Vector3.Dot(v, direction);
            return p.LengthSquared() > 1e-10f ? Vector3.Normalize(p) : Vector3.Zero;
        }
        var fallback = Perp(lastBend ?? Vector3.Zero);
        if(fallback == Vector3.Zero)
            fallback = Perp(bendHint ?? Vector3.Zero);
        if(fallback == Vector3.Zero)
            fallback = MathUtil.AnyPerpendicular(direction);
        var acDir = c - a;
        acDir = acDir.LengthSquared() > 1e-10f ? Vector3.Normalize(acDir) : direction;
        var animPole = (b - a) - acDir * Vector3.Dot(b - a, acDir);
        float w = Math.Clamp(animPole.Length() / (0.15f * upperLength), 0f, 1f);
        var own = Perp(animPole);
        var bend = own == Vector3.Zero ? fallback : Vector3.Lerp(fallback, own, w);
        bend = Perp(bend);
        if(bend == Vector3.Zero)
            bend = own != Vector3.Zero ? own : fallback;
        lastBend = bend;

        // Law of cosines for the angle at the upper joint.
        float cosUpper = (upperLength * upperLength + reach * reach - lowerLength * lowerLength) / (2f * upperLength * reach);
        cosUpper = Math.Clamp(cosUpper, -1f, 1f);
        float sinUpper = MathF.Sqrt(MathF.Max(0f, 1f - cosUpper * cosUpper));

        var newMiddle = a + upperLength * (direction * cosUpper + bend * sinUpper);
        var newEnd = a + direction * reach;

        // 1. Swing the upper bone so the middle joint lands in place and the limb lies in the new bend plane.
        var oldUpperDir = b - a;
        var oldLowerDir = c - b;
        var newUpperDir = newMiddle - a;
        var newLowerDir = newEnd - newMiddle;

        var oldNormal = Vector3.Cross(oldUpperDir, oldLowerDir);
        var newNormal = Vector3.Cross(newUpperDir, newLowerDir);

        // (an animated limb that's straight has no plane of its own: the plane it would bend in, toward the chosen
        // bend, stands in for it, so the upper bone doesn't suddenly roll as the limb straightens)
        if(oldNormal.LengthSquared() <= 1e-10f)
            oldNormal = Vector3.Cross(bend, oldUpperDir);
        Quaternion swing;
        if(oldNormal.LengthSquared() > 1e-10f && newNormal.LengthSquared() > 1e-10f)
            swing = MathUtil.AlignFrames(oldUpperDir, oldNormal, newUpperDir, newNormal);
        else
            swing = MathUtil.FromTo(oldUpperDir, newUpperDir);

        var upperRotation = skeleton.GetModelFrame(upper).Rotation;
        skeleton.SetModelRotation(upper, swing * upperRotation);

        // 2. Bend the middle joint so the end lands on the target.
        var middleNow = skeleton.GetModelPosition(middle);
        var endNow = skeleton.GetModelPosition(end);
        var bendRotation = MathUtil.FromTo(endNow - middleNow, newEnd - middleNow);

        var middleRotation = skeleton.GetModelFrame(middle).Rotation;
        skeleton.SetModelRotation(middle, bendRotation * middleRotation);

        return true;
    }

    // Most a single joint in a chain may turn away from where the animation had it. Past this, spine and neck
    // meshes tear and stretch; the bend is spread over the other joints instead.
    private const float MaxJointTurn = 70f * MathF.PI / 180f;

    // Turn up to `joints.Count` joints (nearest first in the list) so `end` reaches the target.
    // The bend is spread evenly from the far end of the chain to the near end (so a spine or neck curves
    // smoothly instead of kinking at one joint), and no joint turns more than MaxJointTurn.
    // `damping` is kept for callers; the even spread replaces it.
    public static bool SolveChain(in ActorSkeleton skeleton, List<int> joints, int end, Vector3 target, int iterations, float damping)
    {
        int n = joints.Count;
        if(n == 0)
            return false;

        Span<Quaternion> applied = stackalloc Quaternion[Math.Min(n, 32)];
        n = applied.Length;
        for(int i = 0; i < n; i++)
            applied[i] = Quaternion.Identity;

        int passes = n == 1 ? 1 : Math.Clamp(iterations / 3, 2, 6);
        for(int pass = 0; pass < passes; pass++)
        {
            // Farthest joint first; each takes an equal share of what is still left to turn.
            for(int k = n - 1; k >= 0; k--)
            {
                int joint = joints[k];
                var jointPosition = skeleton.GetModelPosition(joint);
                var toEnd = skeleton.GetModelPosition(end) - jointPosition;
                var toTarget = target - jointPosition;
                if(toEnd.LengthSquared() < 1e-10f || toTarget.LengthSquared() < 1e-10f)
                    continue;

                var turn = MathUtil.FromTo(toEnd, toTarget);
                turn = Quaternion.Slerp(Quaternion.Identity, turn, 1f / (k + 1));

                // Keep this joint within its limit (measured from where the animation had it). A single joint
                // (elbow / knee swing) has nothing to spread the bend over, so it isn't limited.
                var total = Quaternion.Normalize(turn * applied[k]);
                float angle = AngleOf(total);
                if(n > 1 && angle > MaxJointTurn)
                {
                    var limited = Quaternion.Slerp(Quaternion.Identity, total, MaxJointTurn / angle);
                    turn = Quaternion.Normalize(limited * Quaternion.Inverse(applied[k]));
                    total = limited;
                }
                applied[k] = total;

                var rotation = skeleton.GetModelFrame(joint).Rotation;
                skeleton.SetModelRotation(joint, turn * rotation);
            }

            if(Vector3.DistanceSquared(skeleton.GetModelPosition(end), target) < ReachTolerance * ReachTolerance)
                break;
        }

        return true;
    }

    // Angle (radians) of a rotation, 0..pi.
    public static float AngleOf(Quaternion q)
    {
        float w = Math.Clamp(MathF.Abs(q.W), 0f, 1f);
        return 2f * MathF.Acos(w);
    }

    // A held rotation for a bone at the end of a chain, kept within `maxAngle` of the rotation the bone has after
    // the chain moved it, so the mesh between the bone and its parent never twists apart.
    public static Quaternion LimitHeldRotation(Quaternion held, Quaternion following, float maxAngle)
    {
        var difference = Quaternion.Normalize(held * Quaternion.Inverse(following));
        float angle = AngleOf(difference);
        if(angle <= maxAngle || angle < 1e-5f)
            return held;
        var limited = Quaternion.Slerp(Quaternion.Identity, difference.W < 0 ? Quaternion.Negate(difference) : difference, maxAngle / angle);
        return Quaternion.Normalize(limited * following);
    }

    // The joints a Chain goal on `bone` may turn: real joints above it, nearest first.
    public static void CollectChain(SkeletonInfo info, int bone, int length, List<int> joints)
    {
        joints.Clear();
        int joint = info.JointParentOf(bone);
        while(joint >= 0 && joints.Count < length)
        {
            // A spine bone bends the spine, never the hips (turning them twisted the hips and legs, and fought
            // pinned hips): the chain stops above them. Only the lowest spine bone, with nothing else to bend,
            // turns the hips.
            if(info.Names[joint] == "j_kosi" && joints.Count > 0)
                break;
            joints.Add(joint);
            if(info.Names[joint] == "j_kosi")
                break;
            joint = info.JointParentOf(joint);
        }
    }
}
