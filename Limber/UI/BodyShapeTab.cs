using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Limber.BodyShape;

namespace Limber.UI;

/// <summary>
/// The Body Shape tab (like BodySlide): sliders in groups for the chosen character, shown live; presets to save,
/// load and share; and Build, which bakes the shape into every model the character wears.
/// </summary>
public sealed class BodyShapeTab
{
    private static readonly Vector4 Accent = new(0.6f, 0.9f, 1f, 1f);
    private static readonly Vector4 Dim = new(0.65f, 0.65f, 0.65f, 1f);
    private static readonly Vector4 Warn = new(0.95f, 0.75f, 0.30f, 1f);

    private readonly Selection _selection;
    private readonly RigSettings _config;
    private string _filter = "";
    private string _presetName = "";
    private int _preset = -1;
    private Limber.BodyRig.OneClickRig.Progress? _job;
    private string? _jobCharacter;
    private Dictionary<string, float>? _jobValues;
    private bool _jobIsBuild;
    private bool _jobApplied;
    private string? _message;
    private DateTime? _buildSoon; // Auto-build: when to build after a detail slider was let go

    /// <summary>Starts a one-click rig of everything (set by the plugin); false if it couldn't start.</summary>
    public Func<bool>? RigEverything { get; set; }

    public BodyShapeTab(Selection selection, RigSettings config)
    {
        _selection = selection;
        _config = config;
    }

    private Dictionary<string, float> ValuesFor(string name)
    {
        _config.BodyShapes ??= new();
        if (!_config.BodyShapes.TryGetValue(name, out var values))
            _config.BodyShapes[name] = values = new Dictionary<string, float>();
        return values;
    }

    public void Draw()
    {
        var actor = _selection.Actor;
        if (!actor.IsSet)
        {
            ImGui.TextDisabled("Choose a character first.");
            return;
        }
        if (!Limber.Koi.Safety.AdultAllowed(actor.Address))
        {
            ImGui.TextDisabled("Not available for this character.");
            return;
        }
        var values = ValuesFor(actor.Name);
        bool changed = false;

        DrawPresets(values, ref changed);
        ImGui.Separator();

        bool preview = _config.BodyShapePreview;
        if (ImGui.Checkbox("Live preview###lmb_bs_preview", ref preview))
        {
            _config.BodyShapePreview = preview;
            changed = true;
        }
        Tooltip("Shows the sliders on the character straight away (only on your screen). Off: only what's built shows.");
        ImGui.SameLine();
        bool feet = _config.BodyShapeKeepFeet;
        if (ImGui.Checkbox("Keep feet on the ground###lmb_bs_feet", ref feet))
        {
            _config.BodyShapeKeepFeet = feet;
            changed = true;
        }
        Tooltip("With Leg length: the body goes up by what the legs gained, so the feet stay where they were.");
        ImGui.SetNextItemWidth(-130 * ImGuiHelpers.GlobalScale);
        ImGui.InputText("Filter###lmb_bs_filter", ref _filter, 64);
        Tooltip("Only the sliders (or groups) whose name has this in it.");

        bool detailShown = false;
        foreach (var group in BodySliders.Groups)
        {
            if (BodySliders.IsDetail(group) && !detailShown)
            {
                detailShown = true;
                ImGui.Spacing();
                ImGui.TextColored(Accent, "Detail");
                ImGui.TextColored(Dim, "Sculpted into the models when building (no live preview): press Build to see them, or turn on\nAuto-build to build by itself a moment after you let go of one.");
                bool auto = _config.BodyShapeAutoBuild;
                if (ImGui.Checkbox("Auto-build###lmb_bs_autobuild", ref auto))
                {
                    _config.BodyShapeAutoBuild = auto;
                    changed = true;
                }
                Tooltip("Builds (and redraws the character) about a second after you let go of a detail slider.");
            }
            var sliders = BodySliders.All.Where(s => s.Group == group && Matches(s)).ToList();
            if (sliders.Count == 0)
                continue;
            int moved = sliders.Count(s => values.TryGetValue(s.Key, out var v) && v != 0f);
            var flags = _filter.Length > 0 ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None;
            if (!ImGui.CollapsingHeader($"{group}{(moved > 0 ? $" ({moved})" : "")}###lmb_bs_group_{group}", flags))
                continue;
            using (ImRaii.PushId(group))
            {
                foreach (var slider in sliders)
                    changed |= DrawSlider(slider, values);
                if (moved > 0 && ImGui.SmallButton($"Reset {group.ToLowerInvariant()}###lmb_bs_reset_group"))
                {
                    foreach (var s in sliders)
                        values.Remove(s.Key);
                    changed = true;
                }
            }
        }

        ImGui.Spacing();
        if (ImGui.Button("Reset all###lmb_bs_reset_all"))
        {
            values.Clear();
            changed = true;
        }
        Tooltip("Every slider back to the body as it is.");

        ImGui.Separator();
        DrawBuild(actor, values);

        if (changed)
            _config.Save();
    }

    private bool Matches(BodySlider s)
        => _filter.Length == 0 || s.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase) || s.Group.Contains(_filter, StringComparison.OrdinalIgnoreCase);

    private bool DrawSlider(BodySlider slider, Dictionary<string, float> values)
    {
        float value = (values.TryGetValue(slider.Key, out var v) ? v : 0f) * 100f;
        ImGui.SetNextItemWidth(-130 * ImGuiHelpers.GlobalScale);
        string label = slider.LiveOnly ? $"{slider.Name} (live)" : slider.BuildOnly ? $"{slider.Name} (build)" : slider.Name;
        bool edited = ImGui.SliderFloat($"{label}###lmb_bs_{slider.Key}", ref value, slider.Min * 100f, slider.Max * 100f, "%+.0f%%");
        bool released = ImGui.IsItemDeactivatedAfterEdit();
        bool reset = ImGui.IsItemClicked(ImGuiMouseButton.Right);
        if (slider.BuildOnly && (released || reset))
            _buildSoon = DateTime.UtcNow.AddSeconds(1.0);
        Tooltip(slider.Tip + "\nCtrl+click to type a value. Right-click: back to 0.");
        if (reset)
        {
            values.Remove(slider.Key);
            return true;
        }
        if (!edited)
            return false;
        float next = Math.Clamp(value / 100f, slider.Min, slider.Max);
        if (MathF.Abs(next) < 0.005f)
            values.Remove(slider.Key);
        else
            values[slider.Key] = next;
        return true;
    }

    private void DrawPresets(Dictionary<string, float> values, ref bool changed)
    {
        var presets = _config.BodyPresets ??= new();
        ImGui.TextColored(Accent, "Preset");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        string current = _preset >= 0 && _preset < presets.Count ? presets[_preset].Name : "(none)";
        if (ImGui.BeginCombo("###lmb_bs_preset", current))
        {
            for (int i = 0; i < presets.Count; i++)
                if (ImGui.Selectable($"{presets[i].Name}###lmb_bs_preset_{i}", i == _preset))
                {
                    _preset = i;
                    _presetName = presets[i].Name;
                }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(_preset < 0 || _preset >= presets.Count))
        {
            if (ImGui.Button("Load###lmb_bs_load") && _preset >= 0 && _preset < presets.Count)
            {
                values.Clear();
                foreach (var (k, v) in presets[_preset].Values)
                    if (BodySliders.ByKey.TryGetValue(k, out var s))
                        values[k] = Math.Clamp(v, s.Min, s.Max);
                changed = true;
                _message = $"Loaded \"{presets[_preset].Name}\".";
            }
            Tooltip("Puts the preset's values on the sliders.");
            ImGui.SameLine();
            if (ImGui.Button("Delete###lmb_bs_delete") && _preset >= 0 && _preset < presets.Count)
            {
                _message = $"Deleted \"{presets[_preset].Name}\".";
                presets.RemoveAt(_preset);
                _preset = -1;
                changed = true;
            }
        }

        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        ImGui.InputText("Name###lmb_bs_preset_name", ref _presetName, 48);
        ImGui.SameLine();
        using (ImRaii.Disabled(_presetName.Trim().Length == 0))
        {
            if (ImGui.Button("Save###lmb_bs_save"))
            {
                var name = _presetName.Trim();
                int at = presets.FindIndex(p => p.Name == name);
                var preset = new BodyPreset { Name = name, Values = values.Where(kv => kv.Value != 0f).ToDictionary(kv => kv.Key, kv => kv.Value) };
                if (at >= 0)
                    presets[at] = preset;
                else
                {
                    presets.Add(preset);
                    at = presets.Count - 1;
                }
                _preset = at;
                changed = true;
                _message = $"Saved \"{name}\".";
            }
        }
        Tooltip("Saves the sliders as a preset with this name (replacing one with the same name).");
        ImGui.SameLine();
        if (ImGui.Button("Copy###lmb_bs_copy"))
        {
            var name = _presetName.Trim().Length > 0 ? _presetName.Trim() : "Body shape";
            ImGui.SetClipboardText(new BodyPreset { Name = name, Values = new Dictionary<string, float>(values) }.ToText());
            _message = "Copied the sliders as text.";
        }
        Tooltip("Copies the sliders as text, to share or keep.");
        ImGui.SameLine();
        if (ImGui.Button("Paste###lmb_bs_paste"))
        {
            var pasted = BodyPreset.FromText(ImGui.GetClipboardText() ?? "");
            if (pasted == null)
                _message = "The clipboard doesn't hold a body shape.";
            else
            {
                values.Clear();
                foreach (var (k, v) in pasted.Values)
                    values[k] = v;
                _presetName = pasted.Name;
                changed = true;
                _message = $"Pasted \"{pasted.Name}\" (Save to keep it as a preset).";
            }
        }
        Tooltip("Puts a copied body shape on the sliders.");
        if (_message != null)
            ImGui.TextColored(Dim, _message);
    }

    private void DrawBuild(ActorRef actor, Dictionary<string, float> values)
    {
        // Auto-build: a moment after a detail slider was let go (once nothing else is building).
        if (_buildSoon is { } due && DateTime.UtcNow >= due && _job is not { Done: false })
        {
            _buildSoon = null;
            if (_config.BodyShapeAutoBuild)
                StartJob(actor, values.Where(kv => BodySliders.ByKey.TryGetValue(kv.Key, out var s) && !s.LiveOnly && kv.Value != 0f)
                    .ToDictionary(kv => kv.Key, kv => kv.Value), build: true);
        }
        ImGui.TextColored(Accent, "Build");
        bool busy = _job is { Done: false };
        var bakeable = values.Where(kv => BodySliders.ByKey.TryGetValue(kv.Key, out var s) && !s.LiveOnly && kv.Value != 0f)
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        _config.BodyShapeBuilt ??= new();
        bool hasBuilt = _config.BodyShapeBuilt.TryGetValue(actor.Name, out var built) && built.Count > 0;
        bool differs = !hasBuilt ? bakeable.Count > 0 : !Same(bakeable, built!);
        using (ImRaii.Disabled(busy))
        {
            if (ImGui.Button(busy && _jobIsBuild ? "Building...###lmb_bs_build" : "Build###lmb_bs_build", new Vector2(160 * ImGuiHelpers.GlobalScale, 0)))
                StartJob(actor, bakeable, build: true);
            Tooltip("Bakes the shape into every model this character wears (body and outfit) as a Penumbra mod above your\n" +
                    "others in its collection, and redraws it. Your own mods are only read. Live-only sliders aren't built.");
            ImGui.SameLine();
            using (ImRaii.Disabled(!hasBuilt))
            {
                if (ImGui.Button("Remove built shape###lmb_bs_remove"))
                    StartJob(actor, new Dictionary<string, float>(), build: false);
            }
            Tooltip("Turns the built shape off for this character's collection (the sliders stay as they are).");
        }
        if (hasBuilt)
            ImGui.TextColored(differs ? Warn : Dim, differs
                ? "The sliders differ from what's built: the difference shows live. Build again to bake it in."
                : "Built into this character's models.");
        else
            ImGui.TextColored(Dim, "Nothing built yet: the shape is live only.");

        if (_job != null)
        {
            if (_job.Done && !_jobApplied)
            {
                _jobApplied = true;
                if (!_job.Failed && _jobCharacter != null)
                {
                    if (_jobIsBuild && _jobValues != null)
                        _config.BodyShapeBuilt[_jobCharacter] = _jobValues;
                    else if (!_jobIsBuild)
                        _config.BodyShapeBuilt.Remove(_jobCharacter);
                    _config.Save();
                }
            }
            using var child = ImRaii.Child("###lmb_bs_log", new Vector2(-1, 110 * ImGuiHelpers.GlobalScale), true);
            if (child.Success)
            {
                foreach (var line in _job.Lines)
                    ImGui.TextWrapped(line);
                if (_job.Done && ImGui.SmallButton("Clear###lmb_bs_log_clear"))
                    _job = null;
            }
        }
    }

    private void StartJob(ActorRef actor, Dictionary<string, float> bake, bool build)
    {
        _jobCharacter = actor.Name;
        _jobValues = bake;
        _jobIsBuild = build;
        _jobApplied = false;
        _job = build
            ? BodyShapeBuild.Start(actor.ObjectIndex, actor.Address, bake, null)
            : BodyShapeBuild.Remove(actor.ObjectIndex, actor.Address, null);
    }

    private static bool Same(Dictionary<string, float> a, Dictionary<string, float> b)
        => a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && MathF.Abs(v - kv.Value) < 1e-4f);

    private static void Tooltip(string text)
    {
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(text);
    }
}
