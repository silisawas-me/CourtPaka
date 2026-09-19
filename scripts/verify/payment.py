"""Paying for a hold: the countdown, sending the slip, and what the venue's queue is told (US-04)."""

import datetime
import pathlib
import tempfile

from harness import BASE, SEEDED_VENUE, Checks, new_booker, sign_in, venue_today
from playwright.sync_api import sync_playwright

check = Checks(__file__)
tomorrow = venue_today() + datetime.timedelta(days=1)

# The first bytes are what the server reads to recognise a file, so a real header is enough.
JPEG = bytes([0xFF, 0xD8, 0xFF, 0xE0]) + b"JFIF " + b"slip"
PNG = bytes([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]) + b"IHDR slip"


def written(name: str, content: bytes) -> str:
    path = pathlib.Path(tempfile.gettempdir()) / name
    path.write_bytes(content)
    return str(path)


def hold_an_hour(page, venue_id, hour_offset=0):
    """Takes the first free hour on tomorrow's grid, through the pages a booker uses."""
    page.goto(f"{BASE}/book/{venue_id}?date={tomorrow.isoformat()}")
    page.wait_for_selector("[data-testid=availability-grid]")
    cell = page.locator("td.free").nth(hour_offset).get_attribute("data-testid")
    page.click(f"[data-testid={cell}] button")
    page.wait_for_selector("[data-testid=booking-summary]")
    with page.expect_response(lambda response: response.url.endswith("/api/bookings")) as answer:
        page.click("[data-testid=book]")
    page.wait_for_selector("[data-testid=countdown]")
    return answer.value.json()


with sync_playwright() as p:
    browser = p.chromium.launch()
    page = browser.new_page(viewport={"width": 390, "height": 844})

    venue_id = page.request.get(f"{BASE}/api/venues/search?q={SEEDED_VENUE}").json()[0]["id"]

    # 1. Holding an hour lands on the page that pays for it.
    sign_in(page, new_booker(page))
    booking = hold_an_hour(page, venue_id)
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
    with page.expect_response(lambda r: r.url.endswith("/slip")) as bad:
        page.set_input_files("[data-testid=send-slip] input", written("not-a-slip.jpg", b"<script>x</script>"))
    check("a file that is not a slip is refused", bad.value.status == 400)
    check(
        "and the page says why in the reader's language",
        "JPG" in page.locator("[data-testid=upload-error]").inner_text(),
        page,
    )

    # 3. The real slip goes through and moves the booking into the venue's queue.
    with page.expect_response(lambda r: r.url.endswith("/slip")) as sent:
        page.set_input_files("[data-testid=send-slip] input", written("slip.jpg", JPEG))
    check("a real slip is accepted", sent.value.status == 200)
    page.wait_for_selector("[data-testid=slip-sent]")
    check(
        "and the booking is now waiting for the venue",
        "รอสนามตรวจ" in page.locator("[data-testid=booking-status]").inner_text(),
        page,
    )
    check(
        "a better picture can still be sent",
        "ส่งสลิปใหม่" in page.locator("[data-testid=send-slip]").inner_text(),
    )

    # 4. Replacing it keeps both, and the one served back is the newest.
    with page.expect_response(lambda r: r.url.endswith("/slip")) as replaced:
        page.set_input_files("[data-testid=send-slip] input", written("slip.png", PNG))
    check("the slip can be replaced", replaced.value.status == 200)
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
    other_booking = hold_an_hour(second, venue_id, hour_offset=1)
    with second.expect_response(lambda r: r.url.endswith("/slip")) as duplicate:
        second.set_input_files("[data-testid=send-slip] input", written("same.png", PNG))
    check("the same picture is still accepted", duplicate.value.status == 200)
    check(
        "and the booker is told nothing about it",
        second.locator("[data-testid=upload-error]").count() == 0
        and "รอสนามตรวจ" in second.locator("[data-testid=booking-status]").inner_text(),
        second,
    )

    browser.close()

check.summarise()
