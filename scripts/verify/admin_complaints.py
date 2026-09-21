"""Complaints, and the one way the platform sees a slip (US-22)."""

import datetime

from harness import (
    BASE,
    Checks,
    as_upload,
    ensure_bookable,
    new_booker,
    real_jpeg,
    seeded_venue_id,
    send_slip,
    sign_in,
    take_first_free_hour,
    venue_today,
)
from playwright.sync_api import expect, sync_playwright

check = Checks(__file__)
ADMIN = "admin@courtpaka.local"
tomorrow = venue_today() + datetime.timedelta(days=1)

with sync_playwright() as p:
    browser = p.chromium.launch()
    venue_id = seeded_venue_id(browser.new_page())
    ensure_bookable(browser, venue_id)

    # A booking a booker paid for, the way a booker does.
    booker = browser.new_page(viewport={"width": 390, "height": 844})
    sign_in(booker, new_booker(booker))
    booking = take_first_free_hour(booker, venue_id, tomorrow).json()
    sent = send_slip(booker, as_upload("slip.jpg", real_jpeg(), "image/jpeg"))
    check("a booker sends a slip", sent.status == 200)

    admin = browser.new_page(viewport={"width": 1280, "height": 900})
    sign_in(admin, ADMIN)
    admin.goto(f"{BASE}/admin/complaints")

    # 1. Opened against the booking, as pasted from the message it came in.
    admin.fill("[data-testid=complaint-booking]", f"  {booking['id']}  ")
    admin.click("[data-testid=channel-Line]")
    admin.fill("[data-testid=complaint-details]", "โอนแล้ว แต่สนามยังไม่ยืนยัน")
    with admin.expect_response(
        lambda r: r.url.endswith("/api/admin/complaints") and r.request.method == "POST"
    ) as opened:
        admin.click("[data-testid=complaint-open]")
    complaint = opened.value.json()
    check("an admin opens a complaint against a booking", opened.value.status == 201)
    expect(admin.get_by_test_id("complaint-detail")).to_be_visible()
    check("with the booking's history beside it", admin.get_by_test_id("detail-history")
          .locator("li").count() == len(complaint["booking"]["history"]), admin)

    # 2. The slip is not fetched until pressed; pressing records the look.
    views_before = len(complaint["slipViewings"])
    admin.click("[data-testid=see-slip]")
    expect(admin.get_by_test_id("slip-image")).to_be_visible()
    expect(admin.get_by_test_id("slip-viewings").locator("li")).to_have_count(views_before + 1)
    check("the slip shows on a press, and the look is recorded", views_before == 0, admin)

    # 3. Resolved, the slip is closed to the platform again.
    admin.fill("[data-testid=resolution]", "ติดต่อสนามแล้ว สนามยืนยันการชำระ")
    admin.click("[data-testid=resolve]")
    expect(admin.get_by_test_id("detail-resolution")).to_be_visible()
    expect(admin.get_by_test_id("see-slip")).to_have_count(0)
    check("resolving closes the complaint with what was done", True, admin)
    closed = admin.request.get(f"{BASE}/api/admin/complaints/{complaint['id']}/slip")
    check("and the slip is refused once it is resolved",
          closed.status == 409 and closed.json().get("code") == "complaint.not_open")

    # 4. The screens a design review asks for.
    admin.goto(f"{BASE}/admin/complaints")
    admin.click("[data-testid=filter-All]")
    admin.click(f"[data-testid=complaint-{complaint['id']}]")
    expect(admin.get_by_test_id("complaint-detail")).to_be_visible()
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

    browser.close()

check.summarise()
