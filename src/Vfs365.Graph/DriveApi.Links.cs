using System.Text.Json;

namespace Vfs365.Graph;

/// <summary>
/// Where an item lives on the web, for Explorer's VFS365 menu: WebUrl opens it in the browser, WebDavUrl is its file URL (for Office's
/// URI schemes), SiteUrl, ListId and ListItemId (sharepointIds) name it for SharePoint's own pages.
/// </summary>
public sealed record ItemLinks(string? WebUrl, string? WebDavUrl, string? SiteUrl, string? ListId, string? ListItemId);

public sealed partial class DriveApi
{
    const string LinksSelect = "id,webUrl,webDavUrl,sharepointIds";

    /// <summary>One GET for an item's links; null when it no longer exists.</summary>
    public async Task<ItemLinks?> GetLinksAsync(string driveId, string itemId, CancellationToken ct)
    {
        using var doc = await client.GetJsonAsync(new Uri($"{DriveUrl(driveId)}/items/{itemId}?$select={LinksSelect}"), ct, notFoundAsNull: true);
        return doc is null ? null : ParseLinks(doc.RootElement);
    }

    public static ItemLinks ParseLinks(JsonElement item)
    {
        var ids = item.TryGetProperty("sharepointIds", out var found) ? found : default;
        return new ItemLinks(Str(item, "webUrl"), Str(item, "webDavUrl"), Str(ids, "siteUrl"), Str(ids, "listId"), Str(ids, "listItemId"));
    }
}
