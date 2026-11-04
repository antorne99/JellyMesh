#!/usr/bin/env python3
"""HTTP gate in front of a remote, for failures a real remote cannot be made to show.
Usage: httpgate.py <listen port> <target port> <state file> <log file>
State file, read per request:
  up        pass
  down      close the connection at once
  hang      accept, never answer
  noseg     500 on HLS segments, with a sentinel body, as VAAPI without a device does
  noplaylist 500 on master.m3u8
  badcodec  rewrites CODECS in the master to hvc1, as a remote with no transcode permission does
  hangseg   never answers HLS segments
  redirect  302 to another host on HLS segments
  nostream  500 on /stream (the file)
  late0     passes the probe, then 500 from the second master request on
Every request is logged: method, path, User-Agent, and whether Authorization carried a token.
"""
import http.client, http.server, re, socketserver, sys, threading, time
LISTEN, TARGET, STATE, LOG = int(sys.argv[1]), int(sys.argv[2]), sys.argv[3], sys.argv[4]
SENTINEL = b"remote-error-body-sentinel-host.tailnet.ts.net"
lock = threading.Lock()
masters = 0


def mode():
    try:
        return open(STATE).read().strip() or "up"
    except OSError:
        return "up"


class H(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *a):
        pass

    def go(self):
        m = mode()
        tok = re.search(r'Token="([^"]+)"', self.headers.get("Authorization") or "")
        line = f"{time.strftime('%H:%M:%S')} {m} {self.command} {self.path} ua={self.headers.get('User-Agent')} auth={tok[1] if tok else None} range={self.headers.get('Range')}\n"
        with lock, open(LOG, "a") as f, open(LOG + ".all", "a") as a:
            f.write(line)
            a.write(line)
        path = self.path.split("?")[0]
        seg = "/hls1/" in path
        if m == "late0" and path.endswith("master.m3u8"):
            global masters
            masters += 1
        if m == "late0" and masters >= 2:
            self.send_response(500)
            self.send_header("Content-Length", "0")
            self.end_headers()
            return
        if m == "down":
            self.close_connection = True
            self.connection.close()
            return
        if m == "hang" or (m == "hangseg" and seg):
            threading.Event().wait()
            return
        if (m == "noseg" and seg) or (m == "noplaylist" and path.endswith("master.m3u8")) or (m == "nostream" and path.endswith("/stream")):
            self.send_response(500)
            self.send_header("Content-Length", str(len(SENTINEL)))
            self.end_headers()
            self.wfile.write(SENTINEL)
            return
        if m == "redirect" and seg:
            self.send_response(302)
            self.send_header("Location", "http://evil.example.invalid/x")
            self.send_header("Content-Length", "0")
            self.end_headers()
            return
        n = int(self.headers.get("Content-Length") or 0)
        body = self.rfile.read(n) if n else None
        c = http.client.HTTPConnection("127.0.0.1", TARGET, timeout=300)
        h = {k: v for k, v in self.headers.items() if k.lower() not in ("host", "connection")}
        c.request(self.command, self.path, body=body, headers=h)
        r = c.getresponse()
        data = None
        if m == "badcodec" and path.endswith("master.m3u8"):
            data = re.sub(rb'CODECS="[^"]*"', b'CODECS="hvc1.2.4.L153.B0,mp4a.40.2"', r.read())
        self.send_response(r.status)
        sent_len = False
        for k, v in r.getheaders():
            if k.lower() in ("transfer-encoding", "connection"):
                continue
            if k.lower() == "content-length":
                sent_len = True
                if data is not None:
                    v = str(len(data))
            self.send_header(k, v)
        if not sent_len:
            self.send_header("Connection", "close")
            self.close_connection = True
        self.end_headers()
        if self.command == "HEAD":
            c.close()
            return
        try:
            if data is not None:
                self.wfile.write(data)
            else:
                while True:
                    d = r.read(65536)
                    if not d:
                        break
                    self.wfile.write(d)
        except OSError:
            pass
        c.close()

    do_GET = do_POST = do_DELETE = do_HEAD = go


class S(socketserver.ThreadingMixIn, http.server.HTTPServer):
    daemon_threads = True
    allow_reuse_address = True


S(("127.0.0.1", LISTEN), H).serve_forever()
