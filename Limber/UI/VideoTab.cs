using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Limber.Koi;
using Limber.Koi.Posing;
using Limber.Video;

namespace Limber.UI;

/// <summary>
/// The "Video" tab: track a person's movement in a video file, save it as a clip, and play clips onto
/// characters through a Koikatsu-link channel. Everything runs locally.
/// </summary>
public sealed class VideoTab : IDisposable
{
    private static readonly Vector4 Good = new(0.45f, 0.85f, 0.45f, 1f);
    private static readonly Vector4 Bad = new(0.95f, 0.40f, 0.40f, 1f);
    private static readonly Vector4 Dim = new(0.65f, 0.65f, 0.65f, 1f);
    private static readonly Vector4 Accent = new(0.6f, 0.9f, 1f, 1f);

    private readonly KoiLink _koi;
    private readonly VideoProcessor _processor;
    private readonly GvhmrRunner _gvhmr = new();
    private readonly ClipPlayer _player;
    private readonly FileDialogManager _files = new();
    private readonly string _clipDir;

    private string _videoPath = "";
    private string? _checkedPath;
    private bool _pathExists;
    private string _clipName = "";
    private float _start, _end;
    private List<string> _clipFiles = new();
    private string? _selectedFile;
    private string? _message;
    private bool _messageIsError;

    private Configuration Cfg => _koi.Config;

    public ClipPlayer Player => _player;

    public VideoTab(KoiLink koi, string pluginDir, string configDir)
    {
        _koi = koi;
        _processor = new VideoProcessor(pluginDir);
        _player = new ClipPlayer(koi.Receiver);
        _clipDir = Path.Combine(configDir, "Clips");
        RefreshClips();
    }

    /// <summary>Framework thread: playback.</summary>
    public void Update()
    {
        _player.Channel = Math.Clamp(Cfg.VideoChannel, 0, Protocol.Wire.MaxChannels - 1);
        _player.Channel2 = Math.Clamp(Cfg.VideoChannel2, 0, Protocol.Wire.MaxChannels - 1);
        _player.Mirror = Cfg.VideoMirror;
        _player.Loop = Cfg.VideoLoop;
        _player.LoopBlend = Cfg.VideoLoopBlend;
        _player.Speed = Cfg.VideoSpeed;
        _player.Update();
    }

    /// <summary>The file picker draws its own window; call every frame.</summary>
    public void DrawDialogs() => _files.Draw();

    public void Draw()
    {
        // A finished clip: save it and get it ready to play.
        var finished = _processor.TakeResult();
        if (finished != null)
        {
            try
            {
                var path = UniquePath(finished.Name);
                finished.Save(path);
                RefreshClips();
                _selectedFile = path;
                _player.Load(finished);
                Show($"Saved \"{finished.Name}\".", false);
            }
            catch (Exception e)
            {
                Show("Couldn't save the clip: " + e.Message, true);
                _player.Load(finished);
            }
        }

        // A finished GVHMR clip: load it.
        var gvhmrClip = _gvhmr.TakeResult();
        if (gvhmrClip != null)
        {
            RefreshClips();
            LoadClip(gvhmrClip);
            Show($"GVHMR finished: \"{Path.GetFileNameWithoutExtension(gvhmrClip)}\".", false);
        }

        ImGui.TextWrapped("Turn a person's movement in a video into motion for your characters. Pick a video, track it, " +
                          "then play the clip on any character. Works best with one person, fully in view, and a steady camera.");

        Section("1. Track a video");
        DrawTrack();

        DrawGvhmr();

        Section("2. Clips");
        DrawClips();

        Section("3. Play");
        DrawPlayback();

        if (_message != null)
        {
            ImGui.Spacing();
            ImGui.TextColored(_messageIsError ? Bad : Good, _message);
        }
    }

    // ---- 1. Track

    private void DrawTrack()
    {
        bool busy = _processor.Busy;

        ImGui.SetNextItemWidth(-90);
        if (ImGui.InputTextWithHint("##limber_video_path", "Video file (.mp4, .mov, .mkv, .webm ...)", ref _videoPath, 1024))
            SuggestName();
        ImGui.SameLine();
        if (ImGui.Button("Browse...###limber_video_browse", new Vector2(-1, 0)) && !busy)
        {
            _files.OpenFileDialog("Choose a video", "Videos{.mp4,.mov,.mkv,.avi,.webm,.m4v,.wmv},.*", (ok, path) =>
            {
                if (!ok || string.IsNullOrEmpty(path))
                    return;
                _videoPath = path;
                SuggestName(force: true);
            });
        }

        ImGui.SetNextItemWidth(220);
        ImGui.InputTextWithHint("Clip name##limber_video_name", "Name for the clip", ref _clipName, 60);

        bool changed = false;
        int people = Cfg.VideoPeople;
        bool swap = Cfg.VideoSwapSides;
        if (ChannelTarget.DrawPeople(ref people, ref swap, "limber_video"))
        {
            Cfg.VideoPeople = Math.Clamp(people, 1, 2);
            Cfg.VideoSwapSides = swap;
            changed = true;
        }

        if (TrackerChoice.Draw(Cfg, "limber_video"))
            changed = true;

        var fingers = Cfg.VideoTrackFingers;
        if (ImGui.Checkbox("Track fingers", ref fingers)) { Cfg.VideoTrackFingers = fingers; changed = true; }
        Tip("Also follow each finger (the accurate model sees them anyway; the fast one adds a hand model, which is slower).\n" +
            "Needs the hands clearly visible. Off: hands still turn with the wrist; fingers keep their own pose.");
        ImGui.SameLine();
        var guess = Cfg.VideoGuessHiddenLimbs;
        if (ImGui.Checkbox("Work out hidden limbs", ref guess)) { Cfg.VideoGuessHiddenLimbs = guess; changed = true; }
        Tip("On: limbs that are hidden or cut off are worked out from the rest of the body, kept at the person's own\n" +
            "limb lengths and smoothed. Off: those limbs keep the character's own pose instead.");

        var fps = Cfg.VideoMaxFps;
        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderInt("Max frames per second", ref fps, 10, 60)) { Cfg.VideoMaxFps = fps; changed = true; }
        Tip("Fewer frames track faster; playback is blended smoothly either way.");

        var smooth = Cfg.VideoSmoothing;
        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderFloat("Smoothing", ref smooth, 0f, 0.9f, "%.2f")) { Cfg.VideoSmoothing = smooth; changed = true; }
        Tip("Removes jitter without adding delay. Too much makes fast moves look soft.");

        ImGui.SetNextItemWidth(100);
        ImGui.InputFloat("Start (s)##limber_video_start", ref _start, 0, 0, "%.1f");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(100);
        ImGui.InputFloat("End (s, 0 = to the end)##limber_video_end", ref _end, 0, 0, "%.1f");
        _start = Math.Max(0, _start);
        _end = Math.Max(0, _end);

        if (changed)
            Save();

        ImGui.Spacing();
        if (busy)
        {
            ImGui.ProgressBar(_processor.Progress, new Vector2(-90, 0), $"{_processor.Progress * 100:0}%");
            ImGui.SameLine();
            if (ImGui.Button("Cancel###limber_video_cancel", new Vector2(-1, 0)))
                _processor.Cancel();
            ImGui.TextColored(Dim, _processor.Status);
        }
        else
        {
            if (_checkedPath != _videoPath)
            {
                _checkedPath = _videoPath;
                _pathExists = File.Exists(_videoPath);
            }
            bool ready = _pathExists;
            using (Dalamud.Interface.Utility.Raii.ImRaii.Disabled(!ready))
            {
                if (ImGui.Button("Track this video###limber_video_go", new Vector2(-1, 0)) && ready)
                {
                    if (_end > 0 && _end <= _start)
                    {
                        Show("The end time has to be after the start time (or 0 for the end of the video).", true);
                        return;
                    }
                    _message = null;
                    var name = string.IsNullOrWhiteSpace(_clipName) ? Path.GetFileNameWithoutExtension(_videoPath) : _clipName.Trim();
                    _processor.Start(new VideoOptions(_videoPath, name, Cfg.VideoTrackFingers, Cfg.VideoGuessHiddenLimbs,
                        Cfg.VideoMaxFps, _start, _end, Cfg.VideoSmoothing, Cfg.VideoPeople, Cfg.VideoSwapSides,
                        Cfg.TrackerAccurate, Cfg.TrackerGpu));
                }
            }
            if (!string.IsNullOrEmpty(_videoPath) && !ready)
                ImGui.TextColored(Bad, "That file doesn't exist.");
            if (_processor.Error != null)
                ImGui.TextColored(Bad, _processor.Error);
            else if (!string.IsNullOrEmpty(_processor.Status))
                ImGui.TextColored(Dim, _processor.Status);
        }
    }

    private void SuggestName(bool force = false)
    {
        if (force || string.IsNullOrWhiteSpace(_clipName))
        {
            try { _clipName = Path.GetFileNameWithoutExtension(_videoPath.Trim('"')); }
            catch { /* half-typed path */ }
        }
        _videoPath = _videoPath.Trim('"');
    }

    // ---- GVHMR (optional)

    private void DrawGvhmr()
    {
        ImGui.Spacing();
        if (!ImGui.CollapsingHeader("Most accurate: GVHMR (optional, needs setup)"))
            return;

        ImGui.TextWrapped("GVHMR fits a full 3D body to the whole video at once. It's far better with hard clips (lying down, " +
                          "push-ups, turning, hidden limbs), but slow, and it needs a one-time setup: WSL and an NVIDIA graphics card. " +
                          "Get it from Limber.GVHMR.zip on Limber's release page (or the GVHMR folder of a local build) and follow its README. " +
                          "It tracks the body only, not fingers.");

        bool changed = false;
        var still = Cfg.GvhmrStaticCamera;
        if (ImGui.Checkbox("The camera doesn't move##limber_gvhmr_static", ref still)) { Cfg.GvhmrStaticCamera = still; changed = true; }
        Tip("Faster and steadier when the camera stays put. Leave off for handheld or moving cameras.");
        ImGui.SameLine();
        var distro = Cfg.GvhmrDistro;
        ImGui.SetNextItemWidth(140);
        if (ImGui.InputTextWithHint("WSL distro##limber_gvhmr_distro", "default", ref distro, 64)) { Cfg.GvhmrDistro = distro; changed = true; }
        Tip("Only if GVHMR is set up in a WSL distro that isn't your default one (e.g. Ubuntu-22.04).");
        if (changed)
            Save();

        if (_gvhmr.Busy)
        {
            ImGui.ProgressBar(_gvhmr.Progress, new Vector2(-90, 0), $"{_gvhmr.Progress * 100:0}%");
            ImGui.SameLine();
            if (ImGui.Button("Cancel###limber_gvhmr_cancel", new Vector2(-1, 0)))
                _gvhmr.Cancel();
            ImGui.TextColored(Dim, _gvhmr.Status);
            return;
        }

        bool ready = File.Exists(_videoPath) && !_processor.Busy;
        using (Dalamud.Interface.Utility.Raii.ImRaii.Disabled(!ready))
        {
            if (ImGui.Button("Track with GVHMR###limber_gvhmr_go", new Vector2(-1, 0)) && ready)
            {
                var name = string.IsNullOrWhiteSpace(_clipName) ? Path.GetFileNameWithoutExtension(_videoPath) : _clipName.Trim();
                _gvhmr.Start(_videoPath, UniquePath(name + " (GVHMR)"), name + " (GVHMR)", Cfg.GvhmrStaticCamera, Cfg.GvhmrDistro);
            }
        }
        if (_gvhmr.Error != null)
            ImGui.TextColored(Bad, _gvhmr.Error);
        else if (!string.IsNullOrEmpty(_gvhmr.Status))
            ImGui.TextColored(Dim, _gvhmr.Status);
    }

    // ---- 2. Clips

    private void DrawClips()
    {
        if (_clipFiles.Count == 0)
        {
            ImGui.TextColored(Dim, "No clips yet. Track a video above.");
            return;
        }

        string? delete = null;
        if (ImGui.BeginChild("##limber_clip_list", new Vector2(-1, Math.Min(6, _clipFiles.Count) * ImGui.GetFrameHeightWithSpacing() + 8), true))
        {
            foreach (var file in _clipFiles)
            {
                ImGui.PushID(file);
                var name = Path.GetFileNameWithoutExtension(file);
                if (ImGui.Selectable(name, file == _selectedFile, ImGuiSelectableFlags.AllowItemOverlap))
                    LoadClip(file);
                ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - ImGui.CalcTextSize("Delete").X - ImGui.GetStyle().FramePadding.X * 2);
                if (ImGui.SmallButton("Delete"))
                    delete = file;
                ImGui.PopID();
            }
        }
        ImGui.EndChild();

        if (delete != null)
        {
            try
            {
                if (delete == _selectedFile)
                {
                    _player.Stop();
                    _player.Load(null);
                    _selectedFile = null;
                }
                File.Delete(delete);
            }
            catch (Exception e)
            {
                Show("Couldn't delete it: " + e.Message, true);
            }
            RefreshClips();
        }

        if (ImGui.SmallButton("Open clips folder"))
        {
            try
            {
                Directory.CreateDirectory(_clipDir);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_clipDir) { UseShellExecute = true });
            }
            catch { /* nothing to do */ }
        }
    }

    private void LoadClip(string file)
    {
        try
        {
            _player.Stop();
            _player.Load(MotionClip.Load(file));
            _selectedFile = file;
            _message = null;
        }
        catch (Exception e)
        {
            Show("Couldn't open that clip: " + e.Message, true);
        }
    }

    // ---- 3. Play

    private void DrawPlayback()
    {
        var clip = _player.Clip;
        if (clip == null)
        {
            ImGui.TextColored(Dim, "Pick a clip above.");
            return;
        }

        ImGui.TextColored(Accent, clip.Name);
        ImGui.SameLine();
        var found = string.Join(", ", clip.Tracked.Select((t, i) =>
            (clip.People > 1 ? $"person {i + 1}: " : "person found in ") +
            $"{(clip.Frames.Count > 0 ? 100f * t / clip.Frames.Count : 0f):0}%"));
        ImGui.TextColored(Dim, $"{clip.Duration:0.0} s, {clip.Fps:0} fps, {found}");

        // Who plays it.
        int ch1 = Cfg.VideoChannel;
        if (ChannelTarget.Draw(_koi, clip.People > 1 ? "Person 1:" : "Plays on", ref ch1, "limber_video_p1"))
        {
            _player.Stop();
            Cfg.VideoChannel = ch1;
            Save();
        }
        if (clip.People > 1)
        {
            int ch2 = Cfg.VideoChannel2;
            if (ChannelTarget.Draw(_koi, "Person 2:", ref ch2, "limber_video_p2"))
            {
                _player.Stop();
                Cfg.VideoChannel2 = ch2;
                Save();
            }
            if (Cfg.VideoChannel2 == Cfg.VideoChannel)
                ImGui.TextColored(Bad, "Give person 2 a different channel than person 1.");
        }

        // Transport.
        float third = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X * 2) / 3f;
        if (_player.Playing)
        {
            if (ImGui.Button("Pause###limber_video_play", new Vector2(third, 0)))
                _player.Pause();
        }
        else if (ImGui.Button("Play###limber_video_play", new Vector2(third, 0)))
        {
            if (!Cfg.Enabled)
                Cfg.Enabled = true; // links have to be on for anything to move
            _player.Play();
        }
        ImGui.SameLine();
        if (ImGui.Button("Stop###limber_video_stop", new Vector2(third, 0)))
            _player.Stop();
        Tip("Stops sending; the character goes back to its own animation.");
        ImGui.SameLine();
        var loop = Cfg.VideoLoop;
        if (ImGui.Checkbox("Loop", ref loop)) { Cfg.VideoLoop = loop; Save(); }

        if (Cfg.VideoLoop)
        {
            // Loop just a part of the clip (e.g. one clean push-up), with the seam blended.
            float duration = (float)Math.Max(clip.Duration, 0.1);
            float from = _player.LoopStart, to = _player.LoopEnd > 0 ? _player.LoopEnd : duration;
            ImGui.SetNextItemWidth(150);
            if (ImGui.SliderFloat("##limber_video_loop_from", ref from, 0f, duration, "loop from %.2f s"))
                _player.LoopStart = Math.Clamp(from, 0f, Math.Max(0f, to - 0.1f));
            Tip("Only loop part of the clip (e.g. one clean repetition). Ctrl+click to type a time.");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(150);
            if (ImGui.SliderFloat("##limber_video_loop_to", ref to, 0f, duration, "to %.2f s"))
            {
                to = Math.Clamp(to, Math.Min(duration, _player.LoopStart + 0.1f), duration);
                _player.LoopEnd = to >= duration - 0.001f ? 0f : to;
            }
            Tip("Where the loop jumps back. All the way right = the end of the clip.");
            ImGui.SameLine();
            if (ImGui.SmallButton("Whole clip##limber_video_loop_all"))
            {
                _player.LoopStart = 0;
                _player.LoopEnd = 0;
            }
            var blend = Cfg.VideoLoopBlend;
            ImGui.SetNextItemWidth(160);
            if (ImGui.SliderFloat("Smooth the loop##limber_video_blend", ref blend, 0f, 1f, "%.2f s")) { Cfg.VideoLoopBlend = blend; Save(); }
            Tip("Over this long before the loop ends, the pose eases into the loop's first pose, so the jump back to\n" +
                "the start doesn't show. 0 = jump straight back.");
        }

        var t = (float)_player.Time;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.SliderFloat("##limber_video_time", ref t, 0f, (float)Math.Max(clip.Duration, 0.01), "%.2f s"))
        {
            _player.Pause();
            _player.Seek(t);
        }

        var speed = Cfg.VideoSpeed;
        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderFloat("Speed", ref speed, 0.1f, 2f, "%.2fx")) { Cfg.VideoSpeed = speed; Save(); }
        ImGui.SameLine();
        var mirror = Cfg.VideoMirror;
        if (ImGui.Checkbox("Mirror", ref mirror)) { Cfg.VideoMirror = mirror; Save(); }
        Tip("Swap left and right (for selfie-camera videos).");

        ImGui.TextColored(Dim, "Body tab settings (body parts, smoothing, \"Flip facing\", hip height) apply to clips too, " +
                               "and the Pose page's held and pinned bones go on top.");
    }

    // ---- Helpers

    private void RefreshClips()
    {
        try
        {
            _clipFiles = Directory.Exists(_clipDir)
                ? Directory.GetFiles(_clipDir, "*" + MotionClip.Extension).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
                : new List<string>();
        }
        catch
        {
            _clipFiles = new List<string>();
        }
    }

    private string UniquePath(string name)
    {
        var clean = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim();
        if (clean.Length == 0)
            clean = "Clip";
        var path = Path.Combine(_clipDir, clean + MotionClip.Extension);
        for (int i = 2; File.Exists(path); i++)
            path = Path.Combine(_clipDir, $"{clean} ({i}){MotionClip.Extension}");
        return path;
    }

    private void Show(string message, bool error)
    {
        _message = message;
        _messageIsError = error;
    }

    private void Save()
    {
        lock (FaceDriver.PresetLock)
            Cfg.Save();
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

    public void Dispose()
    {
        _player.Stop();
        _processor.Dispose();
        _gvhmr.Dispose();
    }
}
