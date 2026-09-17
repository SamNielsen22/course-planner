# Give the cloudflared Windows service its configuration.
#
# `cloudflared service install` registers a service that runs as LocalSystem,
# which does not share your user profile, so it never sees the config.yml and
# credentials written to C:\Users\<you>\.cloudflared by `tunnel login`. Without
# them the service starts, finds no tunnel to run, and does nothing - while a
# manually started cloudflared keeps the site up and hides the problem.
#
# This copies the three files into the SYSTEM profile the service does read,
# then restarts it. The credentials end up under System32, readable only by
# SYSTEM and administrators, which is a tighter place for them than a user
# profile.
#
# Run this elevated:
#   powershell -Command "Start-Process powershell -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File','<this file>' -Verb RunAs"

$ErrorActionPreference = 'Stop'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
        ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'This must run as Administrator: it writes into the SYSTEM profile and restarts a service.'
}

# The profile of the account the service runs as, not the person who installed it.
$systemProfile = Join-Path $env:SystemRoot 'System32\config\systemprofile\.cloudflared'

# Whoever is logged on owns the files `cloudflared tunnel login` produced. Prefer
# an explicit path so this works when run elevated as a different account.
$userProfile = if ($env:TUNNEL_SOURCE) { $env:TUNNEL_SOURCE }
               else { Join-Path ([Environment]::GetFolderPath('UserProfile')) '.cloudflared' }
if (-not (Test-Path $userProfile)) {
    throw "No tunnel files at $userProfile. Set TUNNEL_SOURCE to the folder holding config.yml and the tunnel's .json."
}

New-Item -ItemType Directory -Force $systemProfile | Out-Null
foreach ($name in 'config.yml', 'cert.pem') {
    $from = Join-Path $userProfile $name
    if (Test-Path $from) { Copy-Item $from $systemProfile -Force; "copied $name" }
    else { "missing $name (continuing)" }
}
# The tunnel's credentials file is named after its id, so copy every .json.
Get-ChildItem $userProfile -Filter '*.json' | ForEach-Object {
    Copy-Item $_.FullName $systemProfile -Force; "copied $($_.Name)"
}

Restart-Service Cloudflared
Start-Sleep -Seconds 5
$svc = Get-Service Cloudflared
"service: $($svc.Name) [$($svc.Status)]"
"config now at: $systemProfile"
Get-ChildItem $systemProfile | ForEach-Object { "  $($_.Name)" }
