using Microsoft.Win32.SafeHandles;

namespace Vfs365.Core.Drive;

/// <summary>
/// One item version downloaded on demand into a sparse local file in 1 MiB blocks. A read waits only for its own blocks.
/// A sequential reader is fed by one streaming request that stays a few blocks ahead; a read elsewhere opens its own ranged request,
/// and when MaxStreams are open already, the one that waited longest for a reader makes room.
/// Streams pause when nobody reads near them and stop after a while, so peeking at a big file doesn't download all of it.
/// Every stream asks for the version the listing named, so one download never mixes two versions.
/// A failed stream is tried again after 1 and 2 s; reads fail only when all attempts did (and again after a cool-down), or for good
/// when the content served isn't that version or isn't Length bytes (then <c>contentChanged</c> gets the item as served). A stream that
/// brings no bytes for the no-progress timeout counts as a failed attempt, whether or not a reader is waiting for it.
/// </summary>
internal sealed class StreamingContent : IContentSource
{
    public const int BlockSize = 1 << 20;
    const int ReadAhead = 8;
    const int MaxStreams = 3;
    const int MaxFailures = 3;

    /// <summary>Bytes read from a stream at a time, so progress shows within a block on a slow line.</summary>
    const int Piece = 64 * 1024;
    static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);
    static readonly TimeSpan IdleStop = TimeSpan.FromSeconds(30);
    static readonly TimeSpan FailureCooldown = TimeSpan.FromSeconds(10);

    /// <summary>
    /// One streaming request. Demand is the furthest block its readers asked for; it runs at most ReadAhead blocks past that.
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
    readonly string driveId, itemId, partialPath, finalPath;
    readonly string? contentTag;
    readonly Action<StreamingContent> completed;
    readonly Action<StreamingContent>? failed;
    readonly Action<DriveItemInfo>? contentChanged;
    readonly TimeSpan noProgress;
    readonly CacheCipher.SealedFile? sealedFile;
    readonly SafeFileHandle file;
    readonly bool[] present, claimed;
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
    bool finished;
    volatile bool moved;

    /// <param name="contentTag">The version to download (the listing's cTag, else eTag); every stream is checked against it.</param>
    /// <param name="name">The cache file's name, which its encrypted chunks are bound to.</param>
    /// <param name="failed">Called when the download gave up, so new readers can start a fresh one.</param>
    /// <param name="noProgress">A stream without bytes for this long is a failed attempt; a read waiting this long without progress fails.</param>
    public StreamingContent(IDriveApi api, string driveId, string itemId, string? contentTag, long length, string finalPath, string name, CacheCipher? cipher,
        Action<StreamingContent> completed, Action<StreamingContent>? failed = null, Action<DriveItemInfo>? contentChanged = null, TimeSpan? noProgress = null)
    {
        (this.api, this.driveId, this.itemId, this.contentTag, this.finalPath) = (api, driveId, itemId, contentTag, finalPath);
        (this.completed, this.failed, this.contentChanged) = (completed, failed, contentChanged);
        this.noProgress = noProgress ?? TimeSpan.FromSeconds(90);
        Length = length;
        partialPath = $"{finalPath}.{Guid.NewGuid():N}.part";
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        file = File.OpenHandle(partialPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        try
        {
            SparseFile.Mark(file);
            if (cipher is null)
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
        present = new bool[(int)((length + BlockSize - 1) / BlockSize)];
        claimed = new bool[present.Length];
    }

    public long Length { get; }

    /// <summary>When the download was asked for: cached versions that completed before then are older.</summary>
    public DateTime StartedAt { get; } = DateTime.UtcNow;

    /// <summary>Opened by the content cache; it disposes this when the last reader is done.</summary>
    internal int References { get; set; }

    /// <summary>Set by the content cache when the item was deleted meanwhile: the finished file isn't kept.</summary>
    internal volatile bool Forgotten;

    static long Now => Environment.TickCount64;

    public async ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct)
    {
        if (offset >= Length || buffer.Length == 0)
        {
            return 0;
        }
        var length = (int)Math.Min(buffer.Length, Length - offset);
        await EnsureAsync((int)(offset / BlockSize), (int)((offset + length - 1) / BlockSize), ct);
        return sealedFile?.Read(offset, buffer.Span[..length]) ?? RandomAccess.Read(file, buffer.Span[..length], offset);
    }

    /// <summary>Waits until every block is here and the file has moved to its final cache path.</summary>
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

    async Task EnsureAsync(int first, int last, CancellationToken ct)
    {
        if (!RequestPriority.IsBackground)
        {
            priority.Promote();
        }
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
                        if (ahead.Next > last && ahead.Next <= last + ReadAhead + 1 && last > ahead.Demand)
                        {
                            ahead.Demand = last; // keeps the stream just ahead of this reader going
                            ahead.ActiveAt = Now;
                            moreDemand = true;
                        }
                    }
                    var missing = Array.IndexOf(present, false, first, last - first + 1);
                    if (missing < 0)
                    {
                        if (moreDemand)
                        {
                            Signal();
                        }
                        return;
                    }
                    if (missing != waitingFor)
                    {
                        // Progress on this read's own blocks; other streams' progress doesn't count
                        (waitingFor, deadline) = (missing, Now + timeout);
                    }
                    var runner = runners.FirstOrDefault(r => r.Next <= missing && missing <= r.Next + ReadAhead);
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
                            runner = new Runner(missing, last, disposed.Token);
                            runners.Add(runner);
                            var started = runner;
                            _ = Task.Run(() => RunAsync(started));
                        }
                    }
                    else if (last > runner.Demand)
                    {
                        runner.Demand = last;
                        runner.ActiveAt = Now;
                        moreDemand = true;
                    }
                    // Bytes arriving on the stream this read waits for count as its progress too (a slow line takes long per block)
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

    async Task RunAsync(Runner runner)
    {
        RequestPriority.Use(priority); // Task.Run copied the context of whoever started this stream
        var token = runner.Stop.Token;
        var stall = runner.Stall;
        var claim = -1;
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
            var buffer = new byte[BlockSize];
            while (true)
            {
                int block;
                lock (gate)
                {
                    block = runner.Next;
                    if (block >= present.Length || present[block])
                    {
                        return;
                    }
                }
                stall.CancelAfter(Timeout.InfiniteTimeSpan); // waiting for readers isn't stalling
                if (!await WaitForDemandAsync(runner, block))
                {
                    return;
                }

                var length = (int)Math.Min(BlockSize, Length - (long)block * BlockSize);
                for (var got = 0; got < length;)
                {
                    stall.CancelAfter(noProgress);
                    var read = await stream.ReadAsync(buffer.AsMemory(got, Math.Min(Piece, length - got)), stall.Token).AsTask().WaitAsync(stall.Token);
                    if (read == 0)
                    {
                        throw new EndOfStreamException($"The download ended at {(long)block * BlockSize + got} of {Length} bytes");
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
                    // Another stream may be writing this block: a block is written once
                    write = !present[block] && !claimed[block];
                    if (write)
                    {
                        (claimed[block], claim) = (true, block);
                    }
                }
                if (write)
                {
                    if (sealedFile is not null)
                    {
                        sealedFile.Write((long)block * BlockSize, buffer.AsSpan(0, length));
                    }
                    else
                    {
                        RandomAccess.Write(file, buffer.AsSpan(0, length), (long)block * BlockSize);
                    }
                }
                bool complete;
                lock (gate)
                {
                    if (write)
                    {
                        (claimed[block], claim) = (false, -1);
                        present[block] = true;
                        presentCount++;
                    }
                    runner.Next = block + 1;
                    runner.ActiveAt = Now;
                    failures = 0;
                    complete = presentCount == present.Length;
                    if (!complete)
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
            if (complete)
            {
                Finish();
            }
        }
    }

    /// <summary>
    /// Holds a stream back while it is more than ReadAhead blocks ahead of the readers. False when nobody came for IdleStop since the
    /// stream was last active, or when it was stopped to make room for another.
    /// </summary>
    async Task<bool> WaitForDemandAsync(Runner runner, int block)
    {
        while (true)
        {
            Task wait;
            long left;
            lock (gate)
            {
                if (block <= runner.Demand + ReadAhead)
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

    /// <summary>Moves the complete file into the cache. Readers waiting for it are released whatever happens.</summary>
    void Finish()
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
                File.Move(partialPath, finalPath, overwrite: false);
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
