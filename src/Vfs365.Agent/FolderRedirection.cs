using System.ComponentModel;
using System.Runtime.InteropServices;
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

/// <summary>What a local folder still holds for its redirected place (a rule with move): <paramref name="From"/> the local folder, <paramref name="To"/> its place in the volume.</summary>
public sealed record PendingMove(string Folder, string From, string To);

/// <summary>
/// Moves known folders into the volume and back. Only while the drive is mounted: at unmount each folder goes back to where it was, so
/// Windows never starts a session with a desktop that isn't there yet; not on a restart, where the new agent takes them over. After a
/// crash the next start takes them over again: what it moved is kept in a state file, so nothing goes back wrongly. A folder that
/// something else moved (OneDrive's folder backup, a Folder Redirection policy, the user) is left alone. The state file is written
/// (and flushed to disk) before a folder moves, so no folder is ever in the volume without a record; one that can't be read is never
/// overwritten or deleted. Thread-safe; after <see cref="RestoreAll"/> nothing moves any more.
/// </summary>
public sealed class FolderRedirection(IKnownFolderStore folders, string statePath, Func<string, bool> onVolume)
{
    public const string StateFileName = "redirection.json";

    /// <summary>A folder moved into the volume: its IDs, where it was and where it is. MovePending: its content (move) hasn't all moved yet.</summary>
    internal sealed record Moved(string Folder, Guid[] Ids, string Original, string Target, bool MovePending = false);

    readonly Lock gate = new();

    /// <summary>Folders this instance moved or found moved already (after a crash): Explorer is told once about the ones it found.</summary>
    readonly HashSet<string> seen = new(StringComparer.Ordinal);

    volatile bool closed;

    /// <summary>How long RestoreAll waits for a run that is moving a folder (the shell can be slow to answer).</summary>
    static readonly TimeSpan RestoreWait = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Points each folder at its target (a Windows path in the volume). Folders moved earlier whose rule is gone go back; rules not in
    /// <paramref name="targets"/> (their target isn't known now) keep what they have. Returns what was done, what was left and why, and
    /// the content still to move (rules with move). Stops between folders when <paramref name="ct"/> is cancelled (unmount). Throws
    /// IOException when the state file can't be read now; nothing changes then.
    /// </summary>
    public (IReadOnlyList<string> Done, IReadOnlyList<string> Left, IReadOnlyList<PendingMove> Moves) Apply(
        IReadOnlyList<(RedirectRule Rule, string Target)> targets, IReadOnlyList<RedirectRule> rules, CancellationToken ct = default)
    {
        lock (gate)
        {
            var (done, left) = (new List<string>(), new List<string>());
            if (closed)
            {
                return (done, left, []);
            }
            var state = Load();
            var refresh = false;
            foreach (var (rule, target) in targets)
            {
                if (ct.IsCancellationRequested || closed)
                {
                    break;
                }
                var key = rule.Ids[0].ToString();
                var current = folders.Get(rule.Ids[0]);
                state.TryGetValue(key, out var moved);
                if (Same(current, target))
                {
                    var kept = moved ?? new(rule.Folder, rule.Ids, folders.Default(rule.Ids[0]) ?? target, target);
                    state[key] = kept with { MovePending = kept.MovePending && rule.Move };
                    refresh |= seen.Add(key); // there already (a crash, a restart): Explorer may still show it as unavailable
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
                state[key] = new(rule.Folder, rule.Ids, original ?? target, target, rule.Move && original is not null && !onVolume(original));
                try
                {
                    Save(state); // recorded before the folder moves
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Revert(state, key, moved);
                    left.Add($"{rule.Folder}: not moved, VFS365 could not record it in {statePath}: {e.Message}");
                    continue;
                }
                if (Move(rule, target, original) is { } problem)
                {
                    left.Add(problem.Why);
                    if (!problem.Partly)
                    {
                        Revert(state, key, moved); // nothing moved: the record goes (saved below)
                    }
                    continue;
                }
                seen.Add(key);
                refresh = true;
                done.Add($"{rule.Folder} is now {target}");
            }
            foreach (var (key, moved) in state.Where(s => !rules.Any(r => r.Ids[0].ToString() == s.Key)).ToList())
            {
                try
                {
                    if (PutBack(moved))
                    {
                        done.Add($"{moved.Folder} is back at {moved.Original} (no longer in policy FolderRedirection)");
                        refresh = true;
                    }
                    state.Remove(key);
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    left.Add($"{moved.Folder}: could not put it back at {moved.Original}: {e.Message}");
                }
            }
            Save(state);
            if (refresh)
            {
                folders.Refresh();
            }
            return (done, left, Pending(state));
        }
    }

    /// <summary>
    /// Every folder moved into the volume goes back where it was (at unmount), each on its own: one that fails stays recorded for the
    /// next start or the uninstall. Nothing moves after this. Returns what was done and what failed.
    /// </summary>
    public (IReadOnlyList<string> Done, IReadOnlyList<string> Problems) RestoreAll()
    {
        closed = true; // a run going on stops before its next folder
        if (!gate.TryEnter(RestoreWait))
        {
            return ([], ["redirected folders stay where they are: Windows didn't finish moving one within 10 s; the next start or the uninstall puts them back"]);
        }
        try
        {
            var (done, problems) = (new List<string>(), new List<string>());
            Dictionary<string, Moved> state;
            try
            {
                state = Load();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                problems.Add($"redirected folders stay where they are: {statePath} can't be read ({e.Message}); the next start or the uninstall puts them back");
                return (done, problems);
            }
            var failed = new Dictionary<string, Moved>(StringComparer.Ordinal);
            foreach (var (key, moved) in state)
            {
                try
                {
                    if (PutBack(moved))
                    {
                        done.Add($"{moved.Folder} is back at {moved.Original}");
                    }
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    failed[key] = moved;
                    problems.Add($"{moved.Folder} stays at {moved.Target}: could not put it back at {moved.Original}: {e.Message}");
                }
            }
            try
            {
                Save(failed);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                problems.Add($"could not update {statePath}: {e.Message}");
            }
            if (done.Count > 0)
            {
                folders.Refresh();
            }
            return (done, problems);
        }
        finally
        {
            gate.Exit();
        }
    }

    /// <summary>Content still to move into folders that are redirected now, also what a stop or crash left halfway; empty after RestoreAll.</summary>
    public IReadOnlyList<PendingMove> PendingMoves()
    {
        lock (gate)
        {
            try
            {
                return closed ? [] : Pending(Load());
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return [];
            }
        }
    }

    /// <summary>The content of <paramref name="move"/> moved as far as it goes: it isn't tried again.</summary>
    public void MoveFinished(PendingMove move)
    {
        lock (gate)
        {
            if (closed)
            {
                return;
            }
            try
            {
                var state = Load();
                var (key, moved) = state.FirstOrDefault(s => s.Value.MovePending && s.Value.Folder == move.Folder && Same(s.Value.Target, move.To));
                if (moved is not null)
                {
                    state[key] = moved with { MovePending = false };
                    Save(state);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Tried again at the next start: what moved already stays where it is
            }
        }
    }

    List<PendingMove> Pending(Dictionary<string, Moved> state) =>
        state.Values.Where(m => m.MovePending && Same(folders.Get(m.Ids[0]), m.Target) && !onVolume(m.Original))
            .Select(m => new PendingMove(m.Folder, m.Original, m.Target)).ToList();

    static void Revert(Dictionary<string, Moved> state, string key, Moved? before)
    {
        if (before is null)
        {
            state.Remove(key);
        }
        else
        {
            state[key] = before;
        }
    }

    /// <summary>Every ID of the rule, or none: when one fails, those moved before it go back. Null when moved; else why not, and whether part stayed moved.</summary>
    (string Why, bool Partly)? Move(RedirectRule rule, string target, string? original)
    {
        var before = rule.Ids.Select(folders.Get).ToArray();
        var set = 0;
        try
        {
            for (; set < rule.Ids.Length; set++)
            {
                folders.Set(rule.Ids[set], target);
            }
            return null;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            var why = $"{rule.Folder}: could not move it to {target}: {e.Message}";
            for (var i = set - 1; i >= 0; i--)
            {
                try
                {
                    if ((before[i] ?? original) is { } back)
                    {
                        folders.Set(rule.Ids[i], back);
                    }
                }
                catch (Exception rollback) when (rollback is not OutOfMemoryException)
                {
                    return ($"{why}; part of it stays there until the next try ({rollback.Message})", true);
                }
            }
            return (why, false);
        }
    }

    /// <summary>Back to the original, each ID still where VFS365 put it; nothing when someone else moved the folder since. True when one went back.</summary>
    bool PutBack(Moved moved)
    {
        var first = folders.Get(moved.Ids[0]);
        if (!Same(first, moved.Target) && !Same(first, moved.Original))
        {
            return false;
        }
        var back = false;
        foreach (var id in moved.Ids)
        {
            if (Same(folders.Get(id), moved.Target))
            {
                folders.Set(id, moved.Original);
                back = true;
            }
        }
        return back;
    }

    /// <summary>
    /// Moves what <paramref name="from"/> holds into <paramref name="to"/>, item by item: a file is copied, flushed to disk and only then
    /// removed locally (MoveFileEx with write-through), so a stop or crash halfway loses nothing; folders merge into ones that exist. Stay
    /// local, with the reason: names that exist there, links, Outlook data files (.pst, .ost: Outlook keeps their path), names SharePoint
    /// refuses and paths longer than it takes, and whatever fails (a file in use). desktop.ini and Thumbs.db stay without a word (the
    /// local folder's own look). Throws OperationCanceledException when <paramref name="ct"/> stops it; what moved stays moved.
    /// </summary>
    public static IReadOnlyList<string> MoveContents(string from, string to, CancellationToken ct = default)
    {
        var stayed = new List<string>();
        if (Directory.Exists(from))
        {
            MoveFolder(new DirectoryInfo(from), to, Path.GetPathRoot(to)?.Length ?? 0, stayed, ct);
        }
        return stayed;
    }

    static void MoveFolder(DirectoryInfo source, string destination, int rootLength, List<string> stayed, CancellationToken ct)
    {
        List<FileSystemInfo> items;
        try
        {
            items = source.EnumerateFileSystemInfos().ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            stayed.Add($"{source.FullName} stays: {e.Message}");
            return;
        }
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.Combine(destination, item.Name);
            if (item.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) || item.Name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (StaysLocal(item, target.Length - rootLength) is { } reason)
            {
                stayed.Add($"{item.FullName} stays: {reason}");
                continue;
            }
            try
            {
                if (item is DirectoryInfo folder)
                {
                    if (File.Exists(target))
                    {
                        stayed.Add($"{item.FullName} stays: {target} is a file");
                        continue;
                    }
                    Directory.CreateDirectory(target); // or the one there already, which takes what isn't in it yet
                    MoveFolder(folder, target, rootLength, stayed, ct);
                    if (!folder.EnumerateFileSystemInfos().Any())
                    {
                        folder.Delete();
                    }
                }
                else if (File.Exists(target) || Directory.Exists(target))
                {
                    stayed.Add($"{item.FullName} stays: {target} exists");
                }
                else if (MoveFile(item.FullName, target, ct) is { } problem)
                {
                    stayed.Add(problem);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception)
            {
                stayed.Add($"{item.FullName} stays: {e.Message}");
            }
        }
    }

    /// <summary>Why an item isn't moved into the volume, or null. <paramref name="pathLength"/>: its path below the volume's root, about SharePoint's.</summary>
    static string? StaysLocal(FileSystemInfo item, int pathLength)
    {
        if (item.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return "it is a link";
        }
        if (item is FileInfo && (item.Extension.Equals(".pst", StringComparison.OrdinalIgnoreCase) || item.Extension.Equals(".ost", StringComparison.OrdinalIgnoreCase)))
        {
            return "an Outlook data file, which Outlook keeps by its path (move it in Outlook)";
        }
        return SharePointNames.Refused(item.Name, pathLength);
    }

    /// <summary>Copied with write-through, then the local file goes (MoveFileEx across volumes); null when moved, else what happened.</summary>
    static string? MoveFile(string from, string to, CancellationToken ct)
    {
        CopyProgress progress = (_, _, _, _, _, _, _, _, _) => ct.IsCancellationRequested ? 1u : 0u; // PROGRESS_CANCEL stops a large copy
        var moved = MoveFileWithProgressW(LongPath(from), LongPath(to), progress, IntPtr.Zero, CopyAllowed | WriteThrough);
        var error = Marshal.GetLastPInvokeError();
        GC.KeepAlive(progress);
        if (!moved)
        {
            ct.ThrowIfCancellationRequested();
            return $"{from} stays: {new Win32Exception(error).Message}";
        }
        // Across volumes Windows keeps the source when it can't delete it after the copy
        return File.Exists(from) ? $"{from} is copied to {to}, but stays here too: it is in use" : null;
    }

    const uint CopyAllowed = 0x2, WriteThrough = 0x8;

    static string LongPath(string path) =>
        path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path
        : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..]
        : @"\\?\" + path;

    delegate uint CopyProgress(long totalSize, long transferred, long streamSize, long streamTransferred, uint stream, uint reason, IntPtr source,
        IntPtr destination, IntPtr data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool MoveFileWithProgressW(string existing, string target, CopyProgress progress, IntPtr data, uint flags);

    static bool Same(string? a, string? b) =>
        a is not null && b is not null && a.TrimEnd('\\').Equals(b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The state; empty without a file. A file that isn't valid is set aside under another name (redirection.json.unreadable-...), never
    /// overwritten; one that can't be opened now (locked) throws IOException, so nothing is saved over it.
    /// </summary>
    Dictionary<string, Moved> Load()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (!File.Exists(statePath))
                {
                    return new(StringComparer.Ordinal);
                }
                if (Parse(File.ReadAllText(statePath)) is { } state)
                {
                    return state;
                }
                File.Move(statePath, $"{statePath}.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}");
                return new(StringComparer.Ordinal);
            }
            catch (IOException) when (attempt < 2)
            {
                Thread.Sleep(100); // antivirus or a backup reading it
            }
        }
    }

    /// <summary>A state file's records by folder; null when it isn't one.</summary>
    static Dictionary<string, Moved>? Parse(string json)
    {
        try
        {
            var state = JsonSerializer.Deserialize<Dictionary<string, Moved>>(json);
            return state is null || state.Values.Any(m => m is null || m.Ids is not { Length: > 0 } || m.Original is null || m.Target is null || m.Folder is null)
                ? null
                : new(state, StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The records in a state file's text, for the uninstall; empty when it isn't one.</summary>
    internal static IReadOnlyList<Moved> ParseState(string json) => Parse(json)?.Values.ToList() ?? [];

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
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, state);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, statePath, overwrite: true);
    }
}

/// <summary>Names and paths SharePoint refuses (Microsoft's restrictions for OneDrive and SharePoint), for content moved into the volume.</summary>
public static class SharePointNames
{
    /// <summary>Most characters SharePoint takes for a decoded path, the file name included.</summary>
    public const int MaxPath = 400;

    static readonly HashSet<string> Reserved = new(
        ["CON", "PRN", "AUX", "NUL", .. Enumerable.Range(0, 10).SelectMany(n => new[] { $"COM{n}", $"LPT{n}" })], StringComparer.OrdinalIgnoreCase);

    /// <summary>Why SharePoint won't take the name, or a path of <paramref name="pathLength"/> characters; null when it will.</summary>
    public static string? Refused(string name, int pathLength)
    {
        if (name.StartsWith("~$", StringComparison.Ordinal))
        {
            return "SharePoint refuses names starting with ~$ (Office's owner files)";
        }
        if (name.Contains("_vti_", StringComparison.OrdinalIgnoreCase))
        {
            return "SharePoint refuses names with _vti_";
        }
        if (name.Equals(".lock", StringComparison.OrdinalIgnoreCase) || Reserved.Contains(name.Split('.')[0].TrimEnd()))
        {
            return $"SharePoint refuses the name {name}";
        }
        return pathLength > MaxPath ? $"its path would be {pathLength} characters, more than the {MaxPath} SharePoint takes" : null;
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
