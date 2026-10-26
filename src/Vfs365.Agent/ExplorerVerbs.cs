using System.Globalization;
using System.Text;
using Microsoft.Win32;

namespace Vfs365.Agent;

/// <summary>
/// Explorer's "VFS365" submenu on files and folders of the volume, per user (HKCU\Software\Classes\*\shell and Directory\shell, no admin
/// rights): static verbs, shown only for items whose path starts with one of the volume's roots (AppliesTo, Advanced Query Syntax on
/// System.ItemPathDisplay), single selection only. Each runs "vfs365-agent.exe verb &lt;name&gt; "%1"", which hands it to the running
/// agent (<see cref="VerbPipe"/>). Windows 11 shows them under "Show more options". No folder background menu: Explorer hides a
/// Directory\Background verb that has AppliesTo even on a matching folder (spike 15, `search-spike menu`), and without it the verb
/// would show in every folder.
/// </summary>
public static class ExplorerVerbs
{
    public const string KeyName = "VFS365";
    const string Classes = @"Software\Classes";
    const string Files = Classes + @"\*\shell\" + KeyName, Folders = Classes + @"\Directory\shell\" + KeyName;
    const string Signature = "VFS365Signature";

    /// <summary>The verbs in menu order, and whether files and folders get them.</summary>
    public static readonly (string Name, bool Files, bool Folders)[] Verbs =
    [
        ("browser", true, true), ("desktop", true, false), ("link", true, true), ("versions", true, false), ("share", true, true), ("keep", true, false),
        ("compare", true, false), ("search", false, true),
    ];

    public static bool IsVerb(string name) => Verbs.Any(v => v.Name == name);

    /// <summary>Office file extensions per Office URI scheme (learn.microsoft.com, "Office URI Schemes").</summary>
    public static readonly IReadOnlyDictionary<string, string> OfficeSchemes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".doc"] = "ms-word", [".docx"] = "ms-word", [".docm"] = "ms-word", [".dot"] = "ms-word", [".dotx"] = "ms-word", [".dotm"] = "ms-word",
        [".xls"] = "ms-excel", [".xlsx"] = "ms-excel", [".xlsm"] = "ms-excel", [".xlsb"] = "ms-excel", [".xlt"] = "ms-excel", [".xltx"] = "ms-excel", [".xltm"] = "ms-excel",
        [".ppt"] = "ms-powerpoint", [".pptx"] = "ms-powerpoint", [".pptm"] = "ms-powerpoint", [".pps"] = "ms-powerpoint", [".ppsx"] = "ms-powerpoint",
        [".ppsm"] = "ms-powerpoint", [".pot"] = "ms-powerpoint", [".potx"] = "ms-powerpoint", [".potm"] = "ms-powerpoint",
    };

    /// <summary>The command a verb runs.</summary>
    public static string Command(string exe, string verb) => $"\"{exe}\" verb {verb} \"%1\"";

    /// <summary>Items below any of <paramref name="roots"/> ("M:\", "\\VFS365\ann\"). Quotes can't occur in Windows paths.</summary>
    public static string AppliesTo(IEnumerable<string> roots) =>
        string.Join(" OR ", roots.Select(root => $"System.ItemPathDisplay:~<\"{root}\""));

    /// <summary>Office files, for Open in desktop app.</summary>
    public static string OfficeFiles() => string.Join(" OR ", OfficeSchemes.Keys.Select(ext => $"System.FileExtension:=\"{ext}\""));

    /// <summary>Copies the engine made (<see cref="Core.Drive.ConflictCopies"/>), for Keep this version and Compare.</summary>
    public static string ConflictCopyFiles() =>
        "System.FileName:~\"* (conflict ????-??-?? ??????*)*\" OR System.FileName:~\"* (recovered ????-??-?? ??????*)*\"";

    /// <summary>
    /// Writes the menu for the volume's <paramref name="roots"/> into <paramref name="hive"/> (HKCU; a key of its own in tests), only when
    /// something changed: true then. No roots: the menu is removed.
    /// </summary>
    public static bool Register(RegistryKey hive, string exe, IReadOnlyCollection<string> roots, CultureInfo? culture = null)
    {
        if (roots.Count == 0)
        {
            var had = IsRegistered(hive);
            Remove(hive);
            return had;
        }
        culture ??= CultureInfo.CurrentUICulture;
        var appliesTo = AppliesTo(roots);
        var signature = $"{exe}|{appliesTo}|{culture.Name}|{string.Join(',', Verbs.Select(v => v.Name))}";
        using (var existing = hive.OpenSubKey(Files))
        {
            if (existing?.GetValue(Signature) as string == signature)
            {
                return false;
            }
        }
        Remove(hive);
        foreach (var (path, folders) in new[] { (Folders, true), (Files, false) })
        {
            using var menu = hive.CreateSubKey(path);
            menu.SetValue("MUIVerb", UiText.Get(culture, "Verb.Menu"));
            menu.SetValue("SubCommands", "");
            menu.SetValue("AppliesTo", appliesTo);
            menu.SetValue("MultiSelectModel", "Single");
            menu.SetValue("Icon", $"{exe},0");
            var order = 0;
            foreach (var (name, _, _) in Verbs.Where(v => folders ? v.Folders : v.Files))
            {
                // Explorer lists subcommands by key name
                using var verb = menu.CreateSubKey($@"shell\{++order:00}{name}");
                verb.SetValue("MUIVerb", UiText.Get(culture, $"Verb.{name}"));
                switch (name)
                {
                    case "desktop":
                        verb.SetValue("AppliesTo", OfficeFiles());
                        break;
                    case "keep":
                        verb.SetValue("AppliesTo", ConflictCopyFiles());
                        verb.SetValue("CommandFlags", 0x20, RegistryValueKind.DWord); // ECF_SEPARATORBEFORE
                        break;
                    case "compare":
                        verb.SetValue("AppliesTo", ConflictCopyFiles());
                        break;
                    case "search":
                        verb.SetValue("CommandFlags", 0x20, RegistryValueKind.DWord); // ECF_SEPARATORBEFORE
                        break;
                }
                using var command = verb.CreateSubKey("command");
                command.SetValue("", Command(exe, name));
            }
            if (path == Files)
            {
                menu.SetValue(Signature, signature); // last: a menu written halfway is written again
            }
        }
        return true;
    }

    public static void Remove(RegistryKey hive)
    {
        hive.DeleteSubKeyTree(Files, false);
        hive.DeleteSubKeyTree(Folders, false);
    }

    public static bool IsRegistered(RegistryKey hive)
    {
        using var files = hive.OpenSubKey(Files);
        using var folders = hive.OpenSubKey(Folders);
        return files is not null || folders is not null;
    }

    /// <summary>reg.exe paths of the menu below <paramref name="root"/> (HKCU), for the logon cleanup.</summary>
    public static IEnumerable<string> RegPaths(string root) => [$@"{root}\{Files}", $@"{root}\{Folders}"];

    /// <summary>
    /// The volume path ("\Sites\Sales\Offer.docx") of a path Explorer gave: on the drive's letter, below <paramref name="unc"/>, or on a
    /// letter for a folder (<paramref name="folders"/>: "Z:" to its \\VFS365\&lt;user&gt;\... folder). Null elsewhere or with . or .. in it.
    /// </summary>
    public static string? VolumePath(string path, string unc, string? letter, IReadOnlyDictionary<string, string> folders)
    {
        var trimmed = path.Trim();
        if (trimmed.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = @"\\" + trimmed[8..];
        }
        else if (trimmed.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            trimmed = trimmed[4..];
        }
        if (trimmed.Length >= 2 && trimmed[1] == ':' && folders.TryGetValue(trimmed[..2], out var folder))
        {
            trimmed = folder + (trimmed.Length > 2 ? @"\" + trimmed[2..].TrimStart('\\') : "");
        }
        if (!VolumePaths.OnVolume(trimmed, unc, letter) || VolumePaths.Relative(trimmed, unc, letter) is not { } relative)
        {
            return null;
        }
        var segments = relative.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(s => s is "." or "..") ? null : @"\" + string.Join('\\', segments);
    }
}

/// <summary>Web addresses for the VFS365 menu, from what Graph gives for an item.</summary>
public static class ItemUrls
{
    /// <summary>
    /// The Office URI that opens a file for editing in its desktop app ("ms-word:ofe|u|https://..."), so Office edits the copy in
    /// Microsoft 365 and co-authoring works; null for files no Office app opens this way.
    /// </summary>
    public static string? Desktop(string name, string fileUrl) =>
        ExplorerVerbs.OfficeSchemes.TryGetValue(Path.GetExtension(name), out var scheme) ? $"{scheme}:ofe|u|{Encode(fileUrl)}" : null;

    /// <summary>SharePoint's version history page of a list item.</summary>
    public static string VersionHistory(string siteUrl, string listId, string listItemId) =>
        $"{siteUrl.TrimEnd('/')}/_layouts/15/Versions.aspx?list={Uri.EscapeDataString("{" + listId.Trim('{', '}') + "}")}&ID={Uri.EscapeDataString(listItemId)}";

    /// <summary>
    /// A file URL in plain ASCII for the Office URI schemes: spaces, %, #, characters URIs don't allow and non-ASCII characters
    /// (as UTF-8) are percent-encoded; RFC 3986's reserved characters stay as they are, as the schemes ask. A URL that is already
    /// encoded (no spaces or non-ASCII characters in it) is decoded first, so nothing is encoded twice.
    /// </summary>
    public static string Encode(string url)
    {
        var start = url.IndexOf("://", StringComparison.Ordinal) is var scheme and >= 0 ? url.IndexOf('/', scheme + 3) : -1;
        if (start < 0)
        {
            return url;
        }
        var path = url[start..];
        if (!path.Any(c => c == ' ' || c > 0x7F))
        {
            path = Uri.UnescapeDataString(path);
        }
        var encoded = new StringBuilder(url, 0, start, url.Length + 16);
        foreach (var b in Encoding.UTF8.GetBytes(path))
        {
            if (b > 0x20 && b < 0x7F && b is not ((byte)'%' or (byte)'#' or (byte)'"' or (byte)'<' or (byte)'>' or (byte)'\\' or (byte)'^' or (byte)'`' or (byte)'{' or (byte)'|' or (byte)'}'))
            {
                encoded.Append((char)b);
            }
            else
            {
                encoded.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }
        return encoded.ToString();
    }
}
