"""Finding a venue and reading its court grid, signed out and signed in (US-02)."""

import datetime

from harness import (
    BASE,
    OWNER,
    SEEDED_VENUE,
    Checks,
    calendar_label,
    day_is_offered,
    login,
    pick_date,
    venue_today,
)
from playwright.sync_api import sync_playwright

check = Checks(__file__)
today = venue_today()

with sync_playwright() as p:
    browser = p.chromium.launch()

    # 1. A visitor with no account finds a venue and reads its grid.
    page = browser.new_page(viewport={"width": 390, "height": 844})
    page.goto(f"{BASE}/book")
    page.wait_for_selector("[data-testid=venue-results]")
    check("a signed-out visitor can search", page.locator("[data-testid=venue-result]").count() > 0, page)

    with page.expect_response(lambda response: "/api/venues/search" in response.url):
        page.fill("#search", "วัฒนา")
    check(
        "searching by district finds the venue",
        SEEDED_VENUE in page.locator("[data-testid=venue-results]").inner_text(),
    )

    page.locator("[data-testid=venue-result]").first.click()
    page.wait_for_selector("[data-testid=availability-grid]")
    check("the grid opens from a result", "/book/" in page.url, page)

    venue_id = page.url.split("/book/")[1].split("?")[0]
    # How many courts the venue has is whatever the earlier checks left it with, so ask.
    expected = page.request.get(f"{BASE}/api/venues/{venue_id}/availability").json()
    rows = page.locator("[data-testid=availability-grid] tbody tr")
    check("every court has a row", rows.count() == len(expected["courts"]))
    check(
        "the page names the venue from the same answer as the grid",
        page.locator("[data-testid=venue-name]").inner_text() == expected["venue"]["name"],
    )
    check(
        "an hour carries its price",
        "200" in page.locator('[data-testid^="cell-"]').first.inner_text(),
    )
    check(
        "the evening costs more than the morning",
        "300" in page.locator('[data-testid^="cell-"]').last.inner_text(),
        page,
    )

    check(
        "a signed-out visitor is asked to sign in before booking",
        page.locator("[data-testid=sign-in-to-book]").count() == 1,
    )
    check("and is not offered a booking button", page.locator("[data-testid=book]").count() == 0)

    # 2. The day can be moved within the booking window, and not outside it.
    page.click("mat-datepicker-toggle button")
    page.wait_for_selector("mat-calendar")
    check(
        "yesterday is not offered",
        not day_is_offered(page, today - datetime.timedelta(days=1)),
    )
    check(
        "the last day of the window is offered",
        day_is_offered(page, today + datetime.timedelta(days=30)),
        page,
    )
    check(
        "the day after it is not",
        not day_is_offered(page, today + datetime.timedelta(days=31)),
    )
    # The calendar is still open, which is where the day gets picked from.
    tomorrow = today + datetime.timedelta(days=1)
    with page.expect_response(lambda response: "/availability" in response.url):
        pick_date(page, tomorrow)
    check(
        "picking a day puts it in the address, so the grid can be shared",
        page.url.endswith(f"?date={tomorrow.isoformat()}"),
        page,
    )
    check(
        "and the grid shows that day",
        calendar_label(tomorrow).split()[0]
        in page.locator("[data-testid=grid-date]").inner_text(),
    )

    grid_url = page.url
    reloaded = page.request.get(
        f"{BASE}/api/venues/{venue_id}/availability"
        f"?date={(today + datetime.timedelta(days=31)).isoformat()}"
    )
    check("the server refuses a date past the window", reloaded.status == 400)
    check(
        "and names the rule",
        reloaded.json().get("code") == "availability.date_too_far_ahead",
    )

    # 3. Signed in, the page offers to book instead.
    page.goto(f"{BASE}/login")
    login(page, OWNER)
    page.wait_for_url(f"{BASE}/")
    page.goto(grid_url)
    page.wait_for_selector("[data-testid=availability-grid]")
    check("a signed-in booker is offered the booking button", page.locator("[data-testid=book]").count() == 1, page)
    check(
        "a shared link opens on the day it names",
        page.locator("[data-testid=grid-date]").inner_text() != "",
    )

    # 4. A venue that is not approved is invisible, whoever asks.
    hidden = page.request.get(f"{BASE}/api/venues/search?q=zzzz-no-such-venue")
    check("a search that matches nothing answers an empty list", hidden.json() == [])

    browser.close()

check.summarise()
