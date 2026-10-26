using System;
using System.Diagnostics;
using Limber.Koi.Net;

namespace Limber.Video;

/// <summary>
/// Plays a <see cref="MotionClip"/> into the Koikatsu link as if Koikatsu were sending it on a channel, so any
/// link on that channel poses its character with it (retargeting, smoothing and Limber's held bones included).
/// Framework thread only.
/// </summary>
public sealed class ClipPlayer
{
    private readonly PoseReceiver _receiver;
    private long _lastTicks;
    private int _lastSent = -1;
    private long _lastSendTicks;
    private uint _packetNumber;
    /// <summary>Streaming to the channel: playing, or paused / scrubbed and holding that frame.</summary>
    private bool _active;

    public MotionClip? Clip { get; private set; }
    public bool Playing { get; private set; }
    public bool Active => _active;
    public bool Loop { get; set; } = true;
    public float Speed { get; set; } = 1f;
    public bool Mirror { get; set; }
    /// <summary>0-based Koikatsu channel the clip plays on (person 1).</summary>
    public int Channel { get; set; } = 7;
    /// <summary>0-based channel for person 2 in two-person clips.</summary>
    public int Channel2 { get; set; } = 5;
    /// <summary>Playback position in seconds.</summary>
    public double Time { get; set; }
    /// <summary>Loop section (seconds). End &lt;= 0: the end of the clip.</summary>
    public float LoopStart { get; set; }
    public float LoopEnd { get; set; }
    /// <summary>Seconds before the loop's end over which the pose eases into the loop's first pose, so the jump
    /// back to the start doesn't show.</summary>
    public float LoopBlend { get; set; } = 0.25f;

    /// <summary>The loop section actually used (clamped to the clip).</summary>
    public (double Start, double End) LoopRange
    {
        get
        {
            var clip = Clip;
            if (clip == null)
                return (0, 0);
            double d = clip.Duration;
            double end = LoopEnd > 0 ? Math.Min(LoopEnd, d) : d;
            double start = Math.Clamp(LoopStart, 0, Math.Max(0, end - 0.1));
            return (start, Math.Max(end, start + 0.1));
        }
    }

    public ClipPlayer(PoseReceiver receiver) => _receiver = receiver;

    public void Load(MotionClip? clip)
    {
        Clip = clip;
        Time = 0;
        LoopStart = 0;
        LoopEnd = 0;
        Playing = false;
        _active = false;
        _lastSent = -1;
    }

    public int FrameIndex => Clip == null || Clip.Frames.Count == 0
        ? 0
        : Math.Clamp((int)(Time * Clip.Fps), 0, Clip.Frames.Count - 1);

    public void Update()
    {
        var now = Stopwatch.GetTimestamp();
        var dt = _lastTicks == 0 ? 0 : (now - _lastTicks) / (double)Stopwatch.Frequency;
        _lastTicks = now;
        dt = Math.Clamp(dt, 0, 0.25);

        var clip = Clip;
        if (clip == null || clip.Frames.Count == 0)
            return;

        // Only stream while playing, or while paused / scrubbed (keeps that pose held).
        if (!_active)
            return;

        var (loopStart, loopEnd) = LoopRange;
        if (Playing)
        {
            Time += dt * Math.Clamp(Speed, 0.05f, 4f);
            if (Loop)
            {
                if (Time >= loopEnd || Time < loopStart)
                {
                    double length = Math.Max(loopEnd - loopStart, 1e-3);
                    Time = Time >= loopEnd ? loopStart + (Time - loopEnd) % length : loopStart;
                }
            }
            else if (Time >= clip.Duration)
            {
                Time = clip.Duration;
                Playing = false;
            }
        }

        // While playing, every update (frames are blended, so motion stays smooth at any speed); while held, a few
        // times a second so the link doesn't time out.
        int index = FrameIndex;
        var sinceSend = (now - _lastSendTicks) / (double)Stopwatch.Frequency;
        if (Playing || index != _lastSent || sinceSend > 0.1)
        {
            for (int track = 0; track < clip.People && track < 2; track++)
            {
                int channel = track == 0 ? Channel : Channel2;
                if (track > 0 && channel == Channel)
                    continue; // both people on one channel would fight; person 1 wins
                var frame = Sample(clip, track, Time, Loop, loopStart, loopEnd);
                var name = clip.People > 1 ? $"{clip.Name} ({track + 1})" : clip.Name;
                _receiver.Inject(MotionClip.Encode(frame, name, ++_packetNumber, channel, Mirror), channel);
            }
            _lastSent = index;
            _lastSendTicks = now;
        }
    }

    /// <summary>
    /// The pose at <paramref name="time"/>: blended between the two nearest frames, and (when looping) eased into
    /// the loop's first pose over the last <see cref="LoopBlend"/> seconds of the loop.
    /// </summary>
    private MotionFrame Sample(MotionClip clip, int track, double time, bool loop, double loopStart, double loopEnd)
    {
        var frame = At(clip, track, time);
        double blend = Math.Min(LoopBlend, (loopEnd - loopStart) * 0.5);
        if (loop && blend > 0.01 && time > loopEnd - blend && time <= loopEnd)
        {
            float s = (float)Math.Clamp((time - (loopEnd - blend)) / blend, 0, 1);
            s = s * s * (3f - 2f * s);
            var first = At(clip, track, loopStart);
            for (int j = 0; j < frame.P.Length; j++)
            {
                if (frame.V[j] && first.V[j])
                    frame.P[j] = System.Numerics.Vector3.Lerp(frame.P[j], first.P[j], s);
                else if (first.V[j] && s > 0.5f)
                {
                    frame.P[j] = first.P[j];
                    frame.V[j] = true;
                }
            }
        }
        return frame;
    }

    private static MotionFrame At(MotionClip clip, int track, double time)
    {
        var frames = clip.Tracks[Math.Clamp(track, 0, clip.Tracks.Count - 1)];
        if (frames.Count == 0)
            return new MotionFrame();
        double position = Math.Clamp(time * clip.Fps, 0, frames.Count - 1);
        int a = (int)position;
        int b = Math.Min(a + 1, frames.Count - 1);
        float t = (float)(position - a);
        var result = frames[a].Clone();
        if (b == a || t <= 0f)
            return result;
        var next = frames[b];
        for (int j = 0; j < result.P.Length; j++)
        {
            if (result.V[j] && next.V[j])
                result.P[j] = System.Numerics.Vector3.Lerp(result.P[j], next.P[j], t);
        }
        return result;
    }

    public void Play()
    {
        if (Clip == null || Clip.Frames.Count == 0)
            return;
        if (Time >= Clip.Duration)
            Time = Loop ? LoopRange.Start : 0;
        else if (Loop && (Time < LoopRange.Start || Time >= LoopRange.End))
            Time = LoopRange.Start;
        Playing = true;
        _active = true;
    }

    /// <summary>Pause and keep holding the current frame.</summary>
    public void Pause() => Playing = false;

    /// <summary>Show one frame (scrubbing) and keep holding it.</summary>
    public void Seek(double seconds)
    {
        if (Clip == null)
            return;
        Time = Math.Clamp(seconds, 0, Clip.Duration);
        _active = true;
        _lastSent = -1; // force a send
    }

    /// <summary>Stop sending; the character goes back to its own animation after the link's release time.</summary>
    public void Stop()
    {
        Playing = false;
        _active = false;
        Time = 0;
        _lastSent = -1;
    }
}
