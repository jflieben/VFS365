using System.Diagnostics;
using System.Reflection;
using Vfs365.Agent;

namespace Vfs365.Tests;

public sealed class MachineStartTests
{
    [Fact]
    public void Starts_in_signed_in_sessions_without_an_agent()
    {
        // WTS states: 0 active, 1 connected, 2 connect query, 4 disconnected, 6 listen
        (int, int)[] sessions = [(0, 4), (1, 0), (2, 4), (3, 0), (4, 6), (65536, 1)];

        Assert.Equal([1, 2], MachineStart.SessionsToStart(sessions, new HashSet<int> { 3 }));
    }

    [Fact]
    public void Lists_this_session()
    {
        var sessions = (List<(int Id, int State)>)typeof(MachineStart).GetMethod("Sessions", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;

        Assert.Contains(sessions, s => s.Id == Process.GetCurrentProcess().SessionId);
    }
}
