#!/usr/bin/env python3
"""A stock Jellyfin remote with a large synthetic catalog and no media, for measuring the sync at scale.

usage: fake_remote.py <port> <request log>

The catalog is generated on demand from the item number, so it costs no memory: SCALE_FILMS films, SCALE_SERIES series
with SCALE_SEASONS seasons of SCALE_EPISODES episodes each. Items look like what a 12.4 server sends for the fields the
plugin asks for (provider IDs, one media source with video, audio and subtitle streams, people, chapters, image tags).
SCALE_IMAGES=0 sends no image tags, so the plugin downloads no image. SCALE_IMAGE_BYTES pads the JPEG and
SCALE_IMAGE_DELAY_MS waits before each image, to stand for a real poster over a real link.

Every request goes to the log as "<epoch ms> <ms spent> <bytes> <kind> <detail>", which is what the phases are cut from.
"""
import base64
import http.server
import json
import os
import sys
import threading
import time
import urllib.parse

PORT, LOG = int(sys.argv[1]), sys.argv[2]
FILMS = int(os.environ.get("SCALE_FILMS", "5000"))
SERIES = int(os.environ.get("SCALE_SERIES", "500"))
SEASONS = int(os.environ.get("SCALE_SEASONS", "5"))
EPISODES = int(os.environ.get("SCALE_EPISODES", "20"))
IMAGES = os.environ.get("SCALE_IMAGES", "1") == "1"
IMAGE_DELAY = int(os.environ.get("SCALE_IMAGE_DELAY_MS", "0")) / 1000

TOKEN = "scale-remote-token"
USER_ID = "5" * 32
FILM_LIBRARY = "6" * 32
SERIES_LIBRARY = "7" * 32
TICKS_PER_SECOND = 10_000_000
# A 1x1 baseline JPEG.
JPEG = base64.b64decode(
    "/9j/4AAQSkZJRgABAQEASABIAAD/2wBDAP//////////////////////////////////////////////////////////////////////////////////////"
    "2wBDAf//////////////////////////////////////////////////////////////////////////////////////wAARCAABAAEDASIAAhEBAxEB/8QAFAAB"
    "AAAAAAAAAAAAAAAAAAAAA//EABQQAQAAAAAAAAAAAAAAAAAAAAD/xAAUAQEAAAAAAAAAAAAAAAAAAAAA/8QAFBEBAAAAAAAAAAAAAAAAAAAAAP/aAAwDAQACEQMR"
    "AD8AH//Z")
JPEG += b"\0" * max(0, int(os.environ.get("SCALE_IMAGE_BYTES", "0")) - len(JPEG))

# The four kinds share one ID space: the first hex digit is the kind, the rest the number.
MOVIE, SERIES_KIND, SEASON, EPISODE = "1", "2", "3", "4"
COUNTS = {
    "Movie": FILMS,
    "Series": SERIES,
    "Season": SERIES * SEASONS,
    "Episode": SERIES * SEASONS * EPISODES,
}
LIBRARY_OF = {"Movie": FILM_LIBRARY, "Series": SERIES_LIBRARY, "Season": SERIES_LIBRARY, "Episode": SERIES_LIBRARY}
KIND_OF = {MOVIE: "Movie", SERIES_KIND: "Series", SEASON: "Season", EPISODE: "Episode"}
PREFIX_OF = {name: prefix for prefix, name in KIND_OF.items()}
GENRES = ["Drama", "Comedy", "Thriller", "Science Fiction", "Crime", "Adventure", "Animation", "Documentary"]
STUDIOS = ["Northlight Pictures", "Harbor Films", "Blue Meridian", "Castello Studios", "Iron Bridge"]
OVERVIEW = ("A synthetic plot written to weigh what a real overview weighs. Someone wants something, something stands in "
            "the way, and over two hours the two meet, part and meet again, until one of them gives. ")
log_lock = threading.Lock()


def item_id(kind, number):
    return f"{PREFIX_OF[kind]}{number:031x}"


def stamp(seconds):
    return time.strftime("%Y-%m-%dT%H:%M:%S.0000000Z", time.gmtime(1_600_000_000 + seconds))


def images(primary, backdrop):
    if not IMAGES:
        return {}
    value = {"ImageTags": {"Primary": primary}}
    value["BackdropImageTags"] = [backdrop] if backdrop else []
    return value


def media_source(identifier, number, minutes, height):
    width = 3840 if height == 2160 else 1920 if height == 1080 else 1280
    bitrate = 18_000_000 if height == 2160 else 8_000_000 if height == 1080 else 3_500_000
    return {
        "Id": identifier,
        "Name": f"Synthetic {number}",
        "Container": "mkv",
        "Protocol": "File",
        "Path": f"/media/synthetic/{identifier}.mkv",
        "IsRemote": False,
        "Size": bitrate // 8 * minutes * 60,
        "Bitrate": bitrate + 640_000,
        "RunTimeTicks": minutes * 60 * TICKS_PER_SECOND,
        "ETag": f"{number:032x}",
        "MediaStreams": [
            {"Index": 0, "Type": "Video", "Codec": "hevc" if height == 2160 else "h264", "Profile": "Main 10" if height == 2160 else "High",
             "Level": 150 if height == 2160 else 41, "Height": height, "Width": width, "BitRate": bitrate, "BitDepth": 10 if height == 2160 else 8,
             "AspectRatio": "16:9", "AverageFrameRate": 23.976, "RealFrameRate": 23.976, "PixelFormat": "yuv420p10le" if height == 2160 else "yuv420p",
             "IsAVC": height != 2160, "IsAnamorphic": False, "IsInterlaced": False, "RefFrames": 1, "CodecTag": "",
             "TimeBase": "1/1000", "IsDefault": True, "VideoRange": "HDR" if height == 2160 else "SDR",
             "VideoRangeType": "HDR10" if height == 2160 else "SDR", "ColorRange": "tv", "ColorSpace": "bt2020nc" if height == 2160 else "bt709",
             "ColorTransfer": "smpte2084" if height == 2160 else "bt709", "ColorPrimaries": "bt2020" if height == 2160 else "bt709"},
            {"Index": 1, "Type": "Audio", "Codec": "eac3", "Language": "eng", "Title": "English 5.1", "Channels": 6, "ChannelLayout": "5.1",
             "SampleRate": 48000, "BitRate": 640_000, "IsDefault": True, "TimeBase": "1/1000"},
            {"Index": 2, "Type": "Audio", "Codec": "aac", "Language": "ita", "Title": "Italiano", "Channels": 2, "ChannelLayout": "stereo",
             "SampleRate": 48000, "BitRate": 192_000, "IsDefault": False, "TimeBase": "1/1000"},
            {"Index": 3, "Type": "Subtitle", "Codec": "subrip", "Language": "eng", "IsDefault": False, "IsForced": False, "TimeBase": "1/1000"},
            {"Index": 4, "Type": "Subtitle", "Codec": "subrip", "Language": "ita", "IsDefault": False, "IsForced": False, "TimeBase": "1/1000"},
        ],
    }


def chapters(minutes, count):
    step = minutes * 60 * TICKS_PER_SECOND // count
    return [{"Name": f"Chapter {i + 1}", "StartPositionTicks": i * step} for i in range(count)]


def people(number, count):
    roles = [("Director", None), ("Writer", None)] + [("Actor", f"Character {i}") for i in range(count - 2)]
    return [{"Name": f"Person {(number * 7 + i) % 20000}", "Role": role, "Type": kind, "Id": f"{(number * 7 + i) % 20000:032x}"}
            for i, (kind, role) in enumerate(roles)]


def movie(number):
    identifier = item_id("Movie", number)
    minutes = 90 + number % 60
    year = 1950 + number % 75
    value = {
        "Id": identifier, "Type": "Movie", "Name": f"Synthetic Film {number:05d}", "OriginalTitle": f"Film Sintetico {number:05d}",
        "Overview": OVERVIEW * 2, "Taglines": ["Nothing here is real."], "Genres": [GENRES[number % 8], GENRES[(number + 3) % 8]],
        "Studios": [{"Name": STUDIOS[number % 5], "Id": f"{number % 5:032x}"}], "Tags": [], "People": people(number, 12),
        "OfficialRating": "PG-13", "CommunityRating": 5 + number % 50 / 10, "CriticRating": 40 + number % 60,
        "PremiereDate": f"{year}-03-15T00:00:00.0000000Z", "ProductionYear": year,
        "ProviderIds": {"Tmdb": str(100000 + number), "Imdb": f"tt{1000000 + number}"},
        "RunTimeTicks": minutes * 60 * TICKS_PER_SECOND, "DateCreated": stamp(number), "Etag": f"{number:032x}",
        "Chapters": chapters(minutes, 8), "MediaSources": [media_source(identifier, number, minutes, [720, 1080, 2160][number % 3])],
        "IsFolder": False, "MediaType": "Video", "LocationType": "FileSystem",
    }
    value.update(images(f"p{number:031x}", f"b{number:031x}"))
    return value


def series_name(number):
    return f"Synthetic Series {number:04d}"


def series(number):
    year = 1990 + number % 35
    value = {
        "Id": item_id("Series", number), "Type": "Series", "Name": series_name(number), "OriginalTitle": series_name(number),
        "Overview": OVERVIEW, "Genres": [GENRES[number % 8]], "Studios": [{"Name": STUDIOS[number % 5], "Id": f"{number % 5:032x}"}],
        "Tags": [], "People": people(number, 10), "OfficialRating": "TV-14", "CommunityRating": 6 + number % 40 / 10,
        "PremiereDate": f"{year}-09-01T00:00:00.0000000Z", "ProductionYear": year, "Status": "Ended",
        "ProviderIds": {"Tvdb": str(300000 + number), "Tmdb": str(200000 + number), "Imdb": f"tt{2000000 + number}"},
        "DateCreated": stamp(number), "Etag": f"{number:032x}", "IsFolder": True, "LocationType": "FileSystem",
    }
    value.update(images(f"p{number:031x}", f"b{number:031x}"))
    return value


def season(number):
    show, index = divmod(number, SEASONS)
    value = {
        "Id": item_id("Season", number), "Type": "Season", "Name": f"Season {index + 1}", "IndexNumber": index + 1,
        "SeriesId": item_id("Series", show), "SeriesName": series_name(show), "ProviderIds": {},
        "DateCreated": stamp(number), "Etag": f"{number:032x}", "IsFolder": True, "LocationType": "FileSystem",
    }
    value.update(images(f"p{number:031x}", None))
    return value


def episode(number):
    season_number, index = divmod(number, EPISODES)
    show, season_index = divmod(season_number, SEASONS)
    identifier = item_id("Episode", number)
    value = {
        "Id": identifier, "Type": "Episode", "Name": f"Episode {index + 1} of {series_name(show)}", "Overview": OVERVIEW,
        "People": people(number, 6), "Genres": [], "Studios": [], "Tags": [], "CommunityRating": 7.0,
        "PremiereDate": f"{1990 + show % 35 + season_index}-10-{1 + index % 28:02d}T00:00:00.0000000Z",
        "ProviderIds": {"Tvdb": str(5000000 + number)}, "RunTimeTicks": 45 * 60 * TICKS_PER_SECOND,
        "DateCreated": stamp(number), "Etag": f"{number:032x}", "IndexNumber": index + 1, "ParentIndexNumber": season_index + 1,
        "SeriesId": item_id("Series", show), "SeriesName": series_name(show), "SeasonId": item_id("Season", season_number),
        "SeasonName": f"Season {season_index + 1}", "Chapters": chapters(45, 4),
        "MediaSources": [media_source(identifier, number, 45, 1080)], "IsFolder": False, "MediaType": "Video", "LocationType": "FileSystem",
    }
    value.update(images(f"p{number:031x}", None))
    return value


MAKERS = {"Movie": movie, "Series": series, "Season": season, "Episode": episode}


def by_id(identifier):
    kind = KIND_OF.get(identifier[:1])
    try:
        number = int(identifier[1:], 16)
    except ValueError:
        return None
    if kind is None or len(identifier) != 32 or number >= COUNTS[kind]:
        return None
    return MAKERS[kind](number)


def items_answer(query):
    def first(name, default=None):
        return query.get(name, [default])[0]

    kinds = [kind for kind in (first("includeItemTypes") or "").split(",") if kind in MAKERS]
    if first("minDateLastSaved"):
        # The catalog never changes after it was made.
        return {"Items": [], "TotalRecordCount": 0, "StartIndex": 0}
    if first("ids"):
        found = [by_id(identifier) for identifier in first("ids").split(",")]
        found = [item for item in found if item and item["Type"] in kinds]
        return {"Items": found, "TotalRecordCount": len(found), "StartIndex": 0}
    if len(kinds) != 1:
        return {"Items": [], "TotalRecordCount": 0, "StartIndex": 0}
    kind = kinds[0]
    parent = first("parentId")
    total = COUNTS[kind] if parent in (None, LIBRARY_OF[kind]) else 0
    start = int(first("startIndex", "0"))
    limit = int(first("limit", "100"))
    numbers = range(start, min(total, start + limit))
    return {"Items": [MAKERS[kind](number) for number in numbers], "TotalRecordCount": total, "StartIndex": start}


class Handler(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def send(self, status, body=b"", kind="application/json"):
        self.send_response(status)
        self.send_header("Content-Type", kind)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        if body and self.command != "HEAD":
            self.wfile.write(body)
        return len(body)

    def json(self, value):
        return self.send(200, json.dumps(value, separators=(",", ":")).encode())

    def route(self, path, query):
        if path == "/Users/AuthenticateByName":
            return "auth", self.json({"AccessToken": TOKEN, "User": {"Id": USER_ID, "Name": "jellymesh", "Policy": {"IsAdministrator": False}}})
        if path == "/System/Info/Public":
            return "info", self.json({"Version": "12.4.0", "ServerName": "scale-remote", "Id": "8" * 32, "ProductName": "Jellyfin Server"})
        if path == "/JellyMesh/Capabilities":
            return "capabilities", self.send(404)
        if path == "/Users/Me":
            return "me", self.json({"Id": USER_ID, "Name": "jellymesh", "Policy": {"IsAdministrator": False, "EnableVideoPlaybackTranscoding": True,
                                                                                    "EnableAudioPlaybackTranscoding": True, "EnablePlaybackRemuxing": True}})
        if path == "/UserViews":
            return "views", self.json({"Items": [
                {"Id": FILM_LIBRARY, "Name": "Films", "CollectionType": "movies", "Type": "CollectionFolder"},
                {"Id": SERIES_LIBRARY, "Name": "Shows", "CollectionType": "tvshows", "Type": "CollectionFolder"},
            ], "TotalRecordCount": 2})
        if path == "/Items":
            kind = (query.get("includeItemTypes") or ["?"])[0]
            detail = "since" if "minDateLastSaved" in query else "ids" if "ids" in query else (query.get("startIndex") or ["0"])[0]
            return f"items-{kind} {detail}", self.json(items_answer(query))
        parts = path.split("/")
        if len(parts) >= 5 and parts[1] == "Items" and parts[3] == "Images":
            if IMAGE_DELAY:
                time.sleep(IMAGE_DELAY)
            return f"image-{parts[4]}", self.send(200, JPEG, "image/jpeg")
        return f"unknown {self.command} {path}", self.send(404)

    def handle_one(self):
        started = time.time()
        target = urllib.parse.urlsplit(self.path)
        length = int(self.headers.get("Content-Length") or 0)
        if length:
            self.rfile.read(length)
        kind, sent = self.route(target.path, urllib.parse.parse_qs(target.query))
        with log_lock, open(LOG, "a") as handle:
            handle.write(f"{int(started * 1000)} {int((time.time() - started) * 1000)} {sent} {kind}\n")

    def do_GET(self):
        self.handle_one()

    def do_POST(self):
        self.handle_one()

    def do_HEAD(self):
        self.handle_one()

    def log_message(self, *args):
        pass


class Server(http.server.ThreadingHTTPServer):
    allow_reuse_address = True
    daemon_threads = True
    request_queue_size = 256


print(f"catalog: {COUNTS}, images {'on' if IMAGES else 'off'}", flush=True)
Server(("0.0.0.0", PORT), Handler).serve_forever()
