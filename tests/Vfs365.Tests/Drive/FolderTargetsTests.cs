using Vfs365.Core.Discovery;
using Vfs365.Core.Drive;

namespace Vfs365.Tests.Drive;

/// <summary>Folders named by policy templates: placeholders, wildcards, folders made inside a library, and what can't be resolved.</summary>
public sealed class FolderTargetsTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "vfs365-tests", Guid.NewGuid().ToString("N"));
    readonly FakeDriveApi api = new();
    readonly DriveEngine engine;

    public FolderTargetsTests()
    {
        static LibraryEntry Library(string site, string title) => new($"k-{site}-{title}", "s", "w", "l", $"https://x.sharepoint.com/sites/{site}", site, title, false, 1);
        engine = new DriveEngine(
            DriveNamespace.Build(new MyDrive("b!me", "https://x-my.sharepoint.com/personal/u"), [Library("Finance", "Documents"), Library("HR", "Policies")],
                (_, _) => Task.FromResult("b!library")),
            api, new ContentCache(Path.Combine(directory, "cache"), api), new DriveEngineOptions { StagingDirectory = Path.Combine(directory, "staging") });
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Placeholders_take_the_given_values_then_the_environment()
    {
        var values = new Dictionary<string, string?> { ["UPNNAME"] = "jan" };
        Assert.Equal(@"OneDrive\jan\Desktop", FolderTargets.Expand(@"OneDrive\%upnname%\Desktop", values));
        Assert.Equal($@"Home\{Environment.UserName}", FolderTargets.Expand(@"Home\%USERNAME%", values));
        Assert.Null(FolderTargets.Expand(@"Home\%NOT_SET_ANYWHERE_365%", values));
        Assert.Null(FolderTargets.Expand(@"Home\%UPN%", new Dictionary<string, string?> { ["UPN"] = null })); // not signed in yet
        Assert.Equal(@"Plain\Path", FolderTargets.Expand(@"Plain\Path", values));
    }

    [Fact]
    public async Task An_existing_folder_resolves_and_missing_ones_inside_a_library_are_made()
    {
        api.Add("Desktop", folder: true);

        Assert.Equal((@"\OneDrive\Desktop", null), await FolderTargets.ResolveAsync(engine, "OneDrive/Desktop", create: false, default));

        var (path, problem) = await FolderTargets.ResolveAsync(engine, @"OneDrive\Users\jan\Documents", create: true, default);
        Assert.Null(problem);
        Assert.Equal(@"\OneDrive\Users\jan\Documents", path);
        Assert.True(api.ItemAt("Users/jan/Documents")!.IsFolder);

        Assert.Contains("doesn't exist", (await FolderTargets.ResolveAsync(engine, @"OneDrive\Other", create: false, default)).Problem);
    }

    [Fact]
    public async Task Sites_and_libraries_are_never_made()
    {
        var (path, problem) = await FolderTargets.ResolveAsync(engine, @"Sites\Sales\Documents", create: true, default);
        Assert.Null(path);
        Assert.Contains("only inside a library", problem);
    }

    [Fact]
    public async Task A_wildcard_picks_the_first_matching_folder_by_name()
    {
        Assert.Equal((@"\Sites\Finance\Documents", null), await FolderTargets.ResolveAsync(engine, @"Sites\*\Doc*", create: false, default));
        Assert.Equal((@"\Sites\HR\Policies", null), await FolderTargets.ResolveAsync(engine, @"Sites\H?\*", create: false, default));

        var (path, problem) = await FolderTargets.ResolveAsync(engine, @"Sites\Sales*\Documents", create: true, default);
        Assert.Null(path);
        Assert.Contains("matches 'Sales*'", problem);
    }

    [Fact]
    public async Task A_file_in_the_way_and_dot_folders_are_refused()
    {
        api.Add("Desktop", content: "not a folder");

        Assert.Contains("is a file", (await FolderTargets.ResolveAsync(engine, @"OneDrive\Desktop", create: true, default)).Problem);
        Assert.Contains("may not contain", (await FolderTargets.ResolveAsync(engine, @"OneDrive\..\Sites", create: true, default)).Problem);
    }
}
