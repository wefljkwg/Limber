using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.Windowing;
using Limber.Core;
using Limber.Koi;
using Limber.Koi.Posing;

namespace Limber.UI;

public sealed class MainWindow : Window
{
    private static readonly Vector4 Good = new(0.45f, 0.85f, 0.45f, 1f);
    private static readonly Vector4 Warn = new(0.95f, 0.75f, 0.30f, 1f);
    private static readonly Vector4 Bad = new(0.95f, 0.40f, 0.40f, 1f);
    private static readonly Vector4 Dim = new(0.65f, 0.65f, 0.65f, 1f);

    private readonly KoiLink _plugin;
    private readonly BoneUpdateHook _hook;
    private readonly LimberTab _limberTab;
    private readonly VideoTab _videoTab;
    private readonly LiveTab _liveTab;
    private readonly PropsTab _propsTab;
    private readonly HousingPropsTab _housingTab;
    private readonly RendererTab _rendererTab;
    /// <summary>The Body Shape tab (set by the plugin).</summary>
    public BodyShapeTab? BodyShapeTab { get; set; }
    private int _portEdit;
    private string _newPresetName = "";
    /// <summary>Which link the Face tab captures from / shows presets for.</summary>
    private Guid _faceLink;

    private Configuration Cfg => _plugin.Config;

    private readonly SyncTab _syncTab;

    public void DisposeTabs() => _syncTab.Dispose();

    public MainWindow(KoiLink plugin, BoneUpdateHook hook, LimberTab limberTab, VideoTab videoTab, LiveTab liveTab, SyncHelpWindow syncHelp, PropsTab propsTab, RendererTab rendererTab, HousingPropsTab housingTab)
        : base($"Limber {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}###limber_main_window")
    {
        _plugin = plugin;
        _hook = hook;
        _limberTab = limberTab;
        _videoTab = videoTab;
        _liveTab = liveTab;
        _propsTab = propsTab;
        _housingTab = housingTab;
        _rendererTab = rendererTab;
        _syncTab = new SyncTab(plugin, syncHelp);
        _portEdit = plugin.Config.Port;
        // Game keys (WASD, arrows) never reach Limber's sliders.
        Flags |= ImGuiWindowFlags.NoNavInputs;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(560, 400),
            MaximumSize = new Vector2(1200, 2000),
        };
    }

    // ---- Layout: a bar on top (Limber on, which character), a list of pages on the left, the page on the right.

    private sealed record Page(string Id, string Group, string Title, string Blurb, Action Draw, Func<bool>? Shown = null);

    private Page[]? _pages;

    private Page[] Pages => _pages ??=
    [
        new("start", "", "Getting started", "What Limber does and where to begin.", DrawStartPage),

        new("pose", "Posing", "Pose", "Grab the dots on the character and drag them to pose. Pin bones so they stay put.", () => _limberTab.DrawPosePage()),
        new("hands", "Posing", "Hands", "Preset finger poses (fist, open, point...) for one hand or both.", () => _limberTab.DrawHandsPage()),
        new("scale", "Posing", "Bone scale", "Make any bone bigger or smaller, with control over the bones around it.", () => _limberTab.DrawBoneScalePage()),
        new("attach", "Posing", "Attach", "Hold a bone on to another character (hands on shoulders, holding hands...).", () => _limberTab.DrawAttachPage()),
        new("move", "Posing", "Move character", "Move, turn and resize the whole character.", () => _limberTab.DrawMovePage()),

        new("rig", "Body", "Body physics", "Soft, jiggling breasts, butt and more: one click sets it up from your body mod.", () => _limberTab.DrawRiggingTab()),
        new("shape", "Body", "Body shape", "Sliders for the body's shape (like BodySlide), live or built into your models.", () => BodyShapeTab?.Draw(), () => BodyShapeTab != null),

        new("devices", "Scene", "Devices", "Props that touch the body (colliders, toys) and move the soft bodies.", () => _propsTab.Draw()),
        new("props", "Scene", "Housing props", "Place housing furniture around you in GPose.", () => _housingTab.Draw()),
        new("renderer", "Scene", "Renderer", "Extra models and bones drawn on the character.", () => _rendererTab.Draw()),

        new("link", "Koikatsu", "Koikatsu link", "Pose FFXIV characters live from Koikatsu / KoiPose.", DrawKoikatsuPage),

        new("settings", "Settings", "Posing settings", "How the dots look and behave; resetting things.", () => _limberTab.DrawPosingSettingsPage()),
        new("hotkeys", "Settings", "Hotkeys", "Undo / redo keys.", () => _limberTab.DrawHotkeysTab()),
        new("advanced", "Settings", "Advanced", "Running outside GPose, unfinished pages.", DrawMainAdvanced),

        new("video", "Experimental", "Video (not working)", "Motion capture from a video file.", () => _videoTab.Draw(), () => Cfg.ShowVideoTab),
        new("vlc", "Experimental", "VLC live (not working)", "Motion capture from a live video.", () => _liveTab.Draw(), () => Cfg.ShowVlcTab),
    ];

    private string CurrentPage
    {
        get => Cfg.Ik.UiPage ?? "start";
        set
        {
            if (Cfg.Ik.UiPage == value)
                return;
            Cfg.Ik.UiPage = value;
            SaveLocked();
        }
    }

    public override void Draw()
    {
        if (!_hook.IsHooked)
        {
            ImGui.TextColored(Bad, _hook.Status);
            ImGui.Separator();
        }

        // Whole plugin on / off, and the character everything works on (shared by every page).
        var pluginOn = Cfg.PluginEnabled;
        if (ImGui.Checkbox("Limber on###limber_plugin_on", ref pluginOn))
        {
            Cfg.PluginEnabled = pluginOn;
            SaveLocked();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Turns all of Limber on or off (/limber on|off). Your setup is kept.");
        ImGui.SameLine();
        _limberTab.DrawShowHandles();
        if (!Cfg.PluginEnabled)
            ImGui.TextColored(Warn, "Limber is off: nothing is posed, simulated or linked.");
        else if (!Plugin.ClientState.IsGPosing && !(Cfg.AllowOutsideGPose && _limberTab.WorkOutsideGPose))
            ImGui.TextColored(Warn, "Enter GPose to use Limber (or turn on \"Also run outside GPose\" in Advanced).");
        _limberTab.DrawActorPicker();
        ImGui.Separator();

        var pages = Pages.Where(p => p.Shown?.Invoke() ?? true).ToArray();
        var current = pages.FirstOrDefault(p => p.Id == CurrentPage) ?? pages[0];

        float navWidth = 150f * Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale;
        using (var nav = Dalamud.Interface.Utility.Raii.ImRaii.Child("###limber_nav", new Vector2(navWidth, -1), true))
        {
            if (nav.Success)
            {
                string group = "";
                foreach (var page in pages)
                {
                    if (page.Group != group)
                    {
                        group = page.Group;
                        ImGui.Spacing();
                        ImGui.TextColored(Dim, group.ToUpperInvariant());
                    }
                    if (ImGui.Selectable($"{page.Title}###limber_nav_{page.Id}", page == current))
                        CurrentPage = page.Id;
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(page.Blurb);
                }
            }
        }
        ImGui.SameLine();
        using (var body = Dalamud.Interface.Utility.Raii.ImRaii.Child("###limber_page", new Vector2(-1, -1), false))
        {
            if (body.Success)
            {
                using var id = Dalamud.Interface.Utility.Raii.ImRaii.PushId("page_" + current.Id);
                ImGui.TextColored(Good, current.Title);
                using (Dalamud.Interface.Utility.Raii.ImRaii.PushColor(ImGuiCol.Text, Dim))
                    ImGui.TextWrapped(current.Blurb);
                ImGui.Separator();
                ImGui.Spacing();
                current.Draw();
            }
        }

        _videoTab.DrawDialogs();
    }

    // The first page: a checklist of what has to be right, and where to go for what.
    private void DrawStartPage()
    {
        ImGui.TextWrapped("Limber poses characters in GPose by dragging dots on their bones, gives bodies soft-body physics, " +
                          "and shapes and scales them. Everything it does is only on your screen.");
        ImGui.Spacing();
        ImGui.Text("Checklist");
        Check(Cfg.PluginEnabled, "Limber is on", "Tick \"Limber on\" at the top.");
        Check(_hook.IsHooked && _limberTab.PosingHookReady, "Limber can reach the game's bones", "Limber needs an update for this game version.");
        bool here = Plugin.ClientState.IsGPosing || (Cfg.AllowOutsideGPose && _limberTab.WorkOutsideGPose);
        Check(here, Plugin.ClientState.IsGPosing ? "In GPose" : "Running outside GPose", "Enter GPose (or turn on \"Also run outside GPose\" in Advanced).");
        Check(_limberTab.HasCharacter, "A character is chosen", "Pick one under Character at the top, or tick \"Follow my target\".");
        Check(_limberTab.PosingOn, "Posing is on", "Tick \"Posing on\" on the Pose page.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Text("What do you want to do?");
        ImGui.Spacing();
        Jump("Pose a character", "pose", "Drag the dots. Right-click a dot to pin it; middle-click to let go. Ctrl+Z undoes.");
        Jump("Pose the hands", "hands", "Fists, open hands, pointing...");
        Jump("Make a bone bigger or smaller", "scale", "Any bone, with or without the bones below it.");
        Jump("Hold on to another character", "attach", "A hand on a shoulder, holding hands...");
        Jump("Add body physics", "rig", "One click rigs your body mod for soft breasts and butt.");
        if (BodyShapeTab != null)
            Jump("Change the body's shape", "shape", "Sliders for every part of the body.");
        Jump("Place props or colliders", "devices", "Things the body can touch and press against.");
        Jump("Link Koikatsu", "link", "Pose from Koikatsu / KoiPose live.");
        ImGui.Spacing();
        ImGui.TextColored(Dim, "Hover a page's name on the left to see what it's for.");
    }

    private void Check(bool ok, string text, string fix)
    {
        ImGui.TextColored(ok ? Good : Warn, ok ? "  Done " : "  To do");
        ImGui.SameLine();
        ImGui.TextUnformatted(text);
        if (!ok)
        {
            ImGui.SameLine();
            ImGui.TextColored(Dim, "- " + fix);
        }
    }

    private void Jump(string label, string page, string tip)
    {
        if (ImGui.Button($"{label}###limber_jump_{page}", new Vector2(MathF.Min(260f * Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().X), 0)))
            CurrentPage = page;
        ImGui.SameLine();
        ImGui.TextColored(Dim, tip);
    }

    private void DrawKoikatsuPage()
    {
        DrawStatus();
        DrawLinkTab();
        ImGui.Spacing();
        if (ImGui.CollapsingHeader("Body###limber_kk_body"))
        {
            using (Dalamud.Interface.Utility.Raii.ImRaii.PushId("kk_body"))
                DrawBodyTab();
        }
        if (ImGui.CollapsingHeader("Face (Buggy)###limber_kk_face"))
        {
            using (Dalamud.Interface.Utility.Raii.ImRaii.PushId("kk_face"))
                DrawFaceTab();
        }
        if (ImGui.CollapsingHeader("Sync###limber_kk_sync"))
        {
            using (Dalamud.Interface.Utility.Raii.ImRaii.PushId("kk_sync"))
                _syncTab.Draw();
        }
        if (ImGui.CollapsingHeader("Advanced###limber_kk_advanced"))
        {
            using (Dalamud.Interface.Utility.Raii.ImRaii.PushId("kk_advanced"))
                DrawLinkAdvanced();
        }
    }

    // ----------------------------------------------------------------------------------------

    private void DrawStatus()
    {
        var receiver = _plugin.Receiver;

        var enabled = Cfg.Enabled;
        if (ImGui.Checkbox("All Koikatsu links enabled", ref enabled))
            Cfg.Enabled = enabled;
        ImGui.SameLine();
        ImGui.TextColored(Dim, "(/koilink on|off)");

        // Pause / resume Koikatsu's animations from here (same as Ctrl+F8 in Koikatsu).
        ImGui.SameLine();
        // With a live channel we know the state; otherwise just toggle.
        var kkLive = _plugin.KoikatsuLive;
        var paused = _plugin.KoikatsuAnimationPaused;
        var label = !kkLive ? "Pause / resume Koikatsu animation" : paused ? "Resume Koikatsu animation" : "Pause Koikatsu animation";
        if (ImGui.Button(label + "###limber_anim_pause"))
            _plugin.SendAnimationCommand(!kkLive ? Protocol.Wire.CmdToggle : paused ? Protocol.Wire.CmdResume : Protocol.Wire.CmdPause);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Freezes (or restarts) every character's animation in Koikatsu.\n" +
                             "Same as Ctrl+F8 in Koikatsu, or /limber pause.");

        if (receiver.LastError != null)
        {
            ImGui.TextColored(Bad, receiver.LastError);
            ImGui.Separator();
            return;
        }

        var live = new List<string>();
        for (int ch = 0; ch < Protocol.Wire.MaxChannels; ch++)
        {
            var f = receiver.GetLatest(ch);
            if (f != null && f.AgeSeconds <= Cfg.TimeoutSeconds)
                live.Add($"{ch + 1}: {f.Name}");
        }
        if (live.Count == 0)
            ImGui.TextColored(Warn, "No data from Koikatsu");
        else
            ImGui.TextColored(Good, "Koikatsu channels  " + string.Join("   ", live));
        ImGui.Separator();
    }

    private static string ChannelLabel(int ch, Limber.Koi.Net.PoseReceiver receiver, float timeout)
    {
        var f = receiver.GetLatest(ch);
        var name = f != null && f.AgeSeconds <= timeout ? f.Name : "no data";
        return $"Channel {ch + 1}  ({name})";
    }

    private void DrawLinkTab()
    {

        LinkConfig? remove = null;
        for (int i = 0; i < Cfg.Links.Count; i++)
        {
            var link = Cfg.Links[i];
            ImGui.PushID(link.Id.ToString());

            var on = link.Enabled;
            if (ImGui.Checkbox("##on", ref on))
            {
                link.Enabled = on;
                SaveLocked();
            }
            ImGui.SameLine();
            ImGui.Text($"Link {i + 1}");
            ImGui.SameLine();

            ImGui.SetNextItemWidth(170);
            if (ImGui.BeginCombo("##ch", ChannelLabel(link.Channel, _plugin.Receiver, Cfg.TimeoutSeconds)))
            {
                for (int ch = 0; ch < Protocol.Wire.MaxChannels; ch++)
                {
                    if (ImGui.Selectable(ChannelLabel(ch, _plugin.Receiver, Cfg.TimeoutSeconds), link.Channel == ch))
                    {
                        link.Channel = ch;
                        SaveLocked();
                    }
                }
                ImGui.EndCombo();
            }
            ImGui.SameLine();
            ImGui.Text("->");
            ImGui.SameLine();

            ImGui.SetNextItemWidth(200);
            var current = link.TargetObjectIndex switch
            {
                0 => "Current GPose target",
                KoiLink.MyCharacter => "My character",
                _ => DescribeObject(link.TargetObjectIndex),
            };
            if (ImGui.BeginCombo("##actor", current))
            {
                if (ImGui.Selectable("Current GPose target", link.TargetObjectIndex == 0))
                    SetTarget(link, 0);
                if (ImGui.Selectable("My character", link.TargetObjectIndex == KoiLink.MyCharacter))
                    SetTarget(link, KoiLink.MyCharacter);

                if (Plugin.ClientState.IsGPosing)
                {
                    // Only client-only GPose actors are offered (and accepted).
                    for (int idx = Safety.ClientObjectStart; idx < Math.Min(Safety.ClientObjectEnd, Plugin.ObjectTable.Length); idx++)
                    {
                        if (Plugin.ObjectTable[idx] is not ICharacter c)
                            continue;
                        if (ImGui.Selectable($"{c.Name.TextValue}  [#{idx}]", link.TargetObjectIndex == idx))
                            SetTarget(link, idx);
                    }
                }
                ImGui.EndCombo();
            }

            if (Cfg.Links.Count > 1)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Remove"))
                    remove = link;
            }

            var rt = _plugin.Applier.GetRuntime(link.Id);
            var status = rt.Status;
            var color = status.StartsWith("Driving") ? Good : status.StartsWith("Error") ? Bad : Warn;
            _plugin.LinkViews.TryGetValue(link.Id, out var view);
            var who = view != null && !string.IsNullOrEmpty(view.ActorName) ? $"{view.ActorName}: " : "";
            ImGui.Indent(28);
            ImGui.TextColored(color, who + status);
            ImGui.Unindent(28);

            ImGui.PopID();
        }

        if (remove != null)
        {
            Cfg.Links.Remove(remove);
            _plugin.Applier.ForgetLink(remove.Id);
            SaveLocked();
        }

        if (Cfg.Links.Count < KoiLink.MaxLinks && ImGui.Button("Add link"))
        {
            // Next unused channel, and a specific actor so it doesn't collide with link 1's GPose target.
            var used = Cfg.Links.Select(l => l.Channel).ToHashSet();
            var ch = Enumerable.Range(0, Protocol.Wire.MaxChannels).FirstOrDefault(c => !used.Contains(c));
            Cfg.Links.Add(new LinkConfig { Channel = ch, TargetObjectIndex = NextUnusedActor() });
            SaveLocked();
        }
        ImGui.TextColored(Dim, "Two links can share a channel (one Koikatsu character driving several actors), " +
                               "but each actor can only be driven by one link.");

    }

    // Koikatsu Link > Advanced: the port, the release time and in-between frames.
    private void DrawLinkAdvanced()
    {
        ImGui.SetNextItemWidth(120);
        ImGui.InputInt("UDP port", ref _portEdit);
        ImGui.SameLine();
        if (ImGui.Button("Apply port"))
        {
            Cfg.Port = Math.Clamp(_portEdit, 1024, 65535);
            _portEdit = Cfg.Port;
            _plugin.RestartReceiver();
            SaveLocked();
        }

        var timeout = Cfg.TimeoutSeconds;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Release after (s)", ref timeout, 0.2f, 5f, "%.1f"))
        {
            Cfg.TimeoutSeconds = timeout;
            SaveLocked();
        }
        ImGui.TextColored(Dim, "When a channel stops sending, its actor returns to its own animation.");

        ImGui.Spacing();
        var interp = Cfg.Interpolate;
        if (ImGui.Checkbox("Fill in frames between Koikatsu updates (smoother)", ref interp))
        {
            Cfg.Interpolate = interp;
            SaveLocked();
        }
        ImGui.TextColored(Dim, "Blends between Koikatsu poses so motion is smooth when FFXIV runs at a higher frame rate. " +
                               "Adds about one Koikatsu frame of delay. With this on, Body -> Smoothing can be lowered.");
        for (int ch = 0; ch < Protocol.Wire.MaxChannels; ch++)
        {
            var f = _plugin.Receiver.GetLatest(ch);
            if (f == null || f.AgeSeconds > Cfg.TimeoutSeconds)
                continue;
            var rate = _plugin.Receiver.GetRate(ch);
            var delayMs = Cfg.Interpolate ? _plugin.Receiver.GetInterpolationDelay(ch) * 1000 : 0;
            ImGui.TextColored(Dim, $"Channel {ch + 1}: Koikatsu sending {rate:0} fps" +
                                   (Cfg.Interpolate ? $", {delayMs:0} ms behind live" : ""));
        }

        var r = _plugin.Receiver;
        ImGui.TextColored(Dim, $"Packets: {r.PacketsReceived} received, {r.PacketsRejected} rejected");
    }

    /// <summary>First GPose actor not already used by a link, or 0 (GPose target) if none.</summary>
    private int NextUnusedActor()
    {
        if (!Plugin.ClientState.IsGPosing)
            return 0;
        var used = Cfg.Links.Select(l => l.TargetObjectIndex).ToHashSet();
        foreach (var v in _plugin.LinkViews.Values)
            used.Add(v.ObjectIndex);
        for (int idx = Safety.ClientObjectStart; idx < Math.Min(Safety.ClientObjectEnd, Plugin.ObjectTable.Length); idx++)
        {
            if (Plugin.ObjectTable[idx] is ICharacter && !used.Contains(idx))
                return idx;
        }
        return 0;
    }

    private void SetTarget(LinkConfig link, int index)
    {
        link.TargetObjectIndex = index == KoiLink.MyCharacter || Safety.IsClientOnlyIndex(index) ? index : 0;
        SaveLocked();
    }

    private static string DescribeObject(int index)
    {
        if (index >= Plugin.ObjectTable.Length)
            return $"#{index}";
        var go = Plugin.ObjectTable[index];
        return go == null ? $"#{index} (not in scene)" : $"{go.Name.TextValue}  [#{index}]";
    }

    private void DrawBodyTab()
    {
        var changed = false;
        changed |= Toggle("Torso", () => Cfg.DriveTorso, v => Cfg.DriveTorso = v);
        ImGui.SameLine();
        changed |= Toggle("Neck & head", () => Cfg.DriveHead, v => Cfg.DriveHead = v);
        ImGui.SameLine();
        changed |= Toggle("Arms", () => Cfg.DriveArms, v => Cfg.DriveArms = v);
        changed |= Toggle("Hands & fingers", () => Cfg.DriveHands, v => Cfg.DriveHands = v);
        ImGui.SameLine();
        changed |= Toggle("Legs & feet", () => Cfg.DriveLegs, v => Cfg.DriveLegs = v);
        changed |= Toggle("Genitals (YAS / IVCS skeletons)", () => Cfg.DriveGenitals, v => Cfg.DriveGenitals = v);
        changed |= Toggle("Breast physics", () => Cfg.DriveBreasts, v => Cfg.DriveBreasts = v);
        if (Cfg.DriveBreasts)
        {
            ImGui.SameLine();
            var bs = Cfg.BreastStrength;
            ImGui.SetNextItemWidth(140);
            if (ImGui.SliderFloat("Bounce strength", ref bs, 0f, 2f, "%.2f")) { Cfg.BreastStrength = bs; changed = true; }
        }

        ImGui.Spacing();
        changed |= Toggle("Follow hip height (sitting, crouching, lying)", () => Cfg.ApplyRootHeight, v => Cfg.ApplyRootHeight = v);
        changed |= Toggle("Follow hip movement across the floor", () => Cfg.ApplyRootHorizontal, v => Cfg.ApplyRootHorizontal = v);

        ImGui.Spacing();
        var smoothing = Cfg.Smoothing;
        ImGui.SetNextItemWidth(220);
        if (ImGui.SliderFloat("Smoothing", ref smoothing, 0f, 0.9f, "%.2f"))
        {
            Cfg.Smoothing = smoothing;
            changed = true;
        }

        var pitch = Cfg.HeadPitchOffset;
        ImGui.SetNextItemWidth(220);
        if (ImGui.SliderFloat("Head pitch offset", ref pitch, -30f, 30f, "%.1f°"))
        {
            Cfg.HeadPitchOffset = pitch;
            changed = true;
        }
        ImGui.TextColored(Dim, "Nudge if the head looks slightly up or down compared to Koikatsu.");

        ImGui.Spacing();
        changed |= Toggle("Mirror left/right", () => Cfg.MirrorLeftRight, v => Cfg.MirrorLeftRight = v);
        ImGui.SameLine();
        changed |= Toggle("Flip facing", () => Cfg.FlipFacing, v => Cfg.FlipFacing = v);

        if (changed)
            SaveLocked();
    }

    private static readonly string[] ModeNames = ["Off (leave the actor's own face)", "Automatic (mirror Koikatsu)", "Captured presets"];

    private void DrawFaceTab()
    {
        var changed = false;

        changed |= Toggle("Track face from Koikatsu", () => Cfg.DriveFace, v => Cfg.DriveFace = v);
        ImGui.TextColored(Dim, "Turn off to use in-game facial expressions or pose the face with Brio / Ktisis. " +
                               "The head still follows Koikatsu.");
        if (!Cfg.DriveFace)
        {
            if (changed)
                SaveLocked();
            return;
        }
        ImGui.Separator();

        // Expression mode
        var mode = (int)Cfg.ExpressionMode;
        ImGui.SetNextItemWidth(260);
        if (ImGui.Combo("Facial expressions", ref mode, ModeNames, ModeNames.Length))
        {
            Cfg.ExpressionMode = (ExpressionMode)mode;
            changed = true;
        }

        if (Cfg.ExpressionMode == ExpressionMode.Automatic)
        {
            var strength = Cfg.ExpressionStrength;
            ImGui.SetNextItemWidth(200);
            if (ImGui.SliderFloat("Expression strength", ref strength, 0f, 2f, "%.2f")) { Cfg.ExpressionStrength = strength; changed = true; }
            changed |= Toggle("Eyelids / blinking", () => Cfg.ExpressionEyelids, v => Cfg.ExpressionEyelids = v);
            ImGui.SameLine();
            changed |= Toggle("Brows", () => Cfg.ExpressionBrows, v => Cfg.ExpressionBrows = v);
            ImGui.SameLine();
            changed |= Toggle("Mouth / lips", () => Cfg.ExpressionMouth, v => Cfg.ExpressionMouth = v);
            ImGui.SameLine();
            changed |= Toggle("Cheeks", () => Cfg.ExpressionCheeks, v => Cfg.ExpressionCheeks = v);

            if (Cfg.ExpressionEyelids)
            {
                var lid = Cfg.EyelidStrength;
                ImGui.SetNextItemWidth(200);
                if (ImGui.SliderFloat("Eyelid strength", ref lid, 0f, 2f, "%.2f")) { Cfg.EyelidStrength = lid; changed = true; }
                var gap = Cfg.EyelidClearance;
                ImGui.SetNextItemWidth(200);
                if (ImGui.SliderFloat("Eyelid clearance", ref gap, 0f, 0.3f, "%.2f")) { Cfg.EyelidClearance = gap; changed = true; }
                ImGui.SameLine();
                ImGui.TextColored(Dim, "(raise if lids still touch the eyes)");
            }
        }

        ImGui.Spacing();
        changed |= Toggle("Eyes follow Koikatsu gaze", () => Cfg.DriveGaze, v => Cfg.DriveGaze = v);
        var gaze = Cfg.GazeMaxAngle;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Max eye angle", ref gaze, 5f, 45f, "%.0f°")) { Cfg.GazeMaxAngle = gaze; changed = true; }

        if (Cfg.ExpressionMode != ExpressionMode.Off)
        {
            changed |= Toggle("Open the jaw with the mouth", () => Cfg.DriveJaw, v => Cfg.DriveJaw = v);
            var jaw = Cfg.JawMaxAngle;
            ImGui.SetNextItemWidth(200);
            if (ImGui.SliderFloat("Max jaw angle", ref jaw, 0f, 35f, "%.0f°")) { Cfg.JawMaxAngle = jaw; changed = true; }
        }

        if (Cfg.ExpressionMode == ExpressionMode.CapturedPresets)
        {
            changed |= Toggle("Invert Koikatsu eye openness", () => Cfg.InvertEyeOpen, v => Cfg.InvertEyeOpen = v);
            var speed = Cfg.ExpressionBlendSpeed;
            ImGui.SetNextItemWidth(200);
            if (ImGui.SliderFloat("Expression blend speed", ref speed, 1f, 30f, "%.0f")) { Cfg.ExpressionBlendSpeed = speed; changed = true; }
        }

        if (changed)
            SaveLocked();

        // Per-link tools: which link (and therefore which actor's face) they work on.
        ImGui.Separator();
        var link = Cfg.Links.FirstOrDefault(l => l.Id == _faceLink) ?? Cfg.Links.FirstOrDefault();
        if (link == null)
            return;
        _faceLink = link.Id;
        ImGui.SetNextItemWidth(260);
        if (ImGui.BeginCombo("Face of", DescribeLink(link)))
        {
            foreach (var l in Cfg.Links)
                if (ImGui.Selectable(DescribeLink(l), l.Id == _faceLink))
                    _faceLink = l.Id;
            ImGui.EndCombo();
        }

        DrawLiveFace(link);

        if (Cfg.ExpressionMode == ExpressionMode.Automatic)
            DrawNeutral(link);
        else if (Cfg.ExpressionMode == ExpressionMode.CapturedPresets)
            DrawPresets(link);
    }

    private void DrawNeutral(LinkConfig link)
    {
        var frame = _plugin.Receiver.GetLatest(link.Channel);
        var hasFace = frame?.RegionDisp != null;
        ImGui.Spacing();
        if (!hasFace)
        {
            ImGui.TextColored(Warn, "No face data on this channel. Make sure \"Send facial expressions\" is on in the " +
                                    "Koikatsu plugin's settings (F1) and the character has a normal face.");
            return;
        }

        ImGui.TextWrapped("If the FFXIV face looks slightly off when the Koikatsu character has a relaxed face, set " +
                          "Koikatsu to its default expression and press this:");
        if (ImGui.Button("Use current Koikatsu face as neutral"))
        {
            var disp = frame!.RegionDisp!;
            var arr = new float[disp.Length * 3];
            for (int k = 0; k < disp.Length; k++)
            {
                arr[k * 3] = disp[k].X;
                arr[k * 3 + 1] = disp[k].Y;
                arr[k * 3 + 2] = disp[k].Z;
            }
            link.FaceNeutral = arr; // replaced, never mutated
            SaveLocked();
        }
        if (link.FaceNeutral != null)
        {
            ImGui.SameLine();
            if (ImGui.Button("Clear neutral"))
            {
                link.FaceNeutral = null;
                SaveLocked();
            }
        }
    }

    private string DescribeLink(LinkConfig link)
    {
        var i = Cfg.Links.IndexOf(link) + 1;
        _plugin.LinkViews.TryGetValue(link.Id, out var view);
        var actor = view != null && !string.IsNullOrEmpty(view.ActorName) ? view.ActorName : "no actor";
        return $"Link {i}: channel {link.Channel + 1} -> {actor}";
    }

    private void DrawLiveFace(LinkConfig link)
    {
        var f = _plugin.Receiver.GetLatest(link.Channel);
        ImGui.Spacing();
        if (f == null)
        {
            ImGui.TextColored(Dim, "No Koikatsu face data yet.");
            return;
        }
        ImGui.TextColored(Dim,
            $"Koikatsu now: eyes #{f.EyesPattern}  mouth #{f.MouthPattern}  brows #{f.EyebrowPattern}  " +
            $"eye open {(f.HasEyeOpen ? f.EyeOpen.ToString("0.00") : "?")}  mouth {(f.HasMouthOpen ? f.MouthOpen.ToString("0.00") : "?")}");
    }

    private void DrawPresets(LinkConfig link)
    {
        _plugin.LinkViews.TryGetValue(link.Id, out var view);
        if (view == null || string.IsNullOrEmpty(view.ActorName))
        {
            ImGui.TextColored(Dim, "This link has no actor right now (enter GPose and pick one on the Koikatsu link page).");
            return;
        }
        var raceKey = view.RaceKey;
        var runtime = _plugin.Applier.GetRuntime(link.Id);
        ImGui.Spacing();
        if (!ImGui.CollapsingHeader($"Expression presets for {view.ActorName}'s face ({raceKey})", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.TextWrapped("Set the actor's face in game (an expression emote, or Brio/Ktisis face posing), pause the link, " +
                          "then capture. 'blink' is driven by eye openness, 'mouth_open' by mouth openness; any other " +
                          "preset can be assigned to a Koikatsu pattern number below.");

        if (ImGui.Button($"Capture as '{FaceDriver.BlinkPreset}'"))
            runtime.PendingCapture = FaceDriver.BlinkPreset;
        ImGui.SameLine();
        if (ImGui.Button($"Capture as '{FaceDriver.MouthOpenPreset}'"))
            runtime.PendingCapture = FaceDriver.MouthOpenPreset;

        ImGui.SetNextItemWidth(180);
        ImGui.InputText("##newpreset", ref _newPresetName, 32);
        ImGui.SameLine();
        if (ImGui.Button("Capture as new preset") && !string.IsNullOrWhiteSpace(_newPresetName))
        {
            runtime.PendingCapture = _newPresetName.Trim();
            _newPresetName = "";
        }
        if (runtime.CaptureResult is { } captured)
            ImGui.TextColored(captured.StartsWith("Captured") ? Good : Bad, captured);

        Dictionary<string, FacePreset>? presets;
        string[] names;
        int presetCount;
        lock (FaceDriver.PresetLock)
        {
            if (!Cfg.FacePresets.TryGetValue(raceKey, out presets))
                presets = null;
            names = presets?.Keys.Where(k => k != FaceDriver.BlinkPreset && k != FaceDriver.MouthOpenPreset)
                .OrderBy(k => k).ToArray() ?? [];
            presetCount = presets?.Count ?? 0;
        }

        if (presets == null || presetCount == 0)
        {
            ImGui.TextColored(Dim, "No presets captured for this face yet. Gaze and jaw still work without them.");
            return;
        }

        var weights = runtime.Driver.FaceWeights;
        string? delete = null;
        KeyValuePair<string, FacePreset>[] snapshot;
        lock (FaceDriver.PresetLock)
            snapshot = presets.OrderBy(p => p.Key).ToArray();
        foreach (var (name, preset) in snapshot)
        {
            weights.TryGetValue(name, out var w);
            ImGui.ProgressBar(w, new Vector2(80, 0), "");
            ImGui.SameLine();
            ImGui.Text($"{name}  ({preset.Bones.Count} bones)");
            ImGui.SameLine();
            if (ImGui.SmallButton($"Delete##{name}"))
                delete = name;
        }
        if (delete != null)
        {
            lock (FaceDriver.PresetLock)
                presets.Remove(delete);
            SaveLocked();
        }

        ImGui.Spacing();
        var live = _plugin.Receiver.GetLatest(link.Channel);
        DrawPatternMap("Eye patterns", Cfg.EyesPatternMap, live?.EyesPattern, names);
        DrawPatternMap("Mouth patterns", Cfg.MouthPatternMap, live?.MouthPattern, names);
        DrawPatternMap("Eyebrow patterns", Cfg.EyebrowPatternMap, live?.EyebrowPattern, names);
    }

    private void DrawPatternMap(string title, Dictionary<int, string> map, int? livePattern, string[] presetNames)
    {
        if (!ImGui.TreeNode(title))
            return;

        KeyValuePair<int, string>[] entries;
        lock (FaceDriver.PresetLock)
            entries = map.OrderBy(m => m.Key).ToArray();

        if (livePattern is int p)
        {
            string? assigned;
            lock (FaceDriver.PresetLock)
                map.TryGetValue(p, out assigned);
            ImGui.Text($"Current Koikatsu pattern #{p} ->");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(160);
            if (ImGui.BeginCombo($"##assign_{title}", string.IsNullOrEmpty(assigned) ? "(none)" : assigned))
            {
                if (ImGui.Selectable("(none)", string.IsNullOrEmpty(assigned)))
                {
                    lock (FaceDriver.PresetLock)
                        map.Remove(p);
                    SaveLocked();
                }
                foreach (var n in presetNames)
                {
                    if (ImGui.Selectable(n, n == assigned))
                    {
                        lock (FaceDriver.PresetLock)
                            map[p] = n;
                        SaveLocked();
                    }
                }
                ImGui.EndCombo();
            }
        }

        int? remove = null;
        foreach (var (pattern, preset) in entries)
        {
            ImGui.TextColored(Dim, $"#{pattern} -> {preset}");
            ImGui.SameLine();
            if (ImGui.SmallButton($"x##{title}{pattern}"))
                remove = pattern;
        }
        if (remove is int rm)
        {
            lock (FaceDriver.PresetLock)
                map.Remove(rm);
            SaveLocked();
        }

        ImGui.TreePop();
    }

    // Main Advanced tab: settings for the whole plugin.
    private void DrawMainAdvanced()
    {
        var outside = Cfg.AllowOutsideGPose && _limberTab.WorkOutsideGPose;
        if (ImGui.Checkbox("Also run outside GPose###limber_outside_gpose", ref outside))
        {
            Cfg.AllowOutsideGPose = outside;
            _limberTab.WorkOutsideGPose = outside;
            SaveLocked();
        }
        ImGui.TextColored(Dim, "Posing, the soft bodies and the Koikatsu link (\"My character\" links) also work in the open world. " +
                               "Only you see it: it changes your screen's render skeleton and is never sent to the game server. " +
                               "Paused during cutscenes.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Text("Unfinished pages");
        var video = Cfg.ShowVideoTab;
        if (ImGui.Checkbox("Show the Video page (not working yet)###limber_show_video", ref video))
        {
            Cfg.ShowVideoTab = video;
            SaveLocked();
        }
        var vlc = Cfg.ShowVlcTab;
        if (ImGui.Checkbox("Show the VLC Live page (not working yet)###limber_show_vlc", ref vlc))
        {
            Cfg.ShowVlcTab = vlc;
            SaveLocked();
        }
    }

    private void SaveLocked()
    {
        lock (FaceDriver.PresetLock)
            Cfg.Save();
    }

    private static bool Toggle(string label, Func<bool> get, Action<bool> set)
    {
        var v = get();
        if (!ImGui.Checkbox(label, ref v))
            return false;
        set(v);
        return true;
    }
}
