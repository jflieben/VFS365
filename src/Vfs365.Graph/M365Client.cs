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
/// HTTP for Graph and SharePoint REST: token per resource, User-Agent, retries, one shared pause when throttled, this user's request
/// budget (<see cref="RequestBudget"/>), usage counts.
/// </summary>
public sealed class M365Client(HttpClient http, ITokenSource tokens, RequestBudget? budget = null)
{
    public const string GraphResource = "https://graph.microsoft.com";
    const int MaxAttempts = 5;

    readonly Lock pauseLock = new();
    DateTimeOffset pausedUntil = DateTimeOffset.MinValue;
    long graphRequests;
    long sharePointRequests;
    long resourceUnits;
    long bytesReceived;
    long bytesSent;
    long throttledResponses;
    long throttledTicks;

    public long GraphRequests => Interlocked.Read(ref graphRequests);

    /// <summary>When Graph or SharePoint last answered 429 or 503.</summary>
    public DateTimeOffset LastThrottled { get; private set; } = DateTimeOffset.MinValue;
    public long SharePointRequests => Interlocked.Read(ref sharePointRequests);

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
        var resource = isGraph ? GraphResource : $"{uri.Scheme}://{uri.Host}";
        var cost = ResourceUnits.Estimate(method, uri, authenticate);

        string? claims = null;
        for (var attempt = 1; ; attempt++)
        {
            await WaitWhilePausedAsync(ct);
            if (budget is not null)
            {
                await budget.TakeAsync(cost, RequestPriority.IsBackground, ct);
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
                if (throttled)
                {
                    LastThrottled = DateTimeOffset.UtcNow;
                }
                if (!(throttled || status is 500 or 502 or 504) || attempt == MaxAttempts)
                {
                    throw new M365RequestException(response.StatusCode, uri, await ReadDetailAsync(response, ct));
                }

                var delay = RetryAfter(response) ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
                if (throttled)
                {
                    Interlocked.Increment(ref throttledResponses);
                    Interlocked.Add(ref throttledTicks, delay.Ticks);
                    Pause(delay);
                }
                else
                {
                    await Task.Delay(delay, ct);
                }
            }
        }
    }

    void Pause(TimeSpan delay)
    {
        lock (pauseLock)
        {
            var until = DateTimeOffset.UtcNow + delay;
            if (until > pausedUntil)
            {
                pausedUntil = until;
            }
        }
    }

    async Task WaitWhilePausedAsync(CancellationToken ct)
    {
        while (true)
        {
            TimeSpan remaining;
            lock (pauseLock)
            {
                remaining = pausedUntil - DateTimeOffset.UtcNow;
            }
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }
            await Task.Delay(remaining, ct);
        }
    }

    static TimeSpan? RetryAfter(HttpResponseMessage response) => response.Headers.RetryAfter switch
    {
        { Delta: { } delta } => delta,
        { Date: { } date } => date - DateTimeOffset.UtcNow,
        _ => null,
    };

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