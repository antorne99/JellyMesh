using System.Collections.Generic;
using Jellyfin.Plugin.JellyMesh.Library;

namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// Why the libraries of a server were not switched, with what a confirmation would delete.
/// </summary>
/// <param name="Outcome">The reason.</param>
/// <param name="Message">The reason for the page.</param>
/// <param name="ToDelete">The libraries a confirmation would delete.</param>
public sealed record AutomaticLibrariesRefusal(AutomaticLibrariesOutcome Outcome, string Message, IReadOnlyList<string> ToDelete);
