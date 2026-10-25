using System.Buffers;
using Microsoft.Win32.SafeHandles;

namespace Vfs365.Core.Drive;

/// <summary>
/// One item version downloaded on demand into a sparse local file in 1 MiB blocks. Each 64 KiB chunk can be read as soon as it is
/// written (encrypted: sealed), so a read waits only for its own chunks.
/// A sequential reader is fed by one streaming request that stays ahead of it, 1 block at first and up to 8 while the reader follows;
/// a read elsewhere opens its own ranged request, and when MaxStreams are open already, the one that waited longest for a reader makes room.
/// Streams pause when nobody reads near them and stop after a while, so peeking at a big file doesn't download all of it.
/// Every stream asks for the version the listing named, so one download never mixes two versions.
/// A failed stream is tried again after 1 and 2 s; reads fail only when all attempts did (and again after a cool-down), or for good
/// when the content served isn't that version or isn't Length bytes (then <c>contentChanged</c> gets the item as served). A stream that
/// brings no bytes for the no-progress timeout counts as a failed attempt, whether or not a reader is waiting for it.
/// </summary>
internal sealed class StreamingContent : IContentSource
{
    public const int BlockSize = 1 << 20;

    /// <summary>The unit readers get as soon as it is written: the cipher's chunk, also read from a stream at a time.</summary>
    const int ChunkSize = CacheCipher.ChunkSize;
    const int ChunksPerBlock = BlockSize / ChunkSize;
    const int ReadAhead = 8;
    const int MaxStreams = 3;
    const int MaxFailures = 3;
    static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);
    static readonly TimeSpan IdleStop = TimeSpan.FromSeconds(30);
    static readonly TimeSpan FailureCooldown = TimeSpan.FromSeconds(10);

    /// <summary>
    /// One streaming request. Demand is the furthest block its readers asked for; it runs at most Window blocks past that, a window
    /// that starts at 1 (a random read pulls little) and doubles up to ReadAhead each time a reader follows it.
    /// ActiveAt is when a reader last wanted more or a block arrived: its own idle time, which other streams don't extend.
    /// ProgressAt is when it started or last got bytes; Flowing once it got any.
    /// </summary>
    sealed class Runner
    {
        public Runner(int next, int demand, CancellationToken disposed)
        {
            (Next, Demand) = (next, demand);
            Stop = CancellationTokenSource.CreateLinkedTokenSource(disposed);
            Stall = CancellationTokenSource.CreateLinkedTokenSource(Stop.Token);
        }

        public int Next { get; set; }
        public int Demand { get; set; }
        public int Window { get; set; } = 1;
        public long ActiveAt { get; set; } = Now;
        public long ProgressAt { get; set; } = Now;
        public bool Flowing { get; set; }
        public bool Parked { get; set; }

        /// <summary>Stops the stream without counting a failure (room for another, nobody reading, disposed, given up by a reader).</summary>
        public CancellationTokenSource Stop { get; }

        /// <summary>Fires when no bytes came for the no-progress timeout: a failed attempt.</summary>
        public CancellationTokenSource Stall { get; }
    }

    readonly IDriveApi api;
    readonly string driveId, itemId, partialPath;
    readonly string? contentTag;
    readonly Action<StreamingContent> completed;
    readonly Action<StreamingContent>? failed;
    readonly Action<DriveItemInfo>? contentChanged;
    readonly TimeSpan noProgress;
    readonly CacheCipher.SealedFile? sealedFile;
    readonly SafeFileHandle file;
    readonly bool inPlace;

    /// <summary>Per chunk: written (and readable), or being written by a stream.</summary>
    readonly bool[] present, claimed;
    readonly int blocks;
    readonly Lock gate = new();
    readonly List<Runner> runners = [];
    readonly CancellationTokenSource disposed = new();
    // Its creator's priority (read ahead in the background, or opened by a user); a user reading it promotes it for good
    readonly PriorityHolder priority = new(RequestPriority.IsBackground);
    readonly TaskCompletionSource done = NewSignal();
    TaskCompletionSource changed = NewSignal();
    int presentCount, failures;
    long failedAt;
    Exception? failure;
    bool finished, held;
    volatile bool moved;

    /// <param name="contentTag">The version to download (the listing's cTag, else eTag); every stream is checked against it.</param>
    /// <param name="name">The cache file's name, which its encrypted chunks are bound to.</param>
    /// <param name="failed">Called when the download gave up, so new readers can start a fresh one.</param>
    /// <param name="noProgress">A stream without bytes for this long is a failed attempt; a read waiting this long without progress fails.</param>
    /// <param name="inPlace">Downloads straight into <paramref name="finalPath"/> (plain, replacing what is there) instead of a partial file moved there when complete.</param>
    public StreamingContent(IDriveApi api, string driveId, string itemId, string? contentTag, long length, string finalPath, string name, CacheCipher? cipher,
        Action<StreamingContent> completed, Action<StreamingContent>? failed = null, Action<DriveItemInfo>? contentChanged = null, TimeSpan? noProgress = null,
        bool inPlace = false)
    {
        (this.api, this.driveId, this.itemId, this.contentTag, FinalPath, this.inPlace) = (api, driveId, itemId, contentTag, finalPath, inPlace);
        (this.completed, this.failed, this.contentChanged) = (completed, failed, contentChanged);
        this.noProgress = noProgress ?? TimeSpan.FromSeconds(90);
        Length = length;
        partialPath = inPlace ? finalPath : $"{finalPath}.{Guid.NewGuid():N}.part";
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        file = File.OpenHandle(partialPath, inPlace ? FileMode.Create : FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        try
        {
            if (!inPlace)
            {
                SparseFile.Mark(file);
            }
            if (cipher is null || inPlace)
            {
                RandomAccess.SetLength(file, length);
            }
            else
            {
                sealedFile = cipher.Create(file, length, name);
            }
        }
        catch
        {
            file.Dispose();
            File.Delete(partialPath);
            throw;
        }
        present = new bool[(int)((length + ChunkSize - 1) / ChunkSize)];
        claimed = new bool[present.Length];
        blocks = (int)((length + BlockSize - 1) / BlockSize);
    }

    public long Length { get; }

    /// <summary>Where the complete file goes (or, in place, is written).</summary>
    public string FinalPath { get; }

    /// <summary>When the download was asked for: cached versions that completed before then are older.</summary>
    public DateTime StartedAt { get; } = DateTime.UtcNow;

    /// <summary>Every chunk is here and the file went to its final path (or couldn't).</summary>
    public bool IsComplete => done.Task.IsCompleted;

    /// <summary>Opened by the content cache; it disposes this when the last reader is done (an incomplete download a little later).</summary>
    internal int References { get; set; }

    /// <summary>Counts the content cache's releases, so a delayed drop of a released download skips one that was opened again meanwhile.</summary>
    internal int Releases { get; set; }

    /// <summary>Set by the content cache when the finished file isn't kept (the item was deleted meanwhile, the cache was emptied).</summary>
    internal volatile bool Discard;

    static long Now => Environment.TickCount64;

    /// <summary>Its last reader closed: streams finish the block they are in and wait there (they stop when nobody comes back).</summary>
    internal void Released()
    {
        lock (gate)
        {
            held = true;
        }
    }

    /// <summary>Opened again after its last reader closed: streams go on, and failed attempts before don't count (a download that gave up isn't reopened).</summary>
    internal void Reopened()
    {
        lock (gate)
        {
            (held, failures) = (false, 0);
            Signal();
        }
    }

    public async ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct)
    {
        if (offset >= Length || buffer.Length == 0)
        {
            return 0;
        }
        var length = (int)Math.Min(buffer.Length, Length - offset);
        await EnsureAsync((int)(offset / ChunkSize), (int)((offset + length - 1) / ChunkSize), ct);
        return sealedFile?.Read(offset, buffer.Span[..length]) ?? RandomAccess.Read(file, buffer.Span[..length], offset);
    }

    /// <summary>Waits until every chunk is here and the file has moved to its final path.</summary>
    public async Task CompleteAsync(CancellationToken ct)
    {
        if (present.Length > 0)
        {
            await EnsureAsync(0, present.Length - 1, ct);
        }
        else
        {
            Finish();
        }
        await done.Task.WaitAsync(ct);
    }

    /// <summary>Waits until chunks <paramref name="first"/> to <paramref name="last"/> are here.</summary>
    async Task EnsureAsync(int first, int last, CancellationToken ct)
    {
        if (!RequestPriority.IsBackground)
        {
            priority.Promote();
        }
        var lastBlock = last / ChunksPerBlock;
        CancellationTokenSource? cancel = null;
        var waitingFor = -1;
        var deadline = 0L;
        var timeout = (long)noProgress.TotalMilliseconds;
        try
        {
            while (true)
            {
                Task wait;
                Runner? stop = null, stalled = null;
                var gaveUp = false;
                long left;
                lock (gate)
                {
                    var moreDemand = false;
                    foreach (var ahead in runners)
                    {
                        if (ahead.Next > lastBlock && ahead.Next <= lastBlock + ahead.Window + 1 && lastBlock > ahead.Demand)
                        {
                            Follow(ahead, lastBlock); // keeps the stream just ahead of this reader going
                            moreDemand = true;
                        }
                    }
                    var missingChunk = Array.IndexOf(present, false, first, last - first + 1);
                    if (missingChunk < 0)
                    {
                        if (moreDemand)
                        {
                            Signal();
                        }
                        return;
                    }
                    if (missingChunk != waitingFor)
                    {
                        // Progress on this read's own chunks; other streams' progress doesn't count
                        (waitingFor, deadline) = (missingChunk, Now + timeout);
                        first = missingChunk; // the chunks before it stay here: not looked at again
                    }
                    var missing = missingChunk / ChunksPerBlock;
                    var runner = runners.FirstOrDefault(r => r.Next <= missing && missing <= r.Next + r.Window);
                    if (runner is null)
                    {
                        if (failures >= MaxFailures)
                        {
                            if (failure is ContentChangedException mismatch)
                            {
                                throw new ContentChangedException(mismatch.Current, mismatch.Listed, mismatch.OtherVersion);
                            }
                            if (Now - failedAt < (long)FailureCooldown.TotalMilliseconds)
                            {
                                throw new IOException($"Download failed after {MaxFailures} attempts: {failure?.Message}", failure);
                            }
                            failures = 0; // cooled down: try again
                        }
                        if (runners.Count >= MaxStreams && runners.Where(r => r.Parked).MinBy(r => r.ActiveAt) is { } stalest)
                        {
                            runners.Remove(stalest);
                            stop = stalest;
                        }
                        if (runners.Count < MaxStreams)
                        {
                            runner = new Runner(missing, lastBlock, disposed.Token);
                            runners.Add(runner);
                            var started = runner;
                            _ = Task.Run(() => RunAsync(started));
                        }
                    }
                    else if (lastBlock > runner.Demand)
                    {
                        Follow(runner, lastBlock);
                        moreDemand = true;
                    }
                    // Bytes arriving on the stream this read waits for count as its progress too (a slow line takes long per chunk)
                    left = (runner is { Flowing: true } ? Math.Max(deadline, runner.ProgressAt + timeout) : deadline) - Now;
                    if (left <= 0 && runner is not null && Now - runner.ProgressAt >= timeout)
                    {
                        // Stalled: stopped here and counted as a failed attempt, so the next read starts a fresh stream
                        runners.Remove(runner);
                        stalled = runner;
                        failure = new TimeoutException($"No download progress for {noProgress.TotalSeconds:0.#} s");
                        gaveUp = ++failures >= MaxFailures;
                        if (gaveUp)
                        {
                            failedAt = Now;
                        }
                        moreDemand = true;
                    }
                    if (moreDemand)
                    {
                        Signal();
                    }
                    wait = changed.Task;
                }
                // Outside the gate: cancellation runs the stream's code inline
                if (stop is not null)
                {
                    Cancel(stop);
                }
                if (left <= 0)
                {
                    if (stalled is not null)
                    {
                        Cancel(stalled);
                        if (gaveUp)
                        {
                            failed?.Invoke(this);
                        }
                    }
                    throw new TimeoutException($"No download progress for {noProgress.TotalSeconds:0.#} s");
                }
                cancel ??= CancellationTokenSource.CreateLinkedTokenSource(ct, disposed.Token);
                try
                {
                    await wait.WaitAsync(TimeSpan.FromMilliseconds(left), cancel.Token);
                }
                catch (TimeoutException)
                {
                    // Checked again above: the stream may have made progress meanwhile
                }
            }
        }
        finally
        {
            cancel?.Dispose();
        }
    }

    /// <summary>A reader wants blocks up to <paramref name="block"/> from this stream: it follows it, so the stream may run further ahead. Called under the gate.</summary>
    static void Follow(Runner runner, int block)
    {
        runner.Demand = block;
        runner.Window = Math.Min(ReadAhead, runner.Window * 2);
        runner.ActiveAt = Now;
    }

    static void Cancel(Runner runner)
    {
        try
        {
            runner.Stop.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>Every chunk of the block is here. Called under the gate.</summary>
    bool BlockPresent(int block)
    {
        var first = block * ChunksPerBlock;
        return Array.IndexOf(present, false, first, Math.Min(ChunksPerBlock, present.Length - first)) < 0;
    }

    async Task RunAsync(Runner runner)
    {
        RequestPriority.Use(priority); // Task.Run copied the context of whoever started this stream
        var token = runner.Stop.Token;
        var stall = runner.Stall;
        var claim = -1;
        var buffer = ArrayPool<byte>.Shared.Rent((int)Math.Min(ChunkSize, Length));
        Task<int>? reading = null;
        try
        {
            int retry;
            lock (gate)
            {
                retry = failures;
            }
            if (retry > 0)
            {
                await Task.Delay(RetryDelay * retry, token);
            }
            lock (gate)
            {
                runner.ProgressAt = Now;
            }
            // WaitAsync as well as the token: a call that ignores its token can't hold the stream
            stall.CancelAfter(noProgress);
            await using var stream = await api.OpenReadAsync(driveId, itemId, contentTag, (long)runner.Next * BlockSize, Length, stall.Token).WaitAsync(stall.Token);
            while (true)
            {
                int block;
                lock (gate)
                {
                    block = runner.Next;
                    if (block >= blocks || BlockPresent(block))
                    {
                        return;
                    }
                }
                stall.CancelAfter(Timeout.InfiniteTimeSpan); // waiting for readers isn't stalling
                if (!await WaitForDemandAsync(runner, block))
                {
                    return;
                }

                var end = Math.Min((block + 1) * ChunksPerBlock, present.Length);
                for (var chunk = block * ChunksPerBlock; chunk < end; chunk++)
                {
                    var length = (int)Math.Min(ChunkSize, Length - (long)chunk * ChunkSize);
                    for (var got = 0; got < length;)
                    {
                        stall.CancelAfter(noProgress);
                        reading = stream.ReadAsync(buffer.AsMemory(got, length - got), stall.Token).AsTask();
                        var read = await reading.WaitAsync(stall.Token);
                        if (read == 0)
                        {
                            throw new EndOfStreamException($"The download ended at {(long)chunk * ChunkSize + got} of {Length} bytes");
                        }
                        got += read;
                        lock (gate)
                        {
                            (runner.ProgressAt, runner.Flowing) = (Now, true);
                        }
                    }
                    bool write;
                    lock (gate)
                    {
                        // Another stream may be writing this chunk: a chunk is written once
                        write = !present[chunk] && !claimed[chunk];
                        if (write)
                        {
                            (claimed[chunk], claim) = (true, chunk);
                        }
                    }
                    if (write)
                    {
                        if (sealedFile is not null)
                        {
                            sealedFile.Write((long)chunk * ChunkSize, buffer.AsSpan(0, length));
                        }
                        else
                        {
                            RandomAccess.Write(file, buffer.AsSpan(0, length), (long)chunk * ChunkSize);
                        }
                    }
                    bool complete;
                    lock (gate)
                    {
                        if (write)
                        {
                            // Readable only now: written, and sealed when encrypted
                            (claimed[chunk], claim) = (false, -1);
                            present[chunk] = true;
                            presentCount++;
                        }
                        if (chunk == end - 1)
                        {
                            runner.Next = block + 1;
                            runner.ActiveAt = Now;
                            failures = 0;
                        }
                        complete = presentCount == present.Length;
                        if (!complete && (write || chunk == end - 1))
                        {
                            Signal();
                        }
                    }
                    if (complete)
                    {
                        // Into the cache before readers wake, so a reader closing at once can't discard the finished download
                        Finish();
                        lock (gate)
                        {
                            Signal();
                        }
                        return;
                    }
                }
            }
        }
        catch (ContentChangedException e) when (!token.IsCancellationRequested)
        {
            lock (gate)
            {
                failure = e;
                failures = MaxFailures; // trying again serves the same
                failedAt = Now;
            }
            contentChanged?.Invoke(e.Current);
            failed?.Invoke(this);
        }
        catch (Exception e) when (!token.IsCancellationRequested)
        {
            bool gaveUp;
            lock (gate)
            {
                failure = e is OperationCanceledException && stall.IsCancellationRequested
                    ? new TimeoutException($"No download progress for {noProgress.TotalSeconds:0.#} s", e)
                    : e;
                gaveUp = ++failures >= MaxFailures;
                if (gaveUp)
                {
                    failedAt = Now;
                }
            }
            if (gaveUp)
            {
                failed?.Invoke(this);
            }
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            bool complete;
            lock (gate)
            {
                if (claim >= 0)
                {
                    claimed[claim] = false;
                }
                runners.Remove(runner);
                complete = presentCount == present.Length;
                Signal();
            }
            runner.Stall.Dispose();
            runner.Stop.Dispose();
            if (reading is not { IsCompleted: false })
            {
                ArrayPool<byte>.Shared.Return(buffer); // not while a read the stream never finished may still write into it
            }
            if (complete)
            {
                Finish();
            }
        }
    }

    /// <summary>
    /// Holds a stream back while it is more than its window ahead of the readers, or nobody holds the download. False when nobody came
    /// for IdleStop since the stream was last active, or when it was stopped to make room for another.
    /// </summary>
    async Task<bool> WaitForDemandAsync(Runner runner, int block)
    {
        while (true)
        {
            Task wait;
            long left;
            lock (gate)
            {
                if (!held && block <= runner.Demand + runner.Window)
                {
                    runner.Parked = false;
                    return true;
                }
                left = runner.ActiveAt + (long)IdleStop.TotalMilliseconds - Now;
                if (left <= 0)
                {
                    return false;
                }
                if (!runner.Parked)
                {
                    runner.Parked = true;
                    Signal(); // a reader waiting for a free stream may now stop this one
                }
                wait = changed.Task;
            }
            try
            {
                await wait.WaitAsync(TimeSpan.FromMilliseconds(left), runner.Stop.Token);
            }
            catch (TimeoutException)
            {
            }
        }
    }

    /// <summary>Moves the complete file to its final path (nothing to move in place). Readers waiting for it are released whatever happens.</summary>
    internal void Finish()
    {
        lock (gate)
        {
            if (finished)
            {
                return;
            }
            finished = true;
        }
        try
        {
            try
            {
                if (!inPlace)
                {
                    File.Move(partialPath, FinalPath, overwrite: false);
                }
                moved = true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The same version landed meanwhile, or the cache was cleared: readers keep reading through the open handle
                TryDelete(partialPath);
            }
            if (moved)
            {
                completed(this);
            }
        }
        finally
        {
            done.TrySetResult();
        }
    }

    void Signal()
    {
        var previous = changed;
        changed = NewSignal();
        previous.TrySetResult();
    }

    static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        disposed.Cancel();
        file.Dispose();
        if (!moved)
        {
            TryDelete(partialPath);
        }
    }
}
