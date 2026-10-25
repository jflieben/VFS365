using System.Text.RegularExpressions;

namespace Vfs365.Agent;

/// <summary>
/// Removes tokens and secrets from text that leaves the agent's own files (support bundles, event log entries): JWTs, Authorization
/// and Bearer values, cookies, private keys, the whole query of storage and download URLs and of any URL with a SAS signature, and the
/// values of secret-like names (tempauth, access_token, client_secret, password, AccountKey, SharedAccessSignature and so on), also
/// long opaque values after names that contain token, key, secret and the like.
/// </summary>
public static partial class Redactor
{
    public const string Mark = "[redacted]";

    /// <summary>Names whose value is always a secret, as a URL query parameter, in JSON or as name=value.</summary>
    const string SecretNames = "access_token|refresh_token|id_token|client_secret|client_assertion|assertion|password|passwd|pwd|AccountKey|SharedAccessKey|" +
        "sig|signature|tempauth|apikey|api_key|x-api-key|secret|FedAuth|rtFa|SAMLResponse";

    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }
        text = PrivateKey().Replace(text, Mark);
        text = Url().Replace(text, m => RedactUrl(m.Value));
        text = AuthorizationHeader().Replace(text, m => $"{m.Groups[1].Value}{m.Groups[2].Value}{Mark}");
        text = Cookie().Replace(text, m => $"{m.Groups[1].Value}{m.Groups[2].Value}{Mark}");
        text = Bearer().Replace(text, m => $"{m.Groups[1].Value} {Mark}");
        text = SharedAccessSignature().Replace(text, m => $"{m.Groups[1].Value}{Mark}");
        text = Jwt().Replace(text, Mark);
        text = Named().Replace(text, m => $"{m.Groups[1].Value}{m.Groups[2].Value}{Mark}");
        text = OAuthCode().Replace(text, m => $"{m.Groups[1].Value}{m.Groups[2].Value}{Mark}");
        return KeyLike().Replace(text, m => $"{m.Groups[1].Value}{m.Groups[2].Value}{Mark}");
    }

    /// <summary>
    /// The whole query of storage URLs (blob, table, queue, file, dfs), SharePoint downloads (download.aspx) and URLs with a SAS
    /// signature (sig=); others keep their query, whose secret-like values the later rules remove.
    /// </summary>
    static string RedactUrl(string url)
    {
        var query = url.IndexOf('?');
        if (query < 0)
        {
            return url;
        }
        var head = url[..query];
        var rest = url[(query + 1)..];
        var storage = Uri.TryCreate(head, UriKind.Absolute, out var uri) && uri.Host.EndsWith(".core.windows.net", StringComparison.OrdinalIgnoreCase);
        return storage || head.Contains("download.aspx", StringComparison.OrdinalIgnoreCase) || SasSignature().IsMatch(rest) ? $"{head}?{Mark}" : url;
    }

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----.*?(-----END [A-Z ]*PRIVATE KEY-----|$)", RegexOptions.Singleline)]
    private static partial Regex PrivateKey();

    [GeneratedRegex(@"https?://[^\s""'<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex Url();

    [GeneratedRegex(@"(?:^|[&?])sig=", RegexOptions.IgnoreCase)]
    private static partial Regex SasSignature();

    /// <summary>Authorization: Bearer x, Proxy-Authorization: Basic x, "Authorization": "..."; the scheme goes too.</summary>
    [GeneratedRegex(@"\b((?:Proxy-)?Authorization)([""']?\s*[:=]\s*[""']?)[^""'\r\n]+", RegexOptions.IgnoreCase)]
    private static partial Regex AuthorizationHeader();

    [GeneratedRegex(@"\b(Set-Cookie|Cookie)(\s*:\s*)[^\r\n]+", RegexOptions.IgnoreCase)]
    private static partial Regex Cookie();

    /// <summary>A scheme and a credential with a digit in it, so prose ("Basic authentication") stays.</summary>
    [GeneratedRegex(@"\b(Bearer|Basic|Negotiate|NTLM|PoP)\s+(?=[A-Za-z._~+/=-]*[0-9])[A-Za-z0-9._~+/=-]{12,}", RegexOptions.IgnoreCase)]
    private static partial Regex Bearer();

    /// <summary>A SAS in a connection string runs to the next ';', with its own '=' and '&amp;' in it.</summary>
    [GeneratedRegex(@"\b(SharedAccessSignature\s*=\s*)[^;\s""']+", RegexOptions.IgnoreCase)]
    private static partial Regex SharedAccessSignature();

    /// <summary>Header.payload.signature, base64url; also a header or payload on its own (eyJ is '{"' in base64).</summary>
    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{8,}(?:\.[A-Za-z0-9_-]*){0,2}")]
    private static partial Regex Jwt();

    [GeneratedRegex(@"(?<![A-Za-z0-9_-])(" + SecretNames + @")([""']?\s*[:=]\s*[""']?)[^""'\s&;,<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex Named();

    /// <summary>An OAuth authorization code: code=... or "code": "..." with a long opaque value (not Graph's short error codes).</summary>
    [GeneratedRegex(@"(?<![A-Za-z0-9_-])(code)([""']?\s*[:=]\s*[""']?)[A-Za-z0-9._~+/-]{20,}={0,2}", RegexOptions.IgnoreCase)]
    private static partial Regex OAuthCode();

    /// <summary>A long opaque value (20 or more base64 or hex characters, not a GUID) after a name that holds token, key, secret and the like.</summary>
    [GeneratedRegex(@"(?<![A-Za-z0-9_])([A-Za-z0-9_-]*(?:token|secret|key|password|pwd|signature|credential|assertion|cookie|auth)[A-Za-z0-9_-]*)" +
        @"([""']?\s*[:=]\s*[""']?)(?![0-9a-fA-F]{8}-[0-9a-fA-F]{4}-)[A-Za-z0-9+/_.~-]{20,}={0,2}", RegexOptions.IgnoreCase)]
    private static partial Regex KeyLike();
}
