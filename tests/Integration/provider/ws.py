#!/usr/bin/env python3
"""Listens on the websocket of a Jellyfin device and prints each DisplayMessage it gets.
Usage: ws.py <port> <token> <device id> <seconds>
The device has to be connected for a DisplayMessage to reach it, as on a real client.
"""
import base64, json, os, socket, struct, sys, time

port, token, device, seconds = int(sys.argv[1]), sys.argv[2], sys.argv[3], float(sys.argv[4])
s = socket.create_connection(("127.0.0.1", port))
key = base64.b64encode(os.urandom(16)).decode()
s.sendall(
    (
        f"GET /socket?ApiKey={token}&deviceId={device} HTTP/1.1\r\nHost: 127.0.0.1\r\nUpgrade: websocket\r\n"
        f"Connection: Upgrade\r\nSec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n"
    ).encode()
)
buffer = b""
while b"\r\n\r\n" not in buffer:
    buffer += s.recv(4096)
head, buffer = buffer.split(b"\r\n\r\n", 1)
print("handshake:", head.split(b"\r\n")[0].decode(), flush=True)
end = time.time() + seconds
s.settimeout(0.5)


def frame():
    global buffer
    if len(buffer) < 2:
        return None
    size, offset = buffer[1] & 127, 2
    if size == 126:
        if len(buffer) < 4:
            return None
        size, offset = struct.unpack(">H", buffer[2:4])[0], 4
    elif size == 127:
        if len(buffer) < 10:
            return None
        size, offset = struct.unpack(">Q", buffer[2:10])[0], 10
    if len(buffer) < offset + size:
        return None
    payload = buffer[offset : offset + size]
    buffer = buffer[offset + size :]
    return payload


while time.time() < end:
    try:
        data = s.recv(65536)
        if not data:
            break
        buffer += data
    except socket.timeout:
        pass
    while (payload := frame()) is not None:
        try:
            message = json.loads(payload)
        except ValueError:
            continue
        print("frame:", str(message)[:300], flush=True)
        data = message.get("Data") or {}
        if isinstance(data, dict) and data.get("Name") == "DisplayMessage":
            print("DisplayMessage:", data.get("Arguments", {}).get("Text"), flush=True)
