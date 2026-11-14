"""Opens the configuration page in English and Italian, on a desktop and a phone width, and captures the overview, the
servers and the folder settings, as an administrator sees them.

Usage: serverpage.py <base> <token> <user id> <server id> <capture prefix>
Prints SERVER lines with the text of each server block, NOTES <language> lines with the folder warnings and SERVERS
<language> lines with the text of the servers tab.
"""
import json
import sys
import time

from playwright.sync_api import sync_playwright

BASE, TOKEN, USER, SERVER, CAP = sys.argv[1:6]
CRED = {"Servers": [{"Id": SERVER, "Name": "p", "AccessToken": TOKEN, "UserId": USER, "ManualAddress": BASE,
                     "LocalAddress": BASE, "LastConnectionMode": 2, "manualAddressOnly": True,
                     "DateLastAccessed": int(time.time() * 1000)}]}
TABS = {"overview": 0, "servers": 1, "catalog": 2, "sharing": 3, "settings": 4}
texts = []

with sync_playwright() as p:
    browser = p.chromium.launch()
    for language, locale in (("en", "en-US"), ("it", "it-IT")):
        for width in (1000, 390):
            context = browser.new_context(viewport={"width": width, "height": 1200}, locale=locale)
            context.add_init_script("localStorage.setItem('jellyfin_credentials', " + json.dumps(json.dumps(CRED)) + ");localStorage.setItem('enableAutoLogin','true');")
            page = context.new_page()
            page.goto(f"{BASE}/web/index.html#/configurationpage?name=JellyMesh")
            page.wait_for_selector("#jmSummary .jmFact", state="attached", timeout=30000)
            page.wait_for_function("document.querySelectorAll('#jmFolderPaths .jmServerFolder').length > 0", timeout=15000)
            page.wait_for_timeout(1500)
            name = f"{CAP}-{language}-{width}"
            page.screenshot(path=f"{name}-overview.png", full_page=True)
            overview = page.evaluate("document.querySelector('#jmSummary').innerText")

            page.locator(f"[role=tab][data-tab-index='{TABS['servers']}']").click()
            page.evaluate("document.querySelectorAll('#jmServers details').forEach(d => d.open = true)")
            page.wait_for_timeout(500)
            page.screenshot(path=f"{name}-servers.png", full_page=True)
            servers = page.evaluate("document.querySelector('#jmServers').innerText")

            page.locator(f"[role=tab][data-tab-index='{TABS['settings']}']").click()
            page.wait_for_timeout(500)
            page.locator("#jmFolderInfo").scroll_into_view_if_needed()
            page.screenshot(path=f"{name}-settings.png", full_page=True)
            folder = page.evaluate("document.querySelector('#jmFolderInfo').innerText")
            if width == 1000:
                if language == "en":
                    for block in page.evaluate("[...document.querySelectorAll('#jmFolderPaths .jmServerFolder')].map(b => b.innerText)"):
                        print("SERVER", json.dumps(block), flush=True)
                print("NOTES", language, json.dumps(folder + "\n" + overview, ensure_ascii=False), flush=True)
                print("SERVERS", language, json.dumps(servers, ensure_ascii=False), flush=True)
                texts.append(folder + "\n" + overview)
            scrolls = page.evaluate("document.documentElement.scrollWidth > window.innerWidth")
            print("SCROLLS", language, width, scrolls, flush=True)
            context.close()
    browser.close()

with open(CAP + ".text", "w", encoding="utf-8") as out:
    out.write("\n".join(texts))
