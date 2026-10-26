using System.Buffers.Binary;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Security.Principal;
using System.Text;
using Vfs365.Agent;
using Vfs365.Core.Discovery;
using Vfs365.Core.Drive;
using Vfs365.Tests.Drive;

namespace Vfs365.Tests;

/// <summary>
/// The shell section vfs365shell.dll reads: layout byte for byte, key hashes (with a known answer the native side checks too), the hash
/// table's probing and load cap, folder states, what Explorer is told, roots, strings, and the section itself.
/// </summary>
public sealed class ShellSectionTests : IDisposable
{
    const string Unc = @"\\VFS365\ann";
    static readonly ShellRoot Letter = new(@"M:\", @"\"), UncRoot = new(@"\\VFS365\ann\", @"\"), Mapping = new(@"Z:\", @"\Sites\Sales\Documents\");
    readonly string directory = Path.Combine(Path.GetTempPath(), "vfs365-tests", Guid.NewGuid().ToString("N"));
    readonly string name = $@"Local\VFS365.Tests.{Guid.NewGuid():N}";

    /// <summary>FNV-1a 64 of the UTF-16 key "\ONEDRIVE\A.DOCX": the native side must give the same.</summary>
    const ulong KnownAnswer = 0x102A06A6FF71615A;

    [Fact]
    public void Keys_are_uppercased_by_windows_and_hashed_with_fnv1a_64()
    {
        Assert.Equal(@"\ONEDRIVE\A.DOCX", ShellImage.Upper(@"\OneDrive\a.docx"));
        Assert.Equal(KnownAnswer, ShellImage.Hash(@"\ONEDRIVE\A.DOCX"));
        Assert.Equal(KnownAnswer, ShellImage.KeyHash(@"\OneDrive\a.docx"));
        Assert.Equal(0x09500007B5DDF961UL, ShellImage.KeyHash(@"\"));

        // The same over the key's bytes (UTF-16, low byte first), written out once more
        var hash = 0xcbf29ce484222325UL;
        foreach (var b in Encoding.Unicode.GetBytes(@"\ONEDRIVE\A.DOCX"))
        {
            hash = (hash ^ b) * 0x100000001b3UL;
        }
        Assert.Equal(KnownAnswer, hash);
        Assert.Equal("CAFÉ\\ÄÖ", ShellImage.Upper("café\\äö"));
        Assert.Equal(0xcbf29ce484222325UL, ShellImage.Hash(""));
    }

    [Fact]
    public void The_image_is_laid_out_byte_for_byte()
    {
        var image = new byte[ShellImage.SectionSize];
        var hash = ShellImage.KeyHash(@"\OneDrive\a.docx");

        var length = ShellImage.Build(image, 1234, ShellImage.MenuFlag | ShellImage.OverlaysFlag, [Letter, Mapping],
            [("Verb.Menu", "VFS365")], [(hash, FileState.Uploading)]);

        uint U32(int at) => BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(at));
        Assert.Equal(0x35363356u, U32(0));
        Assert.Equal("V365", Encoding.ASCII.GetString(image, 0, 4));
        Assert.Equal(1u, U32(4));
        Assert.Equal(0u, U32(8)); // Sequence: the section's own
        Assert.Equal(1234u, U32(12));
        Assert.Equal(3u, U32(16));
        Assert.Equal(64u, U32(20));
        Assert.Equal(2u, U32(24));

        // Roots: u16 3, u16 1, "M:\", "\" (12 bytes); then "Z:\" and its 23-char target (4 + 52 = 56 bytes)
        Assert.Equal(new byte[] { 3, 0, 1, 0, (byte)'M', 0, (byte)':', 0, (byte)'\\', 0, (byte)'\\', 0 }, image[64..76]);
        Assert.Equal(new byte[] { 3, 0, 23, 0 }, image[76..80]);
        Assert.Equal(@"Z:\\Sites\Sales\Documents\", Encoding.Unicode.GetString(image, 80, 52));
        var stringsAt = 132;
        Assert.Equal((uint)stringsAt, U32(28));
        Assert.Equal(1u, U32(32));
        Assert.Equal(new byte[] { 9, 0, 6, 0 }, image[stringsAt..(stringsAt + 4)]);
        Assert.Equal("Verb.MenuVFS365", Encoding.Unicode.GetString(image, stringsAt + 4, 30));
        Assert.Equal(new byte[] { 0, 0 }, image[(stringsAt + 34)..(stringsAt + 36)]); // 34 padded to 36

        var slotsAt = (int)U32(36);
        Assert.Equal(176, slotsAt); // 168 aligned to 16
        Assert.Equal(65536u, U32(40));
        Assert.Equal(1u, U32(44));
        Assert.All(image[48..64], b => Assert.Equal(0, b));
        Assert.Equal(slotsAt + 65536 * 16, length);
        var slot = slotsAt + (int)(hash & 0xFFFF) * 16;
        Assert.Equal(hash, BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(slot)));
        Assert.Equal(2u, U32(slot + 8));
        Assert.Equal(0u, U32(slot + 12));
        Assert.Equal(1, Enumerable.Range(0, 65536).Count(i => BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(slotsAt + i * 16)) != 0));
    }

    [Fact]
    public void Collisions_probe_linearly_and_wrap_and_the_load_stays_at_most_70_percent()
    {
        var image = new byte[ShellImage.SectionSize];
        ShellImage.Build(image, 1, 3, [], [],
            [(0x10005, FileState.Problem), (0x20005, FileState.Cached), (0x30005, FileState.Cached), (0x10005, FileState.Cached), (0x1FFFF, FileState.Uploading), (0x2FFFF, FileState.Downloading)]);
        var slotsAt = (int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(36));
        (ulong, uint) Slot(int index) => (BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(slotsAt + index * 16)), BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(slotsAt + index * 16 + 8)));

        Assert.Equal((0x10005UL, 4u), Slot(5)); // the repeated hash kept its first state
        Assert.Equal((0x20005UL, 1u), Slot(6));
        Assert.Equal((0x30005UL, 1u), Slot(7));
        Assert.Equal((0UL, 0u), Slot(8));
        Assert.Equal((0x1FFFFUL, 2u), Slot(65535));
        Assert.Equal((0x2FFFFUL, 3u), Slot(0));
        Assert.Equal(5u, BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(44)));

        var many = Enumerable.Range(1, ShellImage.MaxEntries + 500).Select(i => ((ulong)i * 2654435761UL, FileState.Cached));
        ShellImage.Build(image, 1, 3, [], [], many);
        Assert.Equal((uint)ShellImage.MaxEntries, BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(44)));
        Assert.Equal(45875, ShellImage.MaxEntries);
    }

    [Fact]
    public void Folders_above_problems_and_uploads_show_them_and_cached_files_go_first_beyond_the_cap()
    {
        var table = ShellTable.From([
            new(@"\Sites\Sales\Documents\a\new.txt", FileState.Uploading),
            new(@"\Sites\Sales\Documents\a\bad.txt", FileState.Problem),
            new(@"\Sites\Sales\Documents\b\cached.txt", FileState.Cached),
            new(@"\Sites\Sales\Documents\b\coming.txt", FileState.Downloading),
            new(@"\OneDrive\x.txt", FileState.Uploading),
        ]);
        FileState? At(string path) => table.Entries.TryGetValue(ShellImage.KeyHash(path), out var e) ? e.State : null;

        Assert.Equal(FileState.Problem, At(@"\Sites\Sales\Documents\a"));
        Assert.Equal(FileState.Problem, At(@"\SITES\sales\Documents"));
        Assert.Equal(FileState.Problem, At(@"\Sites"));
        Assert.Equal(FileState.Uploading, At(@"\OneDrive"));
        Assert.Null(At(@"\Sites\Sales\Documents\b"));
        Assert.Null(At(@"\"));
        Assert.Equal(FileState.Downloading, At(@"\Sites\Sales\Documents\b\coming.txt"));

        var files = Enumerable.Range(0, ShellImage.MaxEntries + 100).Select(i => new VolumeFileState($@"\OneDrive\c\{i}.txt", FileState.Cached))
            .Concat(Enumerable.Range(0, 50).Select(i => new VolumeFileState($@"\OneDrive\u\{i}.txt", FileState.Uploading)));
        var capped = ShellTable.From(files);
        Assert.Equal(ShellImage.MaxEntries, capped.Entries.Count);
        Assert.Equal(52, capped.Entries.Values.Count(e => e.State == FileState.Uploading)); // 50 files, \OneDrive\u and \OneDrive
    }

    [Fact]
    public void Explorer_hears_of_each_changed_path_where_it_shows_or_of_folders_when_many_changed()
    {
        var before = ShellTable.From([new(@"\Sites\Sales\Documents\a.txt", FileState.Cached), new(@"\OneDrive\b.txt", FileState.Cached)]);
        var after = ShellTable.From([new(@"\Sites\Sales\Documents\a.txt", FileState.Uploading), new(@"\OneDrive\c.txt", FileState.Cached)]);

        var changed = ShellTable.Changed(before, after);
        Assert.Equal([@"\OneDrive\b.txt", @"\OneDrive\c.txt", @"\Sites", @"\Sites\Sales", @"\Sites\Sales\Documents", @"\Sites\Sales\Documents\a.txt"], changed.Order(StringComparer.Ordinal));

        var roots = new[] { Letter, Mapping, UncRoot };
        Assert.Equal([@"M:\Sites\Sales\Documents\a.txt", @"Z:\a.txt", @"\\VFS365\ann\Sites\Sales\Documents\a.txt"], ShellNotices.Forms(@"\Sites\Sales\Documents\a.txt", roots));
        Assert.Equal([@"M:\Sites\Sales\Documents", @"Z:\", @"\\VFS365\ann\Sites\Sales\Documents"], ShellNotices.Forms(@"\Sites\Sales\Documents", roots));
        Assert.Equal([@"M:\", @"\\VFS365\ann\"], ShellNotices.Forms(@"\", roots));

        var plan = ShellNotices.Plan([@"\OneDrive\b.txt"], roots);
        Assert.Equal([(ShellNotices.UpdateItem, @"M:\OneDrive\b.txt"), (ShellNotices.UpdateItem, @"\\VFS365\ann\OneDrive\b.txt")], plan);

        var many = Enumerable.Range(0, 300).Select(i => $@"\OneDrive\f{i % 3}\{i}.txt").ToList();
        Assert.Equal(6, ShellNotices.Plan(many, roots).Count(n => n.Event == ShellNotices.UpdateDir));
        Assert.Contains((ShellNotices.UpdateDir, @"M:\OneDrive\f1"), ShellNotices.Plan(many, roots));

        var everywhere = Enumerable.Range(0, 600).Select(i => $@"\OneDrive\f{i}\x.txt").ToList();
        Assert.Equal([(ShellNotices.UpdateDir, @"M:\"), (ShellNotices.UpdateDir, @"Z:\"), (ShellNotices.UpdateDir, @"\\VFS365\ann\")],
            ShellNotices.Plan(everywhere, roots));
        Assert.Empty(ShellNotices.Plan([], roots));
    }

    [Fact]
    public void Roots_name_the_volume_path_they_stand_for_and_strings_follow_the_ui_language()
    {
        var folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Z:"] = @"\\VFS365\ann\Sites\Sales\Documents", ["Y:"] = @"\\elsewhere\share" };

        Assert.Equal([Letter, Mapping, UncRoot], ExplorerMenu.ShellRoots(Unc, "M:", folders));
        Assert.Equal([UncRoot], ExplorerMenu.ShellRoots(Unc, null, new Dictionary<string, string>()));

        var english = ShellPublisher.MenuStrings(CultureInfo.GetCultureInfo("en-GB"));
        Assert.Equal(("Verb.Menu", "VFS365"), english[0]);
        Assert.Equal(["Verb.Menu", .. ExplorerVerbs.Verbs.Select(v => $"Verb.{v.Name}")], english.Select(s => s.Key));
        Assert.Contains(("Verb.browser", "Open in browser"), english);
        Assert.Contains(("Verb.browser", "Openen in browser"), ShellPublisher.MenuStrings(CultureInfo.GetCultureInfo("nl-NL")));
    }

    static byte[] ReadSection(string name)
    {
        using var mapped = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
        using var view = mapped.CreateViewAccessor(0, ShellImage.SectionSize, MemoryMappedFileAccess.Read);
        var bytes = new byte[ShellImage.SectionSize];
        view.ReadArray(0, bytes, 0, bytes.Length);
        return bytes;
    }

    static SecurityIdentifier User => WindowsIdentity.GetCurrent().User!;

    [Fact]
    public void The_section_takes_images_under_a_seqlock_is_used_again_and_clears()
    {
        var image = new byte[ShellImage.SectionSize];
        var length = ShellImage.Build(image, 42, 3, [Letter], [], [(7, FileState.Cached)]);
        Assert.Equal($@"Local\VFS365.Shell.{User.Value}", ShellSection.NameFor(User));
        Assert.StartsWith("S-1-", User.Value);

        using (var section = ShellSection.Open(name, User))
        {
            section.Write(image.AsSpan(0, length));
            var read = ReadSection(name);
            Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(read.AsSpan(8)));
            Assert.Equal(image[..8], read[..8]);
            Assert.Equal(image[12..length], read[12..length]);

            // Explorer keeps it open: a new agent's section is the same one
            using var again = ShellSection.Open(name, User);
            again.Write(image.AsSpan(0, length));
            Assert.Equal(4, BinaryPrimitives.ReadInt32LittleEndian(ReadSection(name).AsSpan(8)));

            section.Clear();
            read = ReadSection(name);
            Assert.Equal(6, BinaryPrimitives.ReadInt32LittleEndian(read.AsSpan(8)));
            Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(read.AsSpan(ShellImage.FlagsAt)));
            Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(read.AsSpan(ShellImage.RootsCountAt)));
            Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(read.AsSpan(ShellImage.EntryCountAt)));
            Assert.Equal(42u, BinaryPrimitives.ReadUInt32LittleEndian(read.AsSpan(ShellImage.ProcessAt)));
        }
        Assert.Throws<FileNotFoundException>(() => ReadSection(name)); // gone with its last handle
    }

    [Fact]
    public async Task The_publisher_writes_the_engines_states_tells_explorer_and_clears_at_a_clean_stop()
    {
        var api = new FakeDriveApi();
        var engine = new DriveEngine(DriveNamespace.Build(new MyDrive("b!me", "https://x-my.sharepoint.com/personal/u"), [], (_, _) => Task.FromResult("unused")),
            api, new ContentCache(Path.Combine(directory, "cache"), api), new DriveEngineOptions { StagingDirectory = Path.Combine(directory, "staging") });
        var told = new List<(int, string)>();
        var publisher = new ShellPublisher(engine, ShellSection.Open(name, User), overlays: true, [("Verb.Menu", "VFS365")], _ => { },
            (what, path) => told.Add((what, path)), background: false);
        publisher.SetRoots([Letter, UncRoot]);
        publisher.Publish();
        using var explorer = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read); // keeps it after the publisher let go
        var read = ReadSection(name);
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(read.AsSpan(ShellImage.FlagsAt)));
        Assert.Equal((uint)Environment.ProcessId, BinaryPrimitives.ReadUInt32LittleEndian(read.AsSpan(ShellImage.ProcessAt)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(read.AsSpan(ShellImage.RootsCountAt)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(read.AsSpan(ShellImage.EntryCountAt)));
        Assert.Empty(told);

        var entry = await engine.CreateAsync(@"\OneDrive\new.txt", false, default);
        await File.WriteAllTextAsync(entry.StagingPath!, "x");
        engine.MarkDirty(entry);
        publisher.Publish();

        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(ReadSection(name).AsSpan(ShellImage.EntryCountAt))); // the file and \OneDrive
        Assert.Equal([@"M:\OneDrive", @"M:\OneDrive\new.txt", @"\\VFS365\ann\OneDrive", @"\\VFS365\ann\OneDrive\new.txt"],
            told.Select(t => t.Item2).Order(StringComparer.Ordinal));
        Assert.All(told, t => Assert.Equal(ShellNotices.UpdateItem, t.Item1));

        // Nothing changed: nothing written or told
        told.Clear();
        publisher.Publish();
        Assert.Empty(told);

        publisher.Dispose();
        read = ReadSection(name);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(read.AsSpan(ShellImage.FlagsAt)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(read.AsSpan(ShellImage.EntryCountAt)));
        Assert.Contains((ShellNotices.UpdateItem, @"M:\OneDrive\new.txt"), told);
        Assert.Contains((ShellNotices.UpdateDir, @"M:\"), told);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(directory, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
