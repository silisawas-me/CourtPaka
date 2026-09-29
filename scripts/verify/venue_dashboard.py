"""A venue's own figures (US-15): what it kept, what is coming, how much of the courts was used."""

import codecs
import datetime
import pathlib

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


with sync_playwright() as p:
    browser = p.chromium.launch()
    venue_id = seeded_venue_id(browser.new_page())
    ensure_bookable(browser, venue_id)

    page = browser.new_page(viewport={"width": 1280, "height": 900}, accept_downloads=True)
    sign_in(page, OWNER)
    page.goto(f"{BASE}{open_seeded_venue(page)}")
    page.click("[data-testid=dashboard-link]")
    page.wait_for_selector("[data-testid=revenue-panel]")
    check("the venue page has a door to its figures", True, page)
    check("the revenue page is the design's: no range, no report under it",
          page.locator("[data-testid=range-start]").count() == 0
          and page.locator("[data-testid=revenue-total]").count() == 0, page)
    # Everything else about the figures is the report under "อื่น ๆ".
    page.goto(f"{BASE}/venues/{venue_id}/report")
    page.wait_for_selector("[data-testid=revenue-total]")

    before = dashboard(page, venue_id)
    days = page.locator("[data-testid=days] tbody tr")
    check("with no range it shows this month, a row per day", days.count() == len(before["days"]))

    # The figures on the page are the server's, not the page's own sums.
    shown_advance = page.get_by_test_id("advance").inner_text().replace(",", "")
    check("the advance on the page is the server's", float(shown_advance) == before["advanceBaht"])

    # A booking confirmed for tomorrow is money coming, so the advance moves by its price.
    free = page.request.get(
        f"{BASE}/api/venues/{venue_id}/availability?date={tomorrow.isoformat()}").json()
    court, hour, price = next(
        (c["courtId"], h["hour"], h["bahtPerHour"])
        for c in free["courts"] for h in c["hours"] if h["status"] == "Free")
    taken = page.request.post(
        f"{BASE}/api/venues/{venue_id}/bookings",
        data={
            "slots": [{"courtId": court, "date": tomorrow.isoformat(), "hour": hour}],
            "customerName": "ทดสอบภาพรวม",
            "customerPhone": None,
            "paidBy": "Cash",
        },
    )
    booking_id = taken.json()["bookingId"]
    after = dashboard(page, venue_id)
    check("a confirmed booking ahead adds to the advance",
          after["advanceBaht"] == before["advanceBaht"] + price)

    # Picking last month goes through the URL, so the link can be sent on.
    page.click("[data-testid=last-month]")
    page.wait_for_url("**/report?from=*")
    expect(page.get_by_test_id("revenue-total")).to_be_visible()
    check("last month is a link of its own", "from=" in page.url and "to=" in page.url)

    # Four screenshots the design review asks for: phone and desk, Thai and English.
    page.goto(f"{BASE}/venues/{venue_id}/report")
    page.wait_for_selector("[data-testid=revenue-total]")
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
    # 6. The months as a spreadsheet (US-16, columns from PRD 7.3). The file is built in the
    # browser so its headings are in the reader's language — the server sends figures, not words.
    # sign_in leaves the page on the home screen, so the dashboard is opened again for it.
    sign_in(page, OWNER)
    page.goto(f"{BASE}/venues/{venue_id}/report")
    page.wait_for_selector("[data-testid=download-csv]")

    with page.expect_download() as download:
        page.click("[data-testid=download-csv]")
    saved = download.value
    check("the summary comes down as a file", saved.suggested_filename.endswith(".csv"))

    path = pathlib.Path(saved.path())
    raw = path.read_bytes()
    check("which a spreadsheet opens as UTF-8", raw[:3] == codecs.BOM_UTF8)

    rows = raw.decode("utf-8-sig").strip().splitlines()
    figures = page.request.get(
        f"{BASE}/api/venues/{venue_id}/dashboard").json()
    check(
        "with one line per month the page is showing",
        len(rows) == len(figures["months"]) + 1,
    )
    check(
        "and the headings in the language the reader chose",
        rows[0].split(",")[0] not in ("Month", "month"),
    )

    browser.close()

check.summarise()
