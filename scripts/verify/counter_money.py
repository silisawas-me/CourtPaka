"""Money as it crosses the counter, and the count at the end of the day (US-26)."""

import datetime

from harness import (
    BASE,
    OWNER,
    Checks,
    as_upload,
    ensure_bookable,
    new_booker,
    open_seeded_venue,
    pick_date,
    real_jpeg,
    seeded_venue_id,
    send_slip,
    sign_in,
    take_first_free_hour,
    venue_today,
)
from playwright.sync_api import expect, sync_playwright

check = Checks(__file__)
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
    so a script that always closed today would only work the first time it was ever run."""
    for back in range(14):
        day = venue_today() - datetime.timedelta(days=back)
        answer = page.request.get(
            f"{BASE}/api/venues/{venue_id}/money?date={day.isoformat()}")
        if answer.status == 200 and answer.json()["closed"] is None:
            return day
    raise RuntimeError("Every day of the last fortnight has been counted already")


with sync_playwright() as p:
    browser = p.chromium.launch()
    venue_id = seeded_venue_id(browser.new_page())
    ensure_bookable(browser, venue_id)

    booking = waiting_booking(browser, venue_id)

    # The counter opens the day it is selling into, which is where the money is taken.
    page = browser.new_page(viewport={"width": 1280, "height": 900})
    sign_in(page, OWNER)
    page.goto(f"{BASE}{open_seeded_venue(page)}")
    page.wait_for_selector("[data-testid=venue-bookings-link]")
    page.click("[data-testid=venue-bookings-link]")
    page.wait_for_selector("[data-testid=day]")
    pick_date(page, tomorrow)
    page.wait_for_selector("[data-testid=day-list]")

    check(
        "a booking that has not been paid for offers to take money",
        page.locator(f"[data-testid=take-{booking['id']}]").count() == 1,
        page,
    )

    # 1. Money is taken in parts, in the form it arrived.
    page.click(f"[data-testid=take-{booking['id']}]")
    amount = page.locator("[data-testid=take-amount]")
    check(
        "and offers what is owed, because that is what usually changes hands",
        amount.input_value() == str(int(booking["totalBaht"])),
        page,
    )

    half = booking["totalBaht"] / 2
    amount.fill(str(half))
    page.click("[data-testid=pay-method-Cash]")
    with page.expect_response(lambda r: r.url.endswith("/payments")) as deposit:
        page.click("[data-testid=take-money]")
    check("a deposit is accepted", deposit.value.status == 200)
    check(
        "and the rest is what is left",
        deposit.value.json()["toPayBaht"] == booking["totalBaht"] - half,
    )
    check(
        "which the row now says out loud",
        page.locator(f"[data-testid=to-pay-{booking['id']}]").is_visible(),
        page,
    )

    # 2. Paying the last of it is what answers the booking, not a second button.
    page.click(f"[data-testid=take-{booking['id']}]")
    with page.expect_response(lambda r: r.url.endswith("/payments")) as rest:
        page.click("[data-testid=take-money]")
    check("the rest is accepted", rest.value.status == 200)
    check("and nothing is left owing", rest.value.json()["toPayBaht"] == 0)

    # The answer is the row itself, so what the counter now sees is what the server says.
    row = rest.value.json()
    check(
        "a booking waiting on a slip is confirmed by the money arriving",
        row["status"] == "Confirmed",
    )
    check("and the same door is not offered again", row["can"]["takeMoney"] is False)

    # 3. The day's money, from the door the venue page offers.
    page.goto(f"{BASE}{open_seeded_venue(page)}")
    page.wait_for_selector("[data-testid=money-link]")
    page.click("[data-testid=money-link]")
    page.wait_for_selector("[data-testid=taken-today]")
    check("the day's money opens from the venue page", "/money" in page.url, page)

    today = page.request.get(f"{BASE}/api/venues/{venue_id}/money").json()
    check(
        "what was taken at the desk is counted as cash",
        today["cashBaht"] >= booking["totalBaht"],
    )
    check(
        "and each amount is listed by itself, because the till is counted against it",
        len(today["cashReceipts"]) >= 2,
        page,
    )

    # The day being looked at lives in the URL, so a count somebody argues about in the morning
    # is a link. The calendar is the only way to change it: the field is read-only because Intl
    # prints Thai dates but cannot read one back.
    yesterday = venue_today() - datetime.timedelta(days=1)
    pick_date(page, yesterday)
    page.wait_for_selector("[data-testid=taken-today]")
    check("the day being looked at is in the URL", yesterday.isoformat() in page.url, page)

    # 4. The count. A day is counted once and stays counted, so this run closes one that never was.
    counting = day_not_counted_yet(page, venue_id)
    page.goto(f"{BASE}/venues/{venue_id}/money?date={counting.isoformat()}")
    page.wait_for_selector("[data-testid=close-day]")

    before = page.request.get(
        f"{BASE}/api/venues/{venue_id}/money?date={counting.isoformat()}").json()
    expected = 1000 + before["cashBaht"] - before["cashRefundedBaht"]

    page.fill("[data-testid=opening-float]", "1000")
    page.fill("[data-testid=counted-cash]", str(expected - 100))
    page.fill("[data-testid=closing-note]", "ขาดร้อยนึง")
    with page.expect_response(lambda r: "/money/closing" in r.url) as closed:
        page.click("[data-testid=close-day]")
    check("the till is counted", closed.value.status == 200)
    check(
        "and the server works out what should have been in it",
        closed.value.json()["expectedCashBaht"] == expected,
    )
    check("and says how far off it was", closed.value.json()["differenceBaht"] == -100)
    expect(page.locator("[data-testid=closed-difference]")).to_be_visible()
    check(
        "which the page shows instead of the form it replaces",
        page.locator("[data-testid=close-day]").count() == 0,
        page,
    )

    again = page.request.post(
        f"{BASE}/api/venues/{venue_id}/money/closing?date={counting.isoformat()}",
        data={"openingFloatBaht": 1000, "countedCashBaht": expected},
    )
    check("a day is counted once", again.status == 409)
    check("and the second person is told why", again.json().get("code") == "money.already_closed")

    browser.close()

check.summarise()
