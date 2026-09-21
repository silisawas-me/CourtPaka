"""The platform's own figures, and stopping an account (US-22)."""

from harness import (
    BASE,
    Checks,
    login,
    new_booker,
    sign_in,
)
from playwright.sync_api import expect, sync_playwright

check = Checks(__file__)
ADMIN = "admin@courtpaka.local"

with sync_playwright() as p:
    browser = p.chromium.launch()

    admin = browser.new_page(viewport={"width": 1280, "height": 900})
    sign_in(admin, ADMIN)

    # 1. The figures, as the server counts them.
    admin.goto(f"{BASE}/admin/dashboard")
    admin.wait_for_selector("[data-testid=gmv-total]")
    figures = admin.request.get(f"{BASE}/api/admin/dashboard").json()
    shown = admin.get_by_test_id("bookings-total").inner_text().replace(",", "")
    check("the platform dashboard shows the server's count", int(shown) == figures["totals"]["bookings"])
    check("every venue has a line", admin.locator("[data-testid=venues] tbody tr").count()
          == len(figures["venues"]))
    check("desk, Thai", True, admin)

    admin.set_viewport_size({"width": 390, "height": 844})
    check("phone, Thai", True, admin)
    check("nothing on a phone scrolls the page sideways",
          admin.evaluate("document.documentElement.scrollWidth") <= 390)
    if not admin.get_by_test_id("language-en").is_visible():
        admin.click("[data-testid=open-menu]")
    admin.click("[data-testid=language-en]")
    expect(admin.locator("html")).to_have_attribute("lang", "en")
    check("phone, English", True, admin)
    admin.set_viewport_size({"width": 1280, "height": 900})
    check("desk, English", True, admin)
    sign_in(admin, ADMIN)

    # 2. A booker, found and suspended with a reason.
    booker = browser.new_page()
    email = new_booker(booker)

    admin.goto(f"{BASE}/admin/users")
    admin.click("[data-testid=admin-tab-users]")
    admin.fill("[data-testid=user-query]", email)
    admin.click("[data-testid=user-search]")
    row = admin.locator("[data-testid=user-list] li.user").filter(has_text=email).first
    expect(row).to_be_visible()
    user_id = row.get_attribute("data-testid").removeprefix("user-")
    admin.click(f"[data-testid=open-user-{user_id}]")
    admin.fill("[data-testid=standing-reason]", "ทดสอบการระงับบัญชี")
    admin.click("[data-testid=standing-decide]")
    expect(admin.get_by_test_id("suspended-badge")).to_be_visible()
    check("an admin suspends an account with a reason", True, admin)

    # 3. The suspended account cannot sign in, and is told why.
    booker.goto(f"{BASE}/login")
    with booker.expect_response(lambda r: r.url.endswith("/api/auth/login")) as answer:
        login(booker, email)
    body = answer.value.json()
    check("the suspended account cannot sign in",
          answer.value.status == 403 and body.get("code") == "auth.account_suspended")
    expect(booker.get_by_test_id("form-error")).to_be_visible()
    check("and the login page says so", True, booker)

    # 4. Let back in, they can.
    admin.fill("[data-testid=standing-reason]", "ตรวจแล้ว ยกเลิกการระงับ")
    admin.click("[data-testid=standing-decide]")
    expect(admin.get_by_test_id("suspended-badge")).to_have_count(0)
    detail = admin.request.get(f"{BASE}/api/admin/users/{user_id}").json()
    check("the history holds both decisions", [c["suspended"] for c in detail["history"]] == [False, True])

    booker.goto(f"{BASE}/login")
    with booker.expect_response(lambda r: r.url.endswith("/api/auth/login")) as answer:
        login(booker, email)
    check("reinstated, they sign in again", answer.value.status == 204)

    browser.close()

check.summarise()
