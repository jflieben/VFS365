using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Vfs365.Core.Drive;
using Vfs365.Drive.WinFsp;
using Xunit.Abstractions;

namespace Vfs365.Tests.Mounted;

/// <summary>
/// Scans of one folder from several handles at once list every name once (review 2026-10-30, front end 1). WinFsp's directory cache
/// handed a scan another handle's partial first answer and let it continue on its own handle, in that handle's order: names were
/// skipped or repeated.
/// </summary>
[Collection(MountedCollection.Name)]
public sealed class DirectoryScanTests(ITestOutputHelper output)
{
    const int Files = 450; // three listing pages of 200

    [MountedFact]
    public void Two_scans_of_a_folder_that_loads_and_changes_meanwhile_list_every_name_once()
    {
        using var drive = MountedDrive.Start(trace: true, seed: fake =>
        {
            fake.PageSize = 200;
            fake.Add("big", folder: true);
            for (var i = 0; i < Files; i++)
            {
                fake.Add($"big/f{i:000}.txt", content: "x");
            }
        });
        // A new file still uploading: a listing loading page by page shows it with its first page, a loaded one in its sorted place
        drive.Api.UploadDelay = name => name == "zzz.txt" ? TimeSpan.FromSeconds(10) : TimeSpan.Zero;
        File.WriteAllText(drive.PathOf(@"big\zzz.txt"), "new");
        var rest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        drive.Fake.BeforeNextPage = () => rest.Task;
        var expected = Enumerable.Range(0, Files).Select(i => $"f{i:000}.txt").Append("zzz.txt").ToList();

        // X starts while only the first page is in; the rest arrives, Y lists the folder whole, then X goes on
        using var x = Directory.EnumerateFiles(drive.PathOf("big")).GetEnumerator();
        Assert.True(x.MoveNext());
        var seenX = new List<string> { Path.GetFileName(x.Current) };
        var pages = drive.Fake.ListPages;
        rest.SetResult();
        var deadline = Stopwatch.StartNew();
        while (drive.Fake.ListPages < pages + 2 && deadline.Elapsed < TimeSpan.FromSeconds(10))
        {
            Thread.Sleep(10);
        }
        var seenY = Directory.GetFiles(drive.PathOf("big")).Select(path => Path.GetFileName(path)).ToList();
        while (x.MoveNext())
        {
            seenX.Add(Path.GetFileName(x.Current));
        }

        foreach (var line in drive.Lines().Where(line => line.StartsWith(@"readdir \big", StringComparison.Ordinal)))
        {
            output.WriteLine(line);
        }
        Assert.Equal("", Mismatch("X", seenX, expected) + Mismatch("Y", seenY, expected));
        Assert.Empty(drive.Errors());
    }

    /// <summary>Needs WinFsp's DLL (the reader writes with its exports), not a mount.</summary>
    [MountedFact]
    public async Task A_marker_the_reader_never_listed_continues_after_its_sorted_place()
    {
        Fsp.FileSystemHost.Version(); // loads WinFsp's DLL
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reader = new Vfs365FileSystem.DirectoryReader(Batches(second.Task).GetAsyncEnumerator(), Entry("dir"), true);

        Assert.Equal(". .. b d", Answer(reader, null));
        Assert.Null(Answer(reader, "d")); // the next batch isn't here yet
        Assert.Null(Answer(reader, "bb")); // nor the whole listing, which a marker this reader never listed waits for
        second.SetResult();
        await reader.MoreAsync();

        // A marker it listed continues in its own order (batches as they came); one it never listed after its sorted place, which
        // puts the entries in one sorted order from then on
        Assert.Equal("a c (end)", Answer(reader, "d"));
        Assert.Equal("c d (end)", Answer(reader, "bb"));
        Assert.Equal("c d (end)", Answer(reader, "b"));
        Assert.Equal("(end)", Answer(reader, "zz"));

        static async IAsyncEnumerable<IReadOnlyList<FsEntry>> Batches(Task second)
        {
            yield return [Entry("b"), Entry("d")];
            await second;
            yield return [Entry("a"), Entry("c")];
        }
    }

    static FsEntry Entry(string name)
    {
        var time = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
        return new() { Path = $@"\{name}", Name = name, Created = time, Modified = time, ItemId = $"id-{name}" };
    }

    /// <summary>The names one answer after <paramref name="marker"/> holds, "(end)" when it ends the scan; null when it has to wait.</summary>
    static string? Answer(Vfs365FileSystem.DirectoryReader reader, string? marker)
    {
        var buffer = GC.AllocateArray<byte>(4096, pinned: true);
        if (!reader.TryAnswer(marker, Marshal.UnsafeAddrOfPinnedArrayElement(buffer, 0), (uint)buffer.Length, out var transferred))
        {
            return null;
        }
        // FSP_FSCTL_DIR_INFO entries, 8-byte aligned: Size (UINT16), file info, the name from byte 104; a Size of 0 ends the directory
        var names = new List<string>();
        for (var at = 0; at + 2 <= transferred; at += (BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(at)) + 7) & ~7)
        {
            var size = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(at));
            if (size == 0)
            {
                names.Add("(end)");
                break;
            }
            names.Add(Encoding.Unicode.GetString(buffer, at + 104, size - 104));
        }
        return string.Join(" ", names);
    }

    /// <summary>What a scan missed or listed more than once; empty when it saw every name once.</summary>
    static string Mismatch(string scan, List<string> seen, List<string> expected)
    {
        var missing = expected.Except(seen, StringComparer.OrdinalIgnoreCase).ToList();
        var repeated = seen.GroupBy(name => name, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1).Select(group => group.Key).ToList();
        var extra = seen.Except(expected, StringComparer.OrdinalIgnoreCase).ToList();
        return missing.Count + repeated.Count + extra.Count == 0 ? ""
            : $"{scan} saw {seen.Count} names of {expected.Count}: missing {missing.Count} ({string.Join(", ", missing.Take(3))}), " +
              $"repeated {repeated.Count} ({string.Join(", ", repeated.Take(3))}), unknown {extra.Count} ({string.Join(", ", extra.Take(3))}). ";
    }
}
