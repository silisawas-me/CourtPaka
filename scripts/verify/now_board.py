"""The counter's "now": every court this minute, who is due, a quick sale (badPaka 2b).

The page reads the wall clock, so the browser's clock is pinned to an evening hour today: the
script says the same thing whenever it is run. The server's clock is not pinned, so the doors it
opens (check-in) are not what is checked here — the floor the page draws from them is.
"""

import datetime

from harness import (
    BASE,
    OWNER,
    Checks,
    ensure_bookable,
    seeded_venue_id,
    sign_in,
    venue_today,
)
from playwright.sync_api import sync_playwright

check = Checks(__file__)
BANGKOK = datetime.timezone(datetime.timedelta(hours=7))

with sync_playwright() as p:
    browser = p.chromium.launch()
    venue_id = seeded_venue_id(browser.new_page())
    ensure_bookable(browser, venue_id)
    desk = browser.new_page(viewport={"width": 1280, "height": 900})
    sign_in(desk, OWNER)
    api = f"{BASE}/api/venues/{venue_id}"

    # A walk-in on the last hour of the day with a court free, so the floor has a known game. The
    # counter only sells an hour that has not ended by the server's clock, so after closing the
    # day is tomorrow (the browser's clock is pinned to whichever day it is).
    now_hour = datetime.datetime.now(BANGKOK).hour
    found = None
    for ahead in (0, 1):
        today = venue_today() + datetime.timedelta(days=ahead)
        day = desk.request.get(f"{api}/availability", params={"date": today.isoformat()}).json()
        found = next(
            (
                (court["courtId"], one["hour"])
                for court in day["courts"]
                for one in reversed(court["hours"])
                if one["status"] == "Free" and (ahead > 0 or one["hour"] + 1 > now_hour)
            ),
            None,
        )
        if found:
            break
    assert found, "no free hour today or tomorrow"
    court_id, hour = found
    sold = desk.request.post(
        f"{api}/bookings",
        data={
            "slots": [{"courtId": court_id, "date": today.isoformat(), "hour": hour}],
            "customerName": "Now walk-in",
            "customerPhone": None,
            "paidBy": "Cash",
        },
    )
    check("the walk-in is sold at the counter", sold.status == 201)
    booking_id = sold.json()["bookingId"]

    def at(h: int, m: int) -> datetime.datetime:
        return datetime.datetime.combine(today, datetime.time(h, m), tzinfo=BANGKOK)

    # Half an hour before: they are on the way.
    desk.clock.set_fixed_time(at(hour - 1, 30))
    desk.goto(f"{BASE}/venues/{venue_id}/now")
    desk.wait_for_selector(f"[data-testid=now-court-{court_id}]")
    check("the schedule's top bar offers the timeline and right now, with now chosen",
          desk.locator("[data-testid=view-timeline]").count() == 1
          and "on" in (desk.locator("[data-testid=view-now]").get_attribute("class") or ""))
    check(
        "somebody starting within ninety minutes is in the queue at the desk",
        desk.locator(f"[data-testid=now-arrival-{booking_id}]").count() == 1,
        desk,
    )

    # Ten minutes in: they are on the court.
    desk.clock.set_fixed_time(at(hour, 10))
    desk.reload()
    card = desk.locator(f"[data-testid=now-court-{court_id}]")
    card.wait_for()
    state = card.get_attribute("class") or ""
    check("their court shows the game on it", ("due" in state or "playing" in state), desk)
    check("the card names who is on it", "Now walk-in" in card.inner_text())
    check("the card says how long is left",
          "50" in desk.locator(f"[data-testid=now-left-{court_id}]").inner_text())
    check("nobody on the court is queueing at the desk as well",
          desk.locator(f"[data-testid=now-arrival-{booking_id}]").count() == 0)
    # The quick sale is tiles of what the shop sells (or one tile when it sells nothing yet),
    # and a tile opens the sale with no booking attached.
    desk.locator("[data-testid^=quick-], [data-testid=sell-open]").first.click()
    desk.wait_for_selector("[data-testid=sell-onto-booking]")
    check("the desk can sell without a booking",
          desk.locator("[data-testid=sell-onto-booking]").count() == 1, desk)

    # A phone gets the same floor, one court under another.
    phone = browser.new_page(viewport={"width": 390, "height": 844})
    sign_in(phone, OWNER)
    phone.clock.set_fixed_time(at(hour, 10))
    phone.goto(f"{BASE}/venues/{venue_id}/now")
    phone.wait_for_selector(f"[data-testid=now-court-{court_id}]")
    widest = phone.evaluate("() => document.documentElement.scrollWidth")
    check("the floor fits a phone", widest <= 390, phone)

    desk.request.post(
        f"{api}/bookings/{booking_id}/cancel",
        data={"reason": "VenueInitiated", "note": "verify"},
    )
    browser.close()

check.summarise()
