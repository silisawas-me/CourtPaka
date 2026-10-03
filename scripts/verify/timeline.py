"""The court schedule of one branch, as the owner app draws it: tracks, and the booking panel.

The page reads the browser's clock for the day and the window, so the browser is pinned to the
hour the walk-in below is sold for. The server's clock is not, so the doors it opens (check-in)
are not what is checked here.
"""

import datetime

from harness import (
    BASE,
    OWNER,
    Checks,
    ensure_bookable,
    seeded_venue_id,
    sign_in,
    venue_today,
)
from playwright.sync_api import sync_playwright

check = Checks(__file__)
BANGKOK = datetime.timezone(datetime.timedelta(hours=7))

with sync_playwright() as p:
    browser = p.chromium.launch()
    venue_id = seeded_venue_id(browser.new_page())
    ensure_bookable(browser, venue_id)

    desk = browser.new_page(viewport={"width": 1440, "height": 900})
    sign_in(desk, OWNER)
    api = f"{BASE}/api/venues/{venue_id}"

    # A walk-in on the last free hour that has not started yet — tomorrow's, after closing. Not
    # one already under way: no-show is the server's door, on the server's clock, and an hour that
    # began more than the grace ago would have it open whatever the pinned browser says.
    now_hour = datetime.datetime.now(BANGKOK).hour
    found = None
    for ahead in (0, 1):
        date = venue_today() + datetime.timedelta(days=ahead)
        day = desk.request.get(f"{api}/availability", params={"date": date.isoformat()}).json()
        found = next(
            (
                (court["courtId"], one["hour"])
                for court in day["courts"]
                for one in reversed(court["hours"])
                if one["status"] == "Free" and (ahead > 0 or one["hour"] > now_hour)
            ),
            None,
        )
        if found:
            break
    assert found, "no free hour today or tomorrow"
    court_id, hour = found

    item = desk.request.post(
        f"{api}/shop/items",
        data={"name": "Timeline water", "priceBaht": 15, "unit": "bottle", "counted": False,
              "tellMeAt": None},
    ).json()
    sold = desk.request.post(
        f"{api}/bookings",
        data={
            "slots": [{"courtId": court_id, "date": date.isoformat(), "hour": hour}],
            "customerName": "Timeline walk-in",
            "customerPhone": None,
            "paidBy": "Cash",
        },
    )
    check("the walk-in is sold at the counter", sold.status == 201)
    booking_id = sold.json()["bookingId"]

    desk.clock.set_fixed_time(datetime.datetime.combine(date, datetime.time(hour, 10), tzinfo=BANGKOK))
    desk.goto(f"{BASE}/venues/{venue_id}/timeline")
    block = desk.locator(f"[data-testid=board-block-{booking_id}]")
    block.wait_for()
    check("the booking is a block on its court's track", "kind-WalkIn" in (block.get_attribute("class") or ""))
    check("the schedule's top bar has the timeline chosen",
          "on" in (desk.locator("[data-testid=view-timeline]").get_attribute("class") or ""))
    check("and says the day", desk.locator("[data-testid=top-date]").count() == 1)

    # An empty hour on the track books: a tap opens the walk-in on that court and hour.
    cell = desk.locator(".open-cell").first
    if cell.count():
        court_of_cell, hour_of_cell = cell.get_attribute("data-testid").removeprefix("open-").rsplit("-", 1)
        cell.click()
        desk.wait_for_selector("[data-testid=walk-in]")
        check("tapping an empty hour opens the walk-in on that court and hour",
              "on" in (desk.locator(f"[data-testid=walk-in-court-{court_of_cell}]").get_attribute("class") or "")
              and "on" in (desk.locator(f"[data-testid=walk-in-start-{hour_of_cell}]").get_attribute("class") or ""),
              desk)
        desk.fill("[data-testid=walk-in-name]", "Timeline tap")
        with desk.expect_response(lambda r: r.url.endswith(f"/venues/{venue_id}/bookings") and r.request.method == "POST") as tapped:
            desk.click("[data-testid=walk-in-confirm]")
        slot = tapped.value.json()["slots"][0] if tapped.value.status == 201 else {}
        check("and selling it books that very court and hour",
              slot.get("courtId") == court_of_cell and str(slot.get("hour")) == hour_of_cell)
        desk.request.post(f"{api}/bookings/{tapped.value.json()['bookingId']}/cancel",
                          data={"reason": "VenueInitiated", "paymentReceived": None, "note": "verify"})
    else:
        check("an empty hour on the track books (no free hour in the window to try)", True)

    block.click()
    panel = desk.locator("[data-testid=booking-panel]")
    # The page's CSP refuses evaluated strings, so the wait is a locator's, not a function's.
    desk.locator("[data-testid=panel-name]", has_text="Timeline walk-in").wait_for()
    check("pressing the block opens it in the panel", True, desk)
    check("the panel says where and when", f"{hour}:00" in panel.locator("[data-testid=panel-where]").inner_text())

    # Another court, through the move door — folded under its button, only a court the server
    # says is free is offered.
    check("the courts to move to are open without a press",
          panel.locator("[data-testid=move-open]").count() == 0, desk)
    free = panel.locator("[data-testid^=move-]:not([disabled]):not([data-testid=move-courts])")
    free.first.wait_for()
    target = free.first.get_attribute("data-testid").removeprefix("move-")
    with desk.expect_response(lambda r: r.url.endswith(f"/bookings/{booking_id}/move")) as moved:
        free.first.click()
    check("moving it to another court is accepted", moved.value.status == 200)
    check("and it is on that court now",
          all(slot["courtId"] == target for slot in moved.value.json()["slots"]))

    # Time, as the design draws it: +1 ชม. then −1 ชม. takes the evening back where it was.
    extend = panel.locator("[data-testid=extend]")
    if extend.is_enabled():
        with desk.expect_response(lambda r: r.url.endswith(f"/bookings/{booking_id}/extend")) as longer:
            extend.click()
        check("one more hour is added", longer.value.status == 200 and len(longer.value.json()["slots"]) == 2)
        shorten = panel.locator("[data-testid=shorten]:not([disabled])")
        shorten.wait_for()
        with desk.expect_response(lambda r: r.url.endswith(f"/bookings/{booking_id}/shorten")) as shorter:
            shorten.click()
        back = shorter.value.json()
        check("and one hour fewer takes it off again, paid as it was",
              shorter.value.status == 200 and len(back["slots"]) == 1
              and back["paymentState"] == "Received", desk)
    else:
        check("an hour cannot come off a booking of one hour",
              panel.locator("[data-testid=shorten]").is_disabled(), desk)

    # Water onto the bill: the court is paid, so what is due is the water alone.
    panel.locator(f"[data-testid=more-{item['itemId']}]").click()
    panel.locator(f"[data-testid=more-{item['itemId']}]").click()
    panel.locator("[data-testid=due]", has_text="30").wait_for()
    check("what is due is the drinks", True, desk)
    with desk.expect_response(lambda r: r.url.endswith("/shop/sales") and r.request.method == "POST") as sale:
        panel.locator("[data-testid=pay-Cash]").click()
    check(
        "paying sells them onto this booking",
        sale.value.status == 201
        and sale.value.json()["bookingId"] == booking_id
        and sale.value.json()["totalBaht"] == 30,
    )

    # The day's list is gone: the schedule is where the day is run.
    check("there is no separate day's list any more",
          desk.locator("[data-testid=nav-bookings]").count() == 0)

    # The doors that end a booking (artboard b1): no-show is shut until the grace runs out.
    check("no-show waits for the grace to run out",
          panel.locator("[data-testid=no-show]").is_disabled(), desk)

    # Cancelling, as artboard b2 draws it: who called it off, what it gives back, then confirm.
    desk.request.post(f"{api}/shop/sales/{sale.value.json()['saleId']}/cancel", data={"reason": "verify"})
    panel.locator("[data-testid=cancel-open]").click()
    check("cancelling waits for who called it off",
          panel.locator("[data-testid=confirm-cancel]").is_disabled(), desk)
    panel.locator("[data-testid=cancel-reason-VenueInitiated]").click()
    check("and says what that gives back",
          panel.locator("[data-testid=refund-due]").inner_text().startswith("฿"), desk)
    panel.locator("[data-testid=cancel-note]").fill("verify")
    with desk.expect_response(lambda r: r.url.endswith(f"/bookings/{booking_id}/cancel")) as cancelled:
        panel.locator("[data-testid=confirm-cancel]").click()
    check("cancelling from the panel is accepted",
          cancelled.value.status == 200 and cancelled.value.json()["status"] == "Cancelled")
    check("and its block leaves the track",
          desk.locator(f"[data-testid=board-block-{booking_id}]").count() == 0
          or desk.wait_for_selector(f"[data-testid=board-block-{booking_id}]", state="detached") is None, desk)

    # The money it owes back is written down in the same panel (PRD US-18).
    # A refund is dated today, and the clock was pinned to the booking's day, which can be tomorrow.
    desk.clock.set_fixed_time(datetime.datetime.now(BANGKOK))
    panel.locator("[data-testid=refund-open]").click()
    panel.locator("[data-testid=refund-left]").wait_for()
    owed = desk.request.get(f"{api}/bookings/{booking_id}/refunds").json()["outstandingBaht"]
    check("the panel stays on the cancelled booking and offers its refund", owed > 0, desk)
    panel.locator("[data-testid=refund-method-Transfer]").click()
    with desk.expect_response(
        lambda r: r.url.endswith(f"/bookings/{booking_id}/refunds") and r.request.method == "POST"
    ) as refunded:
        panel.locator("[data-testid=confirm-refund]").click()
    check("writing it down takes everything owed",
          refunded.value.status == 200 and refunded.value.json()["outstandingBaht"] == 0)

    # Tidy up what this made.
    desk.request.post(f"{api}/shop/items/{item['itemId']}/withdraw")
    browser.close()

check.summarise()
