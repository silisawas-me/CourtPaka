"""The venue's own shell: a sidebar on a desk, a bar under a thumb on a phone (US-25)."""

from harness import (
    BASE,
    OWNER,
    Checks,
    seeded_venue_id,
    sign_in,
)
from playwright.sync_api import sync_playwright

check = Checks(__file__)

DESK = {"width": 1280, "height": 900}
PHONE = {"width": 390, "height": 844}

with sync_playwright() as p:
    browser = p.chromium.launch()
    venue_id = seeded_venue_id(browser.new_page())

    desk = browser.new_page(viewport=DESK)
    sign_in(desk, OWNER)

    desk.goto(f"{BASE}/venues/{venue_id}/bookings")
    desk.wait_for_selector("[data-testid=nav-slip-queue]")
    check("the venue's doors stand down the side on a desk", desk.locator(".side").is_visible(), desk)
    # The bar above is not drawn beside a sidebar, so the rest of the app has to travel with it.
    check(
        "the app's own doors travel in the sidebar's foot",
        desk.locator("[data-testid=side-nav-account] [data-testid=side-nav-venues]").count() == 1,
    )
    check(
        "the day's door is marked as the one being read",
        "on" in (desk.locator("[data-testid=nav-venue-bookings]").get_attribute("class") or ""),
    )
    # The bar above it would be a second navigation saying the same thing.
    check("the top bar stands down on a desk", not desk.locator("mat-toolbar.bar").is_visible())
    check("the bottom bar is for thumbs, not desks", not desk.locator(".tabs").is_visible())

    # Every door leads where it says, which is the only thing a list of links can get wrong —
    # so the page it lands on is named here, not merely required to be one of this venue's.
    doors = {
        "nav-slip-queue": "slip-queue",
        "nav-money": "money",
        "nav-dashboard": "dashboard",
        "nav-settings": "settings",
        "nav-closures": "closures",
    }
    for door, page in doors.items():
        desk.click(f"[data-testid={door}]")
        wanted = f"{BASE}/venues/{venue_id}/{page}"
        try:
            desk.wait_for_url(f"{wanted}**", timeout=10_000)
            landed = True
        except Exception:
            landed = False
        check(f"{door} leads to {page}", landed)

    # A page outside any venue keeps the bar and is given no shift to work.
    desk.goto(f"{BASE}/venues")
    desk.wait_for_selector("[data-testid=venue-list], [data-testid=no-venues]")
    check(
        "a page outside a venue keeps the bar and is given no shift to work",
        desk.locator("mat-toolbar.bar").is_visible() and desk.locator(".side").count() == 0,
        desk,
    )

    phone = browser.new_page(viewport=PHONE)
    sign_in(phone, OWNER)
    phone.goto(f"{BASE}/venues/{venue_id}/bookings")
    phone.wait_for_selector("[data-testid=tab-venue-bookings]")
    check("the five doors lie along the bottom on a phone", phone.locator(".tabs").is_visible(), phone)
    check("the sidebar is not drawn on a phone", not phone.locator(".side").is_visible())
    check(
        "the day's tab is marked as the one being read",
        "on" in (phone.locator("[data-testid=tab-venue-bookings]").get_attribute("class") or ""),
    )
    phone.click("[data-testid=tab-money]")
    phone.wait_for_url(f"{BASE}/venues/{venue_id}/money**")
    check("a tab carries the thumb to that page", phone.locator(".tabs").is_visible(), phone)

    browser.close()

check.summarise()
