# Browser checks

Playwright scripts that drive the real stack, for the flows unit tests cannot cover: what a page
shows after a reload, what the server actually stored, and what a member without the permission
sees.

```bash
docker compose --profile full up -d --build      # from the repo root
pip install playwright && playwright install chromium
python scripts/verify/venue_settings.py
```

They sign in as the seeded accounts (`owner@courtpaka.local` / `staff@courtpaka.local`,
`DevPassword1`) and write screenshots next to themselves. Each check prints PASS or FAIL and the
script exits non-zero if anything failed.

Six rules worth keeping.

1. Set up the state the script needs through the API before driving the UI, and make it state the
   script can create again — `harness.new_booker()` registers a fresh booker rather than reusing a
   seeded one, because a booker may hold only one booking and the previous run left one. The scripts share one
   venue, so one that closes a weekday will break another that prices one, and the failure will
   look like an app bug.
2. Wait for the response before reloading. The pages apply a change straight away and reconcile
   with the server, so reloading right after a click cancels the request in flight and the check
   reads the old state.
3. Name what you are looking for. Count rows only once the section has rendered, and find a form by
   something inside it rather than by where it sits — the settings page has four now.
4. Assert on elements, not on wording. A signed-in venue's language is saved to their account, so
   a check that greps for Thai text passes until somebody switches to English — give the thing a
   data-testid and look for that.
5. Check the positive next to the negative. "The day past the window is not offered" passes on
   its own when the calendar is simply showing another month — pair it with "the last day of the
   window is offered" so a check that stopped reaching its subject fails instead of passing.
6. Never hard-code what the seed put there. The scripts run in sequence against one database and
   the earlier ones add courts, so ask the API how many to expect rather than writing the number.

| Script | Covers |
|---|---|
| `venue_ui.py` | Sign-in redirects, venue detail, members and permissions (US-14) |
| `venue_settings.py` | Courts and opening hours (US-11) |
| `venue_pricing.py` | Prices and the cancellation policy (US-11) |
| `booking_grid.py` | Venue search and the court-by-hour grid (US-02) |
| `booking.py` | Picking hours, the summary, and holding them (US-03) |
| `payment.py` | The countdown, sending the slip, and who may read it (US-04) |
| `slip_queue.py` | The venue looking at a slip and deciding (US-12) |

7. **Ask for the state you need; do not assume the script before you left it.**
   `venue_settings.py` edits the seeded venue's opening hours because that is what it is about,
   and it leaves them wherever its last check left them. Every script that books an hour calls
   `ensure_bookable()` first, which puts the venue back to 06:00–22:00 every day. This was found
   the hard way: run in one order the suite was green, and in another every booking script failed
   with no free hour anywhere and nothing to say why.

## Service worker

The production build registers a service worker (PWA, PRD 8). Requests the app makes go through
it, and Playwright's `page.route(...)` cannot see requests a service worker makes. A script that
stubs API answers with `page.route` must open its page with `service_workers="block"` (see
`venue_ui.py`). `pwa.py` is the script that checks the worker itself.
