<#
.SYNOPSIS
  Builds Precinct 88 and, optionally, drops it into GTA V.

.DESCRIPTION
  Uses the self-contained Roslyn compiler rather than `dotnet build`. The machine SDK is a
  partial install -- Microsoft.NETCore.App 8.0.28 has three files where 8.0.19 next to it has
  184 -- so every `dotnet` command dies with "hostpolicy.dll not found", and
  `dotnet --list-runtimes` still lists 8.0.28, which hides the cause. Nothing here needs
  MSBuild anyway: one library, no NuGet, no project file.

  The toolchain is NOT in this repo. It is ~174 MB of compiler and reference assemblies and
  there is already a copy on this machine under the hoodrich project, so this looks there
  rather than carrying a second one. Point -Tools somewhere else, or drop a tools\ folder in
  beside this script, and it will use that instead.

.EXAMPLE
  .\build.ps1
  .\build.ps1 -Deploy
  .\build.ps1 -Deploy -Target Both
  .\build.ps1 -Package
#>
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [switch]$Deploy,

    # Builds the release zip in release\, with the tree a player unpacks.
    [switch]$Package,

    [ValidateSet('Legacy', 'Enhanced', 'Both')]
    [string]$Target = 'Both',

    # Deploy while the game is running, for a hot reload.
    #
    # Normally refused, and the refusal is right for asset mods -- but this is one dll and one
    # ini, and ScriptHookVDotNet SHADOW-COPIES the assembly before running it. The file in
    # scripts\ is therefore not locked, and replacing it then pressing Insert reloads the mod
    # in place without leaving the game.
    #
    # Safe here specifically because the Aborted handler hands everything back before the
    # reload: the wanted ceiling, the dispatch services, police attention and the vanilla cop
    # generator all go home, and the criminal profile is written out. A mod that leaked any of
    # those on unload would not be safe to hot swap, and this one is tested on exactly that.
    [switch]$HotSwap,

    [string]$GtaDir = 'C:\Program Files (x86)\Steam\steamapps\common\Grand Theft Auto V',
    [string]$EnhancedDir = 'C:\Program Files (x86)\Steam\steamapps\common\Grand Theft Auto V Enhanced',

    # Where the compiler lives. Its own tools\ first, then the one next door.
    [string]$Tools = ''
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

# --- the toolchain ----------------------------------------------------------
if (-not $Tools) {
    $candidates = @(
        (Join-Path $root 'tools'),
        (Join-Path (Split-Path $root -Parent) 'hoodrich\tools')
    )

    foreach ($c in $candidates) {
        if (Test-Path (Join-Path $c 'roslyn\tasks\net472\csc.exe')) { $Tools = $c; break }
    }
}

if (-not $Tools) {
    throw "No compiler found. Looked in .\tools\ and ..\hoodrich\tools\. Pass -Tools <path>."
}

$csc    = Join-Path $Tools 'roslyn\tasks\net472\csc.exe'
$refDir = Join-Path $Tools 'refasm\build\.NETFramework\v4.8'

if (-not (Test-Path $csc))    { throw "Compiler missing: $csc" }
if (-not (Test-Path $refDir)) { throw "net48 reference assemblies missing: $refDir" }

$srcDir = Join-Path $root 'src\Precinct88'
$outDir = Join-Path $root 'build'
$outDll = Join-Path $outDir 'Precinct88.dll'

# Either install serves as the reference source -- both ship the identical
# ScriptHookVDotNet3.dll 3.9.0.0, so a pure SHVDN script is one build that runs on both.
$shvdn = $null
foreach ($d in @($GtaDir, $EnhancedDir)) {
    $p = Join-Path $d 'ScriptHookVDotNet3.dll'
    if (Test-Path $p) { $shvdn = $p; break }
}
if (-not $shvdn) { throw "ScriptHookVDotNet3.dll not found in either install." }

# WHICH ScriptHookVDotNet, said out loud, every build.
#
# The compiler stamps the reference assembly's EXACT version into the output, so a mod built
# against 3.9 is a mod that ASKS for 3.9 -- and a player on 3.7 gets a load failure with no
# log, because the thing that would have written the log is the thing that did not load.
$shvdnVer = [System.Reflection.AssemblyName]::GetAssemblyName($shvdn).Version
Write-Host "ScriptHookVDotNet reference: $shvdnVer  (players need this or newer)" -ForegroundColor DarkCyan

New-Item -ItemType Directory -Force $outDir | Out-Null

# --- references -------------------------------------------------------------
# Same rule as hoodrich and overspray: the BCL and SHVDN, nothing else. A mod with no external
# dependencies cannot lose a version fight with another mod in scripts\ -- one folder is one
# assembly resolution namespace, and NAudio is strong-named enough to prove it.
$refNames = @(
    'mscorlib.dll'
    'System.dll'
    'System.Core.dll'
    'System.Drawing.dll'
    'System.Windows.Forms.dll'
    'System.Numerics.dll'
)

$refs = @()
foreach ($n in $refNames) {
    $p = Join-Path $refDir $n
    if (-not (Test-Path $p)) { throw "Reference assembly missing: $p" }
    $refs += "/reference:`"$p`""
}
$refs += "/reference:`"$shvdn`""

# --- sources ----------------------------------------------------------------
$sources = Get-ChildItem $srcDir -Recurse -Filter *.cs |
    Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' } |
    ForEach-Object { $_.FullName }

if (-not $sources) { throw "No .cs sources found under $srcDir" }

# --- compiler options -------------------------------------------------------
$opts = @(
    '/target:library'
    '/platform:x64'
    '/langversion:9.0'
    '/nologo'
    '/warnaserror-'
    '/warn:4'
    '/nostdlib+'
    '/utf8output'
    "/out:`"$outDll`""
)

if ($Configuration -eq 'Debug') {
    $opts += '/debug:portable', '/define:DEBUG;TRACE', '/optimize-'
} else {
    $opts += '/debug-', '/optimize+'
}

$rsp = Join-Path $outDir 'build.rsp'
($opts + $refs + ($sources | ForEach-Object { "`"$_`"" })) | Set-Content -Path $rsp -Encoding UTF8

Write-Host "Compiling $($sources.Count) source files -> $outDll ($Configuration)" -ForegroundColor Cyan
$sw = [Diagnostics.Stopwatch]::StartNew()
& $csc "@$rsp"
$exit = $LASTEXITCODE
$sw.Stop()

if ($exit -ne 0) { throw "Compilation failed (csc exit $exit)." }
Write-Host ("OK  {0:N0} bytes in {1:N1}s" -f (Get-Item $outDll).Length, $sw.Elapsed.TotalSeconds) -ForegroundColor Green

# --- deploy -----------------------------------------------------------------
function Deploy-To([string]$dir, [string]$label) {
    if (-not (Test-Path $dir)) { Write-Host "  skip   $label (not installed)" -ForegroundColor DarkGray; return }

    $scripts = Join-Path $dir 'scripts'
    New-Item -ItemType Directory -Force $scripts | Out-Null

    Copy-Item $outDll (Join-Path $scripts 'Precinct88.dll') -Force

    $iniSrc = Join-Path $root 'Precinct88.ini'
    $iniDst = Join-Path $scripts 'Precinct88.ini'

    if (Test-Path $iniSrc) {
        if (Test-Path $iniDst) {
            # NEVER OVERWRITTEN: it is the one file a player hand-edits. When a new option
            # ships, the line below says so and the default applies until they add it.
            Write-Host "  keep   Precinct88.ini" -ForegroundColor DarkGray

            $have = Get-Content $iniDst -Raw
            foreach ($sec in @('General', 'Patrol', 'Wanted', 'Contact', 'Custody')) {
                if ($have -notmatch "(?m)^\s*\[$sec\]") {
                    Write-Host "  STALE  Precinct88.ini is missing [$sec]" -ForegroundColor Yellow
                }
            }
        } else {
            Copy-Item $iniSrc $iniDst
            Write-Host "  new    Precinct88.ini" -ForegroundColor Green
        }
    }

    # --- data ----------------------------------------------------------------
    #
    # Overwritten only when it actually differs, so a deploy that changed no data says so
    # rather than printing a list of files every time.
    $dataSrc = Join-Path $root 'data'
    $dataDst = Join-Path $scripts 'Precinct88'

    if (Test-Path $dataSrc) {
        New-Item -ItemType Directory -Force $dataDst | Out-Null

        $n = 0
        foreach ($f in Get-ChildItem $dataSrc -Filter *.json) {
            $to = Join-Path $dataDst $f.Name

            if ((Test-Path $to) -and (Get-Item $to).Length -eq $f.Length -and
                (Get-FileHash $to).Hash -eq (Get-FileHash $f.FullName).Hash) { continue }

            Copy-Item $f.FullName $to -Force
            $n++
        }

        if ($n -gt 0) { Write-Host "  data   $n file(s)" -ForegroundColor Green }
        else          { Write-Host "  data   up to date" -ForegroundColor DarkGray }
    }

    # --- art -----------------------------------------------------------------
    #
    # The HUD icons. Generated by tools/make_icons.py rather than committed by hand, so the
    # PNGs are build output and this copies them like any other output. Always overwritten
    # when they differ -- nobody hand-edits one, and a stale icon from three builds ago is a
    # bug that looks like a rendering fault.
    $artSrc = Join-Path $root 'data\icons'
    $artDst = Join-Path $scripts 'Precinct88\icons'

    if (Test-Path $artSrc) {
        New-Item -ItemType Directory -Force $artDst | Out-Null

        $a = 0
        foreach ($f in Get-ChildItem $artSrc -Filter *.png) {
            $to = Join-Path $artDst $f.Name

            if ((Test-Path $to) -and (Get-Item $to).Length -eq $f.Length -and
                (Get-FileHash $to).Hash -eq (Get-FileHash $f.FullName).Hash) { continue }

            Copy-Item $f.FullName $to -Force
            $a++
        }

        if ($a -gt 0) { Write-Host "  art    $a icon(s)" -ForegroundColor Green }
        else          { Write-Host "  art    up to date" -ForegroundColor DarkGray }
    } else {
        Write-Host "  ART    missing - run tools/make_icons.py" -ForegroundColor Yellow
    }

    Write-Host "  ok     $label" -ForegroundColor Green
}

if ($Deploy) {
    $running = Get-Process GTA5, GTA5_Enhanced -ErrorAction SilentlyContinue

    if ($running -and -not $HotSwap) {
        throw "GTA V is running - close it before deploying, or pass -HotSwap and press Insert."
    }

    if ($running -and $HotSwap) {
        Write-Host "GTA V is running; hot swapping. Press Insert in game to reload scripts." -ForegroundColor Yellow

        # The ini is read in the constructor, so a reload picks up edits to it as well -- but
        # only keys that are actually IN the installed file. A brand new option still needs
        # adding by hand, or setting from the panel, which writes it.
        Write-Host "  note   new ini options only apply once they exist in the installed ini" -ForegroundColor DarkGray
    }

    if ($Target -in 'Legacy', 'Both')   { Deploy-To $GtaDir      'Legacy' }
    if ($Target -in 'Enhanced', 'Both') { Deploy-To $EnhancedDir 'Enhanced' }

    Write-Host "Deploy complete." -ForegroundColor Green
}

# --- package ----------------------------------------------------------------
#
# THE ZIP IS THE PRODUCT, and its shape is the whole install. Somebody who has never seen this
# repo has one job -- drag "scripts" into the GTA folder -- and every way that goes wrong is a
# folder in the wrong place. So this builds the tree explicitly and then CHECKS it, because a
# packaging script that quietly ships four files instead of five is a support thread.
if ($Package) {
    $ver = (Select-String -Path (Join-Path $root 'src\Precinct88\Core\Log.cs') `
                          -Pattern 'Version = "([^"]+)"').Matches[0].Groups[1].Value

    $stage = Join-Path $root 'build\pkg'
    $zip = Join-Path $root ("release\Precinct88-" + $ver + ".zip")

    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Force (Join-Path $stage 'scripts\Precinct88\icons') | Out-Null
    New-Item -ItemType Directory -Force (Join-Path $root 'release') | Out-Null

    Copy-Item $outDll                             (Join-Path $stage 'scripts\Precinct88.dll')
    Copy-Item (Join-Path $root 'Precinct88.ini')  (Join-Path $stage 'scripts\Precinct88.ini')
    Copy-Item (Join-Path $root 'README.md')       (Join-Path $stage 'README.txt')

    foreach ($p in Get-ChildItem (Join-Path $root 'data') -Filter *.json) {
        Copy-Item $p.FullName (Join-Path $stage 'scripts\Precinct88')
    }

    foreach ($p in Get-ChildItem (Join-Path $root 'data\icons') -Filter *.png) {
        Copy-Item $p.FullName (Join-Path $stage 'scripts\Precinct88\icons')
    }

    # Every file the mod actually reads, by the path it reads it from. Missing any one of
    # these is a different broken install, and all of them are silent.
    [string[]]$must = @(
        'README.txt',
        'scripts\Precinct88.dll',
        'scripts\Precinct88.ini',
        'scripts\Precinct88\stations.json',
        'scripts\Precinct88\limits.json',
        'scripts\Precinct88\icons\seen.png',
        'scripts\Precinct88\icons\search.png',
        'scripts\Precinct88\icons
oid.png',
        'scripts\Precinct88\iconsace.png',
        'scripts\Precinct88\iconsit.png',
        'scripts\Precinct88\icons\car.png',
        'scripts\Precinct88\icons\gun.png',
        'scripts\Precinct88\icons\cam.png',
        'scripts\Precinct88\icons\cuffs.png',
        'scripts\Precinct88\icons\hands.png',
        'scripts\Precinct88\icons\stop.png',
        'scripts\Precinct88\iconsadge.png'
    )

    $missing = @()
    foreach ($m in $must) { if (-not (Test-Path (Join-Path $stage $m))) { $missing += $m } }
    if ($missing) { throw "Package is missing: $($missing -join ', ')" }

    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal

    Write-Host ""
    Write-Host ("Packaged  {0}" -f (Split-Path $zip -Leaf)) -ForegroundColor Green
    foreach ($m in $must) {
        $f = Get-Item (Join-Path $stage $m)
        Write-Host ("  {0,-42} {1,9:N0} bytes" -f $m, $f.Length) -ForegroundColor DarkGray
    }
    Write-Host ("  {0,-42} {1,9:N0} bytes" -f '(zip)', (Get-Item $zip).Length)
}
