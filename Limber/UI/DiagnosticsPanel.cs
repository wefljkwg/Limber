using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using Dalamud.Bindings.ImGui;
using Limber.Core;

namespace Limber.UI;

/// <summary>
/// Settings > Advanced: whether Limber found the game functions it needs (one line to read after a game patch), the
/// parts that were switched off for failing, and "Copy diagnostics": a plain-text report to paste when asking for help.
/// The report leaves out anything personal: no character names, no account details, no folders (file names only).
/// </summary>
public static class DiagnosticsPanel
{
    private static readonly Vector4 Good = new(0.45f, 0.85f, 0.45f, 1f);
    private static readonly Vector4 Warn = new(0.95f, 0.75f, 0.30f, 1f);
    private static readonly Vector4 Dim = new(0.65f, 0.65f, 0.65f, 1f);

    private static double _copiedAt = -10;

    public static void Draw(Configuration cfg)
    {
        ImGui.Text("Health");

        // Game functions (signatures): the one line to look at after a game patch.
        bool done = Signatures.Done;
        bool allFound = done && Signatures.All.All(e => e.Found);
        ImGui.PushTextWrapPos(0f);
        ImGui.TextColored(!done ? Dim : allFound ? Good : Warn, Signatures.Summary());
        ImGui.PopTextWrapPos();
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("Limber finds the game's own functions by searching the game for short byte patterns. After a game patch\n"
                + "some may not be found until Limber is updated: the features listed here then stay off (the rest work as usual).");
        if(ImGui.TreeNode("Each game function###limber_sig_details"))
        {
            foreach(var e in Signatures.All)
            {
                var problem = e.ProblemText();
                ImGui.TextColored(!e.Checked ? Dim : e.Found ? Good : Warn, (e.Found ? "found  " : e.Checked ? "MISSING  " : "...  ") + e.Feature);
                if(ImGui.IsItemHovered())
                    ImGui.SetTooltip($"{e.State}\nIn {e.Where}. Pattern: {e.Pattern}");
                if(problem.Length > 0 && (!e.Found || e.Kind != Signatures.Kind.Unique))
                {
                    ImGui.SameLine();
                    ImGui.TextColored(Dim, "- " + problem);
                }
            }
            ImGui.TreePop();
        }

        // Parts that keep failing: shown (and the stopped ones can be turned back on).
        var failing = StageGuard.Failing();
        if(failing.Length == 0)
            ImGui.TextColored(Dim, "No errors in Limber's per-frame work so far.");
        else
        {
            foreach(var part in failing)
            {
                ImGui.PushID(part.Area + "/" + part.Name);
                if(part.Stopped)
                {
                    ImGui.TextColored(Warn, $"Switched off: {part.Name} ({part.Area}) kept failing.");
                    ImGui.SameLine();
                    if(ImGui.SmallButton("Turn back on"))
                        part.Restart();
                    if(ImGui.IsItemHovered())
                        ImGui.SetTooltip("Runs it again. If it still fails, it's switched off again after a few seconds.");
                }
                else
                    ImGui.TextColored(Dim, $"{part.Name} ({part.Area}): {part.Errors} error(s), the last at {part.LastAt:HH:mm:ss}.");
                if(ImGui.IsItemHovered() && part.LastError.Length > 0)
                    ImGui.SetTooltip(Scrubber.Clean(part.LastError, cfg));
                ImGui.PopID();
            }
        }

        if(ImGui.Button("Copy diagnostics###limber_copy_diagnostics"))
        {
            try
            {
                ImGui.SetClipboardText(Build(cfg));
                _copiedAt = ImGui.GetTime();
            }
            catch(Exception e)
            {
                Plugin.Log.Warning(e, "Limber: couldn't make the diagnostics text");
            }
        }
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("Copies a short report to paste when asking for help: Limber's and the game's versions, which features are on,\n"
                + "the game functions above, the slowest parts, other posing plugins present, and Limber's latest log lines.\n"
                + "Nothing personal: character names, account details and folders are left out.");
        if(ImGui.GetTime() - _copiedAt < 3)
        {
            ImGui.SameLine();
            ImGui.TextColored(Good, "Copied.");
        }
    }

    /// <summary>The whole report, as text.</summary>
    public static string Build(Configuration cfg)
    {
        var ik = cfg.Ik ?? new RigSettings();
        var t = new StringBuilder();
        t.AppendLine($"Limber diagnostics ({DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC)");
        t.AppendLine($"Limber {typeof(Plugin).Assembly.GetName().Version}");
        t.AppendLine($"Dalamud {Try(() => typeof(Dalamud.Plugin.IDalamudPluginInterface).Assembly.GetName().Version?.ToString())}, "
            + $"API level {Try(() => Plugin.PluginInterface.Manifest.DalamudApiLevel.ToString())}, game {Try(GameVersion)}");
        t.AppendLine($"In GPose: {Yes(Plugin.ClientState.IsGPosing)}, logged in: {Yes(Plugin.ClientState.IsLoggedIn)}");

        t.AppendLine();
        t.AppendLine("Settings:");
        var on = new List<string>();
        void Flag(string name, bool value)
        {
            if(value)
                on.Add(name);
        }
        Flag("Limber on", cfg.PluginEnabled);
        Flag("also outside GPose", ik.WorkOutsideGPose);
        Flag("posing", ik.Enabled);
        Flag("body physics", ik.BodyPhysicsOn);
        Flag("  breasts", ik.SoftBody?.Enabled ?? false);
        Flag("  testicles", ik.SoftBodyTesticles?.Enabled ?? false);
        Flag("  butt", ik.SoftBodyButt?.Enabled ?? false);
        Flag("  belly", ik.SoftBodyBelly?.Enabled ?? false);
        Flag("live rig", ik.LiveRig);
        Flag("separate wobbles", ik.LiveRigSeparate);
        Flag("rig auto fit", ik.RigAutoFit);
        Flag("body shape", ik.BodyShapeOn);
        Flag("body shape live preview", ik.BodyShapePreview);
        Flag("hide mounts", ik.HideMounts);
        Flag("camera unlock", ik.CameraUnlock);
        Flag("camera no collision", ik.CameraNoCollide);
        Flag("followers", ik.FollowersOn);
        Flag("appearance kept through redraws", ik.AppearanceKeep);
        Flag("Koikatsu link", cfg.Enabled);
        Flag("sync", cfg.SyncEnabled);
        Flag("Oral page", cfg.ShowOralTab);
        Flag("Climax page", cfg.ShowClimaxTab);
        Flag("Breast size page", cfg.ShowBustTab);
        Flag("Video page", cfg.ShowVideoTab);
        Flag("VLC Live page", cfg.ShowVlcTab);
        t.AppendLine("  on: " + (on.Count > 0 ? string.Join(", ", on.Select(s => s.Trim())) : "(nothing)"));
        t.AppendLine($"  props: {ik.Props?.Count ?? 0}, motions: {ik.Motions?.Count ?? 0}, climax emitters: {ik.ClimaxEmitters?.Count ?? 0}, "
            + $"characters with a body shape: {ik.BodyShapes?.Count ?? 0}");

        t.AppendLine();
        t.AppendLine(Signatures.Summary());
        foreach(var line in Signatures.Lines())
            t.AppendLine(line);

        t.AppendLine();
        var failing = StageGuard.Failing();
        t.AppendLine(failing.Length == 0 ? "Errors in per-frame work: none." : "Errors in per-frame work:");
        foreach(var part in failing)
            t.AppendLine($"  {(part.Stopped ? "SWITCHED OFF " : "")}{part.Area}: {part.Name}: {part.Errors} error(s), last {part.LastAt:HH:mm:ss}: {part.LastError}");

        t.AppendLine();
        var slow = Perf.Summary();
        t.AppendLine(slow.Length == 0 ? $"Slow parts (over {Perf.SlowMs:0} ms): none." : $"Slow parts (over {Perf.SlowMs:0} ms), most frequent first:");
        foreach(var (where, count, worst) in slow.Take(10))
            t.AppendLine($"  {where}: {count} times, worst {worst:0.0} ms");

        t.AppendLine();
        t.AppendLine("Other plugins: " + Try(OtherPlugins));

        t.AppendLine();
        var lines = LimberLog.Latest(60);
        t.AppendLine(lines.Length == 0 ? "Limber's log: nothing kept." : $"Limber's latest log lines ({lines.Length}):");
        foreach(var l in lines)
            t.AppendLine($"  {l.At:HH:mm:ss} {Level(l.Level)} {l.Text}");

        return Scrubber.Clean(t.ToString(), cfg);
    }

    private static string Yes(bool b) => b ? "yes" : "no";

    private static string Level(Serilog.Events.LogEventLevel level) => level switch
    {
        Serilog.Events.LogEventLevel.Fatal => "FTL",
        Serilog.Events.LogEventLevel.Error => "ERR",
        Serilog.Events.LogEventLevel.Warning => "WRN",
        Serilog.Events.LogEventLevel.Information => "INF",
        _ => "DBG",
    };

    private static string Try(Func<string?> get)
    {
        try
        {
            return get() ?? "?";
        }
        catch
        {
            return "?";
        }
    }

    private static string? GameVersion()
        => Plugin.DataManager.GameData.Repositories.TryGetValue("ffxiv", out var repo) ? repo.Version : null;

    // The posing and mod plugins that matter when something looks wrong: whether they're there and loaded.
    private static string OtherPlugins()
    {
        string[] wanted = ["Penumbra", "Glamourer", "Brio", "Ktisis", "CustomizePlus", "Anamnesis", "SimpleHeels", "MareSynchronos"];
        var found = new List<string>();
        foreach(var p in Plugin.PluginInterface.InstalledPlugins)
        {
            if(!wanted.Any(w => string.Equals(w, p.InternalName, StringComparison.OrdinalIgnoreCase)))
                continue;
            found.Add($"{p.InternalName} {p.Version}{(p.IsLoaded ? "" : " (not loaded)")}");
        }
        return found.Count > 0 ? string.Join(", ", found) : "none of Penumbra, Glamourer, Brio, Ktisis, Customize+ found";
    }

    /// <summary>Takes personal details out of text: character names, the Windows user name, folders (file names kept).</summary>
    internal static class Scrubber
    {
        // A Windows path (C:\..., \\server\...) or a home folder path: everything but the last part is dropped.
        private static readonly Regex WindowsPath = new(@"(?:[A-Za-z]:|\\\\[^\\\s]+)\\(?:[^\\""'|<>:*?\r\n]+\\)*([^\\\s""'|<>:*?]*)", RegexOptions.Compiled);
        private static readonly Regex UnixHome = new(@"/(?:home|Users)/[^/\s]+/(?:[^/\s]+/)*([^/\s]*)", RegexOptions.Compiled);

        public static string Clean(string text, Configuration cfg)
        {
            try
            {
                text = WindowsPath.Replace(text, m => m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : "(folder)");
                text = UnixHome.Replace(text, m => m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : "(folder)");
                foreach(var secret in new[] { cfg.RelayUrl, cfg.RelayOwnerToken, cfg.RelayAccountId, cfg.LiveVlcPassword })
                    if(!string.IsNullOrEmpty(secret) && secret.Length >= 4)
                        text = text.Replace(secret, "(hidden)");
                var names = Names(cfg);
                if(names.Count > 0)
                {
                    // (longest first, so a full name goes before its parts; whole words only)
                    var pattern = string.Join("|", names.OrderByDescending(n => n.Length).Select(Regex.Escape));
                    text = Regex.Replace(text, $@"(?<![\p{{L}}\p{{N}}])(?:{pattern})(?![\p{{L}}\p{{N}}])", "(name)");
                }
            }
            catch
            {
            }
            return text;
        }

        // Every character name Limber could have written down: those around now, and the ones in the settings.
        private static HashSet<string> Names(Configuration cfg)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            void Add(string? name)
            {
                if(string.IsNullOrWhiteSpace(name))
                    return;
                name = name.Trim();
                if(name.Length >= 3)
                    names.Add(name);
                foreach(var part in name.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    if(part.Length >= 3)
                        names.Add(part);
            }
            try
            {
                foreach(var obj in Plugin.ObjectTable)
                    if(obj is Dalamud.Game.ClientState.Objects.Types.ICharacter)
                        Add(obj.Name.TextValue);
            }
            catch
            {
            }
            try
            {
                Add(Environment.UserName);
            }
            catch
            {
            }
            var ik = cfg.Ik;
            if(ik != null)
            {
                foreach(var keys in new IEnumerable<string>?[]
                {
                    ik.BodyShapes?.Keys, ik.BodyShapeBuilt?.Keys, ik.BoneScales?.Keys, ik.PhysicsCharacters, ik.BodyShapeCharacters,
                    ik.HideMountsCharacters, ik.DynamicBones?.Keys, ik.FkPoses?.Keys, ik.Throb?.Keys, ik.Twerk?.Keys, ik.Ahegao?.Keys,
                    ik.FaceMorphs?.Keys, ik.Expansion?.Keys, ik.BustShapes?.Keys, ik.BallThrob?.Keys, ik.SizeChange?.Keys,
                    ik.FacePoses?.Keys, ik.Symmetry?.Keys, ik.LookAt?.Keys,
                })
                    if(keys != null)
                        foreach(var k in keys.ToList())
                            Add(k);
            }
            return names;
        }
    }
}
