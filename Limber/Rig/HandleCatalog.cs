using System.Collections.Generic;
using System.Numerics;

namespace Limber.Rig;

// The on-body handles and how each one poses the body.
public sealed record HandleDefinition(
    string Bone,
    string Label,
    GoalSolve Solve,
    GoalSpace Space,
    bool LockRotation,
    int ChainLength = 2,
    string? UpperBone = null,
    string? MiddleBone = null,
    Vector3? BendHint = null,
    bool IsPinTarget = false);

public static class HandleCatalog
{
    // Model space: characters face +Z, +Y is up.
    private static readonly Vector3 ElbowBend = Vector3.Normalize(new Vector3(0f, -0.25f, -1f));
    private static readonly Vector3 KneeBend = Vector3.UnitZ;

    public static readonly HandleDefinition[] Handles =
    [
        // Head: neck, upper and middle spine share the bend, so it can move a long way and stays smooth.
        new("j_kao", "Head", GoalSolve.Chain, GoalSpace.FollowBody, LockRotation: true, ChainLength: 3),
        new("j_sebo_c", "Chest", GoalSolve.Chain, GoalSpace.FollowBody, LockRotation: false, ChainLength: 2),
        new("j_kosi", "Hips", GoalSolve.Rigid, GoalSpace.Pinned, LockRotation: false),

        new("j_te_l", "Left Hand", GoalSolve.TwoBone, GoalSpace.FollowBody, LockRotation: false, UpperBone: "j_ude_a_l", MiddleBone: "j_ude_b_l", BendHint: ElbowBend, IsPinTarget: true),
        new("j_te_r", "Right Hand", GoalSolve.TwoBone, GoalSpace.FollowBody, LockRotation: false, UpperBone: "j_ude_a_r", MiddleBone: "j_ude_b_r", BendHint: ElbowBend, IsPinTarget: true),
        new("j_ude_b_l", "Left Elbow", GoalSolve.Chain, GoalSpace.FollowBody, LockRotation: false, ChainLength: 1),
        new("j_ude_b_r", "Right Elbow", GoalSolve.Chain, GoalSpace.FollowBody, LockRotation: false, ChainLength: 1),

        new("j_asi_d_l", "Left Foot", GoalSolve.TwoBone, GoalSpace.Pinned, LockRotation: true, UpperBone: "j_asi_a_l", MiddleBone: "j_asi_c_l", BendHint: KneeBend, IsPinTarget: true),
        new("j_asi_d_r", "Right Foot", GoalSolve.TwoBone, GoalSpace.Pinned, LockRotation: true, UpperBone: "j_asi_a_r", MiddleBone: "j_asi_c_r", BendHint: KneeBend, IsPinTarget: true),
        new("j_asi_b_l", "Left Knee", GoalSolve.Chain, GoalSpace.Pinned, LockRotation: false, ChainLength: 1),
        new("j_asi_b_r", "Right Knee", GoalSolve.Chain, GoalSpace.Pinned, LockRotation: false, ChainLength: 1),
    ];

    private static readonly Dictionary<string, HandleDefinition> ByBone = [];

    private static readonly Dictionary<string, string> FriendlyNames = new()
    {
        ["j_kosi"] = "Hips",
        ["j_sebo_a"] = "Lower Spine",
        ["j_sebo_b"] = "Middle Spine",
        ["j_sebo_c"] = "Chest",
        ["j_kubi"] = "Neck",
        ["j_kao"] = "Head",
        ["j_sako_l"] = "Left Collarbone",
        ["j_sako_r"] = "Right Collarbone",
        ["j_ude_a_l"] = "Left Upper Arm",
        ["j_ude_a_r"] = "Right Upper Arm",
        ["j_ude_b_l"] = "Left Elbow",
        ["j_ude_b_r"] = "Right Elbow",
        ["j_te_l"] = "Left Hand",
        ["j_te_r"] = "Right Hand",
        ["j_asi_a_l"] = "Left Thigh",
        ["j_asi_a_r"] = "Right Thigh",
        ["j_asi_b_l"] = "Left Knee",
        ["j_asi_b_r"] = "Right Knee",
        ["j_asi_c_l"] = "Left Calf",
        ["j_asi_c_r"] = "Right Calf",
        ["j_asi_d_l"] = "Left Foot",
        ["j_asi_d_r"] = "Right Foot",
        ["j_asi_e_l"] = "Left Toes",
        ["j_asi_e_r"] = "Right Toes",
        ["j_mune_l"] = "Left Breast",
        ["j_mune_r"] = "Right Breast",
        ["n_sippo_a"] = "Tail",
    };

    static HandleCatalog()
    {
        foreach(var handle in Handles)
            ByBone[handle.Bone] = handle;
    }

    public static HandleDefinition? Find(string bone) => ByBone.TryGetValue(bone, out var handle) ? handle : null;

    public static string Label(string bone)
    {
        if(FriendlyNames.TryGetValue(bone, out var name))
            return name;

        return BodyLabel(bone) ?? FaceLabel(bone) ?? bone;
    }

    /// <summary>A face bone (the face skeleton's j_f_ bones: brows, eyelids, eyes, cheeks, lips, jaw, tongue...).</summary>
    public static bool IsFaceBone(string bone) => bone.StartsWith("j_f_", System.StringComparison.Ordinal);

    // Face bones by name start (longest first wins), for the face dots and the bone lists.
    private static readonly (string Start, string Name)[] FaceNames =
    [
        ("j_f_mmayu", "Inner eyebrow"),
        ("j_f_mayu", "Eyebrow"),
        ("j_f_miken", "Between the brows"),
        ("j_f_umab", "Upper eyelid"),
        ("j_f_dmab", "Lower eyelid"),
        ("j_f_mab", "Eyelid"),
        ("j_f_eyepuru", "Eye shine"),
        ("j_f_eye", "Eye"),
        ("j_f_hana", "Nose"),
        ("j_f_hoho", "Cheek"),
        ("j_f_shoho", "Lower cheek"),
        ("j_f_uslip", "Upper lip (side)"),
        ("j_f_dslip", "Lower lip (side)"),
        ("j_f_ulip", "Upper lip"),
        ("j_f_dlip", "Lower lip"),
        ("j_f_lip", "Mouth corner"),
        ("j_f_hagukiu", "Upper teeth"),
        ("j_f_hagukid", "Lower teeth"),
        ("j_f_ago", "Jaw"),
        ("j_f_bero", "Tongue"),
        ("j_f_face", "Face"),
    ];

    private static string? FaceLabel(string bone)
    {
        if(!IsFaceBone(bone))
            return null;
        string side = bone.EndsWith("_l", System.StringComparison.Ordinal) ? " (left)" : bone.EndsWith("_r", System.StringComparison.Ordinal) ? " (right)" : "";
        string core = side.Length > 0 ? bone[..^2] : bone;
        foreach(var (start, name) in FaceNames)
        {
            if(!core.StartsWith(start, System.StringComparison.Ordinal))
                continue;
            string rest = core[start.Length..].Trim('_');
            return "Face: " + name + (rest.Length > 0 ? " " + rest : "") + side;
        }
        return "Face: " + bone[4..];
    }

    // Plain names for the body mod bones (IVCS / Limber's rig), so they can be found by searching in English.
    private static string? BodyLabel(string bone)
    {
        string Side(string b) => b.EndsWith("_l") ? " (left)" : b.EndsWith("_r") ? " (right)" : "";
        if(bone.StartsWith("iv_ochinko_") && bone.Length == "iv_ochinko_a".Length)
        {
            int part = bone[^1] - 'a' + 1;
            return $"Penis {part}" + (part == 1 ? " (base)" : bone[^1] == 'f' ? " (tip)" : "");
        }
        if(bone.StartsWith("iv_funyachin"))
            return "Penis (soft physics)";
        if(bone.StartsWith("iv_kougan_"))
            return "Testicle" + Side(bone);
        if(bone.StartsWith("iv_kintama_phys_"))
            return "Testicle physics" + Side(bone);
        if(bone.StartsWith("iv_c_mune_") || bone.StartsWith("j_mune_"))
            return "Breast" + Side(bone);
        if(bone.StartsWith("iv_shiri_"))
            return "Buttock" + Side(bone);
        if(bone.StartsWith("lmb_lat_mune_") || bone.StartsWith("lmb_mune_"))
            return "Breast rig" + Side(bone[..(bone.LastIndexOf('_'))]) + " " + bone[(bone.LastIndexOf('_') + 1)..];
        if(bone.StartsWith("lmb_lat_kougan_") || bone.StartsWith("lmb_kougan_"))
            return "Testicle rig" + Side(bone[..(bone.LastIndexOf('_'))]) + " " + bone[(bone.LastIndexOf('_') + 1)..];
        if(bone.StartsWith("lmb_lat_shiri_"))
            return "Butt rig" + Side(bone[..(bone.LastIndexOf('_'))]) + " " + bone[(bone.LastIndexOf('_') + 1)..];
        return null;
    }

    // Bones that make sense to grab outside the main handles.
    public static bool IsGrabbable(string bone, bool adultBones = false)
    {
        if(bone.Length == 0 || bone.StartsWith("n_") || bone.StartsWith("j_f_"))
            return false;

        if(!IsPickable(bone, adultBones))
            return false;
        if(IsBodyModBone(bone))
            return true;

        return bone.StartsWith("j_") || System.Array.Exists(Limber.BodyRig.RigParts.All, p => p.IsMine(bone));
    }

    // Bones the pickers (Pose, Attach, Props, Renderer) list: every bone of every part of the skeleton, the face,
    // hair, tail and any a mod adds included. Body mod bones only on adult bodies.
    public static bool IsPickable(string bone, bool adultBones)
        => bone.Length > 0 && (adultBones || !IsBodyModBone(bone)) && (ShowRigBones || !IsRigBone(bone));

    /// <summary>Show Limber's physics rig bones (the breast / butt / testicle lattices, the body shape grids) as dots and
    /// in the bone lists (Settings; off by default: they aren't for posing).</summary>
    public static bool ShowRigBones;

    /// <summary>One of the bones Limber's physics rig adds (lattices, grids, nipples): not for posing.</summary>
    public static bool IsRigBone(string bone) => bone.StartsWith("lmb_", System.StringComparison.Ordinal);

    // Body mod and Limber body-rig bones (IVCS iv_, ya_, Limber's lmb_ rig): adult bodies only.
    public static bool IsBodyModBone(string bone)
        => bone.StartsWith("iv_", System.StringComparison.Ordinal) || bone.StartsWith("ya_", System.StringComparison.Ordinal) || bone.StartsWith("lmb_", System.StringComparison.Ordinal);

    // A goal for a bone, set up the way its handle (or a sensible default) wants it.
    public static Goal CreateGoal(string bone)
    {
        var handle = Find(bone);
        if(handle != null)
        {
            return new Goal
            {
                Bone = bone,
                Solve = handle.Solve,
                Space = handle.Space,
                HomeSpace = handle.Space,
                LockRotation = handle.LockRotation,
                ChainLength = handle.ChainLength,
                UpperBone = handle.UpperBone,
                MiddleBone = handle.MiddleBone,
                BendHint = handle.BendHint,
            };
        }

        return new Goal
        {
            Bone = bone,
            Solve = GoalSolve.Chain,
            Space = GoalSpace.FollowBody,
            HomeSpace = GoalSpace.FollowBody,
            LockRotation = false,
            ChainLength = bone == "j_kubi" ? 3 : 2, // neck: bend down the spine too
        };
    }
}
