#!/bin/sh
# Proving a backup can be restored, which is the only thing that makes it a backup (PRD 8 asks
# for this once a month, with the result written down).
#
#   ./restore-check.sh                    # the newest file in the backup directory
#   ./restore-check.sh backups/courtpaka-20260923T020000Z.dump
#
# It restores into a scratch database beside the real one, compares what came back with what is
# live, and drops it. The live database is only ever read from: the restore names its own target,
# and the script refuses to run if that name is the live one.
set -eu

umask 077

here=$(cd "$(dirname "$0")" && pwd)
cd "$here"

BACKUP_DIR=${BACKUP_DIR:-"$here/backups"}
COMPOSE=${COMPOSE:-"docker compose"}

if [ -f "$here/.env" ]; then
    # shellcheck disable=SC1091
    . "$here/.env"
fi
POSTGRES_DB=${POSTGRES_DB:?set POSTGRES_DB, or put it in the .env beside this script}
POSTGRES_USER=${POSTGRES_USER:?set POSTGRES_USER, or put it in the .env beside this script}

dump=${1:-$(ls -1t "$BACKUP_DIR"/courtpaka-*.dump 2>/dev/null | head -1 || true)}
if [ -z "${dump:-}" ] || [ ! -f "$dump" ]; then
    echo "restore-check.sh: no backup to check (looked in $BACKUP_DIR)" >&2
    exit 1
fi

scratch="restore_check_$(date -u +%Y%m%d%H%M%S)"
if [ "$scratch" = "$POSTGRES_DB" ]; then
    echo "restore-check.sh: refusing to restore over the live database" >&2
    exit 1
fi

psql() {
    $COMPOSE exec -T db psql -U "$POSTGRES_USER" -v ON_ERROR_STOP=1 "$@"
}

restore_log=$(mktemp)

# Dropped however this ends, including a Ctrl-C part way through: what is left behind otherwise
# is a second complete copy of every booking and every address, sitting in the live instance on
# the live disk with nothing naming it (PDPA, PRD 8).
cleanup() {
    psql -d "$POSTGRES_DB" -q -c "DROP DATABASE IF EXISTS $scratch;" >/dev/null 2>&1 || true
    rm -f "$restore_log"
}
trap cleanup EXIT INT TERM HUP

# What the archive says it holds. A dump taken --schema-only by mistake, or one written by a
# command that never reached the data, lists no table data at all — and would otherwise restore
# perfectly and prove nothing.
data_entries=$($COMPOSE exec -T db pg_restore -l < "$dump" 2>/dev/null | grep -c 'TABLE DATA' || true)
if [ "${data_entries:-0}" -lt 1 ]; then
    echo "restore-check.sh: $dump holds no table data (a schema is not a backup)" >&2
    exit 1
fi

echo "Restoring $dump into $scratch ($data_entries tables with data in the archive)"
psql -d "$POSTGRES_DB" -q -c "CREATE DATABASE $scratch;"

# pg_restore's own verdict counts: half a file restores what it reached and then stops, and that
# must not read as a restore. Its exit code is the verdict; the log is shown when it refuses.
restored=0
$COMPOSE exec -T db pg_restore -U "$POSTGRES_USER" -d "$scratch" --no-owner --no-privileges \
    < "$dump" 2> "$restore_log" || restored=$?
if [ "$restored" -ne 0 ]; then
    echo "restore-check.sh: pg_restore refused this file (exit $restored):" >&2
    sed 's/^/  /' "$restore_log" >&2
    exit 1
fi

rows() {
    psql -d "$2" -t -A -c "SELECT count(*) FROM \"$1\";" 2>/dev/null | tr -d '\r'
}

# What came back, beside what is live now. A backup is older than the database it came from, so
# the restore may hold a little less — but not a lot less.
failed=0
live_total=0
for table in AspNetUsers Venues Courts Bookings BookingSlots BookingStatusChanges; do
    came_back=$(rows "$table" "$scratch")
    live=$(rows "$table" "$POSTGRES_DB")
    if [ -z "$came_back" ] || [ -z "$live" ]; then
        echo "  $table: MISSING"
        failed=1
        continue
    fi

    echo "  $table: $came_back restored, $live live"
    live_total=$((live_total + live))
    if [ "$live" -gt 0 ] && [ "$((came_back * 10))" -lt "$((live * 9))" ]; then
        echo "    ^ less than nine tenths of what is live: this file is not a whole backup"
        failed=1
    fi
done

tables=$(psql -d "$scratch" -t -A -c \
    "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public';" | tr -d '\r')
echo "  (tables in the restore: $tables)"

if [ "$failed" -ne 0 ] || [ "${tables:-0}" -lt 10 ]; then
    echo "restore-check.sh: the restore is not a badPaka database" >&2
    exit 1
fi

if [ "$live_total" -eq 0 ]; then
    # Said plainly rather than passed off as more: with nothing live to compare against, the
    # rows cannot be vouched for. On a new machine that is the honest answer, and the check is
    # worth running again once the first venues have used it.
    echo "OK, as far as it goes: $dump is a whole archive and restores, but the live database is"
    echo "empty, so nothing here says the rows are right. Run it again once there is data."
    exit 0
fi

echo "OK: $dump restores, with the rows to match. Write down the date and this output (PRD 8)."
