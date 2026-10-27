# One collection cycle: the daily re-crawl when it is due, the description
# refresh in the quiet hours, then the seats pass.
#
# Schedule this to fire OFTEN (every 5 minutes) with Task Scheduler's default
# "do not start a new instance while one is running". A pass takes ~25-40
# minutes; triggers during it are ignored, and the first one after it ends
# starts the next pass. Passes run back to back with no tuning, however long
# they take, and never two at once.
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

if (-not $env:DESCRIPTION_SLICE)     { $env:DESCRIPTION_SLICE = 400 }   # course pages re-read per overnight cycle
if (-not $env:DESCRIPTION_FROM_HOUR) { $env:DESCRIPTION_FROM_HOUR = 0 }  # local hour the quiet window opens
if (-not $env:DESCRIPTION_TO_HOUR)   { $env:DESCRIPTION_TO_HOUR = 7 }    # and closes; outside it, seats only

$ingest = Join-Path $repo 'Ingest.Schedule\bin\Release\net10.0\Ingest.Schedule.dll'
if (-not (Test-Path $ingest)) { $ingest = Join-Path $repo 'Ingest.Schedule\bin\Debug\net10.0\Ingest.Schedule.dll' }

"$([DateTime]::UtcNow.ToString('u')) starting a cycle"

# Once a day, re-crawl the terms under way before the seats pass. The seats pass
# can only update and cancel rows it already has, so a class the registrar added
# since the last crawl is invisible to it; the re-crawl is what brings the class
# in, with its component, units, times, room and instructors. Running it first
# means the seats pass that follows gives those new rows their counts in the same
# cycle.
#
# Like the description refresh, this is kept to the quiet hours so it never
# delays seats while anyone is registering. If a night is missed - the machine
# was off - it goes ahead regardless once it is badly overdue, so it can never
# be postponed indefinitely.
$hour  = [int](Get-Date).Hour                                      # local, for the quiet window
$quiet = $hour -ge [int]$env:DESCRIPTION_FROM_HOUR -and $hour -lt [int]$env:DESCRIPTION_TO_HOUR
$stamp = Join-Path $repo 'data\last-recrawl.txt'
$age   = [double]::MaxValue
if (Test-Path $stamp) {
    try { $age = ([DateTime]::UtcNow - [DateTime]::Parse((Get-Content $stamp -Raw).Trim()).ToUniversalTime()).TotalHours }
    catch { $age = [double]::MaxValue }                            # an unreadable stamp means crawl
}
if (($age -ge 20 -and $quiet) -or ($age -ge 36)) {
    "$([DateTime]::UtcNow.ToString('u')) daily re-crawl of the terms under way"
    & dotnet $ingest recrawl
    if ($LASTEXITCODE -ne 0) { throw 're-crawl failed' }
    Set-Content -Path $stamp -Value ([DateTime]::UtcNow.ToString('o')) -Encoding Ascii
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
}

& dotnet $ingest seats
if ($LASTEXITCODE -ne 0) { throw 'seats pass failed' }

"$([DateTime]::UtcNow.ToString('u')) cycle complete"
