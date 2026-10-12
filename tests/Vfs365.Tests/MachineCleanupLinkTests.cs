using System.Diagnostics;
using System.Security.Principal;
using Vfs365.Agent;

namespace Vfs365.Tests;

/// <summary>Uninstall cleanup runs as SYSTEM in folders users control: a junction must never lead it elsewhere.</summary>
public sealed class MachineCleanupLinkTests : IDisposable
{
    static readonly string[] DataPath = ["AppData", "Local", "VFS365"];

    readonly string root = Path.Combine(Path.GetTempPath(), "vfs365-links", Guid.NewGuid().ToString("N"));
    readonly string victim;

    public MachineCleanupLinkTests()
    {
        victim = Path.Combine(root, "victim");
        Directory.CreateDirectory(Path.Combine(victim, "sub"));
        File.WriteAllText(Path.Combine(victim, "important.txt"), "keep");
        File.WriteAllText(Path.Combine(victim, "sub", "nested.txt"), "keep");
    }

    MachineCleanup.UserProfile Profile() => new(Path.Combine(root, "profile"), WindowsIdentity.GetCurrent().User!);

    /// <summary>A directory junction (no admin rights needed); false when this machine can't make one.</summary>
    static bool Junction(string link, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c mklink /J \"{link}\" \"{target}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        mklink.WaitForExit();
        return mklink.ExitCode == 0 && new DirectoryInfo(link).LinkTarget is not null;
    }

    void AssertVictimIntact()
    {
        Assert.True(File.Exists(Path.Combine(victim, "important.txt")));
        Assert.True(File.Exists(Path.Combine(victim, "sub", "nested.txt")));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void A_linked_path_component_is_skipped(int depth)
    {
        var profile = Profile();
        // Below the link the victim looks like a VFS365 folder
        var lookalike = Path.Combine([victim, .. DataPath.Skip(depth)]);
        Directory.CreateDirectory(lookalike);
        File.WriteAllText(Path.Combine(lookalike, "agent.log"), "keep");
        if (!Junction(Path.Combine([profile.Folder, .. DataPath.Take(depth)]), victim))
        {
            return;
        }
        var said = new List<string>();

        MachineCleanup.RemoveUserData([profile], dryRun: false, said.Add);

        AssertVictimIntact();
        Assert.True(File.Exists(Path.Combine(lookalike, "agent.log")));
        Assert.Contains(said, line => line.Contains("reparse point", StringComparison.Ordinal));
    }

    [Fact]
    public void A_link_inside_the_data_folder_is_left_and_its_target_kept()
    {
        var profile = Profile();
        Directory.CreateDirectory(Path.Combine(profile.DataFolder, "cache", "drive"));
        File.WriteAllText(Path.Combine(profile.DataFolder, "agent.log"), "log");
        File.WriteAllText(Path.Combine(profile.DataFolder, "cache", "drive", "item"), "cached");
        if (!Junction(Path.Combine(profile.DataFolder, "cache", "drive", "deep"), victim) || !Junction(Path.Combine(profile.DataFolder, "logs"), victim))
        {
            return;
        }
        var said = new List<string>();

        MachineCleanup.RemoveUserData([profile], dryRun: false, said.Add);

        AssertVictimIntact();
        Assert.False(File.Exists(Path.Combine(profile.DataFolder, "agent.log")));
        Assert.False(File.Exists(Path.Combine(profile.DataFolder, "cache", "drive", "item")));
        Assert.Contains(said, line => line.EndsWith(@"\logs: it is a link (reparse point)", StringComparison.Ordinal));
        Assert.Contains(said, line => line.EndsWith(@"\deep: it is a link (reparse point)", StringComparison.Ordinal));
    }

    [Fact]
    public void A_linked_staging_folder_is_neither_counted_nor_emptied()
    {
        var profile = Profile();
        Directory.CreateDirectory(profile.DataFolder);
        File.WriteAllText(Path.Combine(victim, "x.staging.json"), "{}");
        if (!Junction(Path.Combine(profile.DataFolder, "staging"), victim))
        {
            return;
        }
        var said = new List<string>();

        MachineCleanup.RemoveUserData([profile], dryRun: false, said.Add);

        AssertVictimIntact();
        Assert.True(File.Exists(Path.Combine(victim, "x.staging.json")));
        Assert.DoesNotContain(said, line => line.StartsWith("Keeping", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        if (!Directory.Exists(root))
        {
            return;
        }
        // Junctions first, so deleting the tree never walks into the victim
        var links = Directory.EnumerateDirectories(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 })
            .Where(d => new DirectoryInfo(d).LinkTarget is not null).OrderByDescending(d => d.Length).ToList();
        foreach (var link in links)
        {
            Directory.Delete(link);
        }
        Directory.Delete(root, true);
    }
}
