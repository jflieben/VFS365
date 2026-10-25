using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Vfs365.Core.Drive;

/// <summary>Names of the copies the engine makes next to a file: "Offer (conflict 2026-10-25 143012).docx", "(recovered ...)" after a crash.</summary>
public static partial class ConflictCopies
{
    /// <summary>The name of the file a copy was made of ("Offer.docx"), null when <paramref name="name"/> isn't a copy's name.</summary>
    public static string? OriginalName(string name) =>
        CopyName().Match(name) is { Success: true } match ? match.Groups["stem"].Value + match.Groups["ext"].Value : null;

    [GeneratedRegex(@"^(?<stem>.*) \((?:conflict|recovered) \d{4}-\d{2}-\d{2} \d{6}(?: \d+)?\)(?<ext>\.[^.]*)?$")]
    private static partial Regex CopyName();
}

public sealed partial class DriveEngine
{
    /// <summary>Conflict copies made in this run, by drive and the copy's item ID: the content tag of the version that won then.</summary>
    readonly ConcurrentDictionary<string, string> conflictWinners = new(StringComparer.Ordinal);

    void ConflictCopyMade(string driveId, DriveItemInfo copy, DriveItemInfo? winner)
    {
        if ((winner?.CTag ?? winner?.ETag) is { } tag)
        {
            conflictWinners[Key(driveId, copy.Id)] = tag;
        }
    }

    /// <summary>
    /// Keep this version: the content of a conflict copy becomes a new version of the file it was made of (a replace through
    /// <see cref="RenameAsync"/>: an upload into the original item with If-Match, so the other version stays in its version history),
    /// and the copy goes to the recycle bin. Throws RemoteException(Conflict) when the original changed again since the copy was made
    /// (known for copies made in this run) or since it was listed. Returns the original's volume path.
    /// </summary>
    public async Task<string> KeepVersionAsync(string copyPath, CancellationToken ct)
    {
        var copy = await GetEntryAsync(copyPath, ct) ?? throw new FsException(FsError.NotFound, copyPath);
        if (copy.IsDirectory || copy.Drive is null || ConflictCopies.OriginalName(copy.Name) is not { } name)
        {
            throw new FsException(FsError.Invalid, $"{copyPath} is not a conflict copy");
        }
        var originalPath = copy.Path[..(copy.Path.LastIndexOf('\\') + 1)] + name;
        var original = await GetEntryAsync(originalPath, ct);
        var driveId = await copy.Drive.GetDriveIdAsync(ct);
        if (original is not null && copy.ItemId is { } copyId && conflictWinners.TryGetValue(Key(driveId, copyId), out var winner) && original.ContentTag != winner)
        {
            throw new RemoteException(RemoteError.Conflict, $"412 {name} changed in Microsoft 365 again after {copy.Name} was made");
        }
        await RenameAsync(copy.Path, originalPath, replace: true, ct);
        if (copy.ItemId is { } kept)
        {
            conflictWinners.TryRemove(Key(driveId, kept), out _);
        }
        Log($"kept {copy.Name} as the new version of {originalPath}");
        return originalPath;
    }
}
