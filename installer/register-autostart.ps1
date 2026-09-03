# Registers a per-user logon Scheduled Task to autostart the app, and removes any
# legacy HKCU "Run" value so the two mechanisms can't both fire and double-launch.
#
# Windows 11 throttles/delays HKCU "Run" startup apps (they often don't launch
# promptly, or at all, after a reboot), so the durable autostart is a logon task.
# Called by the Inno installer's [Run] step with the installed exe path. Wrapped in
# try/catch so a task-registration hiccup never fails the install.
#
# Settings mirror the live "Clip Autostart" / "WinShot Autostart" tasks:
#   AtLogOn, -User $env:USERNAME, Delay PT3S, Interactive, Limited,
#   ExecutionTimeLimit 0, MultipleInstances IgnoreNew.

param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$TaskName,
    [Parameter(Mandatory = $true)][string]$RunValueName
)

try {
    # Drop the legacy Run-key entry the old installer created (if present).
    Remove-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" `
        -Name $RunValueName -ErrorAction SilentlyContinue

    $workingDir = Split-Path -Parent $Exe
    # --ensure-running makes a duplicate launch exit quietly instead of popping the palette open
    # on whoever is mid-keystroke, which is what lets the watchdog trigger below refire safely.
    # This script had drifted from Install-ClipStartup.ps1 and registered neither the argument nor
    # the watchdog, so every installer-made task was a plain logon launch with no recovery.
    $action    = New-ScheduledTaskAction -Execute $Exe -Argument "--ensure-running" -WorkingDirectory $workingDir
    $trigger   = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
    $trigger.Delay = "PT3S"
    # Watchdog: something on this machine (EDR, most likely) can hard-kill Clip without any trace.
    # Refiring every 30 minutes relaunches it; when it is already running the fire is a no-op.
    $watchdog  = New-ScheduledTaskTrigger -Once -At (Get-Date).Date `
        -RepetitionInterval (New-TimeSpan -Minutes 30) -RepetitionDuration (New-TimeSpan -Days 3650)
    $settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
        -StartWhenAvailable -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
    $principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Limited
    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger @($trigger, $watchdog) `
        -Settings $settings -Principal $principal -Force | Out-Null

    Write-Output "Registered logon task '$TaskName' -> $Exe"
}
catch {
    # Never fail the install over autostart; the app still installs and runs.
    Write-Warning "Autostart task registration failed: $($_.Exception.Message)"
}
