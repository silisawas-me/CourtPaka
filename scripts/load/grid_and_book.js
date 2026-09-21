// PRD 8 load test: 200 people looking at the court grid while 20 bookings a minute are made, for
// ten minutes; fewer than 1% of requests may fail and the API's p95 must stay within 800 ms.
//
//   docker run --rm -i --network host -v "$PWD/scripts/load:/load" grafana/k6 run \
//     -e BASE=http://localhost:8080 -e VENUE_ID=<id> -e ACCOUNTS=/load/accounts.json \
//     /load/grid_and_book.js
//
// A smoke run, to check the script and the stack before the real one:
//   ... -e BROWSERS=20 -e BOOKINGS_PER_MINUTE=5 -e DURATION=1m ...
//
// ACCOUNTS is a JSON array of {"email", "password"} for verified accounts (booking needs a
// verified address, PRD US-01). provision_accounts.py makes them on the local stack; on a
// deployed stack they have to be made by hand beforehand. At most ten are used: sign-in is limited
// to ten a minute from one address (App:AuthRequestsPerMinute), so each booking VU signs in once
// and keeps its session.

import http from 'k6/http';
import { check, sleep } from 'k6';
import { SharedArray } from 'k6/data';

const BASE = __ENV.BASE || 'http://localhost:8080';
const VENUE_ID = __ENV.VENUE_ID;
const BROWSERS = Number(__ENV.BROWSERS || 200);
const BOOKINGS_PER_MINUTE = Number(__ENV.BOOKINGS_PER_MINUTE || 20);
const DURATION = __ENV.DURATION || '10m';
const BOOKERS = 10;

const accounts = new SharedArray('accounts', () => JSON.parse(open(__ENV.ACCOUNTS)));

// A hour already taken by somebody else is the system working, not failing (BR-04).
http.setResponseCallback(http.expectedStatuses(200, 201, 204, 409));

export const options = {
  scenarios: {
    browse: {
      executor: 'constant-vus',
      vus: BROWSERS,
      duration: DURATION,
      exec: 'browse',
    },
    book: {
      executor: 'constant-arrival-rate',
      rate: BOOKINGS_PER_MINUTE,
      timeUnit: '1m',
      duration: DURATION,
      preAllocatedVUs: Math.min(BOOKERS, accounts.length),
      maxVUs: Math.min(BOOKERS, accounts.length),
      exec: 'book',
    },
  },
  thresholds: {
    http_req_failed: ['rate<0.01'],
    // The API only: the static files are Caddy's, and the target is about the API (PRD 8).
    'http_req_duration{kind:api}': ['p(95)<800'],
    'http_req_duration{name:grid}': ['p(95)<500'],
  },
};

/** A date in Bangkok, `days` from today, as the API names days. */
function bangkokDate(days) {
  const now = new Date(Date.now() + 7 * 3600 * 1000 + days * 86400 * 1000);
  return now.toISOString().slice(0, 10);
}

function grid(date) {
  return http.get(`${BASE}/api/venues/${VENUE_ID}/availability?date=${date}`, {
    tags: { kind: 'api', name: 'grid' },
  });
}

/** Somebody looking at the grid for a day in the coming week, then thinking about it. */
export function browse() {
  const answer = grid(bangkokDate(1 + Math.floor(Math.random() * 7)));
  check(answer, { 'grid answers': (r) => r.status === 200 });
  sleep(1 + Math.random() * 2);
}

/** One VU per account, signed in once; the jar keeps the session for its later iterations. */
function signedIn() {
  const account = accounts[(__VU - 1) % accounts.length];
  const jar = http.cookieJar();
  if (!jar.cookiesForURL(BASE)['.AspNetCore.Identity.Application']) {
    const login = http.post(`${BASE}/api/auth/login`, JSON.stringify(account), {
      headers: { 'Content-Type': 'application/json' },
      tags: { kind: 'api', name: 'login' },
    });
    check(login, { 'signs in': (r) => r.status === 204 });
  }
}

/**
 * A booker picking a free hour and holding it, then giving it back so the grid does not fill up
 * over ten minutes — the write path is what is measured, not how full the venue gets.
 */
export function book() {
  signedIn();

  const date = bangkokDate(1 + Math.floor(Math.random() * 14));
  const day = grid(date);
  if (day.status !== 200) {
    return;
  }

  const free = [];
  for (const court of day.json('courts')) {
    for (const hour of court.hours) {
      if (hour.status === 'Free') {
        free.push({ courtId: court.courtId, date, hour: hour.hour });
      }
    }
  }
  if (free.length === 0) {
    return;
  }

  const slot = free[Math.floor(Math.random() * free.length)];
  const held = http.post(
    `${BASE}/api/bookings`,
    JSON.stringify({ venueId: VENUE_ID, slots: [slot] }),
    { headers: { 'Content-Type': 'application/json' }, tags: { kind: 'api', name: 'hold' } },
  );
  check(held, { 'holds or finds it taken': (r) => r.status === 201 || r.status === 409 });

  if (held.status === 201) {
    const cancelled = http.post(`${BASE}/api/bookings/${held.json('id')}/cancel`, null, {
      tags: { kind: 'api', name: 'cancel' },
    });
    check(cancelled, { 'gives it back': (r) => r.status === 200 });
  }
}
