"""Checks the code-review fixes against the running Docker stack (http://localhost:8080)."""
import time

from harness import BASE, OWNER, STAFF, Checks, control, login
from playwright.sync_api import expect, sync_playwright

check = Checks(__file__)

with sync_playwright() as p:
    browser = p.chromium.launch()
    # This script stubs API answers with page.route, which cannot see requests the app's service
    # worker makes on its behalf (PRD 8 PWA). The worker is blocked here, and pwa.py checks it.
    page = browser.new_page(viewport={"width": 1280, "height": 900}, service_workers="block")

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
    # Applying moved to its own page when the form grew a tax identity (US-10).
    page.click("[data-testid=apply-link]")
    page.wait_for_selector("[data-testid=apply]")
    page.fill("#code", code)
    page.fill("#name", f"Second Court {code}")
    page.fill("#address-line", "9 ถนนพระราม 4")
    page.fill("#district", "ปทุมวัน")
    page.fill("#province", "กรุงเทพมหานคร")
    page.fill("#promptpay-id", "0812345678")
    page.fill("#promptpay-name", "บริษัท ทดสอบ จำกัด")
    page.fill("#legal-name", "บริษัท ทดสอบ จำกัด")
    page.fill("#tax-id", "0105561000000")
    page.click("[data-testid=copy-address]")
    control(page, "accepts-agreement").click()
    page.click("[data-testid=apply]")
    # Creating a venue drops the owner on its detail page; the list is one step back.
    page.wait_for_selector("[data-testid=venue-name]")
    page.goto(f"{BASE}/venues")
    page.wait_for_selector("[data-testid=venue-list] a")

    links = page.locator("[data-testid=venue-list] a")
    approved = links.filter(has_not_text="Second Court").first
    created = page.locator("[data-testid=venue-list] a", has_text=f"Second Court {code}").first
    # A row carries the code and the status beside the name, so read the name itself.
    first_url = approved.get_attribute("href")
    first_name = approved.locator(".entry-name").inner_text()
    second_url = created.get_attribute("href")
    second_name = created.locator(".entry-name").inner_text()

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

    staff_permission = (
        page.locator("[data-testid^=permission-][data-testid$=-ViewReports]").first
        .get_attribute("data-testid")
    )
    box = control(page, staff_permission)
    before = box.is_checked()
    box.click()
    expect(box).to_be_checked(checked=not before)
    check("owner can change a staff permission", True, page)

    # A refused change shows next to the roster and leaves the rest of the page standing.
    page.route("**/members/*/permissions", lambda route: route.fulfill(
        status=403, content_type="application/problem+json", body='{"code":"venue.not_approved"}'))
    box.click()
    page.wait_for_selector("[data-testid=member-error]")
    check("a refused change shows a member-level error", page.locator("[data-testid=member-error]").count() == 1)
    check("the roster survives a refused change", page.locator("[data-testid=member-list] .entry").count() >= 2)
    check("the invite form survives a refused change", page.locator("#invite-email").count() == 1, page)
    expect(box).to_be_checked(checked=not before)
    check("a refused change puts the checkbox back", True)
    page.unroute("**/members/*/permissions")
    box.click()  # put the permission back the way it was found
    expect(box).to_be_checked(checked=before)

    # 6b. What that person may send back in one record (US-18). Holding the permission is being
    # given the work, not being trusted with any amount of the venue's money.
    who = staff_permission.removeprefix("permission-").removesuffix("-ViewReports")
    limit = page.locator(f"[data-testid=refund-limit-{who}]")
    check("a staff member's row carries a refund limit", limit.count() == 1, page)
    check(
        "and the owner's own row does not, because they have no ceiling",
        page.locator("[data-testid^=refund-limit-]").count()
        < page.locator("[data-testid=member-list] .entry").count(),
        page,
    )

    with page.expect_response(lambda r: "/permissions" in r.url and r.request.method == "PUT") as set_to:
        limit.fill("750")
        limit.blur()
    check("setting it is saved", set_to.value.status == 204)
    check(
        "and it is sent with the permissions they already had, not instead of them",
        set_to.value.request.post_data_json.get("refundLimitBaht") == 750
        and len(set_to.value.request.post_data_json.get("permissions", [])) > 0,
    )

    page.reload()
    page.wait_for_selector(f"[data-testid=refund-limit-{who}]")
    check(
        "and it is still there when the page is opened again",
        page.locator(f"[data-testid=refund-limit-{who}]").input_value() == "750",
        page,
    )

    # Put it back, so the next run of this script starts where this one found things.
    with page.expect_response(lambda r: "/permissions" in r.url and r.request.method == "PUT"):
        limit.fill("0")
        limit.blur()

    # 7. A staff member sees the roster read-only.
    page.goto(f"{BASE}/")
    page.click("[data-testid=sign-out]")
    page.goto(BASE + first_url)
    page.wait_for_url("**/login?returnUrl=*")
    login(page, STAFF)
    page.wait_for_selector("[data-testid=venue-name]")
    check("staff sees no remove button", page.locator("[data-testid^=remove-]").count() == 0)
    check("staff checkboxes are read-only",
          page.locator("[data-testid^=permission-] input:not([disabled])").count() == 0, page)

    browser.close()

check.summarise()
