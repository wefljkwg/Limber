using System;
using System.Collections.Generic;
using System.Numerics;
using Limber.Core;
using SceneObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object;

namespace Limber.Rig;

/// <summary>
/// Moves, turns and scales whole characters (the Actor tab): an offset on top of where the game puts the
/// character's model, applied every frame in the bone update (first, so everything else works on the moved
/// character). Only your screen's copy of the model is changed; the game never hears of it.
/// </summary>
public sealed unsafe class ActorTransforms : IDisposable
{
    public sealed class Offset
    {
        public required nint Address;
        public required ushort Index;
        public Vector3 Move;          // metres, world axes
        public Vector3 Turn;          // degrees: yaw, pitch, roll (the character's own axes)
        public float Scale = 1f;
        // Kept while the character walks or turns: the game rewrites the model's place every frame it moves
        // (wiping an offset on it), so this one goes on the skeleton's root instead, which the game rebuilds from
        // the animation and Limber re-applies after.
        public bool KeepWhileMoving = true;
        // Wins over other plugins that set the character's place (Brio's actor transform): whenever anything sets
        // it, Limber's offset goes back on top straight after.
        public bool Priority = true;

        public bool IsIdentity => Move == Vector3.Zero && Turn == Vector3.Zero && Scale == 1f;
        internal bool HasState => !IsIdentity || !KeepWhileMoving || !Priority; // anything but the defaults

        // Where the game had the model (before the offset), and what was written last frame.
        internal bool HaveBase;
        internal Vector3 BasePosition, BaseScale, LastPosition, LastScale;
        internal Quaternion BaseRotation, LastRotation;
        internal bool HaveRoot;
        internal Frame RootWritten;
        internal Vector3 RootWrittenScale, LastMove;
        internal Quaternion LastTurn = Quaternion.Identity;
        internal float LastScaleApplied = 1f;
    }

    public const float MinScale = 0.0001f, MaxScale = 10f;

    private readonly Dictionary<nint, Offset> _offsets = new();
    public readonly object Sync = new();
    public Func<bool> Allowed = () => true;

    public Offset? Get(nint address)
    {
        lock(Sync)
            return _offsets.TryGetValue(address, out var o) ? o : null;
    }

    public Offset GetOrAdd(nint address, ushort index)
    {
        lock(Sync)
        {
            if(!_offsets.TryGetValue(address, out var o))
                _offsets[address] = o = new Offset { Address = address, Index = index };
            return o;
        }
    }

    public void Reset(nint address)
    {
        lock(Sync)
            if(_offsets.TryGetValue(address, out var o))
            {
                o.Move = Vector3.Zero;
                o.Turn = Vector3.Zero;
                o.Scale = 1f;
            }
    }

    public void ResetAll()
    {
        lock(Sync)
            foreach(var o in _offsets.Values)
            {
                o.Move = Vector3.Zero;
                o.Turn = Vector3.Zero;
                o.Scale = 1f;
                o.KeepWhileMoving = true;
                o.Priority = true;
            }
    }

    private readonly SkeletonInfoCache _cache = new(); // bone update thread only

    // The offset on the whole skeleton (model space), so it holds whatever the game does to the model.
    private void ApplyToRoot(Offset o)
    {
        if(!ActorSkeleton.TryGet(o.Address, o.Index, _cache, out var skeleton))
            return;
        var t = o.Turn * (MathF.PI / 180f);
        var turn = Quaternion.CreateFromYawPitchRoll(t.X, t.Y, t.Z);
        float s = Math.Clamp(o.Scale, MinScale, MaxScale);
        var rootScale = skeleton.RootScale;
        if(MathF.Abs(rootScale.X) < 1e-6f || MathF.Abs(rootScale.Y) < 1e-6f || MathF.Abs(rootScale.Z) < 1e-6f)
            return;
        // World move -> model space (the model's own turn and size taken out).
        var move = Vector3.Transform(o.Move, Quaternion.Inverse(skeleton.RootRotation)) / rootScale;
        if(!MathUtil.IsFinite(move) || !MathUtil.IsFinite(turn))
            return;
        if(skeleton.Info.Count == 0)
            return;
        // If nothing rebuilt the pose since last frame (animation paused), it still holds last frame's offset:
        // take that off first, so it doesn't pile up.
        var root = skeleton.GetModelFrame(0);
        if(o.HaveRoot && root.Position == o.RootWritten.Position && root.Rotation == o.RootWritten.Rotation && skeleton.GetModelScale(0) == o.RootWrittenScale)
            UndoLast(skeleton, o);
        skeleton.TransformAllBones(move, turn, s);
        o.HaveRoot = true;
        o.LastMove = move;
        o.LastTurn = turn;
        o.LastScaleApplied = s;
        o.RootWritten = skeleton.GetModelFrame(0);
        o.RootWrittenScale = skeleton.GetModelScale(0);
        skeleton.RefreshAttachedModels();
    }

    // The inverse of last frame's TransformAllBones.
    private static void UndoLast(in ActorSkeleton skeleton, Offset o)
    {
        var inverse = Quaternion.Inverse(o.LastTurn);
        float s = 1f / o.LastScaleApplied;
        // p = move + turn * (q * s)  ->  q = inverse * (p - move) / s  =  (-inverse*move/s) + inverse * (p * 1/s)
        skeleton.TransformAllBones(-Vector3.Transform(o.LastMove, inverse) * s, inverse, s);
    }

    // Turned off: if the pose wasn't rebuilt since (animation paused), take the offset back off.
    private void RestoreRoot(Offset o)
    {
        o.HaveRoot = false;
        if(!ActorSkeleton.TryGet(o.Address, o.Index, _cache, out var skeleton) || skeleton.Info.Count == 0)
            return;
        var root = skeleton.GetModelFrame(0);
        if(root.Position != o.RootWritten.Position || root.Rotation != o.RootWritten.Rotation || skeleton.GetModelScale(0) != o.RootWrittenScale)
            return;
        UndoLast(skeleton, o);
        skeleton.RefreshAttachedModels();
    }

    // Undo / redo snapshot: every character's offset.
    public string CaptureState()
    {
        lock(Sync)
        {
            var parts = new List<string>();
            foreach(var o in _offsets.Values)
                if(o.HasState)
                    parts.Add(FormattableString.Invariant($"{(long)o.Address}|{o.Index}|{o.Move.X}|{o.Move.Y}|{o.Move.Z}|{o.Turn.X}|{o.Turn.Y}|{o.Turn.Z}|{o.Scale}|{(o.KeepWhileMoving ? 1 : 0)}|{(o.Priority ? 1 : 0)}"));
            parts.Sort(StringComparer.Ordinal);
            return string.Join(";", parts);
        }
    }

    public void RestoreState(string state)
    {
        lock(Sync)
        {
            foreach(var o in _offsets.Values)
            {
                o.Move = Vector3.Zero;
                o.Turn = Vector3.Zero;
                o.Scale = 1f;
                o.KeepWhileMoving = true;
                o.Priority = true;
            }
            foreach(var entry in state.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var f = entry.Split('|');
                if(f.Length < 9)
                    continue;
                static float F(string v) => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture);
                var o = GetOrAdd((nint)long.Parse(f[0]), ushort.Parse(f[1]));
                o.Move = new Vector3(F(f[2]), F(f[3]), F(f[4]));
                o.Turn = new Vector3(F(f[5]), F(f[6]), F(f[7]));
                o.Scale = F(f[8]);
                o.KeepWhileMoving = f.Length <= 9 || f[9] == "1";
                o.Priority = f.Length <= 10 || f[10] == "1";
            }
        }
    }

    // The offset on the model's place (draw object), on top of wherever the game or another plugin last put it.
    private static bool WriteModel(Offset o, SceneObject* draw, bool allowed)
    {
        var position = *(Vector3*)&draw->Position;
        var rotation = *(Quaternion*)&draw->Rotation;
        var scale = *(Vector3*)&draw->Scale;

        // Whatever the game changed since (walking moves it, turning turns it) is the new base for that part
        // only. The parts it left alone still hold our offset, so they keep their old base; re-basing
        // them too would stack the offset every time the character moved (shrinking it away).
        if(!o.HaveBase || position != o.LastPosition)
            o.BasePosition = position;
        if(!o.HaveBase || rotation != o.LastRotation)
            o.BaseRotation = rotation;
        if(!o.HaveBase || scale != o.LastScale)
            o.BaseScale = scale;
        o.HaveBase = true;
        bool apply = allowed && !o.IsIdentity && !o.KeepWhileMoving;
        var t = o.Turn * (MathF.PI / 180f);
        var newPosition = apply ? o.BasePosition + o.Move : o.BasePosition;
        var newRotation = apply ? Quaternion.Normalize(o.BaseRotation * Quaternion.CreateFromYawPitchRoll(t.X, t.Y, t.Z)) : o.BaseRotation;
        var newScale = apply ? o.BaseScale * Math.Clamp(o.Scale, MinScale, MaxScale) : o.BaseScale;
        if(!MathUtil.IsFinite(newPosition) || !MathUtil.IsFinite(newRotation) || !MathUtil.IsFinite(newScale))
            return false;
        *(Vector3*)&draw->Position = newPosition;
        *(Quaternion*)&draw->Rotation = newRotation;
        *(Vector3*)&draw->Scale = newScale;
        o.LastPosition = newPosition;
        o.LastRotation = newRotation;
        o.LastScale = newScale;
        return true;
    }

    // ---- Priority: the game's "set this character's place" (which Brio also hooks, to put its own transform on
    // the model). Limber's hook goes on top of theirs (hooked again if another plugin hooks after Limber), and puts
    // the offset back on straight after anyone sets the place.

    private delegate void SetPositionDelegate(FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject* gameObject, float x, float y, float z);

    private Dalamud.Hooking.Hook<SetPositionDelegate>? _setPosition;
    private readonly List<SetPositionDelegate> _setPositionDetours = new();
    private Dalamud.Plugin.Services.IGameInteropProvider? _interop;
    private nint _setPositionAddress;
    private readonly byte[] _setPositionEntry = new byte[16], _setPositionCheck = new byte[16];
    private int _setPositionRehooks, _setPositionFrames;

    /// <summary>Hooks the game's set-position (once), for "Take priority over other plugins".</summary>
    public void StartPriority(Dalamud.Plugin.Services.IGameInteropProvider interop)
    {
        if(_interop != null)
            return;
        _interop = interop;
        try
        {
            _setPositionAddress = (nint)FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject.Addresses.SetPosition.Value;
            if(_setPositionAddress != 0)
                HookSetPosition();
        }
        catch(Exception e)
        {
            Plugin.Log.Warning(e, "Limber: couldn't hook the game's set position (Move character priority won't work)");
        }
    }

    private void HookSetPosition()
    {
        SetPositionDelegate? original = null;
        SetPositionDelegate detour = (go, x, y, z) => SetPositionDetour(original!, go, x, y, z);
        var created = _interop!.HookFromAddress<SetPositionDelegate>(_setPositionAddress, detour);
        original = created.Original;
        _setPositionDetours.Add(detour);
        _setPositionDetours.Add(original);
        var old = _setPosition;
        created.Enable();
        _setPosition = created;
        old?.Dispose();
        System.Runtime.InteropServices.Marshal.Copy(_setPositionAddress, _setPositionEntry, 0, _setPositionEntry.Length);
    }

    private void SetPositionDetour(SetPositionDelegate original, FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject* go, float x, float y, float z)
    {
        original(go, x, y, z);
        try
        {
            if(go == null || go->DrawObject == null)
                return;
            lock(Sync)
            {
                if(_offsets.Count == 0 || !_offsets.TryGetValue((nint)go, out var o) || !o.Priority || o.KeepWhileMoving || o.IsIdentity || !Allowed())
                    return;
                WriteModel(o, (SceneObject*)go->DrawObject, true);
            }
        }
        catch
        {
            // (never let a failure here reach the game)
        }
    }

    /// <summary>Framework update: stay on top if another plugin hooked set-position after Limber.</summary>
    public void CheckPriorityHook()
    {
        if(_setPosition == null || ++_setPositionFrames < 120)
            return;
        _setPositionFrames = 0;
        System.Runtime.InteropServices.Marshal.Copy(_setPositionAddress, _setPositionCheck, 0, _setPositionCheck.Length);
        if(!_setPositionCheck.AsSpan().SequenceEqual(_setPositionEntry) && _setPositionRehooks < 25)
        {
            _setPositionRehooks++;
            try
            {
                HookSetPosition();
            }
            catch(Exception e)
            {
                Plugin.Log.Warning(e, "Limber: couldn't hook set position again");
            }
        }
    }

    public void Dispose()
    {
        _setPosition?.Disable();
        _setPosition?.Dispose();
        _setPosition = null;
    }

    /// <summary>Bone update stage (first).</summary>
    public void UpdateFrame()
    {
        lock(Sync)
        {
            if(_offsets.Count == 0)
                return;
            bool allowed = Allowed();
            List<nint>? drop = null;
            foreach(var (address, o) in _offsets)
            {
                var obj = ActorSkeleton.GetObject(o.Index);
                if(obj == null || (nint)obj != address || obj->DrawObject == null)
                {
                    (drop ??= new()).Add(address);
                    continue;
                }
                if(!WriteModel(o, (SceneObject*)obj->DrawObject, allowed))
                    continue;
                if(allowed && !o.IsIdentity && o.KeepWhileMoving)
                    ApplyToRoot(o);
                else if(o.HaveRoot)
                    RestoreRoot(o);
                if(!o.HasState && !o.HaveRoot)
                    (drop ??= new()).Add(address); // back where the game had it: forget it
            }
            if(drop != null)
                foreach(var a in drop)
                    _offsets.Remove(a);
        }
    }
}
