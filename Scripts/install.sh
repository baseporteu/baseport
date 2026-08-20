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

echo "Installing into $DIR (override with BASEPORT_DIR)"
echo "Fetching $ASSET"
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
if [ "\$1" = "help" ] || [ "\$1" = "-h" ] || [ "\$1" = "--help" ]; then
  exec "$DIR/Baseport" help
fi

if [ "\$1" = "update" ]; then
  curl -fsSL "$INSTALLER" | BASEPORT_REPO="$REPO" BASEPORT_DIR="$DIR" BASEPORT_BIN="$BIN" bash
  exit \$?
fi

if [ "\$1" = "-d" ]; then
  if [ "\$(id -u)" != "0" ]; then
    echo "Controlling the service needs root. Try:" >&2
    echo "  sudo \$0 -d" >&2
    exit 1
  fi
  [ -e /etc/systemd/system/baseport.service ] || { echo "No baseport.service yet. Create it with:" >&2; echo "  sudo \$0 -i" >&2; exit 1; }
  systemctl restart baseport
  echo "baseport.service restarted."
  echo "  systemctl status baseport"
  exit 0
fi

if [ "\$1" = "-i" ]; then
  if [ "\$(id -u)" != "0" ]; then
    echo "Creating a service needs root. Try:" >&2
    echo "  sudo \$0 -i" >&2
    exit 1
  fi
  command -v systemctl >/dev/null 2>&1 || { echo "No systemd on this machine." >&2; exit 1; }
  if [ -e /etc/systemd/system/baseport.service ]; then
    echo "baseport.service already exists. Restart it with 'baseport -d', or remove it and run this again:" >&2
    echo "  systemctl disable --now baseport && rm /etc/systemd/system/baseport.service" >&2
    exit 1
  fi

  NOLOGIN=/usr/sbin/nologin
  [ -x "\$NOLOGIN" ] || NOLOGIN=/sbin/nologin
  id baseport >/dev/null 2>&1 || useradd --system --home "$DIR" --shell "\$NOLOGIN" baseport
  chown -R baseport:baseport "$DIR"

  if ! su -s /bin/sh baseport -c "test -r '$DIR/Baseport'"; then
    echo "The baseport user cannot read $DIR, so a service there would not start." >&2
    echo "Reinstall as root, which lands in /opt/baseport, and try again:" >&2
    echo "  curl -sSL $INSTALLER | sudo bash" >&2
    exit 1
  fi

  shift
  ARGS="\$*"
  [ -n "\$ARGS" ] || ARGS="--urls http://localhost:5263"

  cat > /etc/systemd/system/baseport.service <<UNIT
[Unit]
Description=Baseport
After=network.target

[Service]
User=baseport
WorkingDirectory=$DIR
ExecStart=$DIR/Baseport \$ARGS
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
UNIT

  systemctl daemon-reload
  systemctl enable --now baseport >/dev/null 2>&1
  echo "baseport.service created and started."
  echo "  ExecStart=$DIR/Baseport \$ARGS"
  echo
  echo "  systemctl status baseport"
  echo "  journalctl -u baseport -f"
  exit 0
fi

cd "$DIR" || exit 1
if [ "\$1" = "logs" ]; then
  [ -n "\$(ls log/baseport-*.log 2>/dev/null)" ] || { echo "No log files in $DIR/log yet." >&2; exit 1; }
  exec tail -n "\${2:-200}" -f log/baseport-*.log
fi
exec ./Baseport "\$@"
EOF
chmod +x "$BIN/baseport"

echo
if [ "$UPDATE" = "yes" ]; then
  echo "Baseport updated to $TAG in $DIR."
  echo "Kept: baseport.db, baseport.key, log/, uploads/, backups/, appsettings.json."
else
  echo "Baseport $TAG installed in $DIR."
fi
# A home directory is 0700 on most distros, so a service account could not read the install there.
case "$DIR" in
  /root|/root/*|/home/*) SERVICE_OK=no ;;
  *) SERVICE_OK=yes ;;
esac

echo
echo "  baseport --urls http://localhost:5263    start, loopback only"
echo "  baseport --urls http://0.0.0.0:5263      start, every interface"
if [ "$SERVICE_OK" = "yes" ]; then
  echo "  baseport -i                              install it as a systemd service"
  echo "  baseport -d                              restart that service"
fi
echo "  baseport logs                            follow the log files"
echo "  baseport help                            everything else"
echo
echo "Console: http://localhost:5263/_/admin"
echo "First start prints a one-time admin username and password."

if [ -e "$PWD/Baseport" ] && [ "$PWD" != "$DIR" ]; then
  echo
  echo "Warning: another Baseport sits in $PWD. It was not updated."
  echo "If that is the one you run:"
  echo "  BASEPORT_DIR=\"$PWD\" curl -sSL $INSTALLER | bash"
fi

if command -v systemctl >/dev/null 2>&1 && systemctl list-unit-files baseport.service >/dev/null 2>&1 \
   && systemctl cat baseport.service >/dev/null 2>&1; then
  RUNDIR=$(systemctl show baseport.service -p WorkingDirectory --value 2>/dev/null)
  echo
  if [ -n "$RUNDIR" ] && [ "$RUNDIR" != "$DIR" ]; then
    echo "Warning: the service runs from $RUNDIR. That copy was not updated."
    echo "  BASEPORT_DIR=\"$RUNDIR\" curl -sSL $INSTALLER | bash"
  else
    echo "Restart the service:"
    echo "  baseport -d"
  fi
elif [ "$SERVICE_OK" = "yes" ]; then
  echo
  echo "Run it as a service with:  baseport -i"
fi

case ":$PATH:" in
  *":$BIN:"*) ;;
  *)
    echo
    echo "$BIN is not on your PATH. Add it:"
    echo "  echo 'export PATH=\"$BIN:\$PATH\"' >> ~/.bashrc && . ~/.bashrc"
    ;;
esac
