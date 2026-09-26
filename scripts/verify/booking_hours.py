"""An evening that runs on, and one that changes court (US-29)."""

import datetime

from harness import (
    BASE,
    OWNER,
    Checks,
    as_upload,
    ensure_bookable,
    new_booker,
    pick_date,
    real_jpeg,
    seeded_venue_id,
    send_slip,
    sign_in,
    take_first_free_hour,
    venue_today,
)
from playwright.sync_api import sync_playwright

check = Checks(__file__)
tomorrow = venue_today() + datetime.timedelta(days=1)


def confirmed_booking(browser, venue_id, staff, skip=0):
    """A booking somebody made, paid for, and the venue has checked."""
    page = browser.new_page(viewport={"width": 390, "height": 844})
    sign_in(page, new_booker(page))
    booking = take_first_free_hour(page, venue_id, tomorrow, skip=skip).json()
    sent = send_slip(page, as_upload("slip.jpg", real_jpeg(), "image/jpeg"))
    if sent.status != 200:
        raise RuntimeError(f"Could not send a slip: {sent.status}")

    confirmed = staff.request.post(
        f"{BASE}/api/venues/{venue_id}/slip-queue/{booking['id']}/confirm")
    if confirmed.status != 200:
        raise RuntimeError(f"Could not confirm a slip: {confirmed.status}")

    page.close()
    return booking


with sync_playwright() as p:
    browser = p.chromium.launch()
    venue_id = seeded_venue_id(browser.new_page())
    ensure_bookable(browser, venue_id)

    staff = browser.new_page()
    sign_in(staff, OWNER)

    # Whatever an earlier run left waiting is not this run's subject.
    for waiting in staff.request.get(f"{BASE}/api/venues/{venue_id}/slip-queue").json():
        staff.request.post(
            f"{BASE}/api/venues/{venue_id}/slip-queue/{waiting['bookingId']}/confirm")

    booking = confirmed_booking(browser, venue_id, staff)

    # 1. The counter reads the day and finds the door the server opened.
    page = browser.new_page(viewport={"width": 1280, "height": 900})
    sign_in(page, OWNER)
    page.goto(f"{BASE}/venues/{venue_id}/bookings")
    page.wait_for_selector("[data-testid=day]")
    pick_date(page, tomorrow)
    page.wait_for_selector(f"[data-testid=booking-{booking['id']}]")

    check(
        "the counter is offered another hour on a booking being played",
        page.locator(f"[data-testid=hours-{booking['id']}]").count() == 1,
        page,
    )

    page.click(f"[data-testid=hours-{booking['id']}]")
    page.wait_for_selector("[data-testid=extend-until]")
    offered = page.locator("[data-testid=asking] button[data-testid^=extend-]")
    check("and the courts that are free for it", offered.count() > 0, page)

    # 2. One more hour, priced at what that hour costs now and owed at the desk.
    before = page.request.get(
        f"{BASE}/api/venues/{venue_id}/bookings?date={tomorrow.isoformat()}").json()
    was = next(row for row in before if row["bookingId"] == booking["id"])

    with page.expect_response(lambda answer: answer.url.endswith("/extend")) as answer:
        offered.first.click()
    check("the hour is added", answer.value.status == 200)

    extended = answer.value.json()
    check(
        "and the booking has an hour more than it had",
        len(extended["slots"]) == len(was["slots"]) + 1,
    )
    check("and the price grew with it", extended["totalBaht"] > was["totalBaht"])
    check("and what was added is owed at the desk (US-26)", extended["can"]["takeMoney"] is True)
    check(
        "which is the amount of the hour that was added",
        extended["toPayBaht"] == extended["totalBaht"] - was["totalBaht"],
    )

    # 3. The same hours on another court, for the same money.
    page.wait_for_selector(f"[data-testid=hours-{booking['id']}]")
    page.click(f"[data-testid=hours-{booking['id']}]")
    page.wait_for_selector("[data-testid=move-hours]")
    elsewhere = page.locator("[data-testid=asking] button[data-testid^=move-]")
    check("a court that has flooded has somewhere to send them", elsewhere.count() > 0, page)

    moved_to = elsewhere.first.get_attribute("data-testid").removeprefix("move-")
    with page.expect_response(lambda answer: answer.url.endswith("/move")) as answer:
        elsewhere.first.click()
    check("the hours move", answer.value.status == 200)

    moved = answer.value.json()
    check(
        "to the court that was pressed",
        {slot["courtId"] for slot in moved["slots"]} == {moved_to},
    )
    check("for the same money", moved["totalBaht"] == extended["totalBaht"])
    check("and the booking is where it was", moved["status"] == extended["status"])

    # 4. The hour they left is on sale again, and the one they are on is not.
    day = page.request.get(
        f"{BASE}/api/venues/{venue_id}/availability?date={tomorrow.isoformat()}").json()
    hours = {
        (court["courtId"], hour["hour"]): hour["status"]
        for court in day["courts"]
        for hour in court["hours"]
    }
    check(
        "the court they are on is taken for every hour they hold",
        all(
            hours[(slot["courtId"], slot["hour"])] == "Booked"
            for slot in moved["slots"]
        ),
    )
    check(
        "and the court they left is free again",
        all(
            hours[(slot["courtId"], slot["hour"])] == "Free"
            for slot in was["slots"]
            if slot["courtId"] != moved_to
        ),
    )

    # 5. An hour somebody else holds is refused, with the courts that are free instead.
    other = confirmed_booking(browser, venue_id, staff, skip=3)
    possible = page.request.get(
        f"{BASE}/api/venues/{venue_id}/bookings/{other['id']}/hours").json()
    wanted = possible["extend"]

    # Somebody takes exactly the hour they would run on into, on exactly their court.
    queue_jumper = browser.new_page()
    sign_in(queue_jumper, new_booker(queue_jumper))
    held = queue_jumper.request.post(
        f"{BASE}/api/bookings",
        data={
            "venueId": venue_id,
            "slots": [
                {
                    "courtId": wanted["sameCourtId"],
                    "date": wanted["date"],
                    "hour": wanted["hour"],
                }
            ],
        },
    )
    check("somebody else takes the hour they would run on into", held.status == 201)

    taken = page.request.post(
        f"{BASE}/api/venues/{venue_id}/bookings/{other['id']}/extend", data={"courtId": None})
    check("so running on into it is refused", taken.status == 409)
    refusal = taken.json()
    check("and the refusal says which rule it is", refusal.get("code") == "booking.hour_taken")
    check(
        "and offers the courts that are free for that hour instead",
        len(refusal.get("courts", [])) > 0
        and all(
            court["courtId"] != wanted["sameCourtId"] for court in refusal["courts"]
        ),
    )

    # 6. Somebody with another permission cannot touch the hours at all.
    stranger = browser.new_page()
    sign_in(stranger, new_booker(stranger))
    refused = stranger.request.get(
        f"{BASE}/api/venues/{venue_id}/bookings/{booking['id']}/hours")
    check("a stranger cannot ask what could be done with them", refused.status == 403)

    browser.close()

check.summarise()
