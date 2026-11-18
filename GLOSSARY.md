# JellyMesh

A Jellyfin plugin that federates several Jellyfin servers: it shows remote catalogs in local libraries and proxies their playback.

## Language

**Federated folder**:
The folder the plugin writes into, the only place it writes media entries. Set on the config page or with `JELLYMESH_FEDERATED_ROOT`.
_Avoid_: federated root (except as the variable name), output folder

**Server folder**:
The subfolder of the federated folder that belongs to one remote server, named by the server's id, with one remote library folder for each remote library imported from that server.
_Avoid_: remote folder

**Remote library folder**:
The subfolder of a server folder for one imported remote library, named by that library's id, with one type folder for each content type the library holds.
_Avoid_: remote library (that is the library on the remote server)

**Type folder**:
The subfolder of a remote library folder for one content type: films, series, music, music videos, home videos with photos, or books with audiobooks. Each type folder matches one kind of local library.
_Avoid_: content folder, category folder

**Server library**:
A local library that holds the folders of one server only, one per type or one per remote library, created or adopted for that server.
_Avoid_: federated library, remote library (that is a library on the remote server), single library (that holds every server)

**Single library**:
The local library of one content type that holds that type's folders from every server, created or adopted through a global option. It is not a server library.
_Avoid_: shared library, global library, all-servers library

**Plugin library**:
A local library the plugin created itself, as a server library or as a single library. Only these does the plugin rename and delete.
_Avoid_: automatic library

**Adopted library**:
A library the admin made with exactly the paths and type the plugin would give one, which the plugin uses instead of creating its own. The plugin updates its paths but never renames it, deletes it or changes its permissions.
_Avoid_: plugin library (an adopted library is not one), claimed library

**Library mode**:
The per-server choice of which local libraries hold that server's type folders: "by type", "by remote library" or "none". Changing it never moves files.
_Avoid_: automatic libraries switch

**Entity**:
What a user sees for one title inside one local library. Two libraries show the same title as two entities.
_Avoid_: item (Jellyfin's own term), stub

**Version**:
One playable copy of a title offered under an entity: a copy on a remote server, or the local copy.
_Avoid_: source (except for a playback source), backup, copy (except for the file itself)

**Stub**:
The file the plugin writes for one version in a type folder, a `.strm` file or a placeholder, with the metadata and images its type needs.
_Avoid_: placeholder (that is one kind of stub only)

**Placeholder**:
The kind of stub the plugin writes for a content type that Jellyfin does not recognize from a `.strm` file, such as ebooks and photos: a file with the real extension and none of the remote content, which the plugin serves from the remote.
_Avoid_: dummy file, empty stub

**Local play session**:
A playback of a stub that this server serves itself, from a local copy or with a local transcode, with the user who started it.
_Avoid_: stub session, plugin session (that is a playback the plugin proxies from a remote server)

**Peer**:
Another JellyMesh server that plays this server's titles through a dedicated user. It acts for its own users.
_Avoid_: remote (that is a server this one reads from), client

**Store**:
The plugin's single database in its data folder, holding all its settings and state; the keys stay outside it.
_Avoid_: config, XML config, state files
