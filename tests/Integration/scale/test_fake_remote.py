#!/usr/bin/env python3
"""Checks the music catalog of fake_remote.py against the shape of a stock 12.4 server and against itself.

usage: python3 -m unittest test_fake_remote   (from this folder; no container needed)
"""
import importlib
import json
import os
import sys
import tempfile
import threading
import unittest
import urllib.request
from unittest import mock

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

# The fields a 12.4 server was seen to send, or documents, for the music queries of the plugin (F48 probes, research/music).
AUDIO_KEYS = {"Name", "Id", "ServerId", "Type", "Path", "Container", "RunTimeTicks", "ProductionYear", "IndexNumber",
              "ParentIndexNumber", "ParentId", "Album", "AlbumId", "AlbumPrimaryImageTag", "Artists", "ArtistItems",
              "AlbumArtist", "AlbumArtists", "HasLyrics", "ProviderIds", "MediaSources", "ImageTags", "NormalizationGain",
              "DateCreated", "Genres", "MediaType", "CanDownload"}
ALBUM_KEYS = {"Name", "Id", "Type", "Path", "RunTimeTicks", "ProductionYear", "Artists", "AlbumArtist", "AlbumArtists",
              "ProviderIds", "ImageTags", "ParentId", "IsFolder", "DateCreated"}
ARTIST_KEYS = {"Name", "Id", "Type", "Path", "ProviderIds", "ImageTags", "ParentId", "IsFolder", "RunTimeTicks"}
TRACK_IDS = {"MusicBrainzAlbum", "MusicBrainzAlbumArtist", "MusicBrainzArtist", "MusicBrainzRecording",
             "MusicBrainzReleaseGroup", "MusicBrainzTrack"}
ALBUM_IDS = {"MusicBrainzAlbum", "MusicBrainzAlbumArtist", "MusicBrainzReleaseGroup"}
STREAM_KEYS = {"Codec", "BitRate", "Channels", "SampleRate", "Type"}
LOSSLESS = {"alac", "ape", "flac", "mlp", "truehd", "wavpack"}


def load(**env):
    """Imports a fresh copy of the fake with that environment, as a new process would."""
    values = {"SCALE_FILMS": "0", "SCALE_SERIES": "0"}
    values.update(env)
    with mock.patch.dict(os.environ, values):
        sys.modules.pop("fake_remote", None)
        return importlib.import_module("fake_remote")


class Served:
    """A fake on a free port, with its request log."""

    def __init__(self, module):
        self.module = module
        self.log = tempfile.NamedTemporaryFile(delete=False).name
        module.LOG = self.log
        self.server = module.Server(("127.0.0.1", 0), module.Handler)
        threading.Thread(target=self.server.serve_forever, daemon=True).start()

    def get(self, path):
        with urllib.request.urlopen(f"http://127.0.0.1:{self.server.server_address[1]}{path}") as answer:
            return json.loads(answer.read())

    def logged(self):
        with open(self.log) as handle:
            return [line.split(" ", 3)[3] for line in handle.read().splitlines()]

    def close(self):
        self.server.shutdown()
        self.server.server_close()
        os.unlink(self.log)


def pages(served, query, size=200):
    items, start = [], 0
    while True:
        page = served.get(f"/Items?{query}&startIndex={start}&limit={size}")
        items += page["Items"]
        start += size
        if start >= page["TotalRecordCount"]:
            return items, page["TotalRecordCount"]


class DefaultRunTest(unittest.TestCase):
    def test_no_music_without_the_flag(self):
        module = load(SCALE_FILMS="10", SCALE_SERIES="2")
        served = Served(module)
        try:
            views = served.get("/UserViews")["Items"]
            self.assertEqual(["movies", "tvshows"], [view["CollectionType"] for view in views])
            self.assertEqual(0, served.get("/Items?includeItemTypes=Audio&startIndex=0&limit=10")["TotalRecordCount"])
        finally:
            served.close()


class MusicTest(unittest.TestCase):
    TRACKS = 3000

    @classmethod
    def setUpClass(cls):
        cls.module = load(SCALE_TRACKS=str(cls.TRACKS), SCALE_SERVER_INDEX="2")
        cls.served = Served(cls.module)
        cls.tracks, cls.total = pages(cls.served, "userId=x&recursive=true&includeItemTypes=Audio&parentId=" + "9" * 32)
        cls.albums, _ = pages(cls.served, "includeItemTypes=MusicAlbum")

    @classmethod
    def tearDownClass(cls):
        cls.served.close()

    def test_views_list_only_music_when_the_video_catalog_is_empty(self):
        views = self.served.get("/UserViews")["Items"]
        self.assertEqual([("9" * 32, "music")], [(view["Id"], view["CollectionType"]) for view in views])

    def test_paging_lists_every_track_once(self):
        self.assertEqual(self.TRACKS, self.total)
        self.assertEqual(self.TRACKS, len(self.tracks))
        self.assertEqual(self.TRACKS, len({track["Id"] for track in self.tracks}))
        self.assertIn("items-Audio 2800", self.served.logged())

    def test_ids_return_the_same_items(self):
        chosen = [self.tracks[0], self.tracks[1234], self.tracks[-1]]
        found = self.served.get("/Items?includeItemTypes=Audio&ids=" + ",".join(track["Id"] for track in chosen))["Items"]
        self.assertEqual(chosen, found)
        album = self.albums[5]
        self.assertEqual([album], self.served.get(f"/Items?includeItemTypes=MusicAlbum&ids={album['Id']}")["Items"])

    def test_tracks_have_the_shape_of_12_4(self):
        for track in self.tracks[:300]:
            self.assertLessEqual(AUDIO_KEYS, set(track))
            self.assertEqual("Audio", track["Type"])
            self.assertEqual({}, track["ImageTags"])
            self.assertLessEqual(set(track["ProviderIds"]), TRACK_IDS)
            self.assertIsInstance(track["HasLyrics"], bool)
            source = track["MediaSources"][0]
            self.assertEqual(track["Container"], source["Container"])
            audio = [stream for stream in source["MediaStreams"] if stream["Type"] == "Audio"]
            self.assertEqual(1, len(audio))
            self.assertLessEqual(STREAM_KEYS, set(audio[0]))
            self.assertEqual(audio[0]["Codec"] in LOSSLESS | {"pcm_s16le"}, "BitDepth" in audio[0])
            lyrics = [stream for stream in source["MediaStreams"] if stream["Type"] == "Lyric"]
            self.assertEqual(track["HasLyrics"], len(lyrics) == 1)
            self.assertTrue(all(stream.get("Codec") is None for stream in lyrics))
            self.assertEqual([track["AlbumArtist"]], [artist["Name"] for artist in track["AlbumArtists"]])
            self.assertEqual(track["Artists"], [artist["Name"] for artist in track["ArtistItems"]])
            self.assertEqual(track["AlbumId"], track["ParentId"])

    def test_albums_and_artists_have_the_shape_of_12_4(self):
        for album in self.albums:
            self.assertLessEqual(ALBUM_KEYS, set(album))
            self.assertIn(set(album["ProviderIds"]), (set(), ALBUM_IDS))
            self.assertEqual({"Primary"}, set(album["ImageTags"]))
        artists = self.served.get("/Artists/AlbumArtists?parentId=" + "9" * 32 + "&startIndex=0&limit=50")["Items"]
        for artist in artists:
            self.assertLessEqual(ARTIST_KEYS, set(artist))
            self.assertEqual("MusicArtist", artist["Type"])

    def test_tracks_belong_to_their_album(self):
        by_album = {}
        for track in self.tracks:
            by_album.setdefault(track["AlbumId"], []).append(track)
        albums = {album["Id"]: album for album in self.albums}
        self.assertEqual(set(albums), set(by_album))
        several = 0
        for identifier, tracks in by_album.items():
            album = albums[identifier]
            self.assertTrue(all(track["Album"] == album["Name"] and track["AlbumArtist"] == album["AlbumArtist"] for track in tracks))
            self.assertEqual(album["RunTimeTicks"], sum(track["RunTimeTicks"] for track in tracks))
            self.assertEqual(len(tracks), len({(track["ParentIndexNumber"], track["IndexNumber"]) for track in tracks}))
            children = self.served.get(f"/Items?includeItemTypes=Audio&parentId={identifier}&startIndex=0&limit=100")["Items"]
            self.assertEqual([track["Id"] for track in tracks], [track["Id"] for track in children])
            discs = {track["ParentIndexNumber"] for track in tracks}
            if len(discs) > 1:
                several += 1
                self.assertEqual(set(range(1, len(discs) + 1)), discs)
                for track in tracks:
                    self.assertIn(f"/Disc {track['ParentIndexNumber']}/", track["Path"])
                    self.assertTrue(track["Path"].startswith(album["Path"] + "/"))
                first = [track["IndexNumber"] for track in tracks if track["ParentIndexNumber"] == 2]
                self.assertEqual(1, first[0])
        self.assertGreater(several, 0)

    def test_artists_endpoints_page_and_differ_by_the_guests(self):
        album_artists = self.served.get("/Artists/AlbumArtists?startIndex=0&limit=10000")
        artists = self.served.get("/Artists?startIndex=0&limit=10000")
        guests = {name for track in self.tracks for name in track["Artists"] if name != track["AlbumArtist"]}
        self.assertEqual({track["AlbumArtist"] for track in self.tracks}, {item["Name"] for item in album_artists["Items"]})
        self.assertEqual(album_artists["TotalRecordCount"] + len(guests), artists["TotalRecordCount"])
        second = self.served.get("/Artists?startIndex=10&limit=10")["Items"]
        self.assertEqual(artists["Items"][10:20], second)
        ids = {item["Name"]: item["Id"] for item in artists["Items"]}
        for track in self.tracks[:500]:
            for artist in track["ArtistItems"]:
                self.assertEqual(ids[artist["Name"]], artist["Id"])
        self.assertIn("album-artists 0", self.served.logged())

    def test_artist_parent_lists_its_albums_and_tracks(self):
        artist = self.served.get("/Artists/AlbumArtists?startIndex=3&limit=1")["Items"][0]
        albums = self.served.get(f"/Items?includeItemTypes=MusicAlbum&parentId={artist['Id']}&startIndex=0&limit=100")["Items"]
        tracks = self.served.get(f"/Items?includeItemTypes=Audio&parentId={artist['Id']}&startIndex=0&limit=1000")["Items"]
        self.assertTrue(albums)
        self.assertTrue(all(album["AlbumArtist"] == artist["Name"] for album in albums))
        self.assertEqual({album["Id"] for album in albums}, {track["AlbumId"] for track in tracks})

    def test_changes_and_hold_answer_empty(self):
        self.assertEqual(0, self.served.get("/Items?includeItemTypes=Audio&minDateLastSaved=2020-01-01&startIndex=0&limit=10")["TotalRecordCount"])
        with tempfile.NamedTemporaryFile() as hold:
            self.module.HOLD = hold.name
            try:
                self.assertEqual(0, self.served.get("/Items?includeItemTypes=Audio&startIndex=0&limit=10")["TotalRecordCount"])
                self.assertEqual(0, self.served.get("/Artists?startIndex=0&limit=10")["TotalRecordCount"])
            finally:
                self.module.HOLD = None


class OverlapTest(unittest.TestCase):
    TRACKS = 20000

    @classmethod
    def setUpClass(cls):
        cls.module = load()
        cls.catalogs = {server: cls.module.MusicCatalog(server, cls.TRACKS, "mixed") for server in (1, 2, 3)}
        cls.copies = {}
        for server, catalog in cls.catalogs.items():
            for number in range(catalog.tracks):
                local = catalog.local_album(number)
                key = (catalog.albums[local], number - catalog.starts[local])
                cls.copies.setdefault(key, {})[server] = (catalog.policy(catalog.albums[local]), catalog.track(number))

    def test_every_remote_holds_the_tracks_asked_for(self):
        for catalog in self.catalogs.values():
            self.assertEqual(self.TRACKS, catalog.tracks)

    def test_copies_share_names_numbers_and_ids(self):
        kinds = {1: 0, 2: 0, 3: 0}
        for found in self.copies.values():
            kinds[len(found)] += 1
            with_ids = [track for policy, track in found.values() if policy != "names"]
            for policy, track in found.values():
                reference = with_ids[0] if with_ids else track
                for field in ("Album", "AlbumArtist", "IndexNumber", "ParentIndexNumber", "ProductionYear"):
                    self.assertEqual(reference[field], track[field])
                if policy == "names":
                    self.assertEqual({}, track["ProviderIds"])
                    self.assertEqual(reference["Name"].lower(), track["Name"])
                    off = (track["RunTimeTicks"] - reference["RunTimeTicks"]) / 10_000_000
                    self.assertIn(off, (1, 3) if with_ids else (0,))
                else:
                    self.assertEqual(reference["Name"], track["Name"])
                    self.assertEqual(reference["RunTimeTicks"], track["RunTimeTicks"])
                    self.assertEqual(reference["ProviderIds"]["MusicBrainzTrack"], track["ProviderIds"]["MusicBrainzTrack"])
                    self.assertEqual(reference["ProviderIds"].get("MusicBrainzRecording", track["ProviderIds"].get("MusicBrainzRecording")),
                                     track["ProviderIds"].get("MusicBrainzRecording", reference["ProviderIds"].get("MusicBrainzRecording")))
        self.assertTrue(all(kinds.values()), kinds)

    def test_copies_differ_in_quality(self):
        formats = set()
        for found in self.copies.values():
            if len(found) > 1:
                codecs = {track["MediaSources"][0]["MediaStreams"][0]["Codec"] + str(track["MediaSources"][0]["Bitrate"])
                          for _, track in found.values()}
                self.assertGreater(len(codecs), 1)
            for _, track in found.values():
                formats.add(track["Container"])
        self.assertEqual({"flac", "mp3", "m4a", "wav"}, formats)

    def test_some_copies_carry_only_the_release_track_id_and_some_none(self):
        policies = {policy for found in self.copies.values() for policy, _ in found.values()}
        self.assertEqual({"ids", "names", "track"}, policies)
        only_track = [track for found in self.copies.values() for policy, track in found.values() if policy == "track"]
        self.assertTrue(all("MusicBrainzRecording" not in track["ProviderIds"] for track in only_track))

    def test_expectations_add_up(self):
        line = self.module.expectations(3, 2000, "all")
        self.assertIn("6000 tracks listed, 2000 distinct", line)
        line = self.module.expectations(3, 2000, "none")
        self.assertIn("6000 tracks listed, 6000 distinct", line)


if __name__ == "__main__":
    unittest.main()
