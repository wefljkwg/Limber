using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Serilog.Events;

namespace Limber.Core;

/// <summary>
/// Limber's log: everything goes to Dalamud's log exactly as before, and the latest lines are also kept in memory
/// (only Limber's own, only while the game runs) for Settings > Advanced > Copy diagnostics.
/// </summary>
internal sealed class LimberLog : IPluginLog
{
    public const int Keep = 200;

    public readonly record struct Line(DateTime At, LogEventLevel Level, string Text);

    private static readonly Queue<Line> Lines = new();
    private readonly IPluginLog _inner;

    public LimberLog(IPluginLog inner) => _inner = inner;

    /// <summary>The latest lines kept (oldest first), at most <paramref name="count"/>.</summary>
    public static Line[] Latest(int count)
    {
        lock(Lines)
        {
            var all = Lines.ToArray();
            return all.Length <= count ? all : all[^count..];
        }
    }

    private static void Remember(LogEventLevel level, Exception? exception, string template, object[] values)
    {
        try
        {
            string text = template;
            if(values is { Length: > 0 })
                text += " [" + string.Join(", ", values) + "]";
            if(exception != null)
                text += $" ({exception.GetType().Name}: {exception.Message})";
            lock(Lines)
            {
                Lines.Enqueue(new Line(DateTime.Now, level, text));
                while(Lines.Count > Keep)
                    Lines.Dequeue();
            }
        }
        catch
        {
            // (keeping a copy must never be what fails)
        }
    }

    public Serilog.ILogger Logger => _inner.Logger;

    public LogEventLevel MinimumLogLevel
    {
        get => _inner.MinimumLogLevel;
        set => _inner.MinimumLogLevel = value;
    }

    public void Fatal(string messageTemplate, params object[] values) { Remember(LogEventLevel.Fatal, null, messageTemplate, values); _inner.Fatal(messageTemplate, values); }
    public void Fatal(Exception? exception, string messageTemplate, params object[] values) { Remember(LogEventLevel.Fatal, exception, messageTemplate, values); _inner.Fatal(exception, messageTemplate, values); }
    public void Error(string messageTemplate, params object[] values) { Remember(LogEventLevel.Error, null, messageTemplate, values); _inner.Error(messageTemplate, values); }
    public void Error(Exception? exception, string messageTemplate, params object[] values) { Remember(LogEventLevel.Error, exception, messageTemplate, values); _inner.Error(exception, messageTemplate, values); }
    public void Warning(string messageTemplate, params object[] values) { Remember(LogEventLevel.Warning, null, messageTemplate, values); _inner.Warning(messageTemplate, values); }
    public void Warning(Exception? exception, string messageTemplate, params object[] values) { Remember(LogEventLevel.Warning, exception, messageTemplate, values); _inner.Warning(exception, messageTemplate, values); }
    public void Information(string messageTemplate, params object[] values) { Remember(LogEventLevel.Information, null, messageTemplate, values); _inner.Information(messageTemplate, values); }
    public void Information(Exception? exception, string messageTemplate, params object[] values) { Remember(LogEventLevel.Information, exception, messageTemplate, values); _inner.Information(exception, messageTemplate, values); }
    public void Info(string messageTemplate, params object[] values) { Remember(LogEventLevel.Information, null, messageTemplate, values); _inner.Info(messageTemplate, values); }
    public void Info(Exception? exception, string messageTemplate, params object[] values) { Remember(LogEventLevel.Information, exception, messageTemplate, values); _inner.Info(exception, messageTemplate, values); }
    public void Debug(string messageTemplate, params object[] values) { Remember(LogEventLevel.Debug, null, messageTemplate, values); _inner.Debug(messageTemplate, values); }
    public void Debug(Exception? exception, string messageTemplate, params object[] values) { Remember(LogEventLevel.Debug, exception, messageTemplate, values); _inner.Debug(exception, messageTemplate, values); }
    // (very chatty: passed on, not kept)
    public void Verbose(string messageTemplate, params object[] values) => _inner.Verbose(messageTemplate, values);
    public void Verbose(Exception? exception, string messageTemplate, params object[] values) => _inner.Verbose(exception, messageTemplate, values);
    public void Write(LogEventLevel level, Exception? exception, string messageTemplate, params object[] values)
    {
        if(level >= LogEventLevel.Debug)
            Remember(level, exception, messageTemplate, values);
        _inner.Write(level, exception, messageTemplate, values);
    }
}
