using System.Security.Cryptography;
using System.Text;
using Vfs365.Core.Drive;

namespace Vfs365.Tests.Drive;

public sealed class ContentCacheTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "vfs365-tests", Guid.NewGuid().ToString("N"));
    static readonly CacheCipher KeyA = new(RandomNumberGenerator.GetBytes(32));

    string Cache => Path.Combine(directory, "cache");

    static async Task<byte[]> ReadAll(IContentSource source, int chunk = 4096)
    {
        var result = new byte[source.Length];
        for (var offset = 0; offset < result.Length;)
        {
            offset += await source.ReadAsync(offset, result.AsMemory(offset, Math.Min(chunk, result.Length - offset)), default);
        }
        return result;
    }

    static bool Contains(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;

    /// <summary>Content files, in the drive folders (the key marker and the name index are at the top).</summary>
    IEnumerable<string> CacheFiles() => Directory.EnumerateDirectories(Cache).SelectMany(d => Directory.EnumerateFiles(d));

    /// <summary>
    /// Lines in the name index, -1 while it is missing. Read without keeping writers out: the cache appends in the background and
    /// leaves a line for a later trim when it can't open the file.
    /// </summary>
    static int LinesIn(string path)
    {
        try
        {
            return Encoding.UTF8.GetString(ReadShared(path)).Count(c => c == '\n');
        }
        catch (IOException)
        {
            return -1;
        }
    }

    static byte[] ReadShared(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[file.Length];
        file.ReadExactly(bytes);
        return bytes;
    }

    /// <summary>The cache trims in the background.</summary>
    static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }
        Assert.True(condition());
    }

    [Fact]
    public async Task Encrypted_content_reads_back_but_is_not_plain_on_disk()
    {
        var api = new FakeDriveApi();
        var text = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("confidential quarterly figures ", 400)));
        var item = api.Add("report.txt", text);
        var cache = new ContentCache(Cache, api, new ContentCacheOptions { Cipher = KeyA });

        using (var first = cache.Open("d", item.Id, item.CTag, item.Size))
        {
            Assert.Equal(text, await ReadAll(first, 1000));
        }
        using (var again = cache.Open("d", item.Id, item.CTag, item.Size))
        {
            var middle = new byte[100];
            await again.ReadAsync(37, middle, default);
            Assert.Equal(text.AsSpan(37, 100).ToArray(), middle);
        }
        var copy = Path.Combine(directory, "copy.txt");
        await cache.CopyToAsync("d", item.Id, item.CTag, item.Size, copy, default);

        Assert.Equal(text, await File.ReadAllBytesAsync(copy));
        var stored = await File.ReadAllBytesAsync(CacheFiles().Single());
        Assert.False(Contains(stored, Encoding.UTF8.GetBytes("confidential")));
        Assert.Equal(1, api.Downloads);
    }

    [Fact]
    public async Task Multi_block_streaming_download_round_trips_through_the_cipher()
    {
        var api = new FakeDriveApi();
        var bytes = RandomNumberGenerator.GetBytes(3 * (1 << 20) + 12345);
        var item = api.Add("big.bin", bytes);
        var cache = new ContentCache(Cache, api, new ContentCacheOptions { Cipher = KeyA });

        using (var source = cache.Open("d", item.Id, item.CTag, item.Size))
        {
            Assert.Equal(bytes, await ReadAll(source, 65536));
        }
        using var cached = cache.Open("d", item.Id, item.CTag, item.Size);

        Assert.Equal(bytes, await ReadAll(cached, 100000));
    }

    [Fact]
    public async Task Content_under_another_key_is_dropped()
    {
        var api = new FakeDriveApi();
        var item = api.Add("a.txt", content: "abc");
        var first = new ContentCache(Cache, api, new ContentCacheOptions { Cipher = KeyA });
        await first.CopyToAsync("d", item.Id, item.CTag, item.Size, Path.Combine(directory, "x"), default);

        var second = new ContentCache(Cache, api, new ContentCacheOptions { Cipher = new CacheCipher(RandomNumberGenerator.GetBytes(32)) });

        Assert.Equal(0, second.Usage.Files);
        Assert.Empty(CacheFiles());
    }

    [Fact]
    public async Task Least_recently_used_files_are_evicted_beyond_the_limit()
    {
        var api = new FakeDriveApi();
        var cache = new ContentCache(Cache, api, new ContentCacheOptions { MaxBytes = 3500 });
        for (var i = 0; i < 6; i++)
        {
            var item = api.Add($"f{i}.bin", new byte[1000]);
            await cache.FetchAsync("d", item.Id, item.CTag, item.Size, default);
            await Task.Delay(20);
        }

        await Eventually(() => cache.Usage.Bytes <= 3500);
        Assert.True(cache.Usage.Files >= 2);
        using var newest = cache.Open("d", api.ItemAt("f5.bin")!.Id, api.ItemAt("f5.bin")!.CTag, 1000);
        Assert.Equal(6, api.Downloads);
    }

    [Fact]
    public async Task A_new_version_replaces_the_cached_one()
    {
        var api = new FakeDriveApi();
        var item = api.Add("a.txt", content: "one");
        var cache = new ContentCache(Cache, api);
        await cache.CopyToAsync("d", item.Id, item.CTag, item.Size, Path.Combine(directory, "x"), default);
        api.RemoteEdit("a.txt", "two!");
        var edited = api.ItemAt("a.txt")!;

        await cache.CopyToAsync("d", edited.Id, edited.CTag, edited.Size, Path.Combine(directory, "x"), default);

        await Eventually(() => CacheFiles().Count() == 1);
        Assert.Equal("two!", await File.ReadAllTextAsync(Path.Combine(directory, "x")));
    }

    [Fact]
    public async Task Adopted_upload_is_stored_encrypted_and_served_without_a_download()
    {
        var api = new FakeDriveApi();
        var cache = new ContentCache(Cache, api, new ContentCacheOptions { Cipher = KeyA });
        Directory.CreateDirectory(directory);
        var staging = Path.Combine(directory, "staged");
        await File.WriteAllTextAsync(staging, "fresh content from a save");
        var item = new DriveItemInfo("id-x", "x.txt", false, new FileInfo(staging).Length, default, default, "e1", "c1");

        cache.Adopt("d", item, staging);
        using var source = cache.Open("d", item.Id, item.CTag, item.Size);

        Assert.False(File.Exists(staging));
        Assert.Equal("fresh content from a save", Encoding.UTF8.GetString(await ReadAll(source)));
        Assert.False(Contains(await File.ReadAllBytesAsync(CacheFiles().Single()), Encoding.UTF8.GetBytes("fresh")));
        Assert.Equal(0, api.Downloads);
    }

    [Fact]
    public async Task Forgetting_an_item_or_drive_removes_its_content()
    {
        var api = new FakeDriveApi();
        var a = api.Add("a.txt", content: "aaa");
        var b = api.Add("b.txt", content: "bbb");
        var cache = new ContentCache(Cache, api);
        await cache.CopyToAsync("d1", a.Id, a.CTag, a.Size, Path.Combine(directory, "x"), default);
        await cache.CopyToAsync("d2", b.Id, b.CTag, b.Size, Path.Combine(directory, "x"), default);

        cache.ForgetItem("d1", a.Id);
        Assert.Equal(1, cache.Usage.Files);
        cache.ForgetDrive("d2");
        Assert.Equal((0L, 0), cache.Usage);
    }

    [Fact]
    public async Task A_slow_download_of_an_older_version_keeps_the_newer_one()
    {
        var api = new FakeDriveApi();
        var old = api.Add("a.txt", content: "old content");
        var cache = new ContentCache(Cache, api, new ContentCacheOptions { Cipher = KeyA });
        var hold = new TaskCompletionSource();
        api.BeforeDownload = () => hold.Task;
        var slow = cache.FetchAsync("d", old.Id, old.CTag, old.Size, default);

        Directory.CreateDirectory(directory);
        var staging = Path.Combine(directory, "saved");
        await File.WriteAllTextAsync(staging, "newer content");
        var newer = old with { Size = new FileInfo(staging).Length, ETag = "e-new", CTag = "c-new" };
        cache.Adopt("d", newer, staging);
        hold.SetResult();
        await slow;

        using var source = cache.Open("d", newer.Id, newer.CTag, newer.Size);
        Assert.Equal("newer content", Encoding.UTF8.GetString(await ReadAll(source)));
        Assert.Equal(1, api.Downloads);
    }

    [Fact]
    public async Task A_cache_file_changed_on_disk_fails_its_check_and_is_downloaded_again()
    {
        var api = new FakeDriveApi();
        var text = RandomNumberGenerator.GetBytes(200_000);
        var item = api.Add("a.bin", text);
        var cache = new ContentCache(Cache, api, new ContentCacheOptions { Cipher = KeyA });
        await cache.FetchAsync("d", item.Id, item.CTag, item.Size, default);
        var stored = CacheFiles().Single();
        var bytes = await File.ReadAllBytesAsync(stored);
        bytes[100_000] ^= 1;
        await File.WriteAllBytesAsync(stored, bytes);

        using (var damaged = cache.Open("d", item.Id, item.CTag, item.Size))
        {
            var head = new byte[1000];
            await damaged.ReadAsync(0, head, default);
            Assert.Equal(text.AsSpan(0, 1000).ToArray(), head); // other chunks still verify
        }
        await Assert.ThrowsAsync<CacheDamagedException>(async () =>
        {
            using var again = cache.Open("d", item.Id, item.CTag, item.Size);
            await again.ReadAsync(100_000, new byte[10], default);
        });
        using var fresh = cache.Open("d", item.Id, item.CTag, item.Size);

        Assert.Equal(text, await ReadAll(fresh));
        Assert.Equal(2, api.Downloads);
    }

    [Fact]
    public async Task The_same_content_is_encrypted_differently_in_each_file()
    {
        var api = new FakeDriveApi();
        var item = api.Add("a.txt", Encoding.UTF8.GetBytes(new string('x', 5000)));
        var cache = new ContentCache(Cache, api, new ContentCacheOptions { Cipher = KeyA });
        await cache.FetchAsync("d1", item.Id, item.CTag, item.Size, default);
        await cache.FetchAsync("d2", item.Id, item.CTag, item.Size, default);

        var files = CacheFiles().Select(f => File.ReadAllBytes(f)[..5000]).ToList();
        Assert.Equal(2, files.Count);
        Assert.NotEqual(files[0], files[1]);
    }

    [Fact]
    public async Task A_cache_in_the_older_format_is_dropped()
    {
        var api = new FakeDriveApi();
        var item = api.Add("a.txt", content: "abc");
        var cache = new ContentCache(Cache, api, new ContentCacheOptions { Cipher = KeyA });
        await cache.FetchAsync("d", item.Id, item.CTag, item.Size, default);
        await File.WriteAllTextAsync(Path.Combine(Cache, "key"), KeyA.KeyId); // what 0.3 wrote

        var reopened = new ContentCache(Cache, api, new ContentCacheOptions { Cipher = KeyA });

        Assert.Equal((0L, 0), reopened.Usage);
        Assert.Empty(CacheFiles());
    }

    [Fact]
    public async Task Removing_the_same_file_twice_at_once_counts_it_once()
    {
        var api = new FakeDriveApi();
        var a = api.Add("a.bin", new byte[3000]);
        var b = api.Add("b.bin", new byte[5000]);
        var cache = new ContentCache(Cache, api);
        await cache.FetchAsync("d", a.Id, a.CTag, a.Size, default);
        await cache.FetchAsync("d", b.Id, b.CTag, b.Size, default);

        Parallel.For(0, 16, _ => cache.ForgetItem("d", a.Id));

        Assert.Equal((5000L, 1), cache.Usage);
    }

    [Fact]
    public async Task An_empty_file_needs_no_download()
    {
        var api = new FakeDriveApi();
        var item = api.Add("empty.txt", []);
        var cache = new ContentCache(Cache, api, new ContentCacheOptions { Cipher = KeyA, MaxBytes = 1 });

        await cache.CopyToAsync("d", item.Id, item.CTag, 0, Path.Combine(directory, "copy"), default);
        using var source = cache.Open("d", item.Id, item.CTag, 0);

        Assert.Equal(0, source.Length);
        Assert.Equal(0, await source.ReadAsync(0, new byte[10], default));
        Assert.Equal(0, api.Downloads);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_length_of_cached_content_is_known_after_a_restart(bool encrypted)
    {
        var api = new FakeDriveApi();
        var item = api.Add("a.bin", RandomNumberGenerator.GetBytes(200_000));
        var options = new ContentCacheOptions { Cipher = encrypted ? KeyA : null };
        await new ContentCache(Cache, api, options).FetchAsync("d", item.Id, item.CTag, item.Size, default);

        var reopened = new ContentCache(Cache, api, options);

        Assert.Equal(200_000, reopened.LengthOf("d", item.Id, item.CTag));
        Assert.Null(reopened.LengthOf("d", item.Id, "c-another-version"));
        Assert.Null(reopened.LengthOf("d", "id-other", item.CTag));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(CacheCipher.ChunkSize - 1)]
    [InlineData(CacheCipher.ChunkSize)]
    [InlineData(CacheCipher.ChunkSize + 1)]
    [InlineData(5 * CacheCipher.ChunkSize + 777)]
    public void A_cache_file_size_gives_back_its_content_length(int length) =>
        Assert.Equal(length, CacheCipher.ContentLength(CacheCipher.StoredLength(length)));

    [Fact]
    public void A_size_no_cache_file_can_have_holds_no_content()
    {
        Assert.Equal(-1, CacheCipher.ContentLength(10)); // shorter than the footer
        Assert.Equal(-1, CacheCipher.ContentLength(CacheCipher.StoredLength(0) + 8)); // a chunk without room for its tag
        Assert.Equal(-1, CacheCipher.ContentLength(CacheCipher.StoredLength(CacheCipher.ChunkSize) + 1));
    }

    [Fact]
    public async Task An_item_deleted_while_it_downloads_is_not_kept()
    {
        var api = new FakeDriveApi();
        var item = api.Add("a.txt", content: "old content");
        var cache = new ContentCache(Cache, api, new ContentCacheOptions { Cipher = KeyA });
        var hold = new TaskCompletionSource();
        api.BeforeDownload = () => hold.Task;
        var fetch = cache.FetchAsync("d", item.Id, item.CTag, item.Size, default);

        cache.ForgetItem("d", item.Id);
        hold.SetResult();
        await fetch;

        Assert.Equal((0L, 0), cache.Usage);
        Assert.Null(cache.LengthOf("d", item.Id, item.CTag));
        Assert.Empty(CacheFiles());
    }

    [Fact]
    public async Task Cached_files_opened_from_many_threads_at_once_serve_their_own_content()
    {
        var api = new FakeDriveApi();
        var cache = new ContentCache(Cache, api, new ContentCacheOptions { Cipher = KeyA });
        var items = new List<(DriveItemInfo Item, byte[] Bytes)>();
        for (var i = 0; i < 8; i++)
        {
            var bytes = RandomNumberGenerator.GetBytes(70_000 + i);
            var item = api.Add($"f{i}.bin", bytes);
            await cache.FetchAsync("d", item.Id, item.CTag, item.Size, default);
            items.Add((item, bytes));
        }

        await Task.WhenAll(Enumerable.Range(0, 16).Select(t => Task.Run(async () =>
        {
            for (var i = 0; i < 200; i++)
            {
                var (item, bytes) = items[(t + i) % items.Count];
                using var source = cache.Open("d", item.Id, item.CTag, item.Size);
                var page = new byte[1000];
                Assert.Equal(1000, await source.ReadAsync(65_000, page, default));
                Assert.Equal(bytes.AsSpan(65_000, 1000).ToArray(), page);
            }
        })));

        Assert.Equal(8, api.Downloads);
    }

    [Fact]
    public async Task Looking_up_a_cached_length_allocates_nothing()
    {
        var api = new FakeDriveApi();
        var item = api.Add("a.bin", new byte[5000]);
        var cache = new ContentCache(Cache, api, new ContentCacheOptions { Cipher = KeyA });
        await cache.FetchAsync("d", item.Id, item.CTag, item.Size, default);
        long? Look(int i) => cache.LengthOf("d", i % 2 == 0 ? item.Id : "id-not-cached", item.CTag);
        for (var i = 0; i < 100; i++)
        {
            Look(i); // first calls set up comparers
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        long found = 0;
        for (var i = 0; i < 1000; i++)
        {
            found += Look(i) ?? 0;
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(500 * 5000, found);
    }

    [Fact]
    public async Task A_cache_file_deleted_behind_its_back_is_downloaded_again()
    {
        var api = new FakeDriveApi();
        var item = api.Add("a.txt", content: "still here");
        var cache = new ContentCache(Cache, api, new ContentCacheOptions { Cipher = KeyA });
        await cache.FetchAsync("d", item.Id, item.CTag, item.Size, default);
        File.Delete(CacheFiles().Single());

        using var source = cache.Open("d", item.Id, item.CTag, item.Size);

        Assert.Equal("still here", Encoding.UTF8.GetString(await ReadAll(source)));
        Assert.Equal(2, api.Downloads);
    }

    [Fact]
    public async Task Files_of_a_cache_without_its_name_index_are_still_found()
    {
        // A cache from before the name index, or one whose index was lost: found by their hashed names, then listed again
        var api = new FakeDriveApi();
        var item = api.Add("a.bin", RandomNumberGenerator.GetBytes(100_000));
        var options = new ContentCacheOptions { Cipher = KeyA };
        await new ContentCache(Cache, api, options).FetchAsync("d", item.Id, item.CTag, item.Size, default);
        var names = Path.Combine(Cache, "names");
        await Eventually(() => LinesIn(names) == 1); // written in the background
        File.Delete(names);

        var reopened = new ContentCache(Cache, api, options);
        Assert.Null(reopened.LengthOf("d", "id-other", item.CTag));
        Assert.Equal(100_000, reopened.LengthOf("d", item.Id, item.CTag));
        await Eventually(() => LinesIn(names) == 1);
        var again = new ContentCache(Cache, api, options);

        Assert.Equal(100_000, again.LengthOf("d", item.Id, item.CTag));
        using var cached = again.Open("d", item.Id, item.CTag, item.Size);
        Assert.Equal(1, api.Downloads);
    }

    [Fact]
    public async Task The_name_index_is_sealed()
    {
        var api = new FakeDriveApi();
        var item = api.Add("secret-plan.docx", content: "abc");
        var cache = new ContentCache(Cache, api, new ContentCacheOptions { Cipher = KeyA });
        await cache.FetchAsync("b!drive-id", item.Id, item.CTag, item.Size, default);

        await Eventually(() => LinesIn(Path.Combine(Cache, "names")) == 1);
        var index = await File.ReadAllTextAsync(Path.Combine(Cache, "names"));
        Assert.DoesNotContain(item.Id, index);
        Assert.DoesNotContain("b!drive-id", index);
        Assert.Equal(3, new ContentCache(Cache, api, new ContentCacheOptions { Cipher = KeyA }).LengthOf("b!drive-id", item.Id, item.CTag));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_empty_file_is_cached_and_known_after_a_restart(bool encrypted)
    {
        var api = new FakeDriveApi();
        var item = api.Add("empty.txt", []);
        var options = new ContentCacheOptions { Cipher = encrypted ? KeyA : null };
        var cache = new ContentCache(Cache, api, options);

        using (var source = cache.Open("d", item.Id, item.CTag, 0))
        {
            Assert.Equal(0, source.Length);
            Assert.Equal(0, await source.ReadAsync(0, new byte[10], default));
        }
        Assert.True(cache.Has("d", item.Id, item.CTag));
        Assert.Equal(0, cache.LengthOf("d", item.Id, item.CTag));
        Assert.Equal(1, cache.Usage.Files);
        Assert.DoesNotContain(CacheFiles(), f => f.EndsWith(".part"));

        var reopened = new ContentCache(Cache, api, options);
        Assert.Equal(0, reopened.LengthOf("d", item.Id, item.CTag));
        Assert.Equal(0, api.Downloads);
    }

    [Fact]
    public async Task A_file_larger_than_the_cache_target_is_not_kept_and_the_rest_stays()
    {
        var api = new FakeDriveApi();
        var cache = new ContentCache(Cache, api, new ContentCacheOptions { MaxBytes = 10_000 });
        var small = new List<DriveItemInfo>();
        for (var i = 0; i < 3; i++)
        {
            small.Add(api.Add($"s{i}.bin", new byte[2000]));
            await cache.FetchAsync("d", small[i].Id, small[i].CTag, 2000, default);
        }
        var big = api.Add("big.bin", RandomNumberGenerator.GetBytes(9500));

        using (var source = cache.Open("d", big.Id, big.CTag, big.Size))
        {
            Assert.Equal(9500, (await ReadAll(source, 3000)).Length); // still read whole
        }
        await Task.Delay(100);

        Assert.Equal((6000L, 3), cache.Usage);
        Assert.Null(cache.LengthOf("d", big.Id, big.CTag));
        Assert.All(small, s => Assert.Equal(2000, cache.LengthOf("d", s.Id, s.CTag)));
        Assert.Equal(3, CacheFiles().Count());
    }

    [Fact]
    public async Task An_upload_above_an_eighth_of_the_cache_is_not_adopted()
    {
        var api = new FakeDriveApi();
        var cache = new ContentCache(Cache, api, new ContentCacheOptions { Cipher = KeyA, MaxBytes = 80_000 });
        Directory.CreateDirectory(directory);
        var large = Path.Combine(directory, "large");
        var small = Path.Combine(directory, "small");
        await File.WriteAllBytesAsync(large, new byte[10_001]);
        await File.WriteAllBytesAsync(small, new byte[9_000]);

        cache.Adopt("d", new DriveItemInfo("id-l", "l.bin", false, 10_001, default, default, "e1", "c1"), large);
        cache.Adopt("d", new DriveItemInfo("id-s", "s.bin", false, 9_000, default, default, "e2", "c2"), small);

        Assert.False(File.Exists(large));
        Assert.False(File.Exists(small));
        Assert.Null(cache.LengthOf("d", "id-l", "c1"));
        Assert.Equal(9_000, cache.LengthOf("d", "id-s", "c2"));
        Assert.Equal(1, cache.Usage.Files);
    }

    [Fact]
    public async Task A_large_copy_goes_straight_to_its_destination_without_a_cache_file()
    {
        var api = new FakeDriveApi();
        var bytes = RandomNumberGenerator.GetBytes(3 * (1 << 20) + 5);
        var item = api.Add("big.bin", bytes);
        var cache = new ContentCache(Cache, api, new ContentCacheOptions { Cipher = KeyA, MaxBytes = 8 << 20 });
        Directory.CreateDirectory(directory);
        var staging = Path.Combine(directory, "staging");
        File.Create(staging).Dispose();

        await cache.CopyToAsync("d", item.Id, item.CTag, item.Size, staging, default);

        Assert.Equal(bytes, await File.ReadAllBytesAsync(staging));
        Assert.Equal((0L, 0), cache.Usage);
        Assert.Empty(CacheFiles());
        Assert.Equal(1, api.Downloads);
    }

    [Fact]
    public async Task A_released_download_is_dropped_after_the_keep_time()
    {
        var api = new FakeDriveApi();
        var item = api.Add("video.mp4", RandomNumberGenerator.GetBytes(6 << 20));
        var cache = new ContentCache(Cache, api, new ContentCacheOptions { Cipher = KeyA, ReleasedKeep = TimeSpan.FromMilliseconds(100) });
        using (var source = cache.Open("d", item.Id, item.CTag, item.Size))
        {
            await source.ReadAsync(0, new byte[4096], default);
        }
        Assert.False(cache.Has("d", item.Id, item.CTag)); // kept for a reopen, but nobody is reading it

        await Eventually(() => !CacheFiles().Any());
        using var again = cache.Open("d", item.Id, item.CTag, item.Size);
        await again.ReadAsync(0, new byte[4096], default);

        Assert.Equal(2, api.Downloads);
    }

    [Fact]
    public async Task Clearing_the_cache_drops_a_released_download()
    {
        // Another account must never continue a download made with the previous one's access
        var api = new FakeDriveApi();
        var item = api.Add("video.mp4", RandomNumberGenerator.GetBytes(6 << 20));
        var cache = new ContentCache(Cache, api, new ContentCacheOptions { Cipher = KeyA });
        using (var source = cache.Open("d", item.Id, item.CTag, item.Size))
        {
            await source.ReadAsync(0, new byte[4096], default);
        }

        cache.Clear();
        using var again = cache.Open("d", item.Id, item.CTag, item.Size);
        await again.ReadAsync(0, new byte[4096], default);

        Assert.Equal(2, api.Downloads);
    }

    [Fact]
    public async Task Opening_a_file_used_long_ago_sets_its_last_access_time()
    {
        // The last access time orders eviction after a restart; Windows may not keep it up to date by itself
        var api = new FakeDriveApi();
        var item = api.Add("a.txt", content: "abc");
        var options = new ContentCacheOptions { Cipher = KeyA };
        await new ContentCache(Cache, api, options).FetchAsync("d", item.Id, item.CTag, item.Size, default);
        var file = CacheFiles().Single();
        var longAgo = DateTime.UtcNow.AddDays(-30);
        File.SetLastAccessTimeUtc(file, longAgo);
        var reopened = new ContentCache(Cache, api, options);

        using (var source = reopened.Open("d", item.Id, item.CTag, item.Size))
        {
            Assert.Equal("abc", Encoding.UTF8.GetString(await ReadAll(source)));
        }

        Assert.True(File.GetLastAccessTimeUtc(file) > DateTime.UtcNow.AddMinutes(-5));
        Assert.Equal(1, api.Downloads);
    }

    [Fact]
    public async Task Least_recently_used_files_go_beyond_the_file_limit()
    {
        var api = new FakeDriveApi();
        var cache = new ContentCache(Cache, api, new ContentCacheOptions { Cipher = KeyA, MaxFiles = 10 });
        var items = new List<DriveItemInfo>();
        for (var i = 0; i < 25; i++)
        {
            items.Add(api.Add($"f{i}.bin", new byte[100]));
            await cache.FetchAsync("d", items[i].Id, items[i].CTag, 100, default);
            await Task.Delay(5);
        }

        await Eventually(() => cache.Usage.Files <= 10);
        await Task.Delay(100); // a trim still running
        Assert.InRange(cache.Usage.Files, 9, 10); // down to 90% of the limit
        var kept = Enumerable.Range(0, 25).Where(i => cache.LengthOf("d", items[i].Id, items[i].CTag) is not null).ToList();
        Assert.Equal(Enumerable.Range(25 - kept.Count, kept.Count), kept); // the newest
        Assert.Equal(kept.Count, CacheFiles().Count());
        Assert.True(cache.Usage.Bytes < 10 * 200); // far below the size limit: the count alone trimmed
        Assert.Equal(25, api.Downloads);
    }

    [Fact]
    public async Task A_file_that_keeps_being_used_stays_when_the_file_limit_trims()
    {
        var api = new FakeDriveApi();
        var cache = new ContentCache(Cache, api, new ContentCacheOptions { MaxFiles = 5 });
        var first = api.Add("first.bin", RandomNumberGenerator.GetBytes(100));
        await cache.FetchAsync("d", first.Id, first.CTag, 100, default);
        for (var i = 0; i < 15; i++)
        {
            var item = api.Add($"f{i}.bin", new byte[100]);
            await cache.FetchAsync("d", item.Id, item.CTag, 100, default);
            await Task.Delay(5);
            using var again = cache.Open("d", first.Id, first.CTag, 100); // the oldest file, but used again and again
            await Task.Delay(5);
        }

        await Eventually(() => cache.Usage.Files <= 5);
        Assert.Equal(100, cache.LengthOf("d", first.Id, first.CTag));
        using var source = cache.Open("d", first.Id, first.CTag, 100);
        Assert.Equal(api.BytesOf("first.bin"), await ReadAll(source));
        Assert.Equal(16, api.Downloads); // never downloaded again
    }

    [Fact]
    public async Task A_reader_keeps_its_content_when_the_file_limit_evicts_the_file()
    {
        var api = new FakeDriveApi();
        var cache = new ContentCache(Cache, api, new ContentCacheOptions { Cipher = KeyA, MaxFiles = 5 });
        var first = api.Add("first.bin", RandomNumberGenerator.GetBytes(100_000));
        await cache.FetchAsync("d", first.Id, first.CTag, first.Size, default);
        using var held = cache.Open("d", first.Id, first.CTag, first.Size);
        for (var i = 0; i < 15; i++)
        {
            var item = api.Add($"f{i}.bin", new byte[100]);
            await cache.FetchAsync("d", item.Id, item.CTag, 100, default);
            await Task.Delay(5);
        }

        await Eventually(() => cache.LengthOf("d", first.Id, first.CTag) is null); // evicted as the least recently used, like the size limit does
        Assert.Equal(api.BytesOf("first.bin"), await ReadAll(held));
    }

    [Theory]
    [InlineData(3500, 4)] // the size limit is the stricter one
    [InlineData(100_000, 3)] // the file limit is
    public async Task The_size_limit_and_the_file_limit_both_hold(long maxBytes, int maxFiles)
    {
        var api = new FakeDriveApi();
        var cache = new ContentCache(Cache, api, new ContentCacheOptions { MaxBytes = maxBytes, MaxFiles = maxFiles });
        for (var i = 0; i < 8; i++)
        {
            var item = api.Add($"f{i}.bin", new byte[1000]);
            await cache.FetchAsync("d", item.Id, item.CTag, item.Size, default);
            await Task.Delay(20);
        }

        await Eventually(() => cache.Usage.Bytes <= maxBytes && cache.Usage.Files <= maxFiles);
        Assert.True(cache.Usage.Files >= 2);
        Assert.Equal(1000, cache.LengthOf("d", api.ItemAt("f7.bin")!.Id, api.ItemAt("f7.bin")!.CTag));
        Assert.Null(cache.LengthOf("d", api.ItemAt("f0.bin")!.Id, api.ItemAt("f0.bin")!.CTag));
        Assert.Equal(cache.Usage.Files, CacheFiles().Count());
    }

    [Fact]
    public async Task Every_cached_version_of_an_item_goes_with_it()
    {
        // Two versions of one item on disk (the older one wasn't removed before the last stop), next to another item
        var api = new FakeDriveApi();
        var options = new ContentCacheOptions { Cipher = KeyA };
        var cache = new ContentCache(Cache, api, options);
        var one = api.Add("a.txt", content: "one");
        await cache.FetchAsync("d", one.Id, one.CTag, one.Size, default);
        var older = CacheFiles().Single();
        var aside = Path.Combine(directory, "aside");
        File.Copy(older, aside);
        api.RemoteEdit("a.txt", "two!");
        var two = api.ItemAt("a.txt")!;
        await cache.FetchAsync("d", two.Id, two.CTag, two.Size, default);
        await Eventually(() => !File.Exists(older)); // the newer version replaced it
        var other = api.Add("b.txt", content: "other");
        await cache.FetchAsync("d", other.Id, other.CTag, other.Size, default);
        File.Copy(aside, older);

        var reopened = new ContentCache(Cache, api, options);
        Assert.Equal(3, reopened.Usage.Files); // listed by the name index, or found by their hashed names
        Assert.Equal(3, reopened.LengthOf("d", one.Id, one.CTag));
        Assert.Equal(4, reopened.LengthOf("d", two.Id, two.CTag));
        reopened.ForgetItem("d", one.Id);

        Assert.Equal(1, reopened.Usage.Files);
        Assert.Null(reopened.LengthOf("d", one.Id, one.CTag));
        Assert.Null(reopened.LengthOf("d", two.Id, two.CTag));
        Assert.Equal(5, reopened.LengthOf("d", other.Id, other.CTag));
        Assert.Single(CacheFiles());
    }

    [Fact]
    public async Task A_newer_version_replaces_every_older_one_found_at_start()
    {
        var api = new FakeDriveApi();
        var cache = new ContentCache(Cache, api);
        var one = api.Add("a.txt", content: "one");
        await cache.FetchAsync("d", one.Id, one.CTag, one.Size, default);
        var older = CacheFiles().Single();
        var aside = Path.Combine(directory, "aside");
        File.Copy(older, aside);
        api.RemoteEdit("a.txt", "two!");
        var two = api.ItemAt("a.txt")!;
        await cache.FetchAsync("d", two.Id, two.CTag, two.Size, default);
        await Eventually(() => !File.Exists(older));
        File.Copy(aside, older);
        var reopened = new ContentCache(Cache, api);
        Assert.Equal(2, reopened.Usage.Files);

        api.RemoteEdit("a.txt", "three");
        var three = api.ItemAt("a.txt")!;
        await reopened.FetchAsync("d", three.Id, three.CTag, three.Size, default);

        await Eventually(() => reopened.Usage.Files == 1);
        Assert.Equal(5, reopened.LengthOf("d", three.Id, three.CTag));
        Assert.Single(CacheFiles());
    }

    [Fact]
    public async Task A_name_index_that_lists_exactly_the_files_is_left_alone_at_start()
    {
        var api = new FakeDriveApi();
        var options = new ContentCacheOptions { Cipher = KeyA };
        var cache = new ContentCache(Cache, api, options);
        var items = new List<DriveItemInfo>();
        for (var i = 0; i < 3; i++)
        {
            items.Add(api.Add($"f{i}.txt", content: $"content {i}"));
            await cache.FetchAsync("d", items[i].Id, items[i].CTag, items[i].Size, default);
        }
        var names = Path.Combine(Cache, "names");
        await Eventually(() => LinesIn(names) == 3);
        var index = ReadShared(names);

        var reopened = new ContentCache(Cache, api, options);
        Assert.Equal(index, ReadShared(names)); // sealed lines differ each time they are written
        Assert.All(items, i => Assert.Equal(i.Size, reopened.LengthOf("d", i.Id, i.CTag)));

        reopened.ForgetItem("d", items[0].Id);
        var again = new ContentCache(Cache, api, options); // a line names no file any more: written anew without it
        Assert.Equal(2, LinesIn(names));
        Assert.Equal(2, again.Usage.Files);
        Assert.Equal(3, api.Downloads);
    }

    [Fact]
    public async Task Unfinished_downloads_of_an_earlier_run_go_at_start()
    {
        var api = new FakeDriveApi();
        var item = api.Add("a.txt", content: "abc");
        await new ContentCache(Cache, api).FetchAsync("d", item.Id, item.CTag, item.Size, default);
        var leftover = CacheFiles().Single() + ".0123.part";
        await File.WriteAllBytesAsync(leftover, new byte[500]);

        var reopened = new ContentCache(Cache, api);

        Assert.False(File.Exists(leftover));
        Assert.Equal((3L, 1), reopened.Usage);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
