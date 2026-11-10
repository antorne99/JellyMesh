using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Security;

/// <summary>
/// The access list a key file gets on Windows, as plain data so that it can be checked on any system (decision 43).
/// </summary>
/// <param name="FullControl">The security identifiers that get full control, and nobody else.</param>
/// <param name="BlocksInheritance">Whether the file drops the permissions of its folder.</param>
internal sealed record KeyFileAclPlan(IReadOnlyList<string> FullControl, bool BlocksInheritance);
