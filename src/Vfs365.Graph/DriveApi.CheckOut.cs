using System.Collections.Concurrent;
using System.Text.Json;
using Vfs365.Core.Drive;

namespace Vfs365.Graph;

/// <summary>
/// A saved file with empty required columns (<paramref name="Columns"/>, display names), so the library wants details from the user.
/// <paramref name="StillCheckedOut"/>: SharePoint left it checked out and it couldn't be checked in (<paramref name="Error"/>): colleagues
/// see the previous version. No columns: a check-out whose cause couldn't be read.
/// </summary>
public sealed record FileNeedsDetails(string DriveId, string ItemId, string Name, IReadOnlyList<string> Columns, bool StillCheckedOut = false, string? Error = null);

/// <summary>What a library requires, per content type of its documents.</summary>
public sealed record RequiredColumns(IReadOnlyDictionary<string, IReadOnlyList<RequiredColumn>> ByContentType)
{
    public bool Any => ByContentType.Values.Any(columns => columns.Count > 0);

    /// <summary>The Graph answer for a list's content types with their columns. A folder's aren't documents (their ID starts 0x0120).</summary>
    public static RequiredColumns Parse(JsonElement contentTypes) =>
        new(contentTypes.EnumerateArray()
            .Where(type => type.GetProperty("id").GetString()!.StartsWith("0x0101", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(type => type.GetProperty("id").GetString()!, type => In(type.GetProperty("columns")), StringComparer.OrdinalIgnoreCase));

    /// <summary>The columns a user has to fill: required, not hidden, not read-only, and not the file's name, which always has a value.</summary>
    public static IReadOnlyList<RequiredColumn> In(JsonElement columns) =>
        [.. columns.EnumerateArray()
            .Where(c => Flag(c, "required") && !Flag(c, "hidden") && !Flag(c, "readOnly") && c.GetProperty("name").GetString() != "FileLeafRef")
            .Select(c => new RequiredColumn(c.GetProperty("name").GetString()!, c.TryGetProperty("displayName", out var shown) ? shown.GetString()! : c.GetProperty("name").GetString()!))];

    /// <summary>
    /// Display names of the columns without a value in an item's fields. Graph lists an empty column not at all, and a person or lookup
    /// column as its name plus LookupId.
    /// </summary>
    public static IReadOnlyList<string> Empty(IEnumerable<RequiredColumn> columns, JsonElement fields) =>
        [.. columns.Where(c => !HasValue(fields, c.Name) && !HasValue(fields, c.Name + "LookupId")).Select(c => c.DisplayName)];

    static bool Flag(JsonElement column, string name) => column.TryGetProperty(name, out var flag) && flag.ValueKind == JsonValueKind.True;

    static bool HasValue(JsonElement fields, string name) =>
        fields.TryGetProperty(name, out var value) && value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => false,
            JsonValueKind.String => value.GetString()!.Length > 0,
            JsonValueKind.Array => value.GetArrayLength() > 0,
            _ => true,
        };
}

public sealed record RequiredColumn(string Name, string DisplayName);

/// <summary>
/// Libraries with required columns. A simple upload into a file with such a column empty leaves it checked out to the user, also without
/// forced check-out: colleagues read the previous version and can't save. An upload session doesn't, and leaves a deliberate check-out
/// alone, so what a library requires is read at the first write to it (two calls, kept for a day) and decides how a save goes. A check-out
/// seen all the same (a column added since, the lookup failed) is ended with a check-in. The content is saved by the time any of this
/// looks at the result, so nothing after the upload may fail it: that would send it again.
/// </summary>
public sealed partial class DriveApi
{
    /// <summary>The upload's answer says whether the file ended checked out, at no extra call.</summary>
    const string SaveSelect = ItemSelect + ",publication";

    const string CheckInComment = "Saved through VFS365";

    static readonly TimeSpan RequiredColumnsKept = TimeSpan.FromDays(1);

    /// <summary>A lookup that failed (offline, say) isn't tried again for each save.</summary>
    static readonly TimeSpan RequiredColumnsRetry = TimeSpan.FromMinutes(5);

    /// <summary>A save waits this long for the lookup at most, then goes on as if the library requires nothing.</summary>
    public TimeSpan RequiredColumnsWait { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Per drive: the lookup, until when it counts, and whether that is the short time of a failed one.</summary>
    readonly ConcurrentDictionary<string, (Task<RequiredColumns?> Lookup, DateTimeOffset Until, bool Failed)> requiredColumns = new(StringComparer.Ordinal);

    /// <summary>Called after a save of a file with empty required columns, from the upload's task.</summary>
    public Action<FileNeedsDetails>? NeedsDetails { get; set; }

    static bool IsCheckedOut(JsonElement item) => item.TryGetProperty("publication", out var publication) && Str(publication, "level") == "checkout";

    /// <summary>What the library requires; null when that isn't known now.</summary>
    async Task<RequiredColumns?> RequiredColumnsOfAsync(string driveId, CancellationToken ct)
    {
        var now = client.Clock();
        if (!requiredColumns.TryGetValue(driveId, out var known) || known.Until <= now)
        {
            known = (ReadRequiredColumnsAsync(driveId), now + RequiredColumnsKept, false);
            requiredColumns[driveId] = known;
        }
        try
        {
            var found = await known.Lookup.WaitAsync(RequiredColumnsWait, ct);
            if (found is null && !known.Failed)
            {
                requiredColumns.TryUpdate(driveId, (known.Lookup, client.Clock() + RequiredColumnsRetry, true), known);
            }
            return found;
        }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>The drive's list and site, then the list's content types: Graph leaves the columns out when asked through the drive.</summary>
    async Task<RequiredColumns?> ReadRequiredColumnsAsync(string driveId)
    {
        try
        {
            using var list = await client.GetJsonAsync(new Uri($"{DriveUrl(driveId)}/list?$select=id,parentReference"), default);
            var site = Str(list!.RootElement.GetProperty("parentReference"), "siteId");
            using var types = await client.GetJsonAsync(new Uri($"{M365Client.GraphResource}/v1.0/sites/{site}/lists/{Str(list.RootElement, "id")}/contentTypes" +
                "?$select=id,name&$expand=columns($select=name,displayName,required,hidden,readOnly)"), default);
            return RequiredColumns.Parse(types!.RootElement.GetProperty("value"));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            client.Log?.Invoke($"Required columns of drive {driveId}: {e.Message}");
            return null;
        }
    }

    /// <summary>After a progressive upload (the engine's large copies): in a library that requires something, says what this file has empty.</summary>
    public async Task UploadedAsync(string driveId, DriveItemInfo item, CancellationToken ct)
    {
        if (await RequiredColumnsOfAsync(driveId, ct) is { Any: true } library)
        {
            await TellEmptyColumnsAsync(driveId, item, library, ct);
        }
    }

    /// <summary>
    /// The required columns an item has empty now, for a reminder: one call, against what the library requires as kept (read again
    /// when that is a day old). Null when it isn't known now; a deleted item throws NotFound.
    /// </summary>
    public async Task<IReadOnlyList<string>?> EmptyRequiredAsync(string driveId, string itemId, CancellationToken ct) =>
        await RequiredColumnsOfAsync(driveId, ct) switch
        {
            null => null,
            { Any: false } => [],
            var library => await EmptyOfAsync(driveId, itemId, library, ct),
        };

    /// <summary>The item's empty required columns; null when its content type isn't one the library was known to have.</summary>
    async Task<IReadOnlyList<string>?> EmptyOfAsync(string driveId, string itemId, RequiredColumns library, CancellationToken ct)
    {
        using var listItem = await client.GetJsonAsync(new Uri($"{DriveUrl(driveId)}/items/{itemId}/listItem?$select=id,contentType&$expand=fields"), ct);
        var root = listItem!.RootElement;
        return library.ByContentType.TryGetValue(Str(root.GetProperty("contentType"), "id")!, out var columns)
            ? RequiredColumns.Empty(columns, root.GetProperty("fields")) : null;
    }

    /// <summary>After a save into a library that requires something: says so when this file has it empty. One call.</summary>
    async Task TellEmptyColumnsAsync(string driveId, DriveItemInfo saved, RequiredColumns library, CancellationToken ct)
    {
        try
        {
            if (await EmptyOfAsync(driveId, saved.Id, library, ct) is { Count: > 0 } empty)
            {
                Report(new(driveId, saved.Id, saved.Name, empty));
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            client.Log?.Invoke($"{saved.Name}: reading its required columns failed: {e.Message}");
        }
    }

    /// <summary>
    /// A simple upload ended checked out. With a required column empty that is SharePoint's doing, and it is checked in, which keeps a
    /// draft a draft; without one it is the user's own check-out and stays. Afterwards the item is read again for its new eTag; when
    /// that fails, a later save still goes through (a 412 with the same cTag, <c>UploadIntoAsync</c>).
    /// </summary>
    async Task<DriveItemInfo> AfterCheckedOutSaveAsync(string driveId, DriveItemInfo saved, CancellationToken ct)
    {
        IReadOnlyList<string> empty;
        try
        {
            empty = await EmptyRequiredColumnsAsync(driveId, saved.Id, ct);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Report(new(driveId, saved.Id, saved.Name, [], true, e.Message));
            return saved;
        }
        if (empty.Count == 0)
        {
            return saved;
        }
        requiredColumns.TryRemove(driveId, out _); // what was known about this library is out of date
        try
        {
            using var _ = await client.SendJsonAsync(HttpMethod.Post, new Uri($"{DriveUrl(driveId)}/items/{saved.Id}/checkin"), Json(new { comment = CheckInComment }), ct);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Report(new(driveId, saved.Id, saved.Name, empty, true, e.Message));
            return saved;
        }
        Report(new(driveId, saved.Id, saved.Name, empty));
        try
        {
            return await GetItemByIdAsync(driveId, saved.Id, ct) ?? saved;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return saved;
        }
    }

    void Report(FileNeedsDetails details)
    {
        try
        {
            NeedsDetails?.Invoke(details);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            client.Log?.Invoke($"{details.Name}: reporting its required columns failed: {e.Message}");
        }
    }

    /// <summary>The item's values and its content type's columns, read now: a column can be required through the content type alone.</summary>
    async Task<IReadOnlyList<string>> EmptyRequiredColumnsAsync(string driveId, string itemId, CancellationToken ct)
    {
        using var listItem = await client.GetJsonAsync(new Uri($"{DriveUrl(driveId)}/items/{itemId}/listItem?$select=id,contentType,sharepointIds&$expand=fields"), ct);
        var root = listItem!.RootElement;
        var ids = root.GetProperty("sharepointIds");
        var site = $"{new Uri(Str(ids, "siteUrl")!).Host},{Str(ids, "siteId")},{Str(ids, "webId")}";
        using var columns = await client.GetJsonAsync(new Uri($"{M365Client.GraphResource}/v1.0/sites/{site}/lists/{Str(ids, "listId")}/contentTypes/" +
            $"{Str(root.GetProperty("contentType"), "id")}/columns?$select=name,displayName,required,hidden,readOnly"), ct);
        return RequiredColumns.Empty(RequiredColumns.In(columns!.RootElement.GetProperty("value")), root.GetProperty("fields"));
    }
}
