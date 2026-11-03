using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using GameCameraManager = FFXIVClientStructs.FFXIV.Client.Game.Control.CameraManager;
using GameCamera = FFXIVClientStructs.FFXIV.Client.Game.Camera;

namespace Limber.Rig;

/// <summary>A camera place to come back to (Camera page), and what the timeline keys.</summary>
public sealed class CameraShot
{
    public string Name { get; set; } = "Shot";
    public float Distance { get; set; } = 3f;
    public float FoV { get; set; }
    /// <summary>Round the character (radians, 0 = north).</summary>
    public float Yaw { get; set; }
    /// <summary>Up / down (radians, + = looking up).</summary>
    public float Pitch { get; set; }
    public float Tilt { get; set; }
}

/// <summary>
/// The game camera (in GPose or out): its distance (zoom), field of view, the angle round and up / down, and its
/// tilt, read and set directly; its limits widened (closer / further, steeper angles, a wider field of view) while
/// "Unlock" is on; moved off its usual place (sideways, up, forward) on top of where the game puts it; and kept from
/// bumping into floors and walls. Framework thread. Only on your screen.
/// </summary>
public static unsafe class CameraControl
{
    private static GameCamera* Active()
    {
        var manager = GameCameraManager.Instance();
        return manager == null ? null : manager->GetActiveCamera();
    }

    public static bool Available => Active() != null;

    public static CameraShot? Read()
    {
        var c = Active();
        if(c == null)
            return null;
        return new CameraShot { Distance = c->Distance, FoV = c->FoV, Yaw = c->DirH, Pitch = c->DirV, Tilt = c->TiltOffset };
    }

    public static void Write(CameraShot shot, bool distance = true, bool fov = true, bool angles = true, bool tilt = true)
    {
        var c = Active();
        if(c == null)
            return;
        if(distance && float.IsFinite(shot.Distance))
        {
            float d = Math.Clamp(shot.Distance, c->MinDistance, c->MaxDistance);
            c->Distance = d;
            c->InterpDistance = d;
        }
        if(fov && float.IsFinite(shot.FoV))
            c->FoV = Math.Clamp(shot.FoV, c->MinFoV, c->MaxFoV);
        if(angles && float.IsFinite(shot.Yaw) && float.IsFinite(shot.Pitch))
        {
            float yaw = shot.Yaw;
            while(yaw > MathF.PI) yaw -= MathF.Tau;
            while(yaw < -MathF.PI) yaw += MathF.Tau;
            c->DirH = yaw;
            c->DirV = Math.Clamp(shot.Pitch, c->DirVMin, c->DirVMax);
        }
        if(tilt && float.IsFinite(shot.Tilt))
            c->TiltOffset = shot.Tilt;
    }

    // ---- Unlocking the limits (kept on every frame while wanted; put back when turned off)

    private static bool _unlocked;
    private static (float MinD, float MaxD, float MinF, float MaxF, float MinV, float MaxV) _limits;

    /// <summary>Framework thread, once a frame.</summary>
    public static void Update(bool unlock, float maxDistance)
    {
        var c = Active();
        if(c == null)
            return;
        if(unlock)
        {
            if(!_unlocked)
            {
                _limits = (c->MinDistance, c->MaxDistance, c->MinFoV, c->MaxFoV, c->DirVMin, c->DirVMax);
                _unlocked = true;
            }
            c->MinDistance = 0.1f;
            c->MaxDistance = Math.Clamp(maxDistance, 5f, 200f);
            c->MinFoV = -1.2f;
            c->MaxFoV = 1.6f;
            c->DirVMin = -1.55f;
            c->DirVMax = 1.55f;
        }
        else if(_unlocked)
        {
            (c->MinDistance, c->MaxDistance, c->MinFoV, c->MaxFoV, c->DirVMin, c->DirVMax) = _limits;
            c->Distance = Math.Clamp(c->Distance, c->MinDistance, c->MaxDistance);
            c->FoV = Math.Clamp(c->FoV, c->MinFoV, c->MaxFoV);
            c->DirV = Math.Clamp(c->DirV, c->DirVMin, c->DirVMax);
            _unlocked = false;
        }
    }

    // ---- Moved off its place: right after the game updates the camera, it and what it looks at go along by the offset
    // (in the camera's own directions), so the game builds the view from there.

    private delegate void CameraUpdate(nint camera);
    private static Hook<CameraUpdate>? _update;
    private static nint _updateAt;
    /// <summary>Sideways (+ right), up, forward, in metres, along the way the camera looks.</summary>
    public static volatile bool HasOffset;
    private static Vector3 _offset;

    public static void SetOffset(Vector3 offset)
    {
        _offset = offset;
        HasOffset = offset != Vector3.Zero;
        if(HasOffset)
            EnsureHook();
    }

    private static void EnsureHook()
    {
        var c = Active();
        if(c == null)
            return;
        try
        {
            nint fn = ((nint*)*(nint*)c)[3]; // the camera's update (CameraBase)
            if(fn == _updateAt && _update != null)
                return;
            _update?.Dispose();
            _update = Plugin.GameInterop.HookFromAddress<CameraUpdate>(fn, UpdateDetour);
            _update.Enable();
            _updateAt = fn;
        }
        catch(Exception e)
        {
            Plugin.Log.Warning(e, "Limber: couldn't hook the camera update (camera offset won't work)");
        }
    }

    private static void UpdateDetour(nint camera)
    {
        _update!.Original(camera);
        if(!HasOffset)
            return;
        try
        {
            if(camera != (nint)Active())
                return;
            var scene = &((FFXIVClientStructs.FFXIV.Client.Game.CameraBase*)camera)->SceneCamera;
            var obj = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object*)scene;
            // (the game's own vector type: taken as System.Numerics vectors to work with)
            Vector3 position = *(Vector3*)&obj->Position;
            Vector3 look = *(Vector3*)&scene->LookAtVector;
            Vector3 forward = look - position;
            if(forward.LengthSquared() < 1e-8f)
                return;
            forward = Vector3.Normalize(forward);
            var right = Vector3.Cross(forward, Vector3.UnitY);
            right = right.LengthSquared() < 1e-8f ? Vector3.UnitX : Vector3.Normalize(right);
            var up = Vector3.Cross(right, forward);
            Vector3 move = right * _offset.X + up * _offset.Y + forward * _offset.Z;
            Vector3 movedPosition = position + move, movedLook = look + move;
            *(Vector3*)&obj->Position = movedPosition;
            *(Vector3*)&scene->LookAtVector = movedLook;
        }
        catch
        {
        }
    }

    // ---- No collision: the game's "does the camera hit something" check answered "no" (patched in place, put back off)

    [DllImport("kernel32.dll")]
    private static extern bool VirtualProtect(nint address, nuint size, uint protect, out uint old);

    private static nint _collisionAt;
    private static byte[]? _collisionOriginal;
    private static bool _collisionTried;
    public static bool NoCollisionAvailable => _collisionAt != 0;

    /// <summary>Turns the camera's floor / wall collision off or back on. Returns false if it couldn't be found.</summary>
    public static bool SetNoCollision(bool on)
    {
        if(!_collisionTried)
        {
            _collisionTried = true;
            try
            {
                // (the call to the collision test, followed by "if it hit, ...")
                if(Plugin.SigScanner.TryScanText("E8 ?? ?? ?? ?? 84 C0 0F 84 ?? ?? ?? ?? F3 0F 10 44 24 ?? 41 B7 01", out var at))
                {
                    _collisionAt = at;
                    _collisionOriginal = new byte[5];
                    Marshal.Copy(at, _collisionOriginal, 0, 5);
                }
            }
            catch(Exception e)
            {
                Plugin.Log.Warning(e, "Limber: couldn't find the camera collision");
            }
        }
        if(_collisionAt == 0 || _collisionOriginal == null)
            return false;
        // xor al, al (and nops): "it didn't hit anything"
        byte[] bytes = on ? [0x30, 0xC0, 0x90, 0x90, 0x90] : _collisionOriginal;
        if(!VirtualProtect(_collisionAt, 5, 0x40, out var old))
            return false;
        Marshal.Copy(bytes, 0, _collisionAt, 5);
        VirtualProtect(_collisionAt, 5, old, out _);
        return true;
    }

    /// <summary>Everything put back (plugin unloading).</summary>
    public static void Dispose()
    {
        try
        {
            SetNoCollision(false);
        }
        catch
        {
        }
        HasOffset = false;
        _update?.Dispose();
        _update = null;
    }

    /// <summary>The limits as they are now (for the sliders).</summary>
    public static (float MinD, float MaxD, float MinF, float MaxF, float MinV, float MaxV)? Limits()
    {
        var c = Active();
        if(c == null)
            return null;
        return (c->MinDistance, c->MaxDistance, c->MinFoV, c->MaxFoV, c->DirVMin, c->DirVMax);
    }
}
