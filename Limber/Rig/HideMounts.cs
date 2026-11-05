using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Conditions;
using GameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace Limber.Rig;

/// <summary>
/// Hide mounts (Extras): a riding character's mount can't be seen, so they look like they're floating. Only on your
/// screen. The mount's own model is made fully see-through, late in every frame (the bone update: the game sets a
/// character's see-through-ness itself earlier each frame, near the camera for example, so set any sooner it's undone).
/// The riders are models of their own and stay as they are. The mount is found through its rider every frame (never
/// kept by address); paused for loading screens, cutscenes and a moment around entering or leaving GPose.
/// </summary>
public sealed unsafe class HideMounts
{
    private volatile (nint Address, ushort Index)[] _riders = [];
    private bool _wasGPosing;
    private long _pauseUntil;

    /// <summary>Framework update: who's riding a mount to hide.</summary>
    public void Update(bool allowed, bool on, Func<string, bool> wanted)
    {
        long now = Environment.TickCount64;
        bool gposing = Plugin.ClientState.IsGPosing;
        if(gposing != _wasGPosing)
        {
            _wasGPosing = gposing;
            _pauseUntil = now + 3000;
        }
        bool busy = Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.BetweenAreas51]
            || Plugin.Condition[ConditionFlag.WatchingCutscene] || Plugin.Condition[ConditionFlag.OccupiedInCutSceneEvent]
            || Plugin.Condition[ConditionFlag.WatchingCutscene78] || now < _pauseUntil;
        if(!allowed || !on || busy)
        {
            if(_riders.Length > 0)
                _riders = [];
            return;
        }
        var list = new List<(nint, ushort)>();
        foreach(var obj in Plugin.ObjectTable)
            if(obj is Dalamud.Game.ClientState.Objects.Types.ICharacter && obj.Address != 0 && wanted(obj.Name.TextValue)
               && MountOf(obj.Address) != null)
                list.Add((obj.Address, obj.ObjectIndex));
        _riders = list.ToArray();
    }

    private readonly HashSet<(nint, ushort)> _shrunk = new(); // bone update thread only

    /// <summary>Bone update stage (late: after the riders have been placed on their mounts).</summary>
    public void UpdateFrame()
    {
        var riders = _riders;
        // No longer hidden: back to its own size (the game builds the pose again anyway while it's animating).
        if(_shrunk.Count > 0)
            foreach(var r in new List<(nint, ushort)>(_shrunk))
            {
                if(Array.IndexOf(riders, r) >= 0)
                    continue;
                _shrunk.Remove(r);
                try
                {
                    var rider = Limber.Core.ActorSkeleton.GetObject(r.Item2);
                    var m = rider != null && (nint)rider == r.Item1 ? MountOf(r.Item1) : null;
                    if(m != null && m->DrawObject != null)
                        ((FFXIVClientStructs.FFXIV.Client.Graphics.Scene.DrawObject*)m->DrawObject)->SetTransparency(0f);
                }
                catch
                {
                }
            }
        foreach(var (address, index) in riders)
        {
            _shrunk.Add((address, index));
            try
            {
                var rider = Limber.Core.ActorSkeleton.GetObject(index);
                if(rider == null || (nint)rider != address)
                    continue;
                var mount = MountOf(address);
                if(mount != null && mount->DrawObject != null)
                    ((FFXIVClientStructs.FFXIV.Client.Graphics.Scene.DrawObject*)mount->DrawObject)->SetTransparency(1f);
            }
            catch
            {
            }
        }
    }

    private static GameObject* MountOf(nint rider)
    {
        var obj = (GameObject*)rider;
        if(obj == null || !obj->IsCharacter())
            return null;
        return (GameObject*)((NativeCharacter*)rider)->Mount.MountObject;
    }
}
