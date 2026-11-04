#!/usr/bin/env python3
"""Logs every request a .strm target receives, so probing by Jellyfin is visible.

Usage: stub_server.py <port> <clip to serve> <log file>
Paths containing "missing" answer 404, anything else serves the clip.
"""

import datetime
import http.server
import sys

PORT, CLIP, LOG = int(sys.argv[1]), sys.argv[2], sys.argv[3]


class Handler(http.server.BaseHTTPRequestHandler):
    def handle_request(self, send_body):
        with open(LOG, "a", encoding="utf-8") as log:
            stamp = datetime.datetime.now().strftime("%H:%M:%S")
            log.write(f"{stamp} {self.command} {self.path} "
                      f"range={self.headers.get('Range')} ua={self.headers.get('User-Agent')}\n")
        if "missing" in self.path:
            self.send_error(404)
            return
        with open(CLIP, "rb") as clip:
            body = clip.read()
        self.send_response(200)
        self.send_header("Content-Type", "video/x-matroska")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        if send_body:
            try:
                self.wfile.write(body)
            except (BrokenPipeError, ConnectionResetError):
                pass

    def do_GET(self):
        self.handle_request(True)

    def do_HEAD(self):
        self.handle_request(False)

    def log_message(self, *args):
        pass


http.server.ThreadingHTTPServer(("127.0.0.1", PORT), Handler).serve_forever()
