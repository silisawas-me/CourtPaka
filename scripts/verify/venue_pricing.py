"""Prices and the cancellation policy (US-11) against the running stack."""

import datetime

from harness import BASE, OWNER, STAFF, Checks, login, open_seeded_venue
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

    page.goto(BASE + venue_url + "/settings")
    page.wait_for_selector("[data-testid=price-list]")

    # 1. The venue prices its evenings higher, and the page says so.
    monday = page.locator("[data-testid=price-Monday]").inner_text()
    check("a day shows each of its bands", "6:00–18:00" in monday and "18:00–22:00" in monday, page)
    check("and what each one costs", "200" in monday and "300" in monday)

    # 2. Publishing sends the whole list and the page shows what came back. The rows come back in
    # the server's order (Sunday first), so find Monday's rather than assuming where it sits.
    monday_rows = [
        index
        for index in range(page.locator('[data-testid^="band-"]').count())
        # [ngValue] makes the option value an Angular key, so match on what the option reads.
        if page.locator(f'[data-testid="band-{index}"] option:checked').first.inner_text() == "จันทร์"
    ]
    page.locator(f'[data-testid="band-{monday_rows[0]}"] input[type=number]').fill("250")
    with page.expect_response(lambda response: response.url.endswith("/prices")) as saved:
        page.locator("[data-testid=save-prices]").click()
    check("the server took the new prices", saved.value.status == 200)
    page.reload()
    page.wait_for_selector("[data-testid=price-list]")
    check(
        "the new price survives a reload",
        "250" in page.locator("[data-testid=price-Monday]").inner_text(),
        page,
    )

    # 3. A gap in the day is refused, and the page says which rule was broken.
    page.locator(f'[data-testid="remove-band-{monday_rows[0]}"]').click()
    with page.expect_response(lambda response: response.url.endswith("/prices")):
        page.locator("[data-testid=save-prices]").click()
    page.wait_for_selector("[data-testid=price-error]")
    check(
        "an unpriced open hour is refused in Thai",
        "ยังไม่มีราคา" in page.locator("[data-testid=price-error]").inner_text(),
        page,
    )

    # 4. The cancellation policy starts at the default and takes a second step.
    page.reload()
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

    # 6. Staff read both cards and can change neither.
    page.goto(f"{BASE}/")
    page.click("[data-testid=sign-out]")
    page.goto(BASE + venue_url + "/settings")
    page.wait_for_url("**/login?returnUrl=*")
    login(page, STAFF)
    page.wait_for_selector("[data-testid=price-list]")
    check("staff read the prices", page.locator("[data-testid=price-day]").count() > 0)
    check("staff get no price editor", page.locator("[data-testid=add-band]").count() == 0)
    check("staff read the policy", page.locator("[data-testid=policy-list]").count() == 1)
    check("staff get no policy editor", page.locator("[data-testid=add-tier]").count() == 0, page)

    browser.close()

check.summarise()
