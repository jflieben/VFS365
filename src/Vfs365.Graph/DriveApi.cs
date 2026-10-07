using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Vfs365.Core.Drive;

namespace Vfs365.Graph;

/// <summary>driveItem calls by path. Items that are neither file nor folder (OneNote packages) are left out.</summary>
public sealed partial class DriveApi(M365Client client) : IDriveApi
{
    const string Select = "id,name,size,folder,file,fileSystemInfo,createdDateTime,lastModifiedDateTime,eTag,cTag";
    const string DeltaSelect = Select + ",parentReference,deleted,root";

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
        using var doc = await client.GetJsonAsync(new Uri($"{ItemUrl(driveId, path)}?$select={Select}"), ct, notFoundAsNull: true);
        return doc is null ? null : Parse(doc.RootElement);
    }

    /// <summary>Asks for a fresh pre-authenticated download URL per stream; it is never kept.</summary>
    public async Task<Stream> OpenReadAsync(string driveId, string itemId, long offset, CancellationToken ct)
    {
        string url;
        using (var doc = await client.GetJsonAsync(new Uri($"{DriveUrl(driveId)}/items/{itemId}"), ct))
        {
            url = doc!.RootElement.GetProperty("@microsoft.graph.downloadUrl").GetString()!;
        }
        return await client.OpenStreamAsync(new Uri(url), offset, ct);
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

        var session = await CreateUploadSessionAsync(driveId, target, ct);
        for (long offset = 0; ; offset += UploadSession.FragmentSize)
        {
            if (await UploadFragmentAsync(session, sourceFile, offset, Math.Min(UploadSession.FragmentSize, length - offset), length, ct) is { } item)
            {
                return item;
            }
        }
    }

    public async Task<UploadSession> CreateUploadSessionAsync(string driveId, UploadTarget target, CancellationToken ct)
    {
        var body = new Dictionary<string, object>
        {
            ["item"] = new Dictionary<string, object> { ["@microsoft.graph.conflictBehavior"] = target.ItemId is null ? "fail" : "replace" },
        };
        using var session = await client.SendJsonAsync(HttpMethod.Post, new Uri($"{TargetUrl(driveId, target)}/createUploadSession"), Json(body), ct, target.IfMatch);
        return new UploadSession(new Uri(session!.RootElement.GetProperty("uploadUrl").GetString()!));
    }

    public async Task<DriveItemInfo?> UploadFragmentAsync(UploadSession session, string sourceFile, long offset, long length, long totalSize, CancellationToken ct)
    {
        using var response = await client.SendJsonAsync(HttpMethod.Put, session.UploadUrl, () =>
        {
            var fragment = FileContent(sourceFile, offset, length);
            fragment.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + length - 1, totalSize);
            return fragment;
        }, ct, authenticate: false);
        return offset + length >= totalSize ? Parse(response!.RootElement) : null;
    }

    public async Task CancelUploadSessionAsync(UploadSession session, CancellationToken ct)
    {
        using var _ = await client.SendJsonAsync(HttpMethod.Delete, session.UploadUrl, null, ct, authenticate: false, notFoundAsNull: true);
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
        var bytes = new byte[length];
        using (var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
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
        }
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return content;
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
            Str(item, "cTag"));
    }

    static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    static DateTimeOffset? Time(JsonElement element, string name) =>
        Str(element, name) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time) ? time : null;
}
