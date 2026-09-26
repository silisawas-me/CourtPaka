"""The platform deciding which venues may trade on it (US-20)."""

import datetime

from harness import (
    BASE,
    OWNER,
    PASSWORD,
    Checks,
    login,
    new_booker,
    sign_in,
    venue_today,
)
from playwright.sync_api import expect, sync_playwright

check = Checks(__file__)
ADMIN = "admin@courtpaka.local"

with sync_playwright() as p:
    browser = p.chromium.launch()

    # A venue applies, the way a real one would: through the form.
    applicant = browser.new_page()
    email = new_booker(applicant)
    sign_in(applicant, email)
    version = applicant.request.get(f"{BASE}/api/venues/agreement").json()["version"]
    code = email[7:13].upper()
    applied = applicant.request.post(
        f"{BASE}/api/venues",
        data={
            "code": code,
            "name": f"Applicant Court {code}",
            "addressLine": "9 ถนนทดสอบ",
            "district": "คลองเตย",
            "province": "กรุงเทพมหานคร",
            "business": {
                "promptPayId": "0812345678",
                "promptPayAccountName": "บริษัท ผู้สมัคร จำกัด",
                "isVatRegistered": True,
                "legalName": "บริษัท ผู้สมัคร จำกัด",
                "taxId": "0105561000000",
                "taxBranch": "00000",
                "billingAddress": "9 ถนนทดสอบ คลองเตย กรุงเทพมหานคร",
                "latitude": None,
                "longitude": None,
            },
            "agreementVersion": version,
        })
    check("a venue can apply", applied.status == 201)
    venue = applied.json()
    check("and starts out waiting", venue["status"] == "Pending")

    # A booker cannot find it yet.
    anyone = browser.new_page()
    found = anyone.request.get(f"{BASE}/api/venues/search?q=Applicant Court {code}").json()
    check("a waiting venue is not on the booker's side", len(found) == 0)

    # 1. The venue's own owner is not the platform.
    check(
        "a venue cannot judge itself",
        applicant.request.post(f"{BASE}/api/admin/venues/{venue['id']}/approve").status == 403,
    )

    # 2. Nor can somebody who runs a different venue.
    other = browser.new_page()
    sign_in(other, OWNER)
    check(
        "nor can another venue's owner",
        other.request.get(f"{BASE}/api/admin/venues").status == 403,
    )
    check("and the door is not drawn for them", other.locator("[data-testid=nav-admin]").count() == 0)

    # 3. The platform admin sees the door, and the application.
    admin = browser.new_page(viewport={"width": 1280, "height": 900})
    admin.goto(f"{BASE}/login")
    login(admin, ADMIN, PASSWORD)
    admin.wait_for_url(f"{BASE}/")
    admin.click("[data-testid=language-th]")
    check("the platform admin has a door of their own", admin.locator("[data-testid=nav-admin]").count() == 1, admin)

    admin.click("[data-testid=nav-admin]")
    admin.wait_for_selector("[data-testid=venue-list]")
    admin.click(f"[data-testid=open-{venue['id']}]")
    expect(admin.locator("[data-testid=tax-id]")).to_be_visible()
    check(
        "and can read what the venue said about itself",
        "0105561000000" in admin.locator("[data-testid=tax-id]").inner_text(),
        admin,
    )

    # 3b. What the platform charges this venue (US-21). The rate is the platform's own decision,
    # so it is read and set on the platform's own screen.
    admin.wait_for_selector("[data-testid=commission]")
    check(
        "a venue with no rate agreed says so rather than saying it is charged nothing",
        "%" not in admin.locator("[data-testid=commission-today]").inner_text(),
        admin,
    )

    admin.fill("[data-testid=rate-percent]", "12.5")
    with admin.expect_response(lambda r: "/commission" in r.url and r.request.method == "POST") as agreed:
        admin.click("[data-testid=save-rate]")
    check("a rate can be agreed", agreed.value.status == 200)
    check("and it is what the venue is charged today", agreed.value.json()["todayPercent"] == 12.5)

    expect(admin.locator("[data-testid=commission-history]")).to_be_visible()
    check(
        "and the page shows it",
        "12.5%" in admin.locator("[data-testid=commission-today]").inner_text(),
        admin,
    )

    # A rate is added, never edited: the old one is what the days before the new one are charged.
    yesterday = (venue_today() - datetime.timedelta(days=1)).isoformat()
    backwards = admin.request.post(
        f"{BASE}/api/admin/venues/{venue['id']}/commission",
        data={"percent": 5, "effectiveFrom": yesterday, "note": None},
    )
    check("a rate cannot start on a day already charged", backwards.status == 400)
    check(
        "and the refusal says which rule it is",
        backwards.json().get("code") == "venue.rate_starts_in_the_past",
    )

    refused = anyone.request.get(f"{BASE}/api/admin/venues/{venue['id']}/commission")
    check("and a venue cannot read what it is charged", refused.status in (401, 403))

    # 4. Turning it away needs a reason, and the reason reaches the venue.
    admin.click("[data-testid=reject]")
    admin.click("[data-testid=confirm]")
    # Waited for rather than counted: a count taken the instant after a click is a count taken
    # before Angular has drawn anything (rule 2 in this directory's README).
    expect(admin.locator("[data-testid=reason-error]")).to_be_visible()
    check("turning a venue away without a reason is refused on the page", True, admin)

    admin.fill("[data-testid=reason]", "เลขประจำตัวผู้เสียภาษีไม่ตรงกับหนังสือรับรอง")
    with admin.expect_response(lambda r: "/reject" in r.url) as rejected:
        admin.click("[data-testid=confirm]")
    check("with one, it is turned away", rejected.value.status == 200)

    # 5. The venue puts itself right and asks again.
    corrected = applicant.request.put(
        f"{BASE}/api/venues/{venue['id']}/business",
        data={
            "promptPayId": "0899999999",
            "promptPayAccountName": "บริษัท ผู้สมัคร จำกัด",
            "isVatRegistered": True,
            "legalName": "บริษัท ผู้สมัคร จำกัด",
            "taxId": "0105561000001",
            "taxBranch": "00000",
            "billingAddress": "9 ถนนทดสอบ คลองเตย กรุงเทพมหานคร",
            "latitude": None,
            "longitude": None,
        })
    check("a venue that was turned away can still put itself right", corrected.status == 200)
    again = applicant.request.post(f"{BASE}/api/venues/{venue['id']}/resubmit")
    check("and ask again", again.status == 200 and again.json()["status"] == "Pending")

    # 6. Approved, it appears on the booker's side.
    admin.reload()
    # The row itself, not the list: the list is not drawn at all when the filter in force has
    # nothing in it, and whether some other script left a venue waiting is not this one's business.
    admin.wait_for_selector(f"[data-testid=open-{venue['id']}]")
    admin.click(f"[data-testid=open-{venue['id']}]")
    with admin.expect_response(lambda r: "/approve" in r.url) as approved:
        admin.click("[data-testid=approve]")
    check("the platform can approve it", approved.value.status == 200)

    found = anyone.request.get(f"{BASE}/api/venues/search?q=Applicant Court {code}").json()
    check("and bookers can find it", any(v["id"] == venue["id"] for v in found))

    # 7. Suspended, it disappears again — but keeps its own doors open.
    admin.reload()
    admin.wait_for_selector("[data-testid=filter-Approved]")
    admin.click("[data-testid=filter-Approved]")
    admin.wait_for_selector(f"[data-testid=open-{venue['id']}]")
    admin.click(f"[data-testid=open-{venue['id']}]")
    admin.click("[data-testid=suspend]")
    admin.fill("[data-testid=reason]", "มีเรื่องร้องเรียนเรื่องเงิน")
    with admin.expect_response(lambda r: "/suspend" in r.url) as suspended:
        admin.click("[data-testid=confirm]")
    check("and suspend it", suspended.value.status == 200)

    found = anyone.request.get(f"{BASE}/api/venues/search?q=Applicant Court {code}").json()
    check("a suspended venue leaves the booker's side", len(found) == 0)
    check(
        "but its own queue is still open to it",
        applicant.request.get(f"{BASE}/api/venues/{venue['id']}/slip-queue").status == 200,
    )
    check(
        "while what it sells is not",
        applicant.request.post(
            f"{BASE}/api/venues/{venue['id']}/courts", data={"name": "Court X"}).status == 403,
    )

    # 8. Lifting it puts the venue back.
    admin.reload()
    admin.wait_for_selector("[data-testid=filter-Suspended]")
    admin.click("[data-testid=filter-Suspended]")
    admin.wait_for_selector(f"[data-testid=open-{venue['id']}]")
    admin.click(f"[data-testid=open-{venue['id']}]")
    with admin.expect_response(lambda r: "/reinstate" in r.url) as lifted:
        admin.click("[data-testid=reinstate]")
    check("the suspension can be lifted", lifted.value.status == 200, admin)

    found = anyone.request.get(f"{BASE}/api/venues/search?q=Applicant Court {code}").json()
    check("and the venue is back on the booker's side", any(v["id"] == venue["id"] for v in found))

    browser.close()

check.summarise()
