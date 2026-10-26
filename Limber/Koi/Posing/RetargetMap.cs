using System.Collections.Generic;
using Limber.Protocol;

namespace Limber.Koi.Posing;

public enum BoneGroup
{
    Torso,
    Head,
    Arms,
    Hands,
    Legs,
    Genitals,
}

public enum PoleKind
{
    /// <summary>Pole = To - From (a direction that is rigid with the bone, e.g. hip line, palm width).</summary>
    Vector,

    /// <summary>
    /// Pole = normal of the bend plane: cross(aim, (C - B)). Used for elbows and knees so the
    /// limb's twist follows the way it actually bends. Falls back to <see cref="RetargetEntry.BendForward"/>
    /// when the reference pose is straight.
    /// </summary>
    Hinge,
}

/// <summary>
/// One FFXIV bone driven from Koikatsu joint positions.
/// Every entry is defined purely by matching anatomical points on both skeletons, so the same
/// table works on every FFXIV race/body: each actor's own reference pose supplies the local axes.
/// </summary>
public sealed record RetargetEntry(
    string Bone,
    BoneGroup Group,
    string[] FfAimFrom, string[] FfAimTo,
    Joint[] KkAimFrom, Joint[] KkAimTo,
    PoleKind PoleKind,
    string[] FfPoleA, string[] FfPoleB,
    Joint[] KkPoleA, Joint[] KkPoleB,
    string[]? FfHingeC = null, Joint[]? KkHingeC = null,
    float BendForward = 1f,
    bool IsHead = false);

public static class RetargetMap
{
    private static string[] F(params string[] n) => n;
    private static Joint[] K(params Joint[] j) => j;

    public static IReadOnlyList<RetargetEntry> Build()
    {
        var list = new List<RetargetEntry>();

        // ----- Torso -----------------------------------------------------------------
        // Pelvis: from the hip-joint midpoint up to the lower spine, twisted by the hip line.
        list.Add(new RetargetEntry("j_kosi", BoneGroup.Torso,
            F("j_asi_a_l", "j_asi_a_r"), F("j_sebo_a"),
            K(Joint.ThighL, Joint.ThighR), K(Joint.Spine1),
            PoleKind.Vector, F("j_asi_a_r"), F("j_asi_a_l"), K(Joint.ThighR), K(Joint.ThighL)));

        list.Add(new RetargetEntry("j_sebo_a", BoneGroup.Torso,
            F("j_sebo_a"), F("j_sebo_b"), K(Joint.Spine1), K(Joint.Spine2),
            PoleKind.Vector, F("j_asi_a_r"), F("j_asi_a_l"), K(Joint.ThighR), K(Joint.ThighL)));

        list.Add(new RetargetEntry("j_sebo_b", BoneGroup.Torso,
            F("j_sebo_b"), F("j_sebo_c"), K(Joint.Spine2), K(Joint.Spine3),
            PoleKind.Vector, F("j_ude_a_r"), F("j_ude_a_l"), K(Joint.UpperArmR), K(Joint.UpperArmL)));

        list.Add(new RetargetEntry("j_sebo_c", BoneGroup.Torso,
            F("j_sebo_c"), F("j_kubi"), K(Joint.Spine3), K(Joint.Neck),
            PoleKind.Vector, F("j_ude_a_r"), F("j_ude_a_l"), K(Joint.UpperArmR), K(Joint.UpperArmL)));

        list.Add(new RetargetEntry("j_kubi", BoneGroup.Head,
            F("j_kubi"), F("j_kao"), K(Joint.Neck), K(Joint.Head),
            PoleKind.Vector, F("j_ude_a_r"), F("j_ude_a_l"), K(Joint.UpperArmR), K(Joint.UpperArmL)));

        // Head: aimed from the head joint to the point between the eyes, twisted by the eye line.
        list.Add(new RetargetEntry("j_kao", BoneGroup.Head,
            F("j_kao"), F("j_f_eye_l", "j_f_eye_r"), K(Joint.Head), K(Joint.EyeL, Joint.EyeR),
            PoleKind.Vector, F("j_f_eye_r"), F("j_f_eye_l"), K(Joint.EyeR), K(Joint.EyeL),
            IsHead: true));

        AddArm(list, "l", Joint.ShoulderL, Joint.UpperArmL, Joint.ElbowL, Joint.WristL,
            Joint.IndexL1, Joint.MiddleL1, Joint.LittleL1, Joint.ThumbL1);
        AddArm(list, "r", Joint.ShoulderR, Joint.UpperArmR, Joint.ElbowR, Joint.WristR,
            Joint.IndexR1, Joint.MiddleR1, Joint.LittleR1, Joint.ThumbR1);

        AddLeg(list, "l", Joint.ThighL, Joint.KneeL, Joint.AnkleL, Joint.FootL, Joint.ToeL);
        AddLeg(list, "r", Joint.ThighR, Joint.KneeR, Joint.AnkleR, Joint.FootR, Joint.ToeR);

        AddGenitals(list);

        return list;
    }

    private static void AddArm(List<RetargetEntry> list, string s,
        Joint shoulder, Joint upper, Joint elbow, Joint wrist,
        Joint index1, Joint middle1, Joint little1, Joint thumb1)
    {
        string B(string n) => $"{n}_{s}";

        // Palm width (little -> index) drives forearm/hand/finger twist.
        var ffPalmA = F(B("j_ko_a"));
        var ffPalmB = F(B("j_hito_a"));
        var kkPalmA = K(little1);
        var kkPalmB = K(index1);

        list.Add(new RetargetEntry(B("j_sako"), BoneGroup.Arms,
            F(B("j_sako")), F(B("j_ude_a")), K(shoulder), K(upper),
            PoleKind.Vector, F("j_sebo_c"), F("j_kubi"), K(Joint.Spine3), K(Joint.Neck)));

        list.Add(new RetargetEntry(B("j_ude_a"), BoneGroup.Arms,
            F(B("j_ude_a")), F(B("j_ude_b")), K(upper), K(elbow),
            PoleKind.Hinge, F(B("j_ude_a")), F(B("j_ude_b")), K(upper), K(elbow),
            FfHingeC: F(B("j_te")), KkHingeC: K(wrist), BendForward: 1f));

        list.Add(new RetargetEntry(B("j_ude_b"), BoneGroup.Arms,
            F(B("j_ude_b")), F(B("j_te")), K(elbow), K(wrist),
            PoleKind.Vector, ffPalmA, ffPalmB, kkPalmA, kkPalmB));

        list.Add(new RetargetEntry(B("j_te"), BoneGroup.Hands,
            F(B("j_te")), F(B("j_naka_a")), K(wrist), K(middle1),
            PoleKind.Vector, ffPalmA, ffPalmB, kkPalmA, kkPalmB));

        // Fingers: FFXIV has two driven segments (a, b) per finger; Koikatsu has three joints.
        var fingers = new (string ff, Joint first)[]
        {
            ("j_oya", thumb1),
            ("j_hito", index1),
            ("j_naka", middle1),
            ("j_kusu", (Joint)((int)middle1 + 3)),
            ("j_ko", little1),
        };

        foreach (var (ff, first) in fingers)
        {
            var j1 = first;
            var j2 = (Joint)((int)first + 1);
            var j3 = (Joint)((int)first + 2);
            var a = B($"{ff}_a");
            var b = B($"{ff}_b");

            list.Add(new RetargetEntry(a, BoneGroup.Hands,
                F(a), F(b), K(j1), K(j2),
                PoleKind.Vector, ffPalmA, ffPalmB, kkPalmA, kkPalmB));

            // The tip segment has no FFXIV child bone; its axis continues the a->b direction.
            list.Add(new RetargetEntry(b, BoneGroup.Hands,
                F(a), F(b), K(j2), K(j3),
                PoleKind.Vector, ffPalmA, ffPalmB, kkPalmA, kkPalmB));
        }
    }

    /// <summary>
    /// YAS / IVCS shaft chain (iv_ochinko_a..f). Koikatsu sends 7 evenly spaced points along its own
    /// chain (DanP0..DanP6); FFXIV bone i follows segment i -> i+1. Twist follows the hip line.
    /// Skeletons without these bones simply skip them.
    /// </summary>
    private static void AddGenitals(List<RetargetEntry> list)
    {
        string[] ff = ["iv_ochinko_a", "iv_ochinko_b", "iv_ochinko_c", "iv_ochinko_d", "iv_ochinko_e", "iv_ochinko_f"];
        for (int i = 0; i < ff.Length; i++)
        {
            // The last bone has no child to aim at; it continues the previous segment's direction.
            var from = i < ff.Length - 1 ? ff[i] : ff[i - 1];
            var to = i < ff.Length - 1 ? ff[i + 1] : ff[i];
            list.Add(new RetargetEntry(ff[i], BoneGroup.Genitals,
                F(from), F(to),
                K((Joint)((int)Joint.DanP0 + i)), K((Joint)((int)Joint.DanP0 + i + 1)),
                PoleKind.Vector, F("j_asi_a_r"), F("j_asi_a_l"), K(Joint.ThighR), K(Joint.ThighL)));
        }
    }

    private static void AddLeg(List<RetargetEntry> list, string s,
        Joint thigh, Joint knee, Joint ankle, Joint foot, Joint toe)
    {
        string B(string n) => $"{n}_{s}";

        // Thigh twist follows the knee bend plane. Knees bend backwards.
        list.Add(new RetargetEntry(B("j_asi_a"), BoneGroup.Legs,
            F(B("j_asi_a")), F(B("j_asi_c")), K(thigh), K(knee),
            PoleKind.Hinge, F(B("j_asi_a")), F(B("j_asi_c")), K(thigh), K(knee),
            FfHingeC: F(B("j_asi_d")), KkHingeC: K(ankle), BendForward: -1f));

        // FFXIV splits the shin into a knee helper (b) and the calf (c); both follow knee -> ankle,
        // twisted by the foot direction.
        foreach (var bone in new[] { B("j_asi_b"), B("j_asi_c") })
        {
            list.Add(new RetargetEntry(bone, BoneGroup.Legs,
                F(bone), F(B("j_asi_d")), K(knee), K(ankle),
                PoleKind.Vector, F(B("j_asi_d")), F(B("j_asi_e")), K(foot), K(toe)));
        }

        // Foot: ankle -> toes, twisted by the shin direction.
        list.Add(new RetargetEntry(B("j_asi_d"), BoneGroup.Legs,
            F(B("j_asi_d")), F(B("j_asi_e")), K(ankle), K(toe),
            PoleKind.Vector, F(B("j_asi_c")), F(B("j_asi_d")), K(knee), K(ankle)));
    }
}
