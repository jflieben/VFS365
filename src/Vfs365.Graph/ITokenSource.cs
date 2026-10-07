namespace Vfs365.Graph;

/// <summary>Access tokens per resource, for example https://graph.microsoft.com or https://contoso.sharepoint.com.</summary>
public interface ITokenSource
{
    ValueTask<string> GetAccessTokenAsync(string resource, CancellationToken ct);

    /// <summary>A new token that satisfies a claims challenge (Continuous Access Evaluation) from a 401 response.</summary>
    ValueTask<string> GetAccessTokenAsync(string resource, string claims, CancellationToken ct);
}