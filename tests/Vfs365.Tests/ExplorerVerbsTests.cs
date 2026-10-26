using System.Globalization;
using Microsoft.Win32;
using Vfs365.Agent;
using Vfs365.Core.Discovery;
using Vfs365.Core.Drive;
using Vfs365.Graph;
using Vfs365.Tests.Drive;

namespace Vfs365.Tests;

/// <summary>
/// Explorer's VFS365 menu: paths, URLs, the registry entries (in a key of their own, HKCU\Software\VFS365-tests\...), the pipe to the
/// agent and what the verbs do there.
/// </summary>
public sealed class ExplorerVerbsTests : IDisposable
{
    static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-GB"), Dutch = CultureInfo.GetCultureInfo("nl-NL");
    const string Unc = @"\\VFS365\ann", Exe = @"C:\Program Files\VFS365\vfs365-agent.exe";
    static readonly Dictionary<string, string> Folders = new(StringComparer.OrdinalIgnoreCase) { ["Z:"] = @"\\VFS365\ann\Sites\Sales\Documents" };

    readonly string keyPath = $@"Software\VFS365-tests\{Guid.NewGuid():N}";
    readonly RegistryKey hive;
    readonly string directory = Path.Combine(Path.GetTempPath(), "vfs365-tests", Guid.NewGuid().ToString("N"));
    readonly FakeDriveApi api = new();

    public ExplorerVerbsTests() => hive = Registry.CurrentUser.CreateSubKey(keyPath);

    [Theory]
    [InlineData(@"M:\OneDrive\Offer.docx", @"\OneDrive\Offer.docx")]
    [InlineData(@"m:\OneDrive\", @"\OneDrive")]
    [InlineData(@"M:\", @"\")]
    [InlineData(@"\\VFS365\ann\Sites\Sales\Documents\Plan.xlsx", @"\Sites\Sales\Documents\Plan.xlsx")]
    [InlineData(@"\\vfs365\ANN\OneDrive", @"\OneDrive")]
    [InlineData(@"\\?\UNC\VFS365\ann\OneDrive\a.txt", @"\OneDrive\a.txt")]
    [InlineData(@"\\?\M:\OneDrive\a.txt", @"\OneDrive\a.txt")]
    [InlineData(@"Z:\Offers\Q4.docx", @"\Sites\Sales\Documents\Offers\Q4.docx")]
    [InlineData(@"Z:\", @"\Sites\Sales\Documents")]
    [InlineData(@"C:\Users\ann\Offer.docx", null)]
    [InlineData(@"\\VFS365\bob\OneDrive\a.txt", null)]
    [InlineData(@"\\VFS365\anna\OneDrive\a.txt", null)]
    [InlineData(@"M:\OneDrive\..\..\x", null)]
    [InlineData(@"OneDrive\a.txt", null)]
    public void Explorer_paths_become_volume_paths(string path, string? expected) =>
        Assert.Equal(expected, ExplorerVerbs.VolumePath(path, Unc, "M:", Folders));

    [Theory]
    [InlineData("Offer.docx", "ms-word")]
    [InlineData("Budget.XLSX", "ms-excel")]
    [InlineData("Macro.xlsm", "ms-excel")]
    [InlineData("Deck.pptx", "ms-powerpoint")]
    [InlineData("Old.doc", "ms-word")]
    [InlineData("notes.txt", null)]
    [InlineData("report.pdf", null)]
    public void Office_files_open_in_their_desktop_app_for_editing(string name, string? scheme)
    {
        var url = ItemUrls.Desktop(name, $"https://contoso.sharepoint.com/sites/Sales/Shared%20Documents/{name}");
        Assert.Equal(scheme is null ? null : $"{scheme}:ofe|u|https://contoso.sharepoint.com/sites/Sales/Shared%20Documents/{name}", url);
    }

    [Theory]
    [InlineData("https://contoso.sharepoint.com/sites/Sales/Shared Documents/Q4 plan.docx", "https://contoso.sharepoint.com/sites/Sales/Shared%20Documents/Q4%20plan.docx")]
    [InlineData("https://contoso.sharepoint.com/sites/Sales/Shared%20Documents/Q4%20plan.docx", "https://contoso.sharepoint.com/sites/Sales/Shared%20Documents/Q4%20plan.docx")]
    [InlineData("https://contoso.sharepoint.com/sites/Sales/Gedeelde documenten/Offerte café.docx", "https://contoso.sharepoint.com/sites/Sales/Gedeelde%20documenten/Offerte%20caf%C3%A9.docx")]
    [InlineData("https://contoso.sharepoint.com/sites/Sales/Shared%20Documents/caf%C3%A9.docx", "https://contoso.sharepoint.com/sites/Sales/Shared%20Documents/caf%C3%A9.docx")]
    [InlineData("https://contoso.sharepoint.com/sites/Sales/Shared Documents/R&D #1 (draft).docx", "https://contoso.sharepoint.com/sites/Sales/Shared%20Documents/R&D%20%231%20(draft).docx")]
    [InlineData("https://contoso.sharepoint.com/sites/Sales/Shared Documents/100% 日本.docx", "https://contoso.sharepoint.com/sites/Sales/Shared%20Documents/100%25%20%E6%97%A5%E6%9C%AC.docx")]
    public void File_urls_are_encoded_once_and_keep_reserved_characters(string url, string expected) => Assert.Equal(expected, ItemUrls.Encode(url));

    [Fact]
    public void Version_history_is_sharepoints_page_for_the_list_item()
    {
        Assert.Equal("https://contoso.sharepoint.com/sites/Sales/_layouts/15/Versions.aspx?list=%7B0D3F2C55-1E33-4A3B-9C1A-6F1B2B3C4D5E%7D&ID=42",
            ItemUrls.VersionHistory("https://contoso.sharepoint.com/sites/Sales/", "0D3F2C55-1E33-4A3B-9C1A-6F1B2B3C4D5E", "42"));
        Assert.Equal("https://contoso-my.sharepoint.com/personal/ann_contoso_com/_layouts/15/Versions.aspx?list=%7Babc%7D&ID=7",
            ItemUrls.VersionHistory("https://contoso-my.sharepoint.com/personal/ann_contoso_com", "{abc}", "7"));
    }

    [Fact]
    public void Graph_links_are_read_from_webUrl_webDavUrl_and_sharepointIds()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""
            {"id":"1","webUrl":"https://c.sharepoint.com/sites/s/_layouts/15/Doc.aspx?sourcedoc=%7Bx%7D","webDavUrl":"https://c.sharepoint.com/sites/s/Shared Documents/a.docx",
             "sharepointIds":{"siteUrl":"https://c.sharepoint.com/sites/s","listId":"l1","listItemId":"9"}}
            """);
        Assert.Equal(new ItemLinks("https://c.sharepoint.com/sites/s/_layouts/15/Doc.aspx?sourcedoc=%7Bx%7D", "https://c.sharepoint.com/sites/s/Shared Documents/a.docx",
            "https://c.sharepoint.com/sites/s", "l1", "9"), DriveApi.ParseLinks(doc.RootElement));
    }

    [Fact]
    public void Graph_items_carry_who_saved_them_last()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""
            {"id":"1","name":"a.docx","file":{},"lastModifiedBy":{"user":{"displayName":"Ann de Vries","email":"x"}}}
            """);
        Assert.Equal("Ann de Vries", DriveApi.Parse(doc.RootElement)!.ModifiedBy);
        using var app = System.Text.Json.JsonDocument.Parse("""{"id":"2","name":"b.txt","file":{},"lastModifiedBy":{"application":{"displayName":"Power Automate"}}}""");
        Assert.Equal("Power Automate", DriveApi.Parse(app.RootElement)!.ModifiedBy);
        using var none = System.Text.Json.JsonDocument.Parse("""{"id":"3","name":"c.txt","file":{}}""");
        Assert.Null(DriveApi.Parse(none.RootElement)!.ModifiedBy);
    }

    [Fact]
    public void The_menu_applies_to_the_volumes_roots_only()
    {
        var roots = ExplorerMenu.Roots(Unc, "M:", ["Z:"]);
        Assert.Equal([@"M:\", @"Z:\", @"\\VFS365\ann\"], roots);
        Assert.Equal(@"System.ItemPathDisplay:~<""M:\"" OR System.ItemPathDisplay:~<""Z:\"" OR System.ItemPathDisplay:~<""\\VFS365\ann\""",
            ExplorerVerbs.AppliesTo(roots));
        Assert.Equal([@"\\VFS365\ann\"], ExplorerMenu.Roots(Unc, null, []));
    }

    [Fact]
    public void The_menu_is_written_per_user_and_only_when_it_changes()
    {
        Assert.True(ExplorerVerbs.Register(hive, Exe, [@"M:\", @"\\VFS365\ann\"], English));

        using (var files = hive.OpenSubKey(@"Software\Classes\*\shell\VFS365")!)
        {
            Assert.Equal("VFS365", files.GetValue("MUIVerb"));
            Assert.Equal("", files.GetValue("SubCommands"));
            Assert.Equal("Single", files.GetValue("MultiSelectModel"));
            Assert.Equal(ExplorerVerbs.AppliesTo([@"M:\", @"\\VFS365\ann\"]), files.GetValue("AppliesTo"));
            using var shell = files.OpenSubKey("shell")!;
            Assert.Equal(["01browser", "02desktop", "03link", "04versions", "05share", "06keep", "07compare"], shell.GetSubKeyNames().Order());
            using var browser = shell.OpenSubKey(@"01browser\command")!;
            Assert.Equal($@"""{Exe}"" verb browser ""%1""", browser.GetValue(""));
            using var desktop = shell.OpenSubKey("02desktop")!;
            Assert.Equal("Open in desktop app", desktop.GetValue("MUIVerb"));
            Assert.Contains(@"System.FileExtension:="".docx""", (string)desktop.GetValue("AppliesTo")!);
            using var keep = shell.OpenSubKey("06keep")!;
            Assert.Contains("(conflict ????-??-?? ??????*)", (string)keep.GetValue("AppliesTo")!);
        }
        using (var folders = hive.OpenSubKey(@"Software\Classes\Directory\shell\VFS365\shell")!)
        {
            Assert.Equal(["01browser", "02link", "03share", "04search"], folders.GetSubKeyNames().Order());
            using var search = folders.OpenSubKey("04search")!;
            Assert.Equal("Search in Microsoft 365", search.GetValue("MUIVerb"));
            Assert.Equal(0x20, search.GetValue("CommandFlags"));
            using var command = folders.OpenSubKey(@"04search\command")!;
            Assert.Equal($@"""{Exe}"" verb search ""%1""", command.GetValue(""));
        }
        Assert.Null(hive.OpenSubKey(@"Software\Classes\Directory\Background\shell\VFS365")); // Explorer hides it there with AppliesTo
        Assert.True(ExplorerLocation.IsRegistered(hive));

        Assert.False(ExplorerVerbs.Register(hive, Exe, [@"M:\", @"\\VFS365\ann\"], English)); // nothing changed
        Assert.True(ExplorerVerbs.Register(hive, Exe, [@"\\VFS365\ann\"], English)); // the letter went
        Assert.True(ExplorerVerbs.Register(hive, Exe, [@"\\VFS365\ann\"], Dutch));
        using (var link = hive.OpenSubKey(@"Software\Classes\*\shell\VFS365\shell\03link")!)
        {
            Assert.Equal("Koppeling kopiëren", link.GetValue("MUIVerb"));
        }

        Assert.True(ExplorerVerbs.Register(hive, Exe, [], English)); // no roots: removed
        Assert.False(ExplorerVerbs.IsRegistered(hive));
        Assert.False(ExplorerLocation.IsRegistered(hive));
    }

    [Theory]
    [InlineData(@"M:""", @"M:\")]
    [InlineData(@"M:\Sites\Sales", @"M:\Sites\Sales")]
    [InlineData(@"\\VFS365\ann\OneDrive", @"\\VFS365\ann\OneDrive")]
    [InlineData(@"Z:""", @"Z:\")]
    public void A_drive_tops_quoted_path_comes_back_whole(string argument, string expected) =>
        Assert.Equal(expected, VerbPipe.PathArgument(argument));

    [Fact]
    public void Uninstall_and_the_logon_cleanup_remove_the_menu()
    {
        ExplorerVerbs.Register(hive, Exe, [@"M:\"], English);
        ExplorerVerbs.Remove(hive);
        Assert.False(ExplorerVerbs.IsRegistered(hive));

        var command = MachineCleanup.LogonCleanupCommand(["M:"]);
        Assert.Contains(@"delete ""HKCU\Software\Classes\*\shell\VFS365"" /f", command, StringComparison.Ordinal);
        Assert.Contains(@"delete ""HKCU\Software\Classes\Directory\shell\VFS365"" /f", command, StringComparison.Ordinal);    }

    [Fact]
    public async Task Requests_reach_the_agent_over_the_pipe()
    {
        var name = VerbPipe.Name(Environment.ProcessId, "test-" + Guid.NewGuid().ToString("N"));
        var received = new List<(string, string)>();
        using var stop = new CancellationTokenSource();
        var logged = new List<string>();
        var server = Task.Run(() => VerbPipe.ServeAsync(name, (verb, path) =>
        {
            lock (received)
            {
                received.Add((verb, path));
            }
            return verb == "keep" ? "not now" : null;
        }, line => { lock (logged) { logged.Add(line); } }, stop.Token));

        var timeout = TimeSpan.FromSeconds(10);
        Assert.True(VerbPipe.Send(name, "browser", @"M:\OneDrive\Offerte café.docx", timeout, out var answer));
        Assert.Equal("ok", answer);
        Assert.False(VerbPipe.Send(name, "keep", @"M:\OneDrive\a (conflict 2026-10-25 101010).docx", timeout, out answer));
        Assert.Equal("not now", answer);
        Assert.False(VerbPipe.Send(name, "link", new string('x', VerbPipe.MaxRequest + 10), timeout, out answer)); // too long: no answer
        Assert.True(VerbPipe.Send(name, "link", @"\\VFS365\ann\OneDrive", timeout, out _)); // the agent still answers

        lock (received)
        {
            Assert.Equal([("browser", @"M:\OneDrive\Offerte café.docx"), ("keep", @"M:\OneDrive\a (conflict 2026-10-25 101010).docx"), ("link", @"\\VFS365\ann\OneDrive")], received);
        }
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => server);
        Assert.False(VerbPipe.Send(name, "browser", @"M:\a.txt", TimeSpan.FromMilliseconds(200), out answer)); // no agent
        Assert.Null(answer);
    }

    sealed class Recorder
    {
        public readonly List<string> Opened = [], Copied = [];
        public readonly List<(string Title, string Text, bool Problem)> Told = [];
        public readonly List<(string DriveId, string ItemId)> Asked = [];
    }

    (VerbActions Actions, Recorder Seen) Actions(DriveEngine engine, ItemLinks? links)
    {
        var seen = new Recorder();
        var actions = new VerbActions(engine,
            (drive, item, _) =>
            {
                seen.Asked.Add((drive, item));
                return Task.FromResult(links);
            },
            path => ExplorerVerbs.VolumePath(path, Unc, "M:", Folders),
            seen.Opened.Add, seen.Copied.Add, (title, text, problem) => seen.Told.Add((title, text, problem)), _ => { });
        return (actions, seen);
    }

    DriveEngine Engine() =>
        new(DriveNamespace.Build(new MyDrive("b!me", "https://x-my.sharepoint.com/personal/u"), [], (_, _) => Task.FromResult("unused")),
            api, new ContentCache(Path.Combine(directory, "cache"), api), new DriveEngineOptions { StagingDirectory = Path.Combine(directory, "staging") });

    static readonly ItemLinks Links = new("https://x-my.sharepoint.com/personal/u/_layouts/15/Doc.aspx?sourcedoc=%7B1%7D",
        "https://x-my.sharepoint.com/personal/u/Documents/Q4 plan.xlsx", "https://x-my.sharepoint.com/personal/u", "{list}", "12");

    [Fact]
    public async Task Verbs_open_copy_and_name_what_they_did()
    {
        var item = api.Add("Q4 plan.xlsx");
        var (actions, seen) = Actions(Engine(), Links);
        var path = @"M:\OneDrive\Q4 plan.xlsx";

        await actions.RunAsync("browser", path, default);
        await actions.RunAsync("desktop", path, default);
        await actions.RunAsync("versions", path, default);
        await actions.RunAsync("share", path, default);
        await actions.RunAsync("link", path, default);

        Assert.Equal([Links.WebUrl!, "ms-excel:ofe|u|https://x-my.sharepoint.com/personal/u/Documents/Q4%20plan.xlsx",
            "https://x-my.sharepoint.com/personal/u/_layouts/15/Versions.aspx?list=%7Blist%7D&ID=12", Links.WebUrl!], seen.Opened);
        Assert.Equal([Links.WebUrl!], seen.Copied);
        Assert.All(seen.Asked, asked => Assert.Equal(("b!me", item.Id), asked));
        Assert.Equal(2, seen.Told.Count);
        Assert.All(seen.Told, told => Assert.False(told.Problem));
        Assert.Contains("Q4 plan.xlsx", seen.Told[^1].Text);
    }

    [Fact]
    public async Task Verbs_say_in_plain_words_why_they_cant()
    {
        api.Add("notes.txt");
        api.Add("Folder", folder: true);
        var (actions, seen) = Actions(Engine(), Links);

        await actions.RunAsync("desktop", @"M:\OneDrive\notes.txt", default); // not an Office file
        await actions.RunAsync("versions", @"M:\OneDrive\Folder", default); // a folder
        await actions.RunAsync("browser", @"C:\Users\ann\notes.txt", default); // not on the drive
        await actions.RunAsync("browser", @"M:\OneDrive\gone.txt", default);
        await actions.RunAsync("keep", @"M:\OneDrive\notes.txt", default); // not a copy

        Assert.Empty(seen.Opened);
        Assert.Equal(5, seen.Told.Count);
        Assert.All(seen.Told, told => Assert.True(told.Problem));
        Assert.Equal(UiText.Get("Verb.NotOffice"), seen.Told[0].Text);
        Assert.Equal(UiText.Get("Verb.NotOnDrive", "notes.txt"), seen.Told[2].Text);
        Assert.Equal(UiText.Get("Verb.NotFound", "gone.txt"), seen.Told[3].Text);
    }

    [Fact]
    public async Task A_librarys_top_folder_links_to_the_library_and_the_volumes_own_folders_to_nothing()
    {
        var (actions, seen) = Actions(Engine(), Links);

        await actions.RunAsync("browser", @"M:\OneDrive", default);
        await actions.RunAsync("browser", @"M:\", default);

        Assert.Equal([("b!me", "root")], seen.Asked);
        Assert.Equal([Links.WebUrl!], seen.Opened);
        Assert.True(Assert.Single(seen.Told).Problem);
    }

    [Fact]
    public async Task Keep_and_compare_from_the_menu()
    {
        var original = api.Add("offer.docx", content: "theirs");
        api.Add("offer (conflict 2026-10-24 101112).docx", content: "mine");
        var (actions, seen) = Actions(Engine(), Links);
        var copy = @"\\VFS365\ann\OneDrive\offer (conflict 2026-10-24 101112).docx";

        await actions.RunAsync("compare", copy, default);
        Assert.Equal(2, seen.Opened.Count);
        Assert.All(seen.Opened, opened => Assert.StartsWith("ms-word:ofe|u|https://", opened));
        Assert.Equal(original.Id, seen.Asked[0].ItemId);

        await actions.RunAsync("keep", copy, default);
        Assert.Equal("mine", api.ContentOf("offer.docx"));
        Assert.Equal(original.Id, api.ItemAt("offer.docx")!.Id);
        Assert.Equal((UiText.Get("Verb.Kept.Title"), UiText.Get("Verb.Kept", "offer.docx"), false), seen.Told[^1]);
    }

    [Fact]
    public void A_conflict_copy_toast_names_who_saved_and_when()
    {
        var at = new DateTimeOffset(2026, 10, 25, 9, 30, 0, TimeSpan.Zero);
        var shown = at.ToLocalTime();
        foreach (var (culture, expected) in new[]
        {
            (English, $"Ann de Vries saved Offer.docx in Microsoft 365 ({shown.ToString("g", English)}). Your version is saved next to it as Offer (conflict 2026-10-25 093100).docx."),
            (Dutch, $"Ann de Vries heeft Offer.docx opgeslagen in Microsoft 365 ({shown.ToString("g", Dutch)}). Uw versie staat ernaast als Offer (conflict 2026-10-25 093100).docx."),
        })
        {
            var batcher = new NoticeBatcher();
            batcher.Add(new EngineNotice(NoticeKind.ConflictCopy, "Offer.docx", @"\OneDrive\Offer.docx", "Offer (conflict 2026-10-25 093100).docx", "Ann de Vries", at), Cause.Other, at);
            var toast = Assert.Single(batcher.Due(at + NoticeBatcher.Window, culture));
            Assert.Equal(expected, toast.Text);
        }
    }

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        using (var tests = Registry.CurrentUser.OpenSubKey(@"Software\VFS365-tests", writable: true))
        {
            if (tests is not null && tests.SubKeyCount == 0 && tests.ValueCount == 0)
            {
                Registry.CurrentUser.DeleteSubKey(@"Software\VFS365-tests", throwOnMissingSubKey: false);
            }
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
