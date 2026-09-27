"""What the booker is told by email: a hold to pay for, and a hold that ran out (PRD US-06).

The caretaker sends booker mail on its own rounds, so like caretaker.py this waits on a clock. The
Development sender logs every message, which is where the check reads them from.
"""

import datetime
import re
import time

from harness import (
    Checks,
    ensure_bookable,
    logged_mail,
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

# One caretaker round is ten seconds on the local stack (docker-compose.yml); this leaves room for
# a slow one.
ROUNDS = 25


def mail_about(email: str, booking_id: str, count: int) -> list[str]:
    """Waits until this address has been told about this booking this many times."""
    waited = 0
    found = [m for m in logged_mail(email) if f"/bookings/{booking_id}" in m]
    while waited < ROUNDS and len(found) < count:
        time.sleep(2)
        waited += 2
        found = [m for m in logged_mail(email) if f"/bookings/{booking_id}" in m]
    return found


with sync_playwright() as p:
    browser = p.chromium.launch()
    venue_id = seeded_venue_id(browser.new_page())
    ensure_bookable(browser, venue_id)

    booker = browser.new_page()
    email = new_booker(booker)
    sign_in(booker, email)
    booking = take_first_free_hour(booker, venue_id, tomorrow).json()
    check("a booker holds an hour", booking["status"] == "Held")

    told = mail_about(email, booking["id"], 1)
    check("the booker is emailed about the hold without asking", len(told) == 1)

    held = told[0] if told else ""
    check("in the language the booker chose", f"Email to {email} [th]" in held)
    check("with the Buddhist year, as the screen shows dates", str(tomorrow.year + 543) in held)

    # The link still names the booking. The page it pointed at — the booker's own — was taken out
    # (docs/plan/cut-booker.md, D17), so it is not followed here.
    link = re.search(r"(https?://\S*/bookings/[0-9a-f-]+)", held)
    check("the message carries a link to the booking", link is not None)

    run_out_hold(booking["id"])
    told = mail_about(email, booking["id"], 2)
    check("a hold that runs out is emailed too", len(told) == 2)

    time.sleep(12)
    again = [m for m in logged_mail(email) if f"/bookings/{booking['id']}" in m]
    check("and nothing is sent twice on later rounds", len(again) == 2)

    browser.close()

check.summarise()
