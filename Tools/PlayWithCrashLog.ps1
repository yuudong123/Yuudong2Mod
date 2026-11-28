# Start RimWorld normally (real config and saves), but keep what the runtime prints when it dies.
# A stack overflow kills the game without a line in Player.log; the runtime writes the repeating call chain to stderr instead,
# and Steam throws stderr away. This keeps stderr in D:\Games\CrashDumps and also asks the runtime for a dump file.
# Usage: powershell -File Tools/PlayWithCrashLog.ps1
$game = "D:\Games\Steam\steamapps\common\RimWorld"
$dir = "D:\Games\CrashDumps"
New-Item -ItemType Directory -Force $dir | Out-Null

if (Get-Process RimWorld -ErrorAction SilentlyContinue) {
    Write-Output "RimWorld is already running."
    exit 2
}

$env:DOTNET_DbgEnableMiniDump = "1"
$env:DOTNET_DbgMiniDumpType = "2"
$env:DOTNET_DbgMiniDumpName = Join-Path $dir "%e_%p_%t.dmp"

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$out = Join-Path $dir "stdout-$stamp.txt"
$err = Join-Path $dir "stderr-$stamp.txt"
Start-Process (Join-Path $game "RimWorld.exe") -WorkingDirectory $game -RedirectStandardOutput $out -RedirectStandardError $err | Out-Null
Write-Output "Started. If it crashes, the call chain will be in $err"
