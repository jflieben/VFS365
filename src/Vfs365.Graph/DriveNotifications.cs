using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Vfs365.Core;

namespace Vfs365.Graph;

/// <summary>
/// Push notifications for a drive through Graph's socket.io endpoint (GET /drives/{id}/root/subscriptions/socketIo). A notification
/// only says that something changed; the caller then reads the drive's delta feed. Engine.IO 3 / Socket.IO 2 over a WebSocket.
/// </summary>
public sealed class DriveNotifications(M365Client client)
{
    /// <summary>Raw frames, for diagnostics.</summary>
    public Action<string>? Trace { get; set; }

    /// <summary>Why a channel dropped or couldn't open.</summary>
    public Action<string>? Problem { get; set; }

    public async Task<Uri> GetEndpointAsync(string driveId, CancellationToken ct)
    {
        using var doc = (await client.GetJsonAsync(new Uri($"{M365Client.GraphResource}/v1.0/drives/{driveId}/root/subscriptions/socketIo"), ct))!;
        return new Uri(doc.RootElement.GetProperty("notificationUrl").GetString()!);
    }

    /// <summary>
    /// Calls <paramref name="changed"/> for every notification until cancelled. Reconnects with a fresh endpoint after a drop,
    /// backing off up to 5 minutes; <paramref name="connected"/> reports when the channel is up or down.
    /// </summary>
    public async Task ListenAsync(string driveId, Action changed, Action<bool> connected, CancellationToken ct)
    {
        RequestPriority.MarkBackground();
        var delay = TimeSpan.FromSeconds(5);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var endpoint = await GetEndpointAsync(driveId, ct);
                await ReceiveAsync(endpoint, changed, () =>
                {
                    delay = TimeSpan.FromSeconds(5);
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
            try
            {
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 300));
        }
    }

    async Task ReceiveAsync(Uri endpoint, Action changed, Action ready, CancellationToken ct)
    {
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("User-Agent", GraphUserAgent.Value);
        var query = endpoint.Query.TrimStart('?');
        var nsp = endpoint.AbsolutePath;
        await socket.ConnectAsync(new Uri($"wss://{endpoint.Authority}/socket.io/?EIO=3&transport=websocket{(query.Length > 0 ? "&" + query : "")}"), ct);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task? pinger = null;
        var buffer = new byte[16 * 1024];
        try
        {
            while (await ReadAsync(socket, buffer, stop.Token) is { Length: > 0 } message)
            {
                Trace?.Invoke($"< {(message.Length > 200 ? message[..200] + "..." : message)}");
                if (message[0] == '0')
                {
                    // Open: join the endpoint's namespace with its query, then ping at the interval the server asks for
                    using var open = JsonDocument.Parse(message[1..]);
                    var interval = open.RootElement.TryGetProperty("pingInterval", out var ms) && ms.ValueKind == JsonValueKind.Number ? ms.GetInt32() : 25000;
                    await SendAsync(socket, $"40{nsp}{(query.Length > 0 ? "?" + query : "")},", stop.Token);
                    pinger = PingAsync(socket, TimeSpan.FromMilliseconds(interval), stop.Token);
                }
                else if (message == "2")
                {
                    await SendAsync(socket, "3", stop.Token);
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
        finally
        {
            await stop.CancelAsync();
            if (pinger is not null)
            {
                try
                {
                    await pinger;
                }
                catch (Exception e) when (e is OperationCanceledException or WebSocketException)
                {
                }
            }
        }
    }

    static async Task PingAsync(ClientWebSocket socket, TimeSpan interval, CancellationToken ct)
    {
        while (true)
        {
            await Task.Delay(interval, ct);
            await SendAsync(socket, "2", ct);
        }
    }

    static Task SendAsync(ClientWebSocket socket, string text, CancellationToken ct) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct);

    /// <summary>One whole text message; empty when the server closed the socket.</summary>
    static async Task<string> ReadAsync(ClientWebSocket socket, byte[] buffer, CancellationToken ct)
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
