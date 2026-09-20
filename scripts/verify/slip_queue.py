"""The venue looking at a slip and deciding (US-12)."""

import datetime

from harness import (
    BASE,
    OWNER,
    Checks,
    ensure_bookable,
    as_upload,
    new_booker,
    open_seeded_venue,
    real_jpeg,
    seeded_venue_id,
    send_slip,
    sign_in,
    take_first_free_hour,
    venue_today,
)
from playwright.sync_api import expect, sync_playwright

check = Checks(__file__)
tomorrow = venue_today() + datetime.timedelta(days=1)

# A PDF is a slip the server accepts (US-04) and one the page cannot draw, so it is checked too.
PDF = bytes([0x25, 0x50, 0x44, 0x46]) + b"-1.7 slip"


def waiting_booking(browser, venue_id, skip=0, slip=None):
    """A booking that has been paid for and is waiting for the venue, made the way a booker does."""
    page = browser.new_page(viewport={"width": 390, "height": 844})
    sign_in(page, new_booker(page))
    booking = take_first_free_hour(page, venue_id, tomorrow, skip=skip).json()

    sent = send_slip(page, slip or as_upload("slip.jpg", real_jpeg(), "image/jpeg"))
    if sent.status != 200:
        raise RuntimeError(f"Could not send a slip: {sent.status}")

    page.close()
    return booking


with sync_playwright() as p:
    browser = p.chromium.launch()

    venue_id = seeded_venue_id(browser.new_page())
    ensure_bookable(browser, venue_id)

    # Whatever an earlier run left waiting is not this run's subject. Clearing it through the API
    # is what lets these checks be run again.
    staff = browser.new_page()
    sign_in(staff, OWNER)
    for waiting in staff.request.get(f"{BASE}/api/venues/{venue_id}/slip-queue").json():
        staff.request.post(
            f"{BASE}/api/venues/{venue_id}/slip-queue/{waiting['bookingId']}/confirm")
    staff.close()

    # The same bytes twice, so the second one carries the flag the venue is meant to see (BR-07).
    repeated = as_upload("slip.jpg", real_jpeg(), "image/jpeg")
    waiting_booking(browser, venue_id, slip=repeated)
    waiting_booking(browser, venue_id, skip=1, slip=repeated)

    # The venue's own staff open the queue.
    page = browser.new_page(viewport={"width": 1280, "height": 900})
    sign_in(page, OWNER)
    page.goto(f"{BASE}{open_seeded_venue(page)}")
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
        "the queue says the time of day each slip arrived, not only the date",
        ":" in page.locator("[data-testid=queue] li").nth(0).inner_text(),
    )

    # Turning one away needs a reason and an answer about the money, and neither is filled in
    # for the venue.
    page.click("[data-testid=reject]")
    check(
        "neither answer about the money is ticked to begin with",
        not page.is_checked("[data-testid=money-no]")
        and not page.is_checked("[data-testid=money-yes]"),
        page,
    )

    page.click("[data-testid=reject-confirm]")
    expect(page.locator("[data-testid=reason-error]")).to_be_visible()
    expect(page.locator("[data-testid=money-error]")).to_be_visible()
    check("a rejection with neither answer is refused before it is sent", True, page)

    page.fill("#reason", "   ")
    page.click("[data-testid=reject-confirm]")
    expect(page.locator("[data-testid=reason-error]")).to_be_visible()
    check("and a reason of nothing but spaces is no reason", True)

    page.fill("#reason", "ยอดไม่ตรงกับที่ต้องจ่าย")
    page.check("[data-testid=money-yes]")
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

    page.click("[data-testid=reject]")
    check(
        "and what was typed about the one before is not waiting in the box",
        page.locator("#reason").input_value() == "",
        page,
    )
    page.click("[data-testid=reject-cancel]")

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

    # A slip the page cannot draw is offered as a file rather than left as a broken picture.
    waiting_booking(browser, venue_id, slip=as_upload("slip.pdf", PDF, "application/pdf"))
    page.reload()
    page.wait_for_selector("[data-testid=decision]")
    check(
        "a PDF slip is offered to open, not shown as a broken picture",
        page.locator("[data-testid=slip] img").count() == 0
        and page.locator("[data-testid=slip]").count() == 1,
        page,
    )

    # Someone with no permission cannot look.
    stranger = browser.new_page()
    sign_in(stranger, new_booker(stranger))
    refused = stranger.request.get(f"{BASE}/api/venues/{venue_id}/slip-queue")
    check("a stranger cannot read the queue", refused.status == 403)

    browser.close()

check.summarise()
