using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.Havok.Animation.Rig;
using Limber.Koi.Mathx;
using Limber.Koi.Net;
using Limber.Protocol;
using static FFXIVClientStructs.Havok.Animation.Rig.hkaPose;
using CharacterBase = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase;
using RenderSkeleton = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Skeleton;

namespace Limber.Koi.Posing;

/// <summary>
/// Everything needed to drive ONE GPose actor from ONE Koikatsu channel: the actor's skeleton cache,
/// resolved retarget entries, smoothing history and face state. One instance per link, so several
/// actors can be driven at once without sharing state.
/// </summary>
public sealed unsafe class ActorDriver
{
    private readonly Configuration _cfg;
    private readonly IPluginLog _log;
    private readonly FaceDriver _face;
    private readonly IReadOnlyList<RetargetEntry> _map;

    private SkeletonModel? _model;
    private readonly List<ResolvedEntry> _entries = new();
    private int _haraIndex = -1;
    private Vector3 _haraRefPos, _kosiRefPos, _thighMidRef;
    private int _kosiEntry = -1;

    private readonly Vector3[] _ff = new Vector3[(int)Joint.Count];
    private readonly bool[] _ffValid = new bool[(int)Joint.Count];
    private readonly Quaternion[] _targets = new Quaternion[128];
    private readonly bool[] _targetValid = new bool[128];
    private bool _allowGenitals;
    private int _muneL = -1, _muneR = -1;

    public int ResolvedBones => _entries.Count;
    public int DrivenBones { get; private set; }
    public IReadOnlyDictionary<string, float> FaceWeights => _face.Weights;

    private sealed class ResolvedEntry
    {
        public RetargetEntry Def = null!;
        public int Bone;
        public Vector3 AimLocal, PoleLocal;
        public Quaternion RefRot;
        public Vector3 PoleRefModel;
        public int AncestorEntry = -1;
        public Quaternion Last = Quaternion.Identity;
        public bool HasLast;
        public Vector3 LastPole;
        public bool HasLastPole;
        public Quaternion Delta = Quaternion.Identity;
    }

    public ActorDriver(Configuration cfg, IPluginLog log, IReadOnlyList<RetargetEntry> map)
    {
        _cfg = cfg;
        _log = log;
        _map = map;
        _face = new FaceDriver(cfg);
    }

    /// <summary>Make sure the skeleton cache matches the actor. False if the actor has no usable skeleton.</summary>
    public bool Prepare(CharacterBase* cb)
    {
        var skel = cb->Skeleton;
        if (skel == null)
            return false;
        if (_model == null || !_model.Matches(skel))
        {
            _model = SkeletonModel.Create(skel);
            Resolve();
        }
        return _model != null;
    }

    /// <summary>Store the actor's current face as an expression preset.</summary>
    public int Capture(CharacterBase* cb, string raceKey, string presetName)
        => _model == null ? 0 : _face.Capture(_model, cb->Skeleton, raceKey, presetName);

    /// <summary>Retarget one Koikatsu frame onto the actor. Call <see cref="Prepare"/> first.</summary>
    public int Drive(CharacterBase* cb, PoseFrame frame, string raceKey, float dt, bool allowGenitals, float[]? faceNeutral)
    {
        _allowGenitals = allowGenitals;
        var skel = cb->Skeleton;
        var pose0 = SkeletonModel.GetPose(skel, 0);
        if (_model == null || pose0 == null)
            return 0;

        ConvertJoints(frame, _model);
        var driven = SolveBody(dt);
        WriteBody(pose0, driven);
        if (_cfg.DriveBreasts && _allowGenitals)
            ApplyBreasts(pose0);
        ReparentPartials(skel);
        _face.Apply(_model, skel, frame, raceKey, dt, faceNeutral);
        SyncAll(skel);
        DrivenBones = driven;
        return driven;
    }

    public void ResetSmoothing()
    {
        foreach (var e in _entries)
        {
            e.HasLast = false;
            e.HasLastPole = false;
        }
        _face.ResetSmoothing();
        DrivenBones = 0;
    }

    // ----------------------------------------------------------------------------------------
    // Setup

    private void Resolve()
    {
        _entries.Clear();
        _haraIndex = -1;
        _kosiEntry = -1;
        _face.Reset();
        if (_model == null)
            return;

        var body = _model.Partials[0];
        foreach (var def in _map)
        {
            if (!_model.TryFind(def.Bone, out var partial, out var bone) || partial != 0)
                continue;
            if (!_model.TryRefPoint(def.FfAimFrom, out var a0) || !_model.TryRefPoint(def.FfAimTo, out var a1))
                continue;
            if (!QMath.TryNormalize(a1 - a0, out var aim))
                continue;

            if (!TryRefPole(def, aim, out var pole))
                continue;

            var refRot = body.RefModelRot[bone];
            var inv = Quaternion.Inverse(refRot);
            _entries.Add(new ResolvedEntry
            {
                Def = def,
                Bone = bone,
                AimLocal = Vector3.Normalize(inv.Rotate(aim)),
                PoleLocal = Vector3.Normalize(inv.Rotate(pole)),
                RefRot = refRot,
                PoleRefModel = pole,
            });
        }

        // Parents before children so Propagate writes cascade correctly.
        _entries.Sort((x, y) => x.Bone.CompareTo(y.Bone));

        // Nearest driven ancestor, used to carry a pole along when Koikatsu's is degenerate.
        var entryByBone = new Dictionary<int, int>();
        for (int i = 0; i < _entries.Count; i++)
            entryByBone[_entries[i].Bone] = i;
        for (int i = 0; i < _entries.Count; i++)
        {
            int p = body.Parents[_entries[i].Bone];
            while (p >= 0)
            {
                if (entryByBone.TryGetValue(p, out var ei)) { _entries[i].AncestorEntry = ei; break; }
                p = body.Parents[p];
            }
            if (_entries[i].Def.Bone == "j_kosi")
                _kosiEntry = i;
        }

        if (_model.TryFind("n_hara", out var hp, out var hb) && hp == 0)
        {
            _haraIndex = hb;
            _haraRefPos = body.RefModelPos[hb];
        }
        _model.TryRefPos("j_kosi", out _kosiRefPos);
        _muneL = _model.TryFind("j_mune_l", out var mpl, out var mbl) && mpl == 0 ? mbl : -1;
        _muneR = _model.TryFind("j_mune_r", out var mpr, out var mbr) && mpr == 0 ? mbr : -1;
        _model.TryRefPoint(["j_asi_a_l", "j_asi_a_r"], out _thighMidRef);

        _log.Information($"Limber: resolved {_entries.Count} retarget bones " +
                         $"(left {_model.LeftSign:+0;-0}, forward {_model.ForwardSign:+0;-0}, leg {_model.LegLength:0.000})");
    }

    private bool TryRefPole(RetargetEntry def, Vector3 aim, out Vector3 pole)
    {
        pole = default;
        if (!_model!.TryRefPoint(def.FfPoleA, out var pa) || !_model.TryRefPoint(def.FfPoleB, out var pb))
            return false;

        Vector3 raw;
        if (def.PoleKind == PoleKind.Hinge)
        {
            if (def.FfHingeC == null || !_model.TryRefPoint(def.FfHingeC, out var pc))
                return false;
            raw = Vector3.Cross(aim, pc - pb);
            if (raw.Length() < 0.15f * (pc - pb).Length())
            {
                // Straight limb in the reference pose: use the natural bend direction instead.
                var bend = new Vector3(0, 0, _model.ForwardSign * def.BendForward);
                raw = Vector3.Cross(aim, bend);
            }
        }
        else
        {
            raw = pb - pa;
        }

        return QMath.TryNormalize(QMath.Reject(raw, aim), out pole);
    }


    // ----------------------------------------------------------------------------------------
    // Per frame

    private static readonly Joint[] MirrorMap = BuildMirrorMap();

    private void ConvertJoints(PoseFrame frame, SkeletonModel model)
    {
        // Koikatsu (Unity): left-handed, +X = character's right, +Z = forward.
        // FFXIV model space: measured per skeleton (LeftSign / ForwardSign).
        float sx = -model.LeftSign;
        float sz = model.ForwardSign;
        if (_cfg.FlipFacing) { sx = -sx; sz = -sz; }
        if (_cfg.MirrorLeftRight) sx = -sx;

        for (int i = 0; i < (int)Joint.Count; i++)
        {
            int src = _cfg.MirrorLeftRight ? (int)MirrorMap[i] : i;
            _ffValid[i] = frame.Valid[src];
            var p = frame.Positions[src];
            _ff[i] = new Vector3(p.X * sx, p.Y, p.Z * sz);
        }
        _face.SetAxisSigns(sx, sz, _cfg.MirrorLeftRight);
    }

    private bool TryPoint(Joint[] joints, out Vector3 p)
    {
        p = Vector3.Zero;
        foreach (var j in joints)
        {
            if (!_ffValid[(int)j]) return false;
            p += _ff[(int)j];
        }
        p /= joints.Length;
        return true;
    }

    private bool GroupEnabled(BoneGroup g) => g switch
    {
        BoneGroup.Torso => _cfg.DriveTorso,
        BoneGroup.Head => _cfg.DriveHead,
        BoneGroup.Arms => _cfg.DriveArms,
        BoneGroup.Hands => _cfg.DriveHands,
        BoneGroup.Legs => _cfg.DriveLegs,
        BoneGroup.Genitals => _cfg.DriveGenitals && _allowGenitals,
        _ => true,
    };

    private int SolveBody(float dt)
    {
        int driven = 0;
        // Frame-rate independent smoothing factor.
        var smooth = System.Math.Clamp(_cfg.Smoothing, 0f, 0.95f);
        var alpha = smooth <= 0f ? 1f : 1f - MathF.Pow(smooth, dt * 60f);

        for (int i = 0; i < _entries.Count && i < _targets.Length; i++)
        {
            var e = _entries[i];
            _targetValid[i] = false;
            e.Delta = e.AncestorEntry >= 0 ? _entries[e.AncestorEntry].Delta : Quaternion.Identity;

            if (!GroupEnabled(e.Def.Group))
                continue;
            if (!TryPoint(e.Def.KkAimFrom, out var a0) || !TryPoint(e.Def.KkAimTo, out var a1))
                continue;
            if (!QMath.TryNormalize(a1 - a0, out var aim))
                continue;

            // Pole from Koikatsu, or the reference pole carried along by the parent's motion.
            Vector3 pole;
            if (!TryKkPole(e.Def, aim, out pole))
            {
                if (e.HasLastPole && QMath.TryNormalize(QMath.Reject(e.LastPole, aim), out pole)) { }
                else if (!QMath.TryNormalize(QMath.Reject(e.Delta.Rotate(e.PoleRefModel), aim), out pole))
                    pole = QMath.Reject(Vector3.UnitY, aim);
            }
            else
            {
                e.LastPole = pole;
                e.HasLastPole = true;
            }

            var target = QMath.AimPole(e.AimLocal, e.PoleLocal, aim, pole);

            if (e.Def.IsHead && MathF.Abs(_cfg.HeadPitchOffset) > 0.01f && QMath.TryNormalize(pole, out var lateral))
                target = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(lateral, _cfg.HeadPitchOffset * MathF.PI / 180f) * target);

            if (!QMath.IsFinite(target))
                continue;

            if (e.HasLast)
                target = Quaternion.Normalize(Quaternion.Slerp(e.Last, QMath.EnsureSameHemisphere(target, e.Last), alpha));
            e.Last = target;
            e.HasLast = true;

            e.Delta = Quaternion.Normalize(target * Quaternion.Inverse(e.RefRot));
            _targets[i] = target;
            _targetValid[i] = true;
            driven++;
        }
        return driven;
    }

    private bool TryKkPole(RetargetEntry def, Vector3 aim, out Vector3 pole)
    {
        pole = default;
        if (!TryPoint(def.KkPoleA, out var pa) || !TryPoint(def.KkPoleB, out var pb))
            return false;

        Vector3 raw;
        if (def.PoleKind == PoleKind.Hinge)
        {
            if (def.KkHingeC == null || !TryPoint(def.KkHingeC, out var pc))
                return false;
            var lower = pc - pb;
            raw = Vector3.Cross(aim, lower);
            // Nearly straight limb: the bend plane is undefined.
            if (raw.Length() < 0.12f * lower.Length())
                return false;
        }
        else
        {
            raw = pb - pa;
        }
        return QMath.TryNormalize(QMath.Reject(raw, aim), out pole);
    }

    private void WriteBody(hkaPose* pose, int driven)
    {
        if (driven == 0)
            return;

        // Root placement: put the hip-joint midpoint where Koikatsu has it, scaled by leg length.
        if (_haraIndex >= 0 && (_cfg.ApplyRootHeight || _cfg.ApplyRootHorizontal) &&
            TryPoint([Joint.ThighL, Joint.ThighR], out var kkThighMid) &&
            TryLegLength(out var kkLeg) && kkLeg > 1e-4f)
        {
            var ratio = _model!.LegLength / kkLeg;
            var kosiDelta = _kosiEntry >= 0 && _targetValid[_kosiEntry] ? _entries[_kosiEntry].Delta : Quaternion.Identity;
            var desired = kkThighMid * ratio
                          - kosiDelta.Rotate(_thighMidRef - _kosiRefPos)
                          - (_kosiRefPos - _haraRefPos);

            var t = pose->AccessBoneModelSpace(_haraIndex, PropagateOrNot.Propagate);
            var cur = t->Translation.ToVec();
            var next = new Vector3(
                _cfg.ApplyRootHorizontal ? desired.X : cur.X,
                _cfg.ApplyRootHeight ? desired.Y : cur.Y,
                _cfg.ApplyRootHorizontal ? desired.Z : cur.Z);
            if (QMath.IsFinite(next))
                t->Translation = next.ToHk(t->Translation.W);
        }

        for (int i = 0; i < _entries.Count && i < _targets.Length; i++)
        {
            if (!_targetValid[i])
                continue;
            var t = pose->AccessBoneModelSpace(_entries[i].Bone, PropagateOrNot.Propagate);
            t->Rotation = _targets[i].ToHk();
        }
    }

    /// <summary>
    /// Breast physics: Koikatsu sends where each breast tip is and where it would rest without bounce.
    /// The swing between the two is applied on top of j_mune, so the bounce matches Koikatsu while the
    /// FFXIV breast keeps its own shape and size.
    /// </summary>
    private void ApplyBreasts(hkaPose* pose)
    {
        var strength = System.Math.Clamp(_cfg.BreastStrength, 0f, 3f);
        ApplyBreast(pose, _muneL, Joint.BustRootL, Joint.BustTipL, Joint.BustRestL, strength);
        ApplyBreast(pose, _muneR, Joint.BustRootR, Joint.BustTipR, Joint.BustRestR, strength);
    }

    private void ApplyBreast(hkaPose* pose, int bone, Joint root, Joint tip, Joint rest, float strength)
    {
        if (bone < 0 || !_ffValid[(int)root] || !_ffValid[(int)tip] || !_ffValid[(int)rest])
            return;
        if (!QMath.TryNormalize(_ff[(int)rest] - _ff[(int)root], out var a, 1e-6f) ||
            !QMath.TryNormalize(_ff[(int)tip] - _ff[(int)root], out var b, 1e-6f))
            return;

        var swing = QMath.FromTo(a, b);
        if (strength != 1f)
            swing = Quaternion.Slerp(Quaternion.Identity, swing, strength);
        swing = QMath.ClampAngle(swing, 50f * MathF.PI / 180f);
        if (!QMath.IsFinite(swing))
            return;

        var t = pose->AccessBoneModelSpace(bone, PropagateOrNot.Propagate);
        t->Rotation = Quaternion.Normalize(swing * t->Rotation.ToQuat()).ToHk();
    }

    private bool TryLegLength(out float length)
    {
        length = 0f;
        int n = 0;
        foreach (var (a, b, c) in new[] { (Joint.ThighL, Joint.KneeL, Joint.AnkleL), (Joint.ThighR, Joint.KneeR, Joint.AnkleR) })
        {
            if (!_ffValid[(int)a] || !_ffValid[(int)b] || !_ffValid[(int)c]) continue;
            length += Vector3.Distance(_ff[(int)a], _ff[(int)b]) + Vector3.Distance(_ff[(int)b], _ff[(int)c]);
            n++;
        }
        if (n == 0) return false;
        length /= n;
        return true;
    }

    /// <summary>Snap face, hair, tail etc. partial roots back onto the body bones they hang from.</summary>
    private void ReparentPartials(RenderSkeleton* skel)
    {
        var pose0 = SkeletonModel.GetPose(skel, 0);
        if (pose0 == null) return;

        for (int pi = 1; pi < _model!.Partials.Count; pi++)
        {
            var part = _model.Partials[pi];
            if (part.Attachments.Count == 0) continue;
            var pose = SkeletonModel.GetPose(skel, pi);
            if (pose == null) continue;

            foreach (var (root, bodyParent) in part.Attachments)
            {
                var src = pose0->AccessBoneModelSpace(bodyParent, PropagateOrNot.DontPropagate);
                var dst = pose->AccessBoneModelSpace(root, PropagateOrNot.Propagate);
                dst->Translation = src->Translation;
                dst->Rotation = src->Rotation;
                dst->Scale = src->Scale;
            }
        }
    }

    /// <summary>Touch every bone so Havok recomputes anything we dirtied before the frame renders.</summary>
    private void SyncAll(RenderSkeleton* skel)
    {
        for (int pi = 0; pi < _model!.Partials.Count; pi++)
        {
            var pose = SkeletonModel.GetPose(skel, pi);
            if (pose == null) continue;
            int n = pose->Skeleton->Bones.Length;
            for (int i = 0; i < n; i++)
                pose->AccessBoneModelSpace(i, PropagateOrNot.DontPropagate);
        }
    }

    private static Joint[] BuildMirrorMap()
    {
        var map = new Joint[(int)Joint.Count];
        for (int i = 0; i < map.Length; i++)
            map[i] = (Joint)i;

        void Swap(Joint a, Joint b) { map[(int)a] = b; map[(int)b] = a; }

        Swap(Joint.ShoulderL, Joint.ShoulderR);
        Swap(Joint.UpperArmL, Joint.UpperArmR);
        Swap(Joint.ElbowL, Joint.ElbowR);
        Swap(Joint.WristL, Joint.WristR);
        Swap(Joint.ThighL, Joint.ThighR);
        Swap(Joint.KneeL, Joint.KneeR);
        Swap(Joint.AnkleL, Joint.AnkleR);
        Swap(Joint.FootL, Joint.FootR);
        Swap(Joint.ToeL, Joint.ToeR);
        Swap(Joint.EyeL, Joint.EyeR);
        Swap(Joint.TesticleL, Joint.TesticleR);
        Swap(Joint.BustRootL, Joint.BustRootR);
        Swap(Joint.BustTipL, Joint.BustTipR);
        Swap(Joint.BustRestL, Joint.BustRestR);
        for (int k = 0; k < 15; k++)
            Swap((Joint)((int)Joint.ThumbL1 + k), (Joint)((int)Joint.ThumbR1 + k));
        return map;
    }
}
