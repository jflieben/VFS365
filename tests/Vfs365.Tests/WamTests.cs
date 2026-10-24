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
}
