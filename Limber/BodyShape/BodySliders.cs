using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Limber.BodyShape;

/// <summary>Which of a bone's own axes an op works along.</summary>
public enum OpAxes
{
    All,        // every axis (uniform)
    Across,     // the two across the bone (thicker, not longer): needs <see cref="BoneOp.Next"/>
    Along,      // the one along it (longer): needs <see cref="BoneOp.Next"/>
    SideToSide, // the axis nearest the body's left-right
    UpDown,     // the axis nearest the body's up-down
    FrontBack,  // the axis nearest the body's front-back
}

/// <summary>Which of a bone's children keep their own shape when it's scaled (the rest are carried with it).</summary>
public enum OpKeep
{
    None,      // everything below goes with it (a breast and its rig)
    Children,  // every child keeps its own (a thigh thicker, the knee and below as they are)
    Named,     // the children named in <see cref="BoneOp.KeepNames"/> keep theirs
}

/// <summary>
/// One thing a slider does to one bone (both sides when the name ends in "_{side}"): scale it (in its own axes, about
/// its joint), turn it, or move it, per unit of the slider. Done live to the bone itself, and the same done to the
/// vertices it carries when building, so the preview and the build match.
/// </summary>
public sealed record BoneOp
{
    public required string Bone { get; init; }       // "j_mune_{side}" for both sides
    public string? Next { get; init; }                // the joint it runs to (for Across / Along)
    public OpAxes Axes { get; init; } = OpAxes.All;
    public float Scale { get; init; }                 // + this per unit (1 + Scale * value)
    public Vector3 Move { get; init; }                // metres per unit, body axes (x = outward for a side bone, y up, z front)
    public Vector3 Turn { get; init; }                // degrees per unit about body axes (x = tip up, y = turn out, z = roll out)
    public OpKeep Keep { get; init; } = OpKeep.None;
    public string[] KeepNames { get; init; } = [];    // name starts (Keep = Named)
    /// <summary>Longer: the bone stretched along the line to <see cref="Next"/> and that joint (with all below it)
    /// moved out by as much; its other children stay where they are. Live only.</summary>
    public bool Stretch { get; init; }
    /// <summary>Move's x is outward from the body's middle by where the bone itself is (a grid point either side).</summary>
    public bool Mirror { get; init; }
    /// <summary>Metres per unit straight out from a line (fuller): the line from <see cref="AxisFrom"/> to
    /// <see cref="AxisTo"/> (a limb), or straight up through <see cref="AxisFrom"/>.</summary>
    public float Radial { get; init; }
    public string? AxisFrom { get; init; }
    public string? AxisTo { get; init; }
}

/// <summary>One slider: its group, name, range (in its own units, 0 = the body as it is) and what it does.</summary>
public sealed record BodySlider
{
    public required string Key { get; init; }
    public required string Group { get; init; }
    public required string Name { get; init; }
    public string Tip { get; init; } = "";
    public float Min { get; init; } = -10f;  // -1000%
    public float Max { get; init; } = 10f;   // +1000%
    public BoneOp[] Ops { get; init; } = [];
    /// <summary>Done live only (moving joints apart would leave the built mesh off its bones): leg length.</summary>
    public bool LiveOnly { get; init; }
    /// <summary>Sculpted into the vertices when building (no live preview): the detail sliders.</summary>
    public bool BuildOnly { get; init; }
    public Morph[] Morphs { get; init; } = [];
}

/// <summary>
/// A detail slider's sculpt: the skin carried by one bone pushed straight out from its line (fuller), most where the
/// lobe points (body axes: x outward, y up, z front; zero = all round), over a stretch of it, and fading out with
/// how much each vertex is carried by that bone, so it blends into the body the way the body's own weights do.
/// </summary>
public sealed record Morph
{
    public required string Bone { get; init; }   // "{side}" for both sides
    public string? Next { get; init; }           // a limb: its line runs to this joint; else straight up through the bone
    public Vector3 Lobe { get; init; }
    public float From { get; init; }             // a limb: 0..1 along it; the trunk (Heights): metres from the bone
    public float To { get; init; } = 1f;
    public bool Heights { get; init; }
    public float Amount { get; init; }           // metres per unit
}

/// <summary>Every slider the Body Shape tab has, in its groups.</summary>
public static class BodySliders
{
    public static readonly string[] Groups = ["Breasts", "Butt", "Hips & waist", "Legs", "Arms & shoulders", "Torso",
        "Detail: thighs", "Detail: calves", "Detail: hips", "Detail: belly & waist", "Detail: chest & back", "Detail: arms", "Detail: neck"];

    /// <summary>The detail groups are sculpted into the models when building.</summary>
    public static bool IsDetail(string group) => group.StartsWith("Detail", StringComparison.Ordinal);

    // ---- Detail sliders: sculpted into the vertices when building (like BodySlide's sliders, but Limber's own,
    // worked out on the body's bones: no extra bones, so nothing to keep in line at the seams between models).
    private const float Step = 0.01f; // metres per 100%

    private static BodySlider Detail(string key, string group, string name, string tip, params Morph[] morphs) => new()
    {
        Key = key, Group = group, Name = name, Tip = tip + " Built into the models (Build; see Auto-build).", Morphs = morphs, BuildOnly = true,
    };

    // A limb: fuller where the lobe points, over a stretch of its length (0 = its joint, 1 = the next).
    private static Morph Limb(string bone, string next, Vector3 lobe, float from = 0f, float to = 1f, float amount = 1f)
        => new() { Bone = bone, Next = next, Lobe = lobe, From = from, To = to, Amount = amount * Step };

    // The trunk: fuller where the lobe points, between heights (metres from the bone, + up).
    private static Morph Trunk(string bone, Vector3 lobe, float from, float to, float amount = 1f)
        => new() { Bone = bone, Lobe = lobe, From = from, To = to, Amount = amount * Step, Heights = true };

    private const string Thigh = "j_asi_a_{side}", Knee = "j_asi_b_{side}", Shin = "j_asi_c_{side}", Ankle = "j_asi_d_{side}", Toes = "j_asi_e_{side}",
        UpperArm = "j_ude_a_{side}", Forearm = "j_ude_b_{side}", Hand = "j_te_{side}";
    private static readonly Vector3 Any = Vector3.Zero, Out = new(1f, 0f, 0f), In = new(-1f, 0f, 0f), Forward = new(0f, 0f, 1f),
        Back = new(0f, 0f, -1f), Up = new(0f, 1f, 0f), Down = new(0f, -1f, 0f);

    private static readonly BodySlider[] Details =
    [
        Detail("d_thigh_inner", "Detail: thighs", "Inner thigh", "Fuller (+) or slimmer (-) on the inside of the thigh.", Limb(Thigh, Knee, In)),
        Detail("d_thigh_outer", "Detail: thighs", "Outer thigh", "Fuller or slimmer on the outside.", Limb(Thigh, Knee, Out)),
        Detail("d_thigh_front", "Detail: thighs", "Front thigh", "Fuller or flatter at the front.", Limb(Thigh, Knee, Forward)),
        Detail("d_thigh_back", "Detail: thighs", "Back thigh", "Fuller or flatter at the back (under the butt).", Limb(Thigh, Knee, Back)),
        Detail("d_thigh_upper", "Detail: thighs", "Upper thigh", "The top of the thigh fuller or slimmer.", Limb(Thigh, Knee, Any, 0f, 0.4f)),
        Detail("d_thigh_mid", "Detail: thighs", "Mid thigh", "The middle of the thigh fuller or slimmer.", Limb(Thigh, Knee, Any, 0.3f, 0.7f)),
        Detail("d_thigh_lower", "Detail: thighs", "Above the knee", "Just above the knee fuller or slimmer.", Limb(Thigh, Knee, Any, 0.6f, 1f)),
        Detail("d_thigh_gap", "Detail: thighs", "Inner upper thigh", "The top of the inner thigh fuller (-: a thigh gap).", Limb(Thigh, Knee, In, 0f, 0.45f)),
        Detail("d_saddlebags", "Detail: thighs", "Saddlebags", "The top of the outer thigh fuller.", Limb(Thigh, Knee, Out, 0f, 0.45f)),

        Detail("d_calf_muscle", "Detail: calves", "Calf muscle", "The back of the calf fuller or flatter.", Limb(Shin, Ankle, Back, 0f, 0.6f)),
        Detail("d_shins", "Detail: calves", "Shins", "The front of the lower leg fuller or flatter.", Limb(Shin, Ankle, Forward)),
        Detail("d_calf_upper", "Detail: calves", "Upper calf", "Below the knee fuller or slimmer.", Limb(Shin, Ankle, Any, 0f, 0.45f)),
        Detail("d_ankles", "Detail: calves", "Ankles", "Above the ankle thicker or slimmer.", Limb(Shin, Ankle, Any, 0.6f, 1f)),
        Detail("d_calf_inner", "Detail: calves", "Inner calf", "The inside of the calf fuller or slimmer.", Limb(Shin, Ankle, In)),
        Detail("d_calf_outer", "Detail: calves", "Outer calf", "The outside of the calf fuller or slimmer.", Limb(Shin, Ankle, Out)),
        Detail("d_knees", "Detail: calves", "Knees", "The knees fuller or slimmer.", Limb(Knee, Shin, Any)),

        Detail("d_hips_upper", "Detail: hips", "Upper hips", "The top of the hips wider (a curvier waist-to-hip line).", Trunk("j_kosi", Out, -0.02f, 0.08f)),
        Detail("d_hips_lower", "Detail: hips", "Lower hips", "The lower hips wider (-: hip dips).", Trunk("j_kosi", Out, -0.12f, -0.02f)),
        Detail("d_pelvis_front", "Detail: hips", "Pelvis front", "The front of the pelvis fuller or flatter.", Trunk("j_kosi", Forward, -0.1f, 0.06f)),
        Detail("d_lower_back_hips", "Detail: hips", "Lower back", "The small of the back fuller (+) or more arched (-).", Trunk("j_kosi", Back, 0f, 0.1f)),

        Detail("d_belly", "Detail: belly & waist", "Belly", "The belly rounder or flatter.", Trunk("j_sebo_a", Forward, -0.08f, 0.1f)),
        Detail("d_belly_upper", "Detail: belly & waist", "Upper belly", "Just under the ribs rounder or flatter.", Trunk("j_sebo_a", Forward, 0.02f, 0.12f)),
        Detail("d_belly_lower", "Detail: belly & waist", "Lower belly", "Below the navel rounder or flatter.", Trunk("j_kosi", Forward, 0.02f, 0.12f)),
        Detail("d_waist_sides", "Detail: belly & waist", "Waist sides", "The sides of the waist out (+) or in (-: more pinched).", Trunk("j_sebo_a", Out, -0.06f, 0.1f)),
        Detail("d_love_handles", "Detail: belly & waist", "Love handles", "Fuller at the back of the waist's sides.", Trunk("j_sebo_a", Out + Back, -0.08f, 0.04f)),
        Detail("d_waist_back", "Detail: belly & waist", "Lower back", "The back of the waist fuller (+) or more arched (-).", Trunk("j_sebo_a", Back, -0.06f, 0.1f)),

        Detail("d_ribs", "Detail: chest & back", "Ribcage sides", "The sides of the ribcage out or in.", Trunk("j_sebo_b", Out, -0.08f, 0.12f)),
        Detail("d_underbust", "Detail: chest & back", "Under the bust", "Just below the breasts fuller or flatter.", Trunk("j_sebo_b", Forward, -0.1f, 0f)),
        Detail("d_back", "Detail: chest & back", "Back", "The back fuller (+) or flatter (-).", Trunk("j_sebo_b", Back, -0.08f, 0.14f)),
        Detail("d_lats", "Detail: chest & back", "Lats", "The back's sides wider (a V shape).", Trunk("j_sebo_b", Out + Back, -0.04f, 0.12f)),
        Detail("d_shoulder_blades", "Detail: chest & back", "Upper back", "Between the shoulder blades fuller or flatter.", Trunk("j_sebo_c", Back, -0.06f, 0.1f)),

        Detail("d_biceps", "Detail: arms", "Biceps", "The front of the upper arm fuller or slimmer.", Limb(UpperArm, Forearm, Forward)),
        Detail("d_triceps", "Detail: arms", "Triceps", "The back of the upper arm fuller or slimmer.", Limb(UpperArm, Forearm, Back)),
        Detail("d_upper_arm_top", "Detail: arms", "Upper arm (top)", "By the shoulder fuller or slimmer.", Limb(UpperArm, Forearm, Any, 0f, 0.45f)),
        Detail("d_upper_arm_low", "Detail: arms", "Upper arm (low)", "Above the elbow fuller or slimmer.", Limb(UpperArm, Forearm, Any, 0.55f, 1f)),
        Detail("d_forearms", "Detail: arms", "Forearms", "Below the elbow fuller or slimmer.", Limb(Forearm, Hand, Any, 0f, 0.6f)),
        Detail("d_wrists", "Detail: arms", "Wrists", "Thicker or slimmer wrists.", Limb(Forearm, Hand, Any, 0.65f, 1f)),

        Detail("d_neck_front", "Detail: neck", "Neck front", "The front of the neck fuller or slimmer.", Trunk("j_kubi", Forward, -0.03f, 0.08f)),
        Detail("d_nape", "Detail: neck", "Nape", "The back of the neck fuller or slimmer.", Trunk("j_kubi", Back, -0.03f, 0.08f)),
        Detail("d_neck_sides", "Detail: neck", "Neck sides", "The sides of the neck out or in.", Trunk("j_kubi", Out, -0.03f, 0.08f)),
    ];

    private const string Mune = "j_mune_{side}", Shiri = "iv_shiri_{side}";
    // The pelvis's children that keep their own shape when the hips widen: the spine, the legs, the genitals.
    private static readonly string[] PelvisKeep = ["j_sebo_a", "j_asi_a_", "iv_ochinko", "iv_kougan", "iv_kintama", "iv_funyachin", "iv_ochin"];

    public static readonly BodySlider[] All =
    [
        // Breasts (the game's breast bone; Limber's breast rig and the nipples go with it).
        new() { Key = "breast_size", Group = "Breasts", Name = "Size", Tip = "Bigger or smaller breasts.",
            Ops = [new() { Bone = Mune, Scale = 0.5f }] },
        new() { Key = "breast_lift", Group = "Breasts", Name = "Position (up / down)", Tip = "The breasts moved up (+) or down (-) on the chest, keeping their shape (+100% = 2.5 cm).",
            Ops = [new() { Bone = Mune, Move = new Vector3(0f, 0.025f, 0f) }] },
        new() { Key = "breast_width", Group = "Breasts", Name = "Width", Tip = "Wider or narrower across.",
            Ops = [new() { Bone = Mune, Axes = OpAxes.SideToSide, Scale = 0.35f }] },
        new() { Key = "breast_projection", Group = "Breasts", Name = "Projection", Tip = "How far they stick out.",
            Ops = [new() { Bone = Mune, Axes = OpAxes.FrontBack, Scale = 0.45f }] },
        new() { Key = "breast_height", Group = "Breasts", Name = "Height", Tip = "Taller or shorter (top to bottom).",
            Ops = [new() { Bone = Mune, Axes = OpAxes.UpDown, Scale = 0.35f }] },
        new() { Key = "breast_spacing", Group = "Breasts", Name = "Spacing", Tip = "Further apart (+) or closer together (-).",
            Ops = [new() { Bone = Mune, Move = new Vector3(0.018f, 0f, 0f) }] },
        new() { Key = "breast_perk", Group = "Breasts", Name = "Perk", Tip = "Tipped up (+) or hanging (-).",
            Ops = [new() { Bone = Mune, Turn = new Vector3(14f, 0f, 0f) }] },
        new() { Key = "breast_angle", Group = "Breasts", Name = "Point out", Tip = "Pointing out to the sides (+) or forward / in (-).",
            Ops = [new() { Bone = Mune, Turn = new Vector3(0f, 12f, 0f) }] },

        // Butt (the IVCS / YAS butt bones; Limber's butt rig goes with them).
        new() { Key = "butt_size", Group = "Butt", Name = "Size", Tip = "Bigger or smaller (bodies with IVCS / YAS butt bones).",
            Ops = [new() { Bone = Shiri, Scale = 0.5f }] },
        new() { Key = "butt_lift", Group = "Butt", Name = "Position (up / down)", Tip = "The butt moved up (+) or down (-), keeping its shape (+100% = 2 cm).",
            Ops = [new() { Bone = Shiri, Move = new Vector3(0f, 0.02f, 0f) }] },
        new() { Key = "butt_width", Group = "Butt", Name = "Width", Tip = "Wider or narrower.",
            Ops = [new() { Bone = Shiri, Axes = OpAxes.SideToSide, Scale = 0.35f }] },
        new() { Key = "butt_projection", Group = "Butt", Name = "Projection", Tip = "How far it sticks out back.",
            Ops = [new() { Bone = Shiri, Axes = OpAxes.FrontBack, Scale = 0.45f }] },
        new() { Key = "butt_height", Group = "Butt", Name = "Height", Tip = "Taller or shorter.",
            Ops = [new() { Bone = Shiri, Axes = OpAxes.UpDown, Scale = 0.35f }] },
        new() { Key = "butt_spacing", Group = "Butt", Name = "Spacing", Tip = "Cheeks further apart (+) or closer (-).",
            Ops = [new() { Bone = Shiri, Move = new Vector3(0.012f, 0f, 0f) }] },

        // Hips and waist.
        new() { Key = "hips", Group = "Hips & waist", Name = "Hips", Tip = "The pelvis wider or narrower (the butt with it).",
            Ops = [new() { Bone = "j_kosi", Axes = OpAxes.SideToSide, Scale = 0.35f, Keep = OpKeep.Named, KeepNames = PelvisKeep }] },
        new() { Key = "hip_depth", Group = "Hips & waist", Name = "Hip depth", Tip = "The pelvis deeper front to back.",
            Ops = [new() { Bone = "j_kosi", Axes = OpAxes.FrontBack, Scale = 0.3f, Keep = OpKeep.Named, KeepNames = PelvisKeep }] },
        new() { Key = "waist", Group = "Hips & waist", Name = "Waist", Tip = "The waist thicker or slimmer.",
            Ops = [new() { Bone = "j_sebo_a", Next = "j_sebo_b", Axes = OpAxes.Across, Scale = 0.3f, Keep = OpKeep.Children }] },
        new() { Key = "hip_position", Group = "Hips & waist", Name = "Hip position", LiveOnly = true,
            Tip = "The hips (and the body above them) higher or lower, the legs longer or shorter to match, so the feet stay on the floor; the butt stays where it is (+100% = 5 cm up). Live only." },
        new() { Key = "hip_length", Group = "Hips & waist", Name = "Hip length", LiveOnly = true,
            Tip = "The pelvis taller: the body above it raised, the legs as they are. Live only.",
            Ops = [new() { Bone = "j_kosi", Next = "j_sebo_a", Scale = 0.5f, Stretch = true, Keep = OpKeep.Named, KeepNames = PelvisKeep }] },
        new() { Key = "waist_position", Group = "Hips & waist", Name = "Waist position", LiveOnly = true,
            Tip = "The waist moved up (+) or down (-) between the hips and the chest; the hips, chest and everything else stay where they are (+100% = 3 cm). Live only.",
            Ops = [new() { Bone = "j_sebo_a", Move = new Vector3(0f, 0.03f, 0f), Keep = OpKeep.Children }] },
        new() { Key = "waist_length", Group = "Hips & waist", Name = "Waist length", LiveOnly = true,
            Tip = "A longer (or shorter) waist. Live only.",
            Ops = [new() { Bone = "j_sebo_a", Next = "j_sebo_b", Scale = 0.5f, Stretch = true, Keep = OpKeep.Children }] },
        new() { Key = "belly", Group = "Hips & waist", Name = "Belly", Tip = "The middle deeper front to back.",
            Ops = [new() { Bone = "j_sebo_a", Axes = OpAxes.FrontBack, Scale = 0.35f, Keep = OpKeep.Children }] },

        // Legs.
        new() { Key = "leg_spacing", Group = "Legs", Name = "Leg spacing", LiveOnly = true,
            Tip = "The whole legs set further apart (+) or closer together (-), thighs, knees, calves and feet together (+100% = 2 cm each side). Live only.",
            // (The top of the thigh moved, and the knee with everything below it by as much: it adds up with Thigh spacing.)
            Ops = [new() { Bone = "j_asi_a_{side}", Move = new Vector3(0.02f, 0f, 0f), Keep = OpKeep.Named, KeepNames = ["j_asi_b_"] },
                   new() { Bone = "j_asi_b_{side}", Move = new Vector3(0.02f, 0f, 0f) }] },
        new() { Key = "thigh_spacing", Group = "Legs", Name = "Thigh spacing",
            Tip = "The tops of the thighs further apart (+, a wider gap between them) or closer together (-); the knees and lower legs stay where they are (+100% = 1.5 cm each side).",
            Ops = [new() { Bone = "j_asi_a_{side}", Move = new Vector3(0.015f, 0f, 0f), Keep = OpKeep.Named, KeepNames = ["j_asi_b_"] }] },
        new() { Key = "thighs", Group = "Legs", Name = "Thighs", Tip = "Thicker or slimmer thighs (not longer).",
            Ops = [new() { Bone = "j_asi_a_{side}", Next = "j_asi_b_{side}", Axes = OpAxes.Across, Scale = 0.45f, Keep = OpKeep.Children }] },
        new() { Key = "knees", Group = "Legs", Name = "Knees", Tip = "Thicker or slimmer at the knee.",
            Ops = [new() { Bone = "j_asi_b_{side}", Next = "j_asi_c_{side}", Axes = OpAxes.Across, Scale = 0.35f, Keep = OpKeep.Children }] },
        new() { Key = "calves", Group = "Legs", Name = "Calves", Tip = "Thicker or slimmer calves (not longer).",
            Ops = [new() { Bone = "j_asi_c_{side}", Next = "j_asi_d_{side}", Axes = OpAxes.Across, Scale = 0.4f, Keep = OpKeep.Children }] },
        new() { Key = "ankles", Group = "Legs", Name = "Ankles", BuildOnly = true,
            Tip = "Thicker or slimmer ankles; the feet stay as they are. Built into the models.",
            Morphs = [Limb(Shin, Ankle, Any, 0.7f, 1f, 1.5f), Limb(Ankle, Toes, Any, 0f, 0.3f, 1f)] },
        new() { Key = "feet", Group = "Legs", Name = "Feet",
            Tip = "Bigger or smaller feet, toes and all (longer and wider, the same height, so the soles stay on the floor).",
            Ops = [new() { Bone = "j_asi_d_{side}", Next = "j_asi_e_{side}", Axes = OpAxes.Along, Scale = 0.5f },
                   new() { Bone = "j_asi_d_{side}", Axes = OpAxes.SideToSide, Scale = 0.5f }] },
        new() { Key = "foot_width", Group = "Legs", Name = "Foot width", Tip = "Wider or narrower feet, toes and all.",
            Ops = [new() { Bone = "j_asi_d_{side}", Axes = OpAxes.SideToSide, Scale = 0.4f }] },
        new() { Key = "leg_length", Group = "Legs", Name = "Leg length", LiveOnly = true,
            Tip = "Longer or shorter thighs and shins; the feet stay as they are. Live only (not built into the models)." },

        // Arms and shoulders.
        new() { Key = "arm_length", Group = "Arms & shoulders", Name = "Arm length", LiveOnly = true,
            Tip = "Longer or shorter arms (upper arm and forearm); the hands stay as they are. Live only.",
            Ops = [new() { Bone = "j_ude_a_{side}", Next = "j_ude_b_{side}", Scale = 0.5f, Stretch = true, Keep = OpKeep.Children },
                   new() { Bone = "j_ude_b_{side}", Next = "j_te_{side}", Scale = 0.5f, Stretch = true, Keep = OpKeep.Children }] },
        new() { Key = "upper_arms", Group = "Arms & shoulders", Name = "Upper arms", Tip = "Thicker or slimmer upper arms.",
            Ops = [new() { Bone = "j_ude_a_{side}", Next = "j_ude_b_{side}", Axes = OpAxes.Across, Scale = 0.4f, Keep = OpKeep.Children }] },
        new() { Key = "forearms", Group = "Arms & shoulders", Name = "Forearms", Tip = "Thicker or slimmer forearms.",
            Ops = [new() { Bone = "j_ude_b_{side}", Next = "j_te_{side}", Axes = OpAxes.Across, Scale = 0.35f, Keep = OpKeep.Children }] },
        new() { Key = "shoulders", Group = "Arms & shoulders", Name = "Shoulders", Tip = "Rounder, fuller shoulders.",
            Ops = [new() { Bone = "j_sako_{side}", Scale = 0.3f, Keep = OpKeep.Children }] },

        // Torso.
        new() { Key = "chest_length", Group = "Torso", Name = "Chest length", LiveOnly = true,
            Tip = "A longer (or shorter) chest, from the waist up to the shoulders. Live only.",
            Ops = [new() { Bone = "j_sebo_b", Next = "j_sebo_c", Scale = 0.5f, Stretch = true, Keep = OpKeep.Children }] },
        new() { Key = "ribcage", Group = "Torso", Name = "Chest width", Tip = "The chest wider or narrower (the breasts stay as they are).",
            Ops = [new() { Bone = "j_sebo_b", Axes = OpAxes.SideToSide, Scale = 0.3f, Keep = OpKeep.Children }] },
        new() { Key = "chest_depth", Group = "Torso", Name = "Chest depth", Tip = "The chest deeper front to back.",
            Ops = [new() { Bone = "j_sebo_b", Axes = OpAxes.FrontBack, Scale = 0.3f, Keep = OpKeep.Children }] },
        new() { Key = "upper_back", Group = "Torso", Name = "Upper back", Tip = "The upper back and chest broader.",
            Ops = [new() { Bone = "j_sebo_c", Axes = OpAxes.SideToSide, Scale = 0.3f, Keep = OpKeep.Children }] },
        new() { Key = "neck", Group = "Torso", Name = "Neck", Tip = "Thicker or slimmer neck.",
            Ops = [new() { Bone = "j_kubi", Next = "j_kao", Axes = OpAxes.Across, Scale = 0.35f, Keep = OpKeep.Children }] },
        .. Details,
    ];

    public static readonly Dictionary<string, BodySlider> ByKey = All.ToDictionary(s => s.Key);

    /// <summary>A side bone's name for one side ("_{side}" -> "_l" / "_r"), and whether it has sides.</summary>
    public static string ForSide(string name, bool left) => name.Replace("{side}", left ? "l" : "r");
    public static bool HasSides(string name) => name.Contains("{side}", StringComparison.Ordinal);
}

/// <summary>A named set of slider values (only the ones moved from 0 are kept).</summary>
[Serializable]
public sealed class BodyPreset
{
    public string Name { get; set; } = "Preset";
    public Dictionary<string, float> Values { get; set; } = new();

    public BodyPreset Clone() => new() { Name = Name, Values = new Dictionary<string, float>(Values) };

    /// <summary>As text to share: "name|key=value;key=value".</summary>
    public string ToText() => Name.Replace("|", "/") + "|" + string.Join(";", Values.Where(kv => kv.Value != 0f)
        .Select(kv => FormattableString.Invariant($"{kv.Key}={kv.Value:0.###}")));

    public static BodyPreset? FromText(string text)
    {
        text = text.Trim();
        int bar = text.IndexOf('|');
        if (bar < 0)
            return null;
        var preset = new BodyPreset { Name = text[..bar].Trim() };
        foreach (var part in text[(bar + 1)..].Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=');
            if (kv.Length == 2 && BodySliders.ByKey.TryGetValue(kv[0].Trim(), out var slider)
                && float.TryParse(kv[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v)
                && float.IsFinite(v))
                preset.Values[slider.Key] = Math.Clamp(v, slider.Min, slider.Max);
        }
        if (preset.Name.Length == 0)
            preset.Name = "Pasted";
        return preset;
    }
}
