using System;
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
/// The game camera (in GPose or out), the way Brio's camera tab works: its distance (zoom), field of view, the
/// angle round and up / down, and its tilt, read and set directly; and its limits widened (closer / further, steeper
/// angles, a wider field of view) while "Unlock" is on. Framework thread. Only on your screen.
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

    /// <summary>The limits as they are now (for the sliders).</summary>
    public static (float MinD, float MaxD, float MinF, float MaxF, float MinV, float MaxV)? Limits()
    {
        var c = Active();
        if(c == null)
            return null;
        return (c->MinDistance, c->MaxDistance, c->MinFoV, c->MaxFoV, c->DirVMin, c->DirVMax);
    }
}
