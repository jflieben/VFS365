namespace Vfs365.Graph;

/// <summary>Access tokens per resource, for example https://graph.microsoft.com or https://contoso.sharepoint.com.</summary>
public interface ITokenSource
{
    ValueTask<string> GetAccessTokenAsync(string resource, CancellationToken ct);

    /// <summary>A new token that satisfies a claims challenge (Continuous Access Evaluation) from a 401 response.</summary>
    ValueTask<string> GetAccessTokenAsync(string resource, string claims, CancellationToken ct);

    /// <summary>
    /// A token issued now (claims: nbf), asked for although the one in hand still works: that one stays in use until the new one
    /// arrives, and a refusal is not a sign-in problem. Throws when no new token came.
    /// </summary>
    ValueTask<string> RenewAsync(string resource, string claims, CancellationToken ct) => GetAccessTokenAsync(resource, claims, ct);
}