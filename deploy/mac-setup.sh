#!/bin/bash
# Set up a Mac as a web server for the site.
#
# The machine serves the site and nothing else: it never scrapes, never writes
# the catalogue, and never publishes. It pulls the catalogue the collector
# publishes, runs the app against a read-only copy, and offers itself to
# Cloudflare as another connector for the same tunnel.
#
# Everything it needs to survive a reboot is registered with launchd, because a
# process started from a terminal dies with the terminal - the same mistake that
# cost an afternoon on the Windows box.
#
# Run it from the repo, after filling in .env:
#   bash deploy/mac-setup.sh
set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
DATA="$HOME/coursecompass/data"
LOGS="$HOME/coursecompass/logs"
AGENTS="$HOME/Library/LaunchAgents"

say() { printf '\n== %s\n' "$1"; }

say "checking what is installed"
missing=0
for tool in docker rclone cloudflared; do
    if command -v "$tool" >/dev/null 2>&1; then
        printf '  %-12s %s\n' "$tool" "$(command -v "$tool")"
    else
        printf '  %-12s MISSING\n' "$tool"; missing=1
    fi
done
if [ "$missing" = 1 ]; then
    cat <<'EOF'

  Install what is missing, then run this again:
    brew install rclone cloudflared
    brew install --cask docker      # then open Docker once so it finishes setup
EOF
    exit 1
fi

say "checking the environment"
if [ ! -f "$REPO/.env" ]; then
    echo "  no .env - copy .env.example to .env and fill it in first"; exit 1
fi
# shellcheck disable=SC1091
set -a; . "$REPO/.env"; set +a
for v in GITHUB_OWNER CATALOGUE_DIR ConnectionStrings__UserData \
         Authentication__Google__ClientId Authentication__Google__ClientSecret; do
    if [ -z "${!v:-}" ]; then echo "  $v is empty in .env"; exit 1; fi
    printf '  %-38s set\n' "$v"
done
for v in R2_BUCKET RCLONE_CONFIG_R2_TYPE RCLONE_CONFIG_R2_PROVIDER \
         RCLONE_CONFIG_R2_ENDPOINT RCLONE_CONFIG_R2_ACCESS_KEY_ID \
         RCLONE_CONFIG_R2_SECRET_ACCESS_KEY; do
    if [ -z "${!v:-}" ]; then echo "  $v is empty in .env (needed to pull the catalogue)"; exit 1; fi
done

say "checking the tunnel credentials"
if [ ! -f "$HOME/.cloudflared/config.yml" ]; then
    cat <<'EOF'
  ~/.cloudflared/config.yml is missing. Copy these from the collector:
    config.yml
    <tunnel-id>.json
    cert.pem
  The config's ingress should point at http://localhost:5215, and its
  credentials-file at the .json path ON THIS MACHINE.
EOF
    exit 1
fi
echo "  found $(ls "$HOME/.cloudflared" | tr '\n' ' ')"

mkdir -p "$DATA" "$LOGS" "$AGENTS"
say "first catalogue pull (this is ~58 MB, so give it a moment)"
CATALOGUE_DB="$DATA/courseplanner.db" bash "$REPO/deploy/pull-catalogue.sh"
ls -lh "$DATA/courseplanner.db" | awk '{print "  "$5"  "$9}'

say "registering the catalogue pull with launchd (every 10 minutes)"
cat > "$AGENTS/com.coursecompass.pull.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key><string>com.coursecompass.pull</string>
  <key>ProgramArguments</key>
  <array>
    <string>/bin/bash</string>
    <string>$REPO/deploy/pull-catalogue.sh</string>
  </array>
  <!-- The pull needs the same R2 settings the shell has; launchd starts with none. -->
  <key>EnvironmentVariables</key>
  <dict>
    <key>PATH</key><string>$(dirname "$(command -v rclone)"):/usr/bin:/bin</string>
    <key>CATALOGUE_DB</key><string>$DATA/courseplanner.db</string>
    <key>R2_BUCKET</key><string>$R2_BUCKET</string>
    <key>RCLONE_CONFIG_R2_TYPE</key><string>$RCLONE_CONFIG_R2_TYPE</string>
    <key>RCLONE_CONFIG_R2_PROVIDER</key><string>$RCLONE_CONFIG_R2_PROVIDER</string>
    <key>RCLONE_CONFIG_R2_ENDPOINT</key><string>$RCLONE_CONFIG_R2_ENDPOINT</string>
    <key>RCLONE_CONFIG_R2_ACCESS_KEY_ID</key><string>$RCLONE_CONFIG_R2_ACCESS_KEY_ID</string>
    <key>RCLONE_CONFIG_R2_SECRET_ACCESS_KEY</key><string>$RCLONE_CONFIG_R2_SECRET_ACCESS_KEY</string>
  </dict>
  <key>StartInterval</key><integer>600</integer>
  <key>RunAtLoad</key><true/>
  <key>StandardOutPath</key><string>$LOGS/pull.log</string>
  <key>StandardErrorPath</key><string>$LOGS/pull.log</string>
</dict>
</plist>
EOF

say "registering the tunnel with launchd"
cat > "$AGENTS/com.coursecompass.tunnel.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key><string>com.coursecompass.tunnel</string>
  <!-- The subcommand is spelled out: cloudflared with no arguments prints help
       and exits, which on Windows produced a service that crashlooped. -->
  <key>ProgramArguments</key>
  <array>
    <string>$(command -v cloudflared)</string>
    <string>--config</string>
    <string>$HOME/.cloudflared/config.yml</string>
    <string>tunnel</string>
    <string>run</string>
  </array>
  <key>RunAtLoad</key><true/>
  <key>KeepAlive</key><true/>
  <key>StandardOutPath</key><string>$LOGS/tunnel.log</string>
  <key>StandardErrorPath</key><string>$LOGS/tunnel.log</string>
</dict>
</plist>
EOF

for label in com.coursecompass.pull com.coursecompass.tunnel; do
    launchctl unload "$AGENTS/$label.plist" 2>/dev/null || true
    launchctl load  "$AGENTS/$label.plist"
    echo "  loaded $label"
done

say "starting the site"
( cd "$REPO" && docker compose pull && docker compose up -d )

say "state"
docker compose -f "$REPO/docker-compose.yml" ps 2>/dev/null || true
printf '  local:  '; curl -s -o /dev/null -w 'HTTP %{http_code}\n' http://127.0.0.1:5215/ || echo 'no answer yet'
printf '  public: '; curl -s -o /dev/null -w 'HTTP %{http_code}\n' https://utahcoursecompass.com/ || true

cat <<EOF

Two things left, both by hand:

  1. Stop this Mac sleeping, or it is a dead server whenever the lid is shut:
       sudo pmset -a sleep 0 disablesleep 1
     and in System Settings > Battery > Options, prevent sleeping on power.

  2. Set Docker Desktop to start at login (its Settings > General), so the
     container comes back after a reboot.

Check it worked from another machine:
  cloudflared tunnel info coursecompass    # this Mac should appear as a connector
  tail -f $LOGS/tunnel.log
EOF
