#!/usr/bin/env bash
# Refreshes the third-party assets served from wwwroot.
#
# They are vendored, not linked: this instance is meant to be exposed to the
# internet, and a CDN script tag would put a third party in the request path of
# a page we publish. Run this deliberately, read the diff, commit the result.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VENDOR_DIR="$ROOT/Source/Baseport/wwwroot/js/vendor"
mkdir -p "$VENDOR_DIR"

latest_npm_version() {
    curl -fsSL "https://registry.npmjs.org/$1/latest" \
        | python3 -c "import sys,json; print(json.load(sys.stdin)['version'])"
}

echo "[Scalar API Reference]"
VERSION="$(latest_npm_version "@scalar/api-reference")"
echo "   version: $VERSION"
curl -fsSL "https://cdn.jsdelivr.net/npm/@scalar/api-reference@${VERSION}" \
    -o "$VENDOR_DIR/scalar-api-reference.js"
echo "   saved:   wwwroot/js/vendor/scalar-api-reference.js"

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
