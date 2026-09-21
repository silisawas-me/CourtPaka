"""Selling hours at the counter to somebody standing at it (US-13)."""

import datetime

from harness import (
    BASE,
    OWNER,
    Checks,
    ensure_bookable,
    new_booker,
    open_seeded_venue,
    pick_date,
    seeded_venue_id,
    sign_in,
    venue_today,
)
from playwright.sync_api import expect, sync_playwright

check = Checks(__file__)
tomorrow = venue_today() + datetime.timedelta(days=1)

with sync_playwright() as p:
    browser = p.chromium.launch()
    venue_id = seeded_venue_id(browser.new_page())
    ensure_bookable(browser, venue_id)

    page = browser.new_page(viewport={"width": 1280, "height": 900})
    sign_in(page, OWNER)
    page.goto(f"{BASE}{open_seeded_venue(page)}")
    page.click("[data-testid=venue-bookings-link]")
    page.wait_for_selector("[data-testid=day]")
    pick_date(page, tomorrow)

    # 1. The counter sells from the day it is looking at.
    page.click("[data-testid=sell-at-counter]")
    page.wait_for_selector("[data-testid=counter-grid]")
    check("the counter opens a grid of the day it is looking at", True, page)

    cell = page.locator("[data-testid=counter-grid] [data-testid^=counter-cell-]").first
    test_id = cell.get_attribute("data-testid")
    _, _, court_id_and_hour = test_id.partition("counter-cell-")
    court_id, _, hour = court_id_and_hour.rpartition("-")
    cell.click()
    expect(page.locator("[data-testid=counter-total]")).not_to_contain_text(": 0")
    check("picking an hour adds up its price", True)

    # 2. It will not book for nobody.
    page.click("[data-testid=counter-take]")
    expect(page.locator("[data-testid=customer-name-error]")).to_be_visible()
    check("it will not book for nobody", True, page)

    # 3. With a name and how they paid, it books — confirmed, with the money received.
    page.fill("[data-testid=customer-name]", "คุณสมชาย ทดสอบ")
    page.fill("[data-testid=customer-phone]", "081-234-5678")
    page.click("[data-testid=paid-Transfer]")
    with page.expect_response(
        lambda r: r.url.endswith(f"/venues/{venue_id}/bookings") and r.request.method == "POST"
    ) as taken:
        page.click("[data-testid=counter-take]")
    check("the counter can book for somebody without an account", taken.value.status == 201)

    booking = taken.value.json()
    check("and it starts confirmed", booking["status"] == "Confirmed")
    check("with the money received", booking["paymentState"] == "Received")
    check("and no account behind it", booking["bookerEmail"] is None)

    row = page.locator(f"[data-testid=who-{booking['bookingId']}]")
    expect(row).to_be_visible()
    check(
        "the day names the customer, not an address",
        "คุณสมชาย ทดสอบ" in row.inner_text(),
        page,
    )

    # 4. The hour is gone for everybody, including a booker online.
    booker = browser.new_page()
    sign_in(booker, new_booker(booker))
    refused = booker.request.post(
        f"{BASE}/api/bookings",
        data={
            "venueId": venue_id,
            "slots": [{"courtId": court_id, "date": tomorrow.isoformat(), "hour": int(hour)}],
        })
    check("an hour sold at the counter cannot then be booked online", refused.status == 409)

    # 5. A stranger cannot sell here.
    stranger = browser.new_page()
    sign_in(stranger, new_booker(stranger))
    check(
        "somebody who does not work here cannot sell at this counter",
        stranger.request.post(
            f"{BASE}/api/venues/{venue_id}/bookings",
            data={
                "slots": [{"courtId": court_id, "date": tomorrow.isoformat(), "hour": int(hour)}],
                "customerName": "x",
                "customerPhone": None,
                "paidBy": "Cash",
            }).status == 403,
    )

    # Give the hour back, so the next run and the other scripts find it free.
    let_go = page.request.post(
        f"{BASE}/api/venues/{venue_id}/bookings/{booking['bookingId']}/cancel",
        data={"reason": "CustomerRequest", "paymentReceived": None, "note": None})
    check("and it can be cancelled like any other", let_go.status == 200)

    browser.close()

check.summarise()
