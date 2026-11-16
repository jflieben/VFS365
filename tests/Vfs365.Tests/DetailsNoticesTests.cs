using Vfs365.Agent;
using Vfs365.Graph;

namespace Vfs365.Tests;

public sealed class DetailsNoticesTests
{
    DateTimeOffset now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
    readonly List<DetailsRequest> opened = [];
    readonly List<string> logged = [];
    readonly List<(string Title, string Text, Action? Clicked)> shown = [];

    DetailsNotices Notices() => new(opened.Add, (title, text, clicked) => shown.Add((title, text, clicked)), logged.Add, () => now);

    static FileNeedsDetails File(string item = "item1", bool stillCheckedOut = false) => new("drive1", item, "a.txt", ["Needed"], stillCheckedOut, stillCheckedOut ? "refused" : null);

    [Fact]
    public void A_file_is_notified_once_in_a_while_and_logged_every_time()
    {
        var notices = Notices();

        notices.Handle(File());
        notices.Handle(File());
        Assert.Single(shown);
        Assert.Equal(2, logged.Count);

        now += DetailsNotices.NotifyGap;
        notices.Handle(File());
        Assert.Single(shown);

        now += DetailsNotices.NotifyEvery;
        notices.Handle(File());
        Assert.Equal(2, shown.Count);
    }

    [Fact]
    public void A_copy_of_many_files_is_not_a_stream_of_notifications()
    {
        var notices = Notices();

        foreach (var item in Enumerable.Range(0, 100))
        {
            notices.Handle(File($"item{item}"));
        }
        Assert.Single(shown);
        Assert.Equal(100, logged.Count);

        now += DetailsNotices.NotifyGap;
        notices.Handle(File("another"));
        Assert.Equal(2, shown.Count);
    }

    [Fact]
    public void The_click_opens_the_details_window_with_the_empty_columns()
    {
        Notices().Handle(File());

        shown[0].Clicked!();

        var request = Assert.Single(opened);
        Assert.Equal(("drive1", "item1", "a.txt"), (request.DriveId, request.ItemId, request.Name));
        Assert.Equal(["Needed"], request.Missing!);
        Assert.False(request.ReadOnly);
    }

    [Fact]
    public void A_file_still_checked_out_says_colleagues_see_the_previous_version()
    {
        Notices().Handle(File(stillCheckedOut: true));

        Assert.Equal(UiText.Get("CheckedOut.Title"), shown[0].Title);
        Assert.Contains("refused", logged[0]);
    }

    [Fact]
    public void A_failed_lookup_only_goes_to_the_log()
    {
        Notices().Handle(new("drive1", "item1", "a.txt", [], true, "no answer"));

        Assert.Empty(shown);
        Assert.Contains("no answer", Assert.Single(logged));
    }
}
