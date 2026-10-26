using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using FFXIVClientStructs.Havok.Animation.Rig;
using Limber.Koi.Mathx;
using Limber.Koi.Net;
using static FFXIVClientStructs.Havok.Animation.Rig.hkaPose;
using RenderSkeleton = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Skeleton;

namespace Limber.Koi.Posing;

/// <summary>
/// Drives FFXIV's bone-based face from Koikatsu's blend-shape face.
///
/// Koikatsu expresses faces as named blend-shape patterns; FFXIV faces are bones. There is no
/// one-to-one conversion, so the face is driven three ways:
///   1. Gaze  - eye bones rotate toward Koikatsu's look target (procedural, works out of the box).
///   2. Jaw   - j_f_ago opens with Koikatsu's live mouth openness (procedural).
///   3. Presets - FFXIV face poses captured in-game ("blink", "mouth_open", or any name mapped
///      to a Koikatsu eye/mouth/eyebrow pattern id) blended by weight. Presets are stored per
///      tribe+sex because face rigs differ between races.
/// </summary>
public sealed unsafe class FaceDriver
{
    public const string BlinkPreset = "blink";
    public const string MouthOpenPreset = "mouth_open";

    private readonly Configuration _cfg;
    /// <summary>Guards the preset library and pattern maps, which the UI edits while the hook reads them.</summary>
    public static readonly object PresetLock = new();

    private readonly ConcurrentDictionary<string, float> _weights = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float> _targets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Quaternion> _combinedRot = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Vector3> _combinedPos = new(StringComparer.Ordinal);
    private float _sx = -1f, _sz = 1f;

    public IReadOnlyDictionary<string, float> Weights => _weights;

    private bool _mirrored;
    private readonly AutoFace _auto;

    public FaceDriver(Configuration cfg)
    {
        _cfg = cfg;
        _auto = new AutoFace(cfg);
    }

    public void Reset()
    {
        _weights.Clear();
        _auto.Reset();
    }
    public void ResetSmoothing() { /* weights decay naturally; nothing to snap */ }

    public void SetAxisSigns(float sx, float sz, bool mirrored)
    {
        _sx = sx;
        _sz = sz;
        _mirrored = mirrored;
    }

    public static string RaceKey(byte tribe, byte sex) => $"{tribe}_{sex}";

    public Dictionary<string, FacePreset> PresetsFor(string raceKey)
    {
        if (!_cfg.FacePresets.TryGetValue(raceKey, out var dict))
        {
            dict = new Dictionary<string, FacePreset>(StringComparer.Ordinal);
            _cfg.FacePresets[raceKey] = dict;
        }
        return dict;
    }

    // ------------------------------------------------------------------------------------
    // Capture

    /// <summary>Store the actor's current face as a preset. Returns number of bones captured.</summary>
    public int Capture(SkeletonModel model, RenderSkeleton* skel, string raceKey, string presetName)
    {
        if (model.FacePartial < 0)
            return 0;
        var part = model.Partials[model.FacePartial];
        var pose = SkeletonModel.GetPose(skel, model.FacePartial);
        if (pose == null)
            return 0;

        var preset = new FacePreset();
        int n = part.Names.Length;
        for (int i = 0; i < n; i++)
        {
            int parent = part.Parents[i];
            if (parent < 0)
                continue; // root follows the head; never part of an expression

            QMath.Read(pose->AccessBoneModelSpace(parent, PropagateOrNot.DontPropagate), out var pp, out var pr, out var ps);
            QMath.Read(pose->AccessBoneModelSpace(i, PropagateOrNot.DontPropagate), out var cp, out var cr, out _);
            QMath.Decompose(pp, pr, ps, cp, cr, out var lp, out var lr);

            var dr = Quaternion.Normalize(Quaternion.Inverse(part.RefLocalRot[i]) * lr);
            if (dr.W < 0) dr = -dr;
            var dp = lp - part.RefLocalPos[i];

            var angle = 2f * MathF.Acos(System.Math.Clamp(dr.W, -1f, 1f));
            if (angle < 0.005f && dp.Length() < 0.0005f)
                continue;

            preset.Bones[part.Names[i]] = new FaceBoneDelta
            {
                Rx = dr.X, Ry = dr.Y, Rz = dr.Z, Rw = dr.W,
                Px = dp.X, Py = dp.Y, Pz = dp.Z,
            };
        }

        lock (PresetLock)
            PresetsFor(raceKey)[presetName] = preset;
        return preset.Bones.Count;
    }

    // ------------------------------------------------------------------------------------
    // Apply

    public void Apply(SkeletonModel model, RenderSkeleton* skel, PoseFrame frame, string raceKey, float dt, float[]? neutral)
    {
        // Face tracking off: leave every face bone to the game / other posing tools.
        if (!_cfg.DriveFace || model.FacePartial < 0)
            return;
        var part = model.Partials[model.FacePartial];
        var pose = SkeletonModel.GetPose(skel, model.FacePartial);
        if (pose == null)
            return;

        switch (_cfg.ExpressionMode)
        {
            case ExpressionMode.Automatic:
                // Full mirroring from Koikatsu's face mesh; falls back to jaw-only if the sender has no face data.
                if (!_auto.Apply(model, skel, part, pose, frame, neutral, _mirrored) && _cfg.DriveJaw && frame.HasMouthOpen)
                    ApplyJaw(model, skel, part, pose, frame.MouthOpen);
                break;

            case ExpressionMode.CapturedPresets:
                lock (PresetLock)
                    ApplyPresets(part, pose, frame, raceKey, dt);
                if (_cfg.DriveJaw && frame.HasMouthOpen)
                    ApplyJaw(model, skel, part, pose, frame.MouthOpen);
                break;

            case ExpressionMode.Off:
            default:
                break; // leave the actor's own expression alone
        }

        if (_cfg.DriveGaze && frame.HasGaze)
            ApplyGaze(model, skel, part, pose, frame);
    }

    private void ApplyPresets(SkeletonModel.Partial part, hkaPose* pose, PoseFrame frame, string raceKey, float dt)
    {
        if (!_cfg.FacePresets.TryGetValue(raceKey, out var presets) || presets.Count == 0)
            return;

        // Target weights for this frame.
        _targets.Clear();
        float eyeClosed;
        var open = frame.HasEyeOpen ? frame.EyeOpen * frame.EyesOpenMax : frame.EyesOpenMax;
        eyeClosed = _cfg.InvertEyeOpen ? open : 1f - open;
        _targets[BlinkPreset] = System.Math.Clamp(eyeClosed, 0f, 1f);
        if (frame.HasMouthOpen)
            _targets[MouthOpenPreset] = frame.MouthOpen;

        if (frame.HasPatterns)
        {
            AddPattern(_cfg.EyesPatternMap, frame.EyesPattern);
            AddPattern(_cfg.MouthPatternMap, frame.MouthPattern);
            AddPattern(_cfg.EyebrowPatternMap, frame.EyebrowPattern);
        }

        // Smooth toward targets (blink stays snappy).
        var rate = 1f - MathF.Exp(-MathF.Max(0.1f, _cfg.ExpressionBlendSpeed) * dt);
        foreach (var name in presets.Keys)
        {
            _targets.TryGetValue(name, out var target);
            _weights.TryGetValue(name, out var w);
            w = name == BlinkPreset ? target : w + (target - w) * rate;
            _weights[name] = w;
        }

        // Combine all active presets per bone, then rebuild those bones from the reference face.
        var combinedRot = _combinedRot;
        var combinedPos = _combinedPos;
        combinedRot.Clear();
        combinedPos.Clear();
        foreach (var (name, preset) in presets)
        {
            if (!_weights.TryGetValue(name, out var w) || w < 0.001f)
                continue;
            foreach (var (bone, d) in preset.Bones)
            {
                var q = Quaternion.Slerp(Quaternion.Identity, Quaternion.Normalize(new Quaternion(d.Rx, d.Ry, d.Rz, d.Rw)), w);
                combinedRot[bone] = combinedRot.TryGetValue(bone, out var prev) ? Quaternion.Normalize(prev * q) : q;
                combinedPos[bone] = (combinedPos.TryGetValue(bone, out var pp) ? pp : Vector3.Zero) + new Vector3(d.Px, d.Py, d.Pz) * w;
            }
        }

        // Every bone that any preset touches is owned by the presets, so an inactive preset
        // returns its bones to the neutral reference face instead of leaving the game's expression.
        for (int i = 0; i < part.Names.Length; i++)
        {
            int parent = part.Parents[i];
            if (parent < 0)
                continue;
            var name = part.Names[i];
            if (!IsOwned(presets, name))
                continue;

            combinedRot.TryGetValue(name, out var dr);
            if (dr == default) dr = Quaternion.Identity;
            combinedPos.TryGetValue(name, out var dp);

            var localRot = Quaternion.Normalize(part.RefLocalRot[i] * dr);
            var localPos = part.RefLocalPos[i] + dp;

            QMath.Read(pose->AccessBoneModelSpace(parent, PropagateOrNot.DontPropagate), out var pp, out var pr, out var ps);
            QMath.Compose(pp, pr, ps, localPos, localRot, part.RefLocalScale[i], out var mp, out var mr, out _);

            var t = pose->AccessBoneModelSpace(i, PropagateOrNot.Propagate);
            t->Translation = mp.ToHk(t->Translation.W);
            t->Rotation = mr.ToHk();
        }
    }

    private static bool IsOwned(Dictionary<string, FacePreset> presets, string bone)
    {
        foreach (var p in presets.Values)
            if (p.Bones.ContainsKey(bone))
                return true;
        return false;
    }

    private void AddPattern(Dictionary<int, string> map, int pattern)
    {
        if (map.TryGetValue(pattern, out var preset) && !string.IsNullOrWhiteSpace(preset))
            _targets[preset] = 1f;
    }

    private void ApplyJaw(SkeletonModel model, RenderSkeleton* skel, SkeletonModel.Partial part, hkaPose* pose, float mouthOpen)
    {
        int jaw = Array.IndexOf(part.Names, "j_f_ago");
        int eyeL = Array.IndexOf(part.Names, "j_f_eye_l");
        int eyeR = Array.IndexOf(part.Names, "j_f_eye_r");
        if (jaw < 0 || eyeL < 0 || eyeR < 0)
            return;
        if (!model.TryFind("j_kao", out var hp, out var head) || !model.TryFind("j_kubi", out var np, out var neck) || hp != 0 || np != 0)
            return;
        var body = SkeletonModel.GetPose(skel, 0);
        if (body == null)
            return;

        var l = pose->AccessBoneModelSpace(eyeL, PropagateOrNot.DontPropagate)->Translation.ToVec();
        var r = pose->AccessBoneModelSpace(eyeR, PropagateOrNot.DontPropagate)->Translation.ToVec();
        var headPos = body->AccessBoneModelSpace(head, PropagateOrNot.DontPropagate)->Translation.ToVec();
        var neckPos = body->AccessBoneModelSpace(neck, PropagateOrNot.DontPropagate)->Translation.ToVec();

        if (!QMath.TryNormalize(l - r, out var axis) || !QMath.TryNormalize(headPos - neckPos, out var up))
            return;
        if (!QMath.TryNormalize(QMath.Reject((l + r) * 0.5f - headPos, axis), out var fwd))
            return;

        // Pick the rotation direction that moves the chin down, whatever the rig's axis conventions.
        var probe = Quaternion.CreateFromAxisAngle(axis, 0.2f).Rotate(fwd);
        var sign = Vector3.Dot(probe, up) < Vector3.Dot(fwd, up) ? 1f : -1f;
        var angle = sign * System.Math.Clamp(mouthOpen, 0f, 1f) * _cfg.JawMaxAngle * MathF.PI / 180f;

        var t = pose->AccessBoneModelSpace(jaw, PropagateOrNot.Propagate);
        var rot = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(axis, angle) * t->Rotation.ToQuat());
        t->Rotation = rot.ToHk();
    }

    private void ApplyGaze(SkeletonModel model, RenderSkeleton* skel, SkeletonModel.Partial part, hkaPose* pose, PoseFrame frame)
    {
        // Koikatsu's gaze is an absolute direction, so the FFXIV eyes look the same way in the scene.
        var g = frame.Gaze;
        if (!QMath.TryNormalize(new Vector3(g.X * _sx, g.Y, g.Z * _sz), out var gaze))
            return;

        if (!model.TryFind("j_kao", out var hp, out var head) || hp != 0)
            return;
        var body = SkeletonModel.GetPose(skel, 0);
        if (body == null)
            return;

        // How far the head has turned from the reference pose; the reference face looks straight ahead.
        var headRot = body->AccessBoneModelSpace(head, PropagateOrNot.DontPropagate)->Rotation.ToQuat();
        var headDelta = Quaternion.Normalize(headRot * Quaternion.Inverse(model.Partials[0].RefModelRot[head]));
        var faceForward = headDelta.Rotate(new Vector3(0, 0, model.ForwardSign));

        var delta = QMath.ClampAngle(QMath.FromTo(faceForward, gaze), _cfg.GazeMaxAngle * MathF.PI / 180f);

        foreach (var name in new[] { "j_f_eye_l", "j_f_eye_r" })
        {
            int idx = Array.IndexOf(part.Names, name);
            if (idx < 0) continue;
            var neutral = Quaternion.Normalize(headDelta * part.RefModelRot[idx]);
            var t = pose->AccessBoneModelSpace(idx, PropagateOrNot.Propagate);
            t->Rotation = Quaternion.Normalize(delta * neutral).ToHk();
        }
    }
}
