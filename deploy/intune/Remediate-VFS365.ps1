<#
    .SYNOPSIS
    Intune Remediations remediation script for VFS365: puts VFS365's network provider back in Windows' provider order
    ('vfs365.exe machine-setup'), starts the agent in every signed-in session without one ('vfs365.exe machine-start'), then checks
    health again and prints one summary line.

    .DESCRIPTION
    Run as SYSTEM (the Remediations default), in 64-bit PowerShell. Windows PowerShell 5.1 or later. Both steps change nothing that is
    already right. What they can't fix is reported, not hidden: a user who has to sign in, a missing WinFsp, a policy that keeps
    replacing the provider order. Exit 0: healthy afterwards; exit 1: still unhealthy (the summary says why). See docs/DEPLOYMENT.md.
#>

#Program Files of the OS, also when Intune runs the script in 32-bit PowerShell
function Get-Vfs365Exe(){
    $programFiles = if($env:ProgramW6432){ $env:ProgramW6432 }else{ $env:ProgramFiles }
    return Join-Path $programFiles 'VFS365\vfs365.exe'
}

#The health report as an object with the exit code; Report is $null when the output isn't the JSON report
function Get-Vfs365Health([String]$exe){
    $output = (& $exe health --all-users --json | Out-String).Trim()
    $code = $LASTEXITCODE
    $report = $null
    try{
        $report = $output | ConvertFrom-Json
    }catch{
        $report = $null
    }
    return [PSCustomObject]@{ ExitCode = $code; Report = $report; Output = $output }
}

#A command's output as one line
function Get-Vfs365Step([String]$exe, [String]$step){
    $lines = @(& $exe $step | ForEach-Object { "$_".Trim() } | Where-Object { $_ })
    if($lines.Count -eq 0){
        return "$step done"
    }
    return "${step}: $($lines -join '; ')"
}

$exe = Get-Vfs365Exe
if(-not (Test-Path -LiteralPath $exe)){
    Write-Output "VFS365 is not installed ($exe); install it first"
    Exit 1
}
$steps = @((Get-Vfs365Step $exe 'machine-setup'), (Get-Vfs365Step $exe 'machine-start'))

#An agent just started needs a moment to mount: up to a minute
$started = $steps[1] -match 'Started the agent'
$health = Get-Vfs365Health $exe
for($attempt = 0; $started -and $attempt -lt 12 -and $health.ExitCode -eq 1; $attempt++){
    Start-Sleep -Seconds 5
    $health = Get-Vfs365Health $exe
}
$summary = if($health.Report -and $health.Report.Summary){ $health.Report.Summary }else{ "VFS365 health gave no report (exit code $($health.ExitCode)): $($health.Output)" }
Write-Output "$summary ($($steps -join '; '))"
if($health.ExitCode -eq 0){
    Exit 0
}
Exit 1
