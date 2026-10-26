using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text;
using System.Threading;
using Limber.Protocol;

namespace Limber.Koi.Net;

/// <summary>One decoded packet from the Koikatsu sender. Immutable once published.</summary>
public sealed class PoseFrame
{
    public const int JointCount = (int)Joint.Count;

    public uint Frame { get; init; }
    public int Channel { get; init; }

    /// <summary>The original packet bytes, so a sync host can forward exactly what it received.</summary>
    public byte[] Raw { get; init; } = [];
    public long ReceivedTicks { get; init; }
    public string Name { get; init; } = string.Empty;

    public readonly Vector3[] Positions = new Vector3[JointCount];
    public readonly bool[] Valid = new bool[JointCount];

    public bool HasPatterns { get; init; }
    public bool HasEyeOpen { get; init; }
    public bool HasMouthOpen { get; init; }
    public bool HasGaze { get; init; }
    public int EyesPattern { get; init; }
    public int MouthPattern { get; init; }
    public int EyebrowPattern { get; init; }
    public float EyesOpenMax { get; init; } = 1f;
    public float EyeOpen { get; init; } = 1f;
    public float MouthOpen { get; init; }
    public Vector3 Gaze { get; init; } = Vector3.UnitZ;

    /// <summary>Face regions (see Protocol.FaceRegion); null when the sender didn't measure the face.</summary>
    public Vector3[]? RegionPos { get; init; }
    public Vector3[]? RegionDisp { get; init; }
    public bool[]? RegionValid { get; init; }

    /// <summary>The Koikatsu sender has paused its characters' animations (Ctrl+F8 or Limber's button).</summary>
    public bool SenderAnimPaused { get; init; }

    public double AgeSeconds => (Stopwatch.GetTimestamp() - ReceivedTicks) / (double)Stopwatch.Frequency;
}

/// <summary>Background UDP listener on 127.0.0.1. Only ever binds to loopback.</summary>
public sealed class PoseReceiver : IDisposable
{
    private readonly Action<string> _log;
    private UdpClient? _client;
    private Thread? _thread;
    private volatile bool _running;
    private readonly PoseFrame?[] _latest = new PoseFrame?[Wire.MaxChannels];
    private readonly FrameHistory[] _history = CreateHistories();

    private static FrameHistory[] CreateHistories()
    {
        var h = new FrameHistory[Wire.MaxChannels];
        for (int i = 0; i < h.Length; i++) h[i] = new FrameHistory();
        return h;
    }

    /// <summary>
    /// Recent frames for one channel, used to fill in the frames FFXIV draws between Koikatsu updates.
    /// </summary>
    private sealed class FrameHistory
    {
        public const int Size = 8;
        public readonly PoseFrame?[] Frames = new PoseFrame?[Size];
        public int Next;
        public double IntervalSeconds = 1.0 / 60.0; // smoothed time between Koikatsu frames
        public long LastTicks;
        public readonly object Lock = new();
    }

    /// <summary>Measured Koikatsu update rate for a channel (frames per second), or 0.</summary>
    public double GetRate(int channel)
    {
        if (channel is < 0 or >= Wire.MaxChannels) return 0;
        var h = _history[channel];
        lock (h.Lock)
            return h.LastTicks == 0 ? 0 : 1.0 / h.IntervalSeconds;
    }

    /// <summary>The delay interpolation adds for a channel, in seconds (about one Koikatsu frame).</summary>
    public double GetInterpolationDelay(int channel)
    {
        if (channel is < 0 or >= Wire.MaxChannels) return 0;
        var h = _history[channel];
        lock (h.Lock)
            return Math.Clamp(h.IntervalSeconds * 1.2, 0.0, 0.1);
    }

    /// <summary>
    /// A pose for "now minus about one Koikatsu frame", blended between the two received frames around
    /// that moment. Smooth at any FFXIV frame rate, whatever rate Koikatsu sends at.
    /// </summary>
    public PoseFrame? GetInterpolated(int channel)
    {
        if (channel is < 0 or >= Wire.MaxChannels) return null;
        var h = _history[channel];
        PoseFrame? a = null, b = null;
        double t = 0;
        lock (h.Lock)
        {
            var newest = h.Frames[(h.Next - 1 + FrameHistory.Size) % FrameHistory.Size];
            if (newest == null) return null;
            var delayTicks = (long)(Math.Clamp(h.IntervalSeconds * 1.2, 0.0, 0.1) * Stopwatch.Frequency);
            var renderTicks = Stopwatch.GetTimestamp() - delayTicks;

            // Walk back from the newest frame to find the pair that brackets the render time.
            b = newest;
            for (int i = 2; i <= FrameHistory.Size; i++)
            {
                var older = h.Frames[(h.Next - i + FrameHistory.Size * 2) % FrameHistory.Size];
                if (older == null || older.ReceivedTicks >= b.ReceivedTicks) break;
                if (older.ReceivedTicks <= renderTicks)
                {
                    a = older;
                    break;
                }
                b = older;
            }

            if (a == null || renderTicks >= b.ReceivedTicks)
                return b == newest || a == null ? b : newest;

            var span = b.ReceivedTicks - a.ReceivedTicks;
            t = span > 0 ? (double)(renderTicks - a.ReceivedTicks) / span : 1;
            t = Math.Clamp(t, 0, 1);
            // Keep the newest arrival time so the "Koikatsu stopped sending" timeout still works.
            return Blend(a, b, (float)t, newest.ReceivedTicks);
        }
    }

    private static PoseFrame Blend(PoseFrame a, PoseFrame b, float t, long receivedTicks)
    {
        Vector3[]? rPos = null, rDisp = null;
        bool[]? rValid = null;
        if (b.RegionDisp != null && b.RegionPos != null && b.RegionValid != null)
        {
            rPos = b.RegionPos;
            rValid = b.RegionValid;
            if (a.RegionDisp != null && a.RegionValid != null)
            {
                rDisp = new Vector3[b.RegionDisp.Length];
                for (int k = 0; k < rDisp.Length; k++)
                    rDisp[k] = a.RegionValid[k] && b.RegionValid[k]
                        ? Vector3.Lerp(a.RegionDisp[k], b.RegionDisp[k], t)
                        : b.RegionDisp[k];
            }
            else
            {
                rDisp = b.RegionDisp;
            }
        }

        var near = t < 0.5f ? a : b;
        var gaze = Vector3.Lerp(a.Gaze, b.Gaze, t);
        var f = new PoseFrame
        {
            Frame = b.Frame,
            Channel = b.Channel,
            ReceivedTicks = receivedTicks,
            Name = b.Name,
            HasPatterns = near.HasPatterns,
            HasEyeOpen = a.HasEyeOpen && b.HasEyeOpen,
            HasMouthOpen = a.HasMouthOpen && b.HasMouthOpen,
            HasGaze = a.HasGaze && b.HasGaze && gaze.LengthSquared() > 1e-8f,
            EyesPattern = near.EyesPattern,
            MouthPattern = near.MouthPattern,
            EyebrowPattern = near.EyebrowPattern,
            EyesOpenMax = a.EyesOpenMax + (b.EyesOpenMax - a.EyesOpenMax) * t,
            EyeOpen = a.EyeOpen + (b.EyeOpen - a.EyeOpen) * t,
            MouthOpen = a.MouthOpen + (b.MouthOpen - a.MouthOpen) * t,
            Gaze = gaze,
            RegionPos = rPos,
            RegionDisp = rDisp,
            RegionValid = rValid,
        };
        for (int i = 0; i < PoseFrame.JointCount; i++)
        {
            if (a.Valid[i] && b.Valid[i])
            {
                f.Positions[i] = Vector3.Lerp(a.Positions[i], b.Positions[i], t);
                f.Valid[i] = true;
            }
            else
            {
                f.Positions[i] = near.Positions[i];
                f.Valid[i] = near.Valid[i];
            }
        }
        return f;
    }

    public int Port { get; private set; }
    public long PacketsReceived;
    public long PacketsRejected;
    public string? LastError { get; private set; }

    public PoseFrame? GetLatest(int channel)
        => channel is >= 0 and < Wire.MaxChannels ? Volatile.Read(ref _latest[channel]) : null;

    public PoseReceiver(Action<string> log) => _log = log;

    public void Start(int port)
    {
        Stop();
        Port = port;
        try
        {
            _client = new UdpClient(AddressFamily.InterNetwork) { ExclusiveAddressUse = true };
            _client.Client.Bind(new IPEndPoint(IPAddress.Loopback, port));
            _client.Client.ReceiveTimeout = 500;
            _running = true;
            var client = _client;
            _thread = new Thread(() => Loop(client)) { IsBackground = true, Name = "Limber UDP" };
            _thread.Start();
            LastError = null;
        }
        catch (SocketException e) when (e.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
        {
            LastError = $"Port {port} is already taken by another program - usually the old KoiPoseLink plugin " +
                        "(the one with /koilink). Disable or remove it in /xlplugins, then click \"Apply port\" " +
                        "on the Link tab (or pick another port here and in Koikatsu).";
            _log(LastError);
            _client?.Dispose();
            _client = null;
        }
        catch (Exception e)
        {
            LastError = $"Could not listen on 127.0.0.1:{port} ({e.Message})";
            _log(LastError);
            _client?.Dispose();
            _client = null;
        }
    }

    public void Stop()
    {
        _running = false;
        try { _client?.Close(); } catch { /* ignored */ }
        _thread?.Join(1000);
        _thread = null;
        _client = null;
        for (int i = 0; i < _latest.Length; i++)
            Volatile.Write(ref _latest[i], null);
        foreach (var h in _history)
        {
            lock (h.Lock)
            {
                Array.Clear(h.Frames);
                h.Next = 0;
                h.LastTicks = 0;
                h.IntervalSeconds = 1.0 / 60.0;
            }
        }
    }

    private void Loop(UdpClient client)
    {
        var remote = new IPEndPoint(IPAddress.Loopback, 0);
        while (_running && ReferenceEquals(client, _client)) // a stale thread exits once a new socket replaces its own
        {
            byte[] data;
            try
            {
                data = client.Receive(ref remote);
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.TimedOut)
            {
                continue;
            }
            catch (Exception) when (!_running || !ReferenceEquals(client, _client))
            {
                break;
            }
            catch (Exception e)
            {
                LastError = e.Message;
                Thread.Sleep(100);
                continue;
            }

            // Defense in depth: loopback-bound socket, but still drop anything not from loopback.
            if (!IPAddress.IsLoopback(remote.Address))
            {
                Interlocked.Increment(ref PacketsRejected);
                continue;
            }

            var frame = TryParse(data);
            if (frame == null)
            {
                Interlocked.Increment(ref PacketsRejected);
                continue;
            }

            Interlocked.Increment(ref PacketsReceived);
            Publish(frame);
        }
    }

    /// <summary>
    /// Feed a packet that arrived some other way (sync partner) into this receiver, on the given channel.
    /// Returns false if it isn't a valid Limber packet.
    /// </summary>
    public bool Inject(byte[] packet, int channel)
    {
        var frame = TryParse(packet, channel);
        if (frame == null)
        {
            Interlocked.Increment(ref PacketsRejected);
            return false;
        }
        Interlocked.Increment(ref PacketsReceived);
        Publish(frame);
        return true;
    }

    private void Publish(PoseFrame frame)
    {
        {
            Volatile.Write(ref _latest[frame.Channel], frame);

            var h = _history[frame.Channel];
            lock (h.Lock)
            {
                if (h.LastTicks != 0)
                {
                    var dt = (frame.ReceivedTicks - h.LastTicks) / (double)Stopwatch.Frequency;
                    if (dt > 0.0005 && dt < 0.5) // ignore pauses and duplicate bursts
                        h.IntervalSeconds += (dt - h.IntervalSeconds) * 0.1;
                }
                h.LastTicks = frame.ReceivedTicks;
                h.Frames[h.Next] = frame;
                h.Next = (h.Next + 1) % FrameHistory.Size;
            }
        }
    }

    private static PoseFrame? TryParse(byte[] data, int channelOverride = -1)
    {
        if (data.Length < 12 || data.Length > Wire.MaxPacketSize)
            return null;
        try
        {
            using var ms = new MemoryStream(data, false);
            using var r = new BinaryReader(ms);

            if (r.ReadUInt32() != Wire.Magic) return null;
            if (r.ReadUInt16() != Wire.Version) return null;
            int count = r.ReadUInt16();
            var frameNo = r.ReadUInt32();
            int channel = r.ReadByte();
            if (channelOverride >= 0) channel = channelOverride;
            if (channel >= Wire.MaxChannels) return null;

            var positions = new Vector3[PoseFrame.JointCount];
            var valid = new bool[PoseFrame.JointCount];
            for (int i = 0; i < count; i++)
            {
                var ok = r.ReadByte() != 0;
                var p = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                if (i >= PoseFrame.JointCount)
                    continue; // newer sender with extra joints
                valid[i] = ok && float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
                positions[i] = p;
            }

            var flags = r.ReadByte();
            var eyesPtn = r.ReadInt32();
            var mouthPtn = r.ReadInt32();
            var browPtn = r.ReadInt32();
            var openMax = r.ReadSingle();
            var eyeOpen = r.ReadSingle();
            var mouthOpen = r.ReadSingle();
            var gaze = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            int nameLen = r.ReadUInt16();
            var nameBytes = r.ReadBytes(nameLen); // always consume, so the block after it stays aligned
            var name = nameLen > 0 && nameLen <= 256 ? Encoding.UTF8.GetString(nameBytes) : string.Empty;

            // Optional face-region block.
            Vector3[]? regionPos = null, regionDisp = null;
            bool[]? regionValid = null;
            if (ms.Position < ms.Length)
            {
                int regionCount = r.ReadByte();
                if (regionCount > 0)
                {
                    const int n = (int)FaceRegion.Count;
                    regionPos = new Vector3[n];
                    regionDisp = new Vector3[n];
                    regionValid = new bool[n];
                    for (int k = 0; k < regionCount; k++)
                    {
                        var ok = r.ReadByte() != 0;
                        var p = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                        var dsp = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                        if (k >= n) continue;
                        var finite = float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z) &&
                                     float.IsFinite(dsp.X) && float.IsFinite(dsp.Y) && float.IsFinite(dsp.Z);
                        // Clamp absurd values so a bad mesh can never fling face bones across the screen.
                        regionValid[k] = ok && finite && dsp.Length() < 5f;
                        regionPos[k] = p;
                        regionDisp[k] = regionValid[k] ? dsp : Vector3.Zero;
                    }
                }
            }

            // Optional sender state byte.
            byte senderFlags = 0;
            if (ms.Position < ms.Length)
                senderFlags = r.ReadByte();

            var frame = new PoseFrame
            {
                Frame = frameNo,
                Channel = channel,
                Raw = data,
                ReceivedTicks = Stopwatch.GetTimestamp(),
                Name = name,
                HasPatterns = (flags & Wire.FaceHasPatterns) != 0,
                HasEyeOpen = (flags & Wire.FaceHasEyeOpen) != 0 && float.IsFinite(eyeOpen),
                HasMouthOpen = (flags & Wire.FaceHasMouthOpen) != 0 && float.IsFinite(mouthOpen),
                HasGaze = (flags & Wire.FaceHasGaze) != 0 && float.IsFinite(gaze.X) && gaze.LengthSquared() > 1e-8f,
                EyesPattern = eyesPtn,
                MouthPattern = mouthPtn,
                EyebrowPattern = browPtn,
                EyesOpenMax = float.IsFinite(openMax) ? System.Math.Clamp(openMax, 0f, 1f) : 1f,
                EyeOpen = float.IsFinite(eyeOpen) ? System.Math.Clamp(eyeOpen, 0f, 1f) : 1f,
                MouthOpen = float.IsFinite(mouthOpen) ? System.Math.Clamp(mouthOpen, 0f, 1f) : 0f,
                Gaze = gaze,
                RegionPos = regionPos,
                RegionDisp = regionDisp,
                RegionValid = regionValid,
                SenderAnimPaused = (senderFlags & Wire.SenderAnimPaused) != 0,
            };
            Array.Copy(positions, frame.Positions, positions.Length);
            Array.Copy(valid, frame.Valid, valid.Length);
            return frame;
        }
        catch (EndOfStreamException)
        {
            return null;
        }
    }

    public void Dispose() => Stop();
}
