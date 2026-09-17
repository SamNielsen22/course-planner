#!/bin/sh
# Pull the newest catalogue from the R2 bucket and swap it into place.
#
# Run on each web server every 10 minutes. rclone checks the bucket first and
# skips the download when the copy there hasn't changed, so most runs cost one
# check and nothing else. When it has changed, a single rename replaces the
# live file: a request never sees a half-written database, and one that already
# has the old file open finishes on it before the next opens the new one.
#
# The site must open the catalogue read-only, so it never creates WAL sidecar
# files that a swap would leave behind:
#   ConnectionStrings:CoursePlanner = "Data Source=/path/courseplanner.db;Mode=ReadOnly"
#
# Needs these in the environment. A READ-ONLY bucket token is enough here.
#   CATALOGUE_DB                        the live catalogue path on this machine
#   R2_BUCKET                           the bucket name
#   RCLONE_CONFIG_R2_TYPE               s3
#   RCLONE_CONFIG_R2_PROVIDER           Cloudflare
#   RCLONE_CONFIG_R2_ENDPOINT           https://<account id>.r2.cloudflarestorage.com
#   RCLONE_CONFIG_R2_ACCESS_KEY_ID      the read-only token's key
#   RCLONE_CONFIG_R2_SECRET_ACCESS_KEY  its secret
set -eu

DB="${CATALOGUE_DB:?set CATALOGUE_DB to the live catalogue path on this machine}"
STAGE="$(dirname "$DB")/incoming"
NEW="$STAGE/courseplanner.db"
mkdir -p "$STAGE"

# Download only if the bucket copy differs; rclone keeps the bucket's modtime.
# --quiet drops the "no config file" notice; real errors still print.
rclone copy "r2:${R2_BUCKET}/catalogue/courseplanner.db" "$STAGE/" --s3-no-check-bucket --quiet

# Swap only when the staged copy is newer than what is live. cp -p carries the
# modtime across, so this stays false until a newer upload actually lands.
if [ ! -e "$DB" ] || [ "$NEW" -nt "$DB" ]; then
    cp -p "$NEW" "$DB.new" && mv -f "$DB.new" "$DB"
    echo "$(date -u +%FT%TZ) catalogue refreshed"
fi
