namespace Vfs365.Agent;

/// <summary>
/// A search an Explorer window or tab shows. Terms: what was typed. Locations: the folders searched. HasFilters: property filters
/// (kind:, datemodified:) narrow it; Microsoft 365 search gets only those KQL knows (<see cref="SearchTerms.Clean"/>).
/// </summary>
public sealed record ExplorerSearch(string Terms, IReadOnlyList<string> Locations, bool HasFilters)
{
    const string Scheme = "search-ms:";
    const int MaxLength = 8192;

    /// <summary>
    /// The search a window shows, from what Shell Windows says about it; null when it shows none. Windows 11 25H2 names a search view
    /// "&lt;scope's display name&gt;&amp;&lt;words&gt;" ("Search Results in M:\Sites\Sales&amp;budget", the prefix localized) with the words as
    /// LocationName and no LocationURL (spike 15); other builds may give a search-ms: string. The location is where a root
    /// (<paramref name="roots"/>: "M:\", "\\VFS365\ann\") starts in the name. A scope without a path (Explorer names This PC, Home or
    /// Quick access by their display names) has none, unless its name is the display name of <paramref name="previous"/>, the folder
    /// the tab showed before (<paramref name="previousName"/>, else its last path component; a drive's top may be named
    /// "VFS365 (M:)"): a search names its own scope, so one elsewhere is never a search in the folder shown before.
    /// </summary>
    public static ExplorerSearch? FromView(string? parsingName, string? locationUrl, string? locationName, IEnumerable<string> roots, string? previous,
        string? previousName = null)
    {
        if ((Parse(parsingName) ?? Parse(locationUrl)) is { } classic)
        {
            return classic;
        }
        if (parsingName is not { Length: > 0 and <= MaxLength } || !string.IsNullOrEmpty(locationUrl) || parsingName.StartsWith("::", StringComparison.Ordinal)
            || parsingName.StartsWith(@"\\", StringComparison.Ordinal) || parsingName.Length >= 2 && parsingName[1] == ':'
            || parsingName.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var words = locationName ?? "";
        var head = words.Length > 0 && parsingName.EndsWith("&" + words, StringComparison.Ordinal) ? parsingName[..^(words.Length + 1)] : null;
        if (head is null)
        {
            // A search view whose box was cleared: no words, nothing to hand over
            head = parsingName;
            words = "";
        }
        var start = -1;
        foreach (var root in roots)
        {
            var bare = root.TrimEnd('\\');
            var at = bare.Length == 0 ? -1 : head.IndexOf(bare, StringComparison.OrdinalIgnoreCase);
            if (at >= 0 && (start < 0 || at < start) && (head.Length == at + bare.Length || head[at + bare.Length] == '\\'))
            {
                start = at;
            }
        }
        var location = start >= 0 ? head[start..] : previous is not null && Scopes(head, previous, previousName) ? previous : null;
        return new ExplorerSearch(words.Trim(), location is null ? [] : [location], SearchTerms.Words(words).Any(SearchTerms.IsProperty));
    }

    /// <summary>Whether the scope a search view names (the end of <paramref name="head"/>) is the folder <paramref name="folder"/>.</summary>
    static bool Scopes(string head, string folder, string? folderName)
    {
        var scope = head.TrimEnd();
        // A drive's display name ends in its letter: "VFS365 (M:)"
        var bare = scope.EndsWith(":)", StringComparison.Ordinal) && scope.LastIndexOf(" (", StringComparison.Ordinal) is > 0 and var paren ? scope[..paren] : scope;
        var path = folder.TrimEnd('\\');
        var leaf = folder.StartsWith("::", StringComparison.Ordinal) ? "" : path[(path.LastIndexOf('\\') + 1)..];
        return new[] { folderName ?? "", leaf }.Any(name => EndsWithName(scope, name) || EndsWithName(bare, name));
    }

    /// <summary>The display name ends the text, after the space that follows the localized "Search Results in" (a path has a backslash there).</summary>
    static bool EndsWithName(string text, string name) =>
        name.Length > 0 && text.EndsWith(name, StringComparison.OrdinalIgnoreCase) && (text.Length == name.Length || text[^(name.Length + 1)] == ' ');

    /// <summary>The search in a window's location; null for anything that isn't a search-ms: string.</summary>
    public static ExplorerSearch? Parse(string? location)
    {
        if (location is null || location.Length > MaxLength || !location.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var terms = new List<string>();
        var locations = new List<string>();
        string? query = null;
        var filters = false;
        foreach (var part in location[Scheme.Length..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = part.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }
            var name = part[..equals];
            var value = Decode(part[(equals + 1)..]);
            if (name.Equals("query", StringComparison.OrdinalIgnoreCase))
            {
                query = value;
            }
            else if (name.Equals("crumb", StringComparison.OrdinalIgnoreCase) && value.IndexOf(':') is var colon and > 0)
            {
                var property = value[..colon];
                var text = value[(colon + 1)..];
                if (property.Equals("location", StringComparison.OrdinalIgnoreCase))
                {
                    locations.Add(text);
                }
                else if (property.Equals("System.Generic.String", StringComparison.OrdinalIgnoreCase))
                {
                    terms.Add(text);
                }
                else
                {
                    filters = true;
                }
            }
        }
        var words = terms.Count > 0 ? string.Join(' ', terms) : query ?? "";
        return new ExplorerSearch(words.Trim(), locations, filters);
    }

    /// <summary>
    /// A location below the navigation pane node ("::{CLSID}\Sites\Sales") as a path on the node's target (<paramref name="target"/>,
    /// "M:\" or "\\VFS365\ann"); other locations as they are.
    /// </summary>
    public static string Unshell(string location, string clsid, string? target)
    {
        var node = "::" + clsid;
        if (target is null || !location.StartsWith(node, StringComparison.OrdinalIgnoreCase)
            || location.Length > node.Length && location[node.Length] != '\\')
        {
            return location;
        }
        var rest = location[node.Length..].TrimStart('\\');
        var root = target.TrimEnd('\\');
        return rest.Length == 0 ? root + @"\" : $@"{root}\{rest}";
    }

    static string Decode(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return value;
        }
    }
}

/// <summary>Search words as Microsoft 365 search gets them.</summary>
public static class SearchTerms
{
    public const int MaxLength = 255;

    /// <summary>Explorer's file type filters (ext:.docx, type:docx), which KQL writes as filetype:docx.</summary>
    static readonly string[] FileTypes = ["ext", "fileext", "type", "filetype"];

    /// <summary>Filters KQL doesn't have (kind:document, datemodified:this week) or that VFS365 sets itself (path:).</summary>
    static readonly string[] Dropped = ["path", "kind", "datemodified", "datecreated", "dateaccessed", "date", "modified", "created", "size", "folder", "foldername", "folderpath"];

    /// <summary>
    /// Whitespace collapsed, Explorer's file type filters as KQL's filetype:, filters KQL doesn't have and path: (VFS365 sets the scope)
    /// left out, an unpaired quote dropped, at most <see cref="MaxLength"/> characters. Other KQL (author:ann, OR, quotes) stays.
    /// </summary>
    public static string Clean(string? text)
    {
        var tokens = new List<string>();
        var skipNext = false;
        foreach (var token in Words(text ?? ""))
        {
            if (skipNext)
            {
                skipNext = false;
                continue;
            }
            var name = PropertyOf(token);
            if (name is not null && Dropped.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                // "datemodified:this week": the relative date's second word goes with it
                skipNext = token[(token.IndexOf(':') + 1)..].ToLowerInvariant() is "this" or "last" or "next" or "past";
                continue;
            }
            if (name is null)
            {
                tokens.Add(token);
            }
            else if (FileTypes.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                var value = token[(token.IndexOf(':') + 1)..].TrimStart('=').Trim('"').TrimStart('*').TrimStart('.');
                if (value.Length > 0 && value.All(char.IsAsciiLetterOrDigit))
                {
                    tokens.Add($"{(token.StartsWith('-') ? "-" : "")}filetype:{value}");
                }
            }
            else
            {
                tokens.Add(token);
            }
        }
        var joined = string.Join(' ', tokens);
        if (joined.Count(c => c == '"') % 2 == 1)
        {
            var last = joined.LastIndexOf('"');
            joined = (joined[..last] + joined[(last + 1)..]).Trim();
        }
        return joined.Length <= MaxLength ? joined : joined[..MaxLength].TrimEnd();
    }

    /// <summary>A property filter such as kind:document or -filetype:pdf (a word, then a colon, outside quotes).</summary>
    public static bool IsProperty(string token) => PropertyOf(token) is not null;

    static string? PropertyOf(string token)
    {
        var name = token.TrimStart('-');
        var colon = name.IndexOf(':');
        return colon > 0 && name[..colon].All(char.IsLetter) ? name[..colon] : null;
    }

    /// <summary>Words split at whitespace outside quotes.</summary>
    public static IEnumerable<string> Words(string text)
    {
        var start = -1;
        var quoted = false;
        for (var i = 0; i <= text.Length; i++)
        {
            var end = i == text.Length || !quoted && char.IsWhiteSpace(text[i]);
            if (end)
            {
                if (start >= 0)
                {
                    yield return text[start..i];
                    start = -1;
                }
                continue;
            }
            if (start < 0)
            {
                start = i;
            }
            if (text[i] == '"')
            {
                quoted = !quoted;
            }
        }
    }
}
