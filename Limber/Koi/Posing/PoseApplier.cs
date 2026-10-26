using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Dalamud.Plugin.Services;
using Limber.Koi.Net;
using CharacterBase = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase;
using GameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using GameObjectManager = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObjectManager;
using ObjectType = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.ObjectType;

namespace Limber.Koi.Posing;

/// <summary>One link resolved on the framework thread: which Koikatsu channel drives which GPose actor.</summary>
public readonly record struct LinkTarget(
    Guid Id,
    int Channel,
    bool Enabled,
    ushort ObjectIndex,
    nint GameObject,
    nint CharacterBase,
    string RaceKey,
    bool AllowGenitals,
    float[]? FaceNeutral,
    bool IsSync = false,
    bool FromSyncReceiver = false);

/// <summary>Per-link runtime state, readable by the UI.</summary>
public sealed class LinkRuntime
{
    public ActorDriver Driver { get; }
    public volatile string Status = "Idle";
    public volatile string? PendingCapture;
    /// <summary>Result of the last capture, kept until the next one (Status changes every frame).</summary>
    public volatile string? CaptureResult;
    public LinkRuntime(ActorDriver driver) => Driver = driver;
}

/// <summary>
/// For every enabled link, overwrites that actor's bones with the retargeted Koikatsu pose. Runs inside Limber's
/// shared hook on the game's per-frame skeleton update (after animation, IK and physics), before Limber's goals.
/// </summary>
public sealed unsafe class PoseApplier
{
    private readonly Configuration _cfg;
    private readonly PoseReceiver _receiver;
    private readonly IPluginLog _log;
    private readonly IReadOnlyList<RetargetEntry> _map = RetargetMap.Build();
    private readonly ConcurrentDictionary<Guid, LinkRuntime> _runtimes = new();

    // Published by the framework thread, read inside the hook.
    private volatile bool _gposing;
    private LinkTarget[] _targets = [];

    private long _lastTicks;
    private bool _frameInGPose, _frameOverworld;
    private string? _lastError;
    private readonly HashSet<nint> _drivenThisFrame = new();

    public volatile bool SaveRequested;

    public PoseApplier(Configuration cfg, PoseReceiver receiver, IPluginLog log)
    {
        _cfg = cfg;
        _receiver = receiver;
        _log = log;
    }

    public LinkRuntime GetRuntime(Guid id, bool useSyncSettings = false)
        => _runtimes.GetOrAdd(id, _ => new LinkRuntime(new ActorDriver(useSyncSettings && _syncCfg != null ? _syncCfg : _cfg, _log, _map)));

    // Sync: poses from the host and the host's settings (follower side), plus who the partner is.
    private PoseReceiver? _syncReceiver;
    private Configuration? _syncCfg;
    private volatile bool _syncOverworld;
    private volatile int _partnerIndex = -1;
    private bool _frameSyncOverworld;

    public void AttachSync(PoseReceiver incoming, Configuration hostSettings)
    {
        _syncReceiver = incoming;
        _syncCfg = hostSettings;
    }

    public void ForgetLink(Guid id) => _runtimes.TryRemove(id, out _);

    /// <summary>Framework thread: publish the resolved links for this frame.</summary>
    private volatile bool _overworld;

    public void SetTargets(bool gposing, bool overworld, LinkTarget[] targets, bool syncOverworld = false, int partnerIndex = -1)
    {
        foreach (var t in targets)
            GetRuntime(t.Id, t.FromSyncReceiver); // create runtimes off the hot path
        _gposing = gposing;
        _overworld = overworld;
        _syncOverworld = syncOverworld;
        _partnerIndex = partnerIndex;
        Volatile.Write(ref _targets, targets);
    }

    /// <summary>
    /// Re-check a link's actor inside the hook without trusting a possibly stale pointer: look the slot
    /// up in the game's own object table (static memory) and only dereference if it still holds the
    /// same object. Also enforces the client-only object range and the draw-object type.
    /// </summary>
    private static CharacterBase* ValidatedTarget(in LinkTarget t, bool inGPose, bool overworld, bool syncOverworld, int partnerIndex)
    {
        if (t.GameObject == 0 || t.CharacterBase == 0)
            return null;
        // In GPose: client-only GPose actors (or your verified sync partner).
        // Outside GPose: your own character (opt-in), or - while synced - you and your verified partner.
        bool isPartner = t.IsSync && partnerIndex >= 0 && t.ObjectIndex == partnerIndex;
        var allowed = inGPose
            ? Safety.IsClientOnlyIndex(t.ObjectIndex) || isPartner
            : (overworld && Safety.IsAllowedOutsideGPose(t.ObjectIndex))
              || (t.IsSync && syncOverworld && (Safety.IsAllowedOutsideGPose(t.ObjectIndex) || isPartner));
        if (!allowed)
            return null;

        var mgr = GameObjectManager.Instance();
        if (mgr == null)
            return null;
        var slots = mgr->Objects.IndexSorted;
        if (t.ObjectIndex >= slots.Length)
            return null;

        GameObject* go = slots[t.ObjectIndex].Value;
        if ((nint)go != t.GameObject || go->ObjectIndex != t.ObjectIndex)
            return null;

        var draw = go->DrawObject;
        if (draw == null || (nint)draw != t.CharacterBase)
            return null;
        if (draw->Object.GetObjectType() != ObjectType.CharacterBase)
            return null;
        return (CharacterBase*)draw;
    }

    /// <summary>Called from the shared bone-update hook once per frame, after the game's own update.</summary>
    public void RunFrame()
    {
        try
        {
            Run();
        }
        catch (Exception e)
        {
            // This runs every frame; only log each distinct failure once.
            if (e.Message != _lastError)
            {
                _lastError = e.Message;
                _log.Error(e, "Limber: error while applying the Koikatsu pose");
            }
        }
    }

    private void Run()
    {
        var now = Stopwatch.GetTimestamp();
        var dt = _lastTicks == 0 ? 1f / 60f : (float)((now - _lastTicks) / (double)Stopwatch.Frequency);
        _lastTicks = now;
        dt = System.Math.Clamp(dt, 0f, 0.25f);

        var targets = Volatile.Read(ref _targets);

        // GPose flag published on the framework thread AND the game's flag read right now.
        // Leaving GPose stops all GPose writes on that same frame.
        var inGPose = _gposing && Safety.GameIsInGPose();
        _frameInGPose = inGPose;
        _frameOverworld = !inGPose && _overworld && !Safety.GameIsInGPose();
        _frameSyncOverworld = !inGPose && _syncOverworld && !Safety.GameIsInGPose();
        if (!inGPose && !_frameOverworld && !_frameSyncOverworld)
        {
            foreach (var t in targets)
                Idle(t, _cfg.AllowOutsideGPose ? "Waiting for GPose (or a \"My character\" link)" : "Waiting for GPose");
            return;
        }

        _drivenThisFrame.Clear();
        foreach (var t in targets)
        {
            if (!_runtimes.TryGetValue(t.Id, out var rt))
                continue;
            try
            {
                RunLink(t, rt, dt);
            }
            catch (Exception e)
            {
                rt.Status = "Error: " + e.Message;
                rt.Driver.ResetSmoothing();
                if (e.Message != _lastError)
                {
                    _lastError = e.Message;
                    _log.Error(e, $"Limber: error on channel {t.Channel + 1}");
                }
            }
        }
    }

    private void RunLink(in LinkTarget t, LinkRuntime rt, float dt)
    {
        var cb = ValidatedTarget(t, _frameInGPose, _frameOverworld, _frameSyncOverworld, _partnerIndex);
        if (cb == null)
        {
            Idle(t, t.GameObject == 0 ? "No actor selected" : "Actor not available");
            return;
        }

        if (!rt.Driver.Prepare(cb))
        {
            Idle(t, "Actor has no usable skeleton");
            return;
        }

        // Expression capture works whether or not Koikatsu is connected.
        var capture = rt.PendingCapture;
        if (capture != null)
        {
            rt.PendingCapture = null;
            var count = rt.Driver.Capture(cb, t.RaceKey, capture);
            rt.CaptureResult = count > 0 ? $"Captured '{capture}' ({count} face bones)" : "Capture failed: no face skeleton";
            SaveRequested = true;
            return;
        }

        // Sync targets run whenever a sync is active; normal links follow the master switch.
        if ((!t.IsSync && !_cfg.Enabled) || !t.Enabled)
        {
            Idle(t, "Paused");
            return;
        }

        var cfg = t.FromSyncReceiver && _syncCfg != null ? _syncCfg : _cfg;
        var receiver = t.FromSyncReceiver && _syncReceiver != null ? _syncReceiver : _receiver;
        var frame = cfg.Interpolate ? receiver.GetInterpolated(t.Channel) : receiver.GetLatest(t.Channel);
        if (frame == null || frame.AgeSeconds > cfg.TimeoutSeconds)
        {
            Idle(t, t.FromSyncReceiver ? "Waiting for your partner's poses" : $"Waiting for Koikatsu channel {t.Channel + 1}");
            return;
        }

        // Two live links on the same actor would fight; the first one in the list wins.
        // Checked after the data check so an idle link never blocks a live one.
        if (!_drivenThisFrame.Add((nint)cb))
        {
            Idle(t, "Actor is already driven by another link");
            return;
        }

        var driven = rt.Driver.Drive(cb, frame, t.RaceKey, dt, t.AllowGenitals, t.FaceNeutral);
        rt.Status = $"Driving {driven}/{rt.Driver.ResolvedBones} bones from '{frame.Name}'";
        // Limber's goals run right after this, in the same hook, so held and pinned bones apply on top.
    }

    private void Idle(in LinkTarget t, string status)
    {
        if (_runtimes.TryGetValue(t.Id, out var rt))
        {
            rt.Status = status;
            rt.Driver.ResetSmoothing();
        }
    }
}
