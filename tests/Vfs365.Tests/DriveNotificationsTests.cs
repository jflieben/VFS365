using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Vfs365.Graph;

namespace Vfs365.Tests;

public sealed class DriveNotificationsTests
{
    sealed class Tokens : ITokenSource
    {
        public ValueTask<string> GetAccessTokenAsync(string resource, CancellationToken ct) => ValueTask.FromResult("token");
        public ValueTask<string> GetAccessTokenAsync(string resource, string claims, CancellationToken ct) => ValueTask.FromResult("token");
    }

    /// <summary>Graph's answer to GET .../subscriptions/socketIo.</summary>
    sealed class Endpoints : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"notificationUrl":"https://f3hb0mpua.svc.ms/zbaehwg/callback?snthgk=1ff3"}""", Encoding.UTF8, "application/json"),
            });
    }

    /// <summary>A WebSocket pair over loopback TCP: the drive's end and the push server's.</summary>
    static async Task<(WebSocket Drive, WebSocket Server)> PairAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var accepting = listener.AcceptTcpClientAsync();
            var drive = new TcpClient();
            await drive.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            var server = await accepting;
            return (WebSocket.CreateFromStream(drive.GetStream(), new WebSocketCreationOptions { IsServer = false }),
                WebSocket.CreateFromStream(server.GetStream(), new WebSocketCreationOptions { IsServer = true }));
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// An Engine.IO 3 server: opens with a 100 ms ping interval and a 400 ms ping timeout, accepts the namespace join, answers pings for
    /// <paramref name="answerPings"/>, then stays silent.
    /// </summary>
    static async Task ServeAsync(WebSocket socket, TimeSpan answerPings)
    {
        try
        {
            await SendAsync(socket, """0{"sid":"s1","upgrades":[],"pingInterval":100,"pingTimeout":400}""");
            var answering = Stopwatch.StartNew();
            var buffer = new byte[4096];
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, default);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }
                var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                if (message.StartsWith("40/zbaehwg/callback", StringComparison.Ordinal))
                {
                    await SendAsync(socket, "40/zbaehwg/callback?snthgk=1ff3");
                }
                else if (message == "2" && answering.Elapsed < answerPings)
                {
                    await SendAsync(socket, "3");
                }
            }
        }
        catch (Exception e) when (e is WebSocketException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            // the drive's end went away
        }
        finally
        {
            socket.Dispose();
        }
    }

    static Task SendAsync(WebSocket socket, string text) => socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, default);

    [Fact]
    public async Task Reconnects_back_off_with_jitter_and_start_over_only_after_a_minute_up()
    {
        var now = DateTimeOffset.UnixEpoch;
        var waits = new List<TimeSpan>();
        var states = new List<bool>();
        var problems = new List<string>();
        var connections = 0;
        using var cancel = new CancellationTokenSource();
        var notifications = new DriveNotifications(new M365Client(new HttpClient(new Endpoints()), new Tokens()))
        {
            Problem = problems.Add,
            Clock = () => now,
            Random = () => 1, // the full 100% extra
            Delay = (wait, ct) =>
            {
                waits.Add(wait);
                if (waits.Count == 4)
                {
                    cancel.Cancel();
                }
                return ct.IsCancellationRequested ? Task.FromCanceled(ct) : Task.CompletedTask;
            },
            Connect = async (uri, ct) =>
            {
                Assert.StartsWith("wss://f3hb0mpua.svc.ms/socket.io/?EIO=3&transport=websocket&snthgk=1ff3", uri.ToString());
                if (++connections > 2)
                {
                    throw new IOException("refused");
                }
                var (drive, server) = await PairAsync();
                _ = ServeAsync(server, TimeSpan.Zero);
                return drive;
            },
        };

        await notifications.ListenAsync("b!drive", () => { }, up =>
        {
            states.Add(up);
            if (up && connections == 2)
            {
                now += TimeSpan.FromSeconds(61); // the second channel stays up a minute
            }
        }, cancel.Token).WaitAsync(TimeSpan.FromSeconds(30));

        // Silent servers end their channels; the second stayed up long enough to start the back-off over
        Assert.Equal([TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(40)], waits);
        Assert.Equal([true, false, true, false, false, false], states);
        Assert.Equal(2, problems.Count(p => p.Contains("no answer to pings")));
    }

    [Fact]
    public async Task Pongs_keep_a_channel_up_and_silence_ends_it()
    {
        using var cancel = new CancellationTokenSource();
        var up = new Stopwatch();
        var upFor = TimeSpan.Zero;
        var problems = new List<string>();
        var notifications = new DriveNotifications(new M365Client(new HttpClient(new Endpoints()), new Tokens()))
        {
            Problem = problems.Add,
            Delay = (_, _) =>
            {
                cancel.Cancel();
                return Task.FromCanceled(cancel.Token);
            },
            Connect = async (_, _) =>
            {
                var (drive, server) = await PairAsync();
                _ = ServeAsync(server, TimeSpan.FromSeconds(1));
                return drive;
            },
        };

        await notifications.ListenAsync("b!drive", () => { }, isUp =>
        {
            if (isUp)
            {
                up.Start();
            }
            else
            {
                upFor = up.Elapsed;
            }
        }, cancel.Token).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(upFor >= TimeSpan.FromSeconds(1), $"up for {upFor.TotalMilliseconds} ms"); // 500 ms of silence would have ended it
        Assert.Contains(problems, p => p.Contains("no answer to pings for 0.5 s"));
    }
}
