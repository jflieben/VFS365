using System.Globalization;
using Vfs365.Agent;
using Vfs365.Core.Drive;
using Vfs365.Graph;

namespace Vfs365.Tests;

/// <summary>What users read: the string table, plain causes, grouped notifications and why a start waits.</summary>
public sealed class AgentTextTests
{
    static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-GB"), Dutch = CultureInfo.GetCultureInfo("nl-BE");
    static readonly DateTimeOffset T0 = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Every_text_has_both_languages_with_the_same_placeholders()
    {
        foreach (var key in UiText.Keys)
        {
            var en = UiText.Get(English, key);
            var nl = UiText.Get(Dutch, key);
            Assert.False(string.IsNullOrWhiteSpace(en), key);
            Assert.False(string.IsNullOrWhiteSpace(nl), key);
            for (var i = 0; i < 3; i++)
            {
                Assert.True(en.Contains($"{{{i}}}") == nl.Contains($"{{{i}}}"), $"{key} {{{i}}}");
            }
            Assert.DoesNotContain((char)0x2014, en + nl); // no em dashes
        }
    }

    [Fact]
    public void Dutch_for_dutch_windows_english_otherwise()
    {
        Assert.Equal("&Aanmelden", UiText.Get(CultureInfo.GetCultureInfo("nl-NL"), "Menu.SignIn"));
        Assert.Equal("S&ign in", UiText.Get(CultureInfo.GetCultureInfo("de-DE"), "Menu.SignIn"));
        Assert.Equal("S&ign in", UiText.Get(CultureInfo.InvariantCulture, "Menu.SignIn"));
        Assert.Equal("3 wijziging(en) wachten op uploaden", UiText.Get(Dutch, "Status.Waiting", 3));
    }

    [Theory]
    [InlineData("429 TooManyRequests from https://x/y: slow down", Cause.Throttled)]
    [InlineData("503 ServiceUnavailable from https://x/y: busy", Cause.Throttled)]
    [InlineData("423 Locked from https://x/y: checked out", Cause.Locked)]
    [InlineData("403 Forbidden from https://x/y: accessDenied", Cause.NoAccess)]
    [InlineData("403 Forbidden from https://x/y: {\"code\":\"serviceReadOnly\"}", Cause.ReadOnly)]
    [InlineData("404 NotFound from https://x/y: itemNotFound", Cause.Gone)]
    [InlineData("500 InternalServerError from https://x/y: oops", Cause.Unavailable)]
    [InlineData("Er is geen host bekend. (graph.microsoft.com:443)", Cause.Offline)]
    [InlineData("No such host is known. (contoso.sharepoint.com:443)", Cause.Offline)]
    [InlineData("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.", Cause.Offline)]
    [InlineData("AADSTS50076: Due to a configuration change made by your administrator, you must use multi-factor authentication.", Cause.SignedOut)]
    [InlineData("SharePoint has the library read-only for now (maintenance or a site move)", Cause.ReadOnly)]
    [InlineData("Something odd", Cause.Other)]
    [InlineData("507 InsufficientStorage from https://x/y: quota", Cause.Quota)]
    [InlineData("503 ServiceUnavailable from https://x/y: {\"code\":\"quotaLimitReached\"}", Cause.Quota)]
    [InlineData("500 InternalServerError from https://x/y: {\"code\":\"insufficientStorage\"}", Cause.Quota)]
    [InlineData("insufficientStorage", Cause.Quota)]
    [InlineData("400 BadRequest from https://x/y: invalidRequest, the name is not allowed", Cause.Refused)]
    [InlineData("413 RequestEntityTooLarge from https://x/y: too big", Cause.TooLarge)]
    [InlineData("502 BadGateway from https://x/y: oops", Cause.Unavailable)]
    [InlineData("418 Teapot from https://x/y: odd", Cause.Other)]
    public void Errors_get_a_plain_cause(string message, Cause cause) => Assert.Equal(cause, PlainCause.Of(message));

    [Fact]
    public void Every_cause_has_a_text_in_both_languages_and_the_new_ones_say_what_to_do()
    {
        foreach (var cause in Enum.GetValues<Cause>())
        {
            Assert.False(string.IsNullOrWhiteSpace(PlainCause.Text(cause, English)), cause.ToString());
            Assert.False(string.IsNullOrWhiteSpace(PlainCause.Text(cause, Dutch)), cause.ToString());
            Assert.False(string.IsNullOrWhiteSpace(PlainCause.Text(cause, English, upload: true)), cause.ToString());
            Assert.False(string.IsNullOrWhiteSpace(PlainCause.Text(cause, Dutch, upload: true)), cause.ToString());
        }
        Assert.Contains("free up space", PlainCause.Text(Cause.Quota, English));
        Assert.Contains("rename the file", PlainCause.Text(Cause.Refused, English, upload: true));
        Assert.DoesNotContain("name", PlainCause.Text(Cause.Refused, English)); // outside uploads (the details window) a 400 is about something else
        Assert.Contains("too large", PlainCause.Text(Cause.TooLarge, English));
        Assert.Contains("Collect diagnostics", PlainCause.Text(Cause.Other, English, upload: true));
        Assert.Contains("Diagnosegegevens verzamelen", PlainCause.Text(Cause.Other, Dutch, upload: true));
        Assert.Contains("upload when it recovers", PlainCause.Text(Cause.Unavailable, English, upload: true));
        Assert.DoesNotContain("upload", PlainCause.Text(Cause.Unavailable, English)); // nothing uploads when the details window fails to load
        Assert.Contains("opslagruimte", PlainCause.Text(Cause.Quota, Dutch));
        Assert.Equal("report.docx isn't uploaded yet: the file is too large for Microsoft 365. VFS365 keeps trying; your changes are kept on this PC.",
            UiText.Get(English, "Waiting.One", "report.docx", PlainCause.Text(Cause.TooLarge, English)));
    }

    [Fact]
    public void The_tray_menus_access_keys_are_unique_per_language()
    {
        // Items that can show together: everything but Retry (before the drive is up) and Restart/SignIn variants
        string[] keys = ["Menu.ShowFiles", "Menu.Search", "Menu.Details", "Menu.Waiting", "Menu.AddLibrary", "Menu.Why", "Menu.ShowLog", "Menu.Diagnostics", "Menu.Restart", "Menu.SignIn", "Menu.Help"];
        foreach (var culture in new[] { English, Dutch })
        {
            var accelerators = keys.Select(key => UiText.Get(culture, key, 1)).Select(text => char.ToLowerInvariant(text[text.IndexOf('&') + 1])).ToList();
            Assert.Equal(accelerators.Count, accelerators.Distinct().Count());
        }
    }

    [Fact]
    public void What_the_agent_knows_explains_unclear_errors()
    {
        Assert.Equal(Cause.Offline, PlainCause.Of("Something odd", offline: true));
        Assert.Equal(Cause.SignedOut, PlainCause.Of("Something odd", signInProblem: true));
        Assert.Equal(Cause.Locked, PlainCause.Of("423 Locked from https://x/y: z", offline: true));
    }

    [Fact]
    public void Sign_in_problems_in_plain_words()
    {
        Assert.Equal("multi-factor authentication is needed", SignInText.Of(new SignInException("AADSTS50076", "AADSTS50076: MFA", true), English));
        Assert.Equal("aanmelden is geannuleerd", SignInText.Of(new SignInException(SignInException.Cancelled, "cancelled", true), Dutch));
        Assert.Equal("sign-in failed", SignInText.Of(new SignInException("wam_CAA20004", "something"), English));
        Assert.Equal("de aanmelding van Windows (Web Account Manager) werkt niet",
            SignInText.Of(new SignInException(SignInException.Unavailable, "Windows' sign-in (WAM) failed: ..."), Dutch));
    }

    static EngineNotice Waiting(string name, string detail) => new(NoticeKind.UploadWaiting, name, $@"\OneDrive\{name}", detail);

    [Fact]
    public void A_file_refused_for_its_name_is_not_said_to_be_tried_again_and_its_toast_fits()
    {
        foreach (var culture in new[] { English, Dutch })
        {
            var batcher = new NoticeBatcher();
            batcher.Add(Waiting("Quarterly report 2026.docx", "400 BadRequest from https://x/y: invalidRequest"), Cause.Refused, T0);
            var text = Assert.Single(batcher.Due(T0 + NoticeBatcher.Window, culture)).Text;

            Assert.Equal(UiText.Get(culture, "Waiting.Kept.One", "Quarterly report 2026.docx", PlainCause.Text(Cause.Refused, culture, upload: true)), text);
            Assert.InRange(text.Length, 1, 255); // a toast shows no more
        }
    }

    [Fact]
    public void Notices_within_a_few_seconds_become_one_toast_per_kind()
    {
        var batcher = new NoticeBatcher();
        for (var i = 0; i < 27; i++)
        {
            batcher.Add(Waiting($"file{i}.txt", "No such host is known. (graph.microsoft.com:443)"), Cause.Offline, T0.AddMilliseconds(100 * i));
        }
        batcher.Add(Waiting("other.txt", "429 TooManyRequests"), Cause.Throttled, T0.AddSeconds(3));
        batcher.Add(new EngineNotice(NoticeKind.ConflictCopy, "Offer.docx", @"\Sites\Sales\Offer.docx", "Offer (conflict).docx"), Cause.Other, T0.AddSeconds(3));

        Assert.Empty(batcher.Due(T0.AddSeconds(2), English));
        var toasts = batcher.Due(T0 + NoticeBatcher.Window, English);

        Assert.Equal(2, toasts.Count);
        Assert.Equal(("Saved as a copy", "Offer.docx was changed or locked in Microsoft 365. Your version is saved next to it as Offer (conflict).docx."), toasts[0]);
        Assert.Equal("Waiting to upload", toasts[1].Title);
        Assert.StartsWith("28 files are waiting to upload: offline.", toasts[1].Text);
        Assert.Empty(batcher.Due(T0.AddSeconds(10), English));
    }

    [Fact]
    public void One_file_is_named_and_uploaded_toasts_can_be_muted()
    {
        var batcher = new NoticeBatcher();
        batcher.Add(Waiting("report.docx", "423 Locked"), Cause.Locked, T0);
        Assert.Equal("report.docx is nog niet geüpload: het bestand is vergrendeld of uitgecheckt. VFS365 blijft het proberen; uw wijzigingen blijven op deze pc.",
            Assert.Single(batcher.Due(T0.AddSeconds(5), Dutch)).Text);

        var muted = new NoticeBatcher(notifyUploads: false);
        muted.Add(new EngineNotice(NoticeKind.UploadDone, "report.docx", @"\OneDrive\report.docx", null), Cause.Other, T0);
        Assert.Empty(muted.Due(T0.AddSeconds(5), English));
    }

    [Fact]
    public void A_file_that_waited_and_uploaded_in_the_same_window_only_says_uploaded()
    {
        var batcher = new NoticeBatcher();
        batcher.Add(Waiting("a.txt", "timed out"), Cause.Offline, T0);
        batcher.Add(new EngineNotice(NoticeKind.UploadDone, "a.txt", @"\OneDrive\a.txt", null), Cause.Other, T0.AddSeconds(1));

        Assert.Equal(("Uploaded", "a.txt is saved to Microsoft 365."), Assert.Single(batcher.Due(T0.AddSeconds(5), English)));
    }

    [Fact]
    public void A_slow_trickle_of_notices_is_a_toast_a_minute_not_one_per_file()
    {
        var batcher = new NoticeBatcher();
        var toasts = new List<(string Title, string Text)>();
        for (var second = 0; second < 130; second++)
        {
            if (second % 5 == 0 && second < 120)
            {
                batcher.Add(Waiting($"log{second}.txt", "timed out"), Cause.Offline, T0.AddSeconds(second));
            }
            toasts.AddRange(batcher.Due(T0.AddSeconds(second), English));
        }

        Assert.Equal(3, toasts.Count); // at 4 s, then a minute later with what came meanwhile, and once more
        Assert.Equal("log0.txt isn't uploaded yet: offline. VFS365 keeps trying; your changes are kept on this PC.", toasts[0].Text);
        Assert.StartsWith("12 files are waiting to upload: offline.", toasts[1].Text);
    }

    [Fact]
    public void A_cooldown_of_one_kind_does_not_hold_back_another()
    {
        var batcher = new NoticeBatcher();
        batcher.Add(Waiting("a.txt", "timed out"), Cause.Offline, T0);
        Assert.Single(batcher.Due(T0.AddSeconds(5), English));
        batcher.Add(Waiting("b.txt", "timed out"), Cause.Offline, T0.AddSeconds(10));
        Assert.Empty(batcher.Due(T0.AddSeconds(15), English));

        // A burst of conflict copies while "waiting" cools down is still one toast after its own few seconds
        batcher.Add(new EngineNotice(NoticeKind.ConflictCopy, "Offer.docx", @"\Sites\Sales\Offer.docx", "Offer (conflict).docx"), Cause.Other, T0.AddSeconds(20));
        Assert.Empty(batcher.Due(T0.AddSeconds(20), English));
        batcher.Add(new EngineNotice(NoticeKind.ConflictCopy, "Plan.docx", @"\Sites\Sales\Plan.docx", "Plan (conflict).docx"), Cause.Other, T0.AddSeconds(21));
        Assert.Equal(("Saved as a copy", "2 files were changed or locked in Microsoft 365. Your versions are saved next to them as copies."),
            Assert.Single(batcher.Due(T0.AddSeconds(24), English)));
        Assert.Empty(batcher.Due(T0.AddSeconds(30), English));
        Assert.Equal("b.txt isn't uploaded yet: offline. VFS365 keeps trying; your changes are kept on this PC.",
            Assert.Single(batcher.Due(T0.AddSeconds(5) + NoticeBatcher.Cooldown, English)).Text);
    }

    [Fact]
    public void A_refused_delete_says_why_in_plain_words()
    {
        var batcher = new NoticeBatcher();
        batcher.Add(new EngineNotice(NoticeKind.NotDeleted, "report.docx", @"\Sites\Sales\report.docx", "423 Locked from https://graph.microsoft.com/v1.0/drives/b!x/items/01: resourceLocked"),
            PlainCause.Of("423 Locked from https://graph.microsoft.com/v1.0/drives/b!x/items/01: resourceLocked"), T0);

        var toast = Assert.Single(batcher.Due(T0.AddSeconds(5), Dutch));
        Assert.Equal("report.docx kon niet worden verwijderd in Microsoft 365: het bestand is vergrendeld of uitgecheckt. Het staat weer in de map.", toast.Text);
        Assert.DoesNotContain("graph.microsoft.com", toast.Text);
    }

    [Fact]
    public void Start_problems_known_only_by_their_exception_get_plain_words()
    {
        var mounted = Vfs365.Drive.WinFsp.MountConflictException.For(@"\VFS365\ann", new System.Security.Principal.SecurityIdentifier("S-1-5-21-1-2-3-1001"),
            new System.Security.Principal.SecurityIdentifier("S-1-5-21-1-2-3-1001"), unchecked((int)0x80070050));
        Assert.Equal("uw station draait al, in een andere sessie of een ander programma", StartText.Plain(mounted, Dutch));
        Assert.Equal("Windows could not start the drive; WinFsp may be missing",
            StartText.Plain(new IOException("WinFsp could not mount a free drive letter (NTSTATUS 0xC0000034)."), English));
        Assert.Equal("VFS365 can't read or write its files in your profile", StartText.Plain(new UnauthorizedAccessException(@"Access to C:\Users\ann\AppData\Local\VFS365\cache.key is denied."), English));
        Assert.StartsWith("een fout die VFS365 niet herkent", StartText.Plain(new InvalidOperationException("Sequence contains no elements"), Dutch));
    }

    [Fact]
    public void A_start_waits_for_the_network_or_a_sign_in_by_itself_and_for_retry_otherwise()
    {
        Assert.Equal(StartWait.Network, StartProblem.Of(new HttpRequestException("No such host is known. (graph.microsoft.com:443)")).Wait);
        Assert.Equal(StartWait.Network, StartProblem.Of(new TaskCanceledException("timeout")).Wait);
        Assert.Equal(StartWait.Network, StartProblem.Of(new M365RequestException(System.Net.HttpStatusCode.ServiceUnavailable, new Uri("https://graph.microsoft.com/v1.0/me/drive"), "busy")).Wait);
        Assert.Equal(StartWait.SignIn, StartProblem.Of(new SignInException("AADSTS50173", "AADSTS50173: password changed", true)).Wait);
        Assert.Equal(StartWait.SignIn, StartProblem.Of(new SignInException(SignInException.Unavailable, "WAM isn't ready")).Wait);

        var consent = StartProblem.Of(new SignInException("AADSTS65001", "AADSTS65001: The user or administrator has not consented", true), culture: English);
        Assert.Equal(StartWait.Retry, consent.Wait);
        Assert.Equal("an administrator must approve VFS365 for your organisation first", consent.Reason);
        Assert.Equal(StartWait.Retry, StartProblem.Of(new SignInException(SignInException.Guest, "x belongs to tenant y")).Wait);
        Assert.Equal(StartWait.Retry, StartProblem.Of(new M365RequestException(System.Net.HttpStatusCode.Forbidden, new Uri("https://graph.microsoft.com/v1.0/me/drive"), "no license")).Wait);
        Assert.Equal(StartWait.Retry, StartProblem.Of(new IOException("WinFsp could not mount")).Wait);
    }
}
