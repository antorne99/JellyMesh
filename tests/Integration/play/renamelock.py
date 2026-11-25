"""Opens the edit form of a server on the configuration page, as an administrator, and prints whether its name can be
changed and the notes of the form, in English and Italian.

Usage: renamelock.py <base> <token> <user id> <server id> <server name>
Prints LOCK <language> <true|false> <notes as JSON>.
"""
import json
import sys
import time

from playwright.sync_api import sync_playwright

BASE, TOKEN, USER, SERVER, NAME = sys.argv[1:6]
CRED = {"Servers": [{"Id": SERVER, "Name": "p", "AccessToken": TOKEN, "UserId": USER, "ManualAddress": BASE,
                     "LocalAddress": BASE, "LastConnectionMode": 2, "manualAddressOnly": True,
                     "DateLastAccessed": int(time.time() * 1000)}]}

with sync_playwright() as p:
    browser = p.chromium.launch()
    for language, locale in (("en", "en-US"), ("it", "it-IT")):
        context = browser.new_context(viewport={"width": 1000, "height": 1200}, locale=locale)
        context.add_init_script("localStorage.setItem('jellyfin_credentials', " + json.dumps(json.dumps(CRED)) + ");localStorage.setItem('enableAutoLogin','true');")
        page = context.new_page()
        page.goto(f"{BASE}/web/index.html#/configurationpage?name=JellyMesh")
        page.wait_for_selector("#jmSummary .jmFact", state="attached", timeout=30000)
        page.locator("[role=tab][data-tab-index='1']").click()
        page.evaluate("document.querySelectorAll('#jmServers details').forEach(d => d.open = true)")
        page.wait_for_function("(n) => [...document.querySelectorAll('#jmServers *')].some(e => e.jmEdit && e.innerText.includes(n))", arg=NAME, timeout=15000)
        page.evaluate("(n) => [...document.querySelectorAll('#jmServers *')].find(e => e.jmEdit && e.innerText.includes(n)).jmEdit()", NAME)
        page.wait_for_selector("#jmServers .jmServerForm input[is=emby-input]", timeout=15000)
        page.wait_for_timeout(1500)
        locked, notes = page.evaluate("""() => {
            const form = document.querySelector('#jmServers .jmServerForm');
            return [form.querySelector('input[is=emby-input]').readOnly, form.innerText];
        }""")
        print("LOCK", language, "true" if locked else "false", json.dumps(notes, ensure_ascii=False), flush=True)
        context.close()
    browser.close()
