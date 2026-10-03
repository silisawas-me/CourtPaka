"""The setup tabs of "ราคา & ตั้งค่า" (docs/plan/thai-fit.md): courts & closures, packages & shop,
cancellation policy and the grace. Everything made here is taken away again, so the seeded venue stays as the
other scripts expect it (a court added is taken out of use, an offer and an item come off sale,
a closure is lifted, the policy is saved back as it was, the grace goes back to 15).
"""

import datetime
import uuid

from harness import BASE, OWNER, Checks, seeded_venue_id, sign_in, venue_today
from playwright.sync_api import expect, sync_playwright

check = Checks(__file__)
tag = uuid.uuid4().hex[:4]

with sync_playwright() as p:
    browser = p.chromium.launch()
    venue_id = seeded_venue_id(browser.new_page())
    desk = browser.new_page(viewport={"width": 1440, "height": 900})
    sign_in(desk, OWNER)
    api = f"{BASE}/api/venues/{venue_id}"

    desk.goto(f"{BASE}/venues/{venue_id}/pricing")
    desk.wait_for_selector("[data-testid=pricing-tab-policy]")
    check("the section is called prices & setup and has the setup tabs",
          all(desk.locator(f"[data-testid=pricing-tab-{tab}]").count() == 1
              for tab in ("prices", "hours", "courts", "catalog", "policy")), desk)

    # ── courts ──
    desk.click("[data-testid=pricing-tab-courts]")
    desk.wait_for_selector("[data-testid=venue-courts]")
    name = f"คอร์ตทดสอบ {tag}"
    desk.fill("[data-testid=court-new-name]", name)
    with desk.expect_response(lambda r: r.url == f"{api}/courts" and r.request.method == "POST") as added:
        desk.click("[data-testid=court-add]")
    court_id = added.value.json()["id"]
    desk.wait_for_selector(f"[data-testid=court-row-{court_id}]")
    check("a court is added from the list", True, desk)

    # A closure a month out, on the new court, which nobody has booked: it goes through.
    day = (venue_today() + datetime.timedelta(days=20)).isoformat()
    desk.click(f"[data-testid=court-close-{court_id}]")
    desk.fill("[data-testid=closure-starts-on]", day)
    desk.fill("[data-testid=closure-ends-on]", day)
    desk.select_option("[data-testid=closure-start-hour]", "8")
    desk.select_option("[data-testid=closure-end-hour]", "10")
    desk.fill("[data-testid=closure-reason]", "ตรวจระบบ")
    with desk.expect_response(lambda r: r.url.endswith(f"/courts/{court_id}/closures")) as closed:
        desk.click("[data-testid=closure-save]")
    closure = closed.value.json()
    check("a court is closed for a while from its row", closed.value.status == 201 or closed.value.ok)
    desk.wait_for_selector(f"[data-testid=closure-{closure['id']}]")
    with desk.expect_response(lambda r: r.url.endswith(f"/closures/{closure['id']}/lift")):
        desk.click(f"[data-testid=closure-lift-{closure['id']}]")
    expect(desk.locator(f"[data-testid=closure-{closure['id']}]")).to_have_count(0)
    check("and reopened early", True, desk)

    with desk.expect_response(lambda r: r.url.endswith(f"/courts/{court_id}/status")):
        desk.click(f"[data-testid=court-retire-{court_id}]")
    desk.wait_for_selector(f"[data-testid=court-back-{court_id}]")
    check("a court is taken out of use", "state-outOfUse" in (
        desk.locator(f"[data-testid=court-state-{court_id}]").get_attribute("class") or ""), desk)

    # ── packages & shop ──
    desk.click("[data-testid=pricing-tab-catalog]")
    desk.wait_for_selector("[data-testid=venue-catalog]")
    desk.fill("[data-testid=offer-name]", f"แพ็กเกจ {tag}")
    desk.fill("[data-testid=offer-price]", "1800")
    with desk.expect_response(lambda r: r.url == f"{api}/packages/types" and r.request.method == "POST") as offered:
        desk.click("[data-testid=offer-add]")
    type_id = offered.value.json()["typeId"]
    desk.wait_for_selector(f"[data-testid=offer-{type_id}]")
    check("an offer goes on the board", True, desk)

    desk.fill("[data-testid=item-name]", f"น้ำทดสอบ {tag}")
    desk.fill("[data-testid=item-price]", "20")
    desk.fill("[data-testid=item-unit]", "ขวด")
    with desk.expect_response(lambda r: r.url == f"{api}/shop/items" and r.request.method == "POST") as stocked:
        desk.click("[data-testid=item-add]")
    item_id = stocked.value.json()["itemId"]
    desk.wait_for_selector(f"[data-testid=item-{item_id}]")
    desk.click(f"[data-testid=item-restock-{item_id}]")
    desk.fill("[data-testid=restock-qty]", "24")
    desk.fill("[data-testid=restock-cost]", "240")
    with desk.expect_response(lambda r: r.url == f"{api}/spending" and r.request.method == "POST") as bought:
        desk.click("[data-testid=restock-save]")
    check("stock comes in as one expense that fills the shelf", bought.value.ok)
    expect(desk.locator(f"[data-testid=item-left-{item_id}]")).to_contain_text("24")
    check("and the shelf says how many", True, desk)

    desk.click(f"[data-testid=offer-withdraw-{type_id}]")
    desk.click(f"[data-testid=item-withdraw-{item_id}]")
    expect(desk.locator(f"[data-testid=item-{item_id}]")).to_have_count(0)
    expect(desk.locator(f"[data-testid=offer-{type_id}]")).to_have_count(0)
    check("an offer and an item come off sale", True)

    # ── cancellation policy ──
    before = desk.request.get(f"{api}/cancellation-policy").json()["tiers"]
    desk.click("[data-testid=pricing-tab-policy]")
    desk.wait_for_selector("[data-testid=venue-policy]")
    with desk.expect_response(lambda r: r.url == f"{api}/cancellation-policy" and r.request.method == "PUT") as saved:
        desk.click("[data-testid=policy-save]")
    after = saved.value.json()["tiers"]
    check("the policy is saved as shown", saved.value.ok and after == before, desk)

    # ── how long a late customer is waited for (US-24), the card under the policy ──
    def grace_now():
        mine = desk.request.get(f"{BASE}/api/venues/mine").json()
        return next(v for v in mine if v["id"] == venue_id)["graceMinutes"]

    def save_grace(minutes):
        desk.click(f"[data-testid=grace-{minutes}]")
        with desk.expect_response(lambda r: r.url == f"{api}/grace" and r.request.method == "PUT") as put:
            desk.click("[data-testid=grace-save]")
        desk.wait_for_selector("[data-testid=grace-saved]")
        return put.value.ok

    was = grace_now()
    desk.click("[data-testid=grace-more]")
    expect(desk.locator("[data-testid=grace-minutes]")).to_contain_text(str(min(60, was + 5)))
    check("the grace card shows the venue's minutes and steps by five", True, desk)
    check("the grace is saved and the venue waits that long",
          save_grace(30) and grace_now() == 30, desk)
    check("and set back to fifteen", save_grace(15) and grace_now() == 15)

    browser.close()

check.summarise()
