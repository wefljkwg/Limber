using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Limber.Core;

/// <summary>
/// Hitch finder: how long each part of Limber took, kept only when it was slow (a spike), so a stutter that comes and
/// goes can be traced to what caused it (Advanced page, and one line in the log at most once a second). Any thread.
/// </summary>
public static class Perf
{
    /// <summary>Slower than this (milliseconds) is kept as a spike.</summary>
    public const double SlowMs = 4.0;

    public readonly record struct Spike(string Where, double Ms, DateTime At);

    private static readonly List<Spike> Spikes = new();
    private static readonly Dictionary<string, (int Count, double Worst)> Totals = new();
    private static DateTime _lastLog;

    public static void Note(string area, string what, long fromTicks, long toTicks)
    {
        double ms = (toTicks - fromTicks) * 1000.0 / Stopwatch.Frequency;
        if (ms < SlowMs)
            return;
        string where = area + ": " + what;
        var now = DateTime.Now;
        bool log = false;
        lock (Spikes)
        {
            Spikes.Add(new Spike(where, ms, now));
            if (Spikes.Count > 60)
                Spikes.RemoveAt(0);
            var t = Totals.GetValueOrDefault(where);
            Totals[where] = (t.Count + 1, Math.Max(t.Worst, ms));
            if ((now - _lastLog).TotalSeconds >= 1)
            {
                _lastLog = now;
                log = true;
            }
        }
        if (log)
            Plugin.Log.Information($"Limber: slow frame part: {where} took {ms:0.0} ms");
    }

    /// <summary>The latest spikes, newest first.</summary>
    public static Spike[] Recent()
    {
        lock (Spikes)
        {
            var a = Spikes.ToArray();
            Array.Reverse(a);
            return a;
        }
    }

    /// <summary>Everything that has spiked since the last clear: how often, and the worst.</summary>
    public static (string Where, int Count, double Worst)[] Summary()
    {
        lock (Spikes)
        {
            var list = new List<(string, int, double)>();
            foreach (var (k, v) in Totals)
                list.Add((k, v.Count, v.Worst));
            list.Sort((a, b) => b.Item2.CompareTo(a.Item2));
            return list.ToArray();
        }
    }

    public static void Clear()
    {
        lock (Spikes)
        {
            Spikes.Clear();
            Totals.Clear();
        }
    }

    /// <summary>Times one stretch of work after another: Mark("what") notes the time since the last mark.</summary>
    public struct Clock
    {
        private readonly string _area;
        private long _last;

        public Clock(string area)
        {
            _area = area;
            _last = Stopwatch.GetTimestamp();
        }

        public void Mark(string what)
        {
            long now = Stopwatch.GetTimestamp();
            Note(_area, what, _last, now);
            _last = now;
        }
    }
}
