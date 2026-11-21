# Yuudong2Mod automated game test.
#   boot: start the game without a window, quit once loading finishes, list errors and warnings from the log.
#   save: also load a save (a copy, as autostart), run ticks with the Yuudong2Test mod and print its report.
# The game runs with its own save data folder (.gametest/data), so the real config, saves and Player.log are untouched.
# Usage: powershell -File Tools/GameTest.ps1 [-Mode boot|save] [-Save <name>] [-Ticks 5000] [-TimeoutSec 900] [-AllowRunning] [-Windowed]
#   -DisableFeatures A,B,...: turn these Yuudong2Mod features off for this run only ("all" for every feature), to compare TPS with and without them.
#   -DropMods a.b,c.d: leave these mods (package ids) out of the test's mod list, to test a save without a mod it was made with.
#   -Windowed: show the game window. Needed for checks that render, such as the enemy gravship landing cutscene.
param(
    [ValidateSet("boot", "save")] [string]$Mode = "boot",
    [string]$Save = "",
    [int]$Ticks = 5000,
    [int]$TimeoutSec = 900,
    [switch]$AllowRunning,
    [switch]$Windowed,
    [string]$DisableFeatures = "",
    [string]$DropMods = ""
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
if ($DisableFeatures) {
    $names = if ($DisableFeatures -eq "all") {
        [regex]::Matches([IO.File]::ReadAllText((Join-Path $repo "Yuudong2Mod\Source\Yuudong2Mod\Yuudong2Settings.cs")), 'public const string \w+ = "(\w+)";') | ForEach-Object { $_.Groups[1].Value }
    } else { $DisableFeatures -split "," }
    $settingsPath = Join-Path $data "Config\Mod_Yuudong2Mod_Yuudong2ModSettingsMod.xml"
    $settings = [IO.File]::ReadAllText($settingsPath)
    $list = "<disabledFeatures>" + (($names | ForEach-Object { "<li>$_</li>" }) -join "") + "</disabledFeatures>"
    $settings = [regex]::Replace($settings, '<disabledFeatures\s*/>|<disabledFeatures>.*?</disabledFeatures>', $list, "Singleline")
    [IO.File]::WriteAllText($settingsPath, $settings)
    Write-Output "disabled features: $(@($names).Count)"
}
Get-ChildItem (Join-Path $data "Saves") -Filter *.rws | Remove-Item

# Real Ruins keeps its downloaded bases under the save data folder. Bring them along so base placement can be tested.
$ruins = Join-Path $userData "RealRuins"
if (Test-Path $ruins) {
    robocopy $ruins (Join-Path $data "RealRuins") /E /NFL /NDL /NJH /NJS /NP | Out-Null
    # robocopy reports success as 0-7; keep that from becoming this script's exit code.
    if ($LASTEXITCODE -ge 8) { Write-Output "Could not copy Real Ruins bases (robocopy exit $LASTEXITCODE); Real Ruins base placement will not be tested." }
    $global:LASTEXITCODE = 0
}

$modsPath = Join-Path $data "Config\ModsConfig.xml"
if ($DropMods) {
    $mods = [IO.File]::ReadAllText($modsPath)
    foreach ($id in ($DropMods -split ",")) {
        $mods = [regex]::Replace($mods, '\s*<li>' + [regex]::Escape($id.Trim()) + '</li>', '', "IgnoreCase")
    }
    [IO.File]::WriteAllText($modsPath, $mods)
    Write-Output "dropped mods: $DropMods"
}
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
$gameArgs = @("-NoFileLog", "-NoCrashDialog", "-savedatafolder=$data")
if (-not $Windowed) { $gameArgs = @("-headless") + $gameArgs }
if ($Mode -eq "boot") { $gameArgs += "-QuitAfterBoot" } else { $gameArgs += @("-y2test", "-y2ticks=$Ticks", "-y2report=$report") }

$timer = [Diagnostics.Stopwatch]::StartNew()
$proc = Start-Process (Join-Path $game "RimWorld.exe") -ArgumentList $gameArgs -WorkingDirectory $game -PassThru -WindowStyle $(if ($Windowed) { "Normal" } else { "Hidden" }) -RedirectStandardOutput $logFile -RedirectStandardError "$logFile.err"
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
