using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace Limber.Rig;

/// <summary>The motions the engine layer can play (Experimental > Engine animation layer).</summary>
public enum EngineLayerMotion
{
    Thrust,
    Pump,
    Bounce,
    Circle,
}

/// <summary>The engine animation layer's settings (one, for the chosen character).</summary>
public sealed class EngineLayerSettings
{
    /// <summary>On (never on when Limber starts).</summary>
    public bool Enabled { get; set; }
    public EngineLayerMotion Motion { get; set; } = EngineLayerMotion.Thrust;
    /// <summary>Strokes a minute.</summary>
    public float Speed { get; set; } = 80f;
    /// <summary>How far the hips move (centimetres).</summary>
    public float Move { get; set; } = 3f;
    /// <summary>How far the hips tilt (degrees).</summary>
    public float Tilt { get; set; } = 6f;
    /// <summary>Which of the hip bone's own axes it moves along / tilts around (0 X, 1 Y, 2 Z).</summary>
    public int MoveAxis { get; set; } = 2;
    public int TiltAxis { get; set; }
    /// <summary>The lower back turns the other way a little (the chest stays steadier).</summary>
    public bool Spine { get; set; } = true;
    /// <summary>How strongly it's mixed in (0..1).</summary>
    public float Weight { get; set; } = 1f;
    /// <summary>Seconds to fade in and out.</summary>
    public float FadeSeconds { get; set; } = 0.5f;
    /// <summary>How the game mixes it in: 2 additive (the usual), 1 the older additive.</summary>
    public int BlendHint { get; set; } = 2;

    public EngineLayerSettings Copy() => (EngineLayerSettings)MemberwiseClone();
}

/// <summary>
/// Experimental: Limber's motion played by the game's own animation system, as one more animation on the character's
/// body (an additive Havok animation control on the body skeleton's first animation layer, like an emote's), so the game
/// mixes it with whatever is playing, before its physics, instead of Limber setting bones afterwards.
/// Limber builds a tiny two-frame animation for a few bones (hips, lower back) and rewrites its frames every frame;
/// the control's weight fades it in and out. Everything Limber makes is its own memory and is never freed (the game
/// can't free it either: its size field is 0, which Havok takes as "not mine"). Added, rewritten and removed only on
/// the framework thread, before the game samples animations. Never calls the game's PlayAnimation.
/// See <see cref="Diagnostics"/> for what to check in game.
/// </summary>
public sealed unsafe class EngineLayerService
{
    // Havok object sizes (2014, from the game's own destructors / constructors).
    private const int AnimationSize = 0x58;  // hkaInterleavedUncompressedAnimation
    private const int BindingSize = 0x58;    // hkaAnimationBinding
    private const int ControlSize = 0xC0;    // hkaDefaultAnimationControl
    private const int QsSize = 48;           // hkQsTransformf: translation, rotation, scale (each 16 bytes)
    private const uint DontDeallocate = 0x80000000;

    // Functions (2026.10.16), each signature unique.
    // hkaDefaultAnimationControl(binding, bool startEasedIn, int maxCycles)                      @0x141f01960
    private const string ControlCtorSig = "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 44 88 44 24 ?? 57 48 83 EC 20 41 8B F1 48 8B FA 48 8B D9 E8";
    // hkaAnimatedSkeleton::addAnimationControl / removeAnimationControl                          @0x141f03150 / @0x141f03230
    private const string AddSig = "48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC 20 8B 41 2C 48 8B DA";
    private const string RemoveSig = "40 53 48 83 EC 20 48 63 41 28 45 33 D2";

    private delegate* unmanaged<nint, nint, byte, int, nint> _controlCtor;
    private delegate* unmanaged<nint, nint, void> _add;
    private delegate* unmanaged<nint, nint, void> _remove;
    private nint _vtAnimation, _vtBinding, _vtControl, _vtAnimatedSkeleton;
    private bool _resolved;

    /// <summary>Why the layer can't run ("" when it can).</summary>
    public string Problem { get; private set; } = "";
    /// <summary>What it's doing now.</summary>
    public string Status { get; private set; } = "Off.";

    // The one layer (built once; reused for each character it's put on).
    private nint _animation, _binding, _control, _transforms, _boneIndices;
    private int _tracks;
    private string[] _trackBones = [];
    private int _blendHint = -1;

    // Where it's on now.
    private nint _onCharacter, _onDraw, _onAnimatedSkeleton;
    private float _level;
    private double _phase;
    private int _attaches;
    private const int MaxAttaches = 64; // (each re-add after a redraw counts; a safety stop)

    private readonly Func<EngineLayerSettings> _settings;

    public EngineLayerService(Func<EngineLayerSettings> settings) => _settings = settings;

    // ---- Each frame (framework thread)

    /// <summary>Runs the layer on this character (0: none). Framework thread.</summary>
    public void Update(bool here, double seconds, nint address)
    {
        var s = _settings();
        float dt = (float)Math.Clamp(seconds, 0.0, 0.25);
        bool want = here && s.Enabled && address != 0 && Limber.Koi.Safety.AdultAllowed(address);
        try
        {
            // Still on its body? (gone with a redraw or the character leaving: the game dropped it with the body)
            if(_onAnimatedSkeleton != 0)
            {
                nint nowDraw = 0;
                var now = Present(_onCharacter) ? BodyAnimatedSkeleton(_onCharacter, out nowDraw) : 0;
                if(now == 0 || now != _onAnimatedSkeleton || nowDraw != _onDraw || !Contains(now, _control))
                {
                    _onAnimatedSkeleton = _onDraw = _onCharacter = 0;
                    _level = 0f;
                }
                else if(_onCharacter != address)
                    Detach(); // (another character chosen: off the old one at once)
            }
            float target = want ? Math.Clamp(s.Weight, 0f, 1f) : 0f;
            float fade = MathF.Max(s.FadeSeconds, 0.01f);
            _level = _level < target ? MathF.Min(target, _level + dt / fade) : MathF.Max(target, _level - dt / fade);

            // (off: taken off straight away, not faded out: fading kept writing the motion while it went)
            if(!want)
            {
                Detach();
                if(Problem.Length == 0)
                    Status = s.Enabled ? "Ready (choose an adult character; GPose or 'outside GPose' on)." : "Off.";
                return;
            }
            if(!Resolve())
            {
                Status = "Can't run: " + Problem;
                return;
            }
            if(_onAnimatedSkeleton == 0)
            {
                nint draw = 0;
                var animated = Present(address) ? BodyAnimatedSkeleton(address, out draw) : 0;
                if(animated == 0)
                {
                    Status = "The character has no body animation yet (loading?).";
                    return;
                }
                if(!Attach(address, draw, animated, s))
                    return;
            }
            Write(s, dt);
            Status = $"Playing on the body's first animation layer, at {_level * 100f:0}%.";
        }
        catch(Exception e)
        {
            Problem = "Stopped after an error: " + e.Message;
            Status = "Stopped.";
            Plugin.Log.Warning(e, "Limber: engine animation layer");
            s.Enabled = false;
            try { Detach(); } catch { }
        }
    }

    /// <summary>Whether a character is still in the game's object list.</summary>
    private static bool Present(nint address)
    {
        if(address == 0)
            return false;
        foreach(var o in Plugin.ObjectTable)
            if(o != null && o.Address == address)
                return true;
        return false;
    }

    /// <summary>Stop button: off at once, and taken off the character (framework or UI thread, both the game's).</summary>
    public void Stop()
    {
        _settings().Enabled = false;
        try
        {
            Detach();
        }
        catch
        {
        }
        Status = "Stopped.";
    }

    /// <summary>Takes the layer off (framework thread): only from a body it's still on.</summary>
    public void Detach()
    {
        if(_onAnimatedSkeleton == 0 || _control == 0)
        {
            _onAnimatedSkeleton = _onDraw = _onCharacter = 0;
            return;
        }
        try
        {
            // Only if the character still has that body and the control is still in its list (else the game has
            // already dropped it with the body).
            nint draw = 0;
            var animated = Present(_onCharacter) ? BodyAnimatedSkeleton(_onCharacter, out draw) : 0;
            if(animated != 0 && animated == _onAnimatedSkeleton && draw == _onDraw && Contains(animated, _control))
                _remove(animated, _control);
        }
        finally
        {
            _onAnimatedSkeleton = _onDraw = _onCharacter = 0;
            _level = 0f;
        }
    }

    private bool Attach(nint address, nint draw, nint animated, EngineLayerSettings s)
    {
        if(_attaches >= MaxAttaches)
        {
            Problem = "Put on too many times this session (a safety stop); restart Limber to use it again.";
            return false;
        }
        // The animated skeleton must be what it should be, with room in its list.
        if(!Read(animated, out nint vt) || vt != _vtAnimatedSkeleton)
        {
            Status = "The body's animation layer isn't what's expected: not changed.";
            return false;
        }
        if(!Read(animated + 0x28, out int count) || count < 0 || count > 15)
        {
            Status = $"The body's animation layer has {count} animations: not changed.";
            return false;
        }
        if(!Read(animated + 0x30, out nint skeleton) || skeleton == 0)
            return false;
        var bones = BoneNames(skeleton);
        if(bones.Count == 0)
            return false;
        if(!Build(bones, s))
            return false;
        // Its own listeners from a body it was on before: dropped (that body is gone).
        *(int*)(_control + 0x48) = 0;
        *(float*)(_control + 0x14) = 0f;
        *(float*)(_control + 0x58) = 0f;
        _add(animated, _control);
        _attaches++;
        _onCharacter = address;
        _onDraw = draw;
        _onAnimatedSkeleton = animated;
        return true;
    }

    // ---- Building the animation, binding and control (once)

    private bool Build(List<string> skeletonBones, EngineLayerSettings s)
    {
        string[] wanted = s.Spine ? ["j_kosi", "j_sebo_a"] : ["j_kosi"];
        var indices = new List<short>();
        var names = new List<string>();
        foreach(var name in wanted)
        {
            int i = skeletonBones.IndexOf(name);
            if(i >= 0)
            {
                indices.Add((short)i);
                names.Add(name);
            }
        }
        if(indices.Count == 0)
        {
            Status = "The body skeleton has no hip bone (j_kosi).";
            return false;
        }
        int hint = s.BlendHint is 1 or 2 ? s.BlendHint : 2;
        if(_control != 0 && _tracks == indices.Count && SameBones(names) && _blendHint == hint)
        {
            // (the same bones: only the bone numbers may differ on another skeleton)
            for(int t = 0; t < _tracks; t++)
                ((short*)_boneIndices)[t] = indices[t];
            return true;
        }
        // A new layer (the old one, if any, is never freed: the game may still hold it somewhere).
        int tracks = indices.Count;
        var animation = Alloc(AnimationSize);
        var transforms = Alloc(QsSize * tracks * 2);
        var boneIndices = Alloc(Math.Max(16, 2 * tracks));
        var binding = Alloc(BindingSize);
        var control = Alloc(ControlSize);
        for(int t = 0; t < tracks; t++)
            ((short*)boneIndices)[t] = indices[t];
        for(int f = 0; f < 2 * tracks; f++)
            WriteQs(transforms + f * QsSize, Vector3.Zero, Quaternion.Identity);

        // hkaInterleavedUncompressedAnimation: 2 frames over 1 second (the same pose in both, rewritten each frame).
        *(nint*)animation = _vtAnimation;
        *(uint*)(animation + 0x08) = 1;          // reference count 1, size 0 (never freed by Havok)
        *(int*)(animation + 0x10) = 1;           // interleaved
        *(float*)(animation + 0x14) = 1f;        // duration
        *(int*)(animation + 0x18) = tracks;      // transform tracks
        *(int*)(animation + 0x1C) = 0;           // float tracks
        *(nint*)(animation + 0x20) = 0;          // extracted motion
        SetArray(animation + 0x28, 0, 0);        // annotation tracks
        SetArray(animation + 0x38, transforms, 2 * tracks);
        SetArray(animation + 0x48, 0, 0);        // floats

        // hkaAnimationBinding: tracks -> body bones, additive.
        *(nint*)binding = _vtBinding;
        *(uint*)(binding + 0x08) = 1;
        *(nint*)(binding + 0x10) = 0;            // original skeleton name
        *(nint*)(binding + 0x18) = animation;
        SetArray(binding + 0x20, boneIndices, tracks);
        SetArray(binding + 0x30, 0, 0);
        SetArray(binding + 0x40, 0, 0);
        *(sbyte*)(binding + 0x50) = (sbyte)hint;

        // hkaDefaultAnimationControl, made by the game's own constructor (eased in, looping), then marked "not
        // Havok's" (size 0) so nothing ever frees it, and held still (Limber moves it by rewriting the frames).
        _controlCtor(control, binding, 1, -1);
        *(ushort*)(control + 0x0A) = 0;
        *(float*)(control + 0x10) = 0f;          // local time
        *(float*)(control + 0x5C) = 0f;          // playback speed
        if(*(nint*)control != _vtControl)
        {
            Problem = "The game's animation control constructor didn't make what's expected.";
            return false;
        }
        _animation = animation;
        _transforms = transforms;
        _boneIndices = boneIndices;
        _binding = binding;
        _control = control;
        _tracks = tracks;
        _trackBones = names.ToArray();
        _blendHint = hint;
        return true;
    }

    private bool SameBones(List<string> names)
    {
        if(names.Count != _trackBones.Length)
            return false;
        for(int i = 0; i < names.Count; i++)
            if(names[i] != _trackBones[i])
                return false;
        return true;
    }

    private static nint Alloc(int size)
    {
        var p = (nint)NativeMemory.AlignedAlloc((nuint)size, 16);
        NativeMemory.Clear((void*)p, (nuint)size);
        return p;
    }

    private static void SetArray(nint at, nint data, int count)
    {
        *(nint*)at = data;
        *(int*)(at + 8) = count;
        *(uint*)(at + 12) = (uint)count | DontDeallocate;
    }

    private static void WriteQs(nint at, Vector3 move, Quaternion turn)
    {
        var f = (float*)at;
        f[0] = move.X; f[1] = move.Y; f[2] = move.Z; f[3] = 0f;
        f[4] = turn.X; f[5] = turn.Y; f[6] = turn.Z; f[7] = turn.W;
        f[8] = 1f; f[9] = 1f; f[10] = 1f; f[11] = 0f;
    }

    // ---- The motion

    private void Write(EngineLayerSettings s, float dt)
    {
        _phase += dt * Math.Clamp(s.Speed, 1f, 400f) / 60.0 * Math.PI * 2.0;
        if(_phase > Math.PI * 2000.0)
            _phase -= Math.PI * 2000.0;
        float p = (float)_phase;
        float stroke = s.Motion switch
        {
            EngineLayerMotion.Pump => MathF.Pow(MathF.Max(0f, MathF.Sin(p)), 0.5f) * 2f - 1f, // (quick out, slow back)
            EngineLayerMotion.Bounce => MathF.Abs(MathF.Sin(p)) * 2f - 1f,
            _ => MathF.Sin(p),
        };
        float side = s.Motion == EngineLayerMotion.Circle ? MathF.Cos(p) : 0f;
        static Vector3 Axis(int a) => a switch { 0 => Vector3.UnitX, 1 => Vector3.UnitY, _ => Vector3.UnitZ };
        var moveAxis = Axis(s.MoveAxis);
        var tiltAxis = Axis(s.TiltAxis);
        float move = Math.Clamp(s.Move, -30f, 30f) / 100f;
        float tilt = Math.Clamp(s.Tilt, -60f, 60f) * MathF.PI / 180f;
        var hipsMove = moveAxis * (move * stroke);
        if(side != 0f)
            hipsMove += Axis((s.MoveAxis + 1) % 3) * (move * side);
        var hipsTurn = Quaternion.CreateFromAxisAngle(tiltAxis, tilt * stroke);
        for(int t = 0; t < _tracks; t++)
        {
            Vector3 m = Vector3.Zero;
            Quaternion q = Quaternion.Identity;
            if(_trackBones[t] == "j_kosi")
            {
                m = hipsMove;
                q = hipsTurn;
            }
            else if(_trackBones[t] == "j_sebo_a")
                q = Quaternion.CreateFromAxisAngle(tiltAxis, -tilt * stroke * 0.6f);
            WriteQs(_transforms + t * QsSize, m, q);
            WriteQs(_transforms + (_tracks + t) * QsSize, m, q);
        }
        // The mix: its weight (eased in already, so both the weight and the master weight).
        *(float*)(_control + 0x10) = 0f;
        *(float*)(_control + 0x5C) = 0f;
        *(float*)(_control + 0x14) = _level;
        *(float*)(_control + 0x58) = _level;
    }

    // ---- Finding things

    /// <summary>The body skeleton's first animation layer (hkaAnimatedSkeleton) for a character, 0 if none.</summary>
    private static nint BodyAnimatedSkeleton(nint address, out nint draw)
    {
        draw = 0;
        if(address == 0)
            return 0;
        var go = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)address;
        var d = go->DrawObject;
        if(d == null || d->Object.GetObjectType() != FFXIVClientStructs.FFXIV.Client.Graphics.Scene.ObjectType.CharacterBase)
            return 0;
        var cb = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase*)d;
        draw = (nint)cb;
        var skeleton = cb->Skeleton;
        if(skeleton == null || skeleton->PartialSkeletonCount < 1 || skeleton->PartialSkeletons == null)
            return 0;
        // PartialSkeleton+0x138: the two animation layers (hkaAnimatedSkeleton*).
        if(!Read((nint)skeleton->PartialSkeletons + 0x138, out nint animated))
            return 0;
        return animated;
    }

    private static bool Contains(nint animated, nint control)
    {
        if(animated == 0 || control == 0)
            return false;
        if(!Read(animated + 0x20, out nint data) || !Read(animated + 0x28, out int count) || count < 0 || count > 256 || data == 0)
            return false;
        for(int i = 0; i < count; i++)
            if(Read(data + i * 8, out nint c) && c == control)
                return true;
        return false;
    }

    private static List<string> BoneNames(nint skeleton)
    {
        var list = new List<string>();
        if(!Read(skeleton + 0x28, out nint bones) || !Read(skeleton + 0x30, out int count) || bones == 0 || count <= 0 || count > 2000)
            return list;
        for(int i = 0; i < count; i++)
        {
            Read(bones + i * 16, out nint name);
            list.Add(ReadString(name & ~(nint)1));
        }
        return list;
    }

    private static string ReadString(nint at)
    {
        if(at == 0)
            return "";
        var buffer = stackalloc byte[64];
        if(!Copy(at, buffer, 64))
        {
            // (near the end of readable memory: one byte at a time)
            int n = 0;
            while(n < 63 && Copy(at + n, buffer + n, 1) && buffer[n] != 0)
                n++;
            return Encoding.ASCII.GetString(buffer, n);
        }
        int len = 0;
        while(len < 64 && buffer[len] != 0)
            len++;
        return Encoding.ASCII.GetString(buffer, len);
    }

    private bool Resolve()
    {
        if(_resolved)
            return Problem.Length == 0;
        _resolved = true;
        try
        {
            nint Find(string sig, string what)
            {
                var all = Plugin.SigScanner.ScanAllText(sig);
                if(all.Length != 1)
                    throw new InvalidOperationException($"{what} found {all.Length} times");
                return all[0];
            }
            _controlCtor = (delegate* unmanaged<nint, nint, byte, int, nint>)Find(ControlCtorSig, "the animation control constructor");
            _add = (delegate* unmanaged<nint, nint, void>)Find(AddSig, "addAnimationControl");
            _remove = (delegate* unmanaged<nint, nint, void>)Find(RemoveSig, "removeAnimationControl");
            _vtAnimation = Rtti.VTable("hkaInterleavedUncompressedAnimation");
            _vtBinding = Rtti.VTable("hkaAnimationBinding");
            _vtControl = Rtti.VTable("hkaDefaultAnimationControl");
            _vtAnimatedSkeleton = Rtti.VTable("hkaAnimatedSkeleton");
            if(_vtAnimation == 0 || _vtBinding == 0 || _vtControl == 0 || _vtAnimatedSkeleton == 0)
                throw new InvalidOperationException("the Havok classes' tables weren't all found");
            Problem = "";
        }
        catch(Exception e)
        {
            Problem = e.Message;
            Plugin.Log.Warning("Limber: engine animation layer: " + e.Message);
        }
        return Problem.Length == 0;
    }

    // ---- What the game has (the readout)

    /// <summary>The character's animation layers as the game has them (read safely). Framework thread.</summary>
    public List<string> Diagnostics(nint address)
    {
        var lines = new List<string>();
        try
        {
            Resolve();
            if(Problem.Length > 0)
                lines.Add("Problem: " + Problem);
            var module = Plugin.SigScanner.Module.BaseAddress;
            lines.Add($"Havok tables: animation +0x{(long)(_vtAnimation - module):X}, binding +0x{(long)(_vtBinding - module):X}, control +0x{(long)(_vtControl - module):X}, layer +0x{(long)(_vtAnimatedSkeleton - module):X} (expected 2550CE0, 25519F8, 2553E08, 2553EE8 on 2026.10.16)");
            if(address == 0 || !Present(address))
                return lines;
            var go = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)address;
            var d = go->DrawObject;
            if(d == null || d->Object.GetObjectType() != FFXIVClientStructs.FFXIV.Client.Graphics.Scene.ObjectType.CharacterBase)
            {
                lines.Add("No body drawn.");
                return lines;
            }
            var cb = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase*)d;
            var skeleton = cb->Skeleton;
            if(skeleton == null || skeleton->PartialSkeletons == null)
            {
                lines.Add("No skeleton.");
                return lines;
            }
            int partials = Math.Min((int)skeleton->PartialSkeletonCount, 4);
            for(int part = 0; part < Math.Min(partials, 2); part++)
            {
                nint partial = (nint)skeleton->PartialSkeletons + part * 0x230; // (PartialSkeleton is 0x230 bytes)
                for(int layer = 0; layer < 2; layer++)
                {
                    if(!Read(partial + 0x138 + layer * 8, out nint animated) || animated == 0)
                    {
                        lines.Add($"Part {part} ({(part == 0 ? "body" : "face")}), layer {layer + 1}: none");
                        continue;
                    }
                    Read(animated, out nint vt);
                    Read(animated + 0x20, out nint data);
                    Read(animated + 0x28, out int count);
                    Read(animated + 0x30, out nint hkSkeleton);
                    Read(hkSkeleton + 0x30, out int boneCount);
                    lines.Add($"Part {part} ({(part == 0 ? "body" : "face")}), layer {layer + 1}: {count} animations, {boneCount} bones"
                        + (vt != _vtAnimatedSkeleton ? " (not the expected kind!)" : "")
                        + (boneCount >= 80 ? " (80+ bones: the game samples this part without its job list)" : ""));
                    for(int i = 0; i < Math.Min(count, 12); i++)
                    {
                        if(!Read(data + i * 8, out nint c) || c == 0)
                            continue;
                        Read(c, out nint cvt);
                        Read(c + 0x10, out float time);
                        Read(c + 0x14, out float weight);
                        Read(c + 0x58, out float master);
                        Read(c + 0x5C, out float speed);
                        Read(c + 0x98, out int ease);
                        Read(c + 0x38, out nint binding);
                        sbyte hint = 0;
                        nint anim = 0;
                        int type = 0, tracks = 0;
                        float duration = 0;
                        if(binding != 0)
                        {
                            Read(binding + 0x50, out hint);
                            Read(binding + 0x18, out anim);
                        }
                        if(anim != 0)
                        {
                            Read(anim + 0x10, out type);
                            Read(anim + 0x14, out duration);
                            Read(anim + 0x18, out tracks);
                        }
                        string kind = type switch { 1 => "uncompressed", 3 => "spline", 4 => "quantized", 5 => "predictive", _ => $"type {type}" };
                        string blend = hint switch { 0 => "normal", 1 => "additive (old)", 2 => "additive", _ => $"blend {hint}" };
                        lines.Add($"   {(c == _control ? "LIMBER " : "")}#{i}: weight {weight:0.00} (master {master:0.00}), time {time:0.00}/{duration:0.00}s, speed {speed:0.00}, ease {ease}, {blend}, {kind}, {tracks} tracks"
                            + (cvt != _vtControl ? " (other control kind)" : ""));
                    }
                }
            }
            if(_control != 0)
                lines.Add($"Limber's layer: {(_onAnimatedSkeleton != 0 ? "on" : "off")}, {_tracks} bones ({string.Join(", ", _trackBones)}), put on {_attaches} times");
        }
        catch(Exception e)
        {
            lines.Add("Couldn't read: " + e.Message);
        }
        return lines;
    }

    // ---- Safe reads

    [DllImport("kernel32.dll")]
    private static extern bool ReadProcessMemory(nint process, nint address, void* buffer, nint size, out nint read);

    private static bool Copy(nint address, void* to, int length)
        => address != 0 && ReadProcessMemory(-1, address, to, length, out var read) && read == length;

    private static bool Read<T>(nint address, out T value) where T : unmanaged
    {
        T v = default;
        bool ok = Copy(address, &v, sizeof(T));
        value = ok ? v : default;
        return ok;
    }
}

/// <summary>Finds a class's virtual table in the game from its C++ type information (by name).</summary>
internal static unsafe class Rtti
{
    private static readonly Dictionary<string, nint> Found = new();

    public static nint VTable(string className)
    {
        lock(Found)
        {
            if(Found.TryGetValue(className, out var known))
                return known;
            nint result = 0;
            try
            {
                result = Search(className);
            }
            catch(Exception e)
            {
                Plugin.Log.Debug(e, $"Limber: type information for {className}");
            }
            Found[className] = result;
            return result;
        }
    }

    private static nint Search(string className)
    {
        var module = Plugin.SigScanner.Module;
        nint b = module.BaseAddress;
        // The image's sections (from its headers in memory).
        int pe = *(int*)(b + 0x3C);
        int sections = *(ushort*)(b + pe + 6);
        int optional = *(ushort*)(b + pe + 20);
        nint table = b + pe + 24 + optional;
        var data = new List<(nint Start, int Size)>();
        for(int i = 0; i < sections; i++)
        {
            nint sh = table + i * 40;
            int size = *(int*)(sh + 8);
            int rva = *(int*)(sh + 12);
            uint flags = *(uint*)(sh + 36);
            bool readable = (flags & 0x40000000) != 0, executable = (flags & 0x20000000) != 0;
            // (only the game's read-only and data sections: type information and tables live there)
            var sectionName = Encoding.ASCII.GetString((byte*)sh, 8).TrimEnd('\0');
            if(readable && !executable && size > 0 && sectionName is ".rdata" or ".data")
                data.Add((b + rva, size));
        }
        // The type descriptor: its name ".?AV<class>@@", 16 bytes in.
        var name = Encoding.ASCII.GetBytes($".?AV{className}@@\0");
        nint descriptor = 0;
        foreach(var (start, size) in data)
        {
            var span = new ReadOnlySpan<byte>((void*)start, size);
            int at = span.IndexOf(name);
            if(at >= 16)
            {
                descriptor = start + at - 16;
                break;
            }
        }
        if(descriptor == 0)
            return 0;
        int descriptorRva = (int)(descriptor - b);
        // The complete object locator: signature 1, offset 0, its type descriptor's address (relative).
        foreach(var (start, size) in data)
        {
            var span = new ReadOnlySpan<byte>((void*)start, size);
            int from = 0;
            while(from < size - 24)
            {
                int at = span[from..].IndexOf(MemoryMarshal.AsBytes(new ReadOnlySpan<int>(in descriptorRva)));
                if(at < 0)
                    break;
                at += from;
                from = at + 4;
                if(at < 12 || (at & 3) != 0)
                    continue;
                nint locator = start + at - 12;
                if(*(int*)locator != 1 || *(int*)(locator + 4) != 0)
                    continue;
                // The virtual table is right after a pointer to the locator.
                long want = locator;
                foreach(var (s2, z2) in data)
                {
                    var span2 = new ReadOnlySpan<byte>((void*)s2, z2);
                    int p = span2.IndexOf(MemoryMarshal.AsBytes(new ReadOnlySpan<long>(in want)));
                    if(p >= 0 && (p & 7) == 0)
                        return s2 + p + 8;
                }
            }
        }
        return 0;
    }
}
