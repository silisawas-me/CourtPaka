"""The venue looking at a slip and deciding (US-12)."""

import base64
import datetime
import uuid
from pathlib import Path

from harness import BASE, OWNER, SEEDED_VENUE, Checks, new_booker, sign_in, take_first_free_hour, venue_today
from playwright.sync_api import expect, sync_playwright

check = Checks(__file__)
tomorrow = venue_today() + datetime.timedelta(days=1)

# A real one-pixel JPEG. A header with text after it is not decodable, so "the slip is shown"
# would pass against a broken-image icon. The trailer after the end marker is ignored by decoders
# and makes this run's slip unlike any earlier run's — otherwise the duplicate flag, which is
# per venue and forever, would fire on the first booking too.
JPEG = base64.b64decode(Path(__file__).with_name("slip.jpg.b64").read_text()) + uuid.uuid4().bytes


def waiting_booking(browser, venue_id, skip=0):
    """A booking that has been paid for and is waiting for the venue, made the way a booker does."""
    page = browser.new_page(viewport={"width": 390, "height": 844})
    sign_in(page, new_booker(page))
    booking = take_first_free_hour(page, venue_id, tomorrow, skip=skip).json()
    page.wait_for_selector("[data-testid=countdown]")

    with page.expect_response(lambda response: response.url.endswith("/slip")) as sent:
        page.locator("[data-testid=send-slip] input").set_input_files(
            {"name": "slip.jpg", "mimeType": "image/jpeg", "buffer": JPEG}
        )
    if sent.value.status != 200:
        raise RuntimeError(f"Could not send a slip: {sent.value.status}")

    page.close()
    return booking


with sync_playwright() as p:
    browser = p.chromium.launch()

    venue_id = browser.new_page().request.get(
        f"{BASE}/api/venues/search?q={SEEDED_VENUE}"
    ).json()[0]["id"]

    # Whatever an earlier run left waiting is not this run's subject. Clearing it through the API
    # is what lets these checks be run again.
    staff = browser.new_page()
    sign_in(staff, OWNER)
    for waiting in staff.request.get(f"{BASE}/api/venues/{venue_id}/slip-queue").json():
        staff.request.post(
            f"{BASE}/api/venues/{venue_id}/slip-queue/{waiting['bookingId']}/confirm")
    staff.close()

    first = waiting_booking(browser, venue_id)
    second = waiting_booking(browser, venue_id, skip=1)

    # The venue's own staff open the queue.
    page = browser.new_page(viewport={"width": 1280, "height": 900})
    sign_in(page, OWNER)
    page.goto(f"{BASE}/venues")
    page.click(f"[data-testid=venue-list] a:has-text('{SEEDED_VENUE}')")
    page.wait_for_selector("[data-testid=slip-queue-link]")
    page.click("[data-testid=slip-queue-link]")
    page.wait_for_selector("[data-testid=decision]")

    check("the queue opens from the venue page", "/slip-queue" in page.url, page)
    check(
        "both waiting slips are in it",
        page.locator("[data-testid=queue] li").count() == 2,
    )
    check(
        "the oldest one is open first",
        page.locator("[data-testid=decision-baht]").inner_text() != "",
    )
    shown = page.locator("[data-testid=slip] img")
    expect(shown).to_be_visible()
    check(
        "the slip itself is shown, and the browser could decode it",
        shown.evaluate("img => img.complete && img.naturalWidth > 0"),
        page,
    )
    # By element, not by wording: the reader's language is their own, and the venue's staff may
    # well have set it to English.
    rows = page.locator("[data-testid=queue] li")
    check(
        "the one whose slip was seen before carries the flag",
        rows.nth(1).locator("[data-testid=flag-repeat]").count() == 1,
        page,
    )
    check(
        "and the first one does not",
        rows.nth(0).locator("[data-testid=flag-repeat]").count() == 0,
    )
    check(
        "the queue says when each slip arrived",
        ":" in page.locator("[data-testid=queue] li").nth(0).inner_text(),
    )

    # Turning one away needs a reason.
    page.click("[data-testid=reject]")
    page.click("[data-testid=reject-confirm]")
    expect(page.locator("[data-testid=reason-error]")).to_be_visible()
    check("a rejection with no reason is refused before it is sent", True, page)

    page.fill("#reason", "ยอดไม่ตรงกับที่ต้องจ่าย")
    page.locator("[data-testid=decision] input[type=radio]").nth(1).check()
    expect(page.locator("[data-testid=refund-note]")).to_be_visible()
    check("saying the money arrived says the refund out loud", True, page)

    with page.expect_response(lambda response: response.url.endswith("/reject")) as rejected:
        page.click("[data-testid=reject-confirm]")
    check("the rejection is accepted", rejected.value.status == 200)
    check(
        "the booking that owes money back says so",
        rejected.value.json()["refundDueBaht"] == rejected.value.json()["totalBaht"],
    )

    page.wait_for_selector("[data-testid=decision]")
    check(
        "the queue moves on to the next one by itself",
        page.locator("[data-testid=queue] li").count() == 1,
        page,
    )

    # Confirming empties it.
    with page.expect_response(lambda response: response.url.endswith("/confirm")) as confirmed:
        page.click("[data-testid=confirm]")
    check("confirming is accepted", confirmed.value.status == 200)
    check("and records the money as arrived", confirmed.value.json()["paymentState"] == "Received")

    page.wait_for_selector("[data-testid=queue-empty]")
    check("an empty queue says so", page.locator("[data-testid=queue-empty]").count() == 1, page)

    # The hours a rejected booking held are free again.
    day = page.request.get(
        f"{BASE}/api/venues/{venue_id}/availability?date={tomorrow.isoformat()}"
    ).json()
    freed = [
        hour
        for court in day["courts"]
        for hour in court["hours"]
        if hour["status"] == "Free"
    ]
    check("a rejected booking gives its hours back", len(freed) > 0)

    # Someone with no permission cannot look.
    stranger = browser.new_page()
    sign_in(stranger, new_booker(stranger))
    refused = stranger.request.get(f"{BASE}/api/venues/{venue_id}/slip-queue")
    check("a stranger cannot read the queue", refused.status == 403)

    browser.close()

check.summarise()
