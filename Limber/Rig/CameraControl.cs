using System;
using System.Collections.Generic;
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
        if(manager == null)
            return null;
        var c = manager->GetActiveCamera();
        // (the character select screen: its own camera)
        if(c == null && !Plugin.ClientState.IsLoggedIn && manager->LobbyCamera != null)
            c = (GameCamera*)manager->LobbyCamera;
        return c;
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
    private static Vector3 _world; // straight up / along the world's axes (the backdrop's lift into the sky)
    private static Vector3 _lobby; // following the character walking round the character select screen

    /// <summary>The camera moved along with the character walking on the character select screen (world metres).</summary>
    public static void SetLobbyOffset(Vector3 offset)
    {
        if(offset == _lobby)
            return;
        _lobby = offset;
        Refresh();
    }

    public static void SetOffset(Vector3 offset)
    {
        _offset = offset;
        Refresh();
    }

    /// <summary>The camera moved along the world's axes (metres), on top of the offset above (Extras: Backdrop's lift).</summary>
    public static void SetWorldOffset(Vector3 offset)
    {
        if(offset == _world && offset == Vector3.Zero)
            return;
        _world = offset;
        Refresh();
    }

    // ---- Aimed at a point (Extras: Dataset capture): the camera keeps its own angle and distance but looks at this point
    // (world), as if it went round it. And where the game itself put the camera each frame, for working out its angles.

    private static Vector3? _aim;
    /// <summary>The camera's place and what it looked at as the game made them (before Limber moved them), last frame.</summary>
    public static Vector3 RawPosition, RawLook;
    public static volatile bool RawValid;

    public static void SetAim(Vector3? at)
    {
        _aim = at;
        if(at == null)
            RawValid = false;
        Refresh();
    }

    // ---- Free camera (Camera tab): the camera flies on its own (WASD, E / Q up and down), still turned with the mouse
    // as the game turns it; what it looks at goes along in front of it.

    private static bool _free;
    private static Vector3 _freeAt;
    public static bool Free => _free;
    /// <summary>Metres a second (Shift: three times as fast, Ctrl: a third).</summary>
    public static float FreeSpeed = 3f;

    // While it's on, the game is told the movement keys aren't pressed (its key bindings' "held" / "pressed" checks for
    // move forward / back / left / right, strafe, jump, ascend, descend, and the controller's move stick answer "no",
    // as Cammy's free camera does), so the character stays put; Limber reads the keys itself.
    private delegate byte InputIdDelegate(nint inputData, uint id);
    // The player's walking / flying input as the game sums it up each frame (what vnavmesh overrides to steer):
    // zeroed while the free camera is on, so neither the keys nor the stick move the character.
    private delegate void WalkInputDelegate(nint self, float* left, float* forward, float* turnLeft, byte* backwardOrStrafe, byte* a6, byte additive);
    private delegate void FlyInputDelegate(nint self, float* result);
    private static Hook<WalkInputDelegate>? _walk;
    private static Hook<FlyInputDelegate>? _fly;
    private delegate int AxisDelegate(nint inputData, uint id);
    private static Hook<InputIdDelegate>? _held, _pressed;
    private static Hook<AxisDelegate>? _axis;
    private static bool _inputTried, _holdingMovement;
    private static readonly HashSet<uint> MoveInputs = [321, 322, 323, 324, 325, 326, 348, 448, 449];

    private static void HoldMovement(bool hold)
    {
        if(!_inputTried && hold)
        {
            _inputTried = true;
            try
            {
                _held = Plugin.GameInterop.HookFromAddress<InputIdDelegate>(Plugin.SigScanner.ScanText("E9 ?? ?? ?? ?? B9 4F 01 00 00"),
                    (d, id) => _holdingMovement && MoveInputs.Contains(id) ? (byte)0 : _held!.Original(d, id));
                _pressed = Plugin.GameInterop.HookFromAddress<InputIdDelegate>(Plugin.SigScanner.ScanText("E9 ?? ?? ?? ?? 83 7F 44 02"),
                    (d, id) => _holdingMovement && MoveInputs.Contains(id) ? (byte)0 : _pressed!.Original(d, id));
                _axis = Plugin.GameInterop.HookFromAddress<AxisDelegate>(Plugin.SigScanner.ScanText("E8 ?? ?? ?? ?? 66 44 0F 6E C3"),
                    (d, id) => _holdingMovement && id is 3 or 4 ? 0 : _axis!.Original(d, id));
                _walk = Plugin.GameInterop.HookFromAddress<WalkInputDelegate>(Plugin.SigScanner.ScanText("E8 ?? ?? ?? ?? 80 7B 3E 00 48 8D 3D"),
                    (self, left, forward, turn, back, a6, additive) =>
                    {
                        _walk!.Original(self, left, forward, turn, back, a6, additive);
                        if(_holdingMovement)
                        {
                            *left = 0f;
                            *forward = 0f;
                            *turn = 0f;
                        }
                    });
                _fly = Plugin.GameInterop.HookFromAddress<FlyInputDelegate>(Plugin.SigScanner.ScanText("E8 ?? ?? ?? ?? 0F B6 0D ?? ?? ?? ?? B8"),
                    (self, result) =>
                    {
                        _fly!.Original(self, result);
                        if(_holdingMovement)
                            for(int i = 0; i < 4; i++)
                                result[i] = 0f; // forward, left, up, turn
                    });
                _walk.Enable();
                _fly.Enable();
                _held.Enable();
                _pressed.Enable();
                _axis.Enable();
            }
            catch(Exception e)
            {
                Plugin.Log.Warning(e, "Limber: couldn't find the game's movement keys (free camera: the keys move the character too)");
            }
        }
        _holdingMovement = hold;
    }

    public static void SetFree(bool on)
    {
        if(on == _free)
            return;
        HoldMovement(on);
        if(on)
        {
            var c = Active();
            if(c == null)
                return;
            var scene = &((FFXIVClientStructs.FFXIV.Client.Game.CameraBase*)c)->SceneCamera;
            var obj = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object*)scene;
            _freeAt = *(Vector3*)&obj->Position;
        }
        _free = on;
        Refresh();
    }

    /// <summary>Framework update: flies the free camera with the keys (taken from the game while it's on).</summary>
    private static long _flyTicks;

    public static void FlyFree(Dalamud.Plugin.Services.IKeyState keys, bool typing)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        float dt = _flyTicks == 0 ? 0f : (float)((now - _flyTicks) / (double)System.Diagnostics.Stopwatch.Frequency);
        _flyTicks = now;
        if(!_free || typing || !RawValid)
            return;
        var forward = RawLook - RawPosition;
        if(forward.LengthSquared() < 1e-8f)
            return;
        forward = Vector3.Normalize(forward);
        var right = Vector3.Cross(forward, Vector3.UnitY);
        right = right.LengthSquared() < 1e-8f ? Vector3.UnitX : Vector3.Normalize(right);
        var move = Vector3.Zero;
        bool Held(Dalamud.Game.ClientState.Keys.VirtualKey key)
        {
            // (read only, never cleared: a cleared key reads "up" until Windows repeats it, so the camera barely moved;
            // the character is kept still by the game's movement switch instead)
            return keys.IsVirtualKeyValid(key) && keys[key];
        }
        if(Held(Dalamud.Game.ClientState.Keys.VirtualKey.W)) move += forward;
        if(Held(Dalamud.Game.ClientState.Keys.VirtualKey.S)) move -= forward;
        if(Held(Dalamud.Game.ClientState.Keys.VirtualKey.D)) move += right;
        if(Held(Dalamud.Game.ClientState.Keys.VirtualKey.A)) move -= right;
        if(Held(Dalamud.Game.ClientState.Keys.VirtualKey.E) || Held(Dalamud.Game.ClientState.Keys.VirtualKey.SPACE)) move += Vector3.UnitY;
        if(Held(Dalamud.Game.ClientState.Keys.VirtualKey.Q)) move -= Vector3.UnitY;
        if(move == Vector3.Zero)
            return;
        float speed = Math.Clamp(FreeSpeed, 0.1f, 50f);
        if(keys[Dalamud.Game.ClientState.Keys.VirtualKey.SHIFT])
            speed *= 3f;
        if(keys[Dalamud.Game.ClientState.Keys.VirtualKey.CONTROL])
            speed /= 3f;
        _freeAt += Vector3.Normalize(move) * speed * Math.Clamp(dt, 0f, 0.1f);
    }

    private static void Refresh()
    {
        HasOffset = _offset != Vector3.Zero || _world != Vector3.Zero || _lobby != Vector3.Zero || _aim != null || _free;
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
            RawPosition = position;
            RawLook = look;
            RawValid = true;
            Vector3 forward = look - position;
            if(forward.LengthSquared() < 1e-8f)
                return;
            forward = Vector3.Normalize(forward);
            var right = Vector3.Cross(forward, Vector3.UnitY);
            right = right.LengthSquared() < 1e-8f ? Vector3.UnitX : Vector3.Normalize(right);
            var up = Vector3.Cross(right, forward);
            Vector3 move = right * _offset.X + up * _offset.Y + forward * _offset.Z + _world + _lobby;
            if(_aim is { } aim)
                move = aim - look;
            if(_free)
                move = _freeAt - position;
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

    // The call to the camera's collision test, followed by "if it hit, ..." (also listed in Core/Signatures).
    internal const string NoCollisionSig = "E8 ?? ?? ?? ?? 84 C0 0F 84 ?? ?? ?? ?? F3 0F 10 44 24 ?? 41 B7 01";
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
                if(Plugin.SigScanner.TryScanText(NoCollisionSig, out var at))
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
        _holdingMovement = false;
        _held?.Dispose();
        _pressed?.Dispose();
        _axis?.Dispose();
        _walk?.Dispose();
        _fly?.Dispose();
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
