"""Writing down what the venue sent back (US-18)."""

import datetime

from harness import (
    BASE,
    OWNER,
    Checks,
    as_upload,
    clear_waiting,
    control,
    ensure_bookable,
    new_booker,
    open_seeded_venue,
    pick_date,
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
    ensure_bookable(browser, venue_id)

    staff = browser.new_page()
    sign_in(staff, OWNER)
    clear_waiting(staff, venue_id, tomorrow)
    staff.close()

    # A booking the venue turned away after the money had arrived, so it owes all of it.
    booker = browser.new_page(viewport={"width": 390, "height": 844})
    sign_in(booker, new_booker(booker))
    booking = take_first_free_hour(booker, venue_id, tomorrow).json()
    send_slip(booker, as_upload("slip.jpg", real_jpeg(), "image/jpeg"))

    page = browser.new_page(viewport={"width": 1280, "height": 900})
    sign_in(page, OWNER)
    turned = page.request.post(
        f"{BASE}/api/venues/{venue_id}/slip-queue/{booking['id']}/reject",
        data={"reason": "ยอดไม่ตรงกับที่ต้องจ่าย", "paymentReceived": True})
    check("a booking turned away after the money arrived owes it back", turned.status == 200)
    check("and the whole of it", turned.json()["refundDueBaht"] == turned.json()["totalBaht"])

    page.goto(f"{BASE}{open_seeded_venue(page)}")
    page.click("[data-testid=venue-bookings-link]")
    page.wait_for_selector("[data-testid=day]")
    pick_date(page, tomorrow)
    page.wait_for_selector("[data-testid=day-list]")

    row = f"[data-testid=booking-{booking['id']}]"
    check(
        "the row says what is owed and what is still to send",
        page.locator(f"[data-testid=outstanding-{booking['id']}]").count() == 1,
        page,
    )

    # 1. The form opens with what is still owed already in it.
    page.click(f"[data-testid=refunds-{booking['id']}]")
    expect(page.locator("[data-testid=outstanding]")).to_be_visible()
    total = turned.json()["totalBaht"]
    check(
        "the amount is filled in with what is still owed",
        float(page.locator("#amount").input_value()) == float(total),
        page,
    )

    # 2. More than is owed is refused by the server, not only by the form.
    page.fill("#amount", str(float(total) + 100))
    with page.expect_response(lambda r: r.url.endswith("/refunds")) as refused:
        page.click("[data-testid=record-refund]")
    check("more than is owed is refused", refused.value.status == 409)
    expect(page.locator("[data-testid=decide-error]")).to_be_visible()
    check("and the counter is told why", True, page)

    # 3. Half now, half later.
    half = float(total) / 2
    page.fill("#amount", str(half))
    with page.expect_response(lambda r: r.url.endswith("/refunds")) as first:
        page.click("[data-testid=record-refund]")
    check("a part payment is accepted", first.value.status == 200)
    check("and the rest is still owed", first.value.json()["outstandingBaht"] == half)

    with page.expect_response(lambda r: r.url.endswith("/refunds")) as second:
        page.click("[data-testid=record-refund]")
    check("the rest can be written down too", second.value.json()["outstandingBaht"] == 0)
    expect(page.locator("[data-testid=all-sent]")).to_be_visible()
    check("and the row says there is nothing left to send", True, page)

    # 4. Taking one back puts the money back on what is owed, and says why.
    record = second.value.json()["records"][0]["id"]
    page.click(f"[data-testid=ask-void-{record}]")
    page.fill(f"[data-testid=void-reason-{record}]", "โอนไม่สำเร็จ ธนาคารตีกลับ")
    with page.expect_response(lambda r: "/void" in r.url) as voided:
        page.click(f"[data-testid=void-{record}]")
    check("the owner can take a record back", voided.value.status == 200)
    check(
        "and what was sent back is owed again",
        voided.value.json()["outstandingBaht"] == half,
    )
    check(
        "while the record itself stays on the page",
        page.locator("[data-testid=refund-list] li").count() == 2,
        page,
    )

    # 5. The booker sees what has actually come back.
    mine = booker.request.get(f"{BASE}/api/bookings").json()
    seen = next(
        row for row in mine["upcoming"] + mine["past"] if row["id"] == booking["id"])
    check("the booker is shown what has been sent back", seen["refundedBaht"] == half)
    check("beside what is owed", seen["refundDueBaht"] == total)

    # 6. Somebody who cannot handle bookings cannot write any of this down.
    stranger = browser.new_page()
    sign_in(stranger, new_booker(stranger))
    refused_read = stranger.request.get(
        f"{BASE}/api/venues/{venue_id}/bookings/{booking['id']}/refunds")
    check("a stranger cannot read what was sent back", refused_read.status == 403)

    browser.close()

check.summarise()
