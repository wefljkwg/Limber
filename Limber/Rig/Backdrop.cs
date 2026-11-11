using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using Limber.Koi.Props;

namespace Limber.Rig;

/// <summary>The backdrop (Extras page): a flat colour all round the chosen character.</summary>
public sealed class BackdropSettings
{
    /// <summary>On (never on by itself when Limber starts).</summary>
    [Newtonsoft.Json.JsonIgnore]
    public bool Enabled { get; set; }
    /// <summary>Its colour (0..1 each).</summary>
    public Vector3 Colour { get; set; } = Vector3.One;
    /// <summary>How brightly it glows: 1 is the colour as picked; higher pushes it toward pure white / the full colour.</summary>
    public float Brightness { get; set; } = 1f;
    /// <summary>How far it is from the character (metres): the camera has to stay inside it.</summary>
    public float Radius { get; set; } = 15f;
    /// <summary>The character (and the camera) lifted this high into the sky (metres; 0: left where they are).</summary>
    public float Lift { get; set; } = 40f;
    /// <summary>A floor of the same colour under their feet, hiding the ground (else they stand on the real ground).</summary>
    public bool Floor { get; set; }
    /// <summary>The floor's height above the character's feet (metres): up a little to cover grass and bumps.</summary>
    public float FloorHeight { get; set; } = 0.01f;
    /// <summary>It moves with the character (else it stays where it was put).</summary>
    public bool Follow { get; set; } = true;

    public BackdropSettings Copy() => (BackdropSettings)MemberwiseClone();
}

/// <summary>
/// A flat-coloured backdrop for clean pictures (a dataset for training, say): a large sphere seen from the inside and a
/// floor disc, both drawn only with their own glow (no texture colour, no shine), so they look the same flat colour from
/// every side whatever the scene's light. Made like Limber's Koikatsu props: a model, material and textures of its own
/// handed to the mod manager, drawn as a plain render object (only on your screen, nothing to touch or collide with).
/// Centred on the chosen character. Framework thread.
/// </summary>
public sealed unsafe class BackdropService : IDisposable
{
    private const string Folder = "bgcommon/limber/backdrop";

    private readonly Func<BackdropSettings> _settings;
    private readonly Func<(nint Address, ushort Index)> _chosen;
    private readonly ActorTransforms _actors;
    // The lift put on a character (taken off again when it's off, someone else is chosen, or Limber unloads).
    private nint _lifted;
    private float _liftedBy;
    private BgObject* _object;
    private bool _ready;
    private string _path = "";
    private readonly List<string> _files = new();
    private int _version;
    private (Vector3 Colour, float Brightness, bool Floor) _made = (new Vector3(-1f), -1f, false);
    private Vector3 _centre;
    private bool _haveCentre;

    /// <summary>What happened last (shown on the page).</summary>
    public string Note { get; private set; } = "";
    public bool Showing => _object != null && _ready;

    public BackdropService(Func<BackdropSettings> settings, Func<(nint Address, ushort Index)> chosen, ActorTransforms actors)
    {
        _settings = settings;
        _chosen = chosen;
        _actors = actors;
    }

    // The chosen character lifted by this much (the one lifted before let down first); the camera with it.
    private void SetLift(nint address, ushort index, float by)
    {
        if(_lifted != 0 && (_lifted != address || by != _liftedBy))
        {
            if(_actors.Get(_lifted) is { } before)
                lock(_actors.Sync)
                    before.Move -= new Vector3(0f, _liftedBy, 0f);
            _lifted = 0;
            _liftedBy = 0f;
        }
        if(address != 0 && by != 0f && _lifted == 0)
        {
            var o = _actors.GetOrAdd(address, index);
            lock(_actors.Sync)
                o.Move += new Vector3(0f, by, 0f);
            _lifted = address;
            _liftedBy = by;
        }
        CameraControl.SetWorldOffset(new Vector3(0f, _lifted != 0 ? _liftedBy : 0f, 0f));
    }

    private static string DiskDir => Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "backdrop");

    /// <summary>Framework thread, once a frame. <paramref name="allowed"/>: Limber runs here now (GPose).</summary>
    public void Update(bool allowed)
    {
        var s = _settings();
        if(!allowed || !s.Enabled)
        {
            Remove();
            SetLift(0, 0, 0f);
            if(!s.Enabled)
                _haveCentre = false;
            return;
        }
        var (address, index) = _chosen();
        bool here = Ground(address, index) != null;
        float lift = Math.Clamp(s.Lift, 0f, 300f);
        if(here)
            SetLift(address, index, lift);
        // Made again when its colour, brightness or floor changed.
        var want = (s.Colour, s.Brightness, s.Floor);
        if(_object == null || want != _made)
        {
            Remove();
            if(!Make(s))
                return;
            _made = want;
        }
        if(!_ready)
        {
            var handle = _object->ModelResourceHandle;
            if(handle == null || ((ResourceHandle*)handle)->LoadState != 7)
                return;
            _ready = true;
        }
        // Where: under the chosen character's feet (up in the sky with them).
        if(s.Follow || !_haveCentre)
        {
            if(Ground(address, index) is { } feet)
            {
                _centre = feet + new Vector3(0f, _lifted == address ? _liftedBy : 0f, 0f);
                _haveCentre = true;
            }
        }
        if(!_haveCentre)
            return;
        var obj = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object*)_object;
        float radius = Math.Clamp(s.Radius, 2f, 200f);
        obj->Position = _centre + new Vector3(0f, Math.Clamp(s.FloorHeight, -0.5f, 2f), 0f);
        obj->Rotation = Quaternion.Identity;
        obj->Scale = new Vector3(radius);
        var draw = (DrawObject*)_object;
        draw->SetTransparency(0f);
        draw->UpdateTransforms(false);
        draw->UpdateCulling();
    }

    // Where the character stands (the game's place for it, before Limber's moves), or null when it's gone.
    private static Vector3? Ground(nint address, ushort index)
    {
        if(address == 0)
            return null;
        var o = Plugin.ObjectTable[index];
        if(o == null || o.Address != address)
            return null;
        return o.Position;
    }

    // Its files for this colour, handed to the mod manager, and the render object made.
    private bool Make(BackdropSettings s)
    {
        if(KoiProps.Template() is not { } template)
        {
            Note = "Couldn't find a game model to make it from.";
            return false;
        }
        var (mdl, mtrl, slots) = template;
        try
        {
            _version++;
            string tag = $"v{_version}";
            var files = new Dictionary<string, byte[]>();
            string diffuse = $"{Folder}/texture/backdrop_{tag}_d.tex";
            string normal = $"{Folder}/texture/backdrop_{tag}_n.tex";
            string spec = $"{Folder}/texture/backdrop_{tag}_s.tex";
            string material = $"{Folder}/material/backdrop_{tag}.mtrl";
            string model = $"{Folder}/bgparts/backdrop_{tag}.mdl";
            // Its colour, no shine, and the same colour glowing (so it's that colour even out of the light).
            byte R(float v) => (byte)Math.Clamp(v * 255f + 0.5f, 0f, 255f);
            files[diffuse] = TexWriter.Solid(R(s.Colour.X), R(s.Colour.Y), R(s.Colour.Z), 255);
            files[normal] = TexWriter.Solid(128, 128, 255, 255);
            files[spec] = TexWriter.Solid(0, 255, 0, 255);
            var glow = s.Colour * Math.Clamp(s.Brightness, 0f, 20f);
            var textures = new List<string>();
            foreach(var slot in slots)
                textures.Add(slot == "d" ? diffuse : slot == "n" ? normal : spec);
            files[material] = MtrlBuilder.SetConstant(MtrlBuilder.With(mtrl, textures), MtrlBuilder.EmissiveColor, [glow.X, glow.Y, glow.Z]);
            var meshes = new List<PropMesh> { Sphere(24, 48) };
            if(s.Floor)
                meshes.Add(Disc(64));
            files[model] = MdlBuilder.Build(mdl, meshes, [material, material]);

            Directory.CreateDirectory(DiskDir);
            foreach(var old in _files)
                KoiProps.Extra.Remove(old);
            _files.Clear();
            foreach(var (game, bytes) in files)
            {
                var disk = Path.Combine(DiskDir, Path.GetFileName(game));
                File.WriteAllBytes(disk, bytes);
                KoiProps.Extra[game] = disk;
                _files.Add(game);
            }
            KoiProps.Register();
            _path = model;
            _object = BgObject.Create(_path, string.Empty);
            _ready = false;
            Note = _object == null ? "The game didn't make it." : "";
            return _object != null;
        }
        catch(Exception e)
        {
            Note = "Couldn't make it: " + e.Message;
            Plugin.Log.Warning(e, "Limber: backdrop");
            _object = null;
            return false;
        }
    }

    // A sphere of radius 1 seen from the inside (its normals point in).
    private static PropMesh Sphere(int rings, int segments)
    {
        var m = new PropMesh { Material = 0 };
        for(int i = 0; i <= rings; i++)
        {
            float theta = MathF.PI * i / rings;
            for(int j = 0; j <= segments; j++)
            {
                float phi = 2f * MathF.PI * j / segments;
                var p = new Vector3(MathF.Sin(theta) * MathF.Cos(phi), MathF.Cos(theta), MathF.Sin(theta) * MathF.Sin(phi));
                m.P.Add(p);
                m.N.Add(-p);
                m.UV.Add(new Vector2((float)j / segments, (float)i / rings));
                m.T.Add(new Vector4(-MathF.Sin(phi), 0f, MathF.Cos(phi), 1f));
            }
        }
        int row = segments + 1;
        for(int i = 0; i < rings; i++)
            for(int j = 0; j < segments; j++)
            {
                int a = i * row + j, b = a + row;
                // (facing the centre)
                m.I.AddRange([a, a + 1, b]);
                m.I.AddRange([a + 1, b + 1, b]);
            }
        return m;
    }

    // A flat disc of radius 1 at height 0, facing up (the floor).
    private static PropMesh Disc(int segments)
    {
        var m = new PropMesh { Material = 1 };
        m.P.Add(Vector3.Zero);
        m.N.Add(Vector3.UnitY);
        m.UV.Add(new Vector2(0.5f, 0.5f));
        m.T.Add(new Vector4(1f, 0f, 0f, 1f));
        for(int j = 0; j <= segments; j++)
        {
            float phi = 2f * MathF.PI * j / segments;
            var p = new Vector3(MathF.Cos(phi), 0f, MathF.Sin(phi));
            m.P.Add(p);
            m.N.Add(Vector3.UnitY);
            m.UV.Add(new Vector2(0.5f + 0.5f * p.X, 0.5f + 0.5f * p.Z));
            m.T.Add(new Vector4(1f, 0f, 0f, 1f));
        }
        for(int j = 1; j <= segments; j++)
            m.I.AddRange([0, j + 1, j]);
        return m;
    }

    private void Remove()
    {
        if(_object == null)
            return;
        try
        {
            var obj = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object*)_object;
            obj->CleanupRender();
            obj->Dtor(1);
        }
        catch(Exception e)
        {
            Plugin.Log.Warning(e, "Limber: couldn't remove the backdrop");
        }
        _object = null;
        _ready = false;
        _made = (new Vector3(-1f), -1f, false);
    }

    public void Dispose()
    {
        Remove();
        SetLift(0, 0, 0f);
        CameraControl.SetWorldOffset(Vector3.Zero);
        foreach(var old in _files)
            KoiProps.Extra.Remove(old);
        _files.Clear();
    }
}
