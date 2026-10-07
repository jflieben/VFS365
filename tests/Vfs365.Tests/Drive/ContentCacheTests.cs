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
