#!/usr/bin/env bash
set -euo pipefail

REPO="${BASEPORT_REPO:-baseporteu/baseport}"
# Root installs exist to be services, so they default where a service account can read: /root is 0700, /opt is not.
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
DIR="$DIR"
BIN="$BIN"
REPO="$REPO"
INSTALLER="$INSTALLER"
EOF
cat >> "$BIN/baseport" <<'SHIM'
set -eu
UNIT=/etc/systemd/system/baseport.service

die() { echo "$*" >&2; exit 1; }
need_root() { [ "$(id -u)" = "0" ] || die "That needs root. Try: sudo $0 $*"; }

[ -x "$DIR/Baseport" ] || die "No Baseport in $DIR. Reinstall it:
  curl -sSL $INSTALLER | bash"

case "${1:-}" in
update)
  curl -fsSL "$INSTALLER" | BASEPORT_REPO="$REPO" BASEPORT_DIR="$DIR" BASEPORT_BIN="$BIN" bash
  ;;
service)
  shift
  need_root service "$@"
  command -v systemctl >/dev/null 2>&1 || die "No systemd on this machine."
  ARGS="$*"
  [ -n "$ARGS" ] || ARGS="--urls http://localhost:5263"

  NOLOGIN=/usr/sbin/nologin
  [ -x "$NOLOGIN" ] || NOLOGIN=/sbin/nologin
  id baseport >/dev/null 2>&1 || useradd --system --home "$DIR" --shell "$NOLOGIN" baseport
  chown -R baseport:baseport "$DIR"
  su -s /bin/sh baseport -c "test -r '$DIR/Baseport'" || die "The baseport user cannot read $DIR, so the service would not start.
Reinstall as root, which lands in /opt/baseport, and try again:
  curl -sSL $INSTALLER | sudo bash"

  cat > "$UNIT" <<UNITFILE
[Unit]
Description=Baseport
After=network.target

[Service]
User=baseport
WorkingDirectory=$DIR
ExecStart=$DIR/Baseport $ARGS
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
UNITFILE

  systemctl daemon-reload
  systemctl enable baseport >/dev/null 2>&1 || true
  systemctl restart baseport
  echo "baseport.service running $DIR/Baseport $ARGS"
  ;;
restart)
  need_root restart
  [ -e "$UNIT" ] || die "No baseport.service yet. Create it: sudo $0 service"
  systemctl restart baseport
  echo "baseport.service restarted."
  ;;
logs)
  cd "$DIR"
  ls log/baseport-*.log >/dev/null 2>&1 || die "No log files in $DIR/log yet."
  exec tail -n "${2:-200}" -f log/baseport-*.log
  ;;
help|-h|--help)
  exec "$DIR/Baseport" help
  ;;
*)
  cd "$DIR"
  exec ./Baseport "$@"
  ;;
esac
SHIM
chmod +x "$BIN/baseport"

echo
if [ "$UPDATE" = "yes" ]; then
  echo "Baseport updated to $TAG in $DIR."
else
  echo "Baseport $TAG installed in $DIR."
fi

# A home directory is 0700 on most distros, so a service account could not read the install there.
case "$DIR" in
  /root|/root/*|/home/*) SERVICE_OK=no ;;
  *) SERVICE_OK=yes ;;
esac

# An update leaves the service on the old binary until it restarts, so do it here rather than telling the user to.
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
echo "  baseport                             start on http://localhost:5263"
echo "  baseport --urls http://0.0.0.0:5263  start on every interface"
if [ "$SERVICE_OK" = "yes" ]; then
  echo "  sudo baseport service [--urls URL]   run it as a systemd service"
  echo "  sudo baseport restart                restart that service"
fi
echo "  baseport logs                        follow the log files"
echo "  baseport help                        everything else"
echo
echo "Console http://localhost:5263/_/admin, first start prints a one-time admin login."

case ":$PATH:" in
  *":$BIN:"*) ;;
  *)
    echo
    echo "$BIN is not on your PATH. Add it:"
    echo "  echo 'export PATH=\"$BIN:\$PATH\"' >> ~/.bashrc && . ~/.bashrc"
    ;;
esac
