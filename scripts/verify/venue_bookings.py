"""The counter managing a booking somebody else made (US-13)."""

import datetime

from harness import (
    BASE,
    OWNER,
    Checks,
    as_upload,
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


def waiting_booking(browser, venue_id, skip=0):
    """A booking somebody made and paid for, waiting for the venue."""
    page = browser.new_page(viewport={"width": 390, "height": 844})
    sign_in(page, new_booker(page))
    booking = take_first_free_hour(page, venue_id, tomorrow, skip=skip).json()
    sent = send_slip(page, as_upload("slip.jpg", real_jpeg(), "image/jpeg"))
    if sent.status != 200:
        raise RuntimeError(f"Could not send a slip: {sent.status}")
    return page, booking


with sync_playwright() as p:
    browser = p.chromium.launch()
    venue_id = seeded_venue_id(browser.new_page())

    # Whatever an earlier run left waiting is not this run's subject.
    staff = browser.new_page()
    sign_in(staff, OWNER)
    for waiting in staff.request.get(f"{BASE}/api/venues/{venue_id}/slip-queue").json():
        staff.request.post(
            f"{BASE}/api/venues/{venue_id}/slip-queue/{waiting['bookingId']}/confirm")
    staff.close()

    turned_away_page, turned_away = waiting_booking(browser, venue_id)
    given_up_page, given_up = waiting_booking(browser, venue_id, skip=1)

    # The booker lets the second one go while the venue is still checking it (US-05).
    gave_up = given_up_page.request.post(f"{BASE}/api/bookings/{given_up['id']}/cancel")
    check("a booker can give up while the venue is still checking", gave_up.status == 200)
    check("and the money is left unsettled", gave_up.json()["paymentState"] == "Unconfirmed")
    given_up_page.close()

    # The counter opens the day from the venue page.
    page = browser.new_page(viewport={"width": 1280, "height": 900})
    sign_in(page, OWNER)
    page.goto(f"{BASE}{open_seeded_venue(page)}")
    page.wait_for_selector("[data-testid=venue-bookings-link]")
    page.click("[data-testid=venue-bookings-link]")
    page.wait_for_selector("[data-testid=day]")

    # Today has nothing in it; the day being looked at is the venue's own today.
    expect(page.locator("[data-testid=nothing-today], [data-testid=day-list]")).to_be_visible()
    check("the day opens from the venue page", "/bookings" in page.url, page)

    # Move to tomorrow through the calendar, which is the only way in: the field is read-only
    # because Intl prints Thai dates but cannot read one back.
    pick_date(page, tomorrow)
    page.wait_for_selector("[data-testid=day-list]")

    # By id, not by counting: earlier runs left their own bookings on this day, and what this
    # one is about is its two.
    check(
        "the bookings this run made are both on the day",
        page.locator(f"[data-testid=booking-{turned_away['id']}]").count() == 1
        and page.locator(f"[data-testid=booking-{given_up['id']}]").count() == 1,
        page,
    )

    check(
        "the one the booker gave up says the money is unsettled",
        page.locator(f"[data-testid=unsettled-{given_up['id']}]").count() == 1,
        page,
    )

    # 1. Settling it turns the stored share into an amount (6.2).
    page.click(f"[data-testid=settle-{given_up['id']}]")
    expect(page.locator("[data-testid=settle-note]")).to_be_visible()
    with page.expect_response(lambda r: r.url.endswith("/settle-payment")) as answered:
        page.click("[data-testid=settle-received]")
    check("saying the money arrived is accepted", answered.value.status == 200)
    check(
        "and what was owed becomes an amount",
        answered.value.json()["refundDueBaht"] == answered.value.json()["totalBaht"],
    )
    expect(page.locator(f"[data-testid=refund-due-{given_up['id']}]")).to_be_visible()
    check("which the row now says out loud", True, page)

    # 2. Turning one away asks about the money, because the venue is who can see it.
    page.click(f"[data-testid=cancel-{turned_away['id']}]")
    check(
        "a booking still being checked is asked about the money, not for a reason",
        page.locator("[data-testid=reason-CustomerRequest]").count() == 0,
        page,
    )
    with page.expect_response(lambda r: r.url.endswith("/cancel")) as cancelled:
        page.click("[data-testid=cancel-not-received]")
    check("the cancellation is accepted", cancelled.value.status == 200)
    check("and nothing is owed back", cancelled.value.json()["refundDueBaht"] == 0)

    # 3. The hours it held are on sale again (6.1).
    day = page.request.get(
        f"{BASE}/api/venues/{venue_id}/availability?date={tomorrow.isoformat()}"
    ).json()
    hour = turned_away["slots"][0]
    freed = next(
        cell
        for row in day["courts"]
        if row["courtId"] == hour["courtId"]
        for cell in row["hours"]
        if cell["hour"] == hour["hour"]
    )
    check("the hours it held are on sale again", freed["status"] == "Free")

    # 4. A confirmed booking is asked why, and told what each answer gives back.
    confirmed_page, confirmed = waiting_booking(browser, venue_id, skip=2)
    confirmed_page.close()
    decided = page.request.post(
        f"{BASE}/api/venues/{venue_id}/slip-queue/{confirmed['id']}/confirm")
    check("the venue confirms a payment", decided.status == 200)

    # Reloading lands on the venue's today again, so the day has to be chosen once more.
    page.reload()
    page.wait_for_selector("[data-testid=day]")
    pick_date(page, tomorrow)
    page.wait_for_selector("[data-testid=day-list]")
    page.click(f"[data-testid=cancel-{confirmed['id']}]")
    # Each answer carries the amount it settles, in baht, from the server that will decide it.
    expect(page.locator("[data-testid=gives-CustomerRequest]")).to_be_visible()
    expect(page.locator("[data-testid=gives-VenueInitiated]")).to_be_visible()
    check("a paid booking says what each reason would give back", True, page)

    page.click("[data-testid=cancel-confirm]")
    expect(page.locator("[data-testid=decide-error]")).to_be_visible()
    check("and will not be cancelled without one being chosen", True, page)

    page.click("[data-testid=reason-VenueInitiated]")
    with page.expect_response(lambda r: r.url.endswith("/cancel")) as venue_cancelled:
        page.click("[data-testid=cancel-confirm]")
    check("cancelling as the venue is accepted", venue_cancelled.value.status == 200)
    check(
        "and gives the whole amount back",
        venue_cancelled.value.json()["refundDueBaht"]
        == venue_cancelled.value.json()["totalBaht"],
    )

    # 5. Someone with the permission to look but not to touch sees no doors.
    looker = browser.new_page()
    sign_in(looker, new_booker(looker))
    refused = looker.request.get(
        f"{BASE}/api/venues/{venue_id}/bookings?date={tomorrow.isoformat()}")
    check("a stranger cannot read the day", refused.status == 403)

    browser.close()

check.summarise()
