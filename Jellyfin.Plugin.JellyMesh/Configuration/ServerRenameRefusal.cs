using System.Collections.Generic;
using Jellyfin.Plugin.JellyMesh.Library;

namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// Why a server was not renamed, with the libraries a confirmation would rename.
/// </summary>
/// <param name="Outcome">The reason.</param>
/// <param name="Message">The reason for the page.</param>
/// <param name="Libraries">The automatic libraries a confirmation would rename.</param>
public sealed record ServerRenameRefusal(ServerRenameOutcome Outcome, string Message, IReadOnlyList<string> Libraries);
