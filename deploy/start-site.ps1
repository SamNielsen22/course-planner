# Start the published site with the configuration the app expects.
#
# Run at startup by the "CourseCompass site" task (install-startup.ps1), or by
# hand. Does nothing if the site is already up.
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$app  = Join-Path $root 'publish\web\Web.dll'

if (Get-NetTCPConnection -LocalPort 5215 -State Listen -ErrorAction SilentlyContinue) {
    "  already listening on 5215"; exit 0
}

# appsettings.json holds a path relative to the repo root, which does not
# resolve from the app's own folder; the content root has to stay there so the
# rest of appsettings still loads.
$db = Join-Path $root 'data\courseplanner.db'
if (-not (Test-Path $db)) { throw "no catalogue at $db" }
$env:ConnectionStrings__CoursePlanner = "Data Source=$db"
"  {0,-34} {1}" -f 'ConnectionStrings__CoursePlanner', $db

# The keys that sign every visitor's schedule cookie. They must live outside
# the process, or a restart would empty every schedule; data\ is not in git.
$keys = Join-Path $root 'data\keys'
New-Item -ItemType Directory -Force $keys | Out-Null
$env:DataProtection__KeysPath = $keys
"  {0,-34} {1} ({2} key file(s))" -f 'DataProtection__KeysPath', $keys, (Get-ChildItem $keys -Filter 'key-*.xml' | Measure-Object).Count

Start-Process -FilePath 'dotnet' -ArgumentList $app, '--urls', 'http://127.0.0.1:5215' -WorkingDirectory (Join-Path $root 'publish\web') -WindowStyle Hidden
Start-Sleep -Seconds 12
$c = Get-NetTCPConnection -LocalPort 5215 -State Listen -ErrorAction SilentlyContinue
if ($c) { "  listening, pid " + $c[0].OwningProcess } else { "  NOT LISTENING"; exit 1 }
