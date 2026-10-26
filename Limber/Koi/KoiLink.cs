using System;
using System.Linq;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Limber.Koi.Net;
using Limber.Koi.Posing;
using Limber.Protocol;
using Limber.Koi.Sync;
using GameObjectStruct = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using CharacterStruct = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace Limber.Koi;

/// <summary>What the UI shows about one link this frame.</summary>
public sealed record LinkView(string ActorName, string RaceKey, ushort ObjectIndex);

/// <summary>
/// The Koikatsu live link (formerly the KoiPoseLink plugin): receives poses from the Koikatsu sender, resolves
/// which characters they drive each frame, and runs two-player sync. Posing itself happens in
/// <see cref="PoseApplier.RunFrame"/>, called from Limber's shared bone-update hook.
/// </summary>
public sealed unsafe class KoiLink : IDisposable
{
    private static IClientState ClientState => Plugin.ClientState;
    private static IObjectTable ObjectTable => Plugin.ObjectTable;
    private static ITargetManager TargetManager => Plugin.TargetManager;
    private static IFramework Framework => Plugin.Framework;
    private static IPluginLog Log => Plugin.Log;
    private static ICondition Condition => Plugin.Condition;

    /// <summary>LinkConfig.TargetObjectIndex value meaning "my own character".</summary>
    public const int MyCharacter = -1;
    public const int MaxLinks = Wire.MaxChannels;

    public Configuration Config { get; }
    public PoseReceiver Receiver { get; }
    public PoseApplier Applier { get; }
    public SyncSession Sync { get; }
    /// <summary>Your character's name and home world (framework thread), or null while loading.</summary>
    public SyncSession.PlayerInfo? CurrentPlayer { get; private set; }
    /// <summary>Whether your sync partner's character was found on this screen this frame.</summary>
    public bool SyncPartnerVisible { get; private set; }

    /// <summary>Resolved actor per link id, refreshed every frame (main thread only).</summary>
    public Dictionary<Guid, LinkView> LinkViews { get; } = new();

    private readonly List<LinkTarget> _scratch = new();

    public KoiLink(Configuration config)
    {
        Config = config;
        SanitizeConfig();

        Receiver = new PoseReceiver(msg => Log.Warning(msg));
        Receiver.Start(Config.Port);

        Applier = new PoseApplier(Config, Receiver, Log);

        Sync = new SyncSession(Config, Receiver, Log);
        Applier.AttachSync(Sync.Incoming, Sync.HostSettings);
        if (Config.SyncEnabled && Sync.RelayConfigured)
            Sync.ConnectRelay();

        Framework.Update += OnFrameworkUpdate;
    }

    /// <summary>Fix anything an old or hand-edited config could get wrong. Fails safe.</summary>
    private void SanitizeConfig()
    {
        // Never start driving actors automatically after a restart.
        Config.Enabled = false;

        if (Config.Port is < 1024 or > 65535)
            Config.Port = Wire.DefaultPort;

        Config.Links ??= new List<LinkConfig>();

        // v1 -> v2: the single target becomes link #1 on channel 1.
        if (Config.Version < 2)
        {
            if (Config.Links.Count == 0)
                Config.Links.Add(new LinkConfig { Channel = 0, TargetObjectIndex = Config.TargetObjectIndex });
            Config.TargetObjectIndex = 0;
            Config.Version = 2;
        }
        // v2 -> v3: KoiPoseLink became part of Limber (Limber's own options live in Config.Ik).
        if (Config.Version < Configuration.CurrentVersion)
        {
            // A relay deployed by KoiPoseLink speaks the old protocol: forget it so the Sync tab offers
            // "Deploy my relay" again (that sets up the new one on the same Cloudflare account).
            if (Config.RelayUrl != null && Config.RelayUrl.Contains("koiposelink-relay", StringComparison.OrdinalIgnoreCase))
            {
                Config.RelayUrl = null;
                Config.RelayOwnerToken = null;
                Config.SyncEnabled = false;
            }
            Config.Version = Configuration.CurrentVersion;
        }

        if (Config.Links.Count == 0)
            Config.Links.Add(new LinkConfig { Channel = 0, TargetObjectIndex = 0 });
        if (Config.Links.Count > MaxLinks)
            Config.Links.RemoveRange(MaxLinks, Config.Links.Count - MaxLinks);

        var seen = new HashSet<Guid>();
        foreach (var link in Config.Links)
        {
            if (link.Id == Guid.Empty || !seen.Add(link.Id))
            {
                link.Id = Guid.NewGuid();
                seen.Add(link.Id);
            }
            link.Channel = Math.Clamp(link.Channel, 0, Wire.MaxChannels - 1);
            // Only ever 0 (= GPose target), -1 (= my character) or a client-only GPose actor slot.
            if (link.TargetObjectIndex is not (0 or MyCharacter) && !Safety.IsClientOnlyIndex(link.TargetObjectIndex))
                link.TargetObjectIndex = 0;
        }
        Config.Save();
    }

    // Fixed ids for the sync targets (one runtime each, kept across frames).
    private static readonly Guid SyncHostSelf = new("6b1f2a10-0000-4000-8000-000000000001");
    private static readonly Guid SyncHostPartner = new("6b1f2a10-0000-4000-8000-000000000002");
    private static readonly Guid SyncFollowSelf = new("6b1f2a10-0000-4000-8000-000000000003");
    private static readonly Guid SyncFollowPartner = new("6b1f2a10-0000-4000-8000-000000000004");

    private readonly record struct ResolvedActor(ushort Index, nint Address, nint Draw, string RaceKey, bool AllowAdult, string Name);

    private static ResolvedActor Resolve(IGameObject? go)
    {
        if (go is ICharacter chara && chara.Address != 0)
        {
            var native = (GameObjectStruct*)chara.Address;
            var draw = native->DrawObject;
            if (draw != null && draw->Object.GetObjectType() == ObjectType.CharacterBase)
            {
                var customize = ((CharacterStruct*)chara.Address)->DrawData.CustomizeData;
                return new ResolvedActor(chara.ObjectIndex, chara.Address, (nint)draw,
                    FaceDriver.RaceKey(customize.Tribe, customize.Sex),
                    Safety.AdultAllowed(customize.Race, customize.BodyType),
                    chara.Name.TextValue);
            }
        }
        return new ResolvedActor(0, 0, 0, "any", false, "");
    }

    private void AddSyncTarget(Guid id, int channel, IGameObject? go, bool fromSync)
    {
        var r = Resolve(go);
        _scratch.Add(new LinkTarget(id, channel, r.Address != 0, r.Index, r.Address, r.Draw, r.RaceKey, r.AllowAdult,
            null, IsSync: true, FromSyncReceiver: fromSync));
    }

    /// <summary>
    /// Find a synced character on this screen. In GPose: its GPose copy (slots 200+) by name, falling back
    /// to the open-world object. Outside GPose: you (slot 0), or the player with that exact name and home world.
    /// </summary>
    private static IGameObject? FindCharacter(string name, uint world, bool gposing, bool isSelf)
    {
        if (gposing)
        {
            for (int i = Safety.ClientObjectStart; i < Math.Min(Safety.ClientObjectEnd, ObjectTable.Length); i++)
                if (ObjectTable[i] is ICharacter c && c.Name.TextValue == name)
                    return c;
        }
        if (isSelf)
            return gposing ? null : ObjectTable.LocalPlayer;
        for (int i = 1; i < Safety.ClientObjectStart && i < ObjectTable.Length; i++)
        {
            if (ObjectTable[i] is Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter pc &&
                pc.Name.TextValue == name && pc.HomeWorld.RowId == world)
                return pc;
        }
        return null;
    }

    public void RestartReceiver() => Receiver.Start(Config.Port);

    /// <summary>Whether any Koikatsu channel is sending right now.</summary>
    public bool KoikatsuLive
    {
        get
        {
            for (int ch = 0; ch < Wire.MaxChannels; ch++)
            {
                var f = Receiver.GetLatest(ch);
                if (f != null && f.AgeSeconds <= Config.TimeoutSeconds)
                    return true;
            }
            return false;
        }
    }

    /// <summary>Whether a live Koikatsu channel reports its animations as paused.</summary>
    public bool KoikatsuAnimationPaused
    {
        get
        {
            for (int ch = 0; ch < Wire.MaxChannels; ch++)
            {
                var f = Receiver.GetLatest(ch);
                if (f != null && f.AgeSeconds <= Config.TimeoutSeconds && f.SenderAnimPaused)
                    return true;
            }
            return false;
        }
    }

    private long _lastLinksSent;

    /// <summary>
    /// Which FFXIV actor each link's channel drives, for Koikatsu's channel window. Loopback only (127.0.0.1, the
    /// sender's control port); names never leave this PC.
    /// </summary>
    private void SendLinksInfo(bool gposing)
    {
        try
        {
            var ms = new System.IO.MemoryStream();
            var w = new System.IO.BinaryWriter(ms);
            w.Write(Wire.ControlMagic);
            w.Write(Wire.CmdLinks);
            var links = Config.Links.Take(32).ToList();
            w.Write((byte)links.Count);
            foreach (var link in links)
            {
                LinkViews.TryGetValue(link.Id, out var view);
                string name = !string.IsNullOrEmpty(view?.ActorName) ? view!.ActorName
                    : link.TargetObjectIndex switch { 0 => "GPose target", MyCharacter => "My character", _ => $"Actor #{link.TargetObjectIndex}" };
                var bytes = System.Text.Encoding.UTF8.GetBytes(name.Length > 48 ? name[..48] : name);
                w.Write((byte)Math.Clamp(link.Channel, 0, Wire.MaxChannels - 1));
                w.Write((byte)(Config.Enabled && link.Enabled && view != null && view.ObjectIndex != 0 ? 1 : 0));
                w.Write((ushort)bytes.Length);
                w.Write(bytes);
            }
            var packet = ms.ToArray();
            using var udp = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
            udp.Send(packet, packet.Length, new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, Wire.ControlPort(Config.Port)));
        }
        catch
        {
            // Koikatsu not running: nothing to tell.
        }
    }

    /// <summary>
    /// Ask the Koikatsu sender to pause, resume or toggle its characters' animations. One small packet to
    /// 127.0.0.1 only (the sender's control port, pose port + 1); nothing leaves this PC.
    /// </summary>
    public void SendAnimationCommand(byte command)
    {
        try
        {
            using var udp = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
            var packet = new byte[5];
            BitConverter.GetBytes(Wire.ControlMagic).CopyTo(packet, 0);
            packet[4] = command;
            udp.Send(packet, packet.Length, new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, Wire.ControlPort(Config.Port)));
        }
        catch (Exception e)
        {
            Log.Warning(e, "Limber: couldn't send the pause / resume request to Koikatsu");
        }
    }

    /// <summary>
    /// Runs on the framework thread: resolve each link's actor here, where the object table is safe to
    /// read, and hand only a snapshot of raw pointers to the skeleton hook (which re-validates them).
    /// </summary>
    private void OnFrameworkUpdate(IFramework framework)
    {
        var gposing = ClientState.IsGPosing && Safety.GameIsInGPose();
        var inCutscene = Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.OccupiedInCutSceneEvent] ||
                         Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.WatchingCutscene] ||
                         Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.WatchingCutscene78];
        // Outside GPose: only "My character" links, only when enabled, never during cutscenes.
        var overworld = !gposing && Config.AllowOutsideGPose && !inCutscene;
        _scratch.Clear();
        LinkViews.Clear();

        // ---- Sync session (network messages, pose streaming) ----
        var me = ObjectTable.LocalPlayer is { } lp
            ? new SyncSession.PlayerInfo(lp.Name.TextValue, lp.HomeWorld.RowId, lp.HomeWorld.ValueNullable?.Name.ExtractText() ?? "?")
            : null;
        CurrentPlayer = me;
        Sync.Update(me);

        var syncActive = Sync.State == SyncSession.Phase.Active && Sync.Partner != null && me != null;
        var syncOverworld = syncActive && !gposing && Config.SyncOutsideGPose && !inCutscene;
        SyncPartnerVisible = false;
        int partnerIndex = -1;

        if (syncActive && (gposing || syncOverworld))
        {
            var self = FindCharacter(me!.Name, me.World, gposing, isSelf: true);
            var partner = FindCharacter(Sync.Partner!.Name, Sync.Partner.World, gposing, isSelf: false);
            if (partner != null)
                partnerIndex = partner.ObjectIndex;
            SyncPartnerVisible = partner != null;

            if (Sync.IAmHost)
            {
                // Host: my Koikatsu drives me and my partner on my screen, with my settings.
                AddSyncTarget(SyncHostSelf, Config.SyncHostChannel, self, fromSync: false);
                if (Config.SyncPartnerChannel >= 0)
                    AddSyncTarget(SyncHostPartner, Config.SyncPartnerChannel, partner, fromSync: false);
            }
            else
            {
                // Follower: the host's poses drive the host and me on my screen, with the host's settings.
                AddSyncTarget(SyncFollowPartner, SyncSession.HostRole, partner, fromSync: true);
                AddSyncTarget(SyncFollowSelf, SyncSession.FollowerRole, self, fromSync: true);
            }
        }

        // ---- Normal links ----
        foreach (var link in Config.Links)
        {
            IGameObject? go = null;
            if (gposing)
            {
                go = link.TargetObjectIndex switch
                {
                    0 => TargetManager.GPoseTarget,
                    // In GPose your character is shown by its GPose copy (slot 201).
                    MyCharacter => ObjectTable[Safety.ClientObjectStart + 1],
                    _ => ObjectTable[link.TargetObjectIndex],
                };
                if (go is not ICharacter { } gc || !Safety.IsClientOnlyIndex(gc.ObjectIndex))
                    go = null; // GPose: client-only GPose actors only (object table slots 200+)
            }
            else if (overworld && link.TargetObjectIndex == MyCharacter)
            {
                go = ObjectTable.LocalPlayer;
                if (go == null || !Safety.IsAllowedOutsideGPose(go.ObjectIndex))
                    go = null; // outside GPose: your own character and nothing else
            }

            var resolved = Resolve(go);
            _scratch.Add(new LinkTarget(link.Id, link.Channel, link.Enabled, resolved.Index, resolved.Address, resolved.Draw,
                resolved.RaceKey, resolved.AllowAdult, link.FaceNeutral));
            LinkViews[link.Id] = new LinkView(resolved.Name, resolved.RaceKey, resolved.Index);
        }

        Applier.SetTargets(gposing, overworld, _scratch.ToArray(), syncOverworld, partnerIndex);

        // About once a second: tell Koikatsu which FFXIV actor each channel drives (its channel window).
        if (Environment.TickCount64 - _lastLinksSent > 1000)
        {
            _lastLinksSent = Environment.TickCount64;
            SendLinksInfo(gposing);
        }

        if (Applier.SaveRequested)
        {
            Applier.SaveRequested = false;
            lock (FaceDriver.PresetLock)
                Config.Save();
        }
    }

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;
        Sync.Dispose();
        Receiver.Dispose();
        Config.Enabled = false;
        lock (FaceDriver.PresetLock)
            Config.Save();
    }
}
