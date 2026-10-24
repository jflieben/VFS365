using System.Text.RegularExpressions;
using Vfs365.Core.Discovery;

namespace Vfs365.Core.Drive;

/// <summary>
/// Folders in the volume named by an administrator's template (folder redirection, drive letters for a library): %NAME% placeholders
/// from the given values, else the environment; * and ? in a folder name pick the first matching folder by name; missing folders inside
/// a library can be created.
/// </summary>
public static partial class FolderTargets
{
    /// <summary>
    /// <paramref name="template"/> with %NAME% replaced: <paramref name="values"/> first (case-insensitive), then environment variables.
    /// Null when a placeholder has no value (an unknown name, or a value not known yet such as the signed-in account).
    /// </summary>
    public static string? Expand(string template, IReadOnlyDictionary<string, string?> values)
    {
        var missing = false;
        var expanded = Placeholder().Replace(template, match =>
        {
            var name = match.Groups[1].Value;
            var value = values.FirstOrDefault(v => v.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value ?? Environment.GetEnvironmentVariable(name);
            missing |= string.IsNullOrEmpty(value);
            return value ?? "";
        });
        return missing ? null : expanded;
    }

    /// <summary>
    /// The volume path ("\OneDrive\Desktop") of <paramref name="relative"/> ('\' or '/' separated, from the volume root), or why not:
    /// a folder that doesn't exist (and isn't created), no folder matching a wildcard, a file in the way, a folder that can't be made.
    /// With <paramref name="create"/>, missing folders are made inside a library (never sites or libraries themselves).
    /// </summary>
    public static async Task<(string? Path, string? Problem)> ResolveAsync(DriveEngine engine, string relative, bool create, CancellationToken ct)
    {
        var segments = relative.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Any(s => s is "." or ".."))
        {
            return (null, $"'{relative}' may not contain . or .. folders");
        }
        var path = "";
        try
        {
            foreach (var segment in segments)
            {
                if (segment.AsSpan().IndexOfAny('*', '?') >= 0)
                {
                    var parent = await engine.GetEntryAsync(Shown(path), ct);
                    var match = parent is { IsDirectory: true }
                        ? (await engine.ListAsync(parent, ct)).Where(e => e.IsDirectory && Wildcard.IsMatch(e.Name, segment))
                            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
                        : null;
                    if (match is null)
                    {
                        return (null, $"no folder in {Shown(path)} matches '{segment}'");
                    }
                    path += $@"\{match.Name}";
                    continue;
                }

                var next = $@"{path}\{segment}";
                var entry = await engine.GetEntryAsync(next, ct);
                if (entry is null)
                {
                    if (!create)
                    {
                        return (null, $"{next} doesn't exist");
                    }
                    if (await engine.GetEntryAsync(Shown(path), ct) is not { Drive: not null })
                    {
                        return (null, $"{next} doesn't exist (folders are made only inside a library)");
                    }
                    entry = await engine.CreateAsync(next, isDirectory: true, ct);
                }
                else if (!entry.IsDirectory)
                {
                    return (null, $"{next} is a file");
                }
                path = next;
            }
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            return (null, $"{Shown(path)}: {e.Message}");
        }
        return (Shown(path), null);
    }

    static string Shown(string path) => path.Length == 0 ? @"\" : path;

    [GeneratedRegex("%([A-Za-z0-9_()]+)%")]
    private static partial Regex Placeholder();
}
