using System.IO.Compression;
using System.Text.Json;
using Vfs365.Agent;
using Vfs365.Core.Discovery;

namespace Vfs365.Tests;

/// <summary>A support bundle has what a helpdesk needs, none of the user's files and no tokens or secrets.</summary>
public sealed class SupportBundleTests : IDisposable
{
    static readonly DateTimeOffset T0 = new(2026, 10, 25, 9, 0, 0, TimeSpan.FromHours(2));
    readonly string root = Path.Combine(Path.GetTempPath(), $"vfs365-bundle-{Guid.NewGuid():N}");
    string Data => Path.Combine(root, "data");
    string Out => Path.Combine(root, "out");

    const string Jwt = "eyJ0eXAiOiJKV1QiLCJhbGciOiJSUzI1NiJ9.eyJhdWQiOiJodHRwczovL2dyYXBoIn0.QnVuZGxlU2lnbmF0dXJl";

    /// <summary>Seeded into the logs, the status, the settings and a journal; none may appear in the bundle.</summary>
    static readonly string[] Secrets =
    [
        Jwt, "BearerSeeded0123456789", "SasSeeded0123", "TempAuthSeeded0123", "AccessSeeded0123", "RefreshSeeded0123", "ClientSecretSeeded~1",
        "PasswordSeeded1", "QWNjb3VudEtleVNlZWRlZA==", "SettingsSig0123", "JournalTempAuth0123", "StatusTempAuth0123",
    ];

    /// <summary>Content of files that must never be read into a bundle.</summary>
    static readonly string[] Excluded = ["CACHED-FILE-CONTENT", "METADATA-CONTENT", "CACHE-KEY-CONTENT", "STAGING-CONTENT", "TOKEN-CACHE-CONTENT"];

    public SupportBundleTests()
    {
        Directory.CreateDirectory(Path.Combine(Data, "logs"));
        File.WriteAllText(Path.Combine(Data, "agent.log"), string.Join(Environment.NewLine,
            "2026-10-25 09:00:00 VFS365 is made by JSolve B.V., https://jsolve.nl",
            $"2026-10-25 09:00:01 token {Secrets[0]}",
            $"2026-10-25 09:00:02 Authorization: Bearer {Secrets[1]}",
            $"2026-10-25 09:00:03 Monitoring: https://acct.table.core.windows.net/?sv=2022-11-02&ss=t&srt=o&sp=a&sig={Secrets[2]}",
            $"2026-10-25 09:00:04 error: reading \\OneDrive\\a.docx: https://contoso-my.sharepoint.com/_layouts/15/download.aspx?UniqueId=1&tempauth={Secrets[3]}",
            $"2026-10-25 09:00:05 {{\"access_token\":\"{Secrets[4]}\",\"refresh_token\":\"{Secrets[5]}\"}}",
            $"2026-10-25 09:00:06 client_secret={Secrets[6]} password={Secrets[7]}",
            $"2026-10-25 09:00:07 AccountName=acct;AccountKey={Secrets[8]};EndpointSuffix=core.windows.net",
            "2026-10-25 09:00:08 Mounted:    M: and \\\\VFS365\\ann (SharePoint) after 0.1 s"));
        for (var i = 1; i <= 3; i++)
        {
            var log = Path.Combine(Data, "logs", $"agent-2026102{i}-090000.log");
            File.WriteAllText(log, $"earlier log {i} Bearer {Secrets[1]}");
            File.SetLastWriteTimeUtc(log, new DateTime(2026, 10, 20 + i, 9, 0, 0, DateTimeKind.Utc));
        }
        File.WriteAllText(Path.Combine(Data, StatusFile.FileName), JsonSerializer.Serialize(AgentStatus.ForThisProcess(T0) with
        {
            State = AgentStatus.Mounted,
            DriveLetter = "M:",
            Unc = @"\\VFS365\ann",
            User = "ann@contoso.com",
            WaitingUploads = 1,
            LastError = $"https://contoso.sharepoint.com/_layouts/15/download.aspx?tempauth={Secrets[11]}",
            WaitingFiles = [new WaitingFile(@"\Sites\Sales\Offer.docx", "423 Locked from https://x: checked out", T0)],
        }));
        var library = new LibraryEntry(LibraryKey.Of("s", "w", "l"), "s", "w", "l", "https://contoso.sharepoint.com/sites/Sales", "Sales", "Documents", false, 12);
        File.WriteAllText(Path.Combine(Data, "discovery.json"), JsonSerializer.Serialize(new DiscoveryState(T0, "https://contoso.sharepoint.com", [library],
            new Dictionary<string, string>(), OneDrive: new MyDrive("b!me", "https://contoso-my.sharepoint.com/personal/ann_contoso_com/Documents"))));
        DiscoveryReport.Save(new DiscoveryReport(T0, 3, null, null, null,
            [new SkippedLibrary("k1", "https://contoso.sharepoint.com/sites/Sales", "Site Assets", "System or catalog library", "SiteAssets")],
            [new SkippedLibrary("k2", "https://contoso.sharepoint.com/sites/Legal", "Documents", "Site: 503 ServiceUnavailable")], []),
            Path.Combine(Data, DiscoveryReport.FileName));
        File.WriteAllText(Path.Combine(Data, "redirection.json"), "{\"Documents\":{\"Folder\":\"Documents\"}}");
        File.WriteAllText(Path.Combine(Data, "pinned.json"), "[\"https://contoso.sharepoint.com/sites/Secret\"]");

        // What a bundle never reads
        Directory.CreateDirectory(Path.Combine(Data, "cache", "b!drive"));
        File.WriteAllText(Path.Combine(Data, "cache", "b!drive", "item-1"), Excluded[0]);
        File.WriteAllText(Path.Combine(Data, "metadata.bin"), Excluded[1]);
        File.WriteAllText(Path.Combine(Data, "cache.key"), Excluded[2]);
        Directory.CreateDirectory(Path.Combine(Data, "staging"));
        File.WriteAllText(Path.Combine(Data, "staging", "abc.staging"), Excluded[3]);
        File.WriteAllText(Path.Combine(Data, "staging", "abc.staging.json"), JsonSerializer.Serialize(new
        {
            DriveId = "b!drive",
            DrivePath = "Sales/Offer.docx",
            Base = new { Id = "item", DownloadUrl = $"https://contoso.sharepoint.com/_layouts/15/download.aspx?tempauth={Secrets[10]}" },
            Modified = T0,
            Flushed = false,
        }));
        File.WriteAllText(Path.Combine(Data, "msal.cache"), Excluded[4]);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    SupportBundle Bundle(Func<AgentSettings>? settings = null) => new(Data, settings ?? (() => AgentSettings.FromValues("computer policy", new Dictionary<string, object>
    {
        ["MonitoringUrl"] = $"https://acct.table.core.windows.net/?sv=2022-11-02&ss=t&srt=o&sp=a&se=2027-01-01T00:00:00Z&sig={Secrets[9]}",
        ["DriveLetter"] = "M",
        ["DriveMappings"] = new[] { @"Z:=Sites\Sales\Documents|Sales" },
    })))
    {
        Clock = () => T0,
        HealthCheck = () => Health.Build(new MachineHealth("2.1.25156", ["WinFsp.Np", "VFS365.Np"], false), [], T0),
    };

    static Dictionary<string, string> Read(string zip)
    {
        using var archive = ZipFile.OpenRead(zip);
        return archive.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var reader = new StreamReader(e.Open());
            return reader.ReadToEnd();
        });
    }

    [Fact]
    public void A_bundle_has_the_parts_a_helpdesk_asks_for()
    {
        var zip = Bundle().Create(Out);
        Assert.Equal(Out, Path.GetDirectoryName(zip));
        Assert.Matches(@"^VFS365-support-.+-20261025-090000\.zip$", Path.GetFileName(zip));
        Assert.False(File.Exists(zip + ".partial"));

        var entries = Read(zip);
        foreach (var name in new[] { "README.txt", "summary.txt", "health.json", "settings.txt", "tenant.txt", "discovery.txt", "drive.txt", "waiting.txt", "status.json", "redirection.json" })
        {
            Assert.True(entries.ContainsKey(name), name);
        }
        // agent.log and the two newest earlier logs
        Assert.Equal(["logs/agent-20261022-090000.log", "logs/agent-20261023-090000.log", "logs/agent.log"],
            entries.Keys.Where(k => k.StartsWith("logs/", StringComparison.Ordinal)).Order(StringComparer.Ordinal));

        Assert.Contains("MonitoringUrl", entries["settings.txt"]);
        Assert.Contains("acct", entries["settings.txt"]);
        Assert.Contains("https://contoso.sharepoint.com/sites/Secret", entries["settings.txt"]);
        Assert.Contains("Sales / Documents", entries["discovery.txt"]);
        Assert.Contains("System or catalog library", entries["discovery.txt"]);
        Assert.Contains("Site: 503 ServiceUnavailable", entries["discovery.txt"]);
        Assert.Contains(@"\Sites\Sales\Offer.docx: 423 Locked", entries["waiting.txt"]);
        Assert.Contains("Sales/Offer.docx", entries["waiting.txt"]);
        Assert.Contains("still being written", entries["waiting.txt"]);
        Assert.Contains(@"Z: = Sites\Sales\Documents (Sales)", entries["drive.txt"]);
        Assert.Contains("ann@contoso.com", entries["tenant.txt"]);
        Assert.Contains("Health:     healthy", entries["summary.txt"]);
        Assert.Contains("Mounted:    M:", entries["logs/agent.log"]);
    }

    [Fact]
    public void A_bundle_has_no_secrets_and_none_of_the_users_files()
    {
        var entries = Read(Bundle().Create(Out));
        Assert.DoesNotContain(entries.Keys, name => name.Contains("cache", StringComparison.OrdinalIgnoreCase) || name.Contains("metadata", StringComparison.OrdinalIgnoreCase)
            || name.Contains("staging", StringComparison.OrdinalIgnoreCase) || name.Contains("msal", StringComparison.OrdinalIgnoreCase));
        var all = string.Join("\n", entries.Values);
        foreach (var secret in Secrets)
        {
            Assert.DoesNotContain(secret, all, StringComparison.Ordinal);
        }
        foreach (var content in Excluded)
        {
            Assert.DoesNotContain(content, all, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_part_that_fails_holds_a_note_and_the_rest_is_there()
    {
        var entries = Read(Bundle(() => throw new IOException("policy unreadable")).Create(Out));
        Assert.StartsWith("Could not collect settings.txt: IOException: policy unreadable", entries["settings.txt"]);
        Assert.StartsWith("Could not collect drive.txt", entries["drive.txt"]);
        Assert.Contains("Mounted:    M:", entries["logs/agent.log"]);
    }

    [Fact]
    public void A_bundle_never_goes_onto_the_drive()
    {
        Assert.NotNull(SupportBundle.Refusal(@"\\VFS365\ann\OneDrive"));
        Assert.NotNull(SupportBundle.Refusal(@"\\vfs365\ann"));
        Assert.NotNull(SupportBundle.Refusal(@"M:\support", driveLetter: "M:"));
        Assert.Null(SupportBundle.Refusal(Out));
    }

    [Fact]
    public void Only_the_newest_bundles_are_kept()
    {
        Directory.CreateDirectory(Out);
        for (var i = 0; i < SupportBundle.Keep + 3; i++)
        {
            var zip = Path.Combine(Out, $"{SupportBundle.Prefix}pc-ann-2026102{i}-090000.zip");
            File.WriteAllText(zip, "");
            File.SetLastWriteTimeUtc(zip, new DateTime(2026, 10, 1 + i, 9, 0, 0, DateTimeKind.Utc));
        }
        File.WriteAllText(Path.Combine(Out, "other.zip"), "");
        SupportBundle.Prune(Out);
        Assert.Equal(SupportBundle.Keep, Directory.GetFiles(Out, $"{SupportBundle.Prefix}*.zip").Length);
        Assert.True(File.Exists(Path.Combine(Out, "other.zip")));
        Assert.False(File.Exists(Path.Combine(Out, $"{SupportBundle.Prefix}pc-ann-20261020-090000.zip")));
    }
}
