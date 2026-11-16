using Vfs365.Agent;
using Vfs365.Core;
using Vfs365.Graph;

namespace Vfs365.Tests;

public sealed class PendingDetailsTests
{
    DateTimeOffset now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
    readonly List<DetailsRequest> shown = [];
    readonly List<(string Title, string Text, Action? Clicked)> notified = [];
    readonly Dictionary<string, Func<IReadOnlyList<string>?>> answers = new();
    int checks;
    bool background;

    PendingDetails Pending() => new((_, item, _) =>
    {
        checks++;
        background = RequestPriority.IsBackground;
        return Task.FromResult(answers.TryGetValue(item, out var answer) ? answer() : ["Needed"]);
    }, shown.Add, (title, text, clicked) => notified.Add((title, text, clicked)), _ => { }, () => now);

    static FileNeedsDetails File(string item, string name = "a.txt") => new("drive1", item, name, ["Needed"]);

    [Fact]
    public async Task A_file_is_reminded_after_1_3_7_and_15_hours_and_then_no_more()
    {
        var pending = Pending();
        pending.Add(File("item1"));
        var saved = now;

        await pending.RemindAsync(default);
        Assert.Empty(notified); // not due yet
        Assert.Equal(0, checks);

        foreach (var (hours, reminders) in new[] { (1, 1), (2, 1), (3, 2), (6, 2), (7, 3), (14, 3), (15, 4), (20, 4), (23, 4) })
        {
            now = saved + TimeSpan.FromHours(hours);
            await pending.RemindAsync(default);
            Assert.Equal(reminders, notified.Count);
        }
        Assert.Equal(4, checks); // one call per reminder
        Assert.True(background);
        Assert.Equal(1, pending.Count); // still in the tray's list

        notified[0].Clicked!();
        Assert.Equal(["Needed"], Assert.Single(shown).Missing!);

        now = saved + PendingDetails.KeptFor;
        await pending.RemindAsync(default);
        Assert.Equal(0, pending.Count);
    }

    [Fact]
    public async Task Filled_in_elsewhere_or_gone_it_leaves_the_list_without_a_reminder()
    {
        var pending = Pending();
        pending.Add(File("filled"));
        pending.Add(File("deleted"));
        answers["filled"] = () => [];
        answers["deleted"] = () => throw new RemoteException(RemoteError.NotFound, "gone");

        now += PendingDetails.FirstReminder;
        await pending.RemindAsync(default);

        Assert.Empty(notified);
        Assert.Equal(0, pending.Count);
    }

    [Fact]
    public async Task A_file_saved_in_the_window_while_it_is_checked_isn_t_put_back()
    {
        PendingDetails? pending = null;
        pending = new PendingDetails((driveId, item, _) =>
        {
            pending!.Filled(driveId, item); // the window saved it meanwhile
            return Task.FromResult<IReadOnlyList<string>?>(["Needed"]);
        }, shown.Add, (title, text, clicked) => notified.Add((title, text, clicked)), _ => { }, () => now);
        pending.Add(File("item1"));

        now += PendingDetails.FirstReminder;
        await pending.RemindAsync(default);

        Assert.Equal(0, pending.Count);
        Assert.Empty(notified);
    }

    [Fact]
    public async Task A_check_that_fails_or_can_t_tell_keeps_reminding_with_what_was_known()
    {
        var pending = Pending();
        pending.Add(File("offline"));
        pending.Add(File("unknown"));
        answers["offline"] = () => throw new HttpRequestException("offline");
        answers["unknown"] = () => null;

        now += PendingDetails.FirstReminder;
        await pending.RemindAsync(default);

        Assert.Contains("2", Assert.Single(notified).Text);
        Assert.Equal(2, pending.Count);
    }

    [Fact]
    public async Task Several_files_share_one_reminder_whose_click_opens_the_oldest()
    {
        var pending = Pending();
        pending.Add(File("item1", "first.txt"));
        now += TimeSpan.FromMinutes(1);
        pending.Add(File("item2", "second.txt"));

        now += PendingDetails.FirstReminder;
        await pending.RemindAsync(default);

        var reminder = Assert.Single(notified);
        Assert.Contains("2", reminder.Text);
        reminder.Clicked!();
        Assert.Equal("first.txt", Assert.Single(shown).Name);
    }

    [Fact]
    public async Task A_big_copy_costs_a_bounded_number_of_checks_and_the_list_has_a_limit()
    {
        var pending = Pending();
        foreach (var item in Enumerable.Range(0, 1000))
        {
            pending.Add(File($"item{item}"));
        }
        Assert.Equal(PendingDetails.MaxFiles, pending.Count);

        now += PendingDetails.FirstReminder;
        await pending.RemindAsync(default);
        await pending.RemindAsync(default); // none due again within the hour

        Assert.Equal(PendingDetails.MaxChecks, checks);
        Assert.Contains(PendingDetails.MaxFiles.ToString(System.Globalization.CultureInfo.InvariantCulture), Assert.Single(notified).Text); // the unchecked ones count as waiting
    }

    [Fact]
    public void A_save_in_the_window_ends_the_reminders()
    {
        var pending = Pending();
        pending.Add(File("item1"));
        pending.Add(File("item2"));

        pending.Filled("drive1", "item2");

        Assert.Equal(1, pending.Count);
    }

    [Fact]
    public void The_tray_item_opens_the_oldest_and_a_file_without_empty_columns_isn_t_added()
    {
        var pending = Pending();
        pending.Add(new FileNeedsDetails("drive1", "none", "x.txt", []));
        Assert.Equal(0, pending.Count);

        pending.Add(File("item1", "older.txt"));
        now += TimeSpan.FromMinutes(5);
        pending.Add(File("item2", "newer.txt"));
        pending.ShowOldest();

        Assert.Equal("older.txt", Assert.Single(shown).Name);
    }
}
