"""Reads and saves the settings on the real configuration page, as an administrator would.

Usage: configpage.py <base> <token> <user id> <server id> <capture prefix>
"""
import json
import sys
import time

from playwright.sync_api import sync_playwright

BASE, TOKEN, USER, SERVER, CAP = sys.argv[1:6]
FIELDS = ["jmSyncInterval", "jmRemotePlaybackInfoTimeout", "jmStreamOpenTimeout", "jmPlaybackIdle", "jmSessionRetention", "jmPolicyCache"]
CRED = {"Servers": [{"Id": SERVER, "Name": "p", "AccessToken": TOKEN, "UserId": USER, "ManualAddress": BASE,
                     "LocalAddress": BASE, "LastConnectionMode": 2, "manualAddressOnly": True,
                     "DateLastAccessed": int(time.time() * 1000)}]}

with sync_playwright() as p:
    browser = p.chromium.launch()
    context = browser.new_context(viewport={"width": 1000, "height": 1800})
    context.add_init_script("localStorage.setItem('jellyfin_credentials', " + json.dumps(json.dumps(CRED)) + ");localStorage.setItem('enableAutoLogin','true');")
    page = context.new_page()
    page.goto(f"{BASE}/web/index.html#/configurationpage?name=JellyMesh")
    page.wait_for_selector("#jmPlaybackIdle", state="attached", timeout=30000)
    page.wait_for_function("document.querySelector('#jmSyncInterval').value !== ''", timeout=15000)

    def read():
        return page.evaluate("""f => Object.fromEntries(f.map(id => { const e = document.getElementById(id);
          return [id, {value: e.value, min: e.min, max: e.max}]; }))""", FIELDS)

    print("FIELDS", json.dumps(read()), flush=True)
    page.screenshot(path=CAP + ".png", full_page=True)

    # A value below the minimum: the browser itself stops the form.
    page.evaluate("document.getElementById('jmSyncInterval').value = '60'")
    print("INVALID", page.evaluate("document.getElementById('jmSettingsForm').checkValidity()"), flush=True)
    page.evaluate("document.getElementById('jmSyncInterval').value = '300'")

    # A real save with a changed time.
    page.evaluate("document.getElementById('jmPlaybackIdle').value = '95'")
    page.evaluate("document.querySelector('#jmSettingsForm button[type=submit]').click()")
    page.wait_for_timeout(2500)
    browser.close()
