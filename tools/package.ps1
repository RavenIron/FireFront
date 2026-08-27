# Builds the store release zip: RavenIron-FireFront-<version>.zip in dist\.
# Same shape as Ragnarok's Wrath's script, same guard: the THREE places the version
# lives (Plugin.cs const, csproj, manifest.json) must agree or this refuses to
# package — a release can never claim a version its own log denies. (This repo
# shipped with csproj at 0.17.2 while Plugin.cs said 0.17.3; this guard is why
# that can't happen again.)
#
# The same zip uploads to Thunderstore AND Hexium (hexium.gg) — Hexium takes
# Thunderstore-compatible packages with these exact root files.

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

# --- the three versions must agree -------------------------------------------------
$pluginVer   = (Select-String -Path "$root\Plugin.cs" -Pattern 'VERSION\s*=\s*"([^"]+)"').Matches[0].Groups[1].Value
$csprojVer   = (Select-String -Path "$root\FireFront.csproj" -Pattern '<Version>([^<]+)</Version>').Matches[0].Groups[1].Value
$manifestVer = (Get-Content "$root\manifest.json" -Raw | ConvertFrom-Json).version_number

if (($pluginVer -ne $csprojVer) -or ($pluginVer -ne $manifestVer)) {
    Write-Host "VERSION MISMATCH - refusing to package:" -ForegroundColor Red
    Write-Host "  Plugin const : $pluginVer"
    Write-Host "  csproj       : $csprojVer"
    Write-Host "  manifest.json: $manifestVer"
    exit 1
}

# --- clean Release build -----------------------------------------------------------
dotnet build "$root\FireFront.csproj" -c Release -v q --nologo
if ($LASTEXITCODE -ne 0) { Write-Host "Build failed." -ForegroundColor Red; exit 1 }

$dll = "$root\bin\Release\net472\FireFront.dll"
if (-not (Test-Path $dll)) { $dll = "$root\bin\Release\FireFront.dll" }
if (-not (Test-Path $dll)) { Write-Host "No Release DLL under $root\bin\Release" -ForegroundColor Red; exit 1 }

# --- assemble the flat zip both stores expect --------------------------------------
$dist = "$root\dist"
$stage = "$dist\stage"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

Copy-Item $dll, "$root\manifest.json", "$root\README.md", "$root\CHANGELOG.md", "$root\icon.png" -Destination $stage

$zip = "$dist\RavenIron-FireFront-$pluginVer.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
# .NET's writer, NOT Compress-Archive: PS 5.1's cmdlet produces structurally quirky
# zips that at least one store's web-side parser rejects with "No manifest.json found
# in the ZIP" (observed on Hexium 2026-08-27, entries verifiably present and correct).
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip,
    [System.IO.Compression.CompressionLevel]::Optimal, $false)
Remove-Item $stage -Recurse -Force

Write-Host "Packaged: $zip" -ForegroundColor Green
Get-Item $zip | Select-Object Name, Length
