using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Vfs365.Core;
using Vfs365.Core.Drive;

namespace Vfs365.Graph;

/// <summary>driveItem calls by path. Items that are neither file nor folder (OneNote packages) are left out.</summary>
public sealed partial class DriveApi(M365Client client) : IDriveApi
{
    const string Select = "id,name,size,folder,file,fileSystemInfo,createdDateTime,lastModifiedDateTime,eTag,cTag";
    const string DeltaSelect = Select + ",parentReference,deleted,root";

    /// <summary>A single item also brings its parent's ID (not asked for in listings: it is the folder listed, and big).</summary>
    const string ItemSelect = Select + ",parentReference";

    /// <summary>
    /// The first page is small so a big folder starts showing quickly (Graph's time grows with the page); the rest come 1000 at a time
    /// by raising $top in the next links.
    /// </summary>
    public async IAsyncEnumerable<IReadOnlyList<DriveItemInfo>> ListChildrenAsync(string driveId, string folderPath, [EnumeratorCancellation] CancellationToken ct)
    {
        Uri? uri = new($"{ChildrenUrl(driveId, folderPath)}?$select={Select}&$top={FirstPageSize}");
        while (uri is not null)
        {
            var items = new List<DriveItemInfo>();
            using (var doc = (await client.GetJsonAsync(uri, ct))!)
            {
                foreach (var element in doc.RootElement.GetProperty("value").EnumerateArray())
                {
                    if (Parse(element) is { } item)
                    {
                        items.Add(item);
                    }
                }
                uri = Str(doc.RootElement, "@odata.nextLink") is { } link ? new Uri(LargerPages().Replace(link, "${1}1000")) : null;
            }
            yield return items;
        }
    }

    const int FirstPageSize = 200;

    [System.Text.RegularExpressions.GeneratedRegex(@"([?&](?:\$|%24)top=)200\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex LargerPages();

    public async Task<string> GetLatestDeltaLinkAsync(string driveId, CancellationToken ct)
    {
        using var doc = (await client.GetJsonAsync(new Uri($"{DriveUrl(driveId)}/root/delta?token=latest&$select={DeltaSelect}"), ct))!;
        return Str(doc.RootElement, "@odata.deltaLink") ?? throw new InvalidOperationException("No deltaLink in the delta response");
    }

    public async Task<DeltaPage> GetDeltaAsync(string link, CancellationToken ct)
    {
        using var doc = (await client.GetJsonAsync(new Uri(link), ct))!;
        var changes = new List<DriveChange>();
        foreach (var element in doc.RootElement.GetProperty("value").EnumerateArray())
        {
            if (Str(element, "id") is not { } id || element.TryGetProperty("root", out _))
            {
                continue;
            }
            var parentId = element.TryGetProperty("parentReference", out var parent) ? Str(parent, "id") : null;
            var deleted = element.TryGetProperty("deleted", out _);
            changes.Add(new DriveChange(id, parentId, deleted ? null : Parse(element), deleted));
        }
        return new DeltaPage(changes, Str(doc.RootElement, "@odata.nextLink"), Str(doc.RootElement, "@odata.deltaLink"));
    }

    public async Task<DriveItemInfo?> GetItemAsync(string driveId, string path, CancellationToken ct)
    {
        using var doc = await client.GetJsonAsync(new Uri($"{ItemUrl(driveId, path)}?$select={ItemSelect}"), ct, notFoundAsNull: true);
        return doc is null ? null : Parse(doc.RootElement);
    }

    public async Task<DriveItemInfo?> GetItemByIdAsync(string driveId, string itemId, CancellationToken ct)
    {
        using var doc = await client.GetJsonAsync(new Uri($"{DriveUrl(driveId)}/items/{itemId}?$select={ItemSelect}"), ct, notFoundAsNull: true);
        return doc is null ? null : Parse(doc.RootElement);
    }

    /// <summary>
    /// Item fields for a download plus the pre-authenticated URL. Graph returns the URL under $select only when it is named; an answer
    /// without it switches to reading the whole item.
    /// </summary>
    const string DownloadSelect = Select + ",@microsoft.graph.downloadUrl";

    /// <summary>
    /// A download URL is reused this briefly for the same item version (it is valid for about an hour), so the range streams of one
    /// download share it, while a later one reads the item again and so sees a colleague's new version. Never stored or logged.
    /// </summary>
    static readonly TimeSpan DownloadUrlReuse = TimeSpan.FromSeconds(20);

    readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Url, DriveItemInfo Item, DateTimeOffset At)> downloadUrls = new(StringComparer.Ordinal);
    volatile bool selectDropsDownloadUrl;

    /// <summary>
    /// Streams the version <paramref name="contentTag"/> names. The item is read first (unless a download URL for that version was read
    /// seconds ago), so a changed file fails with <see cref="ContentChangedException"/> instead of mixing versions in one download; so
    /// does a download whose ETag names another version.
    /// </summary>
    public async Task<Stream> OpenReadAsync(string driveId, string itemId, string? contentTag, long offset, long length, CancellationToken ct)
    {
        var key = $"{driveId}|{itemId}";
        if (contentTag is not null && downloadUrls.TryGetValue(key, out var known) && client.Clock() - known.At < DownloadUrlReuse
            && (contentTag == known.Item.CTag || contentTag == known.Item.ETag))
        {
            try
            {
                return await DownloadAsync(known.Url, known.Item, offset, length, ct);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                downloadUrls.TryRemove(key, out _); // expired, or the file changed: read the item again
            }
        }

        var (url, current) = await DownloadUrlAsync(driveId, itemId, ct);
        if (ContentTags.Differs(contentTag, current))
        {
            downloadUrls.TryRemove(key, out _);
            throw new ContentChangedException(current, length, otherVersion: true);
        }
        if (downloadUrls.Count > 256)
        {
            foreach (var old in downloadUrls.Where(d => client.Clock() - d.Value.At >= DownloadUrlReuse).Select(d => d.Key).ToList())
            {
                downloadUrls.TryRemove(old, out _);
            }
        }
        downloadUrls[key] = (url, current, client.Clock());
        try
        {
            return await DownloadAsync(url, current, offset, length, ct);
        }
        catch (ContentChangedException)
        {
            downloadUrls.TryRemove(key, out _);
            throw;
        }
    }

    async Task<(string Url, DriveItemInfo Item)> DownloadUrlAsync(string driveId, string itemId, CancellationToken ct)
    {
        while (true)
        {
            var selected = !selectDropsDownloadUrl;
            using var doc = (await client.GetJsonAsync(new Uri($"{DriveUrl(driveId)}/items/{itemId}{(selected ? $"?$select={DownloadSelect}" : "")}"), ct))!;
            if (Str(doc.RootElement, "@microsoft.graph.downloadUrl") is { } url)
            {
                return (url, Parse(doc.RootElement) ?? throw new IOException($"Item {itemId} is no file"));
            }
            if (!selected)
            {
                throw new IOException($"No download URL for item {itemId}");
            }
            selectDropsDownloadUrl = true;
        }
    }

    async Task<Stream> DownloadAsync(string url, DriveItemInfo current, long offset, long length, CancellationToken ct)
    {
        (Stream Content, long? Total) download;
        try
        {
            download = await client.OpenStreamAsync(new Uri(url), offset, ct);
        }
        catch (M365RequestException e) when (e.Status == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            throw new ContentChangedException(current, length); // the offset is past the end of what is there now
        }
        if (download.Content is IServedVersion { ETag: { } tag } && ContentTags.Differs(tag, current))
        {
            await download.Content.DisposeAsync();
            throw new ContentChangedException(current, length, otherVersion: true);
        }
        if (download.Total is { } served && served != length)
        {
            await download.Content.DisposeAsync();
            throw new ContentChangedException(current with { Size = served }, length);
        }
        return download.Content;
    }

    public async Task<DriveItemInfo> UploadAsync(string driveId, UploadTarget target, string sourceFile, CancellationToken ct)
    {
        var length = new FileInfo(sourceFile).Length;
        if (length <= UploadSession.SimpleUploadLimit)
        {
            var query = target.ItemId is null ? "?@microsoft.graph.conflictBehavior=fail" : "";
            using var doc = await client.SendJsonAsync(HttpMethod.Put, new Uri($"{TargetUrl(driveId, target)}/content{query}"), () => FileContent(sourceFile, 0, length), ct, target.IfMatch);
            return Parse(doc!.RootElement)!;
        }

        for (var attempt = 1; ; attempt++)
        {
            var session = await CreateUploadSessionAsync(driveId, target, ct);
            try
            {
                return await SendFragmentsAsync(session, sourceFile, length, ct);
            }
            catch (UploadSessionGoneException) when (attempt < 2 && !ct.IsCancellationRequested)
            {
                // Expired on the server (a long stall or sleep): a new session starts over
            }
            catch (Exception)
            {
                await CancelQuietlyAsync(session);
                throw;
            }
        }
    }

    /// <summary>A fragment slower than this halves the next one (down to 320 KiB); one faster than a quarter of it doubles it again.</summary>
    static readonly TimeSpan SlowFragment = TimeSpan.FromSeconds(30);

    /// <summary>Sends the whole file through the session, fragment by fragment, smaller ones while the uplink is slow.</summary>
    async Task<DriveItemInfo> SendFragmentsAsync(UploadSession session, string sourceFile, long length, CancellationToken ct)
    {
        var size = UploadSession.FragmentSize;
        for (long offset = 0; ;)
        {
            var fragment = Math.Min(size, length - offset);
            var started = client.Clock();
            if (await UploadFragmentAsync(session, sourceFile, offset, fragment, length, ct) is { } item)
            {
                return item;
            }
            offset += fragment;
            var took = client.Clock() - started;
            size = took > SlowFragment ? Math.Max(UploadSession.FragmentUnit, size / 2 / UploadSession.FragmentUnit * UploadSession.FragmentUnit)
                : took < SlowFragment / 4 ? Math.Min(UploadSession.FragmentSize, size * 2)
                : size;
        }
    }

    public async Task<UploadSession> CreateUploadSessionAsync(string driveId, UploadTarget target, CancellationToken ct)
    {
        var body = new Dictionary<string, object>
        {
            ["item"] = new Dictionary<string, object> { ["@microsoft.graph.conflictBehavior"] = target.ItemId is null ? "fail" : "replace" },
        };
        using var session = await client.SendJsonAsync(HttpMethod.Post, new Uri($"{TargetUrl(driveId, target)}/createUploadSession"), Json(body), ct, target.IfMatch);
        return new UploadSession(new Uri(session!.RootElement.GetProperty("uploadUrl").GetString()!)) { DriveId = driveId, Target = target };
    }

    /// <summary>A fragment PUT that failed on the way (network, timeout, 5xx) is sent again this often, from where the session says it is.</summary>
    const int FragmentRetries = 4;

    /// <summary>
    /// Sends one fragment. A failure on the way is retried with back-off from the session's nextExpectedRanges (a fragment that
    /// arrived is not sent again). A session gone on the server (expired) throws <see cref="UploadSessionGoneException"/>.
    /// </summary>
    public async Task<DriveItemInfo?> UploadFragmentAsync(UploadSession session, string sourceFile, long offset, long length, long totalSize, CancellationToken ct)
    {
        var end = offset + length;
        var last = end >= totalSize;
        if (last)
        {
            await CheckUnchangedAsync(session, ct);
        }
        var bytes = ReadFragment(sourceFile, offset, length);
        for (long from = offset, failures = 0; ;)
        {
            try
            {
                var start = from;
                using var response = await client.SendJsonAsync(HttpMethod.Put, session.UploadUrl, () =>
                {
                    var fragment = new ByteArrayContent(bytes, (int)(start - offset), (int)(end - start));
                    fragment.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    fragment.Headers.ContentRange = new ContentRangeHeaderValue(start, end - 1, totalSize);
                    return fragment;
                }, ct, authenticate: false);
                return last ? Parse(response!.RootElement) : null;
            }
            catch (M365RequestException e) when (e.Status is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                throw new UploadSessionGoneException($"The upload session ended on the server: {e.Message}");
            }
            catch (Exception e) when (!ct.IsCancellationRequested && Resumable(e) && ++failures <= FragmentRetries)
            {
                await client.Delay(TimeSpan.FromSeconds(1 << (int)failures), ct);
                var next = await NextExpectedAsync(session, ct);
                if (next >= end && !last)
                {
                    return null; // it arrived, only the answer got lost
                }
                if (next < offset || next >= end)
                {
                    throw;
                }
                from = next;
            }
        }
    }

    /// <summary>Network trouble, timeouts, 5xx and throttling without Retry-After, a range the session didn't expect: worth resuming.</summary>
    static bool Resumable(Exception e) => e switch
    {
        M365RequestException request => request.Error is RemoteError.Unavailable or RemoteError.Throttled || request.Status == HttpStatusCode.RequestedRangeNotSatisfiable,
        RemoteException => false,
        _ => e is HttpRequestException or IOException or OperationCanceledException,
    };

    /// <summary>The first byte the session still expects (nextExpectedRanges).</summary>
    async Task<long> NextExpectedAsync(UploadSession session, CancellationToken ct)
    {
        using var status = await client.SendJsonAsync(HttpMethod.Get, session.UploadUrl, null, ct, notFoundAsNull: true, authenticate: false)
            ?? throw new UploadSessionGoneException("The upload session ended on the server");
        if (status.RootElement.TryGetProperty("nextExpectedRanges", out var ranges) && ranges.ValueKind == JsonValueKind.Array
            && ranges.EnumerateArray().FirstOrDefault().GetString() is { } range
            && long.TryParse(range.Split('-')[0], NumberStyles.None, CultureInfo.InvariantCulture, out var next))
        {
            return next;
        }
        throw new IOException("The upload session didn't say what it expects next");
    }

    /// <summary>
    /// Before the fragment that completes an upload into an existing item: the session checked If-Match only when it began, so an item
    /// whose content changed (or that was deleted) since fails it instead of being replaced; the caller cancels the session. A change
    /// of metadata only passes.
    /// </summary>
    async Task CheckUnchangedAsync(UploadSession session, CancellationToken ct)
    {
        if (session is not { DriveId: { } driveId, Target: { ItemId: { } itemId, IfMatch: { } ifMatch } target })
        {
            return;
        }
        var current = await GetItemByIdAsync(driveId, itemId, ct);
        if (current is not null && (current.ETag == ifMatch || (target.ContentTag is not null && current.CTag == target.ContentTag)))
        {
            return;
        }
        throw current is null
            ? new RemoteException(RemoteError.NotFound, $"Item {itemId} was deleted in Microsoft 365 during the upload")
            : new RemoteException(RemoteError.Conflict, $"412 item {itemId} changed in Microsoft 365 during the upload");
    }

    public async Task CancelUploadSessionAsync(UploadSession session, CancellationToken ct)
    {
        using var _ = await client.SendJsonAsync(HttpMethod.Delete, session.UploadUrl, null, ct, authenticate: false, notFoundAsNull: true);
    }

    async Task CancelQuietlyAsync(UploadSession session)
    {
        try
        {
            await CancelUploadSessionAsync(session, CancellationToken.None);
        }
        catch (Exception)
        {
        }
    }

    static string TargetUrl(string driveId, UploadTarget target) =>
        target.ItemId is { } id ? $"{DriveUrl(driveId)}/items/{id}" : $"{ItemUrl(driveId, JoinPath(target.ParentPath!, target.Name!))}:";

    public async Task<DriveItemInfo> CreateFolderAsync(string driveId, string parentPath, string name, CancellationToken ct)
    {
        var body = new Dictionary<string, object> { ["name"] = name, ["folder"] = new { }, ["@microsoft.graph.conflictBehavior"] = "fail" };
        using var doc = await client.SendJsonAsync(HttpMethod.Post, new Uri(ChildrenUrl(driveId, parentPath)), Json(body), ct);
        return Parse(doc!.RootElement)!;
    }

    public async Task<DriveItemInfo> MoveAsync(string driveId, string itemId, string newParentId, string newName, CancellationToken ct)
    {
        var body = new Dictionary<string, object> { ["name"] = newName, ["parentReference"] = new Dictionary<string, object> { ["id"] = newParentId } };
        using var doc = await client.SendJsonAsync(HttpMethod.Patch, new Uri($"{DriveUrl(driveId)}/items/{itemId}"), Json(body), ct);
        return Parse(doc!.RootElement)!;
    }

    public async Task DeleteAsync(string driveId, string itemId, CancellationToken ct)
    {
        using var _ = await client.SendJsonAsync(HttpMethod.Delete, new Uri($"{DriveUrl(driveId)}/items/{itemId}"), null, ct);
    }

    /// <summary>A file's version history, newest first (for diagnostics).</summary>
    public async Task<IReadOnlyList<(string Id, DateTimeOffset Modified, long Size)>> ListVersionsAsync(string driveId, string itemId, CancellationToken ct)
    {
        var versions = new List<(string, DateTimeOffset, long)>();
        Uri? uri = new($"{DriveUrl(driveId)}/items/{itemId}/versions?$select=id,lastModifiedDateTime,size");
        while (uri is not null)
        {
            using var doc = (await client.GetJsonAsync(uri, ct))!;
            foreach (var version in doc.RootElement.GetProperty("value").EnumerateArray())
            {
                versions.Add((Str(version, "id")!, Time(version, "lastModifiedDateTime") ?? DateTimeOffset.UnixEpoch,
                    version.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Number ? size.GetInt64() : 0));
            }
            uri = doc.RootElement.TryGetProperty("@odata.nextLink", out var next) && next.GetString() is { } link ? new Uri(link) : null;
        }
        return versions;
    }

    static string DriveUrl(string driveId) => $"{M365Client.GraphResource}/v1.0/drives/{driveId}";

    static string ItemUrl(string driveId, string path) => path.Length == 0
        ? $"{DriveUrl(driveId)}/root"
        : $"{DriveUrl(driveId)}/root:/{string.Join('/', path.Split('/').Select(Uri.EscapeDataString))}";

    static string ChildrenUrl(string driveId, string folderPath) =>
        folderPath.Length == 0 ? $"{DriveUrl(driveId)}/root/children" : $"{ItemUrl(driveId, folderPath)}:/children";

    static string JoinPath(string parent, string name) => parent.Length == 0 ? name : $"{parent}/{name}";

    static Func<HttpContent> Json(object body) => () => new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    static ByteArrayContent FileContent(string path, long offset, long length)
    {
        var content = new ByteArrayContent(ReadFragment(path, offset, length));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return content;
    }

    static byte[] ReadFragment(string path, long offset, long length)
    {
        var bytes = new byte[length];
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var read = 0;
        while (read < length)
        {
            var n = RandomAccess.Read(handle, bytes.AsSpan(read), offset + read);
            if (n == 0)
            {
                throw new IOException($"{path} shrank while uploading");
            }
            read += n;
        }
        return bytes;
    }

    public static DriveItemInfo? Parse(JsonElement item)
    {
        var isFolder = item.TryGetProperty("folder", out _);
        if (!isFolder && !item.TryGetProperty("file", out _))
        {
            return null;
        }

        item.TryGetProperty("fileSystemInfo", out var fileSystem);
        return new DriveItemInfo(
            Str(item, "id")!,
            Str(item, "name")!,
            isFolder,
            item.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Number ? size.GetInt64() : 0,
            Time(fileSystem, "createdDateTime") ?? Time(item, "createdDateTime") ?? DateTimeOffset.UnixEpoch,
            Time(fileSystem, "lastModifiedDateTime") ?? Time(item, "lastModifiedDateTime") ?? DateTimeOffset.UnixEpoch,
            Str(item, "eTag"),
            Str(item, "cTag"),
            item.TryGetProperty("parentReference", out var parent) ? Str(parent, "id") : null);
    }

    static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    static DateTimeOffset? Time(JsonElement element, string name) =>
        Str(element, name) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time) ? time : null;
}

/// <summary>An upload session SharePoint no longer knows (expired): the upload starts over. Not a deleted item.</summary>
public sealed class UploadSessionGoneException(string message) : RemoteException(RemoteError.Unavailable, message);

/// <summary>A download stream that knows the ETag its response named, when the server sent one.</summary>
internal interface IServedVersion
{
    string? ETag { get; }
}
