using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin.Services;
using Limber.Core;
using GameObjectManager = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObjectManager;
using SceneObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object;
using SceneObjectType = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.ObjectType;

namespace Limber.Rig;

/// <summary>Body > Body physics > "The game's own physics": settings (each part off by default).</summary>
public sealed class GamePhysicsSettings
{
    /// <summary>Who the character options below apply to: 0 = GPose actors and Limber's actors, 1 = everyone.</summary>
    public int Who { get; set; }

    /// <summary>The game's physics runs at the right speed below 60 fps and for parts with their own rate.</summary>
    public bool FrameRateFix { get; set; }
    /// <summary>At most this many physics steps in one frame (after a slow frame).</summary>
    public int MaxSteps { get; set; } = 4;

    /// <summary>Hair, clothes and body physics always fully on, whatever the game's physics settings say.</summary>
    public bool ForceFull { get; set; }

    /// <summary>Limber's posing (spine, chest, hips) moves the game's physics (jiggle follows Limber's motion).</summary>
    public bool PoseBeforePhysics { get; set; }

    /// <summary>Less buzzing where hair, clothes and breasts touch the body.</summary>
    public bool AntiBuzz { get; set; }
    public float HitKeep { get; set; } = 0.7f;     // chain +0xD4: share of speed kept after touching a collider
    public float HitFriction { get; set; } = 0.2f; // chain +0xD0
    public float HitPush { get; set; } = 0.6f;     // chain +0xD8: how far one step pushes out
    public int CollisionLoops { get; set; } = 2;   // sim +0x6E (only where collisions are on)
    public float BuzzMaxSpeed { get; set; } = 4f;  // chain +0xE0

    /// <summary>Jiggle settings for body-mod bones (breasts, butt, belly) and the game's own chest physics.</summary>
    public bool Jiggle { get; set; }
    public string JigglePreset { get; set; } = "Soft";
    public float Follow { get; set; } = 0.15f;      // sim +0x68 (the game's 0.3)
    public int ConstraintLoops { get; set; } = 3;   // sim +0x6C
    public float JiggleDamping { get; set; } = 0.8f; // multiplier 4
    public float JiggleAttract { get; set; } = 0.8f; // multiplier 5
    public float JiggleMaxSpeed { get; set; } = 4f;  // chain +0xE0
    public bool JiggleChest { get; set; } = true;    // the game's own chest group (7) too

    /// <summary>Turning the character swings hair, clothes and jiggle bones (the game gives turning no inertia).</summary>
    public bool TurnInertia { get; set; }
    public float TurnStrength { get; set; } = 1f;
    public float TurnMaxDegrees { get; set; } = 10f;

    /// <summary>The game's physics starts again from the pose when a character jumps far in one frame.</summary>
    public bool AutoReset { get; set; }

    /// <summary>Named jiggle presets: follow, constraint loops, damping, attract, max speed.</summary>
    public static readonly (string Name, float Follow, int Loops, float Damping, float Attract, float MaxSpeed)[] Presets =
    [
        ("Subtle", 0.40f, 3, 1.3f, 1.2f, 3f),
        ("Soft", 0.15f, 3, 0.8f, 0.8f, 4f),
        ("Bouncy", 0.08f, 4, 0.5f, 0.6f, 6f),
    ];

    public void UsePreset(string name)
    {
        foreach(var p in Presets)
            if(p.Name == name)
            {
                JigglePreset = name;
                Follow = p.Follow;
                ConstraintLoops = p.Loops;
                JiggleDamping = p.Damping;
                JiggleAttract = p.Attract;
                JiggleMaxSpeed = p.MaxSpeed;
            }
    }
}

/// <summary>
/// Improvements to the GAME's own bone physics (the .phyb chains: hair, clothes, the body-mod "iv_" breast / butt /
/// belly bones). All writes happen in a stage run just BEFORE the game's bone physics update (BonePhysicsUpdater::
/// Update), when no physics worker thread is running. Offsets read from the 2026.10.16 executable:
///   CharacterBase +0x158 BonePhysicsModule; module +0xA0: 5 x StdVector&lt;BoneSimulator*&gt; (stride 0x18),
///   +0x1B8 frame time (smoothed), +0x588 the game's own "override time" flag.
///   BoneSimulator: +0x10 group, +0x50 step, +0x54 1/step, +0x68 follow, +0x6C constraint loops (u16),
///   +0x6E collision loops (u16), +0x70 extra steps this frame (s32, the game zeroes it after Update),
///   +0x78 StdVector&lt;Chain*&gt;, +0x108 13 multipliers (4 damping, 5 attract), +0x441 / +0x445 / +0x44D mode bytes,
///   +0x444 simulating, +0x446 collidable, +0x44A fading in, +0x44B fading out, +0x44C reset pending.
///   Chain: +0x20 node state (stride 0x60: pos +0, prev +0x10, inverse mass +0x54), +0xE8 node count (u16),
///   +0xD0 friction, +0xD4 1 - collision damping, +0xD8 repulsion, +0xE0 max speed.
/// </summary>
public sealed unsafe class GamePhysics : IDisposable
{
    // ResetPhysicsGroups(CharacterBase*, uint groupMask, bool children, int extraSteps) - research §0b.
    public const string ResetSignature = "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 41 54 41 55 41 56 41 57 48 83 EC ?? 41 8B D9 45 0F B6 E8 44 8B E2";
    // BoneSimulator::StartSimulation(sim, fadeSeconds) - starts with "cmp byte [rcx+0x444], 0".
    public const string StartSignature = "80 B9 44 04 00 00 00 0F 28 D1 74 ?? 80 B9 4A 04 00 00 00";
    // "cmp [rip+X], bl" on the game's ForceOverrideSimulationTime byte (the one Rebound sets).
    public const string ReboundFlagSignature = "38 1D ?? ?? ?? ?? F3 0F 10 3D";
    // BoneSimulator::Update (research §0b): only looked at, to notice another plugin hooking it (frame limiters do).
    public const string SimulatorUpdateSignature = "40 55 53 56 41 57 48 8D AC 24 ?? ?? ?? ?? 48 81 EC ?? ?? ?? ?? 44 0F 29 94 24";

    private const int ModulePtr = 0x158, Simulators = 0xA0, ModuleFrameTime = 0x1B8, ModuleOverride = 0x588;
    private const int SimGroup = 0x10, SimStep = 0x50, SimStepInv = 0x54, SimFollow = 0x68, SimConstraintLoop = 0x6C,
        SimCollisionLoop = 0x6E, SimExtraSteps = 0x70, SimChains = 0x78, SimMultipliers = 0x108,
        SimMode441 = 0x441, SimSimulating = 0x444, SimIntegrating = 0x445, SimCollidable = 0x446, SimFadingIn = 0x44A,
        SimFadingOut = 0x44B, SimResetPending = 0x44C, SimMode44D = 0x44D;
    private const int ChainNodes = 0x20, ChainFriction = 0xD0, ChainHitKeep = 0xD4, ChainRepulsion = 0xD8, ChainMaxSpeed = 0xE0,
        ChainNodeCount = 0xE8, NodeStride = 0x60, NodePrev = 0x10, NodeInvMass = 0x54;

    private static readonly uint[] BodyGroups = [110, 160, 190, 191, 192];
    private static readonly string[] PoseBones = ["j_kosi", "j_sebo_a", "j_sebo_b", "j_sebo_c"];

    public static GamePhysics? Current { get; private set; }

    private readonly IPluginLog _log;
    private readonly Func<GamePhysicsSettings> _settings;
    private readonly delegate* unmanaged<nint, uint, byte, int, void> _reset;
    private readonly delegate* unmanaged<nint, float, void> _start;
    private readonly byte* _reboundFlag;
    private readonly byte* _simulatorUpdate; // null: not found (already hooked by something, or a game patch)

    /// <summary>Whether Limber as a whole is on (set by the plugin). Off: everything Limber changed is put back.</summary>
    public Func<bool> LimberOn = () => true;

    /// <summary>Something that couldn't be set up (shown on the page).</summary>
    public string Problem { get; private set; } = "";
    /// <summary>What the frame-rate fix is doing (shown on the page).</summary>
    public string FrameRateStatus { get; private set; } = "";
    /// <summary>Another plugin that fixes the physics frame rate (found on the framework thread), or null.</summary>
    private volatile string? _otherFix;
    private DateTime _nextPluginCheck = DateTime.MinValue;

    public bool CanReset => _reset != null;
    public bool CanForce => _start != null;

    // ---- What Limber changed, to put back

    private sealed class SimSave
    {
        public nint Module;
        public uint Group;
        public float Follow, Damping, Attract;
        public ushort ConstraintLoop, CollisionLoop;
        public byte B441, B445, B44D;
        public nint[] Chains = [];
        public (float Friction, float Keep, float Repulsion, float MaxSpeed)[] ChainValues = [];
        public long Seen;
        public bool Forced, Buzz, Jiggle; // what Limber has written now (put back when it goes off)
    }

    private readonly Dictionary<nint, SimSave> _saved = new();
    private readonly Dictionary<nint, (float Acc, int Written)> _steps = new();
    private readonly Dictionary<nint, (Quaternion Rotation, Vector3 Position, long Seen)> _last = new();
    private readonly ConcurrentQueue<nint> _resetRequests = new();
    private long _frame;

    // ---- Pose before physics

    private sealed class PoseState
    {
        public nint Address;
        public ushort Index;
        public int[] Bones = [];
        public Vector3[] AnimPos = new Vector3[PoseBones.Length];
        public Quaternion[] AnimRot = new Quaternion[PoseBones.Length];
        public Vector3[] DeltaPos = new Vector3[PoseBones.Length];
        public Quaternion[] DeltaRot = new Quaternion[PoseBones.Length];
        public bool Wrote, HaveDelta;
        public long AnimFrame, DeltaFrame;
    }

    private readonly Dictionary<nint, PoseState> _poses = new();
    private readonly SkeletonInfoCache _cache = new();

    public GamePhysics(ISigScanner scanner, IPluginLog log, Func<GamePhysicsSettings> settings)
    {
        _log = log;
        _settings = settings;
        Current = this;
        var problems = new List<string>();
        try
        {
            var reset = scanner.ScanAllText(ResetSignature);
            if(reset.Length == 1)
                _reset = (delegate* unmanaged<nint, uint, byte, int, void>)reset[0];
            else
                problems.Add($"\"Reset physics\" can't work (its pattern matched {reset.Length} places)");
            var start = scanner.ScanAllText(StartSignature);
            if(start.Length == 1)
                _start = (delegate* unmanaged<nint, float, void>)start[0];
            else
                problems.Add($"\"Full physics\" can't start physics that is off (its pattern matched {start.Length} places)");
            var flag = scanner.ScanAllText(ReboundFlagSignature);
            if(flag.Length == 1)
                _reboundFlag = (byte*)(flag[0] + 6 + *(int*)(flag[0] + 2));
            var update = scanner.ScanAllText(SimulatorUpdateSignature);
            if(update.Length == 1)
                _simulatorUpdate = (byte*)update[0];
        }
        catch(Exception e)
        {
            problems.Add("couldn't search the game for the physics functions (see /xllog)");
            _log.Error(e, "Limber: game physics signature scan failed");
        }
        Problem = problems.Count == 0 ? "" : string.Join("; ", problems) + ".";
    }

    /// <summary>Framework thread, every frame: looks for plugins that already fix the physics frame rate.</summary>
    public void Refresh()
    {
        if(DateTime.UtcNow < _nextPluginCheck)
            return;
        _nextPluginCheck = DateTime.UtcNow.AddSeconds(5);
        string? found = null;
        try
        {
            foreach(var p in Plugin.PluginInterface.InstalledPlugins)
                if(p.IsLoaded && (p.InternalName.Equals("Rebound", StringComparison.OrdinalIgnoreCase)
                    || p.InternalName.Contains("HighFps", StringComparison.OrdinalIgnoreCase)
                    || p.InternalName.Contains("HoliestFluffiness", StringComparison.OrdinalIgnoreCase)))
                    found = p.Name;
        }
        catch
        {
            // (the plugin list isn't important enough to fail over)
        }
        // Something hooked the game's physics step (a frame limiter, most likely): don't fix the speed twice.
        if(found == null && (_simulatorUpdate == null || *_simulatorUpdate == 0xE9 || *_simulatorUpdate == 0xFF))
            found = "another plugin (it changes the game's physics step)";
        _otherFix = found;
    }

    /// <summary>Reset the game's physics of one character (its address), or of every chosen character (0). Any thread.</summary>
    public void RequestReset(nint address) => _resetRequests.Enqueue(address);

    // ---- The stage before the game's bone physics

    /// <summary>Bone update, BEFORE the game's own update (no physics worker thread is running).</summary>
    public void BeforePhysics()
    {
        _frame++;
        var s = _settings();
        bool on = LimberOn();
        // (resets asked for while Limber is off are dropped, not done later on the next GPose)
        if(!on)
            while(_resetRequests.TryDequeue(out _)) { }
        bool any = on && (s.FrameRateFix || s.ForceFull || s.AntiBuzz || s.Jiggle || s.TurnInertia || s.PoseBeforePhysics || s.AutoReset
            || !_resetRequests.IsEmpty);
        if(!any && _saved.Count == 0 && _steps.Count == 0 && _last.Count == 0 && _poses.Count == 0)
        {
            FrameRateStatus = "";
            return;
        }

        // Reset requests (0 = every chosen character)
        bool resetAll = false;
        HashSet<nint>? resetThese = null;
        while(_resetRequests.TryDequeue(out var r))
        {
            if(r == 0)
                resetAll = true;
            else
                (resetThese ??= new()).Add(r);
        }

        float dt = 0f;
        var framework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance();
        if(framework != null)
            dt = MathF.Min(framework->FrameDeltaTime, 0.1f);
        bool rebound = _reboundFlag != null && *_reboundFlag != 0;
        string? other = rebound ? "Rebound" : _otherFix;
        bool fixRate = on && s.FrameRateFix && other == null && dt > 0f && float.IsFinite(dt);
        if(!on || !s.FrameRateFix)
            FrameRateStatus = "";
        else if(other != null)
            FrameRateStatus = $"Off: {other} already fixes the physics speed.";
        int fixedSims = 0, nativeSims = 0;

        var manager = GameObjectManager.Instance();
        if(manager == null)
            return;
        var objects = manager->Objects.IndexSorted;
        for(int index = 0; index < objects.Length; index++)
        {
            var obj = objects[index].Value;
            if(obj == null)
                continue;
            var draw = obj->DrawObject;
            if(draw == null || ((SceneObject*)draw)->GetObjectType() != SceneObjectType.CharacterBase)
                continue;
            nint cb = (nint)draw;
            nint module = *(nint*)(cb + ModulePtr);
            if(module == 0)
                continue;
            bool chosen = on && (s.Who == 1 || index >= 200);
            nint address = (nint)obj;

            // This character's place and turn (the skeleton as drawn): turning inertia and auto reset.
            var skeleton = ((FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase*)cb)->Skeleton;
            Quaternion rotation = Quaternion.Identity, turn = Quaternion.Identity;
            Vector3 position = default;
            bool haveTurn = false, jumped = false;
            if(skeleton != null)
            {
                rotation = Quaternion.Normalize(skeleton->Transform.Rotation);
                position = skeleton->Transform.Position;
                if(chosen && (s.TurnInertia || s.AutoReset) && MathUtil.IsFinite(rotation) && MathUtil.IsFinite(position))
                {
                    if(_last.TryGetValue(cb, out var last) && last.Seen == _frame - 1)
                    {
                        jumped = Vector3.Distance(last.Position, position) > 1.5f;
                        var delta = Quaternion.Normalize(Quaternion.Inverse(rotation) * last.Rotation);
                        float angle = 2f * MathF.Acos(Math.Clamp(MathF.Abs(delta.W), 0f, 1f));
                        if(s.TurnInertia && !jumped && angle > 1e-4f && angle < MathF.PI / 3f)
                        {
                            float strength = Math.Clamp(s.TurnStrength, 0f, 2f);
                            float max = Math.Clamp(s.TurnMaxDegrees, 1f, 45f) * MathF.PI / 180f;
                            float use = MathF.Min(angle * strength, max);
                            if(delta.W < 0f)
                                delta = Quaternion.Negate(delta);
                            turn = Quaternion.Slerp(Quaternion.Identity, delta, use / angle);
                            haveTurn = MathUtil.IsFinite(turn) && use > 1e-4f;
                        }
                    }
                    _last[cb] = (rotation, position, _frame);
                }
            }

            if(chosen && _reset != null && (resetAll || (resetThese?.Contains(address) ?? false) || (s.AutoReset && jumped)))
            {
                _reset(cb, 0xFFFFFFFF, 1, 4);
                haveTurn = false;
            }

            bool adult = Limber.Koi.Safety.AdultAllowed(address);
            for(int slot = 0; slot < 5; slot++)
            {
                nint* begin = *(nint**)(module + Simulators + slot * 0x18);
                nint* end = *(nint**)(module + Simulators + slot * 0x18 + 8);
                if(begin == null || end < begin || end - begin > 128)
                    continue;
                for(nint* at = begin; at < end; at++)
                {
                    nint sim = *at;
                    if(sim == 0)
                        continue;
                    uint group = *(uint*)(sim + SimGroup);

                    // 2. Full physics
                    bool force = chosen && s.ForceFull && group < 28;
                    SimSave? save = null;
                    if(force)
                    {
                        save = Save(sim, module, group);
                        if(!save.Forced)
                        {
                            // (the game's mode as it is now, to put back when this goes off)
                            save.B441 = *(byte*)(sim + SimMode441);
                            save.B445 = *(byte*)(sim + SimIntegrating);
                            save.B44D = *(byte*)(sim + SimMode44D);
                        }
                        if(_start != null && (*(byte*)(sim + SimSimulating) == 0 || *(byte*)(sim + SimFadingOut) == 1))
                        {
                            *(byte*)(sim + SimSimulating) = 1;
                            _start(sim, 1f / 3f);
                        }
                        *(byte*)(sim + SimIntegrating) = 1;
                        *(byte*)(sim + SimMode44D) = 0;
                        *(byte*)(sim + SimMode441) = 0;
                    }

                    // 4. / 5. Collision and jiggle settings
                    bool buzz = chosen && s.AntiBuzz && *(byte*)(sim + SimCollidable) != 0;
                    bool jiggle = chosen && s.Jiggle && adult && (Array.IndexOf(BodyGroups, group) >= 0 || (s.JiggleChest && group == 7));
                    if(buzz || jiggle)
                        save ??= Save(sim, module, group);
                    if(save != null)
                    {
                        save.Seen = _frame;
                        Apply(sim, save, force, buzz, jiggle, s);
                    }
                    else if(_saved.TryGetValue(sim, out var old))
                    {
                        if(old.Module == module && old.Group == group && SameChains(sim, old))
                            Restore(sim, old);
                        _saved.Remove(sim);
                    }

                    bool simulating = *(byte*)(sim + SimSimulating) != 0 && *(byte*)(sim + SimIntegrating) != 0;
                    bool resetting = *(byte*)(sim + SimResetPending) != 0;

                    // 1. Frame rate
                    if(fixRate && simulating)
                    {
                        int r = FixSteps(sim, module, dt, s);
                        if(r > 0) fixedSims++;
                        else if(r < 0) nativeSims++;
                    }
                    else
                        _steps.Remove(sim);

                    // 6. Turning inertia
                    if(haveTurn && simulating && !resetting && *(byte*)(sim + SimFadingIn) == 0)
                        Turn(sim, turn);
                }
            }

            // 3. Pose before physics
            if(chosen && s.PoseBeforePhysics)
                PoseBefore(address, (ushort)index);
        }

        if(fixRate)
            FrameRateStatus = fixedSims == 0 && nativeSims > 0
                ? "On (nothing to fix right now: above 60 fps the game already steps by the frame time)."
                : $"On: {fixedSims} physics parts stepped at their own rate" + (nativeSims > 0 ? $", {nativeSims} already right." : ".");

        // Forget what's gone (characters unloaded: nothing to put back), and what wasn't seen this frame.
        if((_frame & 63) == 0)
        {
            Prune(_saved, v => v.Seen < _frame - 2);
            PruneSteps();
            var gone = new List<nint>();
            foreach(var (k, v) in _last)
                if(v.Seen < _frame - 2)
                    gone.Add(k);
            foreach(var k in gone)
                _last.Remove(k);
            var stale = new List<nint>();
            foreach(var (k, v) in _poses)
                if(v.AnimFrame < _frame - 2 && !v.Wrote)
                    stale.Add(k);
            foreach(var k in stale)
                _poses.Remove(k);
        }
    }

    private static void Prune(Dictionary<nint, SimSave> d, Func<SimSave, bool> old)
    {
        var gone = new List<nint>();
        foreach(var (k, v) in d)
            if(old(v))
                gone.Add(k);
        foreach(var k in gone)
            d.Remove(k);
    }

    private readonly HashSet<nint> _stepsSeen = new();

    private void PruneSteps()
    {
        var gone = new List<nint>();
        foreach(var k in _steps.Keys)
            if(!_stepsSeen.Contains(k))
                gone.Add(k);
        foreach(var k in gone)
            _steps.Remove(k);
        _stepsSeen.Clear();
    }

    // ---- 1. Frame rate: a time accumulator per simulator; the steps the game would take, corrected through the
    // game's own "extra steps this frame" field (+0x70, which its Reset uses too and Update zeroes afterwards).
    // Returns 1 when it corrected, -1 when the game already steps by the frame time, 0 when skipped.
    private int FixSteps(nint sim, nint module, float dt, GamePhysicsSettings s)
    {
        _stepsSeen.Add(sim);
        int current = *(int*)(sim + SimExtraSteps);
        _steps.TryGetValue(sim, out var state);
        // Something else asked for steps this frame (a reset, another plugin): leave it alone.
        if(*(byte*)(sim + SimResetPending) != 0 || (current != 0 && current != state.Written))
        {
            _steps[sim] = (0f, 0);
            return 0;
        }
        if(*(byte*)(module + ModuleOverride) != 0)
        {
            _steps[sim] = (0f, 0);
            return 0;
        }
        float h = *(float*)(sim + SimStep), inv = *(float*)(sim + SimStepInv);
        if(!(h > 1e-4f && h < 0.5f && inv > 1f && float.IsFinite(inv)))
            return 0;
        // The game steps by its (smoothed) frame time itself for 60 Hz parts above 60 fps: nothing to fix there.
        float frameTime = *(float*)(module + ModuleFrameTime);
        if(MathF.Abs(inv - 60f) <= 1f && frameTime > 1e-5f && 1f / frameTime > 60f)
        {
            if(current == state.Written && current != 0)
                *(int*)(sim + SimExtraSteps) = 0;
            _steps.Remove(sim);
            return -1;
        }
        // What the game will do: max(ceil(dt / h - 0.5), 1) steps of h.
        int game = Math.Max((int)MathF.Ceiling(inv * dt - 0.5f), 1);
        int cap = Math.Clamp(s.MaxSteps, 1, 8);
        float acc = state.Acc + dt;
        int want = Math.Clamp((int)MathF.Floor(acc / h + 1e-3f), 0, cap);
        // (never below -1: the game takes at least one step, so it never ends up with a negative count)
        int extra = Math.Clamp(want - game, -1, cap);
        int taken = game + extra;
        acc = Math.Clamp(acc - taken * h, -h, h);
        *(int*)(sim + SimExtraSteps) = extra;
        _steps[sim] = (acc, extra);
        return 1;
    }

    // ---- 4. / 5. Writing and putting back the simulator's and its chains' values

    private SimSave Save(nint sim, nint module, uint group)
    {
        nint* begin = *(nint**)(sim + SimChains);
        nint* end = *(nint**)(sim + SimChains + 8);
        int count = begin == null || end < begin || end - begin > 256 ? 0 : (int)(end - begin);
        if(_saved.TryGetValue(sim, out var save) && save.Module == module && save.Group == group && SameChains(sim, save))
            return save;
        // (a saved record for a simulator that was freed and another one made at the same address: its chains are
        // gone, so nothing is written back into them; the new one's values are saved fresh)
        save = new SimSave
        {
            Module = module,
            Group = group,
            Follow = *(float*)(sim + SimFollow),
            ConstraintLoop = *(ushort*)(sim + SimConstraintLoop),
            CollisionLoop = *(ushort*)(sim + SimCollisionLoop),
            Damping = *(float*)(sim + SimMultipliers + 4 * 4),
            Attract = *(float*)(sim + SimMultipliers + 5 * 4),
            B441 = *(byte*)(sim + SimMode441),
            B445 = *(byte*)(sim + SimIntegrating),
            B44D = *(byte*)(sim + SimMode44D),
            Chains = new nint[count],
            ChainValues = new (float, float, float, float)[count],
            Seen = _frame,
        };
        for(int i = 0; i < count; i++)
        {
            nint chain = begin[i];
            save.Chains[i] = chain;
            if(chain != 0)
                save.ChainValues[i] = (*(float*)(chain + ChainFriction), *(float*)(chain + ChainHitKeep), *(float*)(chain + ChainRepulsion), *(float*)(chain + ChainMaxSpeed));
        }
        _saved[sim] = save;
        return save;
    }

    // The simulator still has exactly the chains that were saved (a hair or gear change can free a simulator and make a
    // new one at the same address: writing the old chains' values then would write into freed memory).
    private static bool SameChains(nint sim, SimSave save)
    {
        nint* begin = *(nint**)(sim + SimChains);
        nint* end = *(nint**)(sim + SimChains + 8);
        int count = begin == null || end < begin || end - begin > 256 ? 0 : (int)(end - begin);
        if(count != save.Chains.Length)
            return false;
        for(int i = 0; i < count; i++)
            if(begin[i] != save.Chains[i])
                return false;
        return true;
    }

    private static void Apply(nint sim, SimSave save, bool force, bool buzz, bool jiggle, GamePhysicsSettings s)
    {
        // Each group of values is written only while its option is on, and put back once when it goes off (so the
        // game's own later changes to them are never overwritten with old values).
        if(force)
            save.Forced = true;
        else if(save.Forced)
        {
            RestoreModes(sim, save);
            save.Forced = false;
        }
        if(buzz)
        {
            ushort collisionLoop = save.CollisionLoop;
            if(collisionLoop >= 1)
                collisionLoop = (ushort)Math.Max(collisionLoop, Math.Clamp(s.CollisionLoops, 1, 8));
            *(ushort*)(sim + SimCollisionLoop) = collisionLoop;
        }
        else if(save.Buzz)
            *(ushort*)(sim + SimCollisionLoop) = save.CollisionLoop;
        if(jiggle)
        {
            *(float*)(sim + SimFollow) = Math.Clamp(s.Follow, 0f, 1f);
            *(ushort*)(sim + SimConstraintLoop) = (ushort)Math.Clamp(s.ConstraintLoops, 1, 8);
            *(float*)(sim + SimMultipliers + 4 * 4) = Math.Clamp(s.JiggleDamping, 0.1f, 3f);
            *(float*)(sim + SimMultipliers + 5 * 4) = Math.Clamp(s.JiggleAttract, 0.1f, 3f);
        }
        else if(save.Jiggle)
            RestoreJiggle(sim, save);
        bool chainsBefore = save.Buzz || save.Jiggle;
        save.Buzz = buzz;
        save.Jiggle = jiggle;
        if(!buzz && !jiggle && !chainsBefore)
            return;
        for(int i = 0; i < save.Chains.Length; i++)
        {
            nint chain = save.Chains[i];
            if(chain == 0)
                continue;
            var o = save.ChainValues[i];
            float friction = o.Friction, keep = o.Keep, repulsion = o.Repulsion, maxSpeed = o.MaxSpeed;
            if(buzz)
            {
                keep = Math.Clamp(s.HitKeep, 0f, 1f);
                friction = Math.Clamp(s.HitFriction, 0f, 1f);
                repulsion = Math.Clamp(s.HitPush, 0.1f, 1f);
                if(maxSpeed > 0f)
                    maxSpeed = MathF.Min(maxSpeed, Math.Clamp(s.BuzzMaxSpeed, 0.5f, 100f));
            }
            // (only where the chain has a speed limit of its own: 0 might mean something else)
            if(jiggle && o.MaxSpeed > 0f)
                maxSpeed = buzz ? MathF.Min(maxSpeed, Math.Clamp(s.JiggleMaxSpeed, 0.5f, 100f)) : Math.Clamp(s.JiggleMaxSpeed, 0.5f, 100f);
            *(float*)(chain + ChainFriction) = friction;
            *(float*)(chain + ChainHitKeep) = keep;
            *(float*)(chain + ChainRepulsion) = repulsion;
            *(float*)(chain + ChainMaxSpeed) = maxSpeed;
        }
    }

    private static void RestoreModes(nint sim, SimSave save)
    {
        *(byte*)(sim + SimMode441) = save.B441;
        *(byte*)(sim + SimIntegrating) = save.B445;
        *(byte*)(sim + SimMode44D) = save.B44D;
    }

    private static void RestoreJiggle(nint sim, SimSave save)
    {
        *(float*)(sim + SimFollow) = save.Follow;
        *(ushort*)(sim + SimConstraintLoop) = save.ConstraintLoop;
        *(float*)(sim + SimMultipliers + 4 * 4) = save.Damping;
        *(float*)(sim + SimMultipliers + 5 * 4) = save.Attract;
    }

    private static void Restore(nint sim, SimSave save)
    {
        if(save.Forced)
            RestoreModes(sim, save);
        if(save.Jiggle)
            RestoreJiggle(sim, save);
        if(save.Buzz)
            *(ushort*)(sim + SimCollisionLoop) = save.CollisionLoop;
        bool chains = save.Buzz || save.Jiggle;
        save.Forced = save.Buzz = save.Jiggle = false;
        if(!chains)
            return;
        nint* begin = *(nint**)(sim + SimChains);
        nint* end = *(nint**)(sim + SimChains + 8);
        int count = begin == null || end < begin ? 0 : (int)(end - begin);
        if(count != save.Chains.Length)
            return; // (the chains changed: nothing of ours left in them)
        for(int i = 0; i < count; i++)
        {
            nint chain = begin[i];
            if(chain == 0 || chain != save.Chains[i])
                continue;
            var o = save.ChainValues[i];
            *(float*)(chain + ChainFriction) = o.Friction;
            *(float*)(chain + ChainHitKeep) = o.Keep;
            *(float*)(chain + ChainRepulsion) = o.Repulsion;
            *(float*)(chain + ChainMaxSpeed) = o.MaxSpeed;
        }
    }

    // ---- 6. Turning inertia: each moving node's previous place turned by the character's turn this frame (in the
    // character's own space), so the node carries the speed of staying where it was in the world.
    private static void Turn(nint sim, Quaternion turn)
    {
        nint* begin = *(nint**)(sim + SimChains);
        nint* end = *(nint**)(sim + SimChains + 8);
        if(begin == null || end < begin || end - begin > 256)
            return;
        for(nint* at = begin; at < end; at++)
        {
            nint chain = *at;
            if(chain == 0)
                continue;
            int count = *(ushort*)(chain + ChainNodeCount);
            byte* nodes = *(byte**)(chain + ChainNodes);
            if(nodes == null || count <= 0 || count > 512)
                continue;
            for(int i = 0; i < count; i++)
            {
                byte* node = nodes + i * NodeStride;
                if(*(float*)(node + NodeInvMass) == 0f)
                    continue; // (pinned: the chain's root)
                var prev = (Vector3*)(node + NodePrev);
                var turned = Vector3.Transform(*prev, turn);
                if(MathUtil.IsFinite(turned))
                    *prev = turned;
            }
        }
    }

    // ---- 3. Pose before physics. The game's physics reads the spine / chest / hips as animated, before Limber poses
    // them, so Limber's posing never moved the jiggle. Here last frame's difference Limber made to those bones is put
    // on this frame's animation before the physics runs (children carried along), and taken off again right after it
    // (AfterPhysics), so every Limber stage then poses from the animation as always and nothing is turned twice.

    private void PoseBefore(nint address, ushort index)
    {
        if(!ActorSkeleton.TryGet(address, index, _cache, out var sk))
            return;
        if(!_poses.TryGetValue(address, out var p) || p.Index != index)
        {
            p = new PoseState { Address = address, Index = index };
            _poses[address] = p;
        }
        if(p.Bones.Length != PoseBones.Length)
        {
            var bones = new int[PoseBones.Length];
            for(int i = 0; i < bones.Length; i++)
                bones[i] = sk.Info.IndexOf(PoseBones[i]);
            p.Bones = bones;
        }
        for(int i = 0; i < p.Bones.Length; i++)
        {
            if(p.Bones[i] < 0)
                continue;
            var f = sk.GetModelFrame(p.Bones[i]);
            p.AnimPos[i] = f.Position;
            p.AnimRot[i] = f.Rotation;
        }
        p.AnimFrame = _frame;
        p.Wrote = false;
        if(!p.HaveDelta || p.DeltaFrame != _frame - 1)
            return;
        bool needed = false;
        for(int i = 0; i < p.Bones.Length; i++)
        {
            if(p.Bones[i] < 0)
                continue;
            float angle = 2f * MathF.Acos(Math.Clamp(MathF.Abs(p.DeltaRot[i].W), 0f, 1f));
            if(angle > 0.0035f || p.DeltaPos[i].LengthSquared() > 1e-6f)
                needed = true;
            if(angle > 2.2f || p.DeltaPos[i].LengthSquared() > 0.25f)
                return; // (a jump, not posing: leave this frame alone)
        }
        if(!needed)
            return;
        // Targets from the animation as it is now (all read before any is written), then written parents first.
        for(int i = 0; i < p.Bones.Length; i++)
        {
            if(p.Bones[i] < 0)
                continue;
            sk.SetModelRotation(p.Bones[i], Quaternion.Normalize(p.DeltaRot[i] * p.AnimRot[i]));
            sk.SetModelPosition(p.Bones[i], p.AnimPos[i] + p.DeltaPos[i]);
        }
        p.Wrote = true;
    }

    /// <summary>Bone update, first stage after the game's update: the animation's spine / chest / hips back.</summary>
    public void AfterPhysics()
    {
        if(_poses.Count == 0)
            return;
        foreach(var p in _poses.Values)
        {
            if(!p.Wrote)
                continue;
            p.Wrote = false;
            if(!ActorSkeleton.TryGet(p.Address, p.Index, _cache, out var sk))
                continue;
            for(int i = 0; i < p.Bones.Length; i++)
            {
                if(p.Bones[i] < 0)
                    continue;
                sk.SetModelRotation(p.Bones[i], p.AnimRot[i]);
                sk.SetModelPosition(p.Bones[i], p.AnimPos[i]);
            }
        }
    }

    /// <summary>Bone update, last stage: how far Limber moved the spine / chest / hips from the animation.</summary>
    public void EndOfFrame()
    {
        if(_poses.Count == 0)
            return;
        foreach(var p in _poses.Values)
        {
            if(p.AnimFrame != _frame || !ActorSkeleton.TryGet(p.Address, p.Index, _cache, out var sk))
            {
                p.HaveDelta = false;
                continue;
            }
            for(int i = 0; i < p.Bones.Length; i++)
            {
                if(p.Bones[i] < 0)
                    continue;
                var f = sk.GetModelFrame(p.Bones[i]);
                var d = Quaternion.Normalize(f.Rotation * Quaternion.Inverse(p.AnimRot[i]));
                p.DeltaRot[i] = MathUtil.IsFinite(d) ? d : Quaternion.Identity;
                var dp = f.Position - p.AnimPos[i];
                p.DeltaPos[i] = MathUtil.IsFinite(dp) ? dp : Vector3.Zero;
            }
            p.HaveDelta = true;
            p.DeltaFrame = _frame;
        }
    }

    // ---- Unloading: put back what Limber changed (framework thread, between the game's frames)

    public void Dispose()
    {
        if(Current == this)
            Current = null;
        try
        {
            if(_saved.Count == 0)
                return;
            var manager = GameObjectManager.Instance();
            if(manager == null)
                return;
            var objects = manager->Objects.IndexSorted;
            for(int index = 0; index < objects.Length; index++)
            {
                var obj = objects[index].Value;
                if(obj == null || obj->DrawObject == null || ((SceneObject*)obj->DrawObject)->GetObjectType() != SceneObjectType.CharacterBase)
                    continue;
                nint module = *(nint*)((nint)obj->DrawObject + ModulePtr);
                if(module == 0)
                    continue;
                for(int slot = 0; slot < 5; slot++)
                {
                    nint* begin = *(nint**)(module + Simulators + slot * 0x18);
                    nint* end = *(nint**)(module + Simulators + slot * 0x18 + 8);
                    if(begin == null || end < begin || end - begin > 128)
                        continue;
                    for(nint* at = begin; at < end; at++)
                        if(*at != 0 && _saved.TryGetValue(*at, out var save) && save.Module == module && save.Group == *(uint*)(*at + SimGroup) && SameChains(*at, save))
                            Restore(*at, save);
                }
            }
            _saved.Clear();
        }
        catch(Exception e)
        {
            _log.Warning(e, "Limber: couldn't put back the game's physics values");
        }
    }
}
