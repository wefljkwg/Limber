using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Group;
using LayoutTransform = FFXIVClientStructs.FFXIV.Client.LayoutEngine.Transform;

namespace Proppy;

/// <summary>
/// The placed items: each a shared-group layout made by the game's own housing code (the same way Brio places
/// furniture), with its models, lights and collision. Brio switches the collision off; here it's kept on, so you can
/// stand and jump on the items. Only on this PC: nothing is sent to the server. Framework thread only.
/// </summary>
public sealed unsafe class PropService : IDisposable
{
    public sealed class Prop
    {
        public required string Path;
        public required string Name;
        public Vector3 Position;
        public Vector3 Rotation; // degrees: yaw, pitch, roll
        public Vector3 Scale = Vector3.One;
        public bool Collision = true;
        public SharedGroupLayoutInstance* Layout;
        public bool Dirty = true;
        public int Settle = 600; // frames to keep making sure the collision is as wanted (it appears once loaded)

        public Quaternion Quaternion => Quaternion.CreateFromYawPitchRoll(Rotation.X * MathF.PI / 180f, Rotation.Y * MathF.PI / 180f, Rotation.Z * MathF.PI / 180f);
    }

    private const int IsReadyVTable = 22;
    private readonly delegate* unmanaged<short, uint, LayoutTransform*, byte*, byte*, byte, uint, int, nint, nint, SharedGroupLayoutInstance*> _create;
    private readonly delegate* unmanaged<SharedGroupLayoutInstance**, nint, void> _destroy;
    private readonly delegate* unmanaged<SharedGroupLayoutInstance*, byte, void> _setupStain;
    private readonly IPluginLog _log;

    public readonly List<Prop> Props = new();
    public string Problem { get; } = "";

    public PropService(ISigScanner scanner, IPluginLog log)
    {
        _log = log;
        try
        {
            // (the same game functions Brio uses: make a layout, destroy it, set up its dyes)
            _create = (delegate* unmanaged<short, uint, LayoutTransform*, byte*, byte*, byte, uint, int, nint, nint, SharedGroupLayoutInstance*>)
                scanner.ScanText("E8 ?? ?? ?? ?? 48 89 04 ?? C6 44");
            _destroy = (delegate* unmanaged<SharedGroupLayoutInstance**, nint, void>)
                scanner.ScanText("48 89 5C 24 08 48 89 74 24 10 57 48 83 EC 20 48 8B 19 48 8B F9 48 8B F2");
            _setupStain = (delegate* unmanaged<SharedGroupLayoutInstance*, byte, void>)
                scanner.ScanText("E8 ?? ?? ?? ?? 48 8B 8F ?? ?? ?? ?? 0F B6 47");
        }
        catch(Exception e)
        {
            Problem = "Couldn't find the game's housing functions (a game patch?): " + e.Message;
            _log.Error(e, "Proppy: " + Problem);
        }
    }

    public bool Ready => _create != null && _destroy != null;

    private static LayoutTransform TransformOf(Prop p) => new()
    {
        Translation = p.Position,
        Rotation = p.Quaternion,
        Scale = p.Scale,
    };

    public Prop? Place(string path, string name, Vector3 position, Vector3 rotation, Vector3 scale, bool collision = true)
    {
        if(!Ready)
            return null;
        var prop = new Prop { Path = path, Name = name, Position = position, Rotation = rotation, Scale = scale, Collision = collision };
        if(!Create(prop))
            return null;
        Props.Add(prop);
        return prop;
    }

    private bool Create(Prop prop)
    {
        var transform = TransformOf(prop);
        var bytes = System.Text.Encoding.UTF8.GetBytes(prop.Path + "\0");
        fixed(byte* path = bytes)
        {
            // (-1: the global layer; 0: a key made for it; 1: flags; 0xC: a shared group, ours to own and destroy)
            prop.Layout = _create(-1, 0, &transform, path, null, 1, 0, 0xC, 0, 0);
        }
        if(prop.Layout == null)
        {
            _log.Warning($"Proppy: the game didn't make {prop.Path}");
            return false;
        }
        if(_setupStain != null)
            _setupStain(prop.Layout, 0);
        prop.Layout->SetTransformImpl(&transform);
        prop.Dirty = true;
        prop.Settle = 600;
        return true;
    }

    private bool IsLoaded(Prop prop)
    {
        if(prop.Layout == null)
            return false;
        var vtable = *(nint**)prop.Layout;
        if(vtable == null)
            return false;
        var isReady = (delegate* unmanaged<SharedGroupLayoutInstance*, byte>)vtable[IsReadyVTable];
        return isReady != null && isReady(prop.Layout) != 0;
    }

    public void Remove(Prop prop)
    {
        Destroy(prop);
        Props.Remove(prop);
    }

    private void Destroy(Prop prop)
    {
        if(prop.Layout == null)
            return;
        try
        {
            var layout = prop.Layout;
            layout->Deinit();
            _destroy(&layout, 0);
        }
        catch(Exception e)
        {
            _log.Warning(e, "Proppy: couldn't remove an item");
        }
        prop.Layout = null;
    }

    public void RemoveAll()
    {
        foreach(var p in Props)
            Destroy(p);
        Props.Clear();
    }

    /// <summary>Framework update: moved items put where they're wanted, and their collision kept on (or off).</summary>
    public void Update()
    {
        foreach(var p in Props)
        {
            if(p.Layout == null)
                continue;
            if(p.Dirty)
            {
                var t = TransformOf(p);
                p.Layout->SetTransformImpl(&t);
                p.Layout->Instances.ApplyTransforms();
                p.Dirty = false;
                p.Settle = Math.Max(p.Settle, 60);
            }
            // (the collision appears once the item has loaded, and a move can reset it: set again for a while after)
            if(p.Settle > 0)
            {
                p.Settle--;
                if(IsLoaded(p))
                    p.Layout->Instances.SetCollidersActive(p.Collision);
            }
        }
    }

    public void Dispose() => RemoveAll();
}
