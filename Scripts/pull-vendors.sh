#!/usr/bin/env bash
# Refreshes the third-party assets served from wwwroot.
#
# Vendors third-party scripts locally so internet-facing pages stay independent.
# To update: bump a version below, run, review diff, and commit (SRI hashes verified).
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VENDOR_DIR="$ROOT/Source/Baseport/wwwroot/js/vendor"
mkdir -p "$VENDOR_DIR"

SCALAR_VERSION="1.64.0"
PREACT_VERSION="10.29.8"
HTM_VERSION="3.1.1"

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

vendor() {
    local name="$1" version="$2" path="$3" out="$4" tarball integrity
    read -r tarball integrity < <(curl -fsSL "https://registry.npmjs.org/$name/$version" \
        | python3 -c "import sys,json; d=json.load(sys.stdin)['dist']; print(d['tarball'], d.get('integrity', ''))")
    curl -fsSL "$tarball" -o "$TMP/package.tgz"
    python3 - "$TMP/package.tgz" "$integrity" <<'PY'
import base64, hashlib, sys
algorithm, _, expected = sys.argv[2].partition("-")
if algorithm != "sha512":
    sys.exit(f"no sha512 integrity published, refusing: {sys.argv[2]!r}")
actual = base64.b64encode(hashlib.sha512(open(sys.argv[1], "rb").read()).digest()).decode()
if actual != expected:
    sys.exit("tarball does not match the registry integrity, refusing")
PY
    tar -xzf "$TMP/package.tgz" -C "$TMP" "package/$path"
    mv "$TMP/package/$path" "$VENDOR_DIR/$out"
    rm -rf "$TMP/package" "$TMP/package.tgz"
    echo "   $name@$version verified, saved wwwroot/js/vendor/$out"
}

echo "[Scalar API Reference]"
vendor "@scalar/api-reference" "$SCALAR_VERSION" "dist/browser/standalone.js" "scalar-api-reference.js"

echo "[Preact + htm]"
vendor preact "$PREACT_VERSION" "dist/preact.min.js" "preact.min.js"
vendor htm "$HTM_VERSION" "dist/htm.js" "htm.js"

echo "[Onest font]"
FONTS_DIR="$ROOT/Source/Baseport/wwwroot/fonts"
mkdir -p "$FONTS_DIR"
CSS="$(curl -fsSL -H 'User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36' \
    'https://fonts.googleapis.com/css2?family=Onest:wght@400;500;600&display=swap')"
LATIN_URL="$(printf '%s\n' "$CSS" | grep -B1 'U+0000-00FF' | grep -o 'https://fonts.gstatic.com/[^)]*woff2' | head -1)"
LATIN_EXT_URL="$(printf '%s\n' "$CSS" | grep -B1 'U+0100-02BA' | grep -o 'https://fonts.gstatic.com/[^)]*woff2' | head -1)"
curl -fsSL "$LATIN_URL" -o "$FONTS_DIR/onest-latin.woff2"
curl -fsSL "$LATIN_EXT_URL" -o "$FONTS_DIR/onest-latin-ext.woff2"
echo "   saved:   wwwroot/fonts/onest-latin.woff2, onest-latin-ext.woff2"
