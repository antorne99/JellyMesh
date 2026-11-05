"""Merges and undoes on the real configuration page, as an administrator would.

Usage: configpage.py <base> <token> <user id> <server id> <capture prefix>
"""
import json
import sys
import time

from playwright.sync_api import sync_playwright

BASE, TOKEN, USER, SERVER, CAP = sys.argv[1:6]
CRED = {"Servers": [{"Id": SERVER, "Name": "m", "AccessToken": TOKEN, "UserId": USER, "ManualAddress": BASE,
                     "LocalAddress": BASE, "LastConnectionMode": 2, "manualAddressOnly": True,
                     "DateLastAccessed": int(time.time() * 1000)}]}
TITLE = "JM Catena Uno"

with sync_playwright() as p:
    browser = p.chromium.launch()
    context = browser.new_context(viewport={"width": 1000, "height": 1800})
    context.add_init_script("localStorage.setItem('jellyfin_credentials', " + json.dumps(json.dumps(CRED)) + ");localStorage.setItem('enableAutoLogin','true');")
    page = context.new_page()
    page.goto(f"{BASE}/web/index.html#/configurationpage?name=JellyMesh")
    page.wait_for_selector(".jmMergeCase", state="attached", timeout=30000)

    group = page.locator(".jmMergeCase", has_text=TITLE)
    print("CASES", page.locator(".jmMergeCase").count(), flush=True)
    print("CASE", json.dumps(group.inner_text()), flush=True)
    page.locator("#jmMergeCases").scroll_into_view_if_needed()
    page.screenshot(path=CAP + "-list.png", full_page=True)

    # One title only: the server refuses, and the page says so.
    group.locator("input[type=checkbox]").first.check()
    group.get_by_text("Merge selected").click()
    page.wait_for_function("document.getElementById('jmMessage').textContent !== ''", timeout=15000)
    print("REFUSED", json.dumps(page.locator("#jmMessage").inner_text()), flush=True)

    for box in group.locator("input[type=checkbox]").all():
        box.check()
    group.get_by_text("Merge selected").click()
    page.wait_for_selector(".jmMerge:has-text('" + TITLE + "')", state="attached", timeout=30000)
    merged = page.locator(".jmMerge", has_text=TITLE)
    print("MERGED", json.dumps(page.locator("#jmMessage").inner_text()), flush=True)
    print("MERGE", json.dumps(merged.inner_text()), flush=True)
    page.screenshot(path=CAP + "-merged.png", full_page=True)

    merged.get_by_text("Undo merge").click()
    page.locator(".dialogContainer .dialog [data-id=ok]").click()
    page.wait_for_selector(".jmMergeCase:has-text('" + TITLE + "')", state="attached", timeout=30000)
    print("UNDONE", json.dumps(page.locator("#jmMessage").inner_text()), flush=True)
    browser.close()
