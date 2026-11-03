using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin.Services;
using Limber.Core;

namespace Limber.Rig;

/// <summary>One character's breast size (Koikatsu-style tops), kept by name.</summary>
public sealed class BustShapeSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>Follow the character's own Bust slider (character creation / Glamourer / Anamnesis).</summary>
    public bool FromGame { get; set; } = true;
    /// <summary>The size when not following the game (0 flat .. 100 huge).</summary>
    public float Size { get; set; } = 50f;
    /// <summary>Added on top (either way): past 100 carries on growing a little.</summary>
    public float Extra { get; set; }
    /// <summary>Take off the game's own bust scaling (the shapes are the size already).</summary>
    public bool CancelGameScale { get; set; } = true;

    public BustShapeSettings Copy() => (BustShapeSettings)MemberwiseClone();
}

/// <summary>
/// Breast size for tops made with size shapes (Koikatsu-style: flat at 0, small mounds, small, medium, large, huge at
/// 100). Framework thread: who wears one and at what size (the character's Bust slider, or a size of its own); render
/// thread: the shapes blended into the drawn model (<see cref="Limber.Gpu.BustShapes"/>); bone update: the game's own
/// bust scaling taken off the breast bones of those characters, so the two don't stack. Adult characters only.
/// </summary>
public sealed unsafe class BustShapeService
{
    private readonly IObjectTable _objects;
    private readonly Func<Dictionary<string, BustShapeSettings>> _settings;
    private readonly SkeletonInfoCache _cache = new();
    private volatile (nint Address, ushort Index, float Size, bool Cancel)[] _targets = [];

    public BustShapeService(IObjectTable objects, Func<Dictionary<string, BustShapeSettings>> settings)
    {
        _objects = objects;
        _settings = settings;
    }

    /// <summary>The game's Bust slider value of a character (0..100), or -1.</summary>
    public static int GameBust(nint address)
    {
        if(address == 0)
            return -1;
        var c = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)address;
        var cz = (byte*)&c->DrawData.CustomizeData;
        return cz[23];
    }

    /// <summary>The size a character is shown at now, or -1.</summary>
    public float SizeOf(nint address)
    {
        foreach(var t in _targets)
            if(t.Address == address)
                return t.Size;
        return -1f;
    }

    /// <summary>Framework thread, once a frame.</summary>
    public void Refresh()
    {
        var all = _settings();
        var list = new List<(nint, ushort, float, bool)>();
        foreach(var obj in _objects)
        {
            if(obj is not Dalamud.Game.ClientState.Objects.Types.ICharacter || obj.Address == 0)
                continue;
            if(!Limber.Koi.Safety.AdultAllowed(obj.Address))
                continue;
            string name = obj.Name.TextValue;
            var s = name.Length > 0 && all.TryGetValue(name, out var mine) ? mine : Default;
            if(!s.Enabled)
                continue;
            float size = s.FromGame ? GameBust(obj.Address) : s.Size;
            if(size < 0)
                continue;
            size = Math.Clamp(size + s.Extra, 0f, 130f);
            list.Add((obj.Address, obj.ObjectIndex, size, s.CancelGameScale));
        }
        _targets = list.ToArray();
    }

    private static readonly BustShapeSettings Default = new();

    public void Clear() => _targets = [];

    /// <summary>Render thread: the shapes into the drawn models.</summary>
    public void UpdateMeshes(Func<string, byte[]?> readFile)
    {
        var targets = _targets;
        var wanted = new List<(nint, float)>(targets.Length);
        foreach(var t in targets)
            wanted.Add((t.Address, t.Size));
        Limber.Gpu.BustShapes.Update(wanted, readFile);
    }

    /// <summary>Bone update (first, before anything that sizes the breasts on purpose): the game's bust scaling off.</summary>
    public void UpdateFrame()
    {
        foreach(var (address, index, _, cancel) in _targets)
        {
            if(!cancel || !Limber.Gpu.BustShapes.Wears(address))
                continue;
            try
            {
                if(!ActorSkeleton.TryGet(address, index, _cache, out var sk) || sk.Info.Count == 0)
                    continue;
                foreach(var name in new[] { "j_mune_l", "j_mune_r" })
                {
                    int bone = sk.Info.IndexOf(name);
                    int parent = sk.Info.ParentOf(bone);
                    if(bone < 0 || parent < 0)
                        continue;
                    // the breast the size of the body round it (what the shapes were made for)
                    sk.SetModelScale(bone, sk.GetModelScale(parent));
                }
            }
            catch
            {
                // (the character went away)
            }
        }
    }
}
