"""Signing in with LINE, end to end on the stack, against the stand-in LINE (US-01).

The local stack runs App__Line__UseDevelopmentFake, so /api/dev/line/authorize stands in for
LINE's consent screen: it asks who to be, and allows or denies. Everything on this side of it —
the state cookie, the account made only after the policy is accepted, the phone number a venue
would call, and deleting an account that has no password — is the real thing.
"""

import uuid

from harness import BASE, Checks, ensure_bookable, seeded_venue_id
from playwright.sync_api import sync_playwright

check = Checks(__file__)


def be_at_line(page, sub: str, name: str = "ปกป้อง", email: str | None = None, allow: bool = True):
    """Fills in the stand-in LINE screen and comes back, the way a person would."""
    page.wait_for_selector("#sub")
    page.fill("#sub", sub)
    page.fill("#name", name)
    if email:
        page.fill("#email", email)
    page.click("#allow" if allow else "#deny")


with sync_playwright() as p:
    browser = p.chromium.launch()
    ensure_bookable(browser, seeded_venue_id(browser.new_page()))

    page = browser.new_page(viewport={"width": 390, "height": 844})
    sub = f"U{uuid.uuid4().hex}"
    line_email = f"line-{uuid.uuid4().hex[:10]}@example.com"

    # 1. The way in offers LINE, because this stack has a channel configured.
    page.goto(f"{BASE}/login")
    page.wait_for_selector("[data-testid=line-sign-in]")
    check("the sign-in page offers LINE", True, page)

    # 2. LINE's answer is not an account yet: the policy has to be accepted here.
    page.click("[data-testid=line-sign-in]")
    be_at_line(page, sub, email=line_email)
    page.wait_for_url(f"{BASE}/register/line**")
    check("LINE sends the newcomer to the consent step", True, page)
    check(
        "and nobody is signed in yet",
        page.request.get(f"{BASE}/api/auth/me").status == 401,
    )
    check(
        "the address LINE shared is shown",
        line_email in page.locator("[data-testid=line-shared-email]").inner_text(),
    )

    # 3. Refusing the policy makes no account.
    page.click("[data-testid=line-complete]")
    page.wait_for_selector("[data-testid=policy-error]")
    check("no account without the policy", page.request.get(f"{BASE}/api/auth/me").status == 401)

    page.fill("[data-testid=line-phone] input, input[data-testid=line-phone]", "081-234-5678")
    page.click("[data-testid=line-accept-policy] input")
    page.click("[data-testid=line-complete]")
    page.wait_for_url(f"{BASE}/")

    me = page.request.get(f"{BASE}/api/auth/me").json()
    check("accepting it makes the account", me["signsInWithLine"] is True, page)
    check("with the address LINE shared, unverified", me["email"] == line_email)
    check("and the number as a venue would dial it", me["phoneNumber"] == "0812345678")
    check("which is enough to book with", me["cannotBookBecause"] is None)

    # 4. The same LINE account signs straight in next time, on a browser that knows nothing.
    second = browser.new_context()
    later = second.new_page()
    later.goto(f"{BASE}/login")
    later.click("[data-testid=line-sign-in]")
    be_at_line(later, sub)
    later.wait_for_url(f"{BASE}/")
    check(
        "signing in again is the same account",
        later.request.get(f"{BASE}/api/auth/me").json()["id"] == me["id"],
        later,
    )

    # 5. Saying no at LINE says so, and signs nobody in.
    third = browser.new_context()
    refused = third.new_page()
    refused.goto(f"{BASE}/login")
    refused.click("[data-testid=line-sign-in]")
    be_at_line(refused, f"U{uuid.uuid4().hex}", allow=False)
    refused.wait_for_selector("[data-testid=form-error]")
    # The code in the address is the contract; the sentence on screen is a translation of it.
    check("saying no at LINE says so", "line=auth.line_denied" in refused.url, refused)
    check("and signs nobody in", refused.request.get(f"{BASE}/api/auth/me").status == 401)

    # 6. Deleting an account with no password: confirmed by going back to LINE.
    page.goto(f"{BASE}/account")
    page.wait_for_selector("[data-testid=confirm-with-line]")
    page.click("[data-testid=delete-understood] input")
    page.click("[data-testid=delete-account]")
    page.wait_for_selector("[data-testid=delete-error]")
    check(
        "a LINE account is not deleted on the session alone",
        page.request.get(f"{BASE}/api/auth/me").status == 200,
        page,
    )

    page.click("[data-testid=confirm-with-line]")
    be_at_line(page, sub)
    page.wait_for_selector("[data-testid=line-confirmed]")
    page.click("[data-testid=delete-understood] input")
    page.click("[data-testid=delete-account]")
    page.wait_for_url(f"{BASE}/?deleted=1")
    check("after confirming at LINE it is deleted", True, page)

    # 7. The same LINE account afterwards is a newcomer, not the deleted one.
    fourth = browser.new_context()
    again = fourth.new_page()
    again.goto(f"{BASE}/login")
    again.click("[data-testid=line-sign-in]")
    be_at_line(again, sub)
    again.wait_for_url(f"{BASE}/register/line**")
    check("the forgotten LINE id comes back as a newcomer", True, again)

    browser.close()

check.summarise()
