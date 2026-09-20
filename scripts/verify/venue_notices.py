"""What a venue is told is waiting for it (US-17)."""

import datetime

from harness import (
    BASE,
    OWNER,
    Checks,
    as_upload,
    clear_waiting,
    control,
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

with sync_playwright() as p:
    browser = p.chromium.launch()
    venue_id = seeded_venue_id(browser.new_page())

    # Whatever an earlier run left waiting is not this run's subject, and the switch starts on.
    staff = browser.new_page()
    sign_in(staff, OWNER)
    clear_waiting(staff, venue_id, tomorrow)
    staff.request.put(f"{BASE}/api/venues/{venue_id}/notifications",
                      data={"wantsSlipEmails": True})
    staff.close()

    page = browser.new_page(viewport={"width": 1280, "height": 900})
    sign_in(page, OWNER)
    page.goto(f"{BASE}{open_seeded_venue(page)}")
    page.wait_for_selector("[data-testid=slip-queue-link]")
    check(
        "a venue with nothing waiting shows no number",
        page.locator("[data-testid=slips-waiting]").count() == 0,
        page,
    )

    # 1. A slip arriving puts a number on the door it is behind.
    booker = browser.new_page(viewport={"width": 390, "height": 844})
    sign_in(booker, new_booker(booker))
    booking = take_first_free_hour(booker, venue_id, tomorrow).json()
    send_slip(booker, as_upload("slip.jpg", real_jpeg(), "image/jpeg"))

    page.reload()
    expect(page.locator("[data-testid=slips-waiting]")).to_be_visible()
    check("a slip that has arrived puts a number on the door", True, page)

    # 2. So does money nobody has answered for.
    booker.request.post(f"{BASE}/api/bookings/{booking['id']}/cancel")
    page.reload()
    expect(page.locator("[data-testid=money-waiting]")).to_be_visible()
    check("money left unanswered puts one on the other door", True, page)

    # 3. The one notice that can be turned off, and it stays turned off.
    with page.expect_response(lambda r: r.url.endswith("/notifications")) as chosen:
        control(page, "slip-emails").click()
    check("turning slip mail off is accepted", chosen.value.status == 204)

    page.reload()
    expect(control(page, "slip-emails")).to_have_attribute("aria-checked", "false")
    check("and the switch still says so after a reload", True, page)

    # 4. Someone who cannot act on any of it is shown none of it.
    stranger = browser.new_page()
    sign_in(stranger, new_booker(stranger))
    refused = stranger.request.get(f"{BASE}/api/venues/{venue_id}/attention")
    check("a stranger is told nothing", refused.status == 403)

    browser.close()

check.summarise()
