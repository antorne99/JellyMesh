using System;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// An image to write next to a stub, read from a remote or copied from the local series.
/// </summary>
/// <param name="Type">The image type.</param>
/// <param name="RelativeStem">The relative path of the file, without the extension.</param>
/// <param name="ServerId">The server to read it from.</param>
/// <param name="ItemId">The item on that server.</param>
/// <param name="LocalPath">The local file to copy instead.</param>
public sealed record StubImage(ImageType Type, string RelativeStem, Guid? ServerId, string? ItemId, string? LocalPath);
