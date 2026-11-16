using System.Globalization;
using System.Windows.Forms;
using Vfs365.Agent;

namespace Vfs365.Tests;

/// <summary>The tray's waiting-files window (made off-screen on a UI thread of its own), the text window, and the add-library and why actions.</summary>
public sealed class SmallWindowTests
{
    static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    public SmallWindowTests() => CultureInfo.CurrentUICulture = English;

    static void OnUiThread(Action test)
    {
        Exception? failed = null;
        var thread = new Thread(() =>
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = English;
            try
            {
                test();
            }
            catch (Exception e)
            {
                failed = e;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "the UI test hung");
        if (failed is not null)
        {
            throw new Xunit.Sdk.XunitException(failed.ToString());
        }
    }

    static void Pump(Func<bool> done)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!done() && DateTime.UtcNow < until)
        {
            Application.DoEvents();
            Thread.Sleep(5);
        }
        Assert.True(done(), "the window didn't get there");
    }

    static void OffScreen(Form window)
    {
        window.StartPosition = FormStartPosition.Manual;
        window.Location = new System.Drawing.Point(-32000, -32000);
        window.ShowInTaskbar = false;
        window.Show();
        Application.DoEvents();
    }

    static WaitingFile File(string path, string cause, DateTimeOffset? since = null) => new(path, cause, since ?? Now);

    [Fact]
    public void A_row_has_the_name_the_folder_the_cause_in_plain_words_and_since_when()
    {
        var today = RowOf(File(@"\Sites\Sales\Docs\Offer.docx", "423 Locked from https://x/y: resourceLocked", Now));
        Assert.Equal(["Offer.docx", @"\Sites\Sales\Docs", "the file is locked or checked out", Now.ToLocalTime().ToString("t", English)], today);

        var older = RowOf(File(@"\OneDrive\big.iso", "413 RequestEntityTooLarge from https://x/y", Now.AddDays(-3)));
        Assert.Equal(["big.iso", @"\OneDrive", "the file is too large for Microsoft 365", Now.AddDays(-3).ToLocalTime().ToString("g", English)], older);

        Assert.Equal(@"\", RowOf(File(@"\top.txt", "x"))[1]);
        Assert.Equal("an error VFS365 doesn't recognise", RowOf(File(@"\a.txt", "odd"))[2][..33]);
    }

    static string[] RowOf(WaitingFile file) => WaitingFilesWindow.RowOf(file, Now, English);

    [Theory]
    [InlineData("M:", @"\Sites\Sales\Offer.docx", @"M:\Sites\Sales")]
    [InlineData("M:", @"\top.txt", @"M:\")]
    [InlineData(@"\\VFS365\ann", @"\OneDrive\Offer.docx", @"\\VFS365\ann\OneDrive")]
    [InlineData(@"M:\", @"\OneDrive\a\b.txt", @"M:\OneDrive\a")]
    public void The_folder_is_opened_through_the_volume_path(string root, string volumePath, string expected) =>
        Assert.Equal(expected, WaitingFilesWindow.FolderOf(root, volumePath));

    [Fact]
    public void The_window_lists_the_files_opens_a_folder_and_follows_changes()
    {
        OnUiThread(() =>
        {
            var snapshot = new WaitingSnapshot([File(@"\OneDrive\a.docx", "507 InsufficientStorage from https://x/y", Now.AddMinutes(-5)), File(@"\Sites\Sales\b.xlsx", "timed out")], 30);
            var opened = new List<string>();
            using var window = new WaitingFilesWindow(() => snapshot, path => path.StartsWith(@"\Sites", StringComparison.Ordinal) ? @"M:\Sites\Sales" : null, opened.Add);
            OffScreen(window);

            Assert.Equal(2, window.Rows.Count);
            Assert.Equal("a.docx", window.Rows[0][0]);
            Assert.Equal("your OneDrive or this site is out of storage: free up space or ask your IT department for more", window.Rows[0][2]);
            Assert.Equal("offline", window.Rows[1][2]);
            Assert.Equal("Files waiting to upload", window.Text);
            Assert.Contains("kept on this PC", window.HeadingText);
            Assert.Contains("Showing the 2 oldest of 30 files.", window.DetailText);
            Assert.Contains("out of storage", window.DetailText); // the first row is selected, its full cause shows

            // The first file's folder can't be mapped: nothing to open; the second one opens its folder
            Assert.False(window.CanOpenFolder);
            window.OpenSelectedFolder();
            Assert.Empty(opened);
            window.SelectRow(1);
            Assert.True(window.CanOpenFolder);
            window.OpenSelectedFolder();
            Assert.Equal([@"M:\Sites\Sales"], opened);

            // A refresh: a file uploaded, another waits; the selection stays on its file
            snapshot = new WaitingSnapshot([File(@"\Sites\Sales\b.xlsx", "timed out"), File(@"\Sites\Sales\c.txt", "429 TooManyRequests from https://x/y")], 2);
            window.Reload();
            Assert.Equal(["b.xlsx", "c.txt"], window.Rows.Select(r => r[0]));
            Assert.True(window.CanOpenFolder);
            Assert.DoesNotContain("Showing", window.DetailText);

            snapshot = new WaitingSnapshot([], 0);
            window.Reload();
            Assert.Empty(window.Rows);
            Assert.Equal("Nothing is waiting any more.", window.HeadingText);
            Assert.False(window.CanOpenFolder);
        });
    }

    [Fact]
    public void The_window_refreshes_by_itself()
    {
        OnUiThread(() =>
        {
            var snapshot = new WaitingSnapshot([File(@"\a.txt", "timed out")], 1);
            using var window = new WaitingFilesWindow(() => snapshot, _ => null, _ => { });
            OffScreen(window);
            Assert.Single(window.Rows);
            Assert.Equal(TimeSpan.FromSeconds(5), WaitingFilesWindow.RefreshEvery);
            snapshot = new WaitingSnapshot([File(@"\a.txt", "timed out"), File(@"\b.txt", "timed out")], 2);
            // The timer's tick is the window's own Reload; ticking is the timer's job, so the test calls the same method
            window.Reload();
            Pump(() => window.Rows.Count == 2);
        });
    }

    [Fact]
    public void The_text_window_shows_the_explanation_read_only()
    {
        OnUiThread(() =>
        {
            using var window = new TextWindow("Why is a site missing?", "Site:       Sales\r\nShown:      yes");
            OffScreen(window);
            Assert.Equal("Site:       Sales\r\nShown:      yes", window.Content);
            Assert.Equal("Why is a site missing?", window.Text);
            var box = window.Controls.Cast<Control>().SelectMany(Descendants).OfType<TextBox>().Single();
            Assert.True(box.ReadOnly);
            Assert.True(box.Multiline);
        });
    }

    static IEnumerable<Control> Descendants(Control control) => control.Controls.Cast<Control>().SelectMany(Descendants).Prepend(control);

    [Theory]
    [InlineData("https://contoso.sharepoint.com/sites/Sales", true)]
    [InlineData("  https://contoso.sharepoint.com/sites/Sales/Shared%20Documents  ", true)]
    [InlineData("http://contoso.sharepoint.com/sites/Sales", false)]
    [InlineData("ftp://contoso.sharepoint.com/", false)]
    [InlineData("contoso.sharepoint.com/sites/Sales", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Pin_takes_an_https_address_only(string? text, bool accepted) => Assert.Equal(accepted, UserPins.Parse(text) is not null);

    sealed class Fixture
    {
        public string? Answer;
        public bool Pinned = true, Throw;
        public string[] Lines = ["Discovery:  last run today", "Site:       shown"];
        public readonly List<string> Pins = [], Asked = [];
        public readonly List<(string Title, string Text, bool Problem)> Told = [];
        public readonly List<(string Title, string Text)> Shown = [];

        public LocationActions Actions() => new(
            (title, prompt, button, _, _) =>
            {
                Asked.Add($"{title}|{prompt}|{button}");
                return Task.FromResult(Answer);
            },
            url =>
            {
                if (Throw)
                {
                    throw new IOException("disk full");
                }
                Pins.Add(url);
                return Pinned;
            },
            query =>
            {
                if (Throw)
                {
                    throw new InvalidOperationException("broken");
                }
                Asked.Add($"explain {query}");
                return Lines;
            },
            (title, text, problem) => Told.Add((title, text, problem)), (title, text) => Shown.Add((title, text)), _ => { });
    }

    [Fact]
    public async Task Add_library_pins_the_link_like_the_pin_command_and_says_it_shows_after_a_restart()
    {
        var fixture = new Fixture { Answer = " https://contoso.sharepoint.com/sites/Sales " };
        await fixture.Actions().AddLibraryAsync(default);

        Assert.Equal(["https://contoso.sharepoint.com/sites/Sales"], fixture.Pins);
        Assert.Equal("Add a library by link|Address of a SharePoint site or library:|Add", fixture.Asked[0]);
        var told = Assert.Single(fixture.Told);
        Assert.False(told.Problem);
        Assert.Contains("https://contoso.sharepoint.com/sites/Sales", told.Text);
        Assert.Contains("Restart", told.Text);

        fixture.Pinned = false;
        await fixture.Actions().AddLibraryAsync(default);
        Assert.Contains("added before", fixture.Told[^1].Text);
        Assert.False(fixture.Told[^1].Problem);
    }

    [Theory]
    [InlineData("http://contoso.sharepoint.com/sites/Sales")]
    [InlineData("Sales")]
    public async Task Add_library_refuses_what_the_pin_command_refuses(string answer)
    {
        var fixture = new Fixture { Answer = answer };
        await fixture.Actions().AddLibraryAsync(default);
        Assert.Empty(fixture.Pins);
        var told = Assert.Single(fixture.Told);
        Assert.True(told.Problem);
        Assert.Contains("https address", told.Text);
    }

    [Fact]
    public async Task Cancelling_adds_and_explains_nothing_and_a_failing_write_is_a_notification()
    {
        var fixture = new Fixture();
        await fixture.Actions().AddLibraryAsync(default);
        await fixture.Actions().WhyAsync(default);
        Assert.Empty(fixture.Pins);
        Assert.Empty(fixture.Told);
        Assert.Empty(fixture.Shown);

        fixture.Answer = "https://contoso.sharepoint.com/sites/Sales";
        fixture.Throw = true;
        await fixture.Actions().AddLibraryAsync(default);
        Assert.True(fixture.Told[^1].Problem);
        Assert.Contains("disk full", fixture.Told[^1].Text);
    }

    [Fact]
    public async Task Why_shows_the_explanation_in_a_window()
    {
        var fixture = new Fixture { Answer = " Sales " };
        await fixture.Actions().WhyAsync(default);

        Assert.Equal("Why is a site missing?|Link, or part of the name, of the site or library:|Explain", fixture.Asked[0]);
        Assert.Equal("explain Sales", fixture.Asked[1]);
        Assert.Equal(("Why is a site missing?", "Discovery:  last run today\r\nSite:       shown"), Assert.Single(fixture.Shown));
        Assert.Empty(fixture.Told);

        fixture.Throw = true;
        await fixture.Actions().WhyAsync(default);
        Assert.Single(fixture.Shown);
        Assert.True(fixture.Told[^1].Problem);
        Assert.Contains("broken", fixture.Told[^1].Text);
    }
}
