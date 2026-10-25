using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
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
        400 => RemoteError.Rejected,
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

/// <summary>
/// A throttled response: when, its status (429 or 503), how long requests now wait, and what kind of request it was. BackgroundPause:
/// how long background requests now wait (<see cref="M365Client.ThrottlePause"/> with its jitter, at least Pause).
/// </summary>
public sealed record ThrottleNotice(DateTimeOffset At, int Status, TimeSpan Pause, bool Graph, bool Background, TimeSpan BackgroundPause = default);
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
/// since a write that failed with a 5xx may have been committed. A read whose connection dropped is sent once more, and so is a
/// background read without an answer (see <see cref="Resendable"/>). Upload session fragments are retried by DriveApi, from the range
/// the session expects next. Pauses and backoff get 0 to 50% random extra, so a tenant's agents don't come back at the same moment.
/// Timeouts are this client's (the agent's HttpClient has none): <see cref="HeaderTimeout"/> and <see cref="StallTimeout"/>, plus the
/// handler's connect timeout (<see cref="CreateHandler"/>). They fail like HttpClient's own, as a TaskCanceledException while the
/// caller's token is not cancelled. Authenticated (JSON) requests ask for gzip; transfer URLs never do.
/// </remarks>
public sealed class M365Client(HttpClient http, ITokenSource tokens, RequestBudget? budget = null)
{
    public const string GraphResource = "https://graph.microsoft.com";
    const int MaxAttempts = 5;
    static readonly TimeSpan MinRetryAfter = TimeSpan.FromSeconds(1);
    static readonly TimeSpan MaxRetryAfter = TimeSpan.FromMinutes(5);
    static readonly TimeSpan ForegroundRelease = TimeSpan.FromMilliseconds(100), BackgroundRelease = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Background work waits this long (not <see cref="ThrottlePause"/>) after a 503 without Retry-After: a server that is unwell more
    /// than a throttle (Graph's and SharePoint's throttling answers carry Retry-After), so one such answer doesn't stop background work
    /// for a quarter of an hour; a run of them still keeps it waiting.
    /// </summary>
    static readonly TimeSpan UnwellPause = TimeSpan.FromMinutes(1);

    /// <summary>The wait before a read that failed on the way is sent once more.</summary>
    static readonly TimeSpan ResendDelay = TimeSpan.FromSeconds(1);

    /// <summary>A claims value that asks the token source for a new token without asking for more (WamTokenSource adds its cp1 capability).</summary>
    const string NoClaims = "{}";

    static readonly StringWithQualityHeaderValue Gzip = new("gzip");

    /// <summary>RateLimit-Remaining at or below this share of RateLimit-Limit holds background work until RateLimit-Reset.</summary>
    const double LowRemaining = 0.2;

    // Monotonic (see Monotonic), so a wall clock set back doesn't hold requests for the step
    readonly Lock pauseLock = new();
    TimeSpan pausedUntil = TimeSpan.MinValue;
    TimeSpan backgroundUntil = TimeSpan.MinValue;
    TimeSpan foregroundNext = TimeSpan.MinValue;
    TimeSpan backgroundNext = TimeSpan.MinValue;
    long graphRequests;
    long sharePointRequests;
    long resourceUnits;
    long bytesReceived;
    long bytesSent;
    long throttledResponses;
    long throttledTicks;

    /// <summary>
    /// The handler the agent's HttpClient uses. The system proxy with the Windows sign-in for proxies that ask (NTLM, Kerberos); a 15 s
    /// connect timeout, so a network that drops packets fails a request in seconds, not after TCP's 21; connections renewed after
    /// 5 minutes (DNS and front end changes) and closed after 2 idle ones, under common NAT and firewall idle limits; no automatic
    /// decompression (JSON asks for gzip itself, downloads stay plain bytes). HTTP/1.1: HTTP/2 to Graph would multiplex everything on one
    /// connection, which helps little next to pooled connections and is untested behind proxies and inspecting firewalls.
    /// </summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        DefaultProxyCredentials = CredentialCache.DefaultCredentials,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        AutomaticDecompression = DecompressionMethods.None,
    };

    /// <summary>
    /// Wall time (throttle notices, Retry-After dates), monotonic time (<see cref="MonotonicClock"/>: pauses, release slots, the
    /// budget's refill, download URL reuse), randomness (0 to 1) and waiting; replaceable for tests.
    /// </summary>
    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;
    public Func<TimeSpan> Monotonic { get; init; } = MonotonicClock.Now;
    public Func<double> Random { get; init; } = System.Random.Shared.NextDouble;
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    /// <summary>
    /// The tenant's SharePoint root host (contoso.sharepoint.com), when known: tokens then go only to its hosts (-my and multi-geo
    /// included). <see cref="SharePointDiscoveryApi.GetMyDriveAsync"/> sets it when it is still empty.
    /// </summary>
    public string? TenantRootHost { get; set; }

    /// <summary>After a 429 or 503, background requests (<see cref="RequestPriority"/>) wait this long; foreground ones only for Retry-After.</summary>
    public TimeSpan ThrottlePause { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Response headers must arrive this long after a request is sent (a request body: after its last byte).</summary>
    public TimeSpan HeaderTimeout { get; init; } = TimeSpan.FromSeconds(100);

    /// <summary>
    /// A read of a response body that gets nothing this long fails, and so does a request body that makes no progress this long: a dead
    /// connection ends, a slow one that moves goes on however long it takes. Only time spent in a read counts.
    /// </summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public long GraphRequests => Interlocked.Read(ref graphRequests);

    /// <summary>When Graph or SharePoint last answered 429 or 503.</summary>
    public DateTimeOffset LastThrottled { get; private set; } = DateTimeOffset.MinValue;

    long lastThrottledTicks = long.MinValue;

    /// <summary>Whether Graph or SharePoint answered 429 or 503 within <paramref name="span"/>, on the monotonic clock.</summary>
    public bool ThrottledWithin(TimeSpan span) =>
        Interlocked.Read(ref lastThrottledTicks) is var at && at != long.MinValue && Monotonic() - TimeSpan.FromTicks(at) < span;

    /// <summary>
    /// Until when background requests wait, in wall time: <see cref="ThrottlePause"/> after throttling, or the reset of a nearly used
    /// app limit.
    /// </summary>
    public DateTimeOffset BackgroundPausedUntil
    {
        get
        {
            var until = BackgroundUntil();
            return until == TimeSpan.MinValue ? DateTimeOffset.MinValue : Clock() + (until - Monotonic());
        }
    }

    /// <summary>Whether background requests wait now; the engine skips starting background work meanwhile.</summary>
    public bool BackgroundPaused => BackgroundUntil() > Monotonic();

    TimeSpan BackgroundUntil()
    {
        lock (pauseLock)
        {
            return backgroundUntil > pausedUntil ? backgroundUntil : pausedUntil;
        }
    }

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

    /// <summary>Called for every throttled response (429 or 503).</summary>
    public Action<ThrottleNotice>? Throttled { get; set; }

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
        await using var stream = Decoded(response, await BodyAsync(response, uri, ct));
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    /// <summary>The body as sent: counted (wire bytes, so compressed ones) and with the stall timeout.</summary>
    async Task<Stream> BodyAsync(HttpResponseMessage response, Uri uri, CancellationToken ct) =>
        new BodyStream(await response.Content.ReadAsStreamAsync(ct), Received, StallTimeout, uri.Host);

    /// <summary>A gzip body decompressed, any other as it is. JSON only: downloads are never decoded (a range of gzip is no file).</summary>
    static Stream Decoded(HttpResponseMessage response, Stream body) =>
        response.Content.Headers.ContentEncoding.Contains("gzip", StringComparer.OrdinalIgnoreCase) ? new GZipStream(body, CompressionMode.Decompress) : body;

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
            var stream = await BodyAsync(response, uri, ct);
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

        string? claims = null; // a claims challenge's, for every later attempt
        var (challenged, refreshed, fresh, resent) = (false, false, false, false);
        for (var attempt = 1; ; attempt++)
        {
            await WaitWhilePausedAsync(priority, ct);
            if (budget is not null)
            {
                await budget.TakeAsync(cost, () => priority?.IsBackground ?? false, ct, priority?.Promoted);
                await WaitWhilePausedAsync(priority, ct); // a pause that began while this waited for the budget
            }
            Interlocked.Add(ref resourceUnits, cost);
            var body = content?.Invoke();
            using var request = new HttpRequestMessage(method, uri);
            Interlocked.Add(ref bytesSent, body?.Headers.ContentLength ?? 0);
            if (authenticate)
            {
                var token = claims is not null ? await tokens.GetAccessTokenAsync(resource, claims, ct)
                    : fresh ? await tokens.GetAccessTokenAsync(resource, NoClaims, ct)
                    : await tokens.GetAccessTokenAsync(resource, ct);
                fresh = false;
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.AcceptEncoding.Add(Gzip); // only JSON is authenticated; SendJsonAsync decodes it
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
            HttpResponseMessage response = null!;
            ExceptionDispatchInfo? failed = null;
            var resend = false;
            using (var deadline = new Deadline(ct, HeaderTimeout))
            {
                ProgressContent? progress = null;
                if (body is not null)
                {
                    // Each write of the body sets the stall timeout again; the header timeout starts after the last
                    request.Content = progress = new ProgressContent(body, () => deadline.Extend(StallTimeout), () => deadline.Extend(HeaderTimeout));
                }
                try
                {
                    response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                }
                catch (Exception e) when (!ct.IsCancellationRequested && e is OperationCanceledException or HttpRequestException)
                {
                    var where = $"{method.Method} {uri.Host}{uri.AbsolutePath}";
                    var ours = deadline.Token.IsCancellationRequested; // else the handler's own: its connect timeout
                    failed = ExceptionDispatchInfo.Capture(e switch
                    {
                        OperationCanceledException when !ours => new TaskCanceledException($"{where}: {TimeoutText(e)} (timed out)", new TimeoutException()),
                        OperationCanceledException when progress is { Sent: false } =>
                            new TaskCanceledException($"{where}: sending made no progress for {StallTimeout.TotalSeconds:N0} s (timed out)", new TimeoutException()),
                        OperationCanceledException => new TaskCanceledException($"{where}: no answer within {HeaderTimeout.TotalSeconds:N0} s (timed out)", new TimeoutException()),
                        _ => e,
                    });
                    resend = readOnly && Resendable(e, ours, priority);
                }
            }
            if (failed is not null)
            {
                Log?.Invoke($"{(failed.SourceException is TaskCanceledException ? "timeout" : "failed")} {method.Method} {uri.Host}{uri.AbsolutePath} {stopwatch.ElapsedMilliseconds} ms");
                if (resend && !resent && attempt < MaxAttempts)
                {
                    resent = true;
                    await Delay(Jitter(ResendDelay), ct);
                    continue;
                }
                failed.Throw();
            }
            Log?.Invoke($"{(int)response.StatusCode} {method.Method} {uri.Host}{uri.AbsolutePath} {stopwatch.ElapsedMilliseconds} ms");
            if (HeaderNumber(response, "x-ms-resource-unit") is { } charged && charged != cost)
            {
                // Transfer URLs too, when they say: estimated at 0 (unverified that they are free)
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
                if (response.StatusCode == HttpStatusCode.Unauthorized && authenticate)
                {
                    if (!challenged && ClaimsChallenge(response) is { } challenge)
                    {
                        // Continuous Access Evaluation: the token was revoked or no longer meets policy; get one with these claims and retry once
                        (challenged, claims) = (true, challenge);
                        attempt--;
                        continue;
                    }
                    if (!refreshed && claims is null)
                    {
                        // No challenge: the cached token may still be refused (revoked, expired early); one retry with a token asked for anew
                        (refreshed, fresh) = (true, true);
                        attempt--;
                        continue;
                    }
                }

                var status = (int)response.StatusCode;
                var throttled = status is 429 or 503;
                var retryAfter = RetryAfter(response, Clock());
                if (throttled)
                {
                    // Every request waits for the pause; background work longer (only UnwellPause after a 503 without Retry-After)
                    var pause = Jitter(retryAfter ?? Backoff(attempt));
                    var hold = status == 503 && retryAfter is null && UnwellPause < ThrottlePause ? UnwellPause : ThrottlePause;
                    var backgroundPause = hold > pause ? Jitter(hold) : pause;
                    LastThrottled = Clock();
                    Interlocked.Exchange(ref lastThrottledTicks, Monotonic().Ticks);
                    Interlocked.Increment(ref throttledResponses);
                    Interlocked.Add(ref throttledTicks, Pause(pause, backgroundPause).Ticks);
                    Throttled?.Invoke(new ThrottleNotice(LastThrottled, status, pause, isGraph, priority?.IsBackground ?? false, backgroundPause));
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

    /// <summary>
    /// Whether a read that failed on the way is sent once more: after a connection that dropped (a pooled one the server or a
    /// middlebox had closed), and for background work after no answer within <see cref="HeaderTimeout"/> (the next try gets another
    /// connection; a user's lookup rather fails now and answers from what is known). Not after a failed connect (offline, the proxy,
    /// a network that drops packets): another would fail the same way.
    /// </summary>
    static bool Resendable(Exception e, bool ourTimeout, PriorityHolder? priority) => e switch
    {
        HttpRequestException request => request.HttpRequestError is HttpRequestError.ResponseEnded or HttpRequestError.Unknown,
        _ => ourTimeout && (priority?.IsBackground ?? false),
    };

    /// <summary>The handler's own timeout in its words: the innermost TimeoutException's (HttpClient wraps it in one of its own).</summary>
    static string TimeoutText(Exception e)
    {
        var text = e.Message;
        for (var inner = e.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is TimeoutException)
            {
                text = inner.Message;
            }
        }
        return text;
    }

    /// <returns>How much longer every request is held now: the throttled time, so pauses that overlap count once.</returns>
    TimeSpan Pause(TimeSpan all, TimeSpan background)
    {
        lock (pauseLock)
        {
            var now = Monotonic();
            var longer = TimeSpan.Zero;
            if (now + all > pausedUntil)
            {
                longer = now + all - (pausedUntil > now ? pausedUntil : now);
                pausedUntil = now + all;
            }
            if (now + background > backgroundUntil)
            {
                backgroundUntil = now + background;
            }
            return longer;
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
                var now = Monotonic();
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

    /// <summary>
    /// Retry-After as a delay from <paramref name="now"/>, between 1 second and 5 minutes (0 or a date in the past would retry back to
    /// back); null when the response has none.
    /// </summary>
    public static TimeSpan? RetryAfter(HttpResponseMessage response, DateTimeOffset now)
    {
        var delay = response.Headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - now,
            _ => (TimeSpan?)null,
        };
        return delay is { } value ? TimeSpan.FromTicks(Math.Clamp(value.Ticks, MinRetryAfter.Ticks, MaxRetryAfter.Ticks)) : null;
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

    /// <summary>
    /// The start of an error response's body, decompressed (serviceReadOnly is found in it); a body that doesn't arrive leaves the
    /// status to speak for itself.
    /// </summary>
    async Task<string> ReadDetailAsync(HttpResponseMessage response, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(StallTimeout);
        try
        {
            await using var stream = Decoded(response, await response.Content.ReadAsStreamAsync(deadline.Token));
            using var reader = new StreamReader(stream);
            var body = await reader.ReadToEndAsync(deadline.Token);
            return body.Length > 300 ? body[..300] : body;
        }
        catch (Exception e) when (!ct.IsCancellationRequested && e is OperationCanceledException or HttpRequestException or IOException or InvalidDataException)
        {
            return $"(no details: {e.Message})";
        }
    }
}

/// <summary>
/// A response body: reports how many bytes were read through it, and a read that gets nothing for <paramref name="stall"/> fails
/// with a TaskCanceledException (a timeout), however long the whole body takes.
/// </summary>
sealed class BodyStream(Stream inner, Action<long> counted, TimeSpan stall, string host) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(stall);
        try
        {
            var read = await inner.ReadAsync(buffer, deadline.Token);
            counted(read);
            return read;
        }
        catch (OperationCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new TaskCanceledException($"{host}: no data for {stall.TotalSeconds:N0} s (timed out)", new TimeoutException(e.Message));
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

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
sealed class ResponseStream(HttpResponseMessage response, Stream inner) : Stream, IServedVersion
{
    /// <summary>The response's ETag, when the server sent one.</summary>
    public string? ETag => response.Headers.ETag?.Tag;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => inner.ReadAsync(buffer, offset, count, ct);
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

/// <summary>A token that cancels when the time set last runs out. Disposing it ends it without cancelling; later extensions do nothing.</summary>
sealed class Deadline : IDisposable
{
    readonly CancellationTokenSource source;
    readonly Lock gate = new();
    bool disposed;

    public Deadline(CancellationToken ct, TimeSpan after)
    {
        source = CancellationTokenSource.CreateLinkedTokenSource(ct);
        source.CancelAfter(after);
    }

    public CancellationToken Token => source.Token;

    public void Extend(TimeSpan after)
    {
        lock (gate)
        {
            if (!disposed)
            {
                source.CancelAfter(after);
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
        }
        source.Dispose();
    }
}

/// <summary>A request body sent in small writes: <c>progressed</c> after each, <c>sent</c> after the last.</summary>
sealed class ProgressContent : HttpContent
{
    const int Chunk = 16 * 1024;
    readonly HttpContent inner;
    readonly Action progressed;
    readonly Action sent;

    public ProgressContent(HttpContent inner, Action progressed, Action sent)
    {
        (this.inner, this.progressed, this.sent) = (inner, progressed, sent);
        foreach (var header in inner.Headers.Where(h => !h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)))
        {
            Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
    }

    /// <summary>Whether the whole body was written.</summary>
    public bool Sent { get; private set; }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
    {
        await using var source = await inner.ReadAsStreamAsync(ct);
        var buffer = new byte[Chunk];
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            await stream.WriteAsync(buffer.AsMemory(0, read), ct);
            progressed();
        }
        Sent = true;
        sent();
    }

    protected override bool TryComputeLength(out long length)
    {
        length = inner.Headers.ContentLength ?? -1;
        return length >= 0;
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
