"""A venue's own figures (US-15, owner app PR-5): the revenue section is the panel over the last
fourteen days, and the figures under it are the server's."""

import datetime
import re

from harness import (
    BASE,
    OWNER,
    STAFF,
    Checks,
    ensure_bookable,
    open_seeded_venue,
    seeded_venue_id,
    sign_in,
    venue_today,
)
from playwright.sync_api import expect, sync_playwright

check = Checks(__file__)
tomorrow = venue_today() + datetime.timedelta(days=1)


def dashboard(page, venue_id):
    return page.request.get(f"{BASE}/api/venues/{venue_id}/dashboard").json()


def baht(text: str) -> float:
    """The number in a figure the panel prints, whatever currency mark is around it."""
    return float(re.sub(r"[^0-9.\-]", "", text))


with sync_playwright() as p:
    browser = p.chromium.launch()
    venue_id = seeded_venue_id(browser.new_page())
    ensure_bookable(browser, venue_id)

    page = browser.new_page(viewport={"width": 1280, "height": 900})
    sign_in(page, OWNER)
    page.goto(f"{BASE}{open_seeded_venue(page)}/timeline")
    page.wait_for_selector("[data-testid=nav-dashboard]")
    with page.expect_response(
        lambda r: f"/api/venues/{venue_id}/dashboard" in r.url and r.request.method == "GET"
    ) as answered:
        page.click("[data-testid=nav-dashboard]")
    page.wait_for_selector("[data-testid=revenue-panel]")
    check("the rail has a door to the venue's figures", "/dashboard" in page.url, page)
    check("the revenue page is the design's: no range, no report under it",
          page.locator("[data-testid=range-start]").count() == 0
          and page.locator("[data-testid=revenue-total]").count() == 0
          and page.locator("[data-testid=download-csv]").count() == 0, page)

    # The fortnight to today, in the venue's own time: what the page asks for is what it shows.
    asked = answered.value.url
    today = venue_today()
    check("the page asks for the last fourteen days",
          f"from={(today - datetime.timedelta(days=13)).isoformat()}" in asked
          and f"to={today.isoformat()}" in asked)
    figures = answered.value.json()
    check("a bar a day", page.locator("[data-testid=revenue-chart] .bar").count()
          == len(figures["days"]) == 14, page)

    # The figures on the page are the server's, not the page's own sums.
    court = figures["onlineBaht"] + figures["staffBaht"]
    shop = figures["trade"]["shopBaht"]
    check("court money on the panel is the server's",
          baht(page.get_by_test_id("revenue-kpi-court").inner_text()) == court)
    check("shop money on the panel is the server's",
          baht(page.get_by_test_id("revenue-kpi-shop").inner_text()) == shop)
    check("and the total is the two together",
          baht(page.get_by_test_id("revenue-kpi-total").inner_text()) == court + shop)
    check("each way of paying the server counted has its own line",
          all(page.get_by_test_id(f"revenue-method-{one['method']}").count() == 1
              for one in figures["byMethod"]),
          page)

    # A booking confirmed for tomorrow is money coming, so the advance moves by its price. The
    # panel does not draw it; the door that counts it is still the venue's (US-15).
    before = dashboard(page, venue_id)
    free = page.request.get(
        f"{BASE}/api/venues/{venue_id}/availability?date={tomorrow.isoformat()}").json()
    court_id, hour, price = next(
        (c["courtId"], h["hour"], h["bahtPerHour"])
        for c in free["courts"] for h in c["hours"] if h["status"] == "Free")
    taken = page.request.post(
        f"{BASE}/api/venues/{venue_id}/bookings",
        data={
            "slots": [{"courtId": court_id, "date": tomorrow.isoformat(), "hour": hour}],
            "customerName": "ทดสอบภาพรวม",
            "customerPhone": None,
            "paidBy": "Cash",
        },
    )
    booking_id = taken.json()["bookingId"]
    after = dashboard(page, venue_id)
    check("a confirmed booking ahead adds to the advance",
          after["advanceBaht"] == before["advanceBaht"] + price)

    # Four screenshots the design review asks for: phone and desk, Thai and English.
    page.reload()
    page.wait_for_selector("[data-testid=revenue-panel]")
    check("desk, Thai", True, page)
    page.set_viewport_size({"width": 390, "height": 844})
    check("phone, Thai", True, page)
    width = page.evaluate("document.documentElement.scrollWidth")
    check("nothing on a phone scrolls the page sideways", width <= 390)
    # On a phone the language buttons sit in the menu under the bar.
    if not page.get_by_test_id("language-en").is_visible():
        page.click("[data-testid=open-menu]")
    page.click("[data-testid=language-en]")
    expect(page.locator("html")).to_have_attribute("lang", "en")
    check("phone, English", True, page)
    page.set_viewport_size({"width": 1280, "height": 900})
    check("desk, English", True, page)
    page.click("[data-testid=top-language-th]")
    expect(page.locator("html")).to_have_attribute("lang", "th")

    # A member of staff without ViewReports is turned away at the server, not only by a hidden door.
    staff = browser.new_page()
    sign_in(staff, STAFF)
    permissions = [
        m for m in page.request.get(f"{BASE}/api/venues/{venue_id}/members").json()
        if m["email"] == STAFF][0]["permissions"]
    answer = staff.request.get(f"{BASE}/api/venues/{venue_id}/dashboard")
    expected = 200 if "ViewReports" in permissions else 403
    check(f"staff get {expected} by their permission", answer.status == expected)

    # Leave the day as the other scripts expect it.
    page.request.post(
        f"{BASE}/api/venues/{venue_id}/bookings/{booking_id}/cancel",
        data={"reason": "CustomerRequest", "paymentReceived": None, "note": None},
    )

    browser.close()

check.summarise()
