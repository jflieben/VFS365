using System.Text;
using System.Text.Json.Nodes;
using Vfs365.Agent;

namespace Vfs365.Tests;

/// <summary>The parts of the direct WAM sign-in that don't need Windows' broker: interface IDs, claims and token expiry.</summary>
public sealed class WamTests
{
    [Theory]
    [InlineData("pinterface({3c2925fe-8519-45c1-aa79-197b6718c1c1};string;string)", "f6d1f700-49c2-52ae-8154-826f9908773c")] // IMap<String, String>
    [InlineData("pinterface({faa585ea-6214-4217-afda-7f46de5869b3};string)", "e2fcc7c1-3bfc-5a0b-b2b0-72e769d1cb7e")] // IIterable<String>
    public void Parameterized_interface_ids_match_Windows(string signature, string expected) =>
        Assert.Equal(Guid.Parse(expected), Wam.ParameterizedIid(signature));

    [Fact]
    public void Requests_always_ask_for_the_CAE_capability_and_keep_a_challenge()
    {
        var plain = JsonNode.Parse(Wam.Claims(null))!;
        Assert.Equal("cp1", plain["access_token"]!["xms_cc"]!["values"]![0]!.GetValue<string>());

        var merged = JsonNode.Parse(Wam.Claims("""{"access_token":{"nbf":{"essential":true,"value":"1604106651"}}}"""))!;
        Assert.Equal("cp1", merged["access_token"]!["xms_cc"]!["values"]![0]!.GetValue<string>());
        Assert.Equal("1604106651", merged["access_token"]!["nbf"]!["value"]!.GetValue<string>());
    }

    [Fact]
    public void Token_expiry_comes_from_the_exp_claim()
    {
        static string Part(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var jwt = $"{Part("""{"alg":"none"}""")}.{Part("""{"aud":"https://graph.microsoft.com","exp":1792926069}""")}.sig";

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1792926069), Wam.ExpiresOn(jwt));
        Assert.Null(Wam.ExpiresOn("not a token"));
        Assert.Null(Wam.ExpiresOn("a.b!.c"));
    }

    const string Graph = "https://graph.microsoft.com", SharePoint = "https://contoso.sharepoint.com";

    DateTimeOffset now = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);

    WamTokenSource Source(FakeBroker broker, TimeSpan? timeout = null) =>
        new("client", "contoso.com", _ => Task.FromResult<IWamBroker>(broker)) { Clock = () => now, RequestTimeout = timeout ?? TimeSpan.FromSeconds(30) };

    static WamResult Token(string user, string token) =>
        new(WamStatus.Success, token, new WamAccount(IntPtr.Zero, user), new Dictionary<string, string> { ["UPN"] = user, ["OID"] = user, ["TenantId"] = "t1" }, 0, null);

    static WamResult Failed(WamStatus status, string message) => new(status, null, null, new Dictionary<string, string>(), 0x80070057, message);

    [Fact]
    public async Task Requests_for_a_resource_share_one_silent_request()
    {
        var answer = new TaskCompletionSource<WamResult>();
        var broker = new FakeBroker { Respond = (_, _, _) => answer.Task };
        var tokens = Source(broker);

        var waiting = Enumerable.Range(0, 5).Select(_ => tokens.GetAccessTokenAsync(Graph, default).AsTask()).ToList();
        using var cancelled = new CancellationTokenSource();
        var givesUp = tokens.GetAccessTokenAsync(Graph, cancelled.Token).AsTask();
        cancelled.Cancel(); // one caller giving up doesn't end the request for the others
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => givesUp);
        answer.SetResult(Token("ann@contoso.com", "token"));

        Assert.All(await Task.WhenAll(waiting), token => Assert.Equal("token", token));
        Assert.Equal("token", await tokens.GetAccessTokenAsync(Graph, default));
        Assert.Equal(1, broker.Requests);
    }

    [Fact]
    public async Task A_request_WAM_does_not_answer_ends_and_fails_at_once_for_a_while()
    {
        var broker = new FakeBroker
        {
            Respond = async (_, _, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct); // honours the cancellation, as WamClient's wait does
                throw new InvalidOperationException();
            },
        };
        var tokens = Source(broker, TimeSpan.FromMilliseconds(50));

        Assert.Contains("did not answer", (await Assert.ThrowsAsync<HttpRequestException>(() => tokens.GetAccessTokenAsync(Graph, default).AsTask())).Message);
        await Assert.ThrowsAsync<HttpRequestException>(() => tokens.GetAccessTokenAsync(Graph, default).AsTask());
        Assert.Equal(1, broker.Requests);
        Assert.False(tokens.SignInProblem); // the network, not a sign-in

        now += WamTokenSource.NetworkRetry;
        await Assert.ThrowsAsync<HttpRequestException>(() => tokens.GetAccessTokenAsync(Graph, default).AsTask());
        Assert.Equal(2, broker.Requests);
    }

    [Fact]
    public async Task A_failure_no_sign_in_window_solves_is_shown_once_and_tried_again_ever_later()
    {
        var blocked = Failed(WamStatus.ProviderError, "AADSTS53003: Access has been blocked by Conditional Access policies.");
        var broker = new FakeBroker { Respond = (_, _, _) => Task.FromResult(blocked) };
        var tokens = Source(broker);
        var shown = new List<SignInException>();
        tokens.SignInFailed = shown.Add;
        async Task Fails() => await Assert.ThrowsAsync<SignInException>(() => tokens.GetAccessTokenAsync(Graph, default).AsTask());

        await Fails();
        Assert.True(tokens.SignInProblem);
        Assert.False(tokens.SignInNeedsUser); // no window opens by itself for it
        Assert.Equal(SignInText.Of(new SignInException("AADSTS53003", "")), tokens.SignInProblemText); // the tray says Conditional Access
        await Fails();
        Assert.Equal(1, broker.Requests);

        now += WamTokenSource.FailureRetry;
        await Fails();
        Assert.Equal(2, broker.Requests);
        now += WamTokenSource.FailureRetry; // the wait doubled
        await Fails();
        Assert.Equal(2, broker.Requests);
        now += WamTokenSource.FailureRetry;
        await Fails();
        Assert.Equal(3, broker.Requests);
        Assert.Single(shown); // the same problem, told once

        broker.Respond = (_, _, _) => Task.FromResult(Token("ann@contoso.com", "token"));
        now += WamTokenSource.MaxFailureRetry;
        Assert.Equal("token", await tokens.GetAccessTokenAsync(Graph, default));
        Assert.False(tokens.SignInProblem);
    }

    [Fact]
    public async Task A_sign_in_problem_of_one_resource_stays_until_that_resource_has_a_token()
    {
        var broker = new FakeBroker
        {
            Respond = (scope, _, _) => Task.FromResult(scope.StartsWith(SharePoint, StringComparison.Ordinal)
                ? Failed(WamStatus.UserInteractionRequired, "AADSTS50076: multi-factor authentication needed")
                : Token("ann@contoso.com", "graph")),
        };
        var tokens = Source(broker);

        var needed = await Assert.ThrowsAsync<SignInException>(() => tokens.GetAccessTokenAsync(SharePoint, default).AsTask());
        Assert.True(needed.NeedsUser);
        Assert.Equal("graph", await tokens.GetAccessTokenAsync(Graph, default));

        Assert.True(tokens.SignInProblem);
        Assert.True(tokens.SignInNeedsUser);
    }

    [Fact]
    public async Task A_claims_challenge_drops_the_revoked_token()
    {
        var broker = new FakeBroker { Respond = (_, _, _) => Task.FromResult(Token("ann@contoso.com", "revoked")) };
        var tokens = Source(broker);
        Assert.Equal("revoked", await tokens.GetAccessTokenAsync(Graph, default));
        broker.Respond = (_, _, _) => Task.FromResult(Failed(WamStatus.UserInteractionRequired, "AADSTS50173: the grant expired"));

        await Assert.ThrowsAsync<SignInException>(() => tokens.GetAccessTokenAsync(Graph, """{"access_token":{"nbf":{"essential":true}}}""", default).AsTask());

        await Assert.ThrowsAsync<SignInException>(() => tokens.GetAccessTokenAsync(Graph, default).AsTask()); // never the revoked one again
    }

    [Fact]
    public async Task Signing_in_with_another_account_forgets_the_tokens_of_the_first()
    {
        var user = "ann@contoso.com";
        var broker = new FakeBroker
        {
            Respond = (scope, window, _) =>
            {
                if (window != IntPtr.Zero)
                {
                    user = "bob@contoso.com"; // chosen in the sign-in window; silent requests use that account from now on
                }
                return Task.FromResult(user.StartsWith("ann", StringComparison.Ordinal) && scope.StartsWith(SharePoint, StringComparison.Ordinal)
                    ? Failed(WamStatus.UserInteractionRequired, "AADSTS50076: multi-factor authentication needed")
                    : Token(user, $"{user} {scope}"));
            },
        };
        var tokens = Source(broker);
        var switched = new List<(string, string)>();
        tokens.AccountSwitched = (from, to) => switched.Add((from.Username, to.Username));
        Assert.StartsWith("ann", await tokens.GetAccessTokenAsync(Graph, default));
        await Assert.ThrowsAsync<SignInException>(() => tokens.GetAccessTokenAsync(SharePoint, default).AsTask());

        Assert.True(await tokens.SignInAsync(userAsked: true, default)); // the tray's Sign in, where the user picks another account

        Assert.Equal([("ann@contoso.com", "bob@contoso.com")], switched);
        Assert.StartsWith("bob", await tokens.GetAccessTokenAsync(Graph, default)); // ann's token is gone
        Assert.Equal("bob@contoso.com", tokens.LastResult?.Username);
        Assert.False(tokens.SignInProblem);
    }

    [Fact]
    public async Task Sign_out_finds_the_account_also_in_a_new_process()
    {
        var broker = new FakeBroker { Respond = (_, _, _) => Task.FromResult(Token("ann@contoso.com", "token")) };
        var tokens = Source(broker); // vfs365 signout: nothing signed in yet in this process

        await tokens.SignOutAsync();

        Assert.Equal(1, broker.SignOuts);
        Assert.Null(tokens.LastResult);
    }

    sealed class FakeBroker : IWamBroker
    {
        int requests, signOuts;

        public Func<string, IntPtr, CancellationToken, Task<WamResult>> Respond { get; set; } = (_, _, _) => throw new InvalidOperationException();

        public int Requests => Volatile.Read(ref requests);

        public int SignOuts => signOuts;

        public Task<WamResult> RequestAsync(string scope, string clientId, IReadOnlyDictionary<string, string> properties, IntPtr account, IntPtr window,
            CancellationToken ct, bool forcePrompt = false)
        {
            Interlocked.Increment(ref requests);
            return Respond(scope, window, ct);
        }

        public Task SignOutAsync(IntPtr account, string clientId, CancellationToken ct)
        {
            signOuts++;
            return Task.CompletedTask;
        }
    }
}
