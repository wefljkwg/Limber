using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using Limber.Core;
using Limber.Rig;
using System.Collections.Generic;
using System.Linq;

namespace Limber.UI;

public readonly record struct ActorRef(nint Address, ushort ObjectIndex, string Name)
{
    public bool IsSet => Address != 0;

    public string Label => $"{(string.IsNullOrEmpty(Name) ? "(no name)" : Name)}  #{ObjectIndex}";

    public static ActorRef From(IGameObject gameObject)
        => new(gameObject.Address, gameObject.ObjectIndex, gameObject.Name.TextValue);
}

// Which character (and which of its bones) the handles and the window are working on. Main thread only.
public sealed class Selection
{
    private readonly IObjectTable _objects;
    private readonly ITargetManager _targets;
    private readonly IClientState _clientState;
    private readonly RigSettings _config;

    public ActorRef Actor { get; private set; }
    public string? Bone { get; set; }

    // Second character picked in the Attach section.
    public ActorRef AttachTarget { get; set; }

    public Selection(IObjectTable objects, ITargetManager targets, IClientState clientState, RigSettings config)
    {
        _objects = objects;
        _targets = targets;
        _clientState = clientState;
        _config = config;
    }

    public void Select(ActorRef actor)
    {
        if(actor.Address != Actor.Address)
            Bone = null;
        Actor = actor;
    }

    public void Update()
    {
        if(_config.FollowTarget)
        {
            IGameObject? target = _clientState.IsGPosing ? _targets.GPoseTarget : _targets.Target;
            if(target is not ICharacter && !_clientState.IsGPosing)
                target = _objects.LocalPlayer;

            if(target is ICharacter && target.Address != 0)
                Select(ActorRef.From(target));
        }

        if(Actor.IsSet && !StillThere(Actor))
            Select(default);

        if(AttachTarget.IsSet && !StillThere(AttachTarget))
            AttachTarget = default;
    }

    public static unsafe bool StillThere(ActorRef actor)
    {
        var current = ActorSkeleton.GetObject(actor.ObjectIndex);
        return current != null && (nint)current == actor.Address;
    }

    // Characters that can be posed right now, in object-table order.
    public unsafe List<ActorRef> ListCharacters()
    {
        var list = new List<ActorRef>();
        foreach(var gameObject in _objects)
        {
            if(gameObject is not ICharacter || gameObject.Address == 0)
                continue;

            var native = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)gameObject.Address;
            if(native->DrawObject == null)
                continue;

            list.Add(ActorRef.From(gameObject));
        }

        // You first (out of GPose, then in it), then the actors Limber added, other players, NPCs, the rest.
        var kinds = new Dictionary<ushort, Dalamud.Game.ClientState.Objects.Enums.ObjectKind>();
        foreach(var gameObject in _objects)
            if(gameObject.Address != 0)
                kinds[gameObject.ObjectIndex] = gameObject.ObjectKind;
        var made = ActorSpawner.Current?.Made;
        int Rank(ActorRef a)
        {
            if(a.ObjectIndex == 0)
                return 0;
            if(a.ObjectIndex == 201)
                return 1;
            if(made != null && made.Contains(a.ObjectIndex))
                return 2;
            var kind = kinds.GetValueOrDefault(a.ObjectIndex);
            if(kind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Pc)
                return 3;
            if(kind is Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc or Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventNpc
               or Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Retainer or Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Companion)
                return 4;
            return 5;
        }
        return list.OrderBy(Rank).ThenBy(a => a.ObjectIndex).ToList();
    }

    public IEnumerable<(nint, ushort)> Watched()
    {
        if(Actor.IsSet)
            yield return (Actor.Address, Actor.ObjectIndex);
        if(AttachTarget.IsSet)
            yield return (AttachTarget.Address, AttachTarget.ObjectIndex);
    }

    public ActorRig? GetRig(RigService rigs) => Actor.IsSet ? rigs.GetRig(Actor.Address) : null;

    public ActorRig? GetOrCreateRig(RigService rigs)
        => Actor.IsSet ? rigs.GetOrCreateRig(Actor.Address, Actor.ObjectIndex, Actor.Name) : null;
}
