"""Shutting a court for a stretch of time (US-11)."""

import datetime

from harness import (
    BASE,
    OWNER,
    Checks,
    clear_waiting,
    ensure_bookable,
    new_booker,
    open_seeded_venue,
    pick_date,
    seeded_venue_id,
    sign_in,
    take_first_free_hour,
    venue_today,
)
from playwright.sync_api import expect, sync_playwright

check = Checks(__file__)
tomorrow = venue_today() + datetime.timedelta(days=1)

with sync_playwright() as p:
    browser = p.chromium.launch()
    venue_id = seeded_venue_id(browser.new_page())
    ensure_bookable(browser, venue_id)

    staff = browser.new_page()
    sign_in(staff, OWNER)
    clear_waiting(staff, venue_id, tomorrow)

    staff.close()

    page = browser.new_page(viewport={"width": 1280, "height": 900})
    sign_in(page, OWNER)
    page.goto(f"{BASE}{open_seeded_venue(page)}")
    page.click("[data-testid=closures-link]")
    page.wait_for_selector("[data-testid=close-court]")
    check("the venue has a door for closing a court", True, page)

    # 1. A booking standing in the stretch stops the closure, and says which one.
    booker = browser.new_page(viewport={"width": 390, "height": 844})
    sign_in(booker, new_booker(booker))
    booking = take_first_free_hour(booker, venue_id, tomorrow).json()
    booked = booking["slots"][0]

    page.reload()
    page.wait_for_selector("[data-testid=close-court]")
    page.click(f"[data-testid=court-{booked['courtId']}]")
    pick_date(page, tomorrow, "[data-testid=starts-on-toggle] button")
    pick_date(page, tomorrow, "[data-testid=ends-on-toggle] button")
    page.fill("[data-testid=start-hour]", str(booked["hour"]))
    page.fill("[data-testid=end-hour]", str(booked["hour"] + 1))
    page.fill("[data-testid=reason]", "ซ่อมพื้นทั้งวัน")

    with page.expect_response(lambda r: r.url.endswith("/closures")) as refused:
        page.click("[data-testid=close-court]")
    check("a court with a booking in the way will not close", refused.value.status == 409)

    expect(page.locator("[data-testid=in-the-way]")).to_be_visible()
    check(
        "and the venue is shown which booking it is",
        page.locator(f"[data-testid=clash-{booking['id']}]").count() == 1,
        page,
    )

    # The list must not carry the booker's address: this screen is behind CloseCourt, not
    # ManageBookings (PDPA).
    check(
        "without naming the booker",
        "@" not in page.locator("[data-testid=in-the-way]").inner_text(),
    )

    # 2. Out of the way, the same stretch closes.
    booker.request.post(f"{BASE}/api/bookings/{booking['id']}/cancel")

    with page.expect_response(lambda r: r.url.endswith("/closures")) as closed:
        page.click("[data-testid=close-court]")
    check("once it is cancelled the court closes", closed.value.status == 200)

    closure = closed.value.json()
    expect(page.locator(f"[data-testid=closure-{closure['id']}]")).to_be_visible()
    check("and the closure is listed", True, page)

    # 3. The hours are off the grid for a booker, and the rest of the venue is not.
    grid = booker.request.get(
        f"{BASE}/api/venues/{venue_id}/availability?date={tomorrow.isoformat()}").json()
    shut_court = next(c for c in grid["courts"] if c["courtId"] == booked["courtId"])
    shut_hour = next(h for h in shut_court["hours"] if h["hour"] == booked["hour"])
    other = next(c for c in grid["courts"] if c["courtId"] != booked["courtId"])
    other_hour = next(h for h in other["hours"] if h["hour"] == booked["hour"])
    check("the shut hour is off sale", shut_hour["status"] == "Closed")
    check(
        "and the rest of that court's day is not",
        any(
            hour["status"] == "Free"
            for hour in shut_court["hours"]
            if hour["hour"] != booked["hour"]
        ),
    )
    check("nor the same hour on another court", other_hour["status"] != "Closed")

    # 4. And the hour cannot be bought behind the grid's back.
    refused_booking = booker.request.post(
        f"{BASE}/api/bookings",
        data={
            "venueId": venue_id,
            "slots": [{
                "courtId": booked["courtId"],
                "date": tomorrow.isoformat(),
                "hour": booked["hour"],
            }],
        })
    check("nor can the hour be booked directly", refused_booking.status == 409)

    # 5. Opening it again puts the hours back, and the record stays.
    with page.expect_response(lambda r: "/lift" in r.url) as lifted:
        page.click(f"[data-testid=lift-{closure['id']}]")
    check("the court can be opened again", lifted.value.status == 200)

    expect(page.locator(f"[data-testid=lifted-{closure['id']}]")).to_be_visible()
    check("and the closure stays on the list, marked", True, page)

    back = booker.request.get(
        f"{BASE}/api/venues/{venue_id}/availability?date={tomorrow.isoformat()}").json()
    back_court = next(c for c in back["courts"] if c["courtId"] == booked["courtId"])
    back_hour = next(h for h in back_court["hours"] if h["hour"] == booked["hour"])
    check("the hour is on sale again", back_hour["status"] == "Free")

    # 6. Somebody who cannot close a court cannot read this either.
    stranger = browser.new_page()
    sign_in(stranger, new_booker(stranger))
    check(
        "a stranger cannot see what is shut",
        stranger.request.get(f"{BASE}/api/venues/{venue_id}/closures").status == 403,
    )

    browser.close()

check.summarise()
