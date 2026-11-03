using System;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Limber.Rig;

namespace Limber.UI;

/// <summary>The Voice Switcher page (Scene): any character, each actor on its own, speaks with any player voice.</summary>
public sealed class VoiceTab
{
    private readonly Selection _selection;
    private string _search = "";
    private int _number;

    public VoiceTab(Selection selection)
    {
        _selection = selection;
    }

    public void Draw()
    {
        var characters = _selection.ListCharacters();
        if(characters.Count == 0)
        {
            ImGui.TextDisabled("No characters here right now.");
            return;
        }
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("###voice_search", "Search voices (race, clan, male / female)", ref _search, 48);

        // Every character, each with its own voice.
        using var table = ImRaii.Table("###voice_table", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY,
            new System.Numerics.Vector2(-1, -1));
        if(!table.Success)
            return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Character", ImGuiTableColumnFlags.WidthStretch, 2f);
        ImGui.TableSetupColumn("Voice", ImGuiTableColumnFlags.WidthStretch, 1.6f);
        ImGui.TableSetupColumn("###pick", ImGuiTableColumnFlags.WidthFixed, 150 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("###reset", ImGuiTableColumnFlags.WidthFixed, 110 * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();
        foreach(var c in characters)
        {
            using var id = ImRaii.PushId(c.Address.ToString());
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            bool chosen = c.Address == _selection.Actor.Address;
            using(ImRaii.PushColor(ImGuiCol.Text, new System.Numerics.Vector4(0.95f, 0.85f, 0.55f, 1f), chosen))
                ImGui.TextUnformatted(c.Label);

            ImGui.TableNextColumn();
            ushort now = VoiceSwitcher.Current(c.Address, c.ObjectIndex);
            bool swapped = VoiceSwitcher.IsSwapped(c.Address);
            ImGui.TextUnformatted(Describe(now) + (swapped ? $"  (own: {Describe(VoiceSwitcher.Own(c.Address, c.ObjectIndex))})" : ""));

            ImGui.TableNextColumn();
            if(ImGui.Button("Change voice###voice_change", new System.Numerics.Vector2(-1, 0)))
            {
                _number = now;
                ImGui.OpenPopup("###voice_pick");
            }
            DrawPicker(c, characters);

            ImGui.TableNextColumn();
            using(ImRaii.Disabled(!swapped))
            {
                if(ImGui.Button("Own voice###voice_reset", new System.Numerics.Vector2(-1, 0)))
                    VoiceSwitcher.Reset(c.Address, c.ObjectIndex);
            }
        }
    }

    private static string Describe(ushort voice)
    {
        if(voice == 0)
            return "-";
        var v = VoiceSwitcher.PlayerVoices.FirstOrDefault(x => x.Id == voice);
        return v.Group != null ? $"{v.Group} {v.Number}  (#{voice})" : $"#{voice}";
    }

    // The voices to pick from: every player voice by race, clan and sex, another character's, or a number.
    private void DrawPicker(ActorRef who, System.Collections.Generic.List<ActorRef> characters)
    {
        if(!ImGui.BeginPopup("###voice_pick"))
            return;
        try
        {
            DrawPickerContents(who, characters);
        }
        finally
        {
            ImGui.EndPopup();
        }
    }

    private void DrawPickerContents(ActorRef who, System.Collections.Generic.List<ActorRef> characters)
    {
        ImGui.TextUnformatted($"Voice for {who.Label}");
        ImGui.Separator();

        // Another character's voice.
        using(var from = ImRaii.Combo("###voice_from", "Copy another character's voice..."))
        {
            if(from.Success)
                foreach(var other in characters.Where(o => o.Address != who.Address))
                    if(ImGui.Selectable($"{other.Label}  ({Describe(VoiceSwitcher.Own(other.Address, other.ObjectIndex))})###voice_from_{other.Address}"))
                    {
                        VoiceSwitcher.Set(who.Address, who.ObjectIndex, VoiceSwitcher.Own(other.Address, other.ObjectIndex));
                        ImGui.CloseCurrentPopup();
                    }
        }
        // By number.
        ImGui.SetNextItemWidth(120 * ImGuiHelpers.GlobalScale);
        ImGui.InputInt("Voice number###voice_number", ref _number, 1, 10);
        ImGui.SameLine();
        if(ImGui.Button("Use###voice_number_use"))
        {
            VoiceSwitcher.Set(who.Address, who.ObjectIndex, (ushort)Math.Clamp(_number, 0, ushort.MaxValue));
            ImGui.CloseCurrentPopup();
        }
        ImGui.Separator();

        var voices = VoiceSwitcher.PlayerVoices;
        if(voices.Count == 0)
        {
            ImGui.TextDisabled("The game's voice list couldn't be read: use a number, or copy another character's voice.");
            return;
        }
        using var child = ImRaii.Child("###voice_list", new System.Numerics.Vector2(380 * ImGuiHelpers.GlobalScale, 320 * ImGuiHelpers.GlobalScale), true);
        if(!child.Success)
            return;
        ushort now = VoiceSwitcher.Current(who.Address, who.ObjectIndex);
        bool searching = _search.Length > 0;
        foreach(var group in voices.GroupBy(v => v.Group))
        {
            if(searching && !group.Key.Contains(_search, StringComparison.OrdinalIgnoreCase))
                continue;
            if(searching)
                ImGui.SetNextItemOpen(true, ImGuiCond.Always);
            if(!ImGui.TreeNode($"{group.Key}###voice_group_{group.Key}"))
                continue;
            foreach(var v in group)
                if(ImGui.Selectable($"Voice {v.Number}  (#{v.Id})###voice_{v.Id}_{group.Key}", v.Id == now))
                {
                    VoiceSwitcher.Set(who.Address, who.ObjectIndex, v.Id);
                    ImGui.CloseCurrentPopup();
                }
            ImGui.TreePop();
        }
    }
}
