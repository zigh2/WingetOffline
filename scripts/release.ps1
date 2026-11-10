<#
.SYNOPSIS
  Builds the release packages. Called by .github/workflows/release.yml.
  Keeping the logic here (not inside the workflow) means a change to the packaging can be shipped
  through the normal incoming/*.zip update, which never touches .github/.

.OUTPUTS
  out/WingetOffline-<tag>-win-x64-{lite,standalone,full}.zip   (full only if out/catalog/catalog.wglib exists)
  out/assets/WingetOffline-lite.exe, wgo-lite.exe, WingetOffline.Builder-lite.exe
      -> small direct downloads (no .NET inside; they need the .NET 8+ Desktop Runtime).
         Stable names, so  .../releases/latest/download/WingetOffline-lite.exe  always points to the newest release.
#>
param(
  [Parameter(Mandatory = $true)][string]$Tag,
  [string]$Out = 'out'
)
$ErrorActionPreference = 'Stop'

$version = (Get-Content VERSION -Raw).Trim()
if ("v$version" -ne $Tag) { throw "Tag $Tag does not match the VERSION file (v$version)" }

# project folder -> exe name produced
$exes = [ordered]@{ Builder = 'WingetOffline.Builder.exe'; Cli = 'wgo.exe'; App = 'WingetOffline.exe' }

function Publish-All([string]$variant, [bool]$selfContained) {
  foreach ($p in $exes.Keys) {
    $publishArgs = @('publish', "src/WingetOffline.$p", '-c', 'Release', '-r', 'win-x64',
                     '--self-contained', $selfContained.ToString().ToLower(),
                     '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
                     '-p:DebugType=none', '-p:DebugSymbols=false', '-o', "$Out/$variant/$p")
    if ($selfContained) { $publishArgs += '-p:EnableCompressionInSingleFile=true' }
    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $p ($variant)" }
  }
}

Write-Host '== lite (framework-dependent, single file)'
Publish-All 'lite' $false
Write-Host '== standalone (self-contained, compressed single file)'
Publish-All 'standalone' $true

# ---- direct downloads: the lite exes with stable names ----
New-Item -ItemType Directory -Path "$Out/assets" -Force | Out-Null
$assetNames = @{ App = 'WingetOffline-lite.exe'; Cli = 'wgo-lite.exe'; Builder = 'WingetOffline.Builder-lite.exe' }
foreach ($p in $exes.Keys) {
  Copy-Item "$Out/lite/$p/$($exes[$p])" -Destination "$Out/assets/$($assetNames[$p])"
}

# ---- zip packages ----
$liteNote = @"
WingetOffline $Tag - lite package (no .NET inside the files)

Needs the .NET 8 (or newer) runtime on the machine:
  - WingetOffline.exe needs the ".NET Desktop Runtime" (x64)
  - wgo.exe and WingetOffline.Builder.exe need the ".NET Runtime" (x64); the Desktop Runtime installer includes it
Download: https://dotnet.microsoft.com/download/dotnet

WingetOffline.Builder.exe is for the connected machine only. WingetOffline.exe and wgo.exe never use the network.
"@

$standaloneNote = @"
WingetOffline $Tag - standalone package (.NET included, nothing to install)

WingetOffline.exe  the app          wgo.exe  command line for the offline machine
WingetOffline.Builder.exe  for the connected machine only (builds catalog.wglib, collects installers)
Each file is compressed, so the first start can take a couple of seconds longer.
"@

$fullNote = @"
WingetOffline $Tag - full package (standalone + catalog.wglib)

Same files as the standalone package, plus catalog.wglib as it was when this release was built.
In the app: Settings -> choose catalog.wglib -> Load.
"@

function New-Package([string]$variant, [string]$note) {
  $dir = "$Out/pkg-$variant"
  New-Item -ItemType Directory -Path $dir | Out-Null
  foreach ($p in $exes.Keys) { Copy-Item "$Out/$variant/$p/$($exes[$p])" -Destination $dir }
  Copy-Item LICENSE, README.md -Destination $dir
  Set-Content -Path "$dir/READ-ME-FIRST.txt" -Encoding UTF8 -Value $note
  Compress-Archive -Path "$dir/*" -DestinationPath "$Out/WingetOffline-$Tag-win-x64-$variant.zip"
}

New-Package 'lite' $liteNote
New-Package 'standalone' $standaloneNote

if (Test-Path "$Out/catalog/catalog.wglib") {
  Copy-Item "$Out/pkg-standalone" "$Out/pkg-full" -Recurse
  Copy-Item "$Out/catalog/catalog.wglib" -Destination "$Out/pkg-full"
  Set-Content -Path "$Out/pkg-full/READ-ME-FIRST.txt" -Encoding UTF8 -Value $fullNote
  Compress-Archive -Path "$Out/pkg-full/*" -DestinationPath "$Out/WingetOffline-$Tag-win-x64-full.zip"
} else {
  Write-Warning 'catalog.wglib was not available: the full package is skipped.'
}

# ---- real sizes, shown on the workflow run page ----
if ($env:GITHUB_STEP_SUMMARY) {
  $lines = @('### Release assets', '| File | Size |', '|---|---|')
  foreach ($f in (Get-ChildItem "$Out/assets/*.exe") + (Get-ChildItem "$Out/*.zip")) {
    $lines += "| $($f.Name) | $([math]::Round($f.Length / 1MB, 2)) MB |"
  }
  $lines += ''; $lines += '### Standalone exe sizes'; $lines += '| File | Size |'; $lines += '|---|---|'
  foreach ($f in (Get-ChildItem "$Out/pkg-standalone/*.exe")) {
    $lines += "| $($f.Name) | $([math]::Round($f.Length / 1MB, 1)) MB |"
  }
  $lines | Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Append
}
Get-ChildItem "$Out/assets", $Out -File | Format-Table Name, @{n='MB';e={[math]::Round($_.Length/1MB,2)}} -AutoSize
