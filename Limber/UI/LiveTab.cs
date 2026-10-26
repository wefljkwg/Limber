using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Limber.Koi;
using Limber.Koi.Posing;
using Limber.Video;

namespace Limber.UI;

/// <summary>
/// The "VLC Live" tab: whatever VLC is playing, the character moves along with it, live. Limber follows VLC's
/// play position through VLC's own web interface (on this PC only) and tracks the same frames from the file.
/// </summary>
public sealed class LiveTab : IDisposable
{
    private static readonly Vector4 Good = new(0.45f, 0.85f, 0.45f, 1f);
    private static readonly Vector4 Warn = new(0.95f, 0.75f, 0.30f, 1f);
    private static readonly Vector4 Bad = new(0.95f, 0.40f, 0.40f, 1f);
    private static readonly Vector4 Dim = new(0.65f, 0.65f, 0.65f, 1f);

    private static readonly string[] SetupSteps =
    [
        "1. In VLC, open Tools > Preferences.",
        "2. At the bottom left, under \"Show settings\", pick All.",
        "3. Open Interface > Main interfaces, and tick \"Web\" on the right.",
        "4. Open Interface > Main interfaces > Lua, and type a password under \"Lua HTTP\". Any password works.",
        "5. Click Save, then close and reopen VLC. (Windows may ask whether VLC can use the network: \"Private networks\" is enough.)",
        "6. Put the same password below and click Start.",
    ];

    private readonly KoiLink _koi;
    private readonly LiveTracker _live;
    private string _passwordEdit;
    private bool _showSetup;

    private Configuration Cfg => _koi.Config;

    public LiveTab(KoiLink koi, string pluginDir)
    {
        _koi = koi;
        _live = new LiveTracker(pluginDir, koi.Receiver);
        _passwordEdit = koi.Config.LiveVlcPassword;
        _showSetup = string.IsNullOrEmpty(koi.Config.LiveVlcPassword);
    }

    /// <summary>Framework thread.</summary>
    public void Update() => _live.Update(
        [Math.Clamp(Cfg.LiveChannel, 0, Protocol.Wire.MaxChannels - 1), Math.Clamp(Cfg.LiveChannel2, 0, Protocol.Wire.MaxChannels - 1)],
        Cfg.LiveMirror);

    public void Draw()
    {
        ImGui.TextWrapped("Play a video in VLC and your character moves along with it, live - pause, seek and speed included. " +
                          "Limber asks VLC what it's playing and where (on this PC only) and tracks the same frames from the file, " +
                          "so VLC can sit behind the game.");

        ImGui.Spacing();
        if (ImGui.CollapsingHeader("Set up VLC (one time)", _showSetup ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None))
        {
            foreach (var step in SetupSteps)
                ImGui.TextWrapped(step);
            ImGui.TextColored(Dim, "Limber only connects to VLC on this PC (127.0.0.1). The password is VLC's own and stays on this PC.");
            ImGui.Spacing();
        }

        // Connection.
        bool changed = false;
        var port = Cfg.LiveVlcPort;
        ImGui.SetNextItemWidth(100);
        if (ImGui.InputInt("VLC port", ref port))
        {
            Cfg.LiveVlcPort = Math.Clamp(port, 1, 65535);
            changed = true;
        }
        ImGui.SameLine();
        ImGui.TextColored(Dim, "(VLC's default is 8080)");

        ImGui.SetNextItemWidth(200);
        if (ImGui.InputText("VLC password##limber_vlc_pw", ref _passwordEdit, 128, ImGuiInputTextFlags.Password))
        {
            Cfg.LiveVlcPassword = _passwordEdit;
            changed = true;
        }

        ImGui.Spacing();
        if (_live.Running)
        {
            if (ImGui.Button("Stop###limber_vlc_toggle", new Vector2(160, 0)))
                _live.Stop();
        }
        else if (ImGui.Button("Start###limber_vlc_toggle", new Vector2(160, 0)))
        {
            if (!Cfg.Enabled)
                Cfg.Enabled = true; // links have to be on for anything to move
            _live.Start(CurrentSettings);
        }
        ImGui.SameLine();
        DrawStatus();

        // Options.
        Section("Tracking");
        int people = Cfg.LivePeople;
        bool swap = Cfg.LiveSwapSides;
        if (ChannelTarget.DrawPeople(ref people, ref swap, "limber_vlc"))
        {
            Cfg.LivePeople = Math.Clamp(people, 1, 2);
            Cfg.LiveSwapSides = swap;
            changed = true;
        }
        if (TrackerChoice.Draw(Cfg, "limber_vlc"))
            changed = true;
        var fingers = Cfg.LiveFingers;
        if (ImGui.Checkbox("Track fingers", ref fingers)) { Cfg.LiveFingers = fingers; changed = true; }
        Tip("Also follow each finger. With the fast model this adds a hand model, which is slower.");
        ImGui.SameLine();
        var guess = Cfg.LiveGuessHiddenLimbs;
        if (ImGui.Checkbox("Work out hidden limbs", ref guess)) { Cfg.LiveGuessHiddenLimbs = guess; changed = true; }
        Tip("On: limbs that are hidden or cut off are worked out from the rest of the body, kept at the person's own\n" +
            "limb lengths and smoothed. Off: those limbs keep the character's own pose instead.");
        var smooth = Cfg.LiveSmoothing;
        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderFloat("Smoothing", ref smooth, 0f, 0.8f, "%.2f")) { Cfg.LiveSmoothing = smooth; changed = true; }
        Tip("Less jitter, but a little more delay behind the video.");
        ImGui.SameLine();
        var mirror = Cfg.LiveMirror;
        if (ImGui.Checkbox("Mirror", ref mirror)) { Cfg.LiveMirror = mirror; changed = true; }
        Tip("Swap left and right (for selfie-camera videos).");

        // Where it goes.
        Section("Characters");
        int ch1 = Cfg.LiveChannel;
        if (ChannelTarget.Draw(_koi, Cfg.LivePeople > 1 ? "Person 1:" : "Plays on", ref ch1, "limber_vlc_p1"))
        {
            Cfg.LiveChannel = ch1;
            changed = true;
        }
        if (Cfg.LivePeople > 1)
        {
            int ch2 = Cfg.LiveChannel2;
            if (ChannelTarget.Draw(_koi, "Person 2:", ref ch2, "limber_vlc_p2"))
            {
                Cfg.LiveChannel2 = ch2;
                changed = true;
            }
            if (Cfg.LiveChannel2 == Cfg.LiveChannel)
                ImGui.TextColored(Bad, "Give person 2 a different channel than person 1.");
        }

        ImGui.Spacing();
        ImGui.TextColored(Dim, "Works best with people fully in view, not overlapping, and a steady camera. Expect a short delay behind VLC " +
                               "(two people take about twice the work). Looping in VLC is followed too. " +
                               "Body tab settings and the Pose page's held bones apply on top.");

        if (changed)
        {
            lock (FaceDriver.PresetLock)
                Cfg.Save();
        }
    }

    private LiveTracker.Settings CurrentSettings()
        => new(Cfg.LiveVlcPort, Cfg.LiveVlcPassword, Cfg.LiveFingers, Cfg.LiveGuessHiddenLimbs, Cfg.LiveSmoothing,
            Cfg.LivePeople, Cfg.LiveSwapSides, Cfg.TrackerAccurate, Cfg.TrackerGpu);

    private void DrawStatus()
    {
        if (!_live.Running)
        {
            ImGui.TextColored(Dim, "Off");
            return;
        }
        if (_live.Error != null)
        {
            ImGui.TextColored(Bad, _live.Error);
            return;
        }
        var file = _live.FileName;
        if (file == null)
        {
            ImGui.TextColored(Warn, _live.Status);
            return;
        }
        int wanted = Math.Clamp(Cfg.LivePeople, 1, 2);
        int seen = _live.PeopleFound;
        string who = wanted == 1
            ? (seen > 0 ? "" : "  -  no person in view")
            : $"  -  {seen}/{wanted} people in view";
        ImGui.TextColored(seen >= wanted ? Good : Warn, $"{_live.Status}: {file}  -  {_live.TrackFps:0} fps tracked" + who);
        if (_live.Model != null)
            ImGui.TextColored(Dim, "Using the " + _live.Model + ".");
    }

    private static void Section(string text)
    {
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.75f, 0.75f, 0.8f, 1f), text);
        ImGui.Separator();
    }

    private static void Tip(string text)
    {
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(text);
    }

    public void Dispose() => _live.Dispose();
}
