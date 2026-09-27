"""What a booker who signed in with LINE is told, and where (PRD US-34).

The gap this closes is the whole point: a LINE account books with a phone number and never has to
prove an email address (US-01), and the letters only ever went to an address somebody had proved
(US-06) — so that person was told nothing at all. Here they book, and the news reaches them on
LINE, in their own language, with nothing sent to the address LINE happened to share.

The local stack runs App__Line__UseDevelopmentFake, so /api/dev/line/authorize stands in for
LINE's consent screen and the Development messenger logs what it would have pushed, which is where
the check reads it from — the same way booker_mail.py reads the letters.
"""

import datetime
import time
import uuid

from harness import (
    BASE,
    Checks,
    ensure_bookable,
    logged_line,
    logged_mail,
    run_out_hold,
    seeded_venue_id,
    take_first_free_hour,
    venue_today,
)
from playwright.sync_api import sync_playwright

check = Checks(__file__)
tomorrow = venue_today() + datetime.timedelta(days=1)

# One caretaker round is ten seconds on the local stack (docker-compose.yml); room for a slow one.
ROUNDS = 25


def be_at_line(page, sub: str, name: str = "ปกป้อง", email: str | None = None):
    """Fills in the stand-in LINE screen and comes back, the way a person would."""
    page.wait_for_selector("#sub")
    page.fill("#sub", sub)
    page.fill("#name", name)
    if email:
        page.fill("#email", email)
    page.click("#allow")


def said_on_line(line_id: str, booking_id: str, count: int) -> list[str]:
    """Waits until this LINE account has been told about this booking this many times."""
    waited = 0
    found = [one for one in logged_line(line_id) if f"/bookings/{booking_id}" in one]
    while waited < ROUNDS and len(found) < count:
        time.sleep(2)
        waited += 2
        found = [one for one in logged_line(line_id) if f"/bookings/{booking_id}" in one]
    return found


with sync_playwright() as p:
    browser = p.chromium.launch()
    venue_id = seeded_venue_id(browser.new_page())
    ensure_bookable(browser, venue_id)

    page = browser.new_page(viewport={"width": 390, "height": 844})
    line_id = f"U{uuid.uuid4().hex}"
    shared = f"line-{uuid.uuid4().hex[:10]}@example.com"

    # 1. Somebody arrives through LINE and becomes a booker: a phone number, and an address LINE
    #    shared that nobody has proved (US-01).
    page.goto(f"{BASE}/login")
    page.wait_for_selector("[data-testid=line-sign-in]")
    page.click("[data-testid=line-sign-in]")
    be_at_line(page, line_id, email=shared)
    page.wait_for_url(f"{BASE}/register/line**")
    page.fill("[data-testid=line-phone] input, input[data-testid=line-phone]", "081-234-5678")
    page.click("[data-testid=line-accept-policy] input")
    page.click("[data-testid=line-complete]")
    page.wait_for_url(f"{BASE}/")

    me = page.request.get(f"{BASE}/api/auth/me").json()
    check("a LINE account can book without ever proving an address",
          me["signsInWithLine"] is True
          and me["email"] == shared
          and me["cannotBookBecause"] is None,
          page)

    # 2. They book, and the hold is something they need to hear about.
    booking = take_first_free_hour(page, venue_id, tomorrow).json()
    said = said_on_line(line_id, booking["id"], 1)

    check(f"the news reaches them on LINE ({len(said)} message)", len(said) == 1, page)
    check("and nothing was sent to the address LINE shared",
          [one for one in logged_mail(shared) if f"/bookings/{booking['id']}" in one] == [])

    # 3. It is the letter's own words, in their language, with a link back to the booking.
    first = said[0]
    check("the message is the letter, in the booker's own language",
          "booker.Held" in first and "ปกป้อง" not in first.split("\n")[0]
          and f"/bookings/{booking['id']}" in first)
    check("and it says which venue and what it holds",
          "DEV01" in first or "คอร์ท" in first)

    # 4. The clock moves it on, and that reaches them the same way — the job reads what happened
    #    rather than being called from where it happens, so every kind arrives by the same road.
    run_out_hold(booking["id"])
    ran_out = said_on_line(line_id, booking["id"], 2)

    check(f"the hold running out reaches them too ({len(ran_out)} messages)", len(ran_out) == 2)
    check("and that one is the expiry letter",
          any("booker.Expired" in one for one in ran_out))

    # 5. The receipt says which way it went, so "was this person told, and how" has an answer.
    told = page.request.get(f"{BASE}/api/bookings").json()
    check("the booking is theirs and reads as expired",
          any(one["id"] == booking["id"] for one in told["past"]), page)

    page.close()
    browser.close()

check.summarise()
