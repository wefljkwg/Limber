using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using ClientObjectManager = FFXIVClientStructs.FFXIV.Client.Game.Object.ClientObjectManager;
using CopyFlags = FFXIVClientStructs.FFXIV.Client.Game.Character.CharacterSetupContainer.CopyFlags;
using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;
using NativeObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using SceneCameraManager = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CameraManager;

namespace Limber.Rig;

/// <summary>
/// The character select screen: the character shown there (or an actor added there) walks with W / A / S / D (from
/// where the camera looks), runs (Shift: walks), turns the way it goes and plays the run / walk / idle animations; the
/// camera can follow it. Actors can be added there too: copies of the character shown, or wearing a saved look.
/// The lobby has no world to walk in (no ground, walls or collision): characters slide over the flat floor. Only on
/// your screen; the actors are removed when you log in or leave the screen. Framework thread.
/// </summary>
public static unsafe class LobbyWalk
{
    // ActionTimeline rows: normal/idle, normal/walk, normal/run.
    private const ushort Idle = 3, Walk = 13, Run = 22;
    private const string ActorPrefix = "Lobby Actor ";

    private sealed class Walker
    {
        public nint Address;
        public ushort Playing;
        public float Yaw;
    }

    private sealed class LobbyActor
    {
        public ushort Slot;   // the client object list's slot
        public nint Address;
        public string Name = "";
        public int Number;
        public int WaitToDraw = 300;
    }

    private static nint _lobbyChar;
    private static Vector3 _home;
    private static float _homeYaw;
    private static bool _haveHome;
    private static readonly Dictionary<nint, Walker> _walkers = new();
    private static readonly List<LobbyActor> _actors = new();

    /// <summary>Who walks: -1 the character shown, else the number of an added actor.</summary>
    public static int WalkerNumber = -1;

    public static string Status { get; private set; } = "";

    /// <summary>Whether a character select character is there (the game's lobby is open).</summary>
    public static bool InLobby => !Plugin.ClientState.IsLoggedIn && CharaSelectCharacterList.GetCurrentCharacter() != null;

    /// <summary>The actors added on the character select screen: their numbers and names.</summary>
    public static IEnumerable<(int Number, string Name)> Actors
    {
        get
        {
            foreach(var a in _actors)
                yield return (a.Number, a.Name);
        }
    }

    public static void Update(bool on, bool follow, bool loggedIn, IKeyState keys, bool typing, float dt, float walkSpeed, float runSpeed)
    {
        var c = loggedIn ? null : CharaSelectCharacterList.GetCurrentCharacter();
        if(c == null)
        {
            // (left the screen, or logged in: the actors go, the camera back to the game's)
            RemoveAll();
            _lobbyChar = 0;
            _haveHome = false;
            _walkers.Clear();
            CameraControl.SetLobbyFollow(null);
            Status = on && !loggedIn ? "Waiting for the character select screen." : "";
            return;
        }
        if((nint)c != _lobbyChar)
        {
            // (another character chosen in the list: start from where the game put that one)
            _lobbyChar = (nint)c;
            _haveHome = false;
            _walkers.Remove((nint)c);
        }
        var lobbyObj = (NativeObject*)c;
        if(!_haveHome)
        {
            _home = (Vector3)lobbyObj->Position;
            _homeYaw = lobbyObj->Rotation;
            _haveHome = true;
        }
        DrawNewActors();

        var who = Walking(c);
        if(!on || who == null)
        {
            foreach(var (address, w) in _walkers)
                if(StillThere(address))
                    Play((NativeCharacter*)address, w, Idle);
            _walkers.Clear();
            CameraControl.SetLobbyFollow(null);
            Status = "";
            return;
        }
        var go = (NativeObject*)who;
        if(!_walkers.TryGetValue((nint)who, out var walker))
            _walkers[(nint)who] = walker = new Walker { Address = (nint)who, Yaw = go->Rotation };

        // The camera along with them, as it is in the game: it keeps its own turn and zoom round them, centred on
        // where they're drawn this frame.
        _following = (nint)who;
        CameraControl.SetLobbyFollow(follow ? Follow : null);
        Vector3 here = go->Position;

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
            Play(who, walker, Idle);
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
        var at = here + dir * speed * dt;
        // Turning to face the way it goes (quickly, not snapped).
        float want = MathF.Atan2(dir.X, dir.Z);
        float diff = MathF.IEEERemainder(want - walker.Yaw, MathF.Tau);
        walker.Yaw = MathF.IEEERemainder(walker.Yaw + diff * Math.Clamp(dt * 12f, 0f, 1f), MathF.Tau);
        if(!float.IsFinite(at.X) || !float.IsFinite(at.Z) || !float.IsFinite(walker.Yaw))
            return;
        // (kept near where the game put the character: the lobby's floor and lights are only round there)
        var away = at - _home;
        away.Y = 0f;
        if(away.Length() > 30f)
            at = _home + Vector3.Normalize(away) * 30f + new Vector3(0f, at.Y - _home.Y, 0f);
        Place(go, at, walker.Yaw);
        Play(who, walker, walking ? Walk : Run);
        Status = walking ? "Walking." : "Running.";
    }

    private static nint _following;
    private static readonly Func<Vector3?> Follow = FollowOffset;

    // (camera thread of the game's update, same frame: how far the one walking is drawn from the character's place)
    private static Vector3? FollowOffset()
    {
        var go = (NativeObject*)_following;
        if(go == null || !_haveHome || !StillThere(_following))
            return null;
        Vector3 at = go->DrawObject != null ? go->DrawObject->Object.Position : go->Position;
        return at - _home;
    }

    // The one walking: the character shown, or the chosen actor (if it's still there).
    private static NativeCharacter* Walking(NativeCharacter* lobbyChar)
    {
        if(WalkerNumber < 0)
            return lobbyChar;
        foreach(var a in _actors)
            if(a.Number == WalkerNumber && StillOurs(a))
                return (NativeCharacter*)a.Address;
        WalkerNumber = -1;
        return lobbyChar;
    }

    private static void Place(NativeObject* go, Vector3 at, float yaw)
    {
        go->Position = at;
        go->Rotation = yaw;
        if(go->DrawObject != null)
        {
            go->DrawObject->Object.Position = at;
            go->DrawObject->Object.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
        }
    }

    private static void Play(NativeCharacter* c, Walker w, ushort id)
    {
        if(w.Playing == id)
            return;
        bool first = w.Playing == 0;
        w.Playing = id;
        if(id == Idle)
        {
            if(first)
                return; // (standing as the game has it: left alone)
            c->Timeline.BaseOverride = 0;
            c->Timeline.TimelineSequencer.PlayTimeline(Idle);
            return;
        }
        c->Timeline.BaseOverride = id;
        c->Timeline.TimelineSequencer.PlayTimeline(id);
    }

    /// <summary>The one walking back where the game put the character shown, facing the way it did.</summary>
    public static void PutBack()
    {
        var c = CharaSelectCharacterList.GetCurrentCharacter();
        if(c == null || (nint)c != _lobbyChar || !_haveHome)
            return;
        var who = Walking(c);
        var go = (NativeObject*)who;
        var at = _home;
        if(WalkerNumber >= 0)
            at += new Vector3(MathF.Sin(_homeYaw), 0f, MathF.Cos(_homeYaw)) * 1.2f;
        Place(go, at, _homeYaw);
        if(_walkers.TryGetValue((nint)who, out var w))
        {
            w.Yaw = _homeYaw;
            Play(who, w, Idle);
        }
    }

    // ---- Actors on the character select screen

    /// <summary>Adds an actor: a copy of the character shown, or wearing <paramref name="look"/>. Returns a message.</summary>
    public static string AddActor(AppearanceData? look)
    {
        var from = Plugin.ClientState.IsLoggedIn ? null : CharaSelectCharacterList.GetCurrentCharacter();
        if(from == null)
            return "Only on the character select screen.";
        var com = ClientObjectManager.Instance();
        if(com == null)
            return "The game's object list isn't available.";
        uint id = com->CreateBattleCharacter();
        if(id == 0xFFFFFFFF)
            return "The game wouldn't add another actor (too many?).";
        ushort slot = (ushort)id;
        var obj = (NativeObject*)com->GetObjectByIndex(slot);
        if(obj == null)
            return "The new actor didn't appear.";
        var target = (NativeCharacter*)obj;
        target->CharacterSetup.CopyFromCharacter(from, CopyFlags.WeaponHiding);
        target->CharacterSetup.CopyFromCharacter(target, CopyFlags.None);
        bool wearing = look != null && Appearance.WriteUndrawn(target, look);
        int number = 1;
        while(_actors.Exists(a => a.Number == number))
            number++;
        string name = ActorPrefix + number;
        SetName(obj, name);
        // (a step to the side of the character shown, a little further each time, facing the same way)
        var fromObj = (NativeObject*)from;
        Vector3 at = fromObj->Position;
        float yaw = fromObj->Rotation;
        var side = new Vector3(MathF.Cos(yaw), 0f, -MathF.Sin(yaw));
        at += side * (1.1f * ((number + 1) / 2) * (number % 2 == 1 ? 1f : -1f));
        target->GameObject.DefaultPosition = at;
        target->GameObject.Position = at;
        target->GameObject.Rotation = yaw;
        target->GameObject.DefaultRotation = yaw;
        _actors.Add(new LobbyActor { Slot = slot, Address = (nint)obj, Name = name, Number = number });
        return look == null ? $"Added {name} (a copy of the character shown)." :
            wearing ? $"Added {name}, wearing \"{look.Name}\"." : $"Added {name} (the look couldn't be used: a copy instead).";
    }

    public static void RemoveActor(int number)
    {
        var a = _actors.Find(x => x.Number == number);
        if(a == null)
            return;
        Delete(a);
        _actors.Remove(a);
        _walkers.Remove(a.Address);
        if(WalkerNumber == number)
            WalkerNumber = -1;
    }

    public static void RemoveAll()
    {
        foreach(var a in _actors)
            Delete(a);
        _actors.Clear();
        WalkerNumber = -1;
    }

    private static void Delete(LobbyActor a)
    {
        var com = ClientObjectManager.Instance();
        if(com != null && StillOurs(a))
            com->DeleteObjectByIndex(a.Slot, 0);
    }

    // New actors drawn once the game has them ready.
    private static void DrawNewActors()
    {
        for(int i = _actors.Count - 1; i >= 0; i--)
        {
            var a = _actors[i];
            if(!StillOurs(a))
            {
                _actors.RemoveAt(i); // (the game removed it itself)
                continue;
            }
            if(a.WaitToDraw <= 0)
                continue;
            var obj = (NativeObject*)a.Address;
            if(obj->IsReadyToDraw() || --a.WaitToDraw <= 0)
            {
                obj->EnableDraw();
                a.WaitToDraw = 0;
            }
        }
    }

    // The client object at the actor's slot is still the one added (the game reuses slots and their memory).
    private static bool StillOurs(LobbyActor a)
    {
        var com = ClientObjectManager.Instance();
        if(com == null)
            return false;
        var obj = (NativeObject*)com->GetObjectByIndex(a.Slot);
        return obj != null && (nint)obj == a.Address && NameIs(obj, a.Name);
    }

    private static bool StillThere(nint address)
    {
        if(address == _lobbyChar)
            return CharaSelectCharacterList.GetCurrentCharacter() == (NativeCharacter*)address;
        foreach(var a in _actors)
            if(a.Address == address)
                return StillOurs(a);
        return false;
    }

    private static void SetName(NativeObject* obj, string name)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(name);
        int n = Math.Min(bytes.Length, 63);
        for(int i = 0; i < 64; i++)
            obj->Name[i] = i < n ? bytes[i] : (byte)0;
    }

    private static bool NameIs(NativeObject* obj, string name)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(name);
        int n = Math.Min(bytes.Length, 63);
        for(int i = 0; i < n; i++)
            if(obj->Name[i] != bytes[i])
                return false;
        return obj->Name[n] == 0;
    }
}
