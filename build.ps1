<#
    .SYNOPSIS
    Builds, tests, runs or releases VFS365. Finds a .NET 10 SDK in $env:VFS365_DOTNET, on PATH or under %LOCALAPPDATA%\dotnet-portable.

    .DESCRIPTION
    release: builds the version in VERSION (it needs a "## <version>" entry in CHANGELOG.md): runs the tests, publishes
    self-contained x64 and ARM64 builds, wraps each in an MSI and in a setup program that installs WinFsp first, and puts these, the
    unmodified WinFsp MSI, the policy templates (also zipped), the monitoring dashboard, licence, notices, guides and checksums in release\<version>\. The
    version's CHANGELOG entry goes to artifacts\release-notes.md. SHA256SUMS.txt lists the files a GitHub release publishes (the
    installers, the policy zip and the dashboard). GitHub Actions runs this when VERSION changes on main.

    release runs in two stages, or one of them with -BuildOnly or -PackageOnly. Build compiles the network provider and publishes the
    programs into artifacts\, unsigned. Package signs them, builds the MSIs and setups and fills release\<version>\. GitHub Actions runs
    the stages in separate jobs, so the signing sign-in is never on the machine that compiles.

    bump: raises the patch number in VERSION.

    np: compiles the network provider (src\Vfs365.Np) for x64, ARM64 and x86 into artifacts\np\. Uses Zig from $env:VFS365_ZIG, else a
    pinned portable copy in %LOCALAPPDATA%\zig-portable, downloaded and checked on first use. release does this too.

    sign <files>: Authenticode-signs files in place with Azure Artifact Signing (Trusted Signing) when $env:VFS365_SIGN_DLIB (path to
    Azure.CodeSigning.Dlib.dll from the Microsoft.ArtifactSigning.Client package) and $env:VFS365_SIGN_METADATA (its metadata.json:
    Endpoint, CodeSigningAccountName, CertificateProfileName) are set; otherwise does nothing. Uses the newest Windows SDK signtool, or
    $env:VFS365_SIGNTOOL. With them set, release signs vfs365np.dll, VFS365's own exes and DLLs, the MSIs and the setups, then fails
    when any program, DLL, MSI or setup in the release, or any program or DLL inside the MSIs, lacks a valid signature.

    -NoTest skips the tests in release (CI runs them in a job of their own).

    .EXAMPLE
    ./build.ps1 test
    ./build.ps1 run discover --drives
    ./build.ps1 bump
    ./build.ps1 release
#>
param(
    [ValidateSet('build','test','run','np','bump','release','sign')][String]$Task = 'test',
    [Switch]$Release,
    [Switch]$NoTest,
    [Switch]$BuildOnly,
    [Switch]$PackageOnly
)
$ErrorActionPreference = 'Stop'
$configuration = if($Release){ 'Release' }else{ 'Debug' }

$signing = [Bool]($env:VFS365_SIGN_DLIB -and $env:VFS365_SIGN_METADATA)
function Invoke-Sign([String[]]$files){
    if(-not $signing -or -not $files){ return }
    $signtool = if($env:VFS365_SIGNTOOL){ $env:VFS365_SIGNTOOL }else{
        Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
            Sort-Object { [Version]$_.Directory.Parent.Name } -Descending | Select-Object -First 1 -ExpandProperty FullName
    }
    if(-not $signtool){ Throw "signtool.exe not found: install the Windows SDK or set VFS365_SIGNTOOL" }
    & $signtool sign /v /fd SHA256 /tr 'http://timestamp.acs.microsoft.com' /td SHA256 /dlib $env:VFS365_SIGN_DLIB /dmdf $env:VFS365_SIGN_METADATA @files | Out-Host
    if($LASTEXITCODE -ne 0){ Throw "signtool failed ($LASTEXITCODE)" }
}

#Called by the WiX projects for the MSI and the setup (SignOutput=true)
if($Task -eq 'sign'){
    Invoke-Sign $args
    Exit 0
}

$candidates = @()
if($env:VFS365_DOTNET){ $candidates += $env:VFS365_DOTNET }
$onPath = Get-Command dotnet -ErrorAction SilentlyContinue
if($onPath){ $candidates += $onPath.Source }
$candidates += @(Get-ChildItem "$env:LOCALAPPDATA\dotnet-portable\dotnet-10*\dotnet.exe" -ErrorAction SilentlyContinue | Sort-Object Name -Descending | ForEach-Object FullName)

$dotnet = $candidates | Where-Object { (Test-Path $_) -and (@(& $_ --list-sdks) -match '^10\.') } | Select-Object -First 1
if(-not $dotnet){ Throw "No .NET 10 SDK found. Install one, or extract the portable zip to %LOCALAPPDATA%\dotnet-portable\dotnet-10.x.y-win-<arch> (see CLAUDE.md)" }

#WinFsp bundled with every release, unmodified; checked against the digest GitHub publishes
$winFsp = @{
    Name = 'winfsp-2.1.25156.msi'
    Url = 'https://github.com/winfsp/winfsp/releases/download/v2.1/winfsp-2.1.25156.msi'
    Sha256 = '073a70e00f77423e34bed98b86e600def93393ba5822204fac57a29324db9f7a'
}

#Zig (zig cc) builds the network provider without a C runtime; a build tool only, nothing of it ships
$zig = @{
    Version = '0.17.0'
    Sha256 = @{
        aarch64 = '0a59d91fa1cb40cf068e9b0954434ce973500c7a2ea749f1e01af62cdab52d26'
        x86_64 = 'b5663f69581dcf391293fbf16c06cb80d81d806545ce618b4d0bab7f0eb8c428'
    }
}

$env:DOTNET_ROOT = Split-Path -Parent $dotnet
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

function Invoke-Dotnet([String[]]$arguments){
    & $dotnet @arguments
    if($LASTEXITCODE -ne 0){ Throw "dotnet $($arguments[0]) failed ($LASTEXITCODE)" }
}

function Get-Zig(){
    if($env:VFS365_ZIG){ return $env:VFS365_ZIG }
    $hostArch = if([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64'){ 'aarch64' }else{ 'x86_64' }
    $name = "zig-$hostArch-windows-$($zig.Version)"
    $root = Join-Path $env:LOCALAPPDATA 'zig-portable'
    $exe = Join-Path $root "$name\zig.exe"
    if(Test-Path -LiteralPath $exe){ return $exe }
    New-Item -ItemType Directory -Force -Path $root | Out-Null
    $zip = Join-Path $root "$name.zip"
    Invoke-WebRequest -Uri "https://ziglang.org/download/$($zig.Version)/$name.zip" -OutFile $zip
    if((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ne $zig.Sha256[$hostArch]){
        Remove-Item -LiteralPath $zip
        Throw "$name.zip does not match its pinned SHA256"
    }
    Expand-Archive -LiteralPath $zip -DestinationPath $root
    return $exe
}

#vfs365np.dll for x64, arm64 or x86 in artifacts\np\<arch>\: kernel32 and advapi32 only, exports named by vfs365np.def
function Build-NetworkProvider([String]$zigExe, [String]$arch){
    $target = @{ x64 = 'x86_64-windows-gnu'; arm64 = 'aarch64-windows-gnu'; x86 = 'x86-windows-gnu' }[$arch]
    $entry = if($arch -eq 'x86'){ 'DllMain@12' }else{ 'DllMain' }
    $source = Join-Path $PSScriptRoot 'src\Vfs365.Np'
    $dll = Join-Path $PSScriptRoot "artifacts\np\$arch\vfs365np.dll"
    New-Item -ItemType Directory -Force -Path (Split-Path $dll) | Out-Null
    & $zigExe cc -target $target -shared -O2 -nostdlib -Wall -Wextra -Werror `
        -isystem (Join-Path (Split-Path $zigExe) 'lib\libc\include\any-windows-any') `
        -o $dll (Join-Path $source 'vfs365np.c') (Join-Path $source 'vfs365np.def') -lkernel32 -ladvapi32 "-Wl,--entry=$entry" | Out-Host
    if($LASTEXITCODE -ne 0){ Throw "zig cc for $arch failed ($LASTEXITCODE)" }
    return $dll
}

#The files an MSI installs, unpacked from its embedded cabinet (Windows Installer's API and expand.exe; nothing is installed): Name, FullName
function Expand-Msi([String]$msi, [String]$folder){
    if(-not ('MsiTable' -as [Type])){
        Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

public static class MsiTable
{
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] static extern uint MsiOpenDatabaseW(string path, IntPtr persist, out IntPtr database);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] static extern uint MsiDatabaseOpenViewW(IntPtr database, string query, out IntPtr view);
    [DllImport("msi.dll")] static extern uint MsiViewExecute(IntPtr view, IntPtr record);
    [DllImport("msi.dll")] static extern uint MsiViewFetch(IntPtr view, out IntPtr record);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] static extern uint MsiRecordGetStringW(IntPtr record, uint field, StringBuilder value, ref uint size);
    [DllImport("msi.dll")] static extern uint MsiRecordReadStream(IntPtr record, uint field, byte[] buffer, ref uint size);
    [DllImport("msi.dll")] static extern uint MsiCloseHandle(IntPtr handle);

    // The string columns of a query's rows; with saveTo (PowerShell passes $null as ""), the next column is a stream saved as saveTo\<first column>
    public static List<string[]> Query(string msi, string sql, int columns, string saveTo)
    {
        var rows = new List<string[]>();
        IntPtr database, view = IntPtr.Zero, record;
        Check(MsiOpenDatabaseW(msi, IntPtr.Zero, out database));
        try
        {
            Check(MsiDatabaseOpenViewW(database, sql, out view));
            Check(MsiViewExecute(view, IntPtr.Zero));
            while (MsiViewFetch(view, out record) == 0)
            {
                try
                {
                    var row = new string[columns];
                    for (var i = 0; i < columns; i++)
                    {
                        var size = 1024u;
                        var text = new StringBuilder((int)size);
                        Check(MsiRecordGetStringW(record, (uint)i + 1, text, ref size));
                        row[i] = text.ToString();
                    }
                    if (!string.IsNullOrEmpty(saveTo))
                    {
                        using (var file = File.Create(Path.Combine(saveTo, row[0])))
                        {
                            var buffer = new byte[1 << 20];
                            for (var read = (uint)buffer.Length; ; read = (uint)buffer.Length)
                            {
                                Check(MsiRecordReadStream(record, (uint)columns + 1, buffer, ref read));
                                if (read == 0) break;
                                file.Write(buffer, 0, (int)read);
                            }
                        }
                    }
                    rows.Add(row);
                }
                finally
                {
                    MsiCloseHandle(record);
                }
            }
        }
        finally
        {
            MsiCloseHandle(view);
            MsiCloseHandle(database);
        }
        return rows;
    }

    static void Check(uint error)
    {
        if (error != 0) throw new System.ComponentModel.Win32Exception((int)error);
    }
}
'@
    }
    New-Item -ItemType Directory -Force -Path $folder | Out-Null
    $cabinets = [MsiTable]::Query($msi, 'SELECT `Cabinet` FROM `Media`', 1, $null) | ForEach-Object { $_[0] } | Where-Object { $_ -like '#*' }
    foreach($cabinet in $cabinets){
        [MsiTable]::Query($msi, "SELECT ``Name``, ``Data`` FROM ``_Streams`` WHERE ``Name`` = '$($cabinet.Substring(1))'", 1, $folder) | Out-Null
        $cab = Join-Path $folder $cabinet.Substring(1)
        & "$env:SystemRoot\System32\expand.exe" -F:* $cab $folder | Out-Null
        if($LASTEXITCODE -ne 0){ Throw "expand.exe could not unpack $cab from $msi ($LASTEXITCODE)" }
        Remove-Item -LiteralPath $cab
    }
    #Files are named by their File table key in the cabinet; FileName is "short|long" or one name
    [MsiTable]::Query($msi, 'SELECT `File`, `FileName` FROM `File`', 2, $null) | ForEach-Object {
        [PSCustomObject]@{ Name = $_[1].Split('|')[-1]; FullName = Join-Path $folder $_[0] }
    }
}

#Signing set up: every program, DLL, MSI and setup in the release, and every program and DLL the MSIs install, must be validly signed
function Assert-Signed([String]$folder){
    #winfsp.net's NuGet package has winfsp-msil.dll unsigned, and WinFsp's files ship unmodified (its FLOSS exception), so it can't be signed here
    $unsignedAllowed = @('winfsp-msil.dll')
    $unpacked = Join-Path $PSScriptRoot 'artifacts\signature-check'
    if(Test-Path -LiteralPath $unpacked){ Remove-Item -LiteralPath $unpacked -Recurse -Force }
    $files = @(Get-ChildItem -LiteralPath $folder -File | Where-Object Extension -in '.exe', '.dll', '.msi' |
        ForEach-Object { [PSCustomObject]@{ Name = $_.Name; FullName = $_.FullName; Display = $_.Name } })
    foreach($msi in @($files | Where-Object Name -like '*.msi')){
        $files += @(Expand-Msi $msi.FullName (Join-Path $unpacked ([IO.Path]::GetFileNameWithoutExtension($msi.Name))) | Where-Object Name -match '\.(exe|dll|sys)$' |
            ForEach-Object { $_ | Add-Member Display "$($msi.Name): $($_.Name)" -PassThru })
    }
    $failed = foreach($file in $files){
        $status = (Get-AuthenticodeSignature -LiteralPath $file.FullName).Status
        if($status -ne 'Valid' -and $file.Name -notin $unsignedAllowed){ "  $($file.Display): $status" }
    }
    if($failed){ Throw "Signing is set up, but these have no valid signature:`n$($failed -join "`n")" }
    Write-Host "Signatures: $($files.Count) files checked, valid except the allowed $($unsignedAllowed -join ', ')"
}

if($Task -eq 'np'){
    $zigExe = Get-Zig
    foreach($arch in 'x64', 'arm64', 'x86'){ Build-NetworkProvider $zigExe $arch }
    Exit 0
}

if($Task -eq 'run'){
    #remaining arguments go to the agent. unmount skips the build: a running mount locks the exe
    if($args[0] -ne 'unmount'){
        & $dotnet build (Join-Path $PSScriptRoot 'src\Vfs365.Cli\Vfs365.Cli.csproj') -c $configuration --nologo -v q
        if($LASTEXITCODE -ne 0){ Exit $LASTEXITCODE }
    }
    & (Join-Path $PSScriptRoot "src\Vfs365.Cli\bin\$configuration\net10.0-windows\vfs365.exe") @args
    Exit $LASTEXITCODE
}

$versionFile = Join-Path $PSScriptRoot 'VERSION'
$version = (Get-Content -LiteralPath $versionFile -Raw).Trim()
if($version -notmatch '^\d+\.\d+\.\d+$'){ Throw "VERSION holds '$version'; expected major.minor.patch" }

if($Task -eq 'bump'){
    $parts = $version.Split('.')
    $parts[2] = [String]([Int]$parts[2] + 1)
    $version = $parts -join '.'
    Set-Content -LiteralPath $versionFile -Value $version -Encoding ascii -NoNewline
    Write-Host "VERSION is now $version. Add a '## $version' entry to CHANGELOG.md before releasing."
    Exit 0
}

if($Task -eq 'release'){
    #The version's CHANGELOG entry: from its heading up to the next one
    $changelog = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'CHANGELOG.md')
    $start = [Array]::FindIndex($changelog, [Predicate[String]]{ param($line) $line -match "^## $([Regex]::Escape($version))(\s|$)" })
    if($start -lt 0){ Throw "CHANGELOG.md has no '## $version' entry" }
    $end = [Array]::FindIndex($changelog, $start + 1, [Predicate[String]]{ param($line) $line -match '^## ' })
    $notes = $changelog[($start + 1)..$(if($end -lt 0){ $changelog.Count - 1 }else{ $end - 1 })] -join "`n"
    New-Item -ItemType Directory -Force -Path (Join-Path $PSScriptRoot 'artifacts') | Out-Null
    Set-Content -LiteralPath (Join-Path $PSScriptRoot 'artifacts\release-notes.md') -Value $notes.Trim() -Encoding utf8

    Write-Host "Releasing $version$(if($signing){ ', signed' })$(if($BuildOnly){ ', Build stage' }elseif($PackageOnly){ ', Package stage' })"
    $winFspMsi = Join-Path $PSScriptRoot "artifacts\winfsp\$($winFsp.Name)"
    $np = [ordered]@{}
    foreach($arch in 'x86', 'x64', 'arm64'){ $np[$arch] = Join-Path $PSScriptRoot "artifacts\np\$arch\vfs365np.dll" }
    $publish = [ordered]@{}
    foreach($arch in 'x64', 'arm64'){ $publish[$arch] = Join-Path $PSScriptRoot "artifacts\publish\win-$arch" }

    #Build: everything that compiles, unsigned
    if(-not $PackageOnly){
        if(-not $NoTest){ Invoke-Dotnet @('test', (Join-Path $PSScriptRoot 'Vfs365.slnx'), '-c', 'Release', '--nologo', '-v', 'q') }
        if(-not (Test-Path -LiteralPath $winFspMsi)){
            New-Item -ItemType Directory -Force -Path (Split-Path $winFspMsi) | Out-Null
            Invoke-WebRequest -Uri $winFsp.Url -OutFile $winFspMsi
        }
        $zigExe = Get-Zig
        foreach($arch in $np.Keys){ Build-NetworkProvider $zigExe $arch | Out-Null }
        foreach($arch in $publish.Keys){
            if(Test-Path -LiteralPath $publish[$arch]){ Remove-Item -LiteralPath $publish[$arch] -Recurse -Force }
            foreach($project in 'src\Vfs365.Cli\Vfs365.Cli.csproj', 'src\Vfs365.Background\Vfs365.Background.csproj'){
                Invoke-Dotnet @('publish', (Join-Path $PSScriptRoot $project), '-c', 'Release', '-r', "win-$arch", '--self-contained', "-p:Version=$version", '-o', $publish[$arch], '--nologo', '-v', 'q')
            }
            foreach($file in 'LICENSE', 'THIRD-PARTY-NOTICES.md'){ Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $publish[$arch] }
        }
        if($BuildOnly){
            Write-Host "Built $version in artifacts\ (np, publish, winfsp); the Package stage signs and packages it"
            Exit 0
        }
    }

    #Package: signs what the Build stage made (here or in another job) and wraps it in MSIs and setups
    foreach($path in @($winFspMsi) + @($np.Values) + @($publish.Values)){
        if(-not (Test-Path -LiteralPath $path)){ Throw "$path is missing: run the Build stage first" }
    }
    if((Get-FileHash -LiteralPath $winFspMsi -Algorithm SHA256).Hash -ne $winFsp.Sha256){
        Remove-Item -LiteralPath $winFspMsi
        Throw "$($winFsp.Name) does not match its pinned SHA256; download it again or update the pin"
    }
    $signOutput = "-p:SignOutput=$($signing.ToString().ToLower())"
    Invoke-Sign $np['x86']

    $output = Join-Path $PSScriptRoot "release\$version"
    if(Test-Path -LiteralPath $output){ Remove-Item -LiteralPath $output -Recurse -Force }
    New-Item -ItemType Directory -Path $output | Out-Null
    foreach($arch in $publish.Keys){
        Invoke-Sign $np[$arch]
        #Only VFS365's own binaries; the .NET runtime files carry Microsoft's signatures
        Invoke-Sign @(Get-ChildItem -LiteralPath $publish[$arch] -File | Where-Object Name -match '^vfs365.*\.(exe|dll)$' | ForEach-Object FullName)
        $msiFolder = Join-Path $PSScriptRoot "artifacts\installer\$arch"
        $bundleFolder = Join-Path $PSScriptRoot "artifacts\bundle\$arch"
        foreach($folder in $msiFolder, $bundleFolder){
            if(Test-Path -LiteralPath $folder){ Remove-Item -LiteralPath $folder -Recurse -Force }
        }
        Invoke-Dotnet @('build', (Join-Path $PSScriptRoot 'installer\Vfs365.Installer.wixproj'), '-c', 'Release', "-p:Platform=$arch", "-p:Version=$version", "-p:PublishDir=$($publish[$arch])", "-p:NpDll=$($np[$arch])", "-p:NpDllX86=$($np['x86'])", $signOutput, '-o', $msiFolder, '--nologo', '-v', 'q')
        $msi = Get-ChildItem -LiteralPath $msiFolder -Filter '*.msi' -Recurse | Select-Object -First 1
        Copy-Item -LiteralPath $msi.FullName -Destination (Join-Path $output "VFS365-$version-$arch.msi")
        Invoke-Dotnet @('build', (Join-Path $PSScriptRoot 'installer\bundle\Vfs365.Bundle.wixproj'), '-c', 'Release', "-p:Platform=$arch", "-p:Version=$version", "-p:VfsMsi=$($msi.FullName)", "-p:WinFspMsi=$winFspMsi", $signOutput, '-o', $bundleFolder, '--nologo', '-v', 'q')
        $setup = Get-ChildItem -LiteralPath $bundleFolder -Filter '*.exe' -Recurse | Select-Object -First 1
        Copy-Item -LiteralPath $setup.FullName -Destination (Join-Path $output "VFS365-$version-$arch-setup.exe")
    }
    Copy-Item -LiteralPath $winFspMsi -Destination $output
    foreach($file in 'LICENSE', 'THIRD-PARTY-NOTICES.md'){ Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $output }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'policy') -Destination (Join-Path $output 'policy') -Recurse
    #As PolicyDefinitions lays them out: VFS365.admx and en-US\VFS365.adml at the top
    Compress-Archive -Path (Join-Path $PSScriptRoot 'policy\*') -DestinationPath (Join-Path $output "VFS365-$version-policy.zip")
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'monitoring\vfs365-monitoring.html') -Destination $output
    foreach($doc in 'DEPLOYMENT.md', 'APP-REGISTRATION.md'){ Copy-Item -LiteralPath (Join-Path $PSScriptRoot "docs\$doc") -Destination $output }
    if($signing){ Assert-Signed $output }
    #Exactly the files a GitHub release publishes (release.yml uploads what this lists)
    Get-ChildItem -LiteralPath $output -File | Where-Object Extension -in '.exe', '.msi', '.zip', '.html' | Sort-Object Name | ForEach-Object {
        "{0}  {1}" -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.Name
    } | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ascii
    Write-Host "Release $version in $output"
    Get-ChildItem -LiteralPath $output -Recurse -File | ForEach-Object { "  {0,-40} {1,8:N1} MB" -f $_.FullName.Substring($output.Length + 1), ($_.Length / 1MB) }
    Exit 0
}

& $dotnet $Task (Join-Path $PSScriptRoot 'Vfs365.slnx') -c $configuration
Exit $LASTEXITCODE
