#!/usr/bin/env python3
"""A remote that looks like a Jellyfin server until it is told not to.

usage: hostile.py <port> <third-host port> <work folder>

What it does comes from <work>/mode, read at each request, so a test changes it without a restart. The third-host port
stands for a machine the plugin must never reach: anything arriving there is a failure, and it is logged with the headers
so that a stolen token would show. Every request and how many body bytes went out are in <work>/hostile.log, and the
highest number of slow PlaybackInfo calls seen at once in <work>/peak.
"""
import json
import os
import re
import socketserver
import sys
import threading
import time
import urllib.parse
from email.utils import formatdate

PORT, THIRD_PORT, WORK = int(sys.argv[1]), int(sys.argv[2]), sys.argv[3]
THIRD = f"host.docker.internal:{THIRD_PORT}"
TOKEN = "hostile-remote-token-sentinel"
CHUNK = b"x" * (1024 * 1024)
FILE = bytes(range(256)) * 4096  # 1 MiB
MASTER = '#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=800000,CODECS="avc1.640028,mp4a.40.2"\nmain.m3u8?PlaySessionId=hostile-play&ApiKey=' + TOKEN + "\n"
VARIANT = "#EXTM3U\n#EXTINF:3.0,\nhls1/main/0.ts?PlaySessionId=hostile-play&runtimeTicks=0&actualSegmentLengthTicks=30000000\n#EXT-X-ENDLIST\n"

NO_FILE = {"pi-huge", "pi-redirect", "pi-error", "pi-403", "pi-401", "pi-null", "pi-abs", "pi-rooted", "pi-long", "pi-subtitle",
           "master-huge", "master-abs", "master-many", "variant-abs"}

lock = threading.Lock()
running = 0
peak = 0


def mode():
    try:
        with open(os.path.join(WORK, "mode")) as handle:
            return handle.read().strip()
    except OSError:
        return "normal"


def log(line):
    with lock, open(os.path.join(WORK, "hostile.log"), "a") as handle:
        handle.write(line + "\n")


def films():
    ids = ["a1" * 16, "a2" * 16, "a3" * 16]
    if os.path.exists(os.path.join(WORK, "fourth")):
        ids.append("a4" * 16)
    return [film(i, number) for number, i in enumerate(ids)]


def film(item, number):
    return {
        "Id": item,
        "Name": f"Ostile {number}",
        "ProductionYear": 2001 + number,
        "ProviderIds": {"Tmdb": str(990000 + number)},
        "DateCreated": "2026-01-01T00:00:00Z",
        "Etag": f"etag-{number}",
        "RunTimeTicks": 6000000000,
        "ImageTags": {"Primary": "tag"},
        "MediaSources": [{
            "Id": item,
            "Name": "main",
            "Container": "mp4",
            "Size": len(FILE),
            "Protocol": "File",
            "MediaStreams": [{"Index": 0, "Type": "Video", "Codec": "h264", "Width": 1920, "Height": 1080, "BitRate": 4000000}],
        }],
    }


def playback_info(item, m):
    source = {
        "Id": item,
        "Name": "main",
        "Container": "mp4",
        "Size": len(FILE),
        "Protocol": "File",
        "Path": "/secret/remote/path.mp4",
        "SupportsTranscoding": True,
        "SupportsDirectStream": False,
        "SupportsDirectPlay": False,
        "MediaStreams": [{"Index": 0, "Type": "Video", "Codec": "h264"}],
    }
    master = f"/videos/{item}/master.m3u8?PlaySessionId=hostile-play&ApiKey={TOKEN}"
    source.update(TranscodingUrl=master, TranscodingSubProtocol="hls", TranscodingContainer="ts")
    if m in ("direct", "stream-stall", "stream-big", "stream-html"):
        source.update(SupportsDirectPlay=True, TranscodingUrl=None, TranscodingSubProtocol=None)
    elif m == "pi-abs":
        source["TranscodingUrl"] = f"http://{THIRD}/stolen/master.m3u8?ApiKey={TOKEN}"
    elif m == "pi-rooted":
        source["TranscodingUrl"] = f"//{THIRD}/stolen/master.m3u8"
    elif m == "pi-long":
        source["TranscodingUrl"] = "/videos/" + "x" * 100000 + "/master.m3u8"
    elif m == "pi-subtitle":
        source["MediaStreams"].append({"Index": 1, "Type": "Subtitle", "DeliveryUrl": f"http://{THIRD}/stolen.vtt"})
    return {"MediaSources": [source], "PlaySessionId": "hostile-play"}


class Handler(socketserver.StreamRequestHandler):
    def out(self, status, body=b"", kind="application/json", extra=""):
        if isinstance(body, str):
            body = body.encode()
        head = f"HTTP/1.1 {status} X\r\nConnection: close\r\nContent-Type: {kind}\r\nContent-Length: {len(body)}\r\nDate: {formatdate(usegmt=True)}\r\n{extra}\r\n"
        self.wfile.write(head.encode() + body)

    def json(self, value):
        self.out(200, json.dumps(value))

    def endless(self, kind="application/json"):
        self.wfile.write(f"HTTP/1.1 200 OK\r\nConnection: close\r\nContent-Type: {kind}\r\nTransfer-Encoding: chunked\r\n\r\n".encode())
        sent = 0
        try:
            while sent < 2 * 1024 ** 3:
                self.wfile.write(b"100000\r\n" + CHUNK + b"\r\n")
                sent += len(CHUNK)
        finally:
            log(f"BYTES {sent}")

    def stall(self, kind, declared=1000):
        self.wfile.write(f"HTTP/1.1 200 OK\r\nContent-Type: {kind}\r\nContent-Length: {declared}\r\n\r\nstart".encode())
        self.wfile.flush()
        time.sleep(600)

    def error_body(self, status):
        # A declared body far over any limit that never comes: reading it would either fail late or hang.
        self.wfile.write(f"HTTP/1.1 {status} X\r\nContent-Type: text/plain\r\nContent-Length: 2000000000\r\n\r\nhttp://{THIRD}/in-an-error-body {TOKEN}".encode())
        self.wfile.flush()
        time.sleep(120)

    def redirect(self):
        self.out(302, extra=f"Location: http://{THIRD}/stolen\r\n")

    def handle(self):
        try:
            self.answer()
            self.wait_for_close()
        except (BrokenPipeError, ConnectionResetError):
            log("GONE")

    def wait_for_close(self):
        # Lets the plugin close first. When the host closes first, Docker Desktop at times drops a later SYN from the
        # container that reuses the port, and that connect hangs for more than 10 s.
        self.wfile.flush()
        self.request.settimeout(10)
        try:
            while self.request.recv(65536):
                pass
        except OSError:
            pass

    def slow(self):
        global running, peak
        with lock:
            running += 1
            peak = max(peak, running)
            with open(os.path.join(WORK, "peak"), "w") as handle:
                handle.write(str(peak))
        time.sleep(2)
        with lock:
            running -= 1

    def answer(self):
        request = self.rfile.readline().decode("latin-1").strip()
        if not request:
            return
        method, target, _ = request.split(" ", 2)
        headers = {}
        while True:
            line = self.rfile.readline().decode("latin-1").strip()
            if not line:
                break
            name, _, value = line.partition(":")
            headers[name.lower()] = value.strip()
        length = int(headers.get("content-length") or 0)
        if length:
            self.rfile.read(length)

        path = urllib.parse.urlsplit(target).path
        m = mode()
        log(f"{method} {target} token={TOKEN in headers.get('authorization', '')}")

        if path == "/Users/AuthenticateByName":
            if m == "redirect-all":
                return self.redirect()
            return self.json({"AccessToken": TOKEN, "User": {"Id": "b" * 32, "Policy": {"IsAdministrator": False}}})
        if m == "redirect-all":
            return self.redirect()
        if path == "/JellyMesh/Capabilities":
            return self.out(404)
        if path == "/System/Info/Public":
            return self.json({"Version": "12.4.0"})
        if path == "/UserViews":
            return self.json({"Items": [{"Id": "c" * 32, "Name": "Film", "CollectionType": "movies"}], "TotalRecordCount": 1})
        if path == "/Items":
            return self.items(m)
        if re.fullmatch(r"/Items/[^/]+/Images/.+", path):
            return self.image(m)
        if path == "/Users/Me":
            return self.json({"Policy": {"EnableVideoPlaybackTranscoding": True, "EnableAudioPlaybackTranscoding": True, "EnablePlaybackRemuxing": True}})
        if path == "/System/Configuration/encoding":
            return self.json({"HardwareAccelerationType": "none"})
        if path == "/Videos/ActiveEncodings":
            return self.out(204)

        info = re.fullmatch(r"/Items/([^/]+)/PlaybackInfo", path)
        if info:
            return self.playback(info.group(1), m)
        if path.endswith("/stream"):
            return self.stream(m)
        if path.endswith("/master.m3u8"):
            return self.master(m)
        if path.endswith("/main.m3u8"):
            return self.variant(m)
        if path.endswith(".ts"):
            return self.out(200, FILE[:1024], "video/mp2t")
        self.out(404)

    def items(self, m):
        if m == "catalog-huge":
            return self.endless()
        if m == "catalog-stall":
            return self.stall("application/json")
        if m == "catalog-error":
            return self.error_body(500)
        if m == "catalog-redirect":
            return self.redirect()
        if m == "catalog-null":
            return self.json({"Items": [None], "TotalRecordCount": 1})
        all_films = films()
        return self.json({"Items": all_films, "TotalRecordCount": len(all_films)})

    def image(self, m):
        if m == "image-huge":
            return self.endless("image/jpeg")
        if m == "image-stall":
            return self.stall("image/jpeg")
        if m == "image-redirect":
            return self.redirect()
        self.out(200, b"\xff\xd8\xff\xe0" + b"0" * 1000, "image/jpeg")

    def playback(self, item, m):
        if m == "pi-huge":
            return self.endless()
        if m == "pi-stall":
            return self.stall("application/json")
        if m == "pi-redirect":
            return self.redirect()
        if m == "pi-error":
            return self.error_body(500)
        if m == "pi-403":
            return self.out(403)
        if m == "pi-401":
            return self.out(401)
        if m == "pi-null":
            return self.json({"MediaSources": [None], "PlaySessionId": "hostile-play"})
        if m == "pi-slow":
            self.slow()
        self.json(playback_info(item, m))

    def stream(self, m):
        # In these the file cannot be read either, or the chain would end well, in the local transcode.
        if m in NO_FILE:
            return self.out(500)
        if m == "stream-stall":
            return self.stall("video/mp4", len(FILE))
        if m == "stream-big":
            self.wfile.write(b"HTTP/1.1 200 OK\r\nConnection: close\r\nContent-Type: video/mp4\r\nContent-Length: 3221225472\r\n\r\n")
            sent = 0
            try:
                while sent < 3 * 1024 ** 3:
                    self.wfile.write(FILE)
                    sent += len(FILE)
            finally:
                log(f"BYTES {sent}")
            return
        if m == "stream-html":
            return self.out(200, b"<html><script>alert(1)</script></html>", "text/html")
        self.out(200, FILE, "video/mp4", "Server: remote-name\r\n")

    def master(self, m):
        if m == "master-huge":
            return self.endless("application/vnd.apple.mpegurl")
        if m == "master-stall":
            return self.stall("application/vnd.apple.mpegurl")
        if m == "master-abs":
            return self.out(200, f'#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1\nhttp://{THIRD}/stolen.m3u8\n', "application/vnd.apple.mpegurl")
        if m == "master-tag":
            return self.out(200, MASTER + f"#EXT-X-MAP:URI=http://{THIRD}/stolen.mp4\n", "application/vnd.apple.mpegurl")
        if m == "master-many":
            return self.out(200, "#EXT-X-STREAM-INF:BANDWIDTH=1\n" * 200000, "application/vnd.apple.mpegurl")
        self.out(200, MASTER, "application/vnd.apple.mpegurl")

    def variant(self, m):
        if m == "variant-abs":
            return self.out(200, f"#EXTM3U\n#EXTINF:3.0,\nhttp://{THIRD}/stolen.ts\n", "application/vnd.apple.mpegurl")
        self.out(200, VARIANT, "application/vnd.apple.mpegurl")


class Third(socketserver.StreamRequestHandler):
    def handle(self):
        lines = []
        while True:
            line = self.rfile.readline().decode("latin-1").strip()
            if not line:
                break
            lines.append(line)
        with lock, open(os.path.join(WORK, "third.log"), "a") as handle:
            handle.write(" | ".join(lines) + "\n")
        self.wfile.write(b"HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")


class Server(socketserver.ThreadingTCPServer):
    allow_reuse_address = True
    daemon_threads = True
    request_queue_size = 256


threading.Thread(target=Server(("0.0.0.0", THIRD_PORT), Third).serve_forever, daemon=True).start()
Server(("0.0.0.0", PORT), Handler).serve_forever()
