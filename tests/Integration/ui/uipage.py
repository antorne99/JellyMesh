"""The configuration page as the checks see it: every selector and button text lives here, and nowhere else."""
import json
import os
import time
import urllib.error
import urllib.request

from playwright.sync_api import sync_playwright

WEB = "/w"
SHOTS = os.path.join(WEB, "shots")
VIEWPORTS = {"1280": {"width": 1280, "height": 900}, "390": {"width": 390, "height": 844}}
PLUGIN = "28c43ea1-fb5a-4b3a-8e6e-30aa3245c653"
PLUGIN_CONFIG = "**/Plugins/" + PLUGIN + "/Configuration"

PAGE = "#JellyMeshConfigPage"
WARNING = "#jmWarning"
MESSAGE = "#jmMessage"
OUTCOME = "#jmOutcome"
DISMISS = "#jmDismiss"
FIELD_ERROR = ".jmFieldError"
STATUS = "#jmStatus"
SERVERS = "#jmServers"
SERVER = ".jmServer"
SYNC_STATUS = "#jmSyncStatus"
SYNC_SERVERS = "#jmSyncServers"
SYNC_NOW = "#jmSyncNow"
MERGE_CASES = "#jmMergeCases"
MERGE_CASE = ".jmMergeCase"
MERGES = "#jmMerges"
MERGE = ".jmMerge"
PEERS = "#jmPeers"
ADD_FORM = "#jmAddServerForm"
ADD_SUBMIT = "#jmAddServer"
SETTINGS_FORM = "#jmSettingsForm"
ADVANCED = "#jmAdvanced"
ADVANCED_TOGGLE = ADVANCED + " > summary"
FOLDER_INFO = "#jmFolderInfo"
LOADING = ".docspinner.mdlSpinnerActive"
CONFIRM = ".dialogContainer .dialog"
CONFIRM_BACKDROP = ".dialogBackdrop"
CONFIRM_TITLE = CONFIRM + " .formDialogHeaderTitle"
CONFIRM_TEXT = CONFIRM + " .text"
CONFIRM_MARKUP = CONFIRM_TEXT + " b, " + CONFIRM_TEXT + " i"
CONFIRM_OK = CONFIRM + " [data-id=ok]"
CONFIRM_CANCEL = CONFIRM + " [data-id=cancel]"
CHECKBOX = "input[type=checkbox]"
NUMBER = "input[type=number]"
SAVE_SETTINGS = "#jmSettingsForm button[type=submit]"
SYNC_INTERVAL = "#jmSyncInterval"
FEDERATED_FOLDER = "#jmFederatedFolder"
METADATA_PROTECTION = "#jmMetadataProtection"
CHOOSE_FOLDER = "#jmChooseFolder"
FOLDER_PATH = ".jmPath"
FOLDER_NOTES = "#jmFolderNotes"
PICKER_ROW = CONFIRM + " .lnkPath"
PICKER_OK = CONFIRM + " button[type=submit]"
PICKER_CLOSE = CONFIRM + " .btnCloseDialog"
ADD_FIELDS = {"name": "#jmServerName", "url": "#jmServerUrl", "user": "#jmServerUser", "password": "#jmServerPassword"}
SETTINGS = {
    SYNC_INTERVAL: "SyncIntervalSeconds",
    "#jmReconciliationTime": "ReconciliationTime",
    FEDERATED_FOLDER: "FederatedFolderPath",
    "#jmRemotePlaybackInfoTimeout": "RemotePlaybackInfoTimeoutSeconds",
    "#jmStreamOpenTimeout": "StreamOpenTimeoutSeconds",
    "#jmPlaybackIdle": "PlaybackIdleSeconds",
    "#jmSessionRetention": "SessionRetentionHours",
    "#jmPolicyCache": "PolicyCacheMinutes",
}
BUTTON = {
    "test": "Test connection",
    "libraries": "Libraries",
    "remove": "Remove",
    "disconnect": "Disconnect",
    "save_libraries": "Save libraries",
    "merge": "Merge selected",
    "undo": "Undo merge",
    "save": "Save",
    "connect": "Connect",
}
READABLE = "h2, h3, h4, p, label, .fieldDescription, " + MERGE_CASE + ", " + WARNING
FAILURE = {
    "status": "#jmStatusFailure",
    "sync": "#jmSyncFailure",
    "peers": "#jmPeersFailure",
    "merges": "#jmMergesFailure",
    "settings": "#jmSettingsFailure",
}
NEW_PEER = "Connect a server"
ALERT = ".jmAlert"
ALERTS_WITH_DATA = [WARNING, "#jmIndexWarning", "#jmMergeWarning"]
SELECT = "select"
SELECT_ARROW = ".selectArrow"
CHECK_MARK = ".checkboxOutline"
CHECK_LABEL = ".checkboxLabel"
USER_LABEL = "User"
LIMIT_LABEL = "Stream limit"
CONTROLS = ", ".join(scope + " " + part for scope in (SERVERS, PEERS) for part in (CHECK_LABEL, NUMBER, SELECT, "label"))
BUTTONS = PAGE + " .emby-button:not(:disabled)"
NOT_UPGRADED = ", ".join(scope + " " + part for scope in (SERVERS, PEERS) for part in (
    CHECKBOX + ":not(.emby-checkbox)", NUMBER + ":not(.emby-input)", SELECT + ":not(.emby-select)"))
INLINE_STYLE = PAGE + " [style]:not(.selectArrowContainer *)"
NESTED_LABEL = PAGE + " label label"


def targets():
    """Reads what up.sh wrote about the servers."""
    with open(os.path.join(WEB, "targets.json"), encoding="utf-8") as stream:
        return json.load(stream)


def api(server, method, path, body=None):
    """Calls a server as its administrator and returns the status and the decoded answer."""
    target = targets()[server]
    headers = {
        "Authorization": 'MediaBrowser Client="JellyMeshUi", Device="ui", DeviceId="ui-check", Version="0.1", Token="%s"' % target["token"],
        "Accept": 'application/json; profile="PascalCase"',
        "Content-Type": "application/json",
    }
    data = None if body is None else json.dumps(body).encode()
    request = urllib.request.Request(target["base"] + path, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            text = response.read().decode()
            return response.status, json.loads(text) if text else None
    except urllib.error.HTTPError as error:
        return error.code, None


def config(server):
    """Returns the plugin configuration of a server."""
    return api(server, "GET", "/Plugins/%s/Configuration" % PLUGIN)[1]


def settings(server, change):
    """Saves the plugin configuration after a change, and returns the status."""
    current = config(server)
    change(current)
    return api(server, "POST", "/Plugins/%s/Configuration" % PLUGIN, current)[0]


def set_theme(server, theme):
    """Chooses the Dashboard theme of the administrator, where the Jellyfin display settings keep it."""
    path = "/DisplayPreferences/usersettings?userId=%s&client=emby" % targets()[server]["user"]
    prefs = api(server, "GET", path)[1]
    prefs["CustomPrefs"]["dashboardTheme"] = theme
    api(server, "POST", path, prefs)


def theme_of(page):
    """Returns the name of the Jellyfin theme the page has loaded."""
    return page.evaluate("""() => {
      const link = [...document.querySelectorAll('link[rel=stylesheet]')].map(l => l.href).reverse()
        .find(href => /themes\\/[^/]+\\/theme/.test(href));
      return link ? link.match(/themes\\/([^/]+)\\//)[1] : null;
    }""")


class Session:
    """One browser that opens the page as the administrator of a server, at a viewport and in a theme."""

    def __enter__(self):
        self._playwright = sync_playwright().start()
        self._browser = self._playwright.chromium.launch()
        os.makedirs(SHOTS, exist_ok=True)
        return self

    def __exit__(self, *_):
        self._browser.close()
        self._playwright.stop()

    def open(self, server, viewport="1280", theme="dark", route=None, wait=True):
        """Opens the page. route gets the Playwright context before the page loads, to fake or delay answers."""
        set_theme(server, theme)
        target = targets()[server]
        credentials = {"Servers": [{
            "Id": target["server"], "Name": server, "AccessToken": target["token"], "UserId": target["user"],
            "ManualAddress": target["base"], "LocalAddress": target["base"], "LastConnectionMode": 2,
            "manualAddressOnly": True, "DateLastAccessed": int(time.time() * 1000)}]}
        context = self._browser.new_context(viewport=VIEWPORTS[viewport])
        context.add_init_script(
            "localStorage.setItem('jellyfin_credentials', " + json.dumps(json.dumps(credentials)) + ");"
            "localStorage.setItem('enableAutoLogin', 'true');")
        if route:
            route(context)

        page = context.new_page()
        page.errors = []
        page.on("pageerror", lambda error: page.errors.append(str(error)))
        page.on("dialog", lambda dialog: (page.errors.append("browser dialog: " + dialog.message), dialog.dismiss()))
        page.goto(f"{target['base']}/web/index.html#/configurationpage?name=JellyMesh")
        if wait:
            wait_loaded(page)
        return page


def wait_loaded(page):
    """Waits until every part of the page has its data."""
    page.wait_for_selector(PAGE + ":not(.hide)", state="attached", timeout=30000)
    page.wait_for_function(
        "s => document.querySelector(s.settings).value !== '' && document.querySelector(s.merges).textContent !== ''",
        arg={"settings": SYNC_INTERVAL, "merges": MERGES}, timeout=30000)
    wait_idle(page)


def wait_idle(page):
    """Waits until the page has finished what it was doing."""
    page.wait_for_selector(LOADING, state="detached", timeout=30000)


def server_row(page, name):
    """Finds the row of a remote server."""
    return page.locator(SERVER).filter(has=page.get_by_role("heading", name=name, exact=True))


def peer_row(page, name):
    """Finds the row of a connected server."""
    return page.locator(PEERS + " > div").filter(has=page.get_by_role("heading", name=name, exact=True))


def merge_case(page, title):
    """Finds the group of doubtful films that holds a title."""
    return page.locator(MERGE_CASE, has_text=title)


def button(scope, name):
    """Finds an action by its role in a part of the page."""
    return scope.get_by_role("button", name=BUTTON[name], exact=True)


def tick(box, on=True):
    """Sets a Jellyfin checkbox as a person does, by clicking its label: the box itself is hidden under the drawn one."""
    if box.is_checked() != on:
        box.locator("xpath=ancestor::label").click()


def reach(page, node):
    """Puts the keyboard on a control with the Tab key, which is what makes the browser show focus."""
    node.focus()
    page.keyboard.press("Tab")
    page.keyboard.press("Shift+Tab")
    page.mouse.move(0, 0)
    page.wait_for_timeout(300)


def close(page):
    """Closes a page whose reads go through routes, some of which may still be on their way."""
    page.context.unroute_all(behavior="ignoreErrors")
    page.context.close()


def message(page):
    """Returns the outcome the page shows for the last action, or an empty text."""
    node = page.locator(MESSAGE)
    return node.inner_text() if node.is_visible() else ""


def wait_message(page, timeout=30000):
    """Waits for the outcome of an action and returns it, once the page has finished reacting to it."""
    page.wait_for_function("s => { const node = document.querySelector(s); return node && node.textContent !== ''; }", arg=MESSAGE, timeout=timeout)
    wait_idle(page)
    return message(page)


def clear_message(page):
    """Dismisses the outcome of the action before."""
    if page.locator(DISMISS).is_visible():
        page.locator(DISMISS).click()


def advanced_open(page):
    """Tells whether the advanced part of the settings is open."""
    return page.locator(ADVANCED).evaluate("node => node.open")


def open_advanced(page):
    """Opens the advanced part of the settings, where the playback times are."""
    if not advanced_open(page):
        page.locator(ADVANCED_TOGGLE).click()


def copy_button(page, path):
    """Finds the copy button of a path to link."""
    return page.locator(FOLDER_PATH, has_text=path).get_by_role("button", name="Copy")


def pasted(page):
    """Returns what the browser clipboard holds, read by pasting it into the first field of the add form."""
    field = page.locator(ADD_FIELDS["name"])
    field.fill("")
    field.focus()
    page.keyboard.press("Control+V")
    value = field.input_value()
    field.fill("")
    return value


def open_picker(page):
    """Opens the folder browser of Jellyfin and waits for its list of server folders."""
    page.locator(CHOOSE_FOLDER).click()
    page.wait_for_selector(PICKER_ROW, state="visible", timeout=15000)


def choose_folder(page, path):
    """Walks into a folder in the folder browser, accepts it and waits until the dialog has gone."""
    page.locator(PICKER_ROW + '[data-path="%s"]' % path).click()
    page.wait_for_selector(PICKER_ROW + ':has-text("...")', state="visible", timeout=15000)
    page.locator(PICKER_OK).click()
    page.wait_for_selector(CONFIRM, state="detached", timeout=15000)
    page.wait_for_selector(CONFIRM_BACKDROP, state="detached", timeout=15000)
    wait_idle(page)


def has_focus(node):
    """Tells whether the keyboard is on a part of the page, or inside it."""
    return node.evaluate("node => node.contains(document.activeElement)")


def mark(node):
    """Tags an element, to tell later whether it is still the same one."""
    node.evaluate("node => { node.dataset.jmKept = 'yes'; }")


def is_marked(node):
    """Tells whether an element is the one tagged, rather than a rebuilt copy."""
    return node.evaluate("node => node.dataset.jmKept === 'yes'")


def in_view(page, selector):
    """Returns whether an element is whole inside the viewport, with nothing of the Dashboard on top of it."""
    return page.locator(selector).evaluate("""node => {
      const box = node.getBoundingClientRect();
      const top = document.elementFromPoint(box.left + box.width / 2, box.top + box.height / 2);
      return box.height > 0 && box.top >= 0 && box.bottom <= innerHeight && box.left >= 0 && box.right <= innerWidth
        && top !== null && node.contains(top);
    }""")


def field_error(page, selector):
    """Returns the error shown under a field, or an empty text."""
    return page.locator(selector).evaluate("""input => {
      const note = document.getElementById(input.getAttribute('aria-describedby'));
      return note && input.getAttribute('aria-invalid') === 'true' ? note.textContent : '';
    }""")


def focused(page):
    """Returns the id of the focused element."""
    return page.evaluate("document.activeElement.id")


def wait_confirm(page):
    """Waits for the confirmation dialog of Jellyfin to take the focus, and returns its title and its text as shown."""
    page.wait_for_selector(CONFIRM + " :focus", state="attached", timeout=15000)
    return page.locator(CONFIRM_TITLE).inner_text(), page.locator(CONFIRM_TEXT).inner_text()


def confirm_starts_on_cancel(page):
    """Tells whether the confirmation dialog has its focus on Cancel."""
    return page.evaluate("s => document.activeElement === document.querySelector(s)", CONFIRM_CANCEL)


def answer_confirm(page, accept):
    """Answers the confirmation dialog and waits until it has gone and the page has finished reacting."""
    page.wait_for_selector(CONFIRM_OK, state="visible", timeout=15000)
    page.locator(CONFIRM_OK if accept else CONFIRM_CANCEL).click()
    page.wait_for_selector(CONFIRM, state="detached", timeout=15000)
    wait_idle(page)


def shot(page, name, full_page=True):
    """Captures the whole page, or only what the screen shows."""
    page.screenshot(path=os.path.join(SHOTS, name + ".png"), full_page=full_page)
