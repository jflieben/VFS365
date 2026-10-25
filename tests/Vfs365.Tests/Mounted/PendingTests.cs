using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Xunit.Abstractions;

namespace Vfs365.Tests.Mounted;

/// <summary>
/// Reads, listings and writes that wait for Microsoft 365 answer later (STATUS_PENDING) instead of holding one of WinFsp's
/// dispatcher threads (review P1 2): more of them than there are threads leave cached files working. Answers after the unmount are
/// dropped safely.
/// </summary>
[Collection(MountedCollection.Name)]
public sealed class PendingTests(ITestOutputHelper output)
{
    const int MoreThanThreads = 100; // DriveHost runs 64 dispatcher threads

    static TimeSpan Time(Action action)
    {
        var watch = Stopwatch.StartNew();
        action();
        return watch.Elapsed;
    }

    /// <summary>Threads running the body; an exception ends the thread quietly (the test checks the results).</summary>
    static List<Thread> Start(int count, Action<int> body)
    {
        var threads = Enumerable.Range(0, count).Select(i => new Thread(() =>
        {
            try
            {
                body(i);
            }
            catch (Exception)
            {
            }
        }) { IsBackground = true }).ToList();
        threads.ForEach(t => t.Start());
        return threads;
    }

    [MountedFact]
    public void Reads_waiting_for_downloads_leave_cached_files_working()
    {
        using var drive = MountedDrive.Start(trace: true, seed: fake =>
        {
            fake.Add("videos", folder: true);
            for (var i = 0; i < MoreThanThreads; i++)
            {
                fake.Add($"videos/v{i:000}.mp4", content: $"video {i}");
            }
            fake.Add("cached.txt", content: "cached");
        });
        Directory.GetFiles(drive.PathOf("videos"));
        Assert.Equal("cached", File.ReadAllText(drive.PathOf("cached.txt")));
        drive.Api.HoldDownloads = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var results = new string?[MoreThanThreads];
        var readers = Start(MoreThanThreads, i => results[i] = File.ReadAllText(drive.PathOf($"videos/v{i:000}.mp4")));
        var deadline = Stopwatch.StartNew();
        while (drive.Lines().Count(l => l.EndsWith(": pending", StringComparison.Ordinal) && l.StartsWith(@"read \videos", StringComparison.Ordinal)) < MoreThanThreads
            && deadline.Elapsed < TimeSpan.FromSeconds(20))
        {
            Thread.Sleep(20);
        }
        Assert.Equal(MoreThanThreads, drive.Lines().Count(l => l.StartsWith(@"read \videos", StringComparison.Ordinal) && l.EndsWith(": pending", StringComparison.Ordinal)));

        var cached = Time(() => Assert.Equal("cached", File.ReadAllText(drive.PathOf("cached.txt"))));
        var listed = Time(() => Assert.Equal(MoreThanThreads, Directory.GetFiles(drive.PathOf("videos")).Length));
        output.WriteLine($"{MoreThanThreads} reads waiting: a cached file read in {cached.TotalMilliseconds:0} ms, a folder listed in {listed.TotalMilliseconds:0} ms");
        Assert.True(cached < TimeSpan.FromSeconds(2) && listed < TimeSpan.FromSeconds(2));

        drive.Api.HoldDownloads.SetResult();
        foreach (var reader in readers)
        {
            Assert.True(reader.Join(TimeSpan.FromSeconds(30)));
        }
        for (var i = 0; i < MoreThanThreads; i++)
        {
            Assert.Equal($"video {i}", results[i]);
        }
        Assert.Empty(drive.Errors());
    }

    [MountedFact]
    public void Listings_waiting_for_their_first_page_leave_cached_files_working()
    {
        using var drive = MountedDrive.Start(trace: true, seed: fake =>
        {
            for (var i = 0; i < MoreThanThreads; i++)
            {
                fake.Add($"slow{i:000}", folder: true);
                fake.Add($"slow{i:000}/inside.txt", content: "x");
            }
            fake.Add("cached.txt", content: "cached");
        });
        Assert.Equal(MoreThanThreads, Directory.GetDirectories(drive.Root).Length);
        Assert.Equal("cached", File.ReadAllText(drive.PathOf("cached.txt")));
        drive.Api.ListDelay = folder => folder.StartsWith("slow", StringComparison.Ordinal) ? TimeSpan.FromSeconds(4) : TimeSpan.Zero;

        var counts = new int[MoreThanThreads];
        var listers = Start(MoreThanThreads, i => counts[i] = Directory.GetFiles(drive.PathOf($"slow{i:000}")).Length);
        var deadline = Stopwatch.StartNew();
        while (drive.Lines().Count(l => l.StartsWith(@"readdir \slow", StringComparison.Ordinal) && l.EndsWith(": pending", StringComparison.Ordinal)) < MoreThanThreads
            && deadline.Elapsed < TimeSpan.FromSeconds(20))
        {
            Thread.Sleep(20);
        }
        var pending = drive.Lines().Count(l => l.StartsWith(@"readdir \slow", StringComparison.Ordinal) && l.EndsWith(": pending", StringComparison.Ordinal));

        var cached = Time(() => Assert.Equal("cached", File.ReadAllText(drive.PathOf("cached.txt"))));
        output.WriteLine($"{pending} listings waiting: a cached file read in {cached.TotalMilliseconds:0} ms");
        Assert.Equal(MoreThanThreads, pending);
        Assert.True(cached < TimeSpan.FromSeconds(2));

        // Unmounting cancels the waiting listings; their pages arrive after it, and their answers must touch nothing of WinFsp's
        // (its buffers are unmapped then: writing one crashed the process)
        drive.Unmount();
        foreach (var lister in listers)
        {
            Assert.True(lister.Join(TimeSpan.FromSeconds(30)));
        }
        Thread.Sleep(TimeSpan.FromSeconds(5));
        Assert.Empty(drive.Errors());
    }

    [MountedFact]
    public void A_read_still_waiting_at_the_unmount_is_cancelled_and_its_late_answer_dropped()
    {
        using var drive = MountedDrive.Start(trace: true, seed: fake => fake.Add("far.bin", content: "far away"));
        File.GetAttributes(drive.PathOf("far.bin"));
        drive.Api.HoldDownloads = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? failure = null;
        var reader = Start(1, _ =>
        {
            try
            {
                File.ReadAllText(drive.PathOf("far.bin"));
            }
            catch (Exception e)
            {
                failure = e;
            }
        })[0];
        var deadline = Stopwatch.StartNew();
        while (!drive.Lines().Contains(@"read \far.bin: pending") && deadline.Elapsed < TimeSpan.FromSeconds(10))
        {
            Thread.Sleep(10);
        }
        Assert.Contains(@"read \far.bin: pending", drive.Lines());

        drive.Unmount();
        Assert.True(reader.Join(TimeSpan.FromSeconds(30)));
        Assert.True(failure is IOException or OperationCanceledException, $"{failure}"); // ERROR_OPERATION_ABORTED
        drive.Api.HoldDownloads.SetResult(); // the download ends now; its answer must go nowhere
        Thread.Sleep(500);
        Assert.Empty(drive.Errors());
    }

    [MountedFact]
    public void An_empty_file_reads_without_a_download()
    {
        using var drive = MountedDrive.Start(seed: fake => fake.Add("empty.txt", []));
        Assert.Empty(File.ReadAllBytes(drive.PathOf("empty.txt")));
        using (var stream = File.OpenRead(drive.PathOf("empty.txt")))
        {
            Assert.Equal(0, stream.Read(new byte[16]));
        }
        Assert.Equal(0, drive.Api.Downloads);
    }

    [MountedFact]
    public async Task A_new_file_closed_without_writes_uploads_at_its_close()
    {
        // WinFsp sends no cleanup for a new file nobody wrote to unless asked to; without it the file waited for the unmount
        using var drive = MountedDrive.Start();
        File.WriteAllBytes(drive.PathOf("New Text Document.txt"), []);
        File.Create(drive.PathOf("placeholder.zip")).Dispose();
        await drive.Engine.ProcessDueAsync(all: false, default);
        await drive.Engine.WaitForUploadsAsync();
        Assert.Equal(["New Text Document.txt", "placeholder.zip"], drive.ServerTree());
    }

    [MountedFact]
    public async Task Writes_waiting_for_a_streaming_fragment_answer_later()
    {
        const int size = 12 << 20, chunk = 1 << 20;
        using var drive = MountedDrive.Start(trace: true);
        drive.Api.FragmentDelay = TimeSpan.FromMilliseconds(300);
        var content = new byte[size];
        new Random(7).NextBytes(content);
        // As Windows' copy engine writes big files: size first, then unbuffered writes front to back
        using (var file = File.OpenHandle(drive.PathOf("big.bin"), FileMode.CreateNew, FileAccess.Write, FileShare.None, (FileOptions)0x20000000))
        {
            RandomAccess.SetLength(file, size);
            // Unbuffered I/O needs sector-aligned buffers: a pinned array, used from its first 4 KiB boundary
            var pinned = GC.AllocateArray<byte>(chunk + 4096, pinned: true);
            var skip = (int)((4096 - Marshal.UnsafeAddrOfPinnedArrayElement(pinned, 0) % 4096) % 4096);
            var buffer = pinned.AsMemory(skip, chunk);
            for (var at = 0; at < size; at += chunk)
            {
                content.AsSpan(at, chunk).CopyTo(buffer.Span);
                RandomAccess.Write(file, buffer.Span, at);
            }
        }
        await drive.SettleAsync();

        Assert.Contains(drive.Lines(), l => l == @"write \big.bin: pending");
        Assert.True(drive.Fake.Sessions >= 1);
        Assert.Equal(content, drive.Fake.BytesOf("big.bin"));
        Assert.Equal(content, File.ReadAllBytes(drive.PathOf("big.bin")));
        Assert.Empty(drive.Errors());
    }

    [MountedFact]
    public void Text_written_through_a_pending_write_reads_back()
    {
        using var drive = MountedDrive.Start();
        var text = string.Concat(Enumerable.Range(0, 20_000).Select(i => $"line {i}\n"));
        File.WriteAllText(drive.PathOf("log.txt"), text, Encoding.UTF8);
        File.AppendAllText(drive.PathOf("log.txt"), "appended\n");
        Assert.Equal(text + "appended\n", File.ReadAllText(drive.PathOf("log.txt")));
    }
}
