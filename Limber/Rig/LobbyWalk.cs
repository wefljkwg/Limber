using System;
using System.Numerics;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;
using NativeObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using SceneCameraManager = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CameraManager;

namespace Limber.Rig;

/// <summary>
/// Walking round the character select screen: the character shown there moves with W / A / S / D (from where the
/// camera looks), runs (Shift: walks), turns to face the way it goes and plays the run / walk / idle animations.
/// The lobby has no world to walk in (no ground, walls or collision): the character slides over the flat floor it
/// stands on. Only on your screen; logging in puts everything back as the game has it. Framework thread.
/// </summary>
public static unsafe class LobbyWalk
{
    // ActionTimeline rows: normal/idle, normal/walk, normal/run.
    private const ushort Idle = 3, Walk = 13, Run = 22;

    private static nint _who;
    private static ushort _playing;
    private static Vector3 _home;
    private static float _homeYaw, _yaw;
    private static bool _haveHome;

    public static string Status { get; private set; } = "";

    /// <summary>Whether a character select character is there to walk (the game's lobby is open).</summary>
    public static bool InLobby => CharaSelectCharacterList.GetCurrentCharacter() != null;

    public static void Update(bool on, bool loggedIn, IKeyState keys, bool typing, float dt, float walkSpeed, float runSpeed)
    {
        if(loggedIn)
        {
            _who = 0;
            _playing = 0;
            _haveHome = false;
            Status = "";
            return;
        }
        var c = CharaSelectCharacterList.GetCurrentCharacter();
        if(c == null)
        {
            _who = 0;
            Status = on ? "Waiting for the character select screen." : "";
            return;
        }
        if((nint)c != _who)
        {
            // (another character chosen in the list: start from where the game put that one)
            _who = (nint)c;
            _playing = 0;
            _haveHome = false;
        }
        var go = (NativeObject*)c;
        if(!_haveHome)
        {
            _home = (Vector3)go->Position;
            _homeYaw = go->Rotation;
            _yaw = _homeYaw;
            _haveHome = true;
        }
        if(!on)
        {
            Stop(c);
            Status = "";
            return;
        }

        // Which way: W / S forward and back, A / D left and right (from the camera's view).
        var input = Vector2.Zero;
        if(!typing)
        {
            if(keys[VirtualKey.W]) input.Y += 1f;
            if(keys[VirtualKey.S]) input.Y -= 1f;
            if(keys[VirtualKey.D]) input.X += 1f;
            if(keys[VirtualKey.A]) input.X -= 1f;
        }
        dt = Math.Clamp(dt, 0f, 0.1f);
        if(input == Vector2.Zero || dt <= 0f)
        {
            Play(c, Idle);
            Status = "Walk with W / A / S / D (hold Shift to walk instead of running).";
            return;
        }

        // The camera's forward, flat on the floor.
        var forward = new Vector3(0f, 0f, 1f);
        var manager = SceneCameraManager.Instance();
        var camera = manager != null ? manager->CurrentCamera : null;
        if(camera != null)
        {
            Vector3 lookAt = camera->LookAtVector, from = camera->Position;
            var look = lookAt - from;
            look.Y = 0f;
            if(look.LengthSquared() > 1e-6f)
                forward = Vector3.Normalize(look);
        }
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        var dir = forward * input.Y + right * input.X;
        if(dir.LengthSquared() < 1e-6f)
            return;
        dir = Vector3.Normalize(dir);

        bool walking = keys[VirtualKey.SHIFT];
        float speed = walking ? Math.Clamp(walkSpeed, 0.5f, 10f) : Math.Clamp(runSpeed, 1f, 20f);
        Vector3 now = go->Position;
        var at = now + dir * speed * dt;
        // Turning to face the way it goes (quickly, not snapped).
        float want = MathF.Atan2(dir.X, dir.Z);
        float diff = MathF.IEEERemainder(want - _yaw, MathF.Tau);
        _yaw += diff * Math.Clamp(dt * 12f, 0f, 1f);
        _yaw = MathF.IEEERemainder(_yaw, MathF.Tau);
        if(!float.IsFinite(at.X) || !float.IsFinite(at.Z) || !float.IsFinite(_yaw))
            return;
        // (kept near where the game put it: the lobby's floor and lights are only round there)
        var away = at - _home;
        away.Y = 0f;
        if(away.Length() > 30f)
            at = _home + Vector3.Normalize(away) * 30f + new Vector3(0f, at.Y - _home.Y, 0f);
        go->Position = at;
        go->Rotation = _yaw;
        if(go->DrawObject != null)
        {
            go->DrawObject->Object.Position = at;
            go->DrawObject->Object.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, _yaw);
        }
        Play(c, walking ? Walk : Run);
        Status = walking ? "Walking." : "Running.";
    }

    private static void Play(NativeCharacter* c, ushort id)
    {
        if(_playing == id)
            return;
        _playing = id;
        if(id == Idle)
        {
            // (back to whatever the game had it doing)
            c->Timeline.BaseOverride = 0;
            c->Timeline.TimelineSequencer.PlayTimeline(Idle);
            return;
        }
        c->Timeline.BaseOverride = id;
        c->Timeline.TimelineSequencer.PlayTimeline(id);
    }

    private static void Stop(NativeCharacter* c)
    {
        if(_playing != 0 && _playing != Idle)
            Play(c, Idle);
    }

    /// <summary>The character back where the game put it, facing the way it did.</summary>
    public static void PutBack()
    {
        var c = CharaSelectCharacterList.GetCurrentCharacter();
        if(c == null || (nint)c != _who || !_haveHome)
            return;
        var go = (NativeObject*)c;
        go->Position = _home;
        go->Rotation = _homeYaw;
        _yaw = _homeYaw;
        if(go->DrawObject != null)
        {
            go->DrawObject->Object.Position = _home;
            go->DrawObject->Object.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, _homeYaw);
        }
        Play(c, Idle);
    }
}
