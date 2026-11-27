using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// A library of the new library mode and the users who saw some of its titles before the change and do not see it after.
/// </summary>
/// <param name="Library">The name of the library.</param>
/// <param name="Users">The names of the users. Untrusted text.</param>
public sealed record AccessLoss(string Library, IReadOnlyList<string> Users);
