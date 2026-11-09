using System;
using System.Collections.Generic;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;

namespace Limber.Rig;

/// <summary>World collision settings (Extras page, Ragdoll): which of the game's floors count, and how many checks a frame.</summary>
public sealed class WorldCollisionSettings
{
    /// <summary>Ragdoll lands on the real floor, stairs, beds and furniture instead of a flat floor where they stood.</summary>
    public bool Ragdoll { get; set; }
    /// <summary>Also stop at walls and furniture sides while falling (a check along each part's movement).</summary>
    public bool Walls { get; set; } = true;
    /// <summary>Only surfaces of some kinds (the game's own surface filter), so invisible walls are skipped.</summary>
    public bool UseFilter { get; set; } = true;
    /// <summary>The surface filter: which surface bits are looked at, and what they must be (as most of the game's own
    /// checks use: 0x4000 / 0x4000).</summary>
    public ulong FilterMask { get; set; } = 0x4000;
    public ulong FilterValue { get; set; } = 0x4000;
    /// <summary>The most checks against the world in one frame (all of Limber's together).</summary>
    public int MaxCastsPerFrame { get; set; } = 48;

    public WorldCollisionSettings Copy() => (WorldCollisionSettings)MemberwiseClone();
}

/// <summary>One hit against the game world.</summary>
public struct WorldHit
{
    /// <summary>Where it touched (as the game reports it).</summary>
    public Vector3 Point;
    /// <summary>The surface's facing (towards where the check came from).</summary>
    public Vector3 Normal;
    /// <summary>A point on the surface's plane (one of the triangle's corners when the game gives them).</summary>
    public Vector3 PlanePoint;
    /// <summary>How far the check went before it touched.</summary>
    public float Distance;
    public ulong Material;
}

/// <summary>
/// Checks against the game's own world collision (terrain, level geometry, placed housing furniture; never characters):
/// a sphere moved along a line and stopped where it first touches, and plain rays. The game's BGCollisionModule
/// (Framework+0x2B58) does the work and takes its own lock, so this is safe from the bone update. Only nearby collision
/// is loaded (about 120 m around the camera). Limber's spawned props are never part of it (they stay visual only).
/// Calls are limited per frame (Settings.MaxCastsPerFrame); a check over the limit just reports nothing.
/// </summary>
public static unsafe class WorldCollision
{
    // The game's checks (2026.10.16). Each signature is a call to it (unique), followed by Dalamud.
    // Ray: (module, hit, Vector3* origin, Vector3* direction, float maxDistance, int layerMask)                @0x1405f14a0
    private const string RaySig = "E8 ?? ?? ?? ?? 84 C0 74 43 F3 0F 10 44 24 60";
    // Sphere sweep: origin is a Vector4* (xyz + radius in w)                                                 @0x1405f1570
    private const string SweepSig = "E8 ?? ?? ?? ?? 84 C0 74 5F F3 0F 10 44 24 50";
    // Ray with the surface filter (mask, value)                                                              @0x1405f1640
    private const string RayFilterSig = "E8 ?? ?? ?? ?? 84 C0 41 0F B6 D6";
    // Sphere sweep with the surface filter (origin a Vector4*)                                               @0x1405f1760
    private const string SweepFilterSig = "E8 ?? ?? ?? ?? 4C 8B 45 ?? 0F B6 55";

    private static delegate* unmanaged<BGCollisionModule*, RaycastHit*, Vector3*, Vector3*, float, int, byte> _ray;
    private static delegate* unmanaged<BGCollisionModule*, RaycastHit*, Vector4*, Vector3*, float, int, byte> _sweep;
    private static delegate* unmanaged<BGCollisionModule*, RaycastHit*, Vector3*, Vector3*, float, int, RaycastMaterialFilter*, byte> _rayFilter;
    private static delegate* unmanaged<BGCollisionModule*, RaycastHit*, Vector4*, Vector3*, float, int, RaycastMaterialFilter*, byte> _sweepFilter;

    private const int LayerWorld = 1; // (every one of the game's own checks uses layer 1)

    private static bool _tried;
    private static readonly object Sync = new();

    /// <summary>Why world collision can't be used ("" when it can).</summary>
    public static string Problem { get; private set; } = "";

    /// <summary>The settings (set by the plugin).</summary>
    public static Func<WorldCollisionSettings> Settings = () => new WorldCollisionSettings();

    // ---- The per-frame limit

    private static DateTime _frame;
    private static int _casts;
    /// <summary>Checks made in the last frame (for the readout).</summary>
    public static int CastsLastFrame { get; private set; }
    /// <summary>Checks refused for the limit in the last frame.</summary>
    public static int RefusedLastFrame { get; private set; }
    private static int _refused;

    private static bool TakeCast()
    {
        lock(Sync)
        {
            var now = Plugin.Framework.LastUpdate;
            if(now != _frame)
            {
                _frame = now;
                CastsLastFrame = _casts;
                RefusedLastFrame = _refused;
                _casts = 0;
                _refused = 0;
            }
            if(_casts >= Math.Clamp(Settings().MaxCastsPerFrame, 1, 1000))
            {
                _refused++;
                return false;
            }
            _casts++;
            return true;
        }
    }

    /// <summary>Casts left this frame (roughly; for spreading checks over frames).</summary>
    public static int CastsLeft
    {
        get
        {
            lock(Sync)
            {
                if(Plugin.Framework.LastUpdate != _frame)
                    return Math.Clamp(Settings().MaxCastsPerFrame, 1, 1000);
                return Math.Max(0, Math.Clamp(Settings().MaxCastsPerFrame, 1, 1000) - _casts);
            }
        }
    }

    // ---- Setting up

    /// <summary>Finds the game's checks (once). False (with Problem set) if they can't be used.</summary>
    public static bool Ready()
    {
        if(_tried)
            return Problem.Length == 0;
        lock(Sync)
        {
            if(_tried)
                return Problem.Length == 0;
            try
            {
                nint Find(string sig, string what)
                {
                    var all = Plugin.SigScanner.ScanAllText(sig);
                    if(all.Length != 1)
                        throw new InvalidOperationException($"{what}: the pattern matches {all.Length} places");
                    // (follow the call to the function)
                    var at = all[0];
                    return at + 5 + *(int*)(at + 1);
                }
                _ray = (delegate* unmanaged<BGCollisionModule*, RaycastHit*, Vector3*, Vector3*, float, int, byte>)Find(RaySig, "ray");
                _sweep = (delegate* unmanaged<BGCollisionModule*, RaycastHit*, Vector4*, Vector3*, float, int, byte>)Find(SweepSig, "sphere sweep");
                _rayFilter = (delegate* unmanaged<BGCollisionModule*, RaycastHit*, Vector3*, Vector3*, float, int, RaycastMaterialFilter*, byte>)Find(RayFilterSig, "ray (filtered)");
                _sweepFilter = (delegate* unmanaged<BGCollisionModule*, RaycastHit*, Vector4*, Vector3*, float, int, RaycastMaterialFilter*, byte>)Find(SweepFilterSig, "sphere sweep (filtered)");
                Problem = "";
            }
            catch(Exception e)
            {
                Problem = "Couldn't find the game's world collision checks (this game version needs an updated build): " + e.Message;
                Plugin.Log.Warning("Limber: " + Problem);
            }
            _tried = true;
            return Problem.Length == 0;
        }
    }

    private static BGCollisionModule* Module
    {
        get
        {
            var f = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance();
            if(f == null)
                return null;
            var m = f->BGCollisionModule;
            if(m == null || m->ShuttingDown || m->SceneManager == null)
                return null;
            return m;
        }
    }

    // ---- The checks

    /// <summary>
    /// A sphere of this radius moved from one point to another (world space): true where it first touches the world.
    /// The hit's Normal faces back along the movement; PlanePoint is on the touched surface.
    /// </summary>
    public static bool Sweep(Vector3 from, Vector3 to, float radius, out WorldHit hit)
    {
        hit = default;
        if(!Ready() || !TakeCast())
            return false;
        var m = Module;
        if(m == null)
            return false;
        var d = to - from;
        float length = d.Length();
        if(length < 1e-5f || !float.IsFinite(length))
            return false;
        var dir = d / length;
        var origin = new Vector4(from, MathF.Max(radius, 0.001f));
        var raw = new RaycastHit();
        var s = Settings();
        byte found;
        try
        {
            if(s.UseFilter)
            {
                var filter = new RaycastMaterialFilter { Mask = s.FilterMask, Value = s.FilterValue };
                found = _sweepFilter(m, &raw, &origin, &dir, length, LayerWorld, &filter);
            }
            else
                found = _sweep(m, &raw, &origin, &dir, length, LayerWorld);
        }
        catch
        {
            return false;
        }
        if(found == 0)
            return false;
        return Fill(raw, dir, from, radius, out hit);
    }

    /// <summary>A plain ray (world space) along a direction, up to a distance.</summary>
    public static bool Ray(Vector3 from, Vector3 direction, float maxDistance, out WorldHit hit)
    {
        hit = default;
        if(!Ready() || !TakeCast())
            return false;
        var m = Module;
        if(m == null || direction.LengthSquared() < 1e-10f || !(maxDistance > 0f))
            return false;
        var dir = Vector3.Normalize(direction);
        var origin = from;
        var raw = new RaycastHit();
        var s = Settings();
        byte found;
        try
        {
            if(s.UseFilter)
            {
                var filter = new RaycastMaterialFilter { Mask = s.FilterMask, Value = s.FilterValue };
                found = _rayFilter(m, &raw, &origin, &dir, maxDistance, LayerWorld, &filter);
            }
            else
                found = _ray(m, &raw, &origin, &dir, maxDistance, LayerWorld);
        }
        catch
        {
            return false;
        }
        if(found == 0)
            return false;
        return Fill(raw, dir, from, 0f, out hit);
    }

    private static bool Fill(in RaycastHit raw, Vector3 dir, Vector3 from, float radius, out WorldHit hit)
    {
        hit = default;
        if(!float.IsFinite(raw.Distance) || raw.Distance < 0f)
            return false;
        // The surface's facing: from the touched triangle when the game gives it (not every kind of collider fills
        // the normal), else the reported normal, else straight back along the check.
        var n = Vector3.Cross(raw.V2 - raw.V1, raw.V3 - raw.V1);
        bool triangle = n.LengthSquared() > 1e-12f && float.IsFinite(n.X);
        if(triangle)
            n = Vector3.Normalize(n);
        else if(raw.Normal.LengthSquared() > 1e-6f && float.IsFinite(raw.Normal.X))
            n = Vector3.Normalize(raw.Normal);
        else
            n = -dir;
        if(Vector3.Dot(n, dir) > 0f)
            n = -n;
        hit.Normal = n;
        hit.Distance = raw.Distance;
        hit.Material = raw.Material;
        hit.Point = raw.Point;
        // A point on the surface: a triangle corner when there is one (whatever the reported point means for a
        // sphere), else where the sphere's centre stopped, moved out to its surface.
        hit.PlanePoint = triangle ? raw.V1 : from + dir * raw.Distance - n * radius;
        return float.IsFinite(hit.PlanePoint.X) && float.IsFinite(hit.PlanePoint.Y) && float.IsFinite(hit.PlanePoint.Z);
    }

    // ---- The ground below a point (for soft bodies and anything else; cached)

    private readonly record struct Cell(int X, int Z, int Y);
    private static readonly Dictionary<Cell, (float Y, Vector3 Normal, bool Found, long At)> Ground = new();

    /// <summary>
    /// The floor's height under a world point (looked for from a little above it down to maxDown below), and its facing.
    /// Cached on a 20 cm grid for half a second, so it can be asked every frame. False when there's no floor within
    /// reach, world collision can't be used, or this frame's checks have run out (then try again next frame).
    /// Not used by Limber's soft bodies yet (they keep their own floor); here for when they do.
    /// </summary>
    public static bool GroundBelow(Vector3 position, out float groundY, out Vector3 normal, float maxDown = 3f)
    {
        groundY = 0f;
        normal = Vector3.UnitY;
        if(!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
            return false;
        var cell = new Cell((int)MathF.Floor(position.X / 0.2f), (int)MathF.Floor(position.Z / 0.2f), (int)MathF.Floor(position.Y / 0.5f));
        long now = Environment.TickCount64;
        lock(Ground)
        {
            if(Ground.TryGetValue(cell, out var known) && now - known.At < 500)
            {
                groundY = known.Y;
                normal = known.Normal;
                return known.Found;
            }
        }
        const float Lift = 0.3f;
        var from = position + Vector3.UnitY * Lift;
        if(!Ready() || CastsLeft <= 0)
            return false;
        bool found = Sweep(from, from - Vector3.UnitY * (maxDown + Lift), 0.02f, out var hit) && hit.Distance > 1e-4f;
        if(found)
        {
            normal = hit.Normal;
            groundY = from.Y - hit.Distance - 0.02f;
        }
        lock(Ground)
        {
            if(Ground.Count > 4096)
                Ground.Clear();
            Ground[cell] = (groundY, normal, found, now);
        }
        return found;
    }

    /// <summary>Forgets the cached floors (after a zone change, or when the filter changes).</summary>
    public static void ClearCache()
    {
        lock(Ground)
            Ground.Clear();
    }
}
