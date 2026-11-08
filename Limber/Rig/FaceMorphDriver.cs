using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Plugin.Services;
using Limber.Core;

namespace Limber.Rig;

/// <summary>One character's face morphs (Face morphs page, experimental), kept by name.</summary>
public sealed class FaceMorphSettings
{
    public bool Enabled { get; set; }
    /// <summary>The sliders: morph name -> amount (Limber's shapes by name, imported ones as "Source: name").</summary>
    public Dictionary<string, float> Values { get; set; } = new();
    /// <summary>The morphs follow Oral's stroke (when this character is the one with the mouth).</summary>
    public bool FollowOral { get; set; } = true;
    public float Stretch { get; set; } = 0.8f;      // lips stretched round the shaft, by how big it is for the mouth
    public float Vacuum { get; set; } = 0.7f;       // cheeks and lips sucked in / held out on the way out
    public float Deep { get; set; } = 0.7f;         // the face lengthened and strained when it goes deep
    public float NosePress { get; set; } = 0.6f;    // the nose squashed against the body at the deepest
    public float Breathing { get; set; } = 0.5f;    // the nostrils flaring with the breaths
    public float Strain { get; set; } = 0.5f;       // lids heavy, brows up inside, eye bags when it's deep
    public float ThroatBulge { get; set; } = 0.6f;  // the neck swelling when it goes down the throat

    public FaceMorphSettings Copy() => new()
    {
        Enabled = Enabled, Values = new(Values), FollowOral = FollowOral, Stretch = Stretch, Vacuum = Vacuum, Deep = Deep,
        NosePress = NosePress, Breathing = Breathing, Strain = Strain, ThroatBulge = ThroatBulge,
    };
}

/// <summary>
/// Drives the face morphs (Limber.Gpu.FaceMorphs) every frame: the chosen character's sliders, plus what Oral's stroke
/// does to the face (lips stretched, vacuum, deep throat, nose pressed, breathing) and the Ohogao face of Limber
/// Animations. One face at a time. Adult characters only. Experimental.
/// </summary>
public sealed unsafe class FaceMorphService
{
    private readonly IObjectTable _objects;
    private readonly Func<Dictionary<string, FaceMorphSettings>> _settings;
    private readonly Func<Dictionary<string, AhegaoSettings>> _ahegao;
    private readonly AhegaoService _ahegaoService;
    private readonly OralService _oral;
    private readonly SkeletonInfoCache _cache = new();
    private double _time;

    /// <summary>The character chosen in Limber's window (set by the plugin).</summary>
    public Func<(nint Address, string? Name)> Selected = () => (0, null);

    /// <summary>What it's doing (for the page).</summary>
    public string Status = "";
    /// <summary>The weights handed over last frame (for the page).</summary>
    public Dictionary<string, float> Last = new();

    // Framework thread -> bone thread: the neck's swell.
    private volatile float _throat;
    private nint _throatAt;
    private ushort _throatIndex;

    public FaceMorphService(IObjectTable objects, Func<Dictionary<string, FaceMorphSettings>> settings, OralService oral,
        AhegaoService ahegaoService, Func<Dictionary<string, AhegaoSettings>> ahegao)
    {
        _objects = objects;
        _settings = settings;
        _oral = oral;
        _ahegaoService = ahegaoService;
        _ahegao = ahegao;
    }

    // ---- Presets

    /// <summary>The Ohogao face's morphs (Limber Animations), at full.</summary>
    public static readonly Dictionary<string, float> Ohogao = new()
    {
        ["Lips tube (ohogao)"] = 1f, ["Upper lip long"] = 0.8f, ["Nose pulled down"] = 0.6f, ["Nostrils flared"] = 0.6f,
        ["Cheeks hollow"] = 0.8f, ["Long face"] = 0.55f, ["Chin down"] = 0.3f, ["Brows knit"] = 0.8f, ["Lower lids up"] = 0.3f,
        ["Eye bags"] = 0.3f,
        // (imported shapes, when there are: used too)
        ["VaM: PHMLipsPucker"] = 0.6f, ["VaM: Breathing_NoseIn"] = 0.4f, ["Koikatsu: kisu"] = 0.4f,
    };

    public static readonly (string Name, string Tip, Dictionary<string, float> Values)[] Presets =
    [
        ("Deepthroat", "Lips stretched round in an O, the face long and strained, the nose flared and pressed, heavy lids (like your reference).", new Dictionary<string, float>
        {
            ["Lips stretched"] = 0.9f, ["Lips round (O)"] = 0.6f, ["Lips forward (pucker)"] = 0.3f, ["Cheeks puffed"] = 0.25f,
            ["Nostrils flared"] = 0.6f, ["Nose squashed"] = 0.3f, ["Upper lip long"] = 0.4f, ["Long face"] = 0.5f, ["Chin down"] = 0.3f,
            ["Upper lids heavy"] = 0.5f, ["Lower lids up"] = 0.3f, ["Eye bags"] = 0.4f, ["Brows up inside"] = 0.3f,
            ["VaM: Blowjob Lips"] = 0.7f, ["VaM: deepthroat"] = 0.6f, ["Koikatsu: kuwae"] = 0.5f,
        }),
        ("Vacuum", "Cheeks sucked hollow, lips held out and clinging.", new Dictionary<string, float>
        {
            ["Lips tube (ohogao)"] = 0.5f, ["Vacuum lips"] = 0.8f, ["Cheeks hollow"] = 1f, ["Long face"] = 0.3f, ["Nostrils flared"] = 0.4f,
            ["VaM: BJ Cheeks - Suck Left"] = 0.8f, ["VaM: BJ Cheeks - Suck Right"] = 0.8f, ["VaM: PHMCheeksSinkL"] = 0.5f, ["VaM: PHMCheeksSinkR"] = 0.5f,
        }),
        ("Sloppy", "Mouth stretched and slack, lower lip out, cheeks full, lids drooping.", new Dictionary<string, float>
        {
            ["Lips stretched"] = 0.6f, ["Lower lip out"] = 0.5f, ["Cheeks puffed"] = 0.35f, ["Long face"] = 0.6f, ["Upper lids heavy"] = 0.6f,
            ["Mouth corners down"] = 0.3f, ["VaM: BJ Cheeks - Balloon Left"] = 0.4f, ["VaM: BJ Cheeks - Balloon Right"] = 0.4f,
        }),
        ("Gentle", "A soft pucker round it.", new Dictionary<string, float>
        {
            ["Lips forward (pucker)"] = 0.4f, ["Lips round (O)"] = 0.3f, ["Koikatsu: kisu"] = 0.4f,
        }),
        ("Ohogao", "The ohogao face: lips in a long tube, the face and nose drawn down, cheeks hollow, brows knit.", Ohogao),
        ("Strained", "Brows up inside, lids heavy, eye bags, nostrils flared (on top of anything).", new Dictionary<string, float>
        {
            ["Brows up inside"] = 0.7f, ["Upper lids heavy"] = 0.5f, ["Eye bags"] = 0.6f, ["Nostrils flared"] = 0.5f, ["Lower lids up"] = 0.3f,
        }),
    ];

    // ---- Every frame (framework thread)

    public void Update(double dt)
    {
        _time += dt;
        var all = _settings();
        var ahegao = _ahegao();
        var mouth = _oral.MouthState;
        nint best = 0;
        ushort bestIndex = 0;
        Dictionary<string, float>? weights = null;
        float throat = 0f;
        int bestScore = 0;
        string skipped = "";
        string bestName = "";
        foreach(var obj in _objects)
        {
            if(obj == null || obj.Address == 0)
                continue;
            // (the character chosen in Limber's window goes by the name the window shows: the page keeps its
            // settings under that, which for spawned actors and NPCs can differ from the game's name for them)
            var sel = Selected();
            string name = obj.Address == sel.Address && !string.IsNullOrEmpty(sel.Name) ? sel.Name! : obj.Name.TextValue;
            if(name.Length == 0)
                continue;
            all.TryGetValue(name, out var s);
            ahegao.TryGetValue(name, out var a);
            bool ohogao = a != null && a.Enabled && a.Type == AhegaoType.Ohogao && _ahegaoService.LevelOf(obj.Address) > 0f;
            bool oral = s != null && s.Enabled && s.FollowOral && mouth is { } m0 && m0.Giver == obj.Address;
            if((s == null || !s.Enabled) && !ohogao)
                continue;
            if(!Limber.Koi.Safety.AdultAllowed(obj.Address))
            {
                skipped = $"{name} skipped (not an adult character's body)";
                continue;
            }
            var w = new Dictionary<string, float>();
            if(s != null && s.Enabled)
                foreach(var (k, v) in s.Values)
                    w[k] = v;
            if(ohogao)
            {
                float level = Math.Clamp(_ahegaoService.LevelOf(obj.Address), 0f, 1f);
                foreach(var (k, v) in Ohogao)
                    w[k] = w.GetValueOrDefault(k) + v * level;
            }
            float neck = 0f;
            if(oral && mouth is { } m)
                neck = AddOral(w, s!, m);
            // Which one: Oral's mouth first, then the character chosen at the top of Limber's window, then a GPose
            // actor (in GPose the character you see is a copy with the same name as the one walking about, hidden).
            int score = oral ? 4 : obj.Address == sel.Address ? 3 : obj.ObjectIndex >= 200 ? 2 : 1;
            if(score > bestScore)
            {
                bestScore = score;
                weights = w;
                best = obj.Address;
                bestIndex = obj.ObjectIndex;
                bestName = name;
                throat = neck;
            }
        }
        if(weights == null || best == 0)
        {
            if(skipped.Length > 0 && skipped != _lastReport)
            {
                _lastReport = skipped;
                Plugin.Log.Information("Limber face morphs: " + skipped);
            }
            Limber.Gpu.FaceMorphs.Clear();
            Last = new();
            _throat = 0f;
            Status = "";
            return;
        }
        Limber.Gpu.FaceMorphs.Set(best, weights);
        Last = weights;
        _throatAt = best;
        _throatIndex = bestIndex;
        _throat = throat;
        Status = Limber.Gpu.FaceRig.Owner == best || Limber.Gpu.LipMesh.Owner == best
            ? "" : "Starting the face rig on the character (its face needs to be drawn)...";
        // (what it's doing, in the log when it changes: for when nothing shows)
        string report = $"#{bestIndex} ({bestName}), {weights.Count} morph(s); face rig: {(Limber.Gpu.FaceRig.Owner == best ? "on it" : Limber.Gpu.FaceRig.Running ? "on another character" : "not running")} "
            + $"({Limber.Gpu.FaceRig.Status}); last write: {Limber.Gpu.FaceMorphs.Applied}";
        if(report != _lastReport)
        {
            _lastReport = report;
            Plugin.Log.Information("Limber face morphs: " + report);
        }
    }

    private string _lastReport = "";

    private static float Smooth(float u)
    {
        u = Math.Clamp(u, 0f, 1f);
        return u * u * (3f - 2f * u);
    }

    // Oral's stroke onto the face. Returns how much the neck swells.
    private float AddOral(Dictionary<string, float> w, FaceMorphSettings s, OralService.Mouth m)
    {
        void Add(string k, float v)
        {
            if(MathF.Abs(v) > 1e-4f)
                w[k] = w.GetValueOrDefault(k) + v;
        }
        float blend = Math.Clamp(m.Blend, 0f, 1f);
        float big = Math.Clamp(m.Big, 0f, 1f);
        float depth = Math.Clamp(m.Depth, 0f, 1f);
        float outward = Math.Clamp(-m.Speed * 3f, 0f, 1f);  // coming out: the vacuum pulls hardest
        float deep = Smooth((depth - 0.55f) / 0.4f);
        float press = Smooth((depth - 0.85f) / 0.15f);
        // lips stretched round it
        Add("Lips stretched", s.Stretch * (0.3f + 0.7f * big) * blend);
        Add("Lips round (O)", s.Stretch * 0.4f * big * blend);
        Add("VaM: Blowjob Lips", s.Stretch * 0.6f * blend);
        Add("Koikatsu: kuwae", s.Stretch * 0.5f * blend);
        // the vacuum
        Add("Cheeks hollow", s.Vacuum * (0.35f + 0.65f * outward) * blend);
        Add("Vacuum lips", s.Vacuum * 0.8f * outward * blend);
        Add("Lips forward (pucker)", s.Vacuum * 0.3f * outward * blend);
        Add("VaM: BJ Cheeks - Suck Left", s.Vacuum * (0.3f + 0.7f * outward) * blend);
        Add("VaM: BJ Cheeks - Suck Right", s.Vacuum * (0.3f + 0.7f * outward) * blend);
        Add("VaM: BJ Lips - Move In-Out", s.Vacuum * (outward - Math.Clamp(m.Speed * 3f, 0f, 1f)) * blend);
        // deep: the face long, strained
        Add("Long face", s.Deep * 0.55f * deep * blend);
        Add("Chin down", s.Deep * 0.3f * deep * blend);
        Add("VaM: deepthroat", s.Deep * deep * blend);
        Add("Upper lids heavy", s.Strain * 0.6f * deep * blend);
        Add("Brows up inside", s.Strain * 0.45f * deep * blend);
        Add("Lower lids up", s.Strain * 0.3f * deep * blend);
        Add("Eye bags", s.Strain * (0.2f + 0.4f * deep) * blend);
        // the nose pressed against the body at the deepest
        Add("Nose squashed", s.NosePress * press * blend);
        Add("Chin pressed", s.NosePress * 0.4f * press * blend);
        // breathing through the nose
        float breath = 0.5f + 0.5f * MathF.Sin((float)(_time * Math.Tau * (1.1 + 0.6 * deep)));
        Add("Nostrils flared", s.Breathing * (0.25f + 0.55f * breath) * (0.6f + 0.4f * deep) * blend);
        Add("VaM: Breathing_NoseIn", s.Breathing * 0.5f * breath * blend);
        return s.ThroatBulge * Smooth((depth - 0.6f) / 0.3f) * blend;
    }

    // ---- Bone update stage: the neck swells when it goes down the throat

    private readonly Dictionary<(nint, int), (Vector3 Before, Vector3 Written)> _scaled = new();

    public void UpdateBones()
    {
        float k = _throat;
        nint at = _throatAt;
        if(at == 0)
            return;
        if(!ActorSkeleton.TryGet(at, _throatIndex, _cache, out var sk) || sk.Info.Count == 0)
            return;
        int neck = sk.Info.IndexOf("j_kubi");
        if(neck < 0)
            return;
        var now = sk.GetModelScale(neck);
        // (if nothing rebuilt the pose since last frame it still holds what was written: start from before)
        var before = _scaled.TryGetValue((at, neck), out var last) && now == last.Written ? last.Before : now;
        if(k < 1e-4f)
        {
            if(_scaled.Remove((at, neck)) && now == last.Written)
                sk.SetModelScale(neck, before);
            return;
        }
        var written = before * new Vector3(1f + 0.14f * k, 1f + 0.03f * k, 1f + 0.14f * k);
        sk.SetModelScale(neck, written);
        _scaled[(at, neck)] = (before, sk.GetModelScale(neck));
        if(_scaled.Count > 64)
            _scaled.Clear();
    }
}
