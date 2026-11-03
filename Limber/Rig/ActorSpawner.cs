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
    private readonly Dictionary<ushort, ushort> _parent = new();      // object table slot -> the character it was copied from
    private readonly Dictionary<ushort, int> _number = new();         // object table slot -> its number ("Limber 1", ...)
    private readonly HashSet<ushort> _parentSet = new();              // Penumbra told whose copy it is
    private bool _wasGPosing;

    /// <summary>The one the plugin made (for the character list's order).</summary>
    public static ActorSpawner? Current { get; private set; }

    public ActorSpawner()
    {
        Current = this;
    }

    /// <summary>The object table slots of the actors Limber added (in the order they were added).</summary>
    public IReadOnlyList<ushort> Made => _made;

    public string? LastError { get; private set; }

    /// <summary>After an actor is added (the plugin turns Oral off then).</summary>
    public Action? Added;

    /// <summary>
    /// A new GPose actor that looks like <paramref name="source"/> (or wears <paramref name="look"/>: an NPC's, a saved
    /// or a pasted one), standing where <paramref name="source"/> does (or in front of it). GPose only. Returns its
    /// object table slot, or -1.
    /// </summary>
    public int Spawn(ICharacter source, bool samePlace, AppearanceData? look = null)
    {
        if(look != null && (look.Customize.Length < 26 || look.Customize[2] == 4))
        {
            LastError = "That look can't be used (a child body isn't allowed).";
            return -1;
        }
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
        _parent[index] = (ushort)source.ObjectIndex;
        _parentSet.Remove(index);

        var target = (NativeCharacter*)obj;
        var from = (NativeCharacter*)source.Address;
        var events = EventFramework.Instance();
        if(events != null)
            events->EventSceneModule.EventGPoseController.AddCharacterToGPose(target);
        // Copied from the source, then from itself (so Penumbra / Glamourer see it as its own character).
        target->CharacterSetup.CopyFromCharacter(from, CopyFlags.WeaponHiding);
        target->CharacterSetup.CopyFromCharacter(target, CopyFlags.None);
        // A look of its own: written in before it's first drawn, so it appears already wearing it.
        if(look != null)
            Appearance.WriteUndrawn(target, look);
        // Its own name ("Limber 1", "Limber 2", ...), so it's told apart in lists.
        int number = 1;
        while(_number.ContainsValue(number))
            number++;
        _number[index] = number;
        SetName(obj, $"Limber {number}");
        // Penumbra told it's a copy of the source (as Brio does): it then knows the actor, draws it with the source's
        // collection, and the rig / soft body work on it (without this Penumbra didn't recognise it).
        TellPenumbra(index);

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
        _parent.Remove(index);
        _number.Remove(index);
        _parentSet.Remove(index);
        if(_clientSlot.Remove(index, out var clientSlot))
        {
            var com = ClientObjectManager.Instance();
            if(com != null && Still(com, index, clientSlot))
                com->DeleteObjectByIndex(clientSlot, 0);
        }
        return true;
    }

    /// <summary>The number in an added actor's name (0 if Limber didn't add it).</summary>
    public int NumberOf(ushort index) => _number.TryGetValue(index, out var n) ? n : 0;

    private static void SetName(NativeObject* obj, string name)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(name);
        int n = Math.Min(bytes.Length, 63);
        for(int i = 0; i < 64; i++)
            obj->Name[i] = i < n ? bytes[i] : (byte)0;
    }

    private void TellPenumbra(ushort index)
    {
        if(_parentSet.Contains(index) || !_parent.TryGetValue(index, out var parent))
            return;
        try
        {
            int ec = Plugin.PluginInterface.GetIpcSubscriber<int, int, int>("Penumbra.SetCutsceneParentIndex.V5").InvokeFunc(index, parent);
            if(ec == 0)
                _parentSet.Add(index);
        }
        catch
        {
            _parentSet.Add(index); // (no Penumbra)
        }
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
            _parent.Clear();
            _number.Clear();
            _parentSet.Clear();
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
            TellPenumbra(index);
            if(obj->IsReadyToDraw() && (_parentSet.Contains(index) || _waitingToDraw[index] < 240))
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
                    _parent.Remove(_made[k]);
                    _number.Remove(_made[k]);
                    _parentSet.Remove(_made[k]);
                    _made.RemoveAt(k);
                }
    }

    /// <summary>A new zone: the game has cleared every GPose actor already.</summary>
    public void Forget()
    {
        _made.Clear();
        _clientSlot.Clear();
        _waitingToDraw.Clear();
        _parent.Clear();
        _number.Clear();
        _parentSet.Clear();
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
