using System.Diagnostics;
using Vfs365.Agent;

namespace Vfs365.Tests;

public sealed class AgentControlTests
{
    [Fact]
    public void Stop_event_lives_in_the_session_namespace()
    {
        Assert.Equal(@"Local\VFS365.Stop", AgentControl.EventName(3, 3));
        Assert.Equal(@"Global\Session\3\VFS365.Stop", AgentControl.EventName(3, 0));
    }

    [Fact]
    public void The_session_link_reaches_a_local_event()
    {
        var session = Process.GetCurrentProcess().SessionId;
        if (session == 0)
        {
            return; // services (CI runners): Local\ is the global namespace there, and agents never run in session 0
        }
        var name = $"VFS365.Test.{Guid.NewGuid():N}";
        using var local = new EventWaitHandle(false, EventResetMode.ManualReset, $@"Local\{name}");

        Assert.True(EventWaitHandle.TryOpenExisting($@"Global\Session\{session}\{name}", out var opened));
        opened.Dispose();
    }
}
