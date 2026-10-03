"""Inviting staff by a LINE link, with no email (docs/plan/thai-fit.md T1).

The owner makes a link on the staff tab of "ราคา & ตั้งค่า", a stranger opens it, signs up with a
phone number from the sign-in page the link leads to, takes the seat, and signs in again with the
number. The new member is taken off the seeded venue at the end, so the other scripts see the
team they expect.
"""

import random

from harness import BASE, OWNER, PASSWORD, Checks, seeded_venue_id, sign_in
from playwright.sync_api import expect, sync_playwright

check = Checks(__file__)
phone = "08" + str(random.randint(10_000_000, 99_999_999))

with sync_playwright() as p:
    browser = p.chromium.launch()
    venue_id = seeded_venue_id(browser.new_page())
    api = f"{BASE}/api/venues/{venue_id}"

    desk = browser.new_page(viewport={"width": 1440, "height": 900})
    sign_in(desk, OWNER)
    desk.goto(f"{BASE}/venues/{venue_id}/pricing")
    desk.click("[data-testid=pricing-tab-staff]")
    desk.wait_for_selector("[data-testid=venue-staff]")
    check("the owner has a staff tab", True, desk)

    desk.fill("[data-testid=invite-name]", "บอมทดสอบ")
    desk.fill("[data-testid=invite-phone]", phone)
    with desk.expect_response(lambda r: r.url == f"{api}/invitations" and r.request.method == "POST") as made:
        desk.click("[data-testid=invite-create]")
    invitation = made.value.json()
    link = invitation["link"]
    desk.wait_for_selector("[data-testid=invite-made]")
    check("a link comes back with no email asked for", invitation["email"] is None and "/venue-invitation?" in link, desk)
    check("and LINE is offered the link",
          desk.get_attribute("[data-testid=invite-line]", "href").startswith("https://line.me/R/msg/text/?"))
    expect(desk.locator(f"[data-testid=invitation-{invitation['id']}]")).to_have_count(1)

    # Somebody with only a phone opens the link on their own phone.
    stranger = browser.new_context(viewport={"width": 390, "height": 844}).new_page()
    stranger.goto(link)
    stranger.wait_for_selector("[data-testid=register-link]")
    stranger.click("[data-testid=register-link]")
    stranger.wait_for_selector("[data-testid=register-by-link]")
    stranger.fill("#phone", phone)
    stranger.fill("#password", PASSWORD)
    stranger.locator("mat-checkbox input").check()
    stranger.click("button[type=submit]")
    stranger.wait_for_selector("[data-testid=accept-success]")
    check("they sign up with the phone and take the seat in one go", True, stranger)

    members = desk.request.get(f"{api}/members").json()
    joined = next((m for m in members if m.get("phone") == phone), None)
    check("the team shows them by the name they were invited by",
          joined is not None and joined["name"] == "บอมทดสอบ" and joined["permissions"] == ["ManageBookings"])

    # Signed out, then back in with the number typed with dashes.
    stranger.request.post(f"{BASE}/api/auth/logout")
    stranger.goto(f"{BASE}/login?as=staff")
    stranger.fill("#email", f"{phone[:3]}-{phone[3:6]}-{phone[6:]}")
    stranger.fill("#password", PASSWORD)
    stranger.click("button[type=submit]")
    stranger.wait_for_url(lambda url: "/login" not in url)
    check("they sign in with the phone number", f"/venues/{venue_id}" in stranger.url, stranger)

    expect(desk.locator(f"[data-testid=invitation-{invitation['id']}]")).to_have_count(1)
    desk.reload()
    desk.click("[data-testid=pricing-tab-staff]")
    desk.wait_for_selector(f"[data-testid=staff-{joined['userId']}]")
    expect(desk.locator(f"[data-testid=invitation-{invitation['id']}]")).to_have_count(0)
    check("the used link leaves the pending list, the person is on the team", True, desk)

    # A second link, taken back before anybody uses it.
    desk.fill("[data-testid=invite-name]", "ยกเลิกทดสอบ")
    with desk.expect_response(lambda r: r.url == f"{api}/invitations" and r.request.method == "POST") as again:
        desk.click("[data-testid=invite-create]")
    second = again.value.json()
    desk.click(f"[data-testid=invitation-revoke-{second['id']}]")
    expect(desk.locator(f"[data-testid=invitation-{second['id']}]")).to_have_count(0)
    late = browser.new_context().new_page()
    sign_in(late, OWNER)
    used = late.request.post(f"{BASE}/api/venues/invitations/accept",
                             data={"invitationId": second["id"], "token": second["link"].split("token=")[1]})
    check("a revoked link opens nothing", used.status == 400)

    desk.click(f"[data-testid=staff-remove-{joined['userId']}]")
    desk.click(f"[data-testid=staff-remove-{joined['userId']}]")
    expect(desk.locator(f"[data-testid=staff-{joined['userId']}]")).to_have_count(0)
    check("and the owner takes them off the team again", True, desk)

    browser.close()

check.summarise()
