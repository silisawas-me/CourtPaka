"""Paying for a hold: the countdown, sending the slip, and what the venue's queue is told (US-04)."""

import datetime

from harness import (
    BASE,
    Checks,
    ensure_bookable,
    as_upload,
    new_booker,
    seeded_venue_id,
    send_slip,
    sign_in,
    take_first_free_hour,
    venue_today,
)
from playwright.sync_api import sync_playwright

check = Checks(__file__)
tomorrow = venue_today() + datetime.timedelta(days=1)

# The first bytes are what the server reads to recognise a file, so a real header is enough.
JPEG = bytes([0xFF, 0xD8, 0xFF, 0xE0]) + b"JFIF " + b"slip"
PNG = bytes([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]) + b"IHDR slip"


with sync_playwright() as p:
    browser = p.chromium.launch()
    page = browser.new_page(viewport={"width": 390, "height": 844})

    venue_id = seeded_venue_id(page)
    ensure_bookable(browser, venue_id)

    # 1. Holding an hour lands on the page that pays for it.
    sign_in(page, new_booker(page))
    booking = take_first_free_hour(page, venue_id, tomorrow).json()
    check("holding an hour opens the booking", f"/bookings/{booking['id']}" in page.url, page)
    check(
        "which says what is being waited for",
        "รอชำระเงิน" in page.locator("[data-testid=booking-status]").inner_text(),
    )
    countdown = page.locator("[data-testid=countdown]").inner_text()
    minutes = int(countdown.split(":")[0].split()[-1])
    check("and counts down from fifteen minutes", 13 <= minutes <= 15, page)
    check(
        "the QR is honest about not being set up yet",
        page.locator("[data-testid=qr-pending]").count() == 1,
    )

    # 2. A file that is not a picture is refused, whatever it is called.
    bad = send_slip(page, as_upload("not-a-slip.jpg", b"<script>x</script>", "image/jpeg"))
    check("a file that is not a slip is refused", bad.status == 400)
    check(
        "and the page says why in the reader's language",
        "JPG" in page.locator("[data-testid=upload-error]").inner_text(),
        page,
    )

    # 3. The real slip goes through and moves the booking into the venue's queue.
    sent = send_slip(page, as_upload("slip.jpg", JPEG, "image/jpeg"))
    check("a real slip is accepted", sent.status == 200)
    page.wait_for_selector("[data-testid=slip-sent]")
    # Asked of the server rather than read off the badge: the words on the badge are the
    # reader's language and change with it, and what this check is about is the booking.
    check(
        "and the booking is now waiting for the venue",
        page.request.get(f"{BASE}/api/bookings/{booking['id']}").json()["status"]
        == "PendingVerification",
        page,
    )
    check(
        "a better picture can still be sent",
        "ส่งสลิปใหม่" in page.locator("[data-testid=send-slip]").inner_text(),
    )

    # 4. Replacing it keeps both, and the one served back is the newest.
    replaced = send_slip(page, as_upload("slip.png", PNG, "image/png"))
    check("the slip can be replaced", replaced.status == 200)
    served = page.request.get(f"{BASE}/api/bookings/{booking['id']}/slip")
    check("and the newest one is what is served back", served.body() == PNG, page)

    # 5. The slip belongs to its booker and to nobody else.
    stranger = browser.new_page()
    sign_in(stranger, new_booker(stranger))
    check(
        "another booker cannot read the booking",
        stranger.request.get(f"{BASE}/api/bookings/{booking['id']}").status == 404,
    )
    check(
        "nor the slip",
        stranger.request.get(f"{BASE}/api/bookings/{booking['id']}/slip").status == 404,
    )
    check(
        "and neither can someone with no account",
        browser.new_page().request.get(f"{BASE}/api/bookings/{booking['id']}/slip").status == 401,
    )

    # 6. The same picture at the same venue is taken, and flagged where only the venue will see it.
    second = browser.new_page(viewport={"width": 390, "height": 844})
    sign_in(second, new_booker(second))
    take_first_free_hour(second, venue_id, tomorrow, skip=1)
    duplicate = send_slip(second, as_upload("same.png", PNG, "image/png"))
    check("the same picture is still accepted", duplicate.status == 200)
    check(
        "and the booker is told nothing about it",
        second.locator("[data-testid=upload-error]").count() == 0
        and second.locator("[data-testid=slip-sent]").count() == 1,
        second,
    )

    browser.close()

check.summarise()
