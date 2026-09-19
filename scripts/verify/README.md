# Browser checks

Playwright scripts that drive the real stack, for the flows unit tests cannot cover: what a page
shows after a reload, what the server actually stored, and what a member without the permission
sees.

```bash
docker compose --profile full up -d --build      # from the repo root
pip install playwright && playwright install chromium
python scripts/verify/venue_settings.py
```

They sign in as the seeded accounts (`owner@courtpaka.local` / `staff@courtpaka.local`,
`DevPassword1`) and write screenshots next to themselves. Each check prints PASS or FAIL and the
script exits non-zero if anything failed.

Two rules worth keeping. Wait for the section to render before counting what is in it — a count
taken mid-load reads zero, and a check written against an empty database never notices. And wait
for the response before reloading. The pages apply a change straight
away and reconcile with the server, so reloading right after a click cancels the request in flight
and the check reads the old state — which is a bug in the check, not in the page.

`harness.py` holds what every script needs: where the stack is, the seeded accounts, the PASS/FAIL
tally and the screenshots.

| Script | Covers |
|---|---|
| `venue_ui.py` | Sign-in redirects, venue detail, members and permissions (US-14) |
| `venue_settings.py` | Courts and opening hours (US-11) |
