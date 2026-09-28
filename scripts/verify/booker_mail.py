"""Bookers are told nothing while App:TellBookers is off (docs/plan/owner-complete.md 3b).

The booker's pages were taken out, so every message would link to a page that does not exist;
telling bookers is off on every stack until there is a booker-side app again. What the messages
say is covered by BookerMailTests, which switch it on. This waits through several caretaker rounds
and reads the Development sender's log, where a message would have been written.
"""

import datetime
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

with sync_playwright() as p:
    browser = p.chromium.launch()
    venue_id = seeded_venue_id(browser.new_page())
    ensure_bookable(browser, venue_id)

    booker = browser.new_page()
    email = new_booker(booker)
    sign_in(booker, email)
    booking = take_first_free_hour(booker, venue_id, tomorrow).json()
    check("a booker holds an hour", booking["status"] == "Held")

    # Three rounds of the caretaker (ten seconds each on the local stack), then the hold running
    # out and three more: a booker who was going to be told would have been by then.
    time.sleep(32)
    run_out_hold(booking["id"])
    time.sleep(32)
    told = [m for m in logged_mail(email) if f"/bookings/{booking['id']}" in m]
    check("nobody is told about a hold or its running out while telling bookers is off",
          told == [])

    browser.close()

check.summarise()
