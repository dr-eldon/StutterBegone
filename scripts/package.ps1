# package.ps1 - build StutterBegone and package a distributable zip.
#
# Produces:  StutterBegone-<version>.zip  (in the out folder)
#               BepInEx/plugins/StutterBegone/StutterBegone.dll
#               BepInEx/plugins/StutterBegone/README.md
#
# The version is read from the [BepInPlugin] attribute in Plugin.cs.
# The game dir defaults to Directory.Build.props; override with $env:GameDir.
#
# Usage (from anywhere):
#   .\scripts\package.ps1
#   $env:DOTNET  = 'C:\path\to\dotnet'; .\scripts\package.ps1   # force a dotnet
#   $env:GameDir = 'C:\other\game';      .\scripts\package.ps1   # game elsewhere
#
$ErrorActionPreference = 'Stop'
if (-not $PSScriptRoot) { Write-Error 'Run as a script file (.\scripts\package.ps1) so $PSScriptRoot is set.'; exit 1 }
Set-Location (Split-Path $PSScriptRoot -Parent)   # project root = parent of scripts\

# --- pick a dotnet that actually has an SDK (the PATH one may be runtime-only) ---
$dotnet = if ($env:DOTNET) { $env:DOTNET } else { 'dotnet' }
if (-not (& $dotnet --list-sdks 2>$null)) {
    Write-Error "dotnet ('$dotnet') has no .NET SDK (runtime-only?). Install the SDK or set DOTNET."
    exit 1
}

# --- version (from the BepInPlugin attribute) ---
$line    = (Select-String -Path 'Plugin.cs' -Pattern 'BepInPlugin' | Select-Object -First 1).Line
$version = ([regex]::Match($line, '[0-9]+\.[0-9]+\.[0-9]+')).Value
if (-not $version) { Write-Error 'Could not read the version from Plugin.cs'; exit 1 }

Write-Host "==> dotnet : $(& $dotnet --version)"
Write-Host "==> version: $version"

# --- build ---
Write-Host '==> building (Release)'
Remove-Item -Recurse -Force 'out' -ErrorAction SilentlyContinue
$publish = @('publish', '-c', 'Release', '-o', 'out')
if ($env:GameDir) { $publish += "-p:GameDir=$env:GameDir" }
& $dotnet @publish
if ($LASTEXITCODE -ne 0) { Write-Error 'dotnet publish failed'; exit 1 }

# --- stage the files ---
$zip   = Join-Path 'out' "StutterBegone-$version.zip"   # -> out\StutterBegone-<version>.zip
$stage = Join-Path $env:TEMP "sbpkg_$([guid]::NewGuid().ToString('N'))"
$pkg   = Join-Path $stage 'BepInEx/plugins/StutterBegone'
New-Item -ItemType Directory -Path $pkg -Force | Out-Null
Copy-Item (Join-Path 'out' 'StutterBegone.dll') $pkg
Copy-Item 'README.md' $pkg

# --- package ---
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (Join-Path $stage 'BepInEx') -DestinationPath $zip
Remove-Item -Recurse -Force $stage

# --- report ---
Write-Host '==> contents:'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$z = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path $zip).Path)
foreach ($e in $z.Entries) { Write-Host ('   ' + $e.FullName + " ($($e.Length) B)") }
$z.Dispose()
Write-Host "==> done: $zip"
