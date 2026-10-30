using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Vfs365.Core;
using Vfs365.Core.Discovery;
using Vfs365.Core.Drive;

namespace Vfs365.Tests.Drive;

/// <summary>Helpers for the tests of the change feed's holder index and known folders.</summary>
static class ReadPathTesting
{
    public static DriveEngine Create(string directory, IDriveApi api, Func<DateTimeOffset> clock, int maxCachedItems = 250_000) =>
        new(DriveNamespace.Build(new MyDrive("b!me", "https://x-my.sharepoint.com/personal/u"), [], (_, _) => Task.FromResult("unused"), DriveScope.OneDrive),
            api,
            new ContentCache(Path.Combine(directory, "cache"), api),
            new DriveEngineOptions { StagingDirectory = Path.Combine(directory, "staging"), Clock = clock, MaxCachedItems = maxCachedItems });

    public static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++)
        {
            await Task.Delay(10);
        }
        Assert.True(condition());
    }

    /// <summary>The engine's cached listing of a folder of drive b!me (no public view of it).</summary>
    public static object? ListingAt(DriveEngine engine, string drivePath) =>
        ((IDictionary)typeof(DriveEngine).GetField("listings", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(engine)!)[$"b!me|{drivePath}"];

    /// <summary>Entries in the change feed's holder index of drive b!me (no public view of it), read under the lock the engine takes.</summary>
    public static int Holders(DriveEngine engine)
    {
        var feed = ((IDictionary)typeof(DriveEngine).GetField("feeds", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(engine)!)["b!me"]!;
        var gate = (Lock)feed.GetType().GetProperty("ApplyGate")!.GetValue(feed)!;
        lock (gate)
        {
            return ((ICollection)feed.GetType().GetProperty("Holders")!.GetValue(feed)!).Count;
        }
    }

    /// <summary>A weak reference to the cached listing, taken here so no local of the caller keeps it.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static WeakReference Watch(DriveEngine engine, string drivePath) => new(ListingAt(engine, drivePath) ?? throw new InvalidOperationException($"{drivePath} isn't cached"));

    /// <summary>How many of the watched listings a full collection leaves.</summary>
    public static int Reachable(IEnumerable<WeakReference> watched)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return watched.Count(w => w.IsAlive);
    }
}

/// <summary>
/// Delegates to the fake, but every listing page brings new item objects, as parsing Graph's JSON does. Denied answers 403 to a
/// lookup of that path; FailBaselines fails that many requests for a new delta link.
/// </summary>
sealed class FreshItemsApi(FakeDriveApi inner) : IDriveApi
{
    public string? Denied { get; set; }
    public int FailBaselines;

    public async IAsyncEnumerable<IReadOnlyList<DriveItemInfo>> ListChildrenAsync(string driveId, string folderPath, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var page in inner.ListChildrenAsync(driveId, folderPath, ct))
        {
            yield return page.Select(i => i with { Name = new string(i.Name.AsSpan()), Id = new string(i.Id.AsSpan()) }).ToList();
        }
    }

    public Task<string> GetLatestDeltaLinkAsync(string driveId, CancellationToken ct) =>
        Interlocked.Decrement(ref FailBaselines) >= 0 ? throw new RemoteException(RemoteError.Unavailable, "503") : inner.GetLatestDeltaLinkAsync(driveId, ct);

    public Task<DeltaPage> GetDeltaAsync(string link, CancellationToken ct) => inner.GetDeltaAsync(link, ct);

    public Task<DriveItemInfo?> GetItemAsync(string driveId, string path, CancellationToken ct) =>
        path == Denied ? throw new RemoteException(RemoteError.AccessDenied, "403") : inner.GetItemAsync(driveId, path, ct);

    public Task<DriveItemInfo?> GetItemByIdAsync(string driveId, string itemId, CancellationToken ct) => inner.GetItemByIdAsync(driveId, itemId, ct);
    public Task<Stream> OpenReadAsync(string driveId, string itemId, string? contentTag, long offset, long length, CancellationToken ct) => inner.OpenReadAsync(driveId, itemId, contentTag, offset, length, ct);
    public Task<DriveItemInfo> UploadAsync(string driveId, UploadTarget target, string sourceFile, CancellationToken ct) => inner.UploadAsync(driveId, target, sourceFile, ct);
    public Task<UploadSession> CreateUploadSessionAsync(string driveId, UploadTarget target, CancellationToken ct) => inner.CreateUploadSessionAsync(driveId, target, ct);
    public Task<DriveItemInfo?> UploadFragmentAsync(UploadSession session, string sourceFile, long offset, long length, long totalSize, CancellationToken ct) => inner.UploadFragmentAsync(session, sourceFile, offset, length, totalSize, ct);
    public Task CancelUploadSessionAsync(UploadSession session, CancellationToken ct) => inner.CancelUploadSessionAsync(session, ct);
    public Task<DriveItemInfo> CreateFolderAsync(string driveId, string parentPath, string name, CancellationToken ct) => inner.CreateFolderAsync(driveId, parentPath, name, ct);
    public Task<DriveItemInfo> MoveAsync(string driveId, string itemId, string newParentId, string newName, CancellationToken ct) => inner.MoveAsync(driveId, itemId, newParentId, newName, ct);
    public Task DeleteAsync(string driveId, string itemId, CancellationToken ct) => inner.DeleteAsync(driveId, itemId, ct);
}

/// <summary>
/// The change feed's holder index (item ID to the listing holding it) never keeps a listing that left the cache: one read again,
/// one evicted, also on a drive whose feed brings nothing more.
/// </summary>
public sealed class HolderIndexTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "vfs365-tests", Guid.NewGuid().ToString("N"));
    DateTimeOffset now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Listings_read_again_leave_memory()
    {
        var fake = new FakeDriveApi();
        fake.Add("A", folder: true);
        fake.Add("A/B", folder: true);
        fake.Add("A/B/C", folder: true);
        for (var i = 0; i < 500; i++)
        {
            fake.Add($"A/B/C/file-{i:D3}.txt");
        }
        fake.Add("other", folder: true);
        // No access to A: the feed can't see A renamed, so C is only trusted for ListingTtl and read again on use after that
        var engine = ReadPathTesting.Create(directory, new FreshItemsApi(fake) { Denied = "A" }, () => now);
        await engine.PollAsync("b!me");
        now += TimeSpan.FromSeconds(1);
        await engine.ListAsync((await engine.GetEntryAsync(@"\A\B\C", default))!, default);
        await ReadPathTesting.Eventually(() => fake.LookedUp.Contains("A/B"));
        await Task.Delay(100);

        const int rounds = 10;
        var lists = fake.ListCalls;
        var replaced = new List<WeakReference>();
        for (var r = 0; r < rounds; r++)
        {
            fake.Add($"other/colleague-{r}.txt"); // a change in a folder that isn't cached: Apply brings the holder index up to date
            now += TimeSpan.FromSeconds(70);
            await engine.PollAsync("b!me");
            var old = ReadPathTesting.Watch(engine, "A/B/C");
            await engine.GetEntryAsync(@"\A\B\C\file-001.txt", default);
            await ReadPathTesting.Eventually(() => ReadPathTesting.ListingAt(engine, "A/B/C") is { } current && current != old.Target);
            replaced.Add(old);
        }

        Assert.Equal(rounds, fake.ListCalls - lists);
        await ReadPathTesting.Eventually(() => ReadPathTesting.Reachable(replaced) == 0);
    }

    [Fact]
    public async Task Evicted_listings_leave_memory_without_another_read_of_the_feed()
    {
        var api = new FakeDriveApi();
        const int folders = 20, files = 500;
        for (var f = 0; f < folders; f++)
        {
            api.Add($"f{f}", folder: true);
            for (var i = 0; i < files; i++)
            {
                api.Add($"f{f}/file-{i:D3}-with-a-longish-name.txt");
            }
        }
        api.Add("other", folder: true);
        var engine = ReadPathTesting.Create(directory, api, () => now, maxCachedItems: 2_000);
        await engine.PollAsync("b!me");
        var watched = new List<WeakReference>();
        for (var f = 0; f < folders; f++)
        {
            await engine.ListAsync((await engine.GetEntryAsync($@"\f{f}", default))!, default);
            watched.Add(ReadPathTesting.Watch(engine, $"f{f}"));
        }
        api.Add("other/x.txt"); // a change in a folder that isn't cached: the holder index takes in every listing
        now += TimeSpan.FromSeconds(5);
        await engine.PollAsync("b!me");
        Assert.True(ReadPathTesting.Holders(engine) >= folders * files);
        now += TimeSpan.FromMinutes(5); // past ListingKeep
        await engine.PollActiveAsync(); // evicts most of them; the feed brings no change after

        var kept = Enumerable.Range(0, folders).Count(f => ReadPathTesting.ListingAt(engine, $"f{f}") is not null);
        Assert.InRange(kept, 1, folders / 4);
        await ReadPathTesting.Eventually(() => ReadPathTesting.Reachable(watched) == kept);
        Assert.InRange(ReadPathTesting.Holders(engine), 0, (kept + 1) * files); // the item IDs of evicted listings went too
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
