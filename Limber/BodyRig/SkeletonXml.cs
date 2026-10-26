using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Xml;

namespace Limber.BodyRig;

/// <summary>
/// The main skeleton inside a Havok XML tagfile: bone names, parents and rest pose, with the ability to add bones
/// at the end (so every existing bone keeps its index, which the game's own data relies on).
/// </summary>
public sealed class SkeletonXml
{
    public readonly struct Transform
    {
        public readonly Vector3 Position;
        public readonly Quaternion Rotation;
        public readonly Vector3 Scale;

        public Transform(Vector3 position, Quaternion rotation, Vector3 scale)
        {
            Position = position;
            Rotation = rotation;
            Scale = scale;
        }

        /// <summary>This transform applied after <paramref name="parent"/> (local -> model).</summary>
        public Transform Under(Transform parent) => new(
            parent.Position + Vector3.Transform(parent.Scale * Position, parent.Rotation),
            Quaternion.Normalize(parent.Rotation * Rotation),
            parent.Scale * Scale);
    }

    private readonly XmlDocument _doc = new() { PreserveWhitespace = false };
    private readonly XmlElement _parents, _bones, _pose;

    public List<string> Names { get; } = new();
    public List<int> Parents { get; } = new();
    public List<Transform> Local { get; } = new();

    public SkeletonXml(string xml)
    {
        _doc.LoadXml(xml);
        // The skeleton references may be written as child elements or as plain text ("#0051"); accept either.
        var skeletons = _doc.SelectSingleNode("/hktagfile/object[@type='hkaAnimationContainer']/array[@name='skeletons']")?
            .ChildNodes.Cast<XmlNode>().Where(n => n.NodeType is not XmlNodeType.Comment)
            .SelectMany(n => n.InnerText.Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)).ToList();
        if (skeletons == null || skeletons.Count != 1)
            throw new InvalidDataException($"Expected one skeleton in this file, found {skeletons?.Count ?? 0}.");
        var id = skeletons[0];
        var skeleton = _doc.SelectSingleNode($"/hktagfile/object[@type='hkaSkeleton'][@id='{id}']") as XmlElement
            ?? throw new InvalidDataException("The main skeleton is missing.");

        _parents = Array(skeleton, "parentIndices");
        _bones = Array(skeleton, "bones");
        _pose = Array(skeleton, "referencePose");

        foreach (var text in _parents.InnerText.Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries))
            Parents.Add(int.Parse(text, CultureInfo.InvariantCulture));
        foreach (var bone in Elements(_bones))
            Names.Add(bone.SelectSingleNode("string[@name='name']")?.InnerText ?? throw new InvalidDataException("A bone has no name."));
        foreach (var entry in Elements(_pose))
            Local.Add(ReadVec12(entry));

        int count = Names.Count;
        if (Parents.Count != count || Local.Count != count || Size(_parents) != count || Size(_bones) != count || Size(_pose) != count)
            throw new InvalidDataException($"The skeleton's bone lists don't agree (names {count}, parents {Parents.Count}, pose {Local.Count}).");

        // Safety: refuse skeletons with any other per-bone list Limber doesn't know how to extend.
        foreach (var array in skeleton.ChildNodes.OfType<XmlElement>().Where(e => e.Name == "array"))
        {
            var name = array.GetAttribute("name");
            if (name is "parentIndices" or "bones" or "referencePose")
                continue;
            if (Size(array) == count && count > 0)
                throw new InvalidDataException($"This skeleton has another per-bone list (\"{name}\") Limber can't extend safely.");
        }
    }

    public int Count => Names.Count;

    public int IndexOf(string name) => Names.IndexOf(name);

    /// <summary>Rest pose of every bone in model space.</summary>
    public Transform[] ModelPose()
    {
        var model = new Transform[Count];
        for (int i = 0; i < Count; i++)
        {
            int p = Parents[i];
            if (p >= i)
                throw new InvalidDataException("This skeleton lists a bone before its parent.");
            model[i] = p < 0 ? Local[i] : Local[i].Under(model[p]);
        }
        return model;
    }

    /// <summary>Remove bones matching <paramref name="match"/>; they must all be at the end (as Limber adds them).</summary>
    public int RemoveTrailing(Func<string, bool> match)
    {
        int first = Names.FindIndex(n => match(n));
        if (first < 0)
            return 0;
        if (Names.Skip(first).Any(n => !match(n)) || Parents.Skip(first).Any(p => p >= first && p >= 0 && !match(Names[p])))
            throw new InvalidDataException("This skeleton already has Limber's rig bones mixed in with other bones; start from the original skeleton.");
        int removed = Count - first;
        Names.RemoveRange(first, removed);
        Parents.RemoveRange(first, removed);
        Local.RemoveRange(first, removed);
        var bones = Elements(_bones).ToList();
        var pose = Elements(_pose).ToList();
        for (int i = first; i < bones.Count; i++)
            _bones.RemoveChild(bones[i]);
        for (int i = first; i < pose.Count; i++)
            _pose.RemoveChild(pose[i]);
        return removed;
    }

    /// <summary>Add a bone at the end. Its parent must already exist.</summary>
    public int Add(string name, int parent, Transform local)
    {
        if (parent < 0 || parent >= Count)
            throw new ArgumentOutOfRangeException(nameof(parent));
        if (Names.Contains(name))
            throw new InvalidDataException($"The skeleton already has a bone called {name}.");

        var bone = (XmlElement)Elements(_bones).Last().CloneNode(true);
        var nameNode = bone.SelectSingleNode("string[@name='name']")!;
        nameNode.InnerText = name;
        foreach (var flag in bone.SelectNodes("bool")!.OfType<XmlElement>())
            flag.InnerText = "false"; // e.g. lockTranslation
        _bones.AppendChild(bone);

        var entry = (XmlElement)Elements(_pose).Last().CloneNode(true);
        WriteVec12(entry, local);
        _pose.AppendChild(entry);

        Names.Add(name);
        Parents.Add(parent);
        Local.Add(local);
        return Count - 1;
    }

    public string ToXml()
    {
        foreach (var array in new[] { _parents, _bones, _pose })
            array.SetAttribute("size", Count.ToString(CultureInfo.InvariantCulture));
        _parents.InnerText = string.Join(" ", Parents.Select(p => p.ToString(CultureInfo.InvariantCulture)));

        var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false), OmitXmlDeclaration = false };
        using var writer = new StringWriterUtf8();
        using (var xml = XmlWriter.Create(writer, settings))
            _doc.Save(xml);
        return writer.ToString();
    }

    // ---- XML helpers

    private static XmlElement Array(XmlElement skeleton, string name)
        => skeleton.SelectSingleNode($"array[@name='{name}']") as XmlElement
           ?? throw new InvalidDataException($"The skeleton has no {name} list.");

    private static int Size(XmlElement array)
        => int.TryParse(array.GetAttribute("size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : -1;

    private static IEnumerable<XmlElement> Elements(XmlElement parent) => parent.ChildNodes.OfType<XmlElement>();

    /// <summary>The value nodes of a vec12 entry: everything but comments (same rule as Penumbra's reader).</summary>
    private static IEnumerable<XmlNode> Values(XmlElement entry)
        => entry.ChildNodes.Cast<XmlNode>().Where(n => n.NodeType is not (XmlNodeType.Comment or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace));

    private static Transform ReadVec12(XmlElement entry)
    {
        var v = Values(entry).Select(e => ParseReal(e.InnerText)).ToArray();
        if (v.Length != 12)
            throw new InvalidDataException($"Unexpected rest-pose entry ({v.Length} values).");
        return new Transform(new Vector3(v[0], v[1], v[2]), Quaternion.Normalize(new Quaternion(v[4], v[5], v[6], v[7])),
            new Vector3(v[8], v[9], v[10]));
    }

    private static void WriteVec12(XmlElement entry, Transform t)
    {
        var values = new[]
        {
            t.Position.X, t.Position.Y, t.Position.Z, 0f,
            t.Rotation.X, t.Rotation.Y, t.Rotation.Z, t.Rotation.W,
            t.Scale.X, t.Scale.Y, t.Scale.Z, 0f,
        };
        var children = Values(entry).ToList();
        if (children.Count != 12)
            throw new InvalidDataException("Unexpected rest-pose entry layout.");
        // The fourth value of position and scale: keep whatever the game uses (copied from the cloned entry).
        for (int i = 0; i < 12; i++)
        {
            if (i is 3 or 11)
                continue;
            children[i].InnerText = FormatReal(values[i], children[i].InnerText);
        }
    }

    private static float ParseReal(string text)
    {
        var s = text.Trim();
        if (s.StartsWith('x'))
            return BitConverter.Int32BitsToSingle(int.Parse(s.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        return float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    /// <summary>Same notation as the value it replaces (Havok writes exact hex bits, e.g. x3f800000).</summary>
    private static string FormatReal(float value, string like)
        => like.Trim().StartsWith('x')
            ? "x" + BitConverter.SingleToInt32Bits(value).ToString("x8", CultureInfo.InvariantCulture)
            : value.ToString("R", CultureInfo.InvariantCulture);

    private sealed class StringWriterUtf8 : StringWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
    }
}
