using Vfs365.Agent;

namespace Vfs365.Tests;

/// <summary>One log file per sign-in, a size cap per file, and a week of history.</summary>
public sealed class AgentLogTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "vfs365-tests", Guid.NewGuid().ToString("N"));
    DateTime now = new(2026, 10, 7, 9, 0, 0);

    string Current => Path.Combine(folder, "agent.log");

    string[] Archived => Directory.Exists(Path.Combine(folder, "logs"))
        ? [.. Directory.GetFiles(Path.Combine(folder, "logs")).Select(Path.GetFileName).Order()!]
        : [];

    void Session(bool newSession, params string[] lines)
    {
        using var log = AgentLog.Open(folder, newSession, 1 << 20, () => now);
        foreach (var line in lines)
        {
            log.WriteLine(line);
        }
    }

    [Fact]
    public void A_sign_in_starts_a_new_file_and_a_restart_appends()
    {
        Session(newSession: true, "first sign-in");
        File.SetLastWriteTime(Current, now);
        Session(newSession: true, "second sign-in");
        Session(newSession: false, "after a restart");

        Assert.Equal(["second sign-in", "after a restart"], File.ReadAllLines(Current));
        Assert.Equal(["agent-20261007-090000.log"], Archived);
        Assert.Equal(["first sign-in"], File.ReadAllLines(Path.Combine(folder, "logs", Archived[0])));
    }

    [Fact]
    public void A_full_file_continues_in_a_new_one()
    {
        using (var log = AgentLog.Open(folder, true, 1000, () => now))
        {
            for (var i = 0; i < 30; i++)
            {
                log.WriteLine($"line {i} {new string('x', 80)}");
            }
        }

        Assert.True(Archived.Length >= 2);
        Assert.All(Directory.GetFiles(Path.Combine(folder, "logs")), file => Assert.InRange(new FileInfo(file).Length, 1, 1300));
        Assert.Contains("Log continues from logs\\", File.ReadAllText(Current));
        Assert.Contains("line 29", File.ReadAllText(Current));
    }

    [Fact]
    public void Logs_older_than_a_week_go()
    {
        var logs = Directory.CreateDirectory(Path.Combine(folder, "logs")).FullName;
        foreach (var days in new[] { 1, 6, 8, 30 })
        {
            var file = Path.Combine(logs, $"agent-{days}.log");
            File.WriteAllText(file, "old");
            File.SetLastWriteTime(file, now.AddDays(-days));
        }
        File.WriteAllText(Path.Combine(folder, "agent.log.old"), "from an earlier version");

        Session(newSession: true, "today");

        Assert.Equal(["agent-1.log", "agent-6.log"], Archived);
        Assert.False(File.Exists(Path.Combine(folder, "agent.log.old")));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
