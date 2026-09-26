"""A venue that asks for part of the price up front, and the desk collecting the rest (US-28)."""

import datetime

from harness import (
    BASE,
    OWNER,
    Checks,
    as_upload,
    ensure_bookable,
    new_booker,
    real_jpeg,
    seeded_venue_id,
    send_slip,
    sign_in,
    take_first_free_hour,
    venue_today,
)
from playwright.sync_api import sync_playwright

check = Checks(__file__)
# Far enough out that the default terms still give the money back. The venue's policy returns
# everything if the booking is let go more than 24 hours before play (Pricing.Default), so a
# booking made for tomorrow morning stops being refundable some time in the afternoon — and the
# checks below are about what comes back, not about what time this script happens to run.
soon = venue_today() + datetime.timedelta(days=3)

with sync_playwright() as p:
    browser = p.chromium.launch()
    venue_id = seeded_venue_id(browser.new_page())
    ensure_bookable(browser, venue_id)

    owner = browser.new_page(viewport={"width": 1280, "height": 900})
    sign_in(owner, OWNER)

    # 1. The venue says how much it wants before it holds hours.
    owner.goto(f"{BASE}/venues/{venue_id}/settings")
    owner.wait_for_selector("[data-testid=deposit-percent]")
    owner.fill("[data-testid=deposit-percent]", "25")
    owner.dispatch_event("[data-testid=deposit-percent]", "change")
    owner.wait_for_selector("[data-testid=deposit-saved]")
    check("a venue can ask for part of the price", True, owner)

    owner.reload()
    owner.wait_for_selector("[data-testid=deposit-percent]")
    check(
        "and the setting is still there after a reload",
        owner.input_value("[data-testid=deposit-percent]") == "25",
    )

    # 2. A booker is told both numbers, and the code is made out for the deposit.
    booker = browser.new_page(viewport={"width": 390, "height": 844})
    email = new_booker(booker)
    sign_in(booker, email)
    booking = take_first_free_hour(booker, venue_id, soon).json()

    price = booking["totalBaht"]
    deposit = booking["depositBaht"]
    check("the hold asks for a quarter of the price", deposit * 4 == price)

    booker.goto(f"{BASE}/bookings/{booking['id']}")
    booker.wait_for_selector("[data-testid=booking-deposit]")
    check(
        "the page says what to transfer now",
        str(int(deposit)) in booker.inner_text("[data-testid=booking-deposit]"),
        booker,
    )
    check(
        "and what is left to pay at the venue",
        str(int(price - deposit)) in booker.inner_text("[data-testid=booking-at-venue]"),
    )

    paying = booker.request.get(f"{BASE}/api/bookings/{booking['id']}/payment").json()
    check("the amount in the code is the deposit", paying["depositBaht"] == deposit)
    check("and the rest is named as the venue's to collect", paying["payAtVenueBaht"] == price - deposit)

    # 3. The venue accepts the slip. The booking is confirmed with the balance still to come.
    send_slip(booker, as_upload("slip.jpg", real_jpeg(), "image/jpeg"))

    owner.goto(f"{BASE}/venues/{venue_id}/slip-queue")
    owner.wait_for_selector(f"[data-testid=queue-item-{booking['id']}]")
    check(
        "the queue shows the venue what the slip should be for",
        str(int(deposit)) in owner.inner_text(f"[data-testid=queue-item-{booking['id']}]"),
        owner,
    )

    owner.click(f"[data-testid=queue-item-{booking['id']}]")
    owner.wait_for_selector("[data-testid=confirm]")
    owner.click("[data-testid=confirm]")
    owner.wait_for_selector(f"[data-testid=queue-item-{booking['id']}]", state="detached")

    after = booker.request.get(f"{BASE}/api/bookings").json()
    confirmed = next(one for one in after["upcoming"] if one["id"] == booking["id"])
    check("the booking is confirmed on the deposit alone", confirmed["status"] == "Confirmed")
    check(
        "and the venue does not say it has the money",
        confirmed["paymentState"] == "NotReceived",
    )

    # 4. The desk is left the rest to take (US-26).
    day = owner.request.get(
        f"{BASE}/api/venues/{venue_id}/bookings?date={soon.isoformat()}"
    ).json()
    row = next(one for one in day if one["bookingId"] == booking["id"])
    check("the day says what arrived", row["takenBaht"] == deposit)
    check("and what is still owed", row["toPayBaht"] == price - deposit)
    check("and the door to take it is open", row["can"]["takeMoney"] is True)

    # 5. Letting the booking go gives back what arrived, not what it cost.
    offer = next(
        one for one in booker.request.get(f"{BASE}/api/bookings").json()["upcoming"]
        if one["id"] == booking["id"]
    )["cancellation"]
    check("what would come back is the deposit, not the price", offer["refundBaht"] == deposit)

    booker.request.post(f"{BASE}/api/bookings/{booking['id']}/cancel")
    past = booker.request.get(f"{BASE}/api/bookings").json()["past"]
    cancelled = next(one for one in past if one["id"] == booking["id"])
    check("and that is what the venue is told it owes", cancelled["refundDueBaht"] == deposit)

    # Left as every other script expects to find it.
    ensure_bookable(browser, venue_id)
    browser.close()

check.summarise()
