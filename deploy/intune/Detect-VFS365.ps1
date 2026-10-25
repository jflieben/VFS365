<#
    .SYNOPSIS
    Intune Remediations detection script for VFS365: runs 'vfs365.exe health --all-users --json' from the install folder and prints
    its one-line summary (what Intune shows as the detection output).

    .DESCRIPTION
    Run as SYSTEM (the Remediations default), in 64-bit PowerShell. Windows PowerShell 5.1 or later. Exit 0: healthy, nothing to do.
    Exit 1: unhealthy, VFS365 missing, or no answer; Intune then runs Remediate-VFS365.ps1. See docs/DEPLOYMENT.md, Health and Intune.
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

$exe = Get-Vfs365Exe
if(-not (Test-Path -LiteralPath $exe)){
    Write-Output "VFS365 is not installed ($exe)"
    Exit 1
}
$health = Get-Vfs365Health $exe
if($health.Report -and $health.Report.Summary){
    Write-Output $health.Report.Summary
}else{
    Write-Output "VFS365 health gave no report (exit code $($health.ExitCode)): $($health.Output)"
}
if($health.ExitCode -eq 0){
    Exit 0
}
Exit 1
