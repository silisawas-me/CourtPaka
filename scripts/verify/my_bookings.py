"""The booker's own bookings, and letting one go (US-05)."""

import datetime

from harness import (
    BASE,
    OWNER,
    Checks,
    as_upload,
    new_booker,
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


def open_my_bookings(page):
    page.goto(f"{BASE}/bookings")
    page.wait_for_selector("[data-testid=group-upcoming], [data-testid=nothing-yet]")


with sync_playwright() as p:
    browser = p.chromium.launch()
    page = browser.new_page(viewport={"width": 390, "height": 844})

    venue_id = seeded_venue_id(page)

    # 1. Somebody who has taken nothing is told so, and pointed at the courts.
    sign_in(page, new_booker(page))
    open_my_bookings(page)
    check(
        "a booker with nothing sees why the page is empty",
        page.locator("[data-testid=nothing-yet]").count() == 1,
        page,
    )
    check("and is pointed at the courts", page.locator("[data-testid=find-a-court]").count() == 1)

    # 2. A hold appears among what is coming up, and can be let go for nothing.
    held = take_first_free_hour(page, venue_id, tomorrow).json()
    open_my_bookings(page)

    card = f"[data-testid=booking-{held['id']}]"
    check("a booking just taken is what is coming up", page.locator(card).count() == 1, page)
    check(
        "and the hours it holds read as one span",
        ":00" in page.locator(card).inner_text(),
    )

    page.click(f"[data-testid=let-go-{held['id']}]")
    expect(page.locator("[data-testid=refund-note]")).to_be_visible()
    check("letting it go says what comes back before anything is pressed", True, page)

    page.click("[data-testid=keep]")
    expect(page.locator("[data-testid=letting-go]")).to_have_count(0)
    check("answering the other way keeps it", True, page)

    page.click(f"[data-testid=let-go-{held['id']}]")
    with page.expect_response(lambda response: response.url.endswith("/cancel")) as let_go:
        page.click("[data-testid=let-go-confirm]")
    check("and the cancellation is accepted", let_go.value.status == 200)
    check("nothing is owed back on a hold nobody paid for", let_go.value.json()["refundDueBaht"] == 0)

    page.wait_for_selector("[data-testid=group-past]")
    check(
        "the cancelled booking moves to what is behind them",
        page.locator(f"[data-testid=group-past] {card}").count() == 1,
        page,
    )

    # 3. The hour it held is free again the moment it is given up (PRD 6.1).
    day = page.request.get(
        f"{BASE}/api/venues/{venue_id}/availability?date={tomorrow.isoformat()}"
    ).json()
    hour = held["slots"][0]["hour"]
    court = held["slots"][0]["courtId"]
    freed = next(
        cell
        for row in day["courts"]
        if row["courtId"] == court
        for cell in row["hours"]
        if cell["hour"] == hour
    )
    check("the hour it held is on sale again", freed["status"] == "Free")

    # 4. Giving up while the venue is still looking leaves the money unsettled (PRD 6.2).
    waiting = take_first_free_hour(page, venue_id, tomorrow).json()
    sent = send_slip(page, as_upload("slip.jpg", real_jpeg(), "image/jpeg"))
    check("the slip reaches the venue", sent.status == 200)

    open_my_bookings(page)
    page.click(f"[data-testid=let-go-{waiting['id']}]")
    note = page.locator("[data-testid=refund-note]").inner_text()
    check("the booker is told the venue has still to confirm the money", note != "", page)

    with page.expect_response(lambda response: response.url.endswith("/cancel")) as gave_up:
        page.click("[data-testid=let-go-confirm]")
    check(
        "the money is left unsettled rather than written off",
        gave_up.value.json()["paymentState"] == "Unconfirmed",
    )
    expect(page.locator("[data-testid=awaiting-venue]")).to_be_visible()
    check("and the card says so", True, page)

    # 5. A confirmed booking says what its own terms would give back before anything is pressed.
    confirmed_booking = take_first_free_hour(page, venue_id, tomorrow).json()
    send_slip(page, as_upload("slip.jpg", real_jpeg(), "image/jpeg"))

    staff = browser.new_page()
    sign_in(staff, OWNER)
    decided = staff.request.post(
        f"{BASE}/api/venues/{venue_id}/slip-queue/{confirmed_booking['id']}/confirm")
    check("the venue confirms it", decided.status == 200)
    staff.close()

    open_my_bookings(page)
    page.click(f"[data-testid=let-go-{confirmed_booking['id']}]")
    expect(page.locator("[data-testid=refund-note]")).to_be_visible()
    check("a confirmed booking says what its terms would give back", True, page)
    page.click("[data-testid=keep]")

    # 6. Nobody else's bookings are in this list.
    stranger = browser.new_page()
    sign_in(stranger, new_booker(stranger))
    stranger.goto(f"{BASE}/bookings")
    stranger.wait_for_selector("[data-testid=nothing-yet]")
    check(
        "a different booker sees none of them",
        stranger.locator(f"[data-testid=booking-{confirmed_booking['id']}]").count() == 0,
    )

    browser.close()

check.summarise()
