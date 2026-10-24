using System.Text.Json;

namespace Vfs365.Agent;

/// <summary>A FolderRedirection policy entry: "Documents=OneDrive\Documents"; "|move" moves what the local folder holds into it.</summary>
public sealed record RedirectRule(string Folder, Guid[] Ids, string Target, bool Move)
{
    public static IReadOnlyList<RedirectRule> Parse(IEnumerable<string> entries, ICollection<string> problems)
    {
        var rules = new List<RedirectRule>();
        foreach (var entry in entries)
        {
            var at = entry.IndexOf('=');
            var parts = (at < 0 ? "" : entry[(at + 1)..]).Split('|', StringSplitOptions.TrimEntries);
            var options = parts.Skip(1).Where(o => o.Length > 0).ToList();
            if (at < 0 || parts[0].Length == 0 || !KnownFolders.TryParse(entry[..at], out var name, out var ids))
            {
                problems.Add($"FolderRedirection '{entry}' is not like Documents=OneDrive\\Documents (folders: {string.Join(", ", KnownFolders.Names)}, or an ID in braces)");
            }
            else if (options.FirstOrDefault(o => !o.Equals("move", StringComparison.OrdinalIgnoreCase)) is { } unknown)
            {
                problems.Add($"FolderRedirection '{entry}': '{unknown}' is not an option (move)");
            }
            else if (rules.Any(r => r.Ids[0] == ids[0]))
            {
                problems.Add($"FolderRedirection '{entry}': {name} is redirected twice; the first counts");
            }
            else
            {
                rules.Add(new(name, ids, parts[0], options.Count > 0));
            }
        }
        return rules;
    }
}

/// <summary>
/// Moves known folders into the volume and back. Only while the drive is mounted: at unmount each folder goes back to where it was, so
/// Windows never starts a session with a desktop that isn't there yet; not on a restart, where the new agent takes them over. After a
/// crash the next start takes them over again: what it moved is kept in a state file, so nothing goes back wrongly. A folder that
/// something else moved (OneDrive's folder backup, a Folder Redirection policy, the user) is left alone.
/// </summary>
public sealed class FolderRedirection(IKnownFolderStore folders, string statePath, Func<string, bool> onVolume)
{
    sealed record Moved(string Folder, Guid[] Ids, string Original, string Target);

    /// <summary>
    /// Points each folder at its target (a Windows path in the volume). Folders moved earlier whose rule is gone go back; rules not in
    /// <paramref name="targets"/> (their target isn't known now) keep what they have. Returns what was done, what was left and why, and
    /// the local folders whose content moves (rules with move).
    /// </summary>
    public (IReadOnlyList<string> Done, IReadOnlyList<string> Left, IReadOnlyList<(string From, string To)> Moves) Apply(
        IReadOnlyList<(RedirectRule Rule, string Target)> targets, IReadOnlyList<RedirectRule> rules)
    {
        var state = Load();
        var (done, left, moves) = (new List<string>(), new List<string>(), new List<(string, string)>());
        foreach (var (rule, target) in targets)
        {
            var key = rule.Ids[0].ToString();
            var current = folders.Get(rule.Ids[0]);
            state.TryGetValue(key, out var moved);
            if (Same(current, target))
            {
                state[key] = moved ?? new(rule.Folder, rule.Ids, folders.Default(rule.Ids[0]) ?? target, target);
                continue;
            }
            var ours = current is null || onVolume(current) || Same(current, folders.Default(rule.Ids[0]))
                || (moved is not null && (Same(current, moved.Original) || Same(current, moved.Target)));
            if (!ours)
            {
                left.Add($"{rule.Folder} is at {current}, moved there by something else (OneDrive's folder backup, a Folder Redirection policy, the user); VFS365 leaves it");
                continue;
            }
            var original = moved?.Original ?? (current is not null && !onVolume(current) ? current : folders.Default(rule.Ids[0]));
            try
            {
                foreach (var id in rule.Ids)
                {
                    folders.Set(id, target);
                }
            }
            catch (Exception e) when (e is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or IOException or ArgumentException)
            {
                left.Add($"{rule.Folder}: could not move it to {target}: {e.Message}");
                continue;
            }
            state[key] = new(rule.Folder, rule.Ids, original ?? target, target);
            done.Add($"{rule.Folder} is now {target}");
            if (rule.Move && original is not null && !onVolume(original))
            {
                moves.Add((original, target));
            }
        }
        foreach (var (key, moved) in state.Where(s => !rules.Any(r => r.Ids[0].ToString() == s.Key)).ToList())
        {
            if (PutBack(moved))
            {
                done.Add($"{moved.Folder} is back at {moved.Original} (no longer in policy FolderRedirection)");
            }
            state.Remove(key);
        }
        Save(state);
        if (done.Count > 0)
        {
            folders.Refresh();
        }
        return (done, left, moves);
    }

    /// <summary>Every folder moved into the volume goes back where it was (at unmount). Returns what was done.</summary>
    public IReadOnlyList<string> RestoreAll()
    {
        var done = Load().Values.Where(PutBack).Select(m => $"{m.Folder} is back at {m.Original}").ToList();
        Save([]);
        if (done.Count > 0)
        {
            folders.Refresh();
        }
        return done;
    }

    /// <summary>Back to the original when the folder is still where VFS365 put it.</summary>
    bool PutBack(Moved moved)
    {
        if (!Same(folders.Get(moved.Ids[0]), moved.Target))
        {
            return false;
        }
        foreach (var id in moved.Ids)
        {
            folders.Set(id, moved.Original);
        }
        return true;
    }

    /// <summary>
    /// Moves what <paramref name="from"/> holds into <paramref name="to"/>; a name that exists there stays where it is, as do links and
    /// desktop.ini (the local folder's own look). Returns what stayed and why.
    /// </summary>
    public static IReadOnlyList<string> MoveContents(string from, string to)
    {
        var stayed = new List<string>();
        if (!Directory.Exists(from))
        {
            return stayed;
        }
        foreach (var item in new DirectoryInfo(from).EnumerateFileSystemInfos())
        {
            var destination = Path.Combine(to, item.Name);
            if (item.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) || item.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }
            if (File.Exists(destination) || Directory.Exists(destination))
            {
                stayed.Add($"{item.FullName} stays: {destination} exists");
                continue;
            }
            try
            {
                if (item is DirectoryInfo directory)
                {
                    MoveDirectory(directory, destination);
                }
                else
                {
                    ((FileInfo)item).MoveTo(destination);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                stayed.Add($"{item.FullName} stays: {e.Message}");
            }
        }
        return stayed;
    }

    // Across volumes: copied and deleted file by file (Directory.Move works on one volume only); a folder goes once it is empty
    static void MoveDirectory(DirectoryInfo source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var item in source.EnumerateFileSystemInfos().Where(i => !i.Attributes.HasFlag(FileAttributes.ReparsePoint)))
        {
            if (item is DirectoryInfo directory)
            {
                MoveDirectory(directory, Path.Combine(destination, item.Name));
            }
            else
            {
                ((FileInfo)item).MoveTo(Path.Combine(destination, item.Name));
            }
        }
        if (!source.EnumerateFileSystemInfos().Any())
        {
            source.Delete();
        }
    }

    static bool Same(string? a, string? b) =>
        a is not null && b is not null && a.TrimEnd('\\').Equals(b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    Dictionary<string, Moved> Load()
    {
        try
        {
            return File.Exists(statePath) ? JsonSerializer.Deserialize<Dictionary<string, Moved>>(File.ReadAllText(statePath)) ?? [] : [];
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    void Save(Dictionary<string, Moved> state)
    {
        if (state.Count == 0)
        {
            if (File.Exists(statePath))
            {
                File.Delete(statePath);
            }
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        var temporary = statePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state));
        File.Move(temporary, statePath, overwrite: true);
    }
}

/// <summary>Paths in policies for VFS365 folders, and the Windows paths that reach them.</summary>
public static class VolumePaths
{
    /// <summary>
    /// The path from the volume root: a relative path as it is; one on the drive's letter or below \\VFS365\&lt;user&gt; without that part;
    /// null for a path elsewhere.
    /// </summary>
    public static string? Relative(string path, string unc, string? letter)
    {
        var trimmed = path.Trim();
        if (trimmed.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return trimmed.Equals(unc, StringComparison.OrdinalIgnoreCase) ? ""
                : trimmed.StartsWith(unc + @"\", StringComparison.OrdinalIgnoreCase) ? trimmed[(unc.Length + 1)..] : null;
        }
        if (trimmed.Length >= 2 && trimmed[1] == ':')
        {
            return letter is not null && trimmed.StartsWith(letter, StringComparison.OrdinalIgnoreCase) ? trimmed[2..].TrimStart('\\', '/') : null;
        }
        return trimmed;
    }

    /// <summary>The Windows path of a volume path ("\OneDrive\Desktop"): on <paramref name="letter"/> ("M:") when given, else below <paramref name="unc"/>.</summary>
    public static string Windows(string volumePath, string unc, string? letter) =>
        volumePath == @"\" ? letter is null ? unc : letter + @"\" : (letter ?? unc) + volumePath;

    /// <summary>True for paths on the drive's letter or below \\VFS365.</summary>
    public static bool OnVolume(string path, string unc, string? letter) =>
        Relative(path, unc, letter) is not null && (path.StartsWith(@"\\", StringComparison.Ordinal) || path.Length >= 2 && path[1] == ':');
}
