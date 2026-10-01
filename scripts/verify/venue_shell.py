"""The venue's own shell: a sidebar on a desk, a bar under a thumb on a phone (US-25), and the
four sections that are the whole app since the pages under "อื่น ๆ" went (2026-10-02)."""

import time

from harness import (
    BASE,
    OWNER,
    Checks,
    control,
    seeded_venue_id,
    sign_in,
)
from playwright.sync_api import sync_playwright

check = Checks(__file__)

DESK = {"width": 1280, "height": 900}
PHONE = {"width": 390, "height": 844}


def lands(page, url: str, wanted: str) -> bool:
    """Opens `url` and answers whether the app settled on `wanted` (query and fragment aside)."""
    page.goto(BASE + url)
    try:
        page.wait_for_url(lambda now: now.split("?")[0].split("#")[0] == BASE + wanted, timeout=10_000)
        return True
    except Exception:
        return False


with sync_playwright() as p:
    browser = p.chromium.launch()
    venue_id = seeded_venue_id(browser.new_page())

    desk = browser.new_page(viewport=DESK)
    sign_in(desk, OWNER)

    desk.goto(f"{BASE}/venues/{venue_id}/timeline")
    desk.wait_for_selector("[data-testid=nav-schedule]")
    check("the rail shows the design's four sections and nothing folded under them",
          desk.locator(".side-link.section").count() == 4
          and desk.locator("[data-testid=nav-more]").count() == 0, desk)
    check("the venue's doors stand down the side on a desk", desk.locator(".side").is_visible(), desk)
    # The bar above is not drawn beside a sidebar, so the rest of the app has to travel with it.
    check(
        "the app's own doors travel in the sidebar's foot",
        desk.locator("[data-testid=side-nav-account] [data-testid=side-sign-out]").count() == 1,
    )
    check(
        "the schedule is marked as the section being read",
        "on" in (desk.locator("[data-testid=nav-schedule]").get_attribute("class") or ""),
    )
    # The bar above it would be a second navigation saying the same thing.
    check("the top bar stands down on a desk", not desk.locator("mat-toolbar.bar").is_visible())
    check("the bottom bar is for thumbs, not desks", not desk.locator(".tabs").is_visible())

    # Every door leads where it says, which is the only thing a list of links can get wrong —
    # so the page it lands on is named here, not merely required to be one of this venue's.
    doors = {
        "nav-pricing": "pricing",
        "nav-dashboard": "dashboard",
        "nav-packages": "packages",
        "nav-schedule": "timeline",
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

    # The pages that were folded under "อื่น ๆ" are gone. Their addresses — the venue's own emails
    # among them — still land somewhere a person can work from.
    gone = {
        "": "timeline",
        "/settings": "timeline",
        "/slip-queue": "timeline",
        "/money": "timeline",
        "/series": "timeline",
        "/shop": "timeline",
        "/closures": "timeline",
        "/report": "dashboard",
        "/package-board": "packages",
    }
    # The booking list is the schedule's third view, not a gone page (the booking list's design).
    check(
        "/venues/{id}/bookings is the booking list",
        lands(desk, f"/venues/{venue_id}/bookings", f"/venues/{venue_id}/bookings"),
        desk,
    )
    check("the schedule has three views: timeline, now, list",
          all(desk.locator(f"[data-testid=view-{view}]").count() == 1
              for view in ("timeline", "now", "bookings")), desk)

    for old, page in gone.items():
        check(
            f"/venues/{{id}}{old} lands on {page}",
            lands(desk, f"/venues/{venue_id}{old}", f"/venues/{venue_id}/{page}"),
            desk,
        )

    # Switching branch on the top bar keeps the page and loads the other venue (router reuse is
    # the case that used to break). A second venue of the owner's own, made if there is none.
    mine = desk.request.get(f"{BASE}/api/venues/mine").json()
    other = next((v for v in mine if v["id"] != venue_id), None)
    if other is None:
        code = f"S{int(time.time()) % 100000}"
        # Applying is its own page (US-10); the owner app's first page no longer links to it.
        desk.goto(f"{BASE}/venues/apply")
        desk.wait_for_selector("[data-testid=apply]")
        desk.fill("#code", code)
        desk.fill("#name", f"Second Court {code}")
        desk.fill("#address-line", "9 ถนนพระราม 4")
        desk.fill("#district", "ปทุมวัน")
        desk.fill("#province", "กรุงเทพมหานคร")
        desk.fill("#promptpay-id", "0812345678")
        desk.fill("#promptpay-name", "บริษัท ทดสอบ จำกัด")
        desk.fill("#legal-name", "บริษัท ทดสอบ จำกัด")
        desk.fill("#tax-id", "0105561000000")
        desk.click("[data-testid=copy-address]")
        control(desk, "accepts-agreement").click()
        desk.click("[data-testid=apply]")
        # Creating a venue sends the owner to it, and a venue's own address is its schedule now.
        desk.wait_for_url("**/timeline")
        other = next(v for v in desk.request.get(f"{BASE}/api/venues/mine").json()
                     if v["code"] == code)
        check("a venue applied for opens on its schedule",
              desk.url == f"{BASE}/venues/{other['id']}/timeline", desk)

    desk.goto(f"{BASE}/venues/{venue_id}/timeline")
    desk.click(f"[data-testid=pill-{other['id']}]")
    desk.wait_for_url(f"{BASE}/venues/{other['id']}/timeline")
    desk.wait_for_selector(f"[data-testid=pill-{other['id']}].on")
    desk.click(f"[data-testid=pill-{venue_id}]")
    desk.wait_for_url(f"{BASE}/venues/{venue_id}/timeline")
    check("switching branch on the top bar stays on the schedule and marks the branch",
          "on" in (desk.locator(f"[data-testid=pill-{venue_id}]").get_attribute("class") or ""),
          desk)

    # Every venue at once stands in the same frame, with the schedule as its page
    # (docs/plan/owner-app.md).
    desk.goto(f"{BASE}/venues")
    desk.wait_for_selector("[data-testid=owner-top]")
    check(
        "every venue at once stands in the owner app's frame",
        desk.locator(".side").is_visible()
        and "on" in (desk.locator("[data-testid=pill-all]").get_attribute("class") or "")
        if desk.locator("[data-testid=pill-all]").count()
        else desk.locator(".side").is_visible(),
        desk,
    )

    # A page outside any venue keeps the bar and is given no rail.
    desk.goto(f"{BASE}/account")
    desk.wait_for_timeout(1200)
    check(
        "a page outside a venue keeps the bar and is given no shift to work",
        desk.locator("mat-toolbar.bar").is_visible() and desk.locator(".side").count() == 0,
        desk,
    )

    phone = browser.new_page(viewport=PHONE)
    sign_in(phone, OWNER)
    phone.goto(f"{BASE}/venues/{venue_id}/timeline")
    phone.wait_for_selector("[data-testid=tab-schedule]")
    check("the sections lie along the bottom on a phone", phone.locator(".tabs").is_visible(), phone)
    check("the phone has the same four sections", phone.locator(".tabs .tab").count() == 4)
    check("the sidebar is not drawn on a phone", not phone.locator(".side").is_visible())
    check(
        "the schedule's tab is marked as the one being read",
        "on" in (phone.locator("[data-testid=tab-schedule]").get_attribute("class") or ""),
    )
    phone.click("[data-testid=tab-dashboard]")
    phone.wait_for_url(f"{BASE}/venues/{venue_id}/dashboard**")
    check("a tab carries the thumb to that page", phone.locator(".tabs").is_visible(), phone)

    browser.close()

check.summarise()
