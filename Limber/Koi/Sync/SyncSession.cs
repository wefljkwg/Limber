using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Limber.Koi.Net;
using Dalamud.Plugin.Services;

namespace Limber.Koi.Sync;

/// <summary>
/// Two-player sync. Runs on the framework thread (call <see cref="Update"/> every frame).
///
///  * Each player connects only to their OWN relay. Relays connect to each other, so neither player
///    ever learns the other's IP address.
///  * Everything between the two games is AES-256-GCM encrypted with a key that only exists in the
///    join code, so relays only ever forward unreadable bytes.
///  * The host's Koikatsu drives both characters on both screens; the follower doesn't need Koikatsu.
/// </summary>
public sealed class SyncSession : IDisposable
{
    public enum Phase
    {
        Idle,
        Inviting,          // invite code created, waiting for someone to use it
        IncomingRequest,   // someone used our code; waiting for us to accept/decline
        Joining,           // we used someone's code; relays are connecting
        WaitingForAnswer,  // our request was delivered; waiting for them to accept/decline
        Active,
    }

    private enum Msg : byte
    {
        Request = 1, Accept = 2, Decline = 3, Bye = 4, Ping = 5,
        SwapRequest = 6, SwapAccept = 7, SwapDecline = 8, Settings = 9, Frames = 10,
    }

    public sealed record PlayerInfo(string Name, uint World, string WorldName);

    private sealed record RequestBody(string Name, uint World, string WorldName, bool WantsHost, int Version);
    private sealed record AcceptBody(string Name, uint World, string WorldName);

    private readonly Configuration _cfg;
    private readonly PoseReceiver _local;
    private readonly IPluginLog _log;
    private readonly RelayConnection _relay = new();

    private SyncCrypto? _crypto;
    private JoinCode? _code;
    private long _lastInbound, _lastPing, _lastFrames, _lastSettings, _phaseSince;
    private uint _sequence;

    public Phase State { get; private set; } = Phase.Idle;
    public bool IAmHost { get; private set; }
    public PlayerInfo? Partner { get; private set; }
    public bool PartnerWantsHost { get; private set; }
    public bool SwapPending { get; private set; }      // partner asked to swap; waiting for our answer
    public bool SwapRequested { get; private set; }    // we asked to swap; waiting for theirs
    public string? InviteCode { get; private set; }
    public string Message { get; private set; } = "";
    public bool MessageIsError { get; private set; }

    /// <summary>Poses received from the host (role 0 = host's character, 1 = follower's character).</summary>
    public PoseReceiver Incoming { get; }

    /// <summary>The host's settings, applied to synced characters on the follower's screen.</summary>
    public Configuration HostSettings { get; } = new();

    public RelayConnection.State RelayState => _relay.Status;
    public string RelayError => _relay.LastError;
    public bool RelayConfigured => !string.IsNullOrEmpty(_cfg.RelayUrl) && !string.IsNullOrEmpty(_cfg.RelayOwnerToken);

    public const int HostRole = 0;
    public const int FollowerRole = 1;

    public SyncSession(Configuration cfg, PoseReceiver local, IPluginLog log)
    {
        _cfg = cfg;
        _local = local;
        _log = log;
        Incoming = new PoseReceiver(msg => log.Warning(msg));
    }

    // ---------------------------------------------------------------------------------------
    // Relay connection

    public void ConnectRelay()
    {
        if (RelayConfigured)
            _relay.Start(_cfg.RelayUrl!, _cfg.RelayOwnerToken!);
    }

    public void DisconnectRelay()
    {
        End("Sync turned off.", notify: true);
        _relay.Stop();
    }

    // ---------------------------------------------------------------------------------------
    // User actions

    public void CreateInvite()
    {
        if (!_relay.IsConnected) { Say("Your relay isn't connected yet.", true); return; }
        ResetSession();
        _code = JoinCode.Create(_cfg.RelayUrl!);
        _crypto = new SyncCrypto(_code.Key);
        InviteCode = _code.ToString();
        _relay.SendText(JsonSerializer.Serialize(new { t = "open_room", room = _code.RoomHex, authHash = _code.AuthHashHex }));
        SetPhase(Phase.Inviting);
        Say("Invite code created. Send it to your partner however you like (whisper, Discord...).", false);
    }

    public void CancelInvite()
    {
        _relay.SendText("{\"t\":\"close_room\"}");
        ResetSession();
        SetPhase(Phase.Idle);
        Say("Invite cancelled.", false);
    }

    public void Join(string codeText, bool iWantToHost)
    {
        if (!_relay.IsConnected) { Say("Your relay isn't connected yet.", true); return; }
        if (!JoinCode.TryParse(codeText, out var code, out var error)) { Say(error, true); return; }
        if (code!.RelayHost.Equals(new Uri(_cfg.RelayUrl!).Host, StringComparison.OrdinalIgnoreCase))
        {
            Say("That's your own invite code - your partner needs to use it, not you.", true);
            return;
        }
        ResetSession();
        _code = code;
        _crypto = new SyncCrypto(code.Key);
        PartnerWantsHost = !iWantToHost;
        IAmHost = iWantToHost;
        _relay.SendText(JsonSerializer.Serialize(new { t = "join", url = code.RelayUrl, room = code.RoomHex, auth = code.AuthHex }));
        SetPhase(Phase.Joining);
        Say("Connecting to their relay...", false);
    }

    public void AcceptRequest(PlayerInfo me)
    {
        if (State != Phase.IncomingRequest) return;
        Send(Msg.Accept, JsonSerializer.SerializeToUtf8Bytes(new AcceptBody(me.Name, me.World, me.WorldName)));
        IAmHost = !PartnerWantsHost;
        SetPhase(Phase.Active);
        Say($"Synced with {Partner?.Name}. {(IAmHost ? "You are the host." : "They are the host.")}", false);
    }

    public void DeclineRequest()
    {
        if (State != Phase.IncomingRequest) return;
        Send(Msg.Decline, []);
        _relay.SendText("{\"t\":\"leave\"}");
        Partner = null;
        SetPhase(Phase.Inviting); // the invite stays open for someone else
        Say("Request declined. Your invite code still works.", false);
    }

    public void RequestSwap()
    {
        if (State != Phase.Active || SwapRequested || SwapPending) return;
        SwapRequested = true;
        Send(Msg.SwapRequest, []);
        Say(IAmHost ? "Asked your partner to take over as host..." : "Asked your partner to let you host...", false);
    }

    public void AnswerSwap(bool accept)
    {
        if (!SwapPending) return;
        SwapPending = false;
        Send(accept ? Msg.SwapAccept : Msg.SwapDecline, []);
        if (accept)
        {
            IAmHost = !IAmHost;
            Say(IAmHost ? "You are now the host." : "Your partner is now the host.", false);
        }
    }

    public void Leave() => End("Sync ended.", notify: true);

    // ---------------------------------------------------------------------------------------
    // Framework tick

    public void Update(PlayerInfo? me)
    {
        while (_relay.TryDequeue(out var m))
        {
            if (m.Text != null) OnRelayText(m.Text);
            else if (m.Binary != null) OnSealed(m.Binary, me);
        }

        FlushRequest(me);

        var now = Stopwatch.GetTimestamp();
        var connected = State is Phase.Active or Phase.WaitingForAnswer or Phase.IncomingRequest;

        if (connected && Seconds(now - _lastPing) >= 2)
        {
            _lastPing = now;
            Send(Msg.Ping, []);
        }

        if (State == Phase.Active && Seconds(now - _lastInbound) > 15)
            End("Lost connection to your partner.", notify: false, error: true);
        if (State == Phase.WaitingForAnswer && Seconds(now - _phaseSince) > 120)
            End("They didn't answer in time.", notify: true, error: true);
        if (State == Phase.Joining && Seconds(now - _phaseSince) > 30)
            End("Couldn't reach their relay. Check the code and that they still have the invite open.", notify: true, error: true);

        if (State == Phase.Active && IAmHost)
        {
            var interval = 1.0 / Math.Clamp(_cfg.SyncRate, 10, 60);
            if (Seconds(now - _lastFrames) >= interval)
            {
                _lastFrames = now;
                SendFrames();
            }
            if (Seconds(now - _lastSettings) >= 2)
            {
                _lastSettings = now;
                Send(Msg.Settings, JsonSerializer.SerializeToUtf8Bytes(SharedSettings.From(_cfg)));
            }
        }
    }

    private void SendFrames()
    {
        // Forward exactly what Koikatsu sent us, for the host's character and the follower's character.
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(++_sequence);
        long countPos = ms.Position;
        w.Write((byte)0);
        byte count = 0;
        foreach (var (role, channel) in new[] { (HostRole, _cfg.SyncHostChannel), (FollowerRole, _cfg.SyncPartnerChannel) })
        {
            if (channel < 0) continue;
            var frame = _local.GetLatest(channel);
            if (frame == null || frame.AgeSeconds > _cfg.TimeoutSeconds || frame.Raw.Length == 0 || frame.Raw.Length > 8192)
                continue;
            w.Write((byte)role);
            w.Write((ushort)frame.Raw.Length);
            w.Write(frame.Raw);
            count++;
        }
        if (count == 0)
            return;
        w.Flush();
        var bytes = ms.ToArray();
        bytes[countPos] = count;
        Send(Msg.Frames, bytes);
    }

    // ---------------------------------------------------------------------------------------
    // Incoming

    private void OnRelayText(string text)
    {
        string? type;
        string? reason = null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            type = doc.RootElement.TryGetProperty("t", out var t) ? t.GetString() : null;
            if (doc.RootElement.TryGetProperty("reason", out var r)) reason = r.GetString();
        }
        catch (JsonException)
        {
            return;
        }

        switch (type)
        {
            case "_connected":
                if (State == Phase.Inviting && _code != null) // relay reconnected: reopen our invite
                    _relay.SendText(JsonSerializer.Serialize(new { t = "open_room", room = _code.RoomHex, authHash = _code.AuthHashHex }));
                else if (State == Phase.Idle) // forget any invite left over from a previous run
                    _relay.SendText("{\"t\":\"close_room\"}");
                break;
            case "_disconnected":
                if (State is Phase.Active or Phase.WaitingForAnswer or Phase.Joining or Phase.IncomingRequest)
                    End("Lost connection to your relay.", notify: false, error: true);
                break;
            case "joined":
                if (State == Phase.Joining)
                {
                    SetPhase(Phase.WaitingForAnswer);
                    _lastInbound = Stopwatch.GetTimestamp();
                    Say("Request sent. Waiting for them to accept...", false);
                    _pendingRequest = true; // sent on next Update with our identity
                }
                break;
            case "join_failed":
                if (State == Phase.Joining)
                    End("Couldn't connect: " + (reason ?? "unknown error") + ".", notify: false, error: true);
                break;
            case "peer_connected":
                _lastInbound = Stopwatch.GetTimestamp();
                break;
            case "peer_closed":
                if (State is Phase.Active or Phase.WaitingForAnswer)
                    End("Your partner disconnected.", notify: false, error: true);
                else if (State == Phase.IncomingRequest)
                {
                    Partner = null;
                    SetPhase(Phase.Inviting);
                    Say("They cancelled their request.", false);
                }
                break;
        }
    }

    private bool _pendingRequest;

    private void OnSealed(byte[] sealedBytes, PlayerInfo? me)
    {
        if (_crypto == null) return;
        var plain = _crypto.Open(sealedBytes);
        if (plain == null || plain.Length == 0) return; // not from our partner, or tampered with: ignore
        _lastInbound = Stopwatch.GetTimestamp();
        var body = plain.AsSpan(1);

        switch ((Msg)plain[0])
        {
            case Msg.Request when State == Phase.Inviting:
            {
                var req = Deserialize<RequestBody>(body);
                if (req == null) return;
                Partner = new PlayerInfo(Clip(req.Name), req.World, Clip(req.WorldName));
                PartnerWantsHost = req.WantsHost;
                if (!_cfg.SyncAcceptRequests)
                {
                    Send(Msg.Decline, []);
                    _relay.SendText("{\"t\":\"leave\"}");
                    Partner = null;
                    return;
                }
                SetPhase(Phase.IncomingRequest);
                Say($"{Partner.Name} ({Partner.WorldName}) wants to sync.", false);
                break;
            }
            case Msg.Accept when State == Phase.WaitingForAnswer:
            {
                var acc = Deserialize<AcceptBody>(body);
                if (acc == null) return;
                Partner = new PlayerInfo(Clip(acc.Name), acc.World, Clip(acc.WorldName));
                SetPhase(Phase.Active);
                Say($"Synced with {Partner.Name}. {(IAmHost ? "You are the host." : "They are the host.")}", false);
                break;
            }
            case Msg.Decline when State == Phase.WaitingForAnswer:
                End("They declined the request.", notify: false, error: true);
                break;
            case Msg.Bye:
                if (State != Phase.Idle && State != Phase.Inviting)
                    End("Your partner ended the sync.", notify: false, error: false);
                break;
            case Msg.SwapRequest when State == Phase.Active:
                if (SwapRequested)
                {
                    // Both asked at once: the inviter's request wins, the joiner's is declined.
                    if (InviteCode != null) { Send(Msg.SwapDecline, []); return; }
                    SwapRequested = false;
                }
                SwapPending = true;
                Say(IAmHost ? "Your partner wants to take over as host." : "Your partner wants you to take over as host.", false);
                break;
            case Msg.SwapAccept when State == Phase.Active && SwapRequested:
                SwapRequested = false;
                IAmHost = !IAmHost;
                Say(IAmHost ? "You are now the host." : "Your partner is now the host.", false);
                break;
            case Msg.SwapDecline when State == Phase.Active:
                SwapRequested = false;
                Say("Your partner declined the swap.", false);
                break;
            case Msg.Settings when State == Phase.Active && !IAmHost:
                Deserialize<SharedSettings>(body)?.ApplyTo(HostSettings, _cfg);
                break;
            case Msg.Frames when State == Phase.Active && !IAmHost:
                ReadFrames(body);
                break;
        }
    }

    private void ReadFrames(ReadOnlySpan<byte> body)
    {
        if (body.Length < 5) return;
        int count = body[4];
        int o = 5;
        for (int i = 0; i < count && o + 3 <= body.Length; i++)
        {
            int role = body[o];
            int len = body[o + 1] | (body[o + 2] << 8);
            o += 3;
            if (len <= 0 || o + len > body.Length) return;
            if (role is HostRole or FollowerRole)
                Incoming.Inject(body.Slice(o, len).ToArray(), role);
            o += len;
        }
    }

    // ---------------------------------------------------------------------------------------

    /// <summary>Deliver a pending request once our identity (character name / world) is known.</summary>
    private void FlushRequest(PlayerInfo? me)
    {
        if (!_pendingRequest || me == null) return;
        _pendingRequest = false;
        Send(Msg.Request, JsonSerializer.SerializeToUtf8Bytes(new RequestBody(me.Name, me.World, me.WorldName, IAmHost, 1)));
    }

    private void Send(Msg type, byte[] body)
    {
        if (_crypto == null || !_relay.IsConnected) return;
        var plain = new byte[body.Length + 1];
        plain[0] = (byte)type;
        body.CopyTo(plain, 1);
        _relay.SendBinary(_crypto.Seal(plain));
    }

    private void End(string message, bool notify, bool error = false)
    {
        if (notify && State is Phase.Active or Phase.WaitingForAnswer or Phase.IncomingRequest)
            Send(Msg.Bye, []);
        if (State != Phase.Idle)
        {
            // The inviter closes their invite room (which also drops the partner); the joiner just leaves.
            _relay.SendText(InviteCode != null ? "{\"t\":\"close_room\"}" : "{\"t\":\"leave\"}");
        }
        ResetSession();
        SetPhase(Phase.Idle);
        Say(message, error);
    }

    private void ResetSession()
    {
        _crypto?.Dispose();
        _crypto = null;
        _code = null;
        InviteCode = null;
        Partner = null;
        SwapPending = SwapRequested = false;
        _pendingRequest = false;
        IAmHost = false;
        Incoming.Stop(); // clears buffered poses
    }

    private void SetPhase(Phase phase)
    {
        State = phase;
        _phaseSince = Stopwatch.GetTimestamp();
        if (phase == Phase.Active)
            _lastInbound = _phaseSince;
    }

    private void Say(string message, bool error)
    {
        Message = message;
        MessageIsError = error;
    }

    private static double Seconds(long ticks) => ticks / (double)Stopwatch.Frequency;

    private static string Clip(string? s) => string.IsNullOrEmpty(s) ? "?" : s.Length > 40 ? s[..40] : s;

    private static T? Deserialize<T>(ReadOnlySpan<byte> json) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(json); }
        catch (JsonException) { return null; }
    }

    public void Dispose()
    {
        End("", notify: true);
        _relay.Dispose();
        Incoming.Dispose();
    }
}

/// <summary>The host's retargeting settings, sent to the follower so both screens match.</summary>
public sealed class SharedSettings
{
    public bool DriveTorso { get; set; } = true;
    public bool DriveHead { get; set; } = true;
    public bool DriveArms { get; set; } = true;
    public bool DriveHands { get; set; } = true;
    public bool DriveLegs { get; set; } = true;
    public bool DriveGenitals { get; set; } = true;
    public bool DriveBreasts { get; set; } = true;
    public float BreastStrength { get; set; } = 1f;
    public bool ApplyRootHeight { get; set; } = true;
    public bool ApplyRootHorizontal { get; set; }
    public bool MirrorLeftRight { get; set; }
    public bool FlipFacing { get; set; }
    public float Smoothing { get; set; } = 0.35f;
    public float HeadPitchOffset { get; set; }
    public bool DriveFace { get; set; } = true;
    public int ExpressionMode { get; set; } = 1;
    public float ExpressionStrength { get; set; } = 1f;
    public bool ExpressionEyelids { get; set; } = true;
    public float EyelidStrength { get; set; } = 1f;
    public float EyelidClearance { get; set; } = 0.05f;
    public bool ExpressionBrows { get; set; } = true;
    public bool ExpressionMouth { get; set; } = true;
    public bool ExpressionCheeks { get; set; } = true;
    public bool DriveGaze { get; set; } = true;
    public float GazeMaxAngle { get; set; } = 25f;
    public bool DriveJaw { get; set; } = true;
    public float JawMaxAngle { get; set; } = 18f;

    public static SharedSettings From(Configuration c) => new()
    {
        DriveTorso = c.DriveTorso, DriveHead = c.DriveHead, DriveArms = c.DriveArms, DriveHands = c.DriveHands,
        DriveLegs = c.DriveLegs, DriveGenitals = c.DriveGenitals, DriveBreasts = c.DriveBreasts,
        BreastStrength = c.BreastStrength, ApplyRootHeight = c.ApplyRootHeight, ApplyRootHorizontal = c.ApplyRootHorizontal,
        MirrorLeftRight = c.MirrorLeftRight, FlipFacing = c.FlipFacing, Smoothing = c.Smoothing,
        HeadPitchOffset = c.HeadPitchOffset, DriveFace = c.DriveFace, ExpressionMode = (int)c.ExpressionMode,
        ExpressionStrength = c.ExpressionStrength, ExpressionEyelids = c.ExpressionEyelids, EyelidStrength = c.EyelidStrength,
        EyelidClearance = c.EyelidClearance, ExpressionBrows = c.ExpressionBrows, ExpressionMouth = c.ExpressionMouth,
        ExpressionCheeks = c.ExpressionCheeks, DriveGaze = c.DriveGaze, GazeMaxAngle = c.GazeMaxAngle,
        DriveJaw = c.DriveJaw, JawMaxAngle = c.JawMaxAngle,
    };

    /// <summary>
    /// Copy the host's settings into the follower's sync settings. The follower's own "don't show on my
    /// screen" choices always win, and captured face presets (which live on the host's PC) become Automatic.
    /// </summary>
    public void ApplyTo(Configuration target, Configuration local)
    {
        target.DriveTorso = DriveTorso; target.DriveHead = DriveHead; target.DriveArms = DriveArms;
        target.DriveHands = DriveHands; target.DriveLegs = DriveLegs;
        target.DriveGenitals = DriveGenitals && local.SyncShowGenitals;
        target.DriveBreasts = DriveBreasts && local.SyncShowBreasts;
        target.BreastStrength = Math.Clamp(BreastStrength, 0f, 3f);
        target.ApplyRootHeight = ApplyRootHeight; target.ApplyRootHorizontal = ApplyRootHorizontal;
        target.MirrorLeftRight = MirrorLeftRight; target.FlipFacing = FlipFacing;
        target.Smoothing = Math.Clamp(Smoothing, 0f, 0.95f);
        target.HeadPitchOffset = Math.Clamp(HeadPitchOffset, -45f, 45f);
        target.DriveFace = DriveFace;
        target.ExpressionMode = ExpressionMode == (int)global::Limber.ExpressionMode.Off ? global::Limber.ExpressionMode.Off : global::Limber.ExpressionMode.Automatic;
        target.ExpressionStrength = Math.Clamp(ExpressionStrength, 0f, 3f);
        target.ExpressionEyelids = ExpressionEyelids;
        target.EyelidStrength = Math.Clamp(EyelidStrength, 0f, 2f);
        target.EyelidClearance = Math.Clamp(EyelidClearance, 0f, 0.5f);
        target.ExpressionBrows = ExpressionBrows; target.ExpressionMouth = ExpressionMouth; target.ExpressionCheeks = ExpressionCheeks;
        target.DriveGaze = DriveGaze; target.GazeMaxAngle = Math.Clamp(GazeMaxAngle, 0f, 60f);
        target.DriveJaw = DriveJaw; target.JawMaxAngle = Math.Clamp(JawMaxAngle, 0f, 45f);
        // Timing settings stay the follower's own.
        target.Interpolate = local.Interpolate;
        target.TimeoutSeconds = Math.Max(local.TimeoutSeconds, 1.5f);
        target.Enabled = true;
    }
}
