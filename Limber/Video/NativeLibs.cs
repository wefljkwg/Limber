using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Limber.Video;

/// <summary>
/// The video tab uses two native libraries that ship next to Limber.dll: ONNX Runtime (runs the tracking models)
/// and OpenCvSharpExtern (reads video files). The game doesn't look for native DLLs in a plugin's folder, so they
/// are loaded from there by full path and handed to the managed wrappers when they ask for them.
/// </summary>
internal static class NativeLibs
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, nint> Handles = new(StringComparer.OrdinalIgnoreCase);
    private static bool _attempted;
    private static string? _error;

    public static string? Error => _error;

    /// <summary>Load both libraries once. False (with <see cref="Error"/>) if anything is missing.</summary>
    public static bool EnsureLoaded(string pluginDir)
    {
        lock (Gate)
        {
            if (_attempted)
                return _error == null;
            _attempted = true;

            try
            {
                foreach (var name in new[] { "onnxruntime", "OpenCvSharpExtern" })
                {
                    if (Find(pluginDir, name + ".dll") == null)
                    {
                        _error = $"{name}.dll is missing from Limber's folder - reinstall or rebuild Limber.";
                        return false;
                    }
                }

                // Load from a private copy, so the files in Limber's folder are never locked by the game
                // (a rebuild or update can replace them while the game is running).
                var dir = ShadowCopy(pluginDir);

                // DirectML (graphics-card tracking) first, so ONNX Runtime uses this copy rather than an older one
                // that comes with Windows. Optional: without it, tracking runs on the CPU.
                var directMl = Find(dir, "DirectML.dll") ?? Find(pluginDir, "DirectML.dll");
                if (directMl != null)
                {
                    try { Handles["DirectML"] = NativeLibrary.Load(directMl); }
                    catch (Exception e) { Plugin.Log.Warning($"Limber: DirectML didn't load ({e.Message}); tracking will use the CPU."); }
                }

                foreach (var name in new[] { "onnxruntime", "OpenCvSharpExtern" })
                    Handles[name] = NativeLibrary.Load(Find(dir, name + ".dll") ?? Find(pluginDir, name + ".dll")!);

                Register(typeof(Microsoft.ML.OnnxRuntime.InferenceSession).Assembly);
                Register(typeof(OpenCvSharp.Mat).Assembly);
                return true;
            }
            catch (Exception e)
            {
                _error = "Couldn't load the video libraries: " + e.Message;
                return false;
            }
        }
    }

    /// <summary>
    /// Copy every native DLL next to Limber (ONNX Runtime, OpenCV and its FFmpeg video reader) into a temp folder
    /// named after this build, and return that folder. Falls back to Limber's own folder if copying fails.
    /// </summary>
    private static string ShadowCopy(string pluginDir)
    {
        try
        {
            var stamp = File.GetLastWriteTimeUtc(Path.Combine(pluginDir, "Limber.dll")).Ticks.ToString("x");
            var target = Path.Combine(Path.GetTempPath(), "Limber", "native", stamp);
            Directory.CreateDirectory(target);

            // Older builds' copies are no longer needed (skip any still in use by another game window).
            foreach (var old in Directory.GetDirectories(Path.GetDirectoryName(target)!))
            {
                if (string.Equals(old, target, StringComparison.OrdinalIgnoreCase))
                    continue;
                try { Directory.Delete(old, true); } catch { /* in use */ }
            }
            foreach (var file in Directory.GetFiles(pluginDir, "*.dll"))
            {
                var name = Path.GetFileName(file);
                if (name.StartsWith("onnxruntime", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("DirectML", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("OpenCvSharpExtern", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("opencv_", StringComparison.OrdinalIgnoreCase))
                {
                    var dest = Path.Combine(target, name);
                    if (!File.Exists(dest) || new FileInfo(dest).Length != new FileInfo(file).Length)
                        File.Copy(file, dest, true);
                }
            }
            return target;
        }
        catch
        {
            return pluginDir;
        }
    }

    private static void Register(Assembly assembly)
    {
        try
        {
            NativeLibrary.SetDllImportResolver(assembly, Resolve);
        }
        catch (InvalidOperationException)
        {
            // Already set (plugin reloaded in the same session); the libraries are loaded by then anyway.
        }
    }

    private static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        var key = Path.GetFileNameWithoutExtension(libraryName);
        lock (Gate)
            return Handles.TryGetValue(key, out var handle) ? handle : 0;
    }

    private static string? Find(string dir, string file)
    {
        foreach (var candidate in new[]
                 {
                     Path.Combine(dir, file),
                     Path.Combine(dir, "runtimes", "win-x64", "native", file),
                 })
        {
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }
}
