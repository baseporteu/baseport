#!/usr/bin/env bash
# Cuts an upgrade fixture from a released tag: builds that release, runs it on an
# empty directory, seeds it with that release's own POPULATE.sh, adds an API
# account, and copies the database into the test fixtures with what it must keep.
#
#   Scripts/make-fixture.sh v0.0.1-alpha.24
set -euo pipefail

TAG="${1:?Usage: Scripts/make-fixture.sh <tag>}"
HERE="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$HERE/Source/Baseport.Tests/Fixtures/upgrades"
PORT="${PORT:-5397}"
URL="http://127.0.0.1:$PORT"
WORK="$(mktemp -d)"
SERVER=""

cleanup() {
  [ -n "$SERVER" ] && kill "$SERVER" 2>/dev/null && wait "$SERVER" 2>/dev/null || true
  git -C "$HERE" worktree remove --force "$WORK/src" 2>/dev/null || true
  rm -rf "$WORK"
}
trap cleanup EXIT

git -C "$HERE" worktree add --detach "$WORK/src" "$TAG" >/dev/null
dotnet build "$WORK/src/Source/Baseport/Baseport.csproj" -c Release -o "$WORK/bin" -p:RestoreLockedMode=false >/dev/null

mkdir -p "$WORK/data"
(cd "$WORK/data" && Baseport__ConnectionString="Data Source=$WORK/data/baseport.db" \
  exec dotnet "$WORK/bin/Baseport.dll" --urls "$URL" >"$WORK/server.log" 2>&1) &
SERVER=$!
for _ in $(seq 60); do curl -fsS -o /dev/null "$URL/api/openapi.json" 2>/dev/null && break; sleep 1; done
curl -fsS -o /dev/null "$URL/api/openapi.json" || { tail -20 "$WORK/server.log" >&2; exit 1; }

BASE_URL="$URL" SCALE=0.001 DB_PATH="$WORK/data/baseport.db" PORTWAY_TOKEN="" "$WORK/src/POPULATE.sh" >/dev/null

python3 - "$URL" "$WORK/data/baseport.db" "$WORK/expected.json" <<'EOF'
import json, sqlite3, sys, urllib.error, urllib.request

url, db, out = sys.argv[1:]
cookie = ""

def call(method, path, body=None):
    global cookie
    request = urllib.request.Request(url + path, method=method, data=json.dumps(body).encode() if body is not None else None,
                                     headers={"Content-Type": "application/json", "Cookie": cookie})
    try:
        response = urllib.request.urlopen(request)
    except urllib.error.HTTPError as error:
        raise SystemExit(f"{method} {path} failed ({error.code}): {error.read().decode()[:300]}")
    with response:
        for value in response.headers.get_all("Set-Cookie") or []:
            if value.startswith("baseport_auth="):
                cookie = value.split(";")[0]
        raw = response.read()
        return json.loads(raw) if raw else None

conn = sqlite3.connect(db)
admin = conn.execute("SELECT Username FROM _users WHERE Role = 'admin'").fetchone()[0]
call("POST", "/api/auth/login", {"username": admin, "password": "baseport-dev-password"})
account = call("POST", "/api/_admin/accounts", {"username": "fixture-api", "role": "consumer"})
token = call("POST", f"/api/_admin/accounts/{account['id']}/token", {"expiresAt": f"{__import__('datetime').date.today().year + 9}-12-31"})["apiToken"]

tables = {name: tid for tid, name in conn.execute("SELECT Id, Name FROM _tables WHERE IsProxy = 0")}
json.dump({
    "admin": {"username": admin, "password": "baseport-dev-password"},
    "apiAccount": {"username": "fixture-api", "token": token},
    "counts": {t: conn.execute(f'SELECT count(*) FROM "{t}"').fetchone()[0] for t in ("_tables", "_fields", "_records", "_forms", "_users")},
    "records": {name: conn.execute("SELECT Id FROM _records WHERE TableId = ? ORDER BY Id LIMIT 1", (tid,)).fetchone()[0]
                for name, tid in sorted(tables.items())
                if conn.execute("SELECT 1 FROM _records WHERE TableId = ?", (tid,)).fetchone()},
}, open(out, "w"), indent=2)
EOF

kill "$SERVER"; wait "$SERVER" 2>/dev/null || true; SERVER=""
mkdir -p "$OUT"
rm -f "$OUT/$TAG.db"
python3 -c "import sqlite3, sys; sqlite3.connect(sys.argv[1]).execute(\"VACUUM INTO '\" + sys.argv[2] + \"'\")" "$WORK/data/baseport.db" "$OUT/$TAG.db"
cp "$WORK/expected.json" "$OUT/$TAG.expected.json"
echo "Wrote $OUT/$TAG.db ($(du -h "$OUT/$TAG.db" | cut -f1)) and $TAG.expected.json"
