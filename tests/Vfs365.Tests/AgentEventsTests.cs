using System.Runtime.InteropServices;
using System.Text;
using Vfs365.Agent;

namespace Vfs365.Tests;

/// <summary>Windows Event Log entries: fixed IDs and types, redaction and the rate limit, against a fake sink.</summary>
public sealed class AgentEventsTests
{
    sealed class FakeSink : IEventSink
    {
        public List<(int Id, EventType Type, string Message)> Written { get; } = [];

        public void Write(int id, EventType type, string message) => Written.Add((id, type, message));
    }

    static readonly DateTimeOffset T0 = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Every_event_has_a_fixed_id_in_eventcreates_range()
    {
        var ids = Enum.GetValues<AgentEvent>().Select(e => (int)e).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(ids, id => Assert.InRange(id, 1, 1000));
        Assert.Equal(100, (int)AgentEvent.Started);
        Assert.Equal(200, (int)AgentEvent.SignInNeeded);
        Assert.Equal(400, (int)AgentEvent.ConflictCopy);
        Assert.Equal(600, (int)AgentEvent.Throttled);
    }

    [Theory]
    [InlineData(AgentEvent.Started, EventType.Information)]
    [InlineData(AgentEvent.Stopped, EventType.Information)]
    [InlineData(AgentEvent.Recovered, EventType.Information)]
    [InlineData(AgentEvent.SettingsChanged, EventType.Information)]
    [InlineData(AgentEvent.NotStarted, EventType.Error)]
    [InlineData(AgentEvent.Crashed, EventType.Error)]
    [InlineData(AgentEvent.NotRestarted, EventType.Error)]
    [InlineData(AgentEvent.UncPathTaken, EventType.Error)]
    [InlineData(AgentEvent.NotSaved, EventType.Error)]
    [InlineData(AgentEvent.SignInNeeded, EventType.Warning)]
    [InlineData(AgentEvent.ConflictCopy, EventType.Warning)]
    [InlineData(AgentEvent.UploadWaiting, EventType.Warning)]
    [InlineData(AgentEvent.Throttled, EventType.Warning)]
    [InlineData(AgentEvent.SettingWarning, EventType.Warning)]
    public void Each_event_has_its_type(AgentEvent kind, EventType type)
    {
        var sink = new FakeSink();
        Assert.True(new EventReporter(sink, () => T0).Write(kind, "text"));
        Assert.Equal(((int)kind, type, "text"), Assert.Single(sink.Written));
    }

    [Fact]
    public void Without_a_registered_source_nothing_is_written() => Assert.False(new EventReporter(null).Write(AgentEvent.Started, "text"));

    [Fact]
    public void The_same_message_is_written_once_an_hour()
    {
        var sink = new FakeSink();
        var now = T0;
        var events = new EventReporter(sink, () => now);
        Assert.True(events.Write(AgentEvent.SignInNeeded, "Sign-in needed: MFA"));
        now += TimeSpan.FromMinutes(30);
        Assert.False(events.Write(AgentEvent.SignInNeeded, "Sign-in needed: MFA"));
        Assert.True(events.Write(AgentEvent.UploadWaiting, "Sign-in needed: MFA")); // another ID has its own limit
        now = T0 + EventReporter.Window;
        Assert.True(events.Write(AgentEvent.SignInNeeded, "Sign-in needed: MFA"));
        Assert.Equal(3, sink.Written.Count);
        Assert.EndsWith("(1 more event(s) of this kind since 2026-10-25 09:30 were not written)", sink.Written[2].Message);
    }

    [Fact]
    public void At_most_a_few_of_one_kind_an_hour_and_the_next_says_how_many_were_left_out()
    {
        var sink = new FakeSink();
        var now = T0;
        var events = new EventReporter(sink, () => now);
        for (var i = 0; i < 25; i++)
        {
            events.Write(AgentEvent.ConflictCopy, $"file{i}.docx was changed");
            now += TimeSpan.FromMinutes(1);
        }
        Assert.Equal(EventReporter.MaxPerWindow, sink.Written.Count);

        now = T0 + EventReporter.Window + TimeSpan.FromMinutes(1);
        Assert.True(events.Write(AgentEvent.ConflictCopy, "late.docx was changed"));
        Assert.Equal("late.docx was changed (15 more event(s) of this kind since 2026-10-25 09:10 were not written)", sink.Written[^1].Message);
    }

    [Fact]
    public void Messages_are_redacted_and_cut_to_length()
    {
        var sink = new FakeSink();
        var events = new EventReporter(sink, () => T0);
        events.Write(AgentEvent.SettingWarning, "MonitoringUrl https://acct.table.core.windows.net/?sv=2022&ss=t&sig=EventSig0123 is refused");
        events.Write(AgentEvent.NotSaved, new string('x', 20_000));
        Assert.DoesNotContain("EventSig0123", sink.Written[0].Message);
        Assert.Contains("acct.table.core.windows.net", sink.Written[0].Message);
        Assert.Equal(EventReporter.MaxLength, sink.Written[1].Message.Length);
    }

    /// <summary>
    /// The MSI registers the source with EventCreate.exe as its message file: its message table must have "%1" for every ID used, or
    /// Event Viewer shows "The description for Event ID ... cannot be found". Read-only: the file is loaded as data.
    /// </summary>
    [Fact]
    public void Eventcreates_message_table_has_a_plain_message_for_every_id()
    {
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "EventCreate.exe");
        if (!File.Exists(file))
        {
            return; // not on this Windows edition
        }
        var module = LoadLibraryExW(file, IntPtr.Zero, 0x2 | 0x20); // LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_AS_IMAGE_RESOURCE
        Assert.NotEqual(IntPtr.Zero, module);
        try
        {
            foreach (var kind in Enum.GetValues<AgentEvent>())
            {
                var text = new StringBuilder(512);
                var length = FormatMessageW(0x800 | 0x200, module, (uint)kind, 0, text, text.Capacity, IntPtr.Zero); // FROM_HMODULE | IGNORE_INSERTS
                Assert.True(length > 0, $"{kind} ({(int)kind}): no message");
                Assert.Equal("%1", text.ToString(0, length).Trim());
            }
        }
        finally
        {
            FreeLibrary(module);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr LoadLibraryExW(string name, IntPtr file, uint flags);

    [DllImport("kernel32.dll")]
    static extern bool FreeLibrary(IntPtr module);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern int FormatMessageW(uint flags, IntPtr source, uint id, uint language, StringBuilder buffer, int size, IntPtr arguments);
}
