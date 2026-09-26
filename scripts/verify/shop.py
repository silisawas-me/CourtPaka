"""What the counter sells besides court time, and what the venue pays out (US-32, US-33).

What is checked is the thing the venue gets: something on the board, a sale that puts money in the
same till as the courts and takes the stock off, money paid out that leaves that same drawer so the
count still balances, and a sale taken back that puts the stock on the shelf again.
"""

import datetime

from harness import (
    BASE,
    OWNER,
    Checks,
    clear_shop_board,
    ensure_bookable,
    open_seeded_venue,
    seeded_venue_id,
    sign_in,
    venue_today,
)
from playwright.sync_api import expect, sync_playwright

check = Checks(__file__)


def board(page, venue_id):
    return page.request.get(f"{BASE}/api/venues/{venue_id}/shop/items").json()


def money(page, venue_id, day):
    return page.request.get(
        f"{BASE}/api/venues/{venue_id}/money?date={day.isoformat()}").json()


def tills(page, venue_id):
    """Today's drawer and tomorrow's. Which one money taken now lands in depends on whether today
    has been counted (PRD US-26) — and counter_money.py counts a day because that is what it is
    about, so the answer changes between runs. Both are read rather than guessed at."""
    today = venue_today()
    return {
        day: money(page, venue_id, day)
        for day in (today, today + datetime.timedelta(days=1))
    }


def where_the_money_went(before, after, sale_id):
    """The drawer that sale landed in, and what it added to it."""
    for day, till in after.items():
        row = [one for one in till["cashReceipts"] if one["saleId"] == sale_id]
        if row:
            return day, till["cashBaht"] - before[day]["cashBaht"], row[0]

    return None, 0, None


with sync_playwright() as p:
    browser = p.chromium.launch()
    venue_id = seeded_venue_id(browser.new_page())
    ensure_bookable(browser, venue_id)

    page = browser.new_page(viewport={"width": 1280, "height": 900})
    sign_in(page, OWNER)

    page.goto(f"{BASE}{open_seeded_venue(page)}")
    page.click("[data-testid=nav-shop]")
    page.wait_for_selector("[data-testid=open-board], [data-testid=add-item]")
    check("the venue has a door for what it sells besides court time", True, page)

    # 1. Something on the board.
    if page.locator("[data-testid=add-item]").count() == 0:
        page.click("[data-testid=open-board]")
    page.fill("[data-testid=item-name]", "ลูกขนไก่ตรวจสอบ")
    page.fill("[data-testid=item-price]", "90")
    page.fill("[data-testid=item-unit]", "ลูก")
    page.fill("[data-testid=item-tell-me-at]", "3")
    page.click("[data-testid=add-item]")
    page.wait_for_selector("[data-testid^=board-]")

    item = [one for one in board(page, venue_id) if one["withdrawnAt"] is None][0]
    check("something goes on the board and starts with nothing on the shelf",
          item["priceBaht"] == 90 and item["left"] == 0, page)

    # 2. Buying stock is one expense that also fills the shelf.
    bought = page.request.post(
        f"{BASE}/api/venues/{venue_id}/shop/spending",
        data={
            "kind": "Stock",
            "amountBaht": 700,
            "paidOn": None,
            "paidBy": "PromptPay",
            "note": "ซื้อเข้า",
            "itemId": item["itemId"],
            "quantity": 10,
        },
    )
    check("buying stock is one expense and one movement",
          bought.status == 201
          and [one for one in board(page, venue_id)
               if one["itemId"] == item["itemId"]][0]["left"] == 10)

    # 3. Selling takes the money and the stock together.
    before = tills(page, venue_id)

    page.reload()
    page.wait_for_selector(f"[data-testid=more-{item['itemId']}]")
    page.click(f"[data-testid=more-{item['itemId']}]")
    page.click(f"[data-testid=more-{item['itemId']}]")
    page.click("[data-testid=paid-Cash]")
    check("the counter adds up what is being rung up", True, page)

    rung = page.locator("[data-testid^=sale-]").count()
    page.click("[data-testid=take-money]")

    # A row more than there was. Waiting for "a sale" would pass on the ones an earlier run of
    # this script left on today's list, and take one of those back instead of this one.
    expect(page.locator("[data-testid^=sale-]")).to_have_count(rung + 1)

    sale_id = page.locator("[data-testid^=sale-]").first.get_attribute(
        "data-testid").replace("sale-", "")

    after = tills(page, venue_id)
    landed, took, row = where_the_money_went(before, after, sale_id)

    check(f"selling puts the money in the same till as the courts (took {took:.0f})",
          landed is not None and took == 180)
    check("and the row says it was a sale, not a booking",
          row is not None and row["bookingId"] is None)
    check("and the stock comes off the shelf",
          [one for one in board(page, venue_id)
           if one["itemId"] == item["itemId"]][0]["left"] == 8, page)

    # 4. Money paid out leaves the same drawer, so the count still balances.
    written = page.locator("[data-testid=spending] li").count()
    page.click("[data-testid=kind-Utilities]")
    page.fill("[data-testid=spend-amount]", "60")
    page.fill("[data-testid=spend-note]", "ค่าน้ำตรวจสอบ")
    page.click("[data-testid=spend-by-Cash]")
    page.click("[data-testid=record-spend]")

    # A row more than there was: waiting for "a row" would pass on the ones already there.
    expect(page.locator("[data-testid=spending] li")).to_have_count(written + 1)

    paid = money(page, venue_id, landed)
    check("cash paid out comes out of the till",
          paid["cashRefundedBaht"] == after[landed]["cashRefundedBaht"] + 60, page)

    # 5. A sale taken back puts the stock on the shelf again.
    page.click(f"[data-testid=take-back-{sale_id}]")
    expect(page.get_by_test_id(f"taken-back-{sale_id}")).to_be_visible()

    back = [one for one in board(page, venue_id)
            if one["itemId"] == item["itemId"]][0]["left"]
    check(f"taking a sale back puts the stock on the shelf again (left {back})",
          back == 10, page)

    # 6. What is running low says so.
    page.request.post(
        f"{BASE}/api/venues/{venue_id}/shop/items/{item['itemId']}/count",
        data={"counted": 2, "reason": "นับได้เท่านี้"})
    page.reload()
    page.wait_for_selector("[data-testid=running-low]")
    check("what is running low says so", True, page)

    # 7. The dashboard keeps the shop apart from the courts.
    figures = page.request.get(f"{BASE}/api/venues/{venue_id}/dashboard").json()
    check("the shop's money is counted apart from the courts'",
          figures["trade"]["spentBaht"] >= 760)

    page.goto(f"{BASE}/venues/{venue_id}/dashboard")
    expect(page.get_by_test_id("shop-baht")).to_be_visible()
    check("and the venue can see it on its own page", True, page)

    # The screens a design review asks for.
    page.goto(f"{BASE}/venues/{venue_id}/shop")
    page.wait_for_selector("[data-testid^=item-]")
    check("desk, Thai", True, page)
    page.set_viewport_size({"width": 390, "height": 844})
    check("phone, Thai", True, page)
    check("nothing on a phone scrolls the page sideways",
          page.evaluate("document.documentElement.scrollWidth") <= 390)

    if not page.get_by_test_id("language-en").is_visible():
        page.click("[data-testid=open-menu]")
    page.click("[data-testid=language-en]")
    expect(page.locator("html")).to_have_attribute("lang", "en")
    check("phone, English", True, page)
    page.set_viewport_size({"width": 1280, "height": 900})
    check("desk, English", True, page)

    # And the venue is left the way the other scripts expect to find it.
    clear_shop_board(page, venue_id)
    page.click("[data-testid=side-language-th]")
    expect(page.locator("html")).to_have_attribute("lang", "th")

    page.close()
    browser.close()

check.summarise()
