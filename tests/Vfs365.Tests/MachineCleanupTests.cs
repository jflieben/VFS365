using Vfs365.Agent;

namespace Vfs365.Tests;

public sealed class MachineCleanupTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "vfs365-cleanup", Guid.NewGuid().ToString("N"));

    string Profile(string name, bool unsaved)
    {
        var data = Path.Combine(root, name, "VFS365");
        Directory.CreateDirectory(Path.Combine(data, "cache"));
        Directory.CreateDirectory(Path.Combine(data, "staging"));
        File.WriteAllText(Path.Combine(data, "cache", "0123"), "cached");
        File.WriteAllText(Path.Combine(data, "discovery.json"), "{}");
        File.WriteAllText(Path.Combine(data, "agent.log"), "log");
        File.WriteAllText(Path.Combine(data, "staging", "temp.staging"), "temp");
        if (unsaved)
        {
            File.WriteAllText(Path.Combine(data, "staging", "a.staging"), "unsaved");
            File.WriteAllText(Path.Combine(data, "staging", "a.staging.json"), "{}");
        }
        return data;
    }

    [Fact]
    public void Removes_everything_when_nothing_is_unsaved()
    {
        var data = Profile("ann", unsaved: false);

        MachineCleanup.RemoveUserData([data], dryRun: false, _ => { });

        Assert.False(Directory.Exists(data));
    }

    [Fact]
    public void Keeps_unsaved_changes_and_reports_them()
    {
        var data = Profile("bob", unsaved: true);
        var said = new List<string>();

        MachineCleanup.RemoveUserData([data], dryRun: false, said.Add);

        Assert.Equal(["staging"], Directory.GetFileSystemEntries(data).Select(Path.GetFileName));
        Assert.True(File.Exists(Path.Combine(data, "staging", "a.staging")));
        Assert.Contains(said, line => line.Contains("Keeping 1 unsaved", StringComparison.Ordinal));
    }

    [Fact]
    public void Dry_run_changes_nothing()
    {
        var data = Profile("cid", unsaved: false);
        var said = new List<string>();

        MachineCleanup.RemoveUserData([data, Path.Combine(root, "missing")], dryRun: true, said.Add);

        Assert.Equal(4, said.Count);
        Assert.True(File.Exists(Path.Combine(data, "discovery.json")));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
    }
}
