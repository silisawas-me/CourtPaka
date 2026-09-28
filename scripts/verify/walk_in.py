"""Selling a court to somebody at the counter from the top bar (owner app PR-3).

A walk-in is somebody here now, so the modal sells today from the hour the clock is in. The
seeded venue closes at 22:00, which would leave nothing to sell whenever this runs late — so the
venue is opened around the clock for the length of the script, and put back afterwards by
`ensure_bookable()`, the state every other script expects.
"""

from harness import (
    BASE,
    DAYS,
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

    desk = browser.new_page(viewport={"width": 1440, "height": 900})
    sign_in(desk, OWNER)
    api = f"{BASE}/api/venues/{venue_id}"
    # Priced first: the server refuses open hours that have no price.
    priced = desk.request.put(
        f"{api}/prices",
        data={"bands": [{"day": day, "fromHour": 0, "toHour": 24, "bahtPerHour": 200} for day in DAYS]},
    )
    opened = desk.request.put(
        f"{api}/opening-hours",
        data={
            "effectiveFrom": venue_today().isoformat(),
            "days": [{"day": day, "opensHour": 0, "closesHour": 24} for day in DAYS],
        },
    )
    check("the venue is opened around the clock for the script", opened.ok and priced.ok)

    try:
        desk.goto(f"{BASE}/venues/{venue_id}/bookings")
        desk.wait_for_selector("[data-testid=day-board]")
        desk.click("[data-testid=open-walk-in]")
        desk.wait_for_selector("[data-testid=walk-in]")
        check("the top bar opens the walk-in", True, desk)

        # The first start is the hour the clock is in, chosen already, and says so.
        hour = desk.evaluate(
            "() => Number(new Intl.DateTimeFormat('en-GB', {hour: 'numeric', hourCycle: 'h23', timeZone: 'Asia/Bangkok'}).format(new Date()))"
        )
        start = desk.locator(f"[data-testid=walk-in-start-{hour}]")
        check("it starts at the hour the clock is in, chosen",
              start.count() == 1 and "on" in (start.get_attribute("class") or ""), desk)

        free = desk.locator("[data-testid^=walk-in-court-]:not([disabled])").first
        court_test_id = free.get_attribute("data-testid")
        free.click()
        desk.fill("[data-testid=walk-in-name]", "Walk-in verify")
        desk.click("[data-testid=walk-in-paid-Cash]")

        with desk.expect_response(
            lambda r: r.url.endswith(f"/api/venues/{venue_id}/bookings") and r.request.method == "POST"
        ) as answer:
            desk.click("[data-testid=walk-in-confirm]")
        sold = answer.value
        check("the walk-in is sold as the counter sells", sold.status == 201, desk)
        booking_id = sold.json()["bookingId"]

        desk.wait_for_selector("[data-testid=walk-in]", state="detached")
        block = desk.locator(f"[data-testid=board-block-{booking_id}]")
        block.wait_for(timeout=10_000)
        check("and is on the timeline straight away, as a walk-in",
              "kind-WalkIn" in (block.get_attribute("class") or ""), desk)

        # The court just sold is not offered again for that hour.
        desk.click("[data-testid=open-walk-in]")
        desk.wait_for_selector("[data-testid=walk-in]")
        check("the court just sold is not offered again for that hour",
              desk.locator(f"[data-testid={court_test_id}]").is_disabled())
        desk.click("[data-testid=walk-in-close]")

        desk.request.post(
            f"{api}/bookings/{booking_id}/cancel",
            data={"reason": "VenueInitiated", "note": "verify"},
        )
    finally:
        ensure_bookable(browser, venue_id)

    # Every venue at once offers the walk-in too, as the design does: it sells at the first branch.
    desk.goto(f"{BASE}/venues")
    desk.wait_for_selector("[data-testid=owner-top]")
    check("every venue at once offers the walk-in as well",
          desk.locator("[data-testid=open-walk-in]").count() == 1)

    browser.close()

check.summarise()
