using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Limber.Core;
using Newtonsoft.Json.Linq;
using BoneTransform = Limber.Core.ActorSkeleton.BoneTransform;

namespace Limber.Rig;

/// <summary>
/// Freeze motion: a character's whole pose (body, face, hair) held as it was, every frame, so the animation, the
/// game's physics and the face stop; Limber's own posing, shaping and soft bodies still work on top. And pose files:
/// export what's on screen, import one (which freezes the character in it). The file is the .pose format Brio and
/// Ktisis use (bones in model space), so poses go back and forth between them.
/// </summary>
public sealed class MotionFreeze
{
    private sealed class Entry
    {
        public required nint Address;
        public ushort Index;
        public BoneTransform[][]? Snapshot;   // the held pose (taken at the end of a frame)
        public bool Capture = true;           // take it at the end of this frame
        public Dictionary<string, BoneTransform>? Import;
        public bool ImportPositions;
        public Action<string>? Report;
    }

    private sealed class ExportRequest
    {
        public required nint Address;
        public ushort Index;
        public required Action<JObject?, string> Done;
    }

    private readonly Dictionary<nint, Entry> _frozen = new();
    private readonly List<ExportRequest> _exports = new();
    private readonly SkeletonInfoCache _cache = new(); // bone update thread only
    public readonly object Sync = new();
    public Func<bool> Allowed = () => true;

    public bool IsFrozen(nint address)
    {
        lock(Sync)
            return _frozen.ContainsKey(address);
    }

    public int Count
    {
        get
        {
            lock(Sync)
                return _frozen.Count;
        }
    }

    public void Freeze(nint address, ushort index)
    {
        lock(Sync)
            if(!_frozen.ContainsKey(address))
                _frozen[address] = new Entry { Address = address, Index = index };
    }

    public void Resume(nint address)
    {
        lock(Sync)
            _frozen.Remove(address);
    }

    public void ResumeAll()
    {
        lock(Sync)
            _frozen.Clear();
    }

    /// <summary>
    /// Puts a pose file's bones on the character (next frame) and holds it there (frozen). Rotations always;
    /// positions only if <paramref name="positions"/> (for the same body: other bodies keep their own proportions).
    /// </summary>
    public void Import(nint address, ushort index, Dictionary<string, BoneTransform> bones, bool positions, Action<string> report)
    {
        lock(Sync)
            _frozen[address] = new Entry { Address = address, Index = index, Import = bones, ImportPositions = positions, Report = report };
    }

    /// <summary>The character's pose as it is on screen at the end of the next frame, as a .pose file.</summary>
    public void Export(nint address, ushort index, Action<JObject?, string> done)
    {
        lock(Sync)
            _exports.Add(new ExportRequest { Address = address, Index = index, Done = done });
    }

    /// <summary>Bone update stage (first): the held poses back on, imports put on.</summary>
    public void RestoreFrame()
    {
        lock(Sync)
        {
            if(_frozen.Count == 0)
                return;
            if(!Allowed())
            {
                _frozen.Clear();
                return;
            }
            List<nint>? gone = null;
            foreach(var (address, e) in _frozen)
            {
                if(!ActorSkeleton.TryGet(address, e.Index, _cache, out var skeleton) || skeleton.Info.Count == 0)
                {
                    (gone ??= new()).Add(address);
                    continue;
                }
                if(e.Import != null)
                {
                    int matched = ApplyImport(skeleton, e.Import, e.ImportPositions);
                    e.Report?.Invoke(matched > 0
                        ? $"Pose loaded ({matched} bones) and held: untick \"Freeze motion\" to let the animation play again."
                        : "None of the file's bones are on this character; nothing changed.");
                    e.Import = null;
                    e.Report = null;
                    e.Snapshot = null;
                    e.Capture = true;
                    if(matched == 0)
                        (gone ??= new()).Add(address);
                    continue;
                }
                if(e.Snapshot != null && !skeleton.RestoreModelPose(e.Snapshot))
                {
                    // Redrawn or changed (new hairstyle...): hold what it has now instead.
                    e.Snapshot = null;
                    e.Capture = true;
                }
            }
            if(gone != null)
                foreach(var a in gone)
                    _frozen.Remove(a);
        }
    }

    /// <summary>Bone update stage (last): take the poses to hold, and the ones asked for to export.</summary>
    public void CaptureFrame()
    {
        lock(Sync)
        {
            foreach(var e in _frozen.Values)
            {
                if(!e.Capture || e.Import != null)
                    continue;
                if(ActorSkeleton.TryGet(e.Address, e.Index, _cache, out var skeleton) && skeleton.Info.Count > 0)
                {
                    e.Snapshot = skeleton.CaptureModelPose();
                    e.Capture = false;
                }
            }
            if(_exports.Count == 0)
                return;
            foreach(var x in _exports)
            {
                if(!ActorSkeleton.TryGet(x.Address, x.Index, _cache, out var skeleton) || skeleton.Info.Count == 0)
                {
                    x.Done(null, "The character isn't there any more.");
                    continue;
                }
                var file = ToFile(skeleton);
                // Which of the game's animations it was on (and where in it), so it can be played again.
                try
                {
                    if(Animations.Current(x.Address, x.Index) is { } now)
                    {
                        JObject Slot(Animations.Slot s) => new() { ["Id"] = s.Id, ["Key"] = s.Key, ["Time"] = s.Time };
                        file["Animation"] = new JObject
                        {
                            ["Base"] = Slot(now.Base),
                            ["UpperBody"] = Slot(now.Upper),
                            ["Face"] = Slot(now.Face),
                            ["Override"] = now.Override,
                        };
                    }
                }
                catch
                {
                    // (no animation info: the pose is saved without it)
                }
                x.Done(file, "");
            }
            _exports.Clear();
        }
    }

    // ---- Pose files (.pose, as Brio / Ktisis write them: bone name -> model-space position, rotation, scale)

    private JObject ToFile(in ActorSkeleton skeleton)
    {
        var bones = new JObject();
        var parts = skeleton.CaptureModelPose();
        for(int p = 0; p < parts.Length; p++)
        {
            var info = p == 0 ? skeleton.Info : skeleton.PartialInfo(p, _cache);
            if(info == null || info.Count != parts[p].Length)
                continue;
            for(int b = 0; b < info.Count; b++)
            {
                // (an extra skeleton's root is the body's bone it hangs from: the body's copy is the one kept)
                if(p > 0 && info.Parents[b] < 0)
                    continue;
                if(bones.ContainsKey(info.Names[b]))
                    continue;
                var t = parts[p][b];
                bones[info.Names[b]] = new JObject
                {
                    ["Position"] = V(t.Position),
                    ["Rotation"] = Q(t.Rotation),
                    ["Scale"] = V(t.Scale),
                };
            }
        }
        return new JObject
        {
            ["FileExtension"] = ".pose",
            ["TypeName"] = "Limber Pose",
            ["FileVersion"] = 3,
            ["Position"] = V(skeleton.RootPosition),
            ["Rotation"] = Q(skeleton.RootRotation),
            ["Scale"] = V(skeleton.RootScale),
            ["Bones"] = bones,
            ["MainHand"] = new JObject(),
            ["OffHand"] = new JObject(),
        };
    }

    private static string F(float f) => f.ToString(CultureInfo.InvariantCulture);
    private static string V(Vector3 v) => $"{F(v.X)}, {F(v.Y)}, {F(v.Z)}";
    private static string Q(Quaternion q) => $"{F(q.X)}, {F(q.Y)}, {F(q.Z)}, {F(q.W)}";

    /// <summary>The game animation a Limber pose file was saved on (its timeline row and key); null if it doesn't say.</summary>
    public static (ushort Id, string Key)? ReadAnimation(string json)
    {
        try
        {
            var root = JObject.Parse(json);
            if(root["Animation"]?["Base"] is not JObject b)
                return null;
            ushort id = b["Id"]?.Value<ushort>() ?? 0;
            ushort over = root["Animation"]?["Override"]?.Value<ushort>() ?? 0;
            if(over != 0)
                id = over;
            return id == 0 ? null : (id, b["Key"]?.Value<string>() ?? "");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>A .pose file's bones (Brio, Ktisis or Limber); null if it isn't one.</summary>
    public static Dictionary<string, BoneTransform>? ReadFile(string json, out string error)
    {
        error = "";
        JObject root;
        try
        {
            root = JObject.Parse(json);
        }
        catch(Exception e)
        {
            error = "Not a pose file (" + e.Message + ").";
            return null;
        }
        if(root["Bones"] is not JObject list)
        {
            error = "Not a pose file (no bones in it).";
            return null;
        }
        var bones = new Dictionary<string, BoneTransform>(StringComparer.Ordinal);
        foreach(var (name, value) in list)
        {
            if(value is not JObject b)
                continue;
            var rotation = ReadQuaternion(b["Rotation"]);
            if(rotation is not { } r || r.LengthSquared() < 1e-6f)
                continue;
            bones[name] = new BoneTransform(ReadVector(b["Position"]) ?? Vector3.Zero, Quaternion.Normalize(r), ReadVector(b["Scale"]) ?? Vector3.One);
        }
        if(bones.Count == 0)
            error = "The pose file has no bones Limber can read.";
        return bones.Count > 0 ? bones : null;
    }

    private static float[]? Numbers(JToken? token, int count)
    {
        if(token == null)
            return null;
        var values = new float[count];
        if(token.Type == JTokenType.String)
        {
            var parts = ((string)token!).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if(parts.Length < count)
                return null;
            for(int i = 0; i < count; i++)
                if(!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                    return null;
            return values;
        }
        if(token is JObject o)
        {
            string[] keys = ["X", "Y", "Z", "W"];
            for(int i = 0; i < count; i++)
            {
                var v = o[keys[i]] ?? o[keys[i].ToLowerInvariant()];
                if(v == null)
                    return null;
                values[i] = v.Value<float>();
            }
            return values;
        }
        return null;
    }

    private static Vector3? ReadVector(JToken? t) => Numbers(t, 3) is { } v ? new Vector3(v[0], v[1], v[2]) : null;

    private static Quaternion? ReadQuaternion(JToken? t) => Numbers(t, 4) is { } v ? new Quaternion(v[0], v[1], v[2], v[3]) : null;

    // The file's turns put on, each bone keeping its own distance from its parent (so a pose made on another body
    // fits this one); with positions, the file's places too. Sizes stay as they are. Returns how many bones matched.
    private static readonly HashSet<string> Placement = new() { "n_hara", "j_kosi" };

    private int ApplyImport(in ActorSkeleton skeleton, Dictionary<string, BoneTransform> file, bool positions)
    {
        int matched = 0;
        var parts = skeleton.CaptureModelPose();
        for(int p = 0; p < parts.Length; p++)
        {
            var info = p == 0 ? skeleton.Info : skeleton.PartialInfo(p, _cache);
            var old = parts[p];
            if(info == null || info.Count != old.Length)
                continue;
            if(p == 1)
            {
                // The extra skeletons hang from the body: line them up with its new pose first.
                skeleton.RealignPartials(_cache);
                parts = skeleton.CaptureModelPose();
                old = parts[p];
            }
            else if(p > 1)
                old = skeleton.CaptureModelPose()[p];
            var result = new BoneTransform[old.Length];
            for(int b = 0; b < old.Length; b++)
            {
                int parent = info.Parents[b];
                bool has = file.TryGetValue(info.Names[b], out var f);
                if(has)
                    matched++;
                if(parent < 0 || parent >= b)
                {
                    // A root (the body's, or an extra skeleton's root, which follows the body): where it is.
                    result[b] = p == 0 && has ? old[b] with { Rotation = b == 0 ? old[b].Rotation : f.Rotation } : old[b];
                    continue;
                }
                var turn = Quaternion.Normalize(result[parent].Rotation * Quaternion.Inverse(old[parent].Rotation));
                var rotation = has ? f.Rotation : Quaternion.Normalize(turn * old[b].Rotation);
                // Where the body is (the hips, and with them everything above and below) always comes from the file,
                // so two characters posed together line up again exactly as they were; the rest of the bones' places
                // (the body's proportions) only with "positions".
                bool place = p == 0 && Placement.Contains(info.Names[b]);
                var position = (positions || place) && has ? f.Position : result[parent].Position + Vector3.Transform(old[b].Position - old[parent].Position, turn);
                result[b] = new BoneTransform(position, rotation, old[b].Scale);
            }
            skeleton.WritePartModelPose(p, result);
        }
        skeleton.RealignPartials(_cache);
        skeleton.RefreshAttachedModels();
        return matched;
    }
}
