"""A person asking to be forgotten (PDPA, PRD 8, S-15)."""

from harness import BASE, OWNER, PASSWORD, Checks, login, new_booker, sign_in
from playwright.sync_api import expect, sync_playwright

check = Checks(__file__)

with sync_playwright() as p:
    browser = p.chromium.launch()

    page = browser.new_page(viewport={"width": 1280, "height": 900})
    email = new_booker(page)
    sign_in(page, email)

    page.click("[data-testid=nav-account]")
    expect(page.get_by_test_id("account-email")).to_have_text(email)
    check("the account page names the account", True, page)

    # The screens a design review asks for, before anything is deleted.
    page.set_viewport_size({"width": 390, "height": 844})
    check("phone, Thai", True, page)
    check("nothing on a phone scrolls the page sideways",
          page.evaluate("document.documentElement.scrollWidth") <= 390)
    if not page.get_by_test_id("language-en").is_visible():
        page.click("[data-testid=open-menu]")
    page.click("[data-testid=language-en]")
    expect(page.locator("html")).to_have_attribute("lang", "en")
    check("phone, English", True, page)
    page.set_viewport_size({"width": 1280, "height": 900})
    check("desk, English", True, page)
    if not page.get_by_test_id("language-th").is_visible():
        page.click("[data-testid=open-menu]")
    page.click("[data-testid=language-th]")

    # A wrong password is refused, and nothing happens.
    page.get_by_test_id("delete-understood").get_by_role("checkbox").check()
    page.fill("[data-testid=delete-password]", "NotThePassword1")
    page.click("[data-testid=delete-account]")
    expect(page.get_by_test_id("delete-error")).to_be_visible()
    check("a wrong password is refused, and said so", True, page)

    # The owner of a venue is told to settle the venue first.
    owner = browser.new_page()
    sign_in(owner, OWNER)
    refused = owner.request.post(f"{BASE}/api/auth/me/delete", data={"password": PASSWORD})
    check("a venue owner cannot delete the account yet",
          refused.status == 409 and refused.json().get("code") == "account.owns_a_venue")
    owner.close()

    # With the password, the account goes.
    page.fill("[data-testid=delete-password]", PASSWORD)
    with page.expect_response(lambda r: r.url.endswith("/api/auth/me/delete")) as deleted:
        page.click("[data-testid=delete-account]")
    check("with the password, the account is deleted", deleted.value.status == 204)
    expect(page.get_by_test_id("account-deleted")).to_be_visible()
    check("and home says it happened", True, page)

    # The address no longer signs in.
    page.goto(f"{BASE}/login")
    with page.expect_response(lambda r: r.url.endswith("/api/auth/login")) as answer:
        login(page, email)
    check("the address no longer signs in", answer.value.status == 401)

    browser.close()

check.summarise()
