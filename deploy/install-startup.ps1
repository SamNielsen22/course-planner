# Make the site come back on its own after a reboot. Run once, elevated.
#
#   1. A "CourseCompass site" task that runs start-site.ps1 at system startup,
#      as this user, without anyone logging in (S4U: no stored password, local
#      and outbound network access only, which is all the site needs).
#   2. The tunnel service restarts itself when it crashes. At boot it can start
#      before the network is up and exit; without a restart rule it stays down.
#   3. Starts the tunnel now.
#
#   powershell -Command "Start-Process powershell -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-NoExit','-File','<this file>' -Verb RunAs"
$ErrorActionPreference = 'Stop'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
        ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Must run as Administrator.'
}

$script = Join-Path $PSScriptRoot 'start-site.ps1'
$user   = "$env:USERDOMAIN\$env:USERNAME"

"--- site startup task ---"
$action    = New-ScheduledTaskAction -Execute 'powershell.exe' `
               -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$script`"" -WorkingDirectory $PSScriptRoot
# A minute's delay lets the network and the tunnel come up first.
$trigger   = New-ScheduledTaskTrigger -AtStartup
$trigger.Delay = 'PT1M'
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType S4U -RunLevel Limited
# If dotnet is not ready yet, try again every five minutes, up to an hour.
$settings  = New-ScheduledTaskSettingsSet -RestartCount 12 -RestartInterval (New-TimeSpan -Minutes 5) `
               -ExecutionTimeLimit (New-TimeSpan -Minutes 10) -StartWhenAvailable `
               -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
Register-ScheduledTask -TaskName 'CourseCompass site' -Action $action -Trigger $trigger `
    -Principal $principal -Settings $settings -Force | Out-Null
"  registered for $user"

"--- tunnel restarts after a crash ---"
& sc.exe failure CourseCompassTunnel reset= 86400 actions= restart/30000/restart/60000/restart/120000 | ForEach-Object { "  $_" }

"--- starting the tunnel ---"
Start-Service CourseCompassTunnel
Start-Sleep -Seconds 5
"  tunnel is " + (Get-Service CourseCompassTunnel).Status
"done"
