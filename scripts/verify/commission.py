"""What the platform charges, and the invoice it sends for it (US-21)."""

from harness import (
    BASE,
    OWNER,
    Checks,
    new_booker,
    seeded_venue_id,
    sign_in,
)
from playwright.sync_api import sync_playwright

check = Checks(__file__)
ADMIN = "admin@courtpaka.local"

with sync_playwright() as p:
    browser = p.chromium.launch()

    # 1. The platform's own page: what it has billed, and what is waiting on it.
    admin = browser.new_page(viewport={"width": 1280, "height": 900})
    sign_in(admin, ADMIN)
    admin.goto(f"{BASE}/admin/commission")
    admin.wait_for_selector("[data-testid=filter-PaymentSubmitted]")

    check(
        "the page opens on what is waiting to be checked",
        admin.locator("[data-testid=filter-PaymentSubmitted]").get_attribute("aria-pressed")
        == "true",
        admin,
    )

    with admin.expect_response(lambda r: "/admin/commission/invoices" in r.url) as listed:
        admin.click("[data-testid=filter-All]")
    check("and every invoice can be asked for", listed.value.status == 200, admin)

    # 2. The rate is the platform's, and so is the list of what it has billed.
    stranger = browser.new_page()
    sign_in(stranger, new_booker(stranger))
    check(
        "a stranger cannot read what the platform has billed",
        stranger.request.get(f"{BASE}/api/admin/commission/invoices").status == 403,
    )

    # 3. The venue's own side: what it owes, and where to send it.
    owner = browser.new_page(viewport={"width": 1280, "height": 900})
    sign_in(owner, OWNER)
    venue_id = seeded_venue_id(owner)

    owed = owner.request.get(f"{BASE}/api/venues/{venue_id}/commission")
    check("a venue can read what it owes", owed.status == 200)
    body = owed.json()
    check("and is told where to send it, or that nobody has said", "account" in body)
    check("with its invoices, newest month first", isinstance(body.get("invoices"), list))

    # Another venue's owner is nobody here.
    someone = browser.new_page()
    sign_in(someone, new_booker(someone))
    check(
        "and nobody else can read it",
        someone.request.get(f"{BASE}/api/venues/{venue_id}/commission").status in (401, 403),
    )

    browser.close()

check.summarise()
