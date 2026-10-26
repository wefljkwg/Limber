using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using Limber.Core;

namespace Limber.BodyRig;

/// <summary>
/// A collider: pill-shaped (a capsule: a line with a radius, round at both ends), like Koikatsu's collider props, or
/// a box.
/// It sits on a bone of a character (and follows it) or stays at a spot in the world. The soft bodies (breasts,
/// testicles) are always pushed out of it.
/// </summary>
[Serializable]
public sealed class PropCollider
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Collider";
    public bool Enabled { get; set; } = true;
    public bool Visible { get; set; } = true;
    /// <summary>Pushes the soft body bones (moves the breast / testicle as a whole).</summary>
    public bool BoneCollision { get; set; } = true;
    /// <summary>Dents the skin of the live (per-vertex) soft body.</summary>
    public bool MeshCollision { get; set; } = true;
    /// <summary>This collider's own strengths (0..1), on top of the overall sliders.</summary>
    public float BoneStrength { get; set; } = 1f;
    public float MeshStrength { get; set; } = 1f;
    /// <summary>Skin cling (live mesh soft body): skin near it is drawn onto it and wraps round it (0 = none).</summary>
    public float Cling { get; set; }
    /// <summary>Made by "Add hand colliders": sized together with the hand collider size slider.</summary>
    public bool Hand { get; set; }

    /// <summary>Metres (before the character's size when <see cref="ScaleWithCharacter"/>).</summary>
    public float Radius { get; set; } = 0.04f;
    /// <summary>Distance between the centres of the two round ends, along the prop's own up axis (0 = a ball).</summary>
    public float Length { get; set; } = 0.10f;

    /// <summary>A box instead of a capsule: <see cref="Width"/> x <see cref="Height"/> x <see cref="Depth"/> (its own
    /// x, up, z), metres before the character's size.</summary>
    public bool Box { get; set; }
    public float Width { get; set; } = 0.30f;
    public float Height { get; set; } = 0.10f;
    public float Depth { get; set; } = 0.30f;

    /// <summary>Character it is parented to (by name), or empty for a prop that stays in the world.</summary>
    public string ParentCharacter { get; set; } = "";
    /// <summary>Which of several characters with that name (their object slot when it was parented).</summary>
    public int ParentIndexHint { get; set; } = -1;
    public string ParentBone { get; set; } = "";
    /// <summary>Grow and shrink with the character it's on (its height and scale).</summary>
    public bool ScaleWithCharacter { get; set; } = true;

    /// <summary>Position relative to the bone (or in the world), metres.</summary>
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    /// <summary>Turn relative to the bone (or the world), degrees.</summary>
    public float Pitch { get; set; }
    public float Yaw { get; set; }
    public float Roll { get; set; }

    [Newtonsoft.Json.JsonIgnore]
    public bool IsWorld => string.IsNullOrEmpty(ParentCharacter) || string.IsNullOrEmpty(ParentBone);

    [Newtonsoft.Json.JsonIgnore]
    public Vector3 Offset
    {
        get => new(X, Y, Z);
        set
        {
            X = value.X;
            Y = value.Y;
            Z = value.Z;
        }
    }

    [Newtonsoft.Json.JsonIgnore]
    public Quaternion Rotation
    {
        get => Quaternion.CreateFromYawPitchRoll(Yaw * Deg, Pitch * Deg, Roll * Deg);
        set
        {
            var (yaw, pitch, roll) = ToYawPitchRoll(value);
            Yaw = yaw / Deg;
            Pitch = pitch / Deg;
            Roll = roll / Deg;
        }
    }

    public PropCollider Clone() => (PropCollider)MemberwiseClone();

    private const float Deg = MathF.PI / 180f;

    /// <summary>Inverse of Quaternion.CreateFromYawPitchRoll.</summary>
    private static (float Yaw, float Pitch, float Roll) ToYawPitchRoll(Quaternion q)
    {
        q = Quaternion.Normalize(q);
        float x = q.X, y = q.Y, z = q.Z, w = q.W;
        float sp = Math.Clamp(2f * (w * x - y * z), -1f, 1f);
        float pitch = MathF.Asin(sp);
        if (MathF.Abs(sp) < 1f - 1e-6f)
        {
            float yaw = MathF.Atan2(2f * (w * y + x * z), 1f - 2f * (x * x + y * y));
            float roll = MathF.Atan2(2f * (w * z + x * y), 1f - 2f * (x * x + z * z));
            return (yaw, pitch, roll);
        }
        return (MathF.Atan2(-2f * (x * z - w * y), 1f - 2f * (y * y + z * z)), pitch, 0f);
    }
}

/// <summary>A prop where it is this frame (world space).</summary>
public readonly struct PropWorld
{
    public readonly Guid Id;
    public readonly Vector3 A, B;   // centres of the two round ends (a box: both its centre)
    public readonly float Radius;   // (a box: its corner rounding)
    public readonly Shape Shape;    // what's solid
    public readonly Vector3 Size;   // a box: its full size (its own x, up, z)
    public bool IsBox => Shape.Box;
    public readonly Frame Frame;    // the prop itself (its up axis runs A -> B)
    public readonly Frame Parent;   // the bone it's on (identity for a world prop)
    public readonly float Scale;    // the size it's multiplied by
    public readonly bool Visible;
    public readonly bool Bones, Mesh;  // which soft bodies it collides with
    public readonly float BoneStrength, MeshStrength; // its own strength for each (0..1)
    public readonly float Cling;                        // skin cling on the live mesh (0..1)
    public readonly nint Owner;                         // the character it's on (0 = in the world)
    public readonly sbyte HandSide;                     // a hand collider: 0 left, 1 right; -1 = not a hand collider

    public PropWorld(Guid id, Vector3 a, Vector3 b, float radius, Frame frame, Frame parent, float scale, bool visible, bool bones = true, bool mesh = true,
        float boneStrength = 1f, float meshStrength = 1f, float cling = 0f, nint owner = 0, sbyte handSide = -1, Vector3? box = null)
    {
        if (box is { } size)
        {
            Shape = Shape.BoxOf(frame.Position, frame.Rotation, size, BoxRound * scale);
            Size = size;
            a = b = frame.Position;
            radius = Shape.R;
        }
        else
        {
            Shape = new Shape(a, b, radius);
            Size = Vector3.Zero;
        }
        Owner = owner;
        HandSide = handSide;
        Cling = float.IsFinite(cling) ? Math.Clamp(cling, 0f, 1f) : 0f;
        BoneStrength = float.IsFinite(boneStrength) ? Math.Clamp(boneStrength, 0f, 1f) : 1f;
        MeshStrength = float.IsFinite(meshStrength) ? Math.Clamp(meshStrength, 0f, 1f) : 1f;
        Bones = bones;
        Mesh = mesh;
        Id = id;
        A = a;
        B = b;
        Radius = radius;
        Frame = frame;
        Parent = parent;
        Scale = scale;
        Visible = visible;
    }

    /// <summary>How round a box's corners are (m, before the character's size; less for a thin box).</summary>
    public const float BoxRound = 0.01f;

    /// <summary>Signed distance from the solid core (below 0 inside a box's), its nearest point and the way out.</summary>
    public float Gap(Vector3 p, out Vector3 core, out Vector3 normal) => Shape.Gap(p, out core, out normal);
}

/// <summary>
/// Places the props every frame (in the bone update, after posing and before the soft bodies) and hands them to
/// the soft bodies and the overlay.
/// </summary>
public sealed unsafe class PropService
{
    private sealed record Entry(PropCollider Prop, nint Address, ushort Index);

    private volatile Entry[] _entries = [];
    private volatile PropWorld[] _world = [];
    private readonly SkeletonInfoCache _cache = new(); // bone update thread only
    private readonly SkeletonInfoCache _worldCache = new(); // framework thread (UpdateWorld) only

    private readonly IObjectTable _objects;
    private readonly IClientState _clientState;
    private readonly Func<IReadOnlyList<PropCollider>?> _source;
    /// <summary>How much bigger (thicker) every hand collider is.</summary>
    public Func<float> HandSize = () => 1f;

    public PropService(IObjectTable objects, IClientState clientState, Func<IReadOnlyList<PropCollider>?> source)
    {
        _objects = objects;
        _clientState = clientState;
        _source = source;
    }

    private readonly ConcurrentQueue<Action> _hookJobs = new();
    private readonly ConcurrentQueue<Action> _results = new();

    /// <summary>Work on a character's pose, done in the bone update (the only safe place to read it).</summary>
    public delegate T SkeletonWork<T>(in ActorSkeleton skeleton);

    /// <summary>
    /// Run <paramref name="work"/> on a character's skeleton in the next bone update; <paramref name="done"/> gets
    /// the result (default if the character couldn't be read) on the main thread, from <see cref="ApplyResults"/>.
    /// </summary>
    public void WithSkeleton<T>(nint address, ushort index, SkeletonWork<T> work, Action<T?> done)
    {
        if (_hookJobs.Count > 64)
            return;
        _hookJobs.Enqueue(() =>
        {
            T? result = default;
            try
            {
                if (ActorSkeleton.TryGet(address, index, _cache, out var skeleton))
                    result = work(skeleton);
            }
            catch (Exception e)
            {
                Plugin.Log.Warning(e, "Limber: couldn't read a character for a prop");
            }
            _results.Enqueue(() => done(result));
        });
    }

    /// <summary>Hand finished skeleton work back. Main thread, every frame.</summary>
    public void ApplyResults()
    {
        while (_results.TryDequeue(out var apply))
        {
            try
            {
                apply();
            }
            catch (Exception e)
            {
                Plugin.Log.Warning(e, "Limber: couldn't apply a prop change");
            }
        }
    }

    /// <summary>Every enabled prop that could be placed this frame.</summary>
    public PropWorld[] World => _world;

    /// <summary>
    /// Take the props as they are now and find the characters they're on. Main thread (framework / UI); call after
    /// any change, and every so often so props follow characters that come and go (or into and out of GPose).
    /// </summary>
    public void Refresh()
    {
        var props = _source();
        bool gposing = _clientState.IsGPosing;
        if (props == null || props.Count == 0)
        {
            _entries = [];
            return;
        }
        var list = new List<Entry>(props.Count);
        foreach (var prop in props)
        {
            if (prop == null || !prop.Enabled)
                continue;
            if (prop.IsWorld)
            {
                list.Add(new Entry(prop.Clone(), 0, 0));
                continue;
            }
            if (Find(_objects, prop, gposing, out var address, out var index))
                list.Add(new Entry(prop.Clone(), address, index));
        }
        _entries = list.ToArray();
    }

    /// <summary>The character a prop is on: same name; in GPose, a GPose actor first; then the slot it was on.</summary>
    public static bool Find(IObjectTable objects, PropCollider prop, bool gposing, out nint address, out ushort index)
    {
        address = 0;
        index = 0;
        int best = -1;
        foreach (var obj in objects)
        {
            if (obj is not ICharacter || obj.Address == 0)
                continue;
            var native = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)obj.Address;
            if (native->DrawObject == null)
                continue;
            if (!string.Equals(obj.Name.TextValue, prop.ParentCharacter, StringComparison.Ordinal))
                continue;
            int score = 0;
            if ((obj.ObjectIndex >= 200) == gposing)
                score += 2;
            if (obj.ObjectIndex == prop.ParentIndexHint)
                score += 1;
            if (score > best)
            {
                best = score;
                address = obj.Address;
                index = obj.ObjectIndex;
            }
        }
        return best >= 0;
    }

    // Which hand a hand collider is on (by its bone's side), -1 for any other collider.
    private static sbyte HandSideOf(PropCollider prop)
    {
        if (!prop.Hand || string.IsNullOrEmpty(prop.ParentBone))
            return -1;
        if (prop.ParentBone.EndsWith("_l", StringComparison.Ordinal))
            return 0;
        if (prop.ParentBone.EndsWith("_r", StringComparison.Ordinal))
            return 1;
        return -1;
    }

    /// <summary>Bone update stage: jobs that read poses.</summary>
    public void UpdateFrame()
    {
        while (_hookJobs.TryDequeue(out var job))
            job();
    }

    /// <summary>
    /// Where every prop is (framework thread, start of the frame): read from the pose the game finished with, after
    /// its own physics (body mod bones like the penis are moved by it after Limber's bone stage, so props on them
    /// would be left behind if read there).
    /// </summary>
    public void UpdateWorld()
    {
        var entries = _entries;
        if (entries.Length == 0)
        {
            if (_world.Length > 0)
                _world = [];
            return;
        }

        var result = new List<PropWorld>(entries.Length);
        foreach (var entry in entries)
        {
            var prop = entry.Prop;
            var parent = Frame.Identity;
            float scale = 1f;
            if (entry.Address != 0)
            {
                if (!ActorSkeleton.TryGet(entry.Address, entry.Index, _worldCache, out var skeleton))
                    continue;
                if (!skeleton.TryGetDrawnWorldFrame(prop.ParentBone, _worldCache, out parent))
                    continue;
                if (prop.ScaleWithCharacter)
                    scale = skeleton.RootScale.Y;
            }
            if (!(scale > 0.01f) || !float.IsFinite(scale))
                scale = 1f;

            var frame = parent.FromLocal(new Frame(prop.Offset * scale, prop.Rotation));
            if (!MathUtil.IsFinite(frame.Position) || !MathUtil.IsFinite(frame.Rotation))
                continue;
            float half = Math.Clamp(prop.Length, 0f, 10f) * 0.5f * scale;
            var axis = Vector3.Transform(Vector3.UnitY, frame.Rotation) * half;
            float radius = Math.Clamp(prop.Radius, 0.001f, 5f) * scale * (prop.Hand ? Math.Clamp(HandSize(), 0.1f, 10f) : 1f);
            Vector3? box = prop.Box
                ? new Vector3(Math.Clamp(prop.Width, 0.002f, 20f), Math.Clamp(prop.Height, 0.002f, 20f), Math.Clamp(prop.Depth, 0.002f, 20f)) * scale
                : null;
            result.Add(new PropWorld(prop.Id, frame.Position - axis, frame.Position + axis, radius, frame, parent, scale, prop.Visible, prop.BoneCollision, prop.MeshCollision, prop.BoneStrength, prop.MeshStrength, prop.Cling,
                entry.Address, HandSideOf(prop), box));
        }
        _world = result.ToArray();
    }

    /// <summary>
    /// Props for a character's hands (palm and each finger, both sides), sized and placed from its current pose.
    /// Bone update only (see <see cref="WithSkeleton"/>).
    /// </summary>
    public static List<PropCollider> HandProps(in ActorSkeleton pose, ushort index, string characterName, bool fingers)
    {
        var props = new List<PropCollider>();
        var skeleton = pose;
        float scale = skeleton.RootScale.Y > 0.01f ? skeleton.RootScale.Y : 1f;
        foreach (var (side, sideName) in new[] { ("l", "Left"), ("r", "Right") })
        {
            if (!TryPos("j_te_" + side, out var wrist) || !TryPos("j_naka_a_" + side, out var knuckle))
                continue;
            Add($"{sideName} palm", "j_te_" + side, wrist, knuckle, 0.035f);
            if (!fingers)
                continue;
            Finger($"{sideName} thumb", "j_oya_a_" + side, "j_oya_b_" + side, 0.012f);
            Finger($"{sideName} index finger", "j_hito_a_" + side, "j_hito_b_" + side, 0.011f);
            Finger($"{sideName} middle finger", "j_naka_a_" + side, "j_naka_b_" + side, 0.011f);
            Finger($"{sideName} ring finger", "j_kusu_a_" + side, "j_kusu_b_" + side, 0.011f);
            Finger($"{sideName} little finger", "j_ko_a_" + side, "j_ko_b_" + side, 0.010f);
        }
        return props;

        bool TryPos(string bone, out Vector3 position)
        {
            int i = skeleton.Info.IndexOf(bone);
            position = i >= 0 ? skeleton.GetWorldFrame(i).Position : default;
            return i >= 0;
        }

        // Two pieces, so the finger bends: base joint -> middle joint, and the middle joint on past it (there is
        // no fingertip bone, so the tip piece is guessed from the base piece's length).
        void Finger(string name, string first, string second, float radius)
        {
            if (!TryPos(first, out var a) || !TryPos(second, out var b))
                return;
            Add(name + " (base)", first, a, b, radius);
            Add(name + " (tip)", second, b, b + (b - a) * 0.85f, radius * 0.95f);
        }

        // A capsule on `bone` covering `start` -> `end` (world, as posed now), ends included.
        void Add(string name, string bone, Vector3 start, Vector3 end, float radius)
        {
            int i = skeleton.Info.IndexOf(bone);
            if (i < 0)
                return;
            var frame = skeleton.GetWorldFrame(i);
            var from = frame.PointToLocal(start) / scale; // in the bone's frame, without the character's size
            var to = frame.PointToLocal(end) / scale;
            var dir = to - from;
            float length = dir.Length();
            if (length < 1e-4f)
                return;
            props.Add(new PropCollider
            {
                Name = name,
                Radius = radius,
                Length = MathF.Max(0f, length - 2f * radius),
                ParentCharacter = characterName,
                ParentIndexHint = index,
                ParentBone = bone,
                ScaleWithCharacter = true,
                Hand = true,
                Offset = (from + to) * 0.5f,
                Rotation = MathUtil.FromTo(Vector3.UnitY, dir),
            });
        }
    }
}
