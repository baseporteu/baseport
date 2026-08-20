#!/usr/bin/env bash
set -euo pipefail

REPO="${BASEPORT_REPO:-baseporteu/baseport}"
DIR="${BASEPORT_DIR:-$HOME/.baseport}"
BIN="${BASEPORT_BIN:-$HOME/.local/bin}"
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
if [ "\$1" = "update" ]; then
  curl -fsSL "$INSTALLER" | BASEPORT_REPO="$REPO" BASEPORT_DIR="$DIR" BASEPORT_BIN="$BIN" bash
  exit \$?
fi
cd "$DIR" || exit 1
exec ./Baseport "\$@"
EOF
chmod +x "$BIN/baseport"

echo
if [ "$UPDATE" = "yes" ]; then
  echo "Baseport updated to $TAG in $DIR."
  echo "Your baseport.db, baseport.key, log/, uploads/, backups/ and appsettings.json were left alone."
else
  echo "Baseport $TAG installed in $DIR."
fi
echo
echo "Start it:"
echo "  baseport --urls http://localhost:5263"
echo
echo "Other commands:"
echo "  baseport accounts list"
echo "  baseport providers status"
echo "  baseport update"
echo
echo "Console: http://localhost:5263/_/admin"
echo "The first start prints a one-time admin username and password."

case ":$PATH:" in
  *":$BIN:"*) ;;
  *)
    echo
    echo "Note: $BIN is not on your PATH, so \"baseport\" will not resolve yet."
    echo "Add it with:"
    echo "  echo 'export PATH=\"$BIN:\$PATH\"' >> ~/.bashrc && . ~/.bashrc"
    ;;
esac
