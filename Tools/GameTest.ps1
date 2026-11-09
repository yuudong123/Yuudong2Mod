# Yuudong2Mod automated game test.
#   boot: start the game without a window, quit once loading finishes, list errors and warnings from the log.
#   save: also load a save (a copy, as autostart), run ticks with the Yuudong2Test mod and print its report.
# The game runs with its own save data folder (.gametest/data), so the real config, saves and Player.log are untouched.
# Usage: powershell -File Tools/GameTest.ps1 [-Mode boot|save] [-Save <name>] [-Ticks 5000] [-TimeoutSec 900] [-AllowRunning]
param(
    [ValidateSet("boot", "save")] [string]$Mode = "boot",
    [string]$Save = "",
    [int]$Ticks = 5000,
    [int]$TimeoutSec = 900,
    [switch]$AllowRunning
)

$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
$game = "D:\Games\Steam\steamapps\common\RimWorld"
$userData = Join-Path $env:USERPROFILE "AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios"
$data = Join-Path $repo ".gametest\data"
$logFile = Join-Path $repo ".gametest\console.log"
$report = Join-Path $repo ".gametest\report.txt"

if (-not $AllowRunning -and (Get-Process RimWorld -ErrorAction SilentlyContinue)) {
    Write-Output "RimWorld is running. Close it or pass -AllowRunning."
    exit 2
}

# Config: copy the real one each run so mod settings match, then adjust prefs for testing.
New-Item -ItemType Directory -Force (Join-Path $data "Config"), (Join-Path $data "Saves") | Out-Null
Copy-Item (Join-Path $userData "Config\*.xml") (Join-Path $data "Config") -Force
$prefsPath = Join-Path $data "Config\Prefs.xml"
$prefs = [IO.File]::ReadAllText($prefsPath)
$prefs = $prefs -replace '<resetModsConfigOnCrash>True', '<resetModsConfigOnCrash>False'
# Dev mode on in both modes: the game runs def config error checks only in dev mode, and it loads Saves/autostart.rws.
$prefs = $prefs -replace '<devMode>\w+</devMode>', '<devMode>True</devMode>'
[IO.File]::WriteAllText($prefsPath, $prefs)
Get-ChildItem (Join-Path $data "Saves") -Filter *.rws | Remove-Item

$modsPath = Join-Path $data "Config\ModsConfig.xml"
if ($Mode -eq "save") {
    $mods = [IO.File]::ReadAllText($modsPath)
    if ($mods -notmatch 'yuudong2\.yuudong2test') {
        $mods = $mods -replace '</activeMods>', "  <li>yuudong2.yuudong2test</li>`r`n  </activeMods>"
        [IO.File]::WriteAllText($modsPath, $mods)
    }

    # Test runner mod: deploy to the game's Mods folder (inactive in the real mod list, and idle without -y2test).
    $testSrc = Join-Path $repo "Tools\Yuudong2Test"
    $testDst = Join-Path $game "Mods\Yuudong2Test"
    New-Item -ItemType Directory -Force (Join-Path $testDst "About"), (Join-Path $testDst "Assemblies") | Out-Null
    Copy-Item (Join-Path $testSrc "About\About.xml") (Join-Path $testDst "About") -Force
    Copy-Item (Join-Path $testSrc "Assemblies\Yuudong2Test.dll") (Join-Path $testDst "Assemblies") -Force

    # Save: newest real save unless named. Loaded through the vanilla dev-mode "autostart" save.
    $source = if ($Save) { Get-Item (Join-Path $userData "Saves\$Save.rws") } else { Get-ChildItem (Join-Path $userData "Saves") -Filter *.rws | Sort-Object LastWriteTime -Descending | Select-Object -First 1 }
    Copy-Item $source.FullName (Join-Path $data "Saves\autostart.rws")
    Write-Output "save: $($source.Name)"
}

Remove-Item $logFile, $report -ErrorAction SilentlyContinue
$gameArgs = @("-headless", "-NoFileLog", "-NoCrashDialog", "-savedatafolder=$data")
if ($Mode -eq "boot") { $gameArgs += "-QuitAfterBoot" } else { $gameArgs += @("-y2test", "-y2ticks=$Ticks", "-y2report=$report") }

$timer = [Diagnostics.Stopwatch]::StartNew()
$proc = Start-Process (Join-Path $game "RimWorld.exe") -ArgumentList $gameArgs -WorkingDirectory $game -PassThru -WindowStyle Hidden -RedirectStandardOutput $logFile -RedirectStandardError "$logFile.err"
$null = $proc.Handle  # keep the handle so ExitCode is available after exit
if (-not $proc.WaitForExit($TimeoutSec * 1000)) {
    Stop-Process $proc -Force
    Write-Output "TIMEOUT after $TimeoutSec s"
}
Write-Output ("exit {0}, {1:F0}s" -f $proc.ExitCode, $timer.Elapsed.TotalSeconds)

# Log summary: errors and warnings, with the mod's own lines marked.
$lines = Get-Content $logFile -Encoding UTF8 -ErrorAction SilentlyContinue
$issues = $lines | Select-String -Pattern ' (ERROR|WARN|FAIL|CRIT)' | Where-Object { $_.Line -notmatch 'Persistent allocation .* was not freed before shutdown' } | ForEach-Object { $_.Line }
Write-Output "log: $($lines.Count) lines, $(@($issues).Count) errors/warnings"
$issues | Select-Object -First 40 | ForEach-Object { if ($_ -match '(?i)yuudong') { "  [MOD] $_" } else { "  $_" } }

if ($Mode -eq "save") {
    if (Test-Path $report) { Get-Content $report -Encoding UTF8 } else { Write-Output "NO REPORT (game did not finish the test run)" }
}
