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

    /// <summary>
    /// The letter for DriveLetter *: the first free one counting down from Z (as WinFsp picks), but none that a mapping wants, so the
    /// drive never takes Z: from Z:=...; null when none is free.
    /// </summary>
    public static string? FreeLetter(IEnumerable<DriveMapping> mappings, Func<string, bool> inUse)
    {
        var wanted = mappings.Select(m => m.Letter).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var c = 'Z'; c >= 'D'; c--)
        {
            var letter = $"{c}:";
            if (!wanted.Contains(letter) && !inUse(letter))
            {
                return letter;
            }
        }
        return null;
    }
}

/// <summary>
/// Drive letters for folders inside the volume, for this sign-in only (DOS devices of the session, as subst makes): defined after the
/// mount, removed at unmount. vfs365np.dll tells Explorer what they are connected to. A letter that an old SharePoint WebDAV mapping
/// holds or would take back at the next sign-in (OneDriveMapper, net use to *.sharepoint.com) is taken over; so is one an earlier agent
/// of this user left (a crash: its target is a folder below this user's \\VFS365\&lt;user&gt;); other holders keep theirs. Thread-safe;
/// after RemoveAll (unmount) no letter is defined any more.
/// </summary>
public sealed class SessionDriveLetters(string unc)
{
    readonly Lock gate = new();
    readonly Dictionary<string, (string Device, string Unc)> defined = new(StringComparer.OrdinalIgnoreCase);
    bool closed, labelsChanged;

    /// <summary>A letter's DOS device target that is a folder of this user's volume (\??\UNC\VFS365\&lt;user&gt;\...): ours, from this agent or an earlier one.</summary>
    public static bool Ours(string? target, string unc) =>
        target is not null && target.StartsWith($@"\??\UNC\{unc.TrimStart('\\')}\", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gives <paramref name="letter"/> ("Z:") the folder <paramref name="folder"/> (\\VFS365\&lt;user&gt;\...). Null when it has it, else why
    /// not. <paramref name="replaced"/>: the SharePoint WebDAV mapping it took the letter from.
    /// </summary>
    public string? Define(string letter, string folder, string? label, out string? replaced)
    {
        replaced = null;
        lock (gate)
        {
            if (closed)
            {
                return $"{letter}: not defined, the drive is stopping";
            }
            var device = $@"\??\UNC\{folder.TrimStart('\\')}";
            var current = DosDevices.Target(letter);
            if (current is not null && current.Equals(device, StringComparison.OrdinalIgnoreCase))
            {
                defined[letter] = (device, folder);
                Label(folder, label);
                return null;
            }
            if (Ours(current, unc))
            {
                DosDevices.Remove(letter, current!); // another folder of ours: an older policy, or left by an agent that crashed
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
            defined[letter] = (device, folder);
            Label(folder, label);
            ExplorerLocation.DriveAdded(letter);
            return null;
        }
    }

    /// <summary>
    /// Removes letters an earlier agent of this user left (a crash) that no mapping wants now, and never <paramref name="driveLetter"/>
    /// (the drive's own). Returns them.
    /// </summary>
    public IReadOnlyList<string> RemoveLeftovers(IEnumerable<string> mapped, string? driveLetter)
    {
        var keep = mapped.Append(driveLetter ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = new List<string>();
        lock (gate)
        {
            for (var c = 'D'; c <= 'Z'; c++)
            {
                var letter = $"{c}:";
                if (closed || keep.Contains(letter) || defined.ContainsKey(letter) || DosDevices.Target(letter) is not { } target || !Ours(target, unc))
                {
                    continue;
                }
                DosDevices.Remove(letter, target);
                ExplorerLocation.RemoveFolderLabel(@"\" + target[@"\??\UNC".Length..]);
                ExplorerLocation.DriveRemoved(letter);
                removed.Add(letter);
            }
        }
        return removed;
    }

    /// <summary>The letters defined now ("Z:") and their folders (\\VFS365\&lt;user&gt;\...).</summary>
    public IReadOnlyDictionary<string, string> Folders()
    {
        lock (gate)
        {
            return defined.ToDictionary(d => d.Key, d => d.Value.Unc, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>True once when a label was written since the last call: Explorer then reads its names again (once per apply).</summary>
    public bool TakeLabelsChanged()
    {
        lock (gate)
        {
            var changed = labelsChanged;
            labelsChanged = false;
            return changed;
        }
    }

    /// <summary>Removes the letters this agent defined (at unmount), each on its own; none is defined after this. Returns what failed.</summary>
    public IReadOnlyList<string> RemoveAll()
    {
        var problems = new List<string>();
        lock (gate)
        {
            closed = true;
            foreach (var (letter, (device, folder)) in defined)
            {
                try
                {
                    DosDevices.Remove(letter, device);
                    ExplorerLocation.RemoveFolderLabel(folder);
                    ExplorerLocation.DriveRemoved(letter);
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    problems.Add($"{letter}: {e.Message}");
                }
            }
            defined.Clear();
        }
        return problems;
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

    void Label(string folder, string? label)
    {
        if (label is not null && ExplorerLocation.SetFolderLabel(folder, label))
        {
            labelsChanged = true;
        }
    }
}
