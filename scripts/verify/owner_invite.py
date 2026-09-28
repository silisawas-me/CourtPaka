"""A new owner comes in on the platform's invitation (docs/plan/owner-complete.md 3a).

The platform admin invites an address from the admin venues page; the letter's link opens sign-up
with the address filled in; after verifying the address the owner comes in at the owner's door
and lands on applying for their venue. Sign-up is open on the local stack (the verify scripts make
bookers through it), so the closed door itself is OwnerInvitationTests' to check.
"""

import re
import subprocess
import pathlib
import uuid

from harness import BASE, PASSWORD, Checks, login, verify_email
from playwright.sync_api import sync_playwright

check = Checks(__file__)
ADMIN = "admin@courtpaka.local"


def invitation_link(email: str) -> str | None:
    """The link in the invitation, from the Development sender's log."""
    logs = subprocess.run(
        ["docker", "compose", "logs", "--no-log-prefix", "api"],
        cwd=pathlib.Path(__file__).resolve().parents[2],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        check=True,
    ).stdout
    found = re.findall(r"(https?://\S*/register\?email=[^\s]*)", logs)
    wanted = [link for link in found if email.replace("@", "%40") in link or email in link]
    return wanted[-1] if wanted else None


with sync_playwright() as p:
    browser = p.chromium.launch()
    email = f"owner-{uuid.uuid4().hex[:10]}@example.com"

    admin = browser.new_page(viewport={"width": 1280, "height": 900})
    admin.goto(f"{BASE}/login")
    login(admin, ADMIN, PASSWORD)
    admin.wait_for_url(f"{BASE}/**")
    admin.goto(f"{BASE}/admin/venues")
    admin.wait_for_selector("[data-testid=invite-owner]")
    admin.fill("[data-testid=invite-owner-email]", email)
    with admin.expect_response(lambda r: r.url.endswith("/api/admin/owner-invitations") and r.request.method == "POST") as sent:
        admin.click("[data-testid=invite-owner-send]")
    check("the platform invites a new owner by address", sent.value.status == 201, admin)
    admin.locator(f"[data-testid='owner-invitation-{email}']").wait_for()
    check("and the invitation is listed as waiting", True, admin)

    link = invitation_link(email)
    check("the invitation is emailed with a link to sign up", link is not None)

    owner = browser.new_page(viewport={"width": 1280, "height": 900})
    owner.goto(link.replace("http://localhost:8080", BASE) if link else f"{BASE}/register")
    owner.wait_for_selector("#email")
    check("the link opens sign-up with the address filled in",
          owner.input_value("#email") == email, owner)
    owner.fill("#password", PASSWORD)
    owner.locator("[data-testid=accept-policy] input, input[type=checkbox]").first.check()
    owner.click("button[type=submit]")
    owner.wait_for_selector("[data-testid=register-done]")
    verify_email(owner, email)

    owner.click("[data-testid=register-to-login]")
    owner.wait_for_url("**/login?as=admin")
    owner.fill("#email", email)
    owner.fill("#password", PASSWORD)
    owner.click("button[type=submit]")
    owner.wait_for_url(f"{BASE}/venues/apply")
    check("they come in at the owner's door and land on applying for their venue", True, owner)

    admin.reload()
    admin.locator(f"[data-testid='owner-invitation-{email}']").wait_for()
    check("and the admin sees they signed up",
          "สมัครแล้ว" in admin.locator(f"[data-testid='owner-invitation-{email}']").inner_text()
          or "Signed up" in admin.locator(f"[data-testid='owner-invitation-{email}']").inner_text(),
          admin)

    browser.close()

check.summarise()
