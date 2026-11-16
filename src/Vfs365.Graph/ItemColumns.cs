using System.Globalization;
using System.Text;
using System.Text.Json;
using Vfs365.Core;

namespace Vfs365.Graph;

public enum ColumnKind { Text, Note, Number, DateTime, Boolean, Choice, User, Taxonomy, ReadOnly }

/// <summary>A column of the item's content type, from SharePoint's field definition. <see cref="ColumnKind.ReadOnly"/>: shown, not edited here.</summary>
public sealed record Column(string Name, string Title, ColumnKind Kind)
{
    public bool Required { get; init; }
    public bool Multi { get; init; }
    public IReadOnlyList<string> Choices { get; init; } = [];
    public bool FillIn { get; init; }
    public bool DateOnly { get; init; }
    public double? Minimum { get; init; }
    public double? Maximum { get; init; }
    public int? MaxLength { get; init; }
    public string? TermSetId { get; init; }
    public string? AnchorId { get; init; }

    /// <summary>People columns: 0 people only, 1 people and groups.</summary>
    public int SelectionMode { get; init; }

    /// <summary>People columns: only members of this SharePoint group, when not 0.</summary>
    public int SelectionGroup { get; init; }

    public string? Description { get; init; }
}

public sealed record Person(string Key, string Name);

public sealed record Term(string Label, string Id);

/// <summary>A column's value; only the part for its kind is set. Display: SharePoint's own text, for read-only columns.</summary>
public sealed record ColumnValue
{
    public string? Text { get; init; }
    public double? Number { get; init; }
    public DateTimeOffset? Date { get; init; }
    public bool? Boolean { get; init; }
    public IReadOnlyList<string> Choices { get; init; } = [];
    public IReadOnlyList<Person> People { get; init; } = [];
    public IReadOnlyList<Term> Terms { get; init; } = [];
    public string? Display { get; init; }

    public static readonly ColumnValue Empty = new();

    public bool IsEmpty(ColumnKind kind) => kind switch
    {
        ColumnKind.Text or ColumnKind.Note => string.IsNullOrWhiteSpace(Text),
        ColumnKind.Number => Number is null,
        ColumnKind.DateTime => Date is null,
        ColumnKind.Boolean => Boolean is null,
        ColumnKind.Choice => Choices.Count == 0,
        ColumnKind.User => People.Count == 0,
        ColumnKind.Taxonomy => Terms.Count == 0,
        _ => string.IsNullOrEmpty(Display),
    };

    public bool SameAs(ColumnValue other, ColumnKind kind) => kind switch
    {
        ColumnKind.Text or ColumnKind.Note => (Text ?? "") == (other.Text ?? ""),
        ColumnKind.Number => Number == other.Number,
        ColumnKind.DateTime => Date == other.Date,
        ColumnKind.Boolean => (Boolean ?? false) == (other.Boolean ?? false),
        ColumnKind.Choice => Choices.SequenceEqual(other.Choices),
        ColumnKind.User => People.Select(p => p.Key).SequenceEqual(other.People.Select(p => p.Key), StringComparer.OrdinalIgnoreCase),
        ColumnKind.Taxonomy => Terms.Select(t => t.Id).SequenceEqual(other.Terms.Select(t => t.Id), StringComparer.OrdinalIgnoreCase),
        _ => true,
    };
}

public sealed record ContentTypeInfo(string Id, string Name);

/// <summary>What the window shows about the item besides its columns.</summary>
public sealed record ItemDetails(string Name, bool IsFolder, long Size, DateTimeOffset Created, string? CreatedBy, DateTimeOffset Modified, string? ModifiedBy)
{
    public string? WebUrl { get; init; }
    public int? Versions { get; init; }
    public string? CheckedOutTo { get; init; }
    public string? RetentionLabel { get; init; }
    public string? SensitivityLabel { get; init; }
    public string? Library { get; init; }
}

/// <summary>An item's details and columns as read, and what is needed to save them.</summary>
public sealed record ItemColumns(string DriveId, string ItemId, string Site, string WebUrl, string ListId, string ListItemId, ItemDetails Details)
{
    public required string ContentTypeId { get; init; }
    public IReadOnlyList<ContentTypeInfo> ContentTypes { get; init; } = [];
    public IReadOnlyList<Column> Columns { get; init; } = [];
    public IReadOnlyDictionary<string, ColumnValue> Values { get; init; } = new Dictionary<string, ColumnValue>();
    public string? ETag { get; init; }

    public ColumnValue ValueOf(string name) => Values.TryGetValue(name, out var value) ? value : ColumnValue.Empty;
}

/// <summary>Why a save didn't go through: per column (by internal name) and/or for the item as a whole.</summary>
/// <paramref name="ETag"/>: the item's eTag after a save that wrote part of it (null: nothing was written, or it couldn't be read), so a
/// next save doesn't take the user's own write for a change elsewhere. <paramref name="ContentTypeSaved"/>: the new content type is in.
public sealed record SaveProblems(IReadOnlyDictionary<string, string> Columns, string? Item, string? ETag = null, bool ContentTypeSaved = false)
{
    public bool None => Columns.Count == 0 && Item is null;
}

public sealed class NoListItemException(string message) : Exception(message);

/// <summary>The term store answered 403: the tenant hasn't consented to TermStore.Read.All for VFS365.</summary>
public sealed class TermStoreUnavailableException(string message) : Exception(message);

/// <summary>A save refused because the item changed in Microsoft 365 since it was read.</summary>
public sealed class ColumnsChangedException(string message) : Exception(message);

/// <summary>
/// An item's SharePoint columns: read for the details window, saved from it. Definitions come from SharePoint REST (Graph doesn't
/// describe managed metadata). Typed values are written through Graph (dates, numbers and choices without the site's locale: REST
/// read "6.5" as 65 on a Dutch site), people and managed metadata through REST's ValidateUpdateListItem (Graph refuses those).
/// Neither checks a choice against its list, so the window does. People come from SharePoint's people picker, terms from the term
/// store (TermStore.Read.All).
/// </summary>
public sealed class ColumnsApi(M365Client client)
{
    const string SpJson = "application/json;odata=nometadata";
    static string Graph => $"{M365Client.GraphResource}/v1.0";

    /// <summary>Not shown: the file's name (renamed in Explorer) and the content type (its own control).</summary>
    static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase) { "FileLeafRef", "ContentType" };

    public async Task<ItemColumns> GetAsync(string driveId, string itemId, CancellationToken ct)
    {
        using var item = await client.GetJsonAsync(new Uri($"{Graph}/drives/{driveId}/items/{itemId}" +
            "?$select=id,name,size,folder,file,createdDateTime,createdBy,lastModifiedDateTime,lastModifiedBy,webUrl,sharepointIds,publication"), ct, notFoundAsNull: true)
            ?? throw new NoListItemException("The item was not found");
        var root = item.RootElement;
        var ids = root.TryGetProperty("sharepointIds", out var found) ? found : default;
        if (Str(ids, "listItemId") is not { } listItemId || Str(ids, "listId") is not { } listId || Str(ids, "siteUrl") is not { } siteUrl)
        {
            throw new NoListItemException("This item has no SharePoint list item (a library's top folder)");
        }
        var site = $"{new Uri(siteUrl).Host},{Str(ids, "siteId")},{Str(ids, "webId")}";
        var isFolder = root.TryGetProperty("folder", out _);

        var web = GetJsonAsync($"{Graph}/sites/{site}?$select=webUrl", ct);
        var listItem = GetJsonAsync($"{Graph}/drives/{driveId}/items/{itemId}/listItem?$select=id,eTag,contentType&$expand=fields", ct);
        var versions = isFolder ? Task.FromResult<JsonElement?>(null) : GetJsonAsync($"{Graph}/drives/{driveId}/items/{itemId}/versions?$select=id", ct);
        var webUrl = Str(await web, "webUrl")!.TrimEnd('/');
        var list = GetJsonAsync($"{webUrl}/_api/web/lists(guid'{listId}')?$select=Title,ContentTypesEnabled,ContentTypes/Name,ContentTypes/StringId,ContentTypes/Hidden&$expand=ContentTypes", ct);
        var values = (await listItem)!.Value;
        var fields = values.GetProperty("fields");
        var contentTypeId = Str(values.GetProperty("contentType"), "id")!;
        var columns = await ColumnsOfAsync(webUrl, listId, contentTypeId, ct);
        var listed = (await list)!.Value;
        var types = Bool(listed, "ContentTypesEnabled")
            ? listed.GetProperty("ContentTypes").EnumerateArray()
                .Where(t => !Bool(t, "Hidden") && Str(t, "StringId")!.StartsWith(isFolder ? "0x0120" : "0x0101", StringComparison.OrdinalIgnoreCase))
                .Select(t => new ContentTypeInfo(Str(t, "StringId")!, Str(t, "Name")!)).ToList()
            : [];

        var details = new ItemDetails(Str(root, "name")!, isFolder, Long(root, "size"), Time(root, "createdDateTime"), Who(root, "createdBy"),
            Time(root, "lastModifiedDateTime"), Who(root, "lastModifiedBy"))
        {
            WebUrl = Str(root, "webUrl"),
            Versions = (await versions) is { } v ? v.GetProperty("value").GetArrayLength() : null,
            CheckedOutTo = root.TryGetProperty("publication", out var publication) && publication.TryGetProperty("checkedOutBy", out var by) ? Who(by) : null,
            RetentionLabel = NonEmpty(Str(fields, "_ComplianceTag")),
            SensitivityLabel = NonEmpty(Str(fields, "_DisplayName")),
            Library = Str(listed, "Title"),
        };
        return new ItemColumns(driveId, itemId, site, webUrl, listId, listItemId, details)
        {
            ContentTypeId = contentTypeId,
            ContentTypes = types,
            Columns = columns,
            Values = await ValuesAsync(webUrl, listId, listItemId, columns, fields, ct),
            ETag = Str(values, "eTag"),
        };
    }

    /// <summary>Titles of the required columns that are empty, also those changed only in the browser (a lookup, a link): as SharePoint counts them.</summary>
    public static IReadOnlyList<string> MissingRequired(ItemColumns item) =>
        [.. item.Columns.Where(c => c.Required && item.ValueOf(c.Name).IsEmpty(c.Kind)).Select(c => c.Title)];

    /// <summary>The columns a content type shows, in its order: from its field definitions, hidden and read-only ones left out.</summary>
    public async Task<IReadOnlyList<Column>> ColumnsOfAsync(string webUrl, string listId, string contentTypeId, CancellationToken ct)
    {
        var fields = await GetJsonAsync($"{webUrl}/_api/web/lists(guid'{listId}')/contenttypes('{contentTypeId}')/fields", ct);
        return [.. fields!.Value.GetProperty("value").EnumerateArray()
            .Where(f => !Bool(f, "Hidden") && !Bool(f, "ReadOnlyField") && !Skipped.Contains(Str(f, "InternalName")!))
            .Select(ParseColumn)];
    }

    public static Column ParseColumn(JsonElement field)
    {
        var type = Str(field, "TypeAsString");
        var rich = Bool(field, "RichText");
        var (kind, multi) = type switch
        {
            "Text" => (ColumnKind.Text, false),
            "Note" => (rich ? ColumnKind.ReadOnly : ColumnKind.Note, false),
            "Number" or "Currency" => (ColumnKind.Number, false),
            "DateTime" => (ColumnKind.DateTime, false),
            "Boolean" => (ColumnKind.Boolean, false),
            "Choice" => (ColumnKind.Choice, false),
            "MultiChoice" => (ColumnKind.Choice, true),
            "User" => (ColumnKind.User, false),
            "UserMulti" => (ColumnKind.User, true),
            "TaxonomyFieldType" => (ColumnKind.Taxonomy, false),
            "TaxonomyFieldTypeMulti" => (ColumnKind.Taxonomy, true),
            _ => (ColumnKind.ReadOnly, false),
        };
        return new Column(Str(field, "InternalName")!, Str(field, "Title") ?? Str(field, "InternalName")!, kind)
        {
            Required = Bool(field, "Required"),
            Multi = multi || Bool(field, "AllowMultipleValues"),
            Choices = field.TryGetProperty("Choices", out var choices) && choices.ValueKind == JsonValueKind.Array ? [.. choices.EnumerateArray().Select(c => c.GetString()!)] : [],
            FillIn = Bool(field, "FillInChoice"),
            DateOnly = field.TryGetProperty("DisplayFormat", out var format) && format.ValueKind == JsonValueKind.Number && format.GetInt32() == 0,
            Minimum = Number(field, "MinimumValue") is { } min && min > -1e300 ? min : null,
            Maximum = Number(field, "MaximumValue") is { } max && max < 1e300 ? max : null,
            MaxLength = field.TryGetProperty("MaxLength", out var length) && length.ValueKind == JsonValueKind.Number ? length.GetInt32() : null,
            TermSetId = NonEmpty(Str(field, "TermSetId")),
            AnchorId = Str(field, "AnchorId") is { } anchor && anchor != Guid.Empty.ToString() ? anchor : null,
            SelectionMode = field.TryGetProperty("SelectionMode", out var mode) && mode.ValueKind == JsonValueKind.Number ? mode.GetInt32() : 0,
            SelectionGroup = field.TryGetProperty("SelectionGroup", out var group) && group.ValueKind == JsonValueKind.Number ? group.GetInt32() : 0,
            Description = NonEmpty(Str(field, "Description")),
        };
    }

    /// <summary>Typed values from Graph; people with their claims from REST (Graph gives only a lookup ID); display text for read-only ones.</summary>
    async Task<IReadOnlyDictionary<string, ColumnValue>> ValuesAsync(string webUrl, string listId, string listItemId, IReadOnlyList<Column> columns,
        JsonElement fields, CancellationToken ct)
    {
        var values = new Dictionary<string, ColumnValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in columns.Where(c => c.Kind is not (ColumnKind.User or ColumnKind.ReadOnly)))
        {
            values[column.Name] = ParseValue(column, fields);
        }
        var people = columns.Where(c => c.Kind == ColumnKind.User).ToList();
        var shown = columns.Where(c => c.Kind == ColumnKind.ReadOnly).ToList();
        if (people.Count > 0)
        {
            var select = string.Join(",", people.SelectMany(p => new[] { $"{p.Name}/Name", $"{p.Name}/Title" }));
            var expand = string.Join(",", people.Select(p => p.Name));
            var item = await GetJsonAsync($"{webUrl}/_api/web/lists(guid'{listId}')/items({listItemId})?$select={select}&$expand={expand}", ct);
            foreach (var column in people)
            {
                values[column.Name] = new ColumnValue { People = ParsePeople(item!.Value, column.Name) };
            }
        }
        if (shown.Count > 0)
        {
            var text = await GetJsonAsync($"{webUrl}/_api/web/lists(guid'{listId}')/items({listItemId})/FieldValuesAsText", ct);
            foreach (var column in shown)
            {
                values[column.Name] = new ColumnValue { Display = Str(text, column.Name) ?? Str(text, column.Name.Replace("_", "_x005f_", StringComparison.Ordinal)) };
            }
        }
        return values;
    }

    public static ColumnValue ParseValue(Column column, JsonElement fields)
    {
        if (!fields.TryGetProperty(column.Name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return ColumnValue.Empty;
        }
        return column.Kind switch
        {
            ColumnKind.Text or ColumnKind.Note => new ColumnValue { Text = value.ToString() },
            ColumnKind.Number => new ColumnValue { Number = value.ValueKind == JsonValueKind.Number ? value.GetDouble()
                : double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null },
            ColumnKind.DateTime => new ColumnValue { Date = DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null },
            ColumnKind.Boolean => new ColumnValue { Boolean = value.ValueKind == JsonValueKind.True || (value.ValueKind == JsonValueKind.String && value.GetString() is "1" or "true" or "True") },
            ColumnKind.Choice => new ColumnValue { Choices = value.ValueKind == JsonValueKind.Array ? [.. value.EnumerateArray().Select(c => c.GetString()!)] : [value.GetString()!] },
            ColumnKind.Taxonomy => new ColumnValue { Terms = value.ValueKind == JsonValueKind.Array ? [.. value.EnumerateArray().Select(ParseTerm)] : [ParseTerm(value)] },
            _ => ColumnValue.Empty,
        };
    }

    static Term ParseTerm(JsonElement term) => new(Str(term, "Label") ?? "", Str(term, "TermGuid") ?? "");

    static IReadOnlyList<Person> ParsePeople(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return [];
        }
        var people = value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : new[] { value }.AsEnumerable();
        return [.. people.Where(p => Str(p, "Name") is not null).Select(p => new Person(Str(p, "Name")!, Str(p, "Title") ?? Str(p, "Name")!))];
    }

    /// <summary>
    /// Saves a new content type and the changed values. Typed ones in one Graph PATCH, people and managed metadata in one REST call.
    /// The first write checks the item's eTag: a change elsewhere since it was read fails the save before anything is written.
    /// </summary>
    public async Task<SaveProblems> SaveAsync(ItemColumns item, string contentTypeId, IReadOnlyList<(Column Column, ColumnValue Value)> changes, CancellationToken ct)
    {
        var problems = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ifMatch = item.ETag;
        var typeSaved = false;
        async Task<SaveProblems> Refused(string? whole)
        {
            string? now = null;
            if (ifMatch is null)
            {
                try
                {
                    now = await CurrentETagAsync(item, ct);
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                }
            }
            return new SaveProblems(problems, whole, now, typeSaved);
        }
        try
        {
            if (!contentTypeId.Equals(item.ContentTypeId, StringComparison.OrdinalIgnoreCase))
            {
                await SendAsync(new HttpMethod("PATCH"), $"{Graph}/drives/{item.DriveId}/items/{item.ItemId}/listItem",
                    JsonSerializer.Serialize(new { contentType = new { id = contentTypeId } }), "application/json", ifMatch, ct);
                ifMatch = null;
                typeSaved = true;
            }
            var typed = changes.Where(c => c.Column.Kind is not (ColumnKind.User or ColumnKind.Taxonomy)).ToList();
            if (typed.Count > 0)
            {
                await SendAsync(new HttpMethod("PATCH"), $"{Graph}/drives/{item.DriveId}/items/{item.ItemId}/listItem/fields", GraphBody(typed), "application/json", ifMatch, ct);
                ifMatch = null;
            }
            var validated = changes.Where(c => c.Column.Kind is ColumnKind.User or ColumnKind.Taxonomy).ToList();
            if (validated.Count > 0)
            {
                if (ifMatch is not null && await CurrentETagAsync(item, ct) is { } now && now != ifMatch)
                {
                    throw new ColumnsChangedException("changed in Microsoft 365 since it was opened");
                }
                var body = JsonSerializer.Serialize(new
                {
                    formValues = validated.Select(c => new { FieldName = c.Column.Name, FieldValue = FormValue(c.Column, c.Value) }),
                    bNewDocumentUpdate = typed.Count > 0 || !contentTypeId.Equals(item.ContentTypeId, StringComparison.OrdinalIgnoreCase),
                });
                using var answer = await client.SendJsonAsync(HttpMethod.Post, new Uri($"{item.WebUrl}/_api/web/lists(guid'{item.ListId}')/items({item.ListItemId})/ValidateUpdateListItem"),
                    () => Content(body, SpJson), ct);
                ifMatch = null;
                foreach (var row in answer!.RootElement.GetProperty("value").EnumerateArray().Where(r => Bool(r, "HasException")))
                {
                    problems[Str(row, "FieldName")!] = Str(row, "ErrorMessage") ?? "refused";
                }
            }
            return problems.Count == 0 ? new SaveProblems(problems, null) : await Refused(null);
        }
        catch (RemoteException e) when (e.Error == RemoteError.Conflict && ifMatch is not null)
        {
            return new SaveProblems(problems, "changed");
        }
        catch (ColumnsChangedException)
        {
            return new SaveProblems(problems, "changed");
        }
        catch (RemoteException e)
        {
            return await Refused(Detail(e.Message));
        }
    }

    async Task<string?> CurrentETagAsync(ItemColumns item, CancellationToken ct) =>
        Str(await GetJsonAsync($"{Graph}/drives/{item.DriveId}/items/{item.ItemId}/listItem?$select=id,eTag", ct), "eTag");

    /// <summary>Graph's fieldValueSet: numbers and yes/no typed, dates in ISO 8601 (UTC), several choices as a string collection, empty as null.</summary>
    public static string GraphBody(IEnumerable<(Column Column, ColumnValue Value)> changes)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            foreach (var (column, value) in changes)
            {
                var name = column.Name;
                switch (column.Kind)
                {
                    case ColumnKind.Text or ColumnKind.Note when string.IsNullOrEmpty(value.Text):
                    case ColumnKind.Number when value.Number is null:
                    case ColumnKind.DateTime when value.Date is null:
                    case ColumnKind.Choice when value.Choices.Count == 0 && !column.Multi:
                        json.WriteNull(name);
                        break;
                    case ColumnKind.Text or ColumnKind.Note:
                        json.WriteString(name, value.Text);
                        break;
                    case ColumnKind.Number:
                        json.WriteNumber(name, value.Number!.Value);
                        break;
                    case ColumnKind.DateTime:
                        json.WriteString(name, value.Date!.Value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
                        break;
                    case ColumnKind.Boolean:
                        json.WriteBoolean(name, value.Boolean ?? false);
                        break;
                    case ColumnKind.Choice when column.Multi:
                        json.WriteString($"{name}@odata.type", "Collection(Edm.String)");
                        json.WriteStartArray(name);
                        foreach (var choice in value.Choices)
                        {
                            json.WriteStringValue(choice);
                        }
                        json.WriteEndArray();
                        break;
                    case ColumnKind.Choice:
                        json.WriteString(name, value.Choices[0]);
                        break;
                }
            }
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>ValidateUpdateListItem's form value: people as claims JSON, terms as "Label|id;", empty as "".</summary>
    public static string FormValue(Column column, ColumnValue value) => column.Kind switch
    {
        ColumnKind.User => value.People.Count == 0 ? "" : JsonSerializer.Serialize(value.People.Select(p => new { Key = p.Key })),
        ColumnKind.Taxonomy => string.Concat(value.Terms.Select(t => $"{t.Label.Replace(";", " ", StringComparison.Ordinal).Replace("|", " ", StringComparison.Ordinal)}|{t.Id};")),
        _ => throw new ArgumentException($"{column.Kind} isn't written through REST"),
    };

    /// <summary>SharePoint's people picker for a column: users, or users and groups, limited to its group when it has one.</summary>
    public async Task<IReadOnlyList<Person>> SearchPeopleAsync(string webUrl, Column column, string query, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            queryParams = new
            {
                QueryString = query,
                MaximumEntitySuggestions = 20,
                AllowEmailAddresses = false,
                AllowMultipleEntities = column.Multi,
                PrincipalSource = 15,
                PrincipalType = column.SelectionMode == 0 ? 1 : 15,
                SharePointGroupID = column.SelectionGroup,
                AllUrlZones = false,
            },
        });
        using var answer = await client.SendJsonAsync(HttpMethod.Post, new Uri($"{webUrl}/_api/SP.UI.ApplicationPages.ClientPeoplePickerWebServiceInterface.clientPeoplePickerSearchUser"),
            () => Content(body, SpJson), ct);
        using var hits = JsonDocument.Parse(answer!.RootElement.GetProperty("value").GetString()!);
        return [.. hits.RootElement.EnumerateArray()
            .Where(h => Str(h, "Key") is not null)
            .Select(h => new Person(Str(h, "Key")!, Describe(h)))];
    }

    static string Describe(JsonElement hit)
    {
        var name = Str(hit, "DisplayText") ?? Str(hit, "Key")!;
        var mail = hit.TryGetProperty("EntityData", out var data) ? NonEmpty(Str(data, "Email")) : null;
        return mail is null || name.Contains(mail, StringComparison.OrdinalIgnoreCase) ? name : $"{name} ({mail})";
    }

    /// <summary>
    /// A term set's terms, sorted by label, at most <see cref="MaxTerms"/>. The whole set comes flat in pages (Graph v1.0 says nothing
    /// about children counts); below an anchor term its children are walked, one call per term. A 403 renews the token once first: an
    /// admin may have consented to TermStore.Read.All after it was issued.
    /// </summary>
    public async Task<IReadOnlyList<Term>> TermsAsync(string site, Column column, string language, CancellationToken ct)
    {
        var terms = new List<Term>();
        var set = $"{Graph}/sites/{site}/termStore/sets/{column.TermSetId}";
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await ReadAsync();
                return [.. terms.Take(MaxTerms).OrderBy(t => t.Label, StringComparer.CurrentCultureIgnoreCase)];
            }
            catch (RemoteException e) when (e.Error == RemoteError.AccessDenied)
            {
                if (attempt > 0 || !await client.RenewTokenAsync(M365Client.GraphResource, ct))
                {
                    throw new TermStoreUnavailableException(Detail(e.Message));
                }
                terms.Clear();
            }
        }

        async Task ReadAsync()
        {
            if (column.AnchorId is { } anchor)
            {
                var below = new Queue<string>([anchor]);
                while (below.TryDequeue(out var parent) && terms.Count < MaxTerms)
                {
                    foreach (var child in await PagesAsync($"{set}/terms/{parent}/children?$select=id,labels"))
                    {
                        terms.Add(new Term(LabelOf(child, language), Str(child, "id")!));
                        below.Enqueue(Str(child, "id")!);
                    }
                }
            }
            else
            {
                terms.AddRange((await PagesAsync($"{set}/terms?$select=id,labels")).Select(t => new Term(LabelOf(t, language), Str(t, "id")!)));
            }
        }

        async Task<List<JsonElement>> PagesAsync(string url)
        {
            var found = new List<JsonElement>();
            for (var next = url; next is not null && found.Count + terms.Count < MaxTerms;)
            {
                using var page = await client.GetJsonAsync(new Uri(next), ct);
                found.AddRange(page!.RootElement.GetProperty("value").EnumerateArray().Select(t => t.Clone()));
                next = Str(page.RootElement, "@odata.nextLink");
            }
            return found;
        }
    }

    public const int MaxTerms = 2000;

    /// <summary>The label in the user's language when the term has one, else its default label.</summary>
    static string LabelOf(JsonElement term, string language)
    {
        var labels = term.TryGetProperty("labels", out var found) ? found.EnumerateArray().ToList() : [];
        var match = labels.FirstOrDefault(l => string.Equals(Str(l, "languageTag"), language, StringComparison.OrdinalIgnoreCase));
        match = match.ValueKind == JsonValueKind.Object ? match
            : labels.FirstOrDefault(l => Str(l, "languageTag")?.Split('-')[0].Equals(language.Split('-')[0], StringComparison.OrdinalIgnoreCase) == true);
        match = match.ValueKind == JsonValueKind.Object ? match : labels.FirstOrDefault(l => l.TryGetProperty("isDefault", out var d) && d.ValueKind == JsonValueKind.True);
        return Str(match, "name") ?? Str(term, "id")!;
    }

    async Task<JsonElement?> GetJsonAsync(string url, CancellationToken ct)
    {
        using var doc = await client.GetJsonAsync(new Uri(url), ct);
        return doc?.RootElement.Clone();
    }

    async Task SendAsync(HttpMethod method, string url, string body, string mediaType, string? ifMatch, CancellationToken ct)
    {
        using var _ = await client.SendJsonAsync(method, new Uri(url), () => Content(body, mediaType), ct, ifMatch);
    }

    static StringContent Content(string body, string mediaType)
    {
        var content = new StringContent(body, Encoding.UTF8);
        content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(mediaType);
        return content;
    }

    /// <summary>The service's own message, without the status line and address M365RequestException starts with.</summary>
    static string Detail(string message)
    {
        var start = message.IndexOf("\"message\":", StringComparison.Ordinal);
        if (start < 0)
        {
            return message;
        }
        var rest = message[(start + 10)..].TrimStart();
        if (rest.StartsWith('{'))
        {
            var value = rest.IndexOf("\"value\":", StringComparison.Ordinal);
            rest = value < 0 ? rest : rest[(value + 8)..].TrimStart();
        }
        if (!rest.StartsWith('"'))
        {
            return message;
        }
        var end = rest.IndexOf('"', 1);
        return end > 1 ? rest[1..end] : message;
    }

    static string? Who(JsonElement item, string name) => item.TryGetProperty(name, out var set) ? Who(set) : null;

    static string? Who(JsonElement set) =>
        set.TryGetProperty("user", out var user) ? Str(user, "displayName")
        : set.TryGetProperty("application", out var app) ? Str(app, "displayName") : null;

    static string? Str(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString() : null;

    static bool Bool(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    static double? Number(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    static long Long(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : 0;

    static DateTimeOffset Time(JsonElement element, string name) =>
        Str(element, name) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time) ? time : DateTimeOffset.MinValue;

    static string? NonEmpty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;
}
