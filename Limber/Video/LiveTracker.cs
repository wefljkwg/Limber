using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
        bool SwapSides = false, bool Accurate = true, bool Gpu = true, bool Nlf = false, string Distro = "");

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
    private string? _nlfError; // why NLF stopped (kept on screen until it runs again)

    // Streaming state (framework thread).
    private int _sentVersion = -1;
    private long _lastSendTicks;
    private uint _packetNumber;
    // In-betweening (framework thread): the game runs faster than tracking, so each game frame shows a blend from
    // what was on screen when the newest pose came in to that pose, spread over the time between poses.
    private readonly MotionFrame?[] _from = new MotionFrame?[2];
    private readonly MotionFrame?[] _to = new MotionFrame?[2];
    private readonly MotionFrame?[] _shown = new MotionFrame?[2];
    private long _toTicks;
    private double _gap = 0.1; // seconds between tracked poses (smoothed)

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
        _nlfError = null;
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
        _toTicks = 0;
        for (int p = 0; p < 2; p++)
            _from[p] = _to[p] = _shown[p] = null;
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
        if (version != _sentVersion)
        {
            if (_sentVersion >= 0 && _toTicks != 0)
            {
                double gap = (now - _toTicks) / (double)Stopwatch.Frequency;
                if (gap > 0.005 && gap < 1.0)
                    _gap += (Math.Clamp(gap, 0.016, 0.5) - _gap) * 0.3;
            }
            for (int p = 0; p < 2; p++)
            {
                var next = p < frames.Length ? frames[p] : null;
                _from[p] = next == null ? null : _shown[p] ?? next;
                _to[p] = next;
            }
            _toTicks = now;
            _sentVersion = version;
        }
        else if (_to[0] == null && _to[1] == null && (now - _lastSendTicks) / (double)Stopwatch.Frequency < 0.1)
            return;

        float t = (float)Math.Clamp((now - _toTicks) / (double)Stopwatch.Frequency / _gap, 0.0, 1.0);
        for (int p = 0; p < 2 && p < channels.Length; p++)
        {
            var to = _to[p];
            if (to == null)
            {
                _shown[p] = null;
                continue;
            }
            var frame = Blend(_from[p], to, t);
            _shown[p] = frame;
            if (p > 0 && channels[p] == channels[0])
                continue; // both people on one channel would fight; person 1 wins
            _receiver.Inject(MotionClip.Encode(frame, p == 0 ? "VLC" : $"VLC ({p + 1})", ++_packetNumber, channels[p], mirror), channels[p]);
        }
        _lastSendTicks = now;
    }

    private static MotionFrame Blend(MotionFrame? from, MotionFrame to, float t)
    {
        if (from == null || t >= 1f)
            return to;
        var f = new MotionFrame();
        for (int j = 0; j < f.P.Length; j++)
        {
            f.V[j] = to.V[j];
            f.P[j] = from.V[j] && to.V[j] ? System.Numerics.Vector3.Lerp(from.P[j], to.P[j], t) : to.P[j];
        }
        return f;
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
        NlfClient? nlf = null;
        string? nlfFor = null;          // the WSL distro it was started in
        long nlfRetryAt = 0;
        var nlfPelvis = new System.Numerics.Vector3?[2]; // where each tracked person was last (camera space)
        var nlfFloor = new[] { float.NaN, float.NaN };
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
                        Error = _fileError ?? _nlfError;
                        if (Status.StartsWith("Waiting for VLC", StringComparison.Ordinal))
                            Status = settings.Nlf ? "Connected. Waiting for NLF's first frames..." : "Connected.";
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

                if (settings.Nlf)
                {
                    // NLF in WSL: started once, again after the distro changes, retried every 10 s after a failure.
                    if (nlf == null || nlfFor != settings.Distro || !nlf.Running)
                    {
                        if (Stopwatch.GetTimestamp() < nlfRetryAt)
                        {
                            Sleep(200, token);
                            continue;
                        }
                        nlf?.Dispose();
                        nlf = new NlfClient();
                        nlfFor = settings.Distro;
                        Status = "Starting NLF in WSL (the first time takes a while)...";
                        if (!nlf.Start(settings.Distro, token))
                        {
                            Error = _nlfError = nlf.LastError.Length > 0 ? "NLF: " + nlf.LastError : "NLF didn't start.";
                            Status = "NLF isn't running (trying again in 10 s)";
                            nlfRetryAt = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 10;
                            nlf.Dispose();
                            nlf = null;
                            Publish(run, null);
                            continue;
                        }
                        Model = nlf.Model;
                        Error = _nlfError = null;
                    }
                    if (!nlf.Open(capturePath!, token))
                    {
                        Error = _nlfError = "NLF: " + nlf.LastError;
                        nlf.Dispose();
                        nlf = null;
                        nlfRetryAt = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 10;
                        continue;
                    }
                }
                else if (nlf != null)
                {
                    _nlfError = null;
                    nlf.Dispose();
                    nlf = null;
                    nlfFor = null;
                }

                if (!settings.Nlf && (tracker == null || trackerFor != (settings.Fingers, settings.Accurate, settings.Gpu)))
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
                        nlfPelvis = new System.Numerics.Vector3?[2];
                    }
                }
                else if (target < nextFrame)
                {
                    Sleep(4, token);
                    continue;
                }

                if (settings.Nlf && nlf != null)
                {
                    // NLF reads the frame itself (from the same file) and sends back the 3D body.
                    var found3d = nlf.Track(target, token);
                    nextFrame = target + 1;
                    lastTracked = target;
                    if (found3d == null)
                    {
                        Error = _nlfError = "NLF: " + nlf.LastError;
                        nlf.Dispose();
                        nlf = null;
                        continue;
                    }
                    if (nlf.FrameError != null)
                        Error = _nlfError = "NLF: " + nlf.FrameError;
                    else if (_nlfError != null)
                        Error = _nlfError = null;
                    var chosen = PickPeople(found3d, people, settings.SwapSides, nlfPelvis);
                    int count3d = 0;
                    var out3d = new MotionFrame?[2];
                    for (int p = 0; p < people; p++)
                    {
                        if (chosen[p] is { } person)
                        {
                            count3d++;
                            nlfPelvis[p] = person.Joints[0];
                            var frame3d = new MotionFrame();
                            NlfPose.ToFrame(person.Joints, settings.Fingers, frame3d, ref nlfFloor[p]);
                            smoothed[p] = Smooth(smoothed[p], frame3d, settings.Smoothing);
                        }
                        out3d[p] = smoothed[p]?.Clone();
                    }
                    PeopleFound = count3d;
                    PersonFound = count3d > 0;
                    if (count3d > 0)
                        Publish(run, out3d);
                    CountFrame(ref frames, ref fpsWindow);
                    Status = state.Playback == VlcLink.Playback.Playing ? "Following VLC (NLF)" : "VLC is paused - holding the pose";
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
                tracker!.ProcessPeople(src, slots, settings.Fingers, settings.GuessHidden, poses, found);

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

                CountFrame(ref frames, ref fpsWindow);
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
                nlf?.Dispose();
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

    private void CountFrame(ref int frames, ref long fpsWindow)
    {
        frames++;
        var t = Stopwatch.GetTimestamp();
        var window = (t - fpsWindow) / (double)Stopwatch.Frequency;
        if (window >= 1)
        {
            TrackFps = (float)(frames / window);
            frames = 0;
            fpsWindow = t;
        }
    }

    // Which of NLF's people is person 1 and person 2: whoever is nearest to where that person was last frame;
    // at the start, left to right across the picture (or right to left with "swap sides"), or the nearest one
    // to the camera when only one person is followed.
    private static NlfClient.Person?[] PickPeople(System.Collections.Generic.List<NlfClient.Person> found, int people, bool swap,
        System.Numerics.Vector3?[] last)
    {
        var chosen = new NlfClient.Person?[2];
        var left = new System.Collections.Generic.List<NlfClient.Person>(found);
        for (int p = 0; p < people; p++)
        {
            if (last[p] is not { } before || left.Count == 0)
                continue;
            int best = -1;
            float bestDistance = 0.6f; // metres: further than this is someone else
            for (int i = 0; i < left.Count; i++)
            {
                float d = System.Numerics.Vector3.Distance(left[i].Joints[0], before);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = i;
                }
            }
            if (best >= 0)
            {
                chosen[p] = left[best];
                left.RemoveAt(best);
            }
        }
        if (people == 1)
        {
            if (chosen[0] == null && left.Count > 0)
                chosen[0] = left.OrderBy(x => x.Joints[0].Z).First();
            return chosen;
        }
        var byX = left.OrderBy(x => x.Joints[0].X / System.MathF.Max(0.1f, x.Joints[0].Z)).ToList();
        if (swap)
            byX.Reverse();
        int k = 0;
        for (int p = 0; p < people && k < byX.Count; p++)
            if (chosen[p] == null)
                chosen[p] = byX[k++];
        return chosen;
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
