# Start the published site with the configuration the app expects.
#
# The account settings live as USER environment variables (set once, by hand).
# A shell that does not inherit them makes the app run with accounts disabled
# and no sign-in button, which is silent - nothing logs a missing connection
# string. So they are read from the user environment explicitly here.
#
# Run at startup by the "CourseCompass site" task (install-startup.ps1), or by
# hand. Does nothing if the site is already up. Values are never printed: only
# whether each one was found.
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$app  = Join-Path $root 'publish\web\Web.dll'

if (Get-NetTCPConnection -LocalPort 5215 -State Listen -ErrorAction SilentlyContinue) {
    "  already listening on 5215"; exit 0
}

$names = 'ConnectionStrings__UserData',
         'ConnectionStrings__UserDataDirect',
         'Authentication__Google__ClientId',
         'Authentication__Google__ClientSecret'

foreach ($n in $names) {
    $v = [Environment]::GetEnvironmentVariable($n, 'User')
    if ([string]::IsNullOrEmpty($v)) {
        "  {0,-42} MISSING - accounts will be off" -f $n
    } else {
        Set-Item -Path ("env:" + $n) -Value $v
        "  {0,-42} loaded ({1} chars)" -f $n, $v.Length
    }
}

# appsettings.json holds a path relative to the repo root, which does not
# resolve from the app's own folder; the content root has to stay there so the
# rest of appsettings still loads.
$db = Join-Path $root 'data\courseplanner.db'
if (-not (Test-Path $db)) { throw "no catalogue at $db" }
$env:ConnectionStrings__CoursePlanner = "Data Source=$db"
"  {0,-42} {1}" -f 'ConnectionStrings__CoursePlanner', $db

Start-Process -FilePath 'dotnet' -ArgumentList $app, '--urls', 'http://127.0.0.1:5215' -WorkingDirectory (Join-Path $root 'publish\web') -WindowStyle Hidden
Start-Sleep -Seconds 12
$c = Get-NetTCPConnection -LocalPort 5215 -State Listen -ErrorAction SilentlyContinue
if ($c) { "  listening, pid " + $c[0].OwningProcess } else { "  NOT LISTENING"; exit 1 }
