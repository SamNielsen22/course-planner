# Publish a consistent snapshot of the catalogue to the R2 bucket.
#
# Run on the collector right after each seats pass. VACUUM INTO writes a clean
# copy even while the live file is in use, so what goes up is never a torn
# file. rclone then uploads it; each web server pulls it down on its own timer
# (see pull-catalogue.sh).
#
# Needs these in the environment, set at user level so a scheduled task
# inherits them. No credential is ever written into this file.
#   R2_BUCKET                           the bucket name
#   RCLONE_CONFIG_R2_TYPE               s3
#   RCLONE_CONFIG_R2_PROVIDER           Cloudflare
#   RCLONE_CONFIG_R2_ENDPOINT           https://<account id>.r2.cloudflarestorage.com
#   RCLONE_CONFIG_R2_ACCESS_KEY_ID      a token with write access to the bucket
#   RCLONE_CONFIG_R2_SECRET_ACCESS_KEY  that token's secret

$ErrorActionPreference = 'Stop'
$repo  = Split-Path -Parent $PSScriptRoot
$live  = Join-Path $repo 'data\courseplanner.db'
$stage = Join-Path $repo 'data\publish'
$snap  = Join-Path $stage 'courseplanner.db'

New-Item -ItemType Directory -Force $stage | Out-Null
if (Test-Path $snap) { Remove-Item $snap -Force }

# A consistent, compacted copy of the live database.
& python -c "import sqlite3,sys; sqlite3.connect(sys.argv[1]).execute('VACUUM INTO ?', (sys.argv[2],))" $live $snap
if ($LASTEXITCODE -ne 0) { throw 'snapshot failed' }

# Upload. The token is scoped to the bucket, so skip the bucket-exists check.
# --quiet drops rclone's "no config file" notice (the environment is the config);
# real errors still print.
& rclone copy $snap "r2:$env:R2_BUCKET/catalogue/" --s3-no-check-bucket --quiet
if ($LASTEXITCODE -ne 0) { throw 'upload failed' }

"$(Get-Date -Format 'u') catalogue published"
