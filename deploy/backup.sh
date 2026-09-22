#!/bin/sh
# A copy of the database, every day, kept for fourteen (PRD 8, Backup).
#
#   ./backup.sh                 # write one, and throw away the ones older than the window
#   BACKUP_DIR=/srv/backups ./backup.sh
#
# It dumps through the running db container, so there is nothing to install on the host and the
# credentials stay where they already are (the compose file's .env). The dump is a custom-format
# archive: pg_restore can read one table out of it, which a plain SQL file cannot.
#
# It does NOT copy the payment slips. They live in the api-slips volume and are not in the
# database (US-04, PRD 9.1); restoring this without them gives bookings whose evidence is gone.
# Until they move to object storage, the volume has to be in the same backup set — see README.
set -eu

# A dump is every booker's address and phone, every venue's tax id and bank account. Nobody else
# on the machine gets to read it (PDPA, PRD 8) — the same care .env is handled with.
umask 077

here=$(cd "$(dirname "$0")" && pwd)
cd "$here"

BACKUP_DIR=${BACKUP_DIR:-"$here/backups"}
KEEP_DAYS=${KEEP_DAYS:-14}
COMPOSE=${COMPOSE:-"docker compose"}

# The database's own name and user, from the same file the stack reads.
if [ -f "$here/.env" ]; then
    # shellcheck disable=SC1091
    . "$here/.env"
fi
POSTGRES_DB=${POSTGRES_DB:?set POSTGRES_DB, or put it in the .env beside this script}
POSTGRES_USER=${POSTGRES_USER:?set POSTGRES_USER, or put it in the .env beside this script}

mkdir -p "$BACKUP_DIR"
chmod 700 "$BACKUP_DIR"
stamp=$(date -u +%Y%m%dT%H%M%SZ)
file="$BACKUP_DIR/courtpaka-$stamp.dump"

# Written under a name nothing looks for, and only given the real one once it has been checked:
# restore-check.sh takes the newest .dump, and half a file must never be that.
$COMPOSE exec -T db pg_dump -U "$POSTGRES_USER" -d "$POSTGRES_DB" --format=custom > "$file.part"

size=$(wc -c < "$file.part")
if [ "$size" -lt 1024 ]; then
    rm -f "$file.part"
    echo "backup.sh: the dump was only $size bytes — that is not a database" >&2
    exit 1
fi

mv "$file.part" "$file"

# Older than the window, and only this stack's own files. Abandoned halves go too, or a failed
# night would leave its .part behind forever.
find "$BACKUP_DIR" -name 'courtpaka-*.dump' -mtime "+$KEEP_DAYS" -delete
find "$BACKUP_DIR" -name 'courtpaka-*.dump.part' -mtime +1 -delete

echo "$file ($size bytes)"
