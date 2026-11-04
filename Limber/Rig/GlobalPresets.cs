using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Limber.Rig;

/// <summary>A global preset: everything Limber keeps for one character (every page's settings for them), by name.</summary>
[Serializable]
public sealed class GlobalPreset
{
    public string Name { get; set; } = "";
    /// <summary>Part (which page's settings) -> those settings, as saved.</summary>
    public Dictionary<string, string> Parts { get; set; } = new();
}

/// <summary>When a character with this name comes into the scene: a global preset (and a saved look) goes on them.</summary>
[Serializable]
public sealed class GlobalPresetRule
{
    public bool Enabled { get; set; } = true;
    public string Character { get; set; } = "";
    public string Preset { get; set; } = "";
    /// <summary>A saved look (Appearance &amp; gear) put on them too, or "" for none.</summary>
    public string Look { get; set; } = "";
}

/// <summary>
/// Global presets: saving every per-character setting of one character as a preset, putting it on any character, and
/// the rules that put one on a character by itself when they come into the scene (a player arriving, an actor added).
/// </summary>
public sealed class GlobalPresetService
{
    private readonly RigSettings _config;
    private readonly IObjectTable _objects;
    private readonly HashSet<nint> _seen = new();
    private long _nextScan;

    public GlobalPresetService(RigSettings config, IObjectTable objects)
    {
        _config = config;
        _objects = objects;
    }

    private static readonly JsonSerializerSettings Json = new()
    {
        ObjectCreationHandling = ObjectCreationHandling.Replace,
        ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
    };

    // Every setting Limber keeps per character (by the character's name), with the name each is saved under.
    private IEnumerable<(string Part, IDictionary Settings, Type Value)> PerCharacter()
    {
        var c = _config;
        (string, IDictionary, Type) P<T>(string part, Dictionary<string, T> d) => (part, d, typeof(T));
        yield return P("Throbbing", c.Throb ??= new());
        yield return P("Testicle throbbing", c.BallThrob ??= new());
        yield return P("Twerking", c.Twerk ??= new());
        yield return P("Ahegao", c.Ahegao ??= new());
        yield return P("Expansion", c.Expansion ??= new());
        yield return P("Dynamic Bones", c.DynamicBones ??= new());
        yield return P("Look at", c.LookAt ??= new());
        yield return P("Face Posing", c.FacePoses ??= new());
        yield return P("FK Posing", c.FkPoses ??= new());
        yield return P("Bone scale", c.BoneScales ??= new());
        yield return P("Body shape", c.BodyShapes ??= new());
        yield return P("Symmetry", c.Symmetry ??= new());
    }

    /// <summary>What a character has that a preset would hold (the pages with settings for them).</summary>
    public List<string> PartsOf(string character)
        => PerCharacter().Where(p => p.Settings.Contains(character)).Select(p => p.Part).ToList();

    /// <summary>Saves everything Limber keeps for this character as a global preset (over one with the same name).</summary>
    public GlobalPreset Save(string character, string name)
    {
        var preset = new GlobalPreset { Name = name };
        var serializer = JsonSerializer.Create(Json);
        foreach(var (part, settings, _) in PerCharacter())
            if(settings.Contains(character) && settings[character] is { } value)
                preset.Parts[part] = JToken.FromObject(value, serializer).ToString(Formatting.None);
        var all = _config.GlobalPresets ??= new();
        all.RemoveAll(p => p.Name == name);
        all.Add(preset);
        _config.Save();
        return preset;
    }

    /// <summary>Puts a global preset on a character: each of its pages' settings replace theirs.</summary>
    public bool Apply(string presetName, string character)
    {
        var preset = (_config.GlobalPresets ??= new()).Find(p => p.Name == presetName);
        if(preset == null || character.Length == 0)
            return false;
        foreach(var (part, settings, type) in PerCharacter())
        {
            if(!preset.Parts.TryGetValue(part, out var json))
                continue;
            try
            {
                if(JsonConvert.DeserializeObject(json, type, Json) is { } value)
                    settings[character] = value;
            }
            catch(Exception e)
            {
                Plugin.Log.Debug(e, $"Limber: couldn't put the {part} part of a global preset on");
            }
        }
        _config.Save();
        return true;
    }

    /// <summary>Set by the plugin: puts a saved look (by its name) on a character that just came into the scene.</summary>
    public Action<nint, ushort, string>? PutOnLook;

    /// <summary>Framework thread, once a frame: characters new to the scene get the presets their rules give them.</summary>
    public void Update()
    {
        long now = Environment.TickCount64;
        if(now < _nextScan)
            return;
        _nextScan = now + 500;
        var rules = _config.GlobalPresetRules;
        var here = new HashSet<nint>();
        foreach(var obj in _objects)
        {
            if(obj is not Dalamud.Game.ClientState.Objects.Types.ICharacter || obj.Address == 0)
                continue;
            here.Add(obj.Address);
            if(_seen.Contains(obj.Address))
                continue;
            _seen.Add(obj.Address);
            if(rules == null || rules.Count == 0)
                continue;
            string name = obj.Name.TextValue;
            if(name.Length == 0)
                continue;
            foreach(var rule in rules)
            {
                if(!rule.Enabled || !rule.Character.Equals(name, StringComparison.OrdinalIgnoreCase))
                    continue;
                if(rule.Preset.Length > 0)
                    Apply(rule.Preset, name);
                if(rule.Look.Length > 0)
                    PutOnLook?.Invoke(obj.Address, obj.ObjectIndex, rule.Look);
            }
        }
        _seen.IntersectWith(here); // (gone ones are new again when they come back)
    }
}
