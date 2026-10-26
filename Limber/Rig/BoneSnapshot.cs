using Limber.Core;
using System.Collections.Generic;
using System.Numerics;

namespace Limber.Rig;

// Where one character's bones ended up this frame, for the UI to draw and grab. The body skeleton's bones come
// first (same indices as Info), then every bone only the extra skeletons have (face, hair, tail, mod skeletons).
public sealed class BoneSnapshot
{
    public required nint Actor { get; init; }
    public required SkeletonInfo Info { get; init; }
    public required Vector3[] WorldPositions { get; init; }
    public required Quaternion[] WorldRotations { get; init; }
    public required Quaternion RootRotation { get; init; }
    public required float Scale { get; init; }
    public Vector3 RootPosition { get; init; }

    // Every bone (body first, then the extra skeletons'), its parent in this list (-1: none) and which part of the
    // skeleton it's in (0 = body).
    public required string[] Names { get; init; }
    public required int[] Parents { get; init; }
    public required int[] Parts { get; init; }
    public Dictionary<string, int> Extra { get; init; } = new();

    public int Count => Names.Length;

    public int IndexOf(string bone)
    {
        int index = Info.IndexOf(bone);
        if(index >= 0)
            return index;
        return Extra.TryGetValue(bone, out var extra) ? extra : -1;
    }

    public bool TryGetWorld(string bone, out Frame frame)
    {
        int index = IndexOf(bone);
        if(index < 0 || index >= WorldPositions.Length)
        {
            frame = Frame.Identity;
            return false;
        }

        frame = new Frame(WorldPositions[index], WorldRotations[index]);
        return true;
    }
}
