using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Vfs365.Core;

namespace Vfs365.Graph;

/// <summary>
/// Turns one drive's push notifications into change reads. A notification waits <see cref="Settle"/> plus 0 to <see cref="MaxJitter"/>
/// at random (every subscriber of a busy library gets it at the same moment), notifications meanwhile join it, and reads are at least
/// <see cref="MinInterval"/> apart (SharePoint often sends two per change).
/// </summary>
public sealed class PushDebounce(Func<Task> read) : IDisposable
{
    readonly Lock gate = new();
    readonly CancellationTokenSource stop = new();
    bool pending;
    TimeSpan? lastRead;

    public TimeSpan Settle { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan MaxJitter { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan MinInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Monotonic time (<see cref="MonotonicClock"/>), randomness (0 to 1) and waiting; replaceable for tests.</summary>
    public Func<TimeSpan> Monotonic { get; init; } = MonotonicClock.Now;
    public Func<double> Random { get; init; } = System.Random.Shared.NextDouble;
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    public void Notify()
    {
        TimeSpan wait;
        lock (gate)
        {
            if (pending || stop.IsCancellationRequested)
            {
                return;
            }
            pending = true;
            var now = Monotonic();
            var due = now + Settle + MaxJitter * Random();
            if (lastRead is { } last && last + MinInterval > due)
            {
                due = last + MinInterval;
            }
            wait = due - now;
        }
        _ = ReadAsync(wait);
    }

    async Task ReadAsync(TimeSpan wait)
    {
        try
        {
            await Delay(wait, stop.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        lock (gate)
        {
            pending = false;
            lastRead = Monotonic();
        }
        try
        {
            await read();
        }
        catch (Exception)
        {
            // the reader reports its own failures
        }
    }

    public void Dispose() => stop.Cancel();
}

/// <summary>
/// Push notifications for a drive through Graph's socket.io endpoint (GET /drives/{id}/root/subscriptions/socketIo). A notification
/// only says that something changed; the caller then reads the drive's delta feed. Engine.IO 3 / Socket.IO 2 over a WebSocket.
/// </summary>
public sealed class DriveNotifications(M365Client client)
{
    static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(5), MaxRetry = TimeSpan.FromMinutes(5);

    /// <summary>A channel up this long starts its back-off over when it drops; one that drops sooner backs off further.</summary>
    static readonly TimeSpan StableAfter = TimeSpan.FromSeconds(60);

    /// <summary>Engine.IO 3's defaults, until the server's open packet names its own.</summary>
    static readonly TimeSpan DefaultPingInterval = TimeSpan.FromSeconds(25), DefaultPingTimeout = TimeSpan.FromSeconds(20);

    static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Raw frames, for diagnostics.</summary>
    public Action<string>? Trace { get; set; }

    /// <summary>Why a channel dropped or couldn't open.</summary>
    public Action<string>? Problem { get; set; }

    /// <summary>Monotonic time (<see cref="MonotonicClock"/>), randomness (0 to 1) and the wait between connections; replaceable for tests.</summary>
    public Func<TimeSpan> Monotonic { get; init; } = MonotonicClock.Now;
    public Func<double> Random { get; init; } = System.Random.Shared.NextDouble;
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    /// <summary>Opens the WebSocket; replaceable for tests.</summary>
    public Func<Uri, CancellationToken, Task<WebSocket>> Connect { get; init; } = ConnectAsync;

    public async Task<Uri> GetEndpointAsync(string driveId, CancellationToken ct)
    {
        using var doc = (await client.GetJsonAsync(new Uri($"{M365Client.GraphResource}/v1.0/drives/{driveId}/root/subscriptions/socketIo"), ct))!;
        return new Uri(doc.RootElement.GetProperty("notificationUrl").GetString()!);
    }

    /// <summary>
    /// Calls <paramref name="changed"/> for every notification until cancelled. Reconnects with a fresh endpoint after a drop, backing
    /// off from 5 seconds to 5 minutes plus 0 to 100% at random (a front end restart drops every client at once; they mustn't all come
    /// back together), starting over only after a channel stayed up a minute; <paramref name="connected"/> reports when the channel is
    /// up or down.
    /// </summary>
    public async Task ListenAsync(string driveId, Action changed, Action<bool> connected, CancellationToken ct)
    {
        RequestPriority.MarkBackground();
        var delay = FirstRetry;
        while (!ct.IsCancellationRequested)
        {
            TimeSpan? upSince = null;
            try
            {
                var endpoint = await GetEndpointAsync(driveId, ct);
                await ReceiveAsync(endpoint, changed, () =>
                {
                    upSince = Monotonic();
                    connected(true);
                }, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                Problem?.Invoke($"push channel of drive {driveId}: {e.Message}");
            }
            connected(false);
            if (upSince is { } since && Monotonic() - since >= StableAfter)
            {
                delay = FirstRetry;
            }
            try
            {
                await Delay(delay * (1 + Random()), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            delay = delay * 2 < MaxRetry ? delay * 2 : MaxRetry;
        }
    }

    /// <summary>The proxy and its Windows sign-in, and the connect timeout, of the HTTP requests (<see cref="M365Client.CreateHandler"/>).</summary>
    static readonly HttpMessageInvoker sockets = new(M365Client.CreateHandler());

    /// <summary>
    /// Without WebSocket keep-alive frames (30 s by default): Engine.IO's pings keep the channel up and find a dead one, so those would
    /// only double the traffic.
    /// </summary>
    static async Task<WebSocket> ConnectAsync(Uri uri, CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("User-Agent", GraphUserAgent.Value);
        socket.Options.KeepAliveInterval = TimeSpan.Zero;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(ConnectTimeout);
        try
        {
            await socket.ConnectAsync(uri, sockets, deadline.Token);
            return socket;
        }
        catch (OperationCanceledException e) when (!ct.IsCancellationRequested)
        {
            socket.Dispose();
            throw new IOException($"no connection within {ConnectTimeout.TotalSeconds:N0} s (timed out)", e);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// One connection, until it closes. Silence for longer than the ping interval plus the ping timeout (no pong, no other frame) ends
    /// it as dead, as Engine.IO clients do.
    /// </summary>
    async Task ReceiveAsync(Uri endpoint, Action changed, Action ready, CancellationToken ct)
    {
        var query = endpoint.Query.TrimStart('?');
        var nsp = endpoint.AbsolutePath;
        using var socket = await Connect(new Uri($"wss://{endpoint.Authority}/socket.io/?EIO=3&transport=websocket{(query.Length > 0 ? "&" + query : "")}"), ct);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var sending = new SemaphoreSlim(1, 1); // a WebSocket takes one send at a time: the pinger's and the loop's
        var silence = DefaultPingInterval + DefaultPingTimeout;
        stop.CancelAfter(silence);
        Task? pinger = null;
        var buffer = new byte[16 * 1024];
        try
        {
            while (await ReadAsync(socket, buffer, stop.Token) is { Length: > 0 } message)
            {
                stop.CancelAfter(silence);
                Trace?.Invoke($"< {(message.Length > 200 ? message[..200] + "..." : message)}");
                if (message[0] == '0')
                {
                    // Open: join the endpoint's namespace with its query, then ping at the interval the server asks for
                    using var open = JsonDocument.Parse(message[1..]);
                    var interval = Milliseconds(open.RootElement, "pingInterval") ?? DefaultPingInterval;
                    silence = interval + (Milliseconds(open.RootElement, "pingTimeout") ?? DefaultPingTimeout);
                    stop.CancelAfter(silence);
                    await SendAsync(socket, sending, $"40{nsp}{(query.Length > 0 ? "?" + query : "")},", stop.Token);
                    pinger ??= PingAsync(socket, sending, interval, stop.Token);
                }
                else if (message == "2")
                {
                    await SendAsync(socket, sending, "3", stop.Token);
                }
                else if (message.StartsWith($"40{nsp}", StringComparison.Ordinal))
                {
                    ready();
                }
                else if (message.StartsWith("42", StringComparison.Ordinal))
                {
                    changed();
                }
                else if (message.StartsWith("41", StringComparison.Ordinal) || message[0] == '1')
                {
                    return;
                }
                else if (message.StartsWith("44", StringComparison.Ordinal))
                {
                    throw new IOException($"refused: {message}");
                }
            }
        }
        catch (Exception e) when (stop.IsCancellationRequested && !ct.IsCancellationRequested && e is OperationCanceledException or WebSocketException)
        {
            throw new IOException($"no answer to pings for {silence.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s", e);
        }
        finally
        {
            await stop.CancelAsync();
            if (pinger is not null)
            {
                try
                {
                    await pinger;
                }
                catch (Exception e) when (e is OperationCanceledException or WebSocketException or ObjectDisposedException or IOException)
                {
                }
            }
        }
    }

    static TimeSpan? Milliseconds(JsonElement open, string name) =>
        open.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var ms) && ms > 0 ? TimeSpan.FromMilliseconds(ms) : null;

    static async Task PingAsync(WebSocket socket, SemaphoreSlim sending, TimeSpan interval, CancellationToken ct)
    {
        while (true)
        {
            await Task.Delay(interval, ct);
            await SendAsync(socket, sending, "2", ct);
        }
    }

    static async Task SendAsync(WebSocket socket, SemaphoreSlim sending, string text, CancellationToken ct)
    {
        await sending.WaitAsync(ct);
        try
        {
            await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            sending.Release();
        }
    }

    /// <summary>One whole text message; empty when the server closed the socket.</summary>
    static async Task<string> ReadAsync(WebSocket socket, byte[] buffer, CancellationToken ct)
    {
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return "";
            }
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            }
        }
    }
}
