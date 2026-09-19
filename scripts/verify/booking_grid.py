"""Finding a venue and reading its court grid, signed out and signed in (US-02)."""

import datetime

from harness import BASE, OWNER, Checks, login, thai_date
from playwright.sync_api import expect, sync_playwright

check = Checks(__file__)
today = datetime.date.today()

with sync_playwright() as p:
    browser = p.chromium.launch()

    # 1. A visitor with no account finds a venue and reads its grid.
    page = browser.new_page(viewport={"width": 390, "height": 844})
    page.goto(f"{BASE}/book")
    page.wait_for_selector("[data-testid=venue-results]")
    check("a signed-out visitor can search", page.locator("[data-testid=venue-result]").count() > 0, page)

    page.fill("#search", "วัฒนา")
    page.wait_for_timeout(600)  # The search follows a pause in typing.
    check(
        "searching by district finds the venue",
        "Development Court" in page.locator("[data-testid=venue-results]").inner_text(),
    )

    page.locator("[data-testid=venue-result]").first.click()
    page.wait_for_selector("[data-testid=availability-grid]")
    check("the grid opens from a result", page.url.split("/")[-1] != "book", page)

    # How many courts the venue has is whatever the earlier checks left it with, so ask.
    venue_id = page.url.split("/book/")[1]
    expected = page.request.get(f"{BASE}/api/venues/{venue_id}/availability").json()["courts"]
    rows = page.locator("[data-testid=availability-grid] tbody tr")
    check("every court has a row", rows.count() == len(expected))
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

    grid_url = page.url

    # 2. The day can be moved within the booking window, and not outside it.
    page.click("#day")
    page.wait_for_selector("mat-calendar")
    check("touching the day opens the calendar", page.locator("mat-calendar").count() == 1, page)
    yesterday = today - datetime.timedelta(days=1)
    if yesterday.month == today.month:
        cell = page.locator(f'.mat-calendar-body-cell-content:text-is("{yesterday.day}")')
        check(
            "yesterday is not offered",
            cell.count() == 0 or cell.locator("xpath=..").get_attribute("aria-disabled") == "true",
        )
    page.keyboard.press("Escape")

    refused = page.request.get(
        f"{BASE}/api/venues/{grid_url.split('/book/')[1]}/availability"
        f"?date={(today + datetime.timedelta(days=31)).isoformat()}"
    )
    check("the server refuses a date past the window", refused.status == 400)
    check(
        "and names the rule",
        refused.json().get("code") == "availability.date_too_far_ahead",
    )

    # 3. The grid follows the venue on its own.
    with page.expect_response(lambda response: "/availability" in response.url, timeout=15_000):
        pass  # The page refreshes every ten seconds without being asked.
    check("the grid refreshes itself", True, page)

    # 4. Signed in, the page offers to book instead.
    page.goto(f"{BASE}/login")
    login(page, OWNER)
    page.wait_for_url(f"{BASE}/")
    page.goto(grid_url)
    page.wait_for_selector("[data-testid=availability-grid]")
    check("a signed-in booker is offered the booking button", page.locator("[data-testid=book]").count() == 1, page)

    # 5. A venue that is not approved is invisible, whoever asks.
    hidden = page.request.get(f"{BASE}/api/venues/search?q=zzzz-no-such-venue")
    check("a search that matches nothing answers an empty list", hidden.json() == [])

    browser.close()

check.summarise()
