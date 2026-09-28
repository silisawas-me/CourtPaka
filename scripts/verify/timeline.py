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

    # A walk-in on the last free hour that has not ended — tomorrow's, after closing.
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
                if one["status"] == "Free" and (ahead > 0 or one["hour"] + 1 > now_hour)
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

    block.click()
    panel = desk.locator("[data-testid=booking-panel]")
    # The page's CSP refuses evaluated strings, so the wait is a locator's, not a function's.
    desk.locator("[data-testid=panel-name]", has_text="Timeline walk-in").wait_for()
    check("pressing the block opens it in the panel", True, desk)
    check("the panel says where and when", f"{hour}:00" in panel.locator("[data-testid=panel-where]").inner_text())

    # Another court, through the move door — only a court the server says is free is offered.
    free = panel.locator("[data-testid^=move-]:not([disabled])")
    free.first.wait_for()
    target = free.first.get_attribute("data-testid").removeprefix("move-")
    with desk.expect_response(lambda r: r.url.endswith(f"/bookings/{booking_id}/move")) as moved:
        free.first.click()
    check("moving it to another court is accepted", moved.value.status == 200)
    check("and it is on that court now",
          all(slot["courtId"] == target for slot in moved.value.json()["slots"]))

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

    # The list with every other door is one press away.
    check("the day's list is in the quieter group",
          desk.locator("[data-testid=nav-bookings]").count() == 1)

    # Tidy up what this made.
    desk.request.post(f"{api}/shop/sales/{sale.value.json()['saleId']}/cancel", data={"reason": "verify"})
    desk.request.post(f"{api}/shop/items/{item['itemId']}/withdraw")
    desk.request.post(
        f"{api}/bookings/{booking_id}/cancel",
        data={"reason": "VenueInitiated", "note": "verify"},
    )
    browser.close()

check.summarise()
