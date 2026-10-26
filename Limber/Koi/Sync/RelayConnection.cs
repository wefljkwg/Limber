using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Limber.Koi.Sync;

/// <summary>
/// The connection from this game to the player's OWN relay (wss://...workers.dev/owner). Reconnects by
/// itself. Everything received is queued and handled on the framework thread by <see cref="SyncSession"/>.
/// </summary>
public sealed class RelayConnection : IDisposable
{
    public enum State { Off, Connecting, Connected, Failed }

    public readonly record struct Incoming(string? Text, byte[]? Binary);

    private readonly ConcurrentQueue<Incoming> _incoming = new();
    // Pose data may be dropped under load (the next one replaces it); control messages never are.
    private readonly Channel<byte[]> _data =
        Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly ConcurrentQueue<string> _control = new();
    private readonly SemaphoreSlim _wake = new(0);

    private CancellationTokenSource? _cts;
    private Task? _task;
    private volatile State _state = State.Off;
    private volatile string _lastError = "";

    public State Status => _state;
    public string LastError => _lastError;
    public bool IsConnected => _state == State.Connected;

    public void Start(string relayUrl, string ownerToken)
    {
        Stop();
        _cts = new CancellationTokenSource();
        var uri = new Uri(relayUrl.Replace("https://", "wss://", StringComparison.OrdinalIgnoreCase).TrimEnd('/') + "/owner");
        _task = Task.Run(() => RunAsync(uri, ownerToken, _cts.Token));
    }

    public void Stop()
    {
        if (_cts != null)
        {
            // Give queued control messages (end sync, close invite) a moment to go out.
            var deadline = Environment.TickCount64 + 500;
            while (!_control.IsEmpty && _state == State.Connected && Environment.TickCount64 < deadline)
                Thread.Sleep(20);
            _cts.Cancel();
            try { _task?.Wait(2000); } catch { /* shutting down */ }
            _cts.Dispose();
            _cts = null;
        }
        _task = null;
        _state = State.Off;
        while (_data.Reader.TryRead(out _)) { }
        while (_control.TryDequeue(out _)) { }
        while (_incoming.TryDequeue(out _)) { }
    }

    public bool TryDequeue(out Incoming message) => _incoming.TryDequeue(out message);

    public void SendText(string json)
    {
        _control.Enqueue(json);
        _wake.Release();
    }

    public void SendBinary(byte[] data)
    {
        if (_data.Writer.TryWrite(data))
            _wake.Release();
    }

    private async Task RunAsync(Uri uri, string ownerToken, CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            SetState(State.Connecting, ct);
            using var ws = new ClientWebSocket();
            ws.Options.SetRequestHeader("x-lmb-token", ownerToken);
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            try
            {
                await ws.ConnectAsync(uri, ct);
                SetState(State.Connected, ct);
                _lastError = "";
                backoff = TimeSpan.FromSeconds(1);
                _incoming.Enqueue(new Incoming("{\"t\":\"_connected\"}", null));

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var send = SendLoop(ws, linked.Token);
                await ReceiveLoop(ws, linked.Token);
                linked.Cancel();
                try { await send; } catch { /* closing */ }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                _lastError = e is WebSocketException && e.Message.Contains("403")
                    ? "Your relay refused the connection (wrong key). Deploy your relay again."
                    : e.Message;
            }

            if (ct.IsCancellationRequested)
                break;
            SetState(State.Failed, ct);
            _incoming.Enqueue(new Incoming("{\"t\":\"_disconnected\"}", null));
            try { await Task.Delay(backoff, ct); } catch (OperationCanceledException) { break; }
            backoff = TimeSpan.FromSeconds(Math.Min(30, backoff.TotalSeconds * 2));
        }
    }

    /// <summary>A run that has been cancelled (replaced by a newer connection) never overwrites the state.</summary>
    private void SetState(State state, CancellationToken ct)
    {
        if (!ct.IsCancellationRequested)
            _state = state;
    }

    private async Task ReceiveLoop(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            var result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
                return;
            }
            message.Write(buffer, 0, result.Count);
            if (message.Length > 256 * 1024)
                throw new InvalidDataException("message too large");
            if (!result.EndOfMessage)
                continue;

            var bytes = message.ToArray();
            message.SetLength(0);
            _incoming.Enqueue(result.MessageType == WebSocketMessageType.Text
                ? new Incoming(Encoding.UTF8.GetString(bytes), null)
                : new Incoming(null, bytes));
        }
    }

    private async Task SendLoop(ClientWebSocket ws, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            await _wake.WaitAsync(TimeSpan.FromSeconds(1), ct);
            // Control first, then pose data.
            while (_control.TryDequeue(out var text))
                await ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct);
            while (_data.Reader.TryRead(out var binary))
            {
                await ws.SendAsync(binary, WebSocketMessageType.Binary, true, ct);
                if (!_control.IsEmpty)
                    break;
            }
        }
    }

    public void Dispose() => Stop();
}
