using System.IO.Compression;
using Vfs365.Agent;

namespace Vfs365.Tests;

/// <summary>The Windows 11 menu package's identity as the agent reads it; registration itself needs a signed package and isn't run here.</summary>
public sealed class ShellPackageTests
{
    [Theory]
    [InlineData("CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US", "8wekyb3d8bbwe")]
    [InlineData("CN=Microsoft Windows, O=Microsoft Corporation, L=Redmond, S=Washington, C=US", "cw5n1h2txyewy")]
    public void Publisher_ids_match_Windows(string publisher, string id) => Assert.Equal(id, ShellPackage.PublisherId(publisher));

    [Fact]
    public void Versions_come_from_full_names()
    {
        Assert.Equal(new Version(0, 7, 1, 0), ShellPackage.VersionOf("JSolve.VFS365.Shell_0.7.1.0_neutral__abcdefghjkmnp"));
        Assert.Null(ShellPackage.VersionOf("JSolve.VFS365.Shell_abcdefghjkmnp"));
    }

    [Fact]
    public void The_identity_is_read_from_the_package_manifest()
    {
        var folder = Directory.CreateTempSubdirectory("vfs365-package-");
        try
        {
            var msix = Path.Combine(folder.FullName, ShellPackage.FileName);
            using (var zip = ZipFile.Open(msix, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry("AppxManifest.xml").Open()))
            {
                // The repository's manifest, with what build.ps1 fills in
                var manifest = File.ReadAllText(Path.Combine(Repository(), @"src\Vfs365.Shell\package\AppxManifest.xml"))
                    .Replace("Publisher=\"CN=Unsigned\"", "Publisher=\"CN=JSolve B.V., O=JSolve B.V., C=NL\"").Replace("Version=\"0.0.0.0\"", "Version=\"0.7.1.0\"");
                writer.Write(manifest);
            }
            var identity = ShellPackage.ReadIdentity(msix);
            Assert.NotNull(identity);
            Assert.Equal(ShellPackage.PackageName, identity.Name);
            Assert.Equal(new Version(0, 7, 1, 0), identity.Version);
            Assert.Equal($"{ShellPackage.PackageName}_{ShellPackage.PublisherId("CN=JSolve B.V., O=JSolve B.V., C=NL")}", identity.FamilyName);
            Assert.Null(ShellPackage.ReadIdentity(Path.Combine(folder.FullName, "missing.msix")));
        }
        finally
        {
            folder.Delete(true);
        }
    }

    [Fact]
    public void Unknown_families_have_no_registered_packages() =>
        Assert.Empty(ShellPackage.Registered($"{ShellPackage.PackageName}_0000000000000"));

    [Fact]
    public void Paths_are_quoted_for_PowerShell() => Assert.Equal("'C:\\Ann''s\\VFS365'", ShellPackage.Quote(@"C:\Ann's\VFS365"));

    static string Repository()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "Vfs365.slnx")))
            {
                return folder.FullName;
            }
        }
        throw new DirectoryNotFoundException("Vfs365.slnx not found above the test folder");
    }
}
