using System;
using System.Linq;
using System.Numerics;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using GameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace Proppy;

/// <summary>
/// Sitting on a placed chair / lying on a placed bed, in GPose only: the GPose target (or your own character's GPose
/// copy) is put on the item's seat spot, facing its way, and plays the game's chair-sitting or lying animation.
/// (Outside GPose this would move your character to somewhere only your screen has: a teleport the server sees.)
/// </summary>
public sealed unsafe class Seat
{
    private readonly ushort _sitId, _lieId;
    private nint _address;
    private ushort _index;

    public PropService.Prop? On { get; private set; }
    public bool Lying { get; private set; }
    public string Status { get; private set; } = "";

    public Seat(IDataManager data)
    {
        var sheet = data.GetExcelSheet<ActionTimeline>();
        if(sheet == null)
            return;
        foreach(var row in sheet)
        {
            string key = row.Key.ToString();
            if(key == "emote/s_pose01_loop")
                _sitId = (ushort)row.RowId;
            else if(key == "emote/l_pose01_loop")
                _lieId = (ushort)row.RowId;
        }
    }

    // The character to sit: the GPose target, else your own character's GPose copy.
    private static (nint Address, ushort Index)? Who()
    {
        var target = Plugin.Targets.GPoseTarget;
        if(target != null && target.Address != 0 && target is Dalamud.Game.ClientState.Objects.Types.ICharacter)
            return (target.Address, target.ObjectIndex);
        var copy = Plugin.Objects[201];
        if(copy != null && copy.Address != 0 && copy is Dalamud.Game.ClientState.Objects.Types.ICharacter)
            return (copy.Address, copy.ObjectIndex);
        return null;
    }

    public void SitOn(PropService.Prop prop, bool lie)
    {
        if(!Plugin.ClientState.IsGPosing)
        {
            Status = "Sitting and lying on items work in GPose only.";
            return;
        }
        ushort id = lie ? _lieId : _sitId;
        if(id == 0)
        {
            Status = "Couldn't find the game's sitting / lying animation.";
            return;
        }
        if(Who() is not { } who)
        {
            Status = "No character to sit (target one in GPose).";
            return;
        }
        if(On != null && _address != who.Address)
            StandUp();
        _address = who.Address;
        _index = who.Index;
        On = prop;
        Lying = lie;
        var c = (NativeCharacter*)_address;
        c->Timeline.BaseOverride = id;
        c->Timeline.TimelineSequencer.PlayTimeline(id);
        Status = lie ? "Lying on it (Lying spot / Facing to fit)." : "Sitting on it (Seat spot / Facing to fit).";
    }

    public void StandUp()
    {
        if(On != null && _address != 0 && Still())
        {
            var c = (NativeCharacter*)_address;
            c->Timeline.BaseOverride = 0;
            c->Timeline.TimelineSequencer.PlayTimeline(3);
        }
        On = null;
        _address = 0;
        Status = "";
    }

    private bool Still()
    {
        var obj = Plugin.Objects[_index];
        return obj != null && obj.Address == _address;
    }

    /// <summary>Framework update: the character kept on the seat (the item may be moved).</summary>
    public void Update(PropService props)
    {
        var prop = On;
        if(prop == null)
            return;
        if(!Plugin.ClientState.IsGPosing || !props.Props.Contains(prop) || !Still())
        {
            On = null;
            _address = 0;
            Status = "";
            return;
        }
        var offset = Lying ? prop.LieOffset : prop.SitOffset;
        float turn = (Lying ? prop.LieTurn : prop.SitTurn) * MathF.PI / 180f;
        var rotation = prop.Quaternion;
        var at = prop.Position + Vector3.Transform(offset * prop.Scale, rotation);
        // facing: the item's own yaw plus the turn
        var forward = Vector3.Transform(Vector3.UnitZ, rotation);
        float yaw = MathF.Atan2(forward.X, forward.Z) + turn;
        if(!float.IsFinite(at.X) || !float.IsFinite(yaw))
            return;
        var go = (GameObject*)_address;
        go->Position = at;
        go->Rotation = yaw;
        if(go->DrawObject != null)
        {
            go->DrawObject->Object.Position = at;
            go->DrawObject->Object.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
        }
    }
}
