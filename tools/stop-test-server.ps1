# Graceful stop for the FireFront test server, with the save actually VERIFIED.
#
# The history is the justification, so keep it: the original CTRL_BREAK helper
# lived in a session scratchpad that got cleaned between sessions. After that,
# every "graceful stop" was launching PowerShell against a file that no longer
# existed, failing SILENTLY, timing out, and falling through to a force-kill —
# which skips Valheim's shutdown save entirely. It only ever failed harmlessly
# because nobody happened to be connected.
#
# Hence two rules here:
#   * fail LOUDLY — every failure mode has a distinct message
#   * verify the save from the LOG, never from a file mtime. mtime races the
#     write and already produced one false "the world didn't save" alarm.
#
# It also will not force-kill unless you explicitly ask. A stop that cannot
# save should leave the server running, not quietly discard world state.

param(
    [string]$ServerDir      = "C:\Users\donfr\FireFrontTestServer",
    [string]$LogPath        = "",
    [int]   $TimeoutSeconds = 180,
    [switch]$AllowForceKill
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrEmpty($LogPath)) { $LogPath = Join-Path $ServerDir "ff-test.log" }

$proc = Get-Process valheim_server -ErrorAction SilentlyContinue
if (-not $proc) { Write-Host "No valheim_server running."; exit 0 }
if (@($proc).Count -gt 1) { Write-Host "WARNING: $(@($proc).Count) server processes; stopping all." -ForegroundColor Yellow }

$helper = Join-Path $PSScriptRoot "_ctrlbreak-helper.ps1"
if (-not (Test-Path $helper)) {
    Write-Host "FATAL: helper missing at $helper" -ForegroundColor Red
    Write-Host "This is the exact failure that caused silent force-kills. Do not proceed." -ForegroundColor Red
    exit 1
}

# Where the log ends now, so only a save from here on counts.
$logLinesBefore = 0
if (Test-Path $LogPath) {
    $logLinesBefore = (Get-Content $LogPath -ErrorAction SilentlyContinue | Measure-Object -Line).Lines
}

foreach ($p in @($proc)) {
    Write-Host "Sending CTRL_BREAK to $($p.Id)..."
    $h = Start-Process powershell -ArgumentList "-NoProfile","-ExecutionPolicy","Bypass","-File",$helper,"-TargetPid",$p.Id `
                       -WindowStyle Hidden -Wait -PassThru
    switch ($h.ExitCode) {
        0 { Write-Host "  signal delivered" }
        # 0xC000013A STATUS_CONTROL_C_EXIT: the helper attached to the target's
        # console, so the break it raised killed the helper too. That is the
        # NORMAL outcome and is itself proof of delivery — not a failure,
        # despite looking like one.
        -1073741510 { Write-Host "  signal delivered (helper consumed by its own event - expected)" }
        2 { Write-Host "  ATTACH FAILED - target has no console. It was probably launched with -RedirectStandardOutput; use -logfile alone (see start-test-server.ps1)." -ForegroundColor Yellow }
        3 { Write-Host "  GenerateConsoleCtrlEvent failed" -ForegroundColor Yellow }
        4 { Write-Host "  process already gone" }
        default { Write-Host "  helper exit $($h.ExitCode)" -ForegroundColor Yellow }
    }

    if ($p.WaitForExit($TimeoutSeconds * 1000)) {
        Write-Host "  exited"
    } else {
        Write-Host "  DID NOT EXIT in ${TimeoutSeconds}s" -ForegroundColor Red
        if ($AllowForceKill) {
            Write-Host "  force-killing - THE WORLD MAY NOT HAVE SAVED" -ForegroundColor Red
            Stop-Process -Id $p.Id -Force
        } else {
            Write-Host "  leaving it running. Re-run with -AllowForceKill only if you accept losing unsaved world state." -ForegroundColor Red
            exit 1
        }
    }
}

Start-Sleep -Seconds 3

$saved = $false
if (Test-Path $LogPath) {
    $new = Get-Content $LogPath -ErrorAction SilentlyContinue | Select-Object -Skip $logLinesBefore
    $saveLine = $new | Where-Object { $_ -match "World saved" } | Select-Object -Last 1
    if ($saveLine) { $saved = $true; Write-Host "SAVE CONFIRMED: $saveLine" -ForegroundColor Green }
}
if (-not $saved) {
    Write-Host "NO shutdown save found in the log after the stop began." -ForegroundColor Red
    Write-Host "If players were connected, world state since the last autosave is lost." -ForegroundColor Red
    exit 1
}
exit 0
