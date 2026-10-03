"""Counting the drawer by shift (docs/plan/thai-fit.md T2, T3), on the revenue section's tab.

A shift hands over through the screen: the count it sent is the float the next shift is handed,
and the day stays open. The day itself is not closed here — closing today would send every other
script's money to tomorrow — so the close is checked as a door that is offered, not pressed.
"""

from harness import BASE, OWNER, Checks, seeded_venue_id, sign_in
from playwright.sync_api import sync_playwright

check = Checks(__file__)

with sync_playwright() as p:
    browser = p.chromium.launch()
    venue_id = seeded_venue_id(browser.new_page())

    desk = browser.new_page(viewport={"width": 1440, "height": 900})
    sign_in(desk, OWNER)
    api = f"{BASE}/api/venues/{venue_id}"

    before = desk.request.get(f"{api}/money").json()
    if before["closed"] is not None:
        print("today is already closed at this venue; nothing to hand over")
        check("a closed day offers no count", True)
        check.summarise()

    desk.goto(f"{BASE}/venues/{venue_id}/dashboard")
    desk.click("[data-testid=revenue-tab-close]")
    desk.wait_for_selector("[data-testid=close-drawer]")
    desk.wait_for_selector("[data-testid=close-open-shift]")
    check("the revenue section has the drawer's count as a tab",
          desk.locator("[data-testid=close-counted]").count() == 1, desk)
    check("bank transfer and TrueMoney are counted apart from the till",
          desk.locator("[data-testid=close-method-BankTransfer]").count() == 1
          and desk.locator("[data-testid=close-method-TrueMoney]").count() == 1)

    # The cash list says what each row was for, from the server's lines, not only how much.
    rows = desk.locator("[data-testid=close-cash-rows] li")
    cash_lines = [line for line in before.get("lines", []) if line["method"] == "Cash"
                  and line["at"] > before["openShift"]["from"]]
    check("the cash list has the shift's cash, in and out",
          rows.count() == max(1, len(cash_lines)), desk)

    # What the screen says the shift should hold is what the server checks against.
    expected = desk.locator("[data-testid=close-expected]").inner_text()
    counted = expected.replace("฿", "").replace(",", "").strip()
    desk.fill("[data-testid=close-counted]", counted)
    check("counting what the screen expects comes out even",
          desk.locator("[data-testid=close-difference]").inner_text().strip() == "฿0", desk)
    check("closing the day is offered beside handing over",
          desk.locator("[data-testid=close-day]").is_enabled())

    with desk.expect_response(lambda r: r.url.startswith(f"{api}/money/closing")) as sent:
        desk.click("[data-testid=close-shift]")
    shift = sent.value.json()
    check("a shift hands over without closing the day",
          sent.value.status == 200 and shift["endsDay"] is False and shift["differenceBaht"] == 0)

    after = desk.request.get(f"{api}/money").json()
    check("the day stays open, with a new shift running",
          after["closed"] is None and after["openShift"] is not None
          and len(after["counts"]) == len(before["counts"]) + 1)

    desk.wait_for_selector(f"[data-testid=close-count-{len(after['counts']) - 1}]")
    check("the next shift starts with the float the last one did: the takings were handed in",
          float(desk.locator("[data-testid=close-float]").input_value()) == float(shift["openingFloatBaht"]),
          desk)

    browser.close()

check.summarise()
