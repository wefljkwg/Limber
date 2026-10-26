using FFXIVClientStructs.Havok.Animation.Rig;
using System.Collections.Generic;

namespace Limber.Core;

// Names and hierarchy of one Havok skeleton, read once and reused.
public sealed unsafe class SkeletonInfo
{
    public readonly string[] Names;
    public readonly int[] Parents;
    public readonly int[] Depths;
    private readonly Dictionary<string, int> _byName;

    private SkeletonInfo(string[] names, int[] parents)
    {
        Names = names;
        Parents = parents;
        Depths = new int[names.Length];
        _byName = new Dictionary<string, int>(names.Length);

        for(int i = 0; i < names.Length; i++)
        {
            _byName.TryAdd(names[i], i);

            int depth = 0;
            int parent = parents[i];
            while(parent >= 0 && depth < 512)
            {
                depth++;
                parent = parents[parent];
            }
            Depths[i] = depth;
        }
    }

    public int Count => Names.Length;

    public int IndexOf(string name) => _byName.TryGetValue(name, out var index) ? index : -1;

    public int ParentOf(int index) => index >= 0 && index < Parents.Length ? Parents[index] : -1;

    // Nearest ancestor that is a real joint (helper bones named n_* are skipped).
    public int JointParentOf(int index)
    {
        int parent = ParentOf(index);
        while(parent >= 0 && Names[parent].StartsWith("n_"))
            parent = ParentOf(parent);
        return parent;
    }

    public static SkeletonInfo? Read(hkaSkeleton* skeleton)
    {
        if(skeleton == null)
            return null;

        int count = skeleton->Bones.Length;
        if(count <= 0 || count > 4096 || skeleton->Bones.Data == null || skeleton->ParentIndices.Data == null)
            return null;

        var names = new string[count];
        var parents = new int[count];
        for(int i = 0; i < count; i++)
        {
            names[i] = skeleton->Bones.Data[i].Name.String ?? string.Empty;
            parents[i] = i < skeleton->ParentIndices.Length ? skeleton->ParentIndices.Data[i] : -1;
        }

        return new SkeletonInfo(names, parents);
    }
}

// Keeps one SkeletonInfo per Havok skeleton resource.
public sealed unsafe class SkeletonInfoCache
{
    private readonly Dictionary<nint, SkeletonInfo> _cache = [];

    public SkeletonInfo? Get(hkaSkeleton* skeleton)
    {
        if(skeleton == null)
            return null;

        var key = (nint)skeleton;
        if(_cache.TryGetValue(key, out var info))
        {
            // The same address can be reused by a different skeleton after a reload.
            if(info.Count == skeleton->Bones.Length && info.Count > 0
                && info.Names[0] == (skeleton->Bones.Data[0].Name.String ?? string.Empty))
                return info;
        }

        info = SkeletonInfo.Read(skeleton);
        if(info != null)
        {
            if(_cache.Count > 256)
                _cache.Clear();
            _cache[key] = info;
        }
        return info;
    }
}
