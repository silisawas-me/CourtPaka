"""Courts and opening hours (US-11) against the running stack at http://localhost:8080."""
import datetime

from harness import BASE, OWNER, STAFF, Checks, login
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
    check("the venue page links to its settings", page.url.endswith("/settings"), page)

    # 1. Add two courts.
    # Count only once the section has rendered: either list or empty note, never mid-load.
    page.wait_for_selector("[data-testid=court-list], [data-testid=no-courts]")
    existing = page.locator("[data-testid=court-list] li").count()
    stamp = datetime.datetime.now().strftime("%H%M%S")
    for name in (f"Court {stamp}A", f"Court {stamp}B"):
        page.fill("#court-name", name)
        page.locator("form").first.locator("button[type=submit]").click()
        page.wait_for_selector(f"text={name}")
    check("courts are added and listed", page.locator("[data-testid=court-list] li").count() == existing + 2, page)

    # 2. A duplicate name is refused, and the page survives it.
    page.fill("#court-name", f"Court {stamp}A")
    page.locator("form").first.locator("button[type=submit]").click()
    page.wait_for_selector("[data-testid=court-error]")
    check("a duplicate court name is refused", page.locator("[data-testid=court-error]").count() == 1)
    check("the page survives the refusal", page.locator("#court-name").count() == 1, page)

    # 3. Taking a court out of use sticks across a reload.
    box = page.locator("[data-testid^=court-active-]").last
    court_id = box.get_attribute("data-testid")
    # Wait for the server to answer: the box flips optimistically, and reloading before the
    # request lands would cancel it.
    with page.expect_response(lambda response: "/status" in response.url) as answered:
        box.uncheck()
    check("the server accepted the change", answered.value.status == 200)
    expect(box).not_to_be_checked()
    page.reload()
    page.wait_for_selector("[data-testid=court-list]")
    check("a court taken out of use stays that way", not page.locator(f"[data-testid={court_id}]").is_checked(), page)
    with page.expect_response(lambda response: "/status" in response.url):
        page.locator(f"[data-testid={court_id}]").check()  # put it back

    # 3b. Renaming a court keeps its place in the grid.
    row = page.locator("[data-testid=court-list] li").last
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
    page.fill("#effective-from", today.isoformat())
    page.uncheck("[data-testid=open-Monday]")
    page.select_option("[data-testid=opens-Tuesday]", label="7:00")
    page.select_option("[data-testid=closes-Tuesday]", label="24:00")
    with page.expect_response(lambda response: "/opening-hours" in response.url) as published:
        page.locator("form").last.locator("button[type=submit]").click()
    check("the week was accepted", published.value.status == 200)
    page.reload()
    page.wait_for_selector("[data-testid=hours-current]")

    monday = page.locator("[data-testid=hours-Monday]").inner_text().strip()
    tuesday = page.locator("[data-testid=hours-Tuesday]").inner_text().strip()
    check("a closed weekday comes back as closed", monday == "ปิด", page)
    check("an open weekday keeps its hours", tuesday == "7:00 – 24:00")
    check("the published week is the one in force", today.isoformat() in page.locator("[data-testid=hours-in-force]").inner_text())

    # 5. A week dated ahead is listed separately.
    later = (today + datetime.timedelta(days=30)).isoformat()
    page.fill("#effective-from", later)
    with page.expect_response(lambda response: "/opening-hours" in response.url):
        page.locator("form").last.locator("button[type=submit]").click()
    page.wait_for_selector("[data-testid=hours-upcoming]")
    check("a week dated ahead is listed as upcoming", later in page.locator("[data-testid=hours-upcoming]").inner_text(), page)

    # 6. Backdating is refused by the server and shown in Thai.
    page.fill("#effective-from", (today - datetime.timedelta(days=1)).isoformat())
    with page.expect_response(lambda response: "/opening-hours" in response.url):
        page.locator("form").last.locator("button[type=submit]").click()
    page.wait_for_selector("[data-testid=hours-error]")
    error = page.locator("[data-testid=hours-error]").inner_text()
    check("backdating opening hours is refused", "ย้อนหลัง" in error, page)

    # 7. Staff without ManageSettings read it and can change nothing.
    page.goto(f"{BASE}/")
    page.click("[data-testid=sign-out]")
    page.goto(BASE + venue_url + "/settings")
    page.wait_for_url("**/login?returnUrl=*")
    login(page, STAFF)
    page.wait_for_selector("[data-testid=court-list]")
    check("staff see the courts", page.locator("[data-testid=court-list] li").count() > 0)
    check("staff get no court form", page.locator("#court-name").count() == 0)
    check("staff get no opening-hours form", page.locator("#effective-from").count() == 0)
    check("staff see why it is read-only", page.locator("[data-testid=read-only]").count() == 1, page)
    check("staff cannot toggle a court", page.locator("[data-testid^=court-active-]:not([disabled])").count() == 0)

    browser.close()

check.summarise()
