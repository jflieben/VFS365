using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;
using Vfs365.Agent;

namespace Vfs365.Tests;

public sealed class MachineCleanupTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "vfs365-cleanup", Guid.NewGuid().ToString("N"));

    MachineCleanup.UserProfile Profile(string name, bool unsaved)
    {
        var profile = new MachineCleanup.UserProfile(Path.Combine(root, name), WindowsIdentity.GetCurrent().User!);
        var data = profile.DataFolder;
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
        return profile;
    }

    [Fact]
    public void Removes_everything_when_nothing_is_unsaved()
    {
        var profile = Profile("ann", unsaved: false);
        File.SetAttributes(Path.Combine(profile.DataFolder, "agent.log"), FileAttributes.ReadOnly);

        MachineCleanup.RemoveUserData([profile], dryRun: false, _ => { });

        Assert.False(Directory.Exists(profile.DataFolder));
        Assert.True(Directory.Exists(Path.Combine(profile.Folder, "AppData", "Local")));
    }

    [Fact]
    public void Keeps_unsaved_changes_and_reports_them()
    {
        var profile = Profile("bob", unsaved: true);
        var said = new List<string>();

        MachineCleanup.RemoveUserData([profile], dryRun: false, said.Add);

        Assert.Equal(["staging"], Directory.GetFileSystemEntries(profile.DataFolder).Select(Path.GetFileName));
        Assert.True(File.Exists(Path.Combine(profile.DataFolder, "staging", "a.staging")));
        Assert.Contains(said, line => line.Contains("Keeping 1 unsaved", StringComparison.Ordinal));
    }

    [Fact]
    public void Dry_run_changes_nothing()
    {
        var profile = Profile("cid", unsaved: false);
        var said = new List<string>();

        MachineCleanup.RemoveUserData([profile, profile with { Folder = Path.Combine(root, "missing") }], dryRun: true, said.Add);

        Assert.Equal(4, said.Count);
        Assert.True(File.Exists(Path.Combine(profile.DataFolder, "discovery.json")));
    }

    [Fact]
    public void Skips_a_folder_owned_by_someone_else()
    {
        var profile = Profile("dan", unsaved: false) with { User = new SecurityIdentifier("S-1-5-21-1-2-3-1001") };
        var owner = (SecurityIdentifier)new DirectoryInfo(profile.DataFolder).GetAccessControl().GetOwner(typeof(SecurityIdentifier))!;
        if (owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) || owner.IsWellKnown(WellKnownSidType.LocalSystemSid))
        {
            return; // elevated test run: those owners are accepted
        }
        var said = new List<string>();

        MachineCleanup.RemoveUserData([profile], dryRun: false, said.Add);

        Assert.True(File.Exists(Path.Combine(profile.DataFolder, "discovery.json")));
        Assert.Contains(said, line => line.Contains("owned by", StringComparison.Ordinal));
    }

    [Fact]
    public void A_deep_folder_chain_is_cleaned_down_to_the_depth_limit_without_recursion()
    {
        var profile = Profile("eve", unsaved: false);
        // 200 levels: far below what recursion survived on SYSTEM's stack, far above the 64 folders cleaned
        var chain = Path.Combine(profile.DataFolder, "cache");
        for (var i = 0; i < 200; i++)
        {
            chain = Path.Combine(chain, "d");
            Directory.CreateDirectory(chain);
            if (i is 10 or 150)
            {
                File.WriteAllText(Path.Combine(chain, $"file{i}"), "x");
            }
        }
        var said = new List<string>();

        MachineCleanup.RemoveUserData([profile], dryRun: false, said.Add);

        var kept = Directory.EnumerateFiles(profile.DataFolder, "*", SearchOption.AllDirectories).Select(Path.GetFileName).ToList();
        Assert.Equal(["file150"], kept);
        Assert.Single(said, line => line.Contains("more than 64 folders deep", StringComparison.Ordinal));
        Assert.DoesNotContain(said, line => line.StartsWith("Could not", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(profile.DataFolder, "agent.log")));
    }

    [Fact]
    public void Reads_the_user_and_program_version_of_an_agent_process()
    {
        var describe = typeof(MachineCleanup).GetMethod("Describe", BindingFlags.NonPublic | BindingFlags.Static)!;
        using var self = Process.GetCurrentProcess();

        var found = ((SecurityIdentifier User, Version Version)?)describe.Invoke(null, [self]);

        var file = FileVersionInfo.GetVersionInfo(Environment.ProcessPath!);
        Assert.Equal(WindowsIdentity.GetCurrent().User, found!.Value.User);
        Assert.Equal(new Version(file.FileMajorPart, file.FileMinorPart, file.FileBuildPart), found.Value.Version);
    }

    [Fact]
    public void Logon_cleanup_uses_built_in_tools_only()
    {
        var command = MachineCleanup.LogonCleanupCommand();

        Assert.StartsWith(@"%SystemRoot%\System32\cmd.exe ", command, StringComparison.Ordinal);
        Assert.DoesNotContain("vfs365.exe", command, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"Desktop\NameSpace\{3CB016A6-FE2C-45AC-ADBD-05A92CDE26CC}"" /f", command, StringComparison.Ordinal);
        Assert.Equal("2026,1012,931,5", MachineCleanup.ActiveSetupVersion(new DateTime(2026, 10, 12, 9, 31, 5, DateTimeKind.Utc)));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(root, true);
        }
    }
}
