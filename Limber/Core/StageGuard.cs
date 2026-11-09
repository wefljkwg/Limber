using System;
using System.Collections.Generic;
using System.Threading;

namespace Limber.Core;

/// <summary>
/// Keeps one broken part of Limber from spamming the log or taking the rest down. Each part run every frame (a bone
/// update stage, a framework update, the draw) reports its errors here: the first is logged in full, the same error
/// again at most every 10 seconds. A part that may be stopped on its own and keeps failing (many frames in a row) is
/// switched off for the rest of the session, with one line in chat; Settings > Advanced lists it and can turn it back
/// on. Any thread.
/// </summary>
public static class StageGuard
{
    /// <summary>Errors this many times within <see cref="Window"/> stop a part that may be stopped.</summary>
    // (about five seconds of failing every frame: a moment of trouble while characters load doesn't stop anything)
    public const int StopAfter = 300;
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LogEvery = TimeSpan.FromSeconds(10);

    public sealed class Part
    {
        public string Area { get; }
        public string Name { get; }
        /// <summary>False: errors are only logged (a part everything else depends on, or one made of many steps).</summary>
        public bool CanStop { get; }

        private volatile bool _stopped;
        /// <summary>Switched off for this session after failing again and again.</summary>
        public bool Stopped => _stopped;

        public int Errors => Volatile.Read(ref _errors);
        public string LastError { get; private set; } = "";
        public DateTime LastAt { get; private set; }

        private int _errors;
        private int _inWindow;
        private DateTime _windowStart;
        private DateTime _lastLog;
        private string _lastLogged = "";
        private readonly object _sync = new();

        internal Part(string area, string name, bool canStop)
        {
            Area = area;
            Name = name;
            CanStop = canStop;
        }

        /// <summary>Notes an error (logged at most every 10 seconds; the part stopped if it keeps failing and may be).</summary>
        public void Failed(Exception e, string? during = null)
        {
            bool log, stop = false;
            string what = e.GetType().Name + ": " + e.Message;
            var now = DateTime.UtcNow;
            lock(_sync)
            {
                _errors++;
                LastError = during != null ? $"{what} (in {during})" : what;
                LastAt = DateTime.Now;
                if(now - _windowStart > Window)
                {
                    _windowStart = now;
                    _inWindow = 0;
                }
                _inWindow++;
                // (a different error is worth a line straight away; the same one again only every 10 seconds)
                log = what != _lastLogged || now - _lastLog > LogEvery;
                if(log)
                {
                    _lastLog = now;
                    _lastLogged = what;
                }
                if(CanStop && !_stopped && _inWindow >= StopAfter)
                {
                    _stopped = true;
                    stop = true;
                }
            }
            string label = during != null ? $"{Area}: {Name} (in {during})" : $"{Area}: {Name}";
            try
            {
                if(log)
                    Plugin.Log.Error(e, $"Limber: error in {label}" + (_errors > 1 ? $" ({_errors} errors so far)" : ""));
                if(stop)
                {
                    string message = $"Limber: \"{Name}\" kept failing and was switched off until Limber restarts (Settings > Advanced can turn it back on). See /xllog.";
                    Plugin.Log.Warning(message);
                    // (one line in chat at most every 30 seconds: when many parts fail together, Advanced lists them all)
                    if(TakeChatTurn())
                        Plugin.Framework.RunOnFrameworkThread(() => Plugin.ChatGui.PrintError(message));
                }
            }
            catch
            {
                // (logging must never be the thing that fails)
            }
        }

        /// <summary>Back on (Settings > Advanced); the error count starts again.</summary>
        public void Restart()
        {
            lock(_sync)
            {
                _inWindow = 0;
                _windowStart = default;
                _stopped = false;
            }
        }
    }

    private static readonly List<Part> Parts = new();
    private static long _lastChat = long.MinValue / 2;

    private static bool TakeChatTurn()
    {
        long now = Environment.TickCount64;
        long last = Interlocked.Read(ref _lastChat);
        return now - last >= 30_000 && Interlocked.CompareExchange(ref _lastChat, now, last) == last;
    }

    /// <summary>The part with this name (made the first time it's asked for).</summary>
    public static Part For(string area, string name, bool canStop = true)
    {
        lock(Parts)
        {
            foreach(var p in Parts)
                if(p.Area == area && p.Name == name)
                    return p;
            var part = new Part(area, name, canStop);
            Parts.Add(part);
            return part;
        }
    }

    /// <summary>Every part that has failed at least once.</summary>
    public static Part[] Failing()
    {
        lock(Parts)
            return Parts.FindAll(p => p.Errors > 0).ToArray();
    }

    /// <summary>Runs <paramref name="run"/> unless the part is switched off; an error is noted, never thrown.</summary>
    public static void Run(Part part, Action run)
    {
        if(part.Stopped)
            return;
        try
        {
            run();
        }
        catch(Exception e)
        {
            part.Failed(e);
        }
    }

    /// <summary>A framework update handler that can't throw (and stops if it keeps failing, when it may).</summary>
    public static Dalamud.Plugin.Services.IFramework.OnUpdateDelegate Wrap(string name, Dalamud.Plugin.Services.IFramework.OnUpdateDelegate update, bool canStop = true)
    {
        var part = For("framework", name, canStop);
        return framework =>
        {
            if(part.Stopped)
                return;
            try
            {
                update(framework);
            }
            catch(Exception e)
            {
                part.Failed(e);
            }
        };
    }
}
