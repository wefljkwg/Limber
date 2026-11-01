using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Dalamud.Plugin.Services;
using Limber.Core;

namespace Limber.Rig;

/// <summary>Ready-made looks for the ahegao face (each sets the sliders).</summary>
public enum AhegaoType
{
    Classic,
    CrossEyed,
    Dazed,
    Panting,
    Overwhelmed,
    Giddy,
    Pained,
    Worried,
    Exhausted,
    Happy,
    Excited,
}

/// <summary>One character's ahegao face (kept by name).</summary>
public sealed class AhegaoSettings
{
    public bool Enabled { get; set; }
    public AhegaoType Type { get; set; } = AhegaoType.Classic;
    /// <summary>How far the eyes roll up (degrees).</summary>
    public float EyesUp { get; set; } = 28f;
    /// <summary>How far the eyes turn in toward each other (degrees).</summary>
    public float CrossEyes { get; set; } = 4f;
    /// <summary>How far the upper eyelids droop (+, 0..1) or open wide (-).</summary>
    public float Lids { get; set; } = 0.3f;
    /// <summary>The lower eyelids pushed up (a squint, as in a smile or a wince), 0..1.</summary>
    public float LowerLids { get; set; } = 0.1f;
    /// <summary>Eyebrows raised (+) or lowered (-), -1..1.</summary>
    public float Brows { get; set; } = 0.4f;
    /// <summary>The inner ends of the brows up (+, worried / helpless) or down (-, cross), -1..1.</summary>
    public float BrowTilt { get; set; } = 0.5f;
    /// <summary>The brows pulled together (a frown, as in pain), 0..1.</summary>
    public float Furrow { get; set; } = 0f;
    /// <summary>How far the jaw opens (degrees).</summary>
    public float MouthOpen { get; set; } = 22f;
    /// <summary>How far the tongue comes out (0..1).</summary>
    public float TongueOut { get; set; } = 0.7f;
    /// <summary>The tongue longer (0..1).</summary>
    public float TongueLength { get; set; } = 0.3f;
    /// <summary>How far the tongue hangs down over the lip (degrees).</summary>
    public float TongueDroop { get; set; } = 25f;
    /// <summary>The middle of the upper lip up (+) or down (-), -1..1.</summary>
    public float UpperLip { get; set; } = 0f;
    /// <summary>The middle of the lower lip up (+) or down (-), -1..1.</summary>
    public float LowerLip { get; set; } = 0f;
    /// <summary>Tongue physics: it springs toward its pose (a little jiggle) and bumps against the lips instead of
    /// going through them.</summary>
    public bool TonguePhysics { get; set; } = true;
    /// <summary>Hold the face: emotes' and animations' expressions (/happy, /cheer...) don't change it while the
    /// ahegao is on; it's built on the face at rest instead.</summary>
    public bool HoldFace { get; set; } = true;
    /// <summary>Use the live face rig (smooth shapes on the face's own mesh) for the lips, corners, brows, lids and
    /// cheeks, instead of only moving the face bones.</summary>
    public bool FaceRig { get; set; } = true;
    /// <summary>How loose the tongue is with physics on (0 = firm, 1 = floppy).</summary>
    public float TongueJiggle { get; set; } = 0.6f;
    /// <summary>The tongue pointing up (+, the tip curling up toward the upper lip) or down (-), degrees.</summary>
    public float TongueUp { get; set; } = 0f;
    /// <summary>The tongue moving side to side and up and down (0..1).</summary>
    public float Wiggle { get; set; } = 0.2f;
    /// <summary>Mouth corners up (+) or down (-), -1..1.</summary>
    public float Smile { get; set; } = 0.3f;
    /// <summary>The head tipped back (degrees, + = chin up).</summary>
    public float HeadTilt { get; set; } = 8f;
    /// <summary>The head leaning to one side (degrees).</summary>
    public float HeadSide { get; set; } = 0f;
    /// <summary>Panting: the mouth and tongue moving with quick breaths (0..1).</summary>
    public float Pant { get; set; } = 0.3f;
    public float PantSpeed { get; set; } = 70f;
    /// <summary>The eyes trembling (0..1).</summary>
    public float Tremble { get; set; } = 0.2f;
    public float Ease { get; set; } = 0.8f;

    public AhegaoSettings Copy() => (AhegaoSettings)MemberwiseClone();

    public void ApplyType(AhegaoType type)
    {
        Type = type;
        // eyes up, cross, lids, lower lids, brows, brow tilt, furrow, mouth, tongue out, length, droop, wiggle,
        // corners, head back, head side, pant, tremble
        var v = type switch
        {
            AhegaoType.CrossEyed =>   (22f, 14f, 0.25f, 0.1f, 0.5f, 0.6f, 0f, 22f, 0.75f, 0.35f, 28f, 0.15f, 0.3f, 6f, 0f, 0.25f, 0.15f),
            AhegaoType.Dazed =>       (18f, 3f, 0.6f, 0f, 0.1f, 0.3f, 0f, 12f, 0.35f, 0.1f, 35f, 0.05f, 0f, 3f, 4f, 0.15f, 0.1f),
            AhegaoType.Panting =>     (15f, 2f, 0.35f, 0.1f, 0.3f, 0.5f, 0f, 18f, 0.6f, 0.25f, 30f, 0.35f, 0f, 4f, 0f, 0.9f, 0.1f),
            AhegaoType.Overwhelmed => (38f, 6f, 0.15f, 0.1f, 0.8f, 0.7f, 0.1f, 30f, 0.9f, 0.5f, 22f, 0.25f, 0.1f, 18f, 0f, 0.4f, 0.45f),
            AhegaoType.Giddy =>       (25f, 5f, 0.3f, 0.35f, 0.4f, 0.3f, 0f, 20f, 0.7f, 0.3f, 20f, 0.3f, 0.9f, 6f, 3f, 0.2f, 0.15f),
            AhegaoType.Pained =>      (24f, 4f, 0.45f, 0.6f, 0.1f, 0.9f, 0.8f, 18f, 0.45f, 0.15f, 30f, 0.1f, -0.6f, 10f, 0f, 0.5f, 0.5f),
            AhegaoType.Worried =>     (20f, 3f, 0.15f, 0.15f, 0.4f, 1f, 0.35f, 14f, 0.4f, 0.1f, 30f, 0.1f, -0.35f, 2f, 0f, 0.35f, 0.25f),
            AhegaoType.Exhausted =>   (16f, 2f, 0.75f, 0f, -0.15f, 0.3f, 0f, 16f, 0.5f, 0.3f, 45f, 0.05f, -0.15f, -4f, 8f, 0.5f, 0.05f),
            AhegaoType.Happy =>       (22f, 3f, 0.35f, 0.6f, 0.3f, 0.2f, 0f, 20f, 0.7f, 0.3f, 22f, 0.25f, 1f, 6f, 3f, 0.2f, 0.1f),
            AhegaoType.Excited =>     (30f, 5f, -0.4f, 0f, 0.9f, 0.3f, 0f, 28f, 0.85f, 0.4f, 20f, 0.45f, 0.7f, 10f, 0f, 0.6f, 0.3f),
            _ =>                      (28f, 4f, 0.3f, 0.1f, 0.4f, 0.5f, 0f, 22f, 0.7f, 0.3f, 25f, 0.2f, 0.3f, 8f, 0f, 0.3f, 0.2f),
        };
        (EyesUp, CrossEyes, Lids, LowerLids, Brows, BrowTilt, Furrow, MouthOpen) = (v.Item1, v.Item2, v.Item3, v.Item4, v.Item5, v.Item6, v.Item7, v.Item8);
        (TongueOut, TongueLength, TongueDroop, Wiggle, Smile, HeadTilt, HeadSide, Pant, Tremble) = (v.Item9, v.Item10, v.Item11, v.Item12, v.Item13, v.Item14, v.Item15, v.Item16, v.Item17);
    }
}

/// <summary>
/// An ahegao face made from the face's own bones: eyes rolled up (and crossed), lids drooping, brows raised, the jaw
/// open with the tongue out and hanging over the lip, the head tipped back, panting and trembling. Works over any
/// expression; eased in and out. Adult characters only. Only on your screen.
/// </summary>
public sealed unsafe class AhegaoService
{
    private sealed class State
    {
        public float Level;
        public double Time;
        public long LastTicks;
        // What the face had before it was changed (to put back if the game doesn't rebuild it, e.g. paused).
        public int Partial = -1;
        public List<int>? Bones;
        public Frame[]? Before;
        public Frame Sentinel;
        public int SentinelBone = -1;
        public int Head = -1;
        public Quaternion HeadBefore, HeadWritten;
        public bool Wrote;
        // the tongue's physics: where each bone is and how fast it's going (in the character's space)
        public Vector3[]? TongueAt, TongueSpeed;
    }

    private readonly IObjectTable _objects;
    private readonly Func<Dictionary<string, AhegaoSettings>> _settings;
    private readonly SkeletonInfoCache _cache = new();
    private readonly Dictionary<nint, State> _states = new();
    private volatile (nint Address, ushort Index, AhegaoSettings Settings)[] _targets = [];
    private volatile Dictionary<nint, float> _levels = new();
    public Func<nint, bool> Skip = _ => false;

    public AhegaoService(IObjectTable objects, Func<Dictionary<string, AhegaoSettings>> settings)
    {
        _objects = objects;
        _settings = settings;
    }

    /// <summary>How far in it is (0..1), -1 if off.</summary>
    public float LevelOf(nint address) => _levels.TryGetValue(address, out var v) ? v : -1f;

    /// <summary>Framework thread, once a frame.</summary>
    public void Refresh()
    {
        var all = _settings();
        if(all.Count == 0)
        {
            _targets = [];
            return;
        }
        var list = new List<(nint, ushort, AhegaoSettings)>();
        foreach(var obj in _objects)
        {
            if(obj == null || obj.Address == 0)
                continue;
            string name = obj.Name.TextValue;
            if(name.Length == 0 || !all.TryGetValue(name, out var s))
                continue;
            if(!Limber.Koi.Safety.AdultAllowed(obj.Address))
                continue;
            list.Add((obj.Address, obj.ObjectIndex, s.Copy()));
        }
        _targets = list.ToArray();
    }

    public void Clear() => _targets = [];

    private long _nextRigTry;
    private string _rigTried = "";

    /// <summary>
    /// Render thread: start the live face rig on the character whose ahegao uses it (once per face model; starting
    /// searches the model's memory), stop it when nobody's does, and run it. Oral's lips mesh has the mouth while it's on.
    /// </summary>
    public void UpdateFaceRig(Func<string, byte[]?> readFile)
    {
        nint want = 0;
        var levels = _levels;
        foreach(var (address, _, s) in _targets)
            if(s.Enabled && s.FaceRig && levels.ContainsKey(address)
               && !(Limber.Gpu.LipMesh.Running && Limber.Gpu.LipMesh.Owner == address))
            {
                want = address;
                break;
            }
        if(want == 0)
        {
            if(Limber.Gpu.FaceRig.Running)
                Limber.Gpu.FaceRig.Stop();
            _rigTried = "";
            return;
        }
        if(Limber.Gpu.FaceRig.Owner != want)
        {
            long now = Environment.TickCount64;
            if(now >= _nextRigTry)
            {
                _nextRigTry = now + 1000;
                var drawObject = (nint)((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)want)->DrawObject;
                string face = "";
                if(drawObject != 0)
                    foreach(var (_, file) in Limber.Gpu.LiveMesh.ModelFiles(drawObject))
                        if(Limber.Gpu.LipMesh.IsFace(file))
                            face = file;
                string key = want + "|" + face;
                if(drawObject != 0 && face.Length > 0 && key != _rigTried)
                {
                    _rigTried = key;
                    Limber.Gpu.FaceRig.Start(want, drawObject, readFile);
                }
            }
        }
        else
            _rigTried = "";
        Limber.Gpu.FaceRig.Update();
    }

    /// <summary>Bone update stage (after look at, so the eyes roll up over it).</summary>
    public void UpdateFrame()
    {
        var targets = _targets;
        long now = Stopwatch.GetTimestamp();
        var levels = new Dictionary<nint, float>();
        var seen = new HashSet<nint>();
        foreach(var (address, index, s) in targets)
        {
            seen.Add(address);
            try
            {
                if(!_states.TryGetValue(address, out var state))
                {
                    if(!s.Enabled)
                        continue;
                    _states[address] = state = new State { LastTicks = now };
                }
                if(Skip(address))
                    continue;
                Step(address, index, s, state, now);
                if(state.Level > 0f)
                    levels[address] = state.Level;
                else if(!s.Enabled)
                    _states.Remove(address);
            }
            catch
            {
                _states.Remove(address);
            }
        }
        if(_states.Count > seen.Count)
            foreach(var a in new List<nint>(_states.Keys))
                if(!seen.Contains(a))
                    _states.Remove(a);
        _levels = levels;
    }

    private static float Smooth(float u)
    {
        u = Math.Clamp(u, 0f, 1f);
        return u * u * (3f - 2f * u);
    }

    // Smooth noise (-1..1) from a seed and time.
    private static float Noise(double t, int salt)
    {
        long i = (long)Math.Floor(t);
        float u = Smooth((float)(t - i));
        return (Hash(i, salt) * (1f - u) + Hash(i + 1, salt) * u) * 2f - 1f;
    }

    private static float Hash(long n, int salt)
    {
        unchecked
        {
            ulong x = ((ulong)n * 0x9E3779B97F4A7C15UL) ^ ((ulong)(uint)salt * 0xBF58476D1CE4E5B9UL);
            x ^= x >> 31;
            x *= 0x94D049BB133111EBUL;
            x ^= x >> 29;
            return (x & 0xFFFFFF) / (float)0x1000000;
        }
    }

    private static Quaternion Turn(Vector3 axis, float degrees)
        => axis.LengthSquared() < 1e-10f ? Quaternion.Identity : Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), degrees * MathF.PI / 180f);

    private static bool Near(Quaternion a, Quaternion b) => MathF.Abs(Quaternion.Dot(a, b)) > 0.9999999f;
    private static bool Near(Vector3 a, Vector3 b) => Vector3.DistanceSquared(a, b) < 1e-12f;

    // The bone's rest turn in model space.
    private static Quaternion RestModel(in ActorSkeleton sk, int bone)
    {
        var q = Quaternion.Identity;
        var chain = new List<int>();
        for(int b = bone; b >= 0 && chain.Count < 256; b = sk.Info.ParentOf(b))
            chain.Add(b);
        for(int i = chain.Count - 1; i >= 0; i--)
            if(sk.TryGetReferenceLocal(chain[i], out _, out var local))
                q = Quaternion.Normalize(q * local);
        return q;
    }

    /// <summary>
    /// The face's bones put to the face skeleton's rest pose (a neutral face), following the head as it is now; blended
    /// in by `amount`. Whatever expression the game's animation or an emote gave it is replaced.
    /// </summary>
    private static void Neutral(in ActorSkeleton sk, int part, SkeletonInfo info, float amount)
    {
        var pose = sk.PartialPose(part);
        if(pose == null || pose->Skeleton == null)
            return;
        int count = Math.Min(info.Count, pose->Skeleton->ReferencePose.Length);
        var made = new Frame[count];
        var has = new bool[count];
        for(int b = 0; b < count; b++)
        {
            int parent = info.ParentOf(b);
            if(parent < 0 || parent >= b || !has[parent])
            {
                made[b] = sk.GetPartialFrame(part, b); // the face's root(s): where the body has them
                has[b] = true;
                continue;
            }
            var t = pose->Skeleton->ReferencePose[b];
            var localPos = *(Vector3*)&t.Translation;
            var localRot = Quaternion.Normalize(*(Quaternion*)&t.Rotation);
            var up = made[parent];
            var parentScale = sk.GetPartialScale(part, parent); // (the head's size and the face's own scaling)
            made[b] = new Frame(up.Position + Vector3.Transform(localPos * parentScale, up.Rotation), Quaternion.Normalize(up.Rotation * localRot));
            has[b] = true;
        }
        float k = Math.Clamp(amount, 0f, 1f);
        for(int b = 0; b < count; b++)
        {
            int parent = info.ParentOf(b);
            if(parent < 0 || parent >= b)
                continue;
            var now = sk.GetPartialFrame(part, b);
            var pos = Vector3.Lerp(now.Position, made[b].Position, k);
            var rot = Quaternion.Normalize(Quaternion.Slerp(now.Rotation, made[b].Rotation, k));
            sk.SetPartialPosition(part, b, pos, children: false);
            sk.SetPartialRotation(part, b, rot, children: false);
        }
    }

    private void Step(nint address, ushort index, AhegaoSettings s, State state, long now)
    {
        float dt = Math.Clamp((float)((now - state.LastTicks) / (double)Stopwatch.Frequency), 0f, 0.1f);
        state.LastTicks = now;
        state.Level = Math.Clamp(state.Level + (s.Enabled ? dt : -dt) / MathF.Max(0.05f, s.Ease), 0f, 1f);
        state.Time += dt;
        if(!ActorSkeleton.TryGet(address, index, _cache, out var sk) || sk.Info.Count == 0)
            return;
        int head = sk.Info.IndexOf("j_kao");
        if(head < 0 || !sk.TryFindExtraBone("j_f_eye_l", _cache, out int part, out int eyeL) || !sk.TryFindExtraBone("j_f_eye_r", _cache, out int part2, out int eyeR) || part2 != part)
            return;
        var info = sk.PartialInfo(part, _cache);
        if(info == null)
            return;

        // The face wasn't rebuilt since last frame (paused): put back what it had.
        if(state.Wrote && state.Partial == part && state.Bones != null && state.Before != null && state.SentinelBone >= 0)
        {
            var nowS = sk.GetPartialFrame(part, state.SentinelBone);
            if(Near(nowS.Rotation, state.Sentinel.Rotation) && Near(nowS.Position, state.Sentinel.Position))
            {
                if(state.Head >= 0 && Near(sk.GetModelFrame(state.Head).Rotation, state.HeadWritten))
                {
                    sk.SetModelRotation(state.Head, state.HeadBefore);
                    sk.RealignPartials(_cache);
                }
                for(int i = 0; i < state.Bones.Count; i++)
                {
                    sk.SetPartialPosition(part, state.Bones[i], state.Before[i].Position, children: true);
                    sk.SetPartialRotation(part, state.Bones[i], state.Before[i].Rotation, children: true);
                }
            }
        }
        state.Wrote = false;

        float level = Smooth(state.Level);
        if(level <= 0f)
            return;
        double t = state.Time;

        // 1. The head tipped back (chin up), first, so the face follows it.
        var facing = Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, Quaternion.Normalize(sk.GetModelFrame(head).Rotation * Quaternion.Inverse(RestModel(sk, head)))));
        var upGuess = Vector3.Normalize(Vector3.Transform(Vector3.UnitY, Quaternion.Normalize(sk.GetModelFrame(head).Rotation * Quaternion.Inverse(RestModel(sk, head)))));
        float pantNow = Math.Clamp(s.Pant, 0f, 2f) * (0.5f + 0.5f * MathF.Sin((float)(t * Math.Clamp(s.PantSpeed, 10f, 400f) / 60.0 * 2 * Math.PI)));
        float tilt = (Math.Clamp(s.HeadTilt, -45f, 70f) + pantNow * 2.5f) * level;
        state.HeadBefore = sk.GetModelFrame(head).Rotation;
        state.Head = head;
        float lean = Math.Clamp(s.HeadSide, -60f, 60f) * level;
        if(MathF.Abs(tilt) > 1e-3f || MathF.Abs(lean) > 1e-3f)
        {
            sk.SetModelRotation(head, Quaternion.Normalize(Turn(facing, lean) * Turn(Vector3.Cross(facing, upGuess), tilt) * sk.GetModelFrame(head).Rotation));
            sk.RealignPartials(_cache);
        }
        state.HeadWritten = sk.GetModelFrame(head).Rotation;

        // Hold the face: the face put back to its rest (neutral) shape, so an emote's expression doesn't mix in.
        if(s.HoldFace)
            Neutral(sk, part, info, level);

        // The face's own directions now: forward, up (mouth to eyes), and its size.
        facing = Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, Quaternion.Normalize(sk.GetModelFrame(head).Rotation * Quaternion.Inverse(RestModel(sk, head)))));
        var eyeLPos = sk.GetPartialFrame(part, eyeL).Position;
        var eyeRPos = sk.GetPartialFrame(part, eyeR).Position;
        var eyesMid = (eyeLPos + eyeRPos) * 0.5f;
        float eyeGap = Vector3.Distance(eyeLPos, eyeRPos);
        if(eyeGap < 1e-4f)
            return;
        float sc = eyeGap / 0.064f;

        // Which bones this face has.
        var names = info.Names;
        int Find(string name) { for(int i = 0; i < names.Length; i++) if(names[i] == name) return i; return -1; }
        var jaw = Find("j_f_ago");
        if(jaw < 0) jaw = Find("j_f_dago");
        var lids = new List<int>();
        var lowLids = new List<int>();
        var brows = new List<int>();
        var tongue = new List<(string Name, int Bone)>();
        var corners = new List<int>();
        var cheeks = new List<int>();
        var lips = new List<int>();
        for(int i = 0; i < names.Length; i++)
        {
            string n = names[i];
            if(n.StartsWith("j_f_umab", StringComparison.Ordinal)) lids.Add(i);
            else if(n.StartsWith("j_f_dmab", StringComparison.Ordinal)) lowLids.Add(i);
            else if(n.StartsWith("j_f_mayu", StringComparison.Ordinal) || n.StartsWith("j_f_mmayu", StringComparison.Ordinal) || n.StartsWith("j_f_miken", StringComparison.Ordinal)) brows.Add(i);
            else if(n.StartsWith("j_f_bero", StringComparison.Ordinal)) tongue.Add((n, i));
            else if(n == "j_f_lip_l" || n == "j_f_lip_r") corners.Add(i);
            else if(n.StartsWith("j_f_hoho", StringComparison.Ordinal)) cheeks.Add(i);
            else if(n.StartsWith("j_f_ulip", StringComparison.Ordinal) || n.StartsWith("j_f_umlip", StringComparison.Ordinal) || n.StartsWith("j_f_uslip", StringComparison.Ordinal)
                    || n.StartsWith("j_f_dlip", StringComparison.Ordinal) || n.StartsWith("j_f_dmlip", StringComparison.Ordinal) || n.StartsWith("j_f_dslip", StringComparison.Ordinal))
                lips.Add(i);
        }
        tongue.Sort((x, y) => string.CompareOrdinal(x.Name, y.Name));

        // Up: from the mouth to the eyes (the face's own), kept square to forward.
        var mouth = eyesMid - upGuess * 0.07f * sc;
        if(lips.Count > 0)
        {
            mouth = Vector3.Zero;
            foreach(var b in lips)
                mouth += sk.GetPartialFrame(part, b).Position;
            mouth /= lips.Count;
        }
        var up = eyesMid - mouth;
        up -= facing * Vector3.Dot(up, facing);
        up = up.LengthSquared() > 1e-10f ? Vector3.Normalize(up) : upGuess;

        // Remember what the face has now (parents first), to put it back if it isn't rebuilt.
        var touched = new List<int> { eyeL, eyeR };
        if(jaw >= 0) touched.Add(jaw);
        touched.AddRange(lids);
        touched.AddRange(lowLids);
        touched.AddRange(brows);
        foreach(var (_, b) in tongue) touched.Add(b);
        touched.AddRange(corners);
        touched.AddRange(lips);
        touched.AddRange(cheeks);
        touched.Sort();
        for(int i = touched.Count - 1; i > 0; i--)
            if(touched[i] == touched[i - 1])
                touched.RemoveAt(i);
        var before = new Frame[touched.Count];
        for(int i = 0; i < touched.Count; i++)
            before[i] = sk.GetPartialFrame(part, touched[i]);
        state.Partial = part;
        state.Bones = touched;
        state.Before = before;

        var down = -up;
        var gazeUp = Vector3.Cross(facing, up);     // turning about this takes forward toward up
        var gazeDown = Vector3.Cross(facing, down); // ...and toward down

        // 2. The jaw open (more with each breath).
        if(jaw >= 0)
        {
            float open = (Math.Clamp(s.MouthOpen, 0f, 70f) + pantNow * 7f) * level;
            var j = sk.GetPartialFrame(part, jaw);
            sk.SetPartialRotation(part, jaw, Quaternion.Normalize(Turn(gazeDown, open) * j.Rotation), children: true);
        }

        // 4. The eyes rolled up (crossed in, trembling).
        float tremble = Math.Clamp(s.Tremble, 0f, 2f) * level;
        foreach(var (eye, other) in new[] { (eyeL, eyeR), (eyeR, eyeL) })
        {
            var f = sk.GetPartialFrame(part, eye);
            var inward = sk.GetPartialFrame(part, other).Position - f.Position;
            inward -= facing * Vector3.Dot(inward, facing);
            float jitterUp = tremble * 2.5f * Noise(t * 9.0, eye * 7 + 1);
            float jitterIn = tremble * 1.5f * Noise(t * 9.0, eye * 7 + 2);
            var turn = Turn(Vector3.Cross(facing, inward), Math.Clamp(s.CrossEyes, 0f, 50f) * level + jitterIn)
                     * Turn(gazeUp, Math.Clamp(s.EyesUp, 0f, 90f) * level + jitterUp);
            sk.SetPartialRotation(part, eye, Quaternion.Normalize(turn * f.Rotation), children: true);
        }

        // 5. The eyelids: the upper ones drooping (or opening wide), the lower ones pushed up (squint).
        float lid = Math.Clamp(s.Lids, -2f, 2f) * level;
        // (with the face rig on, the lids, brows, lips and cheeks are shaped by it instead: see 10.)
        bool rig = s.FaceRig && Limber.Gpu.FaceRig.Running && Limber.Gpu.FaceRig.Owner == address;
        if(!rig)
        foreach(var b in lids)
        {
            var f = sk.GetPartialFrame(part, b);
            sk.SetPartialPosition(part, b, f.Position + down * ((lid > 0f ? 0.005f : 0.0025f) * sc * lid), children: true);
        }
        float lowLid = Math.Clamp(s.LowerLids, 0f, 2f) * level + MathF.Max(0f, lid) * 0.15f;
        if(!rig)
        foreach(var b in lowLids)
        {
            var f = sk.GetPartialFrame(part, b);
            sk.SetPartialPosition(part, b, f.Position + up * (0.0035f * sc * lowLid), children: true);
        }

        // 6. The brows: raised or lowered, the inner ends up (worried) or down (cross), pulled together (pain).
        float brow = Math.Clamp(s.Brows, -2f, 2f) * level;
        float browTilt = Math.Clamp(s.BrowTilt, -2f, 2f) * level;
        float furrow = Math.Clamp(s.Furrow, 0f, 2f) * level;
        var across = Vector3.Normalize(eyeRPos - eyeLPos);
        float halfGap = eyeGap * 0.5f;
        if(!rig)
        foreach(var b in brows)
        {
            var f = sk.GetPartialFrame(part, b);
            float x = Vector3.Dot(f.Position - eyesMid, across);
            float inner = Math.Clamp(1f - MathF.Abs(x) / MathF.Max(halfGap * 1.6f, 1e-4f), 0f, 1f); // 1 at the middle, 0 at the outer ends
            float lift = 0.004f * sc * brow + 0.0035f * sc * browTilt * (2f * inner - 1f) - 0.0015f * sc * furrow * inner;
            var toMiddle = x > 0f ? -across : across;
            var pull = MathF.Abs(x) > 1e-4f ? toMiddle * (0.003f * sc * furrow * inner) : Vector3.Zero;
            sk.SetPartialPosition(part, b, f.Position + up * lift + pull, children: true);
        }

        // 7. The mouth corners up (or down) and out, with the lip bones near them following (less toward the
        //    middle), and the cheeks lifting with a smile.
        float smile = Math.Clamp(s.Smile, -2f, 2f) * level;
        if(!rig && MathF.Abs(smile) > 1e-4f)
        {
            var mouthNow = Vector3.Zero;
            var moving = new List<int>(corners);
            moving.AddRange(lips);
            foreach(var b in moving)
                mouthNow += sk.GetPartialFrame(part, b).Position;
            mouthNow /= MathF.Max(1, moving.Count);
            float halfWidth = 0f;
            foreach(var b in moving)
                halfWidth = MathF.Max(halfWidth, MathF.Abs(Vector3.Dot(sk.GetPartialFrame(part, b).Position - mouthNow, across)));
            if(halfWidth > 1e-4f)
                foreach(var b in moving)
                {
                    var f = sk.GetPartialFrame(part, b);
                    float x = Vector3.Dot(f.Position - mouthNow, across);
                    float w = corners.Contains(b) ? 1f : Smooth((MathF.Abs(x) / halfWidth - 0.3f) / 0.7f);
                    if(w <= 0f)
                        continue;
                    var outward = x > 0f ? across : -across;
                    sk.SetPartialPosition(part, b, f.Position + up * (0.009f * sc * smile * w) + outward * (0.003f * sc * MathF.Abs(smile) * w), children: false);
                }
        }
        if(!rig && smile > 0f)
            foreach(var b in cheeks)
            {
                var f = sk.GetPartialFrame(part, b);
                sk.SetPartialPosition(part, b, f.Position + up * (0.004f * sc * smile), children: true);
            }

        // 8. The middle of the lips up or down (the corners stay).
        float upperLip = Math.Clamp(s.UpperLip, -2f, 2f) * level, lowerLip = Math.Clamp(s.LowerLip, -2f, 2f) * level;
        if(!rig && (MathF.Abs(upperLip) > 1e-4f || MathF.Abs(lowerLip) > 1e-4f) && lips.Count > 0)
        {
            var centre = Vector3.Zero;
            foreach(var b in lips)
                centre += sk.GetPartialFrame(part, b).Position;
            centre /= lips.Count;
            float halfWidth = 0f;
            foreach(var b in lips)
                halfWidth = MathF.Max(halfWidth, MathF.Abs(Vector3.Dot(sk.GetPartialFrame(part, b).Position - centre, across)));
            foreach(var b in lips)
            {
                bool top = names[b].StartsWith("j_f_u", StringComparison.Ordinal);
                float amount = top ? upperLip : lowerLip;
                if(MathF.Abs(amount) < 1e-4f)
                    continue;
                var f = sk.GetPartialFrame(part, b);
                float x = halfWidth > 1e-4f ? MathF.Abs(Vector3.Dot(f.Position - centre, across)) / halfWidth : 0f;
                float w = 1f - Smooth((x - 0.25f) / 0.75f); // the middle most, fading out toward the corners
                sk.SetPartialPosition(part, b, f.Position + up * (0.004f * sc * amount * w), children: false);
            }
        }

        // 10. The face rig: the lips, corners, brows, lids and cheeks as smooth shapes on the face's own mesh.
        if(rig)
        {
            var pose = new Limber.Gpu.FaceRig.Pose();
            var o = pose.Offsets;
            float k = Limber.Gpu.FaceRig.Scale;
            float open = Math.Clamp(s.MouthOpen / 30f, 0f, 2.5f) * level;
            for(int i = 0; i < Limber.Gpu.FaceRig.LipCount; i++)
            {
                float u = i / (float)(Limber.Gpu.FaceRig.LipCount - 1) * 2f - 1f; // -1 the right corner, +1 the left
                float c = u * u, mid = 1f - c;
                // corners: up and out with a smile (and back a little), down with a frown
                var corner = new Vector3(0.0028f * MathF.Abs(smile) * c * MathF.Sign(u), 0.0065f * smile * c, -0.0015f * MathF.Max(0f, smile) * c);
                // the middles: their own sliders, and a rounder opening the wider the jaw is
                o[Limber.Gpu.FaceRig.UpperLip + i] = (corner + new Vector3(0f, 0.0045f * upperLip * mid + 0.0022f * open * mid, 0.0008f * open * mid)) * k;
                o[Limber.Gpu.FaceRig.LowerLip + i] = (corner + new Vector3(0f, 0.0045f * lowerLip * mid - 0.0022f * open * mid, 0.0008f * open * mid)) * k;
            }
            for(int i = 0; i < Limber.Gpu.FaceRig.BrowCount; i++)
            {
                float inner = i == 0 ? 1f : i == 1 ? 0.45f : 0f;
                float tiltBy = i == 0 ? 1f : i == 1 ? 0.1f : -0.7f;
                float dy = 0.0042f * brow + 0.0038f * browTilt * tiltBy - 0.0018f * furrow * inner;
                float toMiddle = 0.0032f * furrow * inner;
                o[Limber.Gpu.FaceRig.BrowL + i] = new Vector3(-toMiddle, dy, 0.0006f * furrow * inner) * k; // left brow is at +X: the middle is -X
                o[Limber.Gpu.FaceRig.BrowR + i] = new Vector3(toMiddle, dy, 0.0006f * furrow * inner) * k;
            }
            for(int i = 0; i < Limber.Gpu.FaceRig.LidCount; i++)
            {
                float middle = i == 1 ? 1f : 0.7f;
                float droop = lid > 0f ? -0.0055f * lid * middle : -0.003f * lid * middle;
                o[Limber.Gpu.FaceRig.UpperLidL + i] = o[Limber.Gpu.FaceRig.UpperLidR + i] = new Vector3(0f, droop, 0f) * k;
                float raise = 0.0038f * lowLid * (i == 1 ? 1f : 0.65f);
                o[Limber.Gpu.FaceRig.LowerLidL + i] = o[Limber.Gpu.FaceRig.LowerLidR + i] = new Vector3(0f, raise, 0.0004f * lowLid) * k;
            }
            var cheek = new Vector3(0f, 0.0045f * MathF.Max(0f, smile) + 0.0015f * lowLid, 0.0012f * MathF.Max(0f, smile)) * k;
            o[Limber.Gpu.FaceRig.CheekL] = new Vector3(0.0008f * MathF.Max(0f, smile) * k, cheek.Y, cheek.Z);
            o[Limber.Gpu.FaceRig.CheekR] = new Vector3(-0.0008f * MathF.Max(0f, smile) * k, cheek.Y, cheek.Z);
            Limber.Gpu.FaceRig.Hand(pose);
        }

        // 9. The tongue out: up over the lower lip and forward, longer, hanging down past the lip, wiggling.
        //    Kept between the lips (and out in front of the lower lip where it hangs) so it never goes through them;
        //    with physics on it springs after its pose with a little jiggle and bumps off the lips.
        if(tongue.Count > 0)
        {
            float outAmount = Math.Clamp(s.TongueOut, 0f, 4f) * level * (1f + 0.15f * pantNow);
            float wig = Math.Clamp(s.Wiggle, 0f, 2f) * level;
            var root = tongue[0].Bone;
            var rf = sk.GetPartialFrame(part, root);
            float aim = Math.Clamp(s.TongueUp, -90f, 90f);
            sk.SetPartialPosition(part, root, rf.Position + facing * (0.02f * sc * outAmount) + up * ((0.002f + 0.004f * aim / 45f) * sc * MathF.Min(outAmount, 1f)), children: true);
            float side = 18f * wig * MathF.Sin((float)(t * 2 * Math.PI * 1.3));
            float bob = 8f * wig * MathF.Sin((float)(t * 2 * Math.PI * 2.1 + 0.7));
            float droop = Math.Clamp(s.TongueDroop, 0f, 120f) * MathF.Min(outAmount, 1f);
            float stretch = Math.Clamp(s.TongueLength, 0f, 2f) * 0.6f * MathF.Min(outAmount, 1.5f);
            int n = tongue.Count;
            for(int k = 0; k < n; k++)
            {
                int b = tongue[k].Bone;
                var f = sk.GetPartialFrame(part, b);
                // the root stays level (resting on the lip); the bend is toward the tip
                float share = n > 1 ? k / (float)(n - 1) * 2f / n : 1f;
                float even = n > 1 ? 1f / n : 1f;
                var turn = Turn(up, side * even) * Turn(gazeDown, (droop + bob) * share - aim * MathF.Min(outAmount, 1f) * even);
                sk.SetPartialRotation(part, b, Quaternion.Normalize(turn * f.Rotation), children: true);
                if(k > 0 && stretch > 0f)
                {
                    var prev = sk.GetPartialFrame(part, tongue[k - 1].Bone).Position;
                    var at = sk.GetPartialFrame(part, b).Position;
                    sk.SetPartialPosition(part, b, prev + (at - prev) * (1f + stretch), children: true);
                }
            }

            // The lips (their bones, as they are now): the lower and upper lip's middles, the corners' reach.
            var lowLip = Vector3.Zero; var upLip = Vector3.Zero;
            int lowCount = 0, upCount = 0;
            foreach(var b in lips)
            {
                var pos = sk.GetPartialFrame(part, b).Position;
                if(names[b].StartsWith("j_f_d", StringComparison.Ordinal)) { lowLip += pos; lowCount++; }
                else { upLip += pos; upCount++; }
            }
            if(lowCount > 0 && outAmount > 0f)
            {
                lowLip /= lowCount;
                if(upCount > 0)
                    upLip /= upCount;
                var side3 = across - facing * Vector3.Dot(across, facing) - up * Vector3.Dot(across, up);
                side3 = side3.LengthSquared() > 1e-10f ? Vector3.Normalize(side3) : across;
                float halfWidth = 0f;
                foreach(var b in corners)
                    halfWidth = MathF.Max(halfWidth, MathF.Abs(Vector3.Dot(sk.GetPartialFrame(part, b).Position - lowLip, side3)));
                float clear = 0.0065f * sc;   // the tongue's half thickness and the lip's, off the lip bones
                float front = 0.007f * sc;    // the lip's front, ahead of its bones
                float roof = upCount > 0 ? Vector3.Dot(upLip - lowLip, up) - clear : float.MaxValue;
                float press = 0f;
                // Keeps a point between the lips (inside / at the lips) or out in front of the lower lip (hanging).
                Vector3 Keep(Vector3 p, out bool hit)
                {
                    var rel = p - lowLip;
                    float fw = Vector3.Dot(rel, facing), h = Vector3.Dot(rel, up), x = Vector3.Dot(rel, side3);
                    float fw0 = fw, h0 = h, x0 = x;
                    if(fw < front)
                    {
                        if(h < clear) { press = MathF.Max(press, clear - h); h = clear; }
                        if(roof > clear && h > roof) h = roof;
                        float reach = halfWidth - 0.004f * sc;
                        if(halfWidth > 1e-4f && reach > 0f && MathF.Abs(x) > reach) x = MathF.Sign(x) * reach;
                    }
                    else if(h < clear)
                    {
                        float need = front + 0.6f * (clear - h);
                        if(fw < need) fw = need;
                    }
                    hit = fw != fw0 || h != h0 || x != x0;
                    return hit ? p + facing * (fw - fw0) + up * (h - h0) + side3 * (x - x0) : p;
                }

                // Where the pose puts each bone, kept between the lips (the pose itself never asks it through them).
                var goal = new Vector3[n];
                for(int k = 0; k < n; k++)
                {
                    goal[k] = Keep(sk.GetPartialFrame(part, tongue[k].Bone).Position, out bool hit);
                    if(hit && !s.TonguePhysics)
                        sk.SetPartialPosition(part, tongue[k].Bone, goal[k], children: true);
                }
                if(s.TonguePhysics && n > 1)
                    Physics(sk, part, state, tongue, goal, lips, corners, names, facing, up, side3, sc, dt, s);
                else
                    state.TongueAt = null;
            }
        }
        else
            state.TongueAt = null;

        int sentinel = jaw >= 0 ? jaw : eyeL;
        state.SentinelBone = sentinel;
        state.Sentinel = sk.GetPartialFrame(part, sentinel);
        state.Wrote = true;
    }

    // ---- Tongue physics: a chain of spheres (thick at the root, thinner to the tip, with a point past the last bone
    //      for the tip itself) springing after its pose, with gravity, colliding with capsules along the actual lip
    //      bones (and the teeth); the lips give a little where it presses.

    private readonly record struct Capsule(Vector3 A, Vector3 B, float Radius, int BoneA, int BoneB, bool Upper);

    private static Vector3 Closest(Vector3 p, Vector3 a, Vector3 b, out float u)
    {
        var ab = b - a;
        float len2 = ab.LengthSquared();
        u = len2 > 1e-12f ? Math.Clamp(Vector3.Dot(p - a, ab) / len2, 0f, 1f) : 0f;
        return a + ab * u;
    }

    private void Physics(in ActorSkeleton sk, int part, State state, List<(string Name, int Bone)> tongue, Vector3[] goal,
                         List<int> lips, List<int> corners, string[] names, Vector3 facing, Vector3 up, Vector3 side, float sc, float dt, AhegaoSettings s)
    {
        int n = tongue.Count;
        int count = n + 1; // the bones and the tip
        var target = new Vector3[count];
        Array.Copy(goal, target, n);
        target[n] = goal[n - 1] + (goal[n - 1] - goal[n - 2]) * 0.8f;

        // The lips as capsules: each lip's bones in order across the mouth, the corners at both ends.
        var capsules = new List<Capsule>();
        float lipRadius = 0.0035f * sc;
        foreach(bool upper in new[] { true, false })
        {
            var row = new List<(float X, int Bone, Vector3 At)>();
            foreach(var b in lips)
                if(names[b].StartsWith("j_f_u", StringComparison.Ordinal) == upper)
                {
                    var at = sk.GetPartialFrame(part, b).Position;
                    row.Add((Vector3.Dot(at, side), b, at));
                }
            foreach(var b in corners)
            {
                var at = sk.GetPartialFrame(part, b).Position;
                row.Add((Vector3.Dot(at, side), b, at));
            }
            row.Sort((x, y) => x.X.CompareTo(y.X));
            for(int i = 0; i + 1 < row.Count; i++)
                capsules.Add(new Capsule(row[i].At, row[i + 1].At, lipRadius, row[i].Bone, row[i + 1].Bone, upper));
        }
        // The teeth: a bar across the mouth at each row.
        for(int i = 0; i < names.Length; i++)
        {
            bool upperTeeth = names[i] == "j_f_hagukiu", lowerTeeth = names[i] == "j_f_hagukid";
            if(!upperTeeth && !lowerTeeth)
                continue;
            var at = sk.GetPartialFrame(part, i).Position;
            capsules.Add(new Capsule(at - side * 0.016f * sc, at + side * 0.016f * sc, 0.003f * sc, -1, -1, upperTeeth));
        }

        // Start (or start again if it was far off).
        var p = state.TongueAt;
        var v = state.TongueSpeed;
        if(p == null || v == null || p.Length != count || Vector3.Distance(p[count - 1], target[count - 1]) > 0.06f * sc)
        {
            p = (Vector3[])target.Clone();
            v = new Vector3[count];
        }
        var lengths = new float[count];
        for(int k = 1; k < count; k++)
            lengths[k] = Vector3.Distance(target[k], target[k - 1]);
        float Radius(int k) => (0.0055f - 0.002f * k / (float)(count - 1)) * sc;

        float loose = Math.Clamp(s.TongueJiggle, 0f, 2f);
        float stiff = loose <= 1f ? 320f + (22f - 320f) * loose : 22f / (1f + 2f * (loose - 1f)); // past 100%: looser still                     // pull toward the pose
        float damping = 2f * MathF.Sqrt(stiff) * MathF.Max(0.05f, 0.55f + (0.1f - 0.55f) * MathF.Min(loose, 1f) - 0.04f * MathF.Max(0f, loose - 1f));
        var gravity = new Vector3(0f, -9.8f * (0.15f + 0.55f * MathF.Min(loose, 1.6f)), 0f);
        float span = MathF.Min(dt, 0.05f);
        int steps = Math.Max(1, (int)MathF.Ceiling(span / (1f / 240f)));
        float h = span / steps;
        var giveAt = new Dictionary<int, Vector3>();

        for(int step = 0; step < steps; step++)
        {
            p[0] = target[0];
            v[0] = Vector3.Zero;
            var before = (Vector3[])p.Clone();
            for(int k = 1; k < count; k++)
            {
                // the tip is looser than the root
                float here = stiff * (1.2f - 0.6f * k / (count - 1));
                v[k] += (here * (target[k] - p[k]) - damping * v[k] + gravity) * h;
                p[k] += v[k] * h;
            }
            for(int pass = 0; pass < 3; pass++)
            {
                // keep each piece's length
                for(int k = 1; k < count; k++)
                {
                    var d = p[k] - p[k - 1];
                    if(d.LengthSquared() > 1e-14f)
                        p[k] = p[k - 1] + Vector3.Normalize(d) * lengths[k];
                }
                // collide: the spheres (and points between them) against the capsules
                for(int k = 1; k < count; k++)
                    for(int sub = 0; sub < 3; sub++)
                    {
                        float w = (sub + 1) / 3f;                          // along the piece from k-1 to k
                        var q = Vector3.Lerp(p[k - 1], p[k], w);
                        float r = Radius(k - 1) + (Radius(k) - Radius(k - 1)) * w;
                        foreach(var c in capsules)
                        {
                            var near = Closest(q, c.A, c.B, out float u);
                            var away = q - near;
                            float dist = away.Length();
                            float reach = r + c.Radius;
                            if(dist >= reach)
                                continue;
                            var normal = dist > 1e-6f ? away / dist : (c.Upper ? -up : up);
                            // which side the tongue belongs on: under the upper lip / teeth, over the lower ones
                            if(Vector3.Dot(normal, c.Upper ? -up : up) < -0.2f && Vector3.Dot(q - near, facing) < 0f)
                                normal = c.Upper ? -up : up;
                            float depthIn = reach - dist;
                            // the tongue takes most of it, the lip the rest (it gives a little)
                            var push = normal * depthIn;
                            p[k] += push * (0.85f * w);
                            if(k - 1 > 0)
                                p[k - 1] += push * (0.85f * (1f - w));
                            if(c.BoneA >= 0)
                            {
                                var lipPush = -push * 0.15f;
                                giveAt[c.BoneA] = (giveAt.TryGetValue(c.BoneA, out var g1) ? g1 : Vector3.Zero) + lipPush * (1f - u);
                                giveAt[c.BoneB] = (giveAt.TryGetValue(c.BoneB, out var g2) ? g2 : Vector3.Zero) + lipPush * u;
                            }
                        }
                    }
            }
            // speed from where it actually went (so bumps take the speed out the way they push)
            for(int k = 1; k < count; k++)
                v[k] = (p[k] - before[k]) / h * 0.995f + v[k] * 0.005f;
        }
        state.TongueAt = p;
        state.TongueSpeed = v;

        // Put the bones there: the root where the pose has it, each turned to point at the next (the last at the tip).
        sk.SetPartialPosition(part, tongue[0].Bone, p[0], children: true);
        for(int k = 0; k < n; k++)
        {
            var a = sk.GetPartialFrame(part, tongue[k].Bone);
            var next = k + 1 < n ? sk.GetPartialFrame(part, tongue[k + 1].Bone).Position
                                 : a.Position + (a.Position - sk.GetPartialFrame(part, tongue[k - 1].Bone).Position) * 0.8f;
            var turn = MathUtil.FromTo(next - a.Position, p[k + 1] - a.Position);
            sk.SetPartialRotation(part, tongue[k].Bone, Quaternion.Normalize(turn * a.Rotation), children: true);
        }
        // The lips giving where it pressed (a couple of millimetres at most).
        float most = 0.002f * sc;
        foreach(var (bone, push) in giveAt)
        {
            var move = push / MathF.Max(1, steps);
            if(move.Length() > most)
                move = Vector3.Normalize(move) * most;
            var f = sk.GetPartialFrame(part, bone);
            sk.SetPartialPosition(part, bone, f.Position + move, children: false);
        }
    }
}
