using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Types;
using Limber.Koi;
using Limber.Koi.Posing;

namespace Limber.UI;

/// <summary>
/// "Channel N -> character" picker used by the Video and VLC Live tabs. It edits the first link on that channel
/// (adding one if there's none), with the same rules as the Link tab: in GPose, only GPose actors.
/// </summary>
internal static class ChannelTarget
{
    private static readonly Vector4 Dim = new(0.65f, 0.65f, 0.65f, 1f);

    /// <summary>Draws a channel combo and a character combo on one line. Returns true when settings changed.</summary>
    public static bool Draw(KoiLink koi, string label, ref int channel, string id)
    {
        var cfg = koi.Config;
        bool changed = false;
        channel = Math.Clamp(channel, 0, Protocol.Wire.MaxChannels - 1);

        ImGui.TextUnformatted(label);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(110);
        if (ImGui.BeginCombo($"##{id}_channel", $"Channel {channel + 1}"))
        {
            for (int i = 0; i < Protocol.Wire.MaxChannels; i++)
            {
                if (ImGui.Selectable($"Channel {i + 1}", i == channel))
                {
                    channel = i;
                    changed = true;
                }
            }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Plays like a Koikatsu character on this channel. Use one Koikatsu isn't using.");

        ImGui.SameLine();
        ImGui.TextUnformatted("->");
        ImGui.SameLine();

        int ch = channel;
        var link = cfg.Links.FirstOrDefault(l => l.Channel == ch);
        string current = link == null
            ? "Pick a character"
            : link.TargetObjectIndex switch
            {
                0 => "Current GPose target",
                KoiLink.MyCharacter => "My character",
                _ => Describe(link.TargetObjectIndex),
            };
        if (link != null && koi.LinkViews.TryGetValue(link.Id, out var view) && !string.IsNullOrEmpty(view.ActorName))
            current += $"  ({view.ActorName})";

        ImGui.SetNextItemWidth(260);
        if (ImGui.BeginCombo($"##{id}_actor", current))
        {
            if (ImGui.Selectable("Current GPose target", link?.TargetObjectIndex == 0))
                changed |= Set(cfg, ch, 0);
            if (ImGui.Selectable("My character", link?.TargetObjectIndex == KoiLink.MyCharacter))
                changed |= Set(cfg, ch, KoiLink.MyCharacter);
            if (Plugin.ClientState.IsGPosing)
            {
                for (int idx = Safety.ClientObjectStart; idx < Math.Min(Safety.ClientObjectEnd, Plugin.ObjectTable.Length); idx++)
                {
                    if (Plugin.ObjectTable[idx] is not ICharacter c)
                        continue;
                    if (ImGui.Selectable($"{c.Name.TextValue}  [#{idx}]", link?.TargetObjectIndex == idx))
                        changed |= Set(cfg, ch, idx);
                }
            }
            else
            {
                ImGui.TextColored(Dim, "Enter GPose to pick other characters.");
            }
            ImGui.EndCombo();
        }
        return changed;
    }

    private static bool Set(Configuration cfg, int channel, int target)
    {
        var link = cfg.Links.FirstOrDefault(l => l.Channel == channel);
        if (link == null)
        {
            if (cfg.Links.Count >= KoiLink.MaxLinks)
                return false;
            link = new LinkConfig { Channel = channel };
            cfg.Links.Add(link);
        }
        link.TargetObjectIndex = target == KoiLink.MyCharacter || target == 0 || Safety.IsClientOnlyIndex(target) ? target : 0;
        link.Enabled = true;
        cfg.Enabled = true; // links have to be on for anything to move
        return true;
    }

    private static string Describe(int index)
    {
        if (index >= Plugin.ObjectTable.Length)
            return $"#{index}";
        var go = Plugin.ObjectTable[index];
        return go == null ? $"#{index} (not in scene)" : $"{go.Name.TextValue}  [#{index}]";
    }

    /// <summary>Shared "1 person / 2 people" + swap row. Returns true when changed.</summary>
    public static bool DrawPeople(ref int people, ref bool swap, string id)
    {
        bool changed = false;
        ImGui.TextUnformatted("People to follow:");
        ImGui.SameLine();
        if (ImGui.RadioButton($"1###{id}_p1", people <= 1)) { people = 1; changed = true; }
        ImGui.SameLine();
        if (ImGui.RadioButton($"2###{id}_p2", people >= 2)) { people = 2; changed = true; }
        if (people >= 2)
        {
            ImGui.SameLine();
            if (ImGui.Checkbox($"Swap sides###{id}_swap", ref swap))
                changed = true;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Two people: person 1 is whoever starts on the left of the video, person 2 on the right.\n" +
                                 "Tick this if they're the other way round.");
        }
        return changed;
    }
}
