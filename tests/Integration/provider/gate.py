#!/usr/bin/env python3
"""The network between two servers, for what a real network cannot be made to do on demand.
Usage: gate.py <listen port> <target port> <state file> <log file>
State file, read per request:
  up            pass
  down          close the connection at once, as an offline server
  hang          accept and never answer, as a server that vanished without closing
  slow:<ms>     wait that long before answering each request
  notranscode   500 on HLS playlists, as a remote that cannot transcode
One line per request is logged, without any token: state, method, path without the query, status.
"""
import http.client, http.server, socketserver, sys, threading, time

LISTEN, TARGET, STATE, LOG = int(sys.argv[1]), int(sys.argv[2]), sys.argv[3], sys.argv[4]
lock = threading.Lock()


def mode():
    try:
        return open(STATE).read().strip() or "up"
    except OSError:
        return "up"


def record(m, method, path, status):
    with lock, open(LOG, "a") as f:
        f.write(f"{time.strftime('%H:%M:%S')} {m} {method} {path} {status}\n")


class H(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *a):
        pass

    def go(self):
        m = mode()
        path = self.path.split("?")[0]
        if m == "down":
            record(m, self.command, path, "closed")
            self.close_connection = True
            self.connection.close()
            return
        if m == "hang":
            record(m, self.command, path, "hung")
            threading.Event().wait()
            return
        if m.startswith("slow:"):
            time.sleep(int(m[5:]) / 1000)
        if m == "notranscode" and path.endswith(".m3u8"):
            record(m, self.command, path, 500)
            self.send_response(500)
            self.send_header("Content-Length", "0")
            self.end_headers()
            return
        n = int(self.headers.get("Content-Length") or 0)
        body = self.rfile.read(n) if n else None
        c = http.client.HTTPConnection("127.0.0.1", TARGET, timeout=300)
        h = {k: v for k, v in self.headers.items() if k.lower() not in ("host", "connection")}
        c.request(self.command, self.path, body=body, headers=h)
        r = c.getresponse()
        record(m, self.command, path, r.status)
        self.send_response(r.status)
        has_length = False
        for k, v in r.getheaders():
            if k.lower() in ("transfer-encoding", "connection"):
                continue
            has_length = has_length or k.lower() == "content-length"
            self.send_header(k, v)
        if not has_length:
            self.send_header("Connection", "close")
            self.close_connection = True
        self.end_headers()
        try:
            if self.command != "HEAD":
                while True:
                    d = r.read(65536)
                    if not d:
                        break
                    self.wfile.write(d)
        except OSError:
            self.close_connection = True
        c.close()

    do_GET = do_POST = do_PUT = do_DELETE = do_HEAD = go


class S(socketserver.ThreadingMixIn, http.server.HTTPServer):
    daemon_threads = True
    allow_reuse_address = True


S(("127.0.0.1", LISTEN), H).serve_forever()
