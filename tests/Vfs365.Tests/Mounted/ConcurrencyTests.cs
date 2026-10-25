using System.Collections.Concurrent;
using System.Text;
using Xunit.Abstractions;

namespace Vfs365.Tests.Mounted;

/// <summary>
/// Many threads creating, writing, renaming and deleting files on a mounted volume, with WinFsp's namespace lock and without it
/// (only the engine's per-key locking): afterwards the volume shows what the server holds, every file is whole, and nothing a
/// thread saved is missing. Threads working on names of their own expect no errors at all.
/// </summary>
[Collection(MountedCollection.Name)]
public sealed class ConcurrencyTests(ITestOutputHelper output)
{
    /// <summary>Text content that shows when it is torn: a tag, filler, and a hash of both.</summary>
    static byte[] Payload(string tag, Random random)
    {
        var filler = new string((char)('a' + random.Next(26)), random.Next(1, 40_000));
        var body = $"{tag}|{filler}";
        return Encoding.UTF8.GetBytes($"{body}|{Hash(body):X16}");
    }

    static bool Whole(byte[] content)
    {
        var text = Encoding.UTF8.GetString(content);
        var split = text.LastIndexOf('|');
        return split > 0 && text[(split + 1)..] == $"{Hash(text[..split]):X16}";
    }

    static string TagOf(byte[] content) => Encoding.UTF8.GetString(content).Split('|')[0];

    static ulong Hash(string text)
    {
        var hash = 14695981039346656037UL;
        foreach (var c in text)
        {
            hash = (hash ^ c) * 1099511628211UL;
        }
        return hash;
    }

    /// <summary>The expected outcomes of racing on shared names; anything else is a finding.</summary>
    static string Classify(Exception e) => e switch
    {
        FileNotFoundException or DirectoryNotFoundException => "not found",
        UnauthorizedAccessException => "access denied",
        IOException io => (io.HResult & 0xFFFF) switch
        {
            32 => "sharing violation",
            80 or 183 => "exists",
            303 => "delete pending",
            1006 => "file invalid",
            _ => $"unexpected {io.GetType().Name} 0x{io.HResult:X8}: {io.Message}",
        },
        _ => $"unexpected {e.GetType().Name}: {e.Message}",
    };

    /// <summary>A Word save that, like Word, cleans up when another thread got in the way (its temp file would stay local for good).</summary>
    static void WordOrBackOut(string path, byte[] content, int n)
    {
        var folder = Path.GetDirectoryName(path)!;
        var temp = Path.Combine(folder, $"~WRD{n:0000}.tmp");
        var backup = Path.Combine(folder, $"~WRL{n:0000}.tmp");
        File.WriteAllBytes(temp, content);
        try
        {
            File.Move(path, backup);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
        try
        {
            File.Move(temp, path);
        }
        catch
        {
            File.Delete(temp);
            try
            {
                File.Move(backup, path);
            }
            catch (IOException)
            {
                // Left as a renamed-away original: undone when nothing claims it
            }
            throw;
        }
        File.Delete(backup);
    }

    static void RunThreads(int count, Action<int> body)
    {
        var start = new Barrier(count);
        var failures = new ConcurrentQueue<Exception>();
        var threads = Enumerable.Range(0, count).Select(index => new Thread(() =>
        {
            start.SignalAndWait();
            try
            {
                body(index);
            }
            catch (Exception e)
            {
                failures.Enqueue(e);
            }
        }) { IsBackground = true }).ToList();
        threads.ForEach(t => t.Start());
        foreach (var thread in threads)
        {
            Assert.True(thread.Join(TimeSpan.FromMinutes(3)), "a thread hung");
        }
        if (!failures.IsEmpty)
        {
            throw new AggregateException(failures);
        }
    }

    /// <summary>Equal trees, or a failure naming what is missing and what is extra.</summary>
    internal static void AssertSameTree(IEnumerable<string> expected, IEnumerable<string> actual, string what)
    {
        var missing = expected.Except(actual, StringComparer.OrdinalIgnoreCase).ToList();
        var extra = actual.Except(expected, StringComparer.OrdinalIgnoreCase).ToList();
        Assert.True(missing.Count == 0 && extra.Count == 0, $"{what}: missing [{string.Join(", ", missing)}], extra [{string.Join(", ", extra)}]");
    }

    void AssertVolumeMatchesServer(MountedDrive drive, string folder = "")
    {
        var server = drive.ServerTree(folder);
        var volume = drive.VolumeTree(folder);
        AssertSameTree(server, volume, "the volume against the server");
        foreach (var path in server.Where(p => drive.Fake.ItemAt(p) is { IsFolder: false }))
        {
            Assert.Equal(drive.Fake.BytesOf(path), File.ReadAllBytes(drive.PathOf(path)));
        }
        output.WriteLine($"{server.Count} entries on the server and the volume");
    }

    [MountedTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Threads_on_names_of_their_own_end_as_they_left_them(bool winFspGuard)
    {
        const int threads = 8, rounds = 30;
        using var drive = MountedDrive.Start(winFspGuard, fake =>
        {
            for (var t = 0; t < threads; t++)
            {
                fake.Add($"t{t}", folder: true);
                fake.Add($"t{t}/doc.docx", Encoding.UTF8.GetBytes("seed"));
                fake.Add($"t{t}/book.xlsx", Encoding.UTF8.GetBytes("seed"));
            }
        });
        var models = new Dictionary<string, byte[]>[threads];
        var listers = new CancellationTokenSource();
        var listing = Task.Run(() =>
        {
            // Explorer refreshing meanwhile: listings may miss a folder being renamed, nothing else
            while (!listers.IsCancellationRequested)
            {
                try
                {
                    drive.VolumeTree();
                }
                catch (DirectoryNotFoundException)
                {
                }
            }
        });

        RunThreads(threads, t =>
        {
            var random = new Random(t * 7919);
            var folder = $"t{t}";
            var model = models[t] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["doc.docx"] = Encoding.UTF8.GetBytes("seed"),
                ["book.xlsx"] = Encoding.UTF8.GetBytes("seed"),
            };
            var next = 0;
            var history = new List<string>();
            string Any() => model.Keys.ElementAt(random.Next(model.Count));
            for (var round = 0; round < rounds; round++)
            {
                var tag = $"t{t}r{round}";
                try
                {
                    switch (random.Next(9))
                    {
                        case 0:
                            var created = $"n{next++}.txt";
                            history.Add($"create {created}");
                            File.WriteAllBytes(drive.PathOf($"{folder}/{created}"), model[created] = Payload(tag, random));
                            break;
                        case 1:
                            var overwritten = Any();
                            history.Add($"overwrite {overwritten}");
                            File.WriteAllBytes(drive.PathOf($"{folder}/{overwritten}"), model[overwritten] = Payload(tag, random));
                            break;
                        case 2:
                            var from = Any();
                            var to = $"m{next++}{Path.GetExtension(from)}";
                            history.Add($"rename {from} to {to}");
                            File.Move(drive.PathOf($"{folder}/{from}"), drive.PathOf($"{folder}/{to}"));
                            model[to] = model[from];
                            model.Remove(from);
                            break;
                        case 3 when model.Count > 2:
                            var source = Any();
                            var target = model.Keys.First(k => k != source);
                            history.Add($"replace {target} with {source}");
                            File.Move(drive.PathOf($"{folder}/{source}"), drive.PathOf($"{folder}/{target}"), overwrite: true);
                            model[target] = model[source];
                            model.Remove(source);
                            break;
                        case 4 when model.Count > 2:
                            var deleted = Any();
                            history.Add($"delete {deleted}");
                            File.Delete(drive.PathOf($"{folder}/{deleted}"));
                            model.Remove(deleted);
                            break;
                        case 5:
                            var word = model.Keys.FirstOrDefault(k => k.EndsWith(".docx", StringComparison.Ordinal)) ?? "doc.docx";
                            history.Add($"word {word}");
                            if (!File.Exists(drive.PathOf($"{folder}/{word}")))
                            {
                                File.WriteAllBytes(drive.PathOf($"{folder}/{word}"), []);
                            }
                            SavePatterns.Word(drive.PathOf($"{folder}/{word}"), model[word] = Payload(tag, random), t * 1000 + round);
                            break;
                        case 6:
                            var book = model.Keys.FirstOrDefault(k => k.EndsWith(".xlsx", StringComparison.Ordinal)) ?? "book.xlsx";
                            history.Add($"excel {book}");
                            if (!File.Exists(drive.PathOf($"{folder}/{book}")))
                            {
                                File.WriteAllBytes(drive.PathOf($"{folder}/{book}"), []);
                            }
                            SavePatterns.Excel(drive.PathOf($"{folder}/{book}"), model[book] = Payload(tag, random), random);
                            break;
                        case 7:
                            var read = Any();
                            history.Add($"read {read}");
                            Assert.Equal(model[read], File.ReadAllBytes(drive.PathOf($"{folder}/{read}")));
                            break;
                        default:
                            history.Add("list");
                            var names = Directory.GetFiles(drive.PathOf(folder)).Select(Path.GetFileName).Order(StringComparer.OrdinalIgnoreCase);
                            Assert.Equal(model.Keys.Order(StringComparer.OrdinalIgnoreCase), names);
                            break;
                    }
                }
                catch (Exception e)
                {
                    throw new InvalidOperationException($"{folder} after {string.Join("; ", history)}: {e.Message}", e);
                }
            }
            // Refused while the lister has the folder open, as for Explorer: tried again
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    Directory.Move(drive.PathOf(folder), drive.PathOf($"{folder}-done"));
                    break;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < 100)
                {
                    Thread.Sleep(20);
                }
            }
        });
        listers.Cancel();
        await listing;
        await drive.SettleAsync();

        var expected = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var t = 0; t < threads; t++)
        {
            expected.Add($"t{t}-done");
            foreach (var (name, content) in models[t])
            {
                expected.Add($"t{t}-done/{name}");
                Assert.Equal(content, drive.Fake.BytesOf($"t{t}-done/{name}"));
            }
        }
        AssertSameTree(expected, drive.ServerTree(), "the server");
        AssertVolumeMatchesServer(drive);
        Assert.Empty(drive.Errors());
    }

    [MountedTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Threads_racing_on_shared_names_never_tear_or_invent_a_file(bool winFspGuard)
    {
        const int threads = 8, rounds = 40, names = 5;
        var written = new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);
        using var drive = MountedDrive.Start(winFspGuard, trace: Environment.GetEnvironmentVariable("VFS365_TEST_TRACE") is not null, seed: fake =>
        {
            fake.Add("shared", folder: true);
            for (var n = 0; n < names; n++)
            {
                var seed = Payload($"seed{n}", new Random(n));
                written[TagOf(seed)] = true;
                fake.Add($"shared/s{n}.txt", seed);
            }
        });
        var outcomes = new ConcurrentDictionary<string, int>();
        var torn = new ConcurrentQueue<string>();
        var emptyReads = 0;
        RunThreads(threads, t =>
        {
            var random = new Random(t * 104729);
            string Name() => drive.PathOf($"shared/s{random.Next(names)}.txt");
            for (var round = 0; round < rounds; round++)
            {
                var tag = $"t{t}r{round}";
                try
                {
                    switch (random.Next(7))
                    {
                        case 0 or 1:
                            var content = Payload(tag, random);
                            written[tag] = true; // may land even when the call reports an error
                            File.WriteAllBytes(Name(), content);
                            break;
                        case 2:
                            var read = Name();
                            var bytes = File.ReadAllBytes(read);
                            if (bytes.Length == 0)
                            {
                                Interlocked.Increment(ref emptyReads);
                            }
                            else if (!Whole(bytes) || !written.ContainsKey(TagOf(bytes)))
                            {
                                torn.Enqueue($"{Path.GetFileName(read)} read as {bytes.Length} bytes: {Encoding.UTF8.GetString(bytes)[..Math.Min(40, bytes.Length)]}");
                            }
                            break;
                        case 3:
                            File.Move(Name(), Name(), overwrite: random.Next(2) == 0);
                            break;
                        case 4:
                            File.Delete(Name());
                            break;
                        case 5:
                            var created = Payload(tag, random);
                            written[tag] = true;
                            using (var stream = new FileStream(Name(), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                            {
                                stream.Write(created);
                            }
                            break;
                        default:
                            var saved = Payload(tag, random);
                            written[tag] = true;
                            WordOrBackOut(Name(), saved, t * 1000 + round);
                            break;
                    }
                    outcomes.AddOrUpdate("ok", 1, (_, c) => c + 1);
                }
                catch (Exception e) when (e is not Xunit.Sdk.XunitException)
                {
                    outcomes.AddOrUpdate(Classify(e), 1, (_, c) => c + 1);
                }
            }
        });
        output.WriteLine(string.Join(", ", outcomes.OrderBy(o => o.Key).Select(o => $"{o.Key} {o.Value}")));
        Assert.DoesNotContain(outcomes.Keys, k => k.StartsWith("unexpected", StringComparison.Ordinal));
        Assert.Empty(torn);

        await drive.SettleAsync();
        // Every writer writes something: empty files and reads would come from a create WinFsp refuses after it was made here (its
        // sharing is checked here first, see Vfs365FileSystem.Sharing, so only for other reasons). Counted; none seen in 300 runs.
        var empty = drive.ServerTree("shared").Count(p => drive.Fake.BytesOf(p) is { Length: 0 });
        output.WriteLine($"{emptyReads} empty read(s), {empty} empty file(s) left");
        try
        {
            AssertVolumeMatchesServer(drive);
            foreach (var path in drive.ServerTree("shared"))
            {
                var content = drive.Fake.BytesOf(path)!;
                Assert.True(content.Length == 0 || Whole(content), $"{path} is torn: {content.Length} bytes, {Encoding.UTF8.GetString(content)[..Math.Min(30, content.Length)]}");
                Assert.True(content.Length == 0 || written.ContainsKey(TagOf(content)), $"{path} holds content nobody wrote");
            }
            Assert.Empty(drive.Errors());
        }
        catch
        {
            foreach (var line in drive.Lines().Where(l => !l.StartsWith("notified", StringComparison.Ordinal)))
            {
                output.WriteLine(line);
            }
            throw;
        }
    }

    [MountedTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Office_saves_and_downloads_in_one_folder_at_once(bool winFspGuard)
    {
        const int threads = 6, rounds = 6;
        using var drive = MountedDrive.Start(winFspGuard, fake =>
        {
            fake.Add("office", folder: true);
            for (var t = 0; t < threads; t++)
            {
                fake.Add($"office/doc{t}.docx", Encoding.UTF8.GetBytes("seed"));
                fake.Add($"office/book{t}.xlsx", Encoding.UTF8.GetBytes("seed"));
            }
        });
        drive.Api.UploadDelay = _ => TimeSpan.FromMilliseconds(30); // a little network, so saves overlap
        var last = new ConcurrentDictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        RunThreads(threads, t =>
        {
            var random = new Random(t);
            void Step(string what, Action save)
            {
                try
                {
                    save();
                }
                catch (Exception e)
                {
                    throw new InvalidOperationException($"{what}: {e.GetType().Name}: {e.Message} at {e.StackTrace?.Split('\n').FirstOrDefault(l => l.Contains("SavePatterns"))?.Trim()}", e);
                }
            }
            for (var round = 0; round < rounds; round++)
            {
                var tag = $"t{t}r{round}";
                Step($"word doc{t} round {round}", () => SavePatterns.Word(drive.PathOf($"office/doc{t}.docx"), last[$"office/doc{t}.docx"] = Payload(tag + "w", random), t * 100 + round));
                Step($"excel book{t} round {round}", () => SavePatterns.Excel(drive.PathOf($"office/book{t}.xlsx"), last[$"office/book{t}.xlsx"] = Payload(tag + "x", random), random));
                Step($"chrome{t}-{round}", () => SavePatterns.ChromeDownload(drive.PathOf($"office/chrome{t}-{round}.zip"), last[$"office/chrome{t}-{round}.zip"] = Payload(tag + "c", random)));
                Step($"firefox{t}-{round}", () => SavePatterns.FirefoxDownload(drive.PathOf($"office/firefox{t}-{round}.zip"), last[$"office/firefox{t}-{round}.zip"] = Payload(tag + "f", random)));
            }
        });
        await drive.SettleAsync();

        AssertSameTree(last.Keys.Append("office"), drive.ServerTree(), "the server");
        foreach (var (path, content) in last)
        {
            Assert.Equal(content, drive.Fake.BytesOf(path));
        }
        AssertVolumeMatchesServer(drive);
        Assert.Empty(drive.Errors());
    }

    [MountedTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_folder_renamed_while_its_files_wait_to_upload_takes_them_along(bool winFspGuard)
    {
        using var drive = MountedDrive.Start(winFspGuard);
        drive.Api.UploadDelay = name => name.StartsWith("file", StringComparison.Ordinal) ? TimeSpan.FromMilliseconds(400) : TimeSpan.Zero;
        Directory.CreateDirectory(drive.PathOf("batch"));
        var contents = new Dictionary<string, byte[]>();
        var random = new Random(1);
        for (var i = 0; i < 20; i++)
        {
            File.WriteAllBytes(drive.PathOf($"batch/file{i:00}.txt"), contents[$"file{i:00}.txt"] = Payload($"f{i}", random));
        }
        Directory.Move(drive.PathOf("batch"), drive.PathOf("moved"));
        Directory.Move(drive.PathOf("moved"), drive.PathOf("moved again"));
        await drive.SettleAsync();

        AssertSameTree(contents.Keys.Select(n => $"moved again/{n}").Append("moved again"), drive.ServerTree(), "the server");
        foreach (var (name, content) in contents)
        {
            Assert.Equal(content, drive.Fake.BytesOf($"moved again/{name}"));
        }
        AssertVolumeMatchesServer(drive);
        Assert.Empty(drive.Errors());
    }

    [MountedTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Downloads_renamed_after_their_close_end_under_their_name_only(bool winFspGuard)
    {
        using var drive = MountedDrive.Start(winFspGuard);
        drive.Api.UploadDelay = _ => TimeSpan.FromMilliseconds(500);
        var random = new Random(2);
        var chrome = Payload("chrome", random);
        var firefox = Payload("firefox", random);
        var reader = Task.Run(() =>
        {
            // A virus scanner opening the finished file at once. Right after the rename the renaming handle (delete access) may
            // still be open, which refuses readers that don't share delete, as on any volume: tried again
            for (var i = 0; ; i++)
            {
                try
                {
                    return File.ReadAllBytes(drive.PathOf("chrome.zip"));
                }
                catch (IOException) when (i < 300)
                {
                    Thread.Sleep(10);
                }
            }
        });
        SavePatterns.ChromeDownload(drive.PathOf("chrome.zip"), chrome);
        SavePatterns.FirefoxDownload(drive.PathOf("firefox.zip"), firefox);
        Assert.Equal(chrome, await reader);
        await drive.SettleAsync();

        AssertSameTree(["chrome.zip", "firefox.zip"], drive.ServerTree(), "the server");
        Assert.Equal(chrome, drive.Fake.BytesOf("chrome.zip"));
        Assert.Equal(firefox, drive.Fake.BytesOf("firefox.zip"));
        AssertVolumeMatchesServer(drive);
        Assert.Empty(drive.Errors());
    }

    [MountedFact]
    public void Open_or_create_of_one_new_name_from_many_threads_at_once_never_fails()
    {
        // Without WinFsp's namespace lock, two such creates can both find the name free; the second then opens the first's file
        using var drive = MountedDrive.Start(winFspGuard: false);
        Directory.CreateDirectory(drive.PathOf("race"));
        var failures = new ConcurrentQueue<string>();
        for (var name = 0; name < 30; name++)
        {
            var path = drive.PathOf($"race/r{name}.txt");
            var mode = name % 2 == 0 ? FileMode.OpenOrCreate : FileMode.Create;
            RunThreads(8, _ =>
            {
                try
                {
                    using var stream = new FileStream(path, mode, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
                }
                catch (IOException e)
                {
                    failures.Enqueue($"{mode} {Path.GetFileName(path)}: {Classify(e)}");
                }
            });
        }
        Assert.Empty(failures);
        Assert.Equal(30, Directory.GetFiles(drive.PathOf("race")).Length);
    }
}
