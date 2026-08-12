#!/bin/bash
# Seeds a running Baseport with a demo workspace. Needs the instance's database
# file: schema goes through the admin API, the rows go straight into SQLite.
#
# Overrides work as environment or as NAME=value arguments:
#
#   ADMIN_PASSWORD=<one-time password from the startup log> ./POPULATE.sh
#   ./POPULATE.sh ADMIN_PASSWORD='...' BASE_URL=http://localhost:8080 SCALE=0.02
#
# ADMIN_PASSWORD is the one-time password logged on first start; it gets
# replaced with ADMIN_NEW_PASSWORD. PORTWAY_TOKEN stays out of this repo.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"

REST=()
for arg in "$@"; do
    case "$arg" in
        [A-Za-z_]*=*) export "$arg" ;;
        --*) REST+=("$arg") ;;
        *) echo "Unknown argument: $arg. Overrides are NAME=value, see the header of this script." >&2; exit 2 ;;
    esac
done

BASE_URL="${BASE_URL:-http://localhost:5263}"
ADMIN_USER="${ADMIN_USER:-admin}"
ADMIN_PASSWORD="${ADMIN_PASSWORD:-}"
ADMIN_NEW_PASSWORD="${ADMIN_NEW_PASSWORD:-baseport-dev-password}"
PORTWAY_SPEC="${PORTWAY_SPEC:-https://portway-demo.melosso.com/docs/openapi/v1/openapi.json}"
PORTWAY_TOKEN="${PORTWAY_TOKEN:-}"
DB_PATH="${DB_PATH:-$HERE/Source/Baseport/baseport.db}"
SCALE="${SCALE:-1}"

command -v python3 >/dev/null || { echo "python3 is required." >&2; exit 1; }

BASE_URL="$BASE_URL" ADMIN_USER="$ADMIN_USER" ADMIN_PASSWORD="$ADMIN_PASSWORD" \
ADMIN_NEW_PASSWORD="$ADMIN_NEW_PASSWORD" \
PORTWAY_SPEC="$PORTWAY_SPEC" PORTWAY_TOKEN="$PORTWAY_TOKEN" \
python3 "$HERE/Scripts/populate.py" --db "$DB_PATH" --scale "$SCALE" ${REST[@]+"${REST[@]}"}
