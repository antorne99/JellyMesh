"""Checks of the configuration page in a real browser, at 1280 and 390 px.

Usage: pagecheck.py <scenario>...   Run it through check.sh, which puts the servers in the state each one expects.
"""
import json
import re
import sys

import uipage as u

FAILED = [0]
WIDTHS = list(u.VIEWPORTS)
UNREACHABLE = "http://127.0.0.1:1"
PAIR = "JM Sonda Mobile"
CHAIN = "JM Catena Uno"
HOSTILE = "<b>x</b>"
HOSTILE_PEER = "<i>y</i>"
LONG_NAME = "A library with a very long name " + "and_no_spaces" * 12
SECOND_USER = "a1b2c3d4e5f60718293a4b5c6d7e8f90"
THEMES = [("dark", True), ("light", False), ("blueradiance", True), ("appletv", False), ("purplehaze", True), ("wmc", True)]


def check(name, passed, detail=""):
    """Prints one result."""
    print(("PASS " if passed else "FAIL ") + name + ("" if passed else ": " + str(detail)), flush=True)
    if not passed:
        FAILED[0] = 1


def text(page, selector):
    """Returns the text of a part of the page."""
    return page.locator(selector).inner_text()


def until(page, condition, seconds=15):
    """Waits for a condition while the page goes on, and returns whether it came true."""
    for _ in range(seconds * 10):
        if condition():
            return True
        page.wait_for_timeout(100)
    return False


def healthy(page, name):
    """Checks what must hold in every state: no script error, and no sideways scroll."""
    check(name + " has no script error", not page.errors, page.errors)
    sizes = page.evaluate("[document.documentElement.scrollWidth, innerWidth]")
    check(name + " does not scroll sideways", sizes[0] <= sizes[1], sizes)


def refused_merge(page, case):
    """Selects two titles and lets only one reach the server, which refuses what the page would not send."""
    def one_title(route):
        body = json.loads(route.request.post_data)
        route.continue_(post_data=json.dumps({"WorkKeys": body["WorkKeys"][:1]}))

    page.route("**/JellyMesh/Admin/Merges", one_title)
    for box in case.locator(u.CHECKBOX).all():
        u.tick(box)
    u.button(case, "merge").click()


def settings_match(page, server, name):
    """Checks that the settings on the page are the saved ones."""
    config = u.config(server)
    for selector, field in u.SETTINGS.items():
        shown = page.locator(selector).input_value()
        check(f"{name} shows {field}", shown == str(config[field]), f"{shown} against {config[field]}")
    check(name + " shows metadata protection", page.locator(u.METADATA_PROTECTION).is_checked() == config["MetadataProtection"])


def first(s, width):
    """A server with nothing configured."""
    name = width + " first run"
    page = s.open("first", width)
    check(name + " is active", "active" in text(page, u.STATUS).lower(), text(page, u.STATUS))
    check(name + " has no server rows", page.locator(u.SERVER).count() == 0)
    check(name + " has no warning", not page.locator(u.WARNING).is_visible())
    for field, selector in u.ADD_FIELDS.items():
        check(f"{name} offers the {field} field", page.locator(selector).is_visible())
    check(name + " can add a server", page.locator(u.ADD_SUBMIT).is_enabled())
    check(name + " has no folder yet", page.locator(u.FEDERATED_FOLDER).input_value() == "")
    check(name + " says what the folder needs", text(page, u.FOLDER_INFO) != "")
    check(name + " has no doubtful films", page.locator(u.MERGE_CASE).count() == 0)
    check(name + " has no merges", page.locator(u.MERGE).count() == 0)
    settings_match(page, "first", name)
    healthy(page, name)
    u.shot(page, f"first-{width}")
    page.context.close()


def first_nokey(s, width):
    """A server with nothing configured and no secret key."""
    name = width + " first run without a key"
    page = s.open("first", width)
    warning = text(page, u.WARNING) if page.locator(u.WARNING).is_visible() else ""
    check(name + " warns", "JELLYMESH_SECRET_KEY" in warning, warning)
    check(name + " is suspended", "suspended" in text(page, u.STATUS).lower(), text(page, u.STATUS))
    check(name + " cannot add a server", page.locator(u.ADD_SUBMIT).is_disabled())
    healthy(page, name)
    u.shot(page, f"first-nokey-{width}")
    page.context.close()


def normal(s, width):
    """Working servers, a synced catalog, doubtful films and a connected server."""
    name = width + " normal load"
    page = s.open("fresh", width)
    check(name + " is active", "active" in text(page, u.STATUS).lower(), text(page, u.STATUS))
    check(name + " has no warning", not page.locator(u.WARNING).is_visible())
    stock = u.server_row(page, "stock")
    peer = u.server_row(page, "peer")
    check(name + " lists the stock remote as standard", "standard" in stock.inner_text() and "online" in stock.inner_text(), stock.inner_text())
    check(name + " lists the peer as JellyMesh", "JellyMesh" in peer.inner_text() and "online" in peer.inner_text(), peer.inner_text())
    sync = u.api("fresh", "GET", "/JellyMesh/Admin/Sync")[1]
    catalog = text(page, u.SYNC_SERVERS)
    for server in sync["Servers"]:
        check(f"{name} counts the films of {server['ServerName']}", f"{server['ServerName']}: {server['Movies']} films" in catalog, catalog)
    check(name + " can sync", page.locator(u.SYNC_NOW).is_enabled())
    check(name + " lists the pair", u.merge_case(page, PAIR).count() == 1)
    check(name + " lists the chain", u.merge_case(page, CHAIN).count() == 1)
    connected = u.peer_row(page, "jellymesh")
    check(name + " lists the connected server", connected.count() == 1)
    check(name + " shows its stream limit", connected.locator(u.NUMBER).input_value() == "4")
    check(name + " shows what it shares", connected.locator(u.CHECKBOX + ":checked").count() == 2)
    check(name + " offers the free user", page.locator(u.PEERS + " select").locator("option", has_text="friend").count() == 1)
    settings_match(page, "fresh", name)
    healthy(page, name)
    u.shot(page, f"normal-{width}")
    page.context.close()


def loading(s, width):
    """A slow load shows that it is loading, and a failed one says so."""
    name = width + " loading"

    hold = []

    def slow(context):
        context.route("**/JellyMesh/Admin/Status", lambda route: hold.append(route))

    page = s.open("fresh", width, route=slow, wait=False)
    page.wait_for_selector(u.PAGE, state="attached", timeout=30000)
    page.wait_for_selector(u.LOADING, state="visible", timeout=15000)
    check(name + " shows the spinner while it waits", page.locator(u.LOADING).is_visible())
    u.shot(page, f"loading-{width}")
    until(page, lambda: hold)
    hold.pop().continue_()
    u.wait_loaded(page)
    check(name + " ends with the data", page.locator(u.SERVER).count() == 2)
    healthy(page, name)
    page.context.close()

    name = width + " failed load"

    def broken(context):
        context.route("**/JellyMesh/Admin/Status", lambda route: route.fulfill(status=500, json={}))

    page = s.open("fresh", width, route=broken, wait=False)
    shown = u.wait_message(page)
    check(name + " says so", shown != "", shown)
    page.wait_for_function("s => document.querySelector(s).value !== ''", arg=u.SYNC_INTERVAL, timeout=15000)
    check(name + " still shows the settings", True)
    healthy(page, name)
    u.shot(page, f"load-failed-{width}")
    page.context.close()


def errors(s, width):
    """Actions the server refuses: each says why, and changes nothing."""
    name = width + " errors"
    page = s.open("fresh", width)
    servers = len(u.api("fresh", "GET", "/JellyMesh/Admin/Status")[1]["Servers"])

    page.locator(u.ADD_FIELDS["name"]).fill("nowhere")
    page.locator(u.ADD_FIELDS["url"]).fill(UNREACHABLE)
    page.locator(u.ADD_FIELDS["user"]).fill("jellymesh")
    page.locator(u.ADD_FIELDS["password"]).fill("not-the-password")
    page.locator(u.ADD_SUBMIT).click()
    shown = u.wait_message(page)
    check(name + " an unreachable server says so", "cannot be reached" in shown, shown)
    check(name + " an unreachable server is not added", len(u.api("fresh", "GET", "/JellyMesh/Admin/Status")[1]["Servers"]) == servers)
    check(name + " the password does not stay in the form", page.locator(u.ADD_FIELDS["password"]).input_value() == "")
    check(name + " the rest of the form stays", page.locator(u.ADD_FIELDS["url"]).input_value() == UNREACHABLE)
    u.shot(page, f"error-add-{width}")
    for selector in u.ADD_FIELDS.values():
        page.locator(selector).fill("")

    u.clear_message(page)
    case = u.merge_case(page, PAIR)
    refused_merge(page, case)
    shown = u.wait_message(page)
    page.unroute("**/JellyMesh/Admin/Merges")
    check(name + " one title cannot be merged", "at least two" in shown, shown)
    check(name + " nothing was merged", u.api("fresh", "GET", "/JellyMesh/Admin/Merges")[1]["Merges"] == [])
    u.shot(page, f"error-merge-{width}")

    u.clear_message(page)
    folder = u.config("fresh")["FederatedFolderPath"]
    page.locator(u.FEDERATED_FOLDER).fill("relative/path")
    page.locator(u.SAVE_SETTINGS).click()
    shown = u.wait_message(page)
    check(name + " a refused save says so", "refused" in shown, shown)
    check(name + " a refused save keeps the folder", u.config("fresh")["FederatedFolderPath"] == folder)
    u.shot(page, f"error-settings-{width}")
    page.locator(u.FEDERATED_FOLDER).fill(folder)

    page.locator(u.SYNC_INTERVAL).fill("60")
    check(name + " the browser stops a low interval", not page.locator(u.SETTINGS_FORM).evaluate("form => form.checkValidity()"))
    healthy(page, name)
    page.context.close()


def keyboard(s, width):
    """Every main action can be reached with Tab, in order through each tab, and used from the keyboard."""
    name = width + " keyboard"
    page = s.open("fresh", width)
    pair = u.merge_case(page, PAIR)
    reached = []
    for tab in u.TABS:
        u.show_tab(page, tab)
        inside = False
        for _ in range(400):
            page.keyboard.press("Tab")
            focus = page.evaluate("""p => { const e = document.activeElement;
              return e.closest(p) ? (e.id || (e.textContent || '').trim() || e.type) : null; }""", u.PAGE)
            if focus is None and inside:
                break
            if focus is not None:
                inside = True
                reached.append(focus)
            if focus == "checkbox" and u.has_focus(pair):
                page.keyboard.press("Space")

    for action in [u.BUTTON["test"], u.BUTTON["libraries"], u.BUTTON["remove"], u.BUTTON["merge"], u.BUTTON["connect"], u.BUTTON["save"],
                   u.SYNC_NOW[1:], u.ADD_SUBMIT[1:], u.SYNC_INTERVAL[1:], u.ADD_FIELDS["name"][1:], u.METADATA_PROTECTION[1:]]:
        check(f"{name} reaches {action}", action in reached, reached)
    check(name + " reaches a settings Save", reached.count(u.BUTTON["save"]) >= 2, reached)
    check(name + " reaches the title checkboxes", reached.count("checkbox") >= 5, reached)

    libraries = u.button(u.server_row(page, "stock"), "libraries")
    libraries.focus()
    page.keyboard.press("Enter")
    page.wait_for_selector(u.SERVER + " " + u.CHECKBOX, state="attached", timeout=15000)
    check(name + " Enter opens the libraries", u.server_row(page, "stock").locator(u.CHECKBOX).count() == 2)
    box = u.merge_case(page, CHAIN).locator(u.CHECKBOX).first
    box.focus()
    page.keyboard.press("Space")
    check(name + " Space selects a title", box.is_checked())
    healthy(page, name)
    page.context.close()


COLOURS = """
  const parse = c => {
    const m = c.match(/[\\d.]+/g).map(Number);
    const scale = c.startsWith('color(') ? 255 : 1;
    return {r: m[0] * scale, g: m[1] * scale, b: m[2] * scale, a: m.length > 3 ? m[3] : 1};
  };
  const over = (top, under) => ({r: top.r * top.a + under.r * (1 - top.a), g: top.g * top.a + under.g * (1 - top.a), b: top.b * top.a + under.b * (1 - top.a), a: 1});
  const channel = v => { v /= 255; return v <= 0.04045 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4); };
  const luminance = c => 0.2126 * channel(c.r) + 0.7152 * channel(c.g) + 0.0722 * channel(c.b);
  const ratio = (a, b) => (Math.max(a, b) + 0.05) / (Math.min(a, b) + 0.05);
  const backdrop = node => {
    const layers = [];
    for (let e = node; e; e = e.parentElement) { layers.push(parse(getComputedStyle(e).backgroundColor)); }
    return layers.reverse().reduce((under, top) => over(top, under), {r: 255, g: 255, b: 255, a: 1});
  };
"""

CONTRAST = "s => {" + COLOURS + """
  const root = document.querySelector(s.page);
  const seen = [...root.querySelectorAll(s.readable)].filter(e => e.offsetParent !== null && (e.textContent.trim() !== '' || e.value)).map(e => {
    const under = backdrop(e);
    const text = over(parse(getComputedStyle(e).color), under);
    return {ratio: ratio(luminance(text), luminance(under)), under: luminance(under), font: getComputedStyle(e).fontFamily,
      text: (e.textContent.trim() || e.value).slice(0, 40)};
  }).sort((x, y) => x.ratio - y.ratio);
  return {worst: seen[0], lightest: Math.max(...seen.map(e => e.under)), fonts: [...new Set(seen.map(e => e.font))],
    background: luminance(backdrop(root))};
}"""


def contrast(page, selector):
    """Measures what a selector finds in every tab, comes back to the tab it was on, and returns the worst."""
    shown = u.shown_tab(page)
    seen = []
    for tab in u.TABS:
        u.show_tab(page, tab)
        seen.append(page.evaluate(CONTRAST, {"page": u.PAGE, "readable": selector}))
    u.show_tab(page, shown)
    found = [one for one in seen if one["worst"]]
    return {"worst": min((one["worst"] for one in found), key=lambda worst: worst["ratio"]),
            "lightest": max(one["lightest"] for one in found),
            "fonts": sorted({font for one in found for font in one["fonts"]}),
            "background": seen[0]["background"]}


def themes(s, width):
    """The page follows the theme chosen in Jellyfin and stays readable."""
    for theme, dark in [("dark", True), ("light", False)]:
        name = f"{width} {theme} theme"
        page = s.open("fresh", width, theme=theme)
        check(name + " is loaded", u.theme_of(page) == theme, u.theme_of(page))
        seen = contrast(page, u.READABLE)
        check(name + " sets the background", (seen["background"] < 0.2) == dark, seen["background"])
        check(name + " text is readable", seen["worst"]["ratio"] >= 4.5, seen["worst"])
        healthy(page, name)
        u.shot(page, f"theme-{theme}-{width}")
        page.context.close()
    u.set_theme("fresh", "dark")


def actions(s, width):
    """The actions that work: each one reaches the server."""
    name = width + " actions"
    page = s.open("fresh", width)

    stock = u.server_row(page, "stock")
    u.button(stock, "test").click()
    shown = u.wait_message(page)
    check(name + " a connection test reports", "connected" in shown, shown)

    u.clear_message(page)
    stock = u.server_row(page, "stock")
    u.button(stock, "libraries").click()
    page.wait_for_selector(u.SERVER + " " + u.CHECKBOX, state="attached", timeout=15000)
    check(name + " the libraries show what is imported", stock.locator(u.CHECKBOX + ":checked").count() == 2)
    u.button(stock, "save_libraries").click()
    check(name + " the libraries save", "saved" in u.wait_message(page))

    u.clear_message(page)
    connected = u.peer_row(page, "jellymesh")
    connected.locator(u.NUMBER).fill("3")
    u.button(connected, "save").click()
    u.wait_message(page)
    limit = u.api("fresh", "GET", "/JellyMesh/Admin/Peers")[1][0]["MaxStreams"]
    check(name + " a connected server saves", limit == 3, limit)
    connected = u.peer_row(page, "jellymesh")
    connected.locator(u.NUMBER).fill("4")
    u.button(connected, "save").click()
    until(page, lambda: u.api("fresh", "GET", "/JellyMesh/Admin/Peers")[1][0]["MaxStreams"] == 4)

    u.clear_message(page)
    case = u.merge_case(page, PAIR)
    for box in case.locator(u.CHECKBOX).all():
        u.tick(box)
    u.button(case, "merge").click()
    page.wait_for_selector(u.MERGE, state="attached", timeout=30000)
    check(name + " a merge is listed", PAIR in page.locator(u.MERGE).inner_text())
    check(name + " a merge reports", "Merged" in u.wait_message(page), u.message(page))
    u.shot(page, f"merged-{width}")
    u.clear_message(page)
    u.button(page.locator(u.MERGE), "undo").click()
    u.answer_confirm(page, True)
    page.wait_for_selector(u.MERGE_CASE + f":has-text('{PAIR}')", state="attached", timeout=30000)
    check(name + " an undo brings the case back", u.api("fresh", "GET", "/JellyMesh/Admin/Merges")[1]["Merges"] == [])

    u.clear_message(page)
    page.locator(u.SYNC_INTERVAL).fill("901")
    page.locator(u.SAVE_SETTINGS).click()
    check(name + " the settings save", until(page, lambda: u.config("fresh")["SyncIntervalSeconds"] == 901))
    check(name + " nothing else changed", page.locator(u.FEDERATED_FOLDER).input_value() == u.config("fresh")["FederatedFolderPath"])
    u.settings("fresh", lambda config: config.update(SyncIntervalSeconds=900))
    healthy(page, name)
    page.context.close()


def offline(s, width):
    """The stock remote stopped, and a sync found out."""
    name = width + " offline remote"
    page = s.open("fresh", width)
    stock = u.server_row(page, "stock")
    check(name + " marks the remote", "offline" in stock.inner_text(), stock.inner_text())
    check(name + " leaves the other online", "online" in u.server_row(page, "peer").inner_text())
    lines = text(page, u.SYNC_SERVERS)
    check(name + " says the catalog is kept", re.search(r"stock: [1-9]\d* films.*offline", lines, re.I | re.S) is not None, lines)
    u.shot(page, f"offline-{width}")
    u.button(stock, "test").click()
    shown = u.wait_message(page)
    check(name + " a connection test says so", "cannot be reached" in shown, shown)
    healthy(page, name)
    page.context.close()


def nokey(s, width):
    """Servers saved, and the secret key gone."""
    name = width + " no key"
    page = s.open("fresh", width)
    warning = text(page, u.WARNING) if page.locator(u.WARNING).is_visible() else ""
    check(name + " warns", "JELLYMESH_SECRET_KEY" in warning and "suspended" in warning, warning)
    check(name + " is suspended", "suspended" in text(page, u.STATUS).lower(), text(page, u.STATUS))
    check(name + " cannot add a server", page.locator(u.ADD_SUBMIT).is_disabled())
    check(name + " cannot sync", page.locator(u.SYNC_NOW).is_disabled())
    check(name + " still lists the servers", page.locator(u.SERVER).count() == 2)
    check(name + " shows the settings", page.locator(u.SYNC_INTERVAL).input_value() != "")
    seen = contrast(page, u.WARNING)
    check(name + " the warning is readable", seen["worst"]["ratio"] >= 4.5, seen["worst"])
    healthy(page, name)
    u.shot(page, f"nokey-{width}")
    page.context.close()


def edits(s, width):
    """An action leaves alone what it did not change: open panels, unsaved edits and keyboard focus."""
    name = width + " edits"
    page = s.open("fresh", width)
    stock = u.server_row(page, "stock")
    connected = u.peer_row(page, "jellymesh")
    new_peer = u.peer_row(page, u.NEW_PEER)
    u.button(stock, "libraries").click()
    page.wait_for_selector(u.SERVER + " " + u.CHECKBOX, state="attached", timeout=15000)
    u.tick(stock.locator(u.CHECKBOX).first, False)
    connected.locator(u.NUMBER).fill("9")
    u.tick(connected.locator(u.CHECKBOX).first, False)

    def kept(action, limit="9"):
        check(f"{name} {action} keeps the open panel", stock.locator(u.CHECKBOX).count() == 2, stock.locator(u.CHECKBOX).count())
        check(f"{name} {action} keeps the unchecked library", stock.locator(u.CHECKBOX + ":checked").count() == 1)
        check(f"{name} {action} keeps the unsaved limit", connected.locator(u.NUMBER).input_value() == limit, connected.locator(u.NUMBER).input_value())
        check(f"{name} {action} keeps the unchecked peer box", connected.locator(u.CHECKBOX + ":checked").count() == 1)

    test = u.button(stock, "test")
    test.focus()
    page.keyboard.press("Enter")
    shown = u.wait_message(page)
    check(name + " a connection test reports", "connected" in shown, shown)
    kept("a connection test")
    check(name + " a connection test keeps the focus", u.has_focus(test))

    u.clear_message(page)
    page.locator(u.SYNC_INTERVAL).fill("902")
    page.locator(u.SAVE_SETTINGS).click()
    check(name + " the settings save", until(page, lambda: u.config("fresh")["SyncIntervalSeconds"] == 902))
    u.wait_idle(page)
    kept("a saved setting")
    u.settings("fresh", lambda config: config.update(SyncIntervalSeconds=900))

    held = []
    page.route("**/JellyMesh/Admin/Status", lambda route: held.append(route))
    u.clear_message(page)
    u.button(stock, "test").click()
    until(page, lambda: u.message(page) != "")
    connected.locator(u.NUMBER).fill("7")
    until(page, lambda: held)
    for route in held:
        route.continue_()
    page.unroute("**/JellyMesh/Admin/Status")
    u.wait_idle(page)
    kept("a value typed during a refresh", "7")

    u.clear_message(page)
    u.button(stock, "save_libraries").click()
    u.wait_message(page)
    kept("saved libraries", "7")
    check(name + " saved libraries update their row", "libraries to import: 1" in stock.inner_text(), stock.inner_text())
    u.tick(stock.locator(u.CHECKBOX).first)
    u.clear_message(page)
    u.button(stock, "save_libraries").click()
    u.wait_message(page)

    new_peer.locator(u.NUMBER).fill("5")
    u.tick(new_peer.locator(u.CHECKBOX).first)
    connected.locator(u.NUMBER).fill("4")
    save = u.button(connected, "save")
    u.clear_message(page)
    save.focus()
    page.keyboard.press("Enter")
    u.wait_message(page)
    check(name + " a saved peer keeps the other row's limit", new_peer.locator(u.NUMBER).input_value() == "5", new_peer.locator(u.NUMBER).input_value())
    check(name + " a saved peer keeps the other row's box", new_peer.locator(u.CHECKBOX + ":checked").count() == 1)
    check(name + " a saved peer keeps the focus", u.has_focus(save))
    check(name + " a saved peer shows what was saved", connected.locator(u.CHECKBOX + ":checked").count() == 1)
    u.tick(connected.locator(u.CHECKBOX).first)
    u.clear_message(page)
    u.button(connected, "save").click()
    u.wait_message(page)

    other = u.merge_case(page, CHAIN).locator(u.CHECKBOX).first
    u.tick(other)
    case = u.merge_case(page, PAIR)
    for box in case.locator(u.CHECKBOX).all():
        u.tick(box)
    merge = u.button(case, "merge")
    u.clear_message(page)
    merge.focus()
    page.keyboard.press("Enter")
    u.wait_message(page)
    check(name + " a merge keeps the selection in another case", other.is_checked())
    check(name + " a merge leaves the focus in the list", u.has_focus(page.locator(u.MERGE_CASES)))
    u.clear_message(page)
    u.button(page.locator(u.MERGE), "undo").click()
    u.answer_confirm(page, True)
    u.wait_message(page)
    check(name + " an undo keeps the selection in another case", other.is_checked())
    healthy(page, name)
    page.context.close()


def fake(page, path, change):
    """Rewrites the reads of an admin path with change, which sees the decoded answer."""
    def answer(route):
        if route.request.method != "GET":
            route.continue_()
            return
        response = route.fetch()
        data = response.json()
        change(data)
        route.fulfill(response=response, json=data)

    page.route("**/JellyMesh/Admin/" + path, answer)


def quiet(s, width):
    """A page left open stays cheap, and a hidden tab reads nothing."""
    name = width + " quiet"
    page = s.open("fresh", width)
    reads = []
    page.on("request", lambda request: reads.append(request.url.split("/JellyMesh/Admin/")[1]) if "/JellyMesh/Admin/" in request.url else None)
    page.wait_for_timeout(31000)
    counts = {path: reads.count(path) for path in ["Sync", "Status", "Peers", "Peers/Options", "Merges"]}
    check(name + " reads the sync every 5 seconds", 5 <= counts["Sync"] <= 7, counts)
    check(name + " reads status and streams every 15 seconds", 1 <= counts["Status"] <= 3 and 1 <= counts["Peers"] <= 3, counts)
    check(name + " reads the peer options at most once a minute", counts["Peers/Options"] <= 1, counts)
    check(name + " leaves the merges alone", counts["Merges"] == 0, counts)
    check(name + " reads nothing else", len(reads) == sum(counts.values()), reads)

    page.evaluate("""() => {
      Object.defineProperty(document, 'hidden', {value: true, configurable: true});
      document.dispatchEvent(new Event('visibilitychange'));
    }""")
    page.wait_for_timeout(500)
    reads.clear()
    page.wait_for_timeout(7000)
    check(name + " a hidden tab reads nothing", not reads, reads)
    page.evaluate("""() => {
      delete document.hidden;
      document.dispatchEvent(new Event('visibilitychange'));
    }""")
    check(name + " a shown tab catches up at once", until(page, lambda: "Status" in reads and "Peers" in reads and "Sync" in reads, 2), reads)
    healthy(page, name)
    page.context.close()


def polling(s, width):
    """The page follows what happens elsewhere, without touching what the admin is editing."""
    name = width + " polling"
    page = s.open("fresh", width)
    status_line = page.locator(u.SYNC_STATUS)
    sync_now = page.locator(u.SYNC_NOW)
    stock = u.server_row(page, "stock")
    connected = u.peer_row(page, "jellymesh")
    limit = connected.locator(u.NUMBER)
    u.mark(stock)
    u.mark(connected)
    limit.focus()
    limit.fill("6")

    sync = {"running": False, "fail": None, "stall": False}
    held = []
    reads = []

    def sync_route(route):
        if route.request.method != "GET":
            route.continue_()
        elif sync["stall"]:
            held.append(route)
        elif sync["fail"] == "error":
            route.fulfill(status=500, json={})
        elif sync["fail"] == "network":
            route.abort()
        else:
            response = route.fetch()
            data = response.json()
            data["IsRunning"] = data["IsRunning"] or sync["running"]
            route.fulfill(response=response, json=data)

    page.route("**/JellyMesh/Admin/Sync", sync_route)
    page.on("request", lambda request: reads.append(request.url) if request.url.endswith("/JellyMesh/Admin/Sync") else None)

    sync["running"] = True
    check(name + " a sync started elsewhere appears", until(page, lambda: "running" in status_line.inner_text().lower(), 8), status_line.inner_text())
    check(name + " the button waits for it", sync_now.is_disabled())
    sync["running"] = False
    check(name + " the end of that sync appears", until(page, lambda: "Last sync" in status_line.inner_text(), 8), status_line.inner_text())
    check(name + " the button comes back", sync_now.is_enabled())

    before = status_line.inner_text()
    page.wait_for_timeout(1100)
    started = u.api("fresh", "POST", "/JellyMesh/Admin/Sync")[0]
    check(name + " a real sync starts", started == 202, started)
    check(name + " a real sync updates the last sync", until(page, lambda: "Last sync" in status_line.inner_text() and status_line.inner_text() != before, 30), status_line.inner_text())

    fake(page, "Peers", lambda peers: peers[0].update(ActiveStreams=1))
    fake(page, "Status", lambda status: [server.update(Connection="Offline") for server in status["Servers"] if server["Name"] == "stock"])
    check(name + " stream use stays current", until(page, lambda: "Streams in use: 1 of" in connected.inner_text(), 18), connected.inner_text())
    check(name + " server state stays current", until(page, lambda: "offline" in stock.inner_text(), 18), stock.inner_text())
    check(name + " the rows were not rebuilt", u.is_marked(stock) and u.is_marked(connected))
    check(name + " the row being edited keeps its value", limit.input_value() == "6", limit.input_value())
    check(name + " the row being edited keeps the focus", u.has_focus(limit))

    for failure, label in [("error", "a server error"), ("network", "a network failure")]:
        sync["running"] = True
        sync_now.click()
        until(page, lambda: "running" in status_line.inner_text().lower(), 8)
        sync["fail"] = failure
        note = page.locator(u.FAILURE["sync"])
        check(f"{name} {label} shows in place", until(page, note.is_visible, 8))
        check(f"{name} {label} does not claim a running sync", "running" not in status_line.inner_text().lower(), status_line.inner_text())
        check(f"{name} {label} frees the button", sync_now.is_enabled())
        sync["running"] = False
        sync["fail"] = None
        check(f"{name} polling recovers from {label}", until(page, lambda: not note.is_visible() and "Last sync" in status_line.inner_text(), 8), status_line.inner_text())

    sync["stall"] = True
    check(name + " a stalled read arrives", until(page, lambda: held, 8))
    page.wait_for_timeout(7000)
    check(name + " no read starts on top of a stalled one", len(held) == 1, len(held))
    note = page.locator(u.FAILURE["sync"])
    check(name + " a stalled read shows in place", until(page, note.is_visible, 12))
    sync["stall"] = False
    check(name + " polling recovers from a stall", until(page, lambda: not note.is_visible(), 10), status_line.inner_text())

    page.evaluate("location.hash = '#/dashboard'")
    page.wait_for_function("() => !location.hash.includes('configurationpage')", timeout=15000)
    page.wait_for_timeout(1000)
    reads.clear()
    page.wait_for_timeout(7000)
    check(name + " polling stops when the page is left", not reads, reads)
    page.go_back()
    u.wait_loaded(page)
    reads.clear()
    check(name + " polling resumes when the page is back", until(page, lambda: reads, 8))
    healthy(page, name)
    page.unroute_all(behavior="ignoreErrors")
    page.context.close()


def sections(s, width):
    """Each part of the page loads on its own, and the one that fails says so in its place."""
    parts = {
        "status": "**/JellyMesh/Admin/Status",
        "sync": "**/JellyMesh/Admin/Sync",
        "peers": "**/JellyMesh/Admin/Peers",
        "merges": "**/JellyMesh/Admin/Merges",
        "settings": u.PLUGIN_CONFIG,
    }

    def filled(page):
        """Tells which parts have their data."""
        return {
            "status": page.locator(u.SERVER).count() == 2,
            "sync": "Last sync" in text(page, u.SYNC_STATUS),
            "peers": u.peer_row(page, "jellymesh").count() == 1,
            "merges": u.merge_case(page, PAIR).count() == 1 and text(page, u.MERGES) != "",
            "settings": page.locator(u.SYNC_INTERVAL).input_value() != "",
        }

    for part, pattern in parts.items():
        name = f"{width} {part} failing"
        state = {"fail": True}

        def broken(context, pattern=pattern, state=state):
            context.route(pattern, lambda route: route.fulfill(status=500, json={}) if state["fail"] else route.continue_())

        page = s.open("fresh", width, route=broken, wait=False)
        page.wait_for_selector(u.PAGE, state="attached", timeout=30000)
        note = page.locator(u.FAILURE[part])
        check(name + " shows in place", until(page, note.is_visible, 20), part)
        u.wait_idle(page)
        ready = filled(page)
        for other in parts:
            if other != part:
                check(f"{name} leaves {other} loaded", ready[other], ready)
                check(f"{name} leaves {other} without a failure", not page.locator(u.FAILURE[other]).is_visible())
        check(name + " is not filled", not ready[part], ready)
        check(name + " can still sync", page.locator(u.SYNC_NOW).is_enabled())
        u.shot(page, f"section-{part}-{width}")
        if part != "settings":
            state["fail"] = False
            check(name + " recovers", until(page, lambda: not note.is_visible() and filled(page)[part], 12), filled(page))
        healthy(page, name)
        page.context.close()


SETTING_ERRORS = [
    (u.SYNC_INTERVAL, "60", "Sync interval", "at least 300"),
    (u.SYNC_INTERVAL, "", "Sync interval", "at least 300"),
    ("#jmRemotePlaybackInfoTimeout", "2", "Wait for a remote", "from 3 to 60"),
    ("#jmStreamOpenTimeout", "61", "Wait for each step", "from 5 to 60"),
    ("#jmPlaybackIdle", "110", "Close an idle remote", "from 30 to 109"),
    ("#jmSessionRetention", "0", "Keep paused sessions", "from 1 to 168"),
    ("#jmPolicyCache", "1.5", "Remember the transcoding rights", "from 1 to 60"),
    ("#jmReconciliationTime", "", "Nightly reconciliation time", "hours and minutes"),
    (u.FEDERATED_FOLDER, "relative/path", "Federated folder", "absolute path"),
    (u.FEDERATED_FOLDER, "/", "Federated folder", "absolute path"),
    (u.FEDERATED_FOLDER, "/films/../other", "Federated folder", "absolute path"),
]


def outcome(page, name, kind, says=""):
    """Checks that an outcome has its word, stands in view, and is what the status region holds."""
    shown = u.wait_message(page)
    check(f"{name} says {kind}", shown.startswith(kind + ": "), shown)
    check(name + " says what happened", says in shown, shown)
    check(name + " is in view", u.in_view(page, u.OUTCOME), shown)
    region = page.get_by_role("status").filter(has_text=shown)
    check(name + " is in the status region", region.count() == 1 and region.get_attribute("id") == u.MESSAGE[1:])
    return shown


def outcomes_shown(page, name):
    """Outcomes of the actions, where they appear, and when they go."""
    check(name + " the region is rendered before it has text", page.locator(u.MESSAGE).evaluate(
        "node => node.getAttribute('role') === 'status' && getComputedStyle(node).display !== 'none' && !node.closest('[hidden]')"))
    check(name + " shows no outcome yet", u.message(page) == "" and not page.locator(u.DISMISS).is_visible())

    refused_merge(page, u.merge_case(page, PAIR))
    outcome(page, name + " a refused merge", "Error", "at least two")
    page.unroute("**/JellyMesh/Admin/Merges")
    error = page.locator(u.OUTCOME).evaluate("node => getComputedStyle(node).backgroundColor")
    u.button(u.server_row(page, "stock"), "libraries").click()
    u.wait_idle(page)
    page.wait_for_selector(u.SERVER + " " + u.CHECKBOX, state="attached", timeout=15000)
    check(name + " opening the libraries clears the old error", u.message(page) == "", u.message(page))

    u.button(u.server_row(page, "stock"), "test").click()
    outcome(page, name + " a connection test", "Done", "connected")
    done = page.locator(u.OUTCOME).evaluate("node => getComputedStyle(node).backgroundColor")
    check(name + " an error and a success differ in colour", error != done, done)

    hold = []
    page.route("**/JellyMesh/Admin/Servers/*", lambda route: hold.append(route) if route.request.method == "DELETE" else route.continue_())
    u.button(u.server_row(page, "stock"), "remove").click()
    page.locator(u.CONFIRM_OK).click()
    until(page, lambda: hold)
    check(name + " a new action clears the old outcome at once", u.message(page) == "", u.message(page))
    page.wait_for_selector(u.CONFIRM_BACKDROP, state="detached", timeout=15000)
    hold.pop().fulfill(status=204)
    outcome(page, name + " a removal", "Done", "Removed stock")

    page.route("**/JellyMesh/Admin/Merges", lambda route: route.fulfill(status=200, json={}) if route.request.method == "POST" else route.continue_())
    case = u.merge_case(page, PAIR)
    for box in case.locator(u.CHECKBOX).all():
        u.tick(box)
    u.button(case, "merge").click()
    outcome(page, name + " a merge", "Done", "Merged")
    page.unroute("**/JellyMesh/Admin/Merges")

    page.locator(u.DISMISS).click()
    check(name + " Dismiss empties the outcome", u.message(page) == "" and not page.locator(u.DISMISS).is_visible(), u.message(page))


def settings_refused(page, name):
    """Each wrong setting is named with its rule and focused, and nothing reaches the server."""
    saves = []
    page.on("request", lambda request: saves.append(request.url) if "/Plugins/" in request.url and request.method == "POST" else None)
    u.open_advanced(page)
    for selector, value, field, rule in SETTING_ERRORS:
        label = f"{name} {selector} set to '{value}'"
        before = page.locator(selector).input_value()
        page.locator(selector).fill(value)
        page.locator(u.SAVE_SETTINGS).click()
        shown = outcome(page, label, "Error", field)
        check(label + " gives the rule", rule in shown, shown)
        check(label + " says nothing was saved", "nothing was saved" in shown, shown)
        check(label + " marks the field", rule in u.field_error(page, selector), u.field_error(page, selector))
        check(label + " is focused", "#" + u.focused(page) == selector, u.focused(page))
        check(label + " the field is in view", u.in_view(page, selector))
        healthy(page, label)
        page.locator(selector).fill(before)
    check(name + " sends none of them to the server", saves == [], saves)

    policy = page.locator("#jmPolicyCache").input_value()
    opening = page.locator("#jmStreamOpenTimeout").input_value()
    page.locator("#jmPolicyCache").fill("0")
    page.locator("#jmStreamOpenTimeout").fill("0")
    page.locator(u.SAVE_SETTINGS).click()
    shown = outcome(page, name + " two wrong fields", "Error", "Wait for each step")
    check(name + " two wrong fields counts the other", "1 more field" in shown, shown)
    check(name + " two wrong fields focuses the first", u.focused(page) == "jmStreamOpenTimeout", u.focused(page))
    check(name + " two wrong fields marks both", u.field_error(page, "#jmPolicyCache") != "" and u.field_error(page, "#jmStreamOpenTimeout") != "")
    u.shot(page, f"outcome-fields-{page.viewport_size['width']}")
    page.locator("#jmPolicyCache").fill(policy)
    page.locator("#jmStreamOpenTimeout").fill(opening)


def settings_failed(page, name):
    """Failures the page cannot foresee: the server refuses, breaks, or does not answer."""
    config = u.PLUGIN_CONFIG

    folder = page.locator(u.FEDERATED_FOLDER).input_value()
    page.locator(u.FEDERATED_FOLDER).fill("C:\\films")
    page.locator(u.SAVE_SETTINGS).click()
    outcome(page, name + " a save the server refuses", "Error", "server refused")
    check(name + " the field errors have gone", page.locator(u.FIELD_ERROR).count() == 0)
    page.locator(u.FEDERATED_FOLDER).fill(folder)

    def post(answer):
        page.route(config, lambda route: answer(route) if route.request.method == "POST" else route.continue_())

    post(lambda route: route.fulfill(status=500, body=""))
    page.locator(u.SAVE_SETTINGS).click()
    outcome(page, name + " a server error", "Error", "error 500")
    page.unroute(config)

    post(lambda route: route.abort())
    page.locator(u.SAVE_SETTINGS).click()
    outcome(page, name + " a save with no answer", "Error", "No answer from this server")
    page.unroute(config)

    page.route("**/JellyMesh/Admin/Servers/*/Test", lambda route: route.abort())
    u.button(u.server_row(page, "stock"), "test").click()
    outcome(page, name + " an action with no answer", "Error", "No answer from this server")
    page.unroute("**/JellyMesh/Admin/Servers/*/Test")

    page.context.set_offline(True)
    page.locator(u.SAVE_SETTINGS).click()
    outcome(page, name + " a save offline", "Error", "No answer from this server")
    page.context.set_offline(False)


def peer_limit(page, name):
    """An empty stream limit is named and focused, and never sent."""
    sent = []
    page.on("request", lambda request: sent.append(request.url) if request.method == "PUT" else None)
    connected = u.peer_row(page, "jellymesh")
    limit = connected.locator(u.NUMBER)
    limit.fill("")
    u.button(connected, "save").click()
    shown = outcome(page, name + " an empty stream limit", "Error", "Stream limit")
    check(name + " an empty stream limit gives the rule", "at least 1" in shown, shown)
    check(name + " an empty stream limit is marked", "at least 1" in connected.locator(u.FIELD_ERROR).inner_text())
    check(name + " an empty stream limit is focused", limit.evaluate("node => node === document.activeElement"))
    check(name + " an empty stream limit is not sent", sent == [], sent)
    u.shot(page, f"outcome-peer-{page.viewport_size['width']}")
    limit.fill("4")
    u.button(connected, "save").click()
    outcome(page, name + " a corrected stream limit", "Done", "Saved jellymesh")
    check(name + " a corrected stream limit loses its mark", page.locator(u.FIELD_ERROR).count() == 0)


def outcomes(s, width):
    """What an action leaves on the page, at the width of a phone too."""
    name = width + " outcomes"
    before = u.config("fresh")
    page = s.open("fresh", width)
    outcomes_shown(page, name)
    healthy(page, name)
    page.context.close()

    page = s.open("fresh", width)
    settings_refused(page, name)
    settings_failed(page, name)
    peer_limit(page, name)
    healthy(page, name)
    check(name + " left the settings as they were", u.config("fresh") == before)
    page.context.close()

    name = width + " outcome of a failed load"

    def broken(context):
        context.route("**/JellyMesh/Admin/Status", lambda route: route.fulfill(status=500, body=""))

    page = s.open("fresh", width, route=broken, wait=False)
    outcome(page, name, "Error", "error 500")
    page.context.close()


def outcome_themes(s, width):
    """Both kinds of outcome and a marked field stay readable in the dark and light themes."""
    for theme in ["dark", "light"]:
        name = f"{width} {theme} outcomes"
        page = s.open("fresh", width, theme=theme)
        u.open_advanced(page)
        policy = page.locator("#jmPolicyCache").input_value()
        page.locator("#jmPolicyCache").fill("0")
        page.locator(u.SAVE_SETTINGS).click()
        u.wait_message(page)
        seen = contrast(page, u.MESSAGE + ", " + u.DISMISS + ", " + u.FIELD_ERROR)
        check(name + " an error is readable", seen["worst"]["ratio"] >= 4.5, seen["worst"])
        page.screenshot(path=f"{u.SHOTS}/outcome-error-{theme}-{width}.png")
        page.locator("#jmPolicyCache").fill(policy)

        u.button(u.server_row(page, "stock"), "test").click()
        outcome(page, name + " a success", "Done", "connected")
        seen = contrast(page, u.MESSAGE + ", " + u.DISMISS)
        check(name + " a success is readable", seen["worst"]["ratio"] >= 4.5, seen["worst"])
        page.screenshot(path=f"{u.SHOTS}/outcome-done-{theme}-{width}.png")
        page.context.close()
    u.set_theme("fresh", "dark")


FOCUS_RING = "mark => {" + COLOURS + """
  const node = document.activeElement;
  const ring = mark ? node.parentElement.querySelector(mark) : node;
  const look = getComputedStyle(ring);
  const under = backdrop(ring.parentElement);
  const fill = backdrop(node);
  const text = over(parse(getComputedStyle(node).color), fill);
  return {style: look.outlineStyle, width: parseFloat(look.outlineWidth),
    ring: ratio(luminance(over(parse(look.outlineColor), under)), luminance(under)), text: ratio(luminance(text), luminance(fill))};
}"""


def focus_shows(page, name, node, mark=None):
    """Checks that a control reached with the keyboard has a ring that stands out, and stays readable."""
    u.reach(page, node)
    ring_shows(page, name, mark)


def ring_shows(page, name, mark=None):
    """Checks the ring of the control that has the keyboard."""
    seen = page.evaluate(FOCUS_RING, mark)
    check(name + " shows a ring", seen["style"] == "solid" and seen["width"] >= 2, seen)
    check(name + " has a ring that stands out", seen["ring"] >= 3, seen)
    if mark is None:
        check(name + " stays readable", seen["text"] >= 4.5, seen)


def readable(page, name, selector):
    """Checks the contrast of what a selector finds, and returns the measures."""
    seen = contrast(page, selector)
    check(name + " is readable", seen["worst"]["ratio"] >= 4.5, seen["worst"])
    return seen


def broken_reads(context):
    """Refuses every read of the page, so that each part shows its failure."""
    for pattern in ["**/JellyMesh/Admin/Status", "**/JellyMesh/Admin/Sync", "**/JellyMesh/Admin/Peers", "**/JellyMesh/Admin/Merges", u.PLUGIN_CONFIG]:
        context.route(pattern, lambda route: route.fulfill(status=500, json={}))


def warned(context):
    """Gives the status, the sync and the merges each a warning to show."""
    fake(context, "Status", lambda data: data.update(Warning="A warning from the status."))
    fake(context, "Sync", lambda data: data.update(Health="Corrupt"))
    fake(context, "Merges", lambda data: data.update(IsStoreDamaged=True))


def control_themes(s, width):
    """Jellyfin controls, focus, alerts and outcomes look right in each of the six themes."""
    for theme, dark in THEMES:
        name = f"{width} {theme} controls"
        page = s.open("fresh", width, theme=theme, route=warned)
        check(name + " has the theme", u.theme_of(page) == theme, u.theme_of(page))
        stock = u.server_row(page, "stock")
        connected = u.peer_row(page, "jellymesh")
        new_peer = u.peer_row(page, u.NEW_PEER)
        u.button(stock, "libraries").click()
        page.wait_for_selector(u.SERVER + " " + u.CHECK_MARK, state="attached", timeout=15000)
        page.mouse.move(0, 0)

        check(name + " are all upgraded", page.locator(u.NOT_UPGRADED).count() == 0, page.locator(u.NOT_UPGRADED).count())
        seen = readable(page, name, u.CONTROLS)
        check(name + " sets the background", (seen["background"] < 0.2) == dark, seen["background"])
        if dark:
            check(name + " are not white on dark", seen["lightest"] < 0.2, seen["lightest"])
        check(name + " use one font", len(seen["fonts"]) == 1 and "Arial" not in seen["fonts"][0], seen["fonts"])
        readable(page, name + " buttons", u.BUTTONS)
        readable(page, name + " the merge section", u.MERGE_TEXT)

        focus_shows(page, name + " the open tab", page.locator(u.TAB + "[aria-selected=true]"))
        page.keyboard.press("ArrowRight")
        ring_shows(page, name + " a closed tab")
        focus_shows(page, name + " a button", u.button(stock, "test"))
        focus_shows(page, name + " a main button", page.locator(u.SAVE_SETTINGS))
        focus_shows(page, name + " a number", connected.locator(u.NUMBER))
        focus_shows(page, name + " a select", new_peer.locator(u.SELECT))
        focus_shows(page, name + " a library box", stock.locator(u.CHECKBOX).first, u.CHECK_MARK)
        focus_shows(page, name + " a settings box", page.locator(u.METADATA_PROTECTION), u.CHECK_MARK)
        page.evaluate("document.activeElement.blur()")

        for alert in u.ALERTS_WITH_DATA:
            check(f"{name} shows {alert}", page.locator(alert).is_visible())
        readable(page, name + " a warning", u.ALERT)

        limit = connected.locator(u.NUMBER)
        limit.fill("")
        u.button(connected, "save").click()
        u.wait_message(page)
        readable(page, name + " an error", u.MESSAGE + ", " + u.DISMISS + ", " + u.FIELD_ERROR)
        limit.fill("4")
        u.clear_message(page)
        u.button(stock, "test").click()
        u.wait_message(page)
        readable(page, name + " a success", u.MESSAGE + ", " + u.DISMISS)
        u.clear_message(page)

        check(name + " has no inline style", page.locator(u.INLINE_STYLE).count() == 0, page.locator(u.INLINE_STYLE).count())
        healthy(page, name)
        u.shot(page, f"control-{theme}-{width}")
        u.close(page)

        name = f"{width} {theme} failures"
        page = s.open("fresh", width, theme=theme, route=broken_reads, wait=False)
        page.wait_for_selector(u.PAGE, state="attached", timeout=30000)
        notes = [page.locator(selector) for selector in u.FAILURE.values()]
        check(name + " all show", until(page, lambda: all(note.is_visible() for note in notes), 20))
        u.wait_idle(page)
        readable(page, name, u.ALERT)
        check(name + " have no inline style", page.locator(u.INLINE_STYLE).count() == 0)
        healthy(page, name)
        u.shot(page, f"failure-{theme}-{width}")
        u.close(page)
    u.set_theme("fresh", "dark")


def controls(s, width):
    """The Jellyfin controls take any text, keep what the admin does, and send what the form shows."""
    name = width + " controls"
    extra = [{"Id": "jm-long", "Name": LONG_NAME, "CollectionType": "movies"}, {"Id": "jm-hostile", "Name": HOSTILE, "CollectionType": "movies"}]

    def odd(context):
        fake(context, "Servers/*/Libraries", lambda data: data.extend(dict(item, Selected=False) for item in extra))
        fake(context, "Peers/Options", lambda data: (data["Users"].append({"Id": SECOND_USER, "Name": "second"}), data["Libraries"].extend(extra)))

    page = s.open("fresh", width, route=odd)
    stock = u.server_row(page, "stock")
    connected = u.peer_row(page, "jellymesh")
    new_peer = u.peer_row(page, u.NEW_PEER)
    u.button(stock, "libraries").click()
    page.wait_for_selector(u.SERVER + " " + u.CHECK_MARK, state="attached", timeout=15000)

    select = new_peer.get_by_label(u.USER_LABEL, exact=True)
    check(name + " the select has its label", select.evaluate("node => node.tagName") == "SELECT")
    check(name + " the select has one arrow", new_peer.locator(u.SELECT_ARROW).count() == 1, new_peer.locator(u.SELECT_ARROW).count())
    limit = connected.get_by_label(u.LIMIT_LABEL, exact=True)
    check(name + " the limit has its label", limit.get_attribute("type") == "number")
    check(name + " has no label inside a label", page.locator(u.NESTED_LABEL).count() == 0)
    limit.fill("")
    check(name + " an empty limit keeps its label", connected.get_by_text(u.LIMIT_LABEL, exact=True).is_visible())
    limit.fill("4")

    names = stock.locator(u.CHECK_LABEL)
    check(name + " a name with markup stays text", names.filter(has_text=HOSTILE).count() == 1, names.all_inner_texts())
    check(name + " a name renders no markup", stock.locator("b").count() == 0)
    long_label = stock.locator("label", has_text=LONG_NAME)
    box = long_label.bounding_box()
    below = stock.locator("label", has_text=HOSTILE).bounding_box()
    check(name + " a long name stays on the screen", box["x"] + box["width"] <= u.VIEWPORTS[width]["width"], box)
    check(name + " a long name does not cover the next", box["y"] + box["height"] <= below["y"], [box, below])

    long_label.locator(u.CHECK_MARK).click()
    check(name + " the drawn box selects", stock.locator(u.CHECKBOX + "[value=jm-long]").is_checked())
    last = stock.locator(u.CHECKBOX).last
    last.focus()
    page.keyboard.press("Space")
    check(name + " Space selects a library", last.is_checked())

    u.mark(select)
    u.mark(limit)
    select.select_option(SECOND_USER)
    limit.fill("6")
    select.focus()
    with page.expect_response("**/JellyMesh/Admin/Peers", timeout=25000):
        pass
    u.wait_idle(page)
    check(name + " polling keeps the select", u.is_marked(select))
    check(name + " polling keeps the choice", select.input_value() == SECOND_USER, select.input_value())
    check(name + " polling keeps the focus", u.has_focus(select))
    check(name + " polling keeps the limit", u.is_marked(limit) and limit.input_value() == "6", limit.input_value())
    limit.fill("4")

    sent = []

    def connect(route):
        if route.request.method == "PUT":
            sent.append((route.request.url, route.request.post_data_json))
            route.fulfill(status=204)
        else:
            route.fallback()

    page.route("**/JellyMesh/Admin/Peers/*", connect)
    new_peer.locator(u.NUMBER).fill("3")
    u.tick(new_peer.locator(u.CHECKBOX).first)
    chosen = new_peer.locator(u.CHECKBOX).first.get_attribute("value")
    u.button(new_peer, "connect").click()
    u.wait_message(page)
    check(name + " connect sends the chosen user", len(sent) == 1 and sent[0][0].endswith("/Peers/" + SECOND_USER), sent)
    check(name + " connect sends the form", bool(sent) and sent[0][1] == {"SharedLibraryIds": [chosen], "MaxStreams": 3}, sent)
    healthy(page, name)
    u.shot(page, f"controls-{width}")
    u.close(page)


def confirm_shows(page, name, title, says, viewport):
    """Checks what a confirmation dialog says, that Cancel is the safe default, and that it fits the screen."""
    shown_title, shown_text = u.wait_confirm(page)
    check(name + " has the title", shown_title == title, shown_title)
    for words in says:
        check(f"{name} says: {words}", words in shown_text, shown_text)
    check(name + " renders no markup from a name", page.locator(u.CONFIRM_MARKUP).count() == 0)
    check(name + " starts on Cancel", u.confirm_starts_on_cancel(page))
    box = page.locator(u.CONFIRM).bounding_box()
    check(name + " fits the screen", box["x"] >= 0 and box["x"] + box["width"] <= viewport["width"]
          and box["y"] >= 0 and box["y"] + box["height"] <= viewport["height"], box)


def confirm_keeps(page, name, stays):
    """Cancels a confirmation dialog and checks that nothing happened."""
    u.answer_confirm(page, False)
    check(name + " Cancel changes nothing", stays())


def hostile_peer(context):
    """Gives the connected servers a name that would be markup if it were not escaped."""
    def rename(route):
        answer = route.fetch()
        route.fulfill(response=answer, json=[dict(peer, UserName=HOSTILE_PEER) for peer in answer.json()])

    context.route("**/JellyMesh/Admin/Peers", rename)


def confirms(s, width):
    """The destructive actions ask first, say what happens, and do it only when confirmed."""
    name = width + " confirm"
    viewport = u.VIEWPORTS[width]
    status = lambda: u.api("fresh", "GET", "/JellyMesh/Admin/Status")[1]["Servers"]
    peers = lambda: u.api("fresh", "GET", "/JellyMesh/Admin/Peers")[1]
    merges = lambda: u.api("fresh", "GET", "/JellyMesh/Admin/Merges")[1]
    stock = next(server for server in status() if server["Name"] == "stock")
    added = u.api("fresh", "POST", "/JellyMesh/Admin/Servers", {
        "Name": HOSTILE, "Url": stock["Url"], "Username": "jellymesh", "Password": "jellymesh-test"})
    check(name + " has a remote to remove", added[0] == 200, added)
    friend = next(user for user in u.api("fresh", "GET", "/Users")[1] if user["Name"] == "friend")
    library = u.api("fresh", "GET", "/JellyMesh/Admin/Peers/Options")[1]["Libraries"][0]["Id"]
    u.api("fresh", "PUT", "/JellyMesh/Admin/Peers/" + friend["Id"], {"SharedLibraryIds": [library], "MaxStreams": 1})
    case = next(group for group in merges()["Cases"] if PAIR in str(group))
    u.api("fresh", "POST", "/JellyMesh/Admin/Merges", {"WorkKeys": [title["WorkKey"] for title in case["Titles"]]})
    page = s.open("fresh", width)
    count = len(status())

    remove = u.button(u.server_row(page, HOSTILE), "remove")
    remove.click()
    confirm_shows(page, name + " remove", "Remove this server?", [
        HOSTILE + " and its stored login leave JellyMesh now", "login is not revoked", "do not play", "next sync",
        "does not start a sync", "without this backup", "Doubtful films", "may not stop right away", "same titles and watch state"], viewport)
    u.shot(page, f"confirm-remove-{width}", full_page=False)
    confirm_keeps(page, name + " remove", lambda: len(status()) == count and u.server_row(page, HOSTILE).count() == 1)
    remove.click()
    u.answer_confirm(page, True)
    check(name + " remove acts", until(page, lambda: len(status()) == count - 1 and u.server_row(page, HOSTILE).count() == 0))
    check(name + " remove leaves the others", u.server_row(page, "stock").count() == 1 and u.server_row(page, "peer").count() == 1)

    row = u.peer_row(page, "friend")
    check(name + " one word for a connected server", u.button(row, "disconnect").count() == 1 and u.button(row, "remove").count() == 0)
    u.button(row, "disconnect").click()
    confirm_shows(page, name + " disconnect", "Disconnect this server?", [
        "friend stops being a connected server now", "streams here are closed", "can still log in", "standard mode", "no stream limit",
        "Dashboard > Users"], viewport)
    u.shot(page, f"confirm-disconnect-{width}", full_page=False)
    held = len(peers())
    confirm_keeps(page, name + " disconnect", lambda: len(peers()) == held)
    u.button(u.peer_row(page, "friend"), "disconnect").click()
    u.answer_confirm(page, True)
    check(name + " disconnect acts", until(page, lambda: len(peers()) == held - 1 and u.peer_row(page, "friend").count() == 0))
    check(name + " disconnect keeps the user", any(user["Name"] == "friend" for user in u.api("fresh", "GET", "/Users")[1]))

    u.button(page.locator(u.MERGE), "undo").click()
    confirm_shows(page, name + " undo", "Undo this merge?", ["separate titles again", "keeps its watch state", "Local files are not changed"], viewport)
    u.shot(page, f"confirm-undo-{width}", full_page=False)
    confirm_keeps(page, name + " undo", lambda: len(merges()["Merges"]) == 1)
    u.button(page.locator(u.MERGE), "undo").click()
    u.answer_confirm(page, True)
    check(name + " undo acts", until(page, lambda: merges()["Merges"] == [] and page.locator(u.MERGE).count() == 0))
    healthy(page, name)
    page.context.close()

    page = s.open("fresh", width, route=hostile_peer)
    u.button(u.peer_row(page, HOSTILE_PEER), "disconnect").click()
    shown = u.wait_confirm(page)[1]
    check(name + " a user name shows as text", HOSTILE_PEER + " stops being" in shown, shown)
    check(name + " a user name renders no markup", page.locator(u.CONFIRM_MARKUP).count() == 0)
    confirm_keeps(page, name + " a hostile name", lambda: len(peers()) == held - 1)
    healthy(page, name)
    page.context.close()


def confirm_keyboard(s, width):
    """A confirmation can be opened and refused from the keyboard alone."""
    name = width + " confirm keyboard"
    page = s.open("fresh", width)
    remove = u.button(u.server_row(page, "stock"), "remove")
    remove.focus()
    page.keyboard.press("Enter")
    u.wait_confirm(page)
    page.keyboard.press("Enter")
    page.wait_for_selector(u.CONFIRM, state="detached", timeout=15000)
    servers = u.api("fresh", "GET", "/JellyMesh/Admin/Status")[1]["Servers"]
    check(name + " Enter on the first focus cancels", u.server_row(page, "stock").count() == 1 and len(servers) == 2, servers)
    healthy(page, name)
    page.context.close()


def select(case, count):
    """Selects the first titles of a group and leaves the others clear."""
    for index, box in enumerate(case.locator(u.CHECKBOX).all()):
        u.tick(box, index < count)


def result(case):
    """Returns the line under a group once the server has answered it."""
    node = case.locator(u.MERGE_RESULT)
    until(case.page, lambda: "Checking" not in node.inner_text(), 10)
    return node.inner_text()


def titles_of(data, name):
    """Returns the titles of the group that holds a film, in an answer of the merge list."""
    return next(group for group in data["Cases"] if name in str(group))["Titles"]


def merge_markup(page, name):
    """The markup of a title: a real emby-checkbox on the line of its short name, with the details outside the label."""
    case = u.merge_case(page, PAIR)
    u.reveal(case)
    seen = case.locator(u.MERGE_TITLE).first.evaluate("""row => {
      const label = row.querySelector('label');
      const box = label.querySelector('input');
      const details = document.getElementById(box.getAttribute('aria-describedby'));
      const nameBox = label.querySelector('.checkboxLabel').getBoundingClientRect();
      const outline = label.querySelector('.checkboxOutline').getBoundingClientRect();
      return {upgraded: box.classList.contains('emby-checkbox') && box.dataset.embycheckbox === 'true',
        blocks: label.querySelectorAll(':not(input, span)').length,
        outside: !!details && !label.contains(details), described: details ? details.textContent : '',
        level: outline.top < nameBox.bottom && nameBox.top < outline.bottom, right: nameBox.left >= outline.right - 1};
    }""")
    check(name + " the checkbox is an emby-checkbox", seen["upgraded"], seen)
    check(name + " the label holds no block element", seen["blocks"] == 0, seen)
    check(name + " the details are outside the label", seen["outside"], seen)
    check(name + " the details describe the checkbox", "min" in seen["described"], seen)
    check(name + " the checkbox is on the line of its name", seen["level"] and seen["right"], seen)

    data = u.api("fresh", "GET", "/JellyMesh/Admin/Merges")[1]
    copies = [title["Copies"][0] for title in titles_of(data, PAIR)]
    for copy in copies:
        box = page.get_by_role("checkbox", name=f"{PAIR} (2026), {copy['ServerName']}", exact=True)
        check(f"{name} the name of {copy['ServerName']} is short and whole", box.count() == 1, copy["ServerName"])

    shown = case.inner_text()
    for copy in copies:
        quality = copy["Versions"][0]["Quality"]
        for value in [f"{quality['Height']}p", "HDR" if quality["IsHdr"] else "SDR", "Mbit/s" if quality["BitRate"] >= 1000000 else "kbit/s", quality["Codec"].upper()]:
            check(f"{name} shows {value} of {copy['ServerName']}", value in shown, shown)


def merge_unknown(s, width, name):
    """Missing values stay unknown, several versions are all shown, and remote text stays text."""
    def change(data):
        titles = titles_of(data, PAIR)
        titles[0]["Copies"][0]["Versions"] = [
            {"MediaSourceId": "best", "Quality": {"Height": 2160, "IsHdr": True, "IsRangeKnown": True, "BitRate": 800000, "Codec": HOSTILE}},
            {"MediaSourceId": "low", "Quality": {"Height": None, "IsHdr": False, "IsRangeKnown": False, "BitRate": None, "Codec": None}},
        ]
        titles[1]["Copies"][0]["Versions"] = [
            {"MediaSourceId": "gone", "Quality": {"Height": 1080, "IsHdr": False, "IsRangeKnown": False, "BitRate": None, "Codec": None}}]
        titles[1]["Copies"].append({**titles[1]["Copies"][0], "Copy": {**titles[1]["Copies"][0]["Copy"], "IsLocal": True, "ItemId": "here"}, "Versions": []})

    page = s.open("fresh", width, route=lambda context: context.route("**/JellyMesh/Admin/Merges", lambda route: answer_list(route, change)))
    shown = u.merge_case(page, PAIR).inner_text()
    check(name + " shows the best version first", "Best version: 2160p - HDR - 800 kbit/s" in shown, shown)
    check(name + " shows the other version", "Other version: Quality unknown" in shown, shown)
    check(name + " says what is unknown one by one", "1080p - HDR unknown - bitrate unknown - codec unknown" in shown, shown)
    check(name + " says a local copy has no quality read", "Quality not read" in shown, shown)
    check(name + " shows several copies in the name", " and 1 more" in text(page, u.MERGE_NAME + " >> nth=1"), shown)
    check(name + " keeps remote text as text", HOSTILE.upper() in shown and page.locator(u.MERGE_CASES + " b").count() == 0, shown)
    healthy(page, name)
    u.shot(page, f"merge-unknown-{width}")
    u.close(page)


def answer_list(route, change):
    """Rewrites the merge list and passes every other request on."""
    if route.request.method != "GET":
        route.continue_()
        return
    response = route.fetch()
    data = response.json()
    change(data)
    route.fulfill(response=response, json=data)


def merge_selection(page, name):
    """The button follows the rules of the server, and the line under it says why or what would play."""
    case = u.merge_case(page, PAIR)
    merge = u.button(case, "merge")
    check(name + " nothing selected cannot merge", merge.is_disabled())
    check(name + " nothing selected says what is needed", "at least two" in result(case), result(case))
    select(case, 1)
    check(name + " one title cannot merge", merge.is_disabled())
    check(name + " one title says what is needed", "at least two" in result(case), result(case))
    posts = []
    page.on("request", lambda request: posts.append(request.url) if request.method == "POST" and "/Merges" in request.url else None)
    faded = merge.evaluate("node => getComputedStyle(node).opacity")
    select(case, 2)
    check(name + " two titles can merge", merge.is_enabled())
    solid = until(page, lambda: float(merge.evaluate("node => getComputedStyle(node).opacity")) == 1, 3)
    check(name + " a button that cannot merge looks it", float(faded) < 1 and solid, faded)

    data = u.api("fresh", "GET", "/JellyMesh/Admin/Merges")[1]
    keys = [title["WorkKey"] for title in titles_of(data, PAIR)]
    forecast = u.api("fresh", "POST", "/JellyMesh/Admin/Merges/Preview", {"WorkKeys": keys})[1]
    said = result(case)
    check(name + " the server has a winner", forecast["Copy"]["ServerName"] == "peer" and forecast["IsCertain"], forecast)
    check(name + " says what would play", f"on {forecast['Copy']['ServerName']} plays" in said and f"{forecast['Version']['Quality']['Height']}p" in said, said)
    check(name + " says the others stay as backups", "backups" in said, said)
    check(name + " the line is a status region", case.locator(u.MERGE_RESULT).get_attribute("role") == "status")
    check(name + " asks only for a preview", posts and all(url.endswith("/Merges/Preview") for url in posts), posts)
    check(name + " nothing was merged by asking", u.api("fresh", "GET", "/JellyMesh/Admin/Merges")[1]["Merges"] == [])
    select(case, 0)


def merge_forecasts(s, width, name):
    """Every answer about what would play, and two local titles."""
    def two_local(data):
        for title in titles_of(data, PAIR):
            title["HasLocalCopy"] = True

    page = s.open("fresh", width, route=lambda context: context.route("**/JellyMesh/Admin/Merges", lambda route: answer_list(route, two_local)))
    case = u.merge_case(page, PAIR)
    select(case, 2)
    check(name + " two local titles cannot merge", u.button(case, "merge").is_disabled())
    check(name + " two local titles say why", "two local films" in result(case), result(case))
    u.close(page)

    page = s.open("fresh", width)
    case = u.merge_case(page, PAIR)
    copy = titles_of(u.api("fresh", "GET", "/JellyMesh/Admin/Merges")[1], PAIR)[0]["Copies"][0]
    local = {**copy, "Copy": {**copy["Copy"], "IsLocal": True}, "Versions": []}
    answers = [
        ({"Copy": local, "Version": None, "IsCertain": True}, 200, "local copy always wins"),
        ({"Copy": copy, "Version": copy["Versions"][0], "IsCertain": False}, 200, "cannot be known for sure"),
        ({"Copy": None, "Version": None, "IsCertain": False}, 200, "cannot be known"),
        ({"Message": "The list changed. Check it again, then merge."}, 409, "The list changed"),
    ]
    for answer, status, says in answers:
        page.route("**/JellyMesh/Admin/Merges/Preview", lambda route: route.fulfill(status=status, json=answer))
        select(case, 0)
        select(case, 2)
        check(f"{name} says '{says}'", says in result(case), result(case))
        page.unroute("**/JellyMesh/Admin/Merges/Preview")

    hold = []
    page.route("**/JellyMesh/Admin/Merges/Preview", lambda route: hold.append(route))
    select(case, 0)
    select(case, 2)
    until(page, lambda: hold)
    select(case, 1)
    hold.pop().fulfill(status=200, json={"Copy": None, "Version": None, "IsCertain": False})
    page.wait_for_timeout(300)
    check(name + " a late answer does not replace the current one", "at least two" in result(case), result(case))
    healthy(page, name)
    u.shot(page, f"merge-forecast-{width}")
    u.close(page)


def merge_refresh(s, width, name):
    """New data reaches a kept row, which keeps its selection, its focus and its answer."""
    state = {"bitrate": None}

    def change(data):
        if state["bitrate"]:
            titles_of(data, PAIR)[0]["Copies"][0]["Versions"][0]["Quality"]["BitRate"] = state["bitrate"]

    page = s.open("fresh", width, route=lambda context: context.route("**/JellyMesh/Admin/Merges", lambda route: answer_list(route, change)))
    case = u.merge_case(page, PAIR)
    box = case.locator(u.CHECKBOX).first
    last = case.locator(u.CHECKBOX).last
    select(case, 1)
    u.mark(box)
    last.focus()
    state["bitrate"] = 123456789
    u.refresh_merges(page)
    check(name + " shows the new data", until(page, lambda: "123.5 Mbit/s" in case.inner_text()), case.inner_text())
    check(name + " keeps the row", u.is_marked(box))
    check(name + " keeps the selection", box.is_checked() and not last.is_checked())
    check(name + " keeps the focus", last.evaluate("node => node === document.activeElement"))
    check(name + " keeps the rule of the button", u.button(case, "merge").is_disabled())
    page.keyboard.press("Space")
    check(name + " the focused box still works", last.is_checked())
    check(name + " the button follows", u.button(case, "merge").is_enabled())
    healthy(page, name)
    u.close(page)


ADVANCED_RANGES = {
    "#jmRemotePlaybackInfoTimeout": "From 3 to 60",
    "#jmStreamOpenTimeout": "From 5 to 60",
    "#jmPlaybackIdle": "From 30 to 109",
    "#jmSessionRetention": "From 1 to 168",
    "#jmPolicyCache": "From 1 to 60",
}


def advanced_closed(page, name):
    """The playback times start closed, and the keyboard opens them."""
    check(name + " starts closed", not u.advanced_open(page))
    for selector in ADVANCED_RANGES:
        check(f"{name} hides {selector}", not page.locator(selector).is_visible())
    for selector in [u.SYNC_INTERVAL, u.FEDERATED_FOLDER, u.METADATA_PROTECTION, u.SAVE_SETTINGS]:
        check(f"{name} leaves {selector} in reach", page.locator(selector).is_visible())

    toggle = page.locator(u.ADVANCED_TOGGLE)
    toggle.focus()
    page.keyboard.press("Enter")
    check(name + " Enter opens it", u.advanced_open(page))
    page.keyboard.press("Enter")
    check(name + " Enter closes it", not u.advanced_open(page))
    page.keyboard.press("Space")
    check(name + " Space opens it", u.advanced_open(page))
    for selector, rule in ADVANCED_RANGES.items():
        check(f"{name} shows {selector}", page.locator(selector).is_visible())
        said = page.locator(selector).evaluate("input => input.parentElement.querySelector('.fieldDescription').textContent")
        check(f"{name} gives the range of {selector}", said.startswith(rule), said)


def advanced_opens(page, name):
    """A wrong value left in the closed part opens it, so the field can be seen and focused."""
    policy = page.locator("#jmPolicyCache").input_value()
    interval = page.locator(u.SYNC_INTERVAL).input_value()
    u.open_advanced(page)
    page.locator("#jmPolicyCache").fill("0")
    page.locator(u.ADVANCED_TOGGLE).click()
    check(name + " the wrong value is hidden", not page.locator("#jmPolicyCache").is_visible())
    page.locator(u.SAVE_SETTINGS).click()
    outcome(page, name + " a hidden field", "Error", "Remember the transcoding rights")
    check(name + " a hidden field opens the part", u.advanced_open(page))
    check(name + " a hidden field is focused", u.focused(page) == "jmPolicyCache", u.focused(page))
    check(name + " a hidden field is in view", u.in_view(page, "#jmPolicyCache"))
    check(name + " a hidden field is marked", "from 1 to 60" in u.field_error(page, "#jmPolicyCache"))
    page.locator("#jmPolicyCache").fill(policy)
    page.locator(u.ADVANCED_TOGGLE).click()

    page.locator(u.SYNC_INTERVAL).fill("60")
    page.locator(u.SAVE_SETTINGS).click()
    outcome(page, name + " a common field", "Error", "Sync interval")
    check(name + " a common field leaves the part closed", not u.advanced_open(page))
    check(name + " a common field is focused", "#" + u.focused(page) == u.SYNC_INTERVAL, u.focused(page))

    u.open_advanced(page)
    page.locator("#jmPolicyCache").fill("0")
    page.locator(u.ADVANCED_TOGGLE).click()
    page.locator(u.SAVE_SETTINGS).click()
    outcome(page, name + " both", "Error", "1 more field")
    check(name + " both focuses the first", "#" + u.focused(page) == u.SYNC_INTERVAL, u.focused(page))
    check(name + " both shows the other", u.advanced_open(page) and page.locator("#jmPolicyCache").is_visible())
    page.locator("#jmPolicyCache").fill(policy)
    page.locator(u.SYNC_INTERVAL).fill(interval)
    u.clear_message(page)


def folder_chosen(page, name):
    """The folder browser fills the field, and nothing is saved until the admin saves."""
    field = page.locator(u.FEDERATED_FOLDER)
    field.fill("relative/path")
    page.locator(u.SAVE_SETTINGS).click()
    outcome(page, name + " a wrong path", "Error", "absolute path")

    u.open_picker(page)
    check(name + " lists the folders of the server", page.locator(u.PICKER_ROW + '[data-path="/federata"]').count() == 1)
    page.locator(u.PICKER_CLOSE).click()
    page.wait_for_selector(u.CONFIRM, state="detached", timeout=15000)
    check(name + " cancelling keeps the field", field.input_value() == "relative/path", field.input_value())

    u.open_picker(page)
    u.shot(page, f"folder-browser-{page.viewport_size['width']}", full_page=False)
    u.choose_folder(page, "/federata")
    check(name + " the choice fills the field", field.input_value() == "/federata", field.input_value())
    check(name + " the choice clears the old error", u.field_error(page, u.FEDERATED_FOLDER) == "")
    check(name + " the focus returns to the button", "#" + u.focused(page) == u.CHOOSE_FOLDER, u.focused(page))
    outcome(page, name + " the choice", "Done", "Save the settings")
    check(name + " the choice is not saved yet", u.config("first")["FederatedFolderPath"] == "")
    page.wait_for_timeout(6000)
    check(name + " reading again keeps the choice", field.input_value() == "/federata", field.input_value())

    page.locator(u.SAVE_SETTINGS).click()
    check(name + " the choice saves", until(page, lambda: u.config("first")["FederatedFolderPath"] == "/federata"))
    u.wait_idle(page)

    field.fill("/typed/by/hand")
    check(name + " the field stays usable", field.input_value() == "/typed/by/hand")
    page.locator(u.SAVE_SETTINGS).click()
    check(name + " a typed path saves", until(page, lambda: u.config("first")["FederatedFolderPath"] == "/typed/by/hand"))
    u.wait_idle(page)


def folder_paths(server):
    """Returns the two paths to link that the server reports."""
    folder = u.api(server, "GET", "/JellyMesh/Admin/Status")[1]["FederatedFolder"]
    return folder["MoviesPath"], folder["SeriesPath"]


def copied_over_http(page, name):
    """The copy buttons work on plain HTTP, where the browser has no clipboard API, and say so."""
    check(name + " runs on plain HTTP", page.evaluate("!isSecureContext && navigator.clipboard === undefined"))
    for path in folder_paths("fresh"):
        button = u.copy_button(page, path)
        button.click()
        outcome(page, f"{name} {path}", "Done", path)
        check(f"{name} {path} reaches the clipboard", u.pasted(page) == path)
    u.clear_message(page)
    button = u.copy_button(page, folder_paths("fresh")[1])
    button.focus()
    page.keyboard.press("Enter")
    outcome(page, name + " from the keyboard", "Done", "Copied")
    check(name + " the focus stays on the button", u.has_focus(button))
    u.clear_message(page)


def copy_refused(s, width):
    """When the browser refuses, the page says so and leaves the path selected."""
    name = width + " copy refused"

    def refuses(context):
        context.add_init_script("document.execCommand = () => false;")

    page = s.open("fresh", width, route=refuses)
    path = folder_paths("fresh")[0]
    u.copy_button(page, path).click()
    outcome(page, name, "Error", "copy it by hand")
    check(name + " selects the path", page.evaluate("getSelection().toString()") == path, page.evaluate("getSelection().toString()"))
    healthy(page, name)
    u.close(page)


def merge_dates(s, width, name):
    """A merge shows its date, its quality and why it is not in effect."""
    merges = lambda: u.api("fresh", "GET", "/JellyMesh/Admin/Merges")[1]
    keys = [title["WorkKey"] for title in titles_of(merges(), PAIR)]
    u.api("fresh", "POST", "/JellyMesh/Admin/Merges", {"WorkKeys": keys})
    merge = merges()["Merges"][0]

    def lapsed(data):
        data["Merges"][0]["IsInEffect"] = False

    try:
        page = s.open("fresh", width, route=lambda context: context.route("**/JellyMesh/Admin/Merges", lambda route: answer_list(route, lapsed)))
        row = page.locator(u.MERGE)
        check(name + " has a date", row.locator(u.MERGE_TIME).get_attribute("datetime") == merge["CreatedUtc"], row.inner_text())
        check(name + " says when", row.locator(u.MERGE_DATE).inner_text().startswith("Merged on ") and any(c.isdigit() for c in row.locator(u.MERGE_TIME).inner_text()), row.inner_text())
        check(name + " shows the quality of its copies", "Mbit/s" in row.inner_text() or "kbit/s" in row.inner_text(), row.inner_text())
        check(name + " explains that it is not in effect", "Not in effect: a copy is missing, or the IDs already make these one film." in row.inner_text(), row.inner_text())
        u.shot(page, f"merge-date-{width}")
        healthy(page, name)
        u.close(page)
    finally:
        u.api("fresh", "DELETE", "/JellyMesh/Admin/Merges/" + merge["Id"])


def merge_details(s, width):
    """What the group of doubtful films says about quality, what would play, and when a merge can be made."""
    name = width + " merge details"
    page = s.open("fresh", width)
    merge_markup(page, name)
    merge_selection(page, name)
    healthy(page, name)
    select(u.merge_case(page, PAIR), 2)
    result(u.merge_case(page, PAIR))
    u.merge_case(page, PAIR).scroll_into_view_if_needed()
    u.shot(page, f"merge-details-{width}")
    page.context.close()
    merge_unknown(s, width, name + " unknown values")
    merge_forecasts(s, width, name + " forecast")
    merge_refresh(s, width, name + " refresh")
    merge_dates(s, width, name + " merged")


def copy_with_api(s, width):
    """Where the browser has a clipboard API it is used, and a refusal falls back to the selection."""
    name = width + " copy with the API"

    def clipboard(context):
        context.add_init_script("""Object.defineProperty(navigator, 'clipboard', {value: {writeText: text => {
          window.jmWritten = (window.jmWritten || []).concat(text);
          return window.jmWritten.length > 1 ? Promise.reject(new Error('denied')) : Promise.resolve();
        }}});""")

    page = s.open("fresh", width, route=clipboard)
    path = folder_paths("fresh")[0]
    u.copy_button(page, path).click()
    outcome(page, name, "Done", path)
    check(name + " writes the path", page.evaluate("window.jmWritten") == [path], page.evaluate("window.jmWritten"))
    u.clear_message(page)
    u.copy_button(page, path).click()
    outcome(page, name + " refused", "Done", path)
    check(name + " refused still reaches the clipboard", u.pasted(page) == path)
    page.context.close()


def link_warnings(page, name):
    """A change in the links leaves the path rows and their focus alone, and a long path fits."""
    button = u.copy_button(page, folder_paths("fresh")[0])
    u.mark(button)
    button.focus()

    def unlinked(status):
        status["FederatedFolder"]["Link"] = "NotLinked"
        status["FederatedFolder"]["Movies"]["Link"] = "NotLinked"

    fake(page, "Status", unlinked)
    check(name + " warns about the path", until(page, lambda: "is not linked" in text(page, u.FOLDER_NOTES), 20), text(page, u.FOLDER_NOTES))
    check(name + " keeps the row", u.is_marked(button))
    check(name + " keeps the focus", u.has_focus(button))
    u.shot(page, f"folder-warning-{page.viewport_size['width']}")
    page.unroute("**/JellyMesh/Admin/Status")

    def long_path(status):
        status["FederatedFolder"]["MoviesPath"] = "/media/" + "a-very-long-folder-name/" * 6 + "Film"

    fake(page, "Status", long_path)
    check(name + " shows a long path", until(page, lambda: u.copy_button(page, "a-very-long-folder-name").count() == 1, 20))
    healthy(page, name + " long path")
    page.unroute("**/JellyMesh/Admin/Status")


def settings_look(s, width):
    """The settings in both themes, with the advanced part open, stay readable."""
    for theme in ["dark", "light"]:
        name = f"{width} {theme} settings"
        page = s.open("fresh", width, theme=theme)
        u.shot(page, f"settings-closed-{theme}-{width}")
        u.open_advanced(page)
        readable = ", ".join([u.READABLE, u.ADVANCED_TOGGLE, u.FOLDER_PATH + " code", u.FOLDER_PATH + " span", u.FOLDER_NOTES])
        seen = contrast(page, readable)
        check(name + " text is readable", seen["worst"]["ratio"] >= 4.5, seen["worst"])
        healthy(page, name)
        u.shot(page, f"settings-open-{theme}-{width}")
        page.context.close()
    u.set_theme("fresh", "dark")


def settings_form(s, width):
    """The Settings form: what stays closed, how the folder is chosen, and how its paths are copied."""
    name = width + " settings form"
    page = s.open("fresh", width)
    advanced_closed(page, name + " advanced")
    healthy(page, name + " advanced")
    page.context.close()

    page = s.open("fresh", width)
    advanced_opens(page, name)
    healthy(page, name)
    page.context.close()

    page = s.open("first", width)
    try:
        folder_chosen(page, name + " folder")
        healthy(page, name + " folder")
    finally:
        u.settings("first", lambda config: config.update(FederatedFolderPath=""))
    page.context.close()

    page = s.open("fresh", width)
    copied_over_http(page, name + " copy")
    healthy(page, name + " copy")
    link_warnings(page, name + " links")
    page.context.close()

    copy_refused(s, width)
    copy_with_api(s, width)
    settings_look(s, width)


TAB_LABELS = ["Overview", "Servers", "Catalog", "Sharing", "Settings"]
TAB_PARTS = {
    "overview": [u.STATUS],
    "servers": [u.SERVERS, u.ADD_FORM],
    "catalog": [u.SYNC_NOW, u.MERGE_CASES, u.MERGES],
    "sharing": [u.PEERS],
    "settings": [u.SETTINGS_FORM],
}
HEADINGS = """panel => [...panel.querySelectorAll('h1, h2, h3, h4, h5, h6')].filter(h => h.offsetParent).map(h => h.tagName)"""
SCROLL = "[document.documentElement.scrollHeight, innerHeight]"


def tabs_laid_out(s, width):
    """The tabs are in order, each holds its part of the page, and each fits the screen."""
    name = width + " tabs"
    page = s.open("fresh", width)
    check(name + " are in order", page.locator(u.TAB).all_inner_texts() == TAB_LABELS, page.locator(u.TAB).all_inner_texts())
    check(name + " start on Overview", u.shown_tab(page) == "overview", u.shown_tab(page))
    for box in [page.locator(u.TAB).nth(index).bounding_box() for index in range(5)]:
        check(name + " are all on the screen", box["x"] >= 0 and box["x"] + box["width"] <= u.VIEWPORTS[width]["width"], box)
    for index, tab in enumerate(u.TABS):
        u.show_tab(page, tab)
        label = f"{name} {tab}"
        check(label + " is the one panel shown", page.locator(u.PANEL + ":not([hidden])").count() == 1)
        check(label + " is marked in the header", page.locator(u.TAB + "[aria-selected=true]").inner_text() == TAB_LABELS[index])
        check(label + " is in the address", page.evaluate("location.hash").endswith("#" + tab), page.evaluate("location.hash"))
        panel = page.locator(u.PANEL + ":not([hidden])")
        for part in TAB_PARTS[tab]:
            check(f"{label} holds {part}", panel.locator(part).count() == 1)
        levels = panel.evaluate(HEADINGS)
        check(label + " opens with one heading level", levels[0] == "H3" and set(levels) <= {"H3", "H4"}, levels)
        check(label + " does not scroll sideways", page.evaluate("document.documentElement.scrollWidth <= innerWidth"))
        sizes = page.evaluate(SCROLL)
        print(f"INFO {label} is {sizes[0] / sizes[1]:.1f} screens long, {sizes[0]} px", flush=True)
        u.shot(page, f"tab-{tab}-{width}")
    u.show_tab(page, "servers")
    check(name + " Servers says what it reads", "reads from other servers" in page.locator(u.PANEL + ":not([hidden])").inner_text())
    u.show_tab(page, "sharing")
    check(name + " Sharing says what others read", "read from this server" in page.locator(u.PANEL + ":not([hidden])").inner_text())
    healthy(page, name)
    page.context.close()


def tabs_remembered(s, width):
    """A tab survives a reload, a return and the browser buttons, and can be linked to."""
    name = width + " tabs"
    page = s.open("fresh", width)
    u.show_tab(page, "catalog")
    page.reload()
    u.wait_loaded(page)
    check(name + " a reload keeps the tab", u.shown_tab(page) == "catalog", u.shown_tab(page))
    check(name + " a reload keeps the mark", page.locator(u.TAB + "[aria-selected=true]").inner_text() == "Catalog")

    page.evaluate("location.hash = '#/dashboard'")
    page.wait_for_function("() => !location.hash.includes('configurationpage')", timeout=15000)
    page.wait_for_selector(u.TAB, state="detached", timeout=15000)
    check(name + " leave the header with the page", page.locator(u.TAB).count() == 0)
    page.go_back()
    u.wait_loaded(page)
    check(name + " Back to the page keeps the tab", u.shown_tab(page) == "catalog", u.shown_tab(page))
    check(name + " come back with the page", page.locator(u.TAB + "[aria-selected=true]").inner_text() == "Catalog")
    page.evaluate("location.hash = '#/dashboard'")
    page.wait_for_function("() => !location.hash.includes('configurationpage')", timeout=15000)
    page.evaluate("location.hash = '#/configurationpage?name=JellyMesh'")
    u.wait_loaded(page)
    page.wait_for_selector(u.TAB + "[aria-selected=true]", timeout=15000)
    check(name + " a return with a plain link keeps the tab", u.shown_tab(page) == "catalog", u.shown_tab(page))
    check(name + " the address names the tab again", page.evaluate("location.hash").endswith("#catalog"), page.evaluate("location.hash"))

    page.goto(page.url.split("#/")[0] + "#/configurationpage?name=JellyMesh")
    page.reload()
    u.wait_loaded(page)
    check(name + " a new load of the plain address remembers the tab", u.shown_tab(page) == "catalog", u.shown_tab(page))

    page.evaluate("sessionStorage.setItem('jellymesh-tab', 'nonsense')")
    page.goto(page.url.split("#/")[0] + "#/configurationpage?name=JellyMesh")
    page.reload()
    u.wait_loaded(page)
    check(name + " an unknown remembered tab is ignored", u.shown_tab(page) in u.TABS, u.shown_tab(page))
    page.context.close()

    for tab in ["settings", "sharing"]:
        page = s.open("fresh", width, tab=tab)
        check(f"{name} a link opens {tab}", u.shown_tab(page) == tab, u.shown_tab(page))
        check(f"{name} a link marks {tab}", page.locator(u.TAB + "[aria-selected=true]").inner_text().lower() == tab)
        page.context.close()
    page = s.open("fresh", width, tab="nonsense")
    check(name + " an unknown link opens Overview", u.shown_tab(page) == "overview", u.shown_tab(page))
    page.context.close()

    page = s.open("fresh", width)
    u.mark(page.locator(u.PAGE))
    field = page.locator(u.ADD_FIELDS["name"])
    field.fill("kept")
    u.show_tab(page, "catalog")
    u.show_tab(page, "sharing")
    page.go_back()
    check(name + " Back goes to the tab before", until(page, lambda: u.shown_tab(page) == "catalog"), u.shown_tab(page))
    check(name + " Back moves the mark", until(page, lambda: page.locator(u.TAB + "[aria-selected=true]").inner_text() == "Catalog"))
    page.go_forward()
    check(name + " Forward goes to the next tab", until(page, lambda: u.shown_tab(page) == "sharing"), u.shown_tab(page))
    page.locator(u.PAGE).evaluate("""node => { const link = document.createElement('a');
      link.href = '#/configurationpage?name=JellyMesh#settings'; node.appendChild(link); link.click(); link.remove(); }""")
    check(name + " a plain link on the page opens a tab", until(page, lambda: u.shown_tab(page) == "settings"), u.shown_tab(page))
    check(name + " a plain link keeps the page loaded", u.is_marked(page.locator(u.PAGE)) and page.locator(u.PAGE).count() == 1)
    page.go_back()
    until(page, lambda: u.shown_tab(page) == "sharing")
    page.locator(u.NUMBER).first.focus()
    page.go_back()
    until(page, lambda: u.shown_tab(page) == "catalog")
    check(name + " Back from a field keeps the focus on the page", page.evaluate("document.activeElement.getAttribute('role')") == "tabpanel",
          page.evaluate("document.activeElement.tagName"))
    check(name + " the browser buttons keep the page loaded", u.is_marked(page.locator(u.PAGE)) and page.locator(u.PAGE).count() == 1)
    check(name + " the browser buttons keep an edit", field.input_value() == "kept", field.input_value())
    healthy(page, name)
    page.context.close()

    page = s.open("fresh", width)
    u.show_tab(page, "catalog")
    page.evaluate("location.hash = '#/home'")
    page.wait_for_selector(".emby-tab-button", state="attached", timeout=15000)
    check(name + " another page keeps its own tabs", page.locator(u.TAB).count() == 0 and page.locator(".emby-tab-button").count() > 0)
    check(name + " are not left in the styles of another page", not page.evaluate("document.body.classList.contains('jmTabs')"))
    page.context.close()


def tabs_keyboard(s, width):
    """The tabs work with arrows, Enter and Space, and show where the keyboard is."""
    name = width + " tabs keyboard"
    page = s.open("fresh", width)
    first_tab = page.locator(u.TAB).first
    first_tab.focus()
    check(name + " one tab takes Tab", page.locator(u.TAB + "[tabindex='0']").count() == 1)
    page.keyboard.press("ArrowRight")
    check(name + " an arrow moves along the tabs", page.evaluate("document.activeElement.textContent") == "Servers", page.evaluate("document.activeElement.textContent"))
    check(name + " an arrow alone opens nothing", u.shown_tab(page) == "overview", u.shown_tab(page))
    page.keyboard.press("Enter")
    check(name + " Enter opens the tab", until(page, lambda: u.shown_tab(page) == "servers"), u.shown_tab(page))
    page.keyboard.press("ArrowRight")
    page.keyboard.press("Space")
    check(name + " Space opens the tab", until(page, lambda: u.shown_tab(page) == "catalog"), u.shown_tab(page))
    check(name + " Space does not scroll", page.evaluate("scrollY") == 0, page.evaluate("scrollY"))
    focus_shows(page, name + " a tab", page.locator(u.TAB).nth(3))
    u.shot(page, f"tab-focus-{width}", full_page=False)
    healthy(page, name)
    page.context.close()


def tabs_visible_parts(s, width):
    """The warning and the outcome show in every tab, and a hidden section goes on updating."""
    name = width + " tabs"
    page = s.open("fresh", width, route=warned)
    u.button(u.server_row(page, "stock"), "test").click()
    u.wait_message(page)
    for tab in u.TABS:
        u.show_tab(page, tab)
        check(f"{name} {tab} shows the warning", u.in_view(page, u.WARNING))
        check(f"{name} {tab} shows the outcome", u.in_view(page, u.OUTCOME))
        page.evaluate("scrollTo(0, document.documentElement.scrollHeight)")
        check(f"{name} {tab} keeps the outcome at the end", u.in_view(page, u.OUTCOME))
        page.evaluate("scrollTo(0, 0)")
        u.shot(page, f"tab-warned-{tab}-{width}", full_page=False)
    u.close(page)

    page = s.open("fresh", width)
    connected = u.peer_row(page, "jellymesh")
    stock = u.server_row(page, "stock")
    fake(page, "Peers", lambda peers: peers[0].update(ActiveStreams=1))
    fake(page, "Status", lambda status: [server.update(Connection="Offline") for server in status["Servers"] if server["Name"] == "stock"])
    check(name + " a hidden section updates", until(page, lambda: "Streams in use: 1 of" in connected.inner_text(), 18), connected.inner_text())
    check(name + " another hidden section updates", until(page, lambda: "offline" in stock.inner_text(), 18), stock.inner_text())
    check(name + " updating leaves the tab alone", u.shown_tab(page) == "overview", u.shown_tab(page))
    page.unroute_all(behavior="ignoreErrors")
    u.close(page)


def tabs_keep_edits(s, width):
    """What the admin was doing in a tab is still there after a walk through the others."""
    name = width + " tabs"
    page = s.open("fresh", width)
    stock = u.server_row(page, "stock")
    connected = u.peer_row(page, "jellymesh")
    case = u.merge_case(page, PAIR)
    page.locator(u.ADD_FIELDS["name"]).fill("kept name")
    u.button(stock, "libraries").click()
    page.wait_for_selector(u.SERVER + " " + u.CHECKBOX, state="attached", timeout=15000)
    u.tick(stock.locator(u.CHECKBOX).first, False)
    connected.locator(u.NUMBER).fill("9")
    u.tick(connected.locator(u.CHECKBOX).first, False)
    select(case, 2)
    forecast = result(case)
    u.open_advanced(page)
    page.locator(u.SYNC_INTERVAL).fill("777")
    page.locator(u.FEDERATED_FOLDER).fill("/typed")
    page.wait_for_timeout(6000)
    for tab in u.TABS + u.TABS:
        u.show_tab(page, tab)
    check(name + " keep the add form", page.locator(u.ADD_FIELDS["name"]).input_value() == "kept name")
    check(name + " keep the open libraries", stock.locator(u.CHECKBOX).count() == 2, stock.locator(u.CHECKBOX).count())
    check(name + " keep the library choice", stock.locator(u.CHECKBOX + ":checked").count() == 1)
    check(name + " keep the stream limit", connected.locator(u.NUMBER).input_value() == "9", connected.locator(u.NUMBER).input_value())
    check(name + " keep the shared box", connected.locator(u.CHECKBOX + ":checked").count() == 1)
    check(name + " keep the selection", case.locator(u.CHECKBOX + ":checked").count() == 2)
    check(name + " keep the forecast", result(case) == forecast, result(case))
    check(name + " keep the settings", page.locator(u.SYNC_INTERVAL).input_value() == "777" and page.locator(u.FEDERATED_FOLDER).input_value() == "/typed")
    check(name + " keep the open part", u.advanced_open(page))

    u.button(stock, "remove").click()
    u.wait_confirm(page)
    u.answer_confirm(page, False)
    check(name + " a dialog leaves the tab", u.shown_tab(page) == "servers", u.shown_tab(page))
    u.open_picker(page)
    page.locator(u.PICKER_CLOSE).click()
    page.wait_for_selector(u.CONFIRM, state="detached", timeout=15000)
    check(name + " the folder browser leaves the tab", u.shown_tab(page) == "settings", u.shown_tab(page))
    healthy(page, name)
    page.context.close()


def tabs_field_error(s, width):
    """A wrong field in a tab that is not on screen opens that tab, and its closed part, before taking the focus."""
    name = width + " tabs field error"
    page = s.open("fresh", width)
    policy = page.locator("#jmPolicyCache").input_value()
    u.open_advanced(page)
    page.locator("#jmPolicyCache").fill("0")
    page.locator(u.ADVANCED_TOGGLE).click()
    u.show_tab(page, "overview")
    page.locator(u.SETTINGS_FORM).evaluate("form => form.requestSubmit()")
    outcome(page, name, "Error", "Remember the transcoding rights")
    check(name + " opens the tab", u.shown_tab(page) == "settings", u.shown_tab(page))
    check(name + " marks the tab", until(page, lambda: page.locator(u.TAB + "[aria-selected=true]").inner_text() == "Settings"))
    check(name + " opens the closed part", u.advanced_open(page))
    check(name + " focuses the field", u.focused(page) == "jmPolicyCache", u.focused(page))
    check(name + " brings the field into view", u.in_view(page, "#jmPolicyCache"))
    check(name + " puts the tab in the address", page.evaluate("location.hash").endswith("#settings"), page.evaluate("location.hash"))
    page.locator("#jmPolicyCache").fill(policy)
    healthy(page, name)
    page.context.close()


def tabs(s, width):
    """The tabs of the page, on their own."""
    tabs_laid_out(s, width)
    tabs_remembered(s, width)
    tabs_keyboard(s, width)
    tabs_visible_parts(s, width)
    tabs_keep_edits(s, width)
    tabs_field_error(s, width)


SCENARIOS = {
    "tabs": (tabs, WIDTHS),
    "first": (first, WIDTHS),
    "first-nokey": (first_nokey, WIDTHS),
    "normal": (normal, WIDTHS),
    "loading": (loading, WIDTHS),
    "errors": (errors, WIDTHS),
    "keyboard": (keyboard, WIDTHS),
    "themes": (themes, WIDTHS),
    "actions": (actions, ["1280"]),
    "confirms": (confirms, WIDTHS),
    "confirm-keyboard": (confirm_keyboard, WIDTHS),
    "offline": (offline, WIDTHS),
    "nokey": (nokey, WIDTHS),
    "edits": (edits, ["1280"]),
    "quiet": (quiet, ["1280"]),
    "polling": (polling, ["1280"]),
    "sections": (sections, ["1280"]),
    "outcomes": (outcomes, WIDTHS),
    "outcome-themes": (outcome_themes, WIDTHS),
    "merge-details": (merge_details, WIDTHS),
    "settings-form": (settings_form, WIDTHS),
    "controls": (controls, WIDTHS),
    "control-themes": (control_themes, WIDTHS),
}

with u.Session() as session:
    for scenario in sys.argv[1:]:
        run, widths = SCENARIOS[scenario]
        for size in widths:
            try:
                run(session, size)
            except Exception as error:
                check(f"{size} {scenario} ran to the end", False, str(error).splitlines()[0])

sys.exit(FAILED[0])
