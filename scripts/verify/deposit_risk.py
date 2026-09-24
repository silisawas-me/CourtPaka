"""Asking somebody for more because of what happened last time (US-28)."""

import datetime

from harness import (
    BASE,
    OWNER,
    Checks,
    ensure_bookable,
    new_booker,
    seeded_venue_id,
    sign_in,
    take_first_free_hour,
    venue_today,
)
from playwright.sync_api import sync_playwright

check = Checks(__file__)
tomorrow = venue_today() + datetime.timedelta(days=1)


def rule(page, venue_id, **overrides):
    """Sets the whole rule, since the thresholds only mean anything together."""
    body = {
        "on": True,
        "lookbackDays": 60,
        "halfAt": 2,
        "fullAt": 3,
        "peakFromHour": None,
        "peakUntilHour": None,
    }
    body.update(overrides)
    return page.request.put(f"{BASE}/api/venues/{venue_id}/risk-rule", data=body)


with sync_playwright() as p:
    browser = p.chromium.launch()
    venue_id = seeded_venue_id(browser.new_page())
    ensure_bookable(browser, venue_id)

    owner = browser.new_page(viewport={"width": 1280, "height": 900})
    sign_in(owner, OWNER)

    # 1. The rule is on the venue's own page, and starts where the product says.
    owner.goto(f"{BASE}/venues/{venue_id}/settings")
    owner.wait_for_selector("[data-testid=risk-lookback]")
    check(
        "a venue starts counting the way the product says",
        (
            owner.input_value("[data-testid=risk-lookback]") == "60"
            and owner.input_value("[data-testid=risk-half-at]") == "2"
            and owner.input_value("[data-testid=risk-full-at]") == "3"
        ),
        owner,
    )

    # The venue asks for a quarter of a price ordinarily, so the rule has room to ask for more.
    owner.request.put(f"{BASE}/api/venues/{venue_id}/deposit", data={"percent": 25})
    sent = rule(owner, venue_id, peakFromHour=18, peakUntilHour=22)
    check("and it can name the hours it will not lose", sent.status == 204)

    # 2. Somebody with nothing against them is asked what everybody is asked.
    booker = browser.new_page(viewport={"width": 390, "height": 844})
    email = new_booker(booker)
    sign_in(booker, email)
    first = take_first_free_hour(booker, venue_id, tomorrow).json()
    check(
        "somebody who turns up is asked for the venue's own share",
        first["depositBaht"] * 4 == first["totalBaht"],
    )
    check("and is told nothing about a rule", first["depositReason"] == "VenueTerms")

    booker.request.post(f"{BASE}/api/bookings/{first['id']}/cancel")

    # 3. A cancellation is not a miss. The tiers themselves are the backend's to prove — a real
    #    no-show needs an hour that has started, which a booker cannot book and a script cannot
    #    wait for — so what is checked here is that the ordinary path is left alone.
    for _ in range(3):
        let_go = take_first_free_hour(booker, venue_id, tomorrow).json()
        booker.request.post(f"{BASE}/api/bookings/{let_go['id']}/cancel")

    after_cancelling = take_first_free_hour(booker, venue_id, tomorrow).json()
    check(
        "letting a booking go is not the same as not turning up",
        after_cancelling["depositReason"] == "VenueTerms"
        and after_cancelling["depositBaht"] * 4 == after_cancelling["totalBaht"],
    )
    booker.request.post(f"{BASE}/api/bookings/{after_cancelling['id']}/cancel")

    # 4. A rule that makes no sense is refused, and the page says which one in the reader's words.
    owner.reload()
    owner.wait_for_selector("[data-testid=risk-full-at]")
    check(
        "the hours it named are still there after a reload",
        owner.input_value("[data-testid=risk-peak-from]") != "",
    )

    owner.fill("[data-testid=risk-full-at]", "1")
    owner.fill("[data-testid=risk-half-at]", "3")
    owner.click("[data-testid=save-risk]")
    owner.wait_for_selector("[data-testid=risk-error]")
    check(
        "a rule that asks for everything before it asks for half is refused",
        owner.inner_text("[data-testid=risk-error]").strip() != "",
        owner,
    )

    # 5. And the whole thing can be turned off.
    off = rule(owner, venue_id, on=False)
    check("the rule can be turned off altogether", off.status == 204)

    read = owner.request.get(f"{BASE}/api/venues/{venue_id}").json()
    check("and the venue reads back what it set", read["risk"]["on"] is False)

    # Left as every other script expects to find it.
    rule(owner, venue_id)
    owner.request.put(f"{BASE}/api/venues/{venue_id}/deposit", data={"percent": 100})
    browser.close()

check.summarise()
