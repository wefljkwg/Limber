using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Limber.Rig;

/// <summary>One character's shader look on one part: the game's own shader swapped and its settings changed.</summary>
[Serializable]
public sealed class ShaderLook
{
    public bool Enabled { get; set; } = true;
    public string Character { get; set; } = "";   // name ("" in a preset)
    public string Part { get; set; } = "All";      // All, Skin, Hair, Face, Gear (see Shaders.Parts)
    public float[] Tint { get; set; } = [1f, 1f, 1f];
    public float Brightness { get; set; } = 1f;
    public float[] Glow { get; set; } = [1f, 1f, 1f];
    public float GlowStrength { get; set; }
    public float Shine { get; set; } = 1f;
    public float Reflection { get; set; } = 1f;
    public float Bumps { get; set; } = 1f;
    public string Shader { get; set; } = "";       // "" keeps the material's own (see Shaders.ShaderChoices)

    public ShaderLook Clone()
    {
        var c = (ShaderLook)MemberwiseClone();
        c.Tint = (float[])(Tint ?? [1f, 1f, 1f]).Clone();
        c.Glow = (float[])(Glow ?? [1f, 1f, 1f]).Clone();
        return c;
    }

    /// <summary>Everything that changes the material, as text (a changed look makes new copies).</summary>
    public string Key()
    {
        var t = Tint ?? [1f, 1f, 1f];
        var g = Glow ?? [1f, 1f, 1f];
        return FormattableString.Invariant(
            $"{Part}|{t[0]:0.###},{t[1]:0.###},{t[2]:0.###}|{Brightness:0.###}|{g[0]:0.###},{g[1]:0.###},{g[2]:0.###}|{GlowStrength:0.###}|{Shine:0.###}|{Reflection:0.###}|{Bumps:0.###}");
    }
}

/// <summary>A saved shader look (no character), to put on anyone.</summary>
[Serializable]
public sealed class ShaderPreset
{
    public string Name { get; set; } = "";
    public ShaderLook Look { get; set; } = new();
}

/// <summary>
/// Scene > Shaders: a character's materials drawn with another of the game's shaders and other settings (colour,
/// brightness, glow, shine, reflections, bumps), on that character only. A loaded material is shared by everything
/// that uses the same file, so it isn't changed in place: the character's own material lookups are answered with
/// private copies (made from the file it really draws, its mods included) with the changes in them, the way the body
/// swap's skin tint works, and the character redrawn once to take them.
/// </summary>
public sealed unsafe class Shaders : IDisposable
{
    public static readonly string[] Parts = ["All", "Skin", "Hair", "Face", "Gear"];

    /// <summary>The game's character shaders a material can be switched to: (file, label, what it does).</summary>
    public static readonly (string File, string Label, string Tip)[] ShaderChoices =
    [
        ("", "Its own", "The material's own shader, unchanged."),
        ("characterglass.shpk", "Glass", "Clear and shiny, like glass or liquid (the game's glass shader)."),
        ("charactertransparency.shpk", "See-through", "Drawn see-through where its texture is (the game's transparent shader)."),
        ("character.shpk", "Plain", "The game's ordinary gear shader (skin and hair lose their own shading)."),
        ("characterlegacy.shpk", "Old style", "The game's older gear shader."),
        ("skin.shpk", "Skin", "The game's skin shader (on gear: its colours shaded like skin)."),
    ];

    // The game's names for the settings changed (CRCs of the constants' names).
    private const uint DiffuseColor = 0x2C2A34DD;   // g_DiffuseColor
    private const uint EmissiveColor = 0x38A64362;  // g_EmissiveColor
    private const uint SpecularColor = 0x141722D5;  // g_SpecularColor
    private const uint EnvMapPower = 0xEEF5665F;    // g_EnvMapPower
    private const uint NormalScale = 0xB5545FBB;    // g_NormalScale

    private readonly Dalamud.Plugin.Services.IObjectTable _objects;
    private readonly Func<List<ShaderLook>> _looks;

    // Draw object -> the character it's for (index, name): kept up to date on the framework thread, read by the hook.
    private static readonly ConcurrentDictionary<nint, (ushort Index, string Name)> Draws = new();
    // Game object -> (index, name), for draw objects being made (the hook asks before the character points at it).
    private static readonly ConcurrentDictionary<nint, (ushort Index, string Name)> Characters = new();
    // Each character's looks right now (by name), read by the hook.
    private static volatile Dictionary<string, ShaderLook[]> _active = new(StringComparer.OrdinalIgnoreCase);
    // (character index, the material asked for, the look's key) -> the private copy's path
    private static readonly ConcurrentDictionary<(ushort Index, string Path, string Key), string> Made = new();
    // Materials asked for that have no copy yet (made on the framework thread).
    private static readonly ConcurrentDictionary<(ushort Index, string Path, string Key, string Part), ShaderLook> Wanted = new();
    private static readonly List<string> Files = new();
    private readonly Dictionary<string, (string Key, long Since)> _lastKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _applied = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What happened last (for the page).</summary>
    public string Status { get; private set; } = "";

    public Shaders(Dalamud.Plugin.Services.IObjectTable objects, Func<List<ShaderLook>> looks)
    {
        _objects = objects;
        _looks = looks;
    }

    // ---- Framework thread

    public void Update(bool here)
    {
        var looks = _looks() ?? new();
        var byName = new Dictionary<string, ShaderLook[]>(StringComparer.OrdinalIgnoreCase);
        if (here)
            foreach (var g in looks.Where(l => l.Enabled && l.Character.Length > 0).GroupBy(l => l.Character, StringComparer.OrdinalIgnoreCase))
                byName[g.Key] = g.Select(l => l.Clone()).ToArray();
        _active = byName;

        // Who's around (and their draw objects), for the hook.
        var draws = new HashSet<nint>();
        var chars = new HashSet<nint>();
        long now = Environment.TickCount64;
        var redraw = new HashSet<ushort>();
        foreach (var obj in _objects)
        {
            if (obj is not Dalamud.Game.ClientState.Objects.Types.ICharacter || obj.Address == 0)
                continue;
            string name = obj.Name.TextValue;
            if (name.Length == 0)
                continue;
            bool mine = byName.ContainsKey(name);
            string key = mine ? string.Join(";", byName[name].Select(l => l.Key())) : "";
            bool had = _applied.TryGetValue(name, out var was);
            if (!mine && !had)
                continue;
            var native = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)obj.Address;
            chars.Add(obj.Address);
            Characters[obj.Address] = (obj.ObjectIndex, name);
            if (native->DrawObject != null)
            {
                draws.Add((nint)native->DrawObject);
                Draws[(nint)native->DrawObject] = (obj.ObjectIndex, name);
                if (mine)
                    BodySwap.EnsureHooks((nint)native->DrawObject);
            }
            // A changed look (held still a moment, not every step of a slider being dragged), or one taken off:
            // drawn again with it.
            if (!_lastKeys.TryGetValue(name, out var last) || last.Key != key)
            {
                _lastKeys[name] = (key, now);
                continue;
            }
            if ((had ? was : "") != key && now - last.Since > 400)
            {
                if (key.Length == 0)
                    _applied.Remove(name);
                else
                    _applied[name] = key;
                redraw.Add(obj.ObjectIndex);
            }
        }
        foreach (var k in Draws.Keys)
            if (!draws.Contains(k))
                Draws.TryRemove(k, out _);
        foreach (var k in Characters.Keys)
            if (!chars.Contains(k))
                Characters.TryRemove(k, out _);

        MakeCopies(redraw);
        foreach (var index in redraw)
            Appearance.RedrawNative(index);
    }

    // The copies the hook asked for, made from the file each character really draws; they're drawn again to take them.
    private void MakeCopies(HashSet<ushort> redraw)
    {
        if (Wanted.IsEmpty)
            return;
        var dir = System.IO.Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "shaders");
        bool any = false;
        foreach (var (want, look) in Wanted.ToList())
        {
            Wanted.TryRemove(want, out _);
            var made = (want.Index, want.Path, want.Key);
            if (Made.ContainsKey(made))
                continue;
            try
            {
                string real = want.Path;
                try
                {
                    real = Plugin.PluginInterface.GetIpcSubscriber<string, int, string>("Penumbra.ResolveGameObjectPath.V5").InvokeFunc(want.Path, want.Index);
                }
                catch
                {
                }
                byte[]? data = System.IO.Path.IsPathRooted(real) && System.IO.File.Exists(real)
                    ? System.IO.File.ReadAllBytes(real)
                    : Plugin.DataManager.GetFile(real)?.Data ?? Plugin.DataManager.GetFile(want.Path)?.Data;
                if (data == null)
                    continue;
                var changed = Apply(data, look);
                string tag = $"i{want.Index}_{(uint)HashCode.Combine(want.Key, want.Path):x8}";
                string name = System.IO.Path.GetFileName(want.Path);
                string game = $"chara/limber/shaders/{tag}/{name}";
                System.IO.Directory.CreateDirectory(dir);
                string disk = System.IO.Path.Combine(dir, $"{tag}_{name}");
                System.IO.File.WriteAllBytes(disk, changed);
                Limber.Koi.Props.KoiProps.Extra[game] = disk;
                lock (Files)
                    Files.Add(game);
                Made[made] = game;
                redraw.Add(want.Index);
                any = true;
                Status = $"Made {Made.Count} material cop{(Made.Count == 1 ? "y" : "ies")}.";
            }
            catch (Exception e)
            {
                Status = $"Couldn't change a material ({System.IO.Path.GetFileName(want.Path)}: {e.Message}).";
                Plugin.Log.Warning(e, "Limber shaders: couldn't make a material copy");
            }
        }
        if (any)
            Limber.Koi.Props.KoiProps.Register();
    }

    /// <summary>The material with the look on it: its settings changed.</summary>
    public static byte[] Apply(byte[] mtrl, ShaderLook look)
    {
        var b = mtrl;
        // (No switching to another of the game's shaders: each wants its own textures, keys and colour table, so a
        // material made for one came out black under the others, and skin's crashed the game. Glass, see-through and
        // the like come as Limber's own effects instead.)
        var t = look.Tint ?? [1f, 1f, 1f];
        float bright = Math.Clamp(look.Brightness, 0f, 10f);
        var colour = new[] { t[0] * bright, t[1] * bright, t[2] * bright };
        if (colour.Any(c => MathF.Abs(c - 1f) > 1e-4f))
            b = Scale(b, DiffuseColor, colour, 1f);
        if (look.GlowStrength > 1e-4f)
        {
            var g = look.Glow ?? [1f, 1f, 1f];
            float s = Math.Clamp(look.GlowStrength, 0f, 20f);
            b = Limber.Koi.Props.MtrlBuilder.SetConstant(b, EmissiveColor, [g[0] * s, g[1] * s, g[2] * s]);
        }
        if (MathF.Abs(look.Shine - 1f) > 1e-4f)
            b = Scale(b, SpecularColor, [look.Shine, look.Shine, look.Shine], 1f);
        if (MathF.Abs(look.Reflection - 1f) > 1e-4f)
            b = Scale(b, EnvMapPower, [look.Reflection], 1f);
        if (MathF.Abs(look.Bumps - 1f) > 1e-4f)
            b = Scale(b, NormalScale, [look.Bumps], 1f);
        return b;
    }

    // A constant multiplied by the factors (set to them times the default when the material hasn't got it).
    private static byte[] Scale(byte[] b, uint id, float[] by, float fallback)
    {
        var have = Limber.Koi.Props.MtrlBuilder.Constants(b).FirstOrDefault(c => c.Id == id).Values;
        var values = new float[by.Length];
        for (int k = 0; k < by.Length; k++)
            values[k] = (have != null && k < have.Length ? have[k] : fallback) * by[k];
        return Limber.Koi.Props.MtrlBuilder.SetConstant(b, id, values);
    }

    /// <summary>The material with another shader package named (its name added to the strings).</summary>
    public static byte[] SetShader(byte[] b, string shader)
    {
        int texCount = b[12], uv = b[13], cs = b[14];
        int stringSize = BitConverter.ToUInt16(b, 8);
        int stringsAt = 16 + 4 * (texCount + uv + cs);
        if (stringsAt + stringSize > b.Length)
            return b;
        var name = System.Text.Encoding.UTF8.GetBytes(shader);
        int add = name.Length + 1;
        int pad = (4 - (stringSize + add) % 4) % 4;   // (the strings stay a multiple of 4 long)
        var o = new System.IO.MemoryStream();
        o.Write(b, 0, stringsAt + stringSize);
        o.Write(name);
        o.WriteByte(0);
        for (int k = 0; k < pad; k++)
            o.WriteByte(0);
        o.Write(b, stringsAt + stringSize, b.Length - stringsAt - stringSize);
        var file = o.ToArray();
        BitConverter.TryWriteBytes(file.AsSpan(8), (ushort)(stringSize + add + pad));
        BitConverter.TryWriteBytes(file.AsSpan(10), (ushort)stringSize);
        BitConverter.TryWriteBytes(file.AsSpan(4), (ushort)Math.Min(file.Length, 0xFFFF));
        return file;
    }

    // ---- The material lookup (the body swap's hook calls this; any thread)

    /// <summary>
    /// A character's material path, answered with its private copy when one of its looks covers it (else noted, for a
    /// copy to be made and the character drawn again).
    /// </summary>
    public static nint Redirect(nint human, nint buffer, nuint size, uint slot, bool skin, nint path)
    {
        if (path == 0 || _active.Count == 0)
            return path;
        try
        {
            if (!Draws.TryGetValue(human, out var who))
            {
                nint creating = BodySwap.CreatingFor;
                if (creating == 0 || !Characters.TryGetValue(creating, out who))
                    return path;
            }
            if (!_active.TryGetValue(who.Name, out var looks))
                return path;
            string got = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(path) ?? "";
            if (got.Length == 0 || !got.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase) || got.StartsWith("chara/limber/shaders/", StringComparison.OrdinalIgnoreCase))
                return path;
            string part = PartOf(got, slot, skin);
            // (the most specific look: one for this part, else one for everything)
            var look = looks.FirstOrDefault(l => l.Part.Equals(part, StringComparison.OrdinalIgnoreCase))
                       ?? looks.FirstOrDefault(l => l.Part.Equals("All", StringComparison.OrdinalIgnoreCase));
            if (look == null)
                return path;
            string key = look.Key();
            if (Made.TryGetValue((who.Index, got, key), out var mine))
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(mine);
                if ((nuint)bytes.Length + 1 > size)
                    return path;
                System.Runtime.InteropServices.Marshal.Copy(bytes, 0, buffer, bytes.Length);
                *((byte*)buffer + bytes.Length) = 0;
                return buffer;
            }
            Wanted.TryAdd((who.Index, got, key, part), look);
        }
        catch
        {
        }
        return path;
    }

    /// <summary>Which part a material is: skin, hair, face or gear.</summary>
    public static string PartOf(string path, uint slot, bool skin)
    {
        var p = path.Replace('\\', '/').ToLowerInvariant();
        if (skin || p.Contains("/skin/") || System.Text.RegularExpressions.Regex.IsMatch(p, @"mt_c\d{4}b\d{4}"))
            return "Skin";
        if (p.Contains("/hair/") || p.Contains("_hir") || p.Contains("/zear/") || p.Contains("_zer") || p.Contains("/tail/") || p.Contains("_til"))
            return "Hair";
        if (p.Contains("/face/") || p.Contains("_fac") || p.Contains("_iri") || p.Contains("_etc"))
            return "Face";
        return "Gear";
    }

    /// <summary>A character drawn again (to take a look, or lose it).</summary>
    public void Redraw(string name)
    {
        foreach (var obj in _objects)
            if (obj is Dalamud.Game.ClientState.Objects.Types.ICharacter && obj.Name.TextValue.Equals(name, StringComparison.OrdinalIgnoreCase))
                Appearance.RedrawNative(obj.ObjectIndex);
    }

    public void Dispose()
    {
        _active = new(StringComparer.OrdinalIgnoreCase);
        var names = _applied.Keys.ToList();
        _applied.Clear();
        foreach (var n in names)
            try
            {
                Redraw(n);
            }
            catch
            {
            }
    }
}
