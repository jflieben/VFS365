using System.Globalization;
using Microsoft.Identity.Client;
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
        Assert.Equal("&Vernieuwen", UiText.Get(CultureInfo.GetCultureInfo("nl-NL"), "Menu.Refresh"));
        Assert.Equal("&Refresh", UiText.Get(CultureInfo.GetCultureInfo("de-DE"), "Menu.Refresh"));
        Assert.Equal("&Refresh", UiText.Get(CultureInfo.InvariantCulture, "Menu.Refresh"));
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
    public void Errors_get_a_plain_cause(string message, Cause cause) => Assert.Equal(cause, PlainCause.Of(message));

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
        Assert.Equal("multi-factor authentication is needed", SignInText.Of(new MsalUiRequiredException("invalid_grant", "AADSTS50076: MFA"), English));
        Assert.Equal("aanmelden is geannuleerd", SignInText.Of(new MsalClientException(MsalError.AuthenticationCanceledError, "cancelled"), Dutch));
        Assert.Equal("sign-in failed", SignInText.Of(new MsalServiceException("x", "something"), English));
    }

    static EngineNotice Waiting(string name, string detail) => new(NoticeKind.UploadWaiting, name, $@"\OneDrive\{name}", detail);

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
    public void A_start_waits_for_the_network_or_a_sign_in_by_itself_and_for_retry_otherwise()
    {
        Assert.Equal(StartWait.Network, StartProblem.Of(new HttpRequestException("No such host is known. (graph.microsoft.com:443)")).Wait);
        Assert.Equal(StartWait.Network, StartProblem.Of(new TaskCanceledException("timeout")).Wait);
        Assert.Equal(StartWait.Network, StartProblem.Of(new M365RequestException(System.Net.HttpStatusCode.ServiceUnavailable, new Uri("https://graph.microsoft.com/v1.0/me/drive"), "busy")).Wait);
        Assert.Equal(StartWait.SignIn, StartProblem.Of(new MsalUiRequiredException("invalid_grant", "AADSTS50173: password changed")).Wait);
        Assert.Equal(StartWait.SignIn, StartProblem.Of(new MsalClientException("wam_failed", "WAM isn't ready")).Wait);

        var consent = StartProblem.Of(new MsalServiceException("invalid_client", "AADSTS65001: The user or administrator has not consented"), culture: English);
        Assert.Equal(StartWait.Retry, consent.Wait);
        Assert.Equal("an administrator must approve VFS365 for your organisation first", consent.Reason);
        Assert.Equal(StartWait.Retry, StartProblem.Of(new MsalClientException("guest_account", "x belongs to tenant y")).Wait);
        Assert.Equal(StartWait.Retry, StartProblem.Of(new M365RequestException(System.Net.HttpStatusCode.Forbidden, new Uri("https://graph.microsoft.com/v1.0/me/drive"), "no license")).Wait);
        Assert.Equal(StartWait.Retry, StartProblem.Of(new IOException("WinFsp could not mount")).Wait);
    }
}
