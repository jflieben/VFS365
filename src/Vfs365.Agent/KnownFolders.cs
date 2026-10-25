using System.Runtime.InteropServices;

namespace Vfs365.Agent;

/// <summary>Where a known folder is, and moving it: Windows' shell (shell32) for the agent, a fake for tests.</summary>
public interface IKnownFolderStore
{
    /// <summary>The folder's path now, not checked for existence; null when Windows doesn't know the folder.</summary>
    string? Get(Guid id);

    /// <summary>Windows' default path for the folder (in the user's profile).</summary>
    string? Default(Guid id);

    void Set(Guid id, string path);

    /// <summary>Tells Explorer, once after a batch of changes.</summary>
    void Refresh();
}

/// <summary>
/// The user folders Windows lets you move (Properties, Location), by the names Explorer shows, with their IDs. Some have a second ID for
/// "This PC" in Windows 10 and later; both are moved together. Any other known folder by its ID in braces.
/// </summary>
public static class KnownFolders
{
    static readonly Dictionary<string, Guid[]> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Desktop"] = [new("b4bfcc3a-db2c-424c-b029-7fe99a87c641")],
        ["Documents"] = [new("fdd39ad0-238f-46af-adb4-6c85480369c7"), new("f42ee2d3-909f-4907-8871-4c22fc0bf756")],
        ["Downloads"] = [new("374de290-123f-4565-9164-39c4925e467b"), new("7d83ee9b-2244-4e70-b1f5-5393042af1e4")],
        ["Music"] = [new("4bd8d571-6d19-48d3-be97-422220080e43"), new("a0c69a99-21c8-4671-8703-7934162fcf1d")],
        ["Pictures"] = [new("33e28130-4e1e-4676-835a-98395c3bc3bb"), new("0ddd015d-b06c-45d5-8c4c-f59713854639")],
        ["Videos"] = [new("18989b1d-99b5-455b-841c-ab7c74e4ddfc"), new("35286a68-3c57-41a1-bbb1-0eae73d76c95")],
        ["Favorites"] = [new("1777f761-68ad-4d8a-87bd-30b759fa33dd")],
        ["Contacts"] = [new("56784854-c6cb-462b-8169-88e350acb882")],
        ["Links"] = [new("bfb9d5e0-c6a9-404c-b2b2-ae6db6af4968")],
        ["SavedGames"] = [new("4c5c32ff-bb9d-43b0-b5b4-2d72e54eaaa4")],
        ["SavedSearches"] = [new("7d1d3a04-debb-4115-95cf-2f29da2920da")],
    };

    public static IEnumerable<string> Names => ByName.Keys;

    /// <summary>
    /// The folders above as values in HKCU\...\Explorer\User Shell Folders (a legacy name, else the ID in braces), with the ID and
    /// Windows' default below %USERPROFILE%: the uninstall resets values left in VFS365 to these.
    /// </summary>
    public static readonly IReadOnlyList<(string Value, Guid Id, string Default)> ShellFolderValues =
    [
        ("Desktop", ByName["Desktop"][0], "Desktop"),
        ("Personal", ByName["Documents"][0], "Documents"),
        ("{F42EE2D3-909F-4907-8871-4C22FC0BF756}", ByName["Documents"][1], "Documents"),
        ("{374DE290-123F-4565-9164-39C4925E467B}", ByName["Downloads"][0], "Downloads"),
        ("{7D83EE9B-2244-4E70-B1F5-5393042AF1E4}", ByName["Downloads"][1], "Downloads"),
        ("My Music", ByName["Music"][0], "Music"),
        ("{A0C69A99-21C8-4671-8703-7934162FCF1D}", ByName["Music"][1], "Music"),
        ("My Pictures", ByName["Pictures"][0], "Pictures"),
        ("{0DDD015D-B06C-45D5-8C4C-F59713854639}", ByName["Pictures"][1], "Pictures"),
        ("My Video", ByName["Videos"][0], "Videos"),
        ("{35286A68-3C57-41A1-BBB1-0EAE73D76C95}", ByName["Videos"][1], "Videos"),
        ("Favorites", ByName["Favorites"][0], "Favorites"),
        ("{56784854-C6CB-462B-8169-88E350ACB882}", ByName["Contacts"][0], "Contacts"),
        ("{BFB9D5E0-C6A9-404C-B2B2-AE6DB6AF4968}", ByName["Links"][0], "Links"),
        ("{4C5C32FF-BB9D-43B0-B5B4-2D72E54EAAA4}", ByName["SavedGames"][0], "Saved Games"),
        ("{7D1D3A04-DEBB-4115-95CF-2F29DA2920DA}", ByName["SavedSearches"][0], "Searches"),
    ];

    /// <summary>"Documents" (also "My Documents"), or "{GUID}": the IDs to move, the first being the one to read.</summary>
    public static bool TryParse(string text, out string name, out Guid[] ids)
    {
        var trimmed = text.Trim();
        var plain = trimmed.StartsWith("My ", StringComparison.OrdinalIgnoreCase) ? trimmed[3..] : trimmed;
        if (ByName.TryGetValue(plain.Replace(" ", ""), out var known))
        {
            (name, ids) = (ByName.Keys.First(k => k.Equals(plain.Replace(" ", ""), StringComparison.OrdinalIgnoreCase)), known);
            return true;
        }
        if (trimmed.StartsWith('{') && Guid.TryParse(trimmed, out var id))
        {
            (name, ids) = (ByName.FirstOrDefault(k => k.Value.Contains(id)).Key ?? id.ToString("B"), [id]);
            return true;
        }
        (name, ids) = (trimmed, []);
        return false;
    }
}

/// <summary>Known folders through shell32: SHGetKnownFolderPath, SHSetKnownFolderPath (which stores %USERPROFILE% paths as such).</summary>
public sealed class ShellKnownFolders : IKnownFolderStore
{
    const uint DontVerify = 0x4000, DefaultPath = 0x400;

    public string? Get(Guid id) => Path(id, DontVerify);

    public string? Default(Guid id) => Path(id, DontVerify | DefaultPath);

    public void Set(Guid id, string path) => Marshal.ThrowExceptionForHR(SHSetKnownFolderPath(in id, 0, IntPtr.Zero, path));

    public void Refresh() => ExplorerLocation.Refresh();

    static string? Path(Guid id, uint flags)
    {
        if (SHGetKnownFolderPath(in id, flags, IntPtr.Zero, out var path) < 0)
        {
            return null;
        }
        try
        {
            return Marshal.PtrToStringUni(path);
        }
        finally
        {
            Marshal.FreeCoTaskMem(path);
        }
    }

    [DllImport("shell32.dll")]
    static extern int SHGetKnownFolderPath(in Guid id, uint flags, IntPtr token, out IntPtr path);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHSetKnownFolderPath(in Guid id, uint flags, IntPtr token, string path);
}
