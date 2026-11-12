using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Limber.Rig;

namespace Limber.UI;

/// <summary>
/// A small window listing every character in the scene: click one to choose it, the eye hides or shows it, the cross
/// removes an actor Limber added. Opened and closed from the main window's top bar.
/// </summary>
public sealed class ActorListWindow : Window
{
    private readonly Selection _selection;
    private readonly RigSettings _config;
    private readonly ActorSpawner _spawner;
    private string _filter = "";
    private bool _onlyScene = true;

    public ActorListWindow(Selection selection, RigSettings config, ActorSpawner spawner)
        : base("Limber - Actors###limber_actor_list", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        _selection = selection;
        _config = config;
        _spawner = spawner;
        Size = new Vector2(280, 360);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(220, 160), MaximumSize = new Vector2(700, 1600) };
        RespectCloseHotkey = false;
    }

    // Its X: Dalamud closes it, but this re-opened it from the setting on the next frame before OnClose ever ran, so
    // the X did nothing. Closed since last frame (by the X) -> OnClose first, then the setting decides.
    private bool _wasOpen;

    public override void PreOpenCheck()
    {
        if(_wasOpen && !IsOpen)
            OnClose();
        IsOpen = _config.ShowActorList;
        _wasOpen = IsOpen;
    }

    public override void OnClose()
    {
        if(!_config.ShowActorList)
            return;
        _config.ShowActorList = false;
        _config.Save();
    }

    private enum Kind { You, Limber, Player, Npc, Other }

    private static readonly (string Tag, Vector4 Colour)[] Tags =
    [
        ("you", new(0.55f, 0.80f, 1.00f, 1f)),
        ("actor", new(0.75f, 0.60f, 1.00f, 1f)),
        ("player", new(0.60f, 0.90f, 0.60f, 1f)),
        ("npc", new(0.95f, 0.80f, 0.45f, 1f)),
        ("", new(0.65f, 0.65f, 0.65f, 1f)),
    ];

    public override void Draw()
    {
        float scale = ImGuiHelpers.GlobalScale;
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("###actors_filter", "Filter by name", ref _filter, 64);
        ImGui.Checkbox("Only GPose / Limber actors###actors_scene", ref _onlyScene);
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("In GPose: only the characters in the scene (yours and actors), not the hidden copies or the crowd outside.");

        var made = _spawner.Made;
        var list = _selection.ListCharacters();
        Kind KindOf(ActorRef a)
        {
            if(a.ObjectIndex == 0 || a.ObjectIndex == 201)
                return Kind.You;
            if(made.Contains(a.ObjectIndex))
                return Kind.Limber;
            return Plugin.ObjectTable[a.ObjectIndex]?.ObjectKind switch
            {
                ObjectKind.Pc => Kind.Player,
                ObjectKind.BattleNpc or ObjectKind.EventNpc or ObjectKind.Retainer or ObjectKind.Companion => Kind.Npc,
                _ => Kind.Other,
            };
        }
        bool gpose = Plugin.ClientState.IsGPosing;
        // Names for the ones without one ("NPC One", "NPC Two"...), the same as the character drop-down's.
        var shownNames = ActorNames.Of(list);
        string NameOf(ActorRef a) => shownNames.TryGetValue(a.Address, out var n) ? n : a.Name ?? "";
        var shown = list.Where(a => (_filter.Length == 0 || NameOf(a).Contains(_filter, StringComparison.OrdinalIgnoreCase))
                                    && (!_onlyScene || !gpose || a.ObjectIndex >= 200)).ToList();

        float footer = ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y;
        using(var child = ImRaii.Child("###actors_rows", new Vector2(-1, -footer), true))
        {
            if(child.Success)
            {
                if(shown.Count == 0)
                    ImGui.TextDisabled(gpose || !_onlyScene ? "No characters." : "Nobody here yet.");
                foreach(var actor in shown)
                {
                    using var id = ImRaii.PushId($"actor_{actor.ObjectIndex}_{actor.Address:X}");
                    bool hidden = ActorVisibility.IsHidden(actor.Address);
                    float rowRight = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;

                    // The eye: hide or show.
                    using(ImRaii.PushFont(UiBuilder.IconFont))
                    {
                        if(ImGui.SmallButton((hidden ? FontAwesomeIcon.EyeSlash : FontAwesomeIcon.Eye).ToIconString()))
                            ActorVisibility.Toggle(actor.Address, actor.ObjectIndex);
                    }
                    if(ImGui.IsItemHovered())
                        ImGui.SetTooltip(hidden ? "Hidden: click to show it again." : "Click to hide this character (it stays in the scene).");
                    ImGui.SameLine();

                    // The name: click to choose it everywhere in Limber.
                    var kind = KindOf(actor);
                    bool chosen = actor.Address == _selection.Actor.Address;
                    string name = NameOf(actor);
                    name += ActorNames.Distance(actor);
                    float tagWidth = 52f * scale;
                    using(ImRaii.PushColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.TextDisabled), hidden))
                    {
                        if(ImGui.Selectable($"{name}###pick", chosen, ImGuiSelectableFlags.None, new Vector2(ImGui.GetContentRegionAvail().X - tagWidth - (kind == Kind.Limber ? 24f * scale : 0f), 0)))
                        {
                            _config.FollowTarget = false;
                            _config.Save();
                            _selection.Select(actor);
                        }
                    }
                    if(ImGui.IsItemHovered())
                        ImGui.SetTooltip($"{name}  #{actor.ObjectIndex}\nClick to choose it (every page works on the chosen character).");

                    // What it is.
                    ImGui.SameLine();
                    var (tag, colour) = Tags[(int)kind];
                    ImGui.TextColored(colour, tag);

                    // Limber's own actors can be removed here.
                    if(kind == Kind.Limber)
                    {
                        ImGui.SameLine(rowRight - 20f * scale);
                        using(ImRaii.PushFont(UiBuilder.IconFont))
                        {
                            if(ImGui.SmallButton(FontAwesomeIcon.Times.ToIconString()))
                            {
                                ActorVisibility.Show(actor.Address, actor.ObjectIndex);
                                _spawner.Despawn(actor.ObjectIndex);
                            }
                        }
                        if(ImGui.IsItemHovered())
                            ImGui.SetTooltip("Remove this actor.");
                    }
                }
            }
        }

        // Everyone back in view.
        using(ImRaii.Disabled(ActorVisibility.Count == 0))
        {
            if(ImGui.Button($"Show all ({ActorVisibility.Count} hidden)###actors_show_all"))
                ActorVisibility.ShowAll();
        }
        ImGui.SameLine();
        using(ImRaii.Disabled(!_spawner.CanAdd || Plugin.ObjectTable.LocalPlayer == null))
        {
            if(ImGui.Button("Add actor###actors_add") && Plugin.ObjectTable.LocalPlayer is { } me)
            {
                int index = _spawner.Spawn(me, false);
                if(index >= 0 && Plugin.ObjectTable[index] is { } added)
                {
                    _config.FollowTarget = false;
                    _config.Save();
                    _selection.Select(ActorRef.From(added));
                }
            }
        }
        if(ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(_spawner.CanAdd ? "A new actor (a copy of you) a step in front of you, chosen at once." : "Enter GPose to add actors.");
    }
}
