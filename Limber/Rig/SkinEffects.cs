using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Shader;

namespace Limber.Rig;

/// <summary>One character's skin effects (kept by name; Skin effects page).</summary>
public sealed class SkinEffectSettings
{
    public bool Enabled { get; set; }
    /// <summary>A wet / sweaty sheen all over (0 dry .. 1 soaked).</summary>
    public float Wetness { get; set; } = 0.6f;
    /// <summary>Soaked below a water line (like standing in water), drier above.</summary>
    public bool WaterLine { get; set; }
    /// <summary>The water line's height (metres above the feet, roughly).</summary>
    public float WaterLineHeight { get; set; } = 1f;
    /// <summary>Seconds for the water line to rise from the feet to its height (0: at once).</summary>
    public float RiseSeconds { get; set; }
    /// <summary>The skin flushes towards the flush colour (0 none .. 1 fully).</summary>
    public float Flush { get; set; } = 0.4f;
    /// <summary>The flush colour (red by default).</summary>
    public Vector3 FlushColor { get; set; } = new(0.9f, 0.48f, 0.46f);
    /// <summary>Wet, glistening eyes (a stronger catchlight; 0 as they are .. 1 very wet).</summary>
    public float WetEyes { get; set; }
    /// <summary>Seconds to fade in and out.</summary>
    public float RampSeconds { get; set; } = 2f;

    /// <summary>Steam / musk rising off the skin (0 none .. 1 a lot).</summary>
    public float Steam { get; set; }
    /// <summary>Where it rises from (<see cref="SteamService.Areas"/>).</summary>
    public int SteamAreas { get; set; } = 1;
    public Vector3 SteamColor { get; set; } = new(0.92f, 0.92f, 0.95f);
    public float SteamOpacity { get; set; } = 0.35f;
    /// <summary>How high it rises (times), how big the wisps are (times), how many (times).</summary>
    public float SteamRise { get; set; } = 1f;
    public float SteamSize { get; set; } = 1f;
    public float SteamDensity { get; set; } = 1f;

    public SkinEffectSettings Copy() => (SkinEffectSettings)MemberwiseClone();
}

/// <summary>
/// Skin effects (adult characters, only on your screen): a wet / sweaty sheen (the game's own per-character wetness,
/// the one rain and swimming use: darker, glossier skin, and clothes and hair too), a flush (the skin colour drawn
/// towards red, as Glamourer changes colours: put back when it ends, and anything another plugin sets meanwhile is
/// kept), and wet eyes (a stronger catchlight). All fade in and out. Framework thread.
/// The wet sheen needs the game's character wetness graphics option.
/// Other features can drive these with <see cref="Drive"/> (e.g. Climax), on top of the page's settings.
/// </summary>
public sealed unsafe class SkinEffectsService : IDisposable
{
    /// <summary>The running service (for <see cref="Drive"/>).</summary>
    public static SkinEffectsService? Instance { get; private set; }

    private sealed class State
    {
        public nint Draw;
        public float Wet, Flush, Eyes, Line;
        public bool LineOn;
        public double Rise;
        public Vector3 FlushColor = new(0.9f, 0.48f, 0.46f);
        public float WaterHeight;
        // The skin colour and catchlight before Limber changed them, and what Limber last wrote.
        public Vector4? SkinBefore, SkinWritten;
        public float? EyesBefore, EyesWritten;
        public bool Seen;
    }

    private readonly IObjectTable _objects;
    private readonly Func<Dictionary<string, SkinEffectSettings>> _settings;
    private readonly Func<bool> _hookWanted;
    private readonly Dictionary<nint, State> _states = new();
    private static readonly ConcurrentDictionary<nint, (float Wet, float Flush, float Eyes, long Until)> Driven = new();

    /// <summary>Why something can't be used ("" when all's well).</summary>
    public string Problem { get; private set; } = "";

    public SkinEffectsService(IObjectTable objects, Func<Dictionary<string, SkinEffectSettings>> settings, Func<bool> hookWanted)
    {
        _objects = objects;
        _settings = settings;
        _hookWanted = hookWanted;
        Instance = this;
    }

    // ---- For other features (Climax...)

    /// <summary>
    /// Wet sheen, flush and wet eyes (each 0..1) on a character for some seconds, on top of its page settings (the
    /// stronger of the two), then fading out by its ramp. Any thread. Adult characters only (checked when applied).
    /// </summary>
    public static void Drive(nint address, float wet, float flush, float eyes, float seconds)
    {
        if(address == 0)
            return;
        long until = Environment.TickCount64 + (long)(Math.Clamp(seconds, 0f, 3600f) * 1000f);
        Driven[address] = (Math.Clamp(wet, 0f, 1f), Math.Clamp(flush, 0f, 1f), Math.Clamp(eyes, 0f, 1f), until);
    }

    /// <summary>Stops what <see cref="Drive"/> put on (it fades out).</summary>
    public static void StopDriving(nint address) => Driven.TryRemove(address, out _);

    /// <summary>What's on a character now (faded values), for the page's readout.</summary>
    public (float Wet, float Flush, float Eyes, float Line)? Current(nint address)
        => _states.TryGetValue(address, out var s) ? (s.Wet, s.Flush, s.Eyes, s.LineOn ? s.Line : 0f) : null;

    // ---- Each frame

    public void Update(bool here, double seconds)
    {
        float dt = (float)Math.Clamp(seconds, 0.0, 0.25);
        UpdateHook();
        if(!here)
        {
            RestoreAll();
            _hookValues = new();
            return;
        }
        var all = _settings();
        long now = Environment.TickCount64;
        foreach(var (address, d) in Driven)
            if(d.Until < now)
                Driven.TryRemove(address, out _);
        // (nothing on, driven or being faded out: no need to look through every character each frame)
        if(Driven.IsEmpty && _states.Count == 0 && !AnyEnabled(all))
        {
            _hookValues = new();
            return;
        }
        foreach(var s in _states.Values)
            s.Seen = false;
        var hookValues = new Dictionary<nint, (float Wet, float Line, bool LineOn)>();
        bool hook = _hook != null && _hook.IsEnabled;
        foreach(var obj in _objects)
        {
            if(obj is not Dalamud.Game.ClientState.Objects.Types.ICharacter || obj.Address == 0)
                continue;
            nint address = obj.Address;
            string name = obj.Name.TextValue;
            SkinEffectSettings? s = null;
            if(name.Length > 0 && all.TryGetValue(name, out var found) && found.Enabled)
                s = found;
            bool driven = Driven.TryGetValue(address, out var d);
            bool known = _states.TryGetValue(address, out var state);
            if(s == null && !driven && !known)
                continue;
            if(!Limber.Koi.Safety.AdultAllowed(address))
            {
                if(known)
                    Restore(address, state!);
                continue;
            }
            if(state == null)
                _states[address] = state = new State();
            state.Seen = true;

            // Where it's heading, and the fade there.
            float wet = s?.Wetness ?? 0f, flush = s?.Flush ?? 0f, eyes = s?.WetEyes ?? 0f;
            if(driven)
            {
                wet = MathF.Max(wet, d.Wet);
                flush = MathF.Max(flush, d.Flush);
                eyes = MathF.Max(eyes, d.Eyes);
            }
            float ramp = MathF.Max(s?.RampSeconds ?? 2f, 0.01f);
            float step = dt / ramp;
            state.Wet = Toward(state.Wet, Math.Clamp(wet, 0f, 1f), step);
            state.Flush = Toward(state.Flush, Math.Clamp(flush, 0f, 1f), step);
            state.Eyes = Toward(state.Eyes, Math.Clamp(eyes, 0f, 1f), step);
            bool lineOn = s != null && s.WaterLine;
            if(lineOn && !state.LineOn)
                state.Rise = 0;
            state.LineOn = lineOn;
            if(lineOn)
            {
                state.Rise += dt;
                float rise = s!.RiseSeconds > 0.01f ? (float)Math.Min(1.0, state.Rise / s.RiseSeconds) : 1f;
                state.Line = Math.Clamp(s.WaterLineHeight, 0f, 5f) * rise;
            }
            if(s != null)
            {
                state.FlushColor = s.FlushColor;
                state.WaterHeight = s.WaterLineHeight;
            }

            try
            {
                Apply(obj.Address, state, hook, hookValues);
            }
            catch(Exception e)
            {
                Problem = "Couldn't change a character's skin: " + e.Message;
            }
        }
        // Characters gone, or faded right out: forget them (putting back what was changed).
        List<nint>? done = null;
        List<nint>? gone = null;
        foreach(var (address, state) in _states)
        {
            // (a character that left: its memory may already be reused, so nothing is written back into it)
            if(!state.Seen)
                (gone ??= new()).Add(address);
            else if(state.Wet < 1e-3f && state.Flush < 1e-3f && state.Eyes < 1e-3f && !state.LineOn && !IsWanted(address, all))
            {
                Restore(address, state);
                (done ??= new()).Add(address);
            }
        }
        if(gone != null)
            foreach(var a in gone)
                _states.Remove(a);
        if(done != null)
            foreach(var a in done)
            {
                if(_states.TryGetValue(a, out var st))
                    Restore(a, st);
                _states.Remove(a);
            }
        _hookValues = hookValues;
    }

    private static bool AnyEnabled(Dictionary<string, SkinEffectSettings> all)
    {
        foreach(var s in all.Values)
            if(s.Enabled)
                return true;
        return false;
    }

    private bool IsWanted(nint address, Dictionary<string, SkinEffectSettings> all)
    {
        if(Driven.ContainsKey(address))
            return true;
        foreach(var obj in _objects)
            if(obj != null && obj.Address == address)
                return all.TryGetValue(obj.Name.TextValue, out var s) && s.Enabled;
        return false;
    }

    private static float Toward(float from, float to, float step)
        => from < to ? MathF.Min(to, from + step) : MathF.Max(to, from - step);

    /// <summary>The character's drawn body, if it's a human one.</summary>
    private static Human* HumanOf(nint address)
    {
        var go = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)address;
        if(go == null)
            return null;
        var draw = go->DrawObject;
        if(draw == null || draw->Object.GetObjectType() != ObjectType.CharacterBase)
            return null;
        var cb = (CharacterBase*)draw;
        if(cb->GetModelType() != CharacterBase.ModelType.Human)
            return null;
        return (Human*)cb;
    }

    private const int CatchlightOffset = 0x2DC; // CharacterBase: the eyes' catchlight strength (into the instance buffer)

    private void Apply(nint address, State state, bool hook, Dictionary<nint, (float Wet, float Line, bool LineOn)> hookValues)
    {
        var human = HumanOf(address);
        if(human == null)
        {
            state.Draw = 0;
            return;
        }
        var cb = (CharacterBase*)human;
        if(state.Draw != (nint)human)
        {
            // A new body (redrawn): what was kept belonged to the old one.
            state.Draw = (nint)human;
            state.SkinBefore = state.SkinWritten = null;
            state.EyesBefore = state.EyesWritten = null;
        }

        // Wet sheen: the game's own wetness. Without the hook, as if it rained on them (the even sheen, which the
        // game shows at up to 80 %) and, for the water line, as if they stood in water that deep.
        if(hook)
            hookValues[(nint)human] = (state.Wet, state.Line, state.LineOn);
        else
        {
            if(state.Wet > 1e-3f && !state.LineOn)
            {
                // (the game shows 0.8 * (1 - (1 - w)^2): this makes the sheen follow the slider evenly)
                cb->WeatherWetness = 1f - MathF.Sqrt(1f - Math.Clamp(state.Wet, 0f, 1f));
            }
            if(state.LineOn && state.Wet > 1e-3f)
            {
                // ForceWetness (virtual 52): soaked below this height this frame (the game lowers it slowly itself).
                var vf = (*(nint**)cb)[52];
                if(vf != 0)
                    ((delegate* unmanaged<CharacterBase*, float, void>)vf)(cb, state.Line);
            }
        }

        // Flush: the skin colour drawn towards the flush colour.
        var buffer = human->CustomizeParameterCBuffer;
        var parameters = buffer == null ? null : (CustomizeParameter*)buffer->UnsafeSourcePointer;
        if(parameters != null)
        {
            var now = (Vector4)parameters->SkinColor;
            if(state.SkinWritten is { } written && Vector4.DistanceSquared(now, written) > 1e-8f)
                state.SkinBefore = now; // (changed by someone else, e.g. Glamourer: that's the colour now)
            if(state.Flush > 1e-3f)
            {
                state.SkinBefore ??= now;
                var before = state.SkinBefore.Value;
                var flushed = Flushed(before, state.FlushColor, state.Flush);
                parameters->SkinColor = flushed;
                state.SkinWritten = flushed;
            }
            else if(state.SkinBefore is { } before)
            {
                if(state.SkinWritten is { } w2 && Vector4.DistanceSquared(now, w2) <= 1e-8f)
                    parameters->SkinColor = before;
                state.SkinBefore = state.SkinWritten = null;
            }
        }

        // Wet eyes: a stronger catchlight.
        float* catchlight = (float*)((byte*)cb + CatchlightOffset);
        float eyesNow = *catchlight;
        if(!float.IsFinite(eyesNow) || eyesNow < 0f || eyesNow > 10f)
            return; // (not what's expected there: leave it alone)
        if(state.EyesWritten is { } ew && MathF.Abs(eyesNow - ew) > 1e-6f)
            state.EyesBefore = eyesNow;
        if(state.Eyes > 1e-3f)
        {
            state.EyesBefore ??= eyesNow;
            float b = state.EyesBefore.Value;
            float value = b + (MathF.Max(b, WetEyesMax) - b) * state.Eyes;
            *catchlight = value;
            state.EyesWritten = value;
        }
        else if(state.EyesBefore is { } eb)
        {
            if(state.EyesWritten is { } ew2 && MathF.Abs(eyesNow - ew2) <= 1e-6f)
                *catchlight = eb;
            state.EyesBefore = state.EyesWritten = null;
        }
    }

    /// <summary>The catchlight at full wet eyes (the game's usual is about 0.1).</summary>
    public static float WetEyesMax = 1f;

    /// <summary>The skin colour flushed: each channel (in the colour pickers' space) scaled towards the flush colour.</summary>
    private static Vector4 Flushed(Vector4 skin, Vector3 color, float amount)
    {
        static float Root(float x) => x < 0 ? -MathF.Sqrt(-x) : MathF.Sqrt(x);
        static float Square(float x) => x < 0 ? -x * x : x * x;
        var g = new Vector3(Root(skin.X), Root(skin.Y), Root(skin.Z));
        var tint = Vector3.Clamp(color, Vector3.Zero, Vector3.One) * 1.25f;
        var f = g * Vector3.Lerp(Vector3.One, tint, Math.Clamp(amount, 0f, 1f));
        return new Vector4(Square(f.X), Square(f.Y), Square(f.Z), skin.W);
    }

    /// <summary>Puts back the skin colour and catchlight Limber changed (only where they're still as Limber left them).</summary>
    private static void Restore(nint address, State state)
    {
        try
        {
            var human = HumanOf(address);
            if(human == null || (nint)human != state.Draw)
                return;
            var buffer = human->CustomizeParameterCBuffer;
            var parameters = buffer == null ? null : (CustomizeParameter*)buffer->UnsafeSourcePointer;
            if(parameters != null && state.SkinBefore is { } before && state.SkinWritten is { } written
                && Vector4.DistanceSquared((Vector4)parameters->SkinColor, written) <= 1e-8f)
                parameters->SkinColor = before;
            float* catchlight = (float*)((byte*)human + CatchlightOffset);
            if(state.EyesBefore is { } eb && state.EyesWritten is { } ew && MathF.Abs(*catchlight - ew) <= 1e-6f)
                *catchlight = eb;
        }
        catch
        {
        }
        finally
        {
            state.SkinBefore = state.SkinWritten = null;
            state.EyesBefore = state.EyesWritten = null;
            state.Wet = state.Flush = state.Eyes = 0f;
        }
    }

    private void RestoreAll()
    {
        foreach(var (address, state) in _states)
            Restore(address, state);
        _states.Clear();
    }

    // ---- The stronger wetness: hooks the game's wetness function (virtual 106, once a frame per character)

    // CharacterBase vf106 (2026.10.16 @0x14043EC00): fills the instance buffer's m_Wetness (+0x40: line ramp, +0x44:
    // water line, +0x48: even sheen (rain), +0x4C: the most it gets).
    private const string WetnessSig = "F3 0F 10 81 A4 02 00 00 F3 0F 59 41 74";
    private delegate void WetnessDelegate(nint characterBase, nint parameters);
    private Hook<WetnessDelegate>? _hook;
    private bool _hookTried;
    private volatile Dictionary<nint, (float Wet, float Line, bool LineOn)> _hookValues = new();

    /// <summary>Whether the stronger wetness is hooked and on.</summary>
    public bool HookActive => _hook != null && _hook.IsEnabled;

    private void UpdateHook()
    {
        bool want = _hookWanted();
        if(want && !_hookTried)
        {
            _hookTried = true;
            try
            {
                var found = Plugin.SigScanner.ScanAllText(WetnessSig);
                if(found.Length != 1)
                    Problem = $"The stronger wetness can't be used: the game's wetness function wasn't found ({found.Length} matches).";
                else
                    _hook = Plugin.GameInterop.HookFromAddress<WetnessDelegate>(found[0], WetnessDetour);
            }
            catch(Exception e)
            {
                Problem = "The stronger wetness can't be used: " + e.Message;
                Plugin.Log.Warning(e, "Limber: wetness hook");
            }
        }
        if(_hook == null)
            return;
        if(want && !_hook.IsEnabled)
            _hook.Enable();
        else if(!want && _hook.IsEnabled)
        {
            _hook.Disable();
            _hookValues = new();
        }
    }

    private void WetnessDetour(nint characterBase, nint parameters)
    {
        _hook!.Original(characterBase, parameters);
        try
        {
            if(parameters == 0 || !_hookValues.TryGetValue(characterBase, out var v))
                return;
            float* w = (float*)(parameters + 0x40);
            float wet = Math.Clamp(v.Wet, 0f, 1f);
            if(wet <= 1e-3f)
                return;
            if(!v.LineOn)
            {
                // Even sheen: up to fully wet (the game's rain stops at 80 %).
                w[2] = MathF.Max(w[2], wet);
                w[3] = MathF.Max(w[3], wet);
                return;
            }
            // The water line: this wet below it, as the game has it above (as the game works it out:
            // line = -(depth + 0.2) / scale, ramp = -5 * scale).
            float scale = -w[0] / 5f;
            if(scale > 1e-3f && float.IsFinite(scale))
            {
                w[1] = -(v.Line + 0.2f) / scale;
                w[3] = MathF.Max(w[3], wet);
            }
        }
        catch
        {
        }
    }

    // ---- The game's wetness option

    private nint _optionGlobal = -1;

    /// <summary>
    /// The game's character wetness option as the wetness function reads it: true on, false off, null unknown.
    /// </summary>
    public bool? WetnessOptionOn()
    {
        try
        {
            if(_optionGlobal == -1)
            {
                _optionGlobal = 0;
                // In the wetness function: mov rax, [the graphics options]; ... cmp byte ptr [rax + 0x15], 0
                if(Plugin.SigScanner.TryGetStaticAddressFromSig("48 8B 05 ?? ?? ?? ?? F3 0F 10 2D ?? ?? ?? ?? F3 0F 10 1D ?? ?? ?? ?? F3 0F 59 81 A0 02 00 00", out var at))
                    _optionGlobal = at;
            }
            if(_optionGlobal == 0)
                return null;
            nint options = *(nint*)_optionGlobal;
            if(options == 0)
                return null;
            return *(byte*)(options + 0x15) != 0;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        try
        {
            RestoreAll();
        }
        catch
        {
        }
        _hookValues = new();
        _hook?.Dispose();
        _hook = null;
        if(Instance == this)
            Instance = null;
    }
}
