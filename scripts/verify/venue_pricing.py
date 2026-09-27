"""Prices and the cancellation policy (US-11) against the running stack."""

import datetime

from harness import BASE, OWNER, STAFF, Checks, login, open_seeded_venue, staff_can
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

    # This script checks what somebody without ManageSettings sees, and venue_ui.py hands the
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

    # The policy stayed on settings.
    page.goto(BASE + venue_url + "/settings")

    # 4. The cancellation policy starts at the default and takes a second step.
    page.wait_for_selector("[data-testid=policy-list]")
    check("the default policy is shown", page.locator("[data-testid=tier-24]").count() == 1)

    page.locator("[data-testid=add-tier]").click()
    page.locator('[data-testid="tier-row-1"] input[type=number]').first.fill("6")
    with page.expect_response(lambda response: response.url.endswith("/cancellation-policy")) as saved:
        page.locator("[data-testid=save-policy]").click()
    check("the server took the policy", saved.value.status == 200)
    page.reload()
    page.wait_for_selector("[data-testid=policy-list]")
    check("the second step survives a reload", page.locator("[data-testid=tier-6]").count() == 1, page)

    # 5. A ladder that goes the wrong way is refused.
    page.locator('[data-testid="tier-row-0"] input[type=number]').nth(1).fill("10")
    with page.expect_response(lambda response: response.url.endswith("/cancellation-policy")):
        page.locator("[data-testid=save-policy]").click()
    page.wait_for_selector("[data-testid=policy-error]")
    check(
        "a policy that pays less for cancelling earlier is refused",
        "ไม่น้อยกว่า" in page.locator("[data-testid=policy-error]").inner_text(),
        page,
    )

    # 6. Staff read the prices and the policy and can change neither.
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
    page.goto(BASE + venue_url + "/settings")
    page.wait_for_selector("[data-testid=policy-list]")
    check("staff read the policy", page.locator("[data-testid=policy-list]").count() == 1)
    check("staff get no policy editor", page.locator("[data-testid=add-tier]").count() == 0, page)

    browser.close()

check.summarise()
