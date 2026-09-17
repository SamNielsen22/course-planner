# Register the Cloudflare tunnel as a Windows service, with its arguments
# stated explicitly.
#
# Why not `cloudflared service install`: it reads the config at install time and
# bakes the arguments in. Run before the config existed in the SYSTEM profile,
# it registered the bare executable with no arguments, so the service started,
# found no tunnel to run, shut itself down, and wedged in Stop Pending - where a
# stop request never completes and only a reboot clears it.
#
# So this creates a service under its own name, with --config and `tunnel run`
# spelled out. Nothing is inferred, and the wedged Cloudflared service is left
# alone to disappear on the next reboot.
#
# Run elevated:
#   powershell -Command "Start-Process powershell -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-NoExit','-File','<this file>' -Verb RunAs"

$ErrorActionPreference = 'Continue'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
        ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Must run as Administrator.'
}

$name   = 'CourseCompassTunnel'
$exe    = 'C:\Program Files (x86)\cloudflared\cloudflared.exe'
$config = Join-Path $env:SystemRoot 'System32\config\systemprofile\.cloudflared\config.yml'

if (-not (Test-Path $exe))    { throw "cloudflared not found at $exe" }
if (-not (Test-Path $config)) { throw "no config at $config - run install-tunnel-service.ps1 first" }

"--- config the service will use ---"
Get-ChildItem (Split-Path $config) | ForEach-Object { "  $($_.Name)" }

# The stock Cloudflared service is registered as the bare executable with no
# subcommand, so it exits at once and Windows restarts it every 20 seconds.
# Disable and delete it, or it crashloops in the log forever.
$old = Get-CimInstance Win32_Service -Filter "Name='Cloudflared'" -ErrorAction SilentlyContinue
if ($old) {
    "--- removing the crashlooping Cloudflared service ---"
    & sc.exe config Cloudflared start= disabled 2>&1 | ForEach-Object { "  $_" }
    if ($old.ProcessId -gt 0) { & taskkill /F /PID $old.ProcessId 2>&1 | ForEach-Object { "  $_" } }
    & sc.exe delete Cloudflared 2>&1 | ForEach-Object { "  $_" }
    Start-Sleep -Seconds 2
}

# Point the service's config at the credentials beside it, rather than at the
# copy in a user profile: the service runs as SYSTEM and should not depend on
# another account's folder existing or staying readable.
$creds = Get-ChildItem (Split-Path $config) -Filter '*.json' | Select-Object -First 1
if ($creds) {
    (Get-Content $config) -replace '^credentials-file:.*', "credentials-file: $($creds.FullName)" |
        Set-Content $config -Encoding ascii
    "--- credentials-file now points at $($creds.FullName) ---"
}

if (Get-Service $name -ErrorAction SilentlyContinue) {
    "--- removing a previous $name ---"
    & sc.exe delete $name 2>&1 | ForEach-Object { "  $_" }
    Start-Sleep -Seconds 2
}

"--- creating $name ---"
$bin = '"{0}" --config "{1}" tunnel run' -f $exe, $config
New-Service -Name $name -BinaryPathName $bin -StartupType Automatic `
            -DisplayName 'Cloudflare Tunnel (Course Compass)' `
            -Description 'Publishes the local site at utahcoursecompass.com.' | Out-Null
"  BinaryPathName: $bin"

"--- starting ---"
Start-Service $name
Start-Sleep -Seconds 8
$svc = Get-Service $name
"  $name is $($svc.Status), start type $($svc.StartType)"
"done - check that Cloudflare now reports two connectors."
