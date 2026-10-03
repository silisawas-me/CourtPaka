"""A booking's receipt, previewed (thai-fit T6): which paper, the full invoice, and the print.

The counter sells two hours a few days ahead through the API; the desk opens it from the booking
list, presses "ใบเสร็จ", and reads the paper. The kind is checked against the server's answer
(a VAT venue gives the abbreviated invoice, otherwise a receipt), the full invoice appears once
the buyer's details are complete, and printing hides everything but the paper.
"""

import datetime
import uuid

from harness import BASE, OWNER, Checks, ensure_bookable, seeded_venue_id, sign_in, venue_today
from playwright.sync_api import sync_playwright

check = Checks(__file__)

with sync_playwright() as p:
    browser = p.chromium.launch()
    venue_id = seeded_venue_id(browser.new_page())
    ensure_bookable(browser, venue_id)

    desk = browser.new_page(viewport={"width": 1440, "height": 900})
    sign_in(desk, OWNER)
    api = f"{BASE}/api/venues/{venue_id}"

    day = venue_today() + datetime.timedelta(days=4)
    grid = desk.request.get(f"{api}/availability", params={"date": day.isoformat()}).json()
    court_id, hour = next(
        (court["courtId"], one["hour"])
        for court in grid["courts"]
        for one in court["hours"]
        if one["status"] == "Free"
        and any(h["hour"] == one["hour"] + 1 and h["status"] == "Free" for h in court["hours"])
    )
    name = f"ใบเสร็จ {uuid.uuid4().hex[:6]}"
    sold = desk.request.post(
        f"{api}/bookings",
        data={
            "slots": [
                {"courtId": court_id, "date": day.isoformat(), "hour": hour},
                {"courtId": court_id, "date": day.isoformat(), "hour": hour + 1},
            ],
            "customerName": name,
            "customerPhone": None,
            "paidBy": "Cash",
        },
    )
    booking = sold.json()
    preview = desk.request.get(f"{api}/bookings/{booking['bookingId']}/receipt").json()
    check("the server previews the receipt with the booking's price",
          preview["totalBaht"] == booking["totalBaht"] and preview["kind"] in ("Rec", "Abb"))

    desk.goto(f"{BASE}/venues/{venue_id}/bookings?date={day.isoformat()}")
    desk.click(f"[data-testid=list-row-{booking['bookingId']}]")
    desk.click("[data-testid=receipt-open]")
    paper = desk.locator("[data-testid=receipt-paper]")
    paper.wait_for()
    check("the panel opens the receipt, the kind the venue's VAT says",
          paper.get_attribute("data-paper") == preview["kind"], desk)
    check("every copy says it is a sample", "ตัวอย่าง" in paper.inner_text())

    if preview["kind"] == "Abb":
        desk.check("[data-testid=receipt-full]")
        desk.fill("[data-testid=buyer-name]", "บจก. ทีมออฟฟิศ")
        desk.fill("[data-testid=buyer-tax-id]", "0105560098765")
        desk.fill("[data-testid=buyer-address]", "99 ถ.พหลโยธิน แขวงสามเสนใน เขตพญาไท กรุงเทพฯ 10400")
        check("the full invoice appears once the buyer is complete, with its VAT",
              paper.get_attribute("data-paper") == "Tax"
              and desk.locator("[data-testid=receipt-vat]").count() == 1, desk)

    # The print shows the paper and nothing else.
    desk.evaluate("document.body.classList.add('printing-receipt')")
    desk.emulate_media(media="print")
    hidden = desk.locator("[data-testid=receipt-print]").evaluate("e => getComputedStyle(e).visibility")
    shown = paper.evaluate("e => getComputedStyle(e).visibility")
    check("printing hides everything but the paper", hidden == "hidden" and shown == "visible")
    desk.emulate_media(media="screen")
    desk.evaluate("document.body.classList.remove('printing-receipt')")

    desk.click("[data-testid=receipt-close]")
    desk.locator("[data-testid=receipt-sheet]").wait_for(state="detached", timeout=5000)
    check("the receipt closes back to the panel",
          desk.locator("[data-testid=panel-name]").count() == 1, desk)

    browser.close()

check.summarise()
