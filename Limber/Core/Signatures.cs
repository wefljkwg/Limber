using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace Limber.Core;

/// <summary>
/// Health check of every game function Limber finds by a byte pattern (a "signature"). After a game patch some may no
/// longer be found; this checks them all once, a few seconds after Limber loads, in the background, so Settings >
/// Advanced can say in one line which features are affected ("Game functions: 10 of 11 found, missing: ...").
/// It only looks: each feature still finds its own function exactly as before (the patterns are read from the
/// features' own constants, so they never drift apart).
/// </summary>
public static class Signatures
{
    public enum Kind
    {
        /// <summary>The feature refuses to use it unless the pattern matches exactly one place.</summary>
        Unique,
        /// <summary>The feature uses the first place the pattern matches.</summary>
        First,
        /// <summary>Found by FFXIVClientStructs (Dalamud's game library) when the game starts, not by Limber.</summary>
        ClientStructs,
    }

    public sealed class Entry
    {
        public string Feature { get; init; } = "";
        /// <summary>The source file it's in (just the name).</summary>
        public string Where { get; init; } = "";
        public Kind Kind { get; init; }
        public string Pattern { get; internal set; } = "";
        internal Func<nint>? Address;
        /// <summary>The feature's own explanation of why it's off ("" when it has none, or nothing is wrong).</summary>
        public Func<string>? Problem { get; internal set; }
        /// <summary>The feature says it found and uses its function (trusted over the search: a pattern can stop matching
        /// once something has hooked the function).</summary>
        internal Func<bool>? Working;

        private volatile int _matches = -1;
        /// <summary>Places the pattern matched: -1 not checked yet, -2 couldn't check.</summary>
        public int Matches { get => _matches; internal set => _matches = value; }
        public string Error { get; internal set; } = "";

        public bool Checked => Matches != -1;
        public bool Found => Kind == Kind.Unique ? Matches == 1 : Matches >= 1;
        public bool Duplicate => Matches > 1;

        /// <summary>In plain words.</summary>
        public string State => Matches switch
        {
            -1 => "not checked yet",
            -2 => "couldn't check: " + Error,
            0 => "missing (not found in this game version)",
            1 => "found",
            _ => Kind == Kind.Unique ? $"matches {Matches} places, so the feature leaves it alone" : $"matches {Matches} places (the first is used)",
        };

        public string ProblemText()
        {
            try
            {
                return Problem?.Invoke() ?? "";
            }
            catch
            {
                return "";
            }
        }
    }

    private static readonly List<Entry> Entries =
    [
        Text("Posing (the game's bone update)", typeof(BoneUpdateHook), nameof(BoneUpdateHook.Signature), Kind.Unique, "BoneUpdateHook.cs"),
        Text("Effects: making an effect", typeof(Limber.BodyRig.Effects), "CreatePattern", Kind.Unique, "Effects.cs"),
        Text("Effects: playing an effect", typeof(Limber.BodyRig.Effects), "PlayPattern", Kind.Unique, "Effects.cs"),
        Text("Character effects: making one", typeof(Limber.BodyRig.Effects), "ActorCreatePattern", Kind.Unique, "Effects.cs"),
        Text("Character effects: knowing when one ends", typeof(Limber.BodyRig.Effects), "ActorVtablePattern", Kind.Unique, "Effects.cs"),
        Text("Camera: no collision", typeof(Limber.Rig.CameraControl), nameof(Limber.Rig.CameraControl.NoCollisionSig), Kind.First, "CameraControl.cs"),
        Text("Separate wobbles: the game's mesh draws", typeof(Limber.Gpu.LiveRigInstances), "DrawMeshSig", Kind.First, "LiveRigInstances.cs",
            () => Limber.Gpu.LiveRigInstances.Problem),
        Text("Separate wobbles: the game's draw commands", typeof(Limber.Gpu.LiveRigInstances), "PushCommandSig", Kind.First, "LiveRigInstances.cs",
            () => Limber.Gpu.LiveRigInstances.Problem),
        Text("Research: final skinning matrices (Advanced)", typeof(Limber.Gpu.JointReadout), "ExecuteSig", Kind.First, "JointReadout.cs",
            () => Limber.Gpu.JointReadout.Problem),
        ClientStructs("Moving characters (Actor page: keeping them where you put them)", "ActorTransforms.cs",
            () => (nint)FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject.Addresses.SetPosition.Value),
        ClientStructs("NPC looks on your own character outside GPose", "PlayerLook.cs",
            () => (nint)FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase.Addresses.Create.Value),
    ];

    private static Task? _check;

    public static IReadOnlyList<Entry> All => Entries;
    public static bool Done => _check is { IsCompleted: true };

    // A pattern kept in a feature's own constant (private ones too), read so the two can never differ.
    private static Entry Text(string feature, Type owner, string constant, Kind kind, string file, Func<string>? problem = null)
    {
        string pattern = "";
        string error = "";
        try
        {
            var field = owner.GetField(constant, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            pattern = field?.GetRawConstantValue() as string ?? "";
            if(pattern.Length == 0)
                error = $"Limber's pattern {constant} wasn't found in its code";
        }
        catch(Exception e)
        {
            error = e.Message;
        }
        var entry = new Entry { Feature = feature, Where = file, Kind = kind, Pattern = pattern, Problem = problem, Error = error };
        if(error.Length > 0)
            entry.Matches = -2;
        return entry;
    }

    private static Entry ClientStructs(string feature, string file, Func<nint> address)
        => new() { Feature = feature, Where = file, Kind = Kind.ClientStructs, Address = address, Pattern = "(FFXIVClientStructs)" };

    /// <summary>A feature's own "why it's off" text, shown beside its pattern (the plugin links the ones it owns).</summary>
    public static void Link(string feature, Func<string> problem, Func<bool>? working = null)
    {
        foreach(var e in Entries)
            if(e.Feature == feature || e.Feature.StartsWith(feature, StringComparison.Ordinal))
            {
                e.Problem = problem;
                e.Working = working;
            }
    }

    /// <summary>Starts the check (once): in the background, a few seconds after Limber has loaded.</summary>
    public static void StartCheck()
    {
        if(_check != null)
            return;
        _check = Task.Run(async () =>
        {
            await Task.Delay(3000).ConfigureAwait(false);
            Check();
        });
    }

    private static void Check()
    {
        foreach(var e in Entries)
        {
            if(e.Matches == -2)
                continue;
            try
            {
                if(e.Kind == Kind.ClientStructs)
                    e.Matches = e.Address!() != 0 ? 1 : 0;
                else
                {
                    int matches = Plugin.SigScanner.ScanAllText(e.Pattern).Length;
                    if(matches == 0 && e.Working != null && e.Working())
                        matches = 1;
                    e.Matches = matches;
                }
            }
            catch(Exception ex)
            {
                e.Error = ex.Message;
                e.Matches = -2;
            }
        }
        try
        {
            if(Entries.Any(e => !e.Found))
                Plugin.Log.Warning("Limber: " + Summary());
            else
                Plugin.Log.Information("Limber: " + Summary());
        }
        catch
        {
        }
    }

    /// <summary>One line: how many were found, and which features are affected.</summary>
    public static string Summary()
    {
        if(!Done)
            return $"Game functions: checking {Entries.Count}...";
        int found = Entries.Count(e => e.Found);
        var bad = Entries.Where(e => !e.Found).ToList();
        if(bad.Count == 0)
            return $"Game functions: all {Entries.Count} found.";
        return $"Game functions: {found} of {Entries.Count} found. Not working: " + string.Join("; ", bad.Select(e => $"{e.Feature} ({e.State})")) + ".";
    }

    /// <summary>Every entry, one per line (for the diagnostics text).</summary>
    public static IEnumerable<string> Lines()
    {
        foreach(var e in Entries)
        {
            string problem = e.ProblemText();
            yield return $"  {(e.Found ? "ok " : e.Checked ? "BAD" : "...")} {e.Feature} [{e.Where}]: {e.State}"
                + (problem.Length > 0 ? $" - feature says: {problem}" : "");
        }
    }
}
