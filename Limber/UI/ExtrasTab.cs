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
    /// <summary>The flat-colour backdrop.</summary>
    public BackdropService? Backdrop { get; set; }
    /// <summary>Dataset capture.</summary>
    public DatasetCapture? Dataset { get; set; }

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
        ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(200f * ImGuiHelpers.GlobalScale));
        if(ImGui.SliderFloat("Bounce###extras_ragdoll_bounce", ref bounce, 0f, 0.95f, "%.2f"))
            RagdollService.Bounce = bounce;
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("How much a part bounces back off the floor.");
        ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(200f * ImGuiHelpers.GlobalScale));
        if(ImGui.SliderFloat("Slide###extras_ragdoll_slide", ref slide, 0f, 0.995f, "%.3f"))
            RagdollService.Slide = slide;
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("How far they slide along the floor (high: like ice).");
        int ragdollSteps = RagdollService.Substeps;
        ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(200f * ImGuiHelpers.GlobalScale));
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
        DrawLobbyWalk();
        DrawBackdrop();
        DrawDataset();
    }

    // ---- Walking round the character select screen

    private string _lobbyStatus = "";

    private void DrawLobbyWalk()
    {
        if(_config == null)
            return;
        ImGui.Spacing();
        ImGui.Separator();
        bool walk = _config.LobbyWalk;
        if(ImGui.Checkbox("Walk around on the character select screen###extras_lobby_walk", ref walk))
        {
            _config.LobbyWalk = walk;
            _config.Save();
        }
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("On the character select screen, the character shown moves with W / A / S / D (from where the camera\n"
                + "looks), runs (hold Shift to walk) and turns the way it goes. The screen has no world in it, so there's no\n"
                + "ground, walls or jumping: it slides over the floor it stands on. Only on your screen; logging in is as usual.");
        if(!_config.LobbyWalk)
            return;
        float run = _config.LobbyRunSpeed, slow = _config.LobbyWalkSpeed;
        ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(160f * ImGuiHelpers.GlobalScale));
        if(ImGui.SliderFloat("Run speed###extras_lobby_run", ref run, 1f, 20f, "%.1f yalms/s"))
        {
            _config.LobbyRunSpeed = run;
            _config.Save();
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(160f * ImGuiHelpers.GlobalScale));
        if(ImGui.SliderFloat("Walk speed###extras_lobby_walkspeed", ref slow, 0.5f, 10f, "%.1f yalms/s"))
        {
            _config.LobbyWalkSpeed = slow;
            _config.Save();
        }
        bool follow = _config.LobbyCameraFollow;
        if(ImGui.Checkbox("Camera follows them###extras_lobby_follow", ref follow))
        {
            _config.LobbyCameraFollow = follow;
            _config.Save();
        }
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("The camera goes along with whoever is walking (you still turn and zoom it as usual).");
        bool inLobby = LobbyWalk.InLobby;
        using(ImRaii.Disabled(!inLobby))
        {
            if(ImGui.Button("Back to the middle###extras_lobby_back"))
                LobbyWalk.PutBack();
        }
        if(LobbyWalk.Status.Length > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(LobbyWalk.Status);
        }

        // Actors on the character select screen.
        ImGui.TextUnformatted("Actors here");
        using(ImRaii.Disabled(!inLobby))
        {
            if(ImGui.Button("Add a copy###extras_lobby_add"))
                _lobbyStatus = LobbyWalk.AddActor(null);
            if(ImGui.IsItemHovered())
                ImGui.SetTooltip("Adds an actor that looks like the character shown, beside them.");
            ImGui.SameLine();
            var looks = _config.AppearancePresets ?? new();
            ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(200f * ImGuiHelpers.GlobalScale));
            using(var combo = ImRaii.Combo("###extras_lobby_look", "Add one wearing a saved look..."))
            {
                if(combo.Success)
                    foreach(var look in looks)
                        if(ImGui.Selectable($"{look.Name}###extras_lobby_look_{look.Name}"))
                            _lobbyStatus = LobbyWalk.AddActor(look.Copy());
            }
        }
        if(!inLobby)
            ImGui.TextDisabled("(on the character select screen)");
        // Who walks: the character shown or one of the actors.
        string whoLabel = LobbyWalk.WalkerNumber < 0 ? "The character shown" : $"Lobby Actor {LobbyWalk.WalkerNumber}";
        ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(200f * ImGuiHelpers.GlobalScale));
        using(var combo = ImRaii.Combo("Who walks###extras_lobby_who", whoLabel))
        {
            if(combo.Success)
            {
                if(ImGui.Selectable("The character shown###extras_lobby_who_main", LobbyWalk.WalkerNumber < 0))
                    LobbyWalk.WalkerNumber = -1;
                foreach(var (number, name) in LobbyWalk.Actors)
                    if(ImGui.Selectable($"{name}###extras_lobby_who_{number}", LobbyWalk.WalkerNumber == number))
                        LobbyWalk.WalkerNumber = number;
            }
        }
        int remove = 0;
        foreach(var (number, name) in LobbyWalk.Actors)
        {
            ImGui.Bullet();
            ImGui.SameLine();
            ImGui.TextUnformatted(name);
            ImGui.SameLine();
            if(ImGui.SmallButton($"Remove###extras_lobby_rm_{number}"))
                remove = number;
        }
        if(remove > 0)
            LobbyWalk.RemoveActor(remove);
        if(_lobbyStatus.Length > 0)
            ImGui.TextDisabled(_lobbyStatus);
    }

    // ---- Dataset capture: a set of pictures of the chosen character, for training an image model (a LoRA)

    private void DrawDataset()
    {
        if(_config == null || Dataset == null)
            return;
        ImGui.Spacing();
        ImGui.Separator();
        if(!ImGui.CollapsingHeader("Dataset capture###extras_dataset"))
            return;
        var s = _config.Dataset ??= new();
        bool changed = false;
        ImGui.TextWrapped("Takes pictures of the chosen character from all round (front, sides, behind, above and below) at four "
            + "distances, for training an image model. Each is copied straight from the game's picture (no windows, dots or "
            + "plugin overlays in it), cropped round the character and saved as a PNG: full body and thigh up at 1024 x 1280, "
            + "upper body and face square.");
        using(ImRaii.Disabled(Dataset.Running))
        {
            bool full = s.FullBody, thigh = s.ThighUp, upper = s.UpperBody, face = s.Face;
            if(ImGui.Checkbox("Full body###ds_full", ref full)) { s.FullBody = full; changed = true; }
            ImGui.SameLine();
            if(ImGui.Checkbox("Thigh up###ds_thigh", ref thigh)) { s.ThighUp = thigh; changed = true; }
            ImGui.SameLine();
            if(ImGui.Checkbox("Upper body###ds_upper", ref upper)) { s.UpperBody = upper; changed = true; }
            ImGui.SameLine();
            if(ImGui.Checkbox("Face###ds_face", ref face)) { s.Face = face; changed = true; }
            bool large = s.LargeSquares;
            if(ImGui.Checkbox("Square pictures at 1280 x 1280 (else 1024)###ds_large", ref large)) { s.LargeSquares = large; changed = true; }
            bool captions = s.Captions;
            if(ImGui.Checkbox("Caption files###ds_captions", ref captions)) { s.Captions = captions; changed = true; }
            if(ImGui.IsItemHovered())
                ImGui.SetTooltip("A .txt next to each picture: your trigger word, then the shot (full body, three-quarter view, from above...).");
            if(captions)
            {
                ImGui.SameLine();
                string trigger = s.Trigger;
                ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(180f * ImGuiHelpers.GlobalScale));
                if(ImGui.InputText("Trigger word###ds_trigger", ref trigger, 64)) { s.Trigger = trigger; changed = true; }
            }
            string folder = s.Folder;
            ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(360f * ImGuiHelpers.GlobalScale));
            if(ImGui.InputText("Folder###ds_folder", ref folder, 260)) { s.Folder = folder; changed = true; }
            if(ImGui.IsItemHovered())
                ImGui.SetTooltip("Where the pictures go (a new folder inside it each time). Empty: Pictures\\Limber dataset.");
        }
        if(changed)
            _config.Save();

        int count = DatasetCapture.Shots(s).Count;
        var actor = _selection.Actor;
        if(!Dataset.Running)
        {
            using(ImRaii.Disabled(!actor.IsSet || count == 0))
                if(ImGui.Button($"Take {count} pictures of {(actor.IsSet ? actor.Name : "...")}###ds_start", new System.Numerics.Vector2(260 * ImGuiHelpers.GlobalScale, 0)))
                    _datasetNote = Dataset.Start(actor.Address, actor.ObjectIndex, actor.Name ?? "", s);
            if(ImGui.IsItemHovered())
                ImGui.SetTooltip("The camera moves round them by itself; it's put back afterwards. Hide the game's own UI first "
                    + "(it would be in the pictures), and keep the character still (a pose, or the animation frozen).");
        }
        else
        {
            if(ImGui.Button("Stop###ds_stop", new System.Numerics.Vector2(120 * ImGuiHelpers.GlobalScale, 0)))
                Dataset.Stop();
            ImGui.SameLine();
            ImGui.ProgressBar(Dataset.Total > 0 ? (float)Dataset.Done / Dataset.Total : 0f, new System.Numerics.Vector2(-1, 0), $"{Dataset.Done} / {Dataset.Total}");
        }
        string note = Dataset.Note.Length > 0 ? Dataset.Note : _datasetNote;
        if(note.Length > 0)
            ImGui.TextWrapped(note);
        if(!Dataset.Running && Dataset.LastFolder.Length > 0 && System.IO.Directory.Exists(Dataset.LastFolder))
        {
            if(ImGui.SmallButton("Open the folder###ds_open"))
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{Dataset.LastFolder}\"") { UseShellExecute = true });
                }
                catch
                {
                }
            }
        }
    }

    private string _datasetNote = "";

    // ---- Backdrop: a flat colour all round the chosen character (clean pictures, training sets)

    private System.Numerics.Vector3? _pickColour;
    private float? _dragBrightness;
    private long _pickedAt;

    private void DrawBackdrop()
    {
        if(_config == null || Backdrop == null)
            return;
        ImGui.Spacing();
        ImGui.Separator();
        var s = _config.Backdrop ??= new();
        bool on = s.Enabled;
        if(ImGui.Checkbox("Backdrop (a flat colour all round)###extras_backdrop", ref on))
            s.Enabled = on;
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("Surrounds the chosen character with one flat colour, sky, ground and all, so they look like they're in an empty void.\n"
                + "Good for clean pictures (a training set for an image model, say). Only on your screen; never on by itself when Limber starts.");
        if(!on)
            return;
        ImGui.Indent();
        bool changed = false;

        // Colour: made again once you stop changing it (not on every step of the picker).
        var colour = _pickColour ?? s.Colour;
        if(ImGui.ColorEdit3("Colour###extras_backdrop_colour", ref colour))
        {
            _pickColour = colour;
            _pickedAt = System.Environment.TickCount64;
        }
        if(_pickColour is { } picked && System.Environment.TickCount64 - _pickedAt > 300 && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            s.Colour = picked;
            _pickColour = null;
            changed = true;
        }
        ImGui.SameLine();
        if(ImGui.SmallButton("White###extras_backdrop_white"))
        {
            s.Colour = System.Numerics.Vector3.One;
            _pickColour = null;
            changed = true;
        }
        ImGui.SameLine();
        if(ImGui.SmallButton("Green screen###extras_backdrop_green"))
        {
            s.Colour = new System.Numerics.Vector3(0f, 1f, 0f);
            _pickColour = null;
            changed = true;
        }
        ImGui.SameLine();
        if(ImGui.SmallButton("Black###extras_backdrop_black"))
        {
            s.Colour = System.Numerics.Vector3.Zero;
            _pickColour = null;
            changed = true;
        }

        // (applied when let go: the backdrop is made again for it)
        float glow = _dragBrightness ?? s.Glow;
        ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(200f * ImGuiHelpers.GlobalScale));
        if(ImGui.SliderFloat("Glow###extras_backdrop_bright", ref glow, 0f, 3f, "%.2f"))
            _dragBrightness = glow;
        if(ImGui.IsItemDeactivated() && _dragBrightness is { } done)
        {
            s.Glow = done;
            _dragBrightness = null;
            changed = true;
        }
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("How brightly it glows. Too high and the game's glare spills it over the character's edges as a halo:\n"
                + "keep it around 0.5, and turn Glare off in the game's graphics settings. Turn it up if the colour looks dull.");

        float radius = s.Radius;
        ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(200f * ImGuiHelpers.GlobalScale));
        if(ImGui.SliderFloat("Size###extras_backdrop_radius", ref radius, 3f, 60f, "%.0f m"))
        {
            s.Radius = radius;
            changed = true;
        }
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("How far it is from them. The camera has to stay inside it (zoomed out past it, you see the world again).");

        float lift = s.Lift;
        ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(200f * ImGuiHelpers.GlobalScale));
        if(ImGui.SliderFloat("Lift into the sky###extras_backdrop_lift", ref lift, 0f, 150f, "%.0f m"))
        {
            s.Lift = lift;
            changed = true;
        }
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("The chosen character and the camera go up this high, away from the ground and everything on it, so they float in the void.\n"
                + "0: they stay where they are (use the floor below to hide the ground). Let down again when the backdrop is turned off.");

        bool floor = s.Floor;
        if(ImGui.Checkbox("Floor of the same colour###extras_backdrop_floor", ref floor))
        {
            s.Floor = floor;
            changed = true;
        }
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("Hides the ground under them, so they stand in the void. Off: they stand on the real ground.");
        if(floor)
        {
            ImGui.SameLine();
            float height = s.FloorHeight * 100f;
            ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(140f * ImGuiHelpers.GlobalScale));
            if(ImGui.SliderFloat("Floor height###extras_backdrop_floor_h", ref height, -20f, 30f, "%.0f cm"))
            {
                s.FloorHeight = height / 100f;
                changed = true;
            }
            if(ImGui.IsItemHovered())
                ImGui.SetTooltip("Up a little if grass or bumps in the ground poke through; down if it hides their feet.");
        }

        bool follow = s.Follow;
        if(ImGui.Checkbox("Follows the chosen character###extras_backdrop_follow", ref follow))
        {
            s.Follow = follow;
            changed = true;
        }
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("It stays centred on whoever is chosen at the top. Off: it stays where it is.");

        if(changed)
            _config.Save();
        if(Backdrop.Note.Length > 0)
            ImGui.TextDisabled(Backdrop.Note);
        else if(!Backdrop.Showing)
            ImGui.TextDisabled("Making it... (in GPose, with a character chosen)");
        ImGui.TextDisabled("Inside it the sun doesn't reach them: light them with GPose's own lights. For a perfect background, "
            + "use Green screen and the tools/backdrop_key script on the pictures afterwards (pure white, no green fringe). "
            + "Other characters inside it still show: hide them from the actor list.");
        ImGui.Unindent();
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
        ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(200f * ImGuiHelpers.GlobalScale));
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
            ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(140f * ImGuiHelpers.GlobalScale));
            if(ImGui.InputText("Mask (hex)###extras_ragdoll_mask", ref _filterMaskText, 18)
                && ulong.TryParse(_filterMaskText, System.Globalization.NumberStyles.HexNumber, null, out var mask))
            {
                w.FilterMask = mask;
                WorldCollision.ClearCache();
                _config.Save();
            }
            ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(140f * ImGuiHelpers.GlobalScale));
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
