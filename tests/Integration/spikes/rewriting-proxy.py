#!/usr/bin/env python3
"""Spike of what the JellyMesh local server must do at playback time.

Stands between a client and a stock Jellyfin remote. The client only ever sees
this proxy. Remote token, address, DeviceId, PlaySessionId and file paths stay
here. Every byte sent to a client is also appended to a capture file, so a leak
check can grep what really went out rather than what was intended.

Usage: rewriting-proxy.py <port> <remote base url> <remote token> <capture file>

Client side, all requiring the local token (LOCAL_TOKEN below) as X-Emby-Token
or as ApiKey in the query, like a locally authenticated user:
  POST /open                    {"item":id,"profile":{...},"audio":n,"subtitle":n,"start":ticks}
                                remote PlaybackInfo with the forwarded profile;
                                answers a sanitized copy pointing at this proxy
  GET  /p/<sid>/master.m3u8     rewritten master
  GET  /p/<sid>/main.m3u8       rewritten variant
  GET  /p/<sid>/hls1/...        segments
  GET  /p/<sid>/<msid>/Subtitles/... HLS subtitle playlists and vtt
  GET  /p/<sid>/stream          direct stream, Range forwarded
  GET  /p/<sid>/sub/<index>.vtt external subtitle
  POST /p/<sid>/stop            closes the remote session
Not authenticated, standing for a .strm target (capability in the path):
  GET  /strm/<sid>              same as /p/<sid>/stream
"""

import datetime
import http.client
import http.server
import json
import re
import secrets
import sys
import threading
import urllib.parse

PORT, REMOTE, TOKEN, CAPTURE = int(sys.argv[1]), sys.argv[2].rstrip("/"), sys.argv[3], sys.argv[4]
REMOTE_URL = urllib.parse.urlsplit(REMOTE)
LOCAL_TOKEN = "local-user-token"
DEVICE_ID = "jm-e3-p4"
# The remote hashes User-Agent into the transcode output path, so one playback
# session must use a single value. Never forward the client's.
UPSTREAM_UA = "JellyMesh-Spike/0.1"
AUTH = (f'MediaBrowser Client="JellyMeshSpike", Device="p4", DeviceId="{DEVICE_ID}", '
        f'Version="0.1", Token="{TOKEN}"')
ACCEPT = 'application/json; profile="PascalCase"'
# Query params a client may send back: segment timing only.
CLIENT_PARAMS = ("runtimeTicks", "actualSegmentLengthTicks", "StartPositionTicks",
                 "EndPositionTicks", "SegmentLength", "CopyTimestamps", "AddVttTimeMap")
# Response headers worth relaying. Everything else is dropped.
RELAY_HEADERS = ("content-type", "content-length", "content-range", "accept-ranges")

SESSIONS = {}
capture_lock = threading.Lock()


def capture(kind, data):
    with capture_lock, open(CAPTURE, "ab") as out:
        out.write(f"\n--- {kind}\n".encode())
        out.write(data)


def log(message):
    print(datetime.datetime.now().strftime("%H:%M:%S.%f")[:-3], message, flush=True)


def upstream(method, path, headers=None, body=None):
    """One request to the remote with header auth. Redirects are never followed."""
    conn = http.client.HTTPConnection(REMOTE_URL.hostname, REMOTE_URL.port, timeout=120)
    send = {"Authorization": AUTH, "User-Agent": UPSTREAM_UA, "Accept": ACCEPT}
    send.update(headers or {})
    conn.request(method, path, body=body, headers=send)
    return conn, conn.getresponse()


def strip_token(path):
    return re.sub(r"([?&])ApiKey=[^&\"\s]*&?", r"\1", path).rstrip("?&")


class Session:
    def __init__(self, item, info):
        self.sid = secrets.token_urlsafe(16)
        self.item = item
        self.ps = info["PlaySessionId"]
        self.source = info["MediaSources"][0]
        url = self.source.get("TranscodingUrl")
        self.master = strip_token(url) if url else None
        # Playlists are relative to the master's folder.
        self.base = url.split("?")[0].rsplit("/", 1)[0] if url else f"/videos/{item}"
        self.query = {}

    def rewrite_uri(self, uri, client_key, folder):
        """Maps one playlist URI to a proxy URL, keeping its upstream form server-side."""
        target = urllib.parse.urlsplit(uri)
        if target.scheme or target.netloc or target.path.startswith("/"):
            raise ValueError("unexpected absolute URI in remote playlist")
        target = target._replace(path=folder + target.path)
        params = urllib.parse.parse_qsl(target.query, keep_blank_values=True)
        # The query is the same for all segments apart from timing, so keep one
        # copy per path shape. Timing params travel through the client.
        upstream_query = [(k, v) for k, v in params if k != "ApiKey" and k not in CLIENT_PARAMS]
        self.query[self.shape(target.path)] = upstream_query
        client = [(k, v) for k, v in params if k in CLIENT_PARAMS]
        if client_key:
            client.append(("ApiKey", client_key))
        query = urllib.parse.urlencode(client)
        return f"/p/{self.sid}/{target.path}" + (f"?{query}" if query else "")

    @staticmethod
    def shape(path):
        return re.sub(r"(/|^)-?\d+\.(ts|mp4|m4s)$", r"\1N.\2", path)

    def rewrite_playlist(self, text, client_key, folder=""):
        lines = []
        for line in text.splitlines():
            if line.startswith("#"):
                line = re.sub(r'URI="([^"]+)"',
                              lambda m: f'URI="{self.rewrite_uri(m.group(1), client_key, folder)}"', line)
            elif line.strip():
                line = self.rewrite_uri(line, client_key, folder)
            lines.append(line)
        result = "\n".join(lines) + "\n"
        self.assert_clean(result.encode())
        return result

    def assert_clean(self, data):
        for secret in (TOKEN, REMOTE_URL.netloc, self.ps, DEVICE_ID):
            if secret.encode() in data:
                raise ValueError("refusing to send a remote value to the client")

    def upstream_path(self, rest, client_query):
        kept = [(k, v) for k, v in client_query if k in CLIENT_PARAMS]
        query = self.query.get(self.shape(rest))
        if query is None:
            return None
        return f"{self.base}/{rest}?" + urllib.parse.urlencode(query + kept)

    def client_view(self, key):
        """What the local PlaybackInfo would hand a client."""
        view = json.loads(json.dumps(self.source))
        view.pop("Path", None)
        view["Id"] = self.sid
        view["ETag"] = None
        suffix = f"?ApiKey={key}" if key else ""
        if view.get("TranscodingUrl"):
            view["TranscodingUrl"] = f"/p/{self.sid}/master.m3u8{suffix}"
        for stream in view.get("MediaStreams", []):
            stream.pop("Path", None)
            if stream.get("DeliveryUrl"):
                stream["DeliveryUrl"] = f"/p/{self.sid}/sub/{stream['Index']}.vtt{suffix}"
        return view


class Handler(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    server_version = "local"
    sys_version = ""

    def log_message(self, *args):
        pass

    def answer(self, status, body=b"", content_type="application/json"):
        headers = f"{status} {content_type} {len(body)}\n".encode()
        capture(f"{self.command} {self.path}", headers + body)
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        if self.command != "HEAD":
            self.wfile.write(body)

    def local_key(self):
        """Local token from query or header. Empty when it came in the header."""
        query = dict(urllib.parse.parse_qsl(urllib.parse.urlsplit(self.path).query))
        if query.get("ApiKey") == LOCAL_TOKEN:
            return LOCAL_TOKEN
        if self.headers.get("X-Emby-Token") == LOCAL_TOKEN:
            return ""
        return None

    def do_POST(self):
        path = urllib.parse.urlsplit(self.path).path
        length = int(self.headers.get("Content-Length") or 0)
        body = self.rfile.read(length) if length else b""
        key = self.local_key()
        if key is None:
            return self.answer(401)
        if path == "/open":
            return self.open(json.loads(body), key)
        match = re.fullmatch(r"/p/([^/]+)/stop", path)
        if match and match.group(1) in SESSIONS:
            session = SESSIONS.pop(match.group(1))
            # PlaySessionId alone: with ItemId and no position the remote marks
            # the title played for its user.
            payload = {"PlaySessionId": session.ps}
            conn, response = upstream("POST", "/Sessions/Playing/Stopped",
                                      {"Content-Type": "application/json"}, json.dumps(payload))
            response.read()
            conn.close()
            log(f"stop upstream={response.status}")
            return self.answer(204)
        return self.answer(404)

    def open(self, request, key):
        payload = {"UserId": request["user"], "DeviceProfile": request["profile"],
                   "MediaSourceId": request["item"]}
        for field, name in (("audio", "AudioStreamIndex"), ("subtitle", "SubtitleStreamIndex"),
                            ("start", "StartTimeTicks")):
            if field in request:
                payload[name] = request[field]
        conn, response = upstream("POST", f"/Items/{request['item']}/PlaybackInfo",
                                  {"Content-Type": "application/json"}, json.dumps(payload))
        info = json.loads(response.read())
        conn.close()
        session = Session(request["item"], info)
        SESSIONS[session.sid] = session
        log(f"open sid={session.sid[:6]} ps={session.ps[:8]} "
            f"directplay={session.source.get('SupportsDirectPlay')} hls={bool(session.master)}")
        body = json.dumps({"Sid": session.sid, "MediaSource": session.client_view(key)}).encode()
        session.assert_clean(body)
        return self.answer(200, body)

    def do_HEAD(self):
        self.do_GET()

    def do_GET(self):
        url = urllib.parse.urlsplit(self.path)
        params = urllib.parse.parse_qsl(url.query, keep_blank_values=True)
        match = re.fullmatch(r"/strm/([^/]+)", url.path)
        if match:
            # A .strm target has no Jellyfin auth: the capability is the path.
            session = SESSIONS.get(match.group(1))
            return self.direct(session) if session else self.answer(404)
        key = self.local_key()
        if key is None:
            return self.answer(401)
        match = re.fullmatch(r"/p/([^/]+)/(.+)", url.path)
        session = SESSIONS.get(match.group(1)) if match else None
        if not session:
            return self.answer(404)
        rest = match.group(2)
        if rest == "master.m3u8":
            return self.playlist(session, strip_token(session.master), key, "")
        if rest == "stream":
            return self.direct(session)
        sub = re.fullmatch(r"sub/(\d+)\.vtt", rest)
        if sub:
            guid = str(session.item)
            path = f"/Videos/{guid}/{session.source['Id']}/Subtitles/{sub.group(1)}/0/Stream.vtt"
            return self.relay(path, session)
        target = session.upstream_path(rest, params)
        if target is None:
            return self.answer(404)
        if rest.endswith(".m3u8"):
            return self.playlist(session, target, key, rest.rpartition("/")[0] + "/" if "/" in rest else "")
        return self.relay(target, session)

    def direct(self, session):
        source = session.source["Id"]
        path = f"/Videos/{session.item}/stream?static=true&MediaSourceId={source}"
        return self.relay(path, session)

    def playlist(self, session, path, key, folder):
        conn, response = upstream("GET", path)
        data = response.read()
        conn.close()
        log(f"playlist {response.status} {path.split('?')[0]}")
        if response.status != 200:
            # Remote error bodies are never relayed.
            return self.answer(502)
        return self.answer(200, session.rewrite_playlist(data.decode(), key, folder).encode(),
                           "application/vnd.apple.mpegurl")

    def relay(self, path, session):
        headers = {}
        if self.headers.get("Range"):
            headers["Range"] = self.headers["Range"]
        conn, response = upstream(self.command, path, headers)
        log(f"relay {response.status} range={headers.get('Range')} "
            f"{path.split('?')[0]} ua={self.headers.get('User-Agent')}")
        if response.status >= 300 and response.status != 416:
            conn.close()
            return self.answer(502)
        out = {k.lower(): v for k, v in response.getheaders() if k.lower() in RELAY_HEADERS}
        self.send_response(response.status)
        for name, value in out.items():
            self.send_header(name, value)
        if "content-length" not in out:
            self.send_header("Connection", "close")
            self.close_connection = True
        self.end_headers()
        capture(f"{self.command} {self.path}", f"{response.status} {out}\n".encode())
        if self.command != "HEAD":
            try:
                while chunk := response.read(65536):
                    capture("bytes", chunk)
                    self.wfile.write(chunk)
            except (BrokenPipeError, ConnectionResetError):
                log("client went away")
        conn.close()


class Server(http.server.ThreadingHTTPServer):
    daemon_threads = True


if __name__ == "__main__":
    log(f"listening on {PORT}")
    Server(("127.0.0.1", PORT), Handler).serve_forever()
