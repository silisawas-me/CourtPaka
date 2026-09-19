"""Checks the code-review fixes against the running Docker stack (http://localhost:8080)."""
import pathlib
import sys
import time

from playwright.sync_api import expect, sync_playwright

BASE = "http://localhost:8080"
OWNER = "owner@courtpaka.local"
STAFF = "staff@courtpaka.local"
PASSWORD = "DevPassword1"
SHOTS = pathlib.Path(__file__).parent / "shots"
SHOTS.mkdir(exist_ok=True)

passed, failed = [], []


def check(name, condition, page=None):
    (passed if condition else failed).append(name)
    print(("PASS  " if condition else "FAIL  ") + name)
    if page is not None:
        page.screenshot(path=str(SHOTS / (name.replace(" ", "_").replace("/", "-") + ".png")), full_page=True)


def login(page, email):
    page.fill("#email", email)
    page.fill("#password", PASSWORD)
    page.click("button[type=submit]")


with sync_playwright() as p:
    browser = p.chromium.launch()
    page = browser.new_page(viewport={"width": 1280, "height": 900})

    # 1. The guard sends an anonymous visitor to login and brings them back afterwards.
    page.goto(f"{BASE}/venues")
    page.wait_for_url("**/login?returnUrl=%2Fvenues")
    check("guard redirects to login with returnUrl", "returnUrl=%2Fvenues" in page.url, page)
    login(page, OWNER)
    page.wait_for_url(f"{BASE}/venues")
    check("login returns to the interrupted page", page.url == f"{BASE}/venues", page)

    # 2. An off-site returnUrl is ignored instead of followed.
    page.goto(f"{BASE}/login?returnUrl=%2F%2Fexample.com")
    login(page, OWNER)
    page.wait_for_url(f"{BASE}/")
    check("off-site returnUrl lands on home", page.url == f"{BASE}/", page)

    # 3. Make a second venue so the detail page can be asked to switch between two.
    page.goto(f"{BASE}/venues")
    page.wait_for_selector("[data-testid=venue-list]")
    code = f"S{int(time.time()) % 100000}"
    page.fill("#code", code)
    page.fill("#name", f"Second Court {code}")
    page.click("form button[type=submit]")
    # Creating a venue drops the owner on its detail page; the list is one step back.
    page.wait_for_selector("[data-testid=venue-name]")
    page.goto(f"{BASE}/venues")
    page.wait_for_selector("[data-testid=venue-list] a")

    links = page.locator("[data-testid=venue-list] a")
    approved = links.filter(has_not_text="Second Court").first
    created = page.locator("[data-testid=venue-list] a", has_text=f"Second Court {code}").first
    first_url, first_name = approved.get_attribute("href"), approved.inner_text()
    second_url, second_name = created.get_attribute("href"), created.inner_text()

    # 4. Moving between two venue pages must load the new venue, not keep the old one.
    page.goto(BASE + first_url)
    expect(page.locator("[data-testid=venue-name]")).to_have_text(first_name)
    page.goto(BASE + second_url)
    expect(page.locator("[data-testid=venue-name]")).to_have_text(second_name)
    check("detail page reloads when the venue id changes", True, page)

    # In-app navigation (router reuse) is the case that used to break.
    page.goto(f"{BASE}/venues")
    page.click(f"[data-testid=venue-list] a[href='{first_url}']")
    expect(page.locator("[data-testid=venue-name]")).to_have_text(first_name)
    page.go_back()
    page.click(f"[data-testid=venue-list] a[href='{second_url}']")
    expect(page.locator("[data-testid=venue-name]")).to_have_text(second_name)
    check("router navigation between venues shows the right venue", True, page)

    # 5. The new venue is Pending, so an owner sees no write controls there.
    check("pending venue says so", page.locator("[data-testid=venue-not-approved]").count() == 1)
    check("pending venue offers no invite form", page.locator("#invite-email").count() == 0, page)

    # 6. On the approved venue the owner can manage, and one failed change keeps the page.
    page.goto(BASE + first_url)
    page.wait_for_selector("#invite-email")
    check("approved venue offers the invite form", page.locator("#invite-email").count() == 1)

    staff_box = page.locator("[data-testid^=permission-][data-testid$=-ViewReports]").first
    before = staff_box.is_checked()
    staff_box.click()
    expect(staff_box).to_be_checked(checked=not before)
    check("owner can change a staff permission", True, page)

    # A refused change shows next to the roster and leaves the rest of the page standing.
    page.route("**/members/*/permissions", lambda route: route.fulfill(
        status=403, content_type="application/problem+json", body='{"code":"venue.not_approved"}'))
    staff_box.click()
    page.wait_for_selector("[data-testid=member-error]")
    check("a refused change shows a member-level error", page.locator("[data-testid=member-error]").count() == 1)
    check("the roster survives a refused change", page.locator("[data-testid=member-list] li").count() >= 2)
    check("the invite form survives a refused change", page.locator("#invite-email").count() == 1, page)
    expect(staff_box).to_be_checked(checked=not before)
    check("a refused change puts the checkbox back", True)
    page.unroute("**/members/*/permissions")
    staff_box.click()  # put the permission back the way it was found
    expect(staff_box).to_be_checked(checked=before)

    # 7. A staff member sees the roster read-only.
    page.goto(f"{BASE}/")
    page.click("[data-testid=sign-out]")
    page.goto(BASE + first_url)
    page.wait_for_url("**/login?returnUrl=*")
    login(page, STAFF)
    page.wait_for_selector("[data-testid=venue-name]")
    check("staff sees no remove button", page.locator("[data-testid^=remove-]").count() == 0)
    check("staff checkboxes are read-only",
          page.locator("[data-testid^=permission-]:not([disabled])").count() == 0, page)

    browser.close()

print(f"\n{len(passed)} passed, {len(failed)} failed")
for name in failed:
    print("  FAILED: " + name)
sys.exit(1 if failed else 0)
