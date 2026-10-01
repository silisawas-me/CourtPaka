"""Money as it crosses the counter, and the count at the end of the day (US-26)."""

import datetime

from harness import (
    BASE,
    OWNER,
    Checks,
    as_upload,
    ensure_bookable,
    new_booker,
    real_jpeg,
    seeded_venue_id,
    send_slip,
    sign_in,
    take_first_free_hour,
    venue_today,
)
from playwright.sync_api import sync_playwright

check = Checks(__file__)


def new_page_for_a_booker(browser):
    """A booker with a page of their own, signed in and ready to take an hour."""
    page = browser.new_page(viewport={"width": 390, "height": 844})
    sign_in(page, new_booker(page))
    return page
tomorrow = venue_today() + datetime.timedelta(days=1)


def waiting_booking(browser, venue_id):
    """A booking somebody made and sent a slip for, which the venue has not answered yet."""
    page = browser.new_page(viewport={"width": 390, "height": 844})
    sign_in(page, new_booker(page))
    booking = take_first_free_hour(page, venue_id, tomorrow).json()
    sent = send_slip(page, as_upload("slip.jpg", real_jpeg(), "image/jpeg"))
    if sent.status != 200:
        raise RuntimeError(f"Could not send a slip: {sent.status}")
    page.close()
    return booking


def day_not_counted_yet(page, venue_id):
    """The most recent day this venue has not closed. A day is counted once and stays counted,
    so a script that always closed today would only work the first time it was ever run — and a
    fortnight of them is a fortnight of runs, which a day of development gets through."""
    for back in range(365):
        day = venue_today() - datetime.timedelta(days=back)
        answer = page.request.get(
            f"{BASE}/api/venues/{venue_id}/money?date={day.isoformat()}")
        if answer.status == 200 and answer.json()["closed"] is None:
            return day
    raise RuntimeError("Every day of the last year has been counted already")


with sync_playwright() as p:
    browser = p.chromium.launch()
    venue_id = seeded_venue_id(browser.new_page())
    ensure_bookable(browser, venue_id)

    booking = waiting_booking(browser, venue_id)

    # The counter's money door (the timeline's panel calls it), in parts.
    page = browser.new_page(viewport={"width": 1280, "height": 900})
    sign_in(page, OWNER)
    pay = f"{BASE}/api/venues/{venue_id}/bookings/{booking['id']}/payments"

    # 1. Money is taken in parts, in the form it arrived.
    half = booking["totalBaht"] / 2
    deposit = page.request.post(pay, data={"amountBaht": half, "method": "Cash", "note": None})
    check("a deposit is accepted", deposit.status == 200)
    check("and the rest is what is left", deposit.json()["toPayBaht"] == booking["totalBaht"] - half)

    # 2. Paying the last of it is what answers the booking, not a second button.
    rest = page.request.post(pay, data={"amountBaht": half, "method": "Cash", "note": None})
    check("the rest is accepted", rest.status == 200)
    check("and nothing is left owing", rest.json()["toPayBaht"] == 0)
    row = rest.json()
    check(
        "a booking waiting on a slip is confirmed by the money arriving",
        row["status"] == "Confirmed",
    )
    check("and the same door is not offered again", row["can"]["takeMoney"] is False)

    # 3. The day's money. The page that drew it is gone (2026-10-02); the door it read is not.
    today = page.request.get(f"{BASE}/api/venues/{venue_id}/money").json()
    check(
        "what was taken at the desk is counted as cash",
        today["cashBaht"] >= booking["totalBaht"],
    )
    check(
        "and each amount is listed by itself, because the till is counted against it",
        len(today["cashReceipts"]) >= 2,
    )

    # 4. The count. A day is counted once and stays counted, so this run closes one that never was.
    counting = day_not_counted_yet(page, venue_id)
    before = page.request.get(
        f"{BASE}/api/venues/{venue_id}/money?date={counting.isoformat()}").json()
    # Everything that left the drawer, not only what was handed back: an expense paid in cash is
    # out of the same till, and a run that forgot it would be short by the water bill (US-33).
    expected = (1000 + before["cashBaht"]
                - before["cashRefundedBaht"] - before["cashPaidOutBaht"])

    # Short by exactly one of the day's cash receipts when there is one, so the run exercises
    # the list of rows that would explain it.
    short = before["cashReceipts"][0]["amountBaht"] if before["cashReceipts"] else 100
    closed = page.request.post(
        f"{BASE}/api/venues/{venue_id}/money/closing?date={counting.isoformat()}",
        data={"openingFloatBaht": 1000, "countedCashBaht": expected - short,
              "note": "ขาดร้อยนึง"},
    )
    check("the till is counted", closed.status == 200)
    check(
        "and the server works out what should have been in it",
        closed.json()["expectedCashBaht"] == expected,
    )
    check("and says how far off it was", closed.json()["differenceBaht"] == -short)

    # 5. A count that did not come out even says which rows are exactly that amount (US-26).
    counted = page.request.get(
        f"{BASE}/api/venues/{venue_id}/money?date={counting.isoformat()}").json()
    check("the day reads as counted afterwards", counted["closed"] is not None)
    leads = counted["leads"]
    if before["cashReceipts"]:
        check(
            "a till that is short points at the rows of exactly that amount",
            any(lead["kind"] == "CashTaken" for lead in leads),
        )
    else:
        check("a day with nothing in the till has nothing to point at", leads == [])

    again = page.request.post(
        f"{BASE}/api/venues/{venue_id}/money/closing?date={counting.isoformat()}",
        data={"openingFloatBaht": 1000, "countedCashBaht": expected},
    )
    check("a day is counted once", again.status == 409)
    check("and the second person is told why", again.json().get("code") == "money.already_closed")

    # 6. Money still arrives after the till is shut, and it belongs to the next drawer (US-26).
    today = venue_today()
    was_counted = page.request.get(
        f"{BASE}/api/venues/{venue_id}/money?date={today.isoformat()}").json()
    if was_counted["closed"] is None:
        page.request.post(
            f"{BASE}/api/venues/{venue_id}/money/closing?date={today.isoformat()}",
            data={"openingFloatBaht": 0, "countedCashBaht": was_counted["cashBaht"]},
        )
        was_counted = page.request.get(
            f"{BASE}/api/venues/{venue_id}/money?date={today.isoformat()}").json()

    # A hold is not something the desk may take money for — PRD 6.1 gives a hold two ways out,
    # a slip or the clock — so the booker sends one and the booking is waiting to be checked.
    booker = new_page_for_a_booker(browser)
    late = take_first_free_hour(booker, venue_id, tomorrow).json()
    sent = send_slip(booker, as_upload("slip.jpg", real_jpeg(), "image/jpeg"))
    check("the hour is waiting to be checked", sent.status == 200)

    paid = page.request.post(
        f"{BASE}/api/venues/{venue_id}/bookings/{late['id']}/payments",
        data={"amountBaht": 50, "method": "Cash", "note": None},
    )
    check("money can still be taken after the till is counted", paid.status == 200)

    after = page.request.get(
        f"{BASE}/api/venues/{venue_id}/money?date={today.isoformat()}").json()
    check(
        "and the day that was counted keeps the number it was signed off with",
        after["cashBaht"] == was_counted["cashBaht"],
    )

    next_day = (today + datetime.timedelta(days=1)).isoformat()
    drawer = page.request.get(
        f"{BASE}/api/venues/{venue_id}/money?date={next_day}").json()
    check(
        "while the money itself is in the next day's drawer",
        any(receipt["amountBaht"] == 50 for receipt in drawer["cashReceipts"]),
    )

    browser.close()

check.summarise()
