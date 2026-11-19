#!/usr/bin/env python3
"""A stock Jellyfin remote with a large synthetic catalog and no media, for measuring the sync at scale.

usage: fake_remote.py <port> <request log>
       fake_remote.py --expect <remotes>   (what a federation of that many fakes holds in music, then exits)

The catalog is generated on demand from the item number, so it costs no memory: SCALE_FILMS films, SCALE_SERIES series
with SCALE_SEASONS seasons of SCALE_EPISODES episodes each. Items look like what a 12.4 server sends for the fields the
plugin asks for (provider IDs, one media source with video, audio and subtitle streams, people, chapters, image tags).
SCALE_IMAGES=0 sends no image tags, so the plugin downloads no image. SCALE_IMAGE_BYTES pads the JPEG and
SCALE_IMAGE_DELAY_MS waits before each image, to stand for a real poster over a real link. SCALE_SERVER_ID and
SCALE_SERVER_NAME set the identity of the fake, so that several of them can be configured on one consumer. While the file
SCALE_HOLD names exists, the catalog is empty.

SCALE_TRACKS above 0 adds a music library of that many tracks, with their albums and artists, as a 12.4 server lists
them (see MusicCatalog). SCALE_SERVER_INDEX (1-3) picks which share of the music this fake holds, so that three fakes
overlap the way MusicCatalog describes; SCALE_MUSIC_SHARING=all gives every fake the same albums, none no shared album.
With music on, the film and series libraries are listed only when they hold something.

Every request goes to the log as "<epoch ms> <ms spent> <bytes> <kind> <detail>", which is what the phases are cut from.
"""
import base64
import bisect
import http.server
import json
import os
import sys
import threading
import time
import urllib.parse

# The request log, set by main.
LOG = None
FILMS = int(os.environ.get("SCALE_FILMS", "5000"))
SERIES = int(os.environ.get("SCALE_SERIES", "500"))
SEASONS = int(os.environ.get("SCALE_SEASONS", "5"))
EPISODES = int(os.environ.get("SCALE_EPISODES", "20"))
IMAGES = os.environ.get("SCALE_IMAGES", "1") == "1"
IMAGE_DELAY = int(os.environ.get("SCALE_IMAGE_DELAY_MS", "0")) / 1000
SERVER_ID = os.environ.get("SCALE_SERVER_ID", "8" * 32)
SERVER_NAME = os.environ.get("SCALE_SERVER_NAME", "scale-remote")
HOLD = os.environ.get("SCALE_HOLD")

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


# Music, on when SCALE_TRACKS is above 0. Albums are numbered once for all the fakes and come in blocks of slots. A
# slot says which of the three remotes hold the album and how each copy carries its MusicBrainz IDs, so an album has the
# same names, numbers, durations and IDs on every remote that holds it, as copies of one release would.
TRACKS = int(os.environ.get("SCALE_TRACKS", "0"))
SERVER_INDEX = (int(os.environ.get("SCALE_SERVER_INDEX", "1")) - 1) % 3 + 1
SHARING = os.environ.get("SCALE_MUSIC_SHARING", "mixed")
ARTIST_ALBUMS = int(os.environ.get("SCALE_ARTIST_ALBUMS", "4"))
GUEST_COUNT = 400
MUSIC_LIBRARY = "9" * 32
# The folder of the library on disk, which holds the artist folders.
MUSIC_FOLDER = "d" + "0" * 31
AUDIO, ALBUM, ARTIST = "a", "b", "c"
MUSIC_KINDS = {"Audio": AUDIO, "MusicAlbum": ALBUM, "MusicArtist": ARTIST}
# "ids": every copy has all the IDs. "names": the copy on the remote the block picks has none, a title in lower case and a
# duration 1 s longer, or 3 s longer on every sixth track number, beyond the 2 s of row 453. "track": the copy on the
# last remote of the slot has the release track ID and not the recording ID.
SHARINGS = {
    "mixed": [((1, 2, 3), "ids"), ((1, 2, 3), "names"), ((1, 2), "ids"), ((2, 3), "ids"), ((1, 3), "track"),
              ((1,), "ids"), ((2,), "ids"), ((3,), "ids")],
    "all": [((1, 2, 3), "ids")],
    "none": [((1,), "ids"), ((2,), "ids"), ((3,), "ids")],
}
# Codec, container, bit depth (none on lossy codecs, as 12.4 reports MP3 and AAC), sample rate and bitrate. Each remote
# takes the format two places after the previous one, so the copies of an album differ, and MP3 at 320k against 310k
# ties on bitrate.
FORMATS = [
    ("flac", "flac", 16, 44100, 900_000),
    ("mp3", "mp3", None, 44100, 320_000),
    ("flac", "flac", 24, 96000, 2_800_000),
    ("mp3", "mp3", None, 44100, 310_000),
    ("alac", "m4a", 16, 44100, 950_000),
    ("aac", "m4a", None, 44100, 256_000),
    ("pcm_s16le", "wav", 16, 44100, 1_411_200),
]
MUSIC_GENRES = ["Rock", "Jazz", "Electronic", "Classical", "Pop", "Folk", "Hip-Hop", "Ambient", "Blues", "Metal"]
# Kinds of invented MusicBrainz IDs, the first group of the UUID.
RECORDING, RELEASE_TRACK, RELEASE, RELEASE_GROUP, MB_ARTIST = 0x5CA1E001, 0x5CA1E002, 0x5CA1E003, 0x5CA1E004, 0x5CA1E005


def mbid(kind, number):
    return f"{kind:08x}-0000-4000-8000-{number:012x}"


def music_id(prefix, number):
    return f"{prefix}{number:031x}"


def album_size(album):
    return 8 + album * 5 % 9


def album_discs(album):
    return 3 if album % 23 == 7 else 2 if album % 6 == 4 else 1


def disc_and_number(album, position):
    size, extra = divmod(album_size(album), album_discs(album))
    for disc in range(album_discs(album)):
        tracks = size + (1 if disc < extra else 0)
        if position < tracks:
            return disc + 1, position + 1
        position -= tracks
    raise ValueError(position)


def artist_of(album):
    return album // ARTIST_ALBUMS


def guest_of(album, position):
    return (album * 7 + position) % GUEST_COUNT if (album + position) % 10 == 3 else None


def artist_name(artist):
    return f"Synthetic Artist {artist:05d}"


def guest_name(guest):
    return f"Synthetic Guest {guest:03d}"


def album_name(album):
    return f"Synthetic Album {album:06d}"


def album_year(album):
    return 1960 + album % 65


def song_title(album, disc, number):
    return f"Synthetic Song {album:06d}-{disc}-{number:02d}"


class MusicCatalog:
    """The music of one remote: the albums of its slots in global order, whole, until it holds the tracks asked for.

    Tracks, albums and album artists are numbered on this remote; the artists that only appear as guests on a track
    follow the album artists, and only /Artists lists them, as for a by-name artist of 12.4.
    """

    def __init__(self, server, tracks, sharing):
        self.server, self.slots = server, SHARINGS[sharing]
        mine = [slot for slot, (holders, _) in enumerate(self.slots) if server in holders]
        self.albums, self.starts, self.counts = [], [], []
        total = 0
        while total < tracks:
            block, slot = divmod(len(self.albums), len(mine))
            album = block * len(self.slots) + mine[slot]
            self.albums.append(album)
            self.starts.append(total)
            self.counts.append(min(album_size(album), tracks - total))
            total += self.counts[-1]
        self.tracks = total
        self.artists, self.artist_number, self.albums_of_artist, guests = [], {}, [], set()
        for local, album in enumerate(self.albums):
            artist = artist_of(album)
            if artist not in self.artist_number:
                self.artist_number[artist] = len(self.artists)
                self.artists.append(artist)
                self.albums_of_artist.append([])
            self.albums_of_artist[self.artist_number[artist]].append(local)
            guests.update(guest for guest in (guest_of(album, p) for p in range(self.counts[local])) if guest is not None)
        self.guests = sorted(guests)
        self.guest_number = {guest: len(self.artists) + n for n, guest in enumerate(self.guests)}

    def policy(self, album):
        holders, ids = self.slots[album % len(self.slots)]
        if ids == "names":
            return "names" if self.server == album // len(self.slots) % 3 + 1 else "ids"
        if ids == "track":
            return "track" if self.server == holders[-1] else "ids"
        return "ids"

    def holders(self, album):
        return self.slots[album % len(self.slots)][0]

    def describe(self):
        by_holders, policies = {}, {}
        for local, album in enumerate(self.albums):
            by_holders[len(self.holders(album))] = by_holders.get(len(self.holders(album)), 0) + self.counts[local]
            policies[self.policy(album)] = policies.get(self.policy(album), 0) + self.counts[local]
        several = sum(1 for album in self.albums if album_discs(album) > 1)
        return (f"music: {self.tracks} tracks in {len(self.albums)} albums ({several} on several discs), {len(self.artists)} album "
                f"artists and {len(self.guests)} guests; remote {self.server} of 3, sharing {SHARING}: {by_holders.get(3, 0)} tracks "
                f"on three remotes, {by_holders.get(2, 0)} on two, {by_holders.get(1, 0)} on this one only, "
                f"{policies.get('names', 0)} without MusicBrainz IDs, {policies.get('track', 0)} with the release track ID only")

    def local_album(self, track):
        return bisect.bisect_right(self.starts, track) - 1

    def artist_items(self, names):
        return [{"Name": name, "Id": self.artist_id_by_name(name)} for name in names]

    def artist_id_by_name(self, name):
        if name.startswith("Synthetic Guest "):
            return music_id(ARTIST, self.guest_number[int(name.rsplit(" ", 1)[1])])
        return music_id(ARTIST, self.artist_number[int(name.rsplit(" ", 1)[1])])

    def format_of(self, album, position):
        codec, container, depth, rate, bitrate = FORMATS[(album + 2 * self.server) % len(FORMATS)]
        if codec in ("flac", "alac"):
            bitrate = bitrate * (90 + (album * 7 + position) % 20) // 100
        return codec, container, depth, rate, bitrate

    def ticks(self, album, position):
        seconds = 150 + (album * 37 + position * 53) % 240
        ticks = seconds * TICKS_PER_SECOND + (album + position) % 1000 * 10_000
        if self.policy(album) == "names":
            ticks += (3 if disc_and_number(album, position)[1] % 6 == 0 else 1) * TICKS_PER_SECOND
        return ticks

    def album_path(self, album):
        return f"/media/music/{artist_name(artist_of(album))}/{album_name(album)} ({album_year(album)})"

    def track(self, number):
        local = self.local_album(number)
        album, position = self.albums[local], number - self.starts[local]
        policy = self.policy(album)
        disc, index = disc_and_number(album, position)
        title = song_title(album, disc, index)
        if policy == "names":
            title = title.lower()
        artist = artist_name(artist_of(album))
        guest = guest_of(album, position)
        artists = [artist] + ([guest_name(guest)] if guest is not None else [])
        codec, container, depth, rate, bitrate = self.format_of(album, position)
        ticks = self.ticks(album, position)
        lyrics = (album + position) % 5 == 0
        identifier = music_id(AUDIO, number)
        folder = self.album_path(album) + (f"/Disc {disc}" if album_discs(album) > 1 else "")
        path = f"{folder}/{index:02d} - {title}.{container}"
        stream = {"Codec": codec, "TimeBase": f"1/{rate}", "VideoRange": "Unknown", "VideoRangeType": "Unknown",
                  "AudioSpatialFormat": "None", "DisplayTitle": f"{codec.split('_')[0].upper()} - Stereo", "IsInterlaced": False,
                  "IsAVC": False, "ChannelLayout": "stereo", "BitRate": bitrate}
        if depth:
            stream["BitDepth"] = depth
        stream.update({"Channels": 2, "SampleRate": rate, "IsDefault": False, "IsForced": False, "IsHearingImpaired": False,
                       "Type": "Audio", "Index": 0, "IsExternal": False, "IsTextSubtitleStream": False,
                       "SupportsExternalStream": False, "Level": 0})
        if codec == "aac":
            stream["Profile"] = "LC"
        streams = [stream]
        if lyrics:
            streams.append({"VideoRange": "Unknown", "VideoRangeType": "Unknown", "AudioSpatialFormat": "None", "IsInterlaced": False,
                            "IsDefault": False, "IsForced": False, "IsHearingImpaired": False, "Type": "Lyric", "Index": 1,
                            "IsExternal": True, "IsTextSubtitleStream": False, "SupportsExternalStream": False,
                            "Path": path.rsplit(".", 1)[0] + ".lrc", "Level": 0})
        ids = {}
        if policy != "names":
            ids = {"MusicBrainzAlbum": mbid(RELEASE, album), "MusicBrainzAlbumArtist": mbid(MB_ARTIST, artist_of(album)),
                   "MusicBrainzArtist": mbid(MB_ARTIST, artist_of(album)), "MusicBrainzReleaseGroup": mbid(RELEASE_GROUP, album),
                   "MusicBrainzTrack": mbid(RELEASE_TRACK, album * 100 + position)}
            if policy != "track":
                ids["MusicBrainzRecording"] = mbid(RECORDING, album * 100 + position)
        genres = [MUSIC_GENRES[album % len(MUSIC_GENRES)]]
        value = {
            "Name": title, "ServerId": SERVER_ID, "Id": identifier, "Etag": f"{number:032x}", "DateCreated": stamp(number),
            "Container": container, "PremiereDate": f"{album_year(album)}-01-01T00:00:00.0000000Z",
            "MediaSources": [{
                "Protocol": "File", "Id": identifier, "Path": path, "Type": "Default", "Container": container,
                "Size": bitrate // 8 * ticks // TICKS_PER_SECOND, "Name": f"{index:02d} - {title}", "IsRemote": False,
                "ETag": f"{number:032x}", "RunTimeTicks": ticks, "ReadAtNativeFramerate": False, "IgnoreDts": False,
                "IgnoreIndex": False, "GenPtsInput": False, "SupportsTranscoding": True, "SupportsDirectStream": True,
                "SupportsDirectPlay": True, "IsInfiniteStream": False, "UseMostCompatibleTranscodingProfile": False,
                "RequiresOpening": False, "RequiresClosing": False, "RequiresLooping": False, "SupportsProbing": True,
                "MediaStreams": streams, "MediaAttachments": [], "Formats": [], "Bitrate": bitrate, "RequiredHttpHeaders": {},
                "TranscodingSubProtocol": "http", "DefaultAudioStreamIndex": 0, "HasSegments": False}],
            "Path": path, "Taglines": [], "Genres": genres, "RunTimeTicks": ticks, "ProductionYear": album_year(album),
            "IndexNumber": index, "ParentIndexNumber": disc, "RemoteTrailers": [], "ProviderIds": ids, "IsFolder": False,
            "ParentId": music_id(ALBUM, local), "Type": "Audio", "People": [], "Studios": [],
            "GenreItems": [{"Name": genre, "Id": f"{MUSIC_GENRES.index(genre):032x}"} for genre in genres], "Tags": [],
            "HasLyrics": lyrics, "CanDownload": True, "Artists": artists, "ArtistItems": self.artist_items(artists), "Album": album_name(album),
            "AlbumId": music_id(ALBUM, local), "AlbumArtist": artist, "AlbumArtists": self.artist_items([artist]),
            "ImageTags": {}, "BackdropImageTags": [], "Chapters": [], "LocationType": "FileSystem", "MediaType": "Audio",
            "NormalizationGain": -round(5 + (album * 13 + position) % 80 / 10, 1),
            "AlbumNormalizationGain": -round(6 + album % 50 / 10, 1),
        }
        if IMAGES:
            value["AlbumPrimaryImageTag"] = f"{ALBUM}{local:031x}"
        return value

    def album(self, local):
        album = self.albums[local]
        artist = artist_name(artist_of(album))
        ids = {} if self.policy(album) == "names" else {
            "MusicBrainzAlbum": mbid(RELEASE, album), "MusicBrainzAlbumArtist": mbid(MB_ARTIST, artist_of(album)),
            "MusicBrainzReleaseGroup": mbid(RELEASE_GROUP, album)}
        genres = [MUSIC_GENRES[album % len(MUSIC_GENRES)]]
        value = {
            "Name": album_name(album), "ServerId": SERVER_ID, "Id": music_id(ALBUM, local), "Etag": f"{local:032x}",
            "DateCreated": stamp(local), "PremiereDate": f"{album_year(album)}-01-01T00:00:00.0000000Z",
            "Path": self.album_path(album), "Taglines": [], "Genres": genres,
            "RunTimeTicks": sum(self.ticks(album, position) for position in range(self.counts[local])),
            "ProductionYear": album_year(album), "RemoteTrailers": [], "ProviderIds": ids, "IsFolder": True,
            "ParentId": music_id(ARTIST, self.artist_number[artist_of(album)]), "Type": "MusicAlbum", "People": [], "Studios": [],
            "GenreItems": [{"Name": genre, "Id": f"{MUSIC_GENRES.index(genre):032x}"} for genre in genres], "Tags": [],
            "Artists": [artist], "ArtistItems": self.artist_items([artist]), "AlbumArtist": artist,
            "AlbumArtists": self.artist_items([artist]), "ImageTags": {}, "BackdropImageTags": [], "Chapters": [],
            "LocationType": "FileSystem", "MediaType": "Unknown", "NormalizationGain": -round(6 + album % 50 / 10, 1),
            "CanDownload": False,
        }
        value.update(images(f"{ALBUM}{local:031x}", None))
        if IMAGES:
            value["PrimaryImageAspectRatio"] = 1
        return value

    def artist(self, local):
        guest = local >= len(self.artists)
        name = guest_name(self.guests[local - len(self.artists)]) if guest else artist_name(self.artists[local])
        value = {
            "Name": name, "ServerId": SERVER_ID, "Id": music_id(ARTIST, local), "Etag": f"{local:032x}", "DateCreated": stamp(local),
            "Path": f"/config/metadata/artists/{name}" if guest else f"/media/music/{name}", "Taglines": [], "Genres": [],
            "RemoteTrailers": [], "ProviderIds": {} if guest else {"MusicBrainzArtist": mbid(MB_ARTIST, self.artists[local])},
            "IsFolder": True, "ParentId": MUSIC_FOLDER, "Type": "MusicArtist", "People": [], "Studios": [], "GenreItems": [],
            "Tags": [], "ImageTags": {}, "BackdropImageTags": [], "Chapters": [], "LocationType": "FileSystem",
            "MediaType": "Unknown", "RunTimeTicks": 0, "CanDownload": False,
        }
        if not guest and local % 2 == 0:
            value["Overview"] = OVERVIEW
            value.update(images(f"{ARTIST}{local:031x}", f"{ARTIST}{local:031x}" if local % 4 == 0 else None))
        return value

    def count(self, kind):
        return {"Audio": self.tracks, "MusicAlbum": len(self.albums), "MusicArtist": len(self.artists)}[kind]

    def by_id(self, identifier):
        try:
            number = int(identifier[1:], 16)
        except ValueError:
            return None
        prefix = identifier[:1]
        if len(identifier) != 32:
            return None
        if prefix == AUDIO and number < self.tracks:
            return self.track(number)
        if prefix == ALBUM and number < len(self.albums):
            return self.album(number)
        if prefix == ARTIST and number < len(self.artists) + len(self.guests):
            return self.artist(number)
        return None

    def children(self, kind, parent):
        """The numbers of the items of a kind under a parent: the library, an album or an album artist."""
        if parent in (None, MUSIC_LIBRARY):
            return range(self.count(kind))
        prefix, number = parent[:1], int(parent[1:], 16) if len(parent) == 32 and parent[1:].isalnum() else -1
        if prefix == ALBUM and 0 <= number < len(self.albums) and kind == "Audio":
            return range(self.starts[number], self.starts[number] + self.counts[number])
        if prefix == ARTIST and 0 <= number < len(self.artists):
            albums = self.albums_of_artist[number]
            if kind == "MusicAlbum":
                return albums
            if kind == "Audio":
                return [track for local in albums for track in range(self.starts[local], self.starts[local] + self.counts[local])]
        return []

    def page(self, kind, parent, start, limit):
        try:
            numbers = self.children(kind, parent)
        except ValueError:
            numbers = []
        maker = {"Audio": self.track, "MusicAlbum": self.album, "MusicArtist": self.artist}[kind]
        return {"Items": [maker(number) for number in numbers[start:start + limit]], "TotalRecordCount": len(numbers), "StartIndex": start}

    def artists_page(self, guests, parent, start, limit):
        total = len(self.artists) + (len(self.guests) if guests else 0) if parent in (None, MUSIC_LIBRARY) else 0
        numbers = range(start, min(total, start + limit))
        return {"Items": [self.artist(number) for number in numbers], "TotalRecordCount": total, "StartIndex": start}


def expectations(remotes, tracks, sharing):
    """What a consumer of that many fakes holds in music: tracks listed, and the tracks row 453 should find distinct."""
    copies = {}
    for server in range(1, remotes + 1):
        catalog = MusicCatalog((server - 1) % 3 + 1, tracks, sharing)
        for local, album in enumerate(catalog.albums):
            policy = catalog.policy(album)
            for position in range(catalog.counts[local]):
                far = policy == "names" and disc_and_number(album, position)[1] % 6 == 0
                copies.setdefault((album, position), []).append("far" if far else policy)
    listed = sum(len(found) for found in copies.values())
    distinct = by_id = by_track = by_names = apart = alone = 0
    for found in copies.values():
        with_ids = [policy for policy in found if policy in ("ids", "track")]
        near = found.count("names")
        far = found.count("far")
        if len(found) == 1:
            alone += 1
        # Copies with IDs share the release track ID; a copy without IDs joins them by names within 2 s, or stays apart.
        groups = (1 if with_ids or near else 0) + far
        distinct += groups
        if with_ids:
            by_id += len(with_ids) - 1
            by_track += 1 if "track" in with_ids and len(with_ids) > 1 else 0
            by_names += near
            apart += far
        else:
            by_names += max(0, near - 1)
            apart += far if near else max(0, far - 1)
    return (f"music of {remotes} remote(s) with {tracks} tracks each, sharing {sharing}: {listed} tracks listed, {distinct} distinct "
            f"under row 453. Copies that join another: {by_id} by MusicBrainz ID ({by_track} groups only by the release track ID), "
            f"{by_names} by names and a duration 1 s off; {apart} copies 3 s off stay apart; {alone} tracks are on one remote only.")


MUSIC = MusicCatalog(SERVER_INDEX, TRACKS, SHARING) if TRACKS > 0 else None


def by_id(identifier):
    if MUSIC and identifier[:1] in MUSIC_KINDS.values():
        return MUSIC.by_id(identifier)
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

    kinds = [kind for kind in (first("includeItemTypes") or "").split(",") if kind in MAKERS or (MUSIC and kind in MUSIC_KINDS)]
    if first("minDateLastSaved") or (HOLD and os.path.exists(HOLD)):
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
    if kind in MUSIC_KINDS:
        return MUSIC.page(kind, parent, int(first("startIndex", "0")), int(first("limit", "100")))
    total = COUNTS[kind] if parent in (None, LIBRARY_OF[kind]) else 0
    start = int(first("startIndex", "0"))
    limit = int(first("limit", "100"))
    numbers = range(start, min(total, start + limit))
    return {"Items": [MAKERS[kind](number) for number in numbers], "TotalRecordCount": total, "StartIndex": start}


def artists_answer(query, album_artists):
    """The /Artists and /Artists/AlbumArtists pages: album artists, and with /Artists also the guests of the tracks."""
    def first(name, default=None):
        return query.get(name, [default])[0]

    if HOLD and os.path.exists(HOLD):
        return {"Items": [], "TotalRecordCount": 0, "StartIndex": 0}
    return MUSIC.artists_page(not album_artists, first("parentId"), int(first("startIndex", "0")), int(first("limit", "100")))


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
            return "auth", self.json({"AccessToken": TOKEN, "ServerId": SERVER_ID, "User": {"Id": USER_ID, "Name": "jellymesh", "Policy": {"IsAdministrator": False}}})
        if path == "/System/Info/Public":
            return "info", self.json({"Version": "12.4.0", "ServerName": SERVER_NAME, "Id": SERVER_ID, "ProductName": "Jellyfin Server"})
        if path == "/JellyMesh/Capabilities":
            return "capabilities", self.send(404)
        if path == "/Users/Me":
            return "me", self.json({"Id": USER_ID, "Name": "jellymesh", "Policy": {"IsAdministrator": False, "EnableVideoPlaybackTranscoding": True,
                                                                                    "EnableAudioPlaybackTranscoding": True, "EnablePlaybackRemuxing": True}})
        if path == "/UserViews":
            views = [
                {"Id": FILM_LIBRARY, "Name": "Films", "CollectionType": "movies", "Type": "CollectionFolder"},
                {"Id": SERIES_LIBRARY, "Name": "Shows", "CollectionType": "tvshows", "Type": "CollectionFolder"},
            ]
            if MUSIC:
                views = [view for view, count in zip(views, (FILMS, SERIES)) if count > 0]
                views.append({"Id": MUSIC_LIBRARY, "Name": "Music", "CollectionType": "music", "Type": "CollectionFolder"})
            return "views", self.json({"Items": views, "TotalRecordCount": len(views)})
        if MUSIC and path in ("/Artists", "/Artists/AlbumArtists"):
            return f"{'album-artists' if path.endswith('AlbumArtists') else 'artists'} {(query.get('startIndex') or ['0'])[0]}", \
                self.json(artists_answer(query, path.endswith("AlbumArtists")))
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
        if LOG is None:
            return
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


def main():
    global LOG
    if sys.argv[1] == "--expect":
        print(expectations(int(sys.argv[2]), TRACKS, SHARING))
        return
    port, LOG = int(sys.argv[1]), sys.argv[2]
    print(f"catalog: {COUNTS}, images {'on' if IMAGES else 'off'}", flush=True)
    if MUSIC:
        print(MUSIC.describe(), flush=True)
    Server(("0.0.0.0", port), Handler).serve_forever()


if __name__ == "__main__":
    main()
