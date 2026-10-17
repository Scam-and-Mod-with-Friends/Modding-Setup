<#
    Scam With Your Friends - Modding Setup
    Run once from a clean install. Re-run after any game update.

        .\setup.ps1
        .\setup.ps1 -GameDir "C:\...\steamapps\common\Scam With Your Friends"
        .\setup.ps1 -Offline          # skip BepInEx download
        .\setup.ps1 -BepInExZip path  # use a local zip
#>
param([string]$GameDir, [string]$BepInExZip, [switch]$Offline)
$ErrorActionPreference = 'Stop'

# Find game -------------------------------------------------------------------
$game = $GameDir, $env:SWYG_GAME_DIR,
    "${env:ProgramFiles(x86)}\Steam\steamapps\common\Scam With Your Friends",
    "${env:ProgramFiles}\Steam\steamapps\common\Scam With Your Friends",
    "${env:HOME}\.local\share\Steam\steamapps\common\Scam With Your Friends",
    "${env:HOME}\.steam\steam\steamapps\common\Scam With Your Friends",
    "${env:ProgramFiles(x86)}\Steam\steamapps\common\Scam With Your Friends Playtest" |
    Where-Object { $_ -and (Test-Path "$_\Scam With Your Friends_Data\Managed\mscorlib.dll") } |
    Select-Object -First 1

if (-not $game) { throw "Game not found. Pass -GameDir or set SWYG_GAME_DIR." }
Write-Host "Game: $game" -ForegroundColor Cyan

# 1. Doorstop (vendored) ------------------------------------------------------
foreach ($f in 'winhttp.dll', '.doorstop_version', 'doorstop_config.ini') {
    $src = "$PSScriptRoot\vendor\doorstop\$f"
    if (-not (Test-Path $src)) { throw "Missing: $src" }
    if (-not (Test-Path "$game\$f")) { Copy-Item $src "$game\$f"; Write-Host "+ $f" }
}

# 2. BepInEx ------------------------------------------------------------------
if (-not (Test-Path "$game\BepInEx\core\BepInEx.dll")) {
    if ($Offline) { throw "BepInEx not installed and -Offline given." }
    $ver = '5.4.23.5'
    $zip = $BepInExZip
    $tmp = $null
    try {
        if (-not $zip) {
            $tmp = "$env:TEMP\BepInEx_$ver.zip"
            Write-Host "Downloading BepInEx $ver..." -ForegroundColor DarkGray
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
            Invoke-WebRequest "https://github.com/BepInEx/BepInEx/releases/download/v$ver/BepInEx_win_x64_$ver.zip" -OutFile $tmp -UseBasicParsing
            $zip = $tmp
        }
        $out = "$env:TEMP\bepinex_extract"
        Expand-Archive $zip $out -Force
        if (-not (Test-Path "$out\BepInEx\core")) { throw "Zip missing BepInEx\core - wrong file?" }
        New-Item -ItemType Directory -Force "$game\BepInEx\core" | Out-Null
        Copy-Item "$out\BepInEx\core\*" "$game\BepInEx\core" -Recurse -Force
        Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "+ BepInEx\core" -ForegroundColor Green
    } finally { if ($tmp) { Remove-Item $tmp -Force -ErrorAction SilentlyContinue } }
}
New-Item -ItemType Directory -Force "$game\BepInEx\plugins" | Out-Null

# 3. Corlib override ----------------------------------------------------------
$override = "$game\unstripped_corlib"
New-Item -ItemType Directory -Force $override | Out-Null
$n = 0
Get-ChildItem "$PSScriptRoot\vendor\corlib" -Filter *.dll | ForEach-Object {
    if (-not (Test-Path "$override\$($_.Name)")) { Copy-Item $_.FullName "$override\$($_.Name)"; $n++ }
}
if ($n) { Write-Host "+ seeded $n assemblies into unstripped_corlib\" -ForegroundColor Green }

# 4 & 5. Patches --------------------------------------------------------------
& "$PSScriptRoot\tools\unstrip-awaiters.ps1"   -GameDir $game
& "$PSScriptRoot\tools\find-vtable-breaks.ps1" -GameDir $game -Fix

# Verify ----------------------------------------------------------------------
$problems = @()
if (-not (Test-Path "$override\mscorlib.dll"))                                     { $problems += "mscorlib missing from unstripped_corlib\" }
if (-not (Test-Path "$game\doorstop_config.ini"))                                  { $problems += "doorstop_config.ini missing" }
if ((& "$PSScriptRoot\tools\find-vtable-breaks.ps1" -GameDir $game 6>&1 | Out-String) -notmatch 'No vtable breakages remain') {
    $problems += "vtable breakages remain - run tools\find-vtable-breaks.ps1"
}

if ($problems.Count -eq 0) {
    Write-Host "`nDone! Launch the game once, then drop mods into BepInEx\plugins\." -ForegroundColor Green
    exit 0
}
Write-Host "`nProblems:" -ForegroundColor Yellow
$problems | ForEach-Object { Write-Host "  - $_" -ForegroundColor Yellow }
exit 1
