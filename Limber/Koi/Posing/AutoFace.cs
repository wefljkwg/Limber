using System;
using System.Collections.Generic;
using System.Numerics;
using FFXIVClientStructs.Havok.Animation.Rig;
using Limber.Koi.Mathx;
using Limber.Koi.Net;
using Limber.Protocol;
using static FFXIVClientStructs.Havok.Animation.Rig.hkaPose;
using RenderSkeleton = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Skeleton;

namespace Limber.Koi.Posing;

/// <summary>
/// Automatic facial-expression mirroring.
///
/// Koikatsu sends how ~20 points on its face (eyelids, brows, lips, mouth corners, cheeks) have moved,
/// in face-relative units. Here every FFXIV face bone is classified by its standard name (mabup / mabdn
/// eyelids, mayu / miken brows, ulip / dlip / lip lips, hoho cheeks), matched to the nearest Koikatsu
/// points of the same kind, and moved by the same amount scaled to this face's own proportions. That
/// works on every race's face rig without per-race tables, and covers any Koikatsu expression, blink
/// and lip sync because it copies the result rather than interpreting pattern numbers.
/// </summary>
public sealed unsafe class AutoFace
{
    private enum BoneClass { LidUp, LidLow, Brow, LipUpper, LipLower, Corner, Cheek }

    private sealed class Driven
    {
        public int Bone;
        public BoneClass Class;
        public int SideSign; // +1 = character's right, -1 = left, 0 = centre
        public int RegionA, RegionB;
        public float WeightA, WeightB;
    }

    private readonly Configuration _cfg;
    private readonly List<Driven> _driven = new();
    private bool[] _reset = [];
    private Vector3[] _neutralPos = [];
    private Vector3[] _keptLocalPos = [];
    private Quaternion[] _keptLocalRot = [];
    private int _jaw = -1, _eyeL = -1, _eyeR = -1, _lipL = -1, _lipR = -1, _lipLowCenter = -1;
    private readonly List<int> _mabUpL = new(), _mabUpR = new(), _mabDnL = new(), _mabDnR = new();
    private SkeletonModel.Partial? _partFor;
    private Vector3[]? _regionPosFor;
    private bool _mirroredFor;

    public AutoFace(Configuration cfg) => _cfg = cfg;

    public void Reset()
    {
        _partFor = null;
        _regionPosFor = null;
    }

    /// <summary>Returns false when the frame carries no face data (caller falls back to simple jaw/gaze).</summary>
    public bool Apply(SkeletonModel model, RenderSkeleton* skel, SkeletonModel.Partial part, hkaPose* pose,
        PoseFrame frame, float[]? neutral, bool mirrored)
    {
        if (frame.RegionPos == null || frame.RegionDisp == null || frame.RegionValid == null)
            return false;

        EnsureSetup(model, part, frame.RegionPos, frame.RegionValid, mirrored);

        int n = part.Names.Length;

        // 1. Put the face back to its neutral shape (relative to wherever the head is now), so the
        //    actor's own expression doesn't mix with Koikatsu's. Parameter bones keep their state.
        // Bones we keep (parameter bones) must stay attached to a parent that gets reset: remember their
        // local transform before anything moves.
        for (int i = 0; i < n; i++)
        {
            int parent = part.Parents[i];
            if (parent < 0 || _reset[i] || !_reset[parent])
                continue;
            QMath.Read(pose->AccessBoneModelSpace(parent, PropagateOrNot.DontPropagate), out var pp0, out var pr0, out var ps0);
            QMath.Read(pose->AccessBoneModelSpace(i, PropagateOrNot.DontPropagate), out var cp0, out var cr0, out _);
            QMath.Decompose(pp0, pr0, ps0, cp0, cr0, out _keptLocalPos[i], out _keptLocalRot[i]);
        }

        for (int i = 0; i < n; i++)
        {
            int parent = part.Parents[i];
            var t = pose->AccessBoneModelSpace(i, PropagateOrNot.DontPropagate);
            if (parent >= 0 && !_reset[i] && _reset[parent])
            {
                // Kept bone under a reset parent: re-attach with its own local transform.
                QMath.Read(pose->AccessBoneModelSpace(parent, PropagateOrNot.DontPropagate), out var kp, out var kr, out var ks);
                QMath.Compose(kp, kr, ks, _keptLocalPos[i], _keptLocalRot[i], Vector3.One, out var kmp, out var kmr, out _);
                t->Translation = kmp.ToHk(t->Translation.W);
                t->Rotation = kmr.ToHk();
                _neutralPos[i] = kmp;
                continue;
            }
            if (parent < 0 || !_reset[i])
            {
                _neutralPos[i] = t->Translation.ToVec();
                continue;
            }
            QMath.Read(pose->AccessBoneModelSpace(parent, PropagateOrNot.DontPropagate), out var pp, out var pr, out var ps);
            QMath.Compose(pp, pr, ps, part.RefLocalPos[i], part.RefLocalRot[i], part.RefLocalScale[i],
                out var mp, out var mr, out var ms);
            t->Translation = mp.ToHk(t->Translation.W);
            t->Rotation = mr.ToHk();
            t->Scale = ms.ToHk(t->Scale.W);
            _neutralPos[i] = mp;
        }

        // 2. This face's frame and proportions, measured on the neutral face.
        if (_eyeL < 0 || _eyeR < 0)
            return true;
        var eyeL = _neutralPos[_eyeL];
        var eyeR = _neutralPos[_eyeR];
        var d = Vector3.Distance(eyeL, eyeR);
        if (d < 1e-5f)
            return true;
        var right = (eyeR - eyeL) / d;
        var eyeMid = (eyeL + eyeR) * 0.5f;
        var mouthMid = _lipL >= 0 && _lipR >= 0
            ? (_neutralPos[_lipL] + _neutralPos[_lipR]) * 0.5f
            : eyeMid - Vector3.UnitY * d; // rough fallback
        var upRaw = QMath.Reject(eyeMid - mouthMid, right);
        var em = upRaw.Length();
        if (em < 1e-5f)
            return true;
        var up = upRaw / em;
        var fwd = Vector3.Normalize(Vector3.Cross(right, up));
        if (model.TryFind("j_kao", out var hp, out var hb) && hp == 0)
        {
            var body = SkeletonModel.GetPose(skel, 0);
            if (body != null)
            {
                var headPos = body->AccessBoneModelSpace(hb, PropagateOrNot.DontPropagate)->Translation.ToVec();
                if (Vector3.Dot(fwd, eyeMid - headPos) < 0) fwd = -fwd;
            }
        }
        else if (fwd.Z * model.ForwardSign < 0)
        {
            fwd = -fwd; // no head bone: fall back to the skeleton's measured forward direction
        }
        var lidHL = LidHeight(_mabUpL, _mabDnL, up, d);
        var lidHR = LidHeight(_mabUpR, _mabDnR, up, d);

        var strength = Math.Clamp(_cfg.ExpressionStrength, 0f, 3f);
        var disp = frame.RegionDisp;
        var valid = frame.RegionValid;

        Vector3 Region(int k)
        {
            // Mirroring swaps left/right regions and flips the sideways component.
            int src = mirrored ? MirrorRegion(k) : k;
            if (!valid[src]) return Vector3.Zero;
            var v = disp[src];
            if (neutral != null && neutral.Length >= (src + 1) * 3)
                v -= new Vector3(neutral[src * 3], neutral[src * 3 + 1], neutral[src * 3 + 2]);
            if (mirrored) v.X = -v.X;
            return v;
        }

        // 3. Jaw: open it as far as Koikatsu's lower lip dropped.
        if (_cfg.DriveJaw && _cfg.ExpressionMouth && _jaw >= 0)
        {
            var low = Region((int)FaceRegion.LipLow);
            var drop = -low.Y * em * strength; // model units, positive = down
            var lipPos = _lipLowCenter >= 0 ? _neutralPos[_lipLowCenter] : mouthMid;
            var pivot = _neutralPos[_jaw];
            var len = Vector3.Distance(lipPos, pivot);
            if (drop > 0f && len > 1e-5f)
            {
                var angle = MathF.Asin(Math.Clamp(drop / len, 0f, 1f));
                angle = MathF.Min(angle, _cfg.JawMaxAngle * MathF.PI / 180f);
                // Rotate about the face's sideways axis, in whichever direction moves the lip down.
                var arm = lipPos - pivot;
                var probe = Quaternion.CreateFromAxisAngle(right, 0.1f).Rotate(arm);
                var sign = Vector3.Dot(probe - arm, up) < 0 ? 1f : -1f;
                var t = pose->AccessBoneModelSpace(_jaw, PropagateOrNot.Propagate);
                t->Rotation = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(right, sign * angle) * t->Rotation.ToQuat()).ToHk();
            }
        }

        // 4. Move each matched bone by its Koikatsu region's displacement, scaled to this face.
        foreach (var b in _driven)
        {
            if (!ClassEnabled(b.Class))
                continue;
            var v = Region(b.RegionA) * b.WeightA;
            if (b.RegionB >= 0)
                v += Region(b.RegionB) * b.WeightB;
            v *= strength;

            Vector3 offset;
            if (b.Class is BoneClass.LidUp or BoneClass.LidLow)
            {
                var h = b.SideSign > 0 ? lidHR : lidHL;
                v *= Math.Clamp(_cfg.EyelidStrength, 0f, 2f);
                // Keep lids from crossing each other or flying off.
                v.Y = Math.Clamp(v.Y, -1.1f, 0.8f);
                offset = (right * v.X + up * v.Y + fwd * v.Z) * h;

                // Slide the lid over the eyeball instead of through it: keep the bone at (at least) its
                // neutral distance from the eye's centre and turn it with the sweep, like a real eyelid.
                int eye = b.SideSign > 0 ? _eyeR : _eyeL;
                if (eye >= 0 && QMath.IsFinite(offset))
                {
                    var c = _neutralPos[eye];
                    var neutralLid = _neutralPos[b.Bone];
                    var r0 = Vector3.Distance(neutralLid, c);
                    if (r0 > 1e-5f && QMath.TryNormalize(neutralLid + offset - c, out var dir1))
                    {
                        var dir0 = (neutralLid - c) / r0;
                        var clearance = Math.Clamp(_cfg.EyelidClearance, 0f, 0.5f);
                        var lidTarget = c + dir1 * (r0 * (1f + clearance));
                        var swing = QMath.FromTo(dir0, dir1);
                        var tl = pose->AccessBoneModelSpace(b.Bone, PropagateOrNot.Propagate);
                        tl->Translation = lidTarget.ToHk(tl->Translation.W);
                        tl->Rotation = Quaternion.Normalize(swing * tl->Rotation.ToQuat()).ToHk();
                        continue;
                    }
                }
            }
            else
            {
                offset = right * (v.X * d) + up * (v.Y * em) + fwd * (v.Z * d);
            }

            if (offset.LengthSquared() < 1e-12f || !QMath.IsFinite(offset))
                continue;
            var target = _neutralPos[b.Bone] + offset;
            var tb = pose->AccessBoneModelSpace(b.Bone, PropagateOrNot.Propagate);
            tb->Translation = target.ToHk(tb->Translation.W);
        }
        return true;
    }

    private bool ClassEnabled(BoneClass c) => c switch
    {
        BoneClass.LidUp or BoneClass.LidLow => _cfg.ExpressionEyelids,
        BoneClass.Brow => _cfg.ExpressionBrows,
        BoneClass.Cheek => _cfg.ExpressionCheeks,
        _ => _cfg.ExpressionMouth,
    };

    private float LidHeight(List<int> up, List<int> down, Vector3 upAxis, float d)
    {
        if (up.Count == 0 || down.Count == 0)
            return 0.2f * d;
        Vector3 a = Vector3.Zero, b = Vector3.Zero;
        foreach (var i in up) a += _neutralPos[i];
        foreach (var i in down) b += _neutralPos[i];
        var h = Vector3.Dot(a / up.Count - b / down.Count, upAxis);
        return h > 0.02f * d ? h : 0.2f * d;
    }

    // ----------------------------------------------------------------------------------------
    // Matching FFXIV face bones to Koikatsu regions (cached until the face or the Koikatsu
    // character changes).

    private void EnsureSetup(SkeletonModel model, SkeletonModel.Partial part, Vector3[] regionPos, bool[] regionValid, bool mirrored)
    {
        if (_partFor == part && mirrored == _mirroredFor && _regionPosFor != null && SamePositions(_regionPosFor, regionPos))
            return;

        _partFor = part;
        _mirroredFor = mirrored;
        _regionPosFor = (Vector3[])regionPos.Clone();
        _driven.Clear();
        _mabUpL.Clear(); _mabUpR.Clear(); _mabDnL.Clear(); _mabDnR.Clear();

        int n = part.Names.Length;
        _reset = new bool[n];
        _neutralPos = new Vector3[n];
        _keptLocalPos = new Vector3[n];
        _keptLocalRot = new Quaternion[n];

        int Find(string name) => Array.IndexOf(part.Names, name);
        _jaw = Find("j_f_ago");
        _eyeL = Find("j_f_eye_l");
        _eyeR = Find("j_f_eye_r");
        _lipL = Find("j_f_lip_l");
        _lipR = Find("j_f_lip_r");
        _lipLowCenter = Find("j_f_dlip_a");

        // Face frame on the reference pose, for matching.
        if (_eyeL < 0 || _eyeR < 0)
            return;
        var eyeL = part.RefModelPos[_eyeL];
        var eyeR = part.RefModelPos[_eyeR];
        var d = Vector3.Distance(eyeL, eyeR);
        if (d < 1e-5f)
            return;
        var right = (eyeR - eyeL) / d;
        var eyeMid = (eyeL + eyeR) * 0.5f;
        var mouthMid = _lipL >= 0 && _lipR >= 0 ? (part.RefModelPos[_lipL] + part.RefModelPos[_lipR]) * 0.5f : eyeMid - Vector3.UnitY * d;
        var upRaw = QMath.Reject(eyeMid - mouthMid, right);
        var em = upRaw.Length();
        if (em < 1e-5f)
            return;
        var up = upRaw / em;

        for (int i = 0; i < n; i++)
        {
            var name = part.Names[i];
            // Only expression bones (j_f_*) are put back to neutral. Ears (j_mimi_*, Viera j_zera_*), hair,
            // earrings and anything else that lives in some races' face skeleton keep whatever the game gives
            // them; resetting those to the skeleton's reference pose made ears flick back and forth.
            // Parameter / no-animation bones carry the character's face customisation: never touch them.
            _reset[i] = part.Parents[i] >= 0 && name.StartsWith("j_f_", StringComparison.Ordinal) &&
                        !name.Contains("noanim") && !name.Contains("prm");

            if (!TryClassify(name, out var cls, out var side))
                continue;

            if (cls == BoneClass.LidUp) (side > 0 ? _mabUpR : _mabUpL).Add(i);
            if (cls == BoneClass.LidLow) (side > 0 ? _mabDnR : _mabDnL).Add(i);

            var rel = part.RefModelPos[i] - eyeMid;
            var pos = new Vector2(Vector3.Dot(rel, right) / d, Vector3.Dot(rel, up) / em);
            var candidates = Candidates(cls, side, pos.X);

            // Nearest one or two candidate regions (by position on the face), blended by distance.
            int bestA = -1, bestB = -1;
            float distA = float.MaxValue, distB = float.MaxValue;
            foreach (var k in candidates)
            {
                if (!regionValid[k]) continue;
                var rp = regionPos[k];
                var dist = Vector2.Distance(pos, new Vector2(rp.X, rp.Y));
                if (dist < distA) { bestB = bestA; distB = distA; bestA = k; distA = dist; }
                else if (dist < distB) { bestB = k; distB = dist; }
            }
            if (bestA < 0)
                continue;

            var driven = new Driven { Bone = i, Class = cls, SideSign = side, RegionA = bestA, RegionB = -1, WeightA = 1f };
            // Blend with the second region only for continuous features (lips, brows).
            if (bestB >= 0 && cls is BoneClass.LipUpper or BoneClass.LipLower or BoneClass.Brow)
            {
                var wa = 1f / MathF.Max(distA, 1e-3f);
                var wb = 1f / MathF.Max(distB, 1e-3f);
                driven.RegionB = bestB;
                driven.WeightA = wa / (wa + wb);
                driven.WeightB = wb / (wa + wb);
            }
            _driven.Add(driven);
        }
    }

    private static bool SamePositions(Vector3[] a, Vector3[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (Vector3.DistanceSquared(a[i], b[i]) > 0.02f * 0.02f)
                return false;
        return true;
    }

    /// <summary>Classify an FFXIV face bone by its standard name. Side: +1 right (_r), -1 left (_l), 0 centre.</summary>
    private static bool TryClassify(string name, out BoneClass cls, out int side)
    {
        cls = default;
        side = name.EndsWith("_l") ? -1 : name.EndsWith("_r") ? 1 : 0;
        if (!name.StartsWith("j_f_") || name.Contains("noanim") || name.Contains("prm"))
            return false;

        if (name.Contains("mabup")) { cls = BoneClass.LidUp; return side != 0; }
        if (name.Contains("mabdn")) { cls = BoneClass.LidLow; return side != 0; }
        if (name.Contains("mayu") || name.Contains("miken")) { cls = BoneClass.Brow; return true; }
        if (name.Contains("hoho")) { cls = BoneClass.Cheek; return side != 0; }
        if (name.StartsWith("j_f_lip_")) { cls = BoneClass.Corner; return side != 0; }
        // ulip / umlip / uslip = upper lip, dlip / dmlip / dslip = lower lip
        if (name.StartsWith("j_f_u") && name.Contains("lip")) { cls = BoneClass.LipUpper; return true; }
        if (name.StartsWith("j_f_d") && name.Contains("lip")) { cls = BoneClass.LipLower; return true; }
        return false;
    }

    private static readonly int[] BrowL = [(int)FaceRegion.BrowInL, (int)FaceRegion.BrowMidL, (int)FaceRegion.BrowOutL];
    private static readonly int[] BrowR = [(int)FaceRegion.BrowInR, (int)FaceRegion.BrowMidR, (int)FaceRegion.BrowOutR];
    private static readonly int[] BrowAll = [.. BrowL, .. BrowR];
    private static readonly int[] Upper = [(int)FaceRegion.LipUpL, (int)FaceRegion.LipUp, (int)FaceRegion.LipUpR, (int)FaceRegion.MouthCornerL, (int)FaceRegion.MouthCornerR];
    private static readonly int[] Lower = [(int)FaceRegion.LipLowL, (int)FaceRegion.LipLow, (int)FaceRegion.LipLowR, (int)FaceRegion.MouthCornerL, (int)FaceRegion.MouthCornerR];

    private static int[] Candidates(BoneClass cls, int side, float x) => cls switch
    {
        BoneClass.LidUp => [side > 0 ? (int)FaceRegion.LidUpR : (int)FaceRegion.LidUpL],
        BoneClass.LidLow => [side > 0 ? (int)FaceRegion.LidLowR : (int)FaceRegion.LidLowL],
        BoneClass.Brow => side > 0 ? BrowR : side < 0 ? BrowL : BrowAll,
        BoneClass.Cheek => [side > 0 ? (int)FaceRegion.CheekR : (int)FaceRegion.CheekL],
        BoneClass.Corner => [side > 0 ? (int)FaceRegion.MouthCornerR : (int)FaceRegion.MouthCornerL],
        BoneClass.LipUpper => Upper,
        BoneClass.LipLower => Lower,
        _ => [],
    };

    private static int MirrorRegion(int k) => (FaceRegion)k switch
    {
        FaceRegion.LidUpL => (int)FaceRegion.LidUpR,
        FaceRegion.LidUpR => (int)FaceRegion.LidUpL,
        FaceRegion.LidLowL => (int)FaceRegion.LidLowR,
        FaceRegion.LidLowR => (int)FaceRegion.LidLowL,
        FaceRegion.BrowInL => (int)FaceRegion.BrowInR,
        FaceRegion.BrowInR => (int)FaceRegion.BrowInL,
        FaceRegion.BrowMidL => (int)FaceRegion.BrowMidR,
        FaceRegion.BrowMidR => (int)FaceRegion.BrowMidL,
        FaceRegion.BrowOutL => (int)FaceRegion.BrowOutR,
        FaceRegion.BrowOutR => (int)FaceRegion.BrowOutL,
        FaceRegion.MouthCornerL => (int)FaceRegion.MouthCornerR,
        FaceRegion.MouthCornerR => (int)FaceRegion.MouthCornerL,
        FaceRegion.LipUpL => (int)FaceRegion.LipUpR,
        FaceRegion.LipUpR => (int)FaceRegion.LipUpL,
        FaceRegion.LipLowL => (int)FaceRegion.LipLowR,
        FaceRegion.LipLowR => (int)FaceRegion.LipLowL,
        FaceRegion.CheekL => (int)FaceRegion.CheekR,
        FaceRegion.CheekR => (int)FaceRegion.CheekL,
        _ => k,
    };
}
