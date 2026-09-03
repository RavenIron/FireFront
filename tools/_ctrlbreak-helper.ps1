# Attaches to a target process's console and raises CTRL_BREAK for the whole
# console group. MUST run as a separate process: GenerateConsoleCtrlEvent hits
# every process attached to that console, so a caller that ran this inline
# would signal itself.
#
# Exit codes are the point of this file — the previous version exited silently
# on failure, so a missing script or a failed attach was indistinguishable from
# a server that simply ignored the signal. That cost two force-kills.
#   0 = event delivered
#   2 = AttachConsole failed (target has no console — check how it was launched)
#   3 = GenerateConsoleCtrlEvent failed
#   4 = target pid not running
param([Parameter(Mandatory=$true)][int]$TargetPid)

if (-not (Get-Process -Id $TargetPid -ErrorAction SilentlyContinue)) { exit 4 }

Add-Type -Namespace Win32 -Name CtrlApi -MemberDefinition @'
[DllImport("kernel32.dll", SetLastError=true)] public static extern bool FreeConsole();
[DllImport("kernel32.dll", SetLastError=true)] public static extern bool AttachConsole(uint pid);
[DllImport("kernel32.dll", SetLastError=true)] public static extern bool GenerateConsoleCtrlEvent(uint evt, uint group);
'@

[Win32.CtrlApi]::FreeConsole() | Out-Null
if (-not [Win32.CtrlApi]::AttachConsole([uint32]$TargetPid)) { exit 2 }

# 1 = CTRL_BREAK_EVENT. Valheim's dedicated server handles BREAK (saves and
# exits) but ignores CTRL_C entirely.
if (-not [Win32.CtrlApi]::GenerateConsoleCtrlEvent(1, 0)) { exit 3 }

Start-Sleep -Seconds 1
exit 0
