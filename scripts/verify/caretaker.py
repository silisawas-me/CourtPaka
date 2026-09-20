"""The work nobody asks for: holds that run out on hours nobody is looking at (PRD 9.2)."""

import datetime
import time

from harness import (
    BASE,
    Checks,
    ensure_bookable,
    booking_status,
    new_booker,
    run_out_hold,
    seeded_venue_id,
    sign_in,
    take_first_free_hour,
    venue_today,
)
from playwright.sync_api import sync_playwright

check = Checks(__file__)
tomorrow = venue_today() + datetime.timedelta(days=1)

# The stack's caretaker goes round every ten seconds (docker-compose.yml), so this waits for one
# round plus room for a slow one. It is the only script here that waits on a clock rather than on
# something it asked for, because the whole point is that nobody asked.
ROUNDS = 25

with sync_playwright() as p:
    browser = p.chromium.launch()
    venue_id = seeded_venue_id(browser.new_page())
    ensure_bookable(browser, venue_id)

    booker = browser.new_page()
    sign_in(booker, new_booker(booker))
    booking = take_first_free_hour(booker, venue_id, tomorrow).json()
    check("a booker holds an hour", booking["status"] == "Held")

    # Run the hold out without touching the hours it sits on: no grid is read, no booking is made
    # on that court, nothing asks the server about it. Before the caretaker, nothing would have
    # moved it, and the booker would have stayed locked out of booking again (S-22).
    run_out_hold(booking["id"])
    check("and its fifteen minutes are made to run out", True)

    # Watched from the database, not through the API: reading a booker's own list is itself one
    # of the things that ends their lapsed holds, so polling it would prove nothing.
    EXPIRED = 6
    waited = 0
    status = booking_status(booking["id"])
    while waited < ROUNDS and status != EXPIRED:
        time.sleep(2)
        waited += 2
        status = booking_status(booking["id"])

    check(f"the caretaker ends it without anybody asking (after {waited}s)", status == EXPIRED)

    # And the hour it was sitting on is on sale again. This one would pass either way — reading
    # the grid releases lapsed holds on the courts it covers — so it is here to show the hour
    # really is back, not to prove who put it back. The check above is what proves that.
    grid = booker.request.get(
        f"{BASE}/api/venues/{venue_id}/availability?date={tomorrow.isoformat()}").json()
    slot = booking["slots"][0]
    court = next(c for c in grid["courts"] if c["courtId"] == slot["courtId"])
    hour = next(h for h in court["hours"] if h["hour"] == slot["hour"])
    check("and the hour it held is free again", hour["status"] == "Free")

    browser.close()

check.summarise()
