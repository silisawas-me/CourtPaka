"""Adding staff with a six-digit passcode (docs/plan/thai-fit.md T1, replacing the LINE link).

The owner adds somebody on the staff tab of "ราคา & ตั้งค่า" and is shown a passcode once. The
person signs in at the staff door with their phone and that passcode, accepts the privacy policy
themselves on the first sign-in, changes the passcode under "บัญชี", and the owner can set a new
one when it is lost. The person is taken off the seeded venue at the end.
"""

import random

from harness import BASE, OWNER, Checks, seeded_venue_id, sign_in
from playwright.sync_api import expect, sync_playwright

check = Checks(__file__)
phone = "08" + str(random.randint(10_000_000, 99_999_999))


def staff_sign_in(page, passcode):
    page.goto(f"{BASE}/login?as=staff")
    page.fill("#email", f"{phone[:3]}-{phone[3:6]}-{phone[6:]}")
    page.fill("#password", passcode)
    page.click("button[type=submit]")


with sync_playwright() as p:
    browser = p.chromium.launch()
    venue_id = seeded_venue_id(browser.new_page())
    api = f"{BASE}/api/venues/{venue_id}"

    desk = browser.new_page(viewport={"width": 1440, "height": 900})
    sign_in(desk, OWNER)
    desk.goto(f"{BASE}/venues/{venue_id}/pricing?tab=staff")
    desk.wait_for_selector("[data-testid=venue-staff]")

    desk.fill("[data-testid=add-name]", "ฝนทดสอบ")
    desk.fill("[data-testid=add-phone]", phone)
    with desk.expect_response(lambda r: r.url == f"{api}/staff" and r.request.method == "POST") as made:
        desk.click("[data-testid=add-staff]")
    added = made.value.json()
    passcode = added["passcode"]
    desk.wait_for_selector("[data-testid=staff-made]")
    check("the owner adds somebody and is shown a six-digit passcode",
          made.value.status == 201 and len(passcode) == 6 and passcode.isdigit()
          and desk.inner_text("[data-testid=staff-passcode]").replace(" ", "") == passcode, desk)
    user_id = added["member"]["userId"]
    expect(desk.locator(f"[data-testid=staff-fresh-{user_id}]")).to_have_count(1)
    check("the new person shows as not signed in yet", True)

    # On their own phone: the staff door, the phone, the passcode.
    staff = browser.new_context(viewport={"width": 390, "height": 844}).new_page()
    staff_sign_in(staff, passcode)
    staff.wait_for_selector("[data-testid=welcome]")
    check("the first sign-in asks them to accept the privacy policy themselves", True, staff)
    staff.locator("[data-testid=welcome-accept] input").check()
    staff.click("[data-testid=welcome-start]")
    staff.wait_for_url(lambda url: f"/venues/{venue_id}" in url)
    check("and then they are at work in the venue", True, staff)

    # They change it to one of their own.
    mine = "246810" if passcode != "246810" else "135790"
    staff.goto(f"{BASE}/account")
    staff.fill("[data-testid=passcode-current]", passcode)
    staff.fill("[data-testid=passcode-new]", mine)
    staff.click("[data-testid=passcode-save]")
    staff.wait_for_selector("[data-testid=passcode-saved]")
    check("they change the passcode themselves", True, staff)

    old = browser.new_context().new_page()
    staff_sign_in(old, passcode)
    old.wait_for_timeout(1500)
    check("the passcode they were given no longer works", "/login" in old.url, old)

    # Lost: the owner sets a new one.
    desk.reload()
    desk.wait_for_selector(f"[data-testid=staff-passcode-{user_id}]")
    with desk.expect_response(lambda r: r.url.endswith(f"/members/{user_id}/passcode")) as reset:
        desk.click(f"[data-testid=staff-passcode-{user_id}]")
    fresh = reset.value.json()["passcode"]
    again = browser.new_context().new_page()
    staff_sign_in(again, fresh)
    again.wait_for_url(lambda url: "/login" not in url)
    check("the owner sets a new passcode and it works at once", f"/venues/{venue_id}" in again.url, again)

    desk.click(f"[data-testid=staff-remove-{user_id}]")
    desk.click(f"[data-testid=staff-remove-{user_id}]")
    expect(desk.locator(f"[data-testid=staff-{user_id}]")).to_have_count(0)
    check("and takes them off the team again", True, desk)

    browser.close()

check.summarise()
