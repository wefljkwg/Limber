using Dalamud.Plugin.Services;
using Limber.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Limber.Rig;

// Holds every character's goals and applies them once per frame, after the game (and any other posing
// tool, and the Koikatsu link) has built that frame's pose and before it is drawn. The shared
// BoneUpdateHook calls UpdateFrame.
public sealed unsafe class RigService : IDisposable
{
    private readonly BoneUpdateHook _hook;
    private readonly IFramework _framework;
    private readonly IClientState _clientState;
    private readonly IPluginLog _log;
    private readonly RigSettings _config;

    // Everything below is shared between the frame update and the UI; always lock Sync to touch it.
    public readonly object Sync = new();

    private readonly Dictionary<nint, ActorRig> _rigs = [];
    private readonly Dictionary<nint, BoneSnapshot> _snapshots = [];
    private readonly Dictionary<nint, ushort> _watched = [];
    private readonly SkeletonInfoCache _infoCache = new();

    private readonly List<ActorRig> _order = [];
    private readonly List<ActorRig> _pending = [];
    private readonly List<Goal> _sortedGoals = [];
    private readonly List<int> _joints = [];
    private readonly HashSet<nint> _snapshotted = [];

    private volatile bool _active;

    public bool IsActive => _active;
    /// <summary>Fingers wrapping onto the skin (Pose > Hand > Grab), by character.</summary>
    public readonly FingerGrab Grab = new();
    public bool IsGPosing { get; private set; }
    public bool HookReady => _hook.IsHooked;

    public RigService(BoneUpdateHook hook, IFramework framework, IClientState clientState, IPluginLog log, RigSettings config)
    {
        _hook = hook;
        _framework = framework;
        _clientState = clientState;
        _log = log;
        _config = config;

        _framework.Update += OnFrameworkUpdate;
    }

    // ---- Public API for the UI (lock Sync around anything that touches rigs or goals)

    public IReadOnlyCollection<ActorRig> Rigs => _rigs.Values;

    public ActorRig? GetRig(nint address) => _rigs.TryGetValue(address, out var rig) ? rig : null;

    public ActorRig GetOrCreateRig(nint address, ushort objectIndex, string name)
    {
        if(_rigs.TryGetValue(address, out var rig))
        {
            rig.Name = name;
            return rig;
        }

        rig = new ActorRig(address, objectIndex, name);
        _rigs[address] = rig;
        return rig;
    }

    public void RemoveRig(nint address) => _rigs.Remove(address);

    public void ClearAll() => _rigs.Clear();

    public BoneSnapshot? GetSnapshot(nint address) => _snapshots.TryGetValue(address, out var snapshot) ? snapshot : null;

    // Characters the UI wants bone positions for, even without any goals on them.
    public void SetWatched(IEnumerable<(nint Address, ushort ObjectIndex)> actors)
    {
        _watched.Clear();
        foreach(var (address, index) in actors)
            if(address != 0)
                _watched[address] = index;
    }

    // Detach everything that holds on to the given character (e.g. it left the scene).
    public void DetachFrom(nint parent)
    {
        foreach(var rig in _rigs.Values)
            foreach(var goal in rig.Goals)
                if(goal.Space == GoalSpace.Attached && goal.ParentActor == parent)
                    Detach(goal);
    }

    public static void Detach(Goal goal)
    {
        goal.Space = goal.HomeSpace == GoalSpace.Attached ? GoalSpace.FollowBody : goal.HomeSpace;
        goal.ParentActor = 0;
        goal.ParentBone = null;
        goal.ParentActorName = string.Empty;
        goal.RequestCapture();
    }

    // ---- Undo / redo (whole-scene snapshots of every character's goals, plus the other undoable parts:
    // the Actor tab's moves and the props)

    private const int MaxHistory = 100;

    private sealed record RigState(nint Address, ushort ObjectIndex, string Name, Goal[] Goals);
    private sealed record Step(RigState[] Rigs, string[] Parts);

    /// <summary>Something else undo / redo covers. Its state is a string (a snapshot it can restore from).
    /// Changes are noticed by <see cref="WatchParts"/>, so its UI doesn't have to call Checkpoint.</summary>
    public sealed class UndoPart
    {
        public required Func<string> Capture;
        public required Action<string> Restore;
        internal string? Settled;
    }

    private readonly List<UndoPart> _parts = [];
    private readonly List<Step> _undo = [];
    private readonly List<Step> _redo = [];

    public void AddUndoPart(UndoPart part)
    {
        lock(Sync)
            _parts.Add(part);
    }

    public bool CanUndo { get { lock(Sync) return _undo.Count > 0; } }
    public bool CanRedo { get { lock(Sync) return _redo.Count > 0; } }

    // Call right before a change the user makes, so Undo returns to the state before it.
    public void Checkpoint()
    {
        lock(Sync)
            Push(CaptureState());
    }

    private void Push(Step step)
    {
        _undo.Add(step);
        if(_undo.Count > MaxHistory)
            _undo.RemoveAt(0);
        _redo.Clear();
    }

    /// <summary>Once per frame (UI thread): a part that changed since it last settled becomes an undo step once
    /// the user lets go (no slider dragged, no mouse button held), so a whole drag is one step.</summary>
    public void WatchParts(bool userBusy)
    {
        lock(Sync)
        {
            for(int i = 0; i < _parts.Count; i++)
            {
                var part = _parts[i];
                var now = part.Capture();
                if(part.Settled == null)
                {
                    part.Settled = now;
                    continue;
                }
                if(now == part.Settled || userBusy)
                    continue;
                var step = CaptureState();
                step.Parts[i] = part.Settled; // the state before the change
                Push(step);
                part.Settled = now;
            }
        }
    }

    public bool Undo()
    {
        lock(Sync)
        {
            SettleParts();
            if(_undo.Count == 0)
                return false;
            _redo.Add(CaptureState());
            RestoreState(_undo[^1]);
            _undo.RemoveAt(_undo.Count - 1);
            return true;
        }
    }

    public bool Redo()
    {
        lock(Sync)
        {
            SettleParts();
            if(_redo.Count == 0)
                return false;
            _undo.Add(CaptureState());
            RestoreState(_redo[^1]);
            _redo.RemoveAt(_redo.Count - 1);
            return true;
        }
    }

    // A change still being made when undo is pressed counts as its own step first.
    private void SettleParts() => WatchParts(false);

    private Step CaptureState()
        => new(_rigs.Values.Select(r => new RigState(r.Address, r.ObjectIndex, r.Name, r.Goals.Select(g => g.Clone()).ToArray())).ToArray(),
               _parts.Select(p => p.Settled ?? p.Capture()).ToArray());

    // Characters that have left since are dropped by the next framework update, like any other rig.
    private void RestoreState(Step state)
    {
        _rigs.Clear();
        foreach(var saved in state.Rigs)
        {
            var rig = new ActorRig(saved.Address, saved.ObjectIndex, saved.Name);
            rig.Goals.AddRange(saved.Goals.Select(g => g.Clone()));
            _rigs[saved.Address] = rig;
        }
        for(int i = 0; i < _parts.Count && i < state.Parts.Length; i++)
        {
            var part = _parts[i];
            if(part.Capture() != state.Parts[i])
            {
                try { part.Restore(state.Parts[i]); }
                catch(Exception e) { _log.Warning(e, "Limber: couldn't undo a change"); }
            }
            part.Settled = part.Capture();
        }
    }

    // ---- Main-thread housekeeping

    private void OnFrameworkUpdate(IFramework framework)
    {
        var wasGPosing = IsGPosing;
        IsGPosing = _clientState.IsGPosing;
        if(wasGPosing != IsGPosing)
        {
            // Entering or leaving GPose swaps the characters; old undo steps would land on the wrong ones.
            lock(Sync)
            {
                _undo.Clear();
                _redo.Clear();
            }
        }
        _active = _config.Enabled && (IsGPosing || _config.WorkOutsideGPose);

        lock(Sync)
        {
            // Forget characters that left (their object slot now holds something else, or nothing).
            List<nint>? gone = null;
            foreach(var rig in _rigs.Values)
            {
                var current = ActorSkeleton.GetObject(rig.ObjectIndex);
                if((nint)current != rig.Address)
                    (gone ??= []).Add(rig.Address);
            }

            if(gone != null)
            {
                foreach(var address in gone)
                {
                    _rigs.Remove(address);
                    _snapshots.Remove(address);
                    DetachFrom(address);
                }
            }

            // Attachments to characters that are no longer there let go.
            foreach(var rig in _rigs.Values)
            {
                foreach(var goal in rig.Goals)
                {
                    if(goal.Space != GoalSpace.Attached)
                        continue;

                    var parent = ActorSkeleton.GetObject(goal.ParentObjectIndex);
                    if((nint)parent != goal.ParentActor)
                        Detach(goal);
                }
            }
        }
    }

    // ---- Per frame (called from the shared bone-update hook, after the Koikatsu link)

    public void UpdateFrame()
    {
        lock(Sync)
        {
            _snapshotted.Clear();

            if(_active && _rigs.Count > 0)
            {
                OrderRigs();
                foreach(var rig in _order)
                    SolveRig(rig);
            }
            if(_active)
                RunGrab();
            else
            {
                // Not posing: last solved frames go stale, so a re-anchor starts from the live pose instead.
                foreach(var rig in _rigs.Values)
                    foreach(var goal in rig.Goals)
                        goal.HasSolved = false;
            }

            // Bone positions for characters the UI is looking at but Limber isn't posing.
            foreach(var (address, index) in _watched)
            {
                if(_snapshotted.Contains(address))
                    continue;

                if(ActorSkeleton.TryGet(address, index, _infoCache, out var skeleton))
                    TakeSnapshot(address, skeleton);
                else
                    _snapshots.Remove(address);
            }

            // Drop snapshots nobody asked for.
            if(_snapshots.Count > _watched.Count + 4)
            {
                foreach(var key in _snapshots.Keys.Where(k => !_watched.ContainsKey(k)).ToList())
                    _snapshots.Remove(key);
            }
        }
    }

    // Fingers curl onto the skin they're over (after all posing, so on top of it).
    private void RunGrab()
    {
        if(Grab.Hands.Count == 0)
            return;
        List<nint>? gone = null;
        foreach(var (address, hand) in Grab.Hands)
        {
            if(!hand.Left && !hand.Right)
                continue;
            ushort index = _rigs.TryGetValue(address, out var rig) ? rig.ObjectIndex : (ushort)0;
            if(rig == null && !_watched.TryGetValue(address, out index))
            {
                (gone ??= new()).Add(address);
                continue;
            }
            if(!ActorSkeleton.TryGet(address, index, _infoCache, out var skeleton))
            {
                (gone ??= new()).Add(address);
                continue;
            }
            Grab.Apply(address, skeleton);
            if(_watched.ContainsKey(address))
                TakeSnapshot(address, skeleton);
        }
        if(gone != null)
            foreach(var a in gone)
                Grab.Forget(a);
    }

    // Characters holding on to another character are posed after it, so they reach its final pose.
    private void OrderRigs()
    {
        _order.Clear();
        _pending.Clear();
        _pending.AddRange(_rigs.Values);

        while(_pending.Count > 0)
        {
            bool progressed = false;
            for(int i = 0; i < _pending.Count; i++)
            {
                var rig = _pending[i];
                bool ready = true;
                foreach(var goal in rig.Goals)
                {
                    if(goal.Space != GoalSpace.Attached || goal.ParentActor == rig.Address)
                        continue;

                    if(_pending.Any(p => p.Address == goal.ParentActor))
                    {
                        ready = false;
                        break;
                    }
                }

                if(ready)
                {
                    _order.Add(rig);
                    _pending.RemoveAt(i);
                    i--;
                    progressed = true;
                }
            }

            if(!progressed)
            {
                // Characters holding on to each other: no order is right, just pose them.
                _order.AddRange(_pending);
                _pending.Clear();
            }
        }
    }

    private void SolveRig(ActorRig rig)
    {
        if(rig.Goals.Count == 0)
            return;

        if(!ActorSkeleton.TryGet(rig.Address, rig.ObjectIndex, _infoCache, out var skeleton))
            return;

        var info = skeleton.Info;

        // Bones higher up first, so hands and feet are placed after the body they hang from has moved.
        _sortedGoals.Clear();
        foreach(var goal in rig.Goals)
        {
            int index = info.IndexOf(goal.Bone);
            goal.SortDepth = index >= 0 ? info.Depths[index] : int.MaxValue;
            _sortedGoals.Add(goal);
        }
        _sortedGoals.Sort((a, b) => a.SortDepth.CompareTo(b.SortDepth));

        // Mirrored bones: the animation's pose of every bone involved (and its other-side partner) before Limber.
        _mirrorBones.Clear();
        _mirrorAnim.Clear();
        foreach(var goal in _sortedGoals)
        {
            if(!goal.Enabled || goal.Mirror == MirrorMode.Off)
                continue;
            foreach(var name in MirrorSources(info, goal))
            {
                int b = info.IndexOf(name), p = info.IndexOf(OtherSide(name) ?? "");
                if(b < 0 || p < 0 || _mirrorBones.Exists(m => m.Bone == b))
                    continue;
                _mirrorBones.Add((b, p, goal.Mirror));
                foreach(int x in new[] { b, p, info.ParentOf(b), info.ParentOf(p) })
                    if(x >= 0)
                        _mirrorAnim[x] = skeleton.GetModelFrame(x).Rotation;
            }
        }

        CaptureHelpers(skeleton);

        bool changed = false;
        _extraGoals.Clear();

        foreach(var goal in _sortedGoals)
        {
            if(!goal.Enabled)
                continue;

            int bone = info.IndexOf(goal.Bone);
            if(bone < 0)
            {
                // A bone of the face, hair, tail or a mod's extra skeleton: placed after the body (below).
                if(skeleton.TryFindExtraBone(goal.Bone, _infoCache, out int part, out int partBone))
                    _extraGoals.Add((goal, part, partBone));
                else
                    goal.LastSolveFailed = true;
                continue;
            }

            if(!TryGetGoalSpace(rig, skeleton, goal, bone, out var space))
            {
                goal.LastSolveFailed = true;
                continue;
            }

            if(goal.PendingRestTurn is Quaternion restTurn)
            {
                goal.PendingRestTurn = null;
                if(skeleton.TryGetReferenceLocal(bone, out _, out var rest))
                    SetRestTurn(goal, rest, restTurn);
            }

            var current = skeleton.GetModelFrame(bone);
            PrepareTarget(goal, skeleton, space, current);

            if(!goal.HasTarget)
                continue;

            var target = Moved(rig, goal, space.FromLocal(goal.Local), skeleton);
            bool solved = Apply(skeleton, goal, bone, target);
            goal.LastSolveFailed = !solved;
            changed |= solved;
        }

        // The other side follows mirrored bones: the turn each joint added (on top of its parent's change),
        // reflected across the body's middle, added on top of whatever the other side's parent did. Opposite
        // swings the limb the other way at the shoulder / hip; elbows and knees still bend the way they bend.
        // Parents first, so each lands exactly.
        if(_mirrorBones.Count > 0)
        {
            var held = new HashSet<string>(rig.Goals.Where(g => g.Enabled && g.HasTarget).Select(g => g.Bone));
            _mirrorAdds.Clear();
            foreach(var (b, _, _) in _mirrorBones)
                _mirrorAdds[b] = Quaternion.Normalize(MirrorDelta(skeleton, b) * Quaternion.Inverse(MirrorDelta(skeleton, info.ParentOf(b))));
            foreach(var (b, p, mode) in _mirrorBones.OrderBy(m => info.Depths[m.Bone]))
            {
                if(held.Contains(info.Names[p]))
                    continue; // the other side has its own pose
                var add = _mirrorAdds[b];
                // In the body's own axes (the root may be turned by the Actor tab), reflected across its middle.
                var root = info.Count > 0 && info.Parents[0] < 0 ? skeleton.GetModelFrame(0).Rotation : Quaternion.Identity;
                var local = Quaternion.Normalize(Quaternion.Inverse(root) * add * root);
                var mirrored = root * new Quaternion(local.X, -local.Y, -local.Z, local.W) * Quaternion.Inverse(root);
                if(mode == MirrorMode.Opposite && (SwingsLimb(info.Names[b]) || !_mirrorAdds.ContainsKey(info.ParentOf(b))))
                    mirrored = Quaternion.Inverse(mirrored);
                skeleton.SetModelRotation(p, Quaternion.Normalize(mirrored * MirrorDelta(skeleton, info.ParentOf(p)) * _mirrorAnim[p]));
                changed = true;
            }
        }

        if(changed)
            FixHelpers(skeleton);

        // Remember where every held bone ended up, for re-anchoring (see Goal.SolvedModel).
        foreach(var goal in _sortedGoals)
        {
            if(_extraGoals.Exists(e => e.Goal == goal))
                continue;
            int bone = goal.Enabled && goal.HasTarget && !goal.LastSolveFailed ? info.IndexOf(goal.Bone) : -1;
            goal.HasSolved = bone >= 0;
            if(bone >= 0)
                goal.SolvedModel = skeleton.GetModelFrame(bone);
        }

        if(changed)
        {
            skeleton.RealignPartials(_infoCache);
            skeleton.RefreshAttachedModels();
        }

        if(_extraGoals.Count > 0)
            SolveExtraGoals(rig, skeleton);

        if(_watched.ContainsKey(rig.Address))
            TakeSnapshot(rig.Address, skeleton);
    }

    private readonly List<(Goal Goal, int Part, int Bone)> _extraGoals = [];

    /// <summary>Motion props (piston, wheel, cycle) by character and bone; set by the plugin.</summary>
    public Func<nint, string, MotionProp?>? MotionFor;

    // A held target moved by the motion prop on that bone, if there is one.
    private Frame Moved(ActorRig rig, Goal goal, Frame target, in ActorSkeleton skeleton)
    {
        var motion = MotionFor?.Invoke(rig.Address, goal.Bone);
        return motion == null ? target : MotionService.Apply(motion, target, skeleton);
    }

    // Bones in the extra skeletons: turned (and placed, once moved) on top of the bone they hang from.
    private void SolveExtraGoals(ActorRig rig, in ActorSkeleton skeleton)
    {
        int Depth(in ActorSkeleton sk, int part, int bone) => sk.PartialInfo(part, _infoCache)?.Depths[bone] ?? 0;
        var ordered = new List<(Goal Goal, int Part, int Bone, int Depth)>();
        foreach(var (goal, part, bone) in _extraGoals)
            ordered.Add((goal, part, bone, Depth(skeleton, part, bone)));
        ordered.Sort((a, b) => a.Part != b.Part ? a.Part.CompareTo(b.Part) : a.Depth.CompareTo(b.Depth));

        foreach(var (goal, part, bone, _) in ordered)
        {
            Frame space;
            switch(goal.Space)
            {
                case GoalSpace.Pinned:
                    space = skeleton.Info.Count > 0 && skeleton.Info.Parents[0] < 0 ? skeleton.GetModelFrame(0) : Frame.Identity;
                    break;
                case GoalSpace.Attached:
                    if(!TryGetAttachedSpace(rig, skeleton, goal, out space))
                    {
                        goal.LastSolveFailed = true;
                        continue;
                    }
                    break;
                default:
                    space = skeleton.GetPartialParentFrame(part, bone, _infoCache);
                    break;
            }

            if(goal.PendingRestTurn is Quaternion restTurn)
            {
                goal.PendingRestTurn = null;
                if(skeleton.TryGetPartialReferenceLocal(part, bone, out var rest))
                    SetRestTurn(goal, rest, restTurn);
            }

            var current = skeleton.GetPartialFrame(part, bone);
            PrepareTarget(goal, skeleton, space, current);
            if(!goal.HasTarget)
                continue;

            var target = Moved(rig, goal, space.FromLocal(goal.Local), skeleton);
            if(goal.Space != GoalSpace.FollowBody || goal.HoldPosition)
                skeleton.SetPartialPosition(part, bone, target.Position);
            if(goal.LockRotation)
                skeleton.SetPartialRotation(part, bone, target.Rotation);

            goal.LastSolveFailed = false;
            goal.HasSolved = true;
            goal.SolvedModel = skeleton.GetPartialFrame(part, bone);
        }
    }

    // Requests from the UI. A goal that was already holding the bone keeps it where it was held
    // (not where the animation would put it).
    // Turned from its rest pose, on top of the bone above it (it moves along with the hand; nothing is held).
    private static void SetRestTurn(Goal goal, Quaternion rest, Quaternion turn)
    {
        goal.Local.Rotation = Quaternion.Normalize(rest * turn);
        goal.CaptureRotation = false;
        goal.LockRotation = true;
    }

    private static void PrepareTarget(Goal goal, in ActorSkeleton skeleton, Frame space, Frame current)
    {
        if(goal.CapturePosition || goal.CaptureRotation)
        {
            var local = space.ToLocal(goal.HasSolved ? goal.SolvedModel : current);
            if(goal.CapturePosition)
            {
                goal.Local.Position = local.Position;
                goal.CapturePosition = false;
                goal.HasTarget = true;
            }
            if(goal.CaptureRotation)
            {
                goal.Local.Rotation = local.Rotation;
                goal.CaptureRotation = false;
            }
        }

        if(goal.PendingWorldPosition is Vector3 worldPosition)
        {
            goal.PendingWorldPosition = null;
            var modelPosition = skeleton.WorldToModelPoint(worldPosition);
            if(MathUtil.IsFinite(modelPosition))
            {
                goal.Local.Position = space.PointToLocal(modelPosition);
                goal.HasTarget = true;
            }
        }

        if(goal.PendingWorldRotation is Quaternion worldRotation)
        {
            goal.PendingWorldRotation = null;
            var modelRotation = skeleton.WorldToModelRotation(worldRotation);
            if(MathUtil.IsFinite(modelRotation))
            {
                goal.Local.Rotation = Quaternion.Normalize(Quaternion.Inverse(space.Rotation) * modelRotation);
                goal.LockRotation = true;
            }
        }
    }

    private readonly List<(int Bone, int Partner, MirrorMode Mode)> _mirrorBones = [];
    private readonly Dictionary<int, Quaternion> _mirrorAnim = [];
    private readonly Dictionary<int, Quaternion> _mirrorAdds = [];

    // How far a bone turned from the animation's pose this frame (model axes).
    private Quaternion MirrorDelta(in ActorSkeleton skeleton, int bone)
        => bone >= 0 && _mirrorAnim.TryGetValue(bone, out var anim)
            ? Quaternion.Normalize(skeleton.GetModelFrame(bone).Rotation * Quaternion.Inverse(anim))
            : Quaternion.Identity;

    private static bool SwingsLimb(string name)
        => name.StartsWith("j_sako", StringComparison.Ordinal) || name.StartsWith("j_ude_a", StringComparison.Ordinal) || name.StartsWith("j_asi_a", StringComparison.Ordinal);

    // ---- Deform helpers: the shoulder (n_hkata), elbow (n_hhiji) and forearm twist (n_hte) bones hang from the
    // bone they help, so turning that bone carried them all the way, where the animation had them part of the
    // way; the shoulder then pinched and stretched. They get back their share (half) of the change.
    private static readonly (string Helper, string Owner, string? Twist)[] HelperBones =
    [
        ("n_hkata", "j_ude_a", null),
        ("n_hhiji", "j_ude_b", null),
        ("n_hte", "j_ude_b", "j_te"),
    ];
    private const float HelperShare = 0.5f;

    private struct HelperState
    {
        public int Helper, Owner, Parent, Twist;
        public Quaternion HelperAnim, OwnerAnim, ParentAnim, TwistAnim;
    }
    private readonly List<HelperState> _helpers = [];

    private void CaptureHelpers(in ActorSkeleton skeleton)
    {
        var info = skeleton.Info;
        _helpers.Clear();
        foreach(var side in new[] { "_l", "_r" })
        {
            foreach(var (helper, owner, twist) in HelperBones)
            {
                int h = info.IndexOf(helper + side), o = info.IndexOf(owner + side);
                int p = o >= 0 ? info.ParentOf(o) : -1;
                int t = twist != null ? info.IndexOf(twist + side) : -1;
                if(h < 0 || o < 0 || p < 0 || (twist != null && t < 0))
                    continue;
                _helpers.Add(new HelperState
                {
                    Helper = h, Owner = o, Parent = p, Twist = t,
                    HelperAnim = skeleton.GetModelFrame(h).Rotation,
                    OwnerAnim = skeleton.GetModelFrame(o).Rotation,
                    ParentAnim = skeleton.GetModelFrame(p).Rotation,
                    TwistAnim = t >= 0 ? skeleton.GetModelFrame(t).Rotation : Quaternion.Identity,
                });
            }
        }
    }

    private void FixHelpers(in ActorSkeleton skeleton)
    {
        foreach(var s in _helpers)
        {
            if(s.Twist < 0)
            {
                // Half of the owner's turn at its joint.
                var parentNow = skeleton.GetModelFrame(s.Parent).Rotation;
                var before = Quaternion.Inverse(s.ParentAnim) * s.OwnerAnim;
                var now = Quaternion.Inverse(parentNow) * skeleton.GetModelFrame(s.Owner).Rotation;
                var change = Quaternion.Normalize(now * Quaternion.Inverse(before));
                var rest = Quaternion.Inverse(s.ParentAnim) * s.HelperAnim;
                skeleton.SetModelRotation(s.Helper, Quaternion.Normalize(parentNow * Quaternion.Slerp(Quaternion.Identity, change, HelperShare) * rest));
            }
            else
            {
                // Half of the hand's twist along the forearm.
                var ownerNow = skeleton.GetModelFrame(s.Owner).Rotation;
                var before = Quaternion.Inverse(s.OwnerAnim) * s.TwistAnim;
                var now = Quaternion.Inverse(ownerNow) * skeleton.GetModelFrame(s.Twist).Rotation;
                var change = Quaternion.Normalize(now * Quaternion.Inverse(before));
                var axis = Vector3.Transform(skeleton.GetModelPosition(s.Twist) - skeleton.GetModelPosition(s.Owner), Quaternion.Inverse(ownerNow));
                if(axis.LengthSquared() < 1e-10f)
                    continue;
                axis = Vector3.Normalize(axis);
                var along = axis * Vector3.Dot(new Vector3(change.X, change.Y, change.Z), axis);
                var twist = new Quaternion(along, change.W);
                if(twist.LengthSquared() < 1e-10f)
                    continue;
                twist = Quaternion.Normalize(twist);
                var rest = Quaternion.Inverse(s.OwnerAnim) * s.HelperAnim;
                skeleton.SetModelRotation(s.Helper, Quaternion.Normalize(ownerNow * Quaternion.Slerp(Quaternion.Identity, twist, HelperShare) * rest));
            }
        }
    }

    /// <summary>The same bone on the other side (j_te_l -> j_te_r), or null for a bone in the middle.</summary>
    public static string? OtherSide(string name)
    {
        if(name.EndsWith("_l", StringComparison.Ordinal))
            return name[..^2] + "_r";
        if(name.EndsWith("_r", StringComparison.Ordinal))
            return name[..^2] + "_l";
        return null;
    }

    // Every bone a goal turns (the bone and the joints its solver bends).
    private IEnumerable<string> MirrorSources(SkeletonInfo info, Goal goal)
    {
        var list = new List<string>();
        if(goal.TurnOnly)
        {
            list.Add(goal.Bone);
            return list;
        }
        if(goal.Solve == GoalSolve.TwoBone && goal.UpperBone != null && goal.MiddleBone != null)
        {
            int upper = info.IndexOf(goal.UpperBone);
            int clavicle = upper >= 0 ? info.ParentOf(upper) : -1;
            if(clavicle >= 0 && info.Names[clavicle].StartsWith("j_sako", StringComparison.Ordinal))
                list.Add(info.Names[clavicle]);
            list.Add(goal.UpperBone);
            list.Add(goal.MiddleBone);
        }
        else if(goal.Solve == GoalSolve.Chain)
        {
            int bone = info.IndexOf(goal.Bone);
            if(bone >= 0)
            {
                Solver.CollectChain(info, bone, Math.Max(1, goal.ChainLength), _joints);
                for(int i = _joints.Count - 1; i >= 0; i--)
                    list.Add(info.Names[_joints[i]]);
            }
        }
        list.Add(goal.Bone);
        return list;
    }

    // How far a held head (or other chain end) may turn away from the bone above it.
    private const float MaxHeldTwist = 80f * MathF.PI / 180f;

    private bool Apply(in ActorSkeleton skeleton, Goal goal, int bone, Frame target)
    {
        var info = skeleton.Info;

        // Only turned: on top of the bone above it, nothing above bends (so turning the neck doesn't drag the
        // chest, and a turned head turns along with the neck).
        if(goal.TurnOnly)
        {
            if(goal.LockRotation)
                skeleton.SetModelRotation(bone, target.Rotation);
            return true;
        }

        switch(goal.Solve)
        {
            case GoalSolve.TwoBone:
            {
                int upper = goal.UpperBone != null ? info.IndexOf(goal.UpperBone) : -1;
                int middle = goal.MiddleBone != null ? info.IndexOf(goal.MiddleBone) : -1;
                bool solved;
                if(upper >= 0 && middle >= 0)
                {
                    ShoulderAssist(skeleton, upper, middle, bone, target.Position);
                    solved = Solver.SolveTwoBone(skeleton, upper, middle, bone, target.Position, goal.BendHint);
                }
                else
                {
                    // Not a regular arm or leg (unusual skeleton): bend the two joints above it instead.
                    Solver.CollectChain(info, bone, 2, _joints);
                    solved = Solver.SolveChain(skeleton, _joints, bone, target.Position, 12, 0.6f);
                }

                if(solved && goal.LockRotation)
                    skeleton.SetModelRotation(bone, target.Rotation);
                return solved;
            }

            case GoalSolve.Chain:
            {
                // Only bend the joints above when the bone has somewhere to go: a bone that's just being turned
                // (head, neck) turns on its own, without dragging the chest and shoulders around.
                bool solved = true;
                if(Vector3.Distance(skeleton.GetModelPosition(bone), target.Position) > 0.002f)
                {
                    Solver.CollectChain(info, bone, Math.Max(1, goal.ChainLength), _joints);
                    solved = Solver.SolveChain(skeleton, _joints, bone, target.Position, 12, 0.5f);
                }
                if(goal.LockRotation)
                {
                    // Held rotation, but never twisted so far from the bone above that the mesh tears (neck).
                    var following = skeleton.GetModelFrame(bone).Rotation;
                    skeleton.SetModelRotation(bone, Solver.LimitHeldRotation(target.Rotation, following, MaxHeldTwist));
                }
                return solved || goal.LockRotation;
            }

            case GoalSolve.Rigid:
            default:
            {
                // Hips on their own: only the pelvis and legs move (the legs go with them). The chest, arms and head
                // stay; the waist bends (and stretches where bending can't reach). Pin the feet to keep them put.
                bool alone = _config.HipsMoveAlone && info.Names[bone] == "j_kosi";
                int spineA = alone ? info.IndexOf("j_sebo_a") : -1, spineB = alone ? info.IndexOf("j_sebo_b") : -1;
                if(spineA < 0 || spineB < 0)
                    alone = false;
                Frame ribs = default;
                if(alone) ribs = skeleton.GetModelFrame(spineB);

                // Not alone: move the body's centre helper (n_hara) so nothing hanging from it is left behind.
                int parent = info.ParentOf(bone);
                var hipsBefore = skeleton.GetModelPosition(bone);
                if(!alone && parent >= 0 && info.Names[parent] == "n_hara")
                {
                    var delta = target.Position - hipsBefore;
                    if(MathUtil.IsFinite(delta))
                        skeleton.SetModelPosition(parent, skeleton.GetModelPosition(parent) + delta);
                }

                skeleton.SetModelPosition(bone, target.Position);
                if(goal.LockRotation)
                    skeleton.SetModelRotation(bone, target.Rotation);

                if(alone)
                {
                    var moved = skeleton.GetModelPosition(bone) - hipsBefore;
                    if(MathUtil.IsFinite(moved))
                    {
                        // The spine starts at the hips again, the lower spine bends toward the ribcage, the ribcage
                        // (and everything on it) keeps its pose; what bending can't take is shared by the waist.
                        skeleton.SetModelPosition(spineA, skeleton.GetModelPosition(spineA) + moved);
                        var a = skeleton.GetModelPosition(spineA);
                        var aim = MathUtil.FromTo(skeleton.GetModelPosition(spineB) - a, ribs.Position - a);
                        skeleton.SetModelRotation(spineA, Quaternion.Normalize(aim * skeleton.GetModelFrame(spineA).Rotation));
                        skeleton.SetModelRotation(spineB, ribs.Rotation);
                        var gap = ribs.Position - skeleton.GetModelPosition(spineB);
                        skeleton.SetModelPosition(spineA, skeleton.GetModelPosition(spineA) + gap * 0.5f);
                        skeleton.SetModelPosition(spineB, ribs.Position);
                        skeleton.SetModelRotation(spineB, ribs.Rotation);
                    }
                }
                return true;
            }
        }
    }

    // Shoulders join in: when the hand reaches far out or up, the collarbone turns toward it (up to ~15 degrees),
    // like a real shoulder, instead of the arm stretching straight out of a fixed joint.
    private static void ShoulderAssist(in ActorSkeleton skeleton, int upper, int middle, int end, Vector3 target)
    {
        var info = skeleton.Info;
        int clavicle = info.ParentOf(upper);
        if(clavicle < 0 || !info.Names[clavicle].StartsWith("j_sako", StringComparison.Ordinal))
            return;
        var s = skeleton.GetModelPosition(clavicle);
        var a = skeleton.GetModelPosition(upper);
        float reach = Vector3.Distance(a, skeleton.GetModelPosition(middle)) + Vector3.Distance(skeleton.GetModelPosition(middle), skeleton.GetModelPosition(end));
        if(reach < 1e-4f)
            return;
        float far = Math.Clamp((Vector3.Distance(target, a) - 0.75f * reach) / (0.35f * reach), 0f, 1f);
        float high = Math.Clamp((target.Y - a.Y) / reach, 0f, 1f);
        float amount = MathF.Max(far, high * 0.8f);
        if(amount <= 0f)
            return;
        var from = a - s;
        var to = target - s;
        if(from.LengthSquared() < 1e-10f || to.LengthSquared() < 1e-10f)
            return;
        var turn = MathUtil.FromTo(Vector3.Normalize(from), Vector3.Normalize(to));
        float angle = 2f * MathF.Acos(Math.Clamp(MathF.Abs(turn.W), 0f, 1f));
        float max = 15f * MathF.PI / 180f;
        float part = angle > 1e-5f ? MathF.Min(1f, max * amount / angle) : 0f;
        turn = Quaternion.Slerp(Quaternion.Identity, turn, part);
        skeleton.SetModelRotation(clavicle, Quaternion.Normalize(turn * skeleton.GetModelFrame(clavicle).Rotation));
    }

    // The frame (in this character's model space) the goal's target is held in.
    private bool TryGetGoalSpace(ActorRig rig, in ActorSkeleton skeleton, Goal goal, int bone, out Frame space)
    {
        space = Frame.Identity;
        var info = skeleton.Info;

        switch(goal.Space)
        {
            case GoalSpace.Pinned:
                // Held still relative to the character's root (normally the model's origin; the Actor tab's
                // "keep while moving" offset turns and moves it, and pinned bones go along).
                if(info.Count > 0 && info.Parents[0] < 0)
                    space = skeleton.GetModelFrame(0);
                return true;

            case GoalSpace.FollowBody:
            {
                int anchor = GetAnchorBone(info, goal, bone);
                if(anchor >= 0)
                    space = skeleton.GetModelFrame(anchor);
                return true;
            }

            case GoalSpace.Attached:
                return TryGetAttachedSpace(rig, skeleton, goal, out space);
        }

        return false;
    }

    private bool TryGetAttachedSpace(ActorRig rig, in ActorSkeleton skeleton, Goal goal, out Frame space)
    {
        space = Frame.Identity;
        if(goal.ParentBone == null)
            return false;

        Frame parentWorld;
        if(goal.ParentActor == rig.Address)
        {
            if(!skeleton.TryGetDrawnWorldFrame(goal.ParentBone, _infoCache, out parentWorld))
                return false;
        }
        else
        {
            if(!ActorSkeleton.TryGet(goal.ParentActor, goal.ParentObjectIndex, _infoCache, out var parent))
                return false;
            if(!parent.TryGetDrawnWorldFrame(goal.ParentBone, _infoCache, out parentWorld))
                return false;
        }

        space = new Frame(skeleton.WorldToModelPoint(parentWorld.Position), skeleton.WorldToModelRotation(parentWorld.Rotation));
        return MathUtil.IsFinite(space.Position);
    }

    // "Follow body" goals hang from the bone just above the part of the body they bend.
    private int GetAnchorBone(SkeletonInfo info, Goal goal, int bone)
    {
        if(goal.TurnOnly)
            return info.ParentOf(bone);

        switch(goal.Solve)
        {
            case GoalSolve.TwoBone:
            {
                int upper = goal.UpperBone != null ? info.IndexOf(goal.UpperBone) : -1;
                if(upper >= 0)
                    return info.ParentOf(upper);

                Solver.CollectChain(info, bone, 2, _joints);
                return _joints.Count > 0 ? info.ParentOf(_joints[^1]) : info.ParentOf(bone);
            }

            case GoalSolve.Chain:
            {
                Solver.CollectChain(info, bone, Math.Max(1, goal.ChainLength), _joints);
                return _joints.Count > 0 ? info.ParentOf(_joints[^1]) : info.ParentOf(bone);
            }

            default:
                return info.ParentOf(bone);
        }
    }

    private void TakeSnapshot(nint address, in ActorSkeleton skeleton)
    {
        var info = skeleton.Info;
        var names = new List<string>(info.Count + 64);
        var parents = new List<int>(info.Count + 64);
        var parts = new List<int>(info.Count + 64);
        var positions = new List<Vector3>(info.Count + 64);
        var rotations = new List<Quaternion>(info.Count + 64);
        var extra = new Dictionary<string, int>();

        for(int i = 0; i < info.Count; i++)
        {
            var world = skeleton.GetWorldFrame(i);
            names.Add(info.Names[i]);
            parents.Add(info.Parents[i]);
            parts.Add(0);
            positions.Add(world.Position);
            rotations.Add(world.Rotation);
        }

        // The extra skeletons: their own bones (copies of body bones they're attached by are left out, except
        // body mod bones, whose drawn copy can live there).
        for(int p = 1; p < skeleton.PartialCount; p++)
        {
            var partInfo = skeleton.PartialInfo(p, _infoCache);
            if(partInfo == null)
                continue;
            var map = new int[partInfo.Count];
            for(int b = 0; b < partInfo.Count; b++)
            {
                var name = partInfo.Names[b];
                int body = info.IndexOf(name);
                if(body >= 0 || extra.ContainsKey(name))
                {
                    map[b] = body >= 0 ? body : extra[name];
                    continue;
                }
                int parent = partInfo.Parents[b];
                int combinedParent = parent >= 0 ? map[parent] : skeleton.PartialParentBone(p);
                var model = skeleton.GetPartialFrame(p, b);
                int index = names.Count;
                map[b] = index;
                extra[name] = index;
                names.Add(name);
                parents.Add(combinedParent);
                parts.Add(p);
                positions.Add(skeleton.ModelToWorldPoint(model.Position));
                rotations.Add(skeleton.ModelToWorldRotation(model.Rotation));
            }
        }

        _snapshots[address] = new BoneSnapshot
        {
            Actor = address,
            Info = info,
            WorldPositions = positions.ToArray(),
            WorldRotations = rotations.ToArray(),
            RootRotation = skeleton.RootRotation,
            RootPosition = skeleton.RootPosition,
            Scale = skeleton.RootScale.Y,
            Names = names.ToArray(),
            Parents = parents.ToArray(),
            Parts = parts.ToArray(),
            Extra = extra,
        };
        _snapshotted.Add(address);
    }

    public void Dispose()
    {
        _framework.Update -= OnFrameworkUpdate;
    }
}
