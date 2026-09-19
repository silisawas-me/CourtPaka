"""Courts and opening hours (US-11) against the running stack at http://localhost:8080."""
import datetime


from harness import (
    BASE,
    OWNER,
    STAFF,
    Checks,
    control,
    login,
    pick_date,
    thai_date,
    thai_month_year,
)
from playwright.sync_api import expect, sync_playwright

check = Checks(__file__)

with sync_playwright() as p:
    browser = p.chromium.launch()
    page = browser.new_page(viewport={"width": 1280, "height": 1100})

    page.goto(f"{BASE}/login")
    login(page, OWNER)
    page.wait_for_url(f"{BASE}/")

    # The seeded DEV01 venue is the approved one.
    page.goto(f"{BASE}/venues")
    page.wait_for_selector("[data-testid=venue-list] a")
    dev = page.locator("[data-testid=venue-list] a", has_text="Development Court").first
    venue_url = dev.get_attribute("href")
    page.goto(BASE + venue_url)
    page.click("[data-testid=settings-link]")
    page.wait_for_url("**/settings")
    venue_id = page.url.split("/venues/")[1].split("/")[0]
    check("the venue page links to its settings", page.url.endswith("/settings"), page)

    # 1. Add two courts.
    # Count only once the section has rendered: either list or empty note, never mid-load.
    page.wait_for_selector("[data-testid=court-row], [data-testid=no-courts]")
    existing = page.locator("[data-testid=court-row]").count()
    stamp = datetime.datetime.now().strftime("%H%M%S")
    for name in (f"Court {stamp}A", f"Court {stamp}B"):
        page.fill("#court-name", name)
        page.locator("form").first.locator("button[type=submit]").click()
        page.wait_for_selector(f"text={name}")
    check("courts are added and listed", page.locator("[data-testid=court-row]").count() == existing + 2, page)

    # 2. A duplicate name is refused, and the page survives it.
    page.fill("#court-name", f"Court {stamp}A")
    page.locator("form").first.locator("button[type=submit]").click()
    page.wait_for_selector("[data-testid=court-error]")
    check("a duplicate court name is refused", page.locator("[data-testid=court-error]").count() == 1)
    check("the page survives the refusal", page.locator("#court-name").count() == 1, page)

    # 3. Taking a court out of use sticks across a reload.
    last_row = page.locator("[data-testid=court-row]").last
    court_id = last_row.locator("[data-testid^=court-active-]").get_attribute("data-testid")
    # Wait for the server to answer: the box flips optimistically, and reloading before the
    # request lands would cancel it.
    if not control(page, court_id).is_checked():
        control(page, court_id).click()  # start from "in use", whatever the last run left behind
        page.wait_for_timeout(500)
    with page.expect_response(lambda response: "/status" in response.url) as answered:
        control(page, court_id).click()
    check("the server accepted the change", answered.value.status == 200)
    expect(control(page, court_id)).not_to_be_checked()
    page.reload()
    page.wait_for_selector("[data-testid=court-list]")
    check("a court taken out of use stays that way", not control(page, court_id).is_checked(), page)
    with page.expect_response(lambda response: "/status" in response.url):
        control(page, court_id).click()  # put it back
    expect(control(page, court_id)).to_be_checked()

    # 3b. Renaming a court keeps its place in the grid.
    row = page.locator("[data-testid=court-row]").last
    court_id = row.locator("[data-testid^=court-active-]").get_attribute("data-testid").removeprefix("court-active-")
    renamed = f"Centre {stamp}"
    page.click(f"[data-testid=rename-{court_id}]")
    page.fill(f"[data-testid=rename-input-{court_id}]", renamed)
    with page.expect_response(lambda response: response.request.method == "PUT") as saved:
        page.locator(f"[data-testid=rename-input-{court_id}]").press("Enter")
    check("a court can be renamed", saved.value.status == 200)
    page.reload()
    page.wait_for_selector("[data-testid=court-list]")
    check("the new name survives a reload", renamed in page.locator("[data-testid=court-list]").inner_text(), page)

    # 4. Publish a week, with Monday closed, and read it back.
    today = datetime.date.today()
    pick_date(page, today)
    control(page, "open-Monday").uncheck()
    page.select_option('[data-testid="opens-Tuesday"]', label="7:00")
    page.select_option('[data-testid="closes-Tuesday"]', label="24:00")
    with page.expect_response(lambda response: "/opening-hours" in response.url) as published:
        page.locator("form").last.locator("button[type=submit]").click()
    check("the week was accepted", published.value.status == 200)
    page.reload()
    page.wait_for_selector("[data-testid=hours-current]")

    monday = page.locator("[data-testid=hours-Monday]").inner_text().strip()
    tuesday = page.locator("[data-testid=hours-Tuesday]").inner_text().strip()
    check("a closed weekday comes back as closed", monday == "ปิด", page)
    check("an open weekday keeps its hours", tuesday == "7:00 – 24:00")
    in_force = page.locator("[data-testid=hours-in-force]").inner_text()
    check("the published week is the one in force", thai_date(today) in in_force)

    # 5. A week dated ahead is listed separately.
    pick_date(page, today + datetime.timedelta(days=30))
    with page.expect_response(lambda response: "/opening-hours" in response.url):
        page.locator("form").last.locator("button[type=submit]").click()
    page.wait_for_selector("[data-testid=hours-upcoming]")
    check(
        "a week dated ahead is listed as upcoming",
        thai_date(today + datetime.timedelta(days=30))
        in page.locator("[data-testid=hours-upcoming]").inner_text(),
        page,
    )

    # 6. A past date never leaves the page: the picker has a minimum, and the form stops there.
    # A past date cannot be picked at all: the calendar will not offer it.
    page.click("mat-datepicker-toggle button")
    page.wait_for_selector("mat-calendar")
    # The field holds a date a month out, so the calendar opens there; walk back to this month.
    for _ in range(3):
        if page.locator("mat-calendar .mat-calendar-period-button").inner_text().strip() == thai_month_year(today):
            break
        page.click(".mat-calendar-previous-button")
    yesterday = today - datetime.timedelta(days=1)
    disabled = page.locator(
        f'.mat-calendar-body-cell[aria-disabled="true"] '
        f'.mat-calendar-body-cell-content:text-is("{yesterday.day}")'
    ).count()
    page.keyboard.press("Escape")
    check("yesterday cannot be picked", disabled == 1, page)

    # The calendar stopping it is convenience; the server refusing it is the rule. Ask the API
    # directly, through the browser's own session, so the check does not only prove the UI.
    refused = page.request.put(
        f"{BASE}/api/venues/{venue_id}/opening-hours",
        data={
            "effectiveFrom": yesterday.isoformat(),
            "days": [
                {"day": day, "opensHour": 6, "closesHour": 22}
                for day in (
                    "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"
                )
            ],
        },
    )
    check("the server refuses a backdated week", refused.status == 400)
    check(
        "and says which rule it broke",
        refused.json().get("code") == "court.effective_date_in_the_past",
    )

    # 7. Staff without ManageSettings read it and can change nothing.
    page.goto(f"{BASE}/")
    page.click("[data-testid=sign-out]")
    page.goto(BASE + venue_url + "/settings")
    page.wait_for_url("**/login?returnUrl=*")
    login(page, STAFF)
    page.wait_for_selector("[data-testid=court-list]")
    check("staff see the courts", page.locator("[data-testid=court-row]").count() > 0)
    check("staff get no court form", page.locator("#court-name").count() == 0)
    check("staff get no opening-hours form", page.locator("#effective-from").count() == 0)
    check("staff see why it is read-only", page.locator("[data-testid=read-only]").count() == 1, page)
    check("staff cannot toggle a court", page.locator("[data-testid^=court-active-] button:not([disabled])").count() == 0)

    browser.close()

check.summarise()
