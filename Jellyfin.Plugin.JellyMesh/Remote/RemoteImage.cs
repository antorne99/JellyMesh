using System;

namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// An image of a remote item.
/// </summary>
/// <param name="Content">The bytes.</param>
/// <param name="ContentType">The media type the remote declared.</param>
public sealed record RemoteImage(ReadOnlyMemory<byte> Content, string ContentType);
