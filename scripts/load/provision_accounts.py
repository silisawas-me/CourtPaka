"""Makes verified booker accounts for the load test, on the local stack only.

Verification follows the link the Development email sender writes to the API's log, the same way
the browser checks do (scripts/verify/harness.py), so this cannot work against a deployed stack —
there, the accounts are made by hand beforehand.

    python scripts/load/provision_accounts.py 10 > scripts/load/accounts.json
"""

import json
import pathlib
import sys
import uuid

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1] / "verify"))

from harness import BASE, PASSWORD, verify_email  # noqa: E402
from playwright.sync_api import sync_playwright  # noqa: E402

count = int(sys.argv[1]) if len(sys.argv) > 1 else 10
accounts = []

with sync_playwright() as p:
    browser = p.chromium.launch()
    page = browser.new_page()
    version = page.request.get(f"{BASE}/api/auth/privacy-policy").json()["version"]

    for _ in range(count):
        email = f"load-{uuid.uuid4().hex[:12]}@example.com"
        registered = page.request.post(
            f"{BASE}/api/auth/register",
            data={
                "email": email,
                "password": PASSWORD,
                "privacyPolicyVersion": version,
                "language": "th",
                "phoneNumber": None,
            },
        )
        if registered.status != 201:
            raise RuntimeError(f"Could not register {email}: {registered.status}")
        verify_email(page, email)
        accounts.append({"email": email, "password": PASSWORD})

    browser.close()

print(json.dumps(accounts, indent=2))
