"""The schedule's list view ("รายการจอง"): any day's bookings, a search across days, the panel.

A counter booking is sold on a day ahead through the API, found again by the customer's name
from the list on today, opened in the same panel the timeline uses — and its story is there.
"""

import datetime
import uuid

from harness import BASE, OWNER, Checks, ensure_bookable, seeded_venue_id, sign_in, venue_today
from playwright.sync_api import sync_playwright

check = Checks(__file__)

with sync_playwright() as p:
    browser = p.chromium.launch()
    venue_id = seeded_venue_id(browser.new_page())
    ensure_bookable(browser, venue_id)

    desk = browser.new_page(viewport={"width": 1440, "height": 900})
    sign_in(desk, OWNER)
    api = f"{BASE}/api/venues/{venue_id}"

    # A booking a few days ahead, under a name nobody else has.
    day = venue_today() + datetime.timedelta(days=3)
    grid = desk.request.get(f"{api}/availability", params={"date": day.isoformat()}).json()
    court_id, hour = next(
        (court["courtId"], one["hour"])
        for court in grid["courts"]
        for one in court["hours"]
        if one["status"] == "Free"
    )
    name = f"ลูกค้า {uuid.uuid4().hex[:6]}"
    sold = desk.request.post(
        f"{api}/bookings",
        data={
            "slots": [{"courtId": court_id, "date": day.isoformat(), "hour": hour}],
            "customerName": name,
            "customerPhone": "0819990000",
            "paidBy": "Cash",
        },
    )
    check("the counter sells a booking three days ahead", sold.status == 201)
    booking_id = sold.json()["bookingId"]

    # The list is the schedule's third view.
    desk.goto(f"{BASE}/venues/{venue_id}/timeline")
    desk.click("[data-testid=view-bookings]")
    desk.wait_for_selector("[data-testid=booking-list]")
    check("the list opens from the schedule's views", "/bookings" in desk.url, desk)
    check("the schedule stays the chosen section",
          desk.locator("[data-testid=nav-schedule][aria-current=page]").count() == 1, desk)
    desk.screenshot(path=str(check.shots / "list-today.png"))

    # Found by name, on whatever day it is.
    desk.fill("[data-testid=list-search]", name)
    row = desk.locator(f"[data-testid=list-row-{booking_id}]")
    row.wait_for()
    check("a search finds the booking on another day", row.count() == 1, desk)

    row.click()
    desk.wait_for_selector("[data-testid=panel-name]")
    check("the row opens the timeline's panel", desk.locator("[data-testid=panel-name]").inner_text() == name, desk)
    desk.wait_for_selector("[data-testid=booking-history] li")
    check("the panel says what happened to it",
          desk.locator("[data-testid=booking-history] li").count() >= 2, desk)
    desk.screenshot(path=str(check.shots / "list-found.png"))

    # Walking to that day through the URL lists it too.
    desk.fill("[data-testid=list-search]", "")
    desk.goto(f"{BASE}/venues/{venue_id}/bookings?date={day.isoformat()}")
    desk.locator(f"[data-testid=list-row-{booking_id}]").wait_for()
    check("the day in the URL lists its bookings",
          desk.locator(f"[data-testid=list-row-{booking_id}]").count() == 1, desk)

    # Cancelled from the panel, it stays on the list as cancelled.
    desk.click(f"[data-testid=list-row-{booking_id}]")
    desk.click("[data-testid=cancel-open]")
    desk.click("[data-testid=cancel-reason-VenueInitiated]")
    with desk.expect_response(lambda r: r.url.endswith(f"/bookings/{booking_id}/cancel")) as done:
        desk.click("[data-testid=confirm-cancel]")
    check("the panel cancels it", done.value.ok, desk)
    desk.click("[data-testid=list-filter-cancelled]")
    check("it is listed under cancelled",
          desk.locator(f"[data-testid=list-row-{booking_id}]").count() == 1, desk)

    # A phone: rows are cards and the panel follows.
    phone = browser.new_page(viewport={"width": 390, "height": 844})
    sign_in(phone, OWNER)
    phone.goto(f"{BASE}/venues/{venue_id}/bookings?date={day.isoformat()}")
    phone.wait_for_selector(f"[data-testid=list-row-{booking_id}]")
    phone.screenshot(path=str(check.shots / "list-phone.png"), full_page=True)
    wide = phone.evaluate("document.documentElement.scrollWidth")
    check("nothing is wider than the phone", wide <= 390, phone)

    browser.close()

check.summarise()
