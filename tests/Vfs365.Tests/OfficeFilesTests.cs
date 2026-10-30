using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Win32;
using Vfs365.Agent;
using Vfs365.Core;
using Vfs365.Core.Discovery;
using Vfs365.Core.Drive;
using Vfs365.Graph;
using Vfs365.Tests.Drive;

namespace Vfs365.Tests;

/// <summary>
/// Office files from Microsoft 365 (decision 50): the policies, the Explorer verb (in a key of its own, HKCU\Software\VFS365-tests\...),
/// what the agent opens and why, and the client that opens the copy on the drive itself when no agent takes the request.
/// </summary>
public sealed class OfficeFilesTests : IDisposable
{
    static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-GB"), Dutch = CultureInfo.GetCultureInfo("nl-NL");
    const string Unc = @"\\VFS365\ann", Exe = @"C:\Program Files\VFS365\vfs365-agent.exe";
    const string WordVerb = @"Software\Classes\Word.Document.12\shell\VFS365.Open", ExcelVerb = @"Software\Classes\Excel.Sheet.12\shell\VFS365.Open";
    const string PowerPointVerb = @"Software\Classes\PowerPoint.Show.12\shell\VFS365.Open", VisioVerb = @"Software\Classes\SystemFileAssociations\.vsdx\shell\VFS365.Open";
    static readonly string[] Roots = [@"M:\", @"\\VFS365\ann\"];

    readonly string keyPath = $@"Software\VFS365-tests\{Guid.NewGuid():N}";
    readonly RegistryKey hive;
    readonly string directory = Path.Combine(Path.GetTempPath(), "vfs365-tests", Guid.NewGuid().ToString("N"));
    readonly FakeDriveApi api = new();

    public OfficeFilesTests() => hive = Registry.CurrentUser.CreateSubKey(keyPath);

    static OfficeSettings Settings(OfficeFilesMode mode = OfficeFilesMode.DesktopApp, OfficeMissingMode missing = OfficeMissingMode.Browser, params string[] types) =>
        new(mode, types.Length == 0 ? OfficeTypes.Defaults : types, missing);

    /// <summary>Word, Excel and PowerPoint as Office installs them; other types as given, else no app.</summary>
    static Func<string, OfficeAssociation> Associations(params (string Extension, OfficeHandler Handler, string? ProgId)[] others)
    {
        var known = new Dictionary<string, OfficeAssociation>(StringComparer.OrdinalIgnoreCase)
        {
            [".docx"] = new(OfficeHandler.Office, "Word.Document.12"),
            [".xlsx"] = new(OfficeHandler.Office, "Excel.Sheet.12"),
            [".pptx"] = new(OfficeHandler.Office, "PowerPoint.Show.12"),
        };
        foreach (var (extension, handler, progId) in others)
        {
            known[extension] = new(handler, progId);
        }
        return extension => known.GetValueOrDefault(extension, new(OfficeHandler.Missing, null));
    }

    static AgentSettings Policy(params (string Name, object Value)[] values) =>
        AgentSettings.FromValues(AgentSettings.ComputerPolicy, values.ToDictionary(v => v.Name, v => v.Value));

    // Settings

    [Fact]
    public void By_default_word_excel_and_powerpoint_open_in_their_desktop_app_and_types_without_an_app_in_the_browser()
    {
        var settings = Policy();
        Assert.Equal(OfficeFilesMode.DesktopApp, settings.OfficeFiles);
        Assert.Equal([".docx", ".xlsx", ".pptx"], settings.OfficeFileTypes);
        Assert.Equal(OfficeMissingMode.Browser, settings.OfficeMissing);
        Assert.Empty(settings.Adjusted());
        var effective = settings.Effective();
        Assert.Equal(("DesktopApp", AgentSettings.Default), effective.Where(s => s.Name == "OfficeFiles").Select(s => (s.Value, s.Source)).Single());
        Assert.Equal(".docx; .xlsx; .pptx", effective.Single(s => s.Name == "OfficeFileTypes").Value);
        Assert.Equal("Browser", effective.Single(s => s.Name == "OfficeMissing").Value);
        Assert.Contains("OfficeFiles", AgentSettings.Names);
        Assert.Contains("OfficeFileTypes", AgentSettings.Names);
        Assert.Contains("OfficeMissing", AgentSettings.Names);
    }

    [Fact]
    public void Types_are_normalized_and_templates_databases_and_other_types_ignored()
    {
        var settings = Policy(("OfficeFiles", " browser "), ("OfficeMissing", "ASK"),
            ("OfficeFileTypes", new[] { "docx", ".XLSM", "*.one", ".dotx", "accdb", ".txt", ".docx", " " }));

        Assert.Equal(OfficeFilesMode.Browser, settings.OfficeFiles);
        Assert.Equal(OfficeMissingMode.Ask, settings.OfficeMissing);
        Assert.Equal([".docx", ".xlsm", ".one"], settings.OfficeFileTypes);
        Assert.Equal("OfficeFileTypes '.dotx', '.accdb', '.txt' ignored: not a Word, Excel, PowerPoint, OneNote or Visio document type", Assert.Single(settings.Adjusted()));

        Assert.Equal([".pptx", ".vsdx"], Policy(("OfficeFileTypes", "pptx; VSDX")).OfficeFileTypes); // REG_SZ, as lists are
        var none = Policy(("OfficeFileTypes", new[] { ".dotx", ".potx" }));
        Assert.Empty(none.OfficeFileTypes);
        Assert.EndsWith("no type is left, so Office files open from the drive", Assert.Single(none.Adjusted()));
        Assert.Equal("none", none.Effective().Single(s => s.Name == "OfficeFileTypes").Value);
    }

    [Theory]
    [InlineData("Coauthor")]
    [InlineData("1")]
    [InlineData("DesktopApp, Browser")]
    public void Choices_take_only_their_names(string value)
    {
        var settings = Policy(("OfficeFiles", value), ("OfficeMissing", value));
        Assert.Equal((OfficeFilesMode.DesktopApp, OfficeMissingMode.Browser), (settings.OfficeFiles, settings.OfficeMissing));
        Assert.Contains($"OfficeFiles '{value}' is not DesktopApp, Browser or Local; DesktopApp is used", settings.Adjusted());
        Assert.Contains($"OfficeMissing '{value}' is not Browser, Ask or Local; Browser is used", settings.Adjusted());
        Assert.Equal(OfficeFilesMode.Local, Policy(("OfficeFiles", "local")).OfficeFiles);
        Assert.Equal(["OfficeFiles"], Policy(("OfficeFiles", "Local")).ChangedFrom(Policy()));
    }

    [Fact]
    public void Urls_per_type()
    {
        const string file = "https://contoso.sharepoint.com/sites/Sales/Shared Documents/Q4 plan";
        Assert.Equal("ms-word:ofe|u|https://contoso.sharepoint.com/sites/Sales/Shared%20Documents/Q4%20plan.docm", OfficeTypes.DesktopUrl(".docm", file + ".docm"));
        Assert.Equal("ms-excel:ofe|u|https://contoso.sharepoint.com/sites/Sales/Shared%20Documents/Q4%20plan.xlsb", OfficeTypes.DesktopUrl(".xlsb", file + ".xlsb"));
        Assert.Equal("ms-powerpoint:ofe|u|https://contoso.sharepoint.com/sites/Sales/Shared%20Documents/Q4%20plan.ppsx", OfficeTypes.DesktopUrl(".ppsx", file + ".ppsx"));
        Assert.Equal("onenote:https://contoso.sharepoint.com/sites/Sales/Shared%20Documents/Q4%20plan.one", OfficeTypes.DesktopUrl(".one", file + ".one"));
        Assert.Equal("ms-visio:ofe|u|https://contoso.sharepoint.com/sites/Sales/Shared%20Documents/Q4%20plan.vsdx", OfficeTypes.DesktopUrl(".vsdx", file + ".vsdx"));
        Assert.Null(OfficeTypes.DesktopUrl(".dotx", file + ".dotx"));

        var deep = "https://contoso.sharepoint.com/sites/Sales/Shared Documents/" + new string('a', 150) + "/x";
        Assert.False(OfficeTypes.TooLong(".xlsx", deep[..200] + ".xlsx"));
        Assert.True(OfficeTypes.TooLong(".xlsx", deep + ".xlsx")); // Excel: 216
        Assert.False(OfficeTypes.TooLong(".docx", deep + ".docx")); // the others: 256
        Assert.True(OfficeTypes.TooLong(".docx", deep + new string('b', 60) + ".docx"));
        Assert.False(OfficeTypes.TooLong(".one", deep + new string('b', 300) + ".one"));
    }

    [Fact]
    public void Associations_of_this_pc_are_read_without_changing_them()
    {
        Assert.Equal(OfficeHandler.Other, OfficeAssociations.Of(".txt").Handler); // not an Office type: never asked
        Assert.Equal(OfficeHandler.Other, OfficeAssociations.Of(".dotx").Handler);
        Assert.True(Enum.IsDefined(OfficeAssociations.Of(".docx").Handler)); // whatever this PC has
    }

    // The verb

    string? Value(string key, string name = "")
    {
        using var opened = hive.OpenSubKey(key);
        return opened?.GetValue(name) as string;
    }

    bool Exists(string key)
    {
        using var opened = hive.OpenSubKey(key);
        return opened is not null;
    }

    [Fact]
    public void Office_types_get_a_default_verb_on_the_drive_written_only_when_something_changed()
    {
        var verbs = new OfficeVerbs(hive, Exe, Settings(), Associations());
        Assert.True(verbs.Update(Roots, English));

        var appliesTo = ExplorerVerbs.AppliesTo(Roots);
        foreach (var key in new[] { WordVerb, ExcelVerb, PowerPointVerb })
        {
            Assert.Equal("Open from Microsoft 365", Value(key, "MUIVerb"));
            Assert.Equal(appliesTo, Value(key, "AppliesTo"));
            Assert.Equal(appliesTo, Value(key, "DefaultAppliesTo"));
            Assert.Equal("Document", Value(key, "MultiSelectModel"));
            Assert.Equal($"{Exe},0", Value(key, "Icon"));
            Assert.Equal($@"""{Exe}"" verb open ""%1""", Value(key + @"\command"));
        }
        Assert.Equal(@"Software\Classes\Word.Document.12", Value(WordVerb, "VFS365Created"));
        Assert.Equal(3, OfficeVerbs.Find(hive).Count);
        Assert.True(ExplorerLocation.IsRegistered(hive));
        Assert.Contains(".docx .xlsx .pptx open in their desktop app from Microsoft 365", verbs.Summary());

        Assert.False(verbs.Update(Roots, English)); // nothing changed
        using (var word = hive.OpenSubKey(WordVerb, writable: true)!)
        {
            word.SetValue("Probe", "kept");
        }
        Assert.False(new OfficeVerbs(hive, Exe, Settings(), Associations()).Update(Roots, English)); // a restart: the registry has it
        Assert.Equal("kept", Value(WordVerb, "Probe"));

        Assert.True(verbs.Update([@"\\VFS365\ann\"], English)); // the letter went
        Assert.Equal(ExplorerVerbs.AppliesTo([@"\\VFS365\ann\"]), Value(ExcelVerb, "DefaultAppliesTo"));
        Assert.True(verbs.Update([@"\\VFS365\ann\"], Dutch));
        Assert.Equal("Openen vanuit Microsoft 365", Value(ExcelVerb, "MUIVerb"));

        Assert.True(verbs.Update([], English)); // no roots: removed
        Assert.Empty(OfficeVerbs.Find(hive));
        Assert.False(Exists(@"Software\Classes\Word.Document.12")); // made by VFS365 and empty again
        Assert.False(ExplorerLocation.IsRegistered(hive));
    }

    [Fact]
    public void Browser_mode_names_the_browser()
    {
        Assert.True(new OfficeVerbs(hive, Exe, Settings(OfficeFilesMode.Browser), Associations()).Update(Roots, English));
        Assert.Equal("Open in the browser", Value(WordVerb, "MUIVerb"));
    }

    [Theory]
    [InlineData(OfficeMissingMode.Browser, "Open in the browser")]
    [InlineData(OfficeMissingMode.Ask, "Open")]
    [InlineData(OfficeMissingMode.Local, null)]
    public void A_type_without_an_app_gets_the_verb_under_SystemFileAssociations(OfficeMissingMode missing, string? label)
    {
        var verbs = new OfficeVerbs(hive, Exe, Settings(OfficeFilesMode.DesktopApp, missing, ".docx", ".vsdx"), Associations());
        Assert.True(verbs.Update(Roots, English));

        Assert.Equal(label, Value(VisioVerb, "MUIVerb"));
        Assert.Equal(label is null ? null : $@"""{Exe}"" verb open ""%1""", Value(VisioVerb + @"\command"));
        Assert.Equal(label is null ? null : @"Software\Classes\SystemFileAssociations", Value(VisioVerb, "VFS365Created"));
        Assert.Equal("Open from Microsoft 365", Value(WordVerb, "MUIVerb"));
        Assert.Equal(OfficeHandler.Missing, verbs.Plan.Single(p => p.Extension == ".vsdx").Handler);

        verbs.Remove();
        Assert.False(Exists(@"Software\Classes\SystemFileAssociations"));
    }

    [Fact]
    public void Another_default_app_and_odd_progids_are_left_alone()
    {
        var verbs = new OfficeVerbs(hive, Exe, Settings(OfficeFilesMode.DesktopApp, OfficeMissingMode.Browser, ".docx", ".xlsx", ".pptx"),
            Associations((".docx", OfficeHandler.Other, "LibreOffice.WriterDocument.1"), (".xlsx", OfficeHandler.Office, @"Applications\excel.exe")));
        Assert.True(verbs.Update(Roots, English));

        Assert.Equal([PowerPointVerb], OfficeVerbs.Find(hive));
        Assert.False(Exists(@"Software\Classes\LibreOffice.WriterDocument.1"));
        Assert.False(Exists(@"Software\Classes\Applications"));
        Assert.Contains(".docx .xlsx open in another default app, left alone", verbs.Summary());
    }

    [Fact]
    public void Local_mode_and_a_changed_association_remove_what_was_written()
    {
        Assert.True(new OfficeVerbs(hive, Exe, Settings(), Associations()).Update(Roots, English));

        // The user picked LibreOffice for .docx: the verb under Word's ProgID goes, the others stay
        var changed = new OfficeVerbs(hive, Exe, Settings(), Associations((".docx", OfficeHandler.Other, "LibreOffice.WriterDocument.1")));
        Assert.True(changed.Update(Roots, English));
        Assert.Equal([ExcelVerb, PowerPointVerb], OfficeVerbs.Find(hive).Order());
        Assert.False(Exists(@"Software\Classes\Word.Document.12"));

        var local = new OfficeVerbs(hive, Exe, Settings(OfficeFilesMode.Local), Associations());
        Assert.True(local.Update(Roots, English));
        Assert.Empty(OfficeVerbs.Find(hive));
        Assert.Equal("off (policy OfficeFiles Local)", local.Summary());
        Assert.False(local.Update(Roots, English));
    }

    [Fact]
    public void Removal_leaves_keys_and_values_vfs365_did_not_make()
    {
        using (var excel = hive.CreateSubKey(@"Software\Classes\Excel.Sheet.12"))
        {
            excel.SetValue("", "Microsoft Excel Worksheet");
            using var open = excel.CreateSubKey(@"shell\Open\command");
            open.SetValue("", @"""C:\Office\EXCEL.EXE"" ""%1""");
        }
        using (hive.CreateSubKey(@"Software\Classes\PowerPoint.Show.12\shell"))
        {
        }
        var verbs = new OfficeVerbs(hive, Exe, Settings(), Associations());
        Assert.True(verbs.Update(Roots, English));
        Assert.Null(Value(ExcelVerb, "VFS365Created"));
        Assert.Null(Value(PowerPointVerb, "VFS365Created"));
        using (hive.CreateSubKey(@"Software\Classes\Word.Document.12\shell\Other")) // something else wrote there since
        {
        }

        Assert.True(verbs.Remove());

        Assert.Empty(OfficeVerbs.Find(hive));
        Assert.Equal("Microsoft Excel Worksheet", Value(@"Software\Classes\Excel.Sheet.12"));
        Assert.Equal(@"""C:\Office\EXCEL.EXE"" ""%1""", Value(@"Software\Classes\Excel.Sheet.12\shell\Open\command"));
        Assert.True(Exists(@"Software\Classes\PowerPoint.Show.12\shell")); // there before: stays, empty or not
        Assert.True(Exists(@"Software\Classes\Word.Document.12\shell\Other"));
        Assert.False(verbs.Remove());
    }

    [Fact]
    public void The_logon_cleanup_removes_the_verbs_with_built_in_tools()
    {
        var command = MachineCleanup.LogonCleanupCommand(["M:"]);
        Assert.Contains(@"query ""HKCU\Software\Classes"" /s /k /f VFS365.Open /e", command, StringComparison.Ordinal);
        Assert.Contains(@"/c:""\shell\VFS365.Open""') do """, command, StringComparison.Ordinal);
        Assert.InRange(command.Length, 1, 8000);

        Assert.True(new OfficeVerbs(hive, Exe, Settings(OfficeFilesMode.DesktopApp, OfficeMissingMode.Browser, ".docx", ".vsdx"), Associations()).Update(Roots, English));
        using (hive.CreateSubKey(@"Software\Classes\Word.Document.12\shell\Open\command"))
        {
        }
        using (hive.CreateSubKey(@"Software\Classes\Decoy\VFS365.Open")) // not a verb
        {
        }
        var test = MachineCleanup.LogonCleanupCommand(["M:"], $@"HKCU\{keyPath}");
        var arguments = test[(test.IndexOf("cmd.exe\" ", StringComparison.Ordinal) + "cmd.exe\" ".Length)..];
        using (var cmd = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!)
        {
            cmd.StandardOutput.ReadToEnd();
            Assert.True(cmd.WaitForExit(60_000));
        }

        Assert.Empty(OfficeVerbs.Find(hive));
        Assert.True(Exists(@"Software\Classes\Word.Document.12\shell\Open\command"));
        Assert.True(Exists(@"Software\Classes\Decoy\VFS365.Open"));
    }

    // What the agent opens

    sealed class Seen
    {
        public readonly List<string> Urls = [], Logs = [];
        public readonly List<(string Path, bool ChooseApp)> Locals = [];
        public readonly List<(string App, string Name)> Asked = [];
        public int LinkCalls;
    }

    static readonly ItemLinks Links = new("https://x-my.sharepoint.com/personal/u/_layouts/15/Doc.aspx?sourcedoc=%7B1%7D",
        "https://x-my.sharepoint.com/personal/u/Documents/Q4 plan.xlsx", "https://x-my.sharepoint.com/personal/u", "{list}", "12");

    DriveEngine Engine() =>
        new(DriveNamespace.Build(new MyDrive("b!me", "https://x-my.sharepoint.com/personal/u"), [], (_, _) => Task.FromResult("unused")),
            api, new ContentCache(Path.Combine(directory, "cache"), api), new DriveEngineOptions { StagingDirectory = Path.Combine(directory, "staging") });

    static (OfficeOpen Opener, Seen Seen) Opener(DriveEngine engine, OfficeSettings settings, Func<string, OfficeAssociation>? associate = null,
        Func<string, string, CancellationToken, Task<ItemLinks?>>? links = null, Func<MissingChoice>? answer = null, Action<string>? openUrl = null,
        TimeSpan? cancelHolds = null)
    {
        var seen = new Seen();
        var opener = new OfficeOpen(engine,
            links is null
                ? (_, _, _) =>
                {
                    Interlocked.Increment(ref seen.LinkCalls);
                    return Task.FromResult<ItemLinks?>(Links);
                }
                : (drive, item, ct) =>
                {
                    Interlocked.Increment(ref seen.LinkCalls);
                    return links(drive, item, ct);
                },
            path => ExplorerVerbs.VolumePath(path, Unc, "M:", new Dictionary<string, string>()),
            settings, associate ?? Associations(), openUrl ?? new Action<string>(seen.Urls.Add), (path, chooseApp) => seen.Locals.Add((path, chooseApp)),
            (app, name, _) =>
            {
                seen.Asked.Add((app, name));
                return Task.FromResult(answer?.Invoke() ?? MissingChoice.Browser);
            },
            seen.Logs.Add)
        { Budget = TimeSpan.FromMilliseconds(300), CancelHolds = cancelHolds ?? TimeSpan.FromSeconds(5) };
        return (opener, seen);
    }

    [Fact]
    public async Task An_uploaded_unchanged_file_opens_in_its_desktop_app_from_microsoft_365()
    {
        api.Add("Q4 plan.xlsx");
        var (opener, seen) = Opener(Engine(), Settings());

        var outcome = await opener.RunAsync(@"M:\OneDrive\Q4 plan.xlsx", default);

        Assert.Equal(OfficeRoute.DesktopApp, outcome.Route);
        Assert.Equal(["ms-excel:ofe|u|https://x-my.sharepoint.com/personal/u/Documents/Q4%20plan.xlsx"], seen.Urls);
        Assert.Empty(seen.Locals);
        Assert.StartsWith(@"Office open: \OneDrive\Q4 plan.xlsx: in the desktop app from Microsoft 365", Assert.Single(seen.Logs));
    }

    [Fact]
    public async Task Browser_mode_shared_and_read_only_files_open_by_url_too()
    {
        api.Add("Q4 plan.xlsx");
        var (opener, seen) = Opener(Engine(), Settings(OfficeFilesMode.Browser));

        Assert.Equal(OfficeRoute.Browser, (await opener.RunAsync(@"\\VFS365\ann\OneDrive\Q4 plan.xlsx", default)).Route);
        Assert.Equal([Links.WebUrl!], seen.Urls);
    }

    [Fact]
    public async Task OneNote_and_Visio_open_through_their_own_schemes()
    {
        api.Add("Notes.one");
        api.Add("Plan.vsdx");
        var associate = Associations((".one", OfficeHandler.Office, "OneNote.Section.1"), (".vsdx", OfficeHandler.Office, "Visio.Drawing.15"));
        var (opener, seen) = Opener(Engine(), Settings(OfficeFilesMode.DesktopApp, OfficeMissingMode.Browser, ".one", ".vsdx"), associate,
            (_, item, _) => Task.FromResult<ItemLinks?>(Links with { WebDavUrl = $"https://x-my.sharepoint.com/personal/u/Documents/{api.Paths.Single(p => api.ItemAt(p)!.Id == item)}" }));

        await opener.RunAsync(@"M:\OneDrive\Notes.one", default);
        await opener.RunAsync(@"M:\OneDrive\Plan.vsdx", default);

        Assert.Equal(["onenote:https://x-my.sharepoint.com/personal/u/Documents/Notes.one", "ms-visio:ofe|u|https://x-my.sharepoint.com/personal/u/Documents/Plan.vsdx"], seen.Urls);
    }

    [Fact]
    public async Task A_type_without_an_app_opens_in_the_browser_or_asks_once_per_type()
    {
        api.Add("Plan.vsdx");
        api.Add("Other.vsdx");
        var settings = Settings(OfficeFilesMode.DesktopApp, OfficeMissingMode.Browser, ".vsdx");
        var (browser, seenBrowser) = Opener(Engine(), settings);
        Assert.Equal(OfficeRoute.Browser, (await browser.RunAsync(@"M:\OneDrive\Plan.vsdx", default)).Route);
        Assert.Equal([Links.WebUrl!], seenBrowser.Urls);

        var answers = new Queue<MissingChoice>([MissingChoice.Cancel, MissingChoice.Cancel, MissingChoice.Browser]);
        var (ask, seen) = Opener(Engine(), settings with { Missing = OfficeMissingMode.Ask }, answer: answers.Dequeue);
        Assert.Equal(OfficeRoute.Cancelled, (await ask.RunAsync(@"M:\OneDrive\Plan.vsdx", default)).Route);
        Assert.Equal(OfficeRoute.Cancelled, (await ask.RunAsync(@"M:\OneDrive\Other.vsdx", default)).Route); // selected with it: no second question
        Assert.Single(seen.Asked);
        Assert.Empty(seen.Urls);
        Assert.Empty(seen.Locals);

        var (later, seenLater) = Opener(Engine(), settings with { Missing = OfficeMissingMode.Ask }, answer: answers.Dequeue, cancelHolds: TimeSpan.Zero);
        Assert.Equal(OfficeRoute.Cancelled, (await later.RunAsync(@"M:\OneDrive\Plan.vsdx", default)).Route);
        Assert.Equal(OfficeRoute.Browser, (await later.RunAsync(@"M:\OneDrive\Plan.vsdx", default)).Route); // asked again after a cancel
        Assert.Equal(OfficeRoute.Browser, (await later.RunAsync(@"M:\OneDrive\Other.vsdx", default)).Route); // answered for .vsdx
        Assert.Equal([("Visio", "Plan.vsdx"), ("Visio", "Plan.vsdx")], seenLater.Asked);
        Assert.Equal([Links.WebUrl!, Links.WebUrl!], seenLater.Urls);

        var (thisPc, seenThisPc) = Opener(Engine(), settings with { Missing = OfficeMissingMode.Ask }, answer: () => MissingChoice.ThisPc);
        Assert.Equal(OfficeRoute.OpenWith, (await thisPc.RunAsync(@"M:\OneDrive\Plan.vsdx", default)).Route);
        Assert.Equal([(@"M:\OneDrive\Plan.vsdx", true)], seenThisPc.Locals);

        var (local, seenLocal) = Opener(Engine(), settings with { Missing = OfficeMissingMode.Local });
        Assert.Equal(OfficeRoute.OpenWith, (await local.RunAsync(@"M:\OneDrive\Plan.vsdx", default)).Route);
        Assert.Equal([(@"M:\OneDrive\Plan.vsdx", true)], seenLocal.Locals);
        Assert.Equal(0, seenLocal.LinkCalls);
    }

    [Fact]
    public async Task Opens_of_several_selected_files_wait_for_one_question()
    {
        api.Add("A.vsdx");
        api.Add("B.vsdx");
        var answer = new TaskCompletionSource<MissingChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
        var asked = 0;
        var urls = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var opener = new OfficeOpen(Engine(), (_, _, _) => Task.FromResult<ItemLinks?>(Links), path => ExplorerVerbs.VolumePath(path, Unc, "M:", new Dictionary<string, string>()),
            Settings(OfficeFilesMode.DesktopApp, OfficeMissingMode.Ask, ".vsdx"), Associations(), urls.Enqueue, (_, _) => { },
            (_, _, _) =>
            {
                Interlocked.Increment(ref asked);
                return answer.Task;
            }, _ => { });

        var first = opener.RunAsync(@"M:\OneDrive\A.vsdx", default);
        var second = opener.RunAsync(@"M:\OneDrive\B.vsdx", default);
        await Task.Delay(200);
        answer.SetResult(MissingChoice.Browser);

        Assert.Equal(OfficeRoute.Browser, (await first).Route);
        Assert.Equal(OfficeRoute.Browser, (await second).Route);
        Assert.Equal(1, asked);
        Assert.Equal(2, urls.Count);
    }

    [Fact]
    public async Task Every_refusal_opens_the_copy_on_the_drive()
    {
        api.Add("offer.docx");
        api.Add("empty.docx", content: "");
        api.Add("macro.docm");
        api.Add("staged.docx");
        var engine = Engine();
        var staged = await engine.GetEntryAsync(@"\OneDrive\staged.docx", default);
        await engine.OpenForWriteAsync(staged!, truncate: false, default);
        var created = await engine.CreateAsync(@"\OneDrive\new.docx", false, default);
        await File.WriteAllTextAsync(created.StagingPath!, "draft");
        engine.MarkDirty(created);

        async Task<string> Local(string path, OfficeSettings? settings = null, Func<string, OfficeAssociation>? associate = null,
            Func<string, string, CancellationToken, Task<ItemLinks?>>? links = null, Action<string>? openUrl = null)
        {
            var (opener, seen) = Opener(engine, settings ?? Settings(), associate, links, openUrl: openUrl);
            var outcome = await opener.RunAsync(path, default);
            Assert.Equal([(path, false)], seen.Locals);
            Assert.Empty(seen.Urls);
            return outcome.Reason;
        }

        Assert.Equal("policy OfficeFiles is Local", await Local(@"M:\OneDrive\offer.docx", Settings(OfficeFilesMode.Local)));
        Assert.Equal("not a type in OfficeFileTypes", await Local(@"M:\OneDrive\macro.docm"));
        Assert.Equal("another app opens this type", await Local(@"M:\OneDrive\offer.docx", associate: Associations((".docx", OfficeHandler.Other, "LibreOffice.WriterDocument.1"))));
        Assert.Equal("not on the drive", await Local(@"C:\Users\ann\offer.docx"));
        Assert.Equal("not found", await Local(@"M:\OneDrive\gone.docx"));
        Assert.Equal("not uploaded yet", await Local(@"M:\OneDrive\new.docx"));
        Assert.Equal("empty, so Office makes it a new document", await Local(@"M:\OneDrive\empty.docx"));
        Assert.Equal("changed or being written on this PC", await Local(@"M:\OneDrive\staged.docx"));
        Assert.Equal("not found in Microsoft 365", await Local(@"M:\OneDrive\offer.docx", links: (_, _, _) => Task.FromResult<ItemLinks?>(null)));
        Assert.Equal("Microsoft 365 didn't give its address: Offline",
            await Local(@"M:\OneDrive\offer.docx", links: (_, _, _) => throw new HttpRequestException("No such host is known. (x-my.sharepoint.com:443)")));
        Assert.Equal("Microsoft 365 didn't give its address: SignedOut",
            await Local(@"M:\OneDrive\offer.docx", links: (_, _, _) => Task.FromException<ItemLinks?>(new RemoteException(RemoteError.Other, "401 Unauthorized from https://graph.microsoft.com/v1.0/x: InvalidAuthenticationToken"))));
        Assert.Equal("no file address", await Local(@"M:\OneDrive\offer.docx", links: (_, _, _) => Task.FromResult<ItemLinks?>(Links with { WebDavUrl = "http://x/offer.docx" })));
        Assert.Equal("its address is too long for Office",
            await Local(@"M:\OneDrive\offer.docx", links: (_, _, _) => Task.FromResult<ItemLinks?>(Links with { WebDavUrl = "https://x-my.sharepoint.com/" + new string('a', 300) + ".docx" })));

        var watch = Stopwatch.StartNew();
        Assert.Equal("Microsoft 365 didn't answer within 0.3 s",
            await Local(@"M:\OneDrive\offer.docx", links: async (_, _, _) =>
            {
                await Task.Delay(Timeout.Infinite, CancellationToken.None); // throttled: never answers in time
                return Links;
            }));
        Assert.InRange(watch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(5));

        // The address didn't open (no handler for ms-word:): the copy on the drive still opens
        var (failing, seen) = Opener(engine, Settings(), openUrl: _ => throw new Win32Exception(1155));
        Assert.Equal(OfficeRoute.DesktopApp, (await failing.RunAsync(@"M:\OneDrive\offer.docx", default)).Route);
        Assert.Equal([(@"M:\OneDrive\offer.docx", false)], seen.Locals);
        Assert.Contains(seen.Logs, line => line.Contains("the address didn't open", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_refused_type_without_an_app_lets_windows_ask_and_logs_without_names_on_request()
    {
        api.Add("Plan.vsdx", content: "");
        var (opener, seen) = Opener(Engine(), Settings(OfficeFilesMode.DesktopApp, OfficeMissingMode.Browser, ".vsdx") with { LogPaths = false });

        Assert.Equal(OfficeRoute.OpenWith, (await opener.RunAsync(@"M:\OneDrive\Plan.vsdx", default)).Route);

        Assert.Equal([(@"M:\OneDrive\Plan.vsdx", true)], seen.Locals);
        var line = Assert.Single(seen.Logs);
        Assert.StartsWith("Office open: a .vsdx file: Windows asks which app", line);
        Assert.DoesNotContain("Plan", line);
    }

    [Fact]
    public void Staged_files_are_known_to_the_engine()
    {
        var engine = Engine();
        var entry = new FsEntry { Path = @"\OneDrive\a.docx", Name = "a.docx", StagingPath = @"C:\staging\a" };
        Assert.True(engine.IsStaged(entry));
        Assert.False(engine.IsStaged(new FsEntry { Path = @"\OneDrive\a.docx", Name = "a.docx" }));
    }

    // The client

    [Fact]
    public async Task The_client_opens_the_copy_itself_when_no_agent_takes_the_request()
    {
        var locals = new List<(string, bool)>();
        void Open(string path, bool chooseApp) => locals.Add((path, chooseApp));
        var associate = Associations();

        Assert.Equal(0, OfficeOpen.RunClient(@"M:\a.docx", _ => true, associate, Open));
        Assert.Empty(locals);
        Assert.Equal(0, OfficeOpen.RunClient(@"M:\a.docx", _ => false, associate, Open));
        Assert.Equal(0, OfficeOpen.RunClient(@"M:\b.vsdx", _ => false, associate, Open)); // no app: Windows asks
        Assert.Equal([(@"M:\a.docx", false), (@"M:\b.vsdx", true)], locals);
        Assert.Equal(1, OfficeOpen.RunClient(@"M:\a.docx", _ => false, associate, (_, _) => throw new Win32Exception(1223))); // cancelled

        // No agent: the request fails within the client's time, then the file opens here
        var name = VerbPipe.Name(Environment.ProcessId, "test-" + Guid.NewGuid().ToString("N"));
        var watch = Stopwatch.StartNew();
        Assert.Equal(0, OfficeOpen.RunClient(@"M:\c.docx", path => VerbPipe.Send(name, OfficeOpen.Verb, path, OfficeOpen.ClientTimeout, out _), associate, Open));
        Assert.InRange(watch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(4));
        Assert.Equal((@"M:\c.docx", false), locals[^1]);

        // An agent that refuses: the same
        using var stop = new CancellationTokenSource();
        var server = Task.Run(() => VerbPipe.ServeAsync(name, (_, _) => "unknown request", _ => { }, stop.Token));
        Assert.Equal(0, OfficeOpen.RunClient(@"M:\d.docx", path => VerbPipe.Send(name, OfficeOpen.Verb, path, TimeSpan.FromSeconds(10), out _), associate, Open));
        Assert.Equal((@"M:\d.docx", false), locals[^1]);
        stop.Cancel();
        try
        {
            await server; // ends at the cancel, between requests without an exception
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Fact]
    public void The_question_has_its_text_and_three_buttons()
    {
        var template = AskWindow.ChoiceTemplate("Open file", "&Open in the browser", "&Choose an app", "Cancel");
        Assert.Equal(["Static", "Button", "Button", "Button"],
            AskWindow.HiddenControls(template, [AskWindow.IdPrompt, AskWindow.IdOk, AskWindow.IdSecond, AskWindow.IdCancel]), StringComparer.OrdinalIgnoreCase);
        Assert.Equal("Word isn't installed on this PC. Open R&D.docx in Word for the web, or choose another app on this PC?",
            UiText.Get(English, "Office.Ask.Text", "Word", "R&D.docx"));
    }

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        try
        {
            using var tests = Registry.CurrentUser.OpenSubKey(@"Software\VFS365-tests", writable: true);
            if (tests is not null && tests.SubKeyCount == 0 && tests.ValueCount == 0)
            {
                Registry.CurrentUser.DeleteSubKey(@"Software\VFS365-tests", throwOnMissingSubKey: false);
            }
        }
        catch (IOException)
        {
            // Another test class removed it meanwhile (marked for deletion)
        }
        hive.Dispose();
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
