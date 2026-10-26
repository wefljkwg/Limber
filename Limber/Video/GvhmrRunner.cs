using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Limber.Video;

/// <summary>
/// Runs GVHMR (set up separately inside WSL, see the GVHMR folder of Limber's repository) on a video and gets a
/// Limber clip back. GVHMR's runner prints "PROGRESS fraction text" and "ERROR text" lines.
/// Everything runs on this PC.
/// </summary>
public sealed class GvhmrRunner : IDisposable
{
    private volatile Process? _process;
    private Task? _task;
    private volatile bool _cancelled;

    public volatile float Progress;
    public volatile string Status = "";
    public volatile string? Error;
    private string? _finishedPath;

    public bool Busy => _task is { IsCompleted: false };

    /// <summary>The finished clip's file, once (then cleared).</summary>
    public string? TakeResult() => Interlocked.Exchange(ref _finishedPath, null);

    public void Start(string video, string outClip, string name, bool staticCamera, string distro)
    {
        if (Busy)
            return;
        Error = null;
        Progress = 0f;
        Status = "Starting GVHMR in WSL...";
        _cancelled = false;
        _task = Task.Run(() => Run(video, outClip, name, staticCamera, distro));
    }

    public void Cancel()
    {
        _cancelled = true;
        try { _process?.Kill(entireProcessTree: true); } catch { /* already gone */ }
    }

    private void Run(string video, string outClip, string name, bool staticCamera, string distro)
    {
        try
        {
            var wsl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");
            if (!File.Exists(wsl))
                throw new InvalidOperationException("WSL isn't installed. See the README in Limber's GVHMR folder to set GVHMR up.");

            var psi = new ProcessStartInfo(wsl)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };
            if (!string.IsNullOrWhiteSpace(distro))
            {
                psi.ArgumentList.Add("-d");
                psi.ArgumentList.Add(distro.Trim());
            }
            psi.ArgumentList.Add("--exec");
            psi.ArgumentList.Add("bash");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("exec \"$HOME/limber-gvhmr/run.sh\" \"$@\"");
            psi.ArgumentList.Add("limber");
            psi.ArgumentList.Add(video);
            psi.ArgumentList.Add(outClip);
            psi.ArgumentList.Add("--name");
            psi.ArgumentList.Add(name);
            if (staticCamera)
                psi.ArgumentList.Add("--static");

            Directory.CreateDirectory(Path.GetDirectoryName(outClip)!);
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Couldn't start WSL.");
            _process = process;
            if (_cancelled) // Cancel came in before the process existed
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            string? lastLine = null;
            process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) lastLine = e.Data; };
            process.BeginErrorReadLine();

            string? line;
            while ((line = process.StandardOutput.ReadLine()) != null)
            {
                line = line.Trim('\0', ' ', '\r');
                if (line.StartsWith("PROGRESS ", StringComparison.Ordinal))
                {
                    var parts = line.Split(' ', 3);
                    if (parts.Length >= 2 && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
                        Progress = Math.Clamp(f, 0f, 1f);
                    if (parts.Length == 3)
                        Status = parts[2];
                }
                else if (line.StartsWith("ERROR ", StringComparison.Ordinal))
                {
                    Error = line[6..];
                }
                else if (line.Length > 0)
                {
                    lastLine = line;
                }
            }
            process.WaitForExit();

            if (_cancelled)
            {
                Status = "Cancelled.";
                return;
            }
            if (process.ExitCode != 0 || !File.Exists(outClip))
            {
                Error ??= "GVHMR stopped (" + (lastLine ?? $"exit code {process.ExitCode}") + ").";
                Status = "Failed.";
                return;
            }
            Progress = 1f;
            Status = "Done.";
            Volatile.Write(ref _finishedPath, outClip);
        }
        catch (Exception e)
        {
            Error = e.Message;
            Status = "Failed.";
            Plugin.Log.Warning(e, "Limber: GVHMR run failed");
        }
        finally
        {
            _process = null;
        }
    }

    public void Dispose()
    {
        if (Busy)
            Cancel();
    }
}
