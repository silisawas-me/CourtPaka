"""The counter managing a booking somebody else made (US-13)."""

import datetime

from harness import (
    BASE,
    OWNER,
    Checks,
    ensure_bookable,
    as_upload,
    clear_waiting,
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
    ensure_bookable(browser, venue_id)

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

    # What the day is waiting for, gathered from the rows themselves (PRD US-25). Which lines are
    # shown depends on what else this shared venue has on the day, so what is checked is the
    # mechanism: there is a list, and pressing a line lands on the booking that line names.
    lines = page.locator("[data-testid=needs-doing] [data-testid^=chore-]")
    check("the day says what it is waiting for", lines.count() > 0, page)

    first = lines.first.get_attribute("data-testid")
    about = first.rsplit("-", 5)[-5:]
    lines.first.click()
    check(
        "and a line leads to the booking it is about",
        page.locator(f"[data-testid=booking-{'-'.join(about)}]").is_visible(),
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

    # 5. The day as one board, and what the counter writes down about a person (US-24, US-25).
    board_page, board_booking = waiting_booking(browser, venue_id, skip=3)
    board_page.close()
    page.request.post(f"{BASE}/api/venues/{venue_id}/slip-queue/{board_booking['id']}/confirm")

    page.reload()
    page.wait_for_selector("[data-testid=day]")
    pick_date(page, tomorrow)
    page.wait_for_selector("[data-testid=day-board]")
    check(
        "the day is drawn as a board of courts and hours",
        page.locator(f"[data-testid=board-block-{board_booking['id']}]").count() == 1,
        page,
    )
    check(
        "and it counts the day beside it",
        page.locator("[data-testid=today-numbers]").is_visible(),
    )

    # The board's own box has to be as wide as the day it draws, not as wide as the window it is
    # read through. They are the same on a desk with room to spare and four times apart on a
    # phone, and everything placed by measuring the box — the clock — is wrong by that much.
    box, rows = page.evaluate(
        """() => {
            const board = document.querySelector('[data-testid=day-board]');
            return [Math.round(board.getBoundingClientRect().width), board.scrollWidth];
        }"""
    )
    check("the board's box is as wide as the day, not as wide as the window", box == rows)

    with page.expect_response(lambda r: r.url.endswith("/confirm-arrival")) as said:
        page.click(f"[data-testid=confirm-arrival-{board_booking['id']}]")
    check("the counter writes down that they are coming", said.value.status == 200)
    check("and the row says so", said.value.json()["arrival"] == "Confirmed")
    check(
        "so the same door is not offered twice",
        page.locator(f"[data-testid=confirm-arrival-{board_booking['id']}]").count() == 0,
        page,
    )

    # Checking in needs their hour to be close, which is the server's rule, not the screen's.
    too_early = page.request.post(
        f"{BASE}/api/venues/{venue_id}/bookings/{board_booking['id']}/check-in")
    check("nobody is checked in a day early", too_early.status == 409)
    check(
        "and the refusal says which rule it is",
        too_early.json().get("code") == "booking.arrival_not_allowed",
    )

    # The clock across today's floor stands where the hour is. It is placed by arithmetic over
    # the board's tracks, and the arithmetic has two ways to come out wrong without looking
    # wrong — a unit on the share, which makes the whole sum invalid and pins the line to the
    # left edge, and a box measured against the window. Read the hour under it instead of the
    # number in it, which is the thing somebody at the counter is actually reading.
    #
    # Only while the venue is open, since there is no clock on the floor otherwise — and the hour
    # is asked here rather than after loading the page, because the page has to be sent back to
    # today (the checks above left it on tomorrow) and that navigation is not worth paying for on
    # a run that cannot assert anything.
    hour_now = datetime.datetime.now().hour
    if 6 <= hour_now < 22:
        page.goto(f"{BASE}/venues/{venue_id}/bookings")
        page.wait_for_selector("[data-testid=board-live]")
        now_hour = f"{hour_now}:00"
        off_by = page.evaluate(
            """(hour) => {
                const at = document.querySelector('[data-testid=board-live]')
                    .getBoundingClientRect();
                // The hour labels, one per hour and never spanning — unlike the cells in a
                // court's row, where a booking of three hours is a single box.
                const label = [...document.querySelectorAll('.board-hour')]
                    .find(h => h.textContent.trim() === hour);
                if (!label) {
                    return null;
                }
                const box = label.getBoundingClientRect();
                // Negative before the hour starts, zero inside it, positive past its end. On the
                // stroke of the hour the line sits in the quarter-rem gap between two hours, so
                // a few pixels either side of the box is still the right hour.
                return Math.round(Math.max(box.left - at.left, at.left - box.right, 0));
            }""",
            now_hour,
        )
        check(
            f"the clock stands on the hour it is ({now_hour})",
            off_by is not None and off_by <= 6,
            page,
        )

    # 6. Someone with the permission to look but not to touch sees no doors.
    looker = browser.new_page()
    sign_in(looker, new_booker(looker))
    refused = looker.request.get(
        f"{BASE}/api/venues/{venue_id}/bookings?date={tomorrow.isoformat()}")
    check("a stranger cannot read the day", refused.status == 403)

    browser.close()

check.summarise()
