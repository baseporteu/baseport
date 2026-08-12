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
