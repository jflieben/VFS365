using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;

namespace Vfs365.Agent;

/// <summary>What a double-click on a listed Office file in the drive opens (policy OfficeFiles).</summary>
public enum OfficeFilesMode { DesktopApp, Browser, Local }

/// <summary>A listed type no app on the PC opens (policy OfficeMissing).</summary>
public enum OfficeMissingMode { Browser, Ask, Local }

/// <summary>The Office policies as the agent started with them; <see cref="LogPaths"/> false (MonitoringDetail NoPaths) keeps names out of the log.</summary>
public sealed record OfficeSettings(OfficeFilesMode Mode, IReadOnlyList<string> Types, OfficeMissingMode Missing, bool LogPaths = true)
{
    public static OfficeSettings From(AgentSettings settings) =>
        new(settings.OfficeFiles, settings.OfficeFileTypes, settings.OfficeMissing, settings.MonitoringDetail != MonitoringDetail.NoPaths);
}

/// <summary>The document types OfficeFiles can apply to: their Office app, its program and how it opens a file by URL.</summary>
public static class OfficeTypes
{
    public sealed record OfficeApp(string Name, string Program, string Scheme);

    static readonly OfficeApp Word = new("Word", "WINWORD.EXE", "ms-word"), Excel = new("Excel", "EXCEL.EXE", "ms-excel"),
        PowerPoint = new("PowerPoint", "POWERPNT.EXE", "ms-powerpoint"), OneNote = new("OneNote", "ONENOTE.EXE", "onenote"),
        Visio = new("Visio", "VISIO.EXE", "ms-visio");

    /// <summary>Documents only: templates (their default verb is New), database files and other types are never redirected.</summary>
    public static readonly IReadOnlyDictionary<string, OfficeApp> Supported = new Dictionary<string, OfficeApp>(StringComparer.OrdinalIgnoreCase)
    {
        [".docx"] = Word, [".docm"] = Word, [".doc"] = Word,
        [".xlsx"] = Excel, [".xlsm"] = Excel, [".xlsb"] = Excel, [".xls"] = Excel,
        [".pptx"] = PowerPoint, [".pptm"] = PowerPoint, [".ppt"] = PowerPoint, [".ppsx"] = PowerPoint,
        [".one"] = OneNote,
        [".vsdx"] = Visio, [".vsdm"] = Visio,
    };

    public static readonly IReadOnlyList<string> Defaults = [".docx", ".xlsx", ".pptx"];

    /// <summary>Entries with or without the dot (or "*."), any case, as ".docx", each once; the others in <paramref name="ignored"/>.</summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string> entries, out IReadOnlyList<string> ignored)
    {
        var types = new List<string>();
        var skipped = new List<string>();
        foreach (var entry in entries.Select(e => e.Trim().TrimStart('*')).Where(e => e.Length > 0))
        {
            var type = (entry.StartsWith('.') ? entry : "." + entry).ToLowerInvariant();
            (Supported.ContainsKey(type) ? types : skipped).Add(type);
        }
        ignored = [.. skipped.Distinct()];
        return [.. types.Distinct()];
    }

    public static OfficeApp? AppOf(string extension) => Supported.GetValueOrDefault(extension);

    /// <summary>
    /// The URI that opens a file URL in its desktop app for editing: "ms-word:ofe|u|https://..." (Excel, PowerPoint and Visio alike),
    /// "onenote:https://..." for OneNote. Null for other types.
    /// </summary>
    public static string? DesktopUrl(string extension, string fileUrl) => AppOf(extension) switch
    {
        null => null,
        { Scheme: "onenote" } => $"onenote:{ItemUrls.Encode(fileUrl)}",
        var app => $"{app.Scheme}:ofe|u|{ItemUrls.Encode(fileUrl)}",
    };

    /// <summary>Office's URI schemes take file URLs up to 256 characters, Excel up to 216 ("Office URI Schemes"; unverified). OneNote: no limit known.</summary>
    public static bool TooLong(string extension, string fileUrl) => AppOf(extension) is { } app && app != OneNote
        && ItemUrls.Encode(fileUrl).Length > (app == Excel ? 216 : 256);
}

/// <summary>Who opens a type for this user: its Office app, nothing at all, or something else.</summary>
public enum OfficeHandler { Office, Missing, Other }

public readonly record struct OfficeAssociation(OfficeHandler Handler, string? ProgId);

/// <summary>The effective association of a type, as Explorer resolves it (the user's choice included).</summary>
public static class OfficeAssociations
{
    const uint NoTruncate = 0x20, IgnoreUnknown = 0x400;
    const int Executable = 2, ProgId = 20;

    /// <summary>
    /// Office when the ProgID's "open" verb runs the type's Office program; Missing when no ProgID is associated (Windows would ask);
    /// Other otherwise, also a ProgID whose program can't be read. The verb is passed explicitly: without it the lookup fails (spike 16c).
    /// </summary>
    public static OfficeAssociation Of(string extension)
    {
        if (OfficeTypes.AppOf(extension) is not { } app)
        {
            return new(OfficeHandler.Other, null);
        }
        var progId = Query(ProgId, extension, null);
        if (progId is null || progId.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return new(OfficeHandler.Missing, null);
        }
        var program = Query(Executable, extension, "open");
        return new(program is not null && Path.GetFileName(program).Equals(app.Program, StringComparison.OrdinalIgnoreCase) ? OfficeHandler.Office : OfficeHandler.Other, progId);
    }

    static string? Query(int what, string extension, string? verb)
    {
        var buffer = new char[1024];
        var length = (uint)buffer.Length;
        return AssocQueryStringW(NoTruncate | IgnoreUnknown, what, extension, verb, buffer, ref length) == 0 && length > 1
            ? new string(buffer, 0, (int)length - 1).Trim() is { Length: > 0 } text ? text : null
            : null;
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    static extern int AssocQueryStringW(uint flags, int what, string association, string? extra, [Out] char[] output, ref uint length);
}

/// <summary>What the Office verb does for one listed type: written under <see cref="Key"/>, left alone, or not written (<see cref="Error"/>).</summary>
public sealed record OfficePlan(string Extension, OfficeHandler Handler, string? Key, string? Error = null);

/// <summary>
/// Explorer's default verb for listed Office types on the drive (decision 50), per user: VFS365.Open under the user's Office ProgID
/// (HKCU\Software\Classes\&lt;ProgID&gt;\shell), or under SystemFileAssociations\&lt;ext&gt; when no app is associated with the type.
/// AppliesTo and DefaultAppliesTo limit it to the volume's roots, so double-click and Enter there run "vfs365-agent.exe verb open "%1""
/// while Office's own verbs, and every other location, stay as they are (spike 16b). Nothing for a type another app opens. File
/// associations and values VFS365 didn't write are never changed; keys it created for a verb go again once they are empty.
/// </summary>
public sealed class OfficeVerbs(RegistryKey hive, string exe, OfficeSettings settings, Func<string, OfficeAssociation> associate)
{
    public const string VerbName = "VFS365.Open";
    const string Classes = @"Software\Classes", Associations = Classes + @"\SystemFileAssociations";
    const string Signature = "VFS365Signature", Created = "VFS365Created";
    string? applied;

    /// <summary>What the last <see cref="Update"/> that changed something did per type.</summary>
    public IReadOnlyList<OfficePlan> Plan { get; private set; } = [];

    /// <summary>
    /// Writes the verbs for the volume's <paramref name="roots"/> into the hive when something changed (the roots, the language, an
    /// association, the settings): true then. No roots, or OfficeFiles Local: every verb goes.
    /// </summary>
    public bool Update(IReadOnlyCollection<string> roots, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentUICulture;
        var plan = roots.Count == 0 || settings.Mode == OfficeFilesMode.Local ? [] : Wanted();
        var wanted = plan.Where(p => p.Key is not null).GroupBy(p => p.Key!, StringComparer.OrdinalIgnoreCase).Select(g => g.Key).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var appliesTo = ExplorerVerbs.AppliesTo(roots);
        var signature = $"{exe}|{appliesTo}|{culture.Name}|{settings.Mode}|{settings.Missing}|{string.Join(',', wanted)}";
        if (signature == applied)
        {
            return false;
        }
        var found = Find(hive);
        if (found.Count == wanted.Count && wanted.All(key => found.Contains(key, StringComparer.OrdinalIgnoreCase) && SignatureOf(key) == signature))
        {
            (applied, Plan) = (signature, plan);
            return false;
        }
        Remove(hive, found);
        for (var i = 0; i < plan.Count; i++)
        {
            if (plan[i].Key is { } key && !plan.Take(i).Any(p => key.Equals(p.Key, StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    Write(key, Label(plan[i].Handler), appliesTo, culture, signature);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
                {
                    plan = [.. plan.Select(p => key.Equals(p.Key, StringComparison.OrdinalIgnoreCase) ? p with { Error = e.Message } : p)];
                }
            }
        }
        (applied, Plan) = (signature, plan);
        return true;
    }

    /// <summary>Removes every VFS365 Office verb (a clean stop); true when there were any.</summary>
    public bool Remove()
    {
        var found = Find(hive);
        Remove(hive, found);
        (applied, Plan) = (null, []);
        return found.Count > 0;
    }

    /// <summary>Per listed type: the key its verb goes under, if any.</summary>
    List<OfficePlan> Wanted()
    {
        var plan = new List<OfficePlan>();
        foreach (var extension in settings.Types.Where(OfficeTypes.Supported.ContainsKey))
        {
            var found = associate(extension);
            plan.Add(found.Handler switch
            {
                OfficeHandler.Office when ValidProgId(found.ProgId) => new(extension, OfficeHandler.Office, $@"{Classes}\{found.ProgId}\shell\{VerbName}"),
                OfficeHandler.Office => new(extension, OfficeHandler.Other, null),
                OfficeHandler.Missing when settings.Missing != OfficeMissingMode.Local => new(extension, OfficeHandler.Missing, $@"{Associations}\{extension}\shell\{VerbName}"),
                _ => new(extension, found.Handler, null),
            });
        }
        return plan;
    }

    /// <summary>One key name, not one of Classes' own (a ProgID such as "Applications\x.exe" or "*" is left alone).</summary>
    static bool ValidProgId(string? progId) => progId is { Length: > 0 and < 200 } && progId.IndexOfAny(['\\', '/', '*', '"']) < 0
        && !progId.Equals("SystemFileAssociations", StringComparison.OrdinalIgnoreCase) && !progId.Equals("CLSID", StringComparison.OrdinalIgnoreCase)
        && !progId.Equals("Directory", StringComparison.OrdinalIgnoreCase) && !progId.Equals("Folder", StringComparison.OrdinalIgnoreCase);

    string Label(OfficeHandler handler) => handler == OfficeHandler.Missing
        ? settings.Missing == OfficeMissingMode.Browser ? "Office.OpenBrowser" : "Office.OpenAsk"
        : settings.Mode == OfficeFilesMode.Browser ? "Office.OpenBrowser" : "Office.Open";

    void Write(string key, string label, string appliesTo, CultureInfo culture, string signature)
    {
        var created = FirstMissing(key);
        using var verb = hive.CreateSubKey(key);
        verb.SetValue("MUIVerb", UiText.Get(culture, label));
        verb.SetValue("AppliesTo", appliesTo);
        verb.SetValue("DefaultAppliesTo", appliesTo); // the default verb (double-click, Enter) on the drive only
        verb.SetValue("MultiSelectModel", "Document");
        verb.SetValue("Icon", $"{exe},0");
        if (created is not null)
        {
            verb.SetValue(Created, created);
        }
        using (var command = verb.CreateSubKey("command"))
        {
            command.SetValue("", ExplorerVerbs.Command(exe, OfficeOpen.Verb));
        }
        verb.SetValue(Signature, signature); // last: a verb written halfway is written again
    }

    /// <summary>The highest key above the verb that doesn't exist yet (below Software\Classes), which writing it creates.</summary>
    string? FirstMissing(string key)
    {
        var parts = key.Split('\\');
        for (var depth = 3; depth < parts.Length; depth++) // Software\Classes\<name>
        {
            var path = string.Join('\\', parts[..depth]);
            using var existing = hive.OpenSubKey(path);
            if (existing is null)
            {
                return path;
            }
        }
        return null;
    }

    string? SignatureOf(string key)
    {
        using var verb = hive.OpenSubKey(key);
        return verb?.GetValue(Signature) as string;
    }

    /// <summary>Every VFS365.Open verb in <paramref name="hive"/>: under a ProgID or SystemFileAssociations\&lt;ext&gt;.</summary>
    public static IReadOnlyList<string> Find(RegistryKey hive)
    {
        var found = new List<string>();
        foreach (var root in new[] { Classes, Associations })
        {
            try
            {
                using var parent = hive.OpenSubKey(root);
                foreach (var name in parent?.GetSubKeyNames() ?? [])
                {
                    try
                    {
                        using var verb = parent!.OpenSubKey($@"{name}\shell\{VerbName}");
                        if (verb is not null)
                        {
                            found.Add($@"{root}\{name}\shell\{VerbName}");
                        }
                    }
                    catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException)
                    {
                        // A key the user can't read holds no verb of VFS365's
                    }
                }
            }
            catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException)
            {
            }
        }
        return found;
    }

    public static bool IsRegistered(RegistryKey hive) => Find(hive).Count > 0;

    public static void RemoveAll(RegistryKey hive) => Remove(hive, Find(hive));

    /// <summary>Deletes the verbs, and above each the keys VFS365 created for it (its Created value) while they are empty.</summary>
    static void Remove(RegistryKey hive, IEnumerable<string> keys)
    {
        foreach (var key in keys)
        {
            try
            {
                string? created;
                using (var verb = hive.OpenSubKey(key))
                {
                    created = verb?.GetValue(Created) as string;
                }
                hive.DeleteSubKeyTree(key, false);
                if (created is null || !created.StartsWith(Classes + @"\", StringComparison.OrdinalIgnoreCase) || !key.StartsWith(created + @"\", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                for (var parent = key[..key.LastIndexOf('\\')]; parent.Length >= created.Length; parent = parent[..parent.LastIndexOf('\\')])
                {
                    using (var open = hive.OpenSubKey(parent))
                    {
                        if (open is not null && (open.SubKeyCount > 0 || open.ValueCount > 0))
                        {
                            break;
                        }
                    }
                    hive.DeleteSubKey(parent, false);
                }
            }
            catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException or InvalidOperationException)
            {
                // The others still go; what is left is inert off the drive and the logon cleanup finds it after an uninstall
            }
        }
    }

    /// <summary>For the log: what the verbs do now, per type.</summary>
    public string Summary()
    {
        if (settings.Mode == OfficeFilesMode.Local)
        {
            return "off (policy OfficeFiles Local)";
        }
        string Types(Func<OfficePlan, bool> which) => string.Join(" ", Plan.Where(which).Select(p => p.Extension));
        var parts = new List<string>();
        void Add(string types, string what)
        {
            if (types.Length > 0)
            {
                parts.Add($"{types} {what}");
            }
        }
        Add(Types(p => p is { Handler: OfficeHandler.Office, Error: null }), settings.Mode == OfficeFilesMode.Browser ? "open in the browser" : "open in their desktop app from Microsoft 365");
        Add(Types(p => p is { Handler: OfficeHandler.Missing, Key: not null, Error: null }), $"have no app on this PC: {settings.Missing}");
        Add(Types(p => p is { Handler: OfficeHandler.Missing, Key: null }), "have no app on this PC: Windows asks (OfficeMissing Local)");
        Add(Types(p => p.Handler == OfficeHandler.Other), "open in another default app, left alone");
        foreach (var failed in Plan.Where(p => p.Error is not null))
        {
            parts.Add($"{failed.Extension} could not be written: {failed.Error}");
        }
        return parts.Count == 0 ? "no listed types" : string.Join("; ", parts);
    }
}
