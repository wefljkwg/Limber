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
    public static void Draw(RigSettings config, string key, Func<Dictionary<string, object>?> parts)
    {
        var all = config.Presets ??= new();
        if(!all.TryGetValue(key, out var mine))
            all[key] = mine = new();
        Names.TryGetValue(key, out var name);
        name ??= "";

        var now = parts();
        using(ImRaii.Disabled(now == null))
        {
            ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
            ImGui.InputTextWithHint($"###preset_name_{key}", "Preset name", ref name, 48);
            Names[key] = name;
            ImGui.SameLine();
            if(ImGui.Button($"Save preset###preset_save_{key}") && now != null && name.Trim().Length > 0)
            {
                var o = new JObject();
                foreach(var (part, value) in now)
                    o[part] = JToken.FromObject(value, JsonSerializer.Create(Json));
                mine[name.Trim()] = o.ToString(Formatting.None);
                config.Save();
                Names[key] = "";
            }
            if(ImGui.IsItemHovered())
                ImGui.SetTooltip(mine.ContainsKey(name.Trim()) ? "Saves over the preset with this name." : "Saves this page's settings as a preset with this name.");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
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
        }
        ImGui.Separator();
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
                if(target is System.Collections.IDictionary dict)
                    dict.Clear();
                JsonConvert.PopulateObject(token.ToString(Formatting.None), target, Json);
            }
        }
        catch(Exception e)
        {
            Plugin.Log.Warning(e, "Limber: couldn't load that preset");
        }
    }
}
