using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Threading;

namespace Limber.Video;

/// <summary>
/// NLF (Neural Localizer Fields), run in WSL on the NVIDIA card: a full 3D body per person per frame, with real
/// depth, fast enough for VLC Live. The helper (NLF folder, limber_nlf.py) reads the video itself; Limber asks it
/// for frame numbers and gets SMPL-X joints back (metres, camera space). Everything stays on this PC.
/// </summary>
public sealed class NlfClient : IDisposable
{
    /// <summary>One person in a frame: their 55 SMPL-X joints (camera space: x right, y down, z away; metres).</summary>
    public sealed record Person(Vector3[] Joints);

    private Process? _process;
    private StreamWriter? _input;
    private StreamReader? _output;
    private string? _openPath;
    private string _lastError = "";
    private System.Threading.Tasks.Task<string?>? _pending;
    private volatile string? _lastStderr; // the helper's last error output (Python tracebacks end with the reason)

    public string? Model { get; private set; }
    public string LastError => _lastError;

    /// <summary>Why the last frame couldn't be tracked (NLF carries on with the next one), or null.</summary>
    public string? FrameError { get; private set; }

    /// <summary>Whether NLF has been set up in WSL (its runner is there).</summary>
    public static bool IsSetUp(string distro)
    {
        try
        {
            var psi = Wsl(distro, "test -f \"$HOME/limber-nlf/run.sh\" && test -f \"$HOME/limber-nlf/models/nlf_l_multi_0.3.2.torchscript\"");
            using var p = Process.Start(psi);
            if (p == null)
                return false;
            if (!p.WaitForExit(15000))
            {
                try { p.Kill(true); } catch { }
                return false;
            }
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static ProcessStartInfo Wsl(string distro, string script)
    {
        var wsl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");
        if (!File.Exists(wsl))
            throw new InvalidOperationException("WSL isn't installed (needed for NLF).");
        var psi = new ProcessStartInfo(wsl)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
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
        psi.ArgumentList.Add(script);
        return psi;
    }

    /// <summary>Starts NLF in WSL and waits until its model is loaded (can take ~30 s the first time).</summary>
    public bool Start(string distro, CancellationToken token)
    {
        Stop();
        try
        {
            var process = Process.Start(Wsl(distro, "exec \"$HOME/limber-nlf/run.sh\""));
            if (process == null)
            {
                _lastError = "Couldn't start WSL.";
                return false;
            }
            _process = process;
            process.ErrorDataReceived += (_, e) =>
            {
                var text = e.Data?.Trim('\0', ' ', '\r');
                if (!string.IsNullOrWhiteSpace(text))
                    _lastStderr = text;
            };
            process.BeginErrorReadLine();
            _input = process.StandardInput;
            _input.AutoFlush = true;
            _output = process.StandardOutput;
            var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 180;
            while (!token.IsCancellationRequested && Stopwatch.GetTimestamp() < deadline)
            {
                var line = ReadLine(token, 180000);
                if (line == null)
                    break;
                if (line.StartsWith("READY", StringComparison.Ordinal))
                {
                    var parts = line.Split(' ');
                    Model = parts.Length >= 3 ? $"NLF ({parts[1].Replace(".torchscript", "")}) on {parts[2].Replace('_', ' ')}" : "NLF";
                    return true;
                }
                if (line.StartsWith("ERROR ", StringComparison.Ordinal))
                {
                    _lastError = line[6..];
                    Stop();
                    return false;
                }
                // (anything else: WSL's own messages, like a missing distro, come out here)
                var other = line.Replace("\0", "").Trim();
                if (other.Length > 0)
                    _lastStderr = other;
            }
            _lastError = _lastStderr is { } why
                ? "NLF stopped while starting: " + why
                : "NLF didn't start (it took too long, or WSL stopped).";
            Stop();
            return false;
        }
        catch (Exception e)
        {
            _lastError = e.Message;
            Stop();
            return false;
        }
    }

    public bool Running => _process is { HasExited: false };

    /// <summary>Opens the video (a Windows path) in NLF.</summary>
    public bool Open(string windowsPath, CancellationToken token)
    {
        if (!Running || _input == null)
            return false;
        if (_openPath == windowsPath)
            return true;
        _input.WriteLine("OPEN " + ToWsl(windowsPath));
        while (!token.IsCancellationRequested)
        {
            var line = ReadLine(token, 30000);
            if (line == null)
            {
                _lastError = "NLF stopped answering.";
                return false;
            }
            if (line.StartsWith("OPENED", StringComparison.Ordinal))
            {
                _openPath = windowsPath;
                return true;
            }
            if (line.StartsWith("ERROR ", StringComparison.Ordinal))
            {
                _lastError = line[6..];
                return false;
            }
        }
        return false;
    }

    /// <summary>The people NLF finds in one frame of the open video (null if it stopped answering).</summary>
    public List<Person>? Track(int frame, CancellationToken token)
    {
        if (!Running || _input == null || _openPath == null)
            return null;
        _input.WriteLine("FRAME " + frame.ToString(CultureInfo.InvariantCulture));
        FrameError = null;
        while (!token.IsCancellationRequested)
        {
            var line = ReadLine(token, 30000);
            if (line == null)
            {
                bool gone = !Running;
                _lastError = (gone ? "NLF stopped" : "NLF stopped answering (no reply in 30 s)")
                    + (_lastStderr is { } why ? ": " + why : ".");
                return null;
            }
            if (line.StartsWith("ERROR ", StringComparison.Ordinal))
            {
                FrameError = line[6..];
                continue;
            }
            if (!line.StartsWith("POSE ", StringComparison.Ordinal))
                continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || !int.TryParse(parts[1], out int index) || index != frame || !int.TryParse(parts[2], out int count))
                continue;
            var people = new List<Person>(count);
            int at = 3;
            const int per = 1 + 55 * 3;
            for (int p = 0; p < count && at + per <= parts.Length; p++)
            {
                at++; // (box centre: not needed, people are told apart by where they stand)
                var joints = new Vector3[55];
                bool ok = true;
                for (int j = 0; j < 55; j++)
                {
                    ok &= float.TryParse(parts[at], NumberStyles.Float, CultureInfo.InvariantCulture, out float x);
                    ok &= float.TryParse(parts[at + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y);
                    ok &= float.TryParse(parts[at + 2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z);
                    joints[j] = new Vector3(x, y, z);
                    at += 3;
                }
                if (ok)
                    people.Add(new Person(joints));
            }
            return people;
        }
        return null;
    }

    // A line from NLF, or null when it's gone (or nothing came in time).
    private string? ReadLine(CancellationToken token, int timeoutMs)
    {
        var output = _output;
        if (output == null)
            return null;
        // (a read that timed out is still waiting for its line: the next call picks it up)
        var task = _pending ??= output.ReadLineAsync();
        try
        {
            if (!task.Wait(timeoutMs, token))
                return null;
        }
        catch
        {
            _pending = null;
            return null;
        }
        _pending = null;
        return task.Result?.Trim('\0', ' ', '\r');
    }

    private static string ToWsl(string path)
    {
        // C:\Videos\a.mp4 -> /mnt/c/Videos/a.mp4 (WSL's standard mount of Windows drives).
        if (path.Length >= 3 && path[1] == ':' && (path[2] == '\\' || path[2] == '/'))
            return "/mnt/" + char.ToLowerInvariant(path[0]) + "/" + path[3..].Replace('\\', '/');
        return path.Replace('\\', '/');
    }

    public void Stop()
    {
        var process = _process;
        _process = null;
        _openPath = null;
        try { _input?.WriteLine("QUIT"); } catch { }
        _input = null;
        _output = null;
        _pending = null;
        if (process == null)
            return;
        try
        {
            if (!process.WaitForExit(1500))
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // already gone
        }
        process.Dispose();
    }

    public void Dispose() => Stop();

    // ---- Setting it up: the NLF folder's files come inside Limber; they're written out and setup_nlf.bat is run.

    /// <summary>Writes the NLF setup files to <paramref name="folder"/> and opens the setup in a console window.</summary>
    public static string? RunSetup(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var assembly = Assembly.GetExecutingAssembly();
            foreach (var name in new[] { "limber_nlf.py", "run.sh", "setup_nlf.sh", "setup_nlf.bat", "README.md" })
            {
                using var stream = assembly.GetManifestResourceStream("Limber.NLF." + name);
                if (stream == null)
                    return "Limber's NLF setup files are missing from this build.";
                using var file = File.Create(Path.Combine(folder, name));
                stream.CopyTo(file);
            }
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"\"{Path.Combine(folder, "setup_nlf.bat")}\"\"")
            {
                UseShellExecute = true,
                WorkingDirectory = folder,
            });
            return null;
        }
        catch (Exception e)
        {
            return e.Message;
        }
    }
}

/// <summary>NLF's SMPL-X joints as one of Limber's motion frames (the same space the built-in tracker uses).</summary>
public static class NlfPose
{
    // SMPL-X joint numbers.
    private const int Pelvis = 0, HipL = 1, HipR = 2, KneeL = 4, KneeR = 5, AnkleL = 7, AnkleR = 8, FootL = 10, FootR = 11,
        Neck = 12, Head = 15, ShoulderL = 16, ShoulderR = 17, ElbowL = 18, ElbowR = 19, WristL = 20, WristR = 21,
        EyeL = 23, EyeR = 24, HandL = 25, HandR = 40;

    /// <summary>
    /// Fills <paramref name="output"/> from one person's joints. <paramref name="floor"/> is the smoothed floor height
    /// for this person (kept by the caller between frames; NaN to start).
    /// </summary>
    public static void ToFrame(Vector3[] cam, bool fingers, MotionFrame output, ref float floor)
    {
        // Camera space (x right, y down, z away) -> Limber's (+X = their right, +Y = up, +Z = forward when facing the camera).
        var j = new Vector3[cam.Length];
        for (int i = 0; i < cam.Length; i++)
            j[i] = new Vector3(-cam[i].X, -cam[i].Y, -cam[i].Z);
        // Centred on the hips side to side and in depth, like the built-in tracker (its points are hip-centred).
        var centre = j[Pelvis];

        var p = output.P;
        var v = output.V;
        Array.Clear(v);
        void Set(Limber.Protocol.Joint joint, Vector3 at)
        {
            p[(int)joint] = at;
            v[(int)joint] = true;
        }
        var hip = (j[HipL] + j[HipR]) * 0.5f;
        var sh = (j[ShoulderL] + j[ShoulderR]) * 0.5f;
        Set(Limber.Protocol.Joint.Hips, hip + (sh - hip) * 0.08f);
        Set(Limber.Protocol.Joint.Spine1, hip + (sh - hip) * 0.30f);
        Set(Limber.Protocol.Joint.Spine2, hip + (sh - hip) * 0.55f);
        Set(Limber.Protocol.Joint.Spine3, hip + (sh - hip) * 0.80f);
        Set(Limber.Protocol.Joint.Neck, j[Neck]);
        Set(Limber.Protocol.Joint.Head, j[Head] + (j[Head] - j[Neck]) * 0.4f);
        Set(Limber.Protocol.Joint.EyeL, j[EyeL]);
        Set(Limber.Protocol.Joint.EyeR, j[EyeR]);

        Set(Limber.Protocol.Joint.ShoulderL, sh + (j[ShoulderL] - sh) * 0.3f);
        Set(Limber.Protocol.Joint.UpperArmL, j[ShoulderL]);
        Set(Limber.Protocol.Joint.ElbowL, j[ElbowL]);
        Set(Limber.Protocol.Joint.WristL, j[WristL]);
        Set(Limber.Protocol.Joint.ShoulderR, sh + (j[ShoulderR] - sh) * 0.3f);
        Set(Limber.Protocol.Joint.UpperArmR, j[ShoulderR]);
        Set(Limber.Protocol.Joint.ElbowR, j[ElbowR]);
        Set(Limber.Protocol.Joint.WristR, j[WristR]);

        Set(Limber.Protocol.Joint.ThighL, j[HipL]);
        Set(Limber.Protocol.Joint.KneeL, j[KneeL]);
        Set(Limber.Protocol.Joint.AnkleL, j[AnkleL]);
        Set(Limber.Protocol.Joint.FootL, j[AnkleL]);
        Set(Limber.Protocol.Joint.ToeL, j[FootL]);
        Set(Limber.Protocol.Joint.ThighR, j[HipR]);
        Set(Limber.Protocol.Joint.KneeR, j[KneeR]);
        Set(Limber.Protocol.Joint.AnkleR, j[AnkleR]);
        Set(Limber.Protocol.Joint.FootR, j[AnkleR]);
        Set(Limber.Protocol.Joint.ToeR, j[FootR]);

        // Fingers: SMPL-X lists each hand as index, middle, little, ring, thumb (3 joints each); Limber's order is
        // thumb, index, middle, ring, little.
        if (fingers && cam.Length >= 55)
        {
            int[] order = [4, 0, 1, 3, 2];
            for (int side = 0; side < 2; side++)
            {
                int start = side == 0 ? HandL : HandR;
                var first = side == 0 ? Limber.Protocol.Joint.ThumbL1 : Limber.Protocol.Joint.ThumbR1;
                for (int f = 0; f < 5; f++)
                    for (int k = 0; k < 3; k++)
                        Set((Limber.Protocol.Joint)((int)first + f * 3 + k), j[start + order[f] * 3 + k]);
            }
        }

        // Feet on the floor: the lowest foot point, smoothed (drops quickly, rises slowly) so the body doesn't bob.
        float low = MathF.Min(MathF.Min(j[AnkleL].Y, j[AnkleR].Y), MathF.Min(j[FootL].Y, j[FootR].Y)) - 0.04f;
        if (!float.IsFinite(floor))
            floor = low;
        else
            floor += (low - floor) * (low < floor ? 0.6f : 0.15f);

        var shift = new Vector3(centre.X, floor, centre.Z);
        for (int i = 0; i < p.Length; i++)
            if (v[i])
                p[i] -= shift;
    }
}
