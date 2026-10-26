using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Limber.Protocol;
using OpenCvSharp;

namespace Limber.Video;

/// <summary>What to track and how.</summary>
public sealed record VideoOptions(
    string Path,
    string Name,
    bool Fingers,
    bool GuessHiddenLimbs,
    int MaxFps,
    float StartSeconds,
    float EndSeconds,      // <= 0: to the end
    float Smoothing,       // 0 = none, 0.9 = heavy
    int People = 1,        // 1 or 2
    bool SwapSides = false, // 2 people: person 1 starts on the right instead of the left
    bool Accurate = true,    // RTMW3D instead of MediaPipe
    bool Gpu = true);        // run it on the graphics card when possible

/// <summary>
/// Turns a video file into a <see cref="MotionClip"/> on a background thread, with progress and cancel.
/// Nothing leaves the PC: the file is read locally and the models run locally.
/// </summary>
public sealed class VideoProcessor : IDisposable
{
    private readonly string _pluginDir;
    private CancellationTokenSource? _cts;
    private Task? _task;

    public volatile float Progress;
    public volatile string Status = "";
    public string? Error { get; private set; }

    /// <summary>The finished clip; taken (and cleared) by the UI with <see cref="TakeResult"/>.</summary>
    private MotionClip? _result;

    public bool Busy => _task is { IsCompleted: false };

    public VideoProcessor(string pluginDir) => _pluginDir = pluginDir;

    public string ModelDir => _pluginDir;

    public MotionClip? TakeResult() => Interlocked.Exchange(ref _result, null);

    public void Start(VideoOptions options)
    {
        if (Busy)
            return;
        Error = null;
        Progress = 0f;
        Status = "Starting...";
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _task = Task.Factory.StartNew(() => Run(options, token), token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public void Cancel() => _cts?.Cancel();

    private void Run(VideoOptions o, CancellationToken token)
    {
        try
        {
            // Stay out of the game's way.
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;

            if (!NativeLibs.EnsureLoaded(_pluginDir))
                throw new InvalidOperationException(NativeLibs.Error);
            bool accurate = o.Accurate && PoseTracker.ModelsPresent(ModelDir, o.Fingers, out _, accurate: true);
            if (!PoseTracker.ModelsPresent(ModelDir, o.Fingers, out var missing, accurate))
                throw new FileNotFoundException($"{missing} is missing from Limber's folder - reinstall or rebuild Limber.");
            if (!File.Exists(o.Path))
                throw new FileNotFoundException("That video file doesn't exist.");

            Status = "Opening the video...";
            using var capture = new VideoCapture(o.Path);
            if (!capture.IsOpened())
                throw new InvalidDataException("Couldn't open that video. Try an .mp4 (H.264), and a path without unusual characters.");

            double fps = capture.Fps;
            if (!double.IsFinite(fps) || fps < 1 || fps > 480)
                fps = 30;
            int total = capture.FrameCount;
            int stride = Math.Max(1, (int)Math.Round(fps / Math.Clamp(o.MaxFps, 5, 120)));
            double outFps = fps / stride;

            int startFrame = Math.Max(0, (int)(o.StartSeconds * fps));
            int endFrame = o.EndSeconds > 0 ? (int)(o.EndSeconds * fps) : (total > 0 ? total : int.MaxValue);
            if (startFrame > 0)
                capture.Set(VideoCaptureProperties.PosFrames, startFrame);

            int threads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
            Status = "Loading the tracking model...";
            using var tracker = new PoseTracker(ModelDir, o.Fingers, threads, accurate, o.Gpu);
            string modelText = o.Accurate && !accurate ? "fast model - the accurate one isn't installed" : tracker.Description;

            int people = Math.Clamp(o.People, 1, 2);
            var clip = new MotionClip { Name = o.Name, Fps = (float)outFps };
            clip.SetPeople(people);
            var slots = PeopleSlots(people, o.SwapSides);
            var results = new MotionFrame[people];
            var found = new bool[people];
            var last = new MotionFrame?[people];

            using var frame = new Mat();
            using var bgr = new Mat();
            int index = startFrame;
            int span = Math.Max(1, (endFrame == int.MaxValue ? total : endFrame) - startFrame);

            while (index < endFrame)
            {
                token.ThrowIfCancellationRequested();
                if (!capture.Read(frame) || frame.Empty())
                    break;

                if ((index - startFrame) % stride == 0)
                {
                    var src = frame;
                    if (frame.Channels() == 4)
                    {
                        Cv2.CvtColor(frame, bgr, ColorConversionCodes.BGRA2BGR);
                        src = bgr;
                    }
                    else if (frame.Channels() == 1)
                    {
                        Cv2.CvtColor(frame, bgr, ColorConversionCodes.GRAY2BGR);
                        src = bgr;
                    }

                    for (int p = 0; p < people; p++)
                        results[p] = new MotionFrame();
                    tracker.ProcessPeople(src, slots, o.Fingers, o.GuessHiddenLimbs, results, found);

                    for (int p = 0; p < people; p++)
                    {
                        if (found[p])
                        {
                            clip.Tracked[p]++;
                            last[p] = results[p];
                            clip.Tracks[p].Add(results[p]);
                        }
                        else
                        {
                            // Lost this person: hold their last pose (or nothing yet) so playback doesn't jump.
                            clip.Tracks[p].Add(last[p]?.Clone() ?? new MotionFrame());
                        }
                    }

                    if (total > 0)
                        Progress = Math.Clamp((index - startFrame) / (float)span, 0f, 1f);
                    Status = $"Tracking ({modelText})... {clip.Frames.Count} frames, found " + FoundText(clip);
                }
                index++;
            }

            if (clip.Tracked.All(t => t == 0))
                throw new InvalidDataException("No person was found in this video. It works best with people fully in view.");

            Status = "Smoothing...";
            foreach (var track in clip.Tracks)
                Smooth(track, o.Smoothing);
            Progress = 1f;
            Status = $"Done: {clip.Frames.Count} frames ({clip.Duration:0.0} s), found " + FoundText(clip) + ".";
            Volatile.Write(ref _result, clip);
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled.";
        }
        catch (Exception e)
        {
            Error = e.Message;
            Status = "Failed.";
            Plugin.Log.Warning(e, "Limber: video tracking failed");
        }
    }

    /// <summary>Tracker slots for 1 or 2 people: two people start on the left and right halves of the picture.</summary>
    public static PoseTracker.Slot[] PeopleSlots(int people, bool swap)
    {
        if (people <= 1)
            return [new PoseTracker.Slot()];
        var left = new PoseTracker.Slot { SeedFrom = 0f, SeedTo = 0.5f };
        var right = new PoseTracker.Slot { SeedFrom = 0.5f, SeedTo = 1f };
        return swap ? [right, left] : [left, right];
    }

    private static string FoundText(MotionClip clip)
        => clip.People == 1
            ? $"the person in {clip.Tracked[0]}"
            : string.Join(", ", clip.Tracked.Select((t, i) => $"person {i + 1} in {t}"));

    /// <summary>
    /// Zero-lag smoothing: an exponential filter run forwards and then backwards over each joint, restarted
    /// wherever the joint wasn't tracked.
    /// </summary>
    private static void Smooth(List<MotionFrame> frames, float amount)
    {
        float s = Math.Clamp(amount, 0f, 0.95f);
        if (s <= 0f || frames.Count < 3)
            return;

        int n = frames.Count;
        for (int j = 0; j < (int)Joint.Count; j++)
        {
            bool has = false;
            Vector3 acc = default;
            for (int i = 0; i < n; i++)
            {
                var f = frames[i];
                if (!f.V[j]) { has = false; continue; }
                acc = has ? acc * s + f.P[j] * (1f - s) : f.P[j];
                has = true;
                f.P[j] = acc;
            }
            has = false;
            for (int i = n - 1; i >= 0; i--)
            {
                var f = frames[i];
                if (!f.V[j]) { has = false; continue; }
                acc = has ? acc * s + f.P[j] * (1f - s) : f.P[j];
                has = true;
                f.P[j] = acc;
            }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _task?.Wait(TimeSpan.FromSeconds(3)); } catch { }
        _cts?.Dispose();
    }
}
