# Load test (PRD 8)

The target: 200 people looking at the court grid while 20 bookings a minute are made, for ten
minutes. Fewer than 1% of requests may fail, and the API's p95 must stay within 800 ms. The grid
has its own target, p95 within 500 ms. PRD 8 says to run it **on the PRD machine, before venues
start using it (end of M3)**.

## Accounts

Booking needs verified accounts. At most ten are used, because sign-in is limited to ten a minute
from one address.

- Local stack: `python scripts/load/provision_accounts.py 10 > scripts/load/accounts.json`. This
  follows the verification link the Development sender logs.
- A deployed stack: make ten accounts by hand and write them to `accounts.json` as
  `[{"email": "...", "password": "..."}]`. The file is git-ignored.

## Run

```bash
docker run --rm -i --network host -v "$PWD/scripts/load:/load" grafana/k6 run \
  -e BASE=https://<host> -e VENUE_ID=<an approved venue> -e ACCOUNTS=/load/accounts.json \
  /load/grid_and_book.js
```

On Docker Desktop (Windows/macOS) use `-e BASE=http://host.docker.internal:8080` instead of
`--network host`.

For a quick smoke run, add `-e BROWSERS=20 -e BOOKINGS_PER_MINUTE=5 -e DURATION=1m`.

Each booking is given back straight away, so the venue does not fill up over ten minutes. What is
measured is the write path, not how full the venue gets. A 409 (the hour was just taken) counts
as the system working.

## Results so far

| When | Where | Load | Failed | API p95 | Grid p95 |
|---|---|---|---|---|---|
| 2026-09-22 | Local Docker Desktop (dev laptop) | 200 VUs + 20 bookings/min, 3 min | 0 / 18,208 | 14 ms | 14 ms |

This is not the PRD result. It shows the script and the stack hold up. It says nothing about the
VPS.
