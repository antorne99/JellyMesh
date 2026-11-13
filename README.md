<p align="center"><img src="assets/jellymesh.png" alt="JellyMesh logo" width="480"></p>

# JellyMesh

JellyMesh is a Jellyfin plugin that federates Jellyfin servers. Films and series from other servers appear in your existing libraries, next to your own, and play through your server. Your users only ever connect to your server.

**Status: pre-release.** It runs on Jellyfin 12.4 and has been tested against real 12.4 servers with jellyfin-web, Android and TV clients, and QSV. The versions added in 0.5 have been tested on real 12.4 servers; how the Android TV and Swiftfin apps show them has only been read from their source. See [Limits](#limits).

Upgrading from 0.4? Read [Upgrading from 0.4](#upgrading-from-04) first: 0.5 moves every server into its own folders.

## What it does

- Reads the film and series libraries you choose on other Jellyfin servers. Those servers can be stock Jellyfin.
- Gives each server its own `Film` and `Serie` folders inside a federated folder you choose. You add them to your libraries, or let JellyMesh create a library for each server, as described in [Server folders and libraries](#server-folders-and-libraries).
- Shows a title once in each library, with the copies of every server and your own copy as versions. A local copy is the primary version; otherwise the best quality is, compared by resolution, then HDR, video bitrate and codec, with bitrates within 5% counted as a tie. See [Versions](#versions).
- Lets an admin merge films and series it can't match safely, as described in [Merging duplicates by hand](#merging-duplicates-by-hand).
- Completes series per episode: in a series library that holds your series and the `Serie` folders of the servers, episodes you lack appear inside your own series.
- Writes a `.strm`, a `.nfo` and local images for each title, and a `season.nfo` and poster for each season, inside the federated folder. Remote YouTube trailers and intro and credits segments come along, and so do remote collections if you turn them on.
- At playback, sends the real client's device profile to the remote, which decides between direct play and transcode and does any transcoding on its own hardware. Your server proxies the stream.
- Falls back when a remote cannot serve, as described in [Stream limit and fallback](#stream-limit-and-fallback).
- Keeps watch state on your server.
- Keeps a history of its syncs and writes its events to Jellyfin's activity log, as described in [Sync history and activity log](#sync-history-and-activity-log).
- Shows its page in the display language set in Jellyfin. English and Italian are available, and any other language shows English.

## What it doesn't do

- It does not copy media files. Only images are stored locally, and playback needs the remote to be reachable.
- It does not handle anything but films and series.
- It writes files only inside the federated folder. It never renames or deletes a library it did not create, and it changes the folders of your libraries only when you ask it to: the [automatic libraries](#automatic-libraries) of a server, the [automatic migration](#upgrading-from-04) from 0.4, and **Add them automatically** for servers you connect later. Each of these adds or removes only JellyMesh's own folders and leaves other folders and settings alone.
- It does not sync watch state to other servers.
- It does not set up Tailscale, and it cannot tell whether a URL is on your tailnet. Using only tailnet URLs is up to you.
- It does not use API keys, and it refuses an administrator account on a remote.
- It does not import items that are themselves federated on a remote, so servers can't loop.

## Roles

Both roles are active on every server with the plugin.

**Consumer.** Reads the catalogs of remote servers, merges them with your own, and plays their titles. It works against stock Jellyfin through the official API, polling for changes every 15 minutes by default, with a full reconciliation each night at 03:00.

**Provider.** Shares chosen libraries with another JellyMesh server. For each connected server you pick a dedicated local user, the libraries it can read and how many streams it can hold at once. On top of what a stock remote offers, the other server gets a change feed instead of polling, and titles that are themselves federated here are left out. A library that holds only federated folders, such as the automatic library of a server, is never offered. Read [Trust between servers](#trust-between-servers) before you rely on these limits.

## Requirements

- Jellyfin 12.1 or a later 12.x. The plugin is compiled against the 12.1 packages and the manifest allows any server from 12.1. It has been tested on 12.4; on 12.1 it has only been checked to install and load.
- A tailnet between your servers. Tailscale is managed outside the plugin.
- `EnableAutomaticSeriesGrouping` on in every series library that holds a server's `Serie` folder, so that a local series, its remote episodes and the same series from other servers merge. In the Dashboard it is called "Automatically merge series that are spread across multiple folders". The plugin page warns if it is off and does not change it for you. The automatic libraries are created with it on.
- A dedicated non-admin user on each remote.

## Install

### From a catalog

Add this plugin repository in the Dashboard, install JellyMesh from the plugin catalog, and restart Jellyfin:

```
https://raw.githubusercontent.com/antorne99/JellyMesh/main/manifest.json
```

### By hand

Download `jellymesh_<version>.zip` from a [release](https://github.com/antorne99/JellyMesh/releases) and compare it with the `.sha256` file next to it. Unpack the zip into a folder in Jellyfin's `plugins` folder, keeping `meta.json`, and restart Jellyfin. For the Docker image that folder is:

```
/config/plugins/JellyMesh_<version>/
```

You can also build the plugin and install it into a running Jellyfin container. This needs Docker and `jq`, builds in the .NET SDK image and restarts the container:

```sh
tests/Integration/scripts/install-plugin.sh <container>
```

## Docker Compose

An example, with the federated folder mounted:

```yaml
services:
  jellyfin:
    image: jellyfin/jellyfin:12.4
    environment:
      JELLYMESH_FEDERATED_ROOT: /federata # optional, see Setup
    volumes:
      - ./config:/config
      - ./cache:/cache
      - /your/media:/media:ro
      - /your/federated:/federata
```

Mount the federated folder at one path only, and add its folders to your libraries with that path. JellyMesh follows symbolic links, but it cannot see bind mounts: a second mount of the same folder looks like an ordinary folder. See [Limits](#limits).

## The credential key

Remote credentials are encrypted before they are saved, with a key that JellyMesh makes by itself at its first start: a random file named `credential.key` in its data folder. In the Docker image that is `/config/plugins/Jellyfin.Plugin.JellyMesh/`. On Linux and macOS only its owner can read it. On Windows it gets its own access list, which lets in only the account running Jellyfin, SYSTEM and Administrators and ignores the permissions of that folder. The access list is checked by unit tests, but applying it has not been tried on a Windows server yet. There is nothing to set.

**Back up the key file, separately from the Jellyfin config.** Without it the saved passwords can't be decrypted.

If the file is lost or replaced, JellyMesh suspends sync and remote playback and marks each server whose password no longer decrypts. Enter the login of each marked server again on its card, and the federation works again. Titles already synced stay in your libraries meanwhile, but won't play. If JellyMesh cannot keep the file private, for example because its data folder is read-only or Windows refuses its access list, it saves and uses no credentials and shows a warning on its page.

**Upgrading from 0.3.** Earlier versions read the key from `JELLYMESH_SECRET_KEY`. At the first start of the new version with the variable still set, JellyMesh moves the saved passwords to the key file, once. After that the variable is no longer used, and the plugin page says you can remove it. If you remove it before upgrading, enter the login of each server again.

## Setup

All of this is on the plugin page, under Dashboard > Plugins > JellyMesh, unless it says otherwise.

### Connect a stock remote

1. On the remote, create a dedicated non-admin user that can see the libraries you want. Leave its transcoding permission on, so that the remote can transcode for you. If you want the remote's collections, let it see the remote's Collections library too: without it JellyMesh reads no collections, and reports no error. Use it for one server only.
2. On your server, make the federated folder writable by Jellyfin. In the Compose example above that is `/federata`.
3. Under **Settings**, enter the path in **Federated folder** and save. It must be an absolute path, and not the filesystem root. If `JELLYMESH_FEDERATED_ROOT` is set, skip this step: at every start the plugin uses that path, and the field is read-only. A value that isn't usable is ignored with a warning in the log. If you remove the variable, the last path stays and the field can be edited again.
4. Under **Add a server**, enter a name, the tailnet URL of the remote, and the dedicated user and password, then choose **Connect and choose libraries**. The remote must be online, and an administrator account is refused. A remote that is already configured is refused too, and the message names it. The name shows in the version list, so pick one your users will recognize.
5. The server's card opens with the libraries read from it. Tick what to import and choose **Save libraries**. Nothing is imported until you do. To change the choice later, choose **Libraries** on the card.
6. JellyMesh creates the server's folders: `<federated folder>/<id>/Film` and `<federated folder>/<id>/Serie`, where `<id>` is the server's ID in JellyMesh. The server's card shows the exact paths.
7. Link them, in one of two ways:
   - In the Dashboard, add the `Film` folder to a film library and the `Serie` folder to a series library by hand.
   - On the server's card, under **Server libraries**, tick **Create a Film and a Serie library for this server**. See [Automatic libraries](#automatic-libraries).

   The plugin page warns until the folders are linked.
8. Under **Catalog**, choose **Sync now**, or wait for the next sync.

After a sync that changes anything, JellyMesh asks Jellyfin to scan the libraries, so titles appear once that scan ends.

To stop reading a server, choose **Remove** on its card. Titles found only there leave your libraries right away, with their images, and the server's folders and automatic libraries are deleted. Titles also on another server stay and play from it. Its copies leave the titles merged by hand, and a merge left with one copy is undone; adding the server back does not merge them again. Nothing changes on the remote or in your local files. If you add the same remote back later, JellyMesh recognizes it by its Jellyfin server ID and gives it back its folder and its name in file names. Only a removal from this page deletes titles: if a server goes missing from the settings in any other way, such as a reset settings file, its titles stay in your libraries, cannot play, and the plugin page warns you. Restore the settings or add the server again to bring them back.

### Connect a JellyMesh remote

The other server must let you in first, or it looks like a stock remote and you get none of the extras.

1. On the other server, create a dedicated non-admin user for your server.
2. On the other server's plugin page, under **Connected JellyMesh servers**, choose that user, tick the libraries to share, set the stream limit and choose **Connect**.
3. On your server, follow the steps for a stock remote with that user. Mode shows `JellyMesh` under **Remote servers**.

If you connect both ways, each server needs its own dedicated user on the other.

Once a user is connected as a server, it can no longer use that server from an app or the web client. The login still works, but every other call gets 403: such a user can reach only what federation needs, that is the JellyMesh endpoints, playback, and images and subtitles of the shared titles. To watch the other server directly, use a separate personal account there.

### Server folders and libraries

Each server has its own `Film` and `Serie` folders, holding only its titles. A file name ends with the server's name, such as `Title (2020) - Home.strm`, and that name is what the version list shows. If two servers have the same name, the one added later gets the first 8 characters of its ID after the name.

Where you add the folders decides what your users see:

- A library that holds the folders of several servers, and your own media, shows each title once, with all those copies as versions.
- A library that holds the folders of one server shows only that server's titles.
- Put the `Serie` folders of all servers into the same libraries. A series on two servers whose `Serie` folders are in different libraries stays split, and the plugin page warns about it.
- Never add the whole federated folder, or a server's folder above `Film` and `Serie`, to a library. The versions of a title come apart and the server libraries empty. The plugin page warns about it.

The plugin page also tells you when a library was added over the federated folder or left it since the last sync. The next sync links the versions again.

### Automatic libraries

Each server has a switch on its card, off by default: **Create a Film and a Serie library for this server**. When on, JellyMesh creates two libraries, `<server name> - Film` and `<server name> - Serie`, holding the server's folders. If a name is taken, it adds the first 8 characters of the server's ID; if that is taken too, the library is not created and the page says so. The libraries use your server's default metadata settings, with series merging on, and with **Automatically add to collection** on when **Write remote collections into films** is on.

- JellyMesh renames and deletes only the libraries it created this way. A library you rename in the Dashboard, keeping its folder and type, stays JellyMesh's. If you change its folder or type, JellyMesh lets it go, the page lists it, and it is yours to keep or delete.
- Permissions stay with you. Users with access to only some libraries do not see these until you enable them for those users in Dashboard > Users.
- If one of them goes missing, **Recreate missing libraries** brings it back.
- Turning the switch off asks first, then deletes the server's automatic libraries. Removing the server deletes them too.
- Another library on the same folders shows the titles twice, and the page warns about it.

### Servers connected later

Under **Settings**, **New servers** decides what happens to the folders of a server you connect later. **Add them by hand** leaves them to you. **Add them automatically** adds them to every library that holds the folders of another server, except the automatic libraries of other servers. It starts as **Add them automatically** after an automatic migration, and as **Add them by hand** otherwise.

### Renaming a server

Choose **Rename** on a server's card. The new name shows in the version list and in the names of the server's automatic libraries; its folder stays the same. JellyMesh renames the server's files and carries the watch state to them.

- The libraries are scanned twice before the rename is done. Until then the titles of this server may show twice among the versions.
- Its automatic libraries are renamed too, and JellyMesh updates the users allowed or blocked on them. Each one empties and fills again, and its sort order and view start over. On Jellyfin 12.1 its place in each user's home screen order and exclusions starts over too.
- A renamed library keeps its old ID until Jellyfin restarts: until then, users who open it by that ID get an error. Restart Jellyfin when you can.
- If the rename stops halfway, the card says so: rename the server again with the same name to finish it. **Cancel the pending rename** keeps the new name for the titles, deletes their old files at once, and loses the watch state not yet carried.

### Settings

| Setting | Default | Range |
|---|---|---|
| Sync interval | 900 s | At least 300 s |
| Nightly reconciliation time | 03:00 | |
| Protect metadata | On | |
| Close the transcode of an expired stream | On | |
| Wait for a remote to answer | 10 s | 3 to 60 |
| Wait for each step of opening a stream | 15 s | 5 to 60 |
| Close an idle remote transcode after | 90 s | 30 to 109 |
| Keep paused sessions | 24 h | 1 to 168 |
| Remember the transcoding rights of a remote | 5 min | 1 to 60 |
| Write remote collections into films | Off | |
| New servers: folders of a server linked later | See [Servers connected later](#servers-connected-later) | |

**Protect metadata** locks federated items, so metadata fetchers and "Replace all metadata" leave them alone. When off, `.nfo` files hold less, fetchers complete the rest, and titles without provider IDs can be matched to the wrong film.

**Close the transcode of an expired stream** is for the servers connected to yours. When a stream has no activity for 2 minutes its slot is freed, and this closes its transcode at that moment. When off, the slot is still freed and Jellyfin's own timer closes the transcode. It applies from the next expiry, without a restart. A dropped connection closes the transcode either way.

**Write remote collections into films** puts the collection a film has on its server into its `.nfo`. Jellyfin groups the films only in libraries with **Automatically add to collection** turned on. The dedicated user on a stock remote must see that remote's Collections library, and a JellyMesh remote sends its collections only from 0.5. A collection with the same name as one of yours fills yours. Jellyfin adds films to collections but never takes them out, so after a collection changes on a remote, or a film leaves, remove old or empty collections by hand. Turning this off keeps the collections already made.

### Sync history and activity log

Under **Catalog**, **Sync history** lists the last 50 syncs and the last 7 nightly reconciliations: start, duration, kind and outcome, and for each server the titles read, created, updated and removed, and the errors. JellyMesh also writes to Jellyfin's activity log when a server goes offline or is reachable again, when a sync fails, when saved passwords no longer decrypt, and once for each completed sync: about 96 entries a day at the default interval. Neither shows server addresses or tokens.

## Versions

Inside a library, a title shows once. Its versions are the copies of every server, and your local copy, that the user can see; a user is never offered a version in a library they cannot open. The version list names each server copy by its server.

- **The primary version** is the same for everyone: the local copy, otherwise the best quality. It changes only when a local copy arrives or leaves, or when its server is removed.
- **The version that starts** when the client picks none is the local copy if the user can see it, otherwise the best version the user can see. A version the client picks always wins among those the user can see.
- **Watch state:** "watched" is one for the whole title, on every version and in every library. The resume position stays with the version that was played.
- **Series** don't become versions. Jellyfin merges the same series from several servers when their `Serie` folders are in the same series library, with series merging on. Episodes become versions like films, but only when they cover the same episodes: a double episode in one file on one server and two separate episodes on another stay separate.
- **JellyMesh keeps the links.** If an admin splits versions apart in Jellyfin (in Italian, "Dividi versioni") or merges versions there, JellyMesh links them back at the next sync. To separate copies you merged, use **Undo merge** on the plugin page. A link lost while nothing else changed comes back at the nightly reconciliation or at the next change.

See [Limits](#limits) for what Jellyfin shows of versions in collections, "Latest" and "Continue watching".

## Merging duplicates by hand

Films and series that JellyMesh cannot safely match, such as copies with contradicting provider IDs or the same title and year with nothing to confirm them, stay separate and are listed in the **Duplicate films** and **Duplicate series** sections of the plugin page. An admin can select copies and choose **Merge selected**: the copies become versions of one title, and a local copy is still the primary version, otherwise the best quality is. A series is merged episode by episode, and each episode starts from its best copy. When two titles of the same library become one, the favourite, rating and play count move to the primary version, and "watched" is aligned; the resume position is not touched. For series this happens per episode, and the favourite and rating of the whole series don't move. The choice survives restarts and syncs.

**Undo merge** separates the copies again. The title that stayed keeps all the watch state. A separated copy usually gets back what it had before the merge, but it can start with no watch state, for instance two copies with the same title and no provider IDs. Use **Undo merge** and not Jellyfin's own split of versions, which JellyMesh undoes at the next sync.

Two local films, or two local series, can't be merged, so such cases aren't listed. Merges belong to one server and don't travel to other JellyMesh servers. Removing a server takes its copies out of the merges, and a merge left with one copy is undone. Two servers' copies of a merged series may still show as two rows in a library, because each server keeps its own series IDs.

## Upgrading from 0.4

0.5 needs Jellyfin 12.1 or later: upgrade Jellyfin first if you are on 12.0.

Up to 0.4 every server shared one `Film` and one `Serie` folder. At its first start 0.5 writes the new folders of each server next to the old ones, and the plugin page shows a banner, **Choose how to move to the folders of each server**, with two choices and no default. Until the folders of each server are in your libraries, the old titles stay as they are and keep playing, but no new titles arrive, for as long as you need.

- **Migrate automatically.** For each library that holds an old `Film` or `Serie` folder, JellyMesh adds the folders of the same kind of each server, waits for the scan, moves the watch state, removes the old folders from the library and empties them. It touches no other folders or settings. The banner shows each step. If a step fails, the migration stops with the old folders still working, the banner shows the error and the steps left, and **Try again** resumes from that step; you can also do the steps left by hand.
- **I'll do it by hand.** Add the folders of each server, of the same kind, to every library that holds an old `Film` or `Serie` folder; the banner lists what each library still lacks. Each server's folders must end up in at least one library. After the scan JellyMesh moves the watch state and empties the old folders, and the banner asks you to remove the old `Film` and `Serie` folders from your libraries. You can still switch to the automatic migration later; once chosen, it can't go back to manual.

Watch state moves to the new titles user by user: the resume position goes to the version of the server that last played the title, otherwise to the best one; the favourite, rating and play count go to the primary version; "watched" is shared by all versions.

**There is no going back to 0.4.** 0.5 saves its state in a new format that 0.4 discards, and an older plugin would read merged series as films.

## Stream limit and fallback

The stream limit exists only between JellyMesh servers, because a stock remote can't report one. It counts the streams a connected server holds on the provider, including a local transcode of that provider's file, since that reads the file from the provider too.

A stream gives its slot back as soon as it ends, fails or is closed. If its connection drops, or the provider fails it with a server error, and nothing else of that stream is running, the slot stays for 5 seconds, so that a seek or a retry keeps it, and then it's free and its transcode is closed, so that the limit also holds on your hardware. If the stream comes back later it takes a free slot and the transcode restarts from there, and if none is free the client gets the 503 and the limit message below, as a new playback does. The provider may have sent everything already and not see a client go, so the server playing the title tells it when a client drops its connection and no new request arrives within those 5 seconds, which ends in the same way. A stock remote gets no such call. A paused HLS stream frees it in under 2 minutes. A server that vanishes with its connection left open is released after 2 minutes without activity, and its transcode is closed then too, unless **Close the transcode of an expired stream** is off. Disconnecting a server frees all its slots at once and ends everything it is playing, including transcodes. Taking a library away from it frees only the slots of what it can no longer reach and ends those streams, including transcodes, while what is still shared with it, from any library, keeps playing and stays counted. Lowering its limit ends at once what no longer fits, including transcodes: first streams that are still opening, then the most recently opened, so the oldest keep playing and stay counted. A slot ended by taking a library away or by lowering the limit stays counted until its transcode is closed, a second or two, and that stream cannot take it back. A limit still at or above what is running ends nothing. Any other change, such as a higher limit or an added library, frees nothing and leaves running streams alone.

When a title is played, the version that starts is the one the client picked, otherwise the local copy if the user can see it, otherwise the best version the user can see. If a server version can't play, JellyMesh tries in this order:

1. The sources of that version, then the other versions the user can see, best quality first. A source at its limit, offline or unable to serve is skipped.
2. A local transcode on your server, reading the file from a source that answered and has a free stream slot. It uses your server's own transcoding settings. The plugin doesn't pick any hardware.
3. A local copy of the title, if the user can see it.

If nothing works, the title stays visible and playback is refused with HTTP 503 and a message. An offline remote costs the wait in **Settings** before the next step.

If a source drops during playback, the player's next request gets an error at once, and its retry plays from another source: the one that dropped goes last for that user and title for 5 minutes. A title that drops more than 3 times within those 5 minutes is refused until they pass.

| Message | Meaning |
|---|---|
| Server non raggiungibile | No server holding the title can be reached |
| Limite di stream raggiunto sul server di origine. Riprova più tardi. | Every source is at its stream limit |
| Riproduzione non disponibile al momento. Riprova più tardi. | A server answered, but nothing could serve the title |

## Trust between servers

> **Trust between servers.** Stream limits and library sharing hold for honest peers. They are not a security boundary against a hostile one. `/Videos/{id}/stream` is anonymous in Jellyfin, so a modified server that drops its token bypasses both. Connect only servers you trust, and use each dedicated user for one server only.

A connected server can stop only the transcodes it opened, and its requests can name only those play sessions. Between ordinary users of one server, on titles that are really on it, Jellyfin's own behaviour applies: anyone who knows a play session can stop it. JellyMesh keeps that away from the titles it federates, including their local transcodes.

Remote tokens stay on your server: they never appear in `.strm` files, in logs or in anything sent to clients, and players never see a remote's address.

**Behind a reverse proxy.** Each `.strm` points to `/JellyMesh/Play/` on `127.0.0.1` and the server's own port, so Jellyfin reads it directly and never through your proxy. That endpoint refuses any request that does not arrive on a loopback connection, or that carries proxy headers (`Forwarded`, `X-Real-IP`, `X-Forwarded-*`, `X-Original-*`). A request relayed by a proxy on the same host is therefore refused, which is intended. Keep your proxy adding `X-Forwarded-For`, as most do by default. If it adds none, a relayed request looks local and only the URL signature, the main defence anyway, stands in its way.

## Limits

- **Messages:** the three refusal messages above are fixed and in Italian only, whatever the server language. They appear for 8 seconds, and only on clients that display them. Other clients show their own error.
- **Unavailable titles:** nothing in a list marks a title as unavailable. You find out when you press play.
- **Metadata saving:** tested with Jellyfin's metadata savers off. Linking versions writes no `.nfo`, even next to a local copy. With the NFO saver on, Jellyfin might rewrite the plugin's `.nfo` files, which hasn't been tested.
- **Collections and versions:** a collection holds only the primary version of a title. A user who can't see that version doesn't see the title in the collection. Playlists keep the version the user added.
- **Latest:** the card of a title in "Latest" shows its most recent version.
- **Continue watching:** a title shows only with the version that was watched.
- **Series from several servers:** they merge only in libraries that hold the `Serie` folders of all those servers. A series merged by hand may show as two rows.
- **Jellyfin's own version actions:** splitting versions apart, or merging them, in Jellyfin is undone at the next sync. Use **Undo merge** on the plugin page.
- **Renaming a library in the Dashboard on 12.1:** Jellyfin 12.1 does not update the users' library permissions. Users who had the library allowed lose it, and users who had it blocked see it. Check Dashboard > Users after renaming a library there. JellyMesh does this for you only when it renames its own libraries.
- **Same folder, other path:** JellyMesh recognizes the federated folder through symbolic links and Unicode forms of its path, but not through a bind mount, which it cannot see. Federated series and seasons reached through a second mount can be offered to other JellyMesh servers, so always add the folders with the same path. A symbolic link from an ordinary library into the federated folder lets nothing out, but a connected server may get a shorter page of titles.
- **Changes by hand:** files you change in the federated folder are put right only by the nightly reconciliation.
- **Going back:** a server that has run 0.5 can't go back to 0.4.
- **Bandwidth:** every stream passes through your server, and over Tailscale it may pass through a relay.
- **Matching:** a title and year alone never merge two copies. When metadata can't confirm a match, both copies stay. A copy with no IDs and no year stays alone.
- **Seasons from two servers:** when two servers give different names or plots for a season of the same series, which one Jellyfin shows is effectively random, and stays the same on a given install. Each episode keeps the season name of its own server.
- **Jellyfin versions:** 12.1 is the minimum. Tested on 12.4; on 12.1 only installing and loading the plugin has been checked.
- **Provider restart:** a stream in progress ends when the provider restarts, and the client has to open the title again.

### Built-in limits

These protect your server and are not settings. They don't stop a dishonest peer.

| Limit | Value |
|---|---|
| Playback calls at once, per remote | 16 |
| Open streams, per remote | 32 |
| Requests at once, per playback session | 8 |
| Playback sessions, per user | 16 |
| Reads at once, per connected JellyMesh server | 8 |
| Size of a JSON answer | 32 MiB |
| Size of an image or a playlist | 16 MiB |
| One catalog read of one server | 2 GiB in total |
| Time one server may take of a sync | 4 hours |

At 16 playback sessions, the oldest unused one closes first. If all are in use, the new playback is refused. A remote that goes silent mid-stream ends at the idle time in **Settings**. If a remote refuses a call from a valid login, the connection status shows "not allowed for the dedicated user".

## Development

`tests/Integration/README.md` describes the test environment and the proof scripts. To run the unit tests, from the repository root:

```sh
docker run --rm -v "$PWD":/src -v jellymesh-nuget:/root/.nuget/packages -w /src \
  mcr.microsoft.com/dotnet/sdk:10.0 dotnet test
```

## Licence

GPLv3. See `LICENSE`.
