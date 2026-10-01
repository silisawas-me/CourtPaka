# Browser checks

Playwright scripts that drive the real stack, for the flows unit tests cannot cover: what a page
shows after a reload, what the server actually stored, and what a member without the permission
sees.

```bash
docker compose --profile full up -d --build      # from the repo root
pip install playwright && playwright install chromium
python scripts/verify/venue_shell.py
```

They sign in as the seeded accounts (`owner@courtpaka.local` / `staff@courtpaka.local`,
`DevPassword1`) and write screenshots next to themselves. Each check prints PASS or FAIL and the
script exits non-zero if anything failed.

Eight rules worth keeping.

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
6. Remember the caretaker is awake. It sweeps every 10 s locally and acts on the same data the
   script is driving — it hands a waiting place an offer, lets a hold lapse, sends a booker's
   mail. A check that depends on something *not* having happened yet is a race the script loses
   on a slow machine, so arrange the state so the job cannot act (a booker who already holds
   hours is skipped when offers go out) rather than
   hoping to get there first.
7. Never hard-code what the seed put there. The scripts run in sequence against one database and
   the earlier ones add courts, so ask the API how many to expect rather than writing the number.

| Script | Covers |
|---|---|
| `doors.py` | The admin door and the staff door, the sign-in guard and its returnUrl; old booker addresses are gone |
| `venue_shell.py` | The four sections on a desk and a phone, branch switching, and where the removed pages' addresses land (US-25) |
| `timeline.py` | The court schedule of one branch and its booking panel |
| `now_board.py` | Every court this minute, with the browser clock pinned (badPaka 2b) |
| `all_venues_today.py` | Today at every venue on "my venues" (badPaka 2c) |
| `walk_in.py` | Selling a walk-in from the top bar |
| `venue_pricing.py` | Prices and peak, opening hours and grace (owner app PR-4, 2c) |
| `venue_dashboard.py` | The revenue panel over the last fourteen days (US-15, PR-5) |
| `packages.py` | Hours sold in advance and the members table (US-31) |
| `counter_money.py` | Money taken at the desk and the day's count, through the API (US-26) |

8. **Ask for the state you need; do not assume the script before you left it.**
   `venue_pricing.py` edits the seeded venue's opening hours because that is what it is about,
   and it leaves them wherever its last check left them. Every script that books an hour calls
   `ensure_bookable()` first, which puts the venue back to 06:00–22:00 every day. This was found
   the hard way: run in one order the suite was green, and in another every booking script failed
   with no free hour anywhere and nothing to say why.

   The same goes for who may do what. A script that hands the seeded staff account permissions
   because that is what it tests leaves them there — so a script checking what somebody
   *without* a permission sees passes alone and fails after it. `staff_can(browser, venue_id)`
   puts the staff back to what the seed gives them, or to whatever the script needs.

## Online bookings without the booker's pages

The booker's pages were taken out (docs/plan/cut-booker.md). Scripts that need an online booking
to exist — a slip to check, a hold to run out — make it through the same API the grid called:
`take_first_free_hour()` holds the first free hour from the grid's answer, and `send_slip()`
posts the file to the booking that page held last. What they check is the venue's side.

## Service worker

The production build registers a service worker (PWA, PRD 8). Requests the app makes go through
it, and Playwright's `page.route(...)` cannot see requests a service worker makes. A script that
stubs API answers with `page.route` must open its page with `service_workers="block"`. `pwa.py` is the script that checks the worker itself.
