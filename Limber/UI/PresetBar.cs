using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Limber.UI;

/// <summary>
/// Your own presets for a page: save what the page is set to now under a name, and load it back (onto the chosen
/// character, for the pages that keep settings per character). One small bar at the top of each page that has settings.
/// </summary>
public static class PresetBar
{
    private static readonly Dictionary<string, string> Names = new();

    private static readonly JsonSerializerSettings Json = new()
    {
        ObjectCreationHandling = ObjectCreationHandling.Replace,
        ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
    };

    /// <param name="key">The page.</param>
    /// <param name="parts">What a preset holds: each part of the page's settings by name (made if missing), or null when
    /// there's nothing to save right now (no character chosen).</param>
    /// <param name="defaults">The same parts on fresh settings (what "Reset to default" puts back), or null.</param>
    public static void Draw(RigSettings config, string key, Func<Dictionary<string, object>?> parts, Func<Dictionary<string, object>?>? defaults = null)
    {
        var all = config.Presets ??= new();
        if(!all.TryGetValue(key, out var mine))
            all[key] = mine = new();
        Names.TryGetValue(key, out var name);
        name ??= "";

        var now = parts();
        using(ImRaii.Disabled(now == null))
        {
            ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(160f * ImGuiHelpers.GlobalScale));
            ImGui.InputTextWithHint($"###preset_name_{key}", "Preset name", ref name, 48);
            Names[key] = name;
            ImGui.SameLine();
            if(ImGui.Button($"Save preset###preset_save_{key}") && now != null && name.Trim().Length > 0)
            {
                var o = new JObject();
                var serializer = JsonSerializer.Create(Json);
                foreach(var (part, value) in now)
                    o[part] = value is PresetValues values ? values.Save(serializer) : JToken.FromObject(value, serializer);
                mine[name.Trim()] = o.ToString(Formatting.None);
                config.Save();
                Names[key] = "";
            }
            if(ImGui.IsItemHovered())
                ImGui.SetTooltip(mine.ContainsKey(name.Trim()) ? "Saves over the preset with this name." : "Saves this page's settings as a preset with this name.");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(200f * ImGuiHelpers.GlobalScale));
            using(var combo = ImRaii.Combo($"###preset_load_{key}", mine.Count == 0 ? "No presets yet" : "Load a preset..."))
            {
                if(combo.Success)
                {
                    string? remove = null;
                    foreach(var (presetName, json) in mine.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
                    {
                        if(ImGui.Selectable($"{presetName}###preset_{key}_{presetName}") && now != null)
                        {
                            Load(json, now);
                            config.Save();
                        }
                        if(ImGui.IsItemHovered())
                            ImGui.SetTooltip("Click to load it.  Ctrl + right-click to delete it.");
                        if(ImGui.IsItemClicked(ImGuiMouseButton.Right) && ImGui.GetIO().KeyCtrl)
                            remove = presetName;
                    }
                    if(remove != null)
                    {
                        mine.Remove(remove);
                        config.Save();
                    }
                }
            }
            if(defaults != null && now != null)
            {
                ImGui.SameLine();
                DrawReset(config, key, now, defaults);
            }
        }
        ImGui.Separator();
    }

    // ---- Reset to default: the whole page, or one group of its settings (asked once more before it's done)

    private static readonly Dictionary<string, string?> Pending = new();

    private static string Spaced(string part)
        => System.Text.RegularExpressions.Regex.Replace(part, "(?<=[a-z0-9])(?=[A-Z])", " ");

    private static void DrawReset(RigSettings config, string key, Dictionary<string, object> now, Func<Dictionary<string, object>?> defaults)
    {
        Pending.TryGetValue(key, out var pending);
        if(pending != null)
        {
            ImGui.TextUnformatted(pending.Length == 0 ? "Reset the whole page?" : $"Reset {Spaced(pending)}?");
            ImGui.SameLine();
            if(ImGui.SmallButton($"Yes###preset_reset_yes_{key}"))
            {
                Reset(now, defaults(), pending.Length == 0 ? null : pending);
                config.Save();
                Pending[key] = null;
            }
            ImGui.SameLine();
            if(ImGui.SmallButton($"No###preset_reset_no_{key}"))
                Pending[key] = null;
            return;
        }
        ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(150f * ImGuiHelpers.GlobalScale));
        using var combo = ImRaii.Combo($"###preset_reset_{key}", "Reset to default...");
        if(!combo.Success)
            return;
        if(ImGui.Selectable($"The whole page###preset_reset_all_{key}"))
            Pending[key] = "";
        if(now.Count > 1)
            foreach(var part in now.Keys)
                if(ImGui.Selectable($"{Spaced(part)}###preset_reset_{key}_{part}"))
                    Pending[key] = part;
    }

    // The parts (or the one named) put back to their defaults.
    private static void Reset(Dictionary<string, object> parts, Dictionary<string, object>? defaults, string? only)
    {
        var serializer = JsonSerializer.Create(Json);
        foreach(var (part, target) in parts)
        {
            if(only != null && part != only)
                continue;
            object? fresh = null;
            defaults?.TryGetValue(part, out fresh);
            try
            {
                if(target is PresetValues values)
                {
                    values.Reset(fresh as PresetValues, serializer);
                    continue;
                }
                fresh ??= Fresh(target);
                if(fresh == null)
                    continue;
                if(target is System.Collections.IDictionary dict)
                    dict.Clear();
                else if(target is System.Collections.IList list)
                    list.Clear();
                JsonConvert.PopulateObject(JsonConvert.SerializeObject(fresh, Json), target, Json);
            }
            catch(Exception e)
            {
                Plugin.Log.Warning(e, $"Limber: couldn't reset {part}");
            }
        }
    }

    // A new object of the same kind (its defaults), when the fresh settings didn't give one.
    private static object? Fresh(object target)
    {
        try
        {
            return Activator.CreateInstance(target.GetType());
        }
        catch
        {
            return null;
        }
    }

    // Each part put back as it was saved (a dictionary is emptied first, so it ends up exactly like the preset).
    private static void Load(string json, Dictionary<string, object> parts)
    {
        try
        {
            var o = JObject.Parse(json);
            foreach(var (part, target) in parts)
            {
                if(o[part] is not { } token)
                    continue;
                if(target is PresetValues values)
                {
                    values.Load(token, JsonSerializer.Create(Json));
                    continue;
                }
                if(target is System.Collections.IDictionary dict)
                    dict.Clear();
                else if(target is System.Collections.IList list)
                    list.Clear();
                JsonConvert.PopulateObject(token.ToString(Formatting.None), target, Json);
            }
        }
        catch(Exception e)
        {
            Plugin.Log.Warning(e, "Limber: couldn't load that preset");
        }
    }
}
