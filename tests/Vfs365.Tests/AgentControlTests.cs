using System.Diagnostics;
using System.Security.Principal;
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

    [Fact]
    public void Another_session_s_event_is_set_only_when_the_agent_s_user_owns_it()
    {
        // Never the real name: an agent may run in this session
        var name = $@"Local\VFS365.Test.{Guid.NewGuid():N}";
        using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, name);
        // The token's default owner: the user, or Administrators in an elevated test run
        var owner = (SecurityIdentifier)stop.GetAccessControl().GetOwner(typeof(SecurityIdentifier))!;

        Assert.False(AgentControl.SetIfOwnedBy(name, new SecurityIdentifier("S-1-5-21-1-2-3-1001")));
        Assert.False(AgentControl.SetIfOwnedBy(name, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)));
        Assert.False(stop.WaitOne(0));
        Assert.True(AgentControl.SetIfOwnedBy(name, owner));
        Assert.True(stop.WaitOne(0));
        Assert.False(AgentControl.SetIfOwnedBy($@"Local\VFS365.Test.{Guid.NewGuid():N}", owner));
    }

    [Fact]
    public void The_stop_event_is_owned_by_the_user_also_in_an_elevated_agent()
    {
        // The agent's security on a test name: an elevated token would make Administrators the owner, which machine-stop refuses
        var name = $@"Local\VFS365.Test.{Guid.NewGuid():N}";
        using var stop = EventWaitHandleAcl.Create(false, EventResetMode.ManualReset, name, out var createdNew, AgentControl.StopEventSecurity());
        var user = WindowsIdentity.GetCurrent().User!;

        Assert.True(createdNew);
        Assert.Equal(user, stop.GetAccessControl().GetOwner(typeof(SecurityIdentifier)));
        Assert.True(AgentControl.SetIfOwnedBy(name, user));
    }
}
