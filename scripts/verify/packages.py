"""Hours sold in advance (US-31).

What is checked is the thing the venue actually gets: an offer on a board, a package sold for
money that lands in today's till, hours that pay for a court without the till being told twice,
hours that come back when the booking is let go, and a number on the dashboard saying what is
still owed — which is the half of this the accountant has to confirm (S-27).
"""

import datetime

from harness import (
    BASE,
    OWNER,
    Checks,
    clear_package_board,
    ensure_bookable,
    open_seeded_venue,
    pick_date,
    seeded_venue_id,
    sign_in,
    venue_today,
)
from playwright.sync_api import expect, sync_playwright

check = Checks(__file__)

# Far enough out that the scripts working on tomorrow are nowhere near it.
DAY = venue_today() + datetime.timedelta(days=17)


def board(page, venue_id):
    return page.request.get(f"{BASE}/api/venues/{venue_id}/packages/types").json()


def sold(page, venue_id):
    return page.request.get(f"{BASE}/api/venues/{venue_id}/packages").json()


def till_day(page, venue_id):
    """The day money taken right now lands in. Normally today — but a day that has been counted
    keeps the count it was written with, so anything taken afterwards belongs to the next one
    (PRD US-26). counter_money.py counts a day because that is what it is about, and it may have
    counted this one."""
    today = venue_today()
    counted = page.request.get(
        f"{BASE}/api/venues/{venue_id}/money?date={today.isoformat()}").json()["closed"]

    return today if counted is None else today + datetime.timedelta(days=1)


def money(page, venue_id, day=None):
    day = day or till_day(page, venue_id)
    return page.request.get(
        f"{BASE}/api/venues/{venue_id}/money?date={day.isoformat()}").json()


with sync_playwright() as p:
    browser = p.chromium.launch()
    venue_id = seeded_venue_id(browser.new_page())
    ensure_bookable(browser, venue_id)

    page = browser.new_page(viewport={"width": 1280, "height": 900})
    sign_in(page, OWNER)

    page.goto(f"{BASE}{open_seeded_venue(page)}")
    page.click("[data-testid=nav-packages]")
    page.wait_for_selector("[data-testid=open-board], [data-testid=add-offer]")
    check("the venue has a door for hours sold in advance", True, page)

    # 1. An offer on the board, and what an hour of it costs said for them.
    if page.locator("[data-testid=add-offer]").count() == 0:
        page.click("[data-testid=open-board]")
    page.fill("[data-testid=offer-name]", "ชุดตรวจสอบ 10 ชั่วโมง")
    page.fill("[data-testid=offer-hours]", "10")
    page.fill("[data-testid=offer-price]", "1800")
    page.fill("[data-testid=offer-days]", "90")
    page.click("[data-testid=add-offer]")
    page.wait_for_selector("[data-testid=sell-package]")

    offer = [one for one in board(page, venue_id) if one["withdrawnAt"] is None][0]
    check("an offer goes on the board and the server works out the hourly rate",
          offer["hours"] == 10 and offer["bahtPerHour"] == 180, page)

    # 2. Selling one is money in today's till.
    # The day the money will land in, asked once so the before and after are the same day.
    till = till_day(page, venue_id)
    before = money(page, venue_id, till)
    already = page.locator("[data-testid^=package-]").count()
    page.fill("[data-testid=customer-name]", "ก๊วนซื้อชั่วโมง")
    page.fill("[data-testid=customer-phone]", "0800000000")
    page.click("[data-testid=paid-Cash]")
    page.click("[data-testid=sell-package]")

    # A row more than there was. Waiting for "a row" would pass on the rows an earlier run left,
    # before this sale had even been answered.
    expect(page.locator("[data-testid^=package-]")).to_have_count(already + 1)

    package = sold(page, venue_id)[0]
    after = money(page, venue_id, till)
    check("selling one puts the money in the till of the day it is taken",
          after["cashBaht"] == before["cashBaht"] + 1800)
    check("and the till row says it was a package, not a booking",
          any(one["packageId"] == package["packageId"] and one["bookingId"] is None
              for one in after["cashReceipts"]))
    check("the hours are on the package, and the ledger says where they came from",
          package["hoursLeft"] == 10 and package["moves"][0]["move"] == "Sold", page)

    # 3. Sold is not earned: the dashboard says what is owed, apart from the money.
    figures = page.request.get(f"{BASE}/api/venues/{venue_id}/dashboard").json()
    check("hours sold are owed rather than earned",
          figures["owedHours"]["hours"] >= 10 and figures["owedHours"]["baht"] >= 1800)

    page.goto(f"{BASE}/venues/{venue_id}/dashboard")
    expect(page.get_by_test_id("owed-hours")).to_be_visible()
    check("and the venue can see it on its own page", True, page)

    # 4. The counter takes hours instead of money.
    # The day is chosen through the picker, which is what the page reads — a date in the address
    # is not what the counter opens on.
    page.goto(f"{BASE}/venues/{venue_id}/bookings")
    page.wait_for_selector("[data-testid=sell-at-counter]")
    pick_date(page, DAY)
    page.click("[data-testid=sell-at-counter]")
    page.wait_for_selector("[data-testid=counter-grid]")

    # Whichever hour is free: what the other scripts have left on this day is not this one's
    # business, and a fixed hour is a script that fails for somebody else's reason.
    grid = page.request.get(
        f"{BASE}/api/venues/{venue_id}/availability?date={DAY.isoformat()}").json()
    court_id, hour = next(
        (court["courtId"], one["hour"])
        for court in grid["courts"]
        for one in court["hours"]
        if one["status"] == "Free"
    )
    page.click(f"[data-testid=counter-cell-{court_id}-{hour}]")
    page.fill("[data-testid=customer-name]", "ก๊วนซื้อชั่วโมง")
    page.click(f"[data-testid=pay-with-{package['packageId']}]")
    check("the counter offers the customer's own hours as a way to pay", True, page)

    till_before = money(page, venue_id, till)
    page.click("[data-testid=counter-take]")
    page.wait_for_selector("[data-testid=counter-grid]", state="detached")

    one_left = [one for one in sold(page, venue_id)
                if one["packageId"] == package["packageId"]][0]
    check("an hour comes off the package", one_left["hoursLeft"] == 9)

    till_after = money(page, venue_id, till)
    check("and nothing new goes in the till, because the money came in when it was sold",
          till_after["takenBaht"] == till_before["takenBaht"])

    day = page.request.get(
        f"{BASE}/api/venues/{venue_id}/bookings?date={DAY.isoformat()}").json()
    # The one just made, found by the hour it took: earlier runs leave bookings of the same name
    # on this day, and cancelling one of those would be cancelling somebody else's check.
    booked = [row for row in day
              if row["status"] == "Confirmed"
              and any(slot["courtId"] == court_id and slot["hour"] == hour
                      for slot in row["slots"])][0]
    check("the booking is confirmed and nobody is asked for money for it",
          booked["can"]["takeMoney"] is False)

    # 5. A booking the venue is already holding, settled with hours afterwards.
    # Whichever hour is free now: the one beside it may be somebody else's from an earlier run.
    free = page.request.get(
        f"{BASE}/api/venues/{venue_id}/availability?date={DAY.isoformat()}").json()
    spare_court, spare_hour = next(
        (court["courtId"], one["hour"])
        for court in free["courts"]
        for one in court["hours"]
        if one["status"] == "Free"
    )

    standing = page.request.post(
        f"{BASE}/api/venues/{venue_id}/bookings",
        data={
            "slots": [{"courtId": spare_court, "date": DAY.isoformat(), "hour": spare_hour}],
            "customerName": "ก๊วนที่ยังไม่จ่าย",
            "customerPhone": None,
            "paidBy": "Cash",
        },
    )
    check("a second booking is taken at the counter", standing.status == 201)

    # The day is chosen through the picker again: a reload lands on today, which is a different
    # day with different rows.
    page.goto(f"{BASE}/venues/{venue_id}/bookings")
    page.wait_for_selector("[data-testid=sell-at-counter]")
    pick_date(page, DAY)
    page.wait_for_selector("[data-testid=day-list]")
    on_hours = page.locator("[data-testid^=pay-with-package-]")
    check("a booking that has been paid for is not offered hours as well",
          on_hours.count() == 0, page)

    # 6. Letting it go gives the hour back, not money.
    page.request.post(
        f"{BASE}/api/venues/{venue_id}/bookings/{booked['bookingId']}/cancel",
        data={"reason": "VenueInitiated", "paymentReceived": None, "note": "ทดสอบ"})

    back = [one for one in sold(page, venue_id)
            if one["packageId"] == package["packageId"]][0]
    check("letting the booking go gives the hour back rather than money",
          back["hoursLeft"] == 10
          and any(move["move"] == "GivenBack" for move in back["moves"]))

    refunded = [row for row in page.request.get(
        f"{BASE}/api/venues/{venue_id}/bookings?date={DAY.isoformat()}").json()
        if row["bookingId"] == booked["bookingId"]][0]
    check("and nothing is owed back in money", refunded["refundDueBaht"] == 0)

    page.goto(f"{BASE}/venues/{venue_id}/packages")
    page.wait_for_selector("[data-testid^=package-]")
    check("the venue can read where every hour went", True, page)

    # The screens a design review asks for.
    check("desk, Thai", True, page)
    page.set_viewport_size({"width": 390, "height": 844})
    check("phone, Thai", True, page)
    check("nothing on a phone scrolls the page sideways",
          page.evaluate("document.documentElement.scrollWidth") <= 390)

    if not page.get_by_test_id("language-en").is_visible():
        page.click("[data-testid=open-menu]")
    page.click("[data-testid=language-en]")
    expect(page.locator("html")).to_have_attribute("lang", "en")
    check("phone, English", True, page)
    page.set_viewport_size({"width": 1280, "height": 900})
    check("desk, English", True, page)

    # And the venue is left the way the other scripts expect to find it.
    clear_package_board(page, venue_id)
    page.click("[data-testid=side-language-th]")
    expect(page.locator("html")).to_have_attribute("lang", "th")

    page.close()
    browser.close()

check.summarise()
