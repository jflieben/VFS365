using System.Globalization;
using System.Windows.Forms;
using Vfs365.Agent;

namespace Vfs365.Tests;

/// <summary>The details window, made hidden on a UI thread of its own against the fake SharePoint of <see cref="ColumnsApiTests"/>.</summary>
public sealed class DetailsWindowTests
{
    static void OnUiThread(Action test)
    {
        Exception? failed = null;
        var thread = new Thread(() =>
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
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

    /// <summary>Runs the window's message loop until <paramref name="done"/> or 10 s.</summary>
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

    static IEnumerable<Control> All(Control root) => root.Controls.Cast<Control>().SelectMany(c => All(c).Prepend(c));

    static DetailsWindow Open(ColumnsApiTests.Server server, DetailsRequest request, List<string> opened)
    {
        var window = new DetailsWindow(ColumnsApiTests.Api(server), request, opened.Add, _ => { })
        {
            StartPosition = FormStartPosition.Manual,
            Location = new System.Drawing.Point(-32000, -32000),
            ShowInTaskbar = false,
        };
        window.Show();
        Pump(() => All(window).OfType<TextBox>().Any());
        return window;
    }

    [Fact]
    public void The_window_shows_the_details_and_an_editor_per_column_and_saves_what_changed()
    {
        OnUiThread(() =>
        {
            var server = new ColumnsApiTests.Server();
            var opened = new List<string>();
            using var window = Open(server, new DetailsRequest("d1", "i1", "a.docx", @"M:\Sites\A\a.docx", false), opened);
            var controls = All(window).ToList();

            Assert.Contains(controls.OfType<Label>(), l => l.Text == "Carol"); // checked out to
            Assert.Contains(controls.OfType<Label>(), l => l.Text == "Keep 7 years");
            Assert.Contains(controls.OfType<Label>(), l => l.Text == "Person *");
            Assert.Single(controls.OfType<DateTimePicker>());
            Assert.Single(controls.OfType<CheckBox>());
            Assert.Single(controls.OfType<CheckedListBox>());
            Assert.Contains(controls.OfType<ComboBox>(), c => c.Items.Cast<object>().Any(i => i.ToString() == "Contract")); // content types
            var text = controls.OfType<TextBox>().Single(t => t.Text == "old");
            Assert.Equal(50, text.MaxLength);

            text.Text = "new";
            controls.OfType<Button>().Single(b => b.Text == "&Save").PerformClick();
            Pump(() => window.IsDisposed || server.Requests.Any(r => r.Method == "PATCH"));
            Pump(() => window.IsDisposed);

            var patch = Assert.Single(server.Requests, r => r.Method == "PATCH");
            Assert.Equal("""{"S17Text":"new"}""", patch.Body);
        });
    }

    [Fact]
    public void Bad_values_stop_the_save_before_anything_is_sent()
    {
        OnUiThread(() =>
        {
            var server = new ColumnsApiTests.Server();
            using var window = Open(server, new DetailsRequest("d1", "i1", "a.docx", null, false), []);
            var controls = All(window).ToList();
            var number = controls.OfType<TextBox>().Single(t => t.Text == "5");

            number.Text = "eleven";
            controls.OfType<Button>().Single(b => b.Text == "&Save").PerformClick();
            Pump(() => controls.OfType<Label>().Any(l => l.Text == UiText.Get("Details.Problems")));
            number.Text = "11"; // above the column's maximum, 10
            controls.OfType<Button>().Single(b => b.Text == "&Save").PerformClick();
            Application.DoEvents();

            Assert.DoesNotContain(server.Requests, r => r.Method == "PATCH");
            Assert.False(window.IsDisposed);
        });
    }

    [Fact]
    public void A_read_only_item_shows_but_doesn_t_save_and_missing_columns_are_bold()
    {
        OnUiThread(() =>
        {
            var server = new ColumnsApiTests.Server();
            using var window = Open(server, new DetailsRequest("d1", "i1", "a.docx", null, true, ["Person"]), []);
            var controls = All(window).ToList();

            Assert.All(controls.OfType<TextBox>(), t => Assert.False(t.Enabled));
            Assert.False(controls.OfType<Button>().Single(b => b.Text == "&Save").Visible);
            Assert.True(controls.OfType<Label>().Single(l => l.Text == "Person *").Font.Bold);
        });
    }

    [Fact]
    public void Closing_with_a_required_column_empty_asks_first_and_a_full_save_reports_it_filled()
    {
        OnUiThread(() =>
        {
            var server = new ColumnsApiTests.Server { People = "{}" }; // the required person column empty
            var saved = new List<DetailsRequest>();
            var window = new DetailsWindow(ColumnsApiTests.Api(server), new DetailsRequest("d1", "i1", "a.docx", null, false), _ => { }, _ => { }, saved.Add)
            {
                StartPosition = FormStartPosition.Manual,
                Location = new System.Drawing.Point(-32000, -32000),
                ShowInTaskbar = false,
            };
            var asked = new List<string>();
            window.AskClose = question =>
            {
                asked.Add(question);
                return false;
            };
            window.Show();
            Pump(() => All(window).OfType<TextBox>().Any());

            window.Close();
            Application.DoEvents();
            Assert.False(window.IsDisposed); // the person column is required and empty
            Assert.Contains("Person", Assert.Single(asked));

            window.AskClose = _ => true;
            window.Close();
            Pump(() => window.IsDisposed);
            Assert.Empty(saved);
        });
    }

    [Fact]
    public void Closing_with_everything_required_filled_ends_the_reminders()
    {
        OnUiThread(() =>
        {
            var saved = new List<DetailsRequest>();
            var window = new DetailsWindow(ColumnsApiTests.Api(new ColumnsApiTests.Server()), new DetailsRequest("d1", "i1", "a.docx", null, false), _ => { }, _ => { }, saved.Add)
            {
                StartPosition = FormStartPosition.Manual,
                Location = new System.Drawing.Point(-32000, -32000),
                ShowInTaskbar = false,
            };
            window.Show();
            Pump(() => All(window).OfType<TextBox>().Any());

            window.Close();
            Pump(() => window.IsDisposed);

            Assert.Equal("i1", Assert.Single(saved).ItemId);
        });
    }

    [Fact]
    public void An_unexpected_error_in_the_window_closes_that_window_and_is_logged()
    {
        OnUiThread(() =>
        {
            var logged = new List<string>();
            var window = Open(new ColumnsApiTests.Server { People = "{}" }, new DetailsRequest("d1", "i1", "a.docx", null, false), []);
            window.AskClose = _ => throw new InvalidOperationException("the close question must not be asked");
            DetailsWindows.Guard(window, logged.Add);

            window.BeginInvoke(() => throw new FormatException("an unexpected column value")); // as a handler on the window's loop would

            Pump(() => window.IsDisposed);
            Assert.Contains("an unexpected column value", Assert.Single(logged));
        });
    }

    [Fact]
    public void Open_in_browser_opens_the_item_s_form()
    {
        OnUiThread(() =>
        {
            var opened = new List<string>();
            using var window = Open(new ColumnsApiTests.Server(), new DetailsRequest("d1", "i1", "a.docx", null, false), opened);

            All(window).OfType<Button>().Single(b => b.Text == "Open in &browser").PerformClick();

            Assert.Equal("https://contoso.sharepoint.com/sites/a/_layouts/15/listform.aspx?PageType=6&ListId=%7Bl1%7D&ID=7", Assert.Single(opened));
        });
    }
}
