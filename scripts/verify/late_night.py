"""A venue open past midnight (docs/plan/thai-fit.md T4).

A venue of its own, applied for and approved here: once a venue's night runs to 02:00 its day
starts at 02:00 for good, and the seeded venue has to stay a midnight venue for the scripts that
open it round the clock (walk_in.py).

The owner sets Friday 16:00–02:00 on the hours tab as the artboard draws it, the row says "ข้าม
เที่ยงคืน", a Saturday that would open at 01:00 is refused, and an hour sold for 01:00 shows on
Friday's timeline when the browser's clock says it is 01:30 on the Saturday.
"""

import datetime
import uuid

from harness import BASE, PASSWORD, Checks, new_booker, sign_in, venue_today
from playwright.sync_api import expect, sync_playwright

check = Checks(__file__)
ADMIN = "admin@courtpaka.local"

friday = venue_today() + datetime.timedelta(days=1)
while friday.weekday() != 4:
    friday += datetime.timedelta(days=1)

with sync_playwright() as p:
    browser = p.chromium.launch()

    owner = browser.new_page(viewport={"width": 1440, "height": 900})
    email = new_booker(owner)
    sign_in(owner, email)
    code = uuid.uuid4().hex[:6].upper()
    version = owner.request.get(f"{BASE}/api/venues/agreement").json()["version"]
    venue = owner.request.post(f"{BASE}/api/venues", data={
        "code": code, "name": f"Late Court {code}", "addressLine": "1 ถนนดึก",
        "district": "ห้วยขวาง", "province": "กรุงเทพมหานคร",
        "business": {
            "promptPayId": "0812345678", "promptPayAccountName": "ร้านดึก", "isVatRegistered": False,
            "legalName": "ร้านดึก", "taxId": "0105561000000", "taxBranch": "00000",
            "billingAddress": "1 ถนนดึก", "latitude": None, "longitude": None,
        },
        "agreementVersion": version,
    }).json()
    api = f"{BASE}/api/venues/{venue['id']}"

    admin = browser.new_page()
    sign_in(admin, ADMIN)
    check("the venue is approved", admin.request.post(
        f"{BASE}/api/admin/venues/{venue['id']}/approve", data={}).ok)

    court = owner.request.post(f"{api}/courts", data={"name": "Court 1"}).json()
    days = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"]
    bands = [{"day": day, "fromHour": 0, "toHour": 24, "bahtPerHour": 200} for day in days]
    bands.append({"day": "Friday", "fromHour": 24, "toHour": 26, "bahtPerHour": 250})
    week = [{"day": day, "opensHour": 16, "closesHour": 22} for day in days]
    owner.request.put(f"{api}/opening-hours", data={"effectiveFrom": venue_today().isoformat(), "days": week})
    # Prices after hours: a venue with no week has no hours to price.
    priced = owner.request.put(f"{api}/prices", data={"bands": bands})
    check("the late hours can be priced", priced.ok)

    # ── the hours tab, as the artboard draws it ──
    owner.goto(f"{BASE}/venues/{venue['id']}/pricing")
    owner.click("[data-testid=pricing-tab-hours]")
    owner.wait_for_selector("[data-testid=opening-hours]")
    owner.select_option("[data-testid=closes-Friday]", "26")
    expect(owner.locator("[data-testid=late-Friday]")).to_have_count(1)
    check("a night past midnight says so on its row", True, owner)

    owner.select_option("[data-testid=opens-Saturday]", "1")
    expect(owner.locator("[data-testid=hours-too-early]")).to_have_count(1)
    check("a day that would open before the night before closes is refused",
          owner.locator("[data-testid=hours-save]").is_disabled(), owner)
    owner.select_option("[data-testid=opens-Saturday]", "16")

    with owner.expect_response(lambda r: r.url == f"{api}/opening-hours" and r.request.method == "PUT") as saved:
        owner.click("[data-testid=hours-save]")
    check("the week is saved", saved.value.ok, owner)
    check("the venue's day now starts at 02:00",
          owner.request.get(api).json()["dayStartsHour"] == 2)

    # ── 01:00 is sold as Friday's hour 25, and lives on Friday ──
    sold = owner.request.post(f"{api}/bookings", data={
        "slots": [{"courtId": court["id"], "date": friday.isoformat(), "hour": 25}],
        "customerName": "คุณดึก", "customerPhone": None, "paidBy": "Cash",
    })
    booking = sold.json()
    check("an hour after midnight is sold", sold.status == 201 and booking["slots"][0]["hour"] == 25)
    friday_list = owner.request.get(f"{api}/bookings?date={friday.isoformat()}").json()
    saturday = (friday + datetime.timedelta(days=1)).isoformat()
    saturday_list = owner.request.get(f"{api}/bookings?date={saturday}").json()
    check("it is on Friday's list, not Saturday's",
          any(b["bookingId"] == booking["bookingId"] for b in friday_list)
          and not any(b["bookingId"] == booking["bookingId"] for b in saturday_list))

    floor = browser.new_page(viewport={"width": 1440, "height": 900})
    floor.clock.set_fixed_time(datetime.datetime.fromisoformat(f"{saturday}T01:30:00+07:00"))
    sign_in(floor, email)
    floor.goto(f"{BASE}/venues/{venue['id']}/timeline")
    block = floor.locator(f"[data-testid=board-block-{booking['bookingId']}]")
    expect(block).to_have_count(1)
    expect(block).to_contain_text("1:00–2:00")
    check("at 01:30 on the Saturday the timeline is still Friday's, with the game on it", True, floor)
    block.click()
    expect(floor.locator("[data-testid=panel-where]")).to_contain_text("1:00–2:00")
    check("the panel names the hour as the wall clock does, not as hour 25",
          "25:" not in floor.locator("[data-testid=panel-where]").inner_text(), floor)

    browser.close()

check.summarise()
