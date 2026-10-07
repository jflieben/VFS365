using System.Net;
using System.Text;
using Vfs365.Graph;

namespace Vfs365.Tests;

public sealed class ClaimsChallengeTests
{
    sealed class Tokens : ITokenSource
    {
        public List<string?> Requests { get; } = [];

        public ValueTask<string> GetAccessTokenAsync(string resource, CancellationToken ct)
        {
            Requests.Add(null);
            return ValueTask.FromResult("plain");
        }

        public ValueTask<string> GetAccessTokenAsync(string resource, string claims, CancellationToken ct)
        {
            Requests.Add(claims);
            return ValueTask.FromResult("with-claims");
        }
    }

    sealed class Server(params Func<HttpRequestMessage, HttpResponseMessage>[] answers) : HttpMessageHandler
    {
        int next;
        public List<string?> Tokens { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Tokens.Add(request.Headers.Authorization?.Parameter);
            return Task.FromResult(answers[Math.Min(next++, answers.Length - 1)](request));
        }
    }

    const string Claims = """{"access_token":{"nbf":{"essential":true,"value":"1700000000"}}}""";

    static HttpResponseMessage Challenge()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        response.Headers.TryAddWithoutValidation("WWW-Authenticate",
            $"Bearer realm=\"\", authorization_uri=\"https://login.microsoftonline.com/common/oauth2/authorize\", error=\"insufficient_claims\", claims=\"{Convert.ToBase64String(Encoding.UTF8.GetBytes(Claims))}\"");
        return response;
    }

    static HttpResponseMessage Ok() => new(HttpStatusCode.OK) { Content = new StringContent("""{"value":1}""", Encoding.UTF8, "application/json") };

    [Fact]
    public async Task A_claims_challenge_gets_a_new_token_and_one_retry()
    {
        var server = new Server(_ => Challenge(), _ => Ok());
        var tokens = new Tokens();
        var client = new M365Client(new HttpClient(server), tokens);

        using var doc = await client.GetJsonAsync(new Uri("https://graph.microsoft.com/v1.0/me"), default);

        Assert.Equal(1, doc!.RootElement.GetProperty("value").GetInt32());
        Assert.Equal([null, Claims], tokens.Requests);
        Assert.Equal(["plain", "with-claims"], server.Tokens);
    }

    [Fact]
    public async Task A_second_challenge_or_a_plain_401_fails()
    {
        var client = new M365Client(new HttpClient(new Server(_ => Challenge())), new Tokens());
        var plain = new M365Client(new HttpClient(new Server(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized))), new Tokens());

        await Assert.ThrowsAsync<M365RequestException>(() => client.GetJsonAsync(new Uri("https://graph.microsoft.com/v1.0/me"), default));
        await Assert.ThrowsAsync<M365RequestException>(() => plain.GetJsonAsync(new Uri("https://graph.microsoft.com/v1.0/me"), default));
    }
}
