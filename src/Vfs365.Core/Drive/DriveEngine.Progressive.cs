namespace Vfs365.Core.Drive;

/// <summary>
/// Large files written front to back, whose final size was set first (Windows' copy engine does both), upload while they are written:
/// each write waits while the previous fragment is still uploading, so the writer's progress follows the network and the close
/// only sends the last fragment. Anything else falls back to a whole-file upload on close.
/// </summary>
public sealed partial class DriveEngine
{
    /// <summary>The writer set the file's size. Before any data, on a large file, this starts a progressive upload.</summary>
    public void SizeSet(FsEntry file, long size)
    {
        if (PendingOf(file) is not { } pending || LocalOnlyNames.IsLocalOnly(pending.Name))
        {
            return;
        }
        if (pending.Progress is { } progress)
        {
            if (progress.Size != size)
            {
                _ = AbandonAsync(pending, progress);
            }
            return;
        }
        if (!pending.HasWrites && size > UploadSession.SimpleUploadLimit)
        {
            pending.Progress = new ProgressiveUpload(size, pending.DrivePath);
        }
    }

    /// <summary>Called after each write. Sends complete fragments; waits while the previous one is still uploading.</summary>
    public async Task WrittenAsync(FsEntry file, long offset, long length, CancellationToken ct)
    {
        if (PendingOf(file) is not { } pending)
        {
            return;
        }
        pending.HasWrites = true;
        if (pending.Progress is not { Broken: false } progress)
        {
            return;
        }

        await progress.Gate.WaitAsync(ct);
        try
        {
            if (progress.Broken)
            {
                return;
            }
            if (!progress.Add(offset, offset + length) || offset + length > progress.Size)
            {
                await AbandonAsync(pending, progress);
                return;
            }

            // The fragment that ends the file completes the item, so it waits for the close (write-through)
            const int fragment = UploadSession.FragmentSize;
            while (progress.Contiguous - progress.Sent >= fragment && progress.Sent + fragment < progress.Size)
            {
                if (progress.InFlight is { } previous)
                {
                    await previous;
                }
                progress.Session ??= await api.CreateUploadSessionAsync(pending.DriveId, TargetOf(pending), ct);
                var start = progress.Sent;
                progress.Sent += fragment;
                progress.InFlight = api.UploadFragmentAsync(progress.Session, pending.StagingPath, start, fragment, progress.Size, CancellationToken.None);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log($"streaming upload of {pending.DrivePath} stopped, uploading on close instead: {e.Message}");
            await AbandonAsync(pending, progress);
        }
        finally
        {
            progress.Gate.Release();
        }
    }

    /// <summary>Sends the rest of a progressive upload. Null when there is none to finish (the caller uploads the whole file).</summary>
    async Task<DriveItemInfo?> FinishProgressiveAsync(PendingFile pending, CancellationToken ct)
    {
        if (pending.Progress is not { } progress)
        {
            return null;
        }
        await progress.Gate.WaitAsync(ct);
        try
        {
            var usable = !progress.Broken && progress.Session is not null && progress.Contiguous == progress.Size
                && new FileInfo(pending.StagingPath).Length == progress.Size
                && pending.DrivePath.Equals(progress.DrivePath, StringComparison.OrdinalIgnoreCase);
            if (!usable)
            {
                await AbandonAsync(pending, progress);
                return null;
            }
            if (progress.InFlight is { } previous)
            {
                await previous;
            }

            DriveItemInfo? item = null;
            for (var start = progress.Sent; start < progress.Size; start += UploadSession.FragmentSize)
            {
                item = await api.UploadFragmentAsync(progress.Session!, pending.StagingPath, start, Math.Min(UploadSession.FragmentSize, progress.Size - start), progress.Size, ct);
            }
            pending.Progress = null;
            return item ?? throw new IOException($"Upload session for {pending.DrivePath} ended without an item");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await AbandonAsync(pending, progress);
            throw;
        }
        finally
        {
            progress.Gate.Release();
        }
    }

    /// <summary>Stops a progressive upload; the file then uploads whole on close.</summary>
    async Task AbandonAsync(PendingFile pending, ProgressiveUpload progress)
    {
        progress.Broken = true;
        if (pending.Progress == progress)
        {
            pending.Progress = null;
        }
        if (progress.InFlight is { } inFlight)
        {
            progress.InFlight = null;
            try
            {
                await inFlight;
            }
            catch (Exception)
            {
            }
        }
        if (progress.Session is { } session)
        {
            progress.Session = null;
            try
            {
                await api.CancelUploadSessionAsync(session, CancellationToken.None);
            }
            catch (Exception)
            {
            }
        }
    }

    PendingFile? PendingOf(FsEntry file) =>
        file.DriveId is not null ? overlay.GetValueOrDefault(Key(file.DriveId, file.DrivePath)) as PendingFile : null;

    static UploadTarget TargetOf(PendingFile pending) =>
        pending.Base is { } baseItem ? UploadTarget.Existing(baseItem) : UploadTarget.New(ParentOf(pending.DrivePath), pending.Name);
}
