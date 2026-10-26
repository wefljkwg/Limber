using System.Collections.Generic;
using Limber.Protocol;
using UnityEngine;
using Joint = Limber.Protocol.Joint;

namespace Limber.Koikatsu
{
    /// <summary>
    /// Maps protocol joints to Koikatsu bone transforms for one character.
    /// Body bones are searched under ChaControl.objBodyBone first so that accessory
    /// or clothing copies of bone names never win; face bones under objHeadBone.
    /// </summary>
    internal sealed class BoneCache
    {
        // Each joint may list several candidate names; the first one found is used.
        private static readonly string[][] Names = BuildNames();

        private readonly Transform[] _joints = new Transform[(int)Joint.Count];
        private ChaControl _owner;
        private GameObject _ownerBodyBone;
        private GameObject _ownerHeadBone;
        private float _nextRetry;

        /// <summary>Body joints found (genital points are not counted, most characters don't have them).</summary>
        public int FoundCount { get; private set; }

        /// <summary>
        /// Eye centres in the head bone's local space, measured once when the rig is found. The head's direction
        /// is aimed at these instead of the live eye bones, which also move with gaze, blinks and expressions and
        /// made the FFXIV head jitter.
        /// </summary>
        public Vector3 EyeLocalL, EyeLocalR;
        public bool HasEyeLocal;

        /// <summary>Koikatsu shaft bones from base to tip, empty if the character has none.</summary>
        public readonly List<Transform> DanChain = new List<Transform>();

        public Transform Get(Joint j)
        {
            var t = _joints[(int)j];
            return t != null ? t : null; // Unity-null aware
        }

        public bool IsValidFor(ChaControl chara)
        {
            if (_owner == null || _owner != chara)
                return false;
            // Character reload / body swap replaces the bone hierarchy.
            if (_ownerBodyBone == null || _ownerBodyBone != chara.objBodyBone)
                return false;
            if (_ownerHeadBone != chara.objHeadBone)
                return false; // head swapped in the maker / Studio
            // If the rig is incomplete, don't rescan the hierarchy every frame.
            if (_joints[(int)Joint.Hips] == null)
                return Time.unscaledTime < _nextRetry;
            return true;
        }

        public void Clear()
        {
            for (int i = 0; i < _joints.Length; i++)
                _joints[i] = null;
            _owner = null;
            _ownerBodyBone = null;
            _ownerHeadBone = null;
            DanChain.Clear();
            FoundCount = 0;
            HasEyeLocal = false;
        }

        public void Build(ChaControl chara)
        {
            Clear();
            _owner = chara;
            _ownerBodyBone = chara.objBodyBone;
            _ownerHeadBone = chara.objHeadBone;
            _nextRetry = Time.unscaledTime + 2f;

            var lookup = new Dictionary<string, Transform>();
            AddHierarchy(lookup, chara.objBodyBone);
            AddHierarchy(lookup, chara.objHeadBone);
            AddHierarchy(lookup, chara.gameObject); // fallback, lowest priority

            int found = 0;
            for (int i = 0; i < Names.Length; i++)
            {
                var candidates = Names[i];
                if (candidates == null)
                    continue;
                for (int c = 0; c < candidates.Length; c++)
                {
                    Transform t;
                    if (lookup.TryGetValue(candidates[c], out t))
                    {
                        _joints[i] = t;
                        if (i < (int)Joint.DanP0)
                            found++;
                        break;
                    }
                }
            }
            FoundCount = found;

            var head = _joints[(int)Joint.Head];
            var eyeL = _joints[(int)Joint.EyeL];
            var eyeR = _joints[(int)Joint.EyeR];
            if (head != null && eyeL != null && eyeR != null)
            {
                EyeLocalL = head.InverseTransformPoint(eyeL.position);
                EyeLocalR = head.InverseTransformPoint(eyeR.position);
                HasEyeLocal = true;
            }

            // Shaft chain: cm_J_dan100_00, cm_J_dan101_00 ... cm_J_dan109_00 (names sort base -> tip).
            var dan = new List<string>();
            foreach (var key in lookup.Keys)
            {
                if (key.Length == 14 && key.StartsWith("cm_J_dan1") && key.EndsWith("_00"))
                    dan.Add(key);
            }
            dan.Sort(System.StringComparer.Ordinal);
            foreach (var key in dan)
                DanChain.Add(lookup[key]);

            if (_joints[(int)Joint.Hips] == null)
                LimberSenderPlugin.Logger.LogWarning("Limber: could not find cf_j_hips on " + chara.name);
        }

        private static void AddHierarchy(Dictionary<string, Transform> lookup, GameObject root)
        {
            if (root == null)
                return;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (!lookup.ContainsKey(t.name))
                    lookup.Add(t.name, t);
            }
        }

        private static string[][] BuildNames()
        {
            var n = new string[(int)Joint.Count][];
            n[(int)Joint.Hips] = new[] { "cf_j_hips" };
            n[(int)Joint.Spine1] = new[] { "cf_j_spine01" };
            n[(int)Joint.Spine2] = new[] { "cf_j_spine02" };
            n[(int)Joint.Spine3] = new[] { "cf_j_spine03" };
            n[(int)Joint.Neck] = new[] { "cf_j_neck" };
            n[(int)Joint.Head] = new[] { "cf_j_head" };

            Side(n, "L", Joint.ShoulderL, Joint.UpperArmL, Joint.ElbowL, Joint.WristL,
                Joint.ThighL, Joint.KneeL, Joint.AnkleL, Joint.FootL, Joint.ToeL, Joint.ThumbL1);
            Side(n, "R", Joint.ShoulderR, Joint.UpperArmR, Joint.ElbowR, Joint.WristR,
                Joint.ThighR, Joint.KneeR, Joint.AnkleR, Joint.FootR, Joint.ToeR, Joint.ThumbR1);

            // Eye centre bones differ slightly between KK builds; try several.
            n[(int)Joint.EyeL] = new[] { "cf_J_Eye_t_L", "cf_J_Eye_tx_L", "cf_J_Eye_s_L", "cf_J_eye_rs_L", "cf_J_look_L" };
            n[(int)Joint.EyeR] = new[] { "cf_J_Eye_t_R", "cf_J_Eye_tx_R", "cf_J_Eye_s_R", "cf_J_eye_rs_R", "cf_J_look_R" };

            n[(int)Joint.TesticleL] = new[] { "cm_J_dan_f_L" };
            n[(int)Joint.TesticleR] = new[] { "cm_J_dan_f_R" };
            n[(int)Joint.BustRootL] = new[] { "cf_d_bust01_L", "cf_s_bust01_L", "cf_j_bust01_L" };
            n[(int)Joint.BustRootR] = new[] { "cf_d_bust01_R", "cf_s_bust01_R", "cf_j_bust01_R" };
            // cf_d_bust03 is the end of the breast physics chain; the nipple bone is a fallback.
            n[(int)Joint.BustTipL] = new[] { "cf_d_bust03_L", "cf_d_bnip01_L", "cf_s_bust03_L", "cf_s_bnip02_L" };
            n[(int)Joint.BustTipR] = new[] { "cf_d_bust03_R", "cf_d_bnip01_R", "cf_s_bust03_R", "cf_s_bnip02_R" };
            // BustRest* are computed by the sender.
            // DanP0..DanP6 are computed from DanChain, not looked up by name.
            return n;
        }

        private static void Side(string[][] n, string s,
            Joint shoulder, Joint upperArm, Joint elbow, Joint wrist,
            Joint thigh, Joint knee, Joint ankle, Joint foot, Joint toe, Joint firstFinger)
        {
            n[(int)shoulder] = new[] { "cf_j_shoulder_" + s };
            n[(int)upperArm] = new[] { "cf_j_arm00_" + s };
            n[(int)elbow] = new[] { "cf_j_forearm01_" + s };
            n[(int)wrist] = new[] { "cf_j_hand_" + s };
            n[(int)thigh] = new[] { "cf_j_thigh00_" + s };
            n[(int)knee] = new[] { "cf_j_leg01_" + s };
            n[(int)ankle] = new[] { "cf_j_leg03_" + s };
            n[(int)foot] = new[] { "cf_j_foot_" + s };
            n[(int)toe] = new[] { "cf_j_toes_" + s };

            // Finger order in the enum: thumb, index, middle, ring, little; 3 joints each.
            var fingers = new[] { "thumb", "index", "middle", "ring", "little" };
            int idx = (int)firstFinger;
            for (int f = 0; f < fingers.Length; f++)
                for (int k = 1; k <= 3; k++)
                    n[idx++] = new[] { "cf_j_" + fingers[f] + "0" + k + "_" + s };
        }
    }
}
