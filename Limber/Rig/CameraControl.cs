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
            c->SavedDistance = d;
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
    //
    // The game's own limits are noted for each camera while they're untouched (every frame: the game changes them,
    // entering GPose for one), so turning Unlock off puts back exactly what that camera had. A camera only ever seen
    // unlocked (unlocked before entering GPose, say) gets the game's usual limits back when it's next active.

    public readonly record struct CameraLimits(float MinDistance, float MaxDistance, float MinFoV, float MaxFoV, float MinPitch, float MaxPitch);

    /// <summary>The game's usual limits (the third-person camera's).</summary>
    public static readonly CameraLimits GameDefaults = new(1.5f, 20f, 0.69f, 0.78f, -1.48353f, 0.785398f);

    private static readonly System.Collections.Generic.Dictionary<nint, CameraLimits> Stock = new();
    private static readonly System.Collections.Generic.HashSet<nint> Touched = new();

    private static CameraLimits Of(GameCamera* c) => new(c->MinDistance, c->MaxDistance, c->MinFoV, c->MaxFoV, c->DirVMin, c->DirVMax);

    private static void Put(GameCamera* c, CameraLimits l)
    {
        c->MinDistance = l.MinDistance;
        c->MaxDistance = l.MaxDistance;
        c->MinFoV = l.MinFoV;
        c->MaxFoV = l.MaxFoV;
        c->DirVMin = l.MinPitch;
        c->DirVMax = l.MaxPitch;
    }

    /// <summary>The distance kept inside the limits: what it's at, what it's heading to and what it goes back to.</summary>
    private static void Settle(GameCamera* c)
    {
        c->Distance = Math.Clamp(c->Distance, c->MinDistance, c->MaxDistance);
        c->InterpDistance = Math.Clamp(c->InterpDistance, c->MinDistance, c->MaxDistance);
        c->SavedDistance = Math.Clamp(c->SavedDistance, c->MinDistance, c->MaxDistance);
        c->FoV = Math.Clamp(c->FoV, c->MinFoV, c->MaxFoV);
        c->DirV = Math.Clamp(c->DirV, c->DirVMin, c->DirVMax);
    }

    /// <summary>This camera's own limits (as the game had them), for resetting and the sliders.</summary>
    public static CameraLimits Own()
    {
        var c = Active();
        return c != null && Stock.TryGetValue((nint)c, out var l) ? l : GameDefaults;
    }

    /// <summary>Framework thread, once a frame. <paramref name="limits"/>: the unlocked limits (when unlocking).</summary>
    public static void Update(bool unlock, CameraLimits limits)
    {
        var c = Active();
        if(c == null)
            return;
        nint key = (nint)c;
        if(unlock)
        {
            if(!Touched.Contains(key))
            {
                if(!Stock.ContainsKey(key))
                    Stock[key] = Sane(Of(c)) ? Of(c) : GameDefaults;
                Touched.Add(key);
            }
            Put(c, limits);
            return;
        }
        if(Touched.Remove(key))
        {
            Put(c, Stock.TryGetValue(key, out var own) ? own : GameDefaults);
            Settle(c);
        }
        else if(Sane(Of(c)))
            Stock[key] = Of(c);
    }

    private static bool Sane(CameraLimits l)
        => float.IsFinite(l.MinDistance) && float.IsFinite(l.MaxDistance) && l.MinDistance > 0f && l.MaxDistance > l.MinDistance && l.MaxDistance <= 1000f
           && float.IsFinite(l.MinFoV) && float.IsFinite(l.MaxFoV) && l.MaxFoV >= l.MinFoV;

    /// <summary>
    /// Back to how the game has it: its usual distance (its own setting for it), field of view, height and angle
    /// straight on; and Limber's move off its place undone by the caller.
    /// </summary>
    public static void Reset(bool distance = true, bool fov = true, bool tilt = true, bool pitch = true)
    {
        var c = Active();
        if(c == null)
            return;
        var own = Own();
        if(distance)
        {
            float d = c->ConfigOption_ThirdPersonDefaultDistance;
            if(!float.IsFinite(d) || d < own.MinDistance || d > own.MaxDistance)
                d = Math.Clamp(6f, own.MinDistance, own.MaxDistance);
            c->Distance = d;
            c->InterpDistance = d;
            c->SavedDistance = d;
        }
        if(fov)
            c->FoV = Math.Clamp(own.MaxFoV, c->MinFoV, c->MaxFoV);
        if(tilt)
            c->TiltOffset = 0f;
        if(pitch)
            c->DirV = Math.Clamp(-0.2f, c->DirVMin, c->DirVMax);
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
            RestoreLimits();
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

    /// <summary>Unloading: every camera Limber unlocked gets its own limits back (the active one now).</summary>
    public static void RestoreLimits()
    {
        var c = Active();
        if(c != null && Touched.Remove((nint)c))
        {
            Put(c, Stock.TryGetValue((nint)c, out var own) ? own : GameDefaults);
            Settle(c);
        }
        Touched.Clear();
    }
}
