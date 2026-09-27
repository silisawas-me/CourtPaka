"""The two doors into the venue side: admin (owner/manager) and staff (docs/plan/cut-booker.md)."""

from harness import BASE, OWNER, PASSWORD, STAFF, Checks, seeded_venue_id
from playwright.sync_api import sync_playwright

check = Checks(__file__)


def through(page, door: str, email: str) -> None:
    """Signs in at one of the two doors on the front page, as a person would."""
    page.goto(BASE)
    page.click(f"[data-testid=home-door-{door}]")
    page.wait_for_selector("#email")
    page.fill("#email", email)
    page.fill("#password", PASSWORD)
    page.click("button[type=submit]")


with sync_playwright() as p:
    browser = p.chromium.launch()
    venue_id = seeded_venue_id(browser.new_page())

    front = browser.new_page()
    front.goto(BASE)
    check("the front page has an admin door and a staff door, and no sign-up",
          front.locator("[data-testid=home-door-admin]").count() == 1
          and front.locator("[data-testid=home-door-staff]").count() == 1
          and front.locator("a[href^='/register']").count() == 0, front)

    owner = browser.new_page()
    through(owner, "admin", OWNER)
    owner.wait_for_url(f"{BASE}/venues")
    check("the owner comes in at the admin door to their venues", True, owner)

    staff = browser.new_page()
    through(staff, "admin", STAFF)
    staff.wait_for_selector("[data-testid=form-error]")
    signed_in = staff.request.get(f"{BASE}/api/auth/me").status == 200
    check("staff are turned away from the admin door, and left signed out",
          not signed_in and "/login" in staff.url, staff)

    staff.goto(BASE)
    through(staff, "staff", STAFF)
    staff.wait_for_url("**/venues/**")
    mine = staff.request.get(f"{BASE}/api/venues/mine").json()
    wanted = f"{BASE}/venues/{mine[0]['id']}/bookings" if len(mine) == 1 else f"{BASE}/venues"
    check("staff come in at the staff door to their venue's timeline",
          staff.url.split("?")[0] == wanted, staff)
    check("the seeded staff work at the seeded venue",
          any(one["id"] == venue_id for one in mine))

    # The pages the booker used to have are gone: their addresses land on the front page.
    for old in ("/book", f"/book/{venue_id}", "/bookings"):
        owner.goto(BASE + old)
        owner.wait_for_timeout(600)
        check(f"{old} is no longer a page", owner.url.rstrip("/") == BASE, owner)

    browser.close()

check.summarise()
