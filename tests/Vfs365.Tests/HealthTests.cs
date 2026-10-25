using System.Text.Json;
using Vfs365.Agent;

namespace Vfs365.Tests;

/// <summary>The agent's status file and `vfs365 health`.</summary>
public sealed class HealthTests : IDisposable
{
    static readonly DateTimeOffset T0 = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);
    readonly string folder = Path.Combine(Path.GetTempPath(), $"vfs365-health-{Guid.NewGuid():N}");
    DateTimeOffset now = T0;

    string StatusPath => Path.Combine(folder, StatusFile.FileName);

    StatusFile NewStatus() => new(StatusPath, AgentStatus.ForThisProcess(T0), () => now, ticking: false);

    static AgentStatus Mounted(DateTimeOffset updated) =>
        AgentStatus.ForThisProcess(T0) with { State = AgentStatus.Mounted, UpdatedAt = updated, Unc = @"\\VFS365\ann", DriveLetter = "M:" };

    public void Dispose()
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void The_status_is_written_at_once_and_when_it_changes()
    {
        using var status = NewStatus();
        Assert.Equal(AgentStatus.Starting, StatusFile.Read(StatusPath)!.State);

        now += TimeSpan.FromSeconds(10);
        status.Update(s => s with { State = AgentStatus.Mounted, DriveLetter = "M:" });
        var written = StatusFile.Read(StatusPath)!;
        Assert.True(written.IsMounted);
        Assert.Equal("M:", written.DriveLetter);
        Assert.Equal(now, written.UpdatedAt);
        Assert.False(File.Exists(StatusPath + ".tmp"));
    }

    [Fact]
    public void An_unchanged_status_is_written_again_only_at_the_heartbeat()
    {
        using var status = NewStatus();
        status.Live = s => s with { WaitingUploads = 0 };
        now += TimeSpan.FromMinutes(1);
        status.Tick();
        Assert.Equal(T0, StatusFile.Read(StatusPath)!.UpdatedAt);

        now = T0 + StatusFile.Heartbeat;
        status.Tick();
        Assert.Equal(now, StatusFile.Read(StatusPath)!.UpdatedAt);
    }

    [Fact]
    public void Error_lines_become_the_last_error_redacted()
    {
        using var status = NewStatus();
        var logged = new List<string>();
        var log = status.Tap(logged.Add);
        log("discovery: OneDrive checked in 0.4 s");
        log("error: reading \\OneDrive\\a.docx: https://contoso-my.sharepoint.com/_layouts/15/download.aspx?UniqueId=1&tempauth=StatusSecret0123");
        Assert.Null(StatusFile.Read(StatusPath)!.LastError); // written at the next tick
        status.Tick();

        Assert.Equal(2, logged.Count);
        var written = StatusFile.Read(StatusPath)!;
        Assert.StartsWith("reading \\OneDrive\\a.docx", written.LastError);
        Assert.DoesNotContain("StatusSecret0123", File.ReadAllText(StatusPath));
        Assert.Equal(T0, written.LastErrorAt);
    }

    [Fact]
    public void Waiting_files_keep_their_cause_until_they_upload()
    {
        using var status = NewStatus();
        status.FileWaiting(@"\Sites\Sales\Offer.docx", "423 Locked from https://x: checked out");
        now += TimeSpan.FromSeconds(5);
        status.FileWaiting(@"\Sites\Sales\Offer.docx", "No such host is known. (contoso.sharepoint.com:443)");
        status.FileWaiting(@"\OneDrive\b.txt", "timed out");
        status.Live = s => s with { WaitingUploads = 2 };
        status.Tick();

        var waiting = StatusFile.Read(StatusPath)!.WaitingFiles;
        Assert.Equal(2, waiting.Count);
        Assert.Equal((@"\Sites\Sales\Offer.docx", "No such host is known. (contoso.sharepoint.com:443)", T0), (waiting[0].Path, waiting[0].Cause, waiting[0].Since));

        status.FileWaiting(@"\OneDrive\b.txt", null);
        status.Tick();
        Assert.Single(StatusFile.Read(StatusPath)!.WaitingFiles);

        status.Live = s => s with { WaitingUploads = 0 }; // gone without an Uploaded notice (renamed, deleted)
        status.Tick();
        Assert.Empty(StatusFile.Read(StatusPath)!.WaitingFiles);
    }

    [Fact]
    public void A_closed_status_says_stopped_and_stays()
    {
        var status = NewStatus();
        status.Update(s => s with { State = AgentStatus.Mounted, DriveLetter = "M:", WaitingUploads = 3 });
        status.Close();
        status.Update(s => s with { State = AgentStatus.Mounted });
        var written = StatusFile.Read(StatusPath)!;
        Assert.Equal(AgentStatus.Stopped, written.State);
        Assert.Null(written.DriveLetter);
        Assert.Equal(0, written.WaitingUploads);
    }

    [Fact]
    public void A_damaged_status_file_reads_as_none()
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(StatusPath, "{ not json");
        Assert.Null(StatusFile.Read(StatusPath));
        Assert.Null(StatusFile.Read(Path.Combine(folder, "missing.json")));
    }

    [Fact]
    public void A_fresh_mounted_agent_is_healthy() => Assert.Empty(Health.UserProblems(Mounted(T0), agentRunning: true, T0 + TimeSpan.FromMinutes(1)));

    [Fact]
    public void Problems_of_one_agent_are_named()
    {
        Assert.Contains("no agent runs", Assert.Single(Health.UserProblems(Mounted(T0), agentRunning: false, T0)));
        Assert.Contains("was stopped", Health.UserProblems(Mounted(T0) with { State = AgentStatus.Stopped }, agentRunning: false, T0)[0]);
        Assert.Contains("may hang", Assert.Single(Health.UserProblems(Mounted(T0), agentRunning: true, T0 + StatusFile.Stale + TimeSpan.FromMinutes(1))));
        Assert.Equal("the drive is waiting: the first start needs Microsoft 365: offline",
            Assert.Single(Health.UserProblems(Mounted(T0) with { State = AgentStatus.Waiting, Problem = "the first start needs Microsoft 365: offline" }, true, T0)));
        Assert.Contains("sign-in needed (AADSTS50076)", Assert.Single(Health.UserProblems(Mounted(T0) with { SignInProblem = "AADSTS50076" }, true, T0)));
        Assert.Equal(2, Health.UserProblems(null, agentRunning: false, T0).Count);
    }

    [Fact]
    public void An_agent_that_just_started_gets_time_to_mount()
    {
        var starting = Mounted(T0) with { State = AgentStatus.Starting, StartedAt = T0 };
        Assert.Empty(Health.UserProblems(starting, true, T0 + TimeSpan.FromMinutes(1)));
        Assert.Equal("the drive is starting", Assert.Single(Health.UserProblems(starting with { UpdatedAt = T0 + Health.StartingGrace }, true, T0 + Health.StartingGrace)));
    }

    [Fact]
    public void Offline_throttled_and_waiting_uploads_are_no_problem() =>
        Assert.Empty(Health.UserProblems(Mounted(T0) with { Online = false, Throttled = true, WaitingUploads = 12, HeldChanges = 1 }, true, T0));

    [Fact]
    public void The_machine_needs_winfsp_and_both_providers()
    {
        Assert.Empty(new MachineHealth("2.1.25156", ["WinFsp.Np", "VFS365.Np", "LanmanWorkstation"], false).Problems());
        var problems = new MachineHealth(null, ["LanmanWorkstation"], true).Problems();
        Assert.Equal(3, problems.Count);
        Assert.Contains(problems, p => p.Contains("machine-setup", StringComparison.Ordinal));
    }

    [Fact]
    public void The_report_sums_up_in_one_line_and_as_json()
    {
        var machine = new MachineHealth("2.1.25156", ["WinFsp.Np", "VFS365.Np"], true);
        var empty = Health.Build(machine, [], T0);
        Assert.True(empty.Healthy);
        Assert.Equal("VFS365 healthy: no user signed in", empty.Summary);

        var users = new[]
        {
            new UserHealth(@"CONTOSO\ann", 2, Mounted(T0), true, []),
            new UserHealth(@"CONTOSO\bob", 3, null, false, Health.UserProblems(null, false, T0)),
        };
        var report = Health.Build(machine with { ProviderOrder = ["WinFsp.Np"] }, users, T0);
        Assert.False(report.Healthy);
        Assert.StartsWith("VFS365 unhealthy: VFS365.Np is missing", report.Summary);
        Assert.Contains(@"CONTOSO\bob: no agent runs", report.Summary);

        using var json = JsonDocument.Parse(report.ToJson());
        Assert.False(json.RootElement.GetProperty("Healthy").GetBoolean());
        var ann = json.RootElement.GetProperty("Users")[0];
        Assert.True(ann.GetProperty("Healthy").GetBoolean());
        Assert.Equal("M:", ann.GetProperty("DriveLetter").GetString());
        Assert.Contains(report.ToText(), line => line.StartsWith(@"User:       CONTOSO\ann, session 2: mounted at M: and \\VFS365\ann", StringComparison.Ordinal));
    }
}
