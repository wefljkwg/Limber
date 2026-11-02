using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using Limber.Core;

namespace Limber.Rig;

/// <summary>How a keyframe goes on to the next one.</summary>
public enum TimelineEase
{
    Smooth,     // a smooth curve through the keys (each key's neighbours shape it), like VaM Timeline's "smooth"
    Linear,     // straight from one to the next
    EaseInOut,  // slow out of this key, slow into the next
    EaseIn,     // slow out of this key, fast into the next
    EaseOut,    // fast out of this key, slow into the next
    Bounce,     // overshoots and settles into the next
    Constant,   // holds this key's value until the next (a cut)
}

public sealed class TimelineKey
{
    public float Time { get; set; }
    public float[] Value { get; set; } = [];
    public TimelineEase Ease { get; set; } = TimelineEase.Smooth;
    public TimelineKey Copy() => new() { Time = Time, Value = (float[])Value.Clone(), Ease = Ease };
}

/// <summary>
/// One thing animated on the timeline (Koikatsu Timeline's "interpolable"): one property of one character (or a
/// scene-wide one), with its keyframes.
/// Kinds: "fk.rot" / "fk.pos" (an FK bone's turn / place), "ik" (an IK goal: place and turn), "move" (the character's
/// move / turn / size), "scale" (a bone's scale), "shape" (a body shape slider), "set" (a setting of one of the
/// character's Limber features: throbbing, twerking, ahegao, look at, dynamic bones), "global" (a scene-wide one:
/// Oral, the soft bodies).
/// </summary>
public sealed class TimelineTrack
{
    public string Character { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Target { get; set; } = "";
    /// <summary>Kind-specific (an IK goal's space; a setting's value type).</summary>
    public string Extra { get; set; } = "";
    /// <summary>The name shown for it ("" = made from what it is).</summary>
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public List<TimelineKey> Keys { get; set; } = new();
    /// <summary>A text setting's values seen so far (its keys hold the number of one of these).</summary>
    public List<string> Choices { get; set; } = new();

    public TimelineTrack Copy() => new()
    {
        Character = Character, Kind = Kind, Target = Target, Extra = Extra, Name = Name, Enabled = Enabled,
        Keys = Keys.Select(k => k.Copy()).ToList(), Choices = new List<string>(Choices),
    };

    public string Id => $"{Character}|{Kind}|{Target}";
}

/// <summary>One animation: its tracks, how long it is, and how it plays.</summary>
public sealed class TimelineClip
{
    public string Name { get; set; } = "Animation";
    public float Length { get; set; } = 5f;
    public bool Loop { get; set; } = true;
    public float Speed { get; set; } = 1f;
    public List<TimelineTrack> Tracks { get; set; } = new();

    public TimelineClip Copy() => new()
    {
        Name = Name, Length = Length, Loop = Loop, Speed = Speed,
        Tracks = Tracks.Select(t => t.Copy()).ToList(),
    };
}

/// <summary>
/// Limber's timeline (Scene: Timeline), after Koikatsu Studio's Timeline, VaM Timeline and Brio's Vivacity: any of
/// Limber's functions can be a track (FK bones, IK goals, moving the character, bone scales, body shape sliders, and
/// every setting of throbbing, twerking, ahegao, look at, dynamic bones, Oral and the soft bodies), each with
/// keyframes and a curve per key; played back (looping or once, at any speed) by writing the values into those
/// functions every frame, where they take effect just as if set on their own pages. Framework thread. Only on your
/// screen.
/// </summary>
public sealed class TimelineService
{
    private readonly IObjectTable _objects;
    private readonly RigSettings _config;
    private readonly RigService _rigs;
    private readonly ActorTransforms _actors;

    public bool Playing { get; private set; }
    public float Time { get; private set; }
    private bool _dirty;
    private readonly Dictionary<string, float[]> _seen = new(); // auto key: each track's value as last applied / seen

    public TimelineService(IObjectTable objects, RigSettings config, RigService rigs, ActorTransforms actors)
    {
        _objects = objects;
        _config = config;
        _rigs = rigs;
        _actors = actors;
    }

    public TimelineClip? Clip
    {
        get
        {
            var all = _config.Timelines ??= new();
            if(all.Count == 0)
                return null;
            int i = Math.Clamp(_config.TimelineIndex, 0, all.Count - 1);
            return all[i];
        }
    }

    // ---- Transport

    public void Play()
    {
        var clip = Clip;
        if(clip == null)
            return;
        if(Time >= clip.Length - 1e-4f && !clip.Loop)
            Time = 0f;
        Playing = true;
    }

    public void Pause()
    {
        if(!Playing)
            return;
        Playing = false;
        _dirty = true; // (once more paused: the game animations freeze where they are)
    }

    public void Stop()
    {
        Playing = false;
        Time = 0f;
        _dirty = true;
    }

    /// <summary>Move the playhead (the tracks show that moment).</summary>
    public void Seek(float t)
    {
        var clip = Clip;
        Time = Math.Clamp(t, 0f, clip?.Length ?? 0f);
        _dirty = true;
    }

    /// <summary>Show the playhead's moment again (after the keys changed).</summary>
    public void Refresh() => _dirty = true;

    /// <summary>Framework thread, once a frame.</summary>
    public void Update(float dt)
    {
        var clip = Clip;
        if(clip == null)
        {
            Playing = false;
            return;
        }
        if(Playing)
        {
            Time += dt * Math.Clamp(clip.Speed, -10f, 10f);
            float length = MathF.Max(0.01f, clip.Length);
            if(clip.Loop)
            {
                Time %= length;
                if(Time < 0f)
                    Time += length;
            }
            else if(Time >= length || Time < 0f)
            {
                Time = Math.Clamp(Time, 0f, length);
                Playing = false;
            }
            Apply(clip, Time);
            return;
        }
        if(_dirty)
        {
            _dirty = false;
            Apply(clip, Time);
        }
    }

    /// <summary>Auto key (paused only): any track whose value was changed elsewhere gets a key at the playhead.</summary>
    public int AutoKey()
    {
        var clip = Clip;
        if(clip == null || Playing || _dirty)
            return 0;
        int added = 0;
        foreach(var track in clip.Tracks)
        {
            if(!track.Enabled || !TryRead(track, out var now))
                continue;
            if(_seen.TryGetValue(track.Id, out var before) && Same(before, now))
                continue;
            if(before != null)
            {
                SetKey(track, Time, now);
                added++;
            }
            _seen[track.Id] = now;
        }
        return added;
    }

    private static bool Same(float[] a, float[] b)
    {
        if(a.Length != b.Length)
            return false;
        for(int i = 0; i < a.Length; i++)
            if(MathF.Abs(a[i] - b[i]) > 1e-4f)
                return false;
        return true;
    }

    // ---- Keys

    /// <summary>A key at <paramref name="time"/> with this value (replacing one already there).</summary>
    public static TimelineKey SetKey(TimelineTrack track, float time, float[] value, TimelineEase? ease = null)
    {
        foreach(var k in track.Keys)
            if(MathF.Abs(k.Time - time) < 1f / 240f)
            {
                k.Value = (float[])value.Clone();
                if(ease != null)
                    k.Ease = ease.Value;
                return k;
            }
        var key = new TimelineKey { Time = time, Value = (float[])value.Clone() };
        // a new key takes the curve of the key before it (so a cut stays a cut)
        var prev = track.Keys.Where(k => k.Time < time).OrderBy(k => k.Time).LastOrDefault();
        key.Ease = ease ?? prev?.Ease ?? TimelineEase.Smooth;
        track.Keys.Add(key);
        track.Keys.Sort((a, b) => a.Time.CompareTo(b.Time));
        return key;
    }

    /// <summary>Key the track's current value at the playhead.</summary>
    public bool KeyNow(TimelineTrack track)
    {
        if(!TryRead(track, out var value))
            return false;
        SetKey(track, Time, value);
        _seen[track.Id] = value;
        return true;
    }

    // ---- Evaluating

    private static float EaseOf(TimelineEase ease, float u) => ease switch
    {
        TimelineEase.Linear => u,
        TimelineEase.EaseInOut => u * u * (3f - 2f * u),
        TimelineEase.EaseIn => u * u * u,
        TimelineEase.EaseOut => 1f - (1f - u) * (1f - u) * (1f - u),
        TimelineEase.Bounce => BounceOut(u),
        TimelineEase.Constant => 0f,
        _ => u,
    };

    private static float BounceOut(float u)
    {
        // overshoot a little and settle (a damped spring)
        return 1f - MathF.Exp(-6f * u) * MathF.Cos(u * MathF.PI * 2.5f) * (1f - u);
    }

    /// <summary>Which values in this kind are a rotation (a quaternion at that index), if any.</summary>
    private static int QuaternionAt(TimelineTrack track) => track.Kind switch
    {
        "fk.rot" => 0,
        "ik" => 3,
        "path" when track.Extra == "q" => 0,
        _ => -1,
    };

    public static float[]? Evaluate(TimelineTrack track, float t, bool step = false)
    {
        var keys = track.Keys;
        if(keys.Count == 0)
            return null;
        if(t <= keys[0].Time)
            return (float[])keys[0].Value.Clone();
        if(t >= keys[^1].Time)
            return (float[])keys[^1].Value.Clone();
        int i = 0;
        while(i + 1 < keys.Count && keys[i + 1].Time <= t)
            i++;
        var a = keys[i];
        var b = keys[i + 1];
        float span = MathF.Max(1e-5f, b.Time - a.Time);
        float u = Math.Clamp((t - a.Time) / span, 0f, 1f);
        if(step || a.Ease == TimelineEase.Constant)
            return (float[])a.Value.Clone();
        int n = Math.Min(a.Value.Length, b.Value.Length);
        var result = new float[n];
        int q = QuaternionAt(track);
        if(a.Ease == TimelineEase.Smooth)
        {
            // Catmull-Rom through the neighbouring keys (rotations: slerp on a smooth step)
            var p0 = i > 0 ? keys[i - 1].Value : a.Value;
            var p3 = i + 2 < keys.Count ? keys[i + 2].Value : b.Value;
            float t0 = i > 0 ? keys[i - 1].Time : a.Time - span, t3 = i + 2 < keys.Count ? keys[i + 2].Time : b.Time + span;
            for(int c = 0; c < n; c++)
            {
                if(q >= 0 && c >= q && c < q + 4)
                    continue;
                float m1 = (b.Value[c] - (c < p0.Length ? p0[c] : a.Value[c])) / MathF.Max(1e-5f, b.Time - t0) * span;
                float m2 = ((c < p3.Length ? p3[c] : b.Value[c]) - a.Value[c]) / MathF.Max(1e-5f, t3 - a.Time) * span;
                float u2 = u * u, u3 = u2 * u;
                result[c] = (2 * u3 - 3 * u2 + 1) * a.Value[c] + (u3 - 2 * u2 + u) * m1 + (-2 * u3 + 3 * u2) * b.Value[c] + (u3 - u2) * m2;
            }
            if(q >= 0 && n >= q + 4)
                Slerp(a.Value, b.Value, q, u * u * (3f - 2f * u), result);
            return result;
        }
        float e = EaseOf(a.Ease, u);
        for(int c = 0; c < n; c++)
            if(!(q >= 0 && c >= q && c < q + 4))
                result[c] = a.Value[c] + (b.Value[c] - a.Value[c]) * e;
        if(q >= 0 && n >= q + 4)
            Slerp(a.Value, b.Value, q, e, result);
        return result;
    }

    private static void Slerp(float[] a, float[] b, int at, float u, float[] into)
    {
        var qa = Quaternion.Normalize(new Quaternion(a[at], a[at + 1], a[at + 2], a[at + 3]));
        var qb = Quaternion.Normalize(new Quaternion(b[at], b[at + 1], b[at + 2], b[at + 3]));
        // (the bounce overshoot is kept: slerp past the ends)
        Quaternion r;
        if(u is >= 0f and <= 1f)
            r = Quaternion.Slerp(qa, qb, u);
        else
        {
            var delta = Quaternion.Normalize(qb * Quaternion.Inverse(qa));
            if(delta.W < 0f) delta = -delta;
            float angle = 2f * MathF.Acos(Math.Clamp(delta.W, -1f, 1f));
            var axis = MathF.Abs(MathF.Sin(angle / 2f)) > 1e-5f ? new Vector3(delta.X, delta.Y, delta.Z) / MathF.Sin(angle / 2f) : Vector3.UnitX;
            r = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(axis, angle * u) * qa);
        }
        into[at] = r.X; into[at + 1] = r.Y; into[at + 2] = r.Z; into[at + 3] = r.W;
    }

    private void Apply(TimelineClip clip, float t)
    {
        foreach(var track in clip.Tracks)
        {
            if(!track.Enabled)
                continue;
            if(track.Kind == "anim")
            {
                try
                {
                    ApplyAnimation(clip, track, t);
                }
                catch
                {
                    // the character isn't here
                }
                continue;
            }
            bool step = IsStep(track);
            var value = Evaluate(track, t, step);
            if(value == null)
                continue;
            try
            {
                Write(track, value);
                _seen[track.Id] = value;
            }
            catch
            {
                // a character who isn't here, a bone that's gone: skip it this frame
            }
        }
    }

    // ---- Game animations: each key plays an animation (its id, at its speed; id 0 = back to the character's own)
    //      from that moment until the next key. Paused with the timeline; moving the playhead puts the animation at
    //      the matching moment (as far as the game lets it be moved).

    private readonly Dictionary<string, (TimelineKey? Key, bool Playing, float Speed)> _animShown = new();

    /// <summary>The key of a game animation track that's on at <paramref name="t"/> (the last one at or before it).</summary>
    public static TimelineKey? AnimationKeyAt(TimelineTrack track, float t)
    {
        TimelineKey? on = null;
        foreach(var k in track.Keys)
            if(k.Time <= t + 1e-4f)
                on = k;
        return on;
    }

    private void ApplyAnimation(TimelineClip clip, TimelineTrack track, float t)
    {
        if(!Find(track.Character, out var address, out var index))
            return;
        var key = AnimationKeyAt(track, t);
        if(key == null || key.Value.Length < 1)
            return;
        ushort id = (ushort)Math.Clamp((int)MathF.Round(key.Value[0]), 0, ushort.MaxValue);
        float keySpeed = key.Value.Length > 1 ? key.Value[1] : 1f;
        float speed = Playing ? MathF.Abs(keySpeed * clip.Speed) : 0f; // (the game can't play them backwards)
        _animShown.TryGetValue(track.Id, out var shown);
        bool newKey = !ReferenceEquals(shown.Key, key);
        if(newKey)
        {
            if(id == 0)
                Animations.Stop(address, index);
            else
                Animations.Play(address, index, id);
        }
        if(newKey || shown.Playing != Playing || MathF.Abs(shown.Speed - speed) > 1e-3f)
            Animations.SetSpeed(address, index, speed);
        // paused (or just switched to it): at the moment matching the playhead
        if(id != 0 && (!Playing || newKey))
            Animations.Seek(address, index, MathF.Max(0f, (t - key.Time) * MathF.Abs(keySpeed)));
        _animShown[track.Id] = (key, Playing, speed);
    }

    // ---- The characters

    private bool Find(string name, out nint address, out ushort index)
    {
        address = 0;
        index = 0;
        // in GPose, a GPose copy first (the one being posed)
        foreach(var obj in _objects)
            if(obj is ICharacter && obj.Address != 0 && obj.ObjectIndex >= 200 && obj.Name.TextValue == name)
            {
                address = obj.Address;
                index = obj.ObjectIndex;
                return true;
            }
        foreach(var obj in _objects)
            if(obj is ICharacter && obj.Address != 0 && obj.Name.TextValue == name)
            {
                address = obj.Address;
                index = obj.ObjectIndex;
                return true;
            }
        return false;
    }

    // ---- Any setting Limber keeps (by reflection), by its path in the settings
    //
    // A path is tokens joined by '/': a property's name, "[key]" for a dictionary's entry (or a list's: "[#id]" by
    // its Id, "[3]" by place), and "@" for the track's character (the per-character settings are kept by name).
    // E.g. "Ahegao/@/EyesUp", "DynamicBones/@/Bones/[iv_ochinko_c]/Weight", "Props/[#<id>]/Radius", "ShowHandles".

    /// <summary>The features whose settings are kept per character by name (property, label, how deep to list).</summary>
    public static readonly (string Property, string Name, int Depth)[] Features =
    [
        (nameof(RigSettings.Throb), "Throbbing", 0),
        (nameof(RigSettings.Twerk), "Twerking", 0),
        (nameof(RigSettings.Ahegao), "Ahegao", 0),
        (nameof(RigSettings.Expansion), "Expansion", 1),
        (nameof(RigSettings.LookAt), "Look at", 0),
        (nameof(RigSettings.DynamicBones), "Dynamic bones", 2),
        (nameof(RigSettings.FkPoses), "FK pose (switches)", 0),
        (nameof(RigSettings.BoneScales), "Bone scale (options)", 2),
    ];

    /// <summary>Scene-wide settings (property, label, how deep to list); "" = Limber's own settings.</summary>
    public static readonly (string Property, string Name, int Depth)[] Globals =
    [
        (nameof(RigSettings.Oral), "Oral", 1),
        (nameof(RigSettings.SoftBody), "Soft breasts", 1),
        (nameof(RigSettings.SoftBodyTesticles), "Soft testicles", 1),
        (nameof(RigSettings.SoftBodyButt), "Soft butt", 1),
        (nameof(RigSettings.LiveMesh), "Live mesh", 1),
        (nameof(RigSettings.Props), "Props and colliders", 2),
        (nameof(RigSettings.Motions), "Motion devices", 2),
        ("", "Limber's settings", 0),
    ];

    /// <summary>Kinds of values that can be keyed.</summary>
    public static bool IsLeaf(Type t)
        => t == typeof(float) || t == typeof(double) || t == typeof(int) || t == typeof(bool) || t.IsEnum || t == typeof(string)
           || t == typeof(Vector2) || t == typeof(Vector3) || t == typeof(Quaternion);

    private static bool Blends(Type t)
        => t == typeof(float) || t == typeof(double) || t == typeof(Vector2) || t == typeof(Vector3) || t == typeof(Quaternion);

    // (left out of the lists: runtime-only values, and the timeline's own)
    private static bool Listed(PropertyInfo p)
        => p.GetIndexParameters().Length == 0 && p.CanRead
           && p.GetCustomAttribute<Newtonsoft.Json.JsonIgnoreAttribute>() == null
           && p.Name is not (nameof(RigSettings.Timelines) or nameof(RigSettings.TimelineIndex) or nameof(RigSettings.TimelineAutoKey)
               or nameof(RigSettings.TimelineSnap) or nameof(RigSettings.UiPage) or "Id");

    private sealed record Leaf(object Owner, PropertyInfo? Property, object? Key, Type Type);

    private static Type? ValueTypeOf(object container)
    {
        var t = container.GetType();
        if(t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Dictionary<,>))
            return t.GetGenericArguments()[1];
        if(t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
            return t.GetGenericArguments()[0];
        return null;
    }

    private static int ListIndex(System.Collections.IList list, string key)
    {
        if(key.StartsWith('#'))
        {
            string id = key[1..];
            for(int i = 0; i < list.Count; i++)
                if(list[i]?.GetType().GetProperty("Id")?.GetValue(list[i])?.ToString() == id)
                    return i;
            return -1;
        }
        return int.TryParse(key, out int n) && n >= 0 && n < list.Count ? n : -1;
    }

    private Leaf? Resolve(TimelineTrack track, bool create)
    {
        var tokens = track.Target.Split('/');
        object cur = _config;
        for(int i = 0; i < tokens.Length; i++)
        {
            string tok = tokens[i] == "@" ? $"[{track.Character}]" : tokens[i];
            bool last = i == tokens.Length - 1;
            if(tok.StartsWith('[') && tok.EndsWith(']'))
            {
                string key = tok[1..^1];
                var valueType = ValueTypeOf(cur);
                if(valueType == null)
                    return null;
                if(cur is System.Collections.IDictionary dict)
                {
                    if(last)
                        return new Leaf(dict, null, key, valueType);
                    if(!dict.Contains(key))
                    {
                        if(!create || valueType.GetConstructor(Type.EmptyTypes) == null)
                            return null;
                        dict[key] = Activator.CreateInstance(valueType);
                    }
                    cur = dict[key]!;
                }
                else if(cur is System.Collections.IList list)
                {
                    int at = ListIndex(list, key);
                    if(at < 0)
                        return null;
                    if(last)
                        return new Leaf(list, null, at, valueType);
                    cur = list[at]!;
                }
                else
                    return null;
                if(cur == null)
                    return null;
                continue;
            }
            var p = cur.GetType().GetProperty(tok, BindingFlags.Public | BindingFlags.Instance);
            if(p == null || p.GetIndexParameters().Length > 0)
                return null;
            if(last)
                return new Leaf(cur, p, null, p.PropertyType);
            var next = p.GetValue(cur);
            if(next == null)
            {
                if(!create || !p.CanWrite || p.PropertyType.GetConstructor(Type.EmptyTypes) == null)
                    return null;
                next = Activator.CreateInstance(p.PropertyType)!;
                p.SetValue(cur, next);
            }
            cur = next;
        }
        return null;
    }

    private static object? GetLeaf(Leaf leaf)
    {
        if(leaf.Property != null)
            return leaf.Property.GetValue(leaf.Owner);
        if(leaf.Owner is System.Collections.IDictionary dict)
            return dict.Contains(leaf.Key!) ? dict[leaf.Key!] : (leaf.Type.IsValueType ? Activator.CreateInstance(leaf.Type) : null);
        if(leaf.Owner is System.Collections.IList list && leaf.Key is int i)
            return list[i];
        return null;
    }

    private static void SetLeaf(Leaf leaf, object? value)
    {
        if(leaf.Property != null)
        {
            if(leaf.Property.CanWrite)
                leaf.Property.SetValue(leaf.Owner, value);
            return;
        }
        if(leaf.Owner is System.Collections.IDictionary dict)
            dict[leaf.Key!] = value;
        else if(leaf.Owner is System.Collections.IList list && leaf.Key is int i)
            list[i] = value;
    }

    private static float[]? Encode(object? raw, TimelineTrack track) => raw switch
    {
        float f => [f],
        double d => [(float)d],
        int i => [i],
        bool b => [b ? 1f : 0f],
        Enum e => [Convert.ToInt32(e)],
        Vector2 v => [v.X, v.Y],
        Vector3 v => [v.X, v.Y, v.Z],
        Quaternion q => [q.X, q.Y, q.Z, q.W],
        string s => [ChoiceOf(track, s)],
        null when track.Extra == "s" => [ChoiceOf(track, "")],
        _ => null,
    };

    private static int ChoiceOf(TimelineTrack track, string s)
    {
        int i = track.Choices.IndexOf(s);
        if(i < 0)
        {
            track.Choices.Add(s);
            i = track.Choices.Count - 1;
        }
        return i;
    }

    private static object? Decode(float[] v, Type t, TimelineTrack track)
    {
        if(t == typeof(float)) return v[0];
        if(t == typeof(double)) return (double)v[0];
        if(t == typeof(int)) return (int)MathF.Round(v[0]);
        if(t == typeof(bool)) return v[0] >= 0.5f;
        if(t.IsEnum) return Enum.ToObject(t, (int)MathF.Round(v[0]));
        if(t == typeof(Vector2) && v.Length >= 2) return new Vector2(v[0], v[1]);
        if(t == typeof(Vector3) && v.Length >= 3) return new Vector3(v[0], v[1], v[2]);
        if(t == typeof(Quaternion) && v.Length >= 4) return Quaternion.Normalize(new Quaternion(v[0], v[1], v[2], v[3]));
        if(t == typeof(string))
        {
            int i = (int)MathF.Round(v[0]);
            return i >= 0 && i < track.Choices.Count ? track.Choices[i] : null;
        }
        return null;
    }

    /// <summary>Switches, choices and text jump from key to key instead of blending.</summary>
    public bool IsStep(TimelineTrack track)
    {
        if(track.Kind == "anim")
            return true;
        if(track.Kind != "path")
            return false;
        return track.Extra is "b" or "s";
    }

    /// <summary>
    /// Everything under a feature that can be keyed, as (path, where it is, its name, type), from the settings as
    /// they are (a character's, if per character; its defaults if it has none yet).
    /// </summary>
    public List<(string Path, string Group, string Name, Type Type)> SettingsOf(string property, string character, int depth)
    {
        var list = new List<(string, string, string, Type)>();
        object? root;
        string prefix;
        if(property.Length == 0)
        {
            root = _config;
            prefix = "";
        }
        else
        {
            var p = typeof(RigSettings).GetProperty(property);
            if(p == null)
                return list;
            root = p.GetValue(_config);
            prefix = property;
            if(root is System.Collections.IDictionary dict && character.Length > 0)
            {
                prefix += "/@";
                var vt = ValueTypeOf(root);
                root = dict.Contains(character) ? dict[character] : (vt?.GetConstructor(Type.EmptyTypes) != null ? Activator.CreateInstance(vt) : null);
            }
            root ??= p.PropertyType.GetConstructor(Type.EmptyTypes) != null ? Activator.CreateInstance(p.PropertyType) : null;
        }
        if(root != null)
            Walk(root, prefix, "", depth, list);
        return list;
    }

    private static void Walk(object obj, string prefix, string group, int depth, List<(string, string, string, Type)> into)
    {
        string Join(string a, string b) => a.Length == 0 ? b : a + "/" + b;
        // a dictionary or list itself (e.g. a bone's settings by bone name)
        if(obj is System.Collections.IDictionary topDict)
        {
            var vt = ValueTypeOf(obj);
            if(vt == null)
                return;
            foreach(var key in topDict.Keys)
            {
                string k = key?.ToString() ?? "";
                if(IsLeaf(vt))
                    into.Add((Join(prefix, $"[{k}]"), group, k, vt));
                else if(depth > 0 && topDict[key!] is { } child)
                    Walk(child, Join(prefix, $"[{k}]"), Join(group, k), depth - 1, into);
            }
            return;
        }
        if(obj is System.Collections.IList topList)
        {
            for(int i = 0; i < topList.Count; i++)
            {
                var child = topList[i];
                if(child == null || depth <= 0)
                    continue;
                string id = child.GetType().GetProperty("Id")?.GetValue(child)?.ToString() is { Length: > 0 } s ? "#" + s : i.ToString();
                string label = child.GetType().GetProperty("Name")?.GetValue(child) as string ?? $"#{i + 1}";
                Walk(child, Join(prefix, $"[{id}]"), Join(group, label), depth - 1, into);
            }
            return;
        }
        foreach(var p in obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if(!Listed(p))
                continue;
            var t = p.PropertyType;
            if(IsLeaf(t))
            {
                if(p.CanWrite)
                    into.Add((Join(prefix, p.Name), group, Spaced(p.Name), t));
                continue;
            }
            if(depth <= 0 || t == typeof(string) || t.IsPrimitive)
                continue;
            var child = p.GetValue(obj);
            if(child == null || child.GetType().Namespace?.StartsWith("Limber", StringComparison.Ordinal) != true
               && child is not System.Collections.IDictionary && child is not System.Collections.IList)
                continue;
            Walk(child, Join(prefix, p.Name), Join(group, Spaced(p.Name)), depth - 1, into);
        }
    }

    /// <summary>What a path track's values are ("q" a rotation, "b" a switch / choice, "s" text, "" numbers).</summary>
    public static string ExtraFor(Type t) => t == typeof(Quaternion) ? "q" : t == typeof(string) ? "s" : Blends(t) ? "" : "b";

    // ---- Reading / writing each kind

    public bool TryRead(TimelineTrack track, out float[] value)
    {
        value = [];
        string name = track.Character;
        switch(track.Kind)
        {
            case "fk.rot":
            {
                var q = _config.FkPoses != null && _config.FkPoses.TryGetValue(name, out var pose) && pose.Bones.TryGetValue(track.Target, out var b)
                    ? b.Rotation : Quaternion.Identity;
                value = [q.X, q.Y, q.Z, q.W];
                return true;
            }
            case "fk.pos":
            {
                var v = _config.FkPoses != null && _config.FkPoses.TryGetValue(name, out var pose) && pose.Bones.TryGetValue(track.Target, out var b)
                    ? b.Position : Vector3.Zero;
                value = [v.X, v.Y, v.Z];
                return true;
            }
            case "ik":
            {
                if(!Find(name, out var address, out _))
                    return false;
                lock(_rigs.Sync)
                {
                    var goal = _rigs.GetRig(address)?.Find(track.Target);
                    if(goal == null || !goal.HasTarget)
                        return false;
                    var f = goal.Local;
                    value = [f.Position.X, f.Position.Y, f.Position.Z, f.Rotation.X, f.Rotation.Y, f.Rotation.Z, f.Rotation.W];
                    return true;
                }
            }
            case "camera":
            {
                if(CameraControl.Read() is not { } shot)
                    return false;
                value = [shot.Distance, shot.FoV, shot.Yaw, shot.Pitch, shot.Tilt];
                return true;
            }
            case "anim":
            {
                // what it's playing now (the base animation, or what Limber put on it), at normal speed
                if(!Find(name, out var address, out var index) || Animations.Current(address, index) is not { } now)
                    return false;
                ushort id = now.Override != 0 ? now.Override : now.Base.Id;
                value = [id, 1f];
                return true;
            }
            case "move":
            {
                if(!Find(name, out var address, out _))
                    return false;
                var o = _actors.Get(address);
                var m = o?.Move ?? Vector3.Zero;
                var r = o?.Turn ?? Vector3.Zero;
                value = [m.X, m.Y, m.Z, r.X, r.Y, r.Z, o?.Scale ?? 1f];
                return true;
            }
            case "scale":
            {
                var v = _config.BoneScales != null && _config.BoneScales.TryGetValue(name, out var bones) && bones.TryGetValue(track.Target, out var e)
                    ? e.Scale : Vector3.One;
                value = [v.X, v.Y, v.Z];
                return true;
            }
            case "shape":
            {
                float v = _config.BodyShapes != null && _config.BodyShapes.TryGetValue(name, out var shape) && shape.TryGetValue(track.Target, out var x) ? x : 0f;
                value = [v];
                return true;
            }
            case "path":
            {
                // (set up with its defaults if this character has none yet, so it can be keyed straight away)
                var leaf = Resolve(track, create: true);
                if(leaf == null)
                    return false;
                var encoded = Encode(GetLeaf(leaf), track);
                if(encoded == null)
                    return false;
                value = encoded;
                return true;
            }
        }
        return false;
    }

    private void Write(TimelineTrack track, float[] value)
    {
        string name = track.Character;
        switch(track.Kind)
        {
            case "fk.rot" when value.Length >= 4:
            case "fk.pos" when value.Length >= 3:
            {
                if(name.Length == 0)
                    return;
                var all = _config.FkPoses ??= new();
                if(!all.TryGetValue(name, out var pose))
                    all[name] = pose = new FkPoseSettings { Layered = true };
                if(!pose.Bones.TryGetValue(track.Target, out var b))
                    pose.Bones[track.Target] = b = new FkBoneSettings();
                b.Enabled = true;
                if(track.Kind == "fk.rot")
                    b.Rotation = new Quaternion(value[0], value[1], value[2], value[3]);
                else
                {
                    b.Move = true;
                    b.Position = new Vector3(value[0], value[1], value[2]);
                }
                return;
            }
            case "ik" when value.Length >= 7:
            {
                if(!Find(name, out var address, out var index))
                    return;
                lock(_rigs.Sync)
                {
                    var rig = _rigs.GetOrCreateRig(address, index, name);
                    var goal = rig.GetOrAdd(track.Target);
                    if(Enum.TryParse<GoalSpace>(track.Extra, out var space) && space != GoalSpace.Attached)
                        goal.Space = space;
                    goal.Enabled = true;
                    goal.Local = new Frame(new Vector3(value[0], value[1], value[2]),
                        Quaternion.Normalize(new Quaternion(value[3], value[4], value[5], value[6])));
                    goal.HasTarget = true;
                    goal.CapturePosition = false;
                    goal.CaptureRotation = false;
                    goal.PendingWorldPosition = null;
                    goal.PendingWorldRotation = null;
                }
                return;
            }
            case "camera" when value.Length >= 5:
            {
                CameraControl.Write(new CameraShot { Distance = value[0], FoV = value[1], Yaw = value[2], Pitch = value[3], Tilt = value[4] });
                return;
            }
            case "move" when value.Length >= 7:
            {
                if(!Find(name, out var address, out var index))
                    return;
                var o = _actors.GetOrAdd(address, index);
                lock(_actors.Sync)
                {
                    o.Move = new Vector3(value[0], value[1], value[2]);
                    o.Turn = new Vector3(value[3], value[4], value[5]);
                    o.Scale = Math.Clamp(value[6], ActorTransforms.MinScale, ActorTransforms.MaxScale);
                }
                return;
            }
            case "scale" when value.Length >= 3:
            {
                var all = _config.BoneScales ??= new();
                if(!all.TryGetValue(name, out var bones))
                    all[name] = bones = new();
                if(!bones.TryGetValue(track.Target, out var e))
                    bones[track.Target] = e = new Limber.BodyShape.BoneScaleEntry();
                e.Scale = new Vector3(value[0], value[1], value[2]);
                return;
            }
            case "shape" when value.Length >= 1:
            {
                var all = _config.BodyShapes ??= new();
                if(!all.TryGetValue(name, out var shape))
                    all[name] = shape = new();
                shape[track.Target] = value[0];
                return;
            }
            case "path" when value.Length >= 1:
            {
                var leaf = Resolve(track, create: true);
                if(leaf == null)
                    return;
                var decoded = Decode(value, leaf.Type, track);
                if(decoded != null || !leaf.Type.IsValueType)
                    SetLeaf(leaf, decoded);
                return;
            }
        }
    }

    // ---- What a character has that can go on the timeline

    /// <summary>The bones posed with FK on a character.</summary>
    public List<string> FkBones(string name)
        => _config.FkPoses != null && _config.FkPoses.TryGetValue(name, out var pose) ? pose.Bones.Keys.OrderBy(b => b).ToList() : new();

    /// <summary>The IK goals on a character (bone, space).</summary>
    public List<(string Bone, GoalSpace Space)> IkGoals(string name)
    {
        var list = new List<(string, GoalSpace)>();
        if(!Find(name, out var address, out _))
            return list;
        lock(_rigs.Sync)
        {
            var rig = _rigs.GetRig(address);
            if(rig != null)
                foreach(var g in rig.Goals)
                    if(g.Space != GoalSpace.Attached)
                        list.Add((g.Bone, g.Space));
        }
        return list;
    }

    public List<string> ScaledBones(string name)
        => _config.BoneScales != null && _config.BoneScales.TryGetValue(name, out var bones) ? bones.Keys.OrderBy(b => b).ToList() : new();

    /// <summary>Add a track (or find the one already there) and key its current value at the playhead.</summary>
    public TimelineTrack AddTrack(TimelineClip clip, string character, string kind, string target, string extra = "", bool key = true, string name = "")
    {
        var track = clip.Tracks.FirstOrDefault(t => t.Character == character && t.Kind == kind && t.Target == target);
        if(track == null)
        {
            track = new TimelineTrack { Character = character, Kind = kind, Target = target, Extra = extra, Name = name };
            clip.Tracks.Add(track);
        }
        if(key)
            KeyNow(track);
        return track;
    }

    /// <summary>A short name for a track, for the list.</summary>
    public static string Label(TimelineTrack track) => track.Name.Length > 0 ? track.Name : track.Kind switch
    {
        "fk.rot" => $"FK turn: {HandleCatalog.Label(track.Target)}",
        "fk.pos" => $"FK move: {HandleCatalog.Label(track.Target)}",
        "ik" => $"IK: {HandleCatalog.Label(track.Target)}{(track.Extra == nameof(GoalSpace.Pinned) ? " (pinned)" : "")}",
        "move" => "Move / turn / size",
        "anim" => "Game animation",
        "camera" => "Camera (distance, field of view, angles, tilt)",
        "scale" => $"Scale: {HandleCatalog.Label(track.Target)}",
        "shape" => $"Body: {(Limber.BodyShape.BodySliders.ByKey.TryGetValue(track.Target, out var s) ? s.Name : track.Target)}",
        "path" => PathLabel(track.Target),
        _ => track.Target,
    };

    private static string PathLabel(string target)
    {
        var tokens = target.Split('/');
        string root = tokens.Length > 0 ? tokens[0] : "";
        string feature = Features.Concat(Globals).FirstOrDefault(f => f.Property == root && root.Length > 0).Name
                         ?? (tokens.Length == 1 ? "Limber" : Spaced(root));
        var rest = tokens.Skip(tokens.Length == 1 ? 0 : 1).Where(t => t != "@")
            .Select(t => t.StartsWith('[') ? t[1..^1].TrimStart('#') : Spaced(t));
        // (an Id in a path: shown short)
        var parts = rest.Select(r => r.Length > 12 && Guid.TryParse(r, out _) ? r[..4] : r);
        return $"{feature}: {string.Join(" / ", parts)}";
    }

    /// <summary>"TongueOut" -> "Tongue out".</summary>
    public static string Spaced(string s)
    {
        var sb = new System.Text.StringBuilder();
        for(int i = 0; i < s.Length; i++)
        {
            if(i > 0 && char.IsUpper(s[i]) && !char.IsUpper(s[i - 1]))
                sb.Append(' ').Append(char.ToLowerInvariant(s[i]));
            else
                sb.Append(s[i]);
        }
        return sb.ToString();
    }
}
