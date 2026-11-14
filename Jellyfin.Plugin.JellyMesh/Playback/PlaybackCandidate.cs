using System;
using Jellyfin.Plugin.JellyMesh.Library;

namespace Jellyfin.Plugin.JellyMesh.Playback;

/// <summary>
/// Another version of a title a playback may fall back to: a stub the user sees.
/// </summary>
/// <param name="ItemId">The item of the stub, which the client is told about when it serves.</param>
/// <param name="Stub">The stub.</param>
public sealed record PlaybackCandidate(Guid ItemId, StubEntry Stub);
