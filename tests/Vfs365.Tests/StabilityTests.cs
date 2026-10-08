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
    [InlineData(unchecked((int)0xC0000005), true)]  // access violation
    [InlineData(unchecked((int)0xC000041D), true)]  // fatal exception in a user callback
    [InlineData(unchecked((int)0xC0000409), true)]  // fail-fast
    [InlineData(unchecked((int)0xE0434352), true)]  // unhandled .NET exception
    public void Only_crashes_restart_the_agent(int exitCode, bool restart) => Assert.Equal(restart, AgentSupervisor.Crashed(exitCode));

    const string Unc = @"\\VFS365\JaneDoe";

    static DriveLetterReport Healthy => new("T:", @"\Device\Volume{x}", 0, Unc, null, true, true);

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

    [Fact]
    public void A_provider_order_without_vfs365_is_reported()
    {
        Assert.NotNull(DriveLetterReport.ProviderProblem(["WinFsp.Np", "RDPNP", "LanmanWorkstation"]));
        Assert.Null(DriveLetterReport.ProviderProblem(["WinFsp.Np", "VFS365.Np", "LanmanWorkstation"]));
    }
}
