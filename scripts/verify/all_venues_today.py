"""Today at every venue a person reads the reports of, on "my venues" (badPaka 2c)."""

from harness import BASE, OWNER, STAFF, Checks, seeded_venue_id, sign_in
from playwright.sync_api import sync_playwright

check = Checks(__file__)

with sync_playwright() as p:
    browser = p.chromium.launch()
    venue_id = seeded_venue_id(browser.new_page())

    owner = browser.new_page(viewport={"width": 1280, "height": 900})
    sign_in(owner, OWNER)
    today = owner.request.get(f"{BASE}/api/venues/mine/today").json()
    row = next((venue for venue in today["venues"] if venue["venueId"] == venue_id), None)
    check("the owner's overview has their venue", row is not None)

    # The same numbers as the venue's own dashboard, for the same day.
    date = today["date"]
    dashboard = owner.request.get(
        f"{BASE}/api/venues/{venue_id}/dashboard", params={"from": date, "to": date}).json()
    check(
        "it says what the venue's dashboard says about today",
        row is not None
        and row["keptBaht"] == dashboard["onlineBaht"] + dashboard["staffBaht"]
        and row["bookedHours"] == dashboard["bookedHours"]
        and row["sellableHours"] == dashboard["sellableHours"],
    )

    owner.goto(f"{BASE}/venues")
    owner.wait_for_selector("[data-testid=all-venues-today]")
    check("the overview is drawn above the list of venues",
          owner.locator(f"[data-testid=overview-venue-{venue_id}]").is_visible(), owner)
    check(
        "every hour the venue sells has a cell",
        row is not None
        and owner.locator(f"[data-testid=overview-venue-{venue_id}] .heat-hour").count() == len(row["hours"]),
    )
    owner.locator(f"[data-testid=overview-venue-{venue_id}] .venue-name").click()
    owner.wait_for_url(f"**/venues/{venue_id}/now")
    check("a venue's name leads to its floor right now", True)

    # Staff who may read the reports see the venue; the door is the dashboard's.
    staff = browser.new_page()
    sign_in(staff, STAFF)
    reads = staff.request.get(f"{BASE}/api/venues/{venue_id}/dashboard").status == 200
    theirs = staff.request.get(f"{BASE}/api/venues/mine/today").json()["venues"]
    check(
        "staff see the venue here exactly when they may open its dashboard",
        reads == any(venue["venueId"] == venue_id for venue in theirs),
    )

    phone = browser.new_page(viewport={"width": 390, "height": 844})
    sign_in(phone, OWNER)
    phone.goto(f"{BASE}/venues")
    phone.wait_for_selector("[data-testid=all-venues-today]")
    check("the overview fits a phone",
          phone.evaluate("() => document.documentElement.scrollWidth") <= 390, phone)

    browser.close()

check.summarise()
