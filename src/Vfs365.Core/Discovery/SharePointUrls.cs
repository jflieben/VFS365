using System.Text.RegularExpressions;

namespace Vfs365.Core.Discovery;

public static class SharePointUrls
{
    /// <summary>Tenant SharePoint root from a OneDrive URL: https://contoso-my.sharepoint.com/personal/x becomes https://contoso.sharepoint.com.</summary>
    public static string TenantRootFromOneDriveUrl(string oneDriveUrl)
    {
        var uri = new Uri(oneDriveUrl);
        var host = Regex.Replace(uri.Host, @"^([^.]+)-my\.", "$1.", RegexOptions.IgnoreCase);
        return $"{uri.Scheme}://{host}";
    }

    /// <summary>Web URL from a library path as search returns it: drops /Forms/*.aspx and the library folder.</summary>
    public static string? WebUrlFromListPath(string? listPath)
    {
        if (string.IsNullOrWhiteSpace(listPath) || !Uri.TryCreate(listPath, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var path = Regex.Replace(Uri.UnescapeDataString(uri.AbsolutePath), @"/Forms/[^/]+\.aspx$", "", RegexOptions.IgnoreCase);
        var segments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var root = $"{uri.Scheme}://{uri.Host}";
        return segments.Length >= 2 ? $"{root}/{string.Join('/', segments[..^1])}" : root;
    }
}
