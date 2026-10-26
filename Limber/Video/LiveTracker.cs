using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Limber.Koi.Net;
using Limber.Protocol;
using OpenCvSharp;

namespace Limber.Video;

/// <summary>
/// Follows VLC live: asks VLC (through <see cref="VlcLink"/>) which file is playing and where, reads that same
/// frame from the file, tracks the person in it, and streams the pose on a Koikatsu-link channel. Tracking runs
/// on its own background thread; <see cref="Update"/> (framework thread) does the streaming.
/// </summary>
public sealed class LiveTracker : IDisposable
{
    public sealed record Settings(int Port, string Password, bool Fingers, bool GuessHidden, float Smoothing, int People = 1,
        bool SwapSides = false, bool Accurate = true, bool Gpu = true);

    private readonly string _pluginDir;
    private readonly PoseReceiver _receiver;
    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private int _runId;
    private Func<Settings>? _settings;

    // Latest tracked pose, handed from the tracking thread to the framework thread.
    private readonly object _gate = new();
    private MotionFrame?[] _latest = new MotionFrame?[2];
    private int _latestVersion;
    private string? _fileError;

    // Streaming state (framework thread).
    private int _sentVersion = -1;
    private long _lastSendTicks;
    private uint _packetNumber;

    public volatile string Status = "Off";
    public volatile string? Error;
    public volatile string? FileName;
    public volatile float TrackFps;
    public volatile bool PersonFound;
    /// <summary>How many of the tracked people were found in the latest frame.</summary>
    public volatile int PeopleFound;
    /// <summary>Which model is tracking, and where.</summary>
    public volatile string? Model;

    public bool Running => _thread != null;

    public LiveTracker(string pluginDir, PoseReceiver receiver)
    {
        _pluginDir = pluginDir;
        _receiver = receiver;
    }

    public void Start(Func<Settings> settings)
    {
        if (_thread != null)
            return;
        _settings = settings;
        Error = null;
        Status = "Connecting to VLC...";
        var cts = new CancellationTokenSource();
        int run;
        lock (_gate)
            run = ++_runId;
        _cts = cts;
        _thread = new Thread(() => SafeLoop(run, cts.Token)) { IsBackground = true, Name = "Limber VLC live", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    public void Stop()
    {
        if (_thread == null)
            return;
        // Never wait here (this runs on the game thread): the old run notices on its own and its results are
        // ignored from now on.
        _cts?.Cancel();
        _cts = null;
        _thread = null;
        lock (_gate)
        {
            _runId++;
            _latest = new MotionFrame?[2];
        }
        _sentVersion = -1;
        Status = "Off";
        FileName = null;
        PersonFound = false;
        TrackFps = 0;
        Model = null;
    }

    /// <summary>
    /// Framework thread: send the newest pose of each person on their channel (and re-send while held, so links
    /// stay on). <paramref name="channels"/>: person 1's channel, then person 2's.
    /// </summary>
    public void Update(int[] channels, bool mirror)
    {
        if (_thread == null)
            return;

        MotionFrame?[] frames;
        int version;
        lock (_gate)
        {
            frames = _latest;
            version = _latestVersion;
        }

        var now = Stopwatch.GetTimestamp();
        if (version == _sentVersion && (now - _lastSendTicks) / (double)Stopwatch.Frequency < 0.1)
            return;

        for (int p = 0; p < frames.Length && p < channels.Length; p++)
        {
            var frame = frames[p];
            if (frame == null)
                continue;
            if (p > 0 && channels[p] == channels[0])
                continue; // both people on one channel would fight; person 1 wins
            _receiver.Inject(MotionClip.Encode(frame, p == 0 ? "VLC" : $"VLC ({p + 1})", ++_packetNumber, channels[p], mirror), channels[p]);
        }
        _sentVersion = version;
        _lastSendTicks = now;
    }

    /// <summary>Hand the latest poses to the framework thread (an entry is null when that person isn't tracked).</summary>
    private void Publish(int run, MotionFrame?[]? frames)
    {
        lock (_gate)
        {
            if (run != _runId)
                return; // a stopped run
            _latest = frames ?? new MotionFrame?[2];
            _latestVersion++;
        }
    }

    private void SafeLoop(int run, CancellationToken token)
    {
        try
        {
            Loop(run, token);
        }
        catch (Exception e)
        {
            // Last line of defence (see Loop).
            Error = e.Message;
            Status = "Stopped.";
            try { Plugin.Log.Error(e, "Limber: VLC live thread failed"); } catch { }
        }
    }

    private void Loop(int run, CancellationToken token)
    {
        VlcLink? vlc = null;
        (int Port, string Password) vlcFor = (-1, "");
        PoseTracker? tracker = null;
        (bool Fingers, bool Accurate, bool Gpu) trackerFor = (false, false, false);
        VideoCapture? capture = null;
        string? capturePath = null;
        double fps = 30;
        int nextFrame = 0;
        int lastTracked = -1;
        var smoothed = new MotionFrame?[2];
        PoseTracker.Slot[]? slots = null;
        (int People, bool Swap) slotsFor = (0, false);

        var state = new VlcLink.State(VlcLink.Playback.Unknown, null, 0, 0, 0, 1, 0);
        long lastPoll = 0;
        long stoppedSince = 0;
        double clock = 0;
        long clockTicks = 0;
        bool hasClock = false;
        int frames = 0;
        long fpsWindow = Stopwatch.GetTimestamp();

        // Nothing on this thread may ever throw out of it: an unhandled exception on a background thread closes
        // the whole game. Video objects are only created after the video libraries are loaded.
        Mat? frame = null, bgr = null;
        try
        {
            if (!NativeLibs.EnsureLoaded(_pluginDir))
                throw new InvalidOperationException(NativeLibs.Error);
            frame = new Mat();
            bgr = new Mat();

            while (!token.IsCancellationRequested)
            {
                var settings = _settings!();

                // (Re)connect when the port or password changed.
                if (vlc == null || vlcFor != (settings.Port, settings.Password))
                {
                    vlc?.Dispose();
                    vlc = new VlcLink(settings.Port, settings.Password);
                    vlcFor = (settings.Port, settings.Password);
                    lastPoll = 0;
                }

                // Ask VLC where it is, 4 times a second; in between, play position is estimated.
                var now = Stopwatch.GetTimestamp();
                if (lastPoll == 0 || (now - lastPoll) / (double)Stopwatch.Frequency >= 0.25)
                {
                    lastPoll = now;
                    try
                    {
                        state = vlc.Read();
                        Error = _fileError;
                    }
                    catch (VlcException e)
                    {
                        Error = e.Message;
                        Status = "Waiting for VLC...";
                        Publish(run, null);
                        Sleep(1000, token);
                        continue;
                    }
                }

                if (state.FilePath == null || state.Playback is VlcLink.Playback.Stopped or VlcLink.Playback.Unknown)
                {
                    // VLC can report "stopped" for a moment when it loops: only let go after half a second.
                    if (stoppedSince == 0)
                        stoppedSince = Stopwatch.GetTimestamp();
                    if ((Stopwatch.GetTimestamp() - stoppedSince) / (double)Stopwatch.Frequency > 0.5)
                    {
                        Status = "Connected. VLC isn't playing a video.";
                        FileName = null;
                        _fileError = null;
                        Error = null;
                        Publish(run, null);
                    }
                    Sleep(100, token);
                    continue;
                }
                stoppedSince = 0;

                // Open the file VLC is playing (Limber reads it itself, in step with VLC).
                if (state.FilePath != capturePath)
                {
                    capture?.Dispose();
                    capture = null;
                    capturePath = null;
                    FileName = Path.GetFileName(state.FilePath);
                    if (!File.Exists(state.FilePath))
                    {
                        Error = _fileError = "Can't find the file VLC is playing.";
                        Publish(run, null);
                        Sleep(1000, token);
                        continue;
                    }
                    var opened = new VideoCapture(state.FilePath);
                    if (!opened.IsOpened())
                    {
                        Error = _fileError = "Couldn't open the file VLC is playing. Try an .mp4 (H.264), and a path without unusual characters.";
                        opened.Dispose();
                        Publish(run, null);
                        Sleep(1000, token);
                        continue;
                    }
                    capture = opened;
                    capturePath = state.FilePath;
                    _fileError = null;
                    Error = null;
                    fps = capture.Fps;
                    if (!double.IsFinite(fps) || fps < 1 || fps > 480)
                        fps = 30;
                    nextFrame = 0;
                    lastTracked = -1;
                    hasClock = false;
                    smoothed = new MotionFrame?[2];
                    slots = null;
                }
                if (capture == null)
                {
                    Sleep(200, token);
                    continue;
                }

                if (tracker == null || trackerFor != (settings.Fingers, settings.Accurate, settings.Gpu))
                {
                    tracker?.Dispose();
                    tracker = null;
                    bool accurate = settings.Accurate && PoseTracker.ModelsPresent(_pluginDir, settings.Fingers, out _, accurate: true);
                    if (!PoseTracker.ModelsPresent(_pluginDir, settings.Fingers, out var missing, accurate))
                        throw new FileNotFoundException($"{missing} is missing from Limber's folder - reinstall or rebuild Limber.");
                    Status = "Loading the tracking model...";
                    tracker = new PoseTracker(_pluginDir, settings.Fingers, Math.Clamp(Environment.ProcessorCount / 2, 1, 4), accurate, settings.Gpu);
                    trackerFor = (settings.Fingers, settings.Accurate, settings.Gpu);
                    Model = settings.Accurate && !accurate ? "fast model - the accurate one isn't installed" : tracker.Description;
                    // A new model starts fresh.
                    if (slots != null)
                        foreach (var slot in slots)
                            slot.Forget();
                }

                // One tracker slot per person (two people start on the left and right halves).
                int people = Math.Clamp(settings.People, 1, 2);
                if (slots == null || slotsFor != (people, settings.SwapSides))
                {
                    slots = VideoProcessor.PeopleSlots(people, settings.SwapSides);
                    slotsFor = (people, settings.SwapSides);
                    smoothed = new MotionFrame?[2];
                }

                // Where VLC is now.
                double elapsed = (Stopwatch.GetTimestamp() - state.ReadTicks) / (double)Stopwatch.Frequency;
                // VLC's "position" (0..1) times the real frame count is finer than its whole-second length.
                int frameCount = capture.FrameCount;
                double baseSeconds = frameCount > 0 && state.Position > 0 ? state.Position * frameCount / fps : state.Seconds;
                bool playing = state.Playback == VlcLink.Playback.Playing;
                double reported = baseSeconds + (playing ? elapsed * state.Rate : 0);

                // Limber's own clock, gently pulled toward what VLC reports. VLC's position wobbles a little from
                // one report to the next; following it directly made the video jump back and forth.
                var clockNow = Stopwatch.GetTimestamp();
                bool jumped = false;
                if (!hasClock)
                {
                    clock = reported;
                    hasClock = true;
                    jumped = true;
                }
                else
                {
                    double dtc = Math.Clamp((clockNow - clockTicks) / (double)Stopwatch.Frequency, 0, 0.5);
                    if (playing)
                        clock += dtc * state.Rate;
                    double error = reported - clock;
                    if (Math.Abs(error) > 1.0)
                    {
                        clock = reported; // a real seek, a loop back to the start, or a stall
                        jumped = true;
                    }
                    else
                    {
                        clock += error * Math.Min(1.0, dtc * 2.0);
                    }
                }
                clockTicks = clockNow;

                int target = Math.Max(0, (int)(clock * fps));
                if (target == lastTracked)
                {
                    Sleep(4, token);
                    continue;
                }

                // Jump on a seek (or when far behind); a little ahead of VLC: just wait for it.
                if (jumped || target < nextFrame - (int)(fps * 0.5) || target > nextFrame + (int)(fps * 2))
                {
                    if (target != nextFrame)
                        capture.Set(VideoCaptureProperties.PosFrames, target);
                    nextFrame = target;
                    if (jumped)
                    {
                        foreach (var slot in slots)
                            slot.Forget();
                        smoothed = new MotionFrame?[2];
                    }
                }
                else if (target < nextFrame)
                {
                    Sleep(4, token);
                    continue;
                }
                while (nextFrame < target && !token.IsCancellationRequested)
                {
                    if (!capture.Grab())
                        break;
                    nextFrame++;
                }
                if (!capture.Read(frame) || frame.Empty())
                {
                    Sleep(20, token); // end of file, or VLC ahead of what can be read
                    lastTracked = target;
                    continue;
                }
                nextFrame++;
                lastTracked = target;

                var src = frame;
                if (frame.Channels() == 4) { Cv2.CvtColor(frame, bgr, ColorConversionCodes.BGRA2BGR); src = bgr; }
                else if (frame.Channels() == 1) { Cv2.CvtColor(frame, bgr, ColorConversionCodes.GRAY2BGR); src = bgr; }

                var poses = new MotionFrame[people];
                var found = new bool[people];
                for (int p = 0; p < people; p++)
                    poses[p] = new MotionFrame();
                tracker.ProcessPeople(src, slots, settings.Fingers, settings.GuessHidden, poses, found);

                int foundCount = 0;
                var publish = new MotionFrame?[2];
                for (int p = 0; p < people; p++)
                {
                    if (found[p])
                    {
                        foundCount++;
                        smoothed[p] = Smooth(smoothed[p], poses[p], settings.Smoothing);
                    }
                    // Keep holding a person's last pose while they're briefly lost.
                    publish[p] = smoothed[p]?.Clone();
                }
                PeopleFound = foundCount;
                PersonFound = foundCount > 0;
                if (foundCount > 0)
                    Publish(run, publish);

                frames++;
                var t = Stopwatch.GetTimestamp();
                var window = (t - fpsWindow) / (double)Stopwatch.Frequency;
                if (window >= 1)
                {
                    TrackFps = (float)(frames / window);
                    frames = 0;
                    fpsWindow = t;
                }
                Status = state.Playback == VlcLink.Playback.Playing ? "Following VLC" : "VLC is paused - holding the pose";
            }
        }
        catch (Exception e)
        {
            Error = e.Message;
            Status = "Stopped.";
            Plugin.Log.Warning(e, "Limber: VLC live tracking stopped");
        }
        finally
        {
            try
            {
                Publish(run, null); // never leave a frozen pose behind
                capture?.Dispose();
                tracker?.Dispose();
                vlc?.Dispose();
                frame?.Dispose();
                bgr?.Dispose();
            }
            catch
            {
                // Cleanup must never take the game down either.
            }
        }
    }

    /// <summary>Light live smoothing (some delay is unavoidable live, so this stays gentle).</summary>
    private static MotionFrame Smooth(MotionFrame? previous, MotionFrame current, float amount)
    {
        float s = Math.Clamp(amount, 0f, 0.9f);
        if (previous == null || s <= 0f)
            return current;
        for (int j = 0; j < (int)Joint.Count; j++)
        {
            if (current.V[j] && previous.V[j])
                current.P[j] = previous.P[j] * s + current.P[j] * (1f - s);
        }
        return current;
    }

    private static void Sleep(int ms, CancellationToken token)
    {
        // Wakes up at once when stopped.
        token.WaitHandle.WaitOne(ms);
    }

    public void Dispose()
    {
        var thread = _thread;
        var cts = _cts;
        Stop();
        // Plugin unload: give the thread a moment to let go of the video file and models.
        thread?.Join(TimeSpan.FromSeconds(2));
        cts?.Dispose();
    }
}
