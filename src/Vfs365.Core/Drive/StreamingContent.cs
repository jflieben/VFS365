using Microsoft.Win32.SafeHandles;

namespace Vfs365.Core.Drive;

/// <summary>
/// One item version downloaded on demand into a sparse local file in 1 MiB blocks. A read waits only for its own blocks.
/// A sequential reader is fed by one streaming request that stays a few blocks ahead; a read elsewhere opens its own ranged request.
/// Streams pause when nobody reads near them and stop after a while, so peeking at a big file doesn't download all of it.
/// A failed stream is tried again after 1 and 2 s; reads fail only when all attempts did, or at once when the content served isn't
/// Length bytes (then <c>contentChanged</c> gets the item as served).
/// </summary>
internal sealed class StreamingContent : IContentSource
{
    public const int BlockSize = 1 << 20;
    const int ReadAhead = 8;
    const int MaxStreams = 3;
    const int MaxFailures = 3;
    static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);
    static readonly TimeSpan IdleStop = TimeSpan.FromSeconds(30);
    static readonly TimeSpan NoProgressTimeout = TimeSpan.FromSeconds(90);

    /// <summary>One streaming request. Demand is the furthest block its readers asked for; it runs at most ReadAhead blocks past that.</summary>
    sealed class Runner(int next, int demand)
    {
        public int Next { get; set; } = next;
        public int Demand { get; set; } = demand;
    }

    readonly IDriveApi api;
    readonly string driveId, itemId, partialPath, finalPath;
    readonly Action<StreamingContent> completed;
    readonly Action<DriveItemInfo>? contentChanged;
    readonly CacheCipher? cipher;
    readonly byte[] nonce;
    readonly SafeFileHandle file;
    readonly bool[] present;
    readonly Lock gate = new();
    readonly List<Runner> runners = [];
    readonly CancellationTokenSource disposed = new();
    readonly TaskCompletionSource done = NewSignal();
    TaskCompletionSource changed = NewSignal();
    int presentCount, failures;
    Exception? failure;
    bool finished;

    /// <param name="cipher">Encrypts blocks as they are written and decrypts reads, with the keystream of <paramref name="finalPath"/>'s name.</param>
    public StreamingContent(IDriveApi api, string driveId, string itemId, long length, string finalPath, CacheCipher? cipher, Action<StreamingContent> completed,
        Action<DriveItemInfo>? contentChanged = null)
    {
        (this.api, this.driveId, this.itemId, this.finalPath, this.cipher, this.completed, this.contentChanged) = (api, driveId, itemId, finalPath, cipher, completed, contentChanged);
        nonce = CacheCipher.NonceFor(Path.GetFileName(finalPath));
        Length = length;
        partialPath = $"{finalPath}.{Guid.NewGuid():N}.part";
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        file = File.OpenHandle(partialPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        SparseFile.Mark(file);
        RandomAccess.SetLength(file, length);
        present = new bool[(int)((length + BlockSize - 1) / BlockSize)];
        if (present.Length == 0)
        {
            Finish();
        }
    }

    public long Length { get; }

    /// <summary>Opened by the content cache; it disposes this when the last reader is done.</summary>
    internal int References { get; set; }

    public async ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct)
    {
        if (offset >= Length || buffer.Length == 0)
        {
            return 0;
        }
        var length = (int)Math.Min(buffer.Length, Length - offset);
        await EnsureAsync((int)(offset / BlockSize), (int)((offset + length - 1) / BlockSize), ct);
        var read = RandomAccess.Read(file, buffer.Span[..length], offset);
        cipher?.Apply(buffer.Span[..read], offset, nonce);
        return read;
    }

    /// <summary>Waits until every block is here and the file has moved to its final cache path.</summary>
    public async Task CompleteAsync(CancellationToken ct)
    {
        if (present.Length > 0)
        {
            await EnsureAsync(0, present.Length - 1, ct);
        }
        await done.Task.WaitAsync(ct);
    }

    async Task EnsureAsync(int first, int last, CancellationToken ct)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct, disposed.Token);
        while (true)
        {
            Task wait;
            lock (gate)
            {
                var moreDemand = false;
                foreach (var ahead in runners.Where(r => r.Next > last && r.Next <= last + ReadAhead + 1 && last > r.Demand))
                {
                    ahead.Demand = last; // keeps the stream just ahead of this reader going
                    moreDemand = true;
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
                var runner = runners.FirstOrDefault(r => r.Next <= missing && missing <= r.Next + ReadAhead);
                if (runner is null)
                {
                    if (failures >= MaxFailures)
                    {
                        throw failure is ContentChangedException mismatch
                            ? new ContentChangedException(mismatch.Current, mismatch.Listed)
                            : new IOException($"Download failed after {MaxFailures} attempts: {failure?.Message}", failure);
                    }
                    if (runners.Count < MaxStreams)
                    {
                        runner = new Runner(missing, last);
                        runners.Add(runner);
                        var started = runner;
                        _ = Task.Run(() => RunAsync(started));
                    }
                }
                else if (last > runner.Demand)
                {
                    runner.Demand = last;
                    moreDemand = true;
                }
                if (moreDemand)
                {
                    Signal();
                }
                wait = changed.Task;
            }
            try
            {
                await wait.WaitAsync(NoProgressTimeout, cancel.Token);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException($"No download progress for {NoProgressTimeout.TotalSeconds} s");
            }
        }
    }

    async Task RunAsync(Runner runner)
    {
        try
        {
            int retry;
            lock (gate)
            {
                retry = failures;
            }
            if (retry > 0)
            {
                await Task.Delay(RetryDelay * retry, disposed.Token);
            }
            await using var stream = await api.OpenReadAsync(driveId, itemId, (long)runner.Next * BlockSize, Length, disposed.Token);
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
                if (!await WaitForDemandAsync(runner, block))
                {
                    return;
                }

                var length = (int)Math.Min(BlockSize, Length - (long)block * BlockSize);
                await stream.ReadExactlyAsync(buffer.AsMemory(0, length), disposed.Token);
                cipher?.Apply(buffer.AsSpan(0, length), (long)block * BlockSize, nonce);
                RandomAccess.Write(file, buffer.AsSpan(0, length), (long)block * BlockSize);
                bool complete;
                lock (gate)
                {
                    if (!present[block])
                    {
                        present[block] = true;
                        presentCount++;
                    }
                    runner.Next = block + 1;
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
        catch (ContentChangedException e) when (!disposed.IsCancellationRequested)
        {
            lock (gate)
            {
                failure = e;
                failures = MaxFailures; // trying again serves the same
            }
            contentChanged?.Invoke(e.Current);
        }
        catch (Exception e) when (!disposed.IsCancellationRequested)
        {
            lock (gate)
            {
                failure = e;
                failures++;
            }
        }
        catch (Exception) when (disposed.IsCancellationRequested)
        {
        }
        finally
        {
            bool complete;
            lock (gate)
            {
                runners.Remove(runner);
                complete = presentCount == present.Length;
                Signal();
            }
            if (complete)
            {
                Finish();
            }
        }
    }

    /// <summary>Holds a stream back while it is more than ReadAhead blocks ahead of the readers. False when nobody came for IdleStop.</summary>
    async Task<bool> WaitForDemandAsync(Runner runner, int block)
    {
        while (true)
        {
            Task wait;
            lock (gate)
            {
                if (block <= runner.Demand + ReadAhead)
                {
                    return true;
                }
                wait = changed.Task;
            }
            try
            {
                await wait.WaitAsync(IdleStop, disposed.Token);
            }
            catch (TimeoutException)
            {
                return false;
            }
        }
    }

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
            File.Move(partialPath, finalPath, overwrite: true);
        }
        catch (IOException)
        {
        }
        completed(this);
        done.TrySetResult();
    }

    void Signal()
    {
        var previous = changed;
        changed = NewSignal();
        previous.TrySetResult();
    }

    static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Dispose()
    {
        disposed.Cancel();
        file.Dispose();
        if (!finished)
        {
            try
            {
                File.Delete(partialPath);
            }
            catch (IOException)
            {
            }
        }
    }
}
