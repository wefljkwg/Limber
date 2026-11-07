using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Dalamud.Plugin.Services;
using Limber.Core;

namespace Limber.Rig;

/// <summary>Which way an emitter shoots.</summary>
public enum ClimaxAim
{
    AlongBone,        // the way its bone points (from the bone before it to it)
    CharacterForward, // the way its character faces
    AtTarget,         // arced to land on another character's bone
    World,            // a fixed direction in the world
}

/// <summary>How the liquid looks (every emitter's): drawn by <see cref="Limber.Gpu.FluidRenderer"/>.</summary>
public sealed class ClimaxLook
{
    public Vector3 Color { get; set; } = new(0.95f, 0.94f, 0.9f);
    public float Opacity { get; set; } = 1f;
    /// <summary>How much of what's behind shows through thin liquid (0 = milky, 1 = clear where thin).</summary>
    public float Translucency { get; set; } = 0.6f;
    /// <summary>See-through everywhere, thick or thin (0..1).</summary>
    public float Transparency { get; set; }
    /// <summary>A yellowish, creamy tint (0 = the colour as it is).</summary>
    public float Yellow { get; set; }
    /// <summary>Landed liquid: how far each drop spreads over the skin (times its size), and how deep it is there.</summary>
    public float CoatSpread { get; set; } = 1.4f;
    public float CoatHeight { get; set; } = 0.3f;
    /// <summary>How quickly it turns milky with thickness (per metre).</summary>
    public float Absorb { get; set; } = 150f;
    public float Refraction { get; set; } = 0.6f;
    /// <summary>The broad shine: sharpness and strength.</summary>
    public float Gloss { get; set; } = 25f;
    public float Shine { get; set; } = 0.12f;
    /// <summary>The small, sharp wet highlight.</summary>
    public float Gloss2 { get; set; } = 400f;
    public float Shine2 { get; set; } = 1.4f;
    public float Rim { get; set; } = 0.08f;
    public float Reflection { get; set; } = 0.06f;
    public float Ambient { get; set; } = 0.36f;
    /// <summary>How far light wraps round (higher = softer, flatter shading).</summary>
    public float Softness { get; set; } = 2.2f;
    /// <summary>Light passing through it: a soft, milky glow (not a metal's mirror shine).</summary>
    public float Subsurface { get; set; } = 0.12f;
    /// <summary>Experimental: drawn by the game itself (in a carrier model, by its path), and Limber's own drawing as well.</summary>
    public bool GameDrawn { get; set; }
    public string GameCarrier { get; set; } = "";
    public bool AlsoLimberDrawn { get; set; }
    /// <summary>The game-drawn surface: how big the drops are in it (times Limber's own drawing) and how thick liquid on skin is.</summary>
    public float GameDropSize { get; set; } = 0.5f;
    public float GameCoatSize { get; set; } = 0.5f;
    public float GameGloss { get; set; } = 0.8f;
    public int GameSmoothing { get; set; } = 2;
    /// <summary>The surface's shape: how far drops reach to melt together, how full it is, how strongly each smoothing pass
    /// pulls, how far a coat sits out of the skin, the flying drops' and strings' size (times Drop size), the finest detail.</summary>
    public float GameMelt { get; set; } = 2f;
    public float GameFullness { get; set; } = 1f;
    public float GameSmoothStrength { get; set; } = 0.5f;
    public float GameCoatOut { get; set; } = 0.5f;
    public float GameAirSize { get; set; } = 1f;
    public float GameStringSize { get; set; } = 1f;
    public float GameDetail { get; set; } = 0.001f;
    /// <summary>Its material: the shine map's four channels (what each does is up to the game's shader), and ripples in its
    /// surface (how strong, how big).</summary>
    public Vector4 GameShineMap { get; set; } = new(0.8f, 0.8f, 0.8f, 1f);
    public float GameRipples { get; set; } = 0.25f;
    public float GameRippleSize { get; set; } = 0.03f;
    /// <summary>The carriers used (model paths; empty: the biggest found, picked again), and how many to pick.</summary>
    public List<string> GameCarriers { get; set; } = new();
    public int GameCarrierCount { get; set; } = 6;
    /// <summary>Which way of picking carriers <see cref="GameCarriers"/> came from (picked again when that changes).</summary>
    public int GameCarriersPicked { get; set; }

    /// <summary>How much it's lit by the game's own lights (the light on the skin under it) instead of Limber's light.</summary>
    public float SceneLight { get; set; } = 0.5f;
    /// <summary>The light, from the camera's view: degrees right of it and above it.</summary>
    public float LightYaw { get; set; } = 30f;
    public float LightPitch { get; set; } = 45f;
    public float LightStrength { get; set; } = 0.45f;
    /// <summary>Smoothing: how far (metres) the drops are blended into one surface, and how many passes.</summary>
    public float Smoothing { get; set; } = 0.014f;
    /// <summary>Smoothing of the outline only (metres): rounds the edges where drops cling together.</summary>
    public float EdgeSmoothing { get; set; } = 0.012f;
    public int SmoothingPasses { get; set; } = 4;
    /// <summary>How different in depth two drops can be and still blend (metres).</summary>
    public float MergeDepth { get; set; } = 0.03f;
    /// <summary>How soft the edges are (lower = softer).</summary>
    public float EdgeSharpness { get; set; } = 180f;
    /// <summary>Every drop drawn this much bigger or smaller.</summary>
    public float DrawSize { get; set; } = 1f;
    /// <summary>Hidden behind walls, bodies and anything else in front of it.</summary>
    public bool HideBehind { get; set; } = true;
    /// <summary>How the game's depth is read: 0 worked out by itself, 1..4 one of the ways it can be stored.</summary>
    public int DepthMode { get; set; }
    /// <summary>(Older setting, no longer used.)</summary>
    public bool FlipDepth { get; set; }

    public ClimaxLook Copy() => (ClimaxLook)MemberwiseClone();
}

/// <summary>One invisible emitter: where it is, how it shoots and how the liquid behaves.</summary>
public sealed class ClimaxEmitter
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Emitter";
    /// <summary>Shooting now (a set of spurts; turns itself off at the end of the set unless Repeat). Not saved.</summary>
    [Newtonsoft.Json.JsonIgnore]
    public bool Shooting { get; set; }

    // ---- Where it is
    /// <summary>The character it's on (by name; empty = placed in the world).</summary>
    public string Character { get; set; } = "";
    public string Bone { get; set; } = "iv_ochinko_f";
    /// <summary>Moved from the bone: X right, Y up, Z forward (along where it shoots), metres.</summary>
    public Vector3 Offset { get; set; }
    public Vector3 WorldPosition { get; set; }

    // ---- Aim
    public ClimaxAim Aim { get; set; } = ClimaxAim.AlongBone;
    /// <summary>Degrees: turned right / up from the aim (or, for World, the direction itself).</summary>
    public float Yaw { get; set; }
    public float Pitch { get; set; }
    public string Target { get; set; } = "";
    public string TargetBone { get; set; } = "j_kao";
    /// <summary>The point aimed at, moved from the target bone (target character's axes: X left, Y up, Z forward), metres.</summary>
    public Vector3 TargetOffset { get; set; } = new(0, 0.03f, 0.09f);
    public bool HighArc { get; set; }
    /// <summary>Degrees the aim sways back and forth, and how often (a second).</summary>
    public float Sway { get; set; } = 3f;
    public float SwayRate { get; set; } = 0.6f;
    /// <summary>The quick wobble of the stream while it shoots (degrees, and how many a second): wavy ropes.</summary>
    public float Wiggle { get; set; } = 2f;
    public float WiggleRate { get; set; } = 7f;
    /// <summary>Degrees each spurt's aim is off by, at random.</summary>
    public float AimRandom { get; set; } = 5f;

    // ---- Spurts
    public int Spurts { get; set; } = 7;
    /// <summary>Seconds from one spurt's start to the next, and how much that varies (0..1).</summary>
    public float Interval { get; set; } = 0.95f;
    public float IntervalRandom { get; set; } = 0.25f;
    /// <summary>Seconds each spurt lasts.</summary>
    public float SpurtLength { get; set; } = 0.42f;
    /// <summary>The first spurt's strength, and each one after as a share of the one before.</summary>
    public float Strength { get; set; } = 1f;
    public float Fade { get; set; } = 0.84f;
    /// <summary>The share of a spurt spent building up (0..1), and how sharply it tails off after.</summary>
    public float Attack { get; set; } = 0.08f;
    public float TailOff { get; set; } = 1.4f;
    /// <summary>Pulsing within a spurt: how much and how fast.</summary>
    public float Throb { get; set; } = 0.12f;
    public float ThrobRate { get; set; } = 8f;
    public bool Repeat { get; set; }
    public float RestBetweenSets { get; set; } = 4f;
    /// <summary>The slow dribble after the last spurt: seconds and drops a second.</summary>
    public float Dribble { get; set; } = 2.5f;
    public float DribbleRate { get; set; } = 10f;

    // ---- The stream
    /// <summary>Metres a second at full strength.</summary>
    public float Speed { get; set; } = 4.2f;
    public float SpeedRandom { get; set; } = 0.01f;
    /// <summary>Drops a second at full strength (more = smoother ropes, more work).</summary>
    public float Rate { get; set; } = 260f;
    /// <summary>The rope's radius (metres), and how much it varies.</summary>
    public float Thickness { get; set; } = 0.011f;
    public float ThicknessRandom { get; set; } = 0.12f;
    /// <summary>Thickness at a spurt's start and end (times Thickness).</summary>
    public float StartThickness { get; set; } = 1.3f;
    public float EndThickness { get; set; } = 0.7f;
    /// <summary>Degrees the drops spread from the aim.</summary>
    public float Spread { get; set; } = 0.15f;
    /// <summary>How much of the emitter's own movement the liquid takes along.</summary>
    public float InheritMotion { get; set; } = 0.6f;

    // ---- The liquid
    /// <summary>The size of each drop of the liquid (radius, metres): smaller is smoother and more work.</summary>
    public float Drop { get; set; } = 0.0035f;
    /// <summary>How thick and slow-flowing it is (0..1): drops move together with the ones around them.</summary>
    public float Viscosity { get; set; } = 0.6f;
    /// <summary>How strongly the drops pull together (surface tension): blobs and strings when high.</summary>
    public float Cohesion { get; set; } = 0.9f;
    /// <summary>How much the skin holds it from sliding (0..1), and how slow (m/s) it has to be to set on the skin.</summary>
    public float Friction { get; set; } = 0.8f;
    public float Settle { get; set; } = 0.25f;
    /// <summary>Solver passes a step (more: firmer liquid, more work).</summary>
    public int Iterations { get; set; } = 3;
    /// <summary>How far a rope stretches (times its length) before it snaps into drops.</summary>
    public float Stretch { get; set; } = 3.2f;
    public float Gravity { get; set; } = 1f;
    public float AirDrag { get; set; } = 0.12f;
    /// <summary>The chance (0..1) a drop sticks where it lands; the rest bounce off.</summary>
    public float Stickiness { get; set; } = 0.6f;
    public float Bounce { get; set; } = 0.1f;
    /// <summary>How far drops splatter round where they land (metres).</summary>
    public float Splatter { get; set; } = 0.006f;

    // ---- On the body
    /// <summary>How fast it runs down the skin (metres a second), and how long until it stops running (seconds).</summary>
    public float RunSpeed { get; set; } = 0.006f;
    public float RunTime { get; set; } = 8f;
    /// <summary>The chance a second a hanging drop drips off (from undersides: chin, breasts...), and how long its string gets.</summary>
    public float DripChance { get; set; } = 0.3f;
    public float StringLength { get; set; } = 0.15f;
    /// <summary>Drips hang on a stretching string from where they were (goopy), instead of just falling off.</summary>
    public bool DripStrings { get; set; } = true;
    /// <summary>How much liquid on the skin keeps creeping down after it's slowed (a share of how fast it first ran).</summary>
    public float Creep { get; set; } = 0.2f;
    /// <summary>How far stuck drops sit out from the skin (times their size; lower sinks them in).</summary>
    public float SitOut { get; set; } = 0.35f;
    /// <summary>Stuck drops drawn this much bigger (a coat instead of beads).</summary>
    public float Coat { get; set; } = 1f;
    /// <summary>The bodies it hits, made bigger or smaller.</summary>
    public float BodySize { get; set; } = 1f;
    public bool HitSelf { get; set; } = true;
    public bool HitOnlyTicked { get; set; }
    public HashSet<string> HitNames { get; set; } = new();

    // ---- The floor
    public bool Floor { get; set; } = true;
    public float FloorOffset { get; set; }
    /// <summary>How flat puddles spread (times the drop size).</summary>
    public float PuddleSpread { get; set; } = 1.4f;

    // ---- How long it stays
    /// <summary>Seconds landed liquid stays (0 = until cleared), and seconds it takes to fade.</summary>
    public float Stay { get; set; }
    public float FadeTime { get; set; } = 3f;
    public int MaxDrops { get; set; } = 15000;
    /// <summary>The aim shown as a wireframe: the emitter, its spread and the arcs of the first and last spurts.</summary>
    public bool ShowAim { get; set; }

    public ClimaxEmitter Copy()
    {
        var c = (ClimaxEmitter)MemberwiseClone();
        c.HitNames = new HashSet<string>(HitNames);
        return c;
    }

    /// <summary>The way it shoots and behaves, from another (not where it is or what it aims at).</summary>
    public void TakeBehaviour(ClimaxEmitter from)
    {
        var keep = (Id, Name, Shooting, ShowAim, Character, Bone, Offset, WorldPosition, Aim, Yaw, Pitch, Target, TargetBone, TargetOffset, HighArc, HitOnlyTicked, HitNames);
        foreach(var p in typeof(ClimaxEmitter).GetProperties())
            if(p.CanWrite)
                p.SetValue(this, p.GetValue(from));
        (Id, Name, Shooting, ShowAim, Character, Bone, Offset, WorldPosition, Aim, Yaw, Pitch, Target, TargetBone, TargetOffset, HighArc, HitOnlyTicked, HitNames) = keep;
    }
}

/// <summary>
/// Climax (Scene): invisible emitters that shoot a thick, sticky liquid in spurts. The liquid is drops joined into
/// ropes that stretch and snap, stick to the bodies they hit (following them as they move), run down the skin, drip
/// off undersides on strings and pool on the floor. Worked out each bone update, after the posing; drawn as one
/// smooth surface by <see cref="Limber.Gpu.FluidRenderer"/>. Only adult characters can shoot it or be hit by it.
/// Everything is on this game client's screen only.
/// </summary>
public sealed class ClimaxService
{
    /// <summary>One drop (or the join between two) for the renderer: two ends and their radii (the same for a drop).</summary>
    public struct Piece
    {
        // a capsule (or ball): A, B its ends and radii; liquid laid on the skin: B.w negative;
        // an ellipsoid (U.w = 1): A its centre, U, V, B.xyz its three axes (each as long as that radius)
        public Vector4 A, B, U, V;
    }

    private readonly IObjectTable _objects;
    private readonly RigSettings _config;
    private readonly SkeletonInfoCache _cache = new();
    private readonly Dictionary<Guid, Run> _runs = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _last;

    // Framework thread -> bone update: who is here.
    private readonly record struct Here(nint Address, ushort Index, string Name, bool Adult);
    private volatile Here[] _here = [];
    private volatile ClimaxEmitter[] _emitters = [];

    // Bone update -> render thread.
    private readonly object _outLock = new();
    private Piece[] _out = new Piece[1024];
    private int _outCount;
    private Piece[] _build = new Piece[1024];
    private int _buildCount;

    /// <summary>Drops in the air / landed, over every emitter (for the page).</summary>
    public int Flying { get; private set; }
    public int Landed { get; private set; }
    public string Status { get; private set; } = "";

    public ClimaxService(IObjectTable objects, RigSettings config)
    {
        _objects = objects;
        _config = config;
    }

    // ---- Framework thread

    public void Update(bool here)
    {
        if(!here)
        {
            _emitters = [];
            return;
        }
        var list = new List<Here>();
        foreach(var obj in _objects)
        {
            if(obj is not Dalamud.Game.ClientState.Objects.Types.ICharacter || obj.Address == 0)
                continue;
            if(ActorSpawner.Current?.IsHelper(obj.ObjectIndex) == true)
                continue;
            // (only characters that are drawn: in GPose your own character out in the world is hidden, its copy shown)
            if(!Drawn(obj.Address))
                continue;
            list.Add(new Here(obj.Address, obj.ObjectIndex, obj.Name.TextValue, Limber.Koi.Safety.AdultAllowed(obj.Address)));
        }
        _here = list.ToArray();
        _emitters = (_config.ClimaxEmitters ?? new()).ToArray();
    }

    private static unsafe bool Drawn(nint address)
    {
        var go = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)address;
        if(go == null || go->DrawObject == null || !go->DrawObject->IsVisible)
            return false;
        return (int)go->RenderFlags == 0;
    }

    /// <summary>About where the liquid is (for the game-drawn carrier), or null when there's none.</summary>
    public Vector3? Centre { get; private set; }

    /// <summary>Points inside the bodies around (for the renderer to work out how the game's depth reads).</summary>
    public Vector3[] Probes { get; private set; } = [];

    /// <summary>Start a set of spurts now (or stop).</summary>
    public void Shoot(ClimaxEmitter e, bool on)
    {
        e.Shooting = on;
        lock(_runs)
            if(_runs.TryGetValue(e.Id, out var run))
                run.Restart = on;
    }

    /// <summary>Where an emitter is and points this frame (for the aim wireframe), if it could be placed.</summary>
    public readonly record struct AimInfo(Vector3 At, Vector3 Dir, Vector3 Up, float Ground, bool HasGround, Vector3? Target);

    public bool TryAim(Guid id, out AimInfo aim)
    {
        lock(_runs)
        {
            if(_runs.TryGetValue(id, out var run) && run.Aim is { } a)
            {
                aim = a;
                return true;
            }
        }
        aim = default;
        return false;
    }

    /// <summary>Takes away the liquid (one emitter's, or everyone's).</summary>
    public void Clear(Guid? id = null)
    {
        lock(_runs)
        {
            foreach(var (key, run) in _runs)
                if(id == null || key == id)
                    run.ClearAll = true;
        }
    }

    // ---- Render thread

    /// <summary>The pieces to draw this frame (copied into <paramref name="into"/>, grown if needed).</summary>
    public int Take(ref Piece[] into)
    {
        lock(_outLock)
        {
            if(into.Length < _outCount)
                into = new Piece[Math.Max(_outCount, into.Length * 2)];
            Array.Copy(_out, into, _outCount);
            return _outCount;
        }
    }

    // ---- Bone update

    public void UpdateFrame()
    {
        double now = _clock.Elapsed.TotalSeconds;
        float dt = (float)Math.Clamp(now - _last, 0.0, 0.05);
        _last = now;
        var emitters = _emitters;
        _buildCount = 0;
        int flying = 0, landed = 0;
        if(emitters.Length == 0 && _runs.Count == 0)
        {
            Publish();
            Flying = Landed = 0;
            return;
        }
        try
        {
            var bodies = ReadBodies();
            _surface.Keep(bodies.Keys);
            var probes = new List<Vector3>();
            foreach(var b in bodies.Values)
                if(probes.Count < 4)
                    probes.Add(b.Center);
            Probes = probes.ToArray();
            var seen = new HashSet<Guid>();
            lock(_runs)
            {
                foreach(var e in emitters)
                {
                    seen.Add(e.Id);
                    if(!_runs.TryGetValue(e.Id, out var run))
                        _runs[e.Id] = run = new Run(e.Id);
                    run.Step(this, e, bodies, dt);
                    run.Emit(this, e);
                    flying += run.FlyingCount;
                    landed += run.LandedCount;
                }
                var gone = new List<Guid>();
                foreach(var id in _runs.Keys)
                    if(!seen.Contains(id))
                        gone.Add(id);
                foreach(var id in gone)
                    _runs.Remove(id);
            }
            Status = "";
        }
        catch(Exception ex)
        {
            Status = "Something went wrong: " + ex.Message;
            Plugin.Log.Debug(ex, "Limber: climax");
        }
        Flying = flying;
        Landed = landed;
        Publish();
    }

    private void Publish()
    {
        if(_buildCount > 0)
        {
            var sum = Vector3.Zero;
            int n = Math.Min(_buildCount, 64);
            for(int i = 0; i < n; i++)
                sum += new Vector3(_build[i * _buildCount / n].A.X, _build[i * _buildCount / n].A.Y, _build[i * _buildCount / n].A.Z);
            Centre = sum / n;
        }
        else
            Centre = null;
        lock(_outLock)
        {
            (_out, _build) = (_build, _out);
            _outCount = _buildCount;
        }
    }

    private void Add(Vector3 a, float ra, Vector3 b, float rb)
    {
        if(_buildCount >= _build.Length)
            Array.Resize(ref _build, _build.Length * 2);
        _build[_buildCount++] = new Piece { A = new Vector4(a, ra), B = new Vector4(b, rb) };
    }

    /// <summary>Liquid lying on skin (or the floor): which way is out from it in U (zero if not known).</summary>
    private void AddCoat(Vector3 at, float r, Vector3 normal)
    {
        if(_buildCount >= _build.Length)
            Array.Resize(ref _build, _build.Length * 2);
        _build[_buildCount++] = new Piece { A = new Vector4(at, r), B = new Vector4(at, -r), U = new Vector4(normal, 0f) };
    }

    private void AddEllipsoid(Vector3 centre, Vector3 ax1, Vector3 ax2, Vector3 ax3)
    {
        if(_buildCount >= _build.Length)
            Array.Resize(ref _build, _build.Length * 2);
        float r = MathF.Max(ax1.Length(), MathF.Max(ax2.Length(), ax3.Length()));
        _build[_buildCount++] = new Piece { A = new Vector4(centre, r), B = new Vector4(ax3, r), U = new Vector4(ax1, 1f), V = new Vector4(ax2, 0f) };
    }

    // ---- The bodies the liquid hits

    // A part of the body: a capsule between two points, each on a bone, moved from it (in the model's rest axes:
    // X left, Y up, Z forward). Radii are for a standard-sized character.
    private readonly record struct Part(string BoneA, Vector3 OffA, string BoneB, Vector3 OffB, float Radius);

    private static readonly Part[] Parts =
    [
        // head: the skull, the face from chin to brow, the jaw, the nose
        // (kept a little inside the real head: the drawn liquid is laid on the real skin by the renderer, and shapes
        // bigger than the head left it floating off the face and above the head)
        new("j_kao", new(0, 0.045f, -0.005f), "j_kao", new(0, 0.095f, -0.015f), 0.078f),
        new("j_kao", new(0, 0.005f, 0.035f), "j_kao", new(0, 0.07f, 0.045f), 0.05f),
        new("j_kao", new(0, -0.012f, 0.03f), "j_kao", new(0, -0.012f, 0.03f), 0.042f),
        new("j_kao", new(0, 0.045f, 0.082f), "j_kao", new(0, 0.045f, 0.082f), 0.015f),
        new("j_kubi", default, "j_kao", default, 0.045f),
        // body
        new("j_sebo_b", new(0, 0, 0.01f), "j_sebo_c", new(0, 0, 0.0f), 0.1f),
        new("j_ude_a_l", default, "j_ude_a_r", default, 0.055f),
        new("j_mune_l", default, "j_mune_l", new(0.015f, -0.03f, 0.085f), 0.06f),
        new("j_mune_r", default, "j_mune_r", new(-0.015f, -0.03f, 0.085f), 0.06f),
        new("j_sebo_a", new(0, 0, 0.01f), "j_sebo_b", new(0, 0, 0.01f), 0.098f),
        new("j_kosi", new(0, -0.04f, 0), "j_kosi", new(0, -0.11f, -0.02f), 0.118f),
        // arms
        new("j_ude_a_l", default, "j_ude_b_l", default, 0.042f),
        new("j_ude_a_r", default, "j_ude_b_r", default, 0.042f),
        new("j_ude_b_l", default, "j_te_l", default, 0.034f),
        new("j_ude_b_r", default, "j_te_r", default, 0.034f),
        new("j_te_l", default, "j_naka_a_l", default, 0.035f),
        new("j_te_r", default, "j_naka_a_r", default, 0.035f),
        // legs
        new("j_asi_a_l", default, "j_asi_c_l", default, 0.075f),
        new("j_asi_a_r", default, "j_asi_c_r", default, 0.075f),
        new("j_asi_c_l", default, "j_asi_d_l", default, 0.05f),
        new("j_asi_c_r", default, "j_asi_d_r", default, 0.05f),
        new("j_asi_d_l", default, "j_asi_e_l", default, 0.04f),
        new("j_asi_d_r", default, "j_asi_e_r", default, 0.04f),
    ];

    // A skeleton's parts: the bones they're on and their offsets in each bone's own axes.
    private sealed class PartsOf
    {
        public required int[] BoneA, BoneB;
        public required Vector3[] LocalA, LocalB;
    }

    private readonly Dictionary<SkeletonInfo, PartsOf> _partsOf = new();

    private PartsOf? Prepare(in ActorSkeleton sk)
    {
        if(_partsOf.TryGetValue(sk.Info, out var have))
            return have;
        var info = sk.Info;
        int n = info.Count;
        var pos = new Vector3[n];
        var rot = new Quaternion[n];
        for(int i = 0; i < n; i++)
        {
            int parent = info.ParentOf(i);
            sk.TryGetReferenceLocal(i, out var lp, out var lq);
            if(parent >= 0 && parent < i)
            {
                pos[i] = pos[parent] + Vector3.Transform(lp, rot[parent]);
                rot[i] = Quaternion.Normalize(rot[parent] * lq);
            }
            else
            {
                pos[i] = lp;
                rot[i] = lq;
            }
        }
        var result = new PartsOf { BoneA = new int[Parts.Length], BoneB = new int[Parts.Length], LocalA = new Vector3[Parts.Length], LocalB = new Vector3[Parts.Length] };
        for(int p = 0; p < Parts.Length; p++)
        {
            int a = info.IndexOf(Parts[p].BoneA), b = info.IndexOf(Parts[p].BoneB);
            result.BoneA[p] = a;
            result.BoneB[p] = b;
            if(a >= 0)
                result.LocalA[p] = Vector3.Transform(Parts[p].OffA, Quaternion.Inverse(rot[a]));
            if(b >= 0)
                result.LocalB[p] = Vector3.Transform(Parts[p].OffB, Quaternion.Inverse(rot[b]));
        }
        _partsOf[sk.Info] = result;
        return result;
    }

    /// <summary>A character the liquid can hit, as it is this frame.</summary>
    private sealed class Body
    {
        public nint Address;
        public string Name = "";
        public bool Adult;
        public float Scale = 1f, Ground;
        public Vector3 Root;
        public Quaternion RootRotation = Quaternion.Identity;
        // per part: world ends, radius (standard size), and the bones they're on (for sticking)
        public Vector3[] A = [], B = [];
        public float[] R = [];
        public bool[] Has = [];
        public int[] BoneOfA = [], BoneOfB = [];
        public Dictionary<int, Frame> Frames = new();
        public Vector3 Center;
        public float Reach;
        public ActorSkeleton Skeleton;
        /// <summary>Its real skin (its models' vertices this frame), once read; null until then (the shapes above stand in).</summary>
        public ClimaxSurface.Frame? Skin;
    }

    private readonly ClimaxSurface _surface = new();

    private Dictionary<nint, Body> ReadBodies()
    {
        var bodies = new Dictionary<nint, Body>();
        long now = Environment.TickCount64;
        foreach(var h in _here)
        {
            try
            {
                if(!ActorSkeleton.TryGet(h.Address, h.Index, _cache, out var sk) || sk.Info.Count == 0)
                    continue;
                var parts = Prepare(sk);
                if(parts == null)
                    continue;
                var body = new Body
                {
                    Address = h.Address, Name = h.Name, Adult = h.Adult, Skeleton = sk,
                    Scale = MathF.Max(sk.RootScale.Y, 0.01f),
                    A = new Vector3[Parts.Length], B = new Vector3[Parts.Length], R = new float[Parts.Length], Has = new bool[Parts.Length],
                    BoneOfA = parts.BoneA, BoneOfB = parts.BoneB,
                };
                body.Root = sk.ModelToWorldPoint(Vector3.Zero);
                body.RootRotation = sk.ModelToWorldRotation(Quaternion.Identity);
                body.Ground = body.Root.Y;
                Vector3 min = new(float.MaxValue), max = new(float.MinValue);
                for(int p = 0; p < Parts.Length; p++)
                {
                    int a = parts.BoneA[p], b = parts.BoneB[p];
                    if(a < 0 || b < 0)
                        continue;
                    var fa = BoneFrame(body, a);
                    var fb = BoneFrame(body, b);
                    body.A[p] = fa.Position + Vector3.Transform(parts.LocalA[p] * body.Scale, fa.Rotation);
                    body.B[p] = fb.Position + Vector3.Transform(parts.LocalB[p] * body.Scale, fb.Rotation);
                    body.R[p] = Parts[p].Radius * body.Scale;
                    body.Has[p] = MathUtil.IsFinite(body.A[p]) && MathUtil.IsFinite(body.B[p]);
                    if(body.Has[p])
                    {
                        min = Vector3.Min(min, Vector3.Min(body.A[p], body.B[p]));
                        max = Vector3.Max(max, Vector3.Max(body.A[p], body.B[p]));
                    }
                }
                if(min.X > max.X)
                    continue;
                body.Center = (min + max) * 0.5f;
                body.Reach = (max - min).Length() * 0.5f + 0.2f * body.Scale;
                // (its real skin: only adult characters are read)
                if(body.Adult && _config.ClimaxEmitters is { Count: > 0 })
                    body.Skin = _surface.Get(h.Address, sk, now);
                bodies[h.Address] = body;
            }
            catch
            {
                // (a character mid-change: skipped this frame)
            }
        }
        return bodies;
    }

    private static Frame BoneFrame(Body body, int bone)
    {
        if(body.Frames.TryGetValue(bone, out var f))
            return f;
        f = body.Skeleton.GetWorldFrame(bone);
        body.Frames[bone] = f;
        return f;
    }

    // ---- One emitter's liquid

    private enum Phase { Idle, Spurt, Gap, Dribble, Rest }

    private const byte Free = 0, Air = 1, Stuck = 2, Puddle = 3;

    private sealed class Run
    {
        public readonly Guid Id;
        public bool Restart, ClearAll;
        public int FlyingCount, LandedCount;
        public AimInfo? Aim;
        private Vector3? _target;

        private readonly Random _random = new();

        // The drops: [0, _n) in use (a drop that goes is swapped with the last).
        private int _n, _capacity;
        private Vector3[] _x = [], _v = [], _p = [], _local = [], _localNormal = [], _newV = [], _wn = [];
        private float[] _age = [], _landedAge = [], _lambda = [], _rest = [];
        private int[] _bone = [], _link = [], _remap = [], _point = [];
        private float[] _pointOff = [];
        private Vector3[] _dp = [];
        // each flying drop's drawn shape (from the drops around it): its centre, and axes for a radius of 1
        private Vector3[] _shapeC = [], _ax1 = [], _ax2 = [], _ax3 = [];
        private bool[] _shaped = [];
        // the slice of the stream let go of last (each new drop is strung to the one at its place in it)
        private readonly List<int> _prevSlice = new(), _curSlice = new();
        private byte[] _state = [];
        private bool[] _clearOfSelf = [], _dripped = [];
        private nint[] _body = [];
        // neighbours (per drop, found once a step), and the grid they're found with
        private const int MaxNeighbours = 40;
        private int[] _nbr = [], _nbrCount = [];
        private const int TableSize = 1 << 15;
        private readonly int[] _cellStart = new int[TableSize + 1], _fill = new int[TableSize];
        private int[] _cellIdx = [], _hash = [];

        // the spurts
        private Phase _phase = Phase.Idle;
        private int _spurt;
        private float _time, _phaseLength, _emitCarry, _clockTime;
        private bool _wasShooting;
        private Vector3 _aimJitter;
        // where the emitter was (for its movement, and to spread one frame's drops along its path)
        private Vector3 _at, _was;
        private bool _hasAt;
        private Vector3 _dir = Vector3.UnitZ, _up = Vector3.UnitY;
        private nint _source;
        private float _ground;
        private bool _hasGround;

        public Run(Guid id) => Id = id;

        private void Grow(int capacity)
        {
            capacity = Math.Clamp(capacity, 64, 30000);
            if(capacity <= _capacity)
                return;
            Array.Resize(ref _x, capacity);
            Array.Resize(ref _v, capacity);
            Array.Resize(ref _p, capacity);
            Array.Resize(ref _newV, capacity);
            Array.Resize(ref _local, capacity);
            Array.Resize(ref _localNormal, capacity);
            Array.Resize(ref _wn, capacity);
            Array.Resize(ref _age, capacity);
            Array.Resize(ref _landedAge, capacity);
            Array.Resize(ref _lambda, capacity);
            Array.Resize(ref _bone, capacity);
            Array.Resize(ref _link, capacity);
            Array.Resize(ref _point, capacity);
            Array.Resize(ref _pointOff, capacity);
            Array.Resize(ref _rest, capacity);
            Array.Resize(ref _remap, capacity);
            Array.Resize(ref _dp, capacity);
            Array.Resize(ref _shapeC, capacity);
            Array.Resize(ref _ax1, capacity);
            Array.Resize(ref _ax2, capacity);
            Array.Resize(ref _ax3, capacity);
            Array.Resize(ref _shaped, capacity);
            Array.Resize(ref _state, capacity);
            Array.Resize(ref _clearOfSelf, capacity);
            Array.Resize(ref _dripped, capacity);
            Array.Resize(ref _body, capacity);
            Array.Resize(ref _nbr, capacity * MaxNeighbours);
            Array.Resize(ref _nbrCount, capacity);
            Array.Resize(ref _cellIdx, capacity);
            Array.Resize(ref _hash, capacity);
            _capacity = capacity;
        }

        private float Rand() => (float)_random.NextDouble();
        private float Signed() => (float)_random.NextDouble() * 2f - 1f;

        private int New(Vector3 at, Vector3 vel)
        {
            if(_n >= _capacity)
                return -1;
            int i = _n++;
            _x[i] = at;
            _p[i] = at;
            _v[i] = vel;
            _age[i] = 0f;
            _landedAge[i] = 0f;
            _wn[i] = Vector3.Zero;
            _dripped[i] = false;
            _state[i] = Air;
            _clearOfSelf[i] = false;
            _body[i] = 0;
            _bone[i] = -1;
            _link[i] = -1;
            _rest[i] = 0f;
            _point[i] = -1;
            return i;
        }

        // Drops marked gone (Free) taken out, the rest moved down in order; the strings between them kept.
        private void Compact()
        {
            int w = 0;
            for(int i = 0; i < _n; i++)
            {
                if(_state[i] == Free)
                {
                    _remap[i] = -1;
                    continue;
                }
                _remap[i] = w;
                if(w != i)
                {
                    _x[w] = _x[i];
                    _p[w] = _p[i];
                    _v[w] = _v[i];
                    _local[w] = _local[i];
                    _localNormal[w] = _localNormal[i];
                    _wn[w] = _wn[i];
                    _age[w] = _age[i];
                    _landedAge[w] = _landedAge[i];
                    _state[w] = _state[i];
                    _clearOfSelf[w] = _clearOfSelf[i];
                    _dripped[w] = _dripped[i];
                    _body[w] = _body[i];
                    _bone[w] = _bone[i];
                    _link[w] = _link[i];
                    _rest[w] = _rest[i];
                    _point[w] = _point[i];
                    _pointOff[w] = _pointOff[i];
                    _shapeC[w] = _shapeC[i];
                    _ax1[w] = _ax1[i];
                    _ax2[w] = _ax2[i];
                    _ax3[w] = _ax3[i];
                    _shaped[w] = _shaped[i];
                }
                w++;
            }
            if(w == _n)
                return;
            int old = _n;
            _n = w;
            for(int i = 0; i < _n; i++)
                if(_link[i] >= 0)
                    _link[i] = _link[i] < old ? _remap[_link[i]] : -1;
            for(int k = 0; k < _prevSlice.Count; k++)
                _prevSlice[k] = _prevSlice[k] < old ? _remap[_prevSlice[k]] : -1;
        }

        // ---- Where the emitter is and where it points

        private bool Locate(ClimaxService s, ClimaxEmitter e, Dictionary<nint, Body> bodies, out Vector3 at, out Vector3 dir, out Vector3 up, out nint source)
        {
            at = default;
            dir = Vector3.UnitZ;
            up = Vector3.UnitY;
            source = 0;
            Vector3 baseDir;
            Quaternion charRot = Quaternion.Identity;
            if(e.Character.Length > 0)
            {
                Body? body = null;
                foreach(var b in bodies.Values)
                    if(b.Name == e.Character)
                    {
                        body = b;
                        break;
                    }
                // (only adult characters shoot it)
                if(body == null || !body.Adult)
                    return false;
                source = body.Address;
                charRot = body.RootRotation;
                if(!body.Skeleton.TryGetDrawnWorldFrame(e.Bone, s._cache, out var bone))
                    return false;
                var forward = Vector3.Transform(Vector3.UnitZ, charRot);
                if(e.Aim == ClimaxAim.AlongBone && ParentOf(s, body.Skeleton, e.Bone) is { } parent
                   && body.Skeleton.TryGetDrawnWorldFrame(parent, s._cache, out var pf) && Vector3.DistanceSquared(pf.Position, bone.Position) > 1e-8f)
                    baseDir = Vector3.Normalize(bone.Position - pf.Position);
                else
                    baseDir = forward;
                // (Offset: right, up, forward of the way the bone points)
                Basis(baseDir, Vector3.Transform(Vector3.UnitY, charRot), out var r0, out var u0);
                at = bone.Position + r0 * e.Offset.X + u0 * e.Offset.Y + baseDir * e.Offset.Z;
                if(e.Aim == ClimaxAim.CharacterForward)
                    baseDir = forward;
                up = Vector3.Transform(Vector3.UnitY, charRot);
            }
            else
            {
                at = e.WorldPosition;
                baseDir = Vector3.UnitZ;
            }

            if(e.Aim == ClimaxAim.World)
            {
                dir = FromAngles(Vector3.UnitZ, Vector3.UnitY, e.Yaw, e.Pitch);
                return true;
            }
            if(e.Aim == ClimaxAim.AtTarget && e.Target.Length > 0)
            {
                foreach(var b in bodies.Values)
                {
                    if(b.Name != e.Target || !b.Adult)
                        continue;
                    if(!b.Skeleton.TryGetDrawnWorldFrame(e.TargetBone, s._cache, out var tf))
                        break;
                    var aimAt = tf.Position + Vector3.Transform(e.TargetOffset * b.Scale, b.RootRotation);
                    _target = aimAt;
                    // (most of a spurt goes a little slower than its peak: aimed for that)
                    float speed = MathF.Max(e.Speed * e.Strength * 0.85f, 0.2f);
                    baseDir = Ballistic(aimAt - at, speed, 9.81f * e.Gravity, e.HighArc);
                    dir = FromAngles(baseDir, Vector3.UnitY, e.Yaw, e.Pitch);
                    return true;
                }
            }
            dir = FromAngles(baseDir, up, e.Yaw, e.Pitch);
            return true;
        }

        private static string? ParentOf(ClimaxService s, in ActorSkeleton sk, string bone)
        {
            if(sk.TryFindExtraBone(bone, s._cache, out int partial, out int b))
            {
                var info = sk.PartialInfo(partial, s._cache);
                int parent = info?.ParentOf(b) ?? -1;
                return parent >= 0 ? info!.Names[parent] : null;
            }
            int i = sk.Info.IndexOf(bone);
            int p = sk.Info.ParentOf(i);
            return p >= 0 ? sk.Info.Names[p] : null;
        }

        private static void Basis(Vector3 forward, Vector3 upHint, out Vector3 right, out Vector3 up)
        {
            right = Vector3.Cross(upHint, forward);
            if(right.LengthSquared() < 1e-6f)
                right = Vector3.Cross(Vector3.UnitX, forward);
            right = Vector3.Normalize(right);
            up = Vector3.Cross(forward, right);
        }

        private static Vector3 FromAngles(Vector3 forward, Vector3 upHint, float yawDeg, float pitchDeg)
        {
            Basis(forward, upHint, out var right, out var up);
            float y = yawDeg * MathF.PI / 180f, p = pitchDeg * MathF.PI / 180f;
            // (right is +yaw: X is the character's left, so "right" here is -right)
            return Vector3.Normalize(forward * (MathF.Cos(p) * MathF.Cos(y)) - right * (MathF.Cos(p) * MathF.Sin(y)) + up * MathF.Sin(p));
        }

        // The way to throw at this speed to land on the point (low or high arc); straight at it at 45° up when out of reach.
        private static Vector3 Ballistic(Vector3 to, float v, float g, bool high)
        {
            var flat = new Vector3(to.X, 0, to.Z);
            float d = flat.Length();
            if(d < 1e-4f || g < 1e-4f)
                return to.LengthSquared() > 1e-8f ? Vector3.Normalize(to) : Vector3.UnitZ;
            var h = flat / d;
            float v2 = v * v, disc = v2 * v2 - g * (g * d * d + 2f * to.Y * v2);
            float angle = disc < 0 ? MathF.PI / 4f : MathF.Atan((v2 + (high ? 1 : -1) * MathF.Sqrt(disc)) / (g * d));
            return Vector3.Normalize(h * MathF.Cos(angle) + Vector3.UnitY * MathF.Sin(angle));
        }

        // ---- A frame: the spurts, then the liquid

        public void Step(ClimaxService s, ClimaxEmitter e, Dictionary<nint, Body> bodies, float dt)
        {
            Grow(Math.Max(e.MaxDrops, 64));
            if(ClearAll)
            {
                ClearAll = false;
                _n = 0;
                _prevSlice.Clear();
            }
            _clockTime += dt;
            _target = null;
            bool located = Locate(s, e, bodies, out var at, out var aim, out var up, out var source);
            _source = source;
            if(located)
            {
                _was = _hasAt ? _at : at;
                _at = at;
                _hasAt = true;
                _dir = aim;
                _up = up;
            }
            else
                _hasAt = false;

            // the floor: under the emitter's character, or the lowest body around a world emitter
            _hasGround = false;
            if(source != 0 && bodies.TryGetValue(source, out var sb))
            {
                _ground = sb.Ground;
                _hasGround = true;
            }
            else
            {
                foreach(var b in bodies.Values)
                {
                    if(!_hasGround || b.Ground < _ground)
                        _ground = b.Ground;
                    _hasGround = true;
                }
            }
            _ground += e.FloorOffset;
            Aim = located ? new AimInfo(at, aim, up, _ground, _hasGround, _target) : null;

            Schedule(e, dt, out float speed, out float thick);
            if(dt <= 0f)
                return;
            if(located && speed > 0f && thick > 0f)
                Spawn(e, dt, speed, thick);
            Simulate(e, bodies, dt);
        }

        private void Schedule(ClimaxEmitter e, float dt, out float speed, out float thick)
        {
            speed = thick = 0f;
            if(Restart || (e.Shooting && !_wasShooting))
            {
                Restart = false;
                if(e.Shooting)
                    Begin(e, 0);
            }
            if(!e.Shooting && _wasShooting && _phase is Phase.Spurt or Phase.Gap or Phase.Rest)
                _phase = Phase.Idle;
            _wasShooting = e.Shooting;

            _time += dt;
            switch(_phase)
            {
                case Phase.Spurt:
                {
                    float tau = _time / MathF.Max(_phaseLength, 0.01f);
                    if(tau >= 1f)
                    {
                        if(_spurt + 1 < Math.Max(e.Spurts, 1))
                        {
                            _phase = Phase.Gap;
                            _phaseLength = MathF.Max(e.Interval * (1f + Signed() * e.IntervalRandom) - _phaseLength, 0.02f);
                            _time = 0f;
                        }
                        else
                        {
                            _phase = e.Dribble > 0.01f ? Phase.Dribble : e.Repeat ? Phase.Rest : Phase.Idle;
                            _phaseLength = e.Dribble > 0.01f ? e.Dribble : e.RestBetweenSets;
                            _time = 0f;
                            if(_phase == Phase.Idle)
                                e.Shooting = _wasShooting = false;
                        }
                        break;
                    }
                    float strength = e.Strength * MathF.Pow(Math.Clamp(e.Fade, 0f, 1.5f), _spurt);
                    float attack = Math.Clamp(e.Attack, 0.001f, 0.95f);
                    float env = tau < attack ? tau / attack : MathF.Pow(MathF.Max(1f - (tau - attack) / (1f - attack), 0f), MathF.Max(e.TailOff, 0.05f));
                    float throb = 1f + e.Throb * MathF.Sin(_time * e.ThrobRate * MathF.Tau);
                    speed = e.Speed * strength * (0.6f + 0.4f * env) * throb;
                    thick = e.Thickness * (e.StartThickness + (e.EndThickness - e.StartThickness) * tau) * MathF.Sqrt(MathF.Max(strength, 0.1f)) * (0.6f + 0.4f * MathF.Max(env, 0f));
                    break;
                }
                case Phase.Gap:
                    if(_time >= _phaseLength)
                        Begin(e, _spurt + 1);
                    break;
                case Phase.Dribble:
                {
                    float left = 1f - _time / MathF.Max(_phaseLength, 0.01f);
                    if(left <= 0f)
                    {
                        _phase = e.Repeat && e.Shooting ? Phase.Rest : Phase.Idle;
                        _phaseLength = e.RestBetweenSets;
                        _time = 0f;
                        if(_phase == Phase.Idle)
                            e.Shooting = _wasShooting = false;
                        break;
                    }
                    speed = 0.1f + 0.3f * left;
                    // (Dribble amount: 10 is a thin ooze)
                    thick = e.Thickness * e.EndThickness * Math.Clamp(e.DribbleRate / 10f, 0.2f, 4f) * (0.5f + 0.5f * left);
                    break;
                }
                case Phase.Rest:
                    if(_time >= _phaseLength)
                        Begin(e, 0);
                    break;
            }
        }

        private void Begin(ClimaxEmitter e, int spurt)
        {
            _phase = Phase.Spurt;
            _spurt = spurt;
            _time = 0f;
            _phaseLength = MathF.Max(e.SpurtLength, 0.03f);
            _aimJitter = new Vector3(Signed(), Signed(), 0) * e.AimRandom;
            _prevSlice.Clear();
        }

        // The stream in slices: each a disc of drops across it (as many as fill its thickness), a drop's width apart.
        private void Spawn(ClimaxEmitter e, float dt, float speed, float thick)
        {
            float d = MathF.Max(e.Drop, 0.0015f) * 2f;
            _emitCarry += speed * dt / (d * 0.9f);
            int slices = Math.Min((int)_emitCarry, 40);
            _emitCarry -= (int)_emitCarry;
            if(slices <= 0)
                return;
            var moved = (_at - _was) / MathF.Max(dt, 1e-4f);
            float sway = e.Sway * MathF.Sin(_clockTime * e.SwayRate * MathF.Tau) + e.Wiggle * MathF.Sin(_clockTime * e.WiggleRate * MathF.Tau);
            float sway2 = e.Sway * 0.6f * MathF.Sin(_clockTime * e.SwayRate * 1.37f * MathF.Tau + 1f) + e.Wiggle * 0.8f * MathF.Sin(_clockTime * e.WiggleRate * 1.21f * MathF.Tau + 2f);
            var dir = FromAngles(_dir, _up, sway + _aimJitter.X, sway2 + _aimJitter.Y);
            Basis(dir, _up, out var right, out var up);
            int rings = Math.Clamp((int)MathF.Round(thick / d + 0.5f), 1, 6);
            for(int sl = 0; sl < slices; sl++)
            {
                float f = (sl + 1f) / slices, early = (1f - f) * dt;
                var from = Vector3.Lerp(_was, _at, f);
                _curSlice.Clear();
                for(int ring = 0; ring < rings; ring++)
                {
                    int count = ring == 0 ? 1 : (int)MathF.Round(MathF.Tau * ring);
                    float spin = Rand();
                    for(int k = 0; k < count; k++)
                    {
                        float a = (k + spin) / count * MathF.Tau;
                        var off = (right * MathF.Cos(a) + up * MathF.Sin(a)) * (ring * d);
                        float v = MathF.Max(speed * (1f + Signed() * e.SpeedRandom), 0.02f);
                        var dd = FromAngles(dir, _up, Signed() * e.Spread, Signed() * e.Spread);
                        var vel = dd * v + moved * e.InheritMotion;
                        int i = New(from + off + vel * early, vel);
                        if(i < 0)
                            return;
                        // (strung to the drop at the same place in the slice before: the stream stretches like goo instead of breaking up)
                        int slot = _curSlice.Count;
                        int j = slot < _prevSlice.Count ? _prevSlice[slot] : -1;
                        if(j >= 0 && j < _n && _state[j] != Free)
                        {
                            _link[i] = j;
                            _rest[i] = MathF.Max(Vector3.Distance(_x[i], _x[j]), d * 0.5f);
                        }
                        _curSlice.Add(i);
                    }
                }
                _prevSlice.Clear();
                _prevSlice.AddRange(_curSlice);
            }
        }

        // ---- The liquid: Position Based Fluids (Macklin and Müller), with viscosity, cohesion and adhesion

        private static int Cell(float c, float size) => (int)MathF.Floor(c / size);

        private static int HashOf(int x, int y, int z) => (int)(((uint)(x * 73856093) ^ (uint)(y * 19349663) ^ (uint)(z * 83492791)) % TableSize);

        private void FindNeighbours(float h)
        {
            int n = _n;
            Array.Clear(_cellStart);
            for(int i = 0; i < n; i++)
            {
                var q = _p[i];
                int hsh = HashOf(Cell(q.X, h), Cell(q.Y, h), Cell(q.Z, h));
                _hash[i] = hsh;
                _cellStart[hsh + 1]++;
            }
            for(int k = 0; k < TableSize; k++)
                _cellStart[k + 1] += _cellStart[k];
            Array.Copy(_cellStart, _fill, TableSize);
            for(int i = 0; i < n; i++)
                _cellIdx[_fill[_hash[i]]++] = i;
            float h2 = h * h;
            for(int i = 0; i < n; i++)
            {
                int count = 0;
                // (landed drops only need their neighbours as company for the flying ones)
                if(_state[i] == Air)
                {
                    var q = _p[i];
                    int cx = Cell(q.X, h), cy = Cell(q.Y, h), cz = Cell(q.Z, h);
                    for(int a = -1; a <= 1 && count < MaxNeighbours; a++)
                        for(int b = -1; b <= 1 && count < MaxNeighbours; b++)
                            for(int c = -1; c <= 1 && count < MaxNeighbours; c++)
                            {
                                int hsh = HashOf(cx + a, cy + b, cz + c);
                                for(int k = _cellStart[hsh]; k < _cellStart[hsh + 1] && count < MaxNeighbours; k++)
                                {
                                    int j = _cellIdx[k];
                                    if(j == i || Vector3.DistanceSquared(q, _p[j]) >= h2)
                                        continue;
                                    _nbr[i * MaxNeighbours + count++] = j;
                                }
                            }
                }
                _nbrCount[i] = count;
            }
        }

        private void Simulate(ClimaxEmitter e, Dictionary<nint, Body> bodies, float dt)
        {
            float r = MathF.Max(e.Drop, 0.0015f), d = r * 2f, h = r * 4f, hh = h * h;
            float poly6 = 315f / (64f * MathF.PI * MathF.Pow(h, 9)), spiky = -45f / (MathF.PI * MathF.Pow(h, 6));
            // rest density: drops packed at their own size
            float rho0 = 0f;
            for(int a = -3; a <= 3; a++)
                for(int b = -3; b <= 3; b++)
                    for(int c = -3; c <= 3; c++)
                    {
                        float r2 = (a * a + b * b + c * c) * d * d;
                        if(r2 < hh)
                            rho0 += poly6 * MathF.Pow(hh - r2, 3);
                    }
            float w0 = poly6 * MathF.Pow(hh, 3);
            float bodySize = MathF.Max(e.BodySize, 0.05f);
            var g = new Vector3(0, -9.81f * e.Gravity, 0);

            // bodies near enough to land on (adult characters only)
            var near = new List<Body>();
            foreach(var b in bodies.Values)
            {
                if(!b.Adult || (b.Address == _source && !e.HitSelf) || (e.HitOnlyTicked && !e.HitNames.Contains(b.Name)))
                    continue;
                near.Add(b);
            }

            // landed drops first: carried by their bones, running down the skin, dripping off undersides
            for(int i = 0; i < _n; i++)
            {
                if(_state[i] != Stuck)
                    continue;
                if(_point[i] >= 0 && bodies.TryGetValue(_body[i], out var sb2) && sb2.Skin is { } skin && _point[i] < skin.Count && skin.Valid[_point[i]])
                {
                    // on the real skin: where its point is now; running down it, onto the next point down
                    int k = _point[i];
                    var on = skin.Pos[k] + skin.Nrm[k] * _pointOff[i];
                    var sn = skin.Nrm[k];
                    _landedAge[i] += dt;
                    float runS = e.RunSpeed * MathF.Max(1f - _landedAge[i] / MathF.Max(e.RunTime, 0.01f), Math.Clamp(e.Creep, 0f, 1f));
                    if(runS > 0f)
                    {
                        var down = new Vector3(0, -1, 0);
                        var along = down - sn * Vector3.Dot(down, sn);
                        float l = along.Length();
                        if(l > 0.05f)
                        {
                            var to = on + along / l * (runS * l * dt * (0.6f + 0.8f * ((i * 7919) % 100) / 100f));
                            int k2 = ClimaxSurface.Nearest(skin, to, 0.02f, out _);
                            if(k2 >= 0)
                            {
                                _point[i] = k2;
                                on = skin.Pos[k2] + skin.Nrm[k2] * _pointOff[i];
                                sn = skin.Nrm[k2];
                            }
                        }
                    }
                    _x[i] = on;
                    _wn[i] = sn;
                    if(sn.Y < -0.3f && _landedAge[i] > 0.5f && !_dripped[i] && Rand() < e.DripChance * -sn.Y * dt)
                    {
                        if(e.DripStrings)
                            Drip(i, e, on, sn);
                        else
                        {
                            _state[i] = Air;
                            _v[i] = new Vector3(0, -0.05f, 0);
                            _clearOfSelf[i] = true;
                            _point[i] = -1;
                        }
                    }
                    continue;
                }
                if(_point[i] >= 0)
                {
                    // (its skin gone: an outfit change, a redraw) falls
                    _state[i] = Air;
                    _v[i] = Vector3.Zero;
                    _clearOfSelf[i] = true;
                    _point[i] = -1;
                    continue;
                }
                if(!bodies.TryGetValue(_body[i], out var b) || _bone[i] < 0)
                {
                    _state[i] = Air;
                    _v[i] = Vector3.Zero;
                    _clearOfSelf[i] = true;
                    continue;
                }
                var f = BoneFrame(b, _bone[i]);
                var at = f.Position + Vector3.Transform(_local[i] * b.Scale, f.Rotation);
                var nrm = Vector3.Transform(_localNormal[i], f.Rotation);
                _landedAge[i] += dt;
                float run = e.RunSpeed * MathF.Max(1f - _landedAge[i] / MathF.Max(e.RunTime, 0.01f), Math.Clamp(e.Creep, 0f, 1f));
                if(run > 0f)
                {
                    var down = new Vector3(0, -1, 0);
                    var along = down - nrm * Vector3.Dot(down, nrm);
                    float l = along.Length();
                    if(l > 0.05f)
                    {
                        at += along / l * (run * l * dt * (0.6f + 0.8f * ((i * 7919) % 100) / 100f));
                        Resnap(i, b, e, ref at, ref nrm, bodySize, r);
                    }
                }
                _x[i] = at;
                _wn[i] = nrm;
                if(nrm.Y < -0.3f && _landedAge[i] > 0.5f && !_dripped[i] && Rand() < e.DripChance * -nrm.Y * dt)
                {
                    if(e.DripStrings)
                        Drip(i, e, at, nrm);
                    else
                    {
                        _state[i] = Air;
                        _v[i] = new Vector3(0, -0.05f, 0);
                        _clearOfSelf[i] = true;
                    }
                }
            }

            const int steps = 2;
            float hs = dt / steps;
            float keep = MathF.Max(1f - e.AirDrag * hs, 0f);
            int iterations = Math.Clamp(e.Iterations, 1, 6);
            // (viscosity: how much drops move with the ones around them, how hard they pull together, how soon they set, how far strings stretch)
            float visc = Math.Clamp(e.Viscosity, 0f, 1f);
            float mixK = MathF.Min(0.15f + 0.85f * visc, 1f), cohesion = MathF.Max(e.Cohesion, 0f) * 3f * (0.5f + 1.5f * visc);
            float settle = e.Settle * (0.4f + 1.2f * visc);
            float stringK = 0.25f + 0.5f * visc, stringFar = 2f + 10f * visc;
            for(int step = 0; step < steps; step++)
            {
                int n = _n;
                for(int i = 0; i < n; i++)
                {
                    if(_state[i] == Air)
                    {
                        _v[i] = (_v[i] + g * hs) * keep;
                        _p[i] = _x[i] + _v[i] * hs;
                    }
                    else
                        _p[i] = _x[i];
                }
                FindNeighbours(h);
                for(int it = 0; it < iterations; it++)
                {
                    // how crowded each drop is, and how hard it pushes (only crowding pushes: no clumping at the surface)
                    System.Threading.Tasks.Parallel.For(0, n, i =>
                    {
                        _lambda[i] = 0f;
                        if(_state[i] != Air)
                            return;
                        float rho = w0, sum2 = 0f;
                        var grad = Vector3.Zero;
                        int baseIdx = i * MaxNeighbours;
                        for(int k = 0; k < _nbrCount[i]; k++)
                        {
                            int j = _nbr[baseIdx + k];
                            var dv = _p[i] - _p[j];
                            float r2 = dv.LengthSquared();
                            if(r2 >= hh)
                                continue;
                            float q = hh - r2;
                            rho += poly6 * q * q * q;
                            float rl = MathF.Sqrt(r2) + 1e-9f;
                            float hl = h - rl;
                            var gj = dv * (spiky * hl * hl / rl / rho0);
                            grad += gj;
                            sum2 += gj.LengthSquared();
                        }
                        float C = MathF.Max(rho / rho0 - 1f, 0f);
                        _lambda[i] = -C / (grad.LengthSquared() + sum2 + 50f);
                    });
                    System.Threading.Tasks.Parallel.For(0, n, i =>
                    {
                        _dp[i] = Vector3.Zero;
                        if(_state[i] != Air)
                            return;
                        var dp = Vector3.Zero;
                        int baseIdx = i * MaxNeighbours;
                        for(int k = 0; k < _nbrCount[i]; k++)
                        {
                            int j = _nbr[baseIdx + k];
                            var dv = _p[i] - _p[j];
                            float r2 = dv.LengthSquared();
                            if(r2 >= hh)
                                continue;
                            float rl = MathF.Sqrt(r2) + 1e-9f;
                            float hl = h - rl;
                            dp += dv * ((_lambda[i] + _lambda[j]) * spiky * hl * hl / rl / rho0);
                        }
                        _dp[i] = dp;
                    });
                    System.Threading.Tasks.Parallel.For(0, n, i =>
                    {
                        if(_state[i] != Air)
                            return;
                        _p[i] += _dp[i];
                        Collide(i, near, bodySize, r, out _, out _, out _, out _, out _);
                    });
                    // the strings: pulled back when stretched, snapping only when stretched far (further the thicker it is)
                    for(int i = 0; i < n; i++)
                    {
                        int j = _link[i];
                        if(j < 0)
                            continue;
                        bool mi = _state[i] == Air, mj = _state[j] == Air;
                        if(!mi && !mj)
                            continue;
                        var dv = _p[i] - _p[j];
                        float l = dv.Length(), rest = _rest[i];
                        if(l > rest * stringFar)
                        {
                            _link[i] = -1;
                            continue;
                        }
                        if(l <= rest)
                            continue;
                        var c = dv * ((l - rest) / l * stringK);
                        if(mi && mj)
                        {
                            _p[i] -= c * 0.5f;
                            _p[j] += c * 0.5f;
                        }
                        else if(mi)
                            _p[i] -= c;
                        else
                            _p[j] += c;
                    }
                }
                // new speeds; then viscosity (moving with the drops around), cohesion (pulling together)
                for(int i = 0; i < n; i++)
                    if(_state[i] == Air)
                        _v[i] = (_p[i] - _x[i]) / hs;
                System.Threading.Tasks.Parallel.For(0, n, i =>
                {
                    if(_state[i] != Air)
                        return;
                    var vi = _v[i];
                    var mix = Vector3.Zero;
                    var pull = Vector3.Zero;
                    float wsum = 0f;
                    int baseIdx = i * MaxNeighbours;
                    for(int k = 0; k < _nbrCount[i]; k++)
                    {
                        int j = _nbr[baseIdx + k];
                        var dv = _p[i] - _p[j];
                        float r2 = dv.LengthSquared();
                        if(r2 >= hh)
                            continue;
                        float q = hh - r2, w = q * q * q;
                        var vj = _state[j] == Air ? _v[j] : Vector3.Zero;
                        mix += (vj - vi) * w;
                        wsum += w;
                        float rl = MathF.Sqrt(r2);
                        if(rl > d * 0.9f)
                            pull -= dv * (cohesion * MathF.Sin(MathF.PI * (rl - d * 0.9f) / (h - d * 0.9f)) / (rl + 1e-9f));
                    }
                    if(wsum > 0f)
                        mix *= mixK / wsum * MathF.Min(wsum / (hh * hh * hh * 3f), 1f);
                    _newV[i] = vi + mix + pull * hs;
                });
                for(int i = 0; i < n; i++)
                {
                    if(_state[i] != Air)
                        continue;
                    _v[i] = _newV[i];
                    _x[i] = _p[i];
                    // touching the skin: pulled onto it, held from sliding; set on it once slow
                    bool touching = Collide(i, near, bodySize, r, out var hit, out int part, out float t, out var nrm, out int point);
                    _x[i] = _p[i];
                    if(!touching)
                    {
                        if(e.Floor && _hasGround && _x[i].Y < _ground + r * 1.5f)
                        {
                            _x[i] = new Vector3(_x[i].X, _ground + r * 0.5f, _x[i].Z);
                            // (the floor holds whatever lands on it gently)
                            if(_v[i].Length() < 3f)
                            {
                                _state[i] = Puddle;
                                _wn[i] = Vector3.UnitY;
                                _landedAge[i] = 0f;
                                _v[i] = Vector3.Zero;
                            }
                            else
                                _v[i] = new Vector3(_v[i].X * 0.5f, 0f, _v[i].Z * 0.5f);
                        }
                        continue;
                    }
                    var v = _v[i];
                    float vn = Vector3.Dot(v, nrm);
                    var vt = v - nrm * vn;
                    float fr = Math.Clamp(e.Friction, 0f, 1f);
                    // (striking it: the speed into the skin is lost, and most of the speed along it)
                    float hold = vn < -0.3f ? 1f - fr : MathF.Max(1f - fr * 12f * hs, 0f);
                    v = vt * hold - nrm * (Math.Clamp(e.Stickiness, 0f, 1f) * 2f * hs);
                    _v[i] = v;
                    if(v.Length() < settle)
                    {
                        if(point >= 0)
                            StickToSkin(i, hit!, point, r * e.SitOut);
                        else
                        {
                            var at = Closest(hit!.A[part], hit.B[part], _x[i], out _);
                            StickTo(i, hit, part, t, at + nrm * (hit.R[part] * bodySize + r * e.SitOut), nrm);
                        }
                    }
                }
            }

            // age, fade and take away
            float stay = e.Stay, fade = MathF.Max(e.FadeTime, 0.01f);
            FlyingCount = LandedCount = 0;
            for(int i = _n - 1; i >= 0; i--)
            {
                _age[i] += dt;
                if(_state[i] == Air)
                {
                    if(_age[i] > 20f || (_hasGround && _x[i].Y < _ground - 3f) || (_hasAt && Vector3.DistanceSquared(_x[i], _at) > 900f))
                        _state[i] = Free;
                    else
                        FlyingCount++;
                    continue;
                }
                if(_state[i] == Puddle)
                    _landedAge[i] += dt;
                if(stay > 0f && _landedAge[i] > stay + fade)
                    _state[i] = Free;
                else
                    LandedCount++;
            }
            Shapes(h);
            Compact();
        }

        // Each flying drop's shape from the drops around it (Yu and Turk): flat in a sheet, long along a string, round
        // alone; and drawn a little towards their middle, which smooths the surface.
        private void Shapes(float h)
        {
            int n = _n;
            float h2 = h * h;
            System.Threading.Tasks.Parallel.For(0, n, i =>
            {
                _shaped[i] = false;
                _shapeC[i] = _x[i];
                if(_state[i] != Air || _nbrCount[i] < 6)
                    return;
                var xi = _x[i];
                var mean = xi;
                float ws = 1f;
                int baseIdx = i * MaxNeighbours;
                for(int k = 0; k < _nbrCount[i]; k++)
                {
                    int j = _nbr[baseIdx + k];
                    float r2 = Vector3.DistanceSquared(xi, _x[j]);
                    if(r2 >= h2)
                        continue;
                    float w = 1f - MathF.Pow(MathF.Sqrt(r2) / h, 3);
                    mean += _x[j] * w;
                    ws += w;
                }
                mean /= ws;
                // the spread of the drops around it
                float c00 = 0, c01 = 0, c02 = 0, c11 = 0, c12 = 0, c22 = 0;
                void Take(Vector3 q, float w)
                {
                    var d = q - mean;
                    c00 += w * d.X * d.X; c01 += w * d.X * d.Y; c02 += w * d.X * d.Z;
                    c11 += w * d.Y * d.Y; c12 += w * d.Y * d.Z; c22 += w * d.Z * d.Z;
                }
                Take(xi, 1f);
                for(int k = 0; k < _nbrCount[i]; k++)
                {
                    int j = _nbr[baseIdx + k];
                    float r2 = Vector3.DistanceSquared(xi, _x[j]);
                    if(r2 >= h2)
                        continue;
                    Take(_x[j], 1f - MathF.Pow(MathF.Sqrt(r2) / h, 3));
                }
                var cov = new float[] { c00 / ws, c01 / ws, c02 / ws, c01 / ws, c11 / ws, c12 / ws, c02 / ws, c12 / ws, c22 / ws };
                Eigen3(cov, out var vals, out var v0, out var v1, out var v2);
                float mx = MathF.Max(vals.X, MathF.Max(vals.Y, vals.Z));
                if(mx <= 1e-14f)
                    return;
                float s0 = MathF.Sqrt(MathF.Max(vals.X, mx / 16f)), s1 = MathF.Sqrt(MathF.Max(vals.Y, mx / 16f)), s2 = MathF.Sqrt(MathF.Max(vals.Z, mx / 16f));
                float g = MathF.Cbrt(s0 * s1 * s2);
                s0 = Math.Clamp(s0 / g, 0.4f, 2.5f);
                s1 = Math.Clamp(s1 / g, 0.4f, 2.5f);
                s2 = Math.Clamp(s2 / g, 0.4f, 2.5f);
                _ax1[i] = v0 * s0;
                _ax2[i] = v1 * s1;
                _ax3[i] = v2 * s2;
                _shapeC[i] = xi * 0.1f + mean * 0.9f;
                _shaped[i] = MathUtil.IsFinite(_ax1[i]) && MathUtil.IsFinite(_ax2[i]) && MathUtil.IsFinite(_ax3[i]) && MathUtil.IsFinite(_shapeC[i]);
                if(!_shaped[i])
                    _shapeC[i] = xi;
            });
        }

        // Eigenvalues and eigenvectors of a symmetric 3x3 (row-major), by Jacobi rotations.
        private static void Eigen3(float[] m, out Vector3 values, out Vector3 v0, out Vector3 v1, out Vector3 v2)
        {
            Span<float> a = stackalloc float[9];
            Span<float> v = stackalloc float[9];
            for(int k = 0; k < 9; k++)
            {
                a[k] = m[k];
                v[k] = k % 4 == 0 ? 1f : 0f;
            }
            for(int sweep = 0; sweep < 8; sweep++)
            {
                for(int pair = 0; pair < 3; pair++)
                {
                    int p = pair == 2 ? 1 : 0, q = pair == 0 ? 1 : 2;
                    float apq = a[p * 3 + q];
                    if(MathF.Abs(apq) < 1e-20f)
                        continue;
                    float th = (a[q * 3 + q] - a[p * 3 + p]) / (2f * apq);
                    float t = (th >= 0 ? 1f : -1f) / (MathF.Abs(th) + MathF.Sqrt(th * th + 1f));
                    float c = 1f / MathF.Sqrt(t * t + 1f), s = t * c;
                    for(int k = 0; k < 3; k++)
                    {
                        float akp = a[k * 3 + p], akq = a[k * 3 + q];
                        a[k * 3 + p] = c * akp - s * akq;
                        a[k * 3 + q] = s * akp + c * akq;
                    }
                    for(int k = 0; k < 3; k++)
                    {
                        float apk = a[p * 3 + k], aqk = a[q * 3 + k];
                        a[p * 3 + k] = c * apk - s * aqk;
                        a[q * 3 + k] = s * apk + c * aqk;
                    }
                    for(int k = 0; k < 3; k++)
                    {
                        float vkp = v[k * 3 + p], vkq = v[k * 3 + q];
                        v[k * 3 + p] = c * vkp - s * vkq;
                        v[k * 3 + q] = s * vkp + c * vkq;
                    }
                }
            }
            values = new Vector3(a[0], a[4], a[8]);
            v0 = new Vector3(v[0], v[3], v[6]);
            v1 = new Vector3(v[1], v[4], v[7]);
            v2 = new Vector3(v[2], v[5], v[8]);
        }

        private static Vector3 Closest(Vector3 a, Vector3 b, Vector3 p, out float t)
        {
            var ab = b - a;
            float len2 = ab.LengthSquared();
            t = len2 > 1e-12f ? Math.Clamp(Vector3.Dot(p - a, ab) / len2, 0f, 1f) : 0f;
            return a + ab * t;
        }

        // Out of any body it's in (onto the surface); true if it's touching one (within a drop of the skin).
        private bool Collide(int i, List<Body> near, float bodySize, float r, out Body? hit, out int part, out float t, out Vector3 normal, out int point)
        {
            hit = null;
            part = -1;
            t = 0f;
            point = -1;
            normal = Vector3.UnitY;
            bool insideSelf = false;
            var q = _p[i];
            foreach(var b in near)
            {
                float reach = (b.Reach + r) * bodySize;
                if(Vector3.DistanceSquared(q, b.Center) > reach * reach)
                    continue;
                bool self = b.Address == _source;
                // its real skin: the nearest point of its models, the drop kept out along that point's normal
                if(b.Skin is { } skin)
                {
                    int k = ClimaxSurface.Nearest(skin, q, r * 2.5f + 0.006f, out float outward);
                    if(k < 0 || outward >= r * 2.5f)
                        continue;
                    if(self && !_clearOfSelf[i])
                    {
                        if(outward < r)
                            insideSelf = true;
                        continue;
                    }
                    var nk = skin.Nrm[k];
                    // (well inside: through a thin part or from behind; left alone rather than pulled through)
                    if(outward < -0.03f)
                        continue;
                    if(outward < r)
                        q += nk * (r - outward);
                    if(hit == null || outward < r)
                    {
                        hit = b;
                        point = k;
                        normal = nk;
                    }
                    continue;
                }
                for(int k = 0; k < Parts.Length; k++)
                {
                    if(!b.Has[k])
                        continue;
                    float R = b.R[k] * bodySize + r;
                    var c = Closest(b.A[k], b.B[k], q, out float tk);
                    var off = q - c;
                    float dist = off.Length();
                    if(dist >= R + r * 1.5f)
                        continue;
                    if(self && !_clearOfSelf[i])
                    {
                        if(dist < R)
                            insideSelf = true;
                        continue;
                    }
                    var nk = dist > 1e-6f ? off / dist : Vector3.UnitY;
                    if(dist < R)
                        q = c + nk * R;
                    if(hit == null || dist < R)
                    {
                        hit = b;
                        part = k;
                        t = tk;
                        normal = nk;
                    }
                }
            }
            if(!insideSelf)
                _clearOfSelf[i] = true;
            _p[i] = q;
            return hit != null;
        }

        // A goopy drip: a new drop hanging from this one on a string, stretching under its own weight until the string snaps
        // (this one stays where it is, and drips only once).
        private void Drip(int i, ClimaxEmitter e, Vector3 at, Vector3 n)
        {
            _dripped[i] = true;
            float r = MathF.Max(e.Drop, 0.0015f);
            int k = New(at + n * (r * 1.5f) + new Vector3(0, -r, 0), new Vector3(0, -0.03f, 0));
            if(k < 0)
                return;
            _clearOfSelf[k] = true;
            _dripped[k] = true;
            float visc = Math.Clamp(e.Viscosity, 0f, 1f), far = 2f + 10f * visc;
            _link[k] = i;
            _rest[k] = MathF.Max(e.StringLength / far, r);
        }

        // Set on the real skin: on one of its points (moving with it from now on), a little way out.
        private void StickToSkin(int i, Body b, int point, float off)
        {
            var skin = b.Skin!;
            _state[i] = Stuck;
            _body[i] = b.Address;
            _bone[i] = -1;
            _point[i] = point;
            _pointOff[i] = off;
            _x[i] = skin.Pos[point] + skin.Nrm[point] * off;
            _wn[i] = skin.Nrm[point];
            _p[i] = _x[i];
            _v[i] = Vector3.Zero;
            _landedAge[i] = 0f;
        }

        private void StickTo(int i, Body b, int part, float t, Vector3 at, Vector3 n)
        {
            _point[i] = -1;
            int bone = t > 0.5f ? b.BoneOfB[part] : b.BoneOfA[part];
            if(bone < 0)
                return;
            var f = BoneFrame(b, bone);
            var inv = Quaternion.Inverse(f.Rotation);
            _state[i] = Stuck;
            _body[i] = b.Address;
            _bone[i] = bone;
            _local[i] = Vector3.Transform(at - f.Position, inv) / b.Scale;
            _localNormal[i] = Vector3.Transform(n, inv);
            _x[i] = at;
            _wn[i] = n;
            _p[i] = at;
            _v[i] = Vector3.Zero;
            _landedAge[i] = 0f;
        }

        // After running along the skin: back onto the nearest part's surface (maybe another part's, another bone's).
        private void Resnap(int i, Body b, ClimaxEmitter e, ref Vector3 at, ref Vector3 n, float bodySize, float r)
        {
            float best = float.MaxValue;
            int bestPart = -1;
            float bestT = 0f;
            Vector3 bestC = default;
            for(int p = 0; p < Parts.Length; p++)
            {
                if(!b.Has[p])
                    continue;
                var c = Closest(b.A[p], b.B[p], at, out float t);
                float dd = Vector3.Distance(at, c) - b.R[p] * bodySize;
                if(dd < best)
                {
                    best = dd;
                    bestPart = p;
                    bestT = t;
                    bestC = c;
                }
            }
            if(bestPart < 0)
                return;
            var off = at - bestC;
            n = off.LengthSquared() > 1e-12f ? Vector3.Normalize(off) : n;
            at = bestC + n * (b.R[bestPart] * bodySize + r * e.SitOut);
            float age = _landedAge[i];
            StickTo(i, b, bestPart, bestT, at, n);
            _landedAge[i] = age;
        }

        // ---- Drawing: each drop (in the air: a ball; landed: laid on the skin, a negative radius)

        public void Emit(ClimaxService s, ClimaxEmitter e)
        {
            // (drawn a little bigger than they are, so neighbours overlap into one surface)
            float size = MathF.Max(s._config.ClimaxLook?.DrawSize ?? 1f, 0.05f) * 2.4f * MathF.Max(e.Drop, 0.0015f);
            float stay = e.Stay, fade = MathF.Max(e.FadeTime, 0.01f);
            // (a drop on a stretched string is drawn as thin as the string: no beads along it)
            var thinOf = new float[_n];
            Array.Fill(thinOf, 1f);
            for(int i = 0; i < _n; i++)
            {
                int j = _link[i];
                if(j < 0 || j >= _n)
                    continue;
                float l = Vector3.Distance(_x[i], _x[j]);
                float thin = Math.Clamp(MathF.Sqrt(MathF.Max(_rest[i], 1e-6f) / MathF.Max(l, 1e-6f)), 0.3f, 1f);
                thinOf[i] = MathF.Min(thinOf[i], thin);
                thinOf[j] = MathF.Min(thinOf[j], thin);
            }
            for(int i = 0; i < _n; i++)
            {
                float r = size;
                bool landed = _state[i] != Air;
                if(landed)
                    r *= e.Coat * (_state[i] == Puddle ? MathF.Max(e.PuddleSpread, 0.2f) / 1.4f : 1f);
                if(stay > 0f && landed && _landedAge[i] > stay)
                    r *= MathF.Max(1f - (_landedAge[i] - stay) / fade, 0f);
                if(r <= 1e-5f)
                    continue;
                if(landed)
                {
                    s.AddCoat(_x[i], r, _wn[i]);
                    continue;
                }
                r *= thinOf[i];
                if(_shaped[i])
                    s.AddEllipsoid(_shapeC[i], _ax1[i] * r, _ax2[i] * r, _ax3[i] * r);
                else
                    s.Add(_x[i], r, _x[i], r);
            }
            // the strings between drops (while either is in the air), thinner the more they're stretched
            for(int i = 0; i < _n; i++)
            {
                int j = _link[i];
                if(j < 0 || j >= _n || (_state[i] != Air && _state[j] != Air))
                    continue;
                var a = _shaped[i] ? _shapeC[i] : _x[i];
                var b = _shaped[j] ? _shapeC[j] : _x[j];
                float l = Vector3.Distance(a, b);
                float thin = Math.Clamp(MathF.Sqrt(MathF.Max(_rest[i], 1e-6f) / MathF.Max(l, 1e-6f)), 0.3f, 1f);
                if(l > size * 0.5f)
                    s.Add(a, size * thin, b, size * thin);
            }
        }
    }
}
