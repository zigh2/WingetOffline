<#
.SYNOPSIS
  Runs every automatic WingetOffline check from the command line and puts everything worth sending into ONE zip.

.DESCRIPTION
  It calls "wgo selftest" (the same engine as the Self-test page of the app), adds a few facts about this PC
  (Windows version, .NET runtimes, winget, drives, file sizes of the program), and zips the reports.
  By default your user name and machine name are replaced in the reports; use -NoRedact to keep them.
  Nothing is installed or removed. Your library and settings are not changed.

.EXAMPLE
  .\run-selftest.ps1 -Catalog D:\catalog.wglib
.EXAMPLE
  .\run-selftest.ps1 -Catalog D:\catalog.wglib -InstallersFolder D:\Installers -LibraryFolder D:\Software -Deep
.EXAMPLE
  .\run-selftest.ps1 -Catalog D:\catalog.wglib -Online      # also downloads one small program (needs internet)
#>
param(
    [string]$Catalog,
    [string]$InstallersFolder,
    [string[]]$LibraryFolder,
    [switch]$Deep,
    [switch]$Online,
    [switch]$NoRedact,
    [string]$OutDir = (Get-Location).Path,
    [string]$Wgo
)

$ErrorActionPreference = 'Stop'

function Find-Wgo {
    if ($Wgo -and (Test-Path $Wgo)) { return (Resolve-Path $Wgo).Path }
    foreach ($dir in @($PSScriptRoot, (Split-Path $PSScriptRoot -Parent), (Get-Location).Path)) {
        if (-not $dir) { continue }
        $hit = Get-ChildItem -Path $dir -Filter 'wgo*.exe' -File -ErrorAction SilentlyContinue | Sort-Object Name | Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }
    $cmd = Get-Command 'wgo*' -ErrorAction SilentlyContinue | Where-Object { $_.CommandType -eq 'Application' } | Select-Object -First 1
    if ($cmd) { return $cmd.Source }
    return $null
}

function Hide-Identity([string]$text) {
    if ($NoRedact -or -not $text) { return $text }
    $t = $text
    foreach ($pair in @(@($env:USERPROFILE, '%USERPROFILE%'), @($env:USERNAME, '<user>'), @($env:COMPUTERNAME, '<machine>'))) {
        if ($pair[0] -and $pair[0].Length -ge 3) { $t = $t -replace [regex]::Escape($pair[0]), $pair[1] }
    }
    return $t
}

$wgoPath = Find-Wgo
if (-not $wgoPath) {
    Write-Error 'wgo.exe was not found. Put this script in the folder of wgo.exe, or pass -Wgo <path>.'
    exit 2
}
Write-Host "Using $wgoPath"

$stamp = Get-Date -Format 'yyyyMMdd-HHmm'
$work = Join-Path ([IO.Path]::GetTempPath()) ("wgo-selftest-run-" + $stamp)
New-Item -ItemType Directory -Path $work -Force | Out-Null
$reportMd = Join-Path $work 'report.md'
$reportJson = Join-Path $work 'report.json'

# ---- arguments for wgo selftest ----
function Build-Args() {
    $a = @('selftest')
    if ($Catalog) { $a += $Catalog }
    if ($Deep) { $a += '--deep' }
    if ($Online) { $a += '--online' }
    if ($NoRedact) { $a += '--no-redact' }
    if ($InstallersFolder) { $a += @('--installers', $InstallersFolder) }
    foreach ($l in @($LibraryFolder)) { if ($l) { $a += @('--library', $l) } }
    $a += @('--export', $reportMd, '--export', $reportJson)   # Markdown و JSON من تشغيل واحد
    return $a
}

# ---- run ----
$console = Join-Path $work 'console.txt'
& $wgoPath @(Build-Args) 2>&1 | Tee-Object -FilePath $console
$exit = $LASTEXITCODE

# ---- facts about this PC ----
$extras = New-Object System.Collections.Generic.List[string]
$extras.Add("Generated: $(Get-Date -Format 's')")
try {
    $os = Get-CimInstance Win32_OperatingSystem
    $extras.Add("Windows: $($os.Caption) $($os.Version) build $($os.BuildNumber) $($os.OSArchitecture)")
} catch { $extras.Add("Windows: (not available) $($_.Exception.Message)") }
$extras.Add("PowerShell: $($PSVersionTable.PSVersion)  Edition: $($PSVersionTable.PSEdition)")
$extras.Add("Culture: $((Get-Culture).Name)  UI: $((Get-UICulture).Name)  List separator: '$((Get-Culture).TextInfo.ListSeparator)'")
$extras.Add("Execution policy: $(Get-ExecutionPolicy)")
$extras.Add("")
$extras.Add("--- dotnet --list-runtimes")
try { $extras.Add((& dotnet --list-runtimes 2>&1 | Out-String)) } catch { $extras.Add('dotnet command not available') }
$extras.Add("--- winget --version")
try { $extras.Add((& winget --version 2>&1 | Out-String)) } catch { $extras.Add('winget not available') }
$extras.Add("--- drives")
try {
    Get-CimInstance Win32_LogicalDisk | ForEach-Object {
        $extras.Add(("{0}  type {1}  {2}  {3:N1} GB free of {4:N1} GB" -f $_.DeviceID, $_.DriveType, $_.FileSystem, ($_.FreeSpace / 1GB), ($_.Size / 1GB)))
    }
} catch { $extras.Add('drive information not available') }
$extras.Add("--- program files next to wgo")
Get-ChildItem -Path (Split-Path $wgoPath -Parent) -Filter '*.exe' -File -ErrorAction SilentlyContinue | ForEach-Object {
    $extras.Add(("{0}  {1:N2} MB  file version {2}" -f $_.Name, ($_.Length / 1MB), $_.VersionInfo.FileVersion))
}
Set-Content -Path (Join-Path $work 'extras.txt') -Value (Hide-Identity ($extras -join "`r`n")) -Encoding UTF8

# ---- one zip to send ----
$zip = Join-Path $OutDir ("selftest-" + $stamp + ".zip")
Compress-Archive -Path (Join-Path $work '*') -DestinationPath $zip -Force
Write-Host ""
Write-Host "Send this file: $zip"
if ($NoRedact) { Write-Host 'It was created with -NoRedact: it contains your user name and machine name.' }
exit $exit
