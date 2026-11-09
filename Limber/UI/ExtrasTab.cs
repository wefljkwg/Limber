using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Limber.Rig;

namespace Limber.UI;

/// <summary>The Extras page (right after Global presets): fun one-press things, like Ragdoll mode.</summary>
public sealed class ExtrasTab
{
    private readonly Selection _selection;
    private readonly RagdollService _ragdoll;
    private readonly RigSettings? _config;
    /// <summary>For the extra minions.</summary>
    public EntitiesTab? Entities { get; set; }

    public ExtrasTab(Selection selection, RagdollService ragdoll, RigSettings? config = null)
    {
        _selection = selection;
        _ragdoll = ragdoll;
        _config = config;
    }

    public void Draw()
    {
        var actor = _selection.Actor;
        bool on = actor.IsSet && _ragdoll.IsOn(actor.Address);
        using(ImRaii.Disabled(!actor.IsSet))
        {
            if(ImGui.Button(on ? "Stop ragdoll###extras_ragdoll" : "Ragdoll mode###extras_ragdoll", new System.Numerics.Vector2(200 * ImGuiHelpers.GlobalScale, 0)) && actor.IsSet)
                _ragdoll.Toggle(actor.Address, actor.ObjectIndex);
        }
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip(on ? "They get back up (back to their pose)." : "The chosen character goes limp and falls where they stand.");
        ImGui.SameLine();
        using(ImRaii.Disabled(!on))
        {
            if(ImGui.Button("Push###extras_ragdoll_push") && on)
                _ragdoll.Push(actor.Address);
        }
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("Shoves them again while they're down (a random way, up and over).");
        if(!actor.IsSet)
            ImGui.TextDisabled("Choose a character at the top first.");

        // How lively it is.
        float bounce = RagdollService.Bounce, slide = RagdollService.Slide;
        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        if(ImGui.SliderFloat("Bounce###extras_ragdoll_bounce", ref bounce, 0f, 0.95f, "%.2f"))
            RagdollService.Bounce = bounce;
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("How much a part bounces back off the floor.");
        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        if(ImGui.SliderFloat("Slide###extras_ragdoll_slide", ref slide, 0f, 0.995f, "%.3f"))
            RagdollService.Slide = slide;
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("How far they slide along the floor (high: like ice).");
        int ragdollSteps = RagdollService.Substeps;
        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        if(ImGui.SliderInt("Steps a frame###extras_ragdoll_steps", ref ragdollSteps, 1, 8))
            RagdollService.Substeps = ragdollSteps;
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("Small physics steps each frame (at 60 fps; the same number per second at any frame rate): it "
                + "falls and settles the same at 60 and 144 fps. More: steadier, a little slower.");
        DrawWorldFloors();

        if(Entities != null)
        {
            ImGui.Spacing();
            ImGui.Separator();
            Entities.DrawMinions();
        }
        DrawHideMounts();
    }

    // Ragdoll on the real floors, stairs, beds and furniture (the game's own world collision).
    private string _filterMaskText = "", _filterValueText = "";

    private void DrawWorldFloors()
    {
        if(_config == null)
            return;
        var w = _config.WorldCollision ??= new WorldCollisionSettings();
        bool on = w.Ragdoll;
        if(ImGui.Checkbox("Real floors and furniture###extras_ragdoll_world", ref on))
        {
            w.Ragdoll = on;
            _config.Save();
        }
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("They land on the game's real ground: slopes, stairs, beds, tables and placed housing furniture\n"
                + "(off: a flat floor where they stood). Limber's own spawned props never count: they're only for looks.\n"
                + "If a surface filter skips the floor, they land on the flat floor where they stood.");
        if(!on)
            return;
        using var indent = ImRaii.PushIndent();
        if(!WorldCollision.Ready())
        {
            ImGui.TextColored(new System.Numerics.Vector4(0.95f, 0.4f, 0.4f, 1f), "Problem: " + WorldCollision.Problem);
            return;
        }
        if(RagdollService.WorldStatus.Length > 0)
            ImGui.TextDisabled(RagdollService.WorldStatus);
        bool walls = w.Walls;
        if(ImGui.Checkbox("Walls too###extras_ragdoll_walls", ref walls))
        {
            w.Walls = walls;
            _config.Save();
        }
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("Parts also stop at walls and the sides of furniture as they move (one more check per moving part).");
        int casts = w.MaxCastsPerFrame;
        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        if(ImGui.SliderInt("Checks per frame###extras_ragdoll_casts", ref casts, 8, 200))
        {
            w.MaxCastsPerFrame = casts;
            _config.Save();
        }
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("The most checks against the world in one frame. Lower is lighter; too low and parts may sink into the floor for a moment.");
        bool filter = w.UseFilter;
        if(ImGui.Checkbox("Skip invisible walls (surface filter)###extras_ragdoll_filter", ref filter))
        {
            w.UseFilter = filter;
            WorldCollision.ClearCache();
            _config.Save();
        }
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("Only surfaces of the kinds the game itself checks against (the same filter most of its own checks use).\n"
                + "Untick if they fall through something they should land on.");
        if(filter && ImGui.TreeNode("Surface filter (advanced)###extras_ragdoll_filter_adv"))
        {
            if(_filterMaskText.Length == 0)
                _filterMaskText = w.FilterMask.ToString("X");
            if(_filterValueText.Length == 0)
                _filterValueText = w.FilterValue.ToString("X");
            ImGui.SetNextItemWidth(140 * ImGuiHelpers.GlobalScale);
            if(ImGui.InputText("Mask (hex)###extras_ragdoll_mask", ref _filterMaskText, 18)
                && ulong.TryParse(_filterMaskText, System.Globalization.NumberStyles.HexNumber, null, out var mask))
            {
                w.FilterMask = mask;
                WorldCollision.ClearCache();
                _config.Save();
            }
            ImGui.SetNextItemWidth(140 * ImGuiHelpers.GlobalScale);
            if(ImGui.InputText("Value (hex)###extras_ragdoll_value", ref _filterValueText, 18)
                && ulong.TryParse(_filterValueText, System.Globalization.NumberStyles.HexNumber, null, out var value))
            {
                w.FilterValue = value;
                WorldCollision.ClearCache();
                _config.Save();
            }
            if(ImGui.Button("Game's usual (4000 / 4000)###extras_ragdoll_filter_reset"))
            {
                w.FilterMask = w.FilterValue = 0x4000;
                _filterMaskText = _filterValueText = "";
                WorldCollision.ClearCache();
                _config.Save();
            }
            ImGui.TreePop();
        }
    }

    // Riders with their mounts not drawn: floating.
    private void DrawHideMounts()
    {
        if(_config == null)
            return;
        ImGui.Spacing();
        ImGui.Separator();
        bool hide = _config.HideMounts;
        if(ImGui.Checkbox("Hide mounts###extras_hide_mounts", ref hide))
        {
            _config.HideMounts = hide;
            _config.Save();
        }
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("A riding character's mount isn't drawn, so they look like they're floating (only on your screen).\n"
                + "Shown again for loading screens, cutscenes and a moment as GPose starts or ends.");
        if(!hide)
            return;
        bool only = _config.HideMountsOnlyTicked;
        if(CharacterFilter.Draw("extras_mounts", "Hide mounts", ref only, _config.HideMountsCharacters ??= new(), _selection.ListCharacters(), _selection.Actor))
        {
            _config.HideMountsOnlyTicked = only;
            _config.Save();
        }
    }
}
