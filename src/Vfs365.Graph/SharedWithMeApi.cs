using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Vfs365.Core.Discovery;

namespace Vfs365.Graph;

/// <summary>
/// The shared list's calls: Graph search for driveItems shared with the user (/me/drive/sharedWithMe is retired after November 2026),
/// the item by ID for its path in the owner's drive, and SharePoint REST for the user's rights on it.
/// </summary>
public sealed class SharedWithMeApi(M365Client client) : ISharedWithMeApi
{
    public async Task<SharedSearchPage> SearchAsync(string user, int from, int size, CancellationToken ct)
    {
        using var doc = (await client.SendJsonAsync(HttpMethod.Post, new Uri($"{M365Client.GraphResource}/v1.0/search/query"),
            () => new StringContent(SearchBody(user, from, size), Encoding.UTF8, "application/json"), ct))!;
        return ParseSearch(doc.RootElement);
    }

    /// <summary>The query: driveItems whose SharedWithUsersOWSUSER holds the user.</summary>
    public static string SearchBody(string user, int from, int size) => JsonSerializer.Serialize(new
    {
        requests = new[]
        {
            new
            {
                entityTypes = new[] { "driveItem" },
                query = new { queryString = $"SharedWithUsersOWSUSER:\"{user.Replace("\"", "")}\"" },
                from,
                size,
            },
        },
    });

    /// <summary>Hits that are files or folders (not OneNote packages and the like), with their drive and owner.</summary>
    public static SharedSearchPage ParseSearch(JsonElement root)
    {
        var items = new List<SharedItem>();
        var (more, hits) = (false, 0);
        foreach (var response in Array(root, "value"))
        {
            foreach (var container in Array(response, "hitsContainers"))
            {
                more |= container.TryGetProperty("moreResultsAvailable", out var m) && m.ValueKind == JsonValueKind.True;
                foreach (var hit in Array(container, "hits"))
                {
                    hits++;
                    if (!hit.TryGetProperty("resource", out var resource) || Str(resource, "id") is not { } id || Str(resource, "name") is not { } name
                        || !resource.TryGetProperty("parentReference", out var parent) || Str(parent, "driveId") is not { } driveId)
                    {
                        continue;
                    }
                    var isFolder = resource.TryGetProperty("folder", out _);
                    if (!isFolder && !resource.TryGetProperty("file", out _))
                    {
                        continue;
                    }
                    var (ownerName, ownerEmail) = Person(resource, "createdBy") is { Name: not null } created ? created : Person(resource, "lastModifiedBy");
                    items.Add(new SharedItem(driveId, id, name, isFolder,
                        OwnerName: ownerName,
                        OwnerEmail: ownerEmail,
                        Size: resource.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes) ? bytes : 0,
                        Modified: Time(resource, "lastModifiedDateTime") ?? default,
                        WebUrl: Str(resource, "webUrl"),
                        ParentId: Str(parent, "id")));
                }
            }
        }
        return new SharedSearchPage(items, more, hits);
    }

    /// <summary>parentReference.path is "/drives/{id}/root:/a/b" (or "/drive/root:"), also when the user can't open the parent.</summary>
    public async Task<SharedLocation?> GetLocationAsync(string driveId, string itemId, CancellationToken ct)
    {
        using var doc = await client.GetJsonAsync(new Uri($"{M365Client.GraphResource}/v1.0/drives/{driveId}/items/{itemId}?$select=id,name,folder,file,parentReference,sharepointIds"),
            ct, notFoundAsNull: true);
        if (doc is null)
        {
            return null;
        }
        var item = doc.RootElement;
        var name = Str(item, "name")!;
        var parent = item.TryGetProperty("parentReference", out var reference) ? reference : default;
        var parentPath = ParentPath(Str(parent, "path")) ?? throw new InvalidDataException($"No path for item {itemId} in drive {driveId}");
        var sp = item.TryGetProperty("sharepointIds", out var ids) ? ids : default;
        return new SharedLocation(parentPath.Length == 0 ? name : $"{parentPath}/{name}", name, Str(parent, "id"), item.TryGetProperty("folder", out _),
            Str(sp, "siteUrl"), Str(sp, "listId"), Str(sp, "listItemId"));
    }

    /// <summary>The part after "root:", unescaped, without leading slash ("" for the root); null when it has none.</summary>
    public static string? ParentPath(string? path)
    {
        if (path is null || path.IndexOf("root:", StringComparison.Ordinal) is not (var at and >= 0))
        {
            return null;
        }
        return Uri.UnescapeDataString(path[(at + 5)..]).Trim('/');
    }

    /// <summary>
    /// The item's EffectiveBasePermissions from SharePoint (its list item, found by the IDs Graph gives): EditListItems (0x4 in Low)
    /// means it can be changed, and for a folder AddListItems (0x2) that files can be added. Null when that can't be read.
    /// </summary>
    public async Task<bool?> CanEditAsync(SharedLocation location, CancellationToken ct)
    {
        if (location is not { SiteUrl: { } siteUrl, ListId: { } listId, ListItemId: { } listItemId })
        {
            return null;
        }
        try
        {
            using var doc = await client.GetJsonAsync(
                new Uri($"{siteUrl.TrimEnd('/')}/_api/web/lists(guid'{listId}')/items({listItemId})?$select=EffectiveBasePermissions"), ct, notFoundAsNull: true);
            return doc is not null && Low(doc.RootElement) is { } low ? (low & 0x4) != 0 && (!location.IsFolder || (low & 0x2) != 0) : null;
        }
        catch (M365RequestException e) when (e.Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null; // a host the client sends no token to (another geo it doesn't know)
        }
    }

    /// <summary>EffectiveBasePermissions.Low, a string or a number depending on the OData format.</summary>
    public static ulong? Low(JsonElement item)
    {
        if (!item.TryGetProperty("EffectiveBasePermissions", out var permissions) || permissions.ValueKind != JsonValueKind.Object
            || !permissions.TryGetProperty("Low", out var low))
        {
            return null;
        }
        return low.ValueKind == JsonValueKind.Number && low.TryGetUInt64(out var number) ? number
            : low.ValueKind == JsonValueKind.String && ulong.TryParse(low.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed
            : null;
    }

    static (string? Name, string? Email) Person(JsonElement item, string property) =>
        item.TryGetProperty(property, out var by) && by.TryGetProperty("user", out var user) ? (Str(user, "displayName"), Str(user, "email")) : (null, null);

    static IEnumerable<JsonElement> Array(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];

    static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    static DateTimeOffset? Time(JsonElement element, string name) =>
        Str(element, name) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time) ? time : null;
}
