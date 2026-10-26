using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using Limber.Protocol;

namespace Limber.Video;

/// <summary>One frame of motion: a position per protocol joint, in the same space the Koikatsu sender uses
/// (character-local, +X = character's right, +Y = up, +Z = forward, metres, feet on the floor).</summary>
public sealed class MotionFrame
{
    public readonly Vector3[] P = new Vector3[(int)Joint.Count];
    public readonly bool[] V = new bool[(int)Joint.Count];

    public MotionFrame Clone()
    {
        var f = new MotionFrame();
        Array.Copy(P, f.P, P.Length);
        Array.Copy(V, f.V, V.Length);
        return f;
    }
}

/// <summary>Motion tracked from a video: frames at a fixed rate, saved as a small binary file.</summary>
public sealed class MotionClip
{
    private const uint FileMagic = 0x43424D4C; // "LMBC"
    private const int FileVersion = 2; // 2: several people (tracks); 1: one person
    public const string Extension = ".limberclip";

    public string Name { get; set; } = "Clip";
    public float Fps { get; set; } = 30f;

    /// <summary>One frame list per person (all the same length). Person 1 is track 0.</summary>
    public List<List<MotionFrame>> Tracks { get; } = new() { new List<MotionFrame>() };

    /// <summary>Frames in which each person was found (for the UI).</summary>
    public List<int> Tracked { get; } = new() { 0 };

    public List<MotionFrame> Frames => Tracks[0];
    public int People => Tracks.Count;

    public double Duration => Frames.Count / (double)Math.Max(1f, Fps);

    /// <summary>Make room for <paramref name="people"/> tracks.</summary>
    public void SetPeople(int people)
    {
        while (Tracks.Count < people)
        {
            Tracks.Add(new List<MotionFrame>());
            Tracked.Add(0);
        }
    }

    // ---- Files

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs, Encoding.UTF8);
        w.Write(FileMagic);
        w.Write(FileVersion);
        w.Write(Name);
        w.Write(Fps);
        w.Write(Tracks.Count);
        w.Write((int)Joint.Count);
        w.Write(Frames.Count);
        for (int t = 0; t < Tracks.Count; t++)
        {
            w.Write(Tracked[t]);
            foreach (var f in Tracks[t])
            {
                for (int j = 0; j < (int)Joint.Count; j++)
                {
                    w.Write(f.V[j]);
                    w.Write(f.P[j].X);
                    w.Write(f.P[j].Y);
                    w.Write(f.P[j].Z);
                }
            }
        }
    }

    public static MotionClip Load(string path)
    {
        using var fs = File.OpenRead(path);
        using var r = new BinaryReader(fs, Encoding.UTF8);
        if (r.ReadUInt32() != FileMagic)
            throw new InvalidDataException("Not a Limber clip.");
        var version = r.ReadInt32();
        if (version is not (1 or 2))
            throw new InvalidDataException($"Clip version {version} isn't supported by this Limber.");

        var clip = new MotionClip
        {
            Name = r.ReadString(),
            Fps = Math.Clamp(r.ReadSingle(), 1f, 240f),
        };
        int people = 1, firstTracked = 0;
        if (version == 1)
            firstTracked = r.ReadInt32();
        else
            people = r.ReadInt32();
        int joints = r.ReadInt32();
        int count = r.ReadInt32();
        if (people < 1 || people > 8 || joints <= 0 || joints > 1024 || count < 0 || count > 10_000_000)
            throw new InvalidDataException("Damaged clip file.");

        clip.SetPeople(people);
        for (int t = 0; t < people; t++)
        {
            clip.Tracked[t] = version == 1 ? firstTracked : r.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                var f = new MotionFrame();
                for (int j = 0; j < joints; j++)
                {
                    var valid = r.ReadBoolean();
                    var p = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    if (j >= (int)Joint.Count)
                        continue; // clip from a newer Limber with more joints
                    f.V[j] = valid && float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
                    f.P[j] = p;
                }
                clip.Tracks[t].Add(f);
            }
        }
        return clip;
    }

    // ---- Playback packets (the Koikatsu link's wire format, so everything downstream just works)

    private static readonly int[] MirrorMap = BuildMirrorMap();

    /// <summary>Encode a frame as a pose packet. Mirroring swaps left and right (for selfie-style videos).</summary>
    public byte[] ToPacket(int track, int frameIndex, uint frameNumber, int channel, bool mirror)
    {
        var frames = Tracks[Math.Clamp(track, 0, Tracks.Count - 1)];
        var name = People > 1 ? $"{Name} ({track + 1})" : Name;
        return Encode(frames[Math.Clamp(frameIndex, 0, frames.Count - 1)], name, frameNumber, channel, mirror);
    }

    /// <summary>Encode any motion frame as a pose packet (also used for live VLC tracking).</summary>
    public static byte[] Encode(MotionFrame f, string name, uint frameNumber, int channel, bool mirror)
    {
        using var ms = new MemoryStream(1400);
        using var w = new BinaryWriter(ms, Encoding.UTF8);

        w.Write(Wire.Magic);
        w.Write(Wire.Version);
        w.Write((ushort)Joint.Count);
        w.Write(frameNumber);
        w.Write((byte)channel);
        for (int j = 0; j < (int)Joint.Count; j++)
        {
            int src = mirror ? MirrorMap[j] : j;
            var p = f.P[src];
            if (mirror)
                p.X = -p.X;
            w.Write((byte)(f.V[src] ? 1 : 0));
            w.Write(p.X);
            w.Write(p.Y);
            w.Write(p.Z);
        }

        // No face data from video (yet).
        w.Write((byte)0);   // face flags
        w.Write(0);         // eyes pattern
        w.Write(0);         // mouth pattern
        w.Write(0);         // eyebrow pattern
        w.Write(1f);        // eyes open max
        w.Write(1f);        // eye open
        w.Write(0f);        // mouth open
        w.Write(0f); w.Write(0f); w.Write(1f); // gaze

        var nameBytes = Encoding.UTF8.GetBytes(name.Length > 60 ? name[..60] : name);
        w.Write((ushort)nameBytes.Length);
        w.Write(nameBytes);
        w.Write((byte)0);   // no face regions
        w.Write((byte)0);   // sender flags
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>Joint index -> its left/right counterpart (by name: ...L / ...L1 <-> ...R / ...R1).</summary>
    private static int[] BuildMirrorMap()
    {
        var names = Enum.GetNames<Joint>();
        var map = new int[(int)Joint.Count];
        for (int i = 0; i < map.Length; i++)
        {
            map[i] = i;
            var name = ((Joint)i).ToString();
            var other = Regex.Replace(name, "L(?=\\d?$)", "R");
            if (other == name)
                other = Regex.Replace(name, "R(?=\\d?$)", "L");
            if (other != name && Array.IndexOf(names, other) is var k and >= 0 && k < map.Length)
                map[i] = k;
        }
        return map;
    }
}
