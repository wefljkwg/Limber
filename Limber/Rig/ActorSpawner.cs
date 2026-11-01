using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Objects.Types;
using ClientObjectManager = FFXIVClientStructs.FFXIV.Client.Game.Object.ClientObjectManager;
using CopyFlags = FFXIVClientStructs.FFXIV.Client.Game.Character.CharacterSetupContainer.CopyFlags;
using EventFramework = FFXIVClientStructs.FFXIV.Client.Game.Event.EventFramework;
using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;
using NativeObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace Limber.Rig;

/// <summary>
/// The Actors page: extra GPose actors, each a copy of a character (appearance and gear), added and removed from a
/// list. Only ones Limber made are removed by it, and they all go when you leave GPose (the game's own rule for
/// GPose actors). Like any GPose actor, they're only on your screen.
/// </summary>
public sealed unsafe class ActorSpawner : IDisposable
{
    private readonly List<ushort> _made = new();                      // object table slots
    private readonly Dictionary<ushort, ushort> _clientSlot = new();  // object table slot -> the client object list's slot
    private readonly Dictionary<ushort, int> _waitingToDraw = new();  // object table slot -> frames left
    private bool _wasGPosing;

    /// <summary>The object table slots of the actors Limber added (in the order they were added).</summary>
    public IReadOnlyList<ushort> Made => _made;

    public string? LastError { get; private set; }

    /// <summary>After an actor is added (the plugin turns Oral off then).</summary>
    public Action? Added;

    /// <summary>
    /// A new GPose actor that looks like <paramref name="source"/>, standing where it does (or in front of it).
    /// GPose only. Returns its object table slot, or -1.
    /// </summary>
    public int Spawn(ICharacter source, bool samePlace)
    {
        LastError = null;
        if(!Plugin.ClientState.IsGPosing)
        {
            LastError = "Actors can only be added in GPose.";
            return -1;
        }
        var com = ClientObjectManager.Instance();
        if(com == null)
        {
            LastError = "The game's object list isn't available.";
            return -1;
        }
        uint id = com->CreateBattleCharacter();
        if(id == 0xFFFFFFFF)
        {
            LastError = "The game wouldn't add another actor (too many?).";
            return -1;
        }
        // (the client object list has its own numbering: the actor's place in the object table is its ObjectIndex)
        ushort clientSlot = (ushort)id;
        var obj = (NativeObject*)com->GetObjectByIndex(clientSlot);
        if(obj == null)
        {
            LastError = "The new actor didn't appear.";
            return -1;
        }
        ushort index = obj->ObjectIndex;
        _made.Add(index);
        _clientSlot[index] = clientSlot;

        var target = (NativeCharacter*)obj;
        var from = (NativeCharacter*)source.Address;
        var events = EventFramework.Instance();
        if(events != null)
            events->EventSceneModule.EventGPoseController.AddCharacterToGPose(target);
        // Copied from the source, then from itself (so Penumbra / Glamourer see it as its own character).
        target->CharacterSetup.CopyFromCharacter(from, CopyFlags.WeaponHiding);
        target->CharacterSetup.CopyFromCharacter(target, CopyFlags.None);

        System.Numerics.Vector3 position = from->GameObject.Position;
        if(from->GameObject.DrawObject != null)
            position = from->GameObject.DrawObject->Object.Position;
        float rotation = from->GameObject.Rotation;
        if(!samePlace)
            position += new System.Numerics.Vector3(MathF.Sin(rotation), 0f, MathF.Cos(rotation)) * 1.2f; // a step in front
        target->GameObject.DefaultPosition = position;
        target->GameObject.Position = position;
        target->GameObject.Rotation = rotation;
        target->GameObject.DefaultRotation = rotation;

        _waitingToDraw[index] = 300;
        Added?.Invoke();
        return index;
    }

    /// <summary>Removes an actor Limber added.</summary>
    public bool Despawn(ushort index)
    {
        if(!_made.Remove(index))
            return false;
        _waitingToDraw.Remove(index);
        if(_clientSlot.Remove(index, out var clientSlot))
        {
            var com = ClientObjectManager.Instance();
            if(com != null && Still(com, index, clientSlot))
                com->DeleteObjectByIndex(clientSlot, 0);
        }
        return true;
    }

    // The client object at that slot is still the actor Limber added there.
    private static bool Still(ClientObjectManager* com, ushort index, ushort clientSlot)
    {
        var obj = (NativeObject*)com->GetObjectByIndex(clientSlot);
        return obj != null && obj->ObjectIndex == index;
    }

    public void DespawnAll()
    {
        foreach(var index in _made.ToArray())
            Despawn(index);
    }

    /// <summary>Framework update: start drawing new actors once they're ready; tidy up when GPose ends.</summary>
    public void Update()
    {
        bool gposing = Plugin.ClientState.IsGPosing;
        if(_wasGPosing && !gposing)
        {
            DespawnAll();
            _made.Clear();
            _clientSlot.Clear();
            _waitingToDraw.Clear();
        }
        _wasGPosing = gposing;
        if(_waitingToDraw.Count == 0)
            return;
        var com = ClientObjectManager.Instance();
        foreach(var index in new List<ushort>(_waitingToDraw.Keys))
        {
            var obj = com == null || !_clientSlot.TryGetValue(index, out var slot) || !Still(com, index, slot)
                ? null : (NativeObject*)com->GetObjectByIndex(slot);
            if(obj == null || --_waitingToDraw[index] <= 0)
            {
                _waitingToDraw.Remove(index);
                continue;
            }
            if(obj->IsReadyToDraw())
            {
                obj->EnableDraw();
                _waitingToDraw.Remove(index);
            }
        }
        // (an actor the game removed itself)
        if(com != null)
            for(int k = _made.Count - 1; k >= 0; k--)
                if(!_clientSlot.TryGetValue(_made[k], out var slot) || !Still(com, _made[k], slot))
                {
                    _clientSlot.Remove(_made[k]);
                    _made.RemoveAt(k);
                }
    }

    /// <summary>A new zone: the game has cleared every GPose actor already.</summary>
    public void Forget()
    {
        _made.Clear();
        _clientSlot.Clear();
        _waitingToDraw.Clear();
    }

    public void Dispose()
    {
        try
        {
            DespawnAll();
        }
        catch
        {
            // (the game may be shutting down)
        }
    }
}
