using Vfs365.Agent;

namespace Vfs365.Tests;

/// <summary>Which exits the supervisor restarts the agent after, and what the drive letter check reports.</summary>
public sealed class StabilityTests
{
    [Theory]
    [InlineData(0, false)]                 // stopped: unmount, sign-out, restart from the tray
    [InlineData(1, false)]                 // a problem the agent reported (sign-in, already mounted)
    [InlineData(2, false)]                 // usage
    [InlineData(-1, false)]                // killed: Task Manager, the uninstaller
    [InlineData(unchecked((int)0xC000013A), false)] // Ctrl+C
    [InlineData(0x40010004, false)]                 // ended by Windows at logoff (DBG_TERMINATE_PROCESS)
    [InlineData(unchecked((int)0xC0000005), true)]  // access violation
    [InlineData(unchecked((int)0xC000041D), true)]  // fatal exception in a user callback
    [InlineData(unchecked((int)0xC0000409), true)]  // fail-fast
    [InlineData(unchecked((int)0xE0434352), true)]  // unhandled .NET exception
    [InlineData(unchecked((int)0x80131623), true)]  // CLR fail-fast (Environment.FailFast)
    [InlineData(unchecked((int)0x80131506), true)]  // CLR execution engine error
    [InlineData(3, true)]                           // nothing the agent returns itself
    public void Only_crashes_restart_the_agent(int exitCode, bool restart) => Assert.Equal(restart, AgentSupervisor.Crashed(exitCode));

    const string Unc = @"\\VFS365\JaneDoe";

    static DriveLetterReport Healthy => new("T:", @"\Device\Volume{x}", 0, Unc, null, true);

    [Fact]
    public void A_healthy_letter_has_no_problems() => Assert.Empty(Healthy.Problems(Unc));

    [Fact]
    public void A_remembered_mapping_on_the_letter_is_named_with_its_fix()
    {
        var problem = Assert.Single((Healthy with { Remembered = @"\\fileserver\data" }).Problems(Unc));
        Assert.Contains(@"\\fileserver\data", problem);
        Assert.Contains(@"HKCU\Network\T", problem);
        Assert.Contains("disconnected network drive", problem);
    }

    [Fact]
    public void A_remembered_mapping_to_vfs365_itself_is_fine() => Assert.Empty((Healthy with { Remembered = Unc.ToUpperInvariant() }).Problems(Unc));

    [Fact]
    public void No_provider_reporting_the_letter_points_at_the_provider_order()
    {
        var problem = Assert.Single((Healthy with { ConnectionError = 2250, RemoteName = null, WinFspInOrder = false }).Problems(Unc));
        Assert.Contains("disconnected network drive", problem);
        Assert.Contains("WinFsp.Np is missing", problem);
    }

    [Fact]
    public void A_letter_that_is_gone_says_so()
    {
        var problem = Assert.Single((Healthy with { Target = null, ConnectionError = 2250, RemoteName = null }).Problems(Unc));
        Assert.Contains("is gone", problem);
        Assert.Contains(Unc, problem);
    }

    [Theory]
    [InlineData("M", "M:", true)]
    [InlineData("m", "M:", true)]
    [InlineData("M:", "M:", true)]
    [InlineData(@"m:\", "M:", true)]
    [InlineData(" T: ", "T:", true)]
    [InlineData("*", null, true)]
    [InlineData("None", null, false)]
    [InlineData("none", null, false)]
    [InlineData("", null, false)]
    public void Drive_letters_are_normalised(string value, string? mountPoint, bool keepLetter)
    {
        var option = DriveLetterOption.Parse(value);
        Assert.Equal((mountPoint, keepLetter, (string?)null), (option.MountPoint, option.KeepLetter, option.Problem));
    }

    [Theory]
    [InlineData("MM")]
    [InlineData("1")]
    [InlineData(@"M:\Data")]
    [InlineData(@"\\server\share")]
    public void An_invalid_drive_letter_mounts_without_one(string value)
    {
        var option = DriveLetterOption.Parse(value);
        Assert.Equal((null, false), (option.MountPoint, option.KeepLetter));
        Assert.Contains(value, option.Problem);
    }

    const string Ns = "http://schemas.microsoft.com/win/2004/08/events/event";

    static string ApplicationError(int processId, bool named, string processIdFormat = "0x{0:x}") =>
        $"""
        <Event xmlns='{Ns}'><System><Provider Name='Application Error'/><EventID>1000</EventID></System><EventData>
        {string.Join("", new[]
        {
            ("AppName", "vfs365-agent.exe"), ("AppVersion", "0.3.2.0"), ("AppTimeStamp", "6700a1b2"), ("ModuleName", "ntdll.dll"), ("ModuleVersion", "10.0.26100.1"),
            ("ModuleTimeStamp", "a1b2c3d4"), ("ExceptionCode", "c0000005"), ("FaultingOffset", "000000000001a2b3"), ("ProcessId", string.Format(processIdFormat, processId)),
        }.Select(d => named ? $"<Data Name='{d.Item1}'>{d.Item2}</Data>" : $"<Data>{d.Item2}</Data>"))}
        </EventData></Event>
        """;

    const string DotNetRuntime = $"""
        <Event xmlns='{Ns}'><System><Provider Name='.NET Runtime'/><EventID Qualifiers='0'>1026</EventID></System><EventData><Data>Application: vfs365-agent.exe
        CoreCLR Version: 10.0.1
        .NET Version: 10.0.1
        Description: The process was terminated due to an unhandled exception.
        Exception Info: System.InvalidOperationException: broken
           at Vfs365.Core.Drive.DriveEngine.Boom()
        </Data></EventData></Event>
        """;

    [Theory]
    [InlineData(true, "0x{0:x}")]
    [InlineData(false, "0x{0:x}")]
    [InlineData(true, "0x0x{0:x}")]
    [InlineData(true, "{0:x}")]
    public void Crash_details_come_from_event_data_in_any_language(bool named, string processIdFormat)
    {
        var lines = AgentSupervisor.ParseCrashEvents(DotNetRuntime + ApplicationError(7700, named, processIdFormat), 7700);

        Assert.Equal("Faulting module: ntdll.dll 10.0.26100.1, exception code 0xc0000005, offset 0x0001a2b3", lines[0]);
        Assert.Equal("Exception Info: System.InvalidOperationException: broken", lines[1]);
        Assert.Equal("at Vfs365.Core.Drive.DriveEngine.Boom()", lines[2]);
    }

    [Fact]
    public void Crash_details_of_another_process_or_nothing_readable_give_nothing()
    {
        Assert.Empty(AgentSupervisor.ParseCrashEvents(ApplicationError(7700, named: true), 7701));
        Assert.Empty(AgentSupervisor.ParseCrashEvents("", 7700));
        Assert.Empty(AgentSupervisor.ParseCrashEvents("<Event", 7700));
    }

    [Fact]
    public void A_provider_order_without_vfs365_is_reported()
    {
        Assert.NotNull(NetworkProviderOrder.Problem(["WinFsp.Np", "RDPNP", "LanmanWorkstation"]));
        Assert.Null(NetworkProviderOrder.Problem(["WinFsp.Np", "VFS365.Np", "LanmanWorkstation"]));
    }
}
