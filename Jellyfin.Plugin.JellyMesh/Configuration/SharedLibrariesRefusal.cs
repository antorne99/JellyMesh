using System.Collections.Generic;
using Jellyfin.Plugin.JellyMesh.Library;

namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// Why the switch of the shared libraries was refused, with the libraries a confirmation would delete.
/// </summary>
/// <param name="Outcome">The reason.</param>
/// <param name="Message">The reason for the page.</param>
/// <param name="ToDelete">The shared libraries a confirmed switch-off would delete.</param>
public sealed record SharedLibrariesRefusal(AutomaticLibrariesOutcome Outcome, string Message, IReadOnlyList<string> ToDelete);
