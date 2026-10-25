using System.Runtime.CompilerServices;
using Vfs365.Core.Drive;
using Vfs365.Tests.Drive;

namespace Vfs365.Tests.Mounted;

/// <summary>
/// The fake drive with calls made slow by name, like a slow uplink or a throttled lookup: each delay is asked per call, so a test
/// can slow one file's upload while the rest go at once. Names are item names (the target's, or the existing item's).
/// </summary>
sealed class SlowDriveApi(FakeDriveApi inner) : IDriveApi
{
    public Func<string, TimeSpan>? UploadDelay { get; set; }
    public Func<string, TimeSpan>? DownloadDelay { get; set; }

    /// <summary>By drive path ('/'-separated).</summary>
    public Func<string, TimeSpan>? LookupDelay { get; set; }

    /// <summary>By folder drive path.</summary>
    public Func<string, TimeSpan>? ListDelay { get; set; }
    public Func<string, TimeSpan>? DeleteDelay { get; set; }
    public Func<string, TimeSpan>? MoveDelay { get; set; }

    /// <summary>Holds every download until it completes (null: none held).</summary>
    public TaskCompletionSource? HoldDownloads { get; set; }

    public int Uploads, Downloads;

    static Task Wait(Func<string, TimeSpan>? delay, string name, CancellationToken ct) =>
        delay?.Invoke(name) is { } wait && wait > TimeSpan.Zero ? Task.Delay(wait, ct) : Task.CompletedTask;

    async Task<string> NameOf(UploadTarget target) =>
        target.Name ?? (target.ItemId is { } id ? (await inner.GetItemByIdAsync("", id, default))?.Name : null) ?? "";

    async Task<string> NameOfId(string itemId) => (await inner.GetItemByIdAsync("", itemId, default))?.Name ?? "";

    public async IAsyncEnumerable<IReadOnlyList<DriveItemInfo>> ListChildrenAsync(string driveId, string folderPath, [EnumeratorCancellation] CancellationToken ct)
    {
        await Wait(ListDelay, folderPath, ct);
        await foreach (var page in inner.ListChildrenAsync(driveId, folderPath, ct))
        {
            yield return page;
        }
    }

    public Task<string> GetLatestDeltaLinkAsync(string driveId, CancellationToken ct) => inner.GetLatestDeltaLinkAsync(driveId, ct);

    public Task<DeltaPage> GetDeltaAsync(string link, CancellationToken ct) => inner.GetDeltaAsync(link, ct);

    public async Task<DriveItemInfo?> GetItemAsync(string driveId, string path, CancellationToken ct)
    {
        await Wait(LookupDelay, path, ct);
        return await inner.GetItemAsync(driveId, path, ct);
    }

    public Task<DriveItemInfo?> GetItemByIdAsync(string driveId, string itemId, CancellationToken ct) => inner.GetItemByIdAsync(driveId, itemId, ct);

    public async Task<Stream> OpenReadAsync(string driveId, string itemId, string? contentTag, long offset, long length, CancellationToken ct)
    {
        Interlocked.Increment(ref Downloads);
        if (HoldDownloads is { } hold)
        {
            await hold.Task.WaitAsync(ct);
        }
        await Wait(DownloadDelay, await NameOfId(itemId), ct);
        return await inner.OpenReadAsync(driveId, itemId, contentTag, offset, length, ct);
    }

    /// <summary>
    /// Reads the file as DriveApi does (sharing read, write and delete: the volume's handle on a staging file is still open when its
    /// close uploads), then hands the fake a copy, which reads with FileShare.Read only.
    /// </summary>
    public async Task<DriveItemInfo> UploadAsync(string driveId, UploadTarget target, string sourceFile, CancellationToken ct)
    {
        Interlocked.Increment(ref Uploads);
        await Wait(UploadDelay, await NameOf(target), ct);
        var copy = Path.Combine(Path.GetTempPath(), $"vfs365-sent-{Guid.NewGuid():N}");
        try
        {
            using (var source = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sent = new FileStream(copy, FileMode.CreateNew, FileAccess.Write))
            {
                await source.CopyToAsync(sent, ct);
            }
            return await inner.UploadAsync(driveId, target, copy, ct);
        }
        finally
        {
            File.Delete(copy);
        }
    }

    public async Task<UploadSession> CreateUploadSessionAsync(string driveId, UploadTarget target, CancellationToken ct)
    {
        Interlocked.Increment(ref Uploads);
        await Wait(UploadDelay, await NameOf(target), ct);
        return await inner.CreateUploadSessionAsync(driveId, target, ct);
    }

    /// <summary>Per fragment of an upload session.</summary>
    public TimeSpan FragmentDelay { get; set; }

    public async Task<DriveItemInfo?> UploadFragmentAsync(UploadSession session, string sourceFile, long offset, long length, long totalSize, CancellationToken ct)
    {
        await Task.Delay(FragmentDelay, ct);
        return await inner.UploadFragmentAsync(session, sourceFile, offset, length, totalSize, ct);
    }

    public Task CancelUploadSessionAsync(UploadSession session, CancellationToken ct) => inner.CancelUploadSessionAsync(session, ct);

    public Task<DriveItemInfo> CreateFolderAsync(string driveId, string parentPath, string name, CancellationToken ct) => inner.CreateFolderAsync(driveId, parentPath, name, ct);

    public async Task<DriveItemInfo> MoveAsync(string driveId, string itemId, string newParentId, string newName, CancellationToken ct)
    {
        await Wait(MoveDelay, await NameOfId(itemId), ct);
        return await inner.MoveAsync(driveId, itemId, newParentId, newName, ct);
    }

    public async Task DeleteAsync(string driveId, string itemId, CancellationToken ct)
    {
        await Wait(DeleteDelay, await NameOfId(itemId), ct);
        await inner.DeleteAsync(driveId, itemId, ct);
    }
}
