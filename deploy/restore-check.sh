#!/bin/sh
# Proving a backup can be restored, which is the only thing that makes it a backup (PRD 8 asks
# for this once a month, with the result written down).
#
#   ./restore-check.sh                    # the newest file in the backup directory
#   ./restore-check.sh backups/courtpaka-20260923T020000Z.dump
#
# It restores into a scratch database beside the real one, compares what came back with what is
# live, and drops it. The live database is never written to: the restore names its own target,
# and the script refuses to run if that name is the live one.
set -eu

here=$(cd "$(dirname "$0")" && pwd)
cd "$here"

BACKUP_DIR=${BACKUP_DIR:-"$here/backups"}
COMPOSE=${COMPOSE:-"docker compose"}

if [ -f "$here/.env" ]; then
    # shellcheck disable=SC1091
    . "$here/.env"
fi
POSTGRES_DB=${POSTGRES_DB:?set POSTGRES_DB, or put it in deploy/.env}
POSTGRES_USER=${POSTGRES_USER:?set POSTGRES_USER, or put it in deploy/.env}

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

cleanup() {
    psql -d "$POSTGRES_DB" -q -c "DROP DATABASE IF EXISTS $scratch;" >/dev/null 2>&1 || true
}
trap cleanup EXIT

echo "Restoring $dump into $scratch"
psql -d "$POSTGRES_DB" -q -c "CREATE DATABASE $scratch;"

# pg_restore's own verdict counts: half a file restores the tables it reached and then stops, and
# that must not read as a restore. Ownership and privilege complaints are the exception — this
# database has none of the live one's roles — so they are the only ones let through.
restore_log=$(mktemp)
if ! $COMPOSE exec -T db pg_restore -U "$POSTGRES_USER" -d "$scratch" --no-owner --no-privileges \
    < "$dump" 2> "$restore_log"; then
    if grep -qv -e 'must be owner' -e 'permission denied' -e 'errors ignored on restore' "$restore_log"; then
        echo "restore-check.sh: pg_restore refused this file:" >&2
        sed 's/^/  /' "$restore_log" >&2
        rm -f "$restore_log"
        exit 1
    fi
fi
rm -f "$restore_log"

rows() {
    psql -d "$2" -t -A -c "SELECT count(*) FROM \"$1\";" 2>/dev/null | tr -d '\r'
}

# What came back, beside what is live now. A backup is older than the database it came from, so
# the restore may hold a little less — but not a lot less, and never a schema with nothing in it,
# which is what half a file restores to.
failed=0
for table in AspNetUsers Venues Courts Bookings BookingSlots BookingStatusChanges; do
    restored=$(rows "$table" "$scratch")
    live=$(rows "$table" "$POSTGRES_DB")
    if [ -z "$restored" ] || [ -z "$live" ]; then
        echo "  $table: MISSING"
        failed=1
        continue
    fi

    echo "  $table: $restored restored, $live live"
    if [ "$live" -gt 0 ] && [ "$((restored * 10))" -lt "$((live * 9))" ]; then
        echo "    ^ less than nine tenths of what is live: this file is not a whole backup"
        failed=1
    fi
done

tables=$(psql -d "$scratch" -t -A -c \
    "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public';" | tr -d '\r')
echo "  (tables in the restore: $tables)"

if [ "$failed" -ne 0 ] || [ "${tables:-0}" -lt 10 ]; then
    echo "restore-check.sh: the restore is not a CourtPaka database" >&2
    exit 1
fi

echo "OK: $dump restores. Write down the date and this output (PRD 8, Backup)."
