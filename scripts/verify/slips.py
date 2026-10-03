"""The schedule's slip view (thai-fit T5): the slips waiting, the picture, and the two answers.

Two bookers each hold an hour tomorrow and send a slip through the API (the booker's pages are
gone). The desk opens "ตรวจสลิป" from the schedule's views, sees both — the sooner game on top —
with the picture on screen, confirms one at an amount it read off the slip, and turns the other
away with a reason. The server's answer is checked through the API, not the words on screen.
"""

import datetime

from harness import (
    BASE,
    OWNER,
    STAFF,
    Checks,
    as_upload,
    clear_waiting,
    ensure_bookable,
    new_booker,
    real_jpeg,
    seeded_venue_id,
    send_slip,
    sign_in,
    staff_can,
    take_first_free_hour,
    venue_today,
)
from playwright.sync_api import expect, sync_playwright

check = Checks(__file__)

with sync_playwright() as p:
    browser = p.chromium.launch()
    venue_id = seeded_venue_id(browser.new_page())
    ensure_bookable(browser, venue_id)
    api = f"{BASE}/api/venues/{venue_id}"
    tomorrow = venue_today() + datetime.timedelta(days=1)

    desk = browser.new_page(viewport={"width": 1440, "height": 900})
    sign_in(desk, OWNER)
    clear_waiting(desk, venue_id, tomorrow)

    # Two bookers, each with an hour tomorrow and a slip sent for it.
    sent = []
    for skip in (0, 3):
        booker = browser.new_page()
        sign_in(booker, new_booker(booker))
        booking = take_first_free_hour(booker, venue_id, tomorrow, skip=skip).json()
        answer = send_slip(booker, as_upload("slip.jpg", real_jpeg(), "image/jpeg"))
        if answer.status != 200:
            raise RuntimeError(f"Could not send a slip: {answer.status} {answer.text()}")
        sent.append(booking)
        booker.close()

    queue = desk.request.get(f"{api}/slip-queue").json()
    ids = [one["bookingId"] for one in queue]
    first, second = (one["id"] for one in sent)
    check("the queue puts the sooner game first",
          ids.index(first) < ids.index(second) if first in ids and second in ids else False)

    # The view sits beside the timeline, the board and the list.
    desk.goto(f"{BASE}/venues/{venue_id}/timeline")
    desk.click("[data-testid=view-slips]")
    desk.wait_for_selector(f"[data-testid=slip-{first}]")
    check("the schedule has a slip view, and both slips are in it",
          desk.locator(f"[data-testid=slip-{second}]").count() == 1, desk)

    desk.click(f"[data-testid=slip-{first}]")
    image = desk.locator("[data-testid=slip-image]")
    expect(image).to_be_visible()
    check("the slip the booker sent is on screen",
          image.evaluate("img => img.complete && img.naturalWidth > 0"), desk)
    check("the automatic check waits, empty, for a service",
          desk.locator("[data-testid=slip-auto-check]").count() == 1)

    # Confirmed at what the slip shows, which is less than the price: the rest is owed.
    price = next(one for one in queue if one["bookingId"] == first)["totalBaht"]
    desk.fill("[data-testid=slip-amount]", str(int(price) - 50))
    with desk.expect_response(lambda r: r.url.endswith(f"/slip-queue/{first}/confirm")) as confirmed:
        desk.click("[data-testid=slip-confirm]")
    desk.wait_for_selector("[data-testid=slips-done]")
    booking = next(
        one for one in desk.request.get(f"{api}/bookings?date={tomorrow.isoformat()}").json()
        if one["bookingId"] == first
    )
    check("confirming at the amount on the slip leaves the rest to collect",
          confirmed.value.ok and booking["status"] == "Confirmed"
          and booking["toPayBaht"] == 50, desk)

    # Turned away: a reason, and the money did not arrive.
    desk.click(f"[data-testid=slip-{second}]")
    desk.click("[data-testid=slip-reject-open]")
    desk.fill("[data-testid=slip-reason]", "ไม่พบเงินเข้าบัญชี")
    desk.click("[data-testid=slip-received-no]")
    with desk.expect_response(lambda r: r.url.endswith(f"/slip-queue/{second}/reject")) as rejected:
        desk.click("[data-testid=slip-reject]")
    desk.wait_for_selector(f"[data-testid=slip-{second}]", state="detached")
    check("rejecting with a reason frees the court and empties the row",
          rejected.value.ok and rejected.value.json()["status"] == "Rejected", desk)

    # Staff without VerifySlip have no slip view: a slip is somebody's bank account.
    staff_can(browser, venue_id, "ManageBookings")
    staff = browser.new_page(viewport={"width": 1440, "height": 900})
    sign_in(staff, STAFF)
    staff.goto(f"{BASE}/venues/{venue_id}/timeline")
    staff.wait_for_selector("[data-testid=view-now]")
    check("staff who may not check slips do not see the view",
          staff.locator("[data-testid=view-slips]").count() == 0, staff)
    staff_can(browser, venue_id)

    browser.close()

check.summarise()
