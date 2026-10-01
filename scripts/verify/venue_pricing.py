"""Prices and peak hours, opening hours and grace (owner app PR-4, 2c) against the running stack."""

import datetime

from harness import BASE, OWNER, STAFF, Checks, ensure_bookable, login, open_seeded_venue, staff_can
from playwright.sync_api import sync_playwright

check = Checks(__file__)

with sync_playwright() as p:
    browser = p.chromium.launch()
    page = browser.new_page(viewport={"width": 1280, "height": 1100})

    page.goto(f"{BASE}/login")
    login(page, OWNER)
    page.wait_for_url(f"{BASE}/")
    venue_url = open_seeded_venue(page)
    venue_id = venue_url.split("/venues/")[1]

    # This script checks what somebody without ManageSettings sees, and an earlier run may have handed the
    # staff that permission as the thing it tests. Ask for the standing needed.
    staff_can(browser, venue_id)

    # These checks share one venue with the other scripts, and prices are only valid against the
    # hours the venue is open, so start from a week this script knows: open 6-22, every day.
    week = page.request.put(
        f"{BASE}/api/venues/{venue_id}/opening-hours",
        data={
            "effectiveFrom": datetime.date.today().isoformat(),
            "days": [
                {"day": day, "opensHour": 6, "closesHour": 22}
                for day in (
                    "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"
                )
            ],
        },
    )
    check("the week these prices are checked against is in place", week.status == 200)
    prices = page.request.put(
        f"{BASE}/api/venues/{venue_id}/prices",
        data={
            "bands": [
                {"day": day, "fromHour": start, "toHour": end, "bahtPerHour": baht}
                for day in (
                    "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"
                )
                for start, end, baht in ((6, 18, 200), (18, 22, 300))
            ]
        },
    )
    check("and the prices this script starts from", prices.status == 200)
    policy = page.request.put(
        f"{BASE}/api/venues/{venue_id}/cancellation-policy",
        data={"tiers": [{"hoursBefore": 24, "refundPercent": 100}]},
    )
    check("and the one-step policy it starts from", policy.status == 200)

    # Prices are their own page now, painted as a week (owner app PR-4).
    page.goto(BASE + venue_url + "/pricing")
    page.wait_for_selector("[data-testid=price-week]")

    # 1. The prices read as tiers, and the evening is painted in the dearer one.
    check("the prices read as tiers, cheapest first",
          "200" in page.inner_text("[data-testid=tier-price-0]")
          and "300" in page.inner_text("[data-testid=tier-price-1]"), page)
    check("an evening hour is painted in the evening's tier",
          "tier-1" in (page.get_attribute("[data-testid=cell-Monday-18]", "class") or "")
          and "tier-0" in (page.get_attribute("[data-testid=cell-Monday-17]", "class") or ""))

    # 2. Painting Saturday night into the top tier, raised by a step, is what the server keeps.
    page.click("[data-testid=tier-2]")
    page.dispatch_event("[data-testid=cell-Saturday-20]", "pointerdown")
    page.dispatch_event("[data-testid=cell-Saturday-21]", "pointerenter")
    page.dispatch_event("body", "pointerup")
    page.click("[data-testid=tier-more-2]")
    with page.expect_response(lambda response: response.url.endswith("/prices")
                              and response.request.method == "PUT") as saved:
        page.click("[data-testid=pricing-save]")
    check("the server took the painted week", saved.value.status == 200)
    saturday = [band for band in saved.value.json()["bands"] if band["day"] == "Saturday"]
    check("as a band of its own for the two hours painted",
          {"day": "Saturday", "fromHour": 20, "toHour": 22, "bahtPerHour": 370} in saturday)
    page.reload()
    page.wait_for_selector("[data-testid=price-week]")
    check("the painted hours survive a reload",
          "tier-2" in (page.get_attribute("[data-testid=cell-Saturday-20]", "class") or ""), page)

    # A gap cannot be painted — every open hour always carries a tier — so the refusal the old
    # editor could provoke is no longer a thing a venue can do from this page.

    # 5b. Opening hours and the grace for latecomers, on the section's second tab (artboard c).
    page.goto(BASE + venue_url + "/pricing")
    page.click("[data-testid=pricing-tab-hours]")
    page.wait_for_selector("[data-testid=opening-hours]")
    api = f"{BASE}/api{venue_url}"
    page.select_option("[data-testid=opens-Sunday]", "5")
    with page.expect_response(lambda r: r.url.endswith("/opening-hours") and r.request.method == "PUT") as week:
        page.click("[data-testid=hours-save]")
    check("a week that opens an hour earlier is saved", week.value.status == 200, page)
    bands = page.request.get(f"{api}/prices").json()["bands"]
    check("and the hour it opens was priced first, from its neighbour",
          any(b["day"] == "Sunday" and b["fromHour"] == 5 for b in bands))
    page.wait_for_selector("[data-testid=hours-result]")
    check("the page says so", page.locator("[data-testid=hours-result]").count() == 1, page)

    page.click("[data-testid=grace-30]")
    with page.expect_response(lambda r: r.url.endswith("/grace")) as grace:
        page.click("[data-testid=grace-save]")
    mine = [v for v in page.request.get(f"{BASE}/api/venues/mine").json() if f"/venues/{v['id']}" == venue_url][0]
    check("the grace for latecomers is saved", grace.value.status == 204 and mine["graceMinutes"] == 30, page)
    page.request.put(f"{api}/grace", data={"minutes": 15})
    ensure_bookable(browser, venue_url.rsplit("/", 1)[-1])

    # 6. Staff read the prices and cannot change them.
    page.goto(f"{BASE}/")
    page.click("[data-testid=sign-out]")
    page.goto(BASE + venue_url + "/pricing")
    page.wait_for_url("**/login?returnUrl=*")
    login(page, STAFF)
    page.wait_for_selector("[data-testid=price-week]")
    check("staff read the prices", page.locator("[data-testid^=cell-]").count() > 0)
    check("staff cannot paint them",
          page.locator("[data-testid=cell-Monday-18]").is_disabled()
          and page.locator("[data-testid=pricing-save]").count() == 0, page)

    browser.close()

check.summarise()
