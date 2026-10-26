using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Limber.Video;

/// <summary>
/// Reads what VLC is playing through VLC's own web interface (Tools > Preferences > All > Interface >
/// Main interfaces > Web), on this PC only: the file, whether it's playing, and the play position. Limber then
/// tracks that same file at that same moment, so the character follows the video in step - pause, seek and speed
/// included - without capturing the screen.
/// </summary>
public sealed class VlcLink : IDisposable
{
    public enum Playback { Unknown, Stopped, Paused, Playing }

    public readonly record struct State(Playback Playback, string? FilePath, double Seconds, double Position, double Length, double Rate, long ReadTicks);

    private readonly HttpClient _http;

    public VlcLink(int port, string password)
    {
        // 127.0.0.1 only: nothing here can ever talk to another computer.
        // No proxy, ever: a system proxy must never see (or answer for) the local VLC connection.
        _http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new Uri($"http://127.0.0.1:{Math.Clamp(port, 1, 65535)}/"), Timeout = TimeSpan.FromSeconds(2) };
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + password));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
    }

    private int _lastPlaylistId = int.MinValue;
    private string? _lastPath;

    /// <summary>Blocking; call from a background thread. Throws <see cref="VlcException"/> with a readable message.</summary>
    public State Read()
    {
        using var status = Get("requests/status.json");
        var root = status.RootElement;

        var playback = Str(root, "state") switch
        {
            "playing" => Playback.Playing,
            "paused" => Playback.Paused,
            "stopped" => Playback.Stopped,
            _ => Playback.Unknown,
        };
        double length = Num(root, "length");
        double position = Num(root, "position");
        double time = Num(root, "time");
        double rate = Num(root, "rate", 1);
        // "position" (0..1) is much finer than "time" (whole seconds).
        double seconds = length > 0 && position > 0 ? position * length : time;

        int id = (int)Num(root, "currentplid", -1);
        if (id != _lastPlaylistId)
        {
            // Only remember the item once its file is known (VLC's playlist can lag a moment behind).
            _lastPath = null;
            var path = id >= 0 ? FindPath(id) : null;
            if (path != null || id < 0)
                _lastPlaylistId = id;
            _lastPath = path;
        }

        return new State(playback, _lastPath, seconds, position, length, rate <= 0 ? 1 : rate, System.Diagnostics.Stopwatch.GetTimestamp());
    }

    private string? FindPath(int id)
    {
        using var playlist = Get("requests/playlist.json");
        var uri = FindUri(playlist.RootElement, id.ToString());
        if (uri == null)
            return null;
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !parsed.IsFile)
            throw new VlcException("VLC is playing a stream or web address. Limber can only follow video files on this PC.");
        return parsed.LocalPath;
    }

    private static string? FindUri(JsonElement node, string id)
    {
        if (node.ValueKind != JsonValueKind.Object)
            return null;
        if (node.TryGetProperty("id", out var nid) && (nid.ValueKind == JsonValueKind.String ? nid.GetString() : nid.ToString()) == id &&
            node.TryGetProperty("uri", out var uri) && uri.ValueKind == JsonValueKind.String)
            return uri.GetString();
        if (node.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in children.EnumerateArray())
            {
                var found = FindUri(child, id);
                if (found != null)
                    return found;
            }
        }
        return null;
    }

    private JsonDocument Get(string path)
    {
        HttpResponseMessage response;
        try
        {
            response = _http.Send(new HttpRequestMessage(HttpMethod.Get, path));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            throw new VlcException("Can't reach VLC. Is it open, with its web interface turned on (see the setup steps)?");
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new VlcException("VLC refused the password. Check it matches VLC's Lua HTTP password.");
            if (!response.IsSuccessStatusCode)
                throw new VlcException($"VLC answered {(int)response.StatusCode}. Is its web interface turned on?");
            try
            {
                using var stream = response.Content.ReadAsStream();
                return JsonDocument.Parse(stream);
            }
            catch (JsonException)
            {
                throw new VlcException("VLC's answer wasn't understood (is something else using that port?).");
            }
            catch (Exception e) when (e is IOException or HttpRequestException or TaskCanceledException)
            {
                throw new VlcException("The connection to VLC dropped. Retrying...");
            }
        }
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double Num(JsonElement e, string name, double fallback = 0)
    {
        if (!e.TryGetProperty(name, out var v))
            return fallback;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d))
            return d;
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var s))
            return s;
        return fallback;
    }

    public void Dispose() => _http.Dispose();
}

public sealed class VlcException(string message) : Exception(message);
