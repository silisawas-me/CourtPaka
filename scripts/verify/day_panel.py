"""The booking pressed on the floor opens beside it, and sells onto the bill (badPaka 2a)."""

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

with sync_playwright() as p:
    browser = p.chromium.launch()
    venue_id = seeded_venue_id(browser.new_page())
    ensure_bookable(browser, venue_id)
    today = venue_today().isoformat()

    desk = browser.new_page(viewport={"width": 1280, "height": 900})
    sign_in(desk, OWNER)
    api = f"{BASE}/api/venues/{venue_id}"

    # The setup, through the API: something on the shop's board, and a walk-in on the floor.
    item = desk.request.post(
        f"{api}/shop/items",
        data={"name": "Panel shuttles", "priceBaht": 90, "unit": "tube", "counted": False,
              "tellMeAt": None},
    ).json()
    day = desk.request.get(f"{api}/availability", params={"date": today}).json()
    free = next(
        (court["courtId"], hour["hour"])
        for court in day["courts"]
        for hour in reversed(court["hours"])
        if hour["status"] == "Free"
    )
    sold = desk.request.post(
        f"{api}/bookings",
        data={
            "slots": [{"courtId": free[0], "date": today, "hour": free[1]}],
            "customerName": "Panel walk-in",
            "customerPhone": None,
            "paidBy": "Cash",
        },
    )
    check("the walk-in is sold at the counter", sold.status == 201)
    booking_id = sold.json()["bookingId"]

    desk.goto(f"{BASE}/venues/{venue_id}/bookings?date={today}")
    block = desk.locator(f"[data-testid=board-block-{booking_id}]")
    block.wait_for()
    check("a walk-in is drawn in the walk-in's colours", "kind-WalkIn" in (block.get_attribute("class") or ""))
    check("the floor has a key to its colours", desk.locator("[data-testid=board-key]").is_visible())

    block.click()
    panel = desk.locator("[data-testid=booking-panel]")
    panel.wait_for()
    check("pressing the block opens that booking beside the floor", panel.is_visible(), desk)
    check(
        "the booking is in the panel, not twice on the page",
        desk.locator(f"[data-testid=booking-{booking_id}]").count() == 1
        and panel.locator(f"[data-testid=booking-{booking_id}]").count() == 1,
    )
    check("the panel sits beside the floor, not under it",
          panel.bounding_box()["x"] > desk.locator("[data-testid=day-board]").bounding_box()["x"] + 300)

    panel.locator("[data-testid=sell-open]").click()
    panel.locator(f"[data-testid=sell-more-{item['itemId']}]").click()
    # Read off the server's own answer, not the day's list of sales: an earlier script may have
    # counted the till today already, and then this sale belongs to tomorrow's till (US-26).
    with desk.expect_response(lambda r: r.url.endswith("/shop/sales") and r.request.method == "POST") as answer:
        panel.locator("[data-testid=sell-confirm]").click()
    sale = answer.value.json()
    panel.locator("[data-testid=sell-done]").wait_for()
    check(
        "the sale is written with the booking it was sold to",
        answer.value.status == 201 and sale["bookingId"] == booking_id and sale["totalBaht"] == 90,
    )

    panel.locator("[data-testid=panel-close]").click()
    returned = desk.locator(f"[data-testid=day-list] [data-testid=booking-{booking_id}]")
    try:
        returned.wait_for(timeout=5_000)
    except Exception:
        pass
    check("closing the panel puts the row back in the list", returned.count() == 1, desk)

    # A phone has no room beside the floor: the panel rises from the foot instead.
    phone = browser.new_page(viewport={"width": 390, "height": 844})
    sign_in(phone, OWNER)
    phone.goto(f"{BASE}/venues/{venue_id}/bookings?date={today}")
    phone.locator(f"[data-testid=board-block-{booking_id}]").click()
    sheet = phone.locator("[data-testid=booking-panel]")
    sheet.wait_for()
    box = sheet.bounding_box()
    check("on a phone the panel is a sheet at the foot of the screen",
          box["y"] > 844 * 0.25 and box["width"] >= 380, phone)

    # Tidy up what this made, so the next script finds the venue as it expects.
    desk.request.post(f"{api}/shop/sales/{sale['saleId']}/cancel", data={"reason": "verify"})
    desk.request.post(f"{api}/shop/items/{item['itemId']}/withdraw")
    desk.request.post(
        f"{api}/bookings/{booking_id}/cancel",
        data={"reason": "VenueInitiated", "note": "verify"},
    )

    browser.close()

check.summarise()
