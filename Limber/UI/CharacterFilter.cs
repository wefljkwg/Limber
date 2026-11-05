using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace Limber.UI;

/// <summary>
/// "Characters affected" (Body physics, Body shape): everyone, or only the characters ticked (kept by name, so a
/// character ticked stays ticked when they leave and come back).
/// </summary>
public static class CharacterFilter
{
    /// <summary>Whether a character (by name) is affected.</summary>
    public static bool Allows(bool onlyTicked, HashSet<string>? names, string name)
        => !onlyTicked || (names != null && names.Contains(name));

    /// <summary>The section; true if anything changed.</summary>
    public static bool Draw(string id, string what, ref bool onlyTicked, HashSet<string> names, IEnumerable<ActorRef> present, ActorRef chosen)
    {
        bool changed = false;
        string summary = onlyTicked ? $"only {names.Count} ticked" : "everyone";
        if(!ImGui.CollapsingHeader($"Characters affected ({summary})###{id}_who"))
        {
            // (the chosen character's own tick, without opening the section)
            if(onlyTicked && chosen.IsSet && !string.IsNullOrEmpty(chosen.Name))
            {
                bool mine = names.Contains(chosen.Name);
                if(ImGui.Checkbox($"{what} on {chosen.Name}###{id}_who_mine", ref mine))
                {
                    if(mine)
                        names.Add(chosen.Name);
                    else
                        names.Remove(chosen.Name);
                    changed = true;
                }
            }
            return changed;
        }
        if(ImGui.RadioButton($"Everyone###{id}_who_all", !onlyTicked))
        {
            onlyTicked = false;
            changed = true;
        }
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip($"{what} works on every character it can (as before).");
        ImGui.SameLine();
        if(ImGui.RadioButton($"Only the ones ticked###{id}_who_some", onlyTicked))
        {
            onlyTicked = true;
            // (starting with the chosen character ticked, so turning it on doesn't switch everything off at once)
            if(names.Count == 0 && chosen.IsSet && !string.IsNullOrEmpty(chosen.Name))
                names.Add(chosen.Name);
            changed = true;
        }
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip($"{what} works only on the characters ticked below (kept by name); everyone else is left as they are.");

        using(ImRaii.Disabled(!onlyTicked))
        {
            var here = new List<string>();
            foreach(var a in present)
                if(!string.IsNullOrEmpty(a.Name) && !here.Contains(a.Name))
                    here.Add(a.Name);
            if(here.Count > 0 && ImGui.SmallButton($"Tick everyone here###{id}_who_tick_all"))
            {
                foreach(var n in here)
                    names.Add(n);
                changed = true;
            }
            ImGui.SameLine();
            if(ImGui.SmallButton($"Untick all###{id}_who_untick"))
            {
                names.Clear();
                changed = true;
            }
            foreach(var n in here.Concat(names.Where(n => !here.Contains(n)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase)).ToList())
            {
                bool on = names.Contains(n);
                string label = here.Contains(n) ? n : n + "  (not here)";
                if(ImGui.Checkbox($"{label}###{id}_who_{n}", ref on))
                {
                    if(on)
                        names.Add(n);
                    else
                        names.Remove(n);
                    changed = true;
                }
            }
        }
        return changed;
    }
}
