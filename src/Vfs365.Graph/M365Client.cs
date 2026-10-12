using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Vfs365.Core;

namespace Vfs365.Graph;

public sealed class M365RequestException(HttpStatusCode status, Uri uri, string detail)
    : RemoteException(ToError(status, detail), $"{(int)status} {status} from {uri.GetLeftPart(UriPartial.Path)}: {detail}")
{
    public HttpStatusCode Status { get; } = status;

    static RemoteError ToError(HttpStatusCode status, string detail) => (int)status switch
    {
        _ when detail.Contains("\"serviceReadOnly\"", StringComparison.OrdinalIgnoreCase) => RemoteError.ReadOnly,
        404 => RemoteError.NotFound,
        410 => RemoteError.Gone,
        401 or 403 => RemoteError.AccessDenied,
        429 => RemoteError.Throttled,
        423 => RemoteError.Locked,
        409 or 412 => RemoteError.Conflict,
        >= 500 => RemoteError.Unavailable,
        _ => RemoteError.Other,
    };
}

/// <summary>What a client did since it started, for statistics.</summary>
public readonly record struct ClientUsage(long GraphRequests, long SharePointRequests, long ResourceUnits, long BytesReceived, long BytesSent,
    long Throttled, long ThrottledSeconds, long BudgetWaitForegroundSeconds, long BudgetWaitBackgroundSeconds)
{
    public ClientUsage Since(ClientUsage earlier) => new(GraphRequests - earlier.GraphRequests, SharePointRequests - earlier.SharePointRequests,
        ResourceUnits - earlier.ResourceUnits, BytesReceived - earlier.BytesReceived, BytesSent - earlier.BytesSent, Throttled - earlier.Throttled,
        ThrottledSeconds - earlier.ThrottledSeconds, BudgetWaitForegroundSeconds - earlier.BudgetWaitForegroundSeconds,
        BudgetWaitBackgroundSeconds - earlier.BudgetWaitBackgroundSeconds);
}

/// <summary>
/// HTTP for Graph and SharePoint REST: token per resource (https Graph and the tenant's SharePoint hosts only), User-Agent, retries,
/// one shared pause when throttled plus a longer one for background work, this user's request budget (<see cref="RequestBudget"/>),
/// usage counts.
/// </summary>
/// <remarks>
/// Retries: reads (GET, HEAD) on 429, 503, 500, 502 and 504; writes only on 429 and 503 with Retry-After (the request wasn't run),
/// since a write that failed with a 5xx may have been committed. Pauses and backoff get 0 to 50% random extra, so a tenant's agents
/// don't come back at the same moment.
/// </remarks>
public sealed class M365Client(HttpClient http, ITokenSource tokens, RequestBudget? budget = null)
{
    public const string GraphResource = "https://graph.microsoft.com";
    const int MaxAttempts = 5;
    static readonly TimeSpan MaxRetryAfter = TimeSpan.FromMinutes(5);
    static readonly TimeSpan ForegroundRelease = TimeSpan.FromMilliseconds(100), BackgroundRelease = TimeSpan.FromMilliseconds(500);

    /// <summary>RateLimit-Remaining at or below this share of RateLimit-Limit holds background work until RateLimit-Reset.</summary>
    const double LowRemaining = 0.2;

    readonly Lock pauseLock = new();
    DateTimeOffset pausedUntil = DateTimeOffset.MinValue;
    DateTimeOffset backgroundUntil = DateTimeOffset.MinValue;
    DateTimeOffset foregroundNext = DateTimeOffset.MinValue;
    DateTimeOffset backgroundNext = DateTimeOffset.MinValue;
    long graphRequests;
    long sharePointRequests;
    long resourceUnits;
    long bytesReceived;
    long bytesSent;
    long throttledResponses;
    long throttledTicks;

    /// <summary>Time, randomness (0 to 1) and waiting; replaceable for tests.</summary>
    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;
    public Func<double> Random { get; init; } = System.Random.Shared.NextDouble;
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    /// <summary>
    /// The tenant's SharePoint root host (contoso.sharepoint.com), when known: tokens then go only to its hosts (-my and multi-geo
    /// included). <see cref="SharePointDiscoveryApi.GetMyDriveAsync"/> sets it when it is still empty.
    /// </summary>
    public string? TenantRootHost { get; set; }

    /// <summary>After a 429 or 503, background requests (<see cref="RequestPriority"/>) wait this long; foreground ones only for Retry-After.</summary>
    public TimeSpan ThrottlePause { get; set; } = TimeSpan.FromMinutes(15);

    public long GraphRequests => Interlocked.Read(ref graphRequests);

    /// <summary>When Graph or SharePoint last answered 429 or 503.</summary>
    public DateTimeOffset LastThrottled { get; private set; } = DateTimeOffset.MinValue;

    /// <summary>Until when background requests wait: <see cref="ThrottlePause"/> after throttling, or the reset of a nearly used app limit.</summary>
    public DateTimeOffset BackgroundPausedUntil
    {
        get
        {
            lock (pauseLock)
            {
                return backgroundUntil > pausedUntil ? backgroundUntil : pausedUntil;
            }
        }
    }

    /// <summary>Whether background requests wait now; the engine skips starting background work meanwhile.</summary>
    public bool BackgroundPaused => BackgroundPausedUntil > Clock();

    public long SharePointRequests => Interlocked.Read(ref sharePointRequests);

    /// <summary>
    /// ResourceUnits counts every attempt sent, retries and throttled ones included (Microsoft counts those too): the estimate
    /// (<see cref="ResourceUnits.Estimate"/>), or the x-ms-resource-unit header when the response has one.
    /// </summary>
    public ClientUsage Usage => new(GraphRequests, SharePointRequests, Interlocked.Read(ref resourceUnits), Interlocked.Read(ref bytesReceived),
        Interlocked.Read(ref bytesSent), Interlocked.Read(ref throttledResponses), (long)TimeSpan.FromTicks(Interlocked.Read(ref throttledTicks)).TotalSeconds,
        (long)(budget?.WaitedForeground.TotalSeconds ?? 0), (long)(budget?.WaitedBackground.TotalSeconds ?? 0));

    void Received(long bytes) => Interlocked.Add(ref bytesReceived, bytes);

    /// <summary>Called once per request with status, method, path and duration.</summary>
    public Action<string>? Log { get; set; }

    public Task<JsonDocument?> GetJsonAsync(Uri uri, CancellationToken ct, bool notFoundAsNull = false) =>
        SendJsonAsync(HttpMethod.Get, uri, null, ct, notFoundAsNull: notFoundAsNull);

    /// <summary>
    /// Sends a request and parses the JSON response; null for an empty response or (with <paramref name="notFoundAsNull"/>) 404.
    /// <paramref name="content"/> builds the body for each attempt. Upload session URLs are pre-authenticated: pass authenticate: false.
    /// </summary>
    public async Task<JsonDocument?> SendJsonAsync(HttpMethod method, Uri uri, Func<HttpContent>? content, CancellationToken ct,
        string? ifMatch = null, bool notFoundAsNull = false, bool authenticate = true)
    {
        using var response = await SendAsync(method, uri, content, ifMatch, authenticate, notFoundAsNull, ct);
        if (response is null || response.StatusCode == HttpStatusCode.NoContent || response.Content.Headers.ContentLength == 0)
        {
            return null;
        }
        await using var stream = new CountingStream(await response.Content.ReadAsStreamAsync(ct), Received);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    /// <summary>
    /// Content from <paramref name="offset"/> to the end as a stream; dispose it to end the request. For pre-authenticated download URLs.
    /// A server that ignores the range sends everything; the bytes before the offset are skipped. Total is the whole content's length
    /// the server states (Content-Range, else Content-Length), when it states one.
    /// </summary>
    public async Task<(Stream Content, long? Total)> OpenStreamAsync(Uri uri, long offset, CancellationToken ct)
    {
        var response = (await SendAsync(HttpMethod.Get, uri, null, null, false, false, ct, offset > 0 ? offset : null))!;
        try
        {
            var partial = response.StatusCode == HttpStatusCode.PartialContent;
            var total = partial ? response.Content.Headers.ContentRange?.Length : response.Content.Headers.ContentLength;
            var stream = new CountingStream(await response.Content.ReadAsStreamAsync(ct), Received);
            if (offset > 0 && !partial && total <= offset)
            {
                return (new ResponseStream(response, stream), total); // shorter than the offset: the caller sees Total
            }
            if (offset > 0 && !partial)
            {
                var skip = new byte[81920];
                for (var left = offset; left > 0;)
                {
                    var read = await stream.ReadAsync(skip.AsMemory(0, (int)Math.Min(skip.Length, left)), ct);
                    if (read == 0)
                    {
                        throw new IOException($"Download ended before offset {offset}");
                    }
                    left -= read;
                }
            }
            return (new ResponseStream(response, stream), total);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    async Task<HttpResponseMessage?> SendAsync(HttpMethod method, Uri uri, Func<HttpContent>? content, string? ifMatch, bool authenticate,
        bool notFoundAsNull, CancellationToken ct, long? rangeFrom = null)
    {
        var isGraph = uri.Host.Equals("graph.microsoft.com", StringComparison.OrdinalIgnoreCase);
        if (authenticate && !MayAuthenticate(uri, TenantRootHost))
        {
            throw new InvalidOperationException($"Refused to send a sign-in token to {uri.Scheme}://{uri.Host}: only https Microsoft Graph and the tenant's SharePoint hosts get one.");
        }
        var resource = isGraph ? GraphResource : $"{uri.Scheme}://{uri.Host}";
        var cost = ResourceUnits.Estimate(method, uri, authenticate);
        var readOnly = method == HttpMethod.Get || method == HttpMethod.Head;
        var priority = RequestPriority.Current;

        string? claims = null;
        for (var attempt = 1; ; attempt++)
        {
            await WaitWhilePausedAsync(priority, ct);
            if (budget is not null)
            {
                await budget.TakeAsync(cost, () => priority?.IsBackground ?? false, ct, priority?.Promoted);
                await WaitWhilePausedAsync(priority, ct); // a pause that began while this waited for the budget
            }
            Interlocked.Add(ref resourceUnits, cost);
            using var request = new HttpRequestMessage(method, uri) { Content = content?.Invoke() };
            Interlocked.Add(ref bytesSent, request.Content?.Headers.ContentLength ?? 0);
            if (authenticate)
            {
                var token = claims is null ? await tokens.GetAccessTokenAsync(resource, ct) : await tokens.GetAccessTokenAsync(resource, claims, ct);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            request.Headers.TryAddWithoutValidation("User-Agent", GraphUserAgent.Value);
            request.Headers.TryAddWithoutValidation("Accept", isGraph ? "application/json" : "application/json;odata=nometadata");
            if (ifMatch is not null)
            {
                request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
            }
            if (rangeFrom is { } from)
            {
                request.Headers.Range = new RangeHeaderValue(from, null);
            }
            if (isGraph)
            {
                Interlocked.Increment(ref graphRequests);
            }
            else
            {
                Interlocked.Increment(ref sharePointRequests);
            }

            var stopwatch = Stopwatch.StartNew();
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            Log?.Invoke($"{(int)response.StatusCode} {method.Method} {uri.Host}{uri.AbsolutePath} {stopwatch.ElapsedMilliseconds} ms");
            if (authenticate && HeaderNumber(response, "x-ms-resource-unit") is { } charged && charged != cost)
            {
                Interlocked.Add(ref resourceUnits, charged - cost);
                budget?.Adjust((int)charged - cost);
            }
            ReadRateLimit(response);
            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.NotFound && notFoundAsNull)
                {
                    return null;
                }
                if (response.StatusCode == HttpStatusCode.Unauthorized && authenticate && claims is null && ClaimsChallenge(response) is { } challenge)
                {
                    // Continuous Access Evaluation: the token was revoked or no longer meets policy; get one with these claims and retry once
                    claims = challenge;
                    attempt--;
                    continue;
                }

                var status = (int)response.StatusCode;
                var throttled = status is 429 or 503;
                var retryAfter = RetryAfter(response, Clock());
                if (throttled)
                {
                    // Every request waits for the pause; background work longer
                    var pause = Jitter(retryAfter ?? Backoff(attempt));
                    LastThrottled = Clock();
                    Interlocked.Increment(ref throttledResponses);
                    Interlocked.Add(ref throttledTicks, pause.Ticks);
                    Pause(pause, ThrottlePause > pause ? Jitter(ThrottlePause) : pause);
                }
                var retry = attempt < MaxAttempts && (throttled ? readOnly || retryAfter is not null : readOnly && status is 500 or 502 or 504);
                if (!retry)
                {
                    throw new M365RequestException(response.StatusCode, uri, await ReadDetailAsync(response, ct));
                }
                if (!throttled)
                {
                    await Delay(Jitter(Backoff(attempt)), ct);
                }
            }
        }
    }

    static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(Math.Pow(2, attempt));

    /// <summary>Plus 0 to 50%.</summary>
    TimeSpan Jitter(TimeSpan delay) => delay * (1 + Random() * 0.5);

    void Pause(TimeSpan all, TimeSpan background)
    {
        lock (pauseLock)
        {
            var now = Clock();
            if (now + all > pausedUntil)
            {
                pausedUntil = now + all;
            }
            if (now + background > backgroundUntil)
            {
                backgroundUntil = now + background;
            }
        }
    }

    /// <summary>
    /// Waits while requests are paused (background ones also while <see cref="BackgroundPausedUntil"/>). Requests that waited are let
    /// go one by one, foreground ones 100 ms apart and background ones 500 ms, so a queue doesn't return all at once. Promoted work
    /// stops waiting for the background pause.
    /// </summary>
    async Task WaitWhilePausedAsync(PriorityHolder? priority, CancellationToken ct)
    {
        var held = false;
        while (true)
        {
            var background = priority?.IsBackground ?? false;
            TimeSpan wait;
            lock (pauseLock)
            {
                var now = Clock();
                var until = background && backgroundUntil > pausedUntil ? backgroundUntil : pausedUntil;
                if (until > now)
                {
                    wait = until - now;
                    held = true;
                }
                else if (held)
                {
                    ref var next = ref background ? ref backgroundNext : ref foregroundNext;
                    var slot = next > now ? next : now;
                    next = slot + (background ? BackgroundRelease : ForegroundRelease);
                    wait = slot - now;
                    held = false;
                }
                else
                {
                    return;
                }
            }
            if (wait > TimeSpan.Zero)
            {
                var waiting = Delay(wait, ct);
                await (background && priority is not null ? Task.WhenAny(waiting, priority.Promoted) : waiting);
                ct.ThrowIfCancellationRequested();
            }
        }
    }

    /// <summary>
    /// RateLimit-Limit, -Remaining and -Reset (seconds), when SharePoint sends them (near the app's limit; Microsoft doesn't promise
    /// them): with little left, background requests wait for the reset.
    /// </summary>
    void ReadRateLimit(HttpResponseMessage response)
    {
        if (HeaderNumber(response, "RateLimit-Limit") is not { } limit || HeaderNumber(response, "RateLimit-Remaining") is not { } remaining
            || limit <= 0 || remaining > limit * LowRemaining)
        {
            return;
        }
        var reset = TimeSpan.FromSeconds(Math.Clamp(HeaderNumber(response, "RateLimit-Reset") ?? 60, 1, MaxRetryAfter.TotalSeconds));
        Pause(TimeSpan.Zero, Jitter(reset));
    }

    /// <summary>The leading whole number of a response header, or null.</summary>
    static long? HeaderNumber(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) && values.FirstOrDefault() is { } value
            && System.Text.RegularExpressions.Regex.Match(value, @"^\s*(\d+)") is { Success: true } match && long.TryParse(match.Groups[1].Value, out var number)
            ? number : null;

    /// <summary>Retry-After as a delay from <paramref name="now"/>, between 0 and 5 minutes; null when the response has none.</summary>
    public static TimeSpan? RetryAfter(HttpResponseMessage response, DateTimeOffset now)
    {
        var delay = response.Headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - now,
            _ => (TimeSpan?)null,
        };
        return delay is { } value ? TimeSpan.FromTicks(Math.Clamp(value.Ticks, 0, MaxRetryAfter.Ticks)) : null;
    }

    /// <summary>
    /// Whether a request may carry a token: <see cref="TrustedHosts"/> once the tenant is known; before that (the first /me/drive
    /// call) https to graph.microsoft.com or a single-label *.sharepoint.com host.
    /// </summary>
    public static bool MayAuthenticate(Uri uri, string? tenantRootHost) =>
        tenantRootHost is not null ? TrustedHosts.IsTrusted(uri, tenantRootHost)
        : uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0
            && (uri.IdnHost.Equals("graph.microsoft.com", StringComparison.OrdinalIgnoreCase) || TenantLabel(uri.IdnHost.ToLowerInvariant()) is not null);

    const string SharePointSuffix = ".sharepoint.com";

    /// <summary>"contoso" for contoso.sharepoint.com and contoso-my.sharepoint.com; null for anything else.</summary>
    static string? TenantLabel(string host)
    {
        if (!host.EndsWith(SharePointSuffix, StringComparison.Ordinal) || host.Length == SharePointSuffix.Length)
        {
            return null;
        }
        var label = host[..^SharePointSuffix.Length];
        if (label.Contains('.'))
        {
            return null;
        }
        return label.EndsWith("-my", StringComparison.Ordinal) ? label[..^3] : label;
    }

    /// <summary>The decoded claims of a 'WWW-Authenticate: Bearer ... error="insufficient_claims", claims="base64"' challenge; null for any other 401.</summary>
    public static string? ClaimsChallenge(HttpResponseMessage response)
    {
        foreach (var header in response.Headers.WwwAuthenticate)
        {
            if (header.Parameter is { } parameter && parameter.Contains("insufficient_claims", StringComparison.OrdinalIgnoreCase)
                && System.Text.RegularExpressions.Regex.Match(parameter, "claims=\"([^\"]+)\"") is { Success: true } match)
            {
                try
                {
                    return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(match.Groups[1].Value));
                }
                catch (FormatException)
                {
                    return match.Groups[1].Value;
                }
            }
        }
        return null;
    }

    static async Task<string> ReadDetailAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        return body.Length > 300 ? body[..300] : body;
    }
}

/// <summary>Reports how many bytes were read through it.</summary>
sealed class CountingStream(Stream inner, Action<long> counted) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => Count(await inner.ReadAsync(buffer, ct));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    int Count(int read)
    {
        counted(read);
        return read;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>A response body that ends its request when disposed.</summary>
sealed class ResponseStream(HttpResponseMessage response, Stream inner) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
            response.Dispose();
        }
        base.Dispose(disposing);
    }
}