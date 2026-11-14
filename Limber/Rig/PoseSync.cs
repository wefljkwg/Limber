using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using Limber.Core;
using Limber.Koi;
using Limber.Koi.Sync;

namespace Limber.Rig;

/// <summary>
/// Sync for everything (Scene > Sync): each side sends its own character's whole pose as it ends up each frame
/// (whatever moved it: Limber's posing, Limber Animations, Oral, the Koikatsu link, the game's own animation...), and on
/// the other screen that player's character takes it. Optionally each side also sends what it did to the partner's
/// character, which moves them on their screen if they allow it. Body skeleton bones, as turns relative to their
/// parents (so different races and heights still fit), plus the hips' place scaled to the height. Over the same
/// encrypted relay as the Koikatsu sync; nothing goes through the game's servers.
/// </summary>
public sealed unsafe class PoseSync
{
    private readonly KoiLink _link;
    private readonly SkeletonInfoCache _cache = new();
    private volatile byte[]? _outgoing;
    private long _lastSent;

    // (what was last put on each character, eased toward each new pose so 30 updates a second look smooth)
    private sealed class Applied
    {
        public nint Address;
        public Dictionary<uint, Quaternion> Turns = new();
        public Vector3 Hips;
        public bool Started;
    }
    private readonly Applied _partner = new(), _me = new();
    private long _lastApply;

    public string Status { get; private set; } = "";

    public PoseSync(KoiLink link) => _link = link;

    private Configuration Cfg => _link.Config;
    private SyncSession S => _link.Sync;

    private const byte Version = 1;
    private const byte WhoSender = 0, WhoReceiver = 1;

    private static uint Hash(string s)
    {
        uint h = 2166136261;
        foreach (char c in s)
        {
            h ^= c;
            h *= 16777619;
        }
        return h;
    }

    // ---- Bone update (the last stage): the poses taken, and the partner's put on

    public void UpdateFrame()
    {
        if (!Cfg.SyncPoses || S.State != SyncSession.Phase.Active)
        {
            _outgoing = null;
            _partner.Started = _me.Started = false;
            Status = "";
            return;
        }
        var self = _link.SyncSelf;
        var partner = _link.SyncPartner;
        long now = Stopwatch.GetTimestamp();
        float dt = _lastApply == 0 ? 1f / 60f : Math.Clamp((float)((now - _lastApply) / (double)Stopwatch.Frequency), 1f / 240f, 0.1f);
        _lastApply = now;

        // What came in: the partner's own character (and maybe mine).
        if (S.LatestPose is { } incoming && incoming.Age < 1.5)
            Apply(incoming.Body, self, partner, dt);
        else
            Status = "Waiting for your partner's pose...";

        // What goes out: my character (and my partner's, when I move them).
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Version);
        int count = 0;
        long countAt = ms.Position;
        w.Write((byte)0);
        if (self.Address != 0 && Write(w, WhoSender, self.Address, self.Index))
            count++;
        if (Cfg.SyncDrivePartner && partner.Address != 0 && Write(w, WhoReceiver, partner.Address, partner.Index))
            count++;
        if (count == 0)
        {
            _outgoing = null;
            return;
        }
        w.Flush();
        var bytes = ms.ToArray();
        bytes[countAt] = (byte)count;
        _outgoing = bytes;
    }

    // One character: [who][bone count][rest hips height][hips (model)] then each bone [name hash][turn (half x4)].
    private bool Write(BinaryWriter w, byte who, nint address, ushort index)
    {
        if (!ActorSkeleton.TryGet(address, index, _cache, out var sk) || sk.Info.Count == 0)
            return false;
        int n = Math.Min(sk.Info.Count, 1000);
        int hips = sk.Info.IndexOf("j_kosi");
        w.Write(who);
        w.Write((ushort)n);
        float restHips = 1f;
        if (hips >= 0 && sk.TryGetReferenceLocal(hips, out var lp, out _))
            restHips = MathF.Max(0.1f, RestHeight(sk, hips));
        w.Write(restHips);
        var hp = hips >= 0 ? sk.GetModelFrame(hips).Position : Vector3.Zero;
        w.Write(hp.X); w.Write(hp.Y); w.Write(hp.Z);
        for (int i = 0; i < n; i++)
        {
            int parent = sk.Info.ParentOf(i);
            var model = sk.GetModelFrame(i).Rotation;
            var local = parent >= 0 ? Quaternion.Normalize(Quaternion.Inverse(sk.GetModelFrame(parent).Rotation) * model) : model;
            w.Write(Hash(sk.Info.Names[i]));
            w.Write((Half)local.X); w.Write((Half)local.Y); w.Write((Half)local.Z); w.Write((Half)local.W);
        }
        return true;
    }

    // How high the hips sit at rest (model space), from the reference pose.
    private static float RestHeight(in ActorSkeleton sk, int bone)
    {
        var position = Vector3.Zero;
        var chain = new List<int>();
        for (int b = bone; b >= 0; b = sk.Info.ParentOf(b))
            chain.Add(b);
        var rotation = Quaternion.Identity;
        for (int i = chain.Count - 1; i >= 0; i--)
        {
            if (!sk.TryGetReferenceLocal(chain[i], out var p, out var r))
                continue;
            position += Vector3.Transform(p, rotation);
            rotation = Quaternion.Normalize(rotation * r);
        }
        return position.Y;
    }

    private void Apply(byte[] body, (nint Address, ushort Index) self, (nint Address, ushort Index) partner, float dt)
    {
        try
        {
            using var r = new BinaryReader(new MemoryStream(body));
            if (r.ReadByte() != Version)
                return;
            int count = r.ReadByte();
            int applied = 0;
            for (int c = 0; c < count; c++)
            {
                byte who = r.ReadByte();
                int n = r.ReadUInt16();
                float restHips = r.ReadSingle();
                var hips = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                var turns = new Dictionary<uint, Quaternion>(n);
                for (int i = 0; i < n; i++)
                {
                    uint h = r.ReadUInt32();
                    var q = new Quaternion((float)r.ReadHalf(), (float)r.ReadHalf(), (float)r.ReadHalf(), (float)r.ReadHalf());
                    if (q.LengthSquared() > 0.5f)
                        turns[h] = Quaternion.Normalize(q);
                }
                // The sender's own character is my partner here (unless I'm the one moving them); "the receiver's" is me.
                if (who == WhoSender && partner.Address != 0 && !Cfg.SyncDrivePartner)
                {
                    Put(_partner, partner, turns, hips, restHips, dt);
                    applied++;
                }
                else if (who == WhoReceiver && self.Address != 0 && Cfg.SyncLetPartnerDriveMe)
                {
                    Put(_me, self, turns, hips, restHips, dt);
                    applied++;
                }
            }
            Status = applied > 0 ? "" : partner.Address == 0 ? "Your partner's character isn't near you." : "";
        }
        catch (EndOfStreamException)
        {
            // (a cut-off message: skipped)
        }
    }

    private void Put(Applied state, (nint Address, ushort Index) who, Dictionary<uint, Quaternion> turns, Vector3 hips, float restHips, float dt)
    {
        if (!ActorSkeleton.TryGet(who.Address, who.Index, _cache, out var sk) || sk.Info.Count == 0)
            return;
        if (state.Address != who.Address)
        {
            state.Address = who.Address;
            state.Started = false;
            state.Turns.Clear();
        }
        // (eased: the pose arrives a few dozen times a second; each frame goes most of the way to the newest)
        float ease = state.Started ? 1f - MathF.Exp(-dt * 25f) : 1f;
        int hipBone = sk.Info.IndexOf("j_kosi");
        if (hipBone >= 0)
        {
            float mine = MathF.Max(0.1f, RestHeight(sk, hipBone));
            var want = hips * (mine / MathF.Max(0.1f, restHips));
            state.Hips = state.Started ? Vector3.Lerp(state.Hips, want, ease) : want;
            if (MathUtil.IsFinite(state.Hips))
                sk.SetModelPosition(hipBone, state.Hips);
        }
        for (int i = 0; i < sk.Info.Count; i++)
        {
            uint h = Hash(sk.Info.Names[i]);
            if (!turns.TryGetValue(h, out var local))
                continue;
            if (state.Turns.TryGetValue(h, out var was) && state.Started)
                local = Quaternion.Slerp(was, local, ease);
            state.Turns[h] = local;
            int parent = sk.Info.ParentOf(i);
            var parentRotation = parent >= 0 ? sk.GetModelFrame(parent).Rotation : Quaternion.Identity;
            sk.SetModelRotation(i, Quaternion.Normalize(parentRotation * local));
        }
        state.Started = true;
    }

    // ---- Framework update: the latest pose sent at the chosen rate

    public void Tick()
    {
        var bytes = _outgoing;
        if (bytes == null || S.State != SyncSession.Phase.Active)
            return;
        long now = Stopwatch.GetTimestamp();
        double interval = 1.0 / Math.Clamp(Cfg.SyncPoseRate, 5, 60);
        if ((now - _lastSent) / (double)Stopwatch.Frequency < interval)
            return;
        _lastSent = now;
        S.SendPose(bytes);
    }
}
