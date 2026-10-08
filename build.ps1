<#
    .SYNOPSIS
    Builds, tests, runs or releases VFS365. Finds a .NET 10 SDK in $env:VFS365_DOTNET, on PATH or under %LOCALAPPDATA%\dotnet-portable.

    .DESCRIPTION
    release: builds the version in VERSION (it needs a "## <version>" entry in CHANGELOG.md): runs the tests, publishes
    self-contained x64 and ARM64 builds, wraps each in an MSI and in a setup program that installs WinFsp first, and puts these, the
    unmodified WinFsp MSI, the policy templates (also zipped), the monitoring dashboard, licence, notices, guides and checksums in release\<version>\. The
    version's CHANGELOG entry goes to artifacts\release-notes.md. GitHub Actions runs this when VERSION changes on main.

    bump: raises the patch number in VERSION.

    np: compiles the network provider (src\Vfs365.Np) for x64, ARM64 and x86 into artifacts\np\. Uses Zig from $env:VFS365_ZIG, else a
    pinned portable copy in %LOCALAPPDATA%\zig-portable, downloaded and checked on first use. release does this too.

    .EXAMPLE
    ./build.ps1 test
    ./build.ps1 run discover --drives
    ./build.ps1 bump
    ./build.ps1 release
#>
param(
    [ValidateSet('build','test','run','np','bump','release')][String]$Task = 'test',
    [Switch]$Release
)
$ErrorActionPreference = 'Stop'
$configuration = if($Release){ 'Release' }else{ 'Debug' }

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

#vfs365np.dll for x64, arm64 or x86 in artifacts\np\<arch>\: kernel32 only, exports named by vfs365np.def
function Build-NetworkProvider([String]$zigExe, [String]$arch){
    $target = @{ x64 = 'x86_64-windows-gnu'; arm64 = 'aarch64-windows-gnu'; x86 = 'x86-windows-gnu' }[$arch]
    $entry = if($arch -eq 'x86'){ 'DllMain@12' }else{ 'DllMain' }
    $source = Join-Path $PSScriptRoot 'src\Vfs365.Np'
    $dll = Join-Path $PSScriptRoot "artifacts\np\$arch\vfs365np.dll"
    New-Item -ItemType Directory -Force -Path (Split-Path $dll) | Out-Null
    & $zigExe cc -target $target -shared -O2 -nostdlib -Wall -Wextra -Werror `
        -isystem (Join-Path (Split-Path $zigExe) 'lib\libc\include\any-windows-any') `
        -o $dll (Join-Path $source 'vfs365np.c') (Join-Path $source 'vfs365np.def') -lkernel32 "-Wl,--entry=$entry" | Out-Host
    if($LASTEXITCODE -ne 0){ Throw "zig cc for $arch failed ($LASTEXITCODE)" }
    return $dll
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

    Write-Host "Releasing $version"
    Invoke-Dotnet @('test', (Join-Path $PSScriptRoot 'Vfs365.slnx'), '-c', 'Release', '--nologo', '-v', 'q')

    $winFspMsi = Join-Path $PSScriptRoot "artifacts\winfsp\$($winFsp.Name)"
    if(-not (Test-Path -LiteralPath $winFspMsi)){
        New-Item -ItemType Directory -Force -Path (Split-Path $winFspMsi) | Out-Null
        Invoke-WebRequest -Uri $winFsp.Url -OutFile $winFspMsi
    }
    if((Get-FileHash -LiteralPath $winFspMsi -Algorithm SHA256).Hash -ne $winFsp.Sha256){
        Remove-Item -LiteralPath $winFspMsi
        Throw "$($winFsp.Name) does not match its pinned SHA256; download it again or update the pin"
    }

    $zigExe = Get-Zig
    $npX86 = Build-NetworkProvider $zigExe 'x86'

    $output = Join-Path $PSScriptRoot "release\$version"
    if(Test-Path -LiteralPath $output){ Remove-Item -LiteralPath $output -Recurse -Force }
    New-Item -ItemType Directory -Path $output | Out-Null
    foreach($arch in 'x64', 'arm64'){
        $np = Build-NetworkProvider $zigExe $arch
        $publish = Join-Path $PSScriptRoot "artifacts\publish\win-$arch"
        $msiFolder = Join-Path $PSScriptRoot "artifacts\installer\$arch"
        $bundleFolder = Join-Path $PSScriptRoot "artifacts\bundle\$arch"
        foreach($folder in $publish, $msiFolder, $bundleFolder){
            if(Test-Path -LiteralPath $folder){ Remove-Item -LiteralPath $folder -Recurse -Force }
        }
        foreach($project in 'src\Vfs365.Cli\Vfs365.Cli.csproj', 'src\Vfs365.Background\Vfs365.Background.csproj'){
            Invoke-Dotnet @('publish', (Join-Path $PSScriptRoot $project), '-c', 'Release', '-r', "win-$arch", '--self-contained', "-p:Version=$version", '-o', $publish, '--nologo', '-v', 'q')
        }
        foreach($file in 'LICENSE', 'THIRD-PARTY-NOTICES.md'){ Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $publish }
        Invoke-Dotnet @('build', (Join-Path $PSScriptRoot 'installer\Vfs365.Installer.wixproj'), '-c', 'Release', "-p:Platform=$arch", "-p:Version=$version", "-p:PublishDir=$publish", "-p:NpDll=$np", "-p:NpDllX86=$npX86", '-o', $msiFolder, '--nologo', '-v', 'q')
        $msi = Get-ChildItem -LiteralPath $msiFolder -Filter '*.msi' -Recurse | Select-Object -First 1
        Copy-Item -LiteralPath $msi.FullName -Destination (Join-Path $output "VFS365-$version-$arch.msi")
        Invoke-Dotnet @('build', (Join-Path $PSScriptRoot 'installer\bundle\Vfs365.Bundle.wixproj'), '-c', 'Release', "-p:Platform=$arch", "-p:Version=$version", "-p:VfsMsi=$($msi.FullName)", "-p:WinFspMsi=$winFspMsi", '-o', $bundleFolder, '--nologo', '-v', 'q')
        $setup = Get-ChildItem -LiteralPath $bundleFolder -Filter '*.exe' -Recurse | Select-Object -First 1
        Copy-Item -LiteralPath $setup.FullName -Destination (Join-Path $output "VFS365-$version-$arch-setup.exe")
    }
    Copy-Item -LiteralPath $winFspMsi -Destination $output
    foreach($file in 'LICENSE', 'THIRD-PARTY-NOTICES.md'){ Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $output }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'policy') -Destination (Join-Path $output 'policy') -Recurse
    Compress-Archive -Path (Join-Path $PSScriptRoot 'policy\*') -DestinationPath (Join-Path $output "VFS365-$version-policy.zip")
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'monitoring\vfs365-monitoring.html') -Destination $output
    foreach($doc in 'DEPLOYMENT.md', 'APP-REGISTRATION.md'){ Copy-Item -LiteralPath (Join-Path $PSScriptRoot "docs\$doc") -Destination $output }
    Get-ChildItem -LiteralPath $output -Recurse -File | Where-Object Name -ne 'SHA256SUMS.txt' | ForEach-Object {
        "{0}  {1}" -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.FullName.Substring($output.Length + 1)
    } | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ascii
    Write-Host "Release $version in $output"
    Get-ChildItem -LiteralPath $output -Recurse -File | ForEach-Object { "  {0,-40} {1,8:N1} MB" -f $_.FullName.Substring($output.Length + 1), ($_.Length / 1MB) }
    Exit 0
}

& $dotnet $Task (Join-Path $PSScriptRoot 'Vfs365.slnx') -c $configuration
Exit $LASTEXITCODE
