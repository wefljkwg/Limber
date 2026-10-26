using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Limber.Protocol;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace Limber.Video;

/// <summary>
/// Finds people in video frames and turns each one's body (and optionally hands) into protocol joint positions.
///
/// Models (Apache-2.0, from the OpenCV model zoo; all MediaPipe models converted to ONNX):
///  - person_detection_mediapipe_2023mar.onnx: finds people (224x224, RGB in [-1, 1]); gives each one's hip
///    centre and a "full body" point, which say where to crop and which way is up.
///  - pose_estimation_mediapipe_2023mar.onnx: BlazePose full body, 256x256 RGB in [0,1]. 39 landmarks
///    (33 body + 6 helpers), each x, y (crop pixels), z, visibility, presence; a confidence; and 39 metric 3D
///    "world" landmarks centred on the hips. It predicts every point, including ones that are hidden or out of
///    the picture, from the rest of the body.
///  - handpose_estimation_mediapipe_2023feb.onnx: 224x224 RGB in [0,1]. 21 landmarks, confidence, handedness,
///    21 metric 3D world landmarks.
///
/// Like MediaPipe: the detector finds a person once, then each frame is cropped around where the body was in
/// the previous one; the detector only runs again when someone is lost.
///
/// Hidden or cut-off limbs: the model's guesses are used, but kept at the person's own limb lengths (learned
/// from frames where the limbs were clearly visible) and smoothed more the less sure the model is.
/// </summary>
public sealed class PoseTracker : IDisposable
{
    public const string PoseModelFile = "pose_estimation_mediapipe_2023mar.onnx";
    public const string HandModelFile = "handpose_estimation_mediapipe_2023feb.onnx";
    public const string DetectorModelFile = "person_detection_mediapipe_2023mar.onnx";

    private const int PoseSize = 256;
    private const int HandSize = 224;
    private const int DetectorSize = 224;
    private const float PoseConfidence = 0.5f;
    private const float HandConfidence = 0.5f;
    private const float DetectorConfidence = 0.5f;
    private const float SureVisibility = 0.6f;

    private readonly InferenceSession? _pose;
    private readonly InferenceSession? _hand;
    private readonly RtmBody? _rtm;
    private readonly InferenceSession? _detector;
    private readonly string _poseInput = "";
    private readonly string _handInput = "";
    private readonly string _detectorInput = "";
    private readonly bool _poseNchw, _handNchw, _detectorNchw;
    private Vector2[]? _anchors;

    /// <summary>One person being followed, and what's been learned about them.</summary>
    public sealed class Slot
    {
        // Where to look in the next frame.
        public bool HasRoi;
        public Vector2 Center;
        public float Side, Angle;

        // Where to look when they're lost: their last known spot, else this part of the picture (0..1, left to right).
        public float SeedFrom = 0f, SeedTo = 1f;
        public bool HasLast;
        public Vector2 LastCenter;

        // Learned limb lengths (metres), smoothed floor height, and last output for steadying guessed points.
        public readonly float[] BoneLength = new float[Bones.Length];
        public readonly bool[] HasBone = new bool[Bones.Length];
        public float Floor;
        public bool HasFloor;
        public readonly Vector3[] Previous = new Vector3[(int)Joint.Count];
        public readonly bool[] HasPrevious = new bool[(int)Joint.Count];

        // Accurate model only: how its depth compares to picture distances, and the person's size in pixels per metre.
        public float DepthRatio = 1f;
        public int DepthSamples;
        public float PixelsPerMeter;

        /// <summary>Lost them: search again (what was learned about their body is kept).</summary>
        public void Reset()
        {
            HasRoi = false;
            Array.Clear(HasPrevious); // don't pull a re-found person toward a pose from seconds ago
        }

        /// <summary>New video or a jump in time: start completely fresh.</summary>
        public void Forget()
        {
            HasRoi = false;
            HasLast = false;
            HasFloor = false;
            PixelsPerMeter = 0f;
            Array.Clear(HasBone);
            Array.Clear(HasPrevious);
        }
    }

    // Limb segments whose length is kept constant: parent joint, child joint, and the joints that move with the child.
    private static readonly (Joint Parent, Joint Child, Joint[] Carried)[] Bones =
    [
        (Joint.UpperArmL, Joint.ElbowL, [Joint.WristL, .. Fingers(Joint.ThumbL1)]),
        (Joint.ElbowL, Joint.WristL, Fingers(Joint.ThumbL1)),
        (Joint.UpperArmR, Joint.ElbowR, [Joint.WristR, .. Fingers(Joint.ThumbR1)]),
        (Joint.ElbowR, Joint.WristR, Fingers(Joint.ThumbR1)),
        (Joint.ThighL, Joint.KneeL, [Joint.AnkleL, Joint.FootL, Joint.ToeL]),
        (Joint.KneeL, Joint.AnkleL, [Joint.FootL, Joint.ToeL]),
        (Joint.ThighR, Joint.KneeR, [Joint.AnkleR, Joint.FootR, Joint.ToeR]),
        (Joint.KneeR, Joint.AnkleR, [Joint.FootR, Joint.ToeR]),
    ];

    private static Joint[] Fingers(Joint first) => Enumerable.Range((int)first, 15).Select(i => (Joint)i).ToArray();

    // Which body-model landmark tells how sure we are about each limb joint.
    private static readonly (Joint Joint, int Landmark)[] LimbVisibility =
    [
        (Joint.ElbowL, 13), (Joint.WristL, 15), (Joint.ElbowR, 14), (Joint.WristR, 16),
        (Joint.KneeL, 25), (Joint.AnkleL, 27), (Joint.FootL, 27), (Joint.ToeL, 31),
        (Joint.KneeR, 26), (Joint.AnkleR, 28), (Joint.FootR, 28), (Joint.ToeR, 32),
    ];

    private readonly Slot _single = new();

    public bool HasDetector => _detector != null;

    /// <summary>Which body model is running, and where (for the status line).</summary>
    public string Description => _rtm != null ? $"accurate model on the {_rtm.Backend}" : "fast model";

    /// <summary>
    /// <paramref name="accurate"/>: RTMW3D (whole body incl. hands, with depth; best on a graphics card) instead
    /// of MediaPipe. <paramref name="gpu"/>: run it on the graphics card when possible.
    /// </summary>
    public PoseTracker(string modelDir, bool withHands, int threads, bool accurate = false, bool gpu = true)
    {
        using var options = new SessionOptions
        {
            IntraOpNumThreads = Math.Clamp(threads, 1, 4),
            InterOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        // Don't let idle worker threads spin and steal CPU time from the game.
        options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");

        if (accurate)
        {
            // Its hands come with the body, so no separate hand model.
            _rtm = new RtmBody(Path.Combine(modelDir, RtmBody.ModelFile), gpu, threads);
        }
        else
        {
            _pose = new InferenceSession(Path.Combine(modelDir, PoseModelFile), options);
            (_poseInput, _poseNchw) = DescribeInput(_pose);
        }

        if (withHands && !accurate)
        {
            _hand = new InferenceSession(Path.Combine(modelDir, HandModelFile), options);
            (_handInput, _handNchw) = DescribeInput(_hand);
        }

        // The detector is optional: without it, lost people are searched for in their part of the picture.
        var detectorPath = Path.Combine(modelDir, DetectorModelFile);
        if (File.Exists(detectorPath))
        {
            _detector = new InferenceSession(detectorPath, options);
            (_detectorInput, _detectorNchw) = DescribeInput(_detector);
        }
    }

    public static bool ModelsPresent(string modelDir, bool withHands, out string? missing, bool accurate = false)
    {
        missing = null;
        var files = accurate ? new[] { RtmBody.ModelFile }
            : withHands ? new[] { PoseModelFile, HandModelFile } : new[] { PoseModelFile };
        foreach (var f in files)
        {
            if (!File.Exists(Path.Combine(modelDir, f)))
            {
                missing = f;
                return false;
            }
        }
        return true;
    }

    private static (string Name, bool Nchw) DescribeInput(InferenceSession session)
    {
        var (name, meta) = session.InputMetadata.First();
        var dims = meta.Dimensions;
        bool nchw = dims.Length == 4 && dims[1] == 3;
        return (name, nchw);
    }

    /// <summary>Start fresh (new video).</summary>
    public void ResetTracking() => _single.Forget();

    /// <summary>Track one person.</summary>
    public bool Process(Mat bgr, bool hands, bool guessHidden, MotionFrame output)
    {
        var found = new bool[1];
        ProcessPeople(bgr, [_single], hands, guessHidden, [output], found);
        return found[0];
    }

    /// <summary>
    /// Track several people, one slot each. Lost slots are found again with the person detector (near where they
    /// were last, or in their own part of the picture at the start). If two slots end up on the same person, the one
    /// that had to search lets go, so the two never swap characters.
    /// </summary>
    public void ProcessPeople(Mat bgr, Slot[] slots, bool hands, bool guessHidden, MotionFrame[] outputs, bool[] found)
    {
        for (int i = 0; i < slots.Length; i++)
        {
            found[i] = false;
            Array.Clear(outputs[i].V);
        }
        int w = bgr.Width, h = bgr.Height;
        if (w < 8 || h < 8)
            return;

        // 1. Find lost people.
        var wasTracking = slots.Select(s => s.HasRoi).ToArray();
        if (slots.Any(s => !s.HasRoi))
            Acquire(bgr, slots);

        // 2. Track everyone who has somewhere to look.
        for (int i = 0; i < slots.Length; i++)
        {
            if (!slots[i].HasRoi)
                continue;
            found[i] = Track(bgr, slots[i], hands, guessHidden, outputs[i]);
            if (!found[i])
                continue;

            for (int k = 0; k < i; k++)
            {
                if (!found[k] || !slots[k].HasRoi || !slots[i].HasRoi)
                    continue;
                var distance = Vector2.Distance(slots[i].Center, slots[k].Center);
                if (distance < 0.3f * MathF.Max(slots[i].Side, slots[k].Side))
                {
                    int drop = !wasTracking[i] || wasTracking[k] ? i : k;
                    found[drop] = false;
                    Array.Clear(outputs[drop].V);
                    slots[drop].Reset();
                    break;
                }
            }
        }
    }

    // ---- Finding people

    private readonly record struct Detection(float Score, Vector2 Hip, Vector2 Up, Vector2 BoxMin, Vector2 BoxMax);

    private void Acquire(Mat bgr, Slot[] slots)
    {
        int w = bgr.Width, h = bgr.Height;
        var detections = _detector != null ? Detect(bgr) : null;

        if (detections == null)
        {
            // No detector: look at each lost slot's own part of the picture.
            foreach (var slot in slots.Where(s => !s.HasRoi))
            {
                float from = Math.Clamp(slot.SeedFrom, 0f, 1f), to = Math.Clamp(slot.SeedTo, from + 0.05f, 1f);
                slot.Center = slot.HasLast ? slot.LastCenter : new Vector2(w * (from + to) * 0.5f, h * 0.5f);
                slot.Side = Math.Max(w * (to - from), h);
                slot.Angle = 0f;
                slot.HasRoi = true;
            }
            return;
        }

        // Ignore people someone is already following.
        var free = detections.Where(d => !slots.Any(s => s.HasRoi &&
            Vector2.Distance(s.Center, d.Hip) < 0.5f * Math.Max(s.Side, (d.BoxMax - d.BoxMin).Length()))).ToList();

        foreach (var slot in slots.Where(s => !s.HasRoi))
        {
            if (free.Count == 0)
                break;

            Detection pick;
            if (slot.HasLast)
            {
                // Found again: whoever is nearest to where they were.
                pick = free.OrderBy(d => Vector2.Distance(d.Hip, slot.LastCenter)).First();
            }
            else
            {
                // First time: the best one in this slot's part of the picture, else the best one anywhere.
                var inBand = free.Where(d => d.Hip.X >= slot.SeedFrom * w && d.Hip.X <= slot.SeedTo * w).ToList();
                pick = (inBand.Count > 0 ? inBand : free).OrderByDescending(d => d.Score).First();
            }
            free.Remove(pick);

            var axis = pick.Up - pick.Hip;
            var len = axis.Length();
            if (len < 4f || !float.IsFinite(len))
                continue;
            slot.Center = pick.Hip;
            slot.Side = Math.Max(32f, len * 2f * 1.25f);
            slot.Angle = AngleUp(axis);
            slot.HasRoi = true;
        }
    }

    /// <summary>People in the frame (null if the detector's outputs aren't understood).</summary>
    private List<Detection>? Detect(Mat bgr)
    {
        int w = bgr.Width, h = bgr.Height;
        float side = Math.Max(w, h);
        var center = new Vector2(w * 0.5f, h * 0.5f);

        // Letterboxed to a square, grey padding (0 in the model's [-1, 1] range).
        var input = Crop(bgr, center, side, 0f, DetectorSize, _detectorNchw, minusOneToOne: true, border: 127.5);
        var tensor = new DenseTensor<float>(input, _detectorNchw ? new[] { 1, 3, DetectorSize, DetectorSize } : new[] { 1, DetectorSize, DetectorSize, 3 });
        using var results = _detector!.Run(new[] { NamedOnnxValue.CreateFromTensor(_detectorInput, tensor) });

        float[]? boxes = null, scores = null;
        int count = 0, stride = 0;
        foreach (var r in results)
        {
            var t = r.AsTensor<float>();
            var dims = t.Dimensions.ToArray();
            if (dims.Length != 3)
                continue;
            if (dims[2] >= 12 && boxes == null) { boxes = t.ToArray(); count = dims[1]; stride = dims[2]; }
            else if (dims[2] == 1 && scores == null) scores = t.ToArray();
        }
        if (boxes == null || scores == null || scores.Length != count)
            return null;

        _anchors ??= BuildAnchors();
        if (_anchors.Length != count)
            return null;

        var found = new List<Detection>();
        for (int i = 0; i < count; i++)
        {
            var score = Sigmoid(Math.Clamp(scores[i], -100f, 100f));
            if (score < DetectorConfidence)
                continue;
            var a = _anchors[i];
            int o = i * stride;
            Vector2 Norm(int k) => new(boxes[o + k] / DetectorSize + a.X, boxes[o + k + 1] / DetectorSize + a.Y);
            Vector2 ToImage(Vector2 n) => (n - new Vector2(0.5f)) * side + center;

            var c = Norm(0);
            var size = new Vector2(boxes[o + 2], boxes[o + 3]) / DetectorSize;
            var hip = ToImage(Norm(4));
            var up = ToImage(Norm(6));
            found.Add(new Detection(score, hip, up, ToImage(c - size * 0.5f), ToImage(c + size * 0.5f)));
        }

        // Keep the best of overlapping boxes.
        var kept = new List<Detection>();
        foreach (var d in found.OrderByDescending(d => d.Score))
        {
            if (kept.All(k => Iou(k, d) < 0.3f))
                kept.Add(d);
        }
        return kept;
    }

    private static float Iou(Detection a, Detection b)
    {
        var min = Vector2.Max(a.BoxMin, b.BoxMin);
        var max = Vector2.Min(a.BoxMax, b.BoxMax);
        var inter = MathF.Max(0, max.X - min.X) * MathF.Max(0, max.Y - min.Y);
        var areaA = (a.BoxMax.X - a.BoxMin.X) * (a.BoxMax.Y - a.BoxMin.Y);
        var areaB = (b.BoxMax.X - b.BoxMin.X) * (b.BoxMax.Y - b.BoxMin.Y);
        var union = areaA + areaB - inter;
        return union > 0 ? inter / union : 0f;
    }

    /// <summary>
    /// MediaPipe's pose-detector anchors (SSD, 224x224, strides 8/16/32/32/32, two anchors per layer, fixed size):
    /// 28x28x2 + 14x14x2 + 7x7x6 = 2254 anchor centres, in order.
    /// </summary>
    private static Vector2[] BuildAnchors()
    {
        int[] strides = [8, 16, 32, 32, 32];
        var anchors = new List<Vector2>();
        int layer = 0;
        while (layer < strides.Length)
        {
            int last = layer;
            while (last + 1 < strides.Length && strides[last + 1] == strides[layer])
                last++;
            int perCell = (last - layer + 1) * 2;
            int size = (int)Math.Ceiling(DetectorSize / (double)strides[layer]);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    for (int k = 0; k < perCell; k++)
                        anchors.Add(new Vector2((x + 0.5f) / size, (y + 0.5f) / size));
            layer = last + 1;
        }
        return anchors.ToArray();
    }

    // ---- Following one person

    private bool Track(Mat bgr, Slot slot, bool hands, bool guessHidden, MotionFrame output)
    {
        Vector2[] image;
        Vector3[] world;
        float[] visibility;
        Vector3[]?[] rtmHands = [null, null];
        if (_rtm != null)
        {
            // Accurate model: also moves the slot's crop to this body for the next frame.
            if (!RunRtm(bgr, slot, out image, out world, out visibility, rtmHands))
            {
                slot.Reset();
                return false;
            }
        }
        else if (!RunPose(bgr, slot, out image, out world, out visibility))
        {
            slot.Reset();
            return false;
        }

        // Next frame: crop around this body (helper landmarks 33 = hip centre, 34 = body size / direction).
        var axis = image[34] - image[33];
        var len = axis.Length();
        if (_rtm != null)
        {
            // Already done by RunRtm.
        }
        else if (len > 4f && float.IsFinite(len))
        {
            slot.Center = image[33];
            slot.Side = Math.Max(32f, len * 2f * 1.25f);
            slot.Angle = AngleUp(axis);
            slot.HasRoi = true;
            slot.LastCenter = slot.Center;
            slot.HasLast = true;
        }
        else
        {
            slot.Reset();
        }

        // ---- Body joints (in MediaPipe's camera-aligned world space: x right, y down, z away from the camera)
        Vector3 W(int i) => world[i];
        var hipL = W(23); var hipR = W(24);
        var shL = W(11); var shR = W(12);
        var hip = (hipL + hipR) * 0.5f;
        var sh = (shL + shR) * 0.5f;
        var ear = (W(7) + W(8)) * 0.5f;

        var j = new Vector3[(int)Joint.Count];
        var v = new bool[(int)Joint.Count];
        void Set(Joint joint, Vector3 p, bool ok = true) { j[(int)joint] = p; v[(int)joint] = ok; }
        float guessFloor = _rtm != null ? RtmGuessFloor : 0f;
        bool Seen(params int[] ids) => ids.All(i => visibility[i] >= (guessHidden ? guessFloor : 0.35f));

        Set(Joint.Hips, hip + (sh - hip) * 0.08f);
        Set(Joint.Spine1, hip + (sh - hip) * 0.30f);
        Set(Joint.Spine2, hip + (sh - hip) * 0.55f);
        Set(Joint.Spine3, hip + (sh - hip) * 0.80f);
        Set(Joint.Neck, sh + (ear - sh) * 0.25f);
        Set(Joint.Head, sh + (ear - sh) * 0.80f);
        Set(Joint.EyeL, W(2));
        Set(Joint.EyeR, W(5));

        // MediaPipe's "left" is the person's own left.
        Set(Joint.ShoulderL, sh + (shL - sh) * 0.3f);
        Set(Joint.UpperArmL, shL);
        Set(Joint.ElbowL, W(13), Seen(13));
        Set(Joint.WristL, W(15), Seen(13, 15));
        Set(Joint.ShoulderR, sh + (shR - sh) * 0.3f);
        Set(Joint.UpperArmR, shR);
        Set(Joint.ElbowR, W(14), Seen(14));
        Set(Joint.WristR, W(16), Seen(14, 16));

        Set(Joint.ThighL, hipL);
        Set(Joint.KneeL, W(25), Seen(25));
        Set(Joint.AnkleL, W(27), Seen(25, 27));
        Set(Joint.FootL, W(27), Seen(25, 27));
        Set(Joint.ToeL, W(31), Seen(27, 31));
        Set(Joint.ThighR, hipR);
        Set(Joint.KneeR, W(26), Seen(26));
        Set(Joint.AnkleR, W(28), Seen(26, 28));
        Set(Joint.FootR, W(28), Seen(26, 28));
        Set(Joint.ToeR, W(32), Seen(28, 32));

        // Hands: from the hand model when asked and found, else just the palm from the body model.
        for (int side = 0; side < 2; side++)
        {
            bool left = side == 0;
            int wrist = left ? 15 : 16, pinky = left ? 17 : 18, index = left ? 19 : 20, thumb = left ? 21 : 22;
            var first = left ? Joint.ThumbL1 : Joint.ThumbR1;
            if (!Seen(wrist))
                continue;

            Vector3[]? handWorld = hands ? rtmHands[side] : null;
            if (hands && (handWorld != null || (_hand != null && visibility[wrist] >= 0.35f &&
                RunHand(bgr, image[wrist], (image[index] + image[pinky]) * 0.5f, out handWorld))) && handWorld != null)
            {
                var origin = W(wrist) - handWorld[0];
                int[][] fingers = [[1, 2, 3], [5, 6, 7], [9, 10, 11], [13, 14, 15], [17, 18, 19]];
                for (int f = 0; f < 5; f++)
                    for (int k = 0; k < 3; k++)
                        Set((Joint)((int)first + f * 3 + k), handWorld[fingers[f][k]] + origin);
            }
            else
            {
                Set(first, W(thumb));
                Set((Joint)((int)first + 3), W(index));
                Set((Joint)((int)first + 6), (W(index) + W(pinky)) * 0.5f);
                Set((Joint)((int)first + 9), W(index) * 0.33f + W(pinky) * 0.67f);
                Set((Joint)((int)first + 12), W(pinky));
            }
        }

        // ---- To Limber's space: +X = character's right, +Y = up, +Z = forward when facing the camera.
        for (int i = 0; i < j.Length; i++)
            j[i] = new Vector3(-j[i].X, -j[i].Y, -j[i].Z);

        float Vis(int landmark) => visibility[landmark];
        var ankleL0 = j[(int)Joint.AnkleL];
        var ankleR0 = j[(int)Joint.AnkleR];
        KeepLimbLengths(slot, j, v, Vis);
        SteadyGuesses(slot, j, v, Vis);
        // Heels come straight from the model: move them with their ankle's correction.
        var heelShift = new[] { j[(int)Joint.AnkleL] - ankleL0, j[(int)Joint.AnkleR] - ankleR0 };

        // Feet on the floor: lowest foot point, smoothed over time (drops quickly, rises slowly) so the body
        // doesn't bob or sink when the feet are uncertain.
        float floor = float.MaxValue;
        bool footSure = false;
        void Foot(float y, float vis)
        {
            if (y < floor)
            {
                floor = y;
                footSure = vis >= SureVisibility;
            }
        }
        foreach (var (id, landmark) in new[] { (Joint.AnkleL, 27), (Joint.AnkleR, 28), (Joint.ToeL, 31), (Joint.ToeR, 32) })
            if (v[(int)id])
                Foot(j[(int)id].Y, visibility[landmark]);
        for (int side = 0; side < 2; side++)
        {
            int heel = side == 0 ? 29 : 30;
            if (visibility[heel] >= (guessHidden ? guessFloor : 0.35f))
                Foot(-world[heel].Y + heelShift[side].Y, visibility[heel]);
        }
        if (floor == float.MaxValue || !float.IsFinite(floor))
            floor = Math.Min(j[(int)Joint.ThighL].Y, j[(int)Joint.ThighR].Y) - 0.85f; // no feet: a standing leg length
        if (!slot.HasFloor)
        {
            slot.Floor = floor;
            slot.HasFloor = true;
        }
        else
        {
            // Clearly seen feet are followed exactly (crouching, standing up); guessed ones drift gently.
            float rate = footSure ? 1f : (floor < slot.Floor ? 0.5f : 0.1f);
            slot.Floor += (floor - slot.Floor) * rate;
        }

        for (int i = 0; i < j.Length; i++)
        {
            if (!v[i])
                continue;
            var q = j[i] - new Vector3(0, slot.Floor, 0);
            if (!float.IsFinite(q.X) || !float.IsFinite(q.Y) || !float.IsFinite(q.Z))
                continue;
            output.P[i] = q;
            output.V[i] = true;
        }
        return true;
    }

    /// <summary>
    /// Learn the person's upper arm, forearm, thigh and shin lengths from frames where both ends are clearly
    /// visible, and hold guessed limbs to those lengths (moving everything below the joint along with it).
    /// </summary>
    private static void KeepLimbLengths(Slot slot, Vector3[] j, bool[] v, Func<int, float> visibilityOf)
    {
        for (int b = 0; b < Bones.Length; b++)
        {
            var (parent, child, carried) = Bones[b];
            if (!v[(int)parent] || !v[(int)child])
                continue;
            float childVis = JointVisibility(child, visibilityOf);
            float parentVis = JointVisibility(parent, visibilityOf);
            var vec = j[(int)child] - j[(int)parent];
            float len = vec.Length();
            if (len < 1e-4f || !float.IsFinite(len))
                continue;

            if (childVis >= SureVisibility && parentVis >= SureVisibility)
            {
                // Clearly seen: learn (slowly, so one odd frame can't change it much).
                slot.BoneLength[b] = slot.HasBone[b] ? slot.BoneLength[b] + (len - slot.BoneLength[b]) * 0.05f : len;
                slot.HasBone[b] = true;
            }
            else if (slot.HasBone[b])
            {
                // Guessed: same direction, the person's real length.
                var shift = vec * (slot.BoneLength[b] / len - 1f);
                j[(int)child] += shift;
                foreach (var c in carried)
                    j[(int)c] += shift;
            }
        }
    }

    /// <summary>The less sure the model is about a limb joint, the more it's smoothed over time.</summary>
    private static void SteadyGuesses(Slot slot, Vector3[] j, bool[] v, Func<int, float> visibilityOf)
    {
        foreach (var (joint, landmark) in LimbVisibility)
        {
            int i = (int)joint;
            if (!v[i])
            {
                slot.HasPrevious[i] = false;
                continue;
            }
            float doubt = Math.Clamp(1f - visibilityOf(landmark) / SureVisibility, 0f, 1f);
            var before = j[i];
            if (slot.HasPrevious[i] && doubt > 0f)
                j[i] = Vector3.Lerp(j[i], slot.Previous[i], doubt * 0.75f);
            if (joint is Joint.WristL or Joint.WristR)
            {
                // The hand comes along with the wrist.
                var delta = j[i] - before;
                var first = joint == Joint.WristL ? Joint.ThumbL1 : Joint.ThumbR1;
                for (int f = 0; f < 15; f++)
                    j[(int)first + f] += delta;
            }
            slot.Previous[i] = j[i];
            slot.HasPrevious[i] = true;
        }
    }

    private static float JointVisibility(Joint joint, Func<int, float> visibilityOf)
    {
        foreach (var (j, landmark) in LimbVisibility)
            if (j == joint)
                return visibilityOf(landmark);
        return 1f; // shoulders and hips: always well placed
    }

    // ---- Accurate body model (RTMW3D)

    private const float RtmLostScore = 0.25f;   // average body-point score below this: nobody there
    private const float RtmSureScore = 0.3f;    // a point this sure is used to place the crop and to measure
    private const float RtmVisibilityScale = 1.25f;

    // COCO-WholeBody point for each of MediaPipe's 33 body landmarks (so the rest of the tracker works unchanged).
    // Hands: left 91..111, right 112..132, each wrist, thumb 1-4, index 1-4, middle, ring, little.
    private static readonly int[] BlazeFromCoco =
    [
        0,          // 0 nose
        1, 1, 1,    // 1-3 left eye (inner, centre, outer)
        2, 2, 2,    // 4-6 right eye
        3, 4,       // 7-8 ears
        77, 71,     // 9-10 mouth left / right (face points 54 and 48)
        5, 6, 7, 8, 9, 10,          // 11-16 shoulders, elbows, wrists
        91 + 17, 112 + 17,          // 17-18 little-finger knuckles
        91 + 5, 112 + 5,            // 19-20 index knuckles
        91 + 2, 112 + 2,            // 21-22 thumbs
        11, 12, 13, 14, 15, 16,     // 23-28 hips, knees, ankles
        19, 22,                     // 29-30 heels
        17, 20,                     // 31-32 big toes
    ];

    // Typical adult body segment lengths (metres), used to work out the person's size and how deep the model's depth is.
    private static readonly (int A, int B, float Length)[] Segments =
    [
        (5, 7, 0.29f), (7, 9, 0.26f), (6, 8, 0.29f), (8, 10, 0.26f),
        (11, 13, 0.43f), (13, 15, 0.42f), (12, 14, 0.43f), (14, 16, 0.42f),
        (5, 6, 0.34f), (11, 12, 0.22f), (5, 11, 0.50f), (6, 12, 0.50f),
    ];

    // The model's depth grows away from the camera (checked against faces seen head-on: the nose comes out nearer
    // than the ears), the same way as MediaPipe's.
    private const float DepthSign = 1f;

    // Points the accurate model is this unsure of are pure guesswork (e.g. legs hidden behind the body): they're left
    // out, so the character keeps its own pose there, even with "work out hidden limbs" on.
    private const float RtmGuessFloor = 0.25f;
    private const float RtmTurnScore = 0.5f;

    private bool RunRtm(Mat bgr, Slot slot, out Vector2[] image, out Vector3[] world, out float[] visibility, Vector3[]?[] hands)
    {
        image = [];
        world = [];
        visibility = [];
        var r = _rtm!.Run(bgr, slot.Center, slot.Side, slot.Angle);
        var s = r.Score;

        float body = 0f;
        for (int i = 5; i <= 16; i++)
            body += s[i];
        body /= 12f;
        if (body < RtmLostScore || !float.IsFinite(body))
            return false;

        var hipImg = (r.Image[11] + r.Image[12]) * 0.5f;
        float hipZ = (r.Z[11] + r.Z[12]) * 0.5f;

        // ---- Size and depth scale. For each body segment: (picture length / ppm)^2 + (ratio * depth)^2 = length^2.
        // Solved for 1/ppm^2 and ratio^2 by least squares; the ratio is a property of the model, so it's averaged
        // slowly over time, and only learned from frames with enough depth in them to tell.
        double spp = 0, spq = 0, sqq = 0, sp = 0, sq = 0, sw = 0;
        foreach (var (a, b, length) in Segments)
        {
            float w = MathF.Min(s[a], s[b]);
            if (w < RtmSureScore)
                continue;
            double l2 = length * length;
            double p = Vector2.DistanceSquared(r.Image[a], r.Image[b]) / l2;
            double dz = r.Z[a] - r.Z[b];
            double q = dz * dz / l2;
            spp += w * p * p; spq += w * p * q; sqq += w * q * q; sp += w * p; sq += w * q; sw += w;
        }
        bool measured = sw > 0 && spp > 0;
        if (!measured && slot.PixelsPerMeter <= 0)
            return false;

        double det = spp * sqq - spq * spq;
        if (measured && sqq / sw > 0.004 && det > 1e-6 * spp * sqq)
        {
            double c = (spp * sq - spq * sp) / det;
            if (c > 0)
            {
                float ratio = Math.Clamp((float)Math.Sqrt(c), 0.25f, 4f);
                float rate = slot.DepthSamples < 30 ? 0.15f : 0.03f;
                slot.DepthRatio += (ratio - slot.DepthRatio) * rate;
                slot.DepthSamples++;
            }
        }
        double c2 = slot.DepthRatio * (double)slot.DepthRatio;
        double invPpm2 = measured ? (sp - c2 * spq) / spp : 0;
        float ppm = invPpm2 > 1e-12 ? (float)(1.0 / Math.Sqrt(invPpm2)) : 0f;
        if (!(ppm > 1f) || !float.IsFinite(ppm))
            ppm = slot.PixelsPerMeter > 0 ? slot.PixelsPerMeter
                : MathF.Max(1f, Vector2.Distance((r.Image[5] + r.Image[6]) * 0.5f, hipImg) / 0.5f);
        slot.PixelsPerMeter = slot.PixelsPerMeter > 0 ? slot.PixelsPerMeter + (ppm - slot.PixelsPerMeter) * 0.3f : ppm;
        ppm = slot.PixelsPerMeter;

        float depth = DepthSign * slot.DepthRatio;

        Vector3 World(int i) => new((r.Image[i].X - hipImg.X) / ppm, (r.Image[i].Y - hipImg.Y) / ppm, (r.Z[i] - hipZ) * depth);
        float Vis(int i) => Math.Clamp(s[i] * RtmVisibilityScale, 0f, 1f);

        image = new Vector2[39];
        world = new Vector3[39];
        visibility = new float[39];
        for (int i = 0; i < BlazeFromCoco.Length; i++)
        {
            int c = BlazeFromCoco[i];
            image[i] = r.Image[c];
            world[i] = World(c);
            visibility[i] = Vis(c);
        }
        image[33] = hipImg;
        world[33] = (world[23] + world[24]) * 0.5f;
        visibility[33] = MathF.Min(visibility[23], visibility[24]);
        image[34] = (r.Image[5] + r.Image[6]) * 0.5f;
        world[34] = (world[11] + world[12]) * 0.5f;
        visibility[34] = MathF.Min(visibility[11], visibility[12]);

        // ---- Hands, with depth, straight from the same model (when it's reasonably sure about them).
        for (int h = 0; h < 2; h++)
        {
            int first = h == 0 ? 91 : 112;
            float score = 0f;
            for (int i = 0; i < 21; i++)
                score += s[first + i];
            if (score / 21f < RtmSureScore)
                continue;
            var hand = new Vector3[21];
            for (int i = 0; i < 21; i++)
                hand[i] = World(first + i);
            hands[h] = hand;
        }

        // ---- Next frame's crop: around every point it's sure of, turned so the body stands upright.
        var shoulders = (r.Image[5] + r.Image[6]) * 0.5f;
        // Only turn the crop when the hips and shoulders are clearly seen: turning it by a guessed hip throws the
        // next frame off, and it snowballs.
        if (MathF.Min(MathF.Min(s[5], s[6]), MathF.Min(s[11], s[12])) >= RtmTurnScore)
        {
            var up = shoulders - hipImg;
            if (up.LengthSquared() > 4f)
            {
                float target = AngleUp(up);
                float turn = MathF.IEEERemainder(target - slot.Angle, MathF.Tau);
                slot.Angle = MathF.IEEERemainder(slot.Angle + turn * 0.5f, MathF.Tau);
            }
        }
        float cos = MathF.Cos(slot.Angle), sin = MathF.Sin(slot.Angle);
        float uMin = float.MaxValue, uMax = float.MinValue, vMin = float.MaxValue, vMax = float.MinValue;
        int used = 0;
        for (int i = 0; i < 23; i++)
        {
            if (s[i] < RtmSureScore)
                continue;
            var d = r.Image[i];
            float u = cos * d.X + sin * d.Y, v = -sin * d.X + cos * d.Y;
            uMin = MathF.Min(uMin, u); uMax = MathF.Max(uMax, u);
            vMin = MathF.Min(vMin, v); vMax = MathF.Max(vMax, v);
            used++;
        }
        if (used < 6)
            return false;
        // The head reaches above the eyes and ears: a little extra room on top.
        vMin -= (vMax - vMin) * 0.06f;
        float cu = (uMin + uMax) * 0.5f, cv = (vMin + vMax) * 0.5f;
        var center = new Vector2(cos * cu - sin * cv, sin * cu + cos * cv);
        float height = MathF.Max(vMax - vMin, (uMax - uMin) * InputAspect) * 1.3f;
        height = Math.Clamp(height, 48f, 1.6f * MathF.Max(bgr.Width, bgr.Height));
        if (!float.IsFinite(height) || !float.IsFinite(center.X) || !float.IsFinite(center.Y))
            return false;

        slot.Center = Vector2.Lerp(slot.Center, center, 0.8f);
        slot.Side += (height - slot.Side) * 0.6f;
        slot.HasRoi = true;
        slot.LastCenter = slot.Center;
        slot.HasLast = true;
        return true;
    }

    private const float InputAspect = RtmBody.InputHeight / (float)RtmBody.InputWidth;

    // ---- Body model

    private bool RunPose(Mat bgr, Slot slot, out Vector2[] image, out Vector3[] world, out float[] visibility)
    {
        image = [];
        world = [];
        visibility = [];

        var input = Crop(bgr, slot.Center, slot.Side, slot.Angle, PoseSize, _poseNchw);
        var tensor = new DenseTensor<float>(input, _poseNchw ? new[] { 1, 3, PoseSize, PoseSize } : new[] { 1, PoseSize, PoseSize, 3 });
        using var results = _pose!.Run(new[] { NamedOnnxValue.CreateFromTensor(_poseInput, tensor) });

        float[]? landmarks = null, worldRaw = null;
        float confidence = -1f;
        foreach (var r in results)
        {
            var t = r.AsTensor<float>();
            switch (t.Length)
            {
                case 195 when landmarks == null: landmarks = t.ToArray(); break;
                case 117 when worldRaw == null: worldRaw = t.ToArray(); break;
                case 1 when confidence < 0f: confidence = t.ToArray()[0]; break;
            }
        }
        if (landmarks == null || worldRaw == null)
            throw new InvalidDataException("The body model's outputs aren't what Limber expects.");

        if (confidence < 0f || confidence > 1f)
            confidence = Sigmoid(confidence);
        if (confidence < PoseConfidence)
            return false;

        float k = slot.Side / PoseSize;
        float cos = MathF.Cos(slot.Angle), sin = MathF.Sin(slot.Angle);
        image = new Vector2[39];
        world = new Vector3[39];
        visibility = new float[39];
        for (int i = 0; i < 39; i++)
        {
            float x = (landmarks[i * 5] - PoseSize * 0.5f) * k;
            float y = (landmarks[i * 5 + 1] - PoseSize * 0.5f) * k;
            image[i] = slot.Center + new Vector2(cos * x - sin * y, sin * x + cos * y);
            visibility[i] = Sigmoid(landmarks[i * 5 + 3]);

            float wx = worldRaw[i * 3], wy = worldRaw[i * 3 + 1], wz = worldRaw[i * 3 + 2];
            world[i] = new Vector3(cos * wx - sin * wy, sin * wx + cos * wy, wz);
        }
        return true;
    }

    // ---- Hand model

    private bool RunHand(Mat bgr, Vector2 wrist, Vector2 knuckles, out Vector3[] world)
    {
        world = [];
        var dir = knuckles - wrist;
        var len = dir.Length();
        if (len < 3f || !float.IsFinite(len))
            return false;

        var center = wrist + dir * 1.05f;
        var side = len * 3.4f;
        var angle = AngleUp(dir);

        var input = Crop(bgr, center, side, angle, HandSize, _handNchw);
        var tensor = new DenseTensor<float>(input, _handNchw ? new[] { 1, 3, HandSize, HandSize } : new[] { 1, HandSize, HandSize, 3 });
        using var results = _hand!.Run(new[] { NamedOnnxValue.CreateFromTensor(_handInput, tensor) });

        // Outputs, in the model's order: landmarks (63), confidence (1), handedness (1), world landmarks (63).
        var outputs = new List<float[]>();
        foreach (var r in results)
            outputs.Add(r.AsTensor<float>().ToArray());
        if (outputs.Count < 4 || outputs[0].Length != 63 || outputs[1].Length != 1 || outputs[3].Length != 63)
            throw new InvalidDataException("The hand model's outputs aren't what Limber expects.");

        var confidence = outputs[1][0];
        if (confidence < 0f || confidence > 1f)
            confidence = Sigmoid(confidence);
        if (confidence < HandConfidence)
            return false;

        var raw = outputs[3];
        float cos = MathF.Cos(angle), sin = MathF.Sin(angle);
        world = new Vector3[21];
        for (int i = 0; i < 21; i++)
        {
            float wx = raw[i * 3], wy = raw[i * 3 + 1], wz = raw[i * 3 + 2];
            world[i] = new Vector3(cos * wx - sin * wy, sin * wx + cos * wy, wz);
        }
        return true;
    }

    // ---- Helpers

    /// <summary>Rotation (image space, y down) that turns the crop's "up" into <paramref name="up"/>.</summary>
    private static float AngleUp(Vector2 up) => MathF.Atan2(up.X, -up.Y);

    /// <summary>
    /// Rotated square crop of <paramref name="side"/> pixels around <paramref name="center"/>, resized to
    /// size x size, as RGB floats in [0, 1] (or [-1, 1]). Outside the picture is filled with <paramref name="border"/>.
    /// </summary>
    private static float[] Crop(Mat bgr, Vector2 center, float side, float angle, int size, bool nchw,
        bool minusOneToOne = false, double border = 0)
    {
        double k = side / size;
        double cos = Math.Cos(angle), sin = Math.Sin(angle);
        // image -> crop: u = R(-angle) * (p - center) / k + size / 2
        double a = cos / k, b = sin / k, c = -sin / k, d = cos / k;

        using var m = new Mat(2, 3, MatType.CV_64FC1);
        m.Set(0, 0, a);
        m.Set(0, 1, b);
        m.Set(0, 2, size * 0.5 - (a * center.X + b * center.Y));
        m.Set(1, 0, c);
        m.Set(1, 1, d);
        m.Set(1, 2, size * 0.5 - (c * center.X + d * center.Y));

        using var crop = new Mat();
        Cv2.WarpAffine(bgr, crop, m, new Size(size, size), InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(border));

        var data = new float[size * size * 3];
        var px = crop.GetGenericIndexer<Vec3b>();
        int plane = size * size;
        float scale = minusOneToOne ? 2f / 255f : 1f / 255f;
        float offset = minusOneToOne ? -1f : 0f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                var p = px[y, x];
                float r = p.Item2 * scale + offset, g = p.Item1 * scale + offset, bl = p.Item0 * scale + offset;
                int i = y * size + x;
                if (nchw)
                {
                    data[i] = r;
                    data[plane + i] = g;
                    data[plane * 2 + i] = bl;
                }
                else
                {
                    data[i * 3] = r;
                    data[i * 3 + 1] = g;
                    data[i * 3 + 2] = bl;
                }
            }
        }
        return data;
    }

    private static float Sigmoid(float x) => 1f / (1f + MathF.Exp(-x));

    public void Dispose()
    {
        _pose?.Dispose();
        _rtm?.Dispose();
        _hand?.Dispose();
        _detector?.Dispose();
    }
}
