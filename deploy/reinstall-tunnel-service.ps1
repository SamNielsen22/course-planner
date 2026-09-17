# Re-register the cloudflared service, now that its configuration exists.
#
# `cloudflared service install` reads the config at install time and bakes the
# arguments into the service. Installed before the config was in the SYSTEM
# profile, it registered with no arguments at all - so the service started,
# found no tunnel to run and shut itself down again, leaving it wedged in
# Stop Pending. The event log says exactly that:
#   Cloudflared service arguments: [.../cloudflared.exe]
#   cloudflared starting graceful shutdown
#
# Uninstalling and reinstalling with the config present fixes it. A manually
# started cloudflared keeps the site up throughout, so there is no outage.
#
# Run elevated:
#   powershell -Command "Start-Process powershell -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-NoExit','-File','<this file>' -Verb RunAs"

$ErrorActionPreference = 'Continue'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
        ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Must run as Administrator.'
}

$exe = 'C:\Program Files (x86)\cloudflared\cloudflared.exe'
$systemProfile = Join-Path $env:SystemRoot 'System32\config\systemprofile\.cloudflared'

"--- config the service will read ---"
if (Test-Path $systemProfile) { Get-ChildItem $systemProfile | ForEach-Object { "  $($_.Name)" } }
else { throw "No config at $systemProfile - run install-tunnel-service.ps1 first." }

# The wedged service will not stop on request, so end its process directly.
$svc = Get-CimInstance Win32_Service -Filter "Name='Cloudflared'" -ErrorAction SilentlyContinue
if ($svc -and $svc.ProcessId -gt 0) {
    "--- ending the stuck service process (pid $($svc.ProcessId)) ---"
    Stop-Process -Id $svc.ProcessId -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 3
}

"--- uninstalling ---"
& $exe service uninstall 2>&1 | ForEach-Object { "  $_" }
Start-Sleep -Seconds 3

"--- installing (config is present now, so arguments get baked in) ---"
& $exe service install 2>&1 | ForEach-Object { "  $_" }
Start-Sleep -Seconds 5

"--- result ---"
$reg = Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\Cloudflared' -ErrorAction SilentlyContinue
"  ImagePath: $($reg.ImagePath)"
$svc = Get-Service Cloudflared -ErrorAction SilentlyContinue
if ($svc) {
    if ($svc.Status -ne 'Running') { Start-Service Cloudflared; Start-Sleep -Seconds 5; $svc = Get-Service Cloudflared }
    "  service: $($svc.Status), start type $($svc.StartType)"
}
"  (an ImagePath containing 'tunnel run' means it is registered correctly)"
