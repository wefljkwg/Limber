using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace Limber.Video;

/// <summary>
/// RTMW3D-x (OpenMMLab, Apache-2.0): a whole-body model, 133 COCO-WholeBody points (17 body, 6 feet, 68 face,
/// 21 per hand) with a depth for each. Input: a 288x384 crop, RGB normalised by ImageNet mean/std, NCHW.
/// Output: SimCC - for every point, a score per position along x, along y and along depth; the best of each is
/// the point. x/y are in crop pixels (two bins per pixel); depth is relative to the hips, in [-ZRange, ZRange].
///
/// Runs on the graphics card through DirectML when it can (any DirectX 12 card), else on the CPU (slow).
/// </summary>
internal sealed class RtmBody : IDisposable
{
    public const string ModelFile = "rtmw3d-x_8xb64_cocktail14-384x288-b0a0eab7_20240626.onnx";
    public const int Points = 133;
    public const int InputWidth = 288, InputHeight = 384;
    private const float SplitRatio = 2f;
    private const float ZRange = 2.1744869f;
    private static readonly float[] Mean = [123.675f, 116.28f, 103.53f]; // R, G, B
    private static readonly float[] Std = [58.395f, 57.12f, 57.375f];

    private readonly InferenceSession _session;
    private readonly string _input;
    private readonly bool _nchw;
    private readonly float[] _buffer = new float[3 * InputWidth * InputHeight];

    /// <summary>"graphics card" or "CPU".</summary>
    public string Backend { get; }

    /// <summary>One crop's result, in image pixels; Z is depth relative to the hips (model units, roughly metres).</summary>
    public sealed class Result
    {
        public readonly Vector2[] Image = new Vector2[Points];
        public readonly float[] Z = new float[Points];
        public readonly float[] Score = new float[Points];
    }

    public RtmBody(string path, bool gpu, int threads)
    {
        InferenceSession? session = null;
        string backend = "CPU";
        if (gpu)
        {
            try
            {
                using var dml = new SessionOptions
                {
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                    EnableMemoryPattern = false,              // required by DirectML
                    ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                    IntraOpNumThreads = Math.Clamp(threads, 1, 8), // for any parts that fall back to the CPU
                    InterOpNumThreads = 1,
                };
                dml.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
                dml.AppendExecutionProvider_DML(0);
                session = new InferenceSession(path, dml);
                backend = "graphics card";
            }
            catch (Exception e)
            {
                Plugin.Log.Warning($"Limber: couldn't use the graphics card for tracking, using the CPU ({e.Message})");
                session = null;
            }
        }
        if (session == null)
        {
            using var cpu = new SessionOptions
            {
                IntraOpNumThreads = Math.Clamp(threads, 1, 8),
                InterOpNumThreads = 1,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            };
            cpu.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
            session = new InferenceSession(path, cpu);
        }
        _session = session;
        Backend = backend;

        var (name, meta) = _session.InputMetadata.First();
        _input = name;
        var dims = meta.Dimensions;
        _nchw = !(dims.Length == 4 && dims[3] == 3);
    }

    /// <summary>
    /// Run on a crop <paramref name="height"/> pixels tall (and 3/4 of that wide) around <paramref name="center"/>,
    /// rotated by <paramref name="angle"/> so the body stands upright in it.
    /// </summary>
    public Result Run(Mat bgr, Vector2 center, float height, float angle)
    {
        Crop(bgr, center, height, angle);
        var shape = _nchw ? new[] { 1, 3, InputHeight, InputWidth } : new[] { 1, InputHeight, InputWidth, 3 };
        var tensor = new DenseTensor<float>(_buffer, shape);
        using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(_input, tensor) });

        // Three outputs [1, 133, bins]: x (2 x 288), y (2 x 384), depth. Matched by name when named, else by size/order.
        var outs = new List<(string Name, float[] Data, int Bins)>();
        foreach (var r in results)
        {
            var t = r.AsTensor<float>();
            var d = t.Dimensions.ToArray();
            if (d.Length < 2 || d[^2] != Points)
                continue;
            outs.Add((r.Name.ToLowerInvariant(), t.ToArray(), d[^1]));
        }
        if (outs.Count < 3)
            throw new InvalidDataException("The RTMW3D model's outputs aren't what Limber expects.");

        int xBins = (int)(InputWidth * SplitRatio), yBins = (int)(InputHeight * SplitRatio);
        var y = outs.FirstOrDefault(o => o.Name.EndsWith("y") && o.Bins == yBins);
        if (y.Data == null)
            y = outs.First(o => o.Bins == yBins);
        var rest = outs.Where(o => !ReferenceEquals(o.Data, y.Data)).ToList();
        var x = rest.FirstOrDefault(o => o.Name.EndsWith("x") && o.Bins == xBins);
        if (x.Data == null)
            x = rest.First(o => o.Bins == xBins);
        var z = rest.FirstOrDefault(o => o.Name.EndsWith("z") && !ReferenceEquals(o.Data, x.Data));
        if (z.Data == null)
            z = rest.Last(o => !ReferenceEquals(o.Data, x.Data));

        float k = height / InputHeight;
        float cos = MathF.Cos(angle), sin = MathF.Sin(angle);
        var result = new Result();
        for (int i = 0; i < Points; i++)
        {
            var (xi, xv) = ArgMax(x.Data, i, x.Bins);
            var (yi, yv) = ArgMax(y.Data, i, y.Bins);
            var (zi, _) = ArgMax(z.Data, i, z.Bins);

            float u = (xi / SplitRatio - InputWidth * 0.5f) * k;
            float v = (yi / SplitRatio - InputHeight * 0.5f) * k;
            result.Image[i] = center + new Vector2(cos * u - sin * v, sin * u + cos * v);
            result.Z[i] = (zi / (z.Bins * 0.5f) - 1f) * ZRange;
            result.Score[i] = MathF.Max(0f, MathF.Min(xv, yv));
        }
        return result;
    }

    private static (int Index, float Value) ArgMax(float[] data, int point, int bins)
    {
        int o = point * bins, best = 0;
        float max = float.NegativeInfinity;
        for (int b = 0; b < bins; b++)
        {
            float v = data[o + b];
            if (v > max)
            {
                max = v;
                best = b;
            }
        }
        return (best, max);
    }

    private void Crop(Mat bgr, Vector2 center, float height, float angle)
    {
        double k = height / InputHeight;
        double cos = Math.Cos(angle), sin = Math.Sin(angle);
        double a = cos / k, b = sin / k, c = -sin / k, d = cos / k;
        using var m = new Mat(2, 3, MatType.CV_64FC1);
        m.Set(0, 0, a);
        m.Set(0, 1, b);
        m.Set(0, 2, InputWidth * 0.5 - (a * center.X + b * center.Y));
        m.Set(1, 0, c);
        m.Set(1, 1, d);
        m.Set(1, 2, InputHeight * 0.5 - (c * center.X + d * center.Y));

        using var crop = new Mat();
        Cv2.WarpAffine(bgr, crop, m, new Size(InputWidth, InputHeight), InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(0));

        var px = crop.GetGenericIndexer<Vec3b>();
        int plane = InputWidth * InputHeight;
        for (int yy = 0; yy < InputHeight; yy++)
        {
            for (int xx = 0; xx < InputWidth; xx++)
            {
                var p = px[yy, xx];
                float r = (p.Item2 - Mean[0]) / Std[0];
                float g = (p.Item1 - Mean[1]) / Std[1];
                float bl = (p.Item0 - Mean[2]) / Std[2];
                int i = yy * InputWidth + xx;
                if (_nchw)
                {
                    _buffer[i] = r;
                    _buffer[plane + i] = g;
                    _buffer[plane * 2 + i] = bl;
                }
                else
                {
                    _buffer[i * 3] = r;
                    _buffer[i * 3 + 1] = g;
                    _buffer[i * 3 + 2] = bl;
                }
            }
        }
    }

    public void Dispose() => _session.Dispose();
}
