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

    IEnumerable<string> CacheFiles() => Directory.EnumerateFiles(Cache, "*", SearchOption.AllDirectories).Where(f => Path.GetFileName(f) != "key");

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
            await cache.CopyToAsync("d", item.Id, item.CTag, item.Size, Path.Combine(directory, "x"), default);
            await Task.Delay(20);
        }

        Assert.True(cache.Usage.Bytes <= 3500);
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

        Assert.Single(CacheFiles());
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
