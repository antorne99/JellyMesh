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

Copy `.env.example` to `.env` to set `BIND_ADDRESS`. Ports bind to loopback by default, so set `BIND_ADDRESS` to test from a phone or TV.

## Folders

- `media/<instance>/film` and `media/<instance>/serie` are mounted read-only at `/media`.
- `federata/` is mounted writable at `/federata` on `locale` only. It stands in for the federated folder and is empty until something writes to it.
- On `locale`, the `Film` library has the paths `/media/film` and `/federata/Film`, and `Serie` has `/media/serie` and `/federata/Serie`. These are the two-path libraries the Phase 0 spikes need. The plugin writes stubs only into `/federata/<server id>/Film` and `/Serie`, made when a server is added, so run `provision.sh` again after adding servers: it adds those folders to the libraries.

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

Phase 0 tests live in `spikes/`. `stub_server.py` stands in for a `.strm` target and logs every request. Spikes write fixtures to `federata/`, so remove them afterwards. Jellyfin skips a completely empty library path and keeps its items, so leave a hidden file there until a scan has removed them.

The spikes for points 2 to 7 run on private throwaway containers, never on this stack. `private-instance.sh` starts one for `series-merge.sh` and `watch-state.sh`. `JellyMeshProbe/` is a test plugin, not the real one, built by `build-probe.sh`. `media-source-probing.sh` and `unavailable-title.sh` read `P3` and `P6`, a work folder holding `tests/Integration` and a copy of the spikes in `out/spikes`. `results.txt` and `unavailable-title-results.txt` are recorded runs.

## Proof scripts

Each folder proves a phase end to end with real Jellyfin servers, on private containers and with the current build. Run them from this folder. The media comes from `MEDIA`, by default this folder's `media/`, which is gitignored, so in a worktree point it at the main checkout. `dedup/` and `merge/` need clips that a media folder prepared earlier may lack, so run `prepare-media.sh` first; to keep new clips out of the shared stack's libraries, run it on a copy of the folder and point `MEDIA` there. Logins are fixed test values.

| Folder | Proves | Containers | Ports | Also needs |
|---|---|---|---|---|
| `materializer/` | Phase 4: stubs, metadata protection, watch state | `jm-mat` | 18310 | `remoto-standard` and `remoto-jellymesh` of this stack |
| `playback/` | Phase 5: the proxy, seek, audio and subtitle changes, in real jellyfin-web | `jm-play` | 18320, gates on 18321-18322 | `remoto-standard` and `remoto-jellymesh` of this stack, `python3` |
| `provider/` | Phase 7: two JellyMesh servers federated to each other | `$PREFIX-a`, `$PREFIX-b` (default `jm-prov`), `$PREFIX-s` with `STOCK=own` | `PORT_A` and the next one (default 18410-18411), gates on the next three | `remoto-standard` of this stack unless `STOCK=own`, `python3`, `WORK` |
| `dedup/` | Decisions 13 and 13a | `jm-dedup-*` | 18421-18424 | Nothing from this stack |
| `play/` | Decisions 11, 15 and 17: signed stubs, an upgrade, the local fallback, a local copy. `watchstate.sh`: decision 41. `keyfile.sh`: decision 42. `last-served.sh`: test 16 of the folders per server (D15) | `$PREFIX-local`, `$PREFIX-remote`, `$PREFIX-peer` (default `jm-play`) | `PORT` and the next two (default 18430-18432) | A checkout in `OLD`, from before signing |
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

To run beside another stack, give every script the same `PREFIX` and `PORT_A`: the servers are `$PREFIX-a` and `$PREFIX-b` on `PORT_A` and `PORT_A+1`, and the gates listen on `PORT_A+2` to `PORT_A+4`. The stock remote is the `remoto-standard` of this stack on `STOCK_PORT` (default 8097). With `STOCK=own`, `up.sh` starts a private one instead, `$PREFIX-s` on `PORT_A+5` unless `STOCK_PORT` says otherwise, and `down.sh` removes it. `down.sh` stops only the gates on this run's ports. `peer-device.sh` makes one server of its own, with defaults `jm-q92` and 8280.

Stubs go to a folder per remote server, `/federata/<server id>/Film` and `/Serie`, so `federate.sh` adds the folders of each server it connects to the libraries, as an admin would. The limit tests need `a` to win ties against the stock remote, which falls to the server ID. A remote added back takes back its ID, so when the draw goes the wrong way, `federate.sh` makes the private stock remote of `STOCK=own` again, with a new Server Id, and draws once more; with the shared one it stops and says so. The `Film` library keeps `/media/film` and `/extra/film`, so it holds local copies and stays one a peer can be given.

```sh
export MEDIA=<main checkout>/tests/Integration/media WORK=<scratch folder>
export PREFIX=jm-prov-2 PORT_A=18510 STOCK=own   # optional: anything else that runs at the same time
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

`MEDIA` and `OLD` are required. `OLD` is a checkout of a commit before the signed URLs, made with `git archive` and `tar`. `PREFIX` (default `jm-play`) names the containers and volumes, `PORT` (default 18430) is the port of `local`, and `remote` and `peer` take the next two. `WORK` defaults to `$TMPDIR/$PREFIX-run`. Every script in this folder reads `PREFIX` and `PORT`, with its own defaults where it makes its own containers, and `provider/` reads `PREFIX` too, so set them per command rather than exporting them. `all.sh` runs everything from fresh containers, in the order the steps need: `up`, `upgrade`, `endpoint`, `fallback`, `settings`, `localcopy`, `cycle`, `leaks`. Scripts print `ok` or `FAIL`, and `all.sh` exits non-zero if a step failed.

```sh
MEDIA=<main checkout>/tests/Integration/media OLD=<folder> [PREFIX=jm-play] [PORT=18430] bash play/all.sh
```

It leaves the containers up. `down` in `play/lib.sh` removes them and their volumes.

`up` installs the `OLD` build, which writes the flat layout into `/federata/Film`. `upgrade` checks that the current build keeps and signs those stubs and writes a stub of its own for each film into the folder of the server, `/federata/<server id>/Film`, with the server name in the file name; then it moves the `Film` library from the flat path to that folder, where the later steps play. The other scripts add the folder of each server to their libraries after adding it.

`removal-series.sh` proves the promise of the removal dialog on a series, with its own `$PREFIX-local` and `$PREFIX-remote` containers (default `jm-rms`) on `PORT` and the next one (default 18470). Two local users watch, half watch and mark favourite episodes of a series with provider IDs and one without. The server is removed and added back, and every episode and series row keeps the same item ID and the same watch state. Jellyfin keeps the user data of removed items by key, so the promise holds as long as stub paths and the IDs in the `.nfo` stay the same. A server added back is known by its Server Id and takes back its ID, so its stubs come back in the same folder, still in the library, with the same label (decisions 116 and 117); the script compares the full paths, checks that the add says which server it recognized, and that adding the server once more while it is configured is refused (decision 120). It needs only `MEDIA`, and `KEEP=1` leaves the containers up.

```sh
MEDIA=<main checkout>/tests/Integration/media bash play/removal-series.sh
```

`watchstate.sh` proves decision 41 on its own, with fresh `$PREFIX-local` and `$PREFIX-remote` containers and the current build. It needs only `MEDIA`. A film watched or stopped on the consumer changes no user's watch state on the remote, and a film the dedicated user and another remote user watch stays unwatched on the consumer after a sync. The clips are shorter than the 300 seconds Jellyfin needs to keep a resume point, so a film stopped halfway counts as watched.

```sh
MEDIA=<main checkout>/tests/Integration/media bash play/watchstate.sh
```

`collections.sh` proves that films a remote groups into collections still reach the consumer, with its own `$PREFIX-local` and `$PREFIX-remote` containers (default `jm-col`) on `PORT` and the next one (default 18480). The stock remote turns on "Group movies into collections" (`EnableGroupingMoviesIntoCollections`) and puts two films in a collection. Asked the sync query as the dedicated user, Jellyfin 12.4 answers the collection in place of both films, and one fewer in `TotalRecordCount`, unless the query carries `collapseBoxSetItems=false`. With it, the consumer gets a stub for each film and none for the collection. It needs only `MEDIA`, and `KEEP=1` leaves the containers up.

```sh
MEDIA=<main checkout>/tests/Integration/media bash play/collections.sh
```

`keyfile.sh` proves decision 42. A consumer on 0.3.0.0, built from the `v0.3.0.0` tag with `git archive`, has `JELLYMESH_SECRET_KEY` set. It is upgraded to the current build with the variable still set, then started again without it, then its key file is deleted. The saved password moves to the key file once, and the stubs stay the same. Once the file is lost, the server asks for its password, and the federation works again after the password is entered. The saved password is read from the plugin XML, because the API never shows it.

```sh
MEDIA=<main checkout>/tests/Integration/media bash play/keyfile.sh
```

`last-served.sh` proves that the last server that served a title is kept per user and stub across a restart (D15). Two stock remotes have the same film, so its stub has a winner and a backup. One user plays it with the winner stopped and another with both up; after a restart of the consumer, the file in the plugin data folder still names the backup for the first and the winner for the second, and holds no address, token or remote item. It makes its own containers, `$PREFIX-local`, `$PREFIX-a` and `$PREFIX-b` (default `jm-last`, on `PORT` and the next two, default 18436), and removes them at the end unless given `--keep`.

```sh
MEDIA=<main checkout>/tests/Integration/media [PREFIX=jm-last] [PORT=18436] bash play/last-served.sh
```

### `merge/`

Proves manual merges on the per-server layout (decision 20, Q23), on two stock remotes and a consumer whose Film library holds both server folders. Copies of one server keep one stub, which takes the state of the other; copies of two servers and a local copy become versions, linked as soon as the merge is saved; a new stub is linked by the scan that makes its item (Q145). The containers are `$PREFIX-local`, `$PREFIX-remote` and `$PREFIX-peer` (default `jm-merge`, on `PORT` and the next two, default 18441). Checks marked `PENDING (row 144)` wait for the thorough detach that dissolves a group after an undo.

```sh
export MEDIA=<main checkout>/tests/Integration/media [PREFIX=jm-merge] [PORT=18441]
bash merge/up.sh                  # the two remotes, a consumer, a chain of contradicting IDs
bash merge/run.sh                 # list, merge, restart, a copy that changes or goes away, a new stub, undo, the page, a local copy
bash merge/down.sh                # removes containers, volumes and scratch files
```

### `ui/`

Checks the configuration page in real jellyfin-web, on private servers built from the checkout it runs in. `UI_PREFIX` and `UI_PORT` are required and name everything it makes, so several checkouts can run it at once if each has its own values. The ports are `UI_PORT` to `UI_PORT+3`. `MEDIA` defaults to the main checkout, and `WORK` to `$TMPDIR/$UI_PREFIX-work`. It takes about 2 minutes to start and 20 to check, and holds about 1.5 GB of memory while up, so stagger runs on a small Docker.

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

`check.sh` moves the servers through the other states and puts them back, and also repairs what an interrupted run left. To stay in one by hand, use `bash ui/state.sh offline` and `online` for a remote (`stock` by default), `nokey` to delete the credential key file (on `fresh` by default), `blockkey` to put a folder where the key file goes (on `first` by default), and `key` to fix either, entering the logins again. Screenshots go to `$WORK/web/shots`, named after the state and the width, and the results to `$WORK/web/check.out`.

The browser runs on the private network of these servers and reaches them by container name. Through `host.docker.internal` a few new connections stayed in `SYN_SENT` for 7 to 45 seconds on Docker Desktop while the server answered others in milliseconds, which failed about one run in three with `urlopen timed out`.

Selectors, button texts and the way to read an outcome live only in `uipage.py`, so a page that changes shape needs changes there. Confirmations are the Jellyfin dialog, read with `wait_confirm` and answered with `answer_confirm`, and a browser dialog counts as a script error. To look at the page some other way, write a script that imports `uipage` and run it with `pw` from `lib.sh`:

```sh
source ui/lib.sh && pw /path/to/script.py
```

The page has five tabs, so most of it is hidden at any moment. Reading text or a value works on a hidden tab, while `uipage.py` makes `click`, `fill`, `focus`, `is_visible`, `select_option`, `bounding_box` and `scroll_into_view_if_needed` open the tab of their element first, so a check does not have to name tabs. `page.evaluate` opens nothing: use `u.reveal(node)` or `u.show_tab(page, name)` before measuring what is on screen, and `contrast` in `pagecheck.py` for a selector across all tabs. `Session.open(tab="catalog")` opens the page on a tab, and the `tabs` scenario checks the tabs themselves.

Servers and connected servers are expandable rows. The same actions also open the rows and parts that hold their element, except `is_visible`, which reports what a person sees. Save appears only with a change, so a check that saves calls `u.change_settings(page)` first if it has changed nothing.

What Jellyfin 12.4 was observed to do with these tabs:

- `LibraryMenu.setTabs` draws them in the Dashboard header at every width, as router links with `role="tab"`, arrow keys and Enter, but not Space. Without `href` a click leaves for `/configurationpage` with no plugin name and ends on Home.
- An `href` with another query, such as `&tab=catalog`, loads a second copy of the page and hides the first with its edits. A hash after the query, as in `#/configurationpage?name=JellyMesh#catalog`, keeps the page loaded, adds a step to the browser history, and survives a reload and a return.
- The router pushes that address without an event, while Back and Forward fire `popstate` and `hashchange`.
- The header empties itself when the next page appears, and tabs must be set again at each `pageshow`. Calling `setTabs(null)` at `pagehide` runs after the next page set its own, and removes the tabs of Home.

## Not covered here

- **QSV (Phase 0, point 5):** this environment runs on an arm64 Mac with no Intel GPU, so it cannot test QSV. Antonio verified it on the OPS pc: it works.
- **Slow network, offline server, stream limit (§8):** belong to the Phase 6 task. To simulate an offline server, stop its service with `docker compose stop remoto-standard`.

## Licences

Source films are from the Blender Foundation under Creative Commons: Elephants Dream (CC BY 2.5), Sintel and Tears of Steel (CC BY 3.0). The media is generated locally and not committed.

## Hardening

`hardening/run.sh` puts a real server with the plugin in front of `hardening/hostile.py`, a fake remote that sends huge bodies, redirects to another host, stalls after its headers and answers with poisoned URLs. It proves each case with what reached a second port standing for a third host (nothing), the memory of a container capped at 1500 MB, and a log at the most verbose level. `hardening/proofs.sh` runs the proofs in `provider/` on private servers to show the limits leave real playback alone, and `hardening/auth-statuses.sh` shows which statuses a real server gives, which is what decides when a new login is due. All of them use `jm-hard-*` containers on ports 18450-18459 and remove them at the end.

## Scale

`scale/run.sh` measures a consumer against a stock remote with a large synthetic catalog and no media: `scale/fake_remote.py` answers the calls the plugin makes (`/System/Info/Public`, `/Users/AuthenticateByName`, `/Users/Me`, `/UserViews`, paged `/Items` by type, by IDs and by `minDateLastSaved`, and image requests with a tiny JPEG). It builds each item from its number, by default 5,000 films and 500 series of 5 seasons × 20 episodes, about 3-5 kB of JSON each with provider IDs, one media source with five streams, people and chapters. One Jellyfin 12.4 with the plugin (`jm-scale-consumer`, port 18460) reads it from the host (port 18461), then the script runs a first sync, an incremental sync with no changes and a full sync with no changes, and `scale/analyze.py` cuts each one into read, index save and materialization from the sync status, the fake's request log, the plugin's log lines and the index file's mtime. Memory is sampled every second with `docker stats` and the process's `VmRSS`, and `VmHWM` gives the true peak. It takes about 25 minutes and removes its container and volumes at the end (`KEEP=1` keeps them).

```sh
./scale/run.sh                          # images on, results in ${TMPDIR:-/tmp}/jm-scale-run/results.txt
SCALE_IMAGES=0 WORK=/tmp/off ./scale/run.sh   # no image tags, so no downloads: the difference is their cost
SCALE_FILMS=100 SCALE_SERIES=10 ./scale/run.sh  # a quick check of the harness
SCALE_SCAN=1 ./scale/run.sh             # also links the folder to two libraries, scans, and syncs again
```

`SCALE_IMAGE_BYTES` and `SCALE_IMAGE_DELAY_MS` make the images heavier or slower, to stand for real posters over a real link. The fake answers in under a millisecond, so every time measured is the consumer's; a real 12.4 remote is slower to page through 50,000 episodes. `scale/results-before-846.txt` holds the run of 2026-11-09 at 248abe4, before phase 846. In short, for 55,000 stubs: the read takes 13-16 s and is not the problem; the first materialization takes 98 s without images and 442 s with 61,000 of them; an incremental sync with nothing to do takes 3-6.5 minutes, and nearly 10 once the stubs are in a library; the process peaks at 2.0-2.4 GB; the index file is 182 MiB and is rewritten on every incremental sync. Stack samples taken with `scale/stacks.sh` (`dotnet-stack` from a container sharing the consumer's PID namespace) put 13 of 16 samples of the incremental in the per-stub list rebuild of `LibraryMaterializer.WriteStubAsync`. The scan of the linked folder is Jellyfin's own work, with no plugin frame in its samples, and was stopped at 92% after 58 minutes. `scale/results.txt` holds the runs after phase 846, which updates stub entries in place, looks up a stub's library item only when something has to reach it, reads remote images four at a time for each server and leaves the index file alone when an incremental sync brings no change. For the same 55,000 stubs the first sync takes 43 s with images, an incremental sync with nothing to do 6 s and a full read with no changes 12 s; the process peaks at 1.4-2.0 GB. A second run with 5,500 stubs, images that take 20 ms each and the folder scanned into libraries reads 6,100 images in 42 s instead of about 156 s one at a time, and its incremental sync on scanned stubs takes 0.5 s.
