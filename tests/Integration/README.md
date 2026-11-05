# JellyMesh test environment

Three Jellyfin 12.4 instances and free test media, as described in §8 of the plan.

| Instance | From the host | From another instance | Plugin |
|---|---|---|---|
| `locale` | http://127.0.0.1:8096 | `http://locale:8096` | From Phase 1 |
| `remoto-standard` | http://127.0.0.1:8097 | `http://remoto-standard:8096` | Never |
| `remoto-jellymesh` | http://127.0.0.1:8098 | `http://remoto-jellymesh:8096` | From Phase 1 |

The containers start without the plugin. Install it with `install-plugin.sh` below.

Accounts, for testing only:

- `admin` / `jellymesh-test` on all three instances
- `jellymesh` / `jellymesh-test`, non-admin, on the two remotes. This is the dedicated user from §3.1.

## Usage

Run from this folder. It needs Docker, `curl` and `jq`, plus about 1.2 GB of disk. Encoding uses the ffmpeg inside the Jellyfin image, so no host ffmpeg is needed.

```sh
./scripts/prepare-media.sh     # download sources and encode clips, takes several minutes
docker compose up -d --wait
./scripts/provision.sh         # wizard, libraries, users, scan
./scripts/report.sh            # what each server really indexed
```

All three scripts can be re-run safely. `prepare-media.sh` skips what already exists, and `provision.sh` skips finished steps and rescans.

```sh
docker compose down            # stop, keep the data
docker compose down -v         # also delete config and databases
rm -rf media federata          # delete the media, then rebuild with prepare-media.sh
```

To install the plugin, run this with the instance running. It builds in the .NET SDK image, copies the plugin into `/config/plugins` and restarts the container:

```sh
./scripts/install-plugin.sh jellymesh-test-locale-1
```

Copy `.env.example` to `.env` to set `JELLYMESH_SECRET_KEY` or `BIND_ADDRESS`. Ports bind to loopback by default, so set `BIND_ADDRESS` to test from a phone or TV.

## Folders

- `media/<instance>/film` and `media/<instance>/serie` are mounted read-only at `/media`.
- `federata/` is mounted writable at `/federata` on `locale` only. It stands in for the federated folder and is empty until something writes to it.
- On `locale`, the `Film` library has the paths `/media/film` and `/federata/Film`, and `Serie` has `/media/serie` and `/federata/Serie`. These are the two-path libraries the Phase 0 spikes need.

Default metadata fetchers stay on, as on the real server, so the spikes see normal Jellyfin behaviour. Nothing is locked in the `.nfo` files.

## Media

Clips are 60 seconds (single episodes 30, and `JM ID Parziali` 44, where the source ends) and show a burned-in label with the server and variant, so playback shows which source is really being served. All are 16:9 at exactly 1920x1080 or 1280x720, padded where needed.

The HDR10 clip is SDR footage converted to PQ/BT.2020: tags and pixels agree, but it is not a real HDR grade. Upscaled clips are not real 1080p detail. Neither matters for dedup or playback tests.

Bitrates below are the video figures reported by Jellyfin, in Mbit/s. Rerun `report.sh` for exact values. Clips with a `JM` title use invented IDs in the `999…` range, which match nothing on TMDb.

### Films with provider IDs

| Title | Scenario | locale | remoto-standard | remoto-jellymesh | Expected result |
|---|---|---|---|---|---|
| Elephants Dream (2006) | Local only | 720p H.264 2.9 | | | Local |
| Tears of Steel (2012) | Remote only | | 720p H.264 2.7 | | Standard |
| JM Solo JellyMesh (2026) | Remote only | | | 720p H.264 3.0 | JellyMesh |
| Sintel (2010) | Local wins though worse | 720p H.264 1.2 | | 1080p HEVC 4.8 | Local |
| JM Confronto Risoluzione (2026) | Resolution beats bitrate | | 720p H.264 6.0 | 1080p H.264 2.8 | JellyMesh, standard as backup |
| JM Confronto HDR (2026) | HDR beats bitrate | | 1080p HEVC SDR 5.9 | 1080p HEVC HDR10 2.9 | JellyMesh, standard as backup |
| JM Confronto Bitrate (2026) | Bitrate alone, MP4 | | 1080p H.264 2.8 | 1080p H.264 5.9 | JellyMesh, standard as backup |
| JM Confronto Codec (2026) | Codec decides within the 5% bitrate tolerance (3.6% apart) | | 1080p H.264 2.75 | 1080p AV1 2.65 | JellyMesh, standard as backup |
| JM Versioni Multiple (2026) | Two versions on one server | | 1080p and 720p H.264 | | Standard |
| JM Audio e Sottotitoli (2026) | 2 audio tracks, 1 embedded and 2 external subtitles | | 1080p H.264 3.7 | | Standard |
| JM ID Parziali (2026) | Same TMDb ID, different IMDb IDs: one match is enough (decision 13) | | 720p H.264 1.5 | 1080p H.264 3.0 | One title, JellyMesh, standard as backup |

Real IDs, checked on Wikidata:

| Film | TMDb | IMDb |
|---|---|---|
| Elephants Dream | 9761 | tt0807840 |
| Sintel | 45745 | tt1727587 |
| Tears of Steel | 133701 | tt2285752 |

### Films without provider IDs

These have no IDs and their invented titles match nothing on TMDb, so `report.sh` shows none. Title and year only propose a match, and the metadata must confirm it (decision 13). Most of these clips have no `.nfo`, so the runtime is the only metadata they have: these clips all last 60 seconds, which confirms the rows with the same title and year.

| Title | Scenario | locale | remoto-standard | remoto-jellymesh | Expected result |
|---|---|---|---|---|---|
| Sonda Senza ID (2026) | Same title and year, the runtime confirms | | 720p 1.9 | 1080p 3.8 | JellyMesh, standard as backup |
| L'Éclisse Fantasma / Eclisse Fantasma (2026) | Same after normalization | | 720p 1.9 | 1080p 3.9 | One title, JellyMesh, standard as backup |
| Titolo Omonimo (2024) and (2025) | Same title, different year | | 2024: 720p 1.9 | 2025: 720p 1.9 | Two separate films |
| Sonda Locale Senza ID (2026) | Local wins, the runtime confirms | 720p 2.0 | 1080p 4.0 | | Local |
| JM Copia Senza ID (2026) | IDs on one copy only, the runtime confirms | | 720p 1.9, with IDs | 1080p 3.9 | One title, JellyMesh, standard as backup |
| La La Sonda Muta / Sonda Muta (2026) | Equal only without articles, same runtime, a `.nfo` with a different plot | | 720p 1.9 | 1080p 3.9 | Two separate films, reported as a possible duplicate |
| Sonda Trama (2026) | Same title, year and runtime, a `.nfo` with a different plot | 720p 1.9 | 1080p 3.6 | | Both shown: the local copy hides nothing |

### Series

`JM Serie Divisa (2026)` has a `tvshow.nfo` with invented IDs (TMDb 999000101, TVDB 999000102). `Serie Senza ID (2026)` has none.

| Episode | locale | remoto-standard | remoto-jellymesh | Expected result |
|---|---|---|---|---|
| JM Serie Divisa S01E01-E02 | 1080p | | | Local |
| JM Serie Divisa S01E03 | 1080p | 720p | | Local |
| JM Serie Divisa S01E04 | | 720p | | Standard |
| JM Serie Divisa S01E05-E06 | | 720p | 1080p | JellyMesh, standard as backup |
| JM Serie Divisa S02E01-E02 | | | 720p | JellyMesh |
| Serie Senza ID S01E01 | 720p | | | Local |
| Serie Senza ID S01E02 | | 720p | 1080p | JellyMesh, standard as backup |

`JM Serie Doppia (2026)` has files with several episodes (decision 13a), with invented IDs (TMDb 999000201, TVDB 999000202). A file written `E01-E02` lasts 60 seconds, and each pair of numbers is one case.

| Episodes | locale | remoto-standard | remoto-jellymesh | Expected result |
|---|---|---|---|---|
| E01-E02 | Double, 1080p | Singles, 720p | | Local, both |
| E03-E04 | | Double, 720p | | Standard, one stub `S01E03-E04` |
| E05-E06 | | Double, 720p | Double, 1080p | JellyMesh, one stub `S01E05-E06`, standard as backup |
| E07-E08 | E07, 720p | Double, 720p | | E07 local, E08 standard on a plain stub that plays the double |
| E09-E10 | | Double, 720p | E10, 1080p | E09 standard on a plain stub, E10 JellyMesh with the double as backup |

## What 12.4 showed

Observed while building this environment, on Jellyfin 12.4.0.

- **JSON casing:** Jellyfin may answer in camelCase. Send `Accept: application/json; profile="PascalCase"` to get the casing of the OpenAPI schema.
- **Startup:** `/health` answers while the server still shows its "starting" page and returns 503 elsewhere. Wait for `/System/Info/Public` to return JSON.
- **Non-admin access:** `/Library/VirtualFolders`, `/ScheduledTasks` and `/System/Logs` return 403, so remote libraries must be listed with `/UserViews`. `/Users/New` is also 403.
- **Non-admin visibility:** `/Users` and `/System/Configuration` return 200, and `/Users` includes the admin account. The dedicated user can see more than its role suggests.
- **Direct stream:** the non-admin user gets `206` on `/Videos/{id}/stream?static=true` with a `Range` header.
- **Film names:** without a `.nfo`, `Name` keeps the year (`Sonda Senza ID (2026)`), while `ProductionYear` is correct. With a `.nfo` it is clean (`Tears of Steel`).
- **MKV bitrate:** MKV has no per-stream bitrate, so Jellyfin reports the container bitrate minus 192 kbit/s per audio track, and shows `192000` as the bitrate of every audio track, even a 128 kbit/s AAC. MP4 values are measured.
- **Episodes:** `ProviderIds` is empty on the test episodes, so identity has to come from the parent series plus season and episode numbers, as §4.1 expects.
- **Files with several episodes:** `S01E01-E02` sets `IndexNumberEnd` on a video file and on a `.strm`, and a list always carries it, whatever `Fields` asks for. The name becomes `Episodio 1 / Episodio 2`, and only one item exists. NextUp treats it as both: once it is played, the next item is `E03`.
- **`.nfo` of several episodes:** `<episodenumberend>` sets `IndexNumberEnd` even when the file name holds one number, and `<lockdata>` keeps it. Several `<episodedetails>` blocks work too, joining their titles. "Replace all metadata" on an unlocked item keeps `IndexNumber` but clears `IndexNumberEnd`, so the stub provider restores it, also on a stub whose file name holds one number.

These observations led to the three decisions recorded in §10 of the plan: a 5% bitrate tolerance, removing the year from `Name`, and using Jellyfin's video bitrate uncorrected.

## Spikes

Phase 0 tests live in `spikes/`, and their results in `docs/fase-0-esiti.md`. `stub_server.py` stands in for a `.strm` target and logs every request. Spikes write fixtures to `federata/`, so remove them afterwards. Jellyfin skips a completely empty library path and keeps its items, so leave a hidden file there until a scan has removed them.

The spikes for points 2 to 7 run on private throwaway containers, never on this stack. `private-instance.sh` starts one for `series-merge.sh` and `watch-state.sh`. `JellyMeshProbe/` is a test plugin, not the real one, built by `build-probe.sh`. `media-source-probing.sh` and `unavailable-title.sh` read `P3` and `P6`, a work folder holding `tests/Integration` and a copy of the spikes in `out/spikes`. `results.txt` and `unavailable-title-results.txt` are recorded runs.

## Proof scripts

Each folder proves a phase end to end with real Jellyfin servers, on private containers and with the current build. Run them from this folder. The media comes from `MEDIA`, by default this folder's `media/`, which is gitignored, so in a worktree point it at the main checkout. `dedup/` and `merge/` need clips that a media folder prepared earlier may lack, so run `prepare-media.sh` first; to keep new clips out of the shared stack's libraries, run it on a copy of the folder and point `MEDIA` there. Logins are fixed test values.

| Folder | Proves | Containers | Ports | Also needs |
|---|---|---|---|---|
| `materializer/` | Phase 4: stubs, metadata protection, watch state | `jm-mat` | 18310 | `remoto-standard` and `remoto-jellymesh` of this stack |
| `playback/` | Phase 5: the proxy, seek, audio and subtitle changes, in real jellyfin-web | `jm-play` | 18320, gates on 18321-18322 | `remoto-standard` and `remoto-jellymesh` of this stack, `python3` |
| `provider/` | Phase 7: two JellyMesh servers federated to each other | `jm-prov-a`, `jm-prov-b` | 18410-18411, gates on 18412-18414 | `remoto-standard` of this stack, `python3`, `WORK` |
| `dedup/` | Decisions 13 and 13a | `jm-dedup-*` | 18421-18424 | Nothing from this stack |
| `play/` | Decisions 11, 15 and 17: signed stubs, an upgrade, the local fallback, a local copy | `jm-play-local`, `jm-play-remote`, `jm-play-peer` | 18430-18432 | A checkout in `OLD`, from before signing |
| `ui/` | The configuration page in Chromium, at 1280 and 390 px | `$UI_PREFIX-*` | `UI_PORT` and the next three | Nothing from this stack |

### `materializer/`

Every scenario needs a fresh instance. `all.sh` does that for `s1` to `s5`, and writes `up-*.log` and `final-*.out` next to the scripts. Those hold what each scenario printed, so read them: the scripts print state and mostly do not judge it. To run one scenario:

```sh
bash materializer/up.sh           # a fresh jm-mat with the two-path libraries and the plugin
bash materializer/s1.sh           # s1 to s5, see the comment at the top of each
bash materializer/check.sh        # after s5: no remote address in the logs, the index or the folder
```

Remove it by hand, with `docker rm -f jm-mat` and `docker volume rm jm-mat-config jm-mat-cache jm-mat-fed jm-mat-extra`.

### `playback/`

This folder is the record of the Phase 5 proof more than a suite. The gates stand between `jm-play` and the two remotes, so a script can cut one off, and they must be up before `up.sh` adds the servers. Captures go to `run/`, and `extra/` is mounted into the container. Both are gitignored.

```sh
bash playback/gates.sh            # the gates, left running until killed
bash playback/up.sh               # jm-play with the plugin and the two remotes, then a sync
bash playback/direct1.sh          # the direct-play fixture, then direct3.sh
bash playback/final.sh            # the whole proof in order, then final2.sh, final3.sh and final4.sh
```

`final.sh` and `final3.sh` name the fixture by the item ID of the recorded run, and `leak.py` an item of its own, so check those IDs on a new run. Browser steps use real jellyfin-web in the Playwright image. Remove it by hand, with `pkill -f httpgate.py`, `docker rm -f jm-play` and `docker volume rm jm-play-config jm-play-cache jm-play-fed jm-play-pw`.

### `provider/`

`MEDIA` and `WORK` are required. The gates on the host carry all traffic between the servers, so a test can slow one down or cut it off. Scripts print `ok` or `FAIL`, and `leaks.sh` fails if it saw no tokens to look for.

```sh
export MEDIA=<main checkout>/tests/Integration/media WORK=<scratch folder>
bash provider/up.sh               # two servers with the plugin, each with the media of one remote
bash provider/federate.sh         # connects them, and b with the stock remote too
bash provider/boundaries.sh       # what a peer can reach, no cycles, shared libraries
bash provider/feed.sh             # the change feed, and polling on a stock remote
bash provider/limits.sh           # the stream limit, about 15 minutes because it waits for real timeouts
bash provider/network.sh          # a slow and an offline provider, after limits.sh
bash provider/leaks.sh            # no remote token or address in a client answer or a log
bash provider/down.sh             # removes containers and volumes, and the recorded tokens
```

### `dedup/`

Scratch files go in `WORK`.

```sh
bash dedup/up.sh                  # the two remotes and a consumer with the current build
bash dedup/run.sh                 # what merges, what stays apart, doubles, playback, NextUp, local copies arriving
OLD=<old checkout> bash dedup/upgrade.sh   # an index and stubs from the old build, then the current one
bash dedup/down.sh                # removes containers, volumes and network
```

### `play/`

`MEDIA` and `OLD` are required. `OLD` is a checkout of a commit before the signed URLs, made with `git archive` and `tar`. `WORK` defaults to `$TMPDIR/jm-play-run`. `all.sh` runs everything from fresh containers, in the order the steps need: `up`, `upgrade`, `endpoint`, `fallback`, `settings`, `localcopy`, `cycle`, `leaks`. Scripts print `ok` or `FAIL`, and `all.sh` exits non-zero if a step failed.

```sh
MEDIA=<main checkout>/tests/Integration/media OLD=<folder> bash play/all.sh
```

It leaves the containers up. `down` in `play/lib.sh` removes them and their volumes.

### `merge/`

Proves decision 20 on private containers named `jm-merge-*`, on ports 18441-18443:

```sh
bash merge/up.sh                  # the two remotes, a consumer, a chain of contradicting IDs
bash merge/run.sh                 # list, merge, restart, sync, a copy that changes or goes, undo, the page, a local copy
bash merge/down.sh                # removes containers, volumes, network and scratch files
```

### `ui/`

Checks the configuration page in real jellyfin-web, on private servers built from the checkout it runs in. `UI_PREFIX` and `UI_PORT` are required and name everything it makes, so several checkouts can run it at once if each has its own values. The ports are `UI_PORT` to `UI_PORT+3`. `MEDIA` defaults to the main checkout, and `WORK` to `$TMPDIR/$UI_PREFIX-work`. It takes about 2 minutes to start and 2 to check, and holds about 1.5 GB of memory while up, so stagger runs on a small Docker.

```sh
export UI_PREFIX=jm-ui-811 UI_PORT=18811
bash ui/up.sh                     # four servers, built from this checkout
bash ui/check.sh                  # every check in every state, non-zero on any failure
bash ui/check.sh errors keyboard  # only these scenarios, from the names at the end of pagecheck.py
bash ui/reinstall.sh              # after changing the page or the plugin: a new build, the state stays
bash ui/down.sh                   # removes containers, volumes, network and scratch files
```

| Server | Port | Media | What it shows |
|---|---|---|---|
| `fresh` | `UI_PORT` | `locale` | Plugin, consumer of the two below, a synced catalog, doubtful films, the connected server `jellymesh`, the free user `friend` |
| `stock` | `UI_PORT+1` | `remoto-standard` | No plugin |
| `peer` | `UI_PORT+2` | `remoto-jellymesh` | Plugin, shares its libraries |
| `first` | `UI_PORT+3` | `locale` | Plugin, nothing configured |

`check.sh` moves the servers through the other states and puts them back, and also repairs what an interrupted run left. To stay in one by hand, use `bash ui/state.sh offline` and `online` for a remote (`stock` by default), and `nokey` and `key` for the secret key (on `fresh` by default). Screenshots go to `$WORK/web/shots`, named after the state and the width, and the results to `$WORK/web/check.out`.

Selectors, button texts and the way to read an outcome live only in `uipage.py`, so a page that changes shape needs changes there. Confirmations are the Jellyfin dialog, read with `wait_confirm` and answered with `answer_confirm`, and a browser dialog counts as a script error. To look at the page some other way, write a script that imports `uipage` and run it with `pw` from `lib.sh`:

```sh
source ui/lib.sh && pw /path/to/script.py
```

## Not covered here

- **QSV (Phase 0, point 5):** this environment runs on an arm64 Mac with no Intel GPU, so the test needs the OPS pc.
- **Slow network, offline server, stream limit (§8):** belong to the Phase 6 task. To simulate an offline server, stop its service with `docker compose stop remoto-standard`.

## Licences

Source films are from the Blender Foundation under Creative Commons: Elephants Dream (CC BY 2.5), Sintel and Tears of Steel (CC BY 3.0). The media is generated locally and not committed.

## Hardening

`hardening/run.sh` puts a real server with the plugin in front of `hardening/hostile.py`, a fake remote that sends huge bodies, redirects to another host, stalls after its headers and answers with poisoned URLs. It proves each case with what reached a second port standing for a third host (nothing), the memory of a container capped at 1500 MB, and a log at the most verbose level. `hardening/proofs.sh` runs the proofs in `provider/` on private servers to show the limits leave real playback alone, and `hardening/auth-statuses.sh` shows which statuses a real server gives, which is what decides when a new login is due. All of them use `jm-hard-*` containers on ports 18450-18459 and remove them at the end.
