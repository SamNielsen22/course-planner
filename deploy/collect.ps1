# One publishing cycle, guarded by a lease so exactly one machine scrapes at a time.
#
# Schedule this to fire OFTEN (every 5 minutes) on every machine allowed to
# publish, with Task Scheduler's default "do not start a new instance while one
# is running". A pass takes ~25-40 minutes; triggers during it are ignored, and
# the first one after it ends starts the next pass. Passes run back to back with
# no tuning, however long they take. The same script runs everywhere; two
# variables set the role:
#   PUBLISHER_NAME      a short name for this machine, e.g. "home" or "parents"
#   PUBLISHER_PRIORITY  1 is preferred and claims at once; a higher number only
#                       steps in after the lease has sat expired a full extra cycle
# plus the R2_* / RCLONE_CONFIG_R2_* variables described in publish-catalogue.ps1.
#
# The lease is a small JSON file in the bucket: who is publishing and until when.
# Each cycle applies one rule:
#   mine and valid              -> renew it, run the pass, publish
#   free or expired             -> claim it and run (priority 1 now; others wait a cycle)
#   held by a LOWER priority    -> outranked, so take it back and run
#   held by a HIGHER priority   -> stand down; pull the latest catalogue instead,
#                                  so this machine is current if it ever takes over
#
# A claim that was not already ours pulls the catalogue first, so a takeover or
# a handback always starts from the freshest copy rather than a stale local one.
#
# -DryRun exercises the lease rules and writes the lease, but skips the scrape
# and the upload, for testing the failover without touching the registrar.
param([switch]$DryRun)

$ErrorActionPreference = 'Stop'
$repo      = Split-Path -Parent $PSScriptRoot
$me        = $env:PUBLISHER_NAME
$priority  = [int]$env:PUBLISHER_PRIORITY
if (-not $me -or -not $priority) { throw 'set PUBLISHER_NAME and PUBLISHER_PRIORITY' }

if (-not $env:DESCRIPTION_SLICE)      { $env:DESCRIPTION_SLICE = 400 }   # course pages re-read per overnight cycle
if (-not $env:DESCRIPTION_FROM_HOUR) { $env:DESCRIPTION_FROM_HOUR = 0 }    # local hour the quiet window opens
if (-not $env:DESCRIPTION_TO_HOUR)   { $env:DESCRIPTION_TO_HOUR = 7 }      # and closes; outside it, seats only
$leaseMinutes = 60     # a claim must outlive the longest pass, so a live holder never lapses
$graceMinutes = 30     # how long past expiry a standby waits before stepping in
$lease        = "r2:$env:R2_BUCKET/lease.json"
$catalogue    = "r2:$env:R2_BUCKET/catalogue/courseplanner.db"
$liveDb       = Join-Path $repo 'data\courseplanner.db'
$now          = [DateTime]::UtcNow

function Read-Lease {
    try { $text = & rclone cat $lease --quiet 2>$null; if ($LASTEXITCODE -ne 0 -or -not $text) { return $null }; return ($text | ConvertFrom-Json) }
    catch { return $null }
}
function Lease-Expiry($l) { return ([DateTime]::Parse($l.expires)).ToUniversalTime() }
function Write-Lease {
    # Stamped from the clock at write time, not the script's start: renewing
    # after a long step must grant a full window, not what is left of the first.
    $body = @{ owner = $me; priority = $priority; expires = [DateTime]::UtcNow.AddMinutes($leaseMinutes).ToString('o') } | ConvertTo-Json -Compress
    $tmp = Join-Path $env:TEMP 'coursecompass-lease.json'
    Set-Content -Path $tmp -Value $body -Encoding Ascii
    # The token is scoped to the bucket, so skip the create-bucket probe it cannot pass.
    & rclone copyto $tmp $lease --s3-no-check-bucket --quiet
    if ($LASTEXITCODE -ne 0) { throw 'could not write the lease' }
}
function Pull-Catalogue {
    # Download only if the bucket copy differs, then swap it in. Retries the
    # rename briefly in case a reader has the file open at that instant.
    $stage = Join-Path (Split-Path $liveDb) 'incoming'
    New-Item -ItemType Directory -Force $stage | Out-Null
    & rclone copy $catalogue "$stage\" --s3-no-check-bucket --quiet
    if ($LASTEXITCODE -ne 0) { throw 'could not pull the catalogue' }
    $new = Join-Path $stage 'courseplanner.db'
    if ((Test-Path $liveDb) -and ((Get-Item $new).LastWriteTimeUtc -le (Get-Item $liveDb).LastWriteTimeUtc)) { return }
    for ($try = 1; $try -le 5; $try++) {
        try {
            Copy-Item $new "$liveDb.new" -Force
            Move-Item "$liveDb.new" $liveDb -Force
            foreach ($side in "$liveDb-wal", "$liveDb-shm") { if (Test-Path $side) { Remove-Item $side -Force -ErrorAction SilentlyContinue } }
            # A VACUUM INTO copy arrives in rollback-journal mode; a publisher
            # writes while others read, so put it back into WAL.
            & python -c "import sqlite3,sys; sqlite3.connect(sys.argv[1]).execute('pragma journal_mode=WAL')" $liveDb
            "  pulled a newer catalogue"; return
        } catch { if ($try -eq 5) { throw }; Start-Sleep -Seconds 2 }
    }
}

# ---- decide -----------------------------------------------------------------
$held     = Read-Lease
$expired  = ($null -eq $held) -or ((Lease-Expiry $held) -lt $now)
$run      = $false
$takeover = $false

if ($held -and $held.owner -eq $me -and -not $expired) {
    $run = $true                                                   # mine: renew
}
elseif ($expired) {
    if ($priority -eq 1) { $run = $true; $takeover = $true }        # preferred: claim now
    else {
        # Wait the grace period past expiry, then a random few minutes, then
        # look again: if still free, claim. The jitter keeps two standbys apart.
        if ($held) { $since = Lease-Expiry $held } else { $since = $now.AddDays(-1) }
        if ($now -gt $since.AddMinutes($graceMinutes)) {
            Start-Sleep -Seconds (Get-Random -Minimum 0 -Maximum 300)
            $again = Read-Lease
            if (($null -eq $again) -or ((Lease-Expiry $again) -lt [DateTime]::UtcNow)) { $run = $true; $takeover = $true }
        }
    }
}
elseif ([int]$held.priority -gt $priority) {
    $run = $true; $takeover = $true                                # outrank the holder
}
# otherwise: a higher-priority machine holds a valid lease; stand down.

# ---- act --------------------------------------------------------------------
if (-not $run) {
    "$($now.ToString('u')) standing down; $($held.owner) holds the lease until $($held.expires)"
    Pull-Catalogue
    exit 0
}

if ($takeover) { "$($now.ToString('u')) claiming the lease (was: $(if ($held) { $held.owner } else { 'free' }))"; Pull-Catalogue }
else           { "$($now.ToString('u')) renewing my lease" }
Write-Lease

if ($DryRun) { "  dry run: skipping the re-crawl, the seats pass and the upload"; exit 0 }

$ingest = Join-Path $repo 'Ingest.Schedule\bin\Release\net10.0\Ingest.Schedule.dll'
if (-not (Test-Path $ingest)) { $ingest = Join-Path $repo 'Ingest.Schedule\bin\Debug\net10.0\Ingest.Schedule.dll' }

# Once a day, re-crawl the terms under way before the seats pass. The seats pass
# can only update and cancel rows it already has, so a class the registrar added
# since the last crawl is invisible to it; the re-crawl is what brings the class
# in, with its component, units, times, room and instructors. Running it first
# means the seats pass that follows gives those new rows their counts in the same
# cycle. The stamp is this machine's own: after a failover the standby may
# re-crawl once sooner than it needed to, which costs one extra pass.
$hour = [int](Get-Date).Hour                                       # local, for the quiet window

# Like the description refresh, this is kept to the quiet hours so it never
# delays seats while anyone is registering. If a night is missed - the machine
# was off, or a standby held the lease - it goes ahead regardless once it is
# badly overdue, so it can never be postponed indefinitely.
$stamp = Join-Path $repo 'data\last-recrawl.txt'
$age = [double]::MaxValue
if (Test-Path $stamp) {
    try { $age = ([DateTime]::UtcNow - [DateTime]::Parse((Get-Content $stamp -Raw).Trim()).ToUniversalTime()).TotalHours }
    catch { $age = [double]::MaxValue }                            # an unreadable stamp means crawl
}
$quiet = $hour -ge [int]$env:DESCRIPTION_FROM_HOUR -and $hour -lt [int]$env:DESCRIPTION_TO_HOUR
$due = ($age -ge 20 -and $quiet) -or ($age -ge 36)
if ($due) {
    "$([DateTime]::UtcNow.ToString('u')) daily re-crawl of the terms under way"
    & dotnet $ingest recrawl
    if ($LASTEXITCODE -ne 0) { throw 're-crawl failed' }
    Set-Content -Path $stamp -Value ([DateTime]::UtcNow.ToString('o')) -Encoding Ascii
    Write-Lease                                                    # a long step; renew so no standby steps in
}


# Course description pages, re-read in slices during the quiet hours only.
#
# A course holds one description and prerequisite list for every term and the
# registrar edits them between terms, so they have to be re-read rather than
# trusted forever. But they change about once a year, while seat counts change
# by the minute, so this must never slow the seats pass down when anyone is
# actually registering. Overnight the slice is large and the cycle is long and
# nobody minds; through the day no time is spent here at all and cycles stay as
# short as a seats-only pass.
if ($quiet) {
    & dotnet $ingest descriptions --limit $env:DESCRIPTION_SLICE
    if ($LASTEXITCODE -ne 0) { throw 'description refresh failed' }
    Write-Lease                                                    # keep the lease fresh before the seats pass
}

& dotnet $ingest seats
if ($LASTEXITCODE -ne 0) { throw 'seats pass failed' }
& powershell -NoProfile -File (Join-Path $PSScriptRoot 'publish-catalogue.ps1')
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

$now = [DateTime]::UtcNow
Write-Lease                                                        # renew after the pass too
"$($now.ToString('u')) cycle complete"
