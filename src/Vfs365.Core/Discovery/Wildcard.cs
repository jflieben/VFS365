using System.Text.RegularExpressions;

namespace Vfs365.Core.Discovery;

/// <summary>PowerShell -like matching: anchored at both ends, case-insensitive, * is any run of characters, ? one character.</summary>
public static class Wildcard
{
    public static bool IsMatch(string input, string pattern) =>
        Regex.IsMatch(input, "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    public static bool MatchesAny(string input, IEnumerable<string> patterns) => patterns.Any(pattern => IsMatch(input, pattern));
}
