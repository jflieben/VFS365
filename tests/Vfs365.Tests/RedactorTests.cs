using Vfs365.Agent;

namespace Vfs365.Tests;

/// <summary>What leaves the agent's own files (support bundles, the event log) carries no tokens or secrets.</summary>
public sealed class RedactorTests
{
    const string Jwt = "eyJ0eXAiOiJKV1QiLCJhbGciOiJSUzI1NiJ9.eyJhdWQiOiJodHRwczovL2dyYXBoLm1pY3Jvc29mdC5jb20ifQ.c2lnbmF0dXJlLXZhbHVlLTEyMw";

    [Theory]
    [InlineData("token " + Jwt + " done", "eyJ0eXAi", "token")]
    [InlineData("payload eyJhdWQiOiJodHRwczovL2dyYXBoIn0 only", "eyJhdWQi", "payload")]
    [InlineData("Authorization: Bearer abc.def-ghi_1234567890", "abc.def-ghi_1234567890", "Authorization")]
    [InlineData("\"Authorization\": \"Bearer abc.def-ghi_1234567890\"", "1234567890", "Authorization")]
    [InlineData("sent Bearer AbCdEf0123456789xyz to graph", "AbCdEf0123456789xyz", "to graph")]
    [InlineData("Proxy-Authorization: Basic dXNlcjpwYXNzd29yZDEyMw==", "dXNlcjpwYXNzd29yZDEyMw", "Proxy-Authorization")]
    [InlineData("Monitoring: https://acct.table.core.windows.net/?sv=2022-11-02&ss=t&srt=o&sp=a&se=2027-01-01T00:00:00Z&sig=AbC%2Bdef%3D", "AbC%2Bdef", "acct.table.core.windows.net")]
    [InlineData("https://acct.table.core.windows.net/?sv=2022-11-02&ss=t&srt=o&sp=a&se=2027-01-01", "sv=2022", "acct.table.core.windows.net")]
    [InlineData("GET https://acct.blob.core.windows.net/container/file.zip?sp=r&st=2026&sig=BlobSig0123", "BlobSig0123", "file.zip")]
    [InlineData("https://acct.queue.core.windows.net/q/messages?sv=2022&sig=QueueSig0123", "QueueSig0123", "queue.core.windows.net")]
    [InlineData("http://127.0.0.1:10002/devstoreaccount1?sv=2022&ss=t&sig=AzuriteSig0123", "AzuriteSig0123", "devstoreaccount1")]
    [InlineData("download https://contoso.sharepoint.com/sites/x/_layouts/15/download.aspx?UniqueId=abc&Translate=false&tempauth=v1.eyJzaXRlaWQiOiJ4In0.c2ln&ApiVersion=2.0",
        "eyJzaXRlaWQi", "download.aspx")]
    [InlineData("@microsoft.graph.downloadUrl: https://contoso-my.sharepoint.com/personal/a/_layouts/15/download.aspx?UniqueId=1&tempauth=OpaqueTempAuth1234", "OpaqueTempAuth1234", "personal/a")]
    [InlineData("PUT https://contoso.sharepoint.com/_api/v2.0/drives/b!x/items/y/uploadSession?guid='a1'&overwrite=True&tempauth=v1e.SECRETupload123", "SECRETupload123", "uploadSession")]
    [InlineData("{\"access_token\":\"opaqueTokenValue123\",\"token_type\":\"Bearer\"}", "opaqueTokenValue123", "token_type")]
    [InlineData("refresh_token=0.ARefreshTokenValue456&scope=x", "0.ARefreshTokenValue456", "scope=x")]
    [InlineData("\"id_token\": \"abc.def.ghi\"", "abc.def.ghi", "id_token")]
    [InlineData("client_secret=Qw8~abcdEFGH&grant_type=client_credentials", "Qw8~abcdEFGH", "grant_type")]
    [InlineData("password=Hunter2! user=ann", "Hunter2", "user=ann")]
    [InlineData("\"Password\": \"Hunter2!\"", "Hunter2", "Password")]
    [InlineData("DefaultEndpointsProtocol=https;AccountName=acct;AccountKey=bXlTZWNyZXRLZXk=;EndpointSuffix=core.windows.net", "bXlTZWNyZXRLZXk", "AccountName=acct")]
    [InlineData("TableEndpoint=https://acct.table.core.windows.net/;SharedAccessSignature=sv=2022-11-02&ss=t&srt=o&sp=a&sig=SasSig0123", "SasSig0123", "TableEndpoint")]
    [InlineData("SharedAccessSignature=sv=2022-11-02&ss=t&sp=a;Other=1", "sv=2022", "Other=1")]
    [InlineData("callback?code=0.AXoAabcdefghijklmnopqrstuvwxyz123&state=1", "AXoAabcdefghijklmnopqrstuvwxyz123", "state=1")]
    [InlineData("MonitoringKey = 0123456789abcdef0123456789abcdef!", "0123456789abcdef0123456789abcdef", "MonitoringKey")]
    [InlineData("x-api-signature: QUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFB", "QUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFB", "x-api-signature")]
    [InlineData("Cookie: FedAuth=77u/PD94bWwgdmVyc2lvbj0iMS4wIj8; rtFa=abcdef", "77u/PD94bWwg", "Cookie")]
    [InlineData("set FedAuth=77u/PD94bWwgdmVyc2lvbj0iMS4wIj8 now", "77u/PD94bWwg", "now")]
    [InlineData("-----BEGIN PRIVATE KEY-----\nMIIEvQIBADANBgkqhkiG9w0BAQEFAASC\n-----END PRIVATE KEY-----\nafter", "MIIEvQIBADANBg", "after")]
    public void Secrets_are_removed_and_the_rest_stays(string text, string secret, string kept)
    {
        var redacted = Redactor.Redact(text);
        Assert.DoesNotContain(secret, redacted, StringComparison.Ordinal);
        Assert.Contains(kept, redacted, StringComparison.Ordinal);
        Assert.Contains(Redactor.Mark, redacted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("403 Forbidden from https://contoso.sharepoint.com/sites/x/_api/web: accessDenied")]
    [InlineData("Agent:      stopped unexpectedly (exit code 0xC0000005); starting again")]
    [InlineData("{\"error\":{\"code\":\"itemNotFound\",\"message\":\"The resource could not be found.\"}}")]
    [InlineData("Basic authentication is turned off for this tenant")]
    [InlineData("Key: aaaaaaaa-0000-0000-0000-000000000001|bbbbbbbb-0000-0000-0000-000000000002|cccccccc-0000-0000-0000-000000000003")]
    [InlineData("your sign-in expired, for example after a password change")]
    [InlineData("Signed in:  ann@contoso.com (tenant 11111111-2222-3333-4444-555555555555)")]
    [InlineData("GET https://graph.microsoft.com/v1.0/drives/b!abc/root/delta?token=latest")]
    [InlineData("https://contoso.sharepoint.com/sites/Finance/Shared%20Documents/Forms/AllItems.aspx?id=%2Fsites%2FFinance")]
    public void Ordinary_lines_stay_as_they_are(string text) => Assert.Equal(text, Redactor.Redact(text));

    [Fact]
    public void A_seeded_log_keeps_its_lines_without_the_secrets()
    {
        var secrets = new[]
        {
            Jwt, "AbCdEf0123456789xyz", "SasSigSeeded0123", "TempAuthSeeded0123", "opaqueSeededAccess123", "0.ASeededRefresh456", "SeededSecret~789",
            "SeededPassw0rd", "U2VlZGVkQWNjb3VudEtleQ==", "SeededCode0123456789abcdefghij",
        };
        var log = string.Join(Environment.NewLine,
            "2026-10-25 09:00:00 VFS365 is made by JSolve B.V., https://jsolve.nl",
            $"2026-10-25 09:00:01 Signed in:  ann@contoso.com (tenant 11111111-2222-3333-4444-555555555555) token {secrets[0]}",
            $"2026-10-25 09:00:02 request Authorization: Bearer {secrets[1]}",
            $"2026-10-25 09:00:03 Monitoring: https://acct.table.core.windows.net/?sv=2022-11-02&ss=t&srt=o&sp=a&sig={secrets[2]}",
            $"2026-10-25 09:00:04 error: reading \\Sites\\Finance\\a.docx: https://contoso.sharepoint.com/sites/Finance/_layouts/15/download.aspx?UniqueId=1&tempauth={secrets[3]}",
            $"2026-10-25 09:00:05 {{\"access_token\":\"{secrets[4]}\",\"refresh_token\":\"{secrets[5]}\"}}",
            $"2026-10-25 09:00:06 client_secret={secrets[6]} password={secrets[7]}",
            $"2026-10-25 09:00:07 AccountName=acct;AccountKey={secrets[8]};EndpointSuffix=core.windows.net",
            $"2026-10-25 09:00:08 redirect?code={secrets[9]}&state=x",
            "2026-10-25 09:00:09 Unmounted.  Requests: Graph 12, SharePoint 3");
        var redacted = Redactor.Redact(log);
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(secret, redacted, StringComparison.Ordinal);
        }
        Assert.Equal(10, redacted.Split(Environment.NewLine).Length);
        Assert.Contains("Unmounted.  Requests: Graph 12, SharePoint 3", redacted);
        Assert.Contains(@"error: reading \Sites\Finance\a.docx", redacted);
        Assert.Contains("ann@contoso.com", redacted);
    }
}
