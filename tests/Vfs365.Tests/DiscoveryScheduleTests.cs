using System.Text.Json;
using Vfs365.Agent;
using Vfs365.Core.Discovery;
using Vfs365.Core.Drive;

namespace Vfs365.Tests;

/// <summary>When the mounted agent discovers: a failed search is a failed run, and a first run that didn't finish is never recent.</summary>
public sealed class DiscoveryScheduleTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 24, 8, 0, 0, TimeSpan.Zero);
    static readonly MyDrive OneDrive = new("b!me", "https://contoso-my.sharepoint.com/personal/ann_contoso_com/Documents");
    const string Root = "https://contoso.sharepoint.com";

    /// <summary>Search answers 429 (rollout day); nothing else is asked.</summary>
    sealed class ThrottledSearch : ISharePointDiscoveryApi
    {
        public Task<MyDrive> GetMyDriveAsync(CancellationToken ct) => Task.FromResult(OneDrive);

        public Task<SearchPage> SearchDocumentLibrariesAsync(string tenantRoot, long afterDocId, int startRow, int rowLimit, CancellationToken ct) =>
            throw new HttpRequestException($"429 TooManyRequests from {tenantRoot}/_api/search/query: throttled");

        public Task<SiteMetadata> GetSiteAsync(string webUrl, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<PinnedLibrary>?> GetWebLibrariesAsync(string webUrl, CancellationToken ct) => throw new NotSupportedException();
        public Task<ListMetadata?> GetListAsync(string baseUrl, string listId, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> GetLibraryDriveIdAsync(LibraryEntry library, CancellationToken ct) => throw new NotSupportedException();
        public Task<PinnedLocation?> ResolveLocationAsync(string url, CancellationToken ct) => throw new NotSupportedException();
    }

    static LibraryEntry Library(string name) =>
        new($"site|web|{name}", "site", "web", name, $"{Root}/sites/{name}", name, "Documents", false, 5, CheckedAt: Now.AddDays(-1));

    static Task<DiscoveryResult> DiscoverAsync(DiscoveryState previous) =>
        new DiscoveryService(new ThrottledSearch(), new DiscoveryOptions { Clock = () => Now }).RunAsync(previous, default, OneDrive);

    /// <summary>As discovery.json holds it between starts.</summary>
    static DiscoveryState SavedAndRead(DiscoveryState state) => JsonSerializer.Deserialize<DiscoveryState>(JsonSerializer.Serialize(state))!;

    [Fact]
    public async Task A_failed_search_keeps_the_libraries_and_the_last_runs_time()
    {
        var previous = new DiscoveryState(Now.AddDays(-2), Root, [Library("Sales"), Library("Finance"), Library("HR")], new Dictionary<string, string>(),
            LibraryRules.Version, OneDrive, Now.AddDays(-2), "contoso");

        var result = await DiscoverAsync(previous);
        var kept = DiscoverySchedule.Keep(result, previous);

        Assert.NotNull(result.SearchError);
        Assert.Equal(["Finance", "HR", "Sales"], kept.Libraries.Select(l => l.ListId).Order());
        Assert.Equal(previous.RunAt, kept.RunAt); // not "read just now"
        Assert.Null(RefreshRule.Blocked(Now, DiscoverySchedule.LibrariesReadAt(kept, DriveScope.SharePoint), null, running: false));
        Assert.InRange(DiscoverySchedule.AfterFailure(1, hasLibraries: true, new Random(1)), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(60));
    }

    [Fact]
    public async Task A_first_run_whose_search_failed_tries_again_in_minutes_and_is_not_recent()
    {
        var placeholder = DiscoverySchedule.Placeholder(OneDrive, "contoso", DriveScope.SharePoint, Now);

        var result = await DiscoverAsync(placeholder);
        var kept = SavedAndRead(DiscoverySchedule.Keep(result, placeholder));

        Assert.NotNull(result.SearchError);
        Assert.Empty(kept.Libraries);
        Assert.False(DiscoverySchedule.Recent(kept, DriveScope.SharePoint, Now.AddMinutes(10))); // a restart discovers at once
        for (var failures = 1; failures <= 8; failures++)
        {
            var wait = DiscoverySchedule.AfterFailure(failures, hasLibraries: false, new Random(failures));
            Assert.InRange(wait, TimeSpan.FromMinutes(Math.Min(15, 2 << (failures - 1))), TimeSpan.FromMinutes(60));
        }
        Assert.InRange(DiscoverySchedule.AfterFailure(1, hasLibraries: false, new Random(3)), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void A_start_after_an_interrupted_first_run_discovers_at_once_and_may_refresh()
    {
        // First start: the OneDrive is read and the placeholder saved; the PC restarts before discovery ends
        var placeholder = SavedAndRead(DiscoverySchedule.Placeholder(OneDrive, "contoso", DriveScope.SharePoint, Now));
        var secondStart = Now.AddMinutes(10);

        Assert.False(DiscoverySchedule.Recent(placeholder, DriveScope.SharePoint, secondStart));
        Assert.False(DiscoverySchedule.Recent(placeholder, DriveScope.All, secondStart));
        Assert.Equal(TimeSpan.Zero, DiscoverySchedule.StartWait(recent: false, Random.Shared));
        Assert.Null(RefreshRule.Blocked(secondStart, DiscoverySchedule.LibrariesReadAt(placeholder, DriveScope.SharePoint), null, running: false));

        // A placeholder of an earlier version has the first start's time: without libraries it isn't recent either
        var earlier = placeholder with { RunAt = Now };
        Assert.False(DiscoverySchedule.Recent(earlier, DriveScope.SharePoint, secondStart));
    }

    [Fact]
    public void A_state_with_libraries_or_a_onedrive_only_drive_is_recent_for_a_week()
    {
        var state = new DiscoveryState(Now.AddDays(-1), Root, [Library("Sales")], new Dictionary<string, string>(), LibraryRules.Version, OneDrive);
        Assert.True(DiscoverySchedule.Recent(state, DriveScope.SharePoint, Now));
        Assert.False(DiscoverySchedule.Recent(state with { RunAt = Now.AddDays(-8) }, DriveScope.SharePoint, Now));
        Assert.InRange(DiscoverySchedule.StartWait(recent: true, Random.Shared), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(90));

        // OneDrive scope: the first start's read of the OneDrive is all discovery does
        var oneDriveOnly = DiscoverySchedule.Placeholder(OneDrive, "contoso", DriveScope.OneDrive, Now);
        Assert.True(DiscoverySchedule.Recent(oneDriveOnly, DriveScope.OneDrive, Now.AddMinutes(10)));
    }

    [Fact]
    public void A_state_that_could_not_be_saved_is_saved_at_the_next_try()
    {
        var saves = new List<DiscoveryState>();
        var failNext = true;
        var keeper = new DiscoveryStateKeeper(DiscoverySchedule.Placeholder(OneDrive, "contoso", DriveScope.SharePoint, Now), state =>
        {
            if (failNext)
            {
                failNext = false;
                throw new IOException("The process cannot access the file because it is being used by another process.");
            }
            saves.Add(state);
        });
        keeper.Touch();

        Assert.Throws<IOException>(keeper.SaveIfChanged);
        keeper.SaveIfChanged();
        keeper.SaveIfChanged();

        Assert.Single(saves);
        Assert.Equal(OneDrive, saves[0].OneDrive);
    }

    [Fact]
    public void The_shared_list_is_kept_when_discovery_replaces_the_state_and_saved_when_it_changes()
    {
        var saves = new List<DiscoveryState>();
        var keeper = new DiscoveryStateKeeper(DiscoverySchedule.Placeholder(OneDrive, "contoso", DriveScope.All, Now), saves.Add);
        var shared = new SharedState(Now, [new SharedItem("b!bob", "01A", "Plan.docx", false, "Docs/Plan.docx")]);

        keeper.SetShared(shared);
        keeper.SaveIfChanged();
        keeper.Replace(DiscoverySchedule.Placeholder(OneDrive, "contoso", DriveScope.All, Now) with { RunAt = Now });

        Assert.Same(shared, saves.Single().Shared);
        Assert.Same(shared, keeper.Current.Shared);
        Assert.Equal(Now, keeper.Current.RunAt);

        // Through discovery.json and back
        var file = Path.Combine(Path.GetTempPath(), $"vfs365-discovery-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(keeper.Current));
            var loaded = DiscoveryStore.Load(file)!.Shared!;
            Assert.Equal(Now, loaded.RunAt);
            Assert.Equal(shared.Items, loaded.Items);
            Assert.DoesNotContain("\"Key\"", File.ReadAllText(file));
        }
        finally
        {
            File.Delete(file);
        }
    }
}
