using System.ComponentModel;

namespace Vfs365.Agent;

/// <summary>A DriveMappings policy entry: "Z:=Sites\Finance\Documents"; "|Finance" names the drive in Explorer.</summary>
public sealed record DriveMapping(string Letter, string Target, string? Label)
{
    public static IReadOnlyList<DriveMapping> Parse(IEnumerable<string> entries, string? driveLetter, ICollection<string> problems)
    {
        var mappings = new List<DriveMapping>();
        foreach (var entry in entries)
        {
            var at = entry.IndexOf('=');
            var parts = (at < 0 ? "" : entry[(at + 1)..]).Split('|', 2, StringSplitOptions.TrimEntries);
            var letter = at < 0 ? null : DriveLetterOption.Parse(entry[..at]).MountPoint;
            if (letter is null || parts[0].Length == 0)
            {
                problems.Add($"DriveMappings '{entry}' is not like Z:=Sites\\Finance\\Documents");
            }
            else if (letter.Equals(driveLetter, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"DriveMappings '{entry}': {letter} is the drive's own letter (DriveLetter)");
            }
            else if (mappings.Any(m => m.Letter == letter))
            {
                problems.Add($"DriveMappings '{entry}': {letter} is mapped twice; the first counts");
            }
            else
            {
                mappings.Add(new(letter, parts[0], parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null));
            }
        }
        return mappings;
    }
}

/// <summary>
/// Drive letters for folders inside the volume, for this sign-in only (DOS devices of the session, as subst makes): defined after the
/// mount, removed at unmount. vfs365np.dll tells Explorer what they are connected to. A letter that an old SharePoint WebDAV mapping
/// holds or would take back at the next sign-in (OneDriveMapper, net use to *.sharepoint.com) is taken over; other holders keep theirs.
/// </summary>
public sealed class SessionDriveLetters
{
    readonly Dictionary<string, (string Device, string Unc)> defined = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gives <paramref name="letter"/> ("Z:") the folder <paramref name="unc"/> (\\VFS365\&lt;user&gt;\...). Null when it has it, else why
    /// not. <paramref name="replaced"/>: the SharePoint WebDAV mapping it took the letter from.
    /// </summary>
    public string? Define(string letter, string unc, string? label, out string? replaced)
    {
        replaced = null;
        var device = $@"\??\UNC\{unc.TrimStart('\\')}";
        var current = DosDevices.Target(letter);
        if (current is not null && current.Equals(device, StringComparison.OrdinalIgnoreCase))
        {
            defined[letter] = (device, unc);
            Label(unc, label);
            return null;
        }
        if (current is not null && defined.TryGetValue(letter, out var mine) && current.Equals(mine.Device, StringComparison.OrdinalIgnoreCase))
        {
            DosDevices.Remove(letter, mine.Device);
        }
        else if (WebDavMapping(letter) is { } webDav)
        {
            DosDevices.Disconnect(letter);
            replaced = webDav;
            if (DosDevices.Target(letter) is { } still)
            {
                return $"{letter} is still in use ({still}) after removing the SharePoint WebDAV mapping to {webDav}";
            }
        }
        else if (current is not null)
        {
            return $"{letter} is in use ({DriveLetterReport.Read(letter).RemoteName ?? current})";
        }
        if (!DosDevices.Define(letter, device))
        {
            return $"{letter}: {new Win32Exception().Message}";
        }
        defined[letter] = (device, unc);
        Label(unc, label);
        ExplorerLocation.DriveAdded(letter);
        return null;
    }

    /// <summary>Removes the letters this agent defined (at unmount).</summary>
    public void RemoveAll()
    {
        foreach (var (letter, (device, unc)) in defined)
        {
            DosDevices.Remove(letter, device);
            ExplorerLocation.RemoveFolderLabel(unc);
            ExplorerLocation.DriveRemoved(letter);
        }
        defined.Clear();
    }

    /// <summary>A SharePoint WebDAV path (\\contoso.sharepoint.com@SSL\DavWWWRoot\...), as OneDriveMapper and net use map them.</summary>
    public static bool IsSharePointWebDav(string? remote) =>
        remote is not null && remote.StartsWith(@"\\", StringComparison.Ordinal) && remote.Contains(".sharepoint.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>The SharePoint WebDAV mapping that holds the letter now or would take it at the next sign-in (HKCU\Network), or null.</summary>
    static string? WebDavMapping(string letter)
    {
        var report = DriveLetterReport.Read(letter);
        return new[] { report.RemoteName, report.Remembered }.FirstOrDefault(IsSharePointWebDav);
    }

    static void Label(string unc, string? label)
    {
        if (label is not null)
        {
            ExplorerLocation.SetFolderLabel(unc, label);
        }
    }
}
