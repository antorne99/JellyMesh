# JellyMesh

A Jellyfin plugin that federates several Jellyfin servers: it shows remote catalogs in local libraries and proxies their playback.

## Language

**Federated folder**:
The folder the plugin writes into, the only place it writes media entries. Set on the config page or with `JELLYMESH_FEDERATED_ROOT`.
_Avoid_: federated root (except as the variable name), output folder

**Server folder**:
The subfolder of the federated folder that belongs to one remote server, named by the server's id, with a `Film` and a `Serie` folder holding only that server's titles.
_Avoid_: remote folder

**Server library**:
A local library whose paths are server folders only.
_Avoid_: federated library, remote library (that is a library on the remote server)

**Entity**:
What a user sees for one title inside one local library. Two libraries show the same title as two entities.
_Avoid_: item (Jellyfin's own term), stub

**Version**:
One playable copy of a title offered under an entity: a copy on a remote server, or the local copy.
_Avoid_: source (except for a playback source), backup, copy (except for the file itself)

**Stub**:
The `.strm` file, with its `.nfo` and images, that the plugin writes for one version in a server folder.
_Avoid_: placeholder
