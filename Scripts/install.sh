#!/usr/bin/env bash
set -euo pipefail

REPO="${BASEPORT_REPO:-baseporteu/baseport}"
# Root installs exist to be services, they default where a service account can read: /root is 0700, /opt is not.
if [ "$(id -u)" = "0" ]; then
  DIR="${BASEPORT_DIR:-/opt/baseport}"
  BIN="${BASEPORT_BIN:-/usr/local/bin}"
else
  DIR="${BASEPORT_DIR:-$HOME/.baseport}"
  BIN="${BASEPORT_BIN:-$HOME/.local/bin}"
fi
API="https://api.github.com/repos/$REPO/releases"
INSTALLER="https://raw.githubusercontent.com/$REPO/main/Scripts/install.sh"

fail() { echo "$*" >&2; exit 1; }

[ "$(uname -s)" = "Linux" ] || fail "This installer covers Linux. On Windows use install.ps1."

case "$(uname -m)" in
  x86_64|amd64) ARCH="x64" ;;
  *) fail "There is no Baseport build for $(uname -m). Releases ship linux-x64 and win-x64." ;;
esac

for CMD in curl tar sha256sum; do
  command -v "$CMD" >/dev/null 2>&1 || fail "This installer needs $CMD, which is not on this machine."
done

# The install fails halfway through without this, once the download is already done and the user is already waiting.
writable() {
  local P="$1"
  while [ ! -e "$P" ]; do P=$(dirname "$P"); done
  [ -w "$P" ]
}
writable "$DIR" || fail "You cannot write to $DIR. Install as root, or somewhere you own:
  curl -sSL $INSTALLER | sudo bash
  BASEPORT_DIR=\"\$HOME/.baseport\" BASEPORT_BIN=\"\$HOME/.local/bin\" curl -sSL $INSTALLER | bash"
writable "$BIN" || fail "You cannot write to $BIN. Install as root, or pick another directory with BASEPORT_BIN:
  curl -sSL $INSTALLER | sudo bash
  BASEPORT_BIN=\"\$HOME/.local/bin\" curl -sSL $INSTALLER | bash"

TAG="${BASEPORT_VERSION:-}"
if [ -z "$TAG" ]; then
  TAG=$(curl -fsSL "$API/latest" 2>/dev/null | sed -n 's/.*"tag_name"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -1)
fi
if [ -z "$TAG" ]; then
  TAG=$(curl -fsSL "$API?per_page=1" 2>/dev/null | sed -n 's/.*"tag_name"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -1)
fi
[ -n "$TAG" ] || fail "Could not resolve a release tag from $REPO."

ASSET="Baseport-$TAG-linux-$ARCH.tar.gz"
BASE="https://github.com/$REPO/releases/download/$TAG"

TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT

echo "Installing $TAG into $DIR (override with BASEPORT_DIR)"
curl -fSL --progress-bar -o "$TMP/$ASSET" "$BASE/$ASSET" || fail "Release $TAG has no asset named $ASSET."
curl -fsSL -o "$TMP/$ASSET.sha256" "$BASE/$ASSET.sha256" || fail "Release $TAG has no checksum for $ASSET."

( cd "$TMP" && sha256sum -c "$ASSET.sha256" >/dev/null 2>&1 ) \
  || fail "Checksum mismatch for $ASSET. Refusing to install."

mkdir -p "$TMP/payload"
tar -xzf "$TMP/$ASSET" -C "$TMP/payload"

for KEEP in baseport.db baseport.db-shm baseport.db-wal baseport.key log uploads backups; do
  rm -rf "${TMP:?}/payload/$KEEP"
done

UPDATE="no"
if [ -e "$DIR/Baseport" ]; then UPDATE="yes"; fi
if [ -f "$DIR/appsettings.json" ]; then rm -f "$TMP/payload/appsettings.json"; fi

mkdir -p "$DIR"
cp -R "$TMP/payload/." "$DIR/"
chmod +x "$DIR/Baseport"

mkdir -p "$BIN"
cat > "$BIN/baseport" <<EOF
#!/bin/sh
# baseport wrapper
DIR="$DIR"
BIN="$BIN"
SELF="$BIN/baseport"
REPO="$REPO"
INSTALLER="$INSTALLER"
EOF
cat >> "$BIN/baseport" <<'SHIM'
set -eu
UNIT=/etc/systemd/system/baseport.service

die() { echo "$*" >&2; exit 1; }

# Telling someone to retype a command with sudo in front of it is a step a script can take itself.
need_root() {
  if [ "$(id -u)" = "0" ]; then return 0; fi
  command -v sudo >/dev/null 2>&1 || die "That needs root and there is no sudo here. Run it as root:
  su -c '$SELF $*'"
  # sudo -n fails when this user has no sudo rights at all, which is worth saying plainly instead of letting sudo prompt for a password it will then reject.
  if ! sudo -n true 2>/dev/null && [ ! -t 0 ]; then
    die "That needs root, and sudo cannot ask for a password here. Run it as root:
  sudo $SELF $*"
  fi
  echo "This needs root, running it again through sudo." >&2
  exec sudo -- "$SELF" "$@"
}

# Escalate only when the thing we are about to write is actually out of reach. Used by the commands that sometimes need root and sometimes do not: a user install in $HOME needs none, the same command against /opt needs all of it.
need_write() {
  CMD=$1
  shift
  for P in "$@"; do
    TARGET=$P
    [ -e "$TARGET" ] || TARGET=$(dirname "$TARGET")
    if [ ! -w "$TARGET" ]; then
      need_root "$CMD"
      return 0
    fi
  done
  return 0
}

need_service() {
  command -v systemctl >/dev/null 2>&1 || die "No systemd on this machine, so there is no service to control."
  [ -e "$UNIT" ] || die "There is no baseport.service yet. Create one: sudo $SELF service"
}

service_url() {
  URL=""
  if [ -e "$UNIT" ]; then
    URL=$(sed -n 's/.*--urls[= ]*\([^ ]*\).*/\1/p' "$UNIT" | head -1)
  fi
  [ -n "$URL" ] || URL="http://localhost:5000"
  URL=${URL%%;*}
  echo "$URL" | sed 's|//0\.0\.0\.0|//localhost|; s|//\[::\]|//localhost|; s|//\*|//localhost|'
}

case "${1:-}" in
uninstall|doctor|update) ;;
*) [ -x "$DIR/Baseport" ] || die "No Baseport in $DIR. Reinstall it:
  curl -sSL $INSTALLER | bash" ;;
esac

case "${1:-}" in
update)
  need_write update "$DIR" "$BIN"

  # Replacing a binary that is still running is what leaves a half-updated install: the old process keeps serving and the file it was started from is gone. --force so an install with no service, or one already stopped, is not an error here.
  "$SELF" stop --force

  # curl piped straight into bash reports the shell's exit code, not curl's, so a 404 or a dropped connection ran an empty script and called it a successful update. Download first, check it, then run it.
  TMP=$(mktemp) || die "Could not create a temporary file for the download."
  trap 'rm -f "$TMP"' EXIT INT TERM
  curl -fsSL "$INSTALLER" -o "$TMP" || die "Could not download the installer from $INSTALLER."
  [ -s "$TMP" ] || die "The installer downloaded from $INSTALLER was empty."
  BASEPORT_REPO="$REPO" BASEPORT_DIR="$DIR" BASEPORT_BIN="$BIN" sh "$TMP"
  ;;
service)
  shift
  need_root service "$@"
  command -v systemctl >/dev/null 2>&1 || die "No systemd on this machine."
  ARGS="$*"
  [ -n "$ARGS" ] || ARGS="--urls http://localhost:5000"

  NOLOGIN=/usr/sbin/nologin
  [ -x "$NOLOGIN" ] || NOLOGIN=/sbin/nologin
  id baseport >/dev/null 2>&1 || useradd --system --home "$DIR" --shell "$NOLOGIN" baseport
  chown -R baseport:baseport "$DIR"
  su -s /bin/sh baseport -c "test -r '$DIR/Baseport'" || die "The baseport user cannot read $DIR, the service would not start.
Reinstall as root, which lands in /opt/baseport, and try again:
  curl -sSL $INSTALLER | sudo bash"

  # A single-file build self-extracts before it runs, and the default base is a /var/tmp/.net owned by whoever ran it first, pin it somewhere the service user owns.
  cat > "$UNIT" <<UNITFILE
[Unit]
Description=Baseport
After=network.target

[Service]
User=baseport
WorkingDirectory=$DIR
Environment=DOTNET_BUNDLE_EXTRACT_BASE_DIR=$DIR/.net
ExecStart=$DIR/Baseport $ARGS
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
UNITFILE

  systemctl daemon-reload
  systemctl enable baseport >/dev/null 2>&1 || true
  systemctl restart baseport
  sleep 1
  systemctl is-active --quiet baseport || {
    systemctl status baseport --no-pager -n 15 >&2 || true
    die "baseport.service was written but is not running."
  }
  echo "baseport.service running $DIR/Baseport $ARGS"
  ;;
start)
  need_service
  need_root start
  systemctl start baseport
  systemctl is-active --quiet baseport || {
    systemctl status baseport --no-pager -n 15 >&2 || true
    die "baseport.service did not start."
  }
  echo "baseport.service started on $(service_url)."
  ;;
stop)
  shift
  FORCE=no
  [ "${1:-}" = "--force" ] && FORCE=yes

  if [ "$FORCE" = "no" ]; then
    need_service
    need_root stop
    systemctl stop baseport
    echo "baseport.service stopped. It comes back on the next boot until you run: sudo $SELF uninstall"
    exit 0
  fi

  # --force means "make sure nothing from this directory is running", and says so rather than failing when there was nothing to stop. No systemd, no unit and not running are all fine outcomes.
  STOPPED=no
  if command -v systemctl >/dev/null 2>&1 && [ -e "$UNIT" ] && systemctl is-active --quiet baseport 2>/dev/null; then
    need_root stop --force
    systemctl stop baseport && STOPPED=yes
  fi

  # A foreground instance holds the binary open just as firmly as the service does, and no unit file knows about it. Matched on this directory so a second install is left alone.
  if command -v pkill >/dev/null 2>&1; then
    if pkill -f "$DIR/Baseport" 2>/dev/null; then
      STOPPED=yes
      for _ in 1 2 3 4 5 6 7 8 9 10; do
        pgrep -f "$DIR/Baseport" >/dev/null 2>&1 || break
        sleep 1
      done
      pkill -KILL -f "$DIR/Baseport" 2>/dev/null || true
    fi
  fi

  if [ "$STOPPED" = "yes" ]; then echo "Stopped Baseport in $DIR."; else echo "Nothing to stop, Baseport in $DIR is not running."; fi
  ;;
restart)
  need_service
  need_root restart
  systemctl restart baseport
  echo "baseport.service restarted."
  ;;
status)
  if command -v systemctl >/dev/null 2>&1 && [ -e "$UNIT" ]; then
    exec systemctl status baseport --no-pager -n 10
  fi
  PID=""
  if command -v pgrep >/dev/null 2>&1; then PID=$(pgrep -f "$DIR/Baseport" | head -1 || true); fi
  if [ -n "$PID" ]; then
    echo "Baseport is running in the foreground as pid $PID, on $(service_url)."
  else
    echo "Baseport is not running. Start it with: baseport"
    echo "Or install it as a service: sudo $SELF service"
  fi
  ;;
doctor)
  RC=0
  ok() { echo "ok    $*"; }
  warn() { echo "warn  $*"; }
  bad() { echo "FAIL  $*"; RC=1; }

  if VERSION=$("$DIR/Baseport" version 2>/dev/null); then
    ok "Baseport $VERSION in $DIR"
  else
    bad "$DIR/Baseport is missing or will not run. Reinstall: curl -sSL $INSTALLER | bash"
  fi

  FOUND=$(command -v baseport 2>/dev/null || true)
  if [ -z "$FOUND" ]; then
    warn "$BIN is not on your PATH. Add it: echo 'export PATH=\"$BIN:\$PATH\"' >> ~/.bashrc"
  elif [ "$FOUND" != "$SELF" ]; then
    warn "PATH finds $FOUND before $SELF, so you are running another install."
  else
    ok "the baseport command is $SELF"
  fi

  if [ ! -d "$DIR" ]; then
    bad "$DIR is gone. Reinstall: curl -sSL $INSTALLER | bash"
  elif [ -w "$DIR" ]; then
    ok "$DIR is writable by $(id -un)"
  else
    warn "$DIR is not writable by $(id -un), which is normal when the service owns it."
  fi

  if [ -f "$DIR/baseport.db" ]; then
    ok "database $DIR/baseport.db, $(du -h "$DIR/baseport.db" | cut -f1)"
  else
    warn "no database yet, the first start creates one and prints a one-time admin login."
  fi

  if [ -e "$UNIT" ]; then
    if systemctl is-active --quiet baseport 2>/dev/null; then
      ok "baseport.service is active"
    else
      bad "baseport.service exists but is not active. Look at: journalctl -u baseport -n 50"
    fi
    RUNDIR=$(systemctl show baseport.service -p WorkingDirectory --value 2>/dev/null || true)
    if [ -n "$RUNDIR" ] && [ "$RUNDIR" != "$DIR" ]; then
      warn "baseport.service runs from $RUNDIR, not $DIR, so \"baseport update\" updates a copy nothing runs."
    fi
  else
    warn "no baseport.service, Baseport only runs while your terminal does. Install one: sudo $SELF service"
  fi

  URL=$(service_url)
  if curl -fsS -o /dev/null --max-time 3 "$URL" 2>/dev/null; then
    ok "answering on $URL, console at $URL/_/admin"
  else
    warn "nothing answered on $URL."
  fi

  if [ -d "$DIR" ]; then
    echo "disk  $(df -h "$DIR" 2>/dev/null | tail -1 | awk '{print $4" free on "$6}')"
  fi
  exit $RC
  ;;
uninstall)
  shift
  PURGE=no
  if [ "${1:-}" = "--purge" ]; then PURGE=yes; fi

  if [ -e "$UNIT" ] || [ ! -w "$DIR" ] || [ ! -w "$BIN" ]; then
    if [ "$PURGE" = "yes" ]; then need_root uninstall --purge; else need_root uninstall; fi
  fi

  if [ "$PURGE" = "yes" ] && [ -t 0 ]; then
    printf "Delete %s with its database, uploads and backups? This cannot be undone. [y/N] " "$DIR"
    read -r ANSWER
    case "$ANSWER" in
      y|Y|yes|YES) ;;
      *) die "Cancelled, nothing was removed." ;;
    esac
  fi

  if [ -e "$UNIT" ]; then
    systemctl disable --now baseport >/dev/null 2>&1 || true
    rm -f "$UNIT"
    systemctl daemon-reload >/dev/null 2>&1 || true
    echo "Removed baseport.service."
  fi

  if [ "$PURGE" = "yes" ]; then
    rm -rf "$DIR"
    if id baseport >/dev/null 2>&1; then userdel baseport >/dev/null 2>&1 || true; fi
    echo "Removed $DIR and everything in it."
  else
    for F in "$DIR"/* "$DIR"/.[!.]*; do
      [ -e "$F" ] || continue
      case "${F##*/}" in
        baseport.db|baseport.db-shm|baseport.db-wal|baseport.key|uploads|backups|log|appsettings.json) continue ;;
      esac
      rm -rf "$F"
    done
    echo "Removed the Baseport program files from $DIR."
    echo "Your data stayed: baseport.db, baseport.key, appsettings.json, uploads, backups, log."
    echo "Delete that as well with: rm -rf $DIR"
  fi

  rm -f "$SELF"
  echo "Removed the baseport command at $SELF."
  ;;
logs)
  cd "$DIR"
  LINES=${2:-200}
  case "$LINES" in
    ''|*[!0-9]*) die "The line count has to be a number: $SELF logs 50" ;;
  esac

  # The seeded login is written once, at the very first start, and is the one line somebody comes looking for. Tailing the last 200 lines scrolls past it on any instance that has been up for a while, and -f then blocks so nothing prints at all. Search the whole history for it first, then follow.
  if ls log/baseport-*.log >/dev/null 2>&1; then
    SEEDED=$(grep -h "Seeded a one-time admin account" log/baseport-*.log 2>/dev/null | tail -1 || true)
  elif [ -e "$UNIT" ]; then
    SEEDED=$(journalctl -u baseport --no-pager 2>/dev/null | grep "Seeded a one-time admin account" | tail -1 || true)
  else
    SEEDED=""
  fi

  if [ -n "$SEEDED" ]; then
    echo "First-start login, from $(if ls log/baseport-*.log >/dev/null 2>&1; then echo "$DIR/log"; else echo "the journal"; fi):"
    echo "  $SEEDED"
    echo "This is a one-time password. You are asked to change it at first sign-in, so if it no longer works somebody has already used it."
    echo
  fi

  if ls log/baseport-*.log >/dev/null 2>&1; then
    exec tail -n "$LINES" -f log/baseport-*.log
  elif [ -e "$UNIT" ]; then
    exec journalctl -u baseport -n "$LINES" -f
  else
    die "No log files in $DIR/log yet."
  fi
  ;;
help|-h|--help)
  exec "$DIR/Baseport" help
  ;;
*)
  # Started by its absolute path, not ./Baseport: "stop --force", "status" and doctor all find a foreground instance by matching this command line, and a relative argv[0] matches none of them.
  if command -v systemctl >/dev/null 2>&1 && [ -e "$UNIT" ] && systemctl is-active --quiet baseport 2>/dev/null; then
    echo "baseport.service is already running from $DIR." >&2
    echo "Two processes on one SQLite file is not what you want. Use one of:" >&2
    echo "  sudo $SELF stop     then run it in the foreground" >&2
    echo "  $SELF status        see where the running one is listening" >&2
    exit 1
  fi
  cd "$DIR"
  exec "$DIR/Baseport" "$@"
  ;;
esac
SHIM
chmod +x "$BIN/baseport"

# A wrapper from an earlier install keeps its own directory and can shadow this one on PATH, drop the ones whose directory is gone.
for OTHER in /usr/local/bin/baseport "$HOME/.local/bin/baseport"; do
  if [ "$OTHER" = "$BIN/baseport" ] || [ ! -f "$OTHER" ]; then continue; fi
  if [ "$(head -1 "$OTHER")" != "#!/bin/sh" ]; then continue; fi
  # This wrapper keeps its directory in a DIR= line; the ones before it inlined the path into every exec.
  OTHERDIR=$(sed -n 's/^DIR="\(.*\)"$/\1/p' "$OTHER" | head -1)
  if [ -z "$OTHERDIR" ]; then
    OTHERDIR=$(grep -o '"[^"]*/Baseport"' "$OTHER" | head -1 | tr -d '"' || true)
    OTHERDIR=${OTHERDIR%/Baseport}
  fi
  if [ -z "$OTHERDIR" ] || [ -x "$OTHERDIR/Baseport" ]; then continue; fi
  rm -f "$OTHER"
  echo "Removed the wrapper at $OTHER, which pointed at the removed $OTHERDIR."
done

echo
if [ "$UPDATE" = "yes" ]; then
  echo "Baseport updated to $TAG in $DIR."
else
  echo "Baseport $TAG installed in $DIR."
fi

# A home directory is 0700 on most distros, a service account could not read the install there.
case "$DIR" in
  /root|/root/*|/home/*) SERVICE_OK=no ;;
  *) SERVICE_OK=yes ;;
esac

# An update leaves the service on the old binary until it restarts, do it here instead of telling the user to.
if [ "$(id -u)" = "0" ] && [ -e /etc/systemd/system/baseport.service ]; then
  RUNDIR=$(systemctl show baseport.service -p WorkingDirectory --value 2>/dev/null || true)
  if [ "$RUNDIR" = "$DIR" ]; then
    systemctl restart baseport && echo "baseport.service restarted on the new binary."
  else
    echo "Note: baseport.service runs from $RUNDIR, which was not updated."
    echo "  BASEPORT_DIR=\"$RUNDIR\" curl -sSL $INSTALLER | sudo bash"
  fi
fi

echo
echo "  baseport                             start on http://localhost:5000"
echo "  baseport --urls http://0.0.0.0:5000  start on every interface"
if [ "$SERVICE_OK" = "yes" ]; then
  echo "  sudo baseport service [--urls URL]   run it as a systemd service"
  echo "  sudo baseport start|stop|restart     control that service"
fi
echo "  baseport stop --force                stop it however it is running"
echo "  baseport status                      is it running, and where"
echo "  baseport logs                        follow the logs, and show the first-start login"
echo "  baseport doctor                      check this install"
echo "  baseport update                      pull the latest release"
echo "  baseport uninstall [--purge]         remove it, --purge deletes the data too"
echo "  baseport help                        everything else"
echo
echo "Console http://localhost:5000/_/admin, first start prints a one-time admin login."

case ":$PATH:" in
  *":$BIN:"*) ;;
  *)
    echo
    echo "$BIN is not on your PATH. Add it:"
    echo "  echo 'export PATH=\"$BIN:\$PATH\"' >> ~/.bashrc && . ~/.bashrc"
    ;;
esac
